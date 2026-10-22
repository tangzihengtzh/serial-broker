#include "win_io.hpp"
#include <algorithm>
#include <cstdio>

namespace sb {
namespace {
IoResult finish(HANDLE handle, OVERLAPPED& op, DWORD& count,
                const std::atomic_bool& running, DWORD timeout) {
    const auto started = GetTickCount64();
    for (;;) {
        auto result = WaitForSingleObject(op.hEvent, 50);
        if (result == WAIT_OBJECT_0) {
            if (GetOverlappedResult(handle, &op, &count, FALSE)) return IoResult::ok;
            return running ? IoResult::closed : IoResult::stopped;
        }
        if (!running || GetTickCount64() - started >= timeout || result == WAIT_FAILED) {
            CancelIoEx(handle, &op);
            // OVERLAPPED and its buffer must outlive cancellation completion.
            const bool completed = GetOverlappedResult(handle, &op, &count, TRUE) != FALSE;
            const auto error = completed ? ERROR_SUCCESS : GetLastError();
            if (!running) return IoResult::stopped;
            // A completion can race the timeout. Preserve its bytes rather than losing a frame prefix.
            if (completed) return IoResult::ok;
            return error == ERROR_OPERATION_ABORTED ? IoResult::timeout : IoResult::closed;
        }
    }
}
}
IoResult read_some(HANDLE handle, std::span<std::uint8_t> out, DWORD& count,
                   const std::atomic_bool& running, DWORD timeout) {
    count = 0;
    if (!running) return IoResult::stopped;
    Handle event(CreateEventW(nullptr, TRUE, FALSE, nullptr));
    if (!event) return IoResult::closed;
    OVERLAPPED op{}; op.hEvent = event.get();
    if (ReadFile(handle, out.data(), static_cast<DWORD>(out.size()), &count, &op)) return IoResult::ok;
    if (GetLastError() != ERROR_IO_PENDING) return IoResult::closed;
    return finish(handle, op, count, running, timeout);
}
bool read_exact(HANDLE handle, std::span<std::uint8_t> out, const std::atomic_bool& running) {
    std::size_t offset = 0;
    while (offset < out.size() && running) {
        DWORD count{};
        const auto result = read_some(handle, out.subspan(offset), count, running);
        if (result == IoResult::timeout) continue;
        if (result != IoResult::ok || !count) return false;
        offset += count;
    }
    return offset == out.size();
}
bool write_all(HANDLE handle, std::span<const std::uint8_t> data,
               const std::atomic_bool& running, DWORD deadline) {
    const auto started = GetTickCount64();
    std::size_t offset = 0;
    while (offset < data.size() && running) {
        const auto elapsed = GetTickCount64() - started;
        if (elapsed >= deadline) return false;
        Handle event(CreateEventW(nullptr, TRUE, FALSE, nullptr));
        if (!event) return false;
        OVERLAPPED op{}; op.hEvent = event.get();
        DWORD count{};
        if (!WriteFile(handle, data.data() + offset, static_cast<DWORD>(data.size() - offset), &count, &op)) {
            if (GetLastError() != ERROR_IO_PENDING ||
                finish(handle, op, count, running, static_cast<DWORD>(deadline - elapsed)) != IoResult::ok) return false;
        }
        if (!count) return false;
        offset += count;
    }
    return offset == data.size();
}
std::string win_error(DWORD code) {
    wchar_t* message = nullptr;
    FormatMessageW(FORMAT_MESSAGE_ALLOCATE_BUFFER | FORMAT_MESSAGE_FROM_SYSTEM | FORMAT_MESSAGE_IGNORE_INSERTS,
                   nullptr, code, 0, reinterpret_cast<LPWSTR>(&message), 0, nullptr);
    std::string text = message ? utf8(message) : "Unknown Windows error";
    if (message) LocalFree(message);
    while (!text.empty() && (text.back() == '\r' || text.back() == '\n')) text.pop_back();
    return text + " (" + std::to_string(code) + ")";
}
std::wstring wide(const std::string& text) {
    if (text.empty()) return {};
    const int n = MultiByteToWideChar(CP_UTF8, 0, text.data(), static_cast<int>(text.size()), nullptr, 0);
    std::wstring result(n, L'\0');
    MultiByteToWideChar(CP_UTF8, 0, text.data(), static_cast<int>(text.size()), result.data(), n);
    return result;
}
std::string utf8(const std::wstring& text) {
    if (text.empty()) return {};
    const int n = WideCharToMultiByte(CP_UTF8, 0, text.data(), static_cast<int>(text.size()), nullptr, 0, nullptr, nullptr);
    std::string result(n, '\0');
    WideCharToMultiByte(CP_UTF8, 0, text.data(), static_cast<int>(text.size()), result.data(), n, nullptr, nullptr);
    return result;
}
std::string timestamp() {
    SYSTEMTIME t{}; GetLocalTime(&t);
    char result[40]{};
    sprintf_s(result, "%04u-%02u-%02u %02u:%02u:%02u.%03u", t.wYear, t.wMonth, t.wDay,
              t.wHour, t.wMinute, t.wSecond, t.wMilliseconds);
    return result;
}
bool read_frame(HANDLE handle, Message& type, Bytes& payload, const std::atomic_bool& running) {
    std::array<std::uint8_t, header_size> header{};
    if (!read_exact(handle, header, running) || !valid_header(header)) return false;
    type = static_cast<Message>(header[2] | (static_cast<unsigned>(header[3]) << 8));
    payload.resize(u32(std::span(header).subspan(4)));
    return payload.empty() || read_exact(handle, payload, running);
}
} // namespace sb
