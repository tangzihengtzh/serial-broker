# Agent 命名管道协议 v1

实现位置：`include/serial_broker/protocol.hpp` 的 `Message`、`frame`、`valid_header`；`src/session.cpp` 的 `Session::receive`；`src/cli.cpp` 的 `Client`。

默认路径 `\\.\pipe\SerialBroker.COM3`。采用字节流，收包端必须拼出完整帧，不能假设一次 ReadFile 等于一帧。所有整数无符号、小端。协议当前没有压缩、版本协商、认证 token、校验和或业务事务。

## 帧头

| 偏移 | 长度 | 含义 |
|---|---|---|
| 0 | 2 | magic = 0x4253，线上字节 53 42 |
| 2 | 2 | 消息类型 |
| 4 | 4 | payload 字节数，0..65536 |
| 8 | length | payload |

头不合法、客户端首帧不是 hello、未知客户端消息或载荷结构不合法时断开。服务端在新连接的首个客户端 hello 到达前即可下发 RX 历史，所以客户端读取循环必须允许历史数据先于 status/granted。

## 消息表

| type | 名称 | 方向 | payload |
|---|---|---|---|
| 1 | hello | 客户端 → 代理 | 1..64 字节客户端名；显示时非可打印 ASCII 替换为 ? |
| 2 | data | 代理 → 客户端 | 原始 RX，包含连接时的历史；不包含 TX 和事件文本 |
| 3 | transmit | 客户端 → 代理 | uint32 request ID + 1..4096 字节原始 TX；单写入者模式下可自动取得空闲写入权 |
| 4 | acquire | 客户端 → 代理 | 空；在单写入者策略下申请空闲写入权，共享策略下确认可写 |
| 5 | release | 客户端 → 代理 | 空；释放自己的写入权并使旧租约队列失效 |
| 6 | status | 代理 → 客户端 | UTF-8 说明文本，如已连接或租约已释放 |
| 7 | granted | 代理 → 客户端 | 空；acquire 申请成功 |
| 8 | written | 代理 → 客户端 | uint32 request ID + uint32 实际完整写入块长度 |
| 9 | rejected | 代理 → 客户端 | uint32 request ID + UTF-8 原因；控制请求的 request ID 为 0 |

建议客户端使用非零、同连接内唯一 request ID；0 留给控制申请。服务端不去重 request ID，不会缓存并重放确认。断线自动释放单写入权，断开客户端尚未进入发送阶段的请求会被拒绝。write/read 执行失败后连接关闭，客户端不能靠重连推断是否曾经发送。

默认共享写入模式下 `acquire` 会 granted，所有在线客户端均可写。通过下拉框切为只读模式后 `acquire` 会 rejected。单写入者模式下已有其它持有者时 rejected；空闲时首个 acquire 或有效 transmit 取得写入权。自动取得写入权的 transmit 仍只以 written/rejected 确认该发送，不额外发送 granted。GUI 不再提供单独授予按钮。切换策略和租约变化不主动推送一份全局策略快照，客户端应以每次 transmit 的 written/rejected 为准；granted 不代表永久权限。

CLI 每次只保留一个等待确认的发送请求。stdout 提供 data 原始字节，stderr 提供 status、错误和 written 说明。默认等待 5000 ms，`--timeout` 可设 100..300000 ms；这个值用于权限/确认等待与管道连接等待，管道底层单次写入仍有约 3 秒截止时间。物理串口发送截止时间为该波特率下 8N1 理论线速时间 + 3 秒，CLI 确认超时需覆盖实际传输和排队时间。控制台 Ctrl+C 或退出会关闭连接，不保证撤销正在物理发送的请求。

## CLI 退出码

| 代码 | 含义 |
|---|---|
| 0 | 单次 TX 收到 written；或 watch/interactive 被用户正常中断 |
| 2 | 参数、连接或初始化错误 |
| 3 | 未取得写入权、写入权申请超时或被中断 |
| 4 | TX 被拒绝、发送失败、等待确认超时或被中断；可能已经部分或全部发送 |
| 5 | watch/interactive 遇到非用户触发的连接断开或输出失败 |

退出码 0 的 `send` 只确认驱动写入，不能当作板端命令返回值。纯 watch 遇到 Ctrl+C 返回 0。interactive 只接受控制台输入，Enter 发送 UTF-8 行加 CR，不是完整 VT 终端。
