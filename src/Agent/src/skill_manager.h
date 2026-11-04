#pragma once

#include <windows.h>

#include <array>
#include <atomic>
#include <cstdint>
#include <mutex>
#include <string>
#include <thread>
#include <vector>

namespace iwk {

// Conversion-only skill engine.
//
// Every key press reads the live 4x3 command card and substitutes the
// configured key with the button's current hotkey. There is no ability scan,
// no per-unit cache, no native ability-data patch, and no background polling.
class SkillManager {
public:
    SkillManager();
    ~SkillManager();

    void Start();
    void SetProfile(bool valid, unsigned int unitListRva, unsigned int cguiRva,
                    unsigned int selectionOffset, unsigned int activeUnitOffset,
                    unsigned int commandBarOffset, unsigned int commandGridOffset,
                    unsigned int commandFrameButtonOffset,
                    unsigned int commandButtonHotkeyOffset);
    int SetBindings(const std::vector<int>& keysBySlot,
                    const std::vector<int>& quickCastBySlot);
    void SetItemQuickCast(const std::vector<int>& quickCastBySlot);
    void SetInfoEnabled(bool enabled);
    void SetGameWindow(HWND hwnd);
    void ArmItemQuickCast(int virtualKey);
    // 0 = no conversion, 1 = convert, 2 = pass through because the native key
    // is the same physical key, 3 = swallow because the configured slot has no
    // usable hotkey.
    int MapKey(int virtualKey, int& nativeKey, int& uiSlot);
    void DisableAndRestore();

private:
    bool ReadCommandCard(std::array<uint32_t, 12>& hotkeys,
                         std::array<uint32_t, 12>& commandIds);
    bool ReadHeroAbilityIds(std::vector<uint32_t>& ids);
    void ArmQuickCast();
    void DisarmQuickCast();
    bool PostQuickCastClick();
    void QuickCastLoop();

    std::mutex mutex_;
    bool profileValid_;
    unsigned int unitListRva_;
    unsigned int cguiRva_;
    unsigned int selectionOffset_;
    unsigned int activeUnitOffset_;
    unsigned int commandBarOffset_;
    unsigned int commandGridOffset_;
    unsigned int commandFrameButtonOffset_;
    unsigned int commandButtonHotkeyOffset_;
    bool commandCardMode_;
    bool infoEnabled_;
    std::vector<int> keys_;
    std::array<bool, 12> quickCast_;
    std::array<bool, 6> itemQuickCast_;
    std::atomic<bool> quickCastStop_;
    std::atomic<bool> quickCastArmed_;
    std::atomic<bool> quickCastModeEligible_;
    std::atomic<uint64_t> quickCastArmedAt_;
    std::atomic<uint64_t> quickCastCardCheckAt_;
    std::atomic<uint32_t> quickCastCguiRva_;
    std::atomic<HWND> quickCastWindow_;
    HANDLE quickCastEvent_;
    std::thread quickCastThread_;
};

}  // namespace iwk
