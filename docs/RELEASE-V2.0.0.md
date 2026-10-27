# Serial Broker V2.0.0

## 简体中文

V2.0.0将串口代理升级为ModernWpf多设备工作区，同时保留原C++串口核心和serialctl协议。

- 多个COM标签独立连接、日志、客户端、写入策略和RX/TX计数；单设备故障不停止其它目标。
- 显示设备管理器友好名称，如USB-SERIAL CH340 (COM4)，长名称可完整查看；保留手动输入、离线历史和COM身份映射。
- 设备备注支持重名和表情，历史保存连接配置，新目标Telnet端口自动避让。
- 明亮、暗色、暖色、淡蓝四种主题，窗口尺寸/位置/最大化和外观持久化。
- 新图标、托盘驻留、关闭窗口继续连接，显式退出才释放设备；使用教程移至独立帮助文档。
- 保留SerialBroker.COMn管道、原CLI命令和写入确认语义，增加serialctl --version。

运行需要Windows10/11 x64及.NET8 Desktop Runtime（Windows x64）。下载SerialBroker-V2.0.0-win-x64.zip并完整解压，保留全部DLL、JSON和docs；附件SHA256SUMS.txt用于核对ZIP。请勿覆盖正在运行的旧版本。

验证：67项功能检查及3项正式包检查通过，原生和托管构建通过；自动测试不打开真实COM。当前本机设备名称已只读核对，CH340/CP210x显示及映射使用测试元数据验证；真实驱动、DTR/RTS、USB拔插和长期高吞吐仍取决于实际设备，详见VALIDATION.md。

main跟随最新版，V2.0.0固定本次正式版；旧版源码及历史归档为V1.0.0。旧版内部版本号仍保留0.1.0，未改写或重编译旧文件。

## English

V2.0.0 introduces a ModernWpf multi-serial workspace while keeping the existing C++ broker engine, SerialBroker.COMn pipes and serialctl protocol.

- Independent device tabs, state, clients, policies, counters and logs with fault isolation.
- Windows Device Manager friendly names, complete long-name display, editable COM input and offline history.
- Unicode/emoji remarks, remembered settings and collision-aware Telnet suggestions.
- Light/Dark/Warm/Blue themes and persistent window geometry.
- Original icon and tray lifecycle: Close hides the window; explicit Exit releases devices. Standalone offline help.

Requires Windows 10/11 x64 and .NET 8 Desktop Runtime x64. Extract the entire ZIP and keep its dependencies. SHA256SUMS.txt accompanies the archive.

67 functional and 3 production-package checks pass. Automated transport tests use isolated simulated devices rather than physical COM ports. See VALIDATION.md for hardware coverage limits.

main tracks the latest source; V2.0.0 fixes this release. V1.0.0 archives the previous source unchanged, including its original internal version metadata.
