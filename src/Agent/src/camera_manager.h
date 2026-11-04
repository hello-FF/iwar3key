#pragma once

#include <windows.h>

#include <atomic>
#include <cstdint>
#include <string>

namespace iwk {

// Applies the configured camera distance through the game's SetCameraField
// implementation. Apply() is polled from the game window thread (WM_TIMER) so
// the engine is only touched at a safe point in its message loop; it pushes the
// distance once per map/UI-ready transition and never re-applies in a loop.
class CameraManager {
public:
    CameraManager();

    void SetGameBase(uint32_t base, uint32_t imageSize);
    void SetProfile(bool valid, uint32_t setFieldRva,
                    uint32_t cguiRva, uint32_t chatBarOffset,
                    uint32_t textObjectOffset, uint32_t textBufferOffset);
    void SetEnabled(bool enabled, int distance);
    // Detach/pause: deliberately leaves the game's current distance untouched.
    void Disable();
    void Apply();
    // Configurable live-adjust hotkeys. Modifier bits match the shell:
    // 1=Alt, 2=Ctrl, 4=Shift, 8=Win. A zero VK disables that direction.
    void SetHotkeys(int zoomOutVk, int zoomOutModifiers,
                    int zoomInVk, int zoomInModifiers);
    // Returns +1 for the zoom-out (increase) hotkey, -1 for zoom-in, 0 for
    // no match or while the feature is disabled.
    int MatchHotkey(int virtualKey, int modifiers) const;
    // Applies a live adjustment. Returns true when the key was consumed
    // (feature enabled and the in-game UI is ready).
    bool AdjustLive(int delta);

    // Diagnostics for the Agent's CAMERA response.
    std::string Status() const;

private:
    struct GameUiPointers {
        uint32_t cgui = 0;
        uint32_t camera = 0;
        uint32_t chatBar = 0;
        uint32_t textObject = 0;
        uint32_t textBuffer = 0;
    };

    void ApplyDistance(int distance);
    bool ResolveGameUi(GameUiPointers& pointers) const;
    void Log(const std::string& text);

    std::atomic<uint32_t> gameBase_;
    std::atomic<uint32_t> imageSize_;
    std::atomic<bool> profileValid_;
    std::atomic<uint32_t> setFieldRva_;
    std::atomic<uint32_t> cguiRva_;
    std::atomic<uint32_t> chatBarOffset_;
    std::atomic<uint32_t> textObjectOffset_;
    std::atomic<uint32_t> textBufferOffset_;
    std::atomic<int> readyState_;
    std::atomic<int> configuredDistance_;
    std::atomic<int> distance_;
    std::atomic<int> zoomOutVk_;
    std::atomic<int> zoomOutModifiers_;
    std::atomic<int> zoomInVk_;
    std::atomic<int> zoomInModifiers_;
    std::atomic<bool> enabled_;
    std::atomic<bool> applyPending_;
    std::atomic<uint32_t> appliedCount_;
    uint64_t lastHotkeyAdjustTick_;
};

}  // namespace iwk
