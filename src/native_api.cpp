#include "broker.hpp"
#include <algorithm>
#include <cstring>
#include <sstream>

namespace {
using Notice = void(__cdecl*)(void*, const char*);
using Monitor = void(__cdecl*)(void*, int, const unsigned char*, int);
struct Instance {
    sb::Broker broker;
    Instance(Notice notice, Monitor monitor, void* context)
        : broker([=](const std::string& text) { if (notice) notice(context, text.c_str()); },
                 [=](bool tx, const sb::Bytes& data) {
                     if (monitor) monitor(context, tx ? 1 : 0, data.data(), static_cast<int>(data.size()));
                 }) {}
};
std::string json(const std::string& value) {
    std::string result = "\"";
    constexpr char hex[] = "0123456789abcdef";
    for (unsigned char c : value) {
        if (c == '"' || c == '\\') { result += '\\'; result += static_cast<char>(c); }
        else if (c < 32) { result += "\\u00"; result += hex[c >> 4]; result += hex[c & 15]; }
        else result += static_cast<char>(c);
    }
    return result + '"';
}
int copy(const std::string& text, char* buffer, int capacity) {
    const int required = static_cast<int>(text.size()) + 1;
    if (buffer && capacity >= required) std::memcpy(buffer, text.c_str(), static_cast<std::size_t>(required));
    return required;
}
}

// 每个句柄拥有独立Broker；内存及线程均在原生模块内创建和释放，不跨CRT释放对象。
extern "C" {
__declspec(dllexport) void* __cdecl sb_create(Notice notice, Monitor monitor, void* context) {
    try { return new Instance(notice, monitor, context); } catch (...) { return nullptr; }
}
__declspec(dllexport) int __cdecl sb_start(void* handle, const wchar_t* port, unsigned long baud,
        unsigned short tcp, int enabled, int dtr, int rts, const wchar_t* logs,
        const wchar_t* test_pipe, char* error, int capacity) {
    try {
        sb::Options options;
        options.port = port; options.baud = baud; options.tcp_port = tcp;
        options.tcp_enabled = enabled != 0; options.dtr = dtr != 0; options.rts = rts != 0;
        options.log_root = logs; if (test_pipe) options.test_pipe = test_pipe;
        static_cast<Instance*>(handle)->broker.start(options);
        return 1;
    } catch (const std::exception& e) {
        const std::string message(e.what());
        if (error && capacity > 0) {
            const auto size = (std::min)(message.size(), static_cast<std::size_t>(capacity - 1));
            std::memcpy(error, message.data(), size); error[size] = 0;
        }
        return 0;
    }
}
__declspec(dllexport) void __cdecl sb_stop(void* handle) { static_cast<Instance*>(handle)->broker.stop(); }
__declspec(dllexport) void __cdecl sb_destroy(void* handle) { delete static_cast<Instance*>(handle); }
__declspec(dllexport) void __cdecl sb_policy(void* handle, int policy) {
    if (policy >= 0 && policy <= 2) static_cast<Instance*>(handle)->broker.set_policy(static_cast<sb::Policy>(policy));
}
__declspec(dllexport) void __cdecl sb_disconnect(void* handle, unsigned long long id) {
    static_cast<Instance*>(handle)->broker.disconnect(id);
}
__declspec(dllexport) int __cdecl sb_snapshot(void* handle, char* output, int capacity) {
    try {
        const auto state = static_cast<Instance*>(handle)->broker.snapshot();
        std::ostringstream text;
        text << "{\"Running\":" << (state.running ? "true" : "false") << ",\"Policy\":"
             << static_cast<int>(state.policy) << ",\"Writer\":" << state.writer << ",\"Rx\":" << state.rx
             << ",\"Tx\":" << state.tx << ",\"Clients\":[";
        bool first = true;
        for (const auto& client : state.clients) {
            if (!first) text << ',';
            first = false;
            text << "{\"Id\":" << client.id << ",\"Name\":" << json(client.name)
                 << ",\"Writer\":" << (client.writer ? "true" : "false") << '}';
        }
        text << "]}";
        return copy(text.str(), output, capacity);
    } catch (...) { return 0; }
}
}
