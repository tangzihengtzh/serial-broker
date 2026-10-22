#include "session.hpp"
#include <array>

namespace sb {
Session::Session(std::uint64_t id, Handle pipe, SessionHooks hooks)
    : id_(id), pipe_(std::move(pipe)), hooks_(std::move(hooks)), name_("Pipe #" + std::to_string(id)) {}
Session::Session(std::uint64_t id, SOCKET socket, SessionHooks hooks)
    : id_(id), socket_(socket), hooks_(std::move(hooks)), name_("Telnet #" + std::to_string(id)) {}
Session::~Session() {
    stop(); join();
    if (socket_ != INVALID_SOCKET) closesocket(socket_);
}
void Session::start() {
    sender_ = std::thread([this] { send(); });
    receiver_ = std::thread([this] { receive(); });
}
void Session::stop() {
    if (running_.exchange(false)) {
        if (pipe_) CancelIoEx(pipe_.get(), nullptr);
        if (telnet()) shutdown(socket_, SD_BOTH);
        ready_.notify_all();
        hooks_.closed(id_);
    }
}
void Session::join() {
    if (receiver_.joinable()) receiver_.join();
    if (sender_.joinable()) sender_.join();
}
std::string Session::name() const { std::lock_guard lock(mutex_); return name_; }
bool Session::enqueue(Message type, Bytes payload) {
    if (!running_) return false;
    Bytes packet;
    if (telnet()) {
        if (type != Message::data) return true;
        packet.reserve(payload.size());
        for (auto c : payload) { packet.push_back(c); if (c == 255) packet.push_back(c); }
    } else packet = frame(type, payload);
    bool overflow = false;
    {
        std::lock_guard lock(mutex_);
        overflow = queued_ + packet.size() > 256 * 1024 || outbound_.size() >= 1024;
        if (!overflow) { queued_ += packet.size(); outbound_.push_back(std::move(packet)); }
    }
    if (overflow) { stop(); return false; }
    ready_.notify_one();
    return true;
}
bool Session::socket_write(std::span<const std::uint8_t> data) {
    const auto started = GetTickCount64();
    std::size_t offset = 0;
    while (offset < data.size() && running_) {
        if (GetTickCount64() - started > 3000) return false;
        WSAPOLLFD fd{socket_, POLLWRNORM, 0};
        const auto result = WSAPoll(&fd, 1, 100);
        if (result == SOCKET_ERROR || (fd.revents & (POLLERR | POLLHUP | POLLNVAL))) return false;
        if (!result) continue;
        const int sent = ::send(socket_, reinterpret_cast<const char*>(data.data() + offset),
                                static_cast<int>(data.size() - offset), 0);
        if (sent == SOCKET_ERROR && WSAGetLastError() == WSAEWOULDBLOCK) continue;
        if (sent <= 0) return false;
        offset += static_cast<std::size_t>(sent);
    }
    return offset == data.size();
}
void Session::send() {
    for (;;) {
        Bytes packet;
        {
            std::unique_lock lock(mutex_);
            ready_.wait(lock, [this] { return !running_ || !outbound_.empty(); });
            if (!running_) break;
            packet = std::move(outbound_.front()); outbound_.pop_front(); queued_ -= packet.size();
        }
        const bool ok = telnet() ? socket_write(packet) : write_all(pipe_.get(), packet, running_);
        if (!ok) break;
    }
    stop();
}
void Session::receive() {
    if (telnet()) telnet_receive();
    else {
        Message type{}; Bytes payload;
        // The first frame must identify the protocol and client. No serial access occurs before a valid frame.
        if (read_frame(pipe_.get(), type, payload, running_) && type == Message::hello &&
            !payload.empty() && payload.size() <= 64) {
            {
                std::lock_guard lock(mutex_);
                name_ = "Pipe #" + std::to_string(id_) + " ";
                for (auto c : payload) name_ += (c >= 32 && c < 127) ? static_cast<char>(c) : '?';
            }
            enqueue(Message::status, bytes("Connected. RX history may precede this status. Default policy is shared; TX follows the current broker policy."));
            while (running_ && read_frame(pipe_.get(), type, payload, running_)) {
                if (type == Message::transmit && payload.size() > 4 && payload.size() <= max_write + 4) {
                    const auto request = u32(payload);
                    hooks_.transmit(id_, request, Bytes(payload.begin() + 4, payload.end()));
                } else if ((type == Message::acquire || type == Message::release) && payload.empty()) {
                    hooks_.control(id_, type);
                } else break;
            }
        }
    }
    stop();
}
void Session::telnet_receive() {
    // Negotiate character-at-a-time, remote echo and binary where the client supports it.
    {
        std::lock_guard lock(mutex_);
        Bytes negotiation{255,251,0,255,253,0,255,251,1,255,251,3,255,253,3};
        queued_ += negotiation.size(); outbound_.push_front(std::move(negotiation));
    }
    ready_.notify_one();
    enum class State { data, command, option, sub, sub_command } state = State::data;
    std::array<bool,256> local{}, remote{};
    local[0] = local[1] = local[3] = true; remote[0] = remote[3] = true;
    std::uint8_t command = 0;
    bool after_cr = false, binary_confirmed = false;
    std::array<std::uint8_t,4096> buffer{};
    while (running_) {
        WSAPOLLFD fd{socket_, POLLRDNORM, 0};
        const auto polled = WSAPoll(&fd, 1, 100);
        if (polled == SOCKET_ERROR || (fd.revents & (POLLERR | POLLHUP | POLLNVAL))) break;
        if (!polled) continue;
        const int count = recv(socket_, reinterpret_cast<char*>(buffer.data()), static_cast<int>(buffer.size()), 0);
        if (count == SOCKET_ERROR && WSAGetLastError() == WSAEWOULDBLOCK) continue;
        if (count <= 0) break;
        Bytes output, replies;
        for (int i = 0; i < count; ++i) {
            const auto c = buffer[static_cast<std::size_t>(i)];
            switch (state) {
            case State::data:
                if (c == 255) state = State::command;
                else {
                    if (!(after_cr && c == 0 && !binary_confirmed)) output.push_back(c);
                    after_cr = c == '\r';
                }
                break;
            case State::command:
                if (c == 255) { output.push_back(c); state = State::data; after_cr = false; }
                else if (c >= 251 && c <= 254) { command = c; state = State::option; }
                else if (c == 250) state = State::sub;
                else state = State::data;
                break;
            case State::option:
                if (command == 253) { // DO
                    if (c != 0 && c != 1 && c != 3) replies.insert(replies.end(), {255,252,c});
                    else if (!local[c]) { local[c] = true; replies.insert(replies.end(), {255,251,c}); }
                } else if (command == 254) {
                    if (local[c]) { local[c] = false; replies.insert(replies.end(), {255,252,c}); }
                } else if (command == 251) {
                    if (c == 0) binary_confirmed = true;
                    if (c != 0 && c != 3) replies.insert(replies.end(), {255,254,c});
                    else if (!remote[c]) { remote[c] = true; replies.insert(replies.end(), {255,253,c}); }
                } else if (command == 252) {
                    if (c == 0) binary_confirmed = false;
                    if (remote[c]) { remote[c] = false; replies.insert(replies.end(), {255,254,c}); }
                }
                state = State::data; break;
            case State::sub: if (c == 255) state = State::sub_command; break;
            case State::sub_command: state = c == 240 ? State::data : State::sub; break;
            }
        }
        if (!replies.empty()) {
            bool overflow;
            {
                std::lock_guard lock(mutex_);
                overflow = queued_ + replies.size() > 256 * 1024 || outbound_.size() >= 1024;
                if (!overflow) { queued_ += replies.size(); outbound_.push_back(std::move(replies)); }
            }
            if (overflow) break;
            ready_.notify_one();
        }
        if (!output.empty()) hooks_.transmit(id_, 0, std::move(output));
    }
}
} // namespace sb
