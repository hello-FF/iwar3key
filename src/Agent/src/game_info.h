#pragma once

#include <string>

namespace iwk {

struct GameInfo {
    std::wstring version;
    unsigned int base;
    unsigned int imageSize;
};

GameInfo DetectGameInfo();

}  // namespace iwk
