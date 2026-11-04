#pragma once

#include <string>
#include <vector>

namespace iwk {

std::wstring Utf16FromHex(const std::string& hex);
std::string HexFromUtf16(const std::wstring& text);
std::string Utf8FromWide(const std::wstring& text);
std::wstring WideFromUtf8(const std::string& text);
std::vector<std::string> SplitTokens(const std::string& line);
bool ParseInt(const std::string& text, int& value);
bool ParseHexU64(const std::string& text, unsigned long long& value);

}  // namespace iwk
