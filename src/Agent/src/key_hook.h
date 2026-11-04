#pragma once

#include <windows.h>

#include <array>
#include <atomic>
#include <thread>

namespace iwk {

class CameraManager;
class SkillManager;

// Hook for the game's own window procedure. It logs keydowns and can translate
// configured skill keys to their native hotkeys before the game sees them.
class KeyHook {
public:
    KeyHook();
    ~KeyHook();

    void Start();
    void Stop();
    void SetSkillManager(SkillManager* manager);
    void SetCameraManager(CameraManager* manager);
    void SetPaused(bool paused);
    // Enables native chat detection. gameBase is the current Game.dll base,
    // chattingRva the profile's IS_CHATTING RVA (0 disables native detection).
    void SetChatState(unsigned int gameBase, unsigned int chattingRva);
    // Enables native EscMenu detection. escapeMenuOffset is the CGameUI field
    // holding the menu object pointer (0 disables menu detection).
    void SetMenuState(unsigned int gameBase, unsigned int cguiRva,
                      unsigned int escapeMenuOffset);

private:
    static LRESULT CALLBACK HookProc(HWND hwnd, UINT message, WPARAM wparam, LPARAM lparam);
    static BOOL CALLBACK FindWindowProc(HWND hwnd, LPARAM parameter);
    static void LogLine(const char* text);

    void Run();
    HWND FindGameWindow();
    bool Install(HWND hwnd);
    void Restore();

    std::thread thread_;
    std::atomic<bool> running_;
    std::atomic<bool> stopRequested_;
    std::atomic<bool> paused_;
    std::atomic<unsigned int> chatGameBase_;
    std::atomic<unsigned int> chattingRva_;
    std::atomic<unsigned int> menuGameBase_;
    std::atomic<unsigned int> menuCguiRva_;
    std::atomic<unsigned int> escapeMenuOffset_;
    SkillManager* skillManager_;
    CameraManager* cameraManager_;
    std::array<int, 256> pendingKeys_;
    std::array<bool, 256> swallowedKeys_;
    bool lastNativeChatState_;
    bool lastNativeMenuState_;
    HWND hwnd_;
    WNDPROC oldProc_;
    DWORD gameThreadId_;
};

}  // namespace iwk
