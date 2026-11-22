<div align="center">

# 🛰️ Farview

**A btop-style terminal dashboard for a remote Linux server — over plain SSH.**

[![NativeAOT](https://img.shields.io/badge/NativeAOT-~2_MB_single_binary-blueviolet)](#)
[![.NET 10](https://img.shields.io/badge/.NET-10.0-512BD4)](#)
[![UI](https://img.shields.io/badge/TUI-Cellray-ff69b4)](#)
[![Server install](https://img.shields.io/badge/server_install-nothing-brightgreen)](#)

CPU · Memory · Swap · Disks · Power — live graphs in your terminal,
with **zero setup on the server** and no .NET runtime on the client.

</div>

---

## ✨ What it is

Farview connects to any Linux box you can already `ssh` into, streams a tiny
read-only shell script over the connection, and renders a live dashboard in your
terminal. One SSH connection, one frame per second, one ~2 MB native executable.

- 🖥️ **CPU** — usage % with a rolling history graph
- 🧠 **Memory & swap** — meters + history
- 💾 **Disks** — per-device usage bars (loops, snaps and fuse filtered out)
- ⚡ **Power** — RAPL energy counters, battery and hwmon sensors, with wrap handling
- 🔁 **Auto-reconnect** — dropped link? The dashboard stays up and retries every 5 s
- 🎭 **`--demo` mode** — fake data, no server needed, just to admire the UI

Built on **[Cellray](../../CoreCLR)**, a terminal UI renderer — widgets like
`Panel`, `Graph`, `Meter` and `Label` do all the drawing.

## 🚀 Quick start

```powershell
# build (needs the .NET SDK; on Windows also VS Build Tools with the C++ workload)
dotnet publish src/Farview -c Release -r win-x64

# binary lands in src/Farview/bin/Release/net10.0/win-x64/publish/
farview                      # connect screen, last server prefilled
farview root@192.168.1.10    # connect straight away ([user@]host[:port])
farview --demo               # made-up data, no server needed
```

### Requirements

| Side | Needs |
|---|---|
| Client | An installed `ssh` client (OpenSSH ships with Windows 10+, Linux, macOS) |
| Auth | Key or agent login (no password prompts — `BatchMode` by design) |
| Server | Linux, with the host key already trusted (`ssh user@host` once) |

## ⌨️ Keys

| Key | Action |
|---|---|
| `Enter` | Connect |
| `Tab` / `Shift+Tab` | Move focus |
| `Esc` | Back to the server prompt |
| `q` | Quit from the dashboard |
| `R` | Retry after a failed connection |
| `Ctrl+C` | Quit anywhere |

## 🧠 How it works

```
App ──▶ SshSession:  ssh -T -o BatchMode=yes host sh -s
          │            stdin  ← RemoteScript (a read-only POSIX loop)
          │            stdout → one text block per second
          ▼
   Telemetry.Feed(line)   parse, delta-compute, keep 512-sample history
          ▼
   Cellray widgets        Panel / Graph / Meter / Label @ 30 fps
```

One connection stays open for the whole session — no repeated handshakes. The UI
thread never blocks: reader threads queue lines, and `Pump()` feeds them to
`Telemetry` once per frame.

<details>
<summary><b>📡 The wire protocol</b> (one block per second, <code>U</code> … <code>END</code>)</summary>

| Line | Meaning |
|---|---|
| `HOST`, `KERNEL`, `CORES`, `MODEL` | Static info, sent once |
| `RAPLMAX <zone> <max_uj> <name>` | RAPL wrap value + zone name, once |
| `U <uptime>` | Block start; doubles as the clock for power rates |
| `C cpu ...` | First line of `/proc/stat` |
| `M <key> <kB>` | `MemTotal`, `MemAvailable`, `MemFree`, `SwapTotal`, `SwapFree` |
| `D <df -P -k line>` | One per filesystem |
| `P <zone> <energy_uj>` | RAPL energy counters |
| `W <name> <microwatts>` | Battery / hwmon instantaneous power |

</details>

## 🗂️ Source map

| File | Role |
|---|---|
| `Program.cs` | Args (`--demo`, `--frames`, `--help`, target), terminal init |
| `App.cs` | All UI: connect / connecting / failed / dashboard screens |
| `Sessions.cs` | `ISession`, `SshSession` (system `ssh`), `DemoSession` |
| `RemoteScript.cs` | The read-only POSIX `sh` script streamed to the server |
| `Telemetry.cs` | Protocol parsing, CPU/power deltas, history buffers |
| `SshTarget.cs` | Strict `[user@]host[:port]` parsing (input can never become an ssh flag) |
| `HostStore.cs` | Last + saved servers in your user config folder |
| `Fmt.cs` | KiB→`3.25G`, uptime, ellipsis |

## 🛠️ Build notes

- **Cellray** is referenced by path (`..\..\..\..\CoreCLR\src\Cellray\Cellray.csproj`),
  so it must sit as a sibling repo until it's published as a package.
- NativeAOT on Windows needs the VS Build Tools C++ workload, and `vswhere.exe` on `PATH`.
- If copying `farview.exe` fails, a running copy has it locked — close it and retry.

## 📈 Status

- ✅ Tested: Windows client against a Proxmox/Debian server (Linux 6.8 PVE, Xeon E5) — live CPU, memory, disks and RAPL power
- 🧪 Not yet: Linux/macOS clients, real network drops, non-root RAPL, battery/hwmon servers
- 🚫 By design: no password auth, no non-Linux servers, no per-process lists

> See [OVERVIEW.md](OVERVIEW.md) for the deep dive: math behind the numbers,
> UI states, and where to extend.

<div align="center">
<sub>farview · see your server from afar 🛰️</sub>
</div>
