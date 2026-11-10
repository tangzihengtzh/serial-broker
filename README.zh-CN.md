# Serial Broker V2.0.1

[English](README.md) | **简体中文**

一个工作区管理多个串口，让终端和自动化工具共享对应设备。新版以 ModernWpf 重做桌面界面，保留原 C++ 串口核心和 `serialctl` 协议。

**V2.0.1 正式版** · [下载运行包](https://github.com/tangzihengtzh/serial-broker/releases/tag/V2.0.1) · [旧版 V1.0.0](https://github.com/tangzihengtzh/serial-broker/tree/V1.0.0)

`main` 跟随最新版，`V2.0.1` 标签固定本次正式版；旧源码和历史完整保存在 `V1.0.0`，未修改旧版内部版本号。

## V2 新增

- **多串口标签页**：独立连接、监视、字节计数、客户端、写入策略及日志；单个设备故障不停止其它连接。
- **欢迎页与新建连接**：启动时不自动打开串口，不恢复上次的活动连接。
- **可识别的串口名称**：从Windows设备信息读取与设备管理器一致的友好名称，例如 `USB-SERIAL CH340 (COM4)`；输入框和下拉候选显示完整名称，长名称可换行查看，仍可手动输入COM号。
- **可编辑历史下拉框**：合并当前串口与历史，保存设备名称、波特率、设备备注、Telnet 端口和开关、DTR/RTS及日志根目录；离线历史保留名称并标明未连接。
- **设置 → 清除历史连接**：清除已保存的串口连接记录。
- **设置 → 开机自启动到托盘**：勾选启用开机自启动，取消勾选关闭。
- **端口建议**：新设备默认从7000起避开历史、其它标签草稿及系统监听端口；历史端口仅是默认值，可手动修改。
- **ModernWpf**：统一圆角、间距和对齐，提供明亮、暗色、暖色、淡蓝四种全局主题，监视区同步更新。
- **布局记忆**：保存窗口大小、位置、最大化状态和主题；窗口移出可见桌面时恢复到可见范围。
- **图标与托盘**：关闭窗口或 Alt+F4 隐藏到托盘，所有连接保持；“退出程序”才释放连接。
- **独立帮助**：界面只保留操作和状态；Telnet、CLI、管道等教程通过“帮助”打开本地文档。

## 下载与运行

下载 [SerialBroker-V2.0.1-win-x64.zip](https://github.com/tangzihengtzh/serial-broker/releases/download/V2.0.1/SerialBroker-V2.0.1-win-x64.zip) 及同页的 `SHA256SUMS.txt`。解压到新目录，不覆盖正在使用的旧版本。

运行环境：Windows 10/11 x64，**.NET 8 Desktop Runtime（Windows x64）**。如果尚未安装，请从 [.NET 8 官方下载页](https://dotnet.microsoft.com/download/dotnet/8.0) 选择 Desktop Runtime。

运行 `SerialBroker.exe`，保留同目录的 DLL、JSON 和 docs。新版GUI需要.NET桌面运行时；`serialctl.exe`和原生DLL仍使用静态MSVC运行库。

1. 新建连接，选择或输入物理串口，按设备设置波特率。
2. 填写可重名的设备备注，可包含表情。
3. 确认Telnet端口和日志目录，按需启用DTR/RTS，点击“连接设备”。
4. 再新建标签连接其它串口。切换标签不会停止已连接设备。
5. 通过顶部或托盘菜单“退出程序”结束全部连接。关闭单个标签只释放该设备。

串口格式为8N1，默认共享写入，DTR/RTS默认关闭。

## 终端与自动化

Telnet只监听 `127.0.0.1`，使用对应标签配置的端口。终端软件不要再直接占用物理COM。

每个COM使用独立且兼容旧版的管道名。例如：

```powershell
.\serialctl.exe watch --pipe SerialBroker.COM4
.\serialctl.exe send --pipe SerialBroker.COM4 --text "help" --cr
.\serialctl.exe send --pipe SerialBroker.COM7 --hex "03"
.\serialctl.exe interactive --pipe SerialBroker.COM7
.\serialctl.exe --version
```

CLI目标由 `--pipe` 决定，与GUI选中的标签无关。默认参数仍指向 `SerialBroker.COM3`，多串口使用时应显式指定。

`--text`使用UTF-8；`--hex`为原始字节；`--cr`/`--lf`追加换行。stdout为原始RX，stderr为状态和错误。

完整步骤、写入策略和故障说明见 [使用指南](docs/V2-USER-GUIDE.md) 与 [管道协议](docs/PROTOCOL.md)。

## 设置和日志

偏好默认存放在 `%LOCALAPPDATA%\SerialBroker\workspace\preferences.json`，窗口位置另存为同目录 `window.json`。可用 `SerialBroker.exe --data-directory PATH` 指定独立偏好目录。启动不会自动连接；历史按COM号去重，备注不是唯一标识。

每次连接按时间、COM和进程ID建立独立日志目录，保存 `rx.bin`、`tx.bin` 和 `events.log`。暂停或清空监视时，转发和日志持续运行。

## 构建与功能验证

开发环境：Visual Studio 2022 C++桌面组件、Windows SDK、CMake 3.24+及.NET SDK 8.0.400或兼容更新版本。

```powershell
.\scripts\build.ps1
.\scripts\test.ps1 -SkipBuild
.\scripts\package.ps1
```

已有程序占用默认打包目录时，可用 `scripts/package.ps1 -OutputDirectory artifacts/V2.0.1-device-names` 输出到新目录，不需结束正在使用的连接。

测试覆盖原生代理、设备转发及WPF界面控件。

已通过 **117项模型/转发/界面功能检查及6项正式包检查**；原生 `/W4 /WX` 和托管警告视为错误的构建通过。完整结果见[验证记录](docs/VALIDATION.md)。

## 文档与贡献

[使用指南](docs/V2-USER-GUIDE.md) · [架构](docs/DESIGN.md) · [CHANGELOG](docs/CHANGELOG.md) · [验证](docs/VALIDATION.md) · [原始需求](docs/原始需求.txt)

- [Ziheng Tang](https://github.com/tangzihengtzh)：作者与维护者。
- [Codex](https://github.com/codex)：协助设计、实现、测试与文档，并在提交中保留共同作者署名。

[MIT](LICENSE) © 2026 Ziheng Tang。ModernWpf相关声明见 [第三方许可证](THIRD-PARTY-NOTICES.md)。
