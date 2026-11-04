#include "chat.h"

#include <windows.h>

#include <algorithm>
#include <cstring>

#include "protocol.h"

namespace iwk {

namespace {

const size_t kMaxChatBytes = 250;
// CGameUI active-mode pointer (shared by 1.24e and 1.27a).
constexpr unsigned int kCurrentModeOffset = 0x1B4;

bool IsRangeAccessible(unsigned int address, size_t size, bool write) {
    if (address == 0 || size == 0) {
        return false;
    }

    unsigned int current = address;
    size_t remaining = size;
    while (remaining > 0) {
        MEMORY_BASIC_INFORMATION info{};
        if (VirtualQuery(reinterpret_cast<LPCVOID>(static_cast<uintptr_t>(current)), &info, sizeof(info)) == 0) {
            return false;
        }

        if (info.State != MEM_COMMIT) {
            return false;
        }

        DWORD protect = info.Protect;
        if ((protect & (PAGE_NOACCESS | PAGE_GUARD)) != 0) {
            return false;
        }

        if (write) {
            bool writable = (protect & (PAGE_READWRITE | PAGE_WRITECOPY | PAGE_EXECUTE_READWRITE | PAGE_EXECUTE_WRITECOPY)) != 0;
            if (!writable) {
                return false;
            }
        }

        uintptr_t base = reinterpret_cast<uintptr_t>(info.BaseAddress);
        uintptr_t end = base + info.RegionSize;
        if (end <= current) {
            return false;
        }

        size_t chunk = static_cast<size_t>(std::min<uintptr_t>(remaining, end - current));
        current += static_cast<unsigned int>(chunk);
        remaining -= chunk;
    }

    return true;
}

bool ReadPointer(unsigned int address, unsigned int& value) {
    if (!IsRangeAccessible(address, sizeof(unsigned int), false)) {
        return false;
    }

    std::memcpy(&value, reinterpret_cast<const void*>(static_cast<uintptr_t>(address)), sizeof(value));
    return true;
}

bool WriteBuffer(unsigned int address, const std::string& text) {
    size_t size = text.size() + 1;
    if (!IsRangeAccessible(address, size, true)) {
        return false;
    }

    std::memcpy(reinterpret_cast<void*>(static_cast<uintptr_t>(address)), text.data(), text.size());
    reinterpret_cast<char*>(static_cast<uintptr_t>(address))[text.size()] = '\0';
    return true;
}

struct WindowSearch {
    DWORD pid;
    HWND best;
    HWND fallback;
};

BOOL CALLBACK EnumWindowProc(HWND hwnd, LPARAM lParam) {
    WindowSearch* search = reinterpret_cast<WindowSearch*>(lParam);
    DWORD windowPid = 0;
    GetWindowThreadProcessId(hwnd, &windowPid);
    if (windowPid != search->pid || !IsWindowVisible(hwnd)) {
        return TRUE;
    }

    wchar_t className[128]{};
    GetClassNameW(hwnd, className, static_cast<int>(sizeof(className) / sizeof(className[0])));
    if (_wcsicmp(className, L"Warcraft III") == 0) {
        search->best = hwnd;
        return FALSE;
    }

    if (search->fallback == nullptr) {
        search->fallback = hwnd;
    }

    return TRUE;
}

HWND FindGameWindow() {
    WindowSearch search{GetCurrentProcessId(), nullptr, nullptr};
    EnumWindows(EnumWindowProc, reinterpret_cast<LPARAM>(&search));
    return search.best != nullptr ? search.best : search.fallback;
}

void PostKey(HWND hwnd, UINT virtualKey, bool down) {
    UINT scanCode = MapVirtualKeyW(virtualKey, MAPVK_VK_TO_VSC) & 0xFF;
    LPARAM lParam = 1 | (static_cast<LPARAM>(scanCode) << 16);
    if (!down) {
        lParam |= static_cast<LPARAM>(0xC0000000u);
    }

    PostMessageW(hwnd, down ? WM_KEYDOWN : WM_KEYUP, virtualKey, lParam);
}

void PostEnter(HWND hwnd) {
    PostKey(hwnd, VK_RETURN, true);
    PostKey(hwnd, VK_RETURN, false);
}

std::string LimitUtf8(const std::string& text) {
    if (text.size() <= kMaxChatBytes) {
        return text;
    }

    size_t cut = kMaxChatBytes;
    while (cut > 0 && (static_cast<unsigned char>(text[cut]) & 0xC0) == 0x80) {
        --cut;
    }

    return text.substr(0, cut);
}

ChatResult Fail(const std::wstring& message) {
    ChatResult result;
    result.error = message;
    return result;
}

bool ResolveBuffer(const ChatProfile& profile, unsigned int& bufferAddress, std::wstring& error) {
    HMODULE game = GetModuleHandleW(L"Game.dll");
    if (game == nullptr) {
        error = L"未找到 Game.dll";
        return false;
    }

    unsigned int base = static_cast<unsigned int>(reinterpret_cast<uintptr_t>(game));
    unsigned int global = 0;
    if (!ReadPointer(base + static_cast<unsigned int>(profile.cguiGlobalRva), global) || global == 0) {
        error = L"读取 CGameUI 失败";
        return false;
    }

    unsigned int chatBar = 0;
    if (!ReadPointer(global + profile.chatBarOffset, chatBar) || chatBar == 0) {
        error = L"读取聊天编辑条失败";
        return false;
    }

    unsigned int textObject = 0;
    if (!ReadPointer(chatBar + profile.textObjectOffset, textObject) || textObject == 0) {
        error = L"读取聊天文本对象失败";
        return false;
    }

    bufferAddress = 0;
    if (!ReadPointer(textObject + profile.textBufferOffset, bufferAddress) || bufferAddress == 0) {
        error = L"读取聊天缓冲区失败";
        return false;
    }

    return true;
}

}  // namespace

bool IsChatReady(const ChatProfile& profile, std::wstring& error) {
    if (!profile.valid) {
        error = L"未应用版本档案";
        return false;
    }

    unsigned int bufferAddress = 0;
    return ResolveBuffer(profile, bufferAddress, error);
}

bool IsChattingAt(unsigned int gameBase, unsigned int chattingRva) {
    if (gameBase == 0 || chattingRva == 0) {
        return false;
    }

    const unsigned int address = gameBase + chattingRva;
    if (address < gameBase || !IsRangeAccessible(address, sizeof(unsigned int), false)) {
        return false;
    }

    unsigned int value = 0;
    std::memcpy(&value, reinterpret_cast<const void*>(static_cast<uintptr_t>(address)), sizeof(value));
    return value != 0;
}

bool IsEscapeMenuOpenAt(unsigned int gameBase, unsigned int cguiRva,
                        unsigned int escapeMenuOffset) {
    if (gameBase == 0 || cguiRva == 0 || escapeMenuOffset == 0) {
        return false;
    }

    const unsigned int cguiAddress = gameBase + cguiRva;
    if (cguiAddress < gameBase) {
        return false;
    }

    unsigned int cgui = 0;
    if (!ReadPointer(cguiAddress, cgui) || cgui == 0) {
        return false;
    }

    unsigned int menu = 0;
    if (!ReadPointer(cgui + escapeMenuOffset, menu) || menu == 0) {
        return false;
    }

    unsigned int currentMode = 0;
    if (!ReadPointer(cgui + kCurrentModeOffset, currentMode)) {
        return false;
    }

    return currentMode == menu;
}

ChatResult SendChat(const ChatProfile& profile, const std::wstring& text, int channel, int sendDelayMs) {
    if (!profile.valid) {
        return Fail(L"未应用版本档案");
    }

    if (text.empty()) {
        return Fail(L"喊话内容为空");
    }

    HWND window = FindGameWindow();
    if (window == nullptr) {
        return Fail(L"未找到游戏窗口");
    }

    unsigned int bufferAddress = 0;
    std::wstring error;
    if (!ResolveBuffer(profile, bufferAddress, error)) {
        return Fail(error);
    }

    std::string utf8 = LimitUtf8(Utf8FromWide(text));
    if (utf8.empty()) {
        return Fail(L"喊话编码失败");
    }

    if (!WriteBuffer(bufferAddress, utf8)) {
        return Fail(L"写入聊天缓冲区失败");
    }

    // Warcraft III: Enter opens chat to allies, Shift+Enter to all players.
    if (channel == 1) {
        PostEnter(window);
    } else {
        PostKey(window, VK_SHIFT, true);
        PostEnter(window);
        PostKey(window, VK_SHIFT, false);
    }

    Sleep(static_cast<DWORD>(std::max(20, std::min(500, sendDelayMs))));

    if (!WriteBuffer(bufferAddress, utf8)) {
        return Fail(L"二次写入聊天缓冲区失败");
    }

    if (!IsRangeAccessible(bufferAddress, utf8.size(), false) ||
        std::memcmp(reinterpret_cast<const void*>(static_cast<uintptr_t>(bufferAddress)), utf8.data(), utf8.size()) != 0) {
        return Fail(L"聊天缓冲区读回校验失败");
    }

    PostEnter(window);

    ChatResult result;
    result.ok = true;
    result.sentBytes = static_cast<int>(utf8.size());
    return result;
}

}  // namespace iwk
