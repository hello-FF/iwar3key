#include "game_info.h"

#include <windows.h>
#include <psapi.h>
#include <winver.h>

#include <sstream>

namespace iwk {

namespace {

std::wstring ReadFileVersion(const std::wstring& path) {
    DWORD ignored = 0;
    DWORD size = GetFileVersionInfoSizeW(path.c_str(), &ignored);
    if (size == 0) {
        return std::wstring();
    }

    std::wstring buffer(size / sizeof(wchar_t) + 1, L'\0');
    if (!GetFileVersionInfoW(path.c_str(), 0, size, &buffer[0])) {
        return std::wstring();
    }

    VS_FIXEDFILEINFO* info = nullptr;
    UINT infoSize = 0;
    if (!VerQueryValueW(&buffer[0], L"\\", reinterpret_cast<void**>(&info), &infoSize) ||
        info == nullptr || infoSize < sizeof(VS_FIXEDFILEINFO)) {
        return std::wstring();
    }

    std::wostringstream stream;
    stream << HIWORD(info->dwFileVersionMS) << L"."
           << LOWORD(info->dwFileVersionMS) << L"."
           << HIWORD(info->dwFileVersionLS) << L"."
           << LOWORD(info->dwFileVersionLS);
    return stream.str();
}

}  // namespace

GameInfo DetectGameInfo() {
    GameInfo info{};
    HMODULE module = GetModuleHandleW(L"Game.dll");
    if (module == nullptr) {
        info.version = L"unknown";
        return info;
    }

    info.base = reinterpret_cast<unsigned int>(module);

    MODULEINFO moduleInfo{};
    if (GetModuleInformation(GetCurrentProcess(), module, &moduleInfo, sizeof(moduleInfo))) {
        info.imageSize = moduleInfo.SizeOfImage;
    }

    wchar_t path[MAX_PATH]{};
    DWORD length = GetModuleFileNameW(module, path, MAX_PATH);
    if (length > 0 && length < MAX_PATH) {
        info.version = ReadFileVersion(path);
    }

    if (info.version.empty()) {
        info.version = L"unknown";
    }

    return info;
}

}  // namespace iwk
