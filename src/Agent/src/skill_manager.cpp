#include "skill_manager.h"

#include <algorithm>
#include <cctype>
#include <cstring>
#include <fstream>
#include <sstream>
#include <string>

#include <timeapi.h>

#pragma comment(lib, "winmm.lib")

namespace iwk {

namespace {

// CGameUI current-mode field and the target-mode singleton. These offsets are
// shared by 1.24e and 1.27a (verified against both Game.dll builds).
constexpr unsigned int kCurrentModeOffset = 0x1B4;
constexpr unsigned int kTargetModeOffset = 0x210;
// Quick-cast window from the key press: enough for the game to switch to
// target mode and for the command-card cancel button to appear.
constexpr uint64_t kQuickCastTimeoutMs = 500;

std::mutex g_logMutex;

std::string LogFilePath() {
    static std::string path = [] {
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
        std::string result(static_cast<size_t>(std::max(0, size)), '\0');
        if (size > 0) {
            WideCharToMultiByte(CP_ACP, 0, wide.c_str(), static_cast<int>(wide.size()),
                                &result[0], size, nullptr, nullptr);
        }
        return result;
    }();
    return path;
}

void Log(const std::string& message) {
    std::lock_guard<std::mutex> lock(g_logMutex);
    std::ofstream file(LogFilePath(), std::ios::app);
    if (file) {
        file << GetTickCount64() << " " << message << "\n";
    }
}

bool IsReadable(uint32_t address, size_t size) {
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

        uint64_t base = reinterpret_cast<uintptr_t>(info.BaseAddress);
        uint64_t end = base + info.RegionSize;
        if (end <= current) {
            return false;
        }

        size_t chunk = static_cast<size_t>(std::min<uint64_t>(remaining, end - current));
        current += chunk;
        remaining -= chunk;
    }
    return true;
}

template <typename T>
bool ReadValue(uint32_t address, T& value) {
    if (!IsReadable(address, sizeof(T))) {
        return false;
    }
    std::memcpy(&value, reinterpret_cast<const void*>(static_cast<uintptr_t>(address)), sizeof(T));
    return true;
}

}  // namespace

SkillManager::SkillManager()
    : profileValid_(false),
      unitListRva_(0),
      cguiRva_(0),
      selectionOffset_(0),
      activeUnitOffset_(0),
      commandBarOffset_(0),
      commandGridOffset_(0),
      commandFrameButtonOffset_(0),
      commandButtonHotkeyOffset_(0),
      commandCardMode_(false),
      infoEnabled_(false),
      keys_(12, 0),
      quickCast_(),
      itemQuickCast_(),
      quickCastStop_(false),
      quickCastArmed_(false),
      quickCastModeEligible_(false),
      quickCastArmedAt_(0),
      quickCastCardCheckAt_(0),
      quickCastCguiRva_(0),
      quickCastWindow_(nullptr),
      quickCastEvent_(nullptr) {}

SkillManager::~SkillManager() {
    quickCastStop_ = true;
    if (quickCastEvent_ != nullptr) {
        SetEvent(quickCastEvent_);
    }
    if (quickCastThread_.joinable()) {
        quickCastThread_.join();
    }
    if (quickCastEvent_ != nullptr) {
        CloseHandle(quickCastEvent_);
        quickCastEvent_ = nullptr;
    }
}

void SkillManager::Start() {
    Log("skill manager started");
    quickCastStop_ = false;
    quickCastEvent_ = CreateEventW(nullptr, FALSE, FALSE, nullptr);
    quickCastThread_ = std::thread(&SkillManager::QuickCastLoop, this);
}

void SkillManager::SetProfile(bool valid, unsigned int unitListRva, unsigned int cguiRva,
                              unsigned int selectionOffset, unsigned int activeUnitOffset,
                              unsigned int commandBarOffset, unsigned int commandGridOffset,
                              unsigned int commandFrameButtonOffset,
                              unsigned int commandButtonHotkeyOffset) {
    {
        std::lock_guard<std::mutex> lock(mutex_);
        profileValid_ = valid;
        unitListRva_ = unitListRva;
        cguiRva_ = cguiRva;
        selectionOffset_ = selectionOffset;
        activeUnitOffset_ = activeUnitOffset;
        commandBarOffset_ = commandBarOffset;
        commandGridOffset_ = commandGridOffset;
        commandFrameButtonOffset_ = commandFrameButtonOffset;
        commandButtonHotkeyOffset_ = commandButtonHotkeyOffset;
        commandCardMode_ = valid && cguiRva_ != 0 && commandBarOffset_ != 0 &&
                           commandGridOffset_ != 0 && commandFrameButtonOffset_ != 0 &&
                           commandButtonHotkeyOffset_ != 0;
        quickCastCguiRva_ = commandCardMode_ ? cguiRva_ : 0;
        quickCastArmed_ = false;
        quickCastModeEligible_ = false;
    }

    std::ostringstream stream;
    stream << "skill profile valid=" << (valid ? "1" : "0")
           << " unitListRva=0x" << std::hex << unitListRva
           << " cguiRva=0x" << cguiRva
           << " selectionOffset=0x" << selectionOffset
           << " activeUnitOffset=0x" << activeUnitOffset
           << " commandBarOffset=0x" << commandBarOffset
           << " commandGridOffset=0x" << commandGridOffset
           << " commandFrameButtonOffset=0x" << commandFrameButtonOffset
           << " commandButtonHotkeyOffset=0x" << commandButtonHotkeyOffset
           << " cardMode=" << (commandCardMode_ ? "1" : "0");
    Log(stream.str());
}

int SkillManager::SetBindings(const std::vector<int>& keysBySlot,
                              const std::vector<int>& quickCastBySlot) {
    int valid = 0;
    int quick = 0;
    std::lock_guard<std::mutex> lock(mutex_);
    keys_.assign(12, 0);
    quickCast_.fill(false);
    for (size_t i = 0; i < keys_.size() && i < keysBySlot.size(); ++i) {
        int key = keysBySlot[i];
        if (key >= 'a' && key <= 'z') {
            key -= 'a' - 'A';
        }
        if ((key >= 'A' && key <= 'Z') || (key >= '0' && key <= '9')) {
            keys_[i] = key;
            ++valid;
            if (i < quickCastBySlot.size() && quickCastBySlot[i] != 0) {
                quickCast_[i] = true;
                ++quick;
            }
        }
    }

    Log("skill bindings valid=" + std::to_string(valid) +
        " quick=" + std::to_string(quick));
    return valid;
}

void SkillManager::SetInfoEnabled(bool enabled) {
    bool changed = false;
    {
        std::lock_guard<std::mutex> lock(mutex_);
        changed = (infoEnabled_ != enabled);
        infoEnabled_ = enabled;
    }

    if (!enabled) {
        DisarmQuickCast();
    }

    if (changed) {
        Log(std::string("skill info enabled=") + (enabled ? "1" : "0"));
    }
}

void SkillManager::SetGameWindow(HWND hwnd) {
    quickCastWindow_ = hwnd;
}

void SkillManager::SetItemQuickCast(const std::vector<int>& quickCastBySlot) {
    int quick = 0;
    std::lock_guard<std::mutex> lock(mutex_);
    itemQuickCast_.fill(false);
    for (size_t i = 0; i < itemQuickCast_.size() && i < quickCastBySlot.size(); ++i) {
        if (quickCastBySlot[i] != 0) {
            itemQuickCast_[i] = true;
            ++quick;
        }
    }

    Log("item quick-cast flags=" + std::to_string(quick));
}

void SkillManager::ArmItemQuickCast(int virtualKey) {
    int slot = -1;
    switch (virtualKey) {
        case VK_NUMPAD7: slot = 0; break;
        case VK_NUMPAD8: slot = 1; break;
        case VK_NUMPAD4: slot = 2; break;
        case VK_NUMPAD5: slot = 3; break;
        case VK_NUMPAD1: slot = 4; break;
        case VK_NUMPAD2: slot = 5; break;
        default: return;
    }

    std::lock_guard<std::mutex> lock(mutex_);
    if (itemQuickCast_[static_cast<size_t>(slot)]) {
        ArmQuickCast();
    }
}

int SkillManager::MapKey(int virtualKey, int& nativeKey, int& uiSlot) {
    // A new key event cancels any pending quick cast, matching the game's own
    // per-event flag reset.
    DisarmQuickCast();
    std::lock_guard<std::mutex> lock(mutex_);
    if (!infoEnabled_ || virtualKey <= 0 || virtualKey > 0xFF || !commandCardMode_) {
        return 0;
    }

    std::array<uint32_t, 12> card{};
    std::array<uint32_t, 12> commandIds{};
    if (!ReadCommandCard(card, commandIds)) {
        return 0;
    }

    bool hasLearnCancel = false;
    for (uint32_t commandId : commandIds) {
        if (commandId == 0xD0020 || commandId == 0xD0007) {
            hasLearnCancel = true;
            break;
        }
    }

    // Identify the learn page by its cancel button plus a button whose
    // command ID belongs to the active hero's AHre skill list. This prevents
    // ordinary build/shop cancel pages from using the learn-slot layout.
    bool learnPage = false;
    if (hasLearnCancel) {
        std::vector<uint32_t> heroIds;
        if (ReadHeroAbilityIds(heroIds)) {
            for (uint32_t heroId : heroIds) {
                for (uint32_t commandId : commandIds) {
                    if (commandId == heroId) {
                        learnPage = true;
                        break;
                    }
                }
                if (learnPage) {
                    break;
                }
            }
        }
    }

    for (size_t slot = 0; slot < keys_.size(); ++slot) {
        int configured = keys_[slot];
        if (configured == 0 || configured != virtualKey) {
            continue;
        }

        size_t column = slot % 4;
        size_t row;
        if (learnPage) {
            // Learn-page skill buttons are in the top command-card row.
            row = slot < 4 ? 0 : (2 - slot / 4);
        } else {
            // Normal command card: SKILLS is bottom-row-first, cache is
            // column-major with the bottom row at row 2.
            row = 2 - slot / 4;
        }

        size_t cacheIndex = column * 3 + row;
        uiSlot = static_cast<int>((2 - slot / 4) * 4 + slot % 4);

        int mapped = static_cast<int>(card[cacheIndex] & 0xFF);
        if (mapped < 0x20 || mapped > 0xFE) {
            // Configured slot exists but has no usable hotkey. Swallow the
            // physical key instead of letting it trigger another command.
            return 3;
        }

        if (quickCast_[slot]) {
            ArmQuickCast();
        }

        if (mapped == virtualKey) {
            return 2;
        }

        nativeKey = mapped;
        return 1;
    }

    return 0;
}

void SkillManager::ArmQuickCast() {
    if (!commandCardMode_ || cguiRva_ == 0) {
        return;
    }

    // 1.24e/1.27a expose the current/target UI mode through these CGameUI
    // fields. 1.20e has a different engine layout, so those fields are not
    // reliable there. Always arm and let QuickCastLoop use the command-card
    // cancel button as the target-mode signal as well.
    bool modeEligible = false;
    HMODULE game = GetModuleHandleW(L"Game.dll");
    if (game != nullptr) {
        const uint32_t base = static_cast<uint32_t>(reinterpret_cast<uintptr_t>(game));
        uint32_t cgui = 0;
        uint32_t current = 0;
        uint32_t target = 0;
        if (ReadValue(base + cguiRva_, cgui) && cgui != 0 &&
            ReadValue(cgui + kCurrentModeOffset, current) &&
            ReadValue(cgui + kTargetModeOffset, target) &&
            target != 0 && current != target) {
            modeEligible = true;
        }
    }

    const uint64_t now = GetTickCount64();
    quickCastModeEligible_ = modeEligible;
    quickCastArmedAt_ = now;
    quickCastCardCheckAt_ = 0;
    quickCastArmed_ = true;
    if (quickCastEvent_ != nullptr) {
        SetEvent(quickCastEvent_);
    }
}

void SkillManager::DisarmQuickCast() {
    quickCastArmed_ = false;
}

bool SkillManager::PostQuickCastClick() {
    HWND window = quickCastWindow_.load();
    if (window == nullptr || !IsWindow(window)) {
        window = GetForegroundWindow();
    }
    if (window == nullptr) {
        return false;
    }

    DWORD pid = 0;
    GetWindowThreadProcessId(window, &pid);
    if (pid != GetCurrentProcessId()) {
        return false;
    }

    POINT point{};
    if (!GetCursorPos(&point) || !ScreenToClient(window, &point)) {
        return false;
    }

    RECT client{};
    if (!GetClientRect(window, &client) ||
        point.x < 0 || point.y < 0 || point.x >= client.right || point.y >= client.bottom) {
        return false;
    }

    const LPARAM position = MAKELPARAM(point.x, point.y);
    PostMessageW(window, WM_LBUTTONDOWN, 0, position);
    PostMessageW(window, WM_LBUTTONUP, 0, position);
    return true;
}

void SkillManager::QuickCastLoop() {
    // Raise the process timer resolution so the 1 ms waits below are real
    // rather than the default ~15.6 ms tick.
    timeBeginPeriod(1);
    int spin = 0;

    while (!quickCastStop_) {
        if (!quickCastArmed_) {
            spin = 0;
            if (quickCastEvent_ != nullptr) {
                WaitForSingleObject(quickCastEvent_, 50);
            } else {
                Sleep(2);
            }
            continue;
        }

        const uint64_t now = GetTickCount64();
        if (now - quickCastArmedAt_.load() > kQuickCastTimeoutMs) {
            quickCastArmed_ = false;
            continue;
        }

        bool clicked = false;

        if (quickCastModeEligible_) {
            const uint32_t cguiRva = quickCastCguiRva_.load();
            HMODULE game = GetModuleHandleW(L"Game.dll");
            uint32_t cgui = 0;
            uint32_t current = 0;
            uint32_t target = 0;
            if (cguiRva != 0 && game != nullptr) {
                const uint32_t base = static_cast<uint32_t>(reinterpret_cast<uintptr_t>(game));
                if (ReadValue(base + cguiRva, cgui) && cgui != 0 &&
                    ReadValue(cgui + kCurrentModeOffset, current) &&
                    ReadValue(cgui + kTargetModeOffset, target) &&
                    target != 0 && current == target) {
                    clicked = true;
                }
            }
        }

        // 1.20e does not expose the 1.24e current/target mode pair. A targeted
        // ability switches the command card to a cancel button (CmdCancel
        // 0xD0020); use that as the version-independent target-mode signal.
        // The first read is immediate (checkAt == 0); afterwards poll every
        // 5 ms until the target-mode cancel button shows up.
        const uint64_t lastCardCheck = quickCastCardCheckAt_.load();
        if (!clicked && (lastCardCheck == 0 || now - lastCardCheck >= 5)) {
            quickCastCardCheckAt_ = now;
            std::array<uint32_t, 12> hotkeys{};
            std::array<uint32_t, 12> commandIds{};
            bool cardValid = false;
            {
                std::lock_guard<std::mutex> lock(mutex_);
                if (quickCastArmed_ && commandCardMode_) {
                    cardValid = ReadCommandCard(hotkeys, commandIds);
                }
            }

            if (cardValid) {
                for (uint32_t commandId : commandIds) {
                    // Heroes expose the target-mode cancel as 0xD0020; plain
                    // units (e.g. 小冰龙) expose it as 0xD000B instead. Both
                    // mean the game is waiting for a target click.
                    if (commandId == 0xD0020 || commandId == 0xD000B) {
                        clicked = true;
                        break;
                    }
                }
            }
        }

        if (clicked) {
            PostQuickCastClick();
            quickCastArmed_ = false;
            continue;
        }

        if (spin < 8) {
            // Target mode normally appears during the same key event. Yield a
            // few times first so the click can land in the same frame.
            ++spin;
            SwitchToThread();
        } else {
            Sleep(1);
        }
    }

    timeEndPeriod(1);
}

void SkillManager::DisableAndRestore() {
    {
        std::lock_guard<std::mutex> lock(mutex_);
        infoEnabled_ = false;
        keys_.assign(12, 0);
        quickCast_.fill(false);
        itemQuickCast_.fill(false);
        quickCastArmed_ = false;
    }
    Log("skill manager disabled");
}

bool SkillManager::ReadHeroAbilityIds(std::vector<uint32_t>& ids) {
    ids.clear();

    HMODULE game = GetModuleHandleW(L"Game.dll");
    if (game == nullptr) {
        return false;
    }
    const uint32_t base = static_cast<uint32_t>(reinterpret_cast<uintptr_t>(game));

    uint32_t ahre = 0;
    if (unitListRva_ != 0) {
        // 1.24e and other unit-list profiles: resolve the selected unit
        // through the game's selection list, then read its AHre container.
        uint32_t a1 = 0;
        uint16_t a2Index = 0;
        uint32_t a2 = 0;
        uint32_t root = 0;
        uint32_t head = 0;
        uint32_t length = 0;
        uint32_t selected = 0;
        if (!ReadValue(base + unitListRva_, a1) || a1 == 0 ||
            !ReadValue(a1 + 0x28, a2Index) ||
            !ReadValue(a1 + 0x58 + 4 * static_cast<uint32_t>(a2Index), a2) || a2 == 0 ||
            !ReadValue(a2 + 0x34, root) || root == 0 ||
            !ReadValue(root + 0x1F0, head) || head == 0 ||
            !ReadValue(root + 0x1F8, length) || length == 0 ||
            !ReadValue(head + 8, selected) || selected == 0 ||
            !ReadValue(selected + 0x1F0, ahre) || ahre == 0) {
            return false;
        }
    } else if (cguiRva_ != 0 && selectionOffset_ != 0 && activeUnitOffset_ != 0) {
        uint32_t cgui = 0;
        uint32_t selection = 0;
        uint32_t unit = 0;
        if (!ReadValue(base + cguiRva_, cgui) || cgui == 0 ||
            !ReadValue(cgui + selectionOffset_, selection) || selection == 0 ||
            !ReadValue(selection + activeUnitOffset_, unit) || unit == 0 ||
            !ReadValue(unit + 0x1F0, ahre) || ahre == 0) {
            return false;
        }
    } else {
        return false;
    }

    uint32_t count = 0;
    if (!ReadValue(ahre + 0xF0, count) || count == 0 || count > 32) {
        return false;
    }

    ids.reserve(count);
    for (uint32_t i = 0; i < count; ++i) {
        uint32_t id = 0;
        if (!ReadValue(ahre + 0xF4 + i * 4, id) || id == 0 || id == 0xFFFFFFFF) {
            continue;
        }
        ids.push_back(id);
    }
    return !ids.empty();
}

bool SkillManager::ReadCommandCard(std::array<uint32_t, 12>& hotkeys,
                                   std::array<uint32_t, 12>& commandIds) {
    hotkeys.fill(0);
    commandIds.fill(0);

    auto fail = [](const char*, uint32_t = 0, uint32_t = 0) {
        return false;
    };

    HMODULE game = GetModuleHandleW(L"Game.dll");
    if (game == nullptr || cguiRva_ == 0) {
        return fail("game");
    }
    const uint32_t base = static_cast<uint32_t>(reinterpret_cast<uintptr_t>(game));

    uint32_t cgui = 0;
    uint32_t bar = 0;
    uint32_t grid = 0;
    if (!ReadValue(base + cguiRva_, cgui) || cgui == 0) {
        return fail("cgui", base + cguiRva_, cgui);
    }
    if (!ReadValue(cgui + commandBarOffset_, bar) || bar == 0) {
        return fail("bar", cgui, bar);
    }
    if (!ReadValue(bar + commandGridOffset_, grid) || grid == 0) {
        return fail("grid", bar, grid);
    }

    const uint32_t gridBase = grid + 8;
    bool anyButton = false;

    for (uint32_t row = 0; row < 3; ++row) {
        uint32_t rowArray = 0;
        if (!ReadValue(gridBase + row * 0x10, rowArray) || rowArray == 0) {
            continue;
        }

        for (uint32_t column = 0; column < 4; ++column) {
            uint32_t frame = 0;
            if (!ReadValue(rowArray + column * 4, frame) || frame == 0) {
                continue;
            }

            uint32_t flag94 = 0;
            uint32_t flag138 = 0;
            if (!ReadValue(frame + 0x94, flag94) || flag94 == 0 ||
                !ReadValue(frame + 0x138, flag138) || flag138 == 0) {
                continue;
            }

            uint32_t button = 0;
            if (!ReadValue(frame + commandFrameButtonOffset_, button) || button == 0) {
                continue;
            }

            uint32_t raw = 0;
            if (!ReadValue(button + commandButtonHotkeyOffset_, raw)) {
                continue;
            }

            // 1.27a/1.24e command or ability rawcode.
            uint32_t commandId = 0;
            ReadValue(button + 0x8, commandId);

            const size_t index = column * 3 + row;
            hotkeys[index] = raw;
            commandIds[index] = commandId;
            anyButton = true;
        }
    }

    if (!anyButton) {
        return fail("buttons", grid, 0);
    }
    return true;
}

}  // namespace iwk
