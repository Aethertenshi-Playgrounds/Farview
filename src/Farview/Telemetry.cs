using System.Globalization;
using Cellray;

namespace Farview;

internal sealed record DiskInfo(string Device, string Mount, long TotalKb, long UsedKb, long AvailKb)
{
    /// <summary>Same definition as df's Use%: used / (used + available).</summary>
    public double Percent => UsedKb + AvailKb > 0 ? 100.0 * UsedKb / (UsedKb + AvailKb) : 0;
}

internal sealed record PowerReading(string Name, double Watts);

/// <summary>Parses the remote script's output lines into current values and rolling histories.</summary>
internal sealed class Telemetry
{
    // static info
    public string Host = "", Kernel = "", Model = "";
    public int Cores;

    // latest sample
    public int Samples;
    public double UptimeSeconds;
    public double CpuPercent, MemPercent, SwapPercent;
    public long MemTotalKb, MemAvailKb, SwapTotalKb, SwapFreeKb;
    public IReadOnlyList<DiskInfo> Disks = Array.Empty<DiskInfo>();
    public double? PowerWatts;
    public string PowerLabel = "";
    public List<PowerReading> PowerParts = new();

    public readonly History CpuHistory = new(512);
    public readonly History MemHistory = new(512);
    public readonly History PowerHistory = new(512);

    // baselines for rate calculations (CPU jiffies, RAPL energy)
    private ulong _prevTotal, _prevIdle;
    private bool _hasCpuBase;
    private double _prevUp;
    private bool _hasPowerBase;
    private Dictionary<string, ulong> _raplPrev = new();
    private readonly Dictionary<string, ulong> _raplMax = new();
    private readonly Dictionary<string, string> _raplName = new();

    // the block being assembled (U ... END)
    private bool _inBlock;
    private double _up;
    private ulong[]? _cpu;
    private long _memTotal, _memAvail, _memFree, _swapTotal, _swapFree;
    private List<DiskInfo> _disks = new();
    private Dictionary<string, ulong> _rapl = new();
    private List<PowerReading> _instant = new();

    /// <summary>Forget counters so a reconnect doesn't compute rates across the gap. Histories are kept.</summary>
    public void ResetBaseline()
    {
        _hasCpuBase = false;
        _hasPowerBase = false;
        _inBlock = false;
        _raplPrev = new();
    }

    /// <summary>Feeds one output line. Returns true when it completed a sample.</summary>
    public bool Feed(string line)
    {
        if (line.Length == 0) return false;
        int sp = line.IndexOf(' ');
        string tag = sp < 0 ? line : line.Substring(0, sp);
        string rest = sp < 0 ? "" : line.Substring(sp + 1);

        switch (tag)
        {
            case "HOST": Host = rest.Trim(); break;
            case "KERNEL": Kernel = rest.Trim(); break;
            case "CORES": int.TryParse(rest.Trim(), out Cores); break;
            case "MODEL": Model = rest.Trim(); break;
            case "RAPLMAX":
            {
                var t = rest.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
                if (t.Length >= 2 && ulong.TryParse(t[1], out ulong max)) _raplMax[t[0]] = max;
                if (t.Length >= 3) _raplName[t[0]] = t[2].Trim();
                break;
            }
            case "U":
                _inBlock = true;
                _cpu = null;
                _memTotal = _memAvail = _memFree = _swapTotal = _swapFree = 0;
                _disks = new();
                _rapl = new();
                _instant = new();
                double.TryParse(rest.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out _up);
                break;
            case "C": ParseCpu(rest); break;
            case "M": ParseMem(rest); break;
            case "D": ParseDisk(rest); break;
            case "P":
            {
                var t = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (t.Length >= 2 && ulong.TryParse(t[1], out ulong e)) _rapl[t[0]] = e;
                break;
            }
            case "W":
            {
                var t = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (t.Length >= 2 && double.TryParse(t[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double uw) && uw > 0)
                    _instant.Add(new PowerReading(t[0], uw / 1_000_000.0));
                break;
            }
            case "END": return Commit();
        }
        return false;
    }

    private void ParseCpu(string rest)
    {
        var t = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (t.Length < 5) return; // "cpu" + at least user nice system idle
        var vals = new List<ulong>(8);
        for (int i = 1; i < t.Length && vals.Count < 8; i++)
        {
            if (!ulong.TryParse(t[i], out ulong v)) return;
            vals.Add(v);
        }
        _cpu = vals.ToArray();
    }

    private void ParseMem(string rest)
    {
        var t = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (t.Length < 2 || !long.TryParse(t[1], out long kb)) return;
        switch (t[0])
        {
            case "MemTotal:": _memTotal = kb; break;
            case "MemAvailable:": _memAvail = kb; break;
            case "MemFree:": _memFree = kb; break; // fallback for kernels without MemAvailable
            case "SwapTotal:": _swapTotal = kb; break;
            case "SwapFree:": _swapFree = kb; break;
        }
    }

    private void ParseDisk(string rest)
    {
        // df -P -k: Filesystem 1024-blocks Used Available Capacity Mounted-on (mount point may contain spaces)
        var t = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (t.Length < 6) return;

        string dev = t[0];
        // real block devices only (not loop/ram devices, or FUSE mounts like Proxmox's /etc/pve)
        if (!dev.StartsWith("/dev/") || dev.StartsWith("/dev/loop") || dev.StartsWith("/dev/ram") || dev == "/dev/fuse") return;
        if (!long.TryParse(t[1], out long total) || !long.TryParse(t[2], out long used) || !long.TryParse(t[3], out long avail)) return;

        string mount = string.Join(' ', t[5..]);
        if (mount.StartsWith("/snap")) return;
        foreach (var d in _disks) if (d.Device == dev) return; // bind mounts / subvolumes of the same device

        _disks.Add(new DiskInfo(dev, mount, total, used, avail));
    }

    private bool Commit()
    {
        if (!_inBlock) return false;
        _inBlock = false;
        UptimeSeconds = _up;

        // CPU: share of non-idle jiffies since the previous sample
        if (_cpu is { Length: >= 4 })
        {
            ulong total = 0;
            for (int i = 0; i < _cpu.Length; i++) total += _cpu[i];
            ulong idle = _cpu[3] + (_cpu.Length > 4 ? _cpu[4] : 0); // idle + iowait

            if (_hasCpuBase && total > _prevTotal)
            {
                double dTotal = total - _prevTotal;
                double dIdle = idle >= _prevIdle ? idle - _prevIdle : 0;
                CpuPercent = Math.Clamp(100.0 * (1.0 - dIdle / dTotal), 0, 100);
                CpuHistory.Push((float)CpuPercent);
            }
            _prevTotal = total;
            _prevIdle = idle;
            _hasCpuBase = true;
        }

        // memory
        if (_memTotal > 0)
        {
            long avail = _memAvail > 0 ? _memAvail : _memFree;
            MemTotalKb = _memTotal; MemAvailKb = avail;
            SwapTotalKb = _swapTotal; SwapFreeKb = _swapFree;
            MemPercent = Math.Clamp(100.0 * (_memTotal - avail) / _memTotal, 0, 100);
            SwapPercent = _swapTotal > 0 ? Math.Clamp(100.0 * (_swapTotal - _swapFree) / _swapTotal, 0, 100) : 0;
            MemHistory.Push((float)MemPercent);
        }

        Disks = _disks;
        CommitPower();

        Samples++;
        return true;
    }

    private void CommitPower()
    {
        var parts = new List<PowerReading>();
        double? total = null;
        string label = "";

        // RAPL energy counters -> watts, using the server's uptime as the clock
        double dt = _up - _prevUp;
        if (_hasPowerBase && dt > 0.2 && dt < 30 && _rapl.Count > 0)
        {
            double pkgSum = 0, all = 0;
            bool havePkg = false;
            double? psys = null;

            foreach (var (zone, energy) in _rapl)
            {
                if (!_raplPrev.TryGetValue(zone, out ulong prev)) continue;
                ulong delta;
                if (energy >= prev) delta = energy - prev;
                else if (_raplMax.TryGetValue(zone, out ulong max) && max > prev) delta = max - prev + energy; // counter wrapped
                else continue;

                double watts = delta / 1e6 / dt;
                string name = _raplName.TryGetValue(zone, out var nm) ? nm : zone;
                parts.Add(new PowerReading(name, watts));
                all += watts;
                if (name == "psys") psys = watts;
                else if (name.StartsWith("package")) { pkgSum += watts; havePkg = true; }
            }

            if (psys is double ps) { total = ps; label = "platform"; }
            else if (havePkg) { total = pkgSum; label = "cpu package"; }
            else if (parts.Count > 0) { total = all; label = "rapl"; }
        }
        _prevUp = _up;
        _raplPrev = _rapl;
        _hasPowerBase = true;

        // instantaneous sensors (battery, hwmon) are shown too, and used as the total when RAPL isn't available
        foreach (var r in _instant) parts.Add(r);
        if (total is null && _instant.Count > 0) { total = _instant[0].Watts; label = _instant[0].Name; }

        PowerWatts = total;
        PowerLabel = label;
        PowerParts = parts;
        if (total is double w) PowerHistory.Push((float)w);
    }
}
