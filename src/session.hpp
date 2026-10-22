#pragma once
#include "win_io.hpp"
#include <condition_variable>
#include <deque>
#include <functional>
#include <memory>
#include <mutex>
#include <thread>

namespace sb {
struct SessionHooks {
    std::function<void(std::uint64_t, std::uint32_t, Bytes)> transmit;
    std::function<void(std::uint64_t, Message)> control;
    std::function<void(std::uint64_t)> closed;
};
class Session {
public:
    Session(std::uint64_t id, Handle pipe, SessionHooks hooks);
    Session(std::uint64_t id, SOCKET socket, SessionHooks hooks);
    ~Session();
    void start();
    void stop();
    void join();
    bool enqueue(Message type, Bytes payload);
    bool alive() const { return running_; }
    std::uint64_t id() const { return id_; }
    bool telnet() const { return socket_ != INVALID_SOCKET; }
    std::string name() const;
private:
    void receive();
    void send();
    void telnet_receive();
    bool socket_write(std::span<const std::uint8_t> data);
    std::uint64_t id_;
    Handle pipe_;
    SOCKET socket_ = INVALID_SOCKET;
    SessionHooks hooks_;
    std::atomic_bool running_{true};
    std::thread receiver_, sender_;
    mutable std::mutex mutex_;
    std::condition_variable ready_;
    std::deque<Bytes> outbound_;
    std::size_t queued_ = 0;
    std::string name_;
};
} // namespace sb
