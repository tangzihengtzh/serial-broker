# SerialBroker

**English** | [简体中文](README.zh-CN.md)

A lightweight Windows serial port broker for sharing one physical COM port between terminals and AI agents via Telnet and named pipes.

Keep MobaXterm open while an agent sends serial commands. SerialBroker owns the physical port, broadcasts received bytes to connected clients, and shows both RX and TX in its monitor.

## Features

- **Shared writing by default:** terminals and agents can send without manual authorization.
- **One policy dropdown:** switch between shared writing, single writer, and read-only while running.
- **MobaXterm support:** connect through a local Telnet session instead of opening the physical COM port directly.
- **Agent CLI:** `serialctl` can watch output, send UTF-8 text or hexadecimal bytes, and accept interactive console input.
- **Live monitoring and logs:** inspect RX/TX, connected clients, raw binary captures, and timestamped events.
- **Bounded queues:** slow clients are disconnected independently; new clients receive up to 64 KiB of recent RX history.
- **Native Windows implementation:** C++20, Win32, overlapped I/O, Winsock2, and CMake. No virtual COM driver or third-party runtime is required.

The desktop UI currently uses Chinese labels. Both README languages describe the same application.

## How it works

SerialBroker opens the physical COM port exclusively. MobaXterm connects to its loopback Telnet endpoint; agents use a local named pipe through `serialctl`. Each client receives device output, and one TX worker serializes outgoing data blocks.

Programs that only accept COM ports cannot connect directly to this version. Telnet is a basic terminal endpoint, not RFC2217.

## Build

Requirements: Windows 10/11 x64, Visual Studio 2022 with Desktop development with C++, Windows SDK, and CMake 3.24 or newer.

```powershell
git clone https://github.com/tangzihengtzh/serial-broker.git
cd serial-broker
cmake --preset windows-x64
cmake --build --preset release
```

Outputs:

- `build/Release/SerialBroker.exe` — desktop broker.
- `build/Release/serialctl.exe` — command-line client.

The executables use the static MSVC runtime. No installer or administrator elevation is normally required; the account must have access to the serial device and write access to the selected log directory.

The build script also saves a local compilation record:

```powershell
.\scripts\build.ps1 -Rebuild
```

If an older executable is still running, use a separate output directory:

```powershell
.\scripts\build.ps1 -BuildDirectory build-next
```

The build commands do not launch the broker or access a serial device.

## Quick start

1. Close the terminal session that directly owns the physical COM port.
2. Open `SerialBroker.exe`, select the port and baud rate, and choose a writable log directory. The serial format is **8N1 with no flow control**. DTR/RTS are configurable and disabled by default; opening a port or changing these lines may reset some boards.
3. Click **启动代理** (Start broker). Every start defaults to **共享写入** (Shared writing).
4. In MobaXterm, create a **Telnet** session to `127.0.0.1`, port `7000` or the port selected in the broker. Use character mode without local line editing, and select the encoding used by the device. Do not use a Serial, Raw TCP, or SSH session for this endpoint.
5. Connect an agent using `serialctl`. For physical `COM4`, the pipe name is `SerialBroker.COM4`.

```powershell
.\build\Release\serialctl.exe watch --pipe SerialBroker.COM4
.\build\Release\serialctl.exe send --pipe SerialBroker.COM4 --text "help" --cr
.\build\Release\serialctl.exe send --pipe SerialBroker.COM4 --hex "03"
.\build\Release\serialctl.exe interactive --pipe SerialBroker.COM4
```

`--text` uses UTF-8. `--hex` accepts pairs of hex digits separated by optional whitespace. `--cr` appends CR, `--lf` appends LF, and both append CRLF. A send request accepts 1–4096 bytes including line endings. `--timeout 10000` changes the permission/acknowledgement wait in milliseconds. Interactive mode requires a console; Enter sends a line followed by CR, and Ctrl+C exits.

### Write policies

Select the policy using **写入策略** (Write policy). There are no separate grant/revoke buttons.

| Policy | Behavior |
|---|---|
| Shared writing — default | All connected clients may send. Blocks are serialized, but commands from different clients may interleave. |
| Single writer | The first client to send valid data or acquire a lease becomes the writer. Other writers are rejected until the lease is released, the client disconnects, or the policy changes. |
| Read-only | All writes and writer-lease requests are rejected. |

Changing policy invalidates queued requests admitted under the previous policy. Bytes already in flight cannot be recalled. To choose another single writer, switch to read-only, switch back to single writer, and let the intended client send first.

### Output and acknowledgements

- CLI stdout contains raw RX bytes; stderr contains status and errors.
- Telnet and named pipes carry device **RX**, not synthetic TX echoes. If the board does not echo input, inspect the broker's RX/TX monitor to see agent writes.
- New connections receive recent RX history before live output. Output after connecting is not automatically associated with a new command.
- A `written` acknowledgement means the Windows serial write completed. It does **not** mean the device finished executing a command.
- Failed or timed-out writes may already have sent some or all bytes. There is no automatic retry; inspect device output before sending again.

## Logs

Each broker start creates a session directory containing:

| File | Contents |
|---|---|
| `rx.bin` | Raw received bytes. |
| `tx.bin` | Fully successful outgoing blocks; a partially failed block is not included. |
| `events.log` | Timestamps, connections, policy changes, direction, byte counts, and binary-log offsets. |

RX and TX logs are each capped at 128 MiB; the event log is capped at 8 MiB. Reaching any limit or encountering a disk error disables logging for that session and displays `LOGGING DISABLED`; forwarding continues. Old session directories are managed by the user. Pausing or clearing the monitor does not stop forwarding or logging.

The GUI monitor escapes control characters and non-ASCII bytes. It is a byte inspector; use MobaXterm for terminal rendering.

## Status and known limitations

The maintainer has manually tested the current serial/Telnet workflow and reported normal operation. Automated runtime and stress tests are not yet provided. See [validation notes](docs/VALIDATION.md) for the distinction between maintainer feedback, compilation checks, and untested scenarios.

- **Resize artifacts:** controls may leave visual trails after resizing. Minimizing and restoring the window clears them in the maintainer's tests. This remains a known issue; the rendering fix is deferred.
- The serial format is fixed to 8N1 with no flow control. This is not a firmware-flashing or modem-control compatibility layer.
- Telnet binds only to `127.0.0.1` and has no authentication. Other local users/processes may connect; disable Telnet if that is unsuitable. Named pipes allow the current Windows user and SYSTEM and reject remote pipe clients.
- Serial errors stop the broker; reconnect manually after fixing the device connection. The broker does not automatically reconnect or resend commands.
- Shared writing does not provide shell-command transactions, prompt matching, or board-side deduplication.

## Documentation

The detailed engineering notes are currently in Chinese:

- [Design and architecture](docs/DESIGN.md)
- [Named-pipe protocol and CLI exit codes](docs/PROTOCOL.md)
- [Validation and follow-up checks](docs/VALIDATION.md)
- [Original requirements](docs/原始需求.txt)

## Contributors

- [Ziheng Tang](https://github.com/tangzihengtzh) — project author and maintainer.
- [Codex](https://github.com/codex) — AI-assisted implementation and documentation; credited as a commit co-author.

## License

[MIT](LICENSE) © 2026 Ziheng Tang.
