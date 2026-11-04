#include "status_bar_manager.h"

#include <algorithm>
#include <cstdio>
#include <cstring>
#include <fstream>
#include <mutex>
#include <sstream>
#include <string>

namespace iwk {

namespace {

constexpr uint32_t kUnitContainerOffset = 0x50;
constexpr uint32_t kBarOffsetFromContainer = 0x0C;
// 1.20e CUnit position helper: __fastcall(unit, float* outXY, float* outZ).
// U9's mb.dll uses this instead of the bar-child chain.
constexpr uint32_t kGetUnitPositionRva120 = 0x1264D0;
// 1.20e unit table used by GetUnitState(state=3). Game.dll+0x92730 reads the
// handle at CUnit+0xC0, resolves it through this table, then dereferences the
// result without a null check. Invalid replay/observer units hit +0x92770.
constexpr uint32_t kUnitTableGlobalRva120 = 0x87C744;
constexpr uint32_t kUnitHandleOffset120 = 0xC0;
constexpr uint32_t kUnitStateLinkOffset120 = 0xC4;
constexpr uint32_t kUnitTableValid120 = 0xFFFFFFFEu;
// 1.20e health-bar forcing: SetHealthBars, the per-frame health-bar update it
// must run before, and that update's call site in the frame function.
constexpr uint32_t kSetHealthBarsRva120 = 0x167470;
constexpr uint32_t kFrameUpdateRva120 = 0x1675F0;
constexpr uint32_t kFrameCallRva120 = 0x17A15D;
// 1.20e CStatBar show (vtable+0x68) only runs when this field is non-zero.
constexpr uint32_t kBarShowEnabledOffset = 0x90;
constexpr uint32_t kChildOffsetFromBar = 0x0C;
constexpr uint32_t kManaOffset = 0x158;
constexpr uint32_t kBarWidthOffset = 0x58;
constexpr uint32_t kBarHeightOffset = 0x5C;
constexpr uint32_t kBarColorChildOffset = 0x134;
constexpr uint32_t kBarColorOffset = 0x68;
constexpr uint32_t kManaColor = 0x0D77FCFF;
constexpr float kManaWidthScale = 72.0f * 0.0005f;
constexpr float kManaHeight = 0.004f;
constexpr float kManaInitialWidth = 0.03f;
constexpr float kManaInitialX = 0.3f;
constexpr float kManaInitialY = 0.3f;
constexpr float kManaYOffset = 0.006f;

typedef void (__thiscall *StatBarCtorFn)(void*, int, int, int);
typedef void (__thiscall *SetBarBoundsFn)(void*, int);
typedef void (__thiscall *SetBarPositionFn)(void*, int, float, float, int);
typedef void (__thiscall *GetUnitStateFn)(void*, float*, int);
typedef void* (__thiscall *GetUnitUIDefFn)(uint32_t);
typedef int (__thiscall *IsUnitStructureFn)(void*, int);
typedef void* (__cdecl *GetLocalPlayerFn)();
typedef void* (__thiscall *GetCPlayerWar3Fn)(void*);
typedef void* (__thiscall *GetPlayerAttrFn)(void*);
typedef int (__thiscall *IntegerGetValueFn)(void*);
typedef int (__thiscall *UnitOwnerFn)(void*);
typedef void (__thiscall *StatBarMethodFn)(void*);
typedef void (__thiscall *StatBarValueFn)(void*, float);
typedef void (__thiscall *StatBarSetUnitFn)(void*, void*);
typedef void (__fastcall *GetUnitPositionFn)(void*, float*, float*);

StatusBarManager* g_manager = nullptr;

// Values consumed by the naked stubs. Keep these plain globals: the stubs
// address them directly from inline assembly.
void* g_sbAllocFn = nullptr;
void* g_sbOriginalUpdateFn = nullptr;
void (*g_sbInitFn)() = nullptr;
void (*g_sbUpdateFn)() = nullptr;
void* g_sbOriginalVtable = nullptr;
void* g_sbUnitScaleFn = nullptr;
void* g_sbUpdateBody = nullptr;
uint32_t g_sbReturnAddress = 0;
uint32_t g_sbBase = 0;
uint32_t g_sbUnit = 0;

std::mutex g_logMutex;

// 1.20e SetHealthBars: Game.dll+0x167470. __fastcall(ecx=enable, edx=ally,
// stack=enemy). This is the engine function that Alt / '[' / ']' ultimately
// drive. We call it once per frame on the game thread right before the
// health-bar list update, instead of synthesizing key events (which would also
// trigger any map hotkey bound to '[' / ']').
void* g_hbSetFn = nullptr;
void* g_hbFrameFn = nullptr;
volatile int g_hbActive = 0;
volatile int g_hbApply = 0;
volatile int g_hbEnable = 0;
volatile int g_hbAlly = 0;
volatile int g_hbEnemy = 0;

__declspec(naked) void StatusBarFrameSettingStub() {
    __asm {
        pushad
        mov eax, dword ptr [g_hbActive]
        test eax, eax
        je skip
        mov eax, dword ptr [g_hbApply]
        test eax, eax
        je skip
        mov dword ptr [g_hbApply], 0
        mov ecx, dword ptr [g_hbEnable]
        mov edx, dword ptr [g_hbAlly]
        push dword ptr [g_hbEnemy]
        call dword ptr [g_hbSetFn]
    skip:
        popad
        jmp dword ptr [g_hbFrameFn]
    }
}

// War3.exe export ordinal 188 (RVA 0x42190) is the engine's key-state query.
// Game.dll's health-bar key handler asks it about internal codes 2 (Alt),
// 0x112 ('[') and 0x113 (']'). Reporting '[' and ']' as held makes the game
// itself keep SetHealthBars(ally+enemy) applied, without synthesizing any key
// event (so map hotkeys and Alt+click are unaffected).
void* g_keyQueryResume = nullptr;

__declspec(naked) void StatusBarKeyQueryStub() {
    __asm {
        cmp dword ptr [g_hbActive], 0
        je original
        cmp ecx, 112h
        je pressed
        cmp ecx, 113h
        je pressed
    original:
        push ebp
        mov ebp, esp
        sub esp, 8
        jmp dword ptr [g_keyQueryResume]
    pressed:
        mov eax, 1
        ret
    }
}

std::string LogPath() {
    wchar_t temp[MAX_PATH] = {};
    DWORD length = GetTempPathW(MAX_PATH, temp);
    std::wostringstream stream;
    stream << temp;
    if (length > 0 && temp[length - 1] != L'\\') {
        stream << L'\\';
    }
    stream << L"iwar3key-agent-" << GetCurrentProcessId() << L".log";
    std::wstring wide = stream.str();
    int size = WideCharToMultiByte(CP_ACP, 0, wide.c_str(), static_cast<int>(wide.size()),
                                   nullptr, 0, nullptr, nullptr);
    std::string result(static_cast<size_t>(size > 0 ? size : 0), '\0');
    if (size > 0) {
        WideCharToMultiByte(CP_ACP, 0, wide.c_str(), static_cast<int>(wide.size()),
                            &result[0], size, nullptr, nullptr);
    }
    return result;
}

void WriteLog(const std::string& message) {
    std::lock_guard<std::mutex> lock(g_logMutex);
    std::ofstream file(LogPath(), std::ios::app);
    if (file) {
        file << GetTickCount64() << " [statusbar] " << message << "\n";
    }
}

void __cdecl StatusBarInitCdecl() {
    if (g_manager != nullptr) {
        g_manager->InitManaBar(g_sbBase);
    }
}

void __cdecl StatusBarUpdateCdecl() {
    if (g_manager != nullptr) {
        g_manager->OnUnitUpdate(g_sbUnit);
    }
}

// Replaces the game's direct call to SMemoryAlloc(0x158) with a call that
// allocates 0x2B0 and constructs a second CStatBar at +0x158. The original
// call site is `push 0x158; call SMemoryAlloc`, so the return address and the
// pushed size are the first two stack slots.
__declspec(naked) void StatusBarAllocationStub() {
    __asm {
        pop dword ptr [g_sbReturnAddress]
        pop eax
        add eax, eax
        push eax
        call dword ptr [g_sbAllocFn]
        pushad
        add eax, 158h
        mov dword ptr [g_sbBase], eax
        call dword ptr [g_sbInitFn]
        popad
        push dword ptr [g_sbReturnAddress]
        ret
    }
}

// Called at the game's per-unit `mov ecx, ebx; call CUnitUpdateHpBar` site.
// Preserve every register, let the manager update the mana bar, then tail-call
// the original function so its ret returns to the game's loop.
__declspec(naked) void StatusBarUpdateStub() {
    __asm {
        pushad
        mov dword ptr [g_sbUnit], ecx
        call dword ptr [g_sbUpdateFn]
        popad
        jmp dword ptr [g_sbOriginalUpdateFn]
    }
}

// 1.20e has no persistent "always show health bars" option. Run the original
// health-bar update first, then show both the health and mana CStatBars, so the
// game's own hide decision cannot override us.
__declspec(naked) void StatusBarUpdateAfterStub() {
    __asm {
        pushad
        mov dword ptr [g_sbUnit], ecx
        call dword ptr [g_sbOriginalUpdateFn]
        call dword ptr [g_sbUpdateFn]
        popad
        ret
    }
}

// 1.20e's CUnitUpdateHpBar thunk is called from several places (including a
// direct jmp). Hook the thunk entry itself so every caller reaches us. We
// reproduce the thunk's [unit+0x50] check, call the original body, then run our
// update after the game's layout pass.
__declspec(naked) void StatusBarUpdateThunkStub() {
    __asm {
        pushad
        mov dword ptr [g_sbUnit], ecx
        mov ecx, dword ptr [ecx + 50h]
        test ecx, ecx
        je skip_body
        call dword ptr [g_sbUpdateBody]
    skip_body:
        call dword ptr [g_sbUpdateFn]
        popad
        ret
    }
}

// Fake CStatBar vtable entries, matching KK's semantics:
//   +0x64 = Hide/Clear : call the original on the health bar, then the same
//           method on the mana bar at +0x158.
//   +0x68 = Show/Layout: forward to the original health-bar method only. The
//           per-unit update shows the mana bar itself after laying it out.
__declspec(naked) void StatusBarFakeHide() {
    __asm {
        push esi
        mov esi, ecx
        mov eax, dword ptr [g_sbOriginalVtable]
        call dword ptr [eax + 64h]
        lea ecx, [esi + 158h]
        mov eax, dword ptr [g_sbOriginalVtable]
        call dword ptr [eax + 64h]
        pop esi
        ret
    }
}

__declspec(naked) void StatusBarFakeShow() {
    __asm {
        push esi
        mov esi, ecx
        mov eax, dword ptr [g_sbOriginalVtable]
        call dword ptr [eax + 68h]
        pop esi
        ret
    }
}

// 1.24e exposes unit UIDef scale as a float-returning thiscall helper:
// ecx = unit type id, result in st(0).
__declspec(naked) float StatusBarUnitScaleCall(uint32_t) {
    __asm {
        push ebp
        mov ebp, esp
        mov ecx, dword ptr [ebp + 8]
        call dword ptr [g_sbUnitScaleFn]
        pop ebp
        ret
    }
}

bool SafeWrite32(uint32_t address, uint32_t value) {
    if (address < 0x10000) {
        return false;
    }
    __try {
        *reinterpret_cast<volatile uint32_t*>(static_cast<uintptr_t>(address)) = value;
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

}  // namespace

StatusBarManager::StatusBarManager()
    : gameBase_(0),
      imageSize_(0),
      profileValid_(false),
      installed_(false),
      healthEnabled_(false),
      friendlyMana_(false),
      enemyMana_(false),
      forceHealthBar_(false),
      updateAfterOriginal_(false),
      pointers_(),
      originalAllocRel_(0),
      allyMask_(0),
      allyMaskAt_(0),
      localPlayerIndex_(0xFFFFFFFFu),
      initCount_(0),
      updateCount_(0),
      hiddenCount_(0),
      shownCount_(0),
      relationFailCount_(0),
      invalidUnitLogged_(false),
      lastUpdateTick_(0) {
    std::memset(fakeVtable_, 0, sizeof(fakeVtable_));
    g_manager = this;
    g_sbInitFn = &StatusBarInitCdecl;
    g_sbUpdateFn = &StatusBarUpdateCdecl;
}

StatusBarManager::~StatusBarManager() {
    g_hbActive = 0;
    if (g_manager == this) {
        g_manager = nullptr;
    }
}

void StatusBarManager::SetGameBase(uint32_t base, uint32_t imageSize) {
    gameBase_.store(base);
    imageSize_.store(imageSize);
}

void StatusBarManager::SetProfile(bool valid,
                                  uint32_t allocCallRva,
                                  uint32_t updateCallRva,
                                  uint32_t originalUpdateRva,
                                  uint32_t ctorRva,
                                  uint32_t getUnitStateRva,
                                  uint32_t unitScaleRva,
                                  uint32_t setBarPositionRva,
                                  uint32_t setBarBoundsRva,
                                  uint32_t getUnitUIDefRva,
                                  uint32_t isUnitStructureRva,
                                  uint32_t getLocalPlayerRva,
                                  uint32_t getCPlayerWar3Rva,
                                  uint32_t cPlayerAttrRva,
                                  uint32_t integerGetValueRva,
                                  uint32_t cguiRva,
                                  uint32_t healthFlagOffset) {
    profileValid_.store(false);
    if (!valid) {
        return;
    }

    const uint32_t base = gameBase_.load();
    if (base == 0 || allocCallRva == 0 || updateCallRva == 0 ||
        originalUpdateRva == 0 || ctorRva == 0 || getUnitStateRva == 0 ||
        setBarPositionRva == 0 || setBarBoundsRva == 0 ||
        getLocalPlayerRva == 0 ||
        cPlayerAttrRva == 0 || integerGetValueRva == 0 || cguiRva == 0) {
        return;
    }

    Pointers pointers;
    pointers.allocCall = base + allocCallRva;
    pointers.updateCall = base + updateCallRva;
    pointers.originalUpdate = base + originalUpdateRva;
    pointers.ctor = base + ctorRva;
    pointers.getUnitState = base + getUnitStateRva;
    pointers.unitScale = unitScaleRva != 0 ? base + unitScaleRva : 0;
    pointers.setBarPosition = base + setBarPositionRva;
    pointers.setBarBounds = base + setBarBoundsRva;
    pointers.getUnitUIDef = getUnitUIDefRva != 0 ? base + getUnitUIDefRva : 0;
    pointers.isUnitStructure = isUnitStructureRva != 0 ? base + isUnitStructureRva : 0;
    pointers.getLocalPlayer = base + getLocalPlayerRva;
    pointers.getCPlayerWar3 = getCPlayerWar3Rva != 0 ? base + getCPlayerWar3Rva : 0;
    pointers.cPlayerAttr = base + cPlayerAttrRva;
    pointers.integerGetValue = base + integerGetValueRva;
    pointers.cguiGlobal = base + cguiRva;
    pointers.healthFlagOffset = healthFlagOffset;
    pointers_ = pointers;
    // 1.20e has neither a UIDef pointer helper nor a scale helper in the
    // profile; use that to select the after-original display path.
    const bool forceHealth = getUnitUIDefRva == 0 && unitScaleRva == 0;
    forceHealthBar_.store(forceHealth);
    updateAfterOriginal_.store(forceHealth);
    profileValid_.store(true);

    std::ostringstream stream;
    stream << "profile base=0x" << std::hex << base
           << " imageSize=0x" << imageSize_.load()
           << " allocCall=0x" << pointers.allocCall
           << " updateCall=0x" << pointers.updateCall
           << " ctor=0x" << pointers.ctor
           << " cguiGlobal=0x" << pointers.cguiGlobal;
    WriteLog(stream.str());
}

void StatusBarManager::SetEnabled(bool health, bool friendlyMana, bool enemyMana) {
    // Mana bars are drawn on the health-bar update path, so they are only
    // usable while health display is on.
    const bool friendly = health && friendlyMana;
    const bool enemy = health && enemyMana;
    healthEnabled_.store(health);
    friendlyMana_.store(friendly);
    enemyMana_.store(enemy);

    if (forceHealthBar_.load()) {
        g_hbActive = health ? 1 : 0;
        g_hbEnable = health ? 1 : 0;
        g_hbAlly = health ? 1 : 0;
        g_hbEnemy = health ? 1 : 0;
    } else {
        g_hbActive = 0;
    }
    if (g_hbActive != 0) {
        g_hbApply = 1;
    }

    std::ostringstream stream;
    stream << "enabled health=" << (health ? 1 : 0)
           << " friendlyMana=" << (friendly ? 1 : 0)
           << " enemyMana=" << (enemy ? 1 : 0)
           << " hbActive=" << g_hbActive
           << " hbAlly=" << g_hbAlly
           << " hbEnemy=" << g_hbEnemy;
    WriteLog(stream.str());
}

bool StatusBarManager::PatchCall(uint32_t site, void* target, int32_t* savedRel) {
    if (site == 0 || target == nullptr) {
        return false;
    }

    uint8_t* code = reinterpret_cast<uint8_t*>(static_cast<uintptr_t>(site));
    if (code[0] != 0xE8) {
        WriteLog("call-site opcode is not E8");
        return false;
    }

    int32_t oldRel = 0;
    std::memcpy(&oldRel, code + 1, sizeof(oldRel));
    const uintptr_t next = static_cast<uintptr_t>(site) + 5;
    const int64_t relative = static_cast<int64_t>(reinterpret_cast<uintptr_t>(target)) -
                             static_cast<int64_t>(next);
    if (relative < INT32_MIN || relative > INT32_MAX) {
        WriteLog("call-site target out of rel32 range");
        return false;
    }

    DWORD oldProtect = 0;
    if (!VirtualProtect(code, 5, PAGE_EXECUTE_READWRITE, &oldProtect)) {
        WriteLog("VirtualProtect failed on call site");
        return false;
    }

    int32_t newRel = static_cast<int32_t>(relative);
    std::memcpy(code + 1, &newRel, sizeof(newRel));
    VirtualProtect(code, 5, oldProtect, &oldProtect);
    FlushInstructionCache(GetCurrentProcess(), code, 5);
    if (savedRel != nullptr) {
        *savedRel = oldRel;
    }
    return true;
}

bool StatusBarManager::PatchJmp(uint32_t site, void* target) {
    if (site == 0 || target == nullptr) {
        return false;
    }

    uint8_t* code = reinterpret_cast<uint8_t*>(static_cast<uintptr_t>(site));
    const int64_t relative = static_cast<int64_t>(reinterpret_cast<uintptr_t>(target)) -
                             static_cast<int64_t>(site + 5);
    if (relative < INT32_MIN || relative > INT32_MAX) {
        WriteLog("jmp target out of rel32 range");
        return false;
    }

    DWORD oldProtect = 0;
    if (!VirtualProtect(code, 5, PAGE_EXECUTE_READWRITE, &oldProtect)) {
        WriteLog("VirtualProtect failed on jmp site");
        return false;
    }

    code[0] = 0xE9;
    int32_t rel = static_cast<int32_t>(relative);
    std::memcpy(code + 1, &rel, sizeof(rel));
    VirtualProtect(code, 5, oldProtect, &oldProtect);
    FlushInstructionCache(GetCurrentProcess(), code, 5);
    return true;
}

void StatusBarManager::Apply() {
    if (!profileValid_.load() || installed_.load()) {
        return;
    }

    const uint32_t base = gameBase_.load();
    if (base == 0) {
        return;
    }

    // Allocation call site: remember the original SMemoryAlloc target before
    // replacing the call. The target is inside Game.dll.
    uint8_t* allocCode = reinterpret_cast<uint8_t*>(static_cast<uintptr_t>(pointers_.allocCall));
    if (allocCode[0] != 0xE8) {
        WriteLog("installation aborted: allocation call site changed");
        return;
    }
    int32_t allocRel = 0;
    std::memcpy(&allocRel, allocCode + 1, sizeof(allocRel));
    uint32_t allocTarget = static_cast<uint32_t>(
        static_cast<int64_t>(pointers_.allocCall) + 5 + allocRel);
    const uint32_t imageSize = imageSize_.load();
    if (allocTarget < base || (imageSize != 0 && allocTarget >= base + imageSize)) {
        std::ostringstream stream;
        stream << "installation aborted: allocation target outside Game.dll"
               << " base=0x" << std::hex << base
               << " imageSize=0x" << imageSize
               << " site=0x" << pointers_.allocCall
               << " target=0x" << allocTarget;
        WriteLog(stream.str());
        return;
    }

    // Update call site: the original function must be the CUnitUpdateHpBar
    // thunk from the profile, otherwise this is not the expected build.
    uint8_t* updateCode = reinterpret_cast<uint8_t*>(static_cast<uintptr_t>(pointers_.updateCall));
    if (updateCode[0] != 0xE8) {
        WriteLog("installation aborted: update call site changed");
        return;
    }
    int32_t updateRel = 0;
    std::memcpy(&updateRel, updateCode + 1, sizeof(updateRel));
    uint32_t updateTarget = static_cast<uint32_t>(
        static_cast<int64_t>(pointers_.updateCall) + 5 + updateRel);
    if (updateTarget != pointers_.originalUpdate) {
        std::ostringstream stream;
        stream << "installation aborted: update target 0x" << std::hex << updateTarget
               << " != expected 0x" << pointers_.originalUpdate;
        WriteLog(stream.str());
        return;
    }

    g_sbUnitScaleFn = reinterpret_cast<void*>(static_cast<uintptr_t>(pointers_.unitScale));
    g_sbAllocFn = reinterpret_cast<void*>(static_cast<uintptr_t>(allocTarget));
    g_sbOriginalUpdateFn = reinterpret_cast<void*>(static_cast<uintptr_t>(pointers_.originalUpdate));

    if (!PatchCall(pointers_.allocCall, &StatusBarAllocationStub, &originalAllocRel_)) {
        return;
    }
    bool updateHooked = false;
    if (forceHealthBar_.load()) {
        // Patch the thunk entry itself. Its body target is the jmp at +7.
        uint8_t* thunk = reinterpret_cast<uint8_t*>(
            static_cast<uintptr_t>(pointers_.originalUpdate));
        if (thunk[0] == 0x8B && thunk[1] == 0x49 && thunk[2] == 0x50 &&
            thunk[7] == 0xE9) {
            int32_t bodyRel = 0;
            std::memcpy(&bodyRel, thunk + 8, sizeof(bodyRel));
            const uint32_t body = static_cast<uint32_t>(
                static_cast<int64_t>(pointers_.originalUpdate) + 12 + bodyRel);
            if (body >= base && (imageSize == 0 || body < base + imageSize)) {
                g_sbUpdateBody = reinterpret_cast<void*>(static_cast<uintptr_t>(body));
                if (PatchJmp(pointers_.originalUpdate, &StatusBarUpdateThunkStub)) {
                    updateHooked = true;
                }
            }
        }
        if (!updateHooked) {
            WriteLog("installation aborted: update thunk pattern changed");
        }
    } else {
        void* updateStub = updateAfterOriginal_.load()
            ? reinterpret_cast<void*>(&StatusBarUpdateAfterStub)
            : reinterpret_cast<void*>(&StatusBarUpdateStub);
        updateHooked = PatchCall(pointers_.updateCall, updateStub);
    }

    if (!updateHooked) {
        // Put the allocation call back so we do not leave a half-installed hook.
        uint8_t* code = reinterpret_cast<uint8_t*>(static_cast<uintptr_t>(pointers_.allocCall));
        DWORD oldProtect = 0;
        if (VirtualProtect(code, 5, PAGE_EXECUTE_READWRITE, &oldProtect)) {
            std::memcpy(code + 1, &originalAllocRel_, sizeof(originalAllocRel_));
            VirtualProtect(code, 5, oldProtect, &oldProtect);
            FlushInstructionCache(GetCurrentProcess(), code, 5);
        }
        return;
    }

    installed_.store(true);
    WriteLog(forceHealthBar_.load() ? "mana-bar hooks installed (thunk)" : "mana-bar hooks installed");

    if (forceHealthBar_.load()) {
        g_hbSetFn = reinterpret_cast<void*>(
            static_cast<uintptr_t>(base + kSetHealthBarsRva120));
        g_hbFrameFn = reinterpret_cast<void*>(
            static_cast<uintptr_t>(base + kFrameUpdateRva120));
        pointers_.frameCall = base + kFrameCallRva120;

        uint8_t* frameCode = reinterpret_cast<uint8_t*>(
            static_cast<uintptr_t>(pointers_.frameCall));
        bool frameOk = false;
        if (frameCode[0] == 0xE8) {
            int32_t frameRel = 0;
            std::memcpy(&frameRel, frameCode + 1, sizeof(frameRel));
            const uint32_t frameTarget = static_cast<uint32_t>(
                static_cast<int64_t>(pointers_.frameCall) + 5 + frameRel);
            if (frameTarget == base + kFrameUpdateRva120) {
                frameOk = PatchCall(pointers_.frameCall, &StatusBarFrameSettingStub);
            } else {
                std::ostringstream stream;
                stream << "health-bar frame target 0x" << std::hex << frameTarget
                       << " != expected 0x" << (base + kFrameUpdateRva120);
                WriteLog(stream.str());
            }
        }
        if (frameOk) {
            WriteLog("health-bar frame hook installed");
        } else {
            WriteLog("health-bar frame hook failed");
        }

        HMODULE exe = GetModuleHandleW(nullptr);
        uint8_t* entry = exe != nullptr
            ? reinterpret_cast<uint8_t*>(exe) + 0x42190
            : nullptr;
        if (entry != nullptr && entry[0] == 0x55 && entry[1] == 0x8B &&
            entry[2] == 0xEC && entry[3] == 0x83 && entry[4] == 0xEC &&
            entry[5] == 0x08) {
            g_keyQueryResume = entry + 6;
            DWORD oldProtect = 0;
            if (VirtualProtect(entry, 6, PAGE_EXECUTE_READWRITE, &oldProtect)) {
                const int64_t relative = static_cast<int64_t>(
                    reinterpret_cast<uintptr_t>(&StatusBarKeyQueryStub)) -
                    static_cast<int64_t>(reinterpret_cast<uintptr_t>(entry) + 5);
                entry[0] = 0xE9;
                const int32_t rel = static_cast<int32_t>(relative);
                std::memcpy(entry + 1, &rel, sizeof(rel));
                entry[5] = 0x90;
                VirtualProtect(entry, 6, oldProtect, &oldProtect);
                FlushInstructionCache(GetCurrentProcess(), entry, 6);
                WriteLog("health-bar key query hook installed");
            } else {
                WriteLog("health-bar key query hook failed");
            }
        } else {
            WriteLog("health-bar key query pattern changed");
        }
    }
}

void StatusBarManager::Disable() {
    healthEnabled_.store(false);
    friendlyMana_.store(false);
    enemyMana_.store(false);
    // Keep the call sites installed. They are safe no-ops when the feature is
    // off and keeping them avoids patching code while the game thread runs.
    g_hbActive = 0;
    WriteLog("disabled");
}

std::string StatusBarManager::Status() const {
    std::ostringstream stream;
    stream << "installed=" << (installed_.load() ? 1 : 0)
           << " health=" << (healthEnabled_.load() ? 1 : 0)
           << " friendly=" << (friendlyMana_.load() ? 1 : 0)
           << " enemy=" << (enemyMana_.load() ? 1 : 0)
           << " init=" << initCount_.load()
           << " update=" << updateCount_.load()
           << " shown=" << shownCount_.load()
           << " hidden=" << hiddenCount_.load()
           << " relationFail=" << relationFailCount_.load()
           << " mask=0x" << std::hex << allyMask_.load();
    return stream.str();
}

bool StatusBarManager::IsUnitStateTargetValid120(uint32_t unit) const {
    const uint32_t base = gameBase_.load();
    if (base == 0 || unit == 0) {
        return false;
    }

    auto read32 = [this](uint32_t address, uint32_t& value) -> bool {
        if (!Readable(address, sizeof(uint32_t))) {
            return false;
        }
        std::memcpy(&value, reinterpret_cast<const void*>(static_cast<uintptr_t>(address)),
                    sizeof(value));
        return true;
    };

    uint32_t handle = 0;
    if (!read32(unit + kUnitHandleOffset120, handle)) {
        return false;
    }

    uint32_t table = 0;
    if (!read32(base + kUnitTableGlobalRva120, table) || table == 0) {
        return false;
    }

    uint32_t count = 0;
    uint32_t entries = 0;
    uint32_t index = 0;
    if ((handle & 0x80000000u) != 0) {
        index = handle & 0x7FFFFFFFu;
        if (!read32(table + 0x3C, count) || !read32(table + 0x2C, entries)) {
            return false;
        }
    } else {
        index = handle;
        if (!read32(table + 0x1C, count) || !read32(table + 0x0C, entries)) {
            return false;
        }
    }

    if (entries == 0 || index >= count) {
        return false;
    }

    const uint32_t entryAddress = entries + index * 8;
    uint32_t sentinel = 0;
    uint32_t unitObject = 0;
    if (!read32(entryAddress, sentinel) || sentinel != kUnitTableValid120 ||
        !read32(entryAddress + 4, unitObject) || unitObject == 0) {
        return false;
    }

    uint32_t link = 0;
    uint32_t marker = 0;
    if (!read32(unit + kUnitStateLinkOffset120, link) ||
        !read32(unitObject + 0x18, marker) || link != marker) {
        return false;
    }

    return Readable(unitObject + 0x84, sizeof(uint32_t));
}

bool StatusBarManager::Readable(uint32_t address, size_t size) const {
    if (address == 0 || size == 0) {
        return false;
    }

    uint64_t current = address;
    size_t remaining = size;
    while (remaining > 0) {
        MEMORY_BASIC_INFORMATION info{};
        if (VirtualQuery(reinterpret_cast<LPCVOID>(static_cast<uintptr_t>(current)), &info,
                         sizeof(info)) == 0) {
            return false;
        }
        if (info.State != MEM_COMMIT || (info.Protect & (PAGE_NOACCESS | PAGE_GUARD)) != 0) {
            return false;
        }

        uint64_t regionBase = reinterpret_cast<uintptr_t>(info.BaseAddress);
        uint64_t regionEnd = regionBase + info.RegionSize;
        if (regionEnd <= current) {
            return false;
        }

        size_t chunk = static_cast<size_t>(std::min<uint64_t>(remaining, regionEnd - current));
        current += chunk;
        remaining -= chunk;
    }
    return true;
}

void StatusBarManager::InitManaBar(uint32_t bar) {
    if (bar == 0 || !Readable(bar, 0x158)) {
        return;
    }

    StatBarCtorFn ctor = reinterpret_cast<StatBarCtorFn>(
        static_cast<uintptr_t>(pointers_.ctor));
    SetBarBoundsFn setBounds = reinterpret_cast<SetBarBoundsFn>(
        static_cast<uintptr_t>(pointers_.setBarBounds));
    SetBarPositionFn setPosition = reinterpret_cast<SetBarPositionFn>(
        static_cast<uintptr_t>(pointers_.setBarPosition));

    if (ctor == nullptr || setBounds == nullptr || setPosition == nullptr) {
        return;
    }

    ctor(reinterpret_cast<void*>(static_cast<uintptr_t>(bar)), 0, 0, 0);

    uint32_t colorChild = 0;
    if (Readable(bar + kBarColorChildOffset, sizeof(uint32_t))) {
        std::memcpy(&colorChild, reinterpret_cast<const void*>(static_cast<uintptr_t>(
                      bar + kBarColorChildOffset)), sizeof(colorChild));
    }
    if (colorChild != 0 && Readable(colorChild + kBarColorOffset, sizeof(uint32_t))) {
        std::memcpy(reinterpret_cast<void*>(static_cast<uintptr_t>(colorChild + kBarColorOffset)),
                    &kManaColor, sizeof(kManaColor));
    }

    float width = kManaInitialWidth;
    std::memcpy(reinterpret_cast<void*>(static_cast<uintptr_t>(bar + kBarWidthOffset)),
                &width, sizeof(width));
    setBounds(reinterpret_cast<void*>(static_cast<uintptr_t>(bar)), 0);

    float height = kManaHeight;
    std::memcpy(reinterpret_cast<void*>(static_cast<uintptr_t>(bar + kBarHeightOffset)),
                &height, sizeof(height));
    setBounds(reinterpret_cast<void*>(static_cast<uintptr_t>(bar)), 0);

    setPosition(reinterpret_cast<void*>(static_cast<uintptr_t>(bar)), 1,
                kManaInitialX, kManaInitialY, 1);

    // Let the bar build its children, then hide it until the per-unit update
    // decides whether this unit should have a mana bar.
    StatBarMethodFn show = nullptr;
    StatBarMethodFn hide = nullptr;
    void** vtable = nullptr;
    if (Readable(bar, sizeof(uint32_t))) {
        std::memcpy(&vtable, reinterpret_cast<const void*>(static_cast<uintptr_t>(bar)),
                    sizeof(vtable));
    }
    if (vtable != nullptr && Readable(static_cast<uint32_t>(reinterpret_cast<uintptr_t>(vtable)) + 0x68, sizeof(uint32_t))) {
        // +0x68 is show/layout, +0x64 is hide/clear.
        std::memcpy(&show, reinterpret_cast<const void*>(
                        reinterpret_cast<uintptr_t>(vtable) + 0x68), sizeof(show));
        std::memcpy(&hide, reinterpret_cast<const void*>(
                        reinterpret_cast<uintptr_t>(vtable) + 0x64), sizeof(hide));
    }
    if (show != nullptr) {
        show(reinterpret_cast<void*>(static_cast<uintptr_t>(bar)));
    }
    if (hide != nullptr) {
        hide(reinterpret_cast<void*>(static_cast<uintptr_t>(bar)));
    }

    initCount_.fetch_add(1);
}

uint32_t StatusBarManager::UnitOwner(uint32_t unit) const {
    if (!Readable(unit, sizeof(uint32_t))) {
        return 0xFFFFFFFFu;
    }

    uint32_t vtable = 0;
    std::memcpy(&vtable, reinterpret_cast<const void*>(static_cast<uintptr_t>(unit)),
                sizeof(vtable));
    if (vtable == 0 || !Readable(vtable + 0xEC, sizeof(uint32_t))) {
        return 0xFFFFFFFFu;
    }

    uint32_t fn = 0;
    std::memcpy(&fn, reinterpret_cast<const void*>(static_cast<uintptr_t>(vtable + 0xEC)),
                sizeof(fn));
    if (fn == 0) {
        return 0xFFFFFFFFu;
    }

    UnitOwnerFn owner = reinterpret_cast<UnitOwnerFn>(static_cast<uintptr_t>(fn));
    return static_cast<uint32_t>(owner(reinterpret_cast<void*>(static_cast<uintptr_t>(unit))));
}

bool StatusBarManager::IsAllied(uint32_t unit) {
    if (forceHealthBar_.load()) {
        // 1.20e has no verified player-attribute/integer-getter chain. The
        // function we used to guess as IntegerGetValue (0x88E10) is a handle
        // resolver that dereferences [0x78] on an invalid handle and crashes.
        // U9's mb.dll does not filter by relation at all. For single-player
        // the useful distinction is self vs. everyone else.
        const uint64_t now = GetTickCount64();
        uint32_t localIndex = localPlayerIndex_.load();
        if (localIndex >= 24 || now - allyMaskAt_.load() > 1000) {
            uint32_t gameObject = 0;
            if (Readable(pointers_.getLocalPlayer, sizeof(uint32_t))) {
                std::memcpy(&gameObject,
                            reinterpret_cast<const void*>(static_cast<uintptr_t>(
                                pointers_.getLocalPlayer)),
                            sizeof(gameObject));
            }
            if (gameObject != 0 && Readable(gameObject + 0x34, sizeof(uint32_t))) {
                uint32_t player = 0;
                std::memcpy(&player,
                            reinterpret_cast<const void*>(static_cast<uintptr_t>(
                                gameObject + 0x34)),
                            sizeof(player));
                if (player != 0 && Readable(player + 0x30, 1)) {
                    uint8_t index = 0;
                    std::memcpy(&index,
                                reinterpret_cast<const void*>(static_cast<uintptr_t>(
                                    player + 0x30)),
                                sizeof(index));
                    // On 1.20e this byte does not hold a valid player slot
                    // (observed 0x20). Single-player maps put the local hero on
                    // slot 0, so use that as the fallback.
                    if (index >= 24) {
                        index = 0;
                    }
                    localIndex = index;
                    localPlayerIndex_.store(index);
                }
            }
            allyMaskAt_.store(now);
        }
        if (localIndex >= 24) {
            relationFailCount_.fetch_add(1);
            return false;
        }
        return UnitOwner(unit) == localIndex;
    }

    const uint64_t now = GetTickCount64();
    uint32_t mask = allyMask_.load();
    if (mask == 0 || now - allyMaskAt_.load() > 1000) {
        if (pointers_.getLocalPlayer != 0 &&
            pointers_.cPlayerAttr != 0 && pointers_.integerGetValue != 0) {
            void* player = nullptr;

            if (pointers_.getCPlayerWar3 != 0) {
                GetLocalPlayerFn getLocalPlayer = reinterpret_cast<GetLocalPlayerFn>(
                    static_cast<uintptr_t>(pointers_.getLocalPlayer));
                GetCPlayerWar3Fn getCPlayer = reinterpret_cast<GetCPlayerWar3Fn>(
                    static_cast<uintptr_t>(pointers_.getCPlayerWar3));
                void* handle = getLocalPlayer();
                if (handle != nullptr) {
                    player = getCPlayer(handle);
                }
            } else {
                // 1.20e: gameObject global -> +0x34 is the local CPlayerWar3.
                uint32_t gameObject = 0;
                if (Readable(pointers_.getLocalPlayer, sizeof(uint32_t))) {
                    std::memcpy(&gameObject,
                                reinterpret_cast<const void*>(static_cast<uintptr_t>(
                                    pointers_.getLocalPlayer)),
                                sizeof(gameObject));
                }
                if (gameObject != 0 && Readable(gameObject + 0x34, sizeof(uint32_t))) {
                    uint32_t localPlayer = 0;
                    std::memcpy(&localPlayer,
                                reinterpret_cast<const void*>(static_cast<uintptr_t>(
                                    gameObject + 0x34)),
                                sizeof(localPlayer));
                    player = reinterpret_cast<void*>(static_cast<uintptr_t>(localPlayer));
                }
            }

            if (player != nullptr) {
                const uint32_t playerAddress = static_cast<uint32_t>(
                    reinterpret_cast<uintptr_t>(player));
                if (Readable(playerAddress + 0x30, 1)) {
                    uint8_t playerIndex = 0;
                    std::memcpy(&playerIndex,
                                reinterpret_cast<const void*>(static_cast<uintptr_t>(
                                    playerAddress + 0x30)),
                                sizeof(playerIndex));
                    localPlayerIndex_.store(playerIndex);
                }

                GetPlayerAttrFn getAttr = reinterpret_cast<GetPlayerAttrFn>(
                    static_cast<uintptr_t>(pointers_.cPlayerAttr));
                IntegerGetValueFn getValue = reinterpret_cast<IntegerGetValueFn>(
                    static_cast<uintptr_t>(pointers_.integerGetValue));
                void* attr = getAttr(player);
                if (attr != nullptr) {
                    const uint32_t newMask = static_cast<uint32_t>(getValue(
                        reinterpret_cast<void*>(reinterpret_cast<uintptr_t>(attr) + 0x38)));
                    allyMask_.store(newMask);
                    allyMaskAt_.store(now);
                    mask = newMask;
                }
            }
        }
    }

    if (mask == 0) {
        relationFailCount_.fetch_add(1);
        return false;
    }

    const uint32_t owner = UnitOwner(unit);
    if (owner >= 24) {
        relationFailCount_.fetch_add(1);
        return false;
    }

    return (mask & (1u << owner)) != 0;
}

void StatusBarManager::HideManaBar(uint32_t bar) const {
    void** vtable = nullptr;
    if (Readable(bar, sizeof(uint32_t))) {
        std::memcpy(&vtable, reinterpret_cast<const void*>(static_cast<uintptr_t>(bar)),
                    sizeof(vtable));
    }
    if (vtable == nullptr || !Readable(static_cast<uint32_t>(reinterpret_cast<uintptr_t>(vtable)) + 0x64, sizeof(uint32_t))) {
        return;
    }

    uint32_t fn = 0;
    std::memcpy(&fn, reinterpret_cast<const void*>(
                    reinterpret_cast<uintptr_t>(vtable) + 0x64), sizeof(fn));
    if (fn != 0) {
        StatBarMethodFn hide = reinterpret_cast<StatBarMethodFn>(static_cast<uintptr_t>(fn));
        hide(reinterpret_cast<void*>(static_cast<uintptr_t>(bar)));
    }
}

void StatusBarManager::ShowManaBar(uint32_t bar) const {
    void** vtable = nullptr;
    if (Readable(bar, sizeof(uint32_t))) {
        std::memcpy(&vtable, reinterpret_cast<const void*>(static_cast<uintptr_t>(bar)),
                    sizeof(vtable));
    }
    if (vtable == nullptr || !Readable(static_cast<uint32_t>(reinterpret_cast<uintptr_t>(vtable)) + 0x68, sizeof(uint32_t))) {
        return;
    }

    uint32_t fn = 0;
    std::memcpy(&fn, reinterpret_cast<const void*>(
                    reinterpret_cast<uintptr_t>(vtable) + 0x68), sizeof(fn));
    if (fn != 0) {
        StatBarMethodFn show = reinterpret_cast<StatBarMethodFn>(static_cast<uintptr_t>(fn));
        show(reinterpret_cast<void*>(static_cast<uintptr_t>(bar)));
    }
}

bool StatusBarManager::IsVisibleToLocalPlayer(uint32_t unit) const {
    const uint32_t localPlayer = localPlayerIndex_.load();
    if (localPlayer >= 24) {
        // Player index not cached yet; do not hide everything during the
        // first second of a map.
        return true;
    }

    uint32_t vtable = 0;
    if (!Readable(unit, sizeof(uint32_t))) {
        return false;
    }
    std::memcpy(&vtable, reinterpret_cast<const void*>(static_cast<uintptr_t>(unit)),
                sizeof(vtable));
    if (vtable == 0 || !Readable(vtable + 0xFC, sizeof(uint32_t))) {
        return false;
    }

    uint32_t fn = 0;
    std::memcpy(&fn, reinterpret_cast<const void*>(static_cast<uintptr_t>(vtable + 0xFC)),
                sizeof(fn));
    if (fn == 0) {
        return false;
    }

    typedef int (__thiscall *IsVisibleFn)(void*, int, int, int);
    IsVisibleFn isVisible = reinterpret_cast<IsVisibleFn>(static_cast<uintptr_t>(fn));
    return isVisible(reinterpret_cast<void*>(static_cast<uintptr_t>(unit)),
                     static_cast<int>(localPlayer), 0, 4) != 0;
}

void StatusBarManager::OnUnitUpdate(uint32_t unit) {
    if (!installed_.load()) {
        return;
    }

    const uint64_t now = GetTickCount64();
    const uint64_t previousTick = lastUpdateTick_.exchange(now);
    if (g_hbActive != 0 && (previousTick == 0 || now - previousTick > 2000)) {
        // First update after a map load / reload: re-assert the forced bar
        // state once. The game's per-frame layout pass keeps it up to date.
        g_hbApply = 1;
    }

    if (healthEnabled_.load() && pointers_.healthFlagOffset != 0) {
        const uint32_t cguiGlobal = pointers_.cguiGlobal;
        if (cguiGlobal != 0 && Readable(cguiGlobal, sizeof(uint32_t))) {
            uint32_t cgui = 0;
            std::memcpy(&cgui, reinterpret_cast<const void*>(static_cast<uintptr_t>(cguiGlobal)),
                        sizeof(cgui));
            if (cgui != 0) {
                SafeWrite32(cgui + pointers_.healthFlagOffset, 1);
            }
        }
    }

    if (!friendlyMana_.load() && !enemyMana_.load()) {
        return;
    }
    if (unit == 0 || !Readable(unit, 0x54)) {
        return;
    }

    updateCount_.fetch_add(1);

    uint32_t container = 0;
    std::memcpy(&container, reinterpret_cast<const void*>(static_cast<uintptr_t>(
                    unit + kUnitContainerOffset)), sizeof(container));
    if (container == 0 || !Readable(container + kBarOffsetFromContainer, sizeof(uint32_t))) {
        return;
    }

    uint32_t healthBar = 0;
    std::memcpy(&healthBar, reinterpret_cast<const void*>(static_cast<uintptr_t>(
                    container + kBarOffsetFromContainer)), sizeof(healthBar));
    if (healthBar == 0 || !Readable(healthBar + kManaOffset, sizeof(uint32_t))) {
        return;
    }

    const uint32_t manaBar = healthBar + kManaOffset;

    // 1.20e's CStatBar set-unit and GetUnitState(state=3) both resolve the
    // unit handle and can null-deref at Game.dll+0x92770 when it is absent
    // from the game's unit table (replay/observer units). Validate before any
    // engine call that can take this unit.
    const bool is120Layout = pointers_.getUnitUIDef == 0 && pointers_.unitScale == 0;
    if (is120Layout && !IsUnitStateTargetValid120(unit)) {
        if (!invalidUnitLogged_.exchange(true)) {
            WriteLog("skip invalid 1.20e mana unit (unit table miss)");
        }
        HideManaBar(manaBar);
        hiddenCount_.fetch_add(1);
        return;
    }

    const uint32_t fakeVtable = static_cast<uint32_t>(
        reinterpret_cast<uintptr_t>(&fakeVtable_[0]));
    uint32_t healthVtable = 0;
    if (!Readable(healthBar, sizeof(uint32_t))) {
        return;
    }
    std::memcpy(&healthVtable, reinterpret_cast<const void*>(static_cast<uintptr_t>(healthBar)),
                sizeof(healthVtable));

    if (g_sbOriginalVtable == nullptr) {
        if (healthVtable == 0 || !Readable(healthVtable, sizeof(fakeVtable_))) {
            return;
        }
        std::memcpy(fakeVtable_, reinterpret_cast<const void*>(static_cast<uintptr_t>(healthVtable)),
                    sizeof(fakeVtable_));
        fakeVtable_[0x64 / sizeof(void*)] = reinterpret_cast<void*>(&StatusBarFakeHide);
        fakeVtable_[0x68 / sizeof(void*)] = reinterpret_cast<void*>(&StatusBarFakeShow);
        g_sbOriginalVtable = reinterpret_cast<void*>(static_cast<uintptr_t>(healthVtable));
        WriteLog("fake vtable installed from first health bar");
    }

    const uint32_t originalVtable = static_cast<uint32_t>(
        reinterpret_cast<uintptr_t>(g_sbOriginalVtable));
    if (healthVtable != originalVtable && healthVtable != fakeVtable) {
        return;
    }

    uint32_t vtable = 0;
    std::memcpy(&vtable, reinterpret_cast<const void*>(static_cast<uintptr_t>(manaBar)),
                sizeof(vtable));
    if (vtable != originalVtable) {
        // This bar was allocated before the hook was installed (or is not a
        // CStatBar); there is no second bar at +0x158 to drive.
        return;
    }

    if (healthVtable == originalVtable) {
        SafeWrite32(healthBar, fakeVtable);
    }

    if (forceHealthBar_.load() && vtable != 0 && Readable(vtable + 0x74, sizeof(uint32_t))) {
        // U9's 1.20e update binds the mana bar to the unit through vtable+0x74
        // before laying it out. Without this link the bar exists but is not
        // rendered/anchored as a unit frame.
        uint32_t setUnitFn = 0;
        std::memcpy(&setUnitFn, reinterpret_cast<const void*>(static_cast<uintptr_t>(
                        vtable + 0x74)), sizeof(setUnitFn));
        if (setUnitFn != 0) {
            StatBarSetUnitFn setUnit = reinterpret_cast<StatBarSetUnitFn>(
                static_cast<uintptr_t>(setUnitFn));
            setUnit(reinterpret_cast<void*>(static_cast<uintptr_t>(manaBar)),
                    reinterpret_cast<void*>(static_cast<uintptr_t>(unit)));
        }
    }

    bool structure = false;
    if (!forceHealthBar_.load()) {
        IsUnitStructureFn isStructure = reinterpret_cast<IsUnitStructureFn>(
            static_cast<uintptr_t>(pointers_.isUnitStructure));
        if (isStructure != nullptr) {
            structure = isStructure(reinterpret_cast<void*>(static_cast<uintptr_t>(unit)), 0) != 0;
        } else if (Readable(unit + 0x5C, sizeof(uint32_t))) {
            uint32_t flags = 0;
            std::memcpy(&flags, reinterpret_cast<const void*>(static_cast<uintptr_t>(unit + 0x5C)),
                        sizeof(flags));
            structure = (flags & 0x10000u) != 0;
        }
    }
    if (structure) {
        HideManaBar(manaBar);
        hiddenCount_.fetch_add(1);
        return;
    }

    GetUnitStateFn getState = reinterpret_cast<GetUnitStateFn>(
        static_cast<uintptr_t>(pointers_.getUnitState));
    if (getState == nullptr) {
        return;
    }

    float life = 0.0f;
    getState(reinterpret_cast<void*>(static_cast<uintptr_t>(unit)), &life, 0);
    if (!(life > 0.0f)) {
        HideManaBar(manaBar);
        hiddenCount_.fetch_add(1);
        return;
    }

    float maxMana = 0.0f;
    getState(reinterpret_cast<void*>(static_cast<uintptr_t>(unit)), &maxMana, 3);
    if (!(maxMana > 0.0f)) {
        HideManaBar(manaBar);
        hiddenCount_.fetch_add(1);
        return;
    }

    float currentMana = 0.0f;
    getState(reinterpret_cast<void*>(static_cast<uintptr_t>(unit)), &currentMana, 2);

    const bool allied = IsAllied(unit);
    // 1.20e only reaches this point for units the game itself is drawing an HP
    // bar for, so a visibility query (whose vtable slot differs on this build)
    // is unnecessary and can hide valid bars.
    const bool visible = forceHealthBar_.load() || IsVisibleToLocalPlayer(unit);
    if (!visible) {
        HideManaBar(manaBar);
        hiddenCount_.fetch_add(1);
        return;
    }

    const bool show = allied ? friendlyMana_.load() : enemyMana_.load();
    if (!show) {
        HideManaBar(manaBar);
        hiddenCount_.fetch_add(1);
        return;
    }

    float x = 0.0f;
    float y = 0.0f;
    if (forceHealthBar_.load()) {
        // 1.20e: U9's mb.dll asks CUnit for its world position. The bar-child
        // chain used on 1.24e/1.27a does not hold on this build.
        const uint32_t getPositionAddress = gameBase_.load() + kGetUnitPositionRva120;
        if (getPositionAddress == 0) {
            return;
        }
        GetUnitPositionFn getPosition =
            reinterpret_cast<GetUnitPositionFn>(static_cast<uintptr_t>(getPositionAddress));
        float outXY[2] = {0.0f, 0.0f};
        float outZ = 0.0f;
        getPosition(reinterpret_cast<void*>(static_cast<uintptr_t>(unit)), outXY, &outZ);
        x = outXY[0];
        y = outXY[1] - kManaYOffset;
    } else {
        // Same position source as KK: the health bar's main position object.
        // [unit+0x50] -> +0x0C (health bar) -> +0x0C (position object) -> +4/+8.
        uint32_t positionChild = 0;
        if (!Readable(healthBar + kChildOffsetFromBar, sizeof(uint32_t))) {
            return;
        }
        std::memcpy(&positionChild, reinterpret_cast<const void*>(static_cast<uintptr_t>(
                        healthBar + kChildOffsetFromBar)), sizeof(positionChild));
        if (positionChild == 0 || !Readable(positionChild + 4, sizeof(float) * 2)) {
            return;
        }

        std::memcpy(&x, reinterpret_cast<const void*>(static_cast<uintptr_t>(positionChild + 4)),
                    sizeof(x));
        std::memcpy(&y, reinterpret_cast<const void*>(static_cast<uintptr_t>(positionChild + 8)),
                    sizeof(y));
        y -= kManaYOffset;
    }

    float scale = 1.0f;
    uint32_t typeId = 0;
    if (Readable(unit + 0x30, sizeof(uint32_t))) {
        std::memcpy(&typeId,
                    reinterpret_cast<const void*>(static_cast<uintptr_t>(unit + 0x30)),
                    sizeof(typeId));
    }

    if (pointers_.unitScale != 0) {
        g_sbUnitScaleFn = reinterpret_cast<void*>(static_cast<uintptr_t>(pointers_.unitScale));
        const float unitScale = StatusBarUnitScaleCall(typeId);
        if (unitScale > 0.0f) {
            scale = unitScale;
        }
    } else {
        GetUnitUIDefFn getUIDef = reinterpret_cast<GetUnitUIDefFn>(
            static_cast<uintptr_t>(pointers_.getUnitUIDef));
        if (getUIDef != nullptr && typeId != 0) {
            void* uiDef = getUIDef(typeId);
            if (uiDef != nullptr &&
                Readable(static_cast<uint32_t>(reinterpret_cast<uintptr_t>(uiDef) + 0x54),
                         sizeof(float))) {
                std::memcpy(&scale, reinterpret_cast<const void*>(
                                reinterpret_cast<uintptr_t>(uiDef) + 0x54), sizeof(scale));
            }
        }
    }

    // Use CStatBar's own progress setter (vtable+0x6C) so the fill grows from
    // the left over the bar's dark background, exactly like the health bar.
    // The base ctor defaults max to 100; set min/max to the unit's mana range
    // and pass the current mana.
    const float minMana = 0.0f;
    std::memcpy(reinterpret_cast<void*>(static_cast<uintptr_t>(manaBar + 0x128)),
                &minMana, sizeof(minMana));
    std::memcpy(reinterpret_cast<void*>(static_cast<uintptr_t>(manaBar + 0x12C)),
                &maxMana, sizeof(maxMana));
    uint32_t valueFn = 0;
    if (Readable(manaBar, sizeof(uint32_t))) {
        uint32_t manaVtable = 0;
        std::memcpy(&manaVtable,
                    reinterpret_cast<const void*>(static_cast<uintptr_t>(manaBar)),
                    sizeof(manaVtable));
        if (manaVtable != 0 && Readable(manaVtable + 0x6C, sizeof(uint32_t))) {
            std::memcpy(&valueFn,
                        reinterpret_cast<const void*>(static_cast<uintptr_t>(manaVtable + 0x6C)),
                        sizeof(valueFn));
        }
    }
    if (valueFn != 0) {
        StatBarValueFn setValue = reinterpret_cast<StatBarValueFn>(
            static_cast<uintptr_t>(valueFn));
        setValue(reinterpret_cast<void*>(static_cast<uintptr_t>(manaBar)), currentMana);
    }

    // Match the unit's actual health-bar width. Heroes and some special units
    // do not use the plain UIDef scale, so the KK formula alone can produce a
    // different width. Fall back to the scale formula only if the health-bar
    // width is unreadable.
    float healthWidth = 0.0f;
    if (Readable(healthBar + kBarWidthOffset, sizeof(float))) {
        std::memcpy(&healthWidth,
                    reinterpret_cast<const void*>(static_cast<uintptr_t>(
                        healthBar + kBarWidthOffset)),
                    sizeof(healthWidth));
    }
    const float width = healthWidth > 0.0001f ? healthWidth : scale * kManaWidthScale;
    std::memcpy(reinterpret_cast<void*>(static_cast<uintptr_t>(manaBar + kBarWidthOffset)),
                &width, sizeof(width));

    SetBarBoundsFn setBounds = reinterpret_cast<SetBarBoundsFn>(
        static_cast<uintptr_t>(pointers_.setBarBounds));
    SetBarPositionFn setPosition = reinterpret_cast<SetBarPositionFn>(
        static_cast<uintptr_t>(pointers_.setBarPosition));
    if (setBounds != nullptr) {
        setBounds(reinterpret_cast<void*>(static_cast<uintptr_t>(manaBar)), 0);
    }
    if (setPosition != nullptr) {
        setPosition(reinterpret_cast<void*>(static_cast<uintptr_t>(manaBar)), 1, x, y, 1);
    }
    if (forceHealthBar_.load()) {
        // 1.20e's CStatBar show (vtable+0x68) early-returns unless this field
        // is set. The game sets it on the health bar right before showing it;
        // do the same for the mana bar.
        SafeWrite32(manaBar + kBarShowEnabledOffset, 1);
    }
    ShowManaBar(manaBar);
    shownCount_.fetch_add(1);
}

}  // namespace iwk
