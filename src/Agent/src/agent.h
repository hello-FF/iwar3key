#pragma once

#include <windows.h>

#include <atomic>
#include <string>

#include "camera_manager.h"
#include "chat.h"
#include "game_info.h"
#include "key_hook.h"
#include "skill_manager.h"
#include "status_bar_manager.h"

namespace iwk {

class Agent {
public:
    static void Start();

private:
    static DWORD WINAPI ThreadProc(LPVOID parameter);
    void Run();
    void ServeClient(HANDLE pipe);
    std::string HandleLine(const std::string& line);

    ChatProfile profile_;
    bool paused_;
    int sendDelayMs_;
    ULONGLONG startTick_;
    GameInfo game_;
    SkillManager skillManager_;
    CameraManager cameraManager_;
    StatusBarManager statusBarManager_;
    KeyHook keyHook_;
};

}  // namespace iwk
