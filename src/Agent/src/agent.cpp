#include "agent.h"

#include <algorithm>
#include <sstream>
#include <thread>
#include <vector>

#include "agent_version.h"
#include "protocol.h"

namespace iwk {

namespace {

std::string OkResponse(int sequence, const std::string& detail) {
    std::ostringstream stream;
    stream << "OK " << sequence;
    if (!detail.empty()) {
        stream << ' ' << detail;
    }
    return stream.str();
}

std::string ErrorResponse(int sequence, int code, const std::wstring& message) {
    std::ostringstream stream;
    stream << "ERR " << sequence << ' ' << code << ' ' << HexFromUtf16(message);
    return stream.str();
}

bool WriteAll(HANDLE pipe, const std::string& data) {
    size_t offset = 0;
    while (offset < data.size()) {
        DWORD written = 0;
        if (!WriteFile(pipe, data.data() + offset, static_cast<DWORD>(data.size() - offset), &written, nullptr) || written == 0) {
            return false;
        }
        offset += written;
    }
    return true;
}

}  // namespace

void Agent::Start() {
    HANDLE thread = CreateThread(nullptr, 0, ThreadProc, nullptr, 0, nullptr);
    if (thread != nullptr) {
        CloseHandle(thread);
    }
}

DWORD WINAPI Agent::ThreadProc(LPVOID) {
    Agent agent;
    agent.Run();
    return 0;
}

void Agent::Run() {
    startTick_ = GetTickCount64();
    paused_ = false;
    sendDelayMs_ = 80;
    game_ = DetectGameInfo();
    cameraManager_.SetGameBase(game_.base, game_.imageSize);
    statusBarManager_.SetGameBase(game_.base, game_.imageSize);

    const DWORD pid = GetCurrentProcessId();
    std::wostringstream mutexName;
    mutexName << L"Local\\iWar3Key.Agent." << pid;
    HANDLE mutex = CreateMutexW(nullptr, TRUE, mutexName.str().c_str());
    if (mutex == nullptr) {
        return;
    }

    if (GetLastError() == ERROR_ALREADY_EXISTS) {
        CloseHandle(mutex);
        return;
    }

    skillManager_.Start();
    keyHook_.SetSkillManager(&skillManager_);
    keyHook_.SetCameraManager(&cameraManager_);
    // 1.20e replaces its window procedure during early main-menu startup.
    // Subclassing it in that window and immediately re-installing after the
    // game changes it back causes intermittent clean exits / 0xC0000005.
    // Start the hook after the menu has settled; skill/camera keys are not
    // needed before then.
    std::thread([this]() {
        Sleep(3000);
        keyHook_.Start();
    }).detach();

    std::wostringstream pipeName;
    pipeName << L"\\\\.\\pipe\\iWar3Key.Agent." << pid;

    while (true) {
        HANDLE pipe = CreateNamedPipeW(
            pipeName.str().c_str(),
            PIPE_ACCESS_DUPLEX,
            PIPE_TYPE_BYTE | PIPE_READMODE_BYTE | PIPE_WAIT,
            1,
            8192,
            8192,
            0,
            nullptr);

        if (pipe == INVALID_HANDLE_VALUE) {
            Sleep(500);
            continue;
        }

        BOOL connected = ConnectNamedPipe(pipe, nullptr);
        DWORD connectError = GetLastError();
        if (connected || connectError == ERROR_PIPE_CONNECTED) {
            ServeClient(pipe);
            skillManager_.DisableAndRestore();
            cameraManager_.Disable();
            statusBarManager_.Disable();
            FlushFileBuffers(pipe);
            DisconnectNamedPipe(pipe);
        }

        CloseHandle(pipe);
        Sleep(50);
    }
}

void Agent::ServeClient(HANDLE pipe) {
    std::string pending;
    char buffer[4096];

    while (true) {
        DWORD read = 0;
        if (!ReadFile(pipe, buffer, sizeof(buffer), &read, nullptr) || read == 0) {
            break;
        }

        pending.append(buffer, read);
        size_t newline;
        while ((newline = pending.find('\n')) != std::string::npos) {
            std::string line = pending.substr(0, newline);
            pending.erase(0, newline + 1);
            if (!line.empty() && line.back() == '\r') {
                line.pop_back();
            }

            if (line.empty()) {
                continue;
            }

            std::string response = HandleLine(line);
            if (response.empty()) {
                continue;
            }

            response.push_back('\n');
            if (!WriteAll(pipe, response)) {
                return;
            }
        }

        if (pending.size() > 64 * 1024) {
            pending.clear();
        }
    }
}

std::string Agent::HandleLine(const std::string& line) {
    std::vector<std::string> tokens = SplitTokens(line);
    if (tokens.size() < 2) {
        return ErrorResponse(0, 400, L"缺少命令序号");
    }

    int sequence = 0;
    ParseInt(tokens[1], sequence);
    const std::string& command = tokens[0];

    if (command == "HELLO") {
        if (game_.base == 0) {
            game_ = DetectGameInfo();
        }
        cameraManager_.SetGameBase(game_.base, game_.imageSize);
        statusBarManager_.SetGameBase(game_.base, game_.imageSize);
        keyHook_.SetChatState(game_.base, profile_.isChattingRva);
        keyHook_.SetMenuState(static_cast<unsigned int>(game_.base),
                              static_cast<unsigned int>(profile_.cguiGlobalRva),
                              profile_.escapeMenuOffset);

        std::ostringstream stream;
        stream << "HELLO " << sequence << ' '
               << IWK_AGENT_ABI << ' '
               << HexFromUtf16(game_.version) << ' '
               << std::hex << std::uppercase << game_.base;
        return stream.str();
    }

    if (command == "PROFILE") {
        if (tokens.size() < 7) {
            return ErrorResponse(sequence, 400, L"版本档案参数不足");
        }

        unsigned long long cgui = 0;
        unsigned long long bar = 0;
        unsigned long long textObject = 0;
        unsigned long long textBuffer = 0;
        unsigned long long chatting = 0;
        unsigned long long escapeMenu = 0;
        if (!ParseHexU64(tokens[2], cgui) || !ParseHexU64(tokens[3], bar) ||
            !ParseHexU64(tokens[4], textObject) || !ParseHexU64(tokens[5], textBuffer) ||
            !ParseHexU64(tokens[6], chatting) ||
            cgui >= 0x10000000ULL || bar >= 0x10000ULL ||
            textObject >= 0x10000ULL || textBuffer >= 0x10000ULL ||
            chatting >= 0x10000000ULL) {
            return ErrorResponse(sequence, 422, L"版本档案参数无效");
        }

        // Optional 1.27+ field: CGameUI offset of the CEscMenu pointer. An
        // older shell simply omits it, leaving menu protection disabled.
        if (tokens.size() >= 8 &&
            (!ParseHexU64(tokens[7], escapeMenu) || escapeMenu >= 0x10000ULL)) {
            return ErrorResponse(sequence, 422, L"版本档案菜单参数无效");
        }

        profile_.cguiGlobalRva = cgui;
        profile_.chatBarOffset = static_cast<unsigned int>(bar);
        profile_.textObjectOffset = static_cast<unsigned int>(textObject);
        profile_.textBufferOffset = static_cast<unsigned int>(textBuffer);
        profile_.isChattingRva = static_cast<unsigned int>(chatting);
        profile_.escapeMenuOffset = static_cast<unsigned int>(escapeMenu);
        profile_.valid = true;
        keyHook_.SetChatState(game_.base, profile_.isChattingRva);
        keyHook_.SetMenuState(static_cast<unsigned int>(game_.base),
                              static_cast<unsigned int>(profile_.cguiGlobalRva),
                              profile_.escapeMenuOffset);

        std::ostringstream stream;
        stream << '1';
        return OkResponse(sequence, stream.str());
    }

    if (command == "SKILLPROFILE") {
        if (tokens.size() < 10) {
            return ErrorResponse(sequence, 400, L"技能档案参数不足");
        }

        unsigned long long unitListRva = 0;
        unsigned long long cguiRva = 0;
        unsigned long long selectionOffset = 0;
        unsigned long long activeUnitOffset = 0;
        unsigned long long commandBarOffset = 0;
        unsigned long long commandGridOffset = 0;
        unsigned long long commandFrameButtonOffset = 0;
        unsigned long long commandButtonHotkeyOffset = 0;
        if (!ParseHexU64(tokens[2], unitListRva) ||
            !ParseHexU64(tokens[3], cguiRva) ||
            !ParseHexU64(tokens[4], selectionOffset) ||
            !ParseHexU64(tokens[5], activeUnitOffset) ||
            !ParseHexU64(tokens[6], commandBarOffset) ||
            !ParseHexU64(tokens[7], commandGridOffset) ||
            !ParseHexU64(tokens[8], commandFrameButtonOffset) ||
            !ParseHexU64(tokens[9], commandButtonHotkeyOffset) ||
            unitListRva >= 0x10000000ULL ||
            cguiRva >= 0x10000000ULL ||
            selectionOffset >= 0x10000ULL ||
            activeUnitOffset >= 0x10000ULL ||
            commandBarOffset >= 0x10000ULL ||
            commandGridOffset >= 0x10000ULL ||
            commandFrameButtonOffset >= 0x10000ULL ||
            commandButtonHotkeyOffset >= 0x10000ULL) {
            return ErrorResponse(sequence, 422, L"技能档案参数无效");
        }
        // Command-card conversion only needs the CGameUI command-bar chain.
        // The selected-unit chain is optional and is used only to recognize
        // the hero learn page. 1.20e has the command bar but no verified
        // selection chain, so allow a command-card-only profile.
        const bool hasSelectionChain = unitListRva != 0 ||
            (cguiRva != 0 && selectionOffset != 0 && activeUnitOffset != 0);
        const bool hasCommandCard = cguiRva != 0 &&
            commandBarOffset != 0 && commandGridOffset != 0 &&
            commandFrameButtonOffset != 0 && commandButtonHotkeyOffset != 0;
        if (!hasSelectionChain && !hasCommandCard) {
            return ErrorResponse(sequence, 422, L"技能档案参数无效");
        }

        skillManager_.SetProfile(true, static_cast<unsigned int>(unitListRva),
                                  static_cast<unsigned int>(cguiRva),
                                  static_cast<unsigned int>(selectionOffset),
                                  static_cast<unsigned int>(activeUnitOffset),
                                  static_cast<unsigned int>(commandBarOffset),
                                  static_cast<unsigned int>(commandGridOffset),
                                  static_cast<unsigned int>(commandFrameButtonOffset),
                                  static_cast<unsigned int>(commandButtonHotkeyOffset));
        return OkResponse(sequence, "1");
    }

    if (command == "SKILLINFO") {
        if (tokens.size() < 3) {
            return ErrorResponse(sequence, 400, L"技能信息参数不足");
        }

        const bool enabled = tokens[2] == "1";
        skillManager_.SetInfoEnabled(enabled);
        return OkResponse(sequence, "1");
    }

    if (command == "SKILLS") {
        if (tokens.size() < 32) {
            return ErrorResponse(sequence, 400, L"技能绑定参数不足");
        }

        std::vector<int> keys(12, 0);
        std::vector<int> quickCast(12, 0);
        int valid = 0;
        for (int i = 0; i < 12; ++i) {
            const std::string& token = tokens[2 + static_cast<size_t>(i)];
            if (token.size() == 1) {
                unsigned char ch = static_cast<unsigned char>(token[0]);
                if ((ch >= 'A' && ch <= 'Z') || (ch >= 'a' && ch <= 'z') || (ch >= '0' && ch <= '9')) {
                    keys[static_cast<size_t>(i)] = ch;
                    ++valid;
                }
            }
        }

        // 12 skill quick-cast flags and 6 item quick-cast flags follow the
        // key list.
        for (int i = 0; i < 12; ++i) {
            quickCast[static_cast<size_t>(i)] = tokens[14 + static_cast<size_t>(i)] == "1" ? 1 : 0;
        }

        std::vector<int> itemQuickCast(6, 0);
        for (int i = 0; i < 6; ++i) {
            itemQuickCast[static_cast<size_t>(i)] = tokens[26 + static_cast<size_t>(i)] == "1" ? 1 : 0;
        }

        skillManager_.SetBindings(keys, quickCast);
        skillManager_.SetItemQuickCast(itemQuickCast);
        std::ostringstream stream;
        stream << valid;
        return OkResponse(sequence, stream.str());
    }

    if (command == "CAMERAPROFILE") {
        if (tokens.size() < 7) {
            return ErrorResponse(sequence, 400, L"视距档案参数不足");
        }

        unsigned long long setFieldRva = 0;
        if (!ParseHexU64(tokens[2], setFieldRva) ||
            setFieldRva >= 0x10000000ULL || setFieldRva == 0) {
            return ErrorResponse(sequence, 422, L"视距档案参数无效");
        }

        unsigned long long cguiRva = 0;
        unsigned long long chatBarOffset = 0;
        unsigned long long textObjectOffset = 0;
        unsigned long long textBufferOffset = 0;
        if (!ParseHexU64(tokens[3], cguiRva) ||
            !ParseHexU64(tokens[4], chatBarOffset) ||
            !ParseHexU64(tokens[5], textObjectOffset) ||
            !ParseHexU64(tokens[6], textBufferOffset) ||
            cguiRva >= 0x10000000ULL || cguiRva == 0 ||
            chatBarOffset >= 0x10000ULL ||
            textObjectOffset >= 0x10000ULL ||
            textBufferOffset >= 0x10000ULL) {
            return ErrorResponse(sequence, 422, L"视距档案参数无效");
        }

        cameraManager_.SetGameBase(game_.base, game_.imageSize);
        cameraManager_.SetProfile(
            true, static_cast<unsigned int>(setFieldRva),
            static_cast<unsigned int>(cguiRva),
            static_cast<unsigned int>(chatBarOffset),
            static_cast<unsigned int>(textObjectOffset),
            static_cast<unsigned int>(textBufferOffset));
        return OkResponse(sequence, "1");
    }

    if (command == "CAMERA") {
        if (tokens.size() < 4) {
            return ErrorResponse(sequence, 400, L"视距参数不足");
        }

        bool enabled = tokens[2] == "1";
        int distance = 3000;
        ParseInt(tokens[3], distance);

        if (tokens.size() >= 8) {
            int zoomOutVk = 0;
            int zoomOutModifiers = 0;
            int zoomInVk = 0;
            int zoomInModifiers = 0;
            if (!ParseInt(tokens[4], zoomOutVk) ||
                !ParseInt(tokens[5], zoomOutModifiers) ||
                !ParseInt(tokens[6], zoomInVk) ||
                !ParseInt(tokens[7], zoomInModifiers) ||
                zoomOutVk < 0 || zoomOutVk > 0xFF ||
                zoomInVk < 0 || zoomInVk > 0xFF ||
                zoomOutModifiers < 0 || zoomOutModifiers > 0x0F ||
                zoomInModifiers < 0 || zoomInModifiers > 0x0F) {
                return ErrorResponse(sequence, 422, L"视距快捷键参数无效");
            }

            cameraManager_.SetHotkeys(
                zoomOutVk, zoomOutModifiers, zoomInVk, zoomInModifiers);
        }

        cameraManager_.SetEnabled(enabled, distance);
        return OkResponse(sequence, cameraManager_.Status());
    }

    if (command == "STATUSBARPROFILE") {
        // allocCall updateCall originalUpdate ctor getUnitState getBarPosition
        // setBarPosition setBarBounds getUnitUIDef isUnitStructure
        // getLocalPlayer getCPlayerWar3 cPlayerAttr integerGetValue cgui
        // healthFlagOffset
        if (tokens.size() < 18) {
            return ErrorResponse(sequence, 400, L"状态条档案参数不足");
        }

        unsigned long long values[16] = {};
        for (size_t i = 0; i < 16; ++i) {
            if (!ParseHexU64(tokens[2 + i], values[i]) ||
                (i < 15 && values[i] >= 0x10000000ULL) ||
                (i == 15 && values[i] >= 0x10000ULL)) {
                return ErrorResponse(sequence, 422, L"状态条档案参数无效");
            }
        }

        statusBarManager_.SetGameBase(game_.base, game_.imageSize);
        statusBarManager_.SetProfile(
            true,
            static_cast<uint32_t>(values[0]),
            static_cast<uint32_t>(values[1]),
            static_cast<uint32_t>(values[2]),
            static_cast<uint32_t>(values[3]),
            static_cast<uint32_t>(values[4]),
            static_cast<uint32_t>(values[5]),
            static_cast<uint32_t>(values[6]),
            static_cast<uint32_t>(values[7]),
            static_cast<uint32_t>(values[8]),
            static_cast<uint32_t>(values[9]),
            static_cast<uint32_t>(values[10]),
            static_cast<uint32_t>(values[11]),
            static_cast<uint32_t>(values[12]),
            static_cast<uint32_t>(values[13]),
            static_cast<uint32_t>(values[14]),
            static_cast<uint32_t>(values[15]));
        statusBarManager_.Apply();
        return OkResponse(sequence, statusBarManager_.Status());
    }

    if (command == "STATUSBAR") {
        if (tokens.size() < 5) {
            return ErrorResponse(sequence, 400, L"状态条参数不足");
        }

        statusBarManager_.SetEnabled(tokens[2] == "1",
                                     tokens[3] == "1",
                                     tokens[4] == "1");
        return OkResponse(sequence, statusBarManager_.Status());
    }

    if (command == "CONFIG") {
        if (tokens.size() < 3) {
            return ErrorResponse(sequence, 400, L"配置参数不足");
        }

        int sendDelay = sendDelayMs_;
        ParseInt(tokens[2], sendDelay);
        sendDelayMs_ = std::max(20, std::min(500, sendDelay));
        return OkResponse(sequence, "1");
    }

    if (command == "PAUSE") {
        if (tokens.size() < 3) {
            return ErrorResponse(sequence, 400, L"暂停参数不足");
        }

        paused_ = tokens[2] == "1";
        keyHook_.SetPaused(paused_);
        return OkResponse(sequence, "1");
    }

    if (command == "STATE") {
        ULONGLONG uptime = GetTickCount64() - startTick_;
        std::wstring readyError;
        const bool ready = IsChatReady(profile_, readyError);
        std::ostringstream stream;
        stream << "STATE " << sequence << ' '
               << uptime << ' '
               << (ready ? 1 : 0);
        return stream.str();
    }

    if (command == "CHAT") {
        if (tokens.size() < 4) {
            return ErrorResponse(sequence, 400, L"喊话参数不足");
        }

        if (paused_) {
            return ErrorResponse(sequence, 409, L"已紧急暂停");
        }

        if (!profile_.valid) {
            return ErrorResponse(sequence, 501, L"未应用版本档案");
        }

        int channel = 0;
        ParseInt(tokens[2], channel);
        std::wstring text = Utf16FromHex(tokens[3]);
        if (text.empty()) {
            return ErrorResponse(sequence, 422, L"喊话内容为空或编码无效");
        }

        ChatResult result = SendChat(profile_, text, channel, sendDelayMs_);
        if (!result.ok) {
            return ErrorResponse(sequence, 502, result.error);
        }

        std::ostringstream stream;
        stream << result.sentBytes;
        return OkResponse(sequence, stream.str());
    }

    return ErrorResponse(sequence, 400, L"未知命令");
}

}  // namespace iwk
