#include "camera_manager.h"

#include <algorithm>
#include <fstream>
#include <iomanip>
#include <mutex>
#include <sstream>

namespace iwk {

namespace {

// Same field id used by JASS CAMERA_FIELD_TARGET_DISTANCE.
constexpr int kDistanceField = 0;

std::mutex g_cameraLogMutex;

std::string CameraLogPath() {
    static std::string path = [] {
        wchar_t temp[MAX_PATH] = {};
        DWORD length = GetTempPathW(MAX_PATH, temp);
        std::wostringstream stream;
        stream << temp;
        if (length > 0 && temp[length - 1] != L'\\') {
            stream << L'\\';
        }
        stream << L"iwar3key-camera-" << GetCurrentProcessId() << L".log";
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

// Reads a pointer-sized value from the game only after validating the page.
// The game may be between maps, so every address is treated as untrusted.
bool ReadU32(uint32_t address, uint32_t& value) {
    if (address < 0x10000) {
        return false;
    }

    MEMORY_BASIC_INFORMATION info{};
    if (VirtualQuery(reinterpret_cast<LPCVOID>(static_cast<uintptr_t>(address)),
                     &info, sizeof(info)) == 0) {
        return false;
    }

    if (info.State != MEM_COMMIT ||
        (info.Protect & (PAGE_NOACCESS | PAGE_GUARD)) != 0) {
        return false;
    }

    __try {
        value = *reinterpret_cast<volatile uint32_t*>(static_cast<uintptr_t>(address));
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

// JASS native SetCameraField(field, value, duration): field is passed by
// value, value and duration are pointers.
using SetCameraFieldFn = void(__cdecl*)(int field, float* value, float* duration);

void CallCameraField(uint32_t address, int field, float value) {
    __try {
        float duration = 0.0f;
        auto setter = reinterpret_cast<SetCameraFieldFn>(address);
        setter(field, &value, &duration);
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        // The game may be tearing down a map; never take the process with us.
    }
}

}  // namespace

CameraManager::CameraManager()
    : gameBase_(0),
      imageSize_(0),
      profileValid_(false),
      setFieldRva_(0),
      cguiRva_(0),
      chatBarOffset_(0),
      textObjectOffset_(0),
      textBufferOffset_(0),
      readyState_(-1),
      configuredDistance_(3000),
      distance_(3000),
      zoomOutVk_(VK_PRIOR),
      zoomOutModifiers_(1),
      zoomInVk_(VK_NEXT),
      zoomInModifiers_(1),
      enabled_(false),
      applyPending_(false),
      appliedCount_(0),
      lastHotkeyAdjustTick_(0) {}

void CameraManager::SetGameBase(uint32_t base, uint32_t imageSize) {
    gameBase_ = base;
    imageSize_ = imageSize;
}

void CameraManager::SetProfile(bool valid, uint32_t setFieldRva,
                               uint32_t cguiRva, uint32_t chatBarOffset,
                               uint32_t textObjectOffset, uint32_t textBufferOffset) {
    profileValid_ = valid && setFieldRva != 0;
    setFieldRva_ = setFieldRva;
    cguiRva_ = cguiRva;
    chatBarOffset_ = chatBarOffset;
    textObjectOffset_ = textObjectOffset;
    textBufferOffset_ = textBufferOffset;
    readyState_ = -1;
    applyPending_ = enabled_.load();
    if (enabled_) {
        distance_ = configuredDistance_.load();
    }

    std::ostringstream stream;
    stream << "profile valid=" << (profileValid_ ? 1 : 0)
           << " setFieldRva=0x" << std::hex << std::uppercase << setFieldRva
           << " cguiRva=0x" << cguiRva
           << " chatBarOffset=0x" << chatBarOffset
           << " textObjectOffset=0x" << textObjectOffset
           << " textBufferOffset=0x" << textBufferOffset;
    Log(stream.str());
}

void CameraManager::SetEnabled(bool enabled, int distance) {
    const int clamped = std::max(1650, std::min(3000, distance));
    const bool wasEnabled = enabled_;
    const int previousConfigured = configuredDistance_.load();
    configuredDistance_ = clamped;

    if (!enabled) {
        enabled_ = false;
        applyPending_ = false;
        Log("set enabled=0 distance=" + std::to_string(clamped) +
            " (camera left untouched)");
        return;
    }

    // A repeated CAMERA command with the same configured distance (config
    // saves, pause/resume notifications) must not discard a live hotkey
    // adjustment. Only a fresh enable or an actual config change re-arms.
    if (!wasEnabled || clamped != previousConfigured) {
        distance_ = clamped;
        applyPending_ = true;
    }

    enabled_ = true;
    Log("set enabled=1 distance=" + std::to_string(clamped));
}

void CameraManager::SetHotkeys(int zoomOutVk, int zoomOutModifiers,
                               int zoomInVk, int zoomInModifiers) {
    zoomOutVk = std::max(0, std::min(0xFF, zoomOutVk));
    zoomOutModifiers = std::max(0, std::min(0x0F, zoomOutModifiers));
    zoomInVk = std::max(0, std::min(0xFF, zoomInVk));
    zoomInModifiers = std::max(0, std::min(0x0F, zoomInModifiers));

    const int oldOutVk = zoomOutVk_.exchange(zoomOutVk);
    const int oldOutModifiers = zoomOutModifiers_.exchange(zoomOutModifiers);
    const int oldInVk = zoomInVk_.exchange(zoomInVk);
    const int oldInModifiers = zoomInModifiers_.exchange(zoomInModifiers);

    if (oldOutVk != zoomOutVk || oldOutModifiers != zoomOutModifiers ||
        oldInVk != zoomInVk || oldInModifiers != zoomInModifiers) {
        std::ostringstream stream;
        stream << "hotkeys zoomOut=vk0x" << std::hex << std::uppercase << zoomOutVk
               << " mods0x" << zoomOutModifiers
               << " zoomIn=vk0x" << zoomInVk
               << " mods0x" << zoomInModifiers;
        Log(stream.str());
    }
}

int CameraManager::MatchHotkey(int virtualKey, int modifiers) const {
    if (!enabled_ || virtualKey == 0) {
        return 0;
    }

    if (virtualKey == zoomOutVk_.load() && modifiers == zoomOutModifiers_.load()) {
        return 1;
    }

    if (virtualKey == zoomInVk_.load() && modifiers == zoomInModifiers_.load()) {
        return -1;
    }

    return 0;
}

void CameraManager::Disable() {
    if (enabled_) {
        Log("disable (camera left untouched)");
    }
    enabled_ = false;
    applyPending_ = false;
}

bool CameraManager::ResolveGameUi(GameUiPointers& pointers) const {
    const uint32_t base = gameBase_;
    const uint32_t cguiRva = cguiRva_;
    if (base == 0 || cguiRva == 0) {
        return false;
    }

    if (!ReadU32(base + cguiRva, pointers.cgui) || pointers.cgui < 0x10000) {
        return false;
    }

    // CGameUI+0x254 holds a camera pointer on 1.24e/1.27a, but it stays zero
    // on 1.20e even when the in-game UI is fully usable. Keep reading it for
    // diagnostics, but do not use it as a readiness requirement.
    if (!ReadU32(pointers.cgui + 0x254, pointers.camera) ||
        pointers.camera < 0x10000) {
        pointers.camera = 0;
    }

    // The chat edit box only exists once the in-game UI is fully built, so it
    // is the "safe to touch the camera" gate.
    if (chatBarOffset_ == 0 || textObjectOffset_ == 0 || textBufferOffset_ == 0) {
        return false;
    }

    if (!ReadU32(pointers.cgui + chatBarOffset_, pointers.chatBar) ||
        pointers.chatBar < 0x10000) {
        return false;
    }

    if (!ReadU32(pointers.chatBar + textObjectOffset_, pointers.textObject) ||
        pointers.textObject < 0x10000) {
        return false;
    }

    if (!ReadU32(pointers.textObject + textBufferOffset_, pointers.textBuffer) ||
        pointers.textBuffer < 0x10000) {
        return false;
    }

    return true;
}

void CameraManager::Apply() {
    if (!profileValid_ || gameBase_ == 0 || setFieldRva_ == 0 || setFieldRva_ >= imageSize_) {
        return;
    }

    GameUiPointers pointers;
    const bool ready = ResolveGameUi(pointers);
    const int state = ready ? 1 : 0;
    const int previous = readyState_.exchange(state);
    if (previous != state) {
        std::ostringstream stream;
        stream << "ui " << (ready ? "ready" : "not-ready")
               << " cgui=0x" << std::hex << std::uppercase << pointers.cgui
               << " camera=0x" << pointers.camera
               << " chatBar=0x" << pointers.chatBar
               << " textObject=0x" << pointers.textObject
               << " textBuffer=0x" << pointers.textBuffer;
        Log(stream.str());
    }

    if (!ready) {
        // Menu and map loading expose a half-built UI. Calling into the camera
        // there crashes both 1.24e and 1.27a (null/bogus UI object). Arm one
        // apply for the next time the in-game UI becomes ready.
        if (enabled_) {
            applyPending_ = true;
        }
        return;
    }

    if (!enabled_) {
        return;
    }

    if (previous != 1) {
        // New map/game session: live hotkey adjustments belong to the previous
        // game, so start again from the configured distance.
        distance_ = configuredDistance_.load();
        applyPending_ = true;
    }

    if (!applyPending_.exchange(false)) {
        return;
    }

    const int distance = distance_.load();
    ApplyDistance(distance);
    appliedCount_++;
    Log("applied distance=" + std::to_string(distance) + " (once)");
}

bool CameraManager::AdjustLive(int delta) {
    if (!enabled_) {
        return false;
    }

    const uint64_t now = GetTickCount64();
    if (lastHotkeyAdjustTick_ != 0 && now - lastHotkeyAdjustTick_ < 100) {
        // Key auto-repeat: keep swallowing the key but do not overshoot.
        return true;
    }

    GameUiPointers pointers;
    if (!ResolveGameUi(pointers)) {
        return false;
    }

    int next = distance_.load() + delta;
    next = std::max(1650, std::min(3000, next));

    // A live adjustment wins over a pending config apply and marks the UI as
    // ready so the next WM_TIMER pass does not reset it to the config value.
    readyState_ = 1;
    applyPending_ = false;
    distance_ = next;
    lastHotkeyAdjustTick_ = now;
    ApplyDistance(next);
    appliedCount_++;
    Log("hotkey distance=" + std::to_string(next));
    return true;
}

std::string CameraManager::Status() const {
    std::ostringstream stream;
    stream << (profileValid_ ? 1 : 0) << ':'
           << (enabled_ ? 1 : 0) << ':'
           << distance_.load() << ':'
           << appliedCount_.load();
    return stream.str();
}

void CameraManager::ApplyDistance(int distance) {
    const uint32_t address = gameBase_ + setFieldRva_;
    CallCameraField(address, kDistanceField, static_cast<float>(distance));
}

void CameraManager::Log(const std::string& text) {
    std::lock_guard<std::mutex> lock(g_cameraLogMutex);
    std::ofstream file(CameraLogPath(), std::ios::app);
    if (file) {
        file << GetTickCount64() << " " << text << "\n";
    }
}

}  // namespace iwk
