#include <windows.h>

#include "agent.h"
#include "agent_version.h"

extern "C" __declspec(dllexport) int __stdcall IwkAgentAbi() {
    return IWK_AGENT_ABI;
}

BOOL APIENTRY DllMain(HMODULE module, DWORD reason, LPVOID) {
    if (reason == DLL_PROCESS_ATTACH) {
        DisableThreadLibraryCalls(module);
        iwk::Agent::Start();
    }
    return TRUE;
}
