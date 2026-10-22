#pragma once
#include "session.hpp"
#include <filesystem>
#include <fstream>

namespace sb {
enum class Policy { read_only, single_writer, shared };
struct Options {
    std::wstring port = L"COM3";
    DWORD baud = 115200;
    unsigned short tcp_port = 7000;
    bool tcp_enabled = true;
    bool dtr = false, rts = false;
    std::filesystem::path log_root;
};
struct ClientInfo { std::uint64_t id; std::string name; bool writer; };
struct Snapshot {
    bool running;
    Policy policy;
    std::uint64_t writer, rx, tx;
    std::vector<ClientInfo> clients;
};
class Broker {
public:
    using Notice = std::function<void(std::string)>;
    using Monitor = std::function<void(bool, Bytes)>;
    Broker(Notice notice, Monitor monitor);
    ~Broker();
    void start(const Options& options);
    void stop();
    void set_policy(Policy policy);
    void disconnect(std::uint64_t id);
    Snapshot snapshot() const;
    std::wstring pipe_path() const { return pipe_path_; }
private:
    struct Pending { std::uint64_t client, generation; std::uint32_t request; Bytes bytes; };
    void serial_receive();
    void serial_transmit();
    void pipe_accept();
    void tcp_accept();
    void attach(std::shared_ptr<Session> session);
    void submit(std::uint64_t id, std::uint32_t request, Bytes data);
    void control(std::uint64_t id, Message type);
    void closed(std::uint64_t id);
    void fail(const std::string& error);
    void event(const std::string& message);
    void record(bool tx, const Bytes& data, std::uint64_t client = 0);
    bool allowed(std::uint64_t id) const;
    void reply(std::uint64_t id, Message type, Bytes payload);
    void reject(std::uint64_t id, std::uint32_t request, const std::string& reason);
    std::shared_ptr<Session> find(std::uint64_t id) const;
    SessionHooks hooks();
    Handle make_pipe(bool first);
    void prepare_pipe_security();
    Notice notice_;
    Monitor monitor_;
    Options options_;
    std::atomic_bool running_{false};
    std::atomic_uint64_t next_id_{1}, rx_{0}, tx_{0};
    Handle serial_, first_pipe_;
    SOCKET listener_ = INVALID_SOCKET;
    bool winsock_ = false;
    std::wstring pipe_path_;
    std::wstring security_sddl_;
    std::thread rx_thread_, tx_thread_, pipe_thread_, tcp_thread_;
    mutable std::mutex clients_mutex_;
    std::vector<std::shared_ptr<Session>> clients_;
    mutable std::mutex gate_;
    Policy policy_ = Policy::shared;
    std::uint64_t writer_ = 0, generation_ = 0;
    std::deque<Pending> pending_;
    std::size_t pending_bytes_ = 0;
    std::condition_variable pending_ready_;
    std::mutex fanout_;
    Bytes history_;
    std::mutex log_mutex_;
    std::ofstream rx_log_, tx_log_, events_log_;
    bool logging_ = false;
    std::uint64_t rx_log_size_ = 0, tx_log_size_ = 0, event_log_size_ = 0;
};
} // namespace sb
