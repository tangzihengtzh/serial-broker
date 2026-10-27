# Serial Broker V2.0.0

**English** | [简体中文](README.zh-CN.md)

A multi-device Windows workspace that lets terminals and automation share physical serial ports. V2 introduces a ModernWpf desktop UI while retaining the C++ broker engine and the existing `serialctl` wire protocol.

**V2.0.0** · [Download](https://github.com/tangzihengtzh/serial-broker/releases/tag/V2.0.0) · [Previous version: V1.0.0](https://github.com/tangzihengtzh/serial-broker/tree/V1.0.0)

The `main` branch tracks the latest version. `V2.0.0` fixes this release, while `V1.0.0` preserves the original source and history without changing its internal version metadata.

## What's new

- Independent serial-device tabs, each with its own connection, monitor, clients, write policy, counters and logs. A failed device does not stop its neighbors.
- Welcome/new-connection page; no automatic connection or restoration of active sessions at startup.
- Editable COM/history selector displays Windows Device Manager friendly names, such as `USB-SERIAL CH340 (COM4)`, in the selection and dropdown. Long names have a complete wrapping display. Manual COM input remains supported.
- Remembered device names, baud rate, Unicode/emoji remarks, Telnet settings, DTR/RTS and log directory. Offline history retains the friendly name with an offline label. Remarks may be duplicated.
- Suggested Telnet ports avoid historical ports, current drafts and local listeners. Historical values are editable defaults, not reservations.
- ModernWpf rounded controls and consistent spacing; Light, Dark, Warm and Blue global themes also style the monitor.
- Saved window size, position, maximized state and appearance.
- An original connector icon and system tray lifecycle. Close/Alt+F4 hides the window without stopping devices; explicit Exit releases all connections.
- Standalone local help rather than terminal/CLI instructions embedded in the workspace.

## Download and run

Download [SerialBroker-V2.0.0-win-x64.zip](https://github.com/tangzihengtzh/serial-broker/releases/download/V2.0.0/SerialBroker-V2.0.0-win-x64.zip) and the accompanying `SHA256SUMS.txt` from the release. Extract to a new directory instead of replacing a running installation.

Requires Windows 10/11 x64 and the **[.NET 8 Desktop Runtime, Windows x64](https://dotnet.microsoft.com/download/dotnet/8.0)**. Start `SerialBroker.exe` and keep all adjacent DLL, JSON and documentation files. Native components and `serialctl` use the static MSVC runtime; the new GUI needs the .NET desktop runtime.

1. Create a tab, select or type a COM port, and configure its baud rate and optional remark.
2. Check the Telnet endpoint, log directory and DTR/RTS settings, then connect.
3. Create another tab for another device. Switching tabs leaves all connected devices running.
4. Close an individual tab to release that device. Use Exit in the window or tray menu to stop the entire workspace.

The serial format remains 8N1 with no flow control. DTR/RTS default to off; opening a device or changing these signals may reset some boards. Check your hardware before acceptance testing.

## Terminals and automation

Each device exposes its configured Telnet endpoint on `127.0.0.1` and the compatible `SerialBroker.COMn` named pipe. Stop terminals that directly own the physical COM port before connecting the broker.

```powershell
.\serialctl.exe watch --pipe SerialBroker.COM4
.\serialctl.exe send --pipe SerialBroker.COM4 --text "help" --cr
.\serialctl.exe send --pipe SerialBroker.COM7 --hex "03"
.\serialctl.exe interactive --pipe SerialBroker.COM7
.\serialctl.exe --version
```

The pipe explicitly selects a target; switching GUI tabs never changes a CLI target. The retained CLI default is `SerialBroker.COM3`, so pass `--pipe` when using multiple devices.

Text is UTF-8; hex sends raw bytes; `--cr` and `--lf` append line endings. A write accepts at most 4096 bytes. stdout contains raw device RX; stderr contains status/errors. A `written` acknowledgment means the Windows write completed, not that the device executed the command. Failed or ambiguous writes are not automatically retried.

See the [user guide](docs/V2-USER-GUIDE.md) for terminal setup, write policies and troubleshooting, and the [protocol](docs/PROTOCOL.md) for frame and exit-code details.

## Preferences and logging

Preferences live in `%LOCALAPPDATA%\SerialBroker\workspace\preferences.json`; geometry lives in `window.json`. `SerialBroker.exe --data-directory PATH` selects a separate preference directory. History is keyed by normalized COM number and contains no active sessions or monitor output.

Each connection creates its own timestamp/COM/process log directory with `rx.bin`, `tx.bin` and `events.log`. Binary logs are capped at 128 MiB each and events at 8 MiB. Reaching a limit or encountering a disk error disables logging but leaves forwarding running. Pausing/clearing the monitor does not pause forwarding or logging.

## Build and validate

Development requires Visual Studio 2022 C++ desktop tools, Windows SDK, CMake 3.24+ and .NET SDK 8.0.400 or a compatible newer SDK.

```powershell
.\scripts\build.ps1
.\scripts\test.ps1 -SkipBuild
.\scripts\package.ps1
```

If the default package directory is in use, pass `scripts/package.ps1 -OutputDirectory artifacts/V2.0.0-device-names` to create a new acceptance directory without stopping existing connections.

The tests use a separate `build-test` native library and isolated named-pipe device fixtures. They do not open physical serial ports or install drivers. WPF controls are rendered in non-activating off-screen windows. The shipped native DLL rejects the simulated transport.

**67 functional checks and 3 production-package checks pass.** Native `/W4 /WX` and managed warnings-as-errors builds pass. See [validation](docs/VALIDATION.md) for the boundary between simulated transfer, read-only device metadata checks and pending real-device transfer acceptance.

## Scope

Windows x64 COM devices, 8N1, no flow control. This is not RFC2217 or a firmware flashing compatibility layer. A physical COM port can have only one broker owner. Telnet is a local unauthenticated endpoint; named pipes retain the current-user/SYSTEM access policy. Device failures require manual reconnection; commands are never replayed automatically. USB hot-plug, board-specific DTR/RTS effects, driver variations and sustained high-throughput operation still require hardware testing.

## Documentation and contributors

[User guide](docs/V2-USER-GUIDE.md) · [Architecture](docs/DESIGN.md) · [Changelog](docs/CHANGELOG.md) · [Validation](docs/VALIDATION.md)

- [Ziheng Tang](https://github.com/tangzihengtzh): author and maintainer.
- [Codex](https://github.com/codex): assisted implementation, testing and documentation, with commit co-author attribution.

[MIT](LICENSE) ©2026 Ziheng Tang. See [third-party notices](THIRD-PARTY-NOTICES.md) for ModernWpf licensing.
