#include "key_hook.h"

#include <cstdint>
#include <fstream>
#include <mutex>
#include <sstream>
#include <string>

#include "camera_manager.h"
#include "chat.h"
#include "skill_manager.h"

namespace iwk {

namespace {

// Window timer used to detect the in-game UI becoming ready. Apply() itself
// only writes the camera distance once per map/ready transition.
constexpr UINT_PTR kCameraTimerId = 0x49574B;
constexpr UINT kCameraTimerMs = 500;
constexpr int kCameraHotkeyStep = 100;

KeyHook* g_hook = nullptr;
std::mutex g_logMutex;

std::string LogPath() {
    wchar_t temp[MAX_PATH] = {};
    DWORD length = GetTempPathW(MAX_PATH, temp);
    std::wostringstream stream;
    stream << temp;
    if (length > 0 && temp[length - 1] != L'\\') {
        stream << L'\\';
    }
    stream << L"iwar3key-whook-" << GetCurrentProcessId() << L".log";
    std::wstring wide = stream.str();
    int size = WideCharToMultiByte(CP_ACP, 0, wide.c_str(), static_cast<int>(wide.size()), nullptr, 0, nullptr, nullptr);
    std::string result(static_cast<size_t>(size > 0 ? size : 0), '\0');
    if (size > 0) {
        WideCharToMultiByte(CP_ACP, 0, wide.c_str(), static_cast<int>(wide.size()), &result[0], size, nullptr, nullptr);
    }
    return result;
}

}  // namespace

KeyHook::KeyHook()
    : running_(false),
      stopRequested_(false),
      paused_(false),
      chatGameBase_(0),
      chattingRva_(0),
      menuGameBase_(0),
      menuCguiRva_(0),
      escapeMenuOffset_(0),
      skillManager_(nullptr),
      cameraManager_(nullptr),
      lastNativeChatState_(false),
      lastNativeMenuState_(false),
      hwnd_(nullptr),
      oldProc_(nullptr),
      gameThreadId_(0) {
    pendingKeys_.fill(0);
    swallowedKeys_.fill(false);
    g_hook = this;
}

void KeyHook::SetSkillManager(SkillManager* manager) {
    skillManager_ = manager;
}

void KeyHook::SetCameraManager(CameraManager* manager) {
    cameraManager_ = manager;
}

void KeyHook::SetPaused(bool paused) {
    paused_ = paused;
}

void KeyHook::SetChatState(unsigned int gameBase, unsigned int chattingRva) {
    chatGameBase_ = gameBase;
    chattingRva_ = chattingRva;
}

void KeyHook::SetMenuState(unsigned int gameBase, unsigned int cguiRva,
                           unsigned int escapeMenuOffset) {
    menuGameBase_ = gameBase;
    menuCguiRva_ = cguiRva;
    escapeMenuOffset_ = escapeMenuOffset;
    if (escapeMenuOffset == 0) {
        lastNativeMenuState_ = false;
    }
}

KeyHook::~KeyHook() {
    Stop();
    if (g_hook == this) {
        g_hook = nullptr;
    }
}

void KeyHook::Start() {
    if (running_.exchange(true)) {
        return;
    }

    stopRequested_ = false;
    thread_ = std::thread(&KeyHook::Run, this);
    LogLine("key hook thread started");
}

void KeyHook::Stop() {
    stopRequested_ = true;
    running_ = false;
    if (thread_.joinable()) {
        thread_.join();
    }
    Restore();
}

void KeyHook::LogLine(const char* text) {
    std::lock_guard<std::mutex> lock(g_logMutex);
    std::ofstream file(LogPath(), std::ios::app);
    if (file) {
        file << GetTickCount64() << " " << text << "\n";
    }
}

BOOL CALLBACK KeyHook::FindWindowProc(HWND hwnd, LPARAM parameter) {
    DWORD pid = 0;
    GetWindowThreadProcessId(hwnd, &pid);
    if (pid != GetCurrentProcessId() || !IsWindowVisible(hwnd)) {
        return TRUE;
    }

    RECT rect{};
    if (!GetClientRect(hwnd, &rect) || rect.right <= 0 || rect.bottom <= 0) {
        return TRUE;
    }

    auto* best = reinterpret_cast<HWND*>(parameter);
    RECT bestRect{};
    if (*best == nullptr || (GetClientRect(*best, &bestRect) &&
                             rect.right * rect.bottom > bestRect.right * bestRect.bottom)) {
        *best = hwnd;
    }
    return TRUE;
}

HWND KeyHook::FindGameWindow() {
    HWND best = nullptr;
    EnumWindows(FindWindowProc, reinterpret_cast<LPARAM>(&best));
    return best;
}

bool KeyHook::Install(HWND hwnd) {
    if (hwnd == nullptr || !IsWindow(hwnd)) {
        return false;
    }

    LONG_PTR current = GetWindowLongPtrW(hwnd, GWLP_WNDPROC);
    if (current == reinterpret_cast<LONG_PTR>(&KeyHook::HookProc)) {
        hwnd_ = hwnd;
        SetTimer(hwnd, kCameraTimerId, kCameraTimerMs, nullptr);
        return true;
    }

    oldProc_ = reinterpret_cast<WNDPROC>(SetWindowLongPtrW(
        hwnd, GWLP_WNDPROC, reinterpret_cast<LONG_PTR>(&KeyHook::HookProc)));
    if (oldProc_ == nullptr) {
        return false;
    }

    hwnd_ = hwnd;
    gameThreadId_ = GetWindowThreadProcessId(hwnd, nullptr);
    if (skillManager_ != nullptr) {
        skillManager_->SetGameWindow(hwnd);
    }
    SetTimer(hwnd, kCameraTimerId, kCameraTimerMs, nullptr);

    std::ostringstream stream;
    stream << "installed hwnd=0x" << std::hex << reinterpret_cast<uintptr_t>(hwnd)
           << " old=0x" << reinterpret_cast<uintptr_t>(oldProc_)
           << " thread=" << std::dec << gameThreadId_;
    LogLine(stream.str().c_str());
    return true;
}

void KeyHook::Restore() {
    if (hwnd_ != nullptr && IsWindow(hwnd_)) {
        KillTimer(hwnd_, kCameraTimerId);
    }

    if (hwnd_ != nullptr && IsWindow(hwnd_) && oldProc_ != nullptr) {
        LONG_PTR current = GetWindowLongPtrW(hwnd_, GWLP_WNDPROC);
        if (current == reinterpret_cast<LONG_PTR>(&KeyHook::HookProc)) {
            SetWindowLongPtrW(hwnd_, GWLP_WNDPROC, reinterpret_cast<LONG_PTR>(oldProc_));
            LogLine("restored");
        }
    }

    hwnd_ = nullptr;
    oldProc_ = nullptr;
    gameThreadId_ = 0;
}

void KeyHook::Run() {
    while (!stopRequested_) {
        if (hwnd_ == nullptr || !IsWindow(hwnd_)) {
            HWND hwnd = FindGameWindow();
            if (hwnd != nullptr) {
                Install(hwnd);
            }
        } else {
            LONG_PTR current = GetWindowLongPtrW(hwnd_, GWLP_WNDPROC);
            if (current != reinterpret_cast<LONG_PTR>(&KeyHook::HookProc) && oldProc_ != nullptr) {
                LogLine("window proc changed; reinstalling");
                hwnd_ = nullptr;
                oldProc_ = nullptr;
                HWND hwnd = FindGameWindow();
                if (hwnd != nullptr) {
                    Install(hwnd);
                }
            }
        }

        Sleep(500);
    }
}

LRESULT CALLBACK KeyHook::HookProc(HWND hwnd, UINT message, WPARAM wparam, LPARAM lparam) {
    KeyHook* hook = g_hook;
    if (hook == nullptr) {
        return DefWindowProcW(hwnd, message, wparam, lparam);
    }

    // The shell's SendInput events carry this marker. Never translate those:
    // item/mapping/macro injection is already final and must not be remapped
    // a second time by the in-game key hook.
    constexpr ULONG_PTR kInjectionMarker = 0x1A2B3C4D;
    const bool injected = static_cast<ULONG_PTR>(GetMessageExtraInfo()) == kInjectionMarker;

    if (message == WM_TIMER && wparam == kCameraTimerId) {
        if (hook->cameraManager_ != nullptr) {
            hook->cameraManager_->Apply();
        }
        return 0;
    }

    if (message == WM_KEYDOWN || message == WM_SYSKEYDOWN) {
        const UINT virtualKey = static_cast<UINT>(wparam & 0xFF);

        // Native chat detection: the game keeps its chat edit bar state set
        // while the chat box owns the input. Do not rewrite camera hotkeys,
        // skill keys or item quick-cast keys in that state.
        const bool nativeChat = IsChattingAt(
            hook->chatGameBase_.load(), hook->chattingRva_.load());
        if (nativeChat != hook->lastNativeChatState_) {
            hook->lastNativeChatState_ = nativeChat;
            LogLine(nativeChat ? "native chat state: open" : "native chat state: closed");
        }

        // F10/EscMenu hotkeys are handled by the menu's own controls. This
        // window procedure sees every key before the game's dispatch (KK's
        // command-card converter does not), so conversion must stop while the
        // game's active UI mode is the CEscMenu object.
        const bool nativeMenu = IsEscapeMenuOpenAt(
            hook->menuGameBase_.load(), hook->menuCguiRva_.load(),
            hook->escapeMenuOffset_.load());
        if (nativeMenu != hook->lastNativeMenuState_) {
            hook->lastNativeMenuState_ = nativeMenu;
            LogLine(nativeMenu ? "native escape menu: open" : "native escape menu: closed");
        }

        // Chat and menu block camera hotkeys too. PAUSE only stops the
        // item/skill/mapping conversions and quick-cast; camera distance and
        // other game settings stay active.
        const bool uiBlocked = nativeChat || nativeMenu;
        const bool remapBlocked = hook->paused_.load() || uiBlocked;

        // Live camera distance: user-configured hotkeys. Modifier bits match
        // the shell: 1=Alt, 2=Ctrl, 4=Shift, 8=Win. The adjustment is kept
        // only in memory for the current game and is never written to config.
        if (!uiBlocked && hook->cameraManager_ != nullptr) {
            int modifiers = 0;
            if (message == WM_SYSKEYDOWN || (GetAsyncKeyState(VK_MENU) & 0x8000) != 0) {
                modifiers |= 1;
            }
            if ((GetAsyncKeyState(VK_CONTROL) & 0x8000) != 0) {
                modifiers |= 2;
            }
            if ((GetAsyncKeyState(VK_SHIFT) & 0x8000) != 0) {
                modifiers |= 4;
            }
            if (((GetAsyncKeyState(VK_LWIN) | GetAsyncKeyState(VK_RWIN)) & 0x8000) != 0) {
                modifiers |= 8;
            }

            const int direction = hook->cameraManager_->MatchHotkey(
                static_cast<int>(virtualKey), modifiers);
            if (direction != 0 &&
                hook->cameraManager_->AdjustLive(direction * kCameraHotkeyStep)) {
                hook->swallowedKeys_[virtualKey] = true;
                hook->pendingKeys_[virtualKey] = 0;
                return 0;
            }
        }

        int nativeKey = 0;
        int uiSlot = 0;
        int action = 0;
        if (!remapBlocked && hook->skillManager_ != nullptr) {
            if (!injected) {
                action = hook->skillManager_->MapKey(static_cast<int>(virtualKey), nativeKey, uiSlot);
            }

            // Item keys are injected by the shell (or pressed natively on the
            // numpad); arm quick cast for the matching item slot.
            hook->skillManager_->ArmItemQuickCast(static_cast<int>(virtualKey));
        }

        if (action == 1) {
            hook->pendingKeys_[virtualKey] = nativeKey;
            hook->swallowedKeys_[virtualKey] = false;
            wparam = static_cast<WPARAM>(nativeKey);

            const uint32_t scan = MapVirtualKeyW(static_cast<UINT>(nativeKey), MAPVK_VK_TO_VSC);
            uint32_t value = static_cast<uint32_t>(lparam);
            value = (value & ~0x00FF0000u) | ((scan & 0xFFu) << 16);
            lparam = static_cast<LPARAM>(value);
        } else if (action == 3) {
            // A configured slot matched, but the current command card has no
            // hotkey there (empty slot / missing button). Swallow both the
            // down and the matching up so the physical key cannot fall
            // through and trigger a different native action.
            hook->swallowedKeys_[virtualKey] = true;
            hook->pendingKeys_[virtualKey] = 0;
            return 0;
        } else {
            hook->swallowedKeys_[virtualKey] = false;
            if (hook->pendingKeys_[virtualKey] != 0) {
                hook->pendingKeys_[virtualKey] = 0;
            }
        }
    } else if (message == WM_KEYUP || message == WM_SYSKEYUP) {
        const UINT virtualKey = static_cast<UINT>(wparam & 0xFF);
        if (hook->swallowedKeys_[virtualKey]) {
            hook->swallowedKeys_[virtualKey] = false;
            return 0;
        }

        const int nativeKey = hook->pendingKeys_[virtualKey];
        if (nativeKey != 0) {
            wparam = static_cast<WPARAM>(nativeKey);
            const uint32_t scan = MapVirtualKeyW(static_cast<UINT>(nativeKey), MAPVK_VK_TO_VSC);
            uint32_t value = static_cast<uint32_t>(lparam);
            value = (value & ~0x00FF0000u) | ((scan & 0xFFu) << 16);
            lparam = static_cast<LPARAM>(value);
            hook->pendingKeys_[virtualKey] = 0;
        }
    }

    if (hook->oldProc_ != nullptr) {
        return CallWindowProcW(hook->oldProc_, hwnd, message, wparam, lparam);
    }
    return DefWindowProcW(hwnd, message, wparam, lparam);
}

}  // namespace iwk
