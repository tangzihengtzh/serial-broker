#include "win_io.hpp"
#include <chrono>
#include <condition_variable>
#include <conio.h>
#include <fcntl.h>
#include <io.h>
#include <iostream>
#include <mutex>
#include <thread>

namespace {
std::atomic_bool running{true};
std::atomic_bool interrupted{false};
BOOL WINAPI interrupt(DWORD kind) {
    if (kind == CTRL_C_EVENT || kind == CTRL_BREAK_EVENT || kind == CTRL_CLOSE_EVENT) {
        interrupted = true; running = false; return TRUE;
    }
    return FALSE;
}
struct Arguments {
    std::wstring pipe = L"SerialBroker.COM3";
    std::wstring command, text, hex;
    bool cr = false, lf = false;
    DWORD timeout = 5000;
};
void usage() {
    std::cerr << "serialctl watch|send|interactive [--pipe SerialBroker.COM3]\n"
                 "  send --text \"uname -a\" --cr\n"
                 "  send --hex \"03 0d\"\n"
                 "  interactive: console input, Enter sends CR; Ctrl+C exits\n"
                 "  --timeout 5000 sets write permission / acknowledgement timeout (ms)\n"
                 "  RX bytes go to stdout; status and errors go to stderr.\n"
                 "  Broker must be running. Default policy is shared; connected clients can write.\n";
}
DWORD number(const std::wstring& text, DWORD min, DWORD max) {
    std::size_t used{}; const auto value = std::stoull(text, &used);
    if (used != text.size() || value < min || value > max) throw std::runtime_error("Number out of range");
    return static_cast<DWORD>(value);
}
Arguments parse(int argc, wchar_t** argv) {
    Arguments a;
    if (argc < 2) throw std::runtime_error("Missing command");
    a.command = argv[1];
    if (a.command != L"watch" && a.command != L"send" && a.command != L"interactive") throw std::runtime_error("Unknown command");
    bool text_set = false, hex_set = false;
    for (int i = 2; i < argc; ++i) {
        const std::wstring option = argv[i];
        if (option == L"--cr") a.cr = true;
        else if (option == L"--lf") a.lf = true;
        else if (option == L"--pipe" || option == L"--text" || option == L"--hex" || option == L"--timeout") {
            if (++i >= argc) throw std::runtime_error("Missing option value");
            if (option == L"--pipe") a.pipe = argv[i];
            else if (option == L"--text") { a.text = argv[i]; text_set = true; }
            else if (option == L"--hex") { a.hex = argv[i]; hex_set = true; }
            else a.timeout = number(argv[i],100,300000);
        } else throw std::runtime_error("Unknown option: " + sb::utf8(option));
    }
    if (a.pipe.empty() || a.pipe.size() > 128 || a.pipe.find_first_of(L"\\/") != std::wstring::npos)
        throw std::runtime_error("Invalid pipe name; use SerialBroker.COM3");
    if (a.command == L"send" ? text_set == hex_set : (text_set || hex_set || a.cr || a.lf))
        throw std::runtime_error("send requires exactly one of --text / --hex; text options apply only to send");
    return a;
}
sb::Bytes payload(const Arguments& args) {
    sb::Bytes data;
    if (!args.hex.empty()) {
        int high = -1;
        for (auto c : args.hex) {
            if (c == L' ' || c == L'\t' || c == L'\r' || c == L'\n') continue;
            const int digit = c >= L'0' && c <= L'9' ? c - L'0' :
                c >= L'a' && c <= L'f' ? c - L'a' + 10 : c >= L'A' && c <= L'F' ? c - L'A' + 10 : -1;
            if (digit < 0) throw std::runtime_error("Invalid hexadecimal input");
            if (high < 0) high = digit;
            else { data.push_back(static_cast<std::uint8_t>((high << 4) | digit)); high = -1; }
        }
        if (high >= 0) throw std::runtime_error("Hex input has an odd number of digits");
    } else data = sb::bytes(sb::utf8(args.text));
    if (args.cr) data.push_back('\r');
    if (args.lf) data.push_back('\n');
    if (data.empty() || data.size() > sb::max_write) throw std::runtime_error("send accepts 1..4096 bytes including CR/LF");
    return data;
}
struct Client {
    sb::Handle pipe;
    std::thread reader;
    std::mutex mutex;
    std::condition_variable changed;
    bool granted = false;
    std::uint32_t acknowledged = 0, rejected = 0;
    bool rejection = false;
    std::string reason;
    ~Client() {
        running = false;
        if (pipe) CancelIoEx(pipe.get(),nullptr);
        if (reader.joinable()) { CancelSynchronousIo(reader.native_handle()); reader.join(); }
    }
    void start(const Arguments& args) {
        const auto path = L"\\\\.\\pipe\\" + args.pipe;
        const auto began = GetTickCount64();
        while (running) {
            pipe.reset(CreateFileW(path.c_str(),GENERIC_READ | GENERIC_WRITE,0,nullptr,OPEN_EXISTING,
                                   FILE_FLAG_OVERLAPPED | SECURITY_SQOS_PRESENT | SECURITY_IDENTIFICATION,nullptr));
            if (pipe) break;
            const auto error = GetLastError();
            if (error != ERROR_PIPE_BUSY || GetTickCount64() - began >= args.timeout)
                throw std::runtime_error("Connect pipe: " + sb::win_error(error));
            WaitNamedPipeW(path.c_str(),100);
        }
        if (!pipe || !running) throw std::runtime_error("Connection cancelled");
        if (!send(sb::Message::hello,sb::bytes("serialctl"))) throw std::runtime_error("Cannot send handshake");
        reader = std::thread([this] {
            sb::Message type{}; sb::Bytes data;
            while (running && sb::read_frame(pipe.get(),type,data,running)) {
                if (type == sb::Message::data) {
                    std::cout.write(reinterpret_cast<const char*>(data.data()),static_cast<std::streamsize>(data.size())); std::cout.flush();
                    if (!std::cout) break;
                } else if (type == sb::Message::status) {
                    std::cerr << "[broker] " << std::string(data.begin(),data.end()) << '\n';
                } else {
                    std::lock_guard lock(mutex);
                    if (type == sb::Message::granted) granted = true;
                    else if (type == sb::Message::written && data.size() == 8) acknowledged = sb::u32(data);
                    else if (type == sb::Message::rejected && data.size() >= 4) {
                        rejected = sb::u32(data); rejection = true; reason.assign(data.begin() + 4,data.end());
                    } else { reason = "Invalid server frame"; break; }
                    changed.notify_all();
                }
            }
            running = false; changed.notify_all();
        });
    }
    bool send(sb::Message type, const sb::Bytes& data) { return sb::write_all(pipe.get(),sb::frame(type,data),running); }
    bool wait(DWORD timeout, std::uint32_t request) {
        std::unique_lock lock(mutex);
        const auto deadline = std::chrono::steady_clock::now() + std::chrono::milliseconds(timeout);
        while (running && !(request ? acknowledged == request : granted) && !(rejection && rejected == request)) {
            const auto now = std::chrono::steady_clock::now();
            if (now >= deadline) break;
            changed.wait_until(lock, (std::min)(deadline,now+std::chrono::milliseconds(100)));
        }
        if (request ? acknowledged == request : granted) return true;
        if (rejection && rejected == request) std::cerr << "Rejected: " << reason << '\n';
        else std::cerr << "Disconnected, cancelled or timed out; transmission may be partial or already accepted. Inspect board output before retrying.\n";
        return false;
    }
    bool acquire(DWORD timeout) {
        if (!send(sb::Message::acquire,{})) return false;
        return wait(timeout,0);
    }
    bool transmit(sb::Bytes data, DWORD timeout, std::uint32_t request) {
        sb::Bytes packet; sb::append_u32(packet,request); packet.insert(packet.end(),data.begin(),data.end());
        if (!send(sb::Message::transmit,packet) || !wait(timeout,request)) return false;
        std::cerr << "[written] " << data.size() << " bytes accepted by Windows serial driver\n";
        return true;
    }
};
}
int wmain(int argc, wchar_t** argv) {
    if (argc == 2 && (std::wstring(argv[1]) == L"--help" || std::wstring(argv[1]) == L"-h")) { usage(); return 0; }
    try {
        const auto args = parse(argc,argv);
        // Validate send input before connecting or requesting a writer lease.
        const auto data = args.command == L"send" ? payload(args) : sb::Bytes{};
        if (args.command == L"interactive" && !_isatty(_fileno(stdin)))
            throw std::runtime_error("interactive requires a console; use send for automation");
        _setmode(_fileno(stdout),_O_BINARY);
        SetConsoleCtrlHandler(interrupt,TRUE);
        Client client; client.start(args);
        if (args.command == L"watch") {
            while (running) std::this_thread::sleep_for(std::chrono::milliseconds(100));
            return interrupted ? 0 : 5;
        }
        if (!client.acquire(args.timeout)) return 3;
        if (args.command == L"send") return client.transmit(data,args.timeout,1) ? 0 : 4;
        std::cerr << "Type a line; Enter sends UTF-8 followed by CR. Ctrl+C exits. Local input is echoed; board RX may also echo it.\n";
        std::wstring line;
        std::uint32_t request = 1;
        while (running) {
            if (!_kbhit()) { std::this_thread::sleep_for(std::chrono::milliseconds(25)); continue; }
            const auto c = _getwch();
            if (c == 3) break;
            if (c == 0 || c == 0xE0) { (void)_getwch(); continue; }
            if (c == L'\b') { if (!line.empty()) { line.pop_back(); std::cerr << "\b \b"; } continue; }
            if (c == L'\r') {
                std::cerr << '\n'; auto tx = sb::bytes(sb::utf8(line)); tx.push_back('\r'); line.clear();
                if (!client.transmit(std::move(tx),args.timeout,request++)) return 4;
            } else if (line.size() < 1000) {
                line.push_back(c); std::cerr << sb::utf8(std::wstring(1,c));
            }
        }
        return running || interrupted ? 0 : 5;
    } catch (const std::exception& e) { std::cerr << "Error: " << e.what() << '\n'; usage(); return 2; }
}
