# SerialBroker 串口代理

[English](README.md) | **简体中文**

轻量级 Windows 串口代理，让终端和 AI Agent 通过 Telnet、命名管道共享同一个物理 COM 口。

保持 MobaXterm 会话打开，同时让 Agent 发送串口命令。SerialBroker 独占物理串口，把设备输出广播给多个客户端，并在监视窗口中显示 RX 和 TX。

## 功能

- **默认共享写入**：终端和 Agent 连接后即可发送，无需手动授权。
- **下拉框统一控制权限**：运行时可切换共享写入、单写入者和全部只读。
- **支持 MobaXterm**：改用本机 Telnet 会话，无需直接占用物理 COM。
- **Agent 命令行客户端**：`serialctl` 支持监听、发送 UTF-8 文本、十六进制字节和控制台交互。
- **实时监视与日志**：显示 RX/TX、在线会话，保存原始字节和带时间戳的事件。
- **有界缓冲**：慢客户端独立断开，新连接可回放最近最多 64 KiB 的 RX 历史。
- **Windows 原生实现**：C++20、Win32、重叠 I/O、Winsock2、CMake；无需虚拟串口驱动或第三方运行时。

当前桌面界面使用中文。中英文 README 描述的是同一个程序。

## 工作原理

SerialBroker 独占打开物理 COM 口。MobaXterm 连接本机 Telnet 端点，Agent 使用 `serialctl` 连接本机命名管道。每个客户端接收设备输出，所有发送数据块由唯一 TX 线程依次写入。

只接受 COM 口的软件暂时不能直接接入。本版 Telnet 是基础终端入口，不是 RFC2217 服务。

## 构建

需要 Windows 10/11 x64、Visual Studio 2022 的“使用 C++ 的桌面开发”组件、Windows SDK，以及 CMake 3.24 或更新版本。

```powershell
git clone https://github.com/tangzihengtzh/serial-broker.git
cd serial-broker
cmake --preset windows-x64
cmake --build --preset release
```

产物：

- `build/Release/SerialBroker.exe`：桌面代理。
- `build/Release/serialctl.exe`：命令行客户端。

程序使用静态 MSVC 运行库。无需安装器，通常不需要管理员权限；运行账号需要能访问串口设备，并能写入所选日志目录。

也可通过脚本构建并保存本机编译记录：

```powershell
.\scripts\build.ps1 -Rebuild
```

如果旧版 EXE 仍在运行，可以换一个输出目录：

```powershell
.\scripts\build.ps1 -BuildDirectory build-next
```

以上构建命令不会启动代理，也不会访问串口设备。

## 快速开始

1. 关闭直接占用该物理 COM 口的旧终端会话。
2. 打开 `SerialBroker.exe`，选择端口、波特率和可写的日志目录。串口格式固定为 **8N1、无流控**。DTR/RTS 默认关闭，可在启动前勾选；打开串口或改变这些信号可能使部分开发板复位。
3. 点击“启动代理”。每次启动都默认使用“共享写入”。
4. 在 MobaXterm 新建 **Telnet** 会话，地址 `127.0.0.1`，端口 `7000` 或代理中配置的值。使用字符模式，关闭本地行编辑，编码按设备设置。此入口不要选择 Serial、Raw TCP 或 SSH。
5. Agent 使用 `serialctl` 连接。物理端口为 `COM4` 时，管道名是 `SerialBroker.COM4`。

```powershell
.\build\Release\serialctl.exe watch --pipe SerialBroker.COM4
.\build\Release\serialctl.exe send --pipe SerialBroker.COM4 --text "help" --cr
.\build\Release\serialctl.exe send --pipe SerialBroker.COM4 --hex "03"
.\build\Release\serialctl.exe interactive --pipe SerialBroker.COM4
```

`--text` 使用 UTF-8；`--hex` 接受成对十六进制数字，可包含空白。`--cr` 追加 CR，`--lf` 追加 LF，两者同时指定为 CRLF。每次发送包含换行最多 4096 字节。`--timeout 10000` 将权限/确认等待设为 10000 毫秒。交互模式需要真实控制台；Enter 发送一行和 CR，Ctrl+C 退出。

### 写入策略

只通过“写入策略”下拉框控制权限，不设置单独的授予/撤销按钮。

| 策略 | 行为 |
|---|---|
| 共享写入——默认 | 所有在线客户端均可发送；数据块依次发送，但不同客户端的命令仍可能交错。 |
| 单写入者 | 首个发送有效数据或申请租约的客户端取得写入权；其释放、断开或策略变化后，其他客户端才可取得写入权。 |
| 全部只读 | 拒绝所有发送及写入权申请。 |

切换策略会使旧策略下尚未开始的排队请求失效，已经进入发送阶段的字节无法收回。需要更换单写入者时，可先切只读再切回单写入者，并让目标会话首先发送。

### 输出和发送确认

- CLI 的 stdout 是原始 RX 字节，stderr 是状态和错误。
- Telnet 和命名管道分发设备的 **RX**，不会伪造 TX 回显。若开发板不回显输入，请在代理 RX/TX 监视窗口查看 Agent 的发送内容。
- 新连接先收到最近 RX 历史，再收到实时输出；连接后的输出不一定属于本次新命令。
- `written` 仅代表 Windows 串口写操作完成，**不代表板端命令执行完毕**。
- 发送失败、超时或确认丢失时，部分或全部字节可能已经发出。程序不自动重试，应先观察设备输出。

## 日志

每次启动创建独立会话目录：

| 文件 | 内容 |
|---|---|
| `rx.bin` | 原始接收字节。 |
| `tx.bin` | 完整成功的发送块；部分发送失败的块不写入此文件。 |
| `events.log` | 时间戳、连接、策略变化、方向、字节数及二进制日志偏移。 |

RX/TX 日志各限制 128 MiB，事件日志限制 8 MiB。任一到限或写盘出错，将停止该会话的全部日志写入，界面提示 `LOGGING DISABLED`，串口转发继续。旧日志目录由用户管理。暂停显示或清空监视不影响转发和日志。

GUI 监视窗口按字节转义控制字符和非 ASCII 内容，适合检查数据；终端渲染由 MobaXterm 提供。

## 当前状态与已知限制

维护者已手动测试当前串口/Telnet 工作流程，并反馈功能正常。尚未提供自动化运行测试和压力测试。[验证说明](docs/VALIDATION.md) 区分了维护者反馈、编译检查和未覆盖场景。

- **窗口缩放残影**：调整窗口大小后，部分控件可能留下残影。维护者实测最小化后恢复窗口可以清除，暂缓修复。
- 串口格式固定为 8N1、无流控，不作为固件烧录或调制解调器控制兼容层。
- Telnet 仅绑定 `127.0.0.1`，没有身份认证，本机其他用户/进程也可能连接；不适合时可关闭 Telnet。命名管道仅允许当前 Windows 用户和 SYSTEM，拒绝远程管道客户端。
- 串口故障会停止代理，修复设备连接后手动启动；不自动重连或重发命令。
- 共享写入不提供完整 shell 命令事务、提示符匹配或板端去重。

## 详细文档

- [技术栈与架构设计](docs/DESIGN.md)
- [命名管道协议与 CLI 退出码](docs/PROTOCOL.md)
- [验证记录和后续检查](docs/VALIDATION.md)
- [原始需求](docs/原始需求.txt)

## 贡献者

- [Ziheng Tang](https://github.com/tangzihengtzh)：项目作者与维护者。
- [Codex](https://github.com/codex)：协助实现和编写文档，并在提交中以共同作者署名。

## 许可证

[MIT](LICENSE) © 2026 Ziheng Tang。
