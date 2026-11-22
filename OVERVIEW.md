# Farview - codebase overview

Farview is a btop-style terminal dashboard that monitors a **remote Linux server over SSH**: CPU, memory/swap,
per-disk usage and power draw. It is a single NativeAOT executable (about 2 MB) with no .NET runtime needed on the
machine you run it on, and nothing to install on the server.

It is built on the [Cellray](../../CoreCLR) terminal UI library (a sibling repo, see "Dependencies").

## Quick start

```powershell
# build (Windows; needs the .NET SDK and VS Build Tools with the C++ workload)
dotnet publish src/Farview -c Release -r win-x64

# run it: output is src/Farview/bin/Release/net10.0/win-x64/publish/farview.exe
farview                      # connect screen, last server prefilled
farview root@192.168.1.10    # connect straight away ([user@]host[:port])
farview --demo               # made-up data, no server needed
farview --help
```

Keys: **Enter** connect, **Tab / Shift+Tab** move focus, **Esc** back to the server prompt (quit from the prompt),
**q** quit from the dashboard, **R** retry after a failed connection, **Ctrl+C** quit anywhere.

Requirements: an installed `ssh` client (OpenSSH ships with Windows 10+, Linux and macOS), **key or agent login**
to the server, and the server's host key already trusted (`ssh user@host` once). A Linux server (reads `/proc`/`/sys`).

## How it works

```
 Program.cs ──▶ App (screens, input, layout)
                 │
   connect ──────┤  SshTarget.TryParse ─▶ validated "[user@]host[:port]"
   screen        │  HostStore            ─▶ last + saved servers (hosts.txt)
                 ▼
            ISession ◀── SshSession  runs:  ssh -T -o BatchMode=yes ... host sh -s
                 │                          stdin  <- RemoteScript.Text (loops forever on the server)
                 │                          stdout -> one text block per second
                 │       DemoSession  (--demo) generates the same text format locally
                 ▼
            Telemetry.Feed(line)  parses lines, keeps latest values + rolling History
                 ▼
            App.DrawDashboard     Cellray widgets: Panel / Graph / Meter / Label
```

One ssh connection stays open for the whole session. The remote script runs a `while` loop and prints a block every
second, so there is no repeated handshake. The UI thread never blocks: reader threads queue lines, and `Pump()` feeds
them to `Telemetry` once per frame.

## Source map (`src/Farview`)

| File | Role |
|---|---|
| `Program.cs` | Argument parsing (`--demo`, `--frames N` for smoke tests, `--help`, optional target), terminal init, runs `App`. Disables Cellray's built-in exit key, since Esc means "back" here. |
| `App.cs` | All UI. `DrawConnect` (text box, Connect, saved-servers dropdown, Forget), `DrawConnecting`, `DrawFailed`, `DrawDashboard` and its panels (`DrawCpu`, `DrawMemory`, `DrawDisks`, `DrawPower`). Owns screen state, reconnect timing and "remember host after first success". |
| `Sessions.cs` | `ISession` plus two implementations. `SshSession` spawns the system `ssh`, sends the script, reads stdout/stderr on background threads, and turns stderr into a message with a hint (key login, untrusted host, DNS, timeout). `DemoSession` feeds synthetic data in the same format. |
| `RemoteScript.cs` | The POSIX `sh` script streamed to the server. Read-only: it reads `/proc`, `/sys` and runs `df`, `hostname`, `uname`, `sleep`. |
| `Telemetry.cs` | Parses the protocol, computes CPU % and power from counter deltas, filters disks, and holds the three `History` buffers (CPU, memory, power; 512 samples each). |
| `SshTarget.cs` | Validates and parses `[user@]host[:port]`. Rejects a leading `-` and unexpected characters, so input can never be read as an ssh option. |
| `HostStore.cs` | Plain-text store at `%APPDATA%\Farview\hosts.txt` (`~/.config/Farview` on Unix): a `last=` line and up to 20 `host=` lines. |
| `Fmt.cs` | Small formatters: KiB to `3.25G`, uptime, truncate with an ellipsis. |

## The remote protocol

The script prints header lines once, then one block per second (`U` starts a block, `END` ends it):

| Line | Meaning |
|---|---|
| `HOST`, `KERNEL`, `CORES`, `MODEL` | Static info, once at start |
| `RAPLMAX <zone> <max_uj> <name>` | RAPL counter wrap value and zone name, once |
| `U <uptime>` | Block start; the server's uptime is also the clock for power rates |
| `C cpu ...` | First line of `/proc/stat` |
| `M <key> <kB>` | `MemTotal`, `MemAvailable`, `MemFree`, `SwapTotal`, `SwapFree` |
| `D <df -P -k line>` | One per filesystem (the header row is skipped by `sed`) |
| `P <zone> <energy_uj>` | RAPL energy counters (`/sys/class/powercap/intel-rapl:N`) |
| `W <name> <microwatts>` | Instantaneous power: battery `power_now`, hwmon `power1_*` |

## How the numbers are computed (`Telemetry.cs`)

- **CPU %** = `100 * (1 - idle delta / total delta)` between two `/proc/stat` samples (idle = idle + iowait). The first sample only sets the baseline.
- **Memory %** = `(MemTotal - MemAvailable) / MemTotal`, falling back to `MemFree` on kernels without `MemAvailable`. Swap likewise.
- **Disks**: only `/dev/*` devices, excluding `loop`, `ram`, `fuse` and `/snap` mounts, one entry per device. Percent matches `df`'s Use% (used / (used + available)).
- **Power**: RAPL energy delta / uptime delta, handling counter wrap. Total is the `psys` zone if present, otherwise the sum of `package-*` zones. Battery/hwmon readings are listed and used as the total if RAPL is unavailable. Needs two samples, so the panel says "measuring..." at first.
- A reconnect calls `ResetBaseline()` so rates are never computed across the gap. History is kept.

## UI states

`Connect` screen, then `Monitor`, which shows one of: connecting spinner, failed panel (with the ssh error and a hint),
or the dashboard. After a dropped connection the dashboard stays up, shows `lost - retrying in Ns`, and reconnects every 5 s.
Minimum sizes: 44x19 for the connect screen, 60x20 for the dashboard. The dashboard layout (CPU on top, memory, then
disks | power) adapts to the terminal size. `AppDomain.ProcessExit` and a `finally` block restore the terminal.

## Dependencies and build notes

- **Cellray** is referenced by project path: `..\..\..\..\CoreCLR\src\Cellray\Cellray.csproj` (a sibling folder). It will not build on another machine until Cellray is published, either as a NuGet package or a git submodule.
- NativeAOT on Windows needs the VS Build Tools C++ workload, and `vswhere.exe` (in `C:\Program Files (x86)\Microsoft Visual Studio\Installer`) must be on `PATH` for the link step.
- If a build fails copying `farview.exe`, a running copy has it locked. Close it and retry.

## Status

- **Tested**: Windows client against a Proxmox/Debian server (Linux 6.8 PVE kernel, Xeon E5-1410), including live CPU, memory, disks and RAPL power. `--demo` and the connect screen render correctly headless.
- **Not tested**: Linux/macOS clients, reconnect after a real network drop, error hints against real failures, non-root RAPL access, servers with batteries/hwmon sensors, many disks.
- **Not supported**: password login (it is `BatchMode` by design), non-Linux servers, per-process lists, per-core CPU, network I/O.
- Power often reads "no readable power sensor" for non-root users: recent kernels restrict RAPL counters to root.

## Where to extend

- New metric: add a line to `RemoteScript`, parse it in `Telemetry.Feed`, draw it in a new `DrawXxx` in `App.cs`.
- New server OS: add another script and pick it per target; `Telemetry` is already independent of how lines arrive.
- Other transports (an agent over HTTP, a local source): implement `ISession`.
