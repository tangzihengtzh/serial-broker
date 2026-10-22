#pragma once
#include <winsock2.h>
#include <windows.h>
#include <atomic>
#include <span>
#include <stdexcept>
#include <string>
#include "serial_broker/protocol.hpp"

namespace sb {
class Handle {
public:
    explicit Handle(HANDLE value = INVALID_HANDLE_VALUE) noexcept : value_(value) {}
    ~Handle() { reset(); }
    Handle(const Handle&) = delete;
    Handle& operator=(const Handle&) = delete;
    Handle(Handle&& other) noexcept : value_(other.release()) {}
    Handle& operator=(Handle&& other) noexcept { if (this != &other) reset(other.release()); return *this; }
    HANDLE get() const noexcept { return value_; }
    explicit operator bool() const noexcept { return value_ != nullptr && value_ != INVALID_HANDLE_VALUE; }
    HANDLE release() noexcept { auto v = value_; value_ = INVALID_HANDLE_VALUE; return v; }
    void reset(HANDLE value = INVALID_HANDLE_VALUE) noexcept { if (*this) CloseHandle(value_); value_ = value; }
private:
    HANDLE value_;
};
enum class IoResult { ok, timeout, closed, stopped };
IoResult read_some(HANDLE handle, std::span<std::uint8_t> out, DWORD& count,
                   const std::atomic_bool& running, DWORD timeout = 250);
bool read_exact(HANDLE handle, std::span<std::uint8_t> out, const std::atomic_bool& running);
bool write_all(HANDLE handle, std::span<const std::uint8_t> data,
               const std::atomic_bool& running, DWORD deadline = 3000);
std::string win_error(DWORD code = GetLastError());
std::wstring wide(const std::string& text);
std::string utf8(const std::wstring& text);
std::string timestamp();
bool read_frame(HANDLE handle, Message& type, Bytes& payload, const std::atomic_bool& running);
} // namespace sb
