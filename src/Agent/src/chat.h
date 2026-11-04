#pragma once

#include <string>

namespace iwk {

struct ChatProfile {
    bool valid = false;
    unsigned long long cguiGlobalRva = 0;
    unsigned int chatBarOffset = 0;
    unsigned int textObjectOffset = 0;
    unsigned int textBufferOffset = 0;
    // RVA of the game's own chat-open state. 0 means the build has no
    // verified native detection; chat gating is then unavailable.
    unsigned int isChattingRva = 0;
    // CGameUI field holding the CEscMenu object pointer. 0 means the build
    // has no verified menu detection; menu-key protection is then unavailable.
    unsigned int escapeMenuOffset = 0;
};

struct ChatResult {
    bool ok = false;
    int sentBytes = 0;
    std::wstring error;
};

ChatResult SendChat(const ChatProfile& profile, const std::wstring& text, int channel, int sendDelayMs);

bool IsChatReady(const ChatProfile& profile, std::wstring& error);

// Reads the game-maintained chat-open state. Returns false for profiles that
// do not expose one or if the address is not in committed, readable memory.
bool IsChattingAt(unsigned int gameBase, unsigned int chattingRva);

// Reads CGameUI+escapeMenuOffset and CGameUI+0x1B4. The EscMenu is open when
// the active mode object is the menu object. Returns false when the profile
// has no menu offset or the UI chain is not readable.
bool IsEscapeMenuOpenAt(unsigned int gameBase, unsigned int cguiRva,
                        unsigned int escapeMenuOffset);

}  // namespace iwk
