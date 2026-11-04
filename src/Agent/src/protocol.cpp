#include "protocol.h"

#include <windows.h>

#include <cstdlib>
#include <cwchar>

namespace iwk {

namespace {

int HexValue(char c) {
    if (c >= '0' && c <= '9') return c - '0';
    if (c >= 'a' && c <= 'f') return c - 'a' + 10;
    if (c >= 'A' && c <= 'F') return c - 'A' + 10;
    return -1;
}

}  // namespace

std::wstring Utf16FromHex(const std::string& hex) {
    if (hex.empty() || (hex.size() & 1) != 0) {
        return std::wstring();
    }

    std::string bytes;
    bytes.reserve(hex.size() / 2);
    for (size_t i = 0; i < hex.size(); i += 2) {
        int hi = HexValue(hex[i]);
        int lo = HexValue(hex[i + 1]);
        if (hi < 0 || lo < 0) {
            return std::wstring();
        }
        bytes.push_back(static_cast<char>((hi << 4) | lo));
    }

    if (bytes.size() < sizeof(wchar_t) || (bytes.size() % sizeof(wchar_t)) != 0) {
        return std::wstring();
    }

    return std::wstring(reinterpret_cast<const wchar_t*>(bytes.data()), bytes.size() / sizeof(wchar_t));
}

std::string HexFromUtf16(const std::wstring& text) {
    static const char* digits = "0123456789ABCDEF";
    const unsigned char* bytes = reinterpret_cast<const unsigned char*>(text.data());
    const size_t count = text.size() * sizeof(wchar_t);
    std::string result;
    result.reserve(count * 2);
    for (size_t i = 0; i < count; ++i) {
        result.push_back(digits[bytes[i] >> 4]);
        result.push_back(digits[bytes[i] & 0x0F]);
    }
    return result;
}

std::string Utf8FromWide(const std::wstring& text) {
    if (text.empty()) {
        return std::string();
    }

    int size = WideCharToMultiByte(CP_UTF8, 0, text.c_str(), static_cast<int>(text.size()), nullptr, 0, nullptr, nullptr);
    if (size <= 0) {
        return std::string();
    }

    std::string result(static_cast<size_t>(size), '\0');
    WideCharToMultiByte(CP_UTF8, 0, text.c_str(), static_cast<int>(text.size()), &result[0], size, nullptr, nullptr);
    return result;
}

std::wstring WideFromUtf8(const std::string& text) {
    if (text.empty()) {
        return std::wstring();
    }

    int size = MultiByteToWideChar(CP_UTF8, 0, text.c_str(), static_cast<int>(text.size()), nullptr, 0);
    if (size <= 0) {
        return std::wstring();
    }

    std::wstring result(static_cast<size_t>(size), L'\0');
    MultiByteToWideChar(CP_UTF8, 0, text.c_str(), static_cast<int>(text.size()), &result[0], size);
    return result;
}

std::vector<std::string> SplitTokens(const std::string& line) {
    std::vector<std::string> tokens;
    size_t start = std::string::npos;
    for (size_t i = 0; i <= line.size(); ++i) {
        bool separator = i == line.size() || line[i] == ' ' || line[i] == '\t' || line[i] == '\r' || line[i] == '\n';
        if (separator) {
            if (start != std::string::npos) {
                tokens.push_back(line.substr(start, i - start));
                start = std::string::npos;
            }
        } else if (start == std::string::npos) {
            start = i;
        }
    }
    return tokens;
}

bool ParseInt(const std::string& text, int& value) {
    if (text.empty()) {
        return false;
    }

    char* end = nullptr;
    long parsed = std::strtol(text.c_str(), &end, 10);
    if (end == text.c_str() || *end != '\0') {
        return false;
    }

    value = static_cast<int>(parsed);
    return true;
}

bool ParseHexU64(const std::string& text, unsigned long long& value) {
    if (text.empty()) {
        return false;
    }

    const char* start = text.c_str();
    if (text.size() > 2 && start[0] == '0' && (start[1] == 'x' || start[1] == 'X')) {
        start += 2;
    }

    char* end = nullptr;
    unsigned long long parsed = std::strtoull(start, &end, 16);
    if (end == start || *end != '\0') {
        return false;
    }

    value = parsed;
    return true;
}

}  // namespace iwk
