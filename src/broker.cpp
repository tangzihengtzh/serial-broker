#include "broker.hpp"
#include <sddl.h>
#include <algorithm>
#include <array>
#include <chrono>

namespace sb {
Broker::Broker(Notice notice, Monitor monitor) : notice_(std::move(notice)), monitor_(std::move(monitor)) {
    WSADATA data{}; winsock_ = WSAStartup(MAKEWORD(2,2), &data) == 0;
}
Broker::~Broker() { stop(); if (winsock_) WSACleanup(); }
void Broker::prepare_pipe_security() {
    HANDLE raw{};
    if (!OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &raw)) throw std::runtime_error(win_error());
    Handle token(raw);
    DWORD size{}; GetTokenInformation(token.get(), TokenUser, nullptr, 0, &size);
    Bytes storage(size);
    if (!GetTokenInformation(token.get(), TokenUser, storage.data(), size, &size)) throw std::runtime_error(win_error());
    LPWSTR sid{};
    if (!ConvertSidToStringSidW(reinterpret_cast<TOKEN_USER*>(storage.data())->User.Sid, &sid))
        throw std::runtime_error(win_error());
    security_sddl_ = L"D:P(A;;GA;;;SY)(A;;GA;;;" + std::wstring(sid) + L")";
    LocalFree(sid);
}
Handle Broker::make_pipe(bool first) {
    PSECURITY_DESCRIPTOR descriptor{};
    if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(security_sddl_.c_str(), SDDL_REVISION_1, &descriptor, nullptr))
        throw std::runtime_error(win_error());
    SECURITY_ATTRIBUTES sa{sizeof(sa), descriptor, FALSE};
    Handle pipe(CreateNamedPipeW(pipe_path_.c_str(), PIPE_ACCESS_DUPLEX | FILE_FLAG_OVERLAPPED |
        (first ? FILE_FLAG_FIRST_PIPE_INSTANCE : 0), PIPE_TYPE_BYTE | PIPE_READMODE_BYTE |
        PIPE_WAIT | PIPE_REJECT_REMOTE_CLIENTS, 64, 65536, 65536, 0, &sa));
    const auto error = GetLastError(); LocalFree(descriptor);
    if (!pipe) throw std::runtime_error("CreateNamedPipe: " + win_error(error));
    return pipe;
}
void Broker::start(const Options& options) {
    stop();
    options_ = options;
    if (options.port.size() < 4 || options.port.substr(0,3) != L"COM" ||
        options.port.find_first_not_of(L"0123456789",3) != std::wstring::npos ||
        options.port[3] == L'0' || !options.baud || (options.tcp_enabled && !options.tcp_port))
        throw std::runtime_error("Invalid port, baud rate or TCP port");
    pipe_path_ = L"\\\\.\\pipe\\SerialBroker." + options.port;
    try {
        // Claim local endpoints before opening the physical device, so duplicate brokers fail early.
        prepare_pipe_security(); first_pipe_ = make_pipe(true);
        if (options.tcp_enabled) {
            if (!winsock_) throw std::runtime_error("Winsock initialization failed");
            listener_ = socket(AF_INET, SOCK_STREAM, IPPROTO_TCP);
            if (listener_ == INVALID_SOCKET) throw std::runtime_error("socket() failed");
            BOOL exclusive = TRUE;
            if (setsockopt(listener_, SOL_SOCKET, SO_EXCLUSIVEADDRUSE, reinterpret_cast<const char*>(&exclusive), sizeof(exclusive)))
                throw std::runtime_error("Cannot reserve TCP endpoint");
            sockaddr_in address{}; address.sin_family = AF_INET;
            address.sin_addr.s_addr = htonl(INADDR_LOOPBACK); address.sin_port = htons(options.tcp_port);
            if (bind(listener_, reinterpret_cast<sockaddr*>(&address), sizeof(address)) == SOCKET_ERROR ||
                listen(listener_, SOMAXCONN) == SOCKET_ERROR)
                throw std::runtime_error("Cannot bind 127.0.0.1:" + std::to_string(options.tcp_port) +
                                         "; WSA=" + std::to_string(WSAGetLastError()));
            u_long nonblock = 1;
            if (ioctlsocket(listener_, FIONBIO, &nonblock)) throw std::runtime_error("Cannot make TCP listener nonblocking");
        }
        serial_.reset(CreateFileW((L"\\\\.\\" + options.port).c_str(), GENERIC_READ | GENERIC_WRITE,
                                 0, nullptr, OPEN_EXISTING, FILE_FLAG_OVERLAPPED, nullptr));
        if (!serial_) throw std::runtime_error("Open " + utf8(options.port) + ": " + win_error());
        if (!SetupComm(serial_.get(), 65536, 65536)) throw std::runtime_error("SetupComm: " + win_error());
        DCB dcb{}; dcb.DCBlength = sizeof(dcb);
        if (!GetCommState(serial_.get(), &dcb)) throw std::runtime_error("GetCommState: " + win_error());
        dcb.BaudRate = options.baud; dcb.ByteSize = 8; dcb.Parity = NOPARITY; dcb.StopBits = ONESTOPBIT;
        dcb.fBinary = TRUE; dcb.fParity = FALSE; dcb.fOutxCtsFlow = FALSE; dcb.fOutxDsrFlow = FALSE;
        dcb.fDtrControl = options.dtr ? DTR_CONTROL_ENABLE : DTR_CONTROL_DISABLE;
        dcb.fRtsControl = options.rts ? RTS_CONTROL_ENABLE : RTS_CONTROL_DISABLE;
        dcb.fDsrSensitivity = FALSE; dcb.fTXContinueOnXoff = TRUE; dcb.fOutX = FALSE; dcb.fInX = FALSE;
        dcb.fErrorChar = FALSE; dcb.fNull = FALSE; dcb.fAbortOnError = FALSE;
        if (!SetCommState(serial_.get(), &dcb)) throw std::runtime_error("SetCommState: " + win_error());
        COMMTIMEOUTS timeouts{};
        timeouts.ReadIntervalTimeout = MAXDWORD;
        timeouts.ReadTotalTimeoutMultiplier = MAXDWORD; timeouts.ReadTotalTimeoutConstant = 100;
        timeouts.WriteTotalTimeoutConstant = 3000;
        timeouts.WriteTotalTimeoutMultiplier = (10000 + options.baud - 1) / options.baud;
        if (!SetCommTimeouts(serial_.get(), &timeouts)) throw std::runtime_error("SetCommTimeouts: " + win_error());
        const auto root = options.log_root.empty() ? std::filesystem::path(L"logs") : options.log_root;
        std::filesystem::create_directories(root);
        auto stamp = timestamp(); std::replace(stamp.begin(), stamp.end(), ':', '-');
        std::replace(stamp.begin(), stamp.end(), ' ', '_');
        auto folder = root / wide(stamp + "_" + utf8(options.port) + "_" + std::to_string(GetCurrentProcessId()));
        if (!std::filesystem::create_directory(folder)) throw std::runtime_error("Log session folder already exists");
        rx_log_.open(folder / L"rx.bin", std::ios::binary); tx_log_.open(folder / L"tx.bin", std::ios::binary);
        events_log_.open(folder / L"events.log", std::ios::binary);
        if (!rx_log_ || !tx_log_ || !events_log_) throw std::runtime_error("Cannot create session logs");
        logging_ = true; rx_log_size_ = tx_log_size_ = event_log_size_ = 0;
        rx_ = 0; tx_ = 0;
        { std::lock_guard lock(fanout_); history_.clear(); }
        { std::lock_guard lock(gate_); policy_ = Policy::shared; writer_ = 0; ++generation_; }
        running_ = true;
        event("START " + utf8(options.port) + " " + std::to_string(options.baud) + " 8N1; policy=shared");
        event("Logs: " + utf8(folder.wstring()));
        rx_thread_ = std::thread([this] { serial_receive(); });
        tx_thread_ = std::thread([this] { serial_transmit(); });
        pipe_thread_ = std::thread([this] { pipe_accept(); });
        if (options.tcp_enabled) tcp_thread_ = std::thread([this] { tcp_accept(); });
    } catch (...) { stop(); throw; }
}
void Broker::stop() {
    const bool was_running = running_.exchange(false);
    pending_ready_.notify_all();
    if (serial_) CancelIoEx(serial_.get(), nullptr);
    if (pipe_thread_.joinable()) pipe_thread_.join();
    if (tcp_thread_.joinable()) tcp_thread_.join();
    std::vector<std::shared_ptr<Session>> clients;
    { std::lock_guard lock(clients_mutex_); clients.swap(clients_); }
    for (auto& client : clients) client->stop();
    if (rx_thread_.joinable()) rx_thread_.join();
    if (tx_thread_.joinable()) tx_thread_.join();
    for (auto& client : clients) client->join();
    serial_.reset(); first_pipe_.reset();
    if (listener_ != INVALID_SOCKET) { closesocket(listener_); listener_ = INVALID_SOCKET; }
    { std::lock_guard lock(gate_); pending_.clear(); pending_bytes_ = 0; writer_ = 0; ++generation_; }
    if (was_running) event("STOP; pending TX cancelled; clients disconnected");
    { std::lock_guard lock(log_mutex_); logging_ = false; rx_log_.close(); tx_log_.close(); events_log_.close(); }
}
void Broker::fail(const std::string& error) {
    if (running_.exchange(false)) event("ERROR " + error + "; broker stopped; reconnect manually");
    pending_ready_.notify_all();
    if (serial_) CancelIoEx(serial_.get(), nullptr);
}
void Broker::event(const std::string& message) {
    const auto line = timestamp() + " " + message + "\r\n";
    bool failed = false;
    {
        std::lock_guard lock(log_mutex_);
        if (logging_) {
            if (event_log_size_ + line.size() > 8 * 1024 * 1024) { logging_ = false; failed = true; }
            else {
                events_log_.write(line.data(), static_cast<std::streamsize>(line.size())); events_log_.flush();
                event_log_size_ += line.size();
                if (!events_log_) { logging_ = false; failed = true; }
            }
        }
    }
    notice_(line);
    if (failed) notice_(timestamp() + " LOGGING DISABLED: event log limit or disk error\r\n");
}
void Broker::record(bool tx, const Bytes& data, std::uint64_t client) {
    bool failed = false;
    std::uint64_t offset{};
    {
        std::lock_guard lock(log_mutex_);
        auto& log = tx ? tx_log_ : rx_log_;
        auto& size = tx ? tx_log_size_ : rx_log_size_;
        offset = size;
        if (logging_) {
            if (size + data.size() > 128ULL * 1024 * 1024) { logging_ = false; failed = true; }
            else {
                log.write(reinterpret_cast<const char*>(data.data()), static_cast<std::streamsize>(data.size())); log.flush();
                size += data.size();
                if (!log) { logging_ = false; failed = true; }
            }
        }
    }
    if (failed) notice_(timestamp() + " LOGGING DISABLED: binary log limit or disk error\r\n");
    event(std::string(tx ? "TX" : "RX") + " client=" + std::to_string(client) + " bytes=" +
          std::to_string(data.size()) + " offset=" + std::to_string(offset));
    monitor_(tx, data);
}
void Broker::serial_receive() {
    std::array<std::uint8_t,4096> buffer{};
    while (running_) {
        DWORD count{};
        const auto result = read_some(serial_.get(), buffer, count, running_, 500);
        if (result == IoResult::timeout) continue;
        if (result != IoResult::ok) { if (running_) fail("Serial read failed"); break; }
        if (!count) continue;
        Bytes data(buffer.begin(), buffer.begin() + count); rx_ += count;
        record(false, data);
        std::lock_guard fanout(fanout_);
        history_.insert(history_.end(), data.begin(), data.end());
        if (history_.size() > 65536) history_.erase(history_.begin(), history_.end() - 65536);
        std::vector<std::shared_ptr<Session>> clients;
        { std::lock_guard lock(clients_mutex_); clients = clients_; }
        for (auto& session : clients) if (session->alive()) session->enqueue(Message::data, data);
    }
}
bool Broker::allowed(std::uint64_t id) const {
    return policy_ == Policy::shared || (policy_ == Policy::single_writer && writer_ == id);
}
std::shared_ptr<Session> Broker::find(std::uint64_t id) const {
    std::lock_guard lock(clients_mutex_);
    for (auto& c : clients_) if (c->id() == id && c->alive()) return c;
    return {};
}
void Broker::reply(std::uint64_t id, Message type, Bytes payload) {
    if (auto session = find(id)) session->enqueue(type, std::move(payload));
}
void Broker::reject(std::uint64_t id, std::uint32_t request, const std::string& reason) {
    Bytes data; append_u32(data, request); auto text = bytes(reason); data.insert(data.end(), text.begin(), text.end());
    reply(id, Message::rejected, std::move(data));
    event("REJECT client=" + std::to_string(id) + " request=" + std::to_string(request) + " " + reason);
}
void Broker::submit(std::uint64_t id, std::uint32_t request, Bytes data) {
    auto session = find(id);
    if (!session) return;
    std::string error;
    bool claimed = false;
    {
        std::lock_guard lock(gate_);
        if (!running_ || !session->alive()) error = "Broker is stopped or client disconnected";
        else if (!allowed(id) && !(policy_ == Policy::single_writer && !writer_))
            error = "No write permission under current policy; select Shared in the policy dropdown to allow all clients";
        else if (data.empty() || data.size() > max_write) error = "Invalid TX size";
        else if (pending_bytes_ + data.size() > 65536 || pending_.size() >= 256) error = "TX queue full";
        else {
            if (policy_ == Policy::single_writer && !writer_) { writer_ = id; ++generation_; claimed = true; }
            pending_bytes_ += data.size(); pending_.push_back({id,generation_,request,std::move(data)});
        }
    }
    if (!error.empty()) reject(id, request, error);
    else {
        if (claimed) event("ACQUIRE client=" + std::to_string(id) + "; first TX in single-writer mode");
        pending_ready_.notify_one();
    }
}
void Broker::serial_transmit() {
    while (running_) {
        Pending item;
        bool permitted;
        {
            std::unique_lock lock(gate_);
            pending_ready_.wait(lock, [this] { return !running_ || !pending_.empty(); });
            if (!running_) break;
            item = std::move(pending_.front()); pending_.pop_front(); pending_bytes_ -= item.bytes.size();
            permitted = item.generation == generation_ && allowed(item.client);
        }
        if (!permitted || !find(item.client)) { reject(item.client, item.request, "Permission changed or client disconnected"); continue; }
        // A policy change cannot retract a block already handed to the Windows driver.
        const auto wire_ms = (item.bytes.size() * 10000ULL + options_.baud - 1) / options_.baud;
        const auto write_deadline = static_cast<DWORD>(wire_ms + 3000);
        if (!write_all(serial_.get(), item.bytes, running_, write_deadline)) {
            reject(item.client, item.request, "Serial write failed; partial transmission is possible; do not retry automatically");
            if (running_) fail("Serial write failed or timed out; partial TX possible");
            break;
        }
        tx_ += item.bytes.size(); record(true, item.bytes, item.client);
        Bytes ack; append_u32(ack, item.request); append_u32(ack, static_cast<std::uint32_t>(item.bytes.size()));
        reply(item.client, Message::written, std::move(ack));
    }
}
void Broker::set_policy(Policy policy) {
    {
        std::lock_guard lock(gate_);
        if (policy_ == policy) return; // Duplicate UI notifications must not revoke an active lease.
        policy_ = policy; writer_ = 0; ++generation_;
    }
    event("POLICY " + std::to_string(static_cast<int>(policy)) + "; writer revoked; queued blocks invalidated");
}
void Broker::control(std::uint64_t id, Message type) {
    auto session = find(id);
    if (!session) return;
    bool accepted = false;
    {
        std::lock_guard lock(gate_);
        if (type == Message::acquire) {
            accepted = running_ && session->alive() && (policy_ == Policy::shared ||
                (policy_ == Policy::single_writer && (!writer_ || writer_ == id)));
            if (accepted && policy_ == Policy::single_writer && writer_ != id) { writer_ = id; ++generation_; }
        } else if (writer_ == id) { writer_ = 0; ++generation_; accepted = true; }
        else accepted = policy_ == Policy::shared;
    }
    if (accepted) {
        reply(id, type == Message::acquire ? Message::granted : Message::status,
              type == Message::acquire ? Bytes{} : bytes("Writer lease released"));
        event(std::string(type == Message::acquire ? "ACQUIRE" : "RELEASE") + " client=" + std::to_string(id));
    } else reject(id, 0, "Writer lease unavailable under current policy");
}
void Broker::closed(std::uint64_t id) {
    { std::lock_guard lock(gate_); if (writer_ == id) { writer_ = 0; ++generation_; } }
    event("DISCONNECT client=" + std::to_string(id));
}
void Broker::disconnect(std::uint64_t id) { if (auto session = find(id)) session->stop(); }
Snapshot Broker::snapshot() const {
    Snapshot out{}; out.running = running_; out.rx = rx_; out.tx = tx_;
    { std::lock_guard lock(gate_); out.policy = policy_; out.writer = writer_; }
    std::lock_guard lock(clients_mutex_);
    for (const auto& c : clients_) if (c->alive()) out.clients.push_back({c->id(), c->name(),
        out.policy == Policy::shared || (out.policy == Policy::single_writer && c->id() == out.writer)});
    return out;
}
SessionHooks Broker::hooks() {
    return {[this](auto id, auto request, auto data) { submit(id,request,std::move(data)); },
            [this](auto id, auto type) { control(id,type); }, [this](auto id) { closed(id); }};
}
void Broker::attach(std::shared_ptr<Session> session) {
    // Reap old sessions outside all broker locks: their stop callbacks may log and revoke a lease.
    std::vector<std::shared_ptr<Session>> dead;
    {
        std::lock_guard lock(clients_mutex_);
        auto it = clients_.begin();
        while (it != clients_.end()) {
            if (!(*it)->alive()) { dead.push_back(*it); it = clients_.erase(it); }
            else ++it;
        }
    }
    for (auto& client : dead) client->join();
    std::lock_guard fanout(fanout_);
    bool full;
    {
        std::lock_guard lock(clients_mutex_);
        full = clients_.size() >= 32 || !running_;
        if (!full) clients_.push_back(session);
    }
    if (full) { session->stop(); return; }
    if (!history_.empty()) session->enqueue(Message::data, history_);
    event("CONNECT client=" + std::to_string(session->id()) + " " + session->name());
    session->start();
}
void Broker::pipe_accept() {
    try {
        auto pipe = std::move(first_pipe_);
        while (running_) {
            Handle event_handle(CreateEventW(nullptr, TRUE, FALSE, nullptr));
            if (!event_handle) throw std::runtime_error("CreateEvent: " + win_error());
            OVERLAPPED op{}; op.hEvent = event_handle.get();
            bool connected = ConnectNamedPipe(pipe.get(), &op) != FALSE;
            if (!connected) {
                const auto error = GetLastError();
                if (error == ERROR_PIPE_CONNECTED) connected = true;
                else if (error == ERROR_IO_PENDING) {
                    while (running_ && WaitForSingleObject(op.hEvent, 100) == WAIT_TIMEOUT) {}
                    DWORD ignored{};
                    if (!running_) CancelIoEx(pipe.get(), &op);
                    connected = GetOverlappedResult(pipe.get(), &op, &ignored, TRUE) != FALSE;
                } else throw std::runtime_error("ConnectNamedPipe: " + win_error(error));
            }
            if (!running_) break;
            if (connected) attach(std::make_shared<Session>(next_id_++, std::move(pipe), hooks()));
            pipe = make_pipe(false);
        }
    } catch (const std::exception& e) { fail(std::string("Pipe listener: ") + e.what()); }
}
void Broker::tcp_accept() {
    try {
        while (running_) {
            WSAPOLLFD fd{listener_, POLLRDNORM, 0};
            const auto polled = WSAPoll(&fd,1,100);
            if (polled == SOCKET_ERROR || (fd.revents & (POLLERR | POLLHUP | POLLNVAL))) {
                if (running_) fail("TCP listener failed"); break;
            }
            if (!polled) continue;
            const auto socket = accept(listener_,nullptr,nullptr);
            if (socket == INVALID_SOCKET) continue;
            u_long nonblock = 1;
            if (ioctlsocket(socket, FIONBIO, &nonblock)) { closesocket(socket); continue; }
            BOOL no_delay = TRUE;
            setsockopt(socket, IPPROTO_TCP, TCP_NODELAY, reinterpret_cast<const char*>(&no_delay),sizeof(no_delay));
            attach(std::make_shared<Session>(next_id_++,socket,hooks()));
        }
    } catch (const std::exception& e) { fail(std::string("TCP listener: ") + e.what()); }
}
} // namespace sb
