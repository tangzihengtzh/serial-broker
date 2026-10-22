#pragma once
#include <array>
#include <cstdint>
#include <span>
#include <string>
#include <vector>

namespace sb {
using Bytes = std::vector<std::uint8_t>;
inline constexpr std::uint16_t magic = 0x4253;
inline constexpr std::size_t header_size = 8;
inline constexpr std::uint32_t max_payload = 65536;
inline constexpr std::uint32_t max_write = 4096;
enum class Message : std::uint16_t {
    hello = 1, data = 2, transmit = 3, acquire = 4, release = 5,
    status = 6, granted = 7, written = 8, rejected = 9
};
constexpr std::uint32_t u32(std::span<const std::uint8_t> data) {
    return static_cast<std::uint32_t>(data[0]) |
        (static_cast<std::uint32_t>(data[1]) << 8) |
        (static_cast<std::uint32_t>(data[2]) << 16) |
        (static_cast<std::uint32_t>(data[3]) << 24);
}
inline void append_u32(Bytes& out, std::uint32_t n) {
    for (unsigned i = 0; i < 4; ++i) out.push_back(static_cast<std::uint8_t>(n >> (8 * i)));
}
inline Bytes bytes(const std::string& text) { return Bytes(text.begin(), text.end()); }
inline Bytes frame(Message type, std::span<const std::uint8_t> payload) {
    const auto t = static_cast<std::uint16_t>(type);
    Bytes out{static_cast<std::uint8_t>(magic), static_cast<std::uint8_t>(magic >> 8),
              static_cast<std::uint8_t>(t), static_cast<std::uint8_t>(t >> 8)};
    append_u32(out, static_cast<std::uint32_t>(payload.size()));
    out.insert(out.end(), payload.begin(), payload.end());
    return out;
}
constexpr bool valid_header(std::span<const std::uint8_t> h) {
    return h.size() == header_size && h[0] == 0x53 && h[1] == 0x42 &&
        u32(h.subspan(4)) <= max_payload;
}
constexpr std::array<std::uint8_t, 8> sample_header{0x53, 0x42, 2, 0, 0, 0x10, 0, 0};
static_assert(valid_header(sample_header));
static_assert(u32(std::span(sample_header).subspan(4)) == 4096);
static_assert(!valid_header(std::array<std::uint8_t,8>{0,0,2,0,0,0,0,0}));
static_assert(!valid_header(std::array<std::uint8_t,8>{0x53,0x42,2,0,1,0,1,0}));
static_assert(valid_header(std::array<std::uint8_t,8>{0x53,0x42,2,0,0,0,1,0}));
static_assert(!valid_header(std::array<std::uint8_t,7>{0x53,0x42,2,0,0,0,0}));
static_assert(max_write + 4 <= max_payload);
} // namespace sb
