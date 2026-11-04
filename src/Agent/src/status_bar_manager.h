#pragma once

#include <windows.h>

#include <atomic>
#include <cstdint>
#include <string>

namespace iwk {

// Adds a native CStatBar below the game's own unit health bar, following the
// approach used by KK's War3Plugin: double the 0x158-byte health-bar allocation,
// construct a second CStatBar at +0x158, and update it from the game's
// per-unit CUnitUpdateHpBar loop. The health bar itself is shown through the
// game's own "always show health bars" flag (CGameUI + 0x1D8).
//
// The shell sends a profile with the Game.dll RVAs for 1.20e / 1.24e / 1.27a.
class StatusBarManager {
public:
    StatusBarManager();
    ~StatusBarManager();

    void SetGameBase(uint32_t base, uint32_t imageSize);
    void SetProfile(bool valid,
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
                    uint32_t healthFlagOffset);
    void SetEnabled(bool health, bool friendlyMana, bool enemyMana);

    // Installs the two call-site hooks. Safe to call repeatedly.
    void Apply();

    // Feature-off path: hide existing mana bars and stop forcing health bars.
    // The code hooks stay installed for the lifetime of the game process so a
    // later re-enable does not need to race the game thread.
    void Disable();

    std::string Status() const;

    // Called from the naked allocation/update stubs.
    void InitManaBar(uint32_t bar);
    void OnUnitUpdate(uint32_t unit);

private:
    struct Pointers {
        uint32_t allocCall = 0;
        uint32_t updateCall = 0;
        uint32_t originalUpdate = 0;
        uint32_t ctor = 0;
        uint32_t getUnitState = 0;
        uint32_t unitScale = 0;
        uint32_t setBarPosition = 0;
        uint32_t setBarBounds = 0;
        uint32_t getUnitUIDef = 0;
        uint32_t isUnitStructure = 0;
        uint32_t getLocalPlayer = 0;
        uint32_t getCPlayerWar3 = 0;
        uint32_t cPlayerAttr = 0;
        uint32_t integerGetValue = 0;
        uint32_t cguiGlobal = 0;
        uint32_t healthFlagOffset = 0x1D8;
        uint32_t frameCall = 0;
    };

    bool PatchCall(uint32_t site, void* target, int32_t* savedRel = nullptr);
    bool PatchJmp(uint32_t site, void* target);
    void HideManaBar(uint32_t bar) const;
    void ShowManaBar(uint32_t bar) const;
    bool IsAllied(uint32_t unit);
    bool IsVisibleToLocalPlayer(uint32_t unit) const;
    uint32_t UnitOwner(uint32_t unit) const;
    bool Readable(uint32_t address, size_t size) const;
    // 1.20e GetUnitState(state=3) dereferences a unit-table lookup result
    // without a null check. Validate the same handle/table link first.
    bool IsUnitStateTargetValid120(uint32_t unit) const;
    void Log(const std::string& text) const;

    std::atomic<uint32_t> gameBase_;
    std::atomic<uint32_t> imageSize_;
    std::atomic<bool> profileValid_;
    std::atomic<bool> installed_;
    std::atomic<bool> healthEnabled_;
    std::atomic<bool> friendlyMana_;
    std::atomic<bool> enemyMana_;
    std::atomic<bool> forceHealthBar_;
    std::atomic<bool> updateAfterOriginal_;
    Pointers pointers_;
    int32_t originalAllocRel_;
    std::atomic<uint32_t> allyMask_;
    std::atomic<uint64_t> allyMaskAt_;
    std::atomic<uint32_t> localPlayerIndex_;
    std::atomic<uint32_t> initCount_;
    std::atomic<uint32_t> updateCount_;
    std::atomic<uint32_t> hiddenCount_;
    std::atomic<uint32_t> shownCount_;
    std::atomic<uint32_t> relationFailCount_;
    std::atomic<bool> invalidUnitLogged_;
    std::atomic<uint64_t> lastUpdateTick_;

    void* fakeVtable_[0x40];
};

}  // namespace iwk
