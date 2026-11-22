using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Farview;

internal enum SessionState { Connecting, Connected, Failed, Disconnected }

/// <summary>A source of telemetry: a real ssh connection or the built-in demo.</summary>
internal interface ISession
{
    SessionState State { get; }
    /// <summary>Explanation for Failed / Disconnected (may contain several lines).</summary>
    string Message { get; }
    bool EverConnected { get; }
    SshTarget Target { get; }
    Telemetry Data { get; }
    void Start();
    /// <summary>Call once per frame on the UI thread: moves received lines into Data.</summary>
    void Pump();
    void Stop();
}

/// <summary>
/// Runs the system ssh client: "ssh host sh -s", sends RemoteScript on stdin and reads one block of lines per second.
/// Uses BatchMode, so it never asks for a password: key (or agent) login is required, and the host must already be trusted.
/// </summary>
internal sealed class SshSession : ISession
{
    private Process? _proc;
    private readonly ConcurrentQueue<string> _lines = new();
    private readonly List<string> _err = new();
    private volatile bool _outDone;
    private volatile bool _stopping;

    public SessionState State { get; private set; } = SessionState.Connecting;
    public string Message { get; private set; } = "";
    public bool EverConnected { get; private set; }
    public SshTarget Target { get; }
    public Telemetry Data { get; }

    public SshSession(SshTarget target, Telemetry data)
    {
        Target = target;
        Data = data;
    }

    public void Start()
    {
        Data.ResetBaseline();

        var psi = new ProcessStartInfo("ssh")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (string a in new[]
        {
            "-T",
            "-o", "BatchMode=yes",
            "-o", "ConnectTimeout=10",
            "-o", "ServerAliveInterval=5",
            "-o", "ServerAliveCountMax=3",
            "-o", "LogLevel=ERROR",
        }) psi.ArgumentList.Add(a);
        if (Target.Port is int port) { psi.ArgumentList.Add("-p"); psi.ArgumentList.Add(port.ToString(CultureInfo.InvariantCulture)); }
        psi.ArgumentList.Add(Target.Destination);
        psi.ArgumentList.Add("sh");
        psi.ArgumentList.Add("-s");

        Process proc;
        try
        {
            proc = Process.Start(psi) ?? throw new InvalidOperationException("ssh did not start");
        }
        catch (Exception ex)
        {
            State = SessionState.Failed;
            Message = "Could not start the ssh client (" + ex.Message + ").\nInstall OpenSSH and make sure `ssh` is on your PATH.";
            return;
        }
        _proc = proc;

        try
        {
            // the script is sent with plain \n line endings; the loop keeps running on the server until ssh is closed
            proc.StandardInput.NewLine = "\n";
            proc.StandardInput.Write(RemoteScript.Text.Replace("\r\n", "\n"));
            proc.StandardInput.Write("\n");
            proc.StandardInput.Flush();
        }
        catch { /* ssh already exited: reported from the reader threads / Pump */ }

        new Thread(() =>
        {
            try { string? l; while ((l = proc.StandardOutput.ReadLine()) != null) _lines.Enqueue(l); } catch { }
            _outDone = true;
        }) { IsBackground = true, Name = "farview ssh stdout" }.Start();

        new Thread(() =>
        {
            try
            {
                string? l;
                while ((l = proc.StandardError.ReadLine()) != null)
                    lock (_err) { _err.Add(l); if (_err.Count > 8) _err.RemoveAt(0); }
            }
            catch { }
        }) { IsBackground = true, Name = "farview ssh stderr" }.Start();
    }

    public void Pump()
    {
        for (int budget = 2000; budget > 0 && _lines.TryDequeue(out string? line); budget--)
        {
            if (Data.Feed(line) && State != SessionState.Connected)
            {
                State = SessionState.Connected;
                EverConnected = true;
                Message = "";
            }
        }

        if ((State == SessionState.Connecting || State == SessionState.Connected) && _outDone && _lines.IsEmpty && !_stopping)
            Finish();
    }

    private void Finish()
    {
        string tail = "";
        try { _proc?.WaitForExit(300); } catch { }
        lock (_err)
        {
            for (int i = _err.Count - 1; i >= 0; i--)
                if (_err[i].Trim().Length > 0) { tail = _err[i].Trim(); break; }
        }

        string explained = Explain(tail);
        if (EverConnected)
        {
            State = SessionState.Disconnected;
            Message = tail.Length > 0 ? explained : "Connection lost.";
        }
        else
        {
            State = SessionState.Failed;
            int code = -1;
            try { if (_proc is { HasExited: true }) code = _proc.ExitCode; } catch { }
            Message = tail.Length > 0 ? explained : $"ssh exited without data (exit code {code}).";
        }
    }

    private string Explain(string err)
    {
        if (err.Length == 0) return err;
        string dest = Target.Destination;
        string portArg = Target.Port is int p ? $" -p {p}" : "";

        if (err.Contains("Permission denied"))
            return err + $"\nKey login is required. Install your public key on the server (ssh-copy-id{portArg} {dest}),\nor load it into ssh-agent, then retry.";
        if (err.Contains("Host key verification failed") || err.Contains("REMOTE HOST IDENTIFICATION HAS CHANGED"))
            return err + $"\nRun `ssh{portArg} {dest}` once in a terminal to check and trust the server, then retry.";
        if (err.Contains("Could not resolve hostname"))
            return err + "\nCheck the spelling or use the IP address.";
        if (err.Contains("timed out") || err.Contains("refused") || err.Contains("No route"))
            return err + "\nIs the server up, reachable from here, and running sshd on this port?";
        return err;
    }

    public void Stop()
    {
        _stopping = true;
        try { _proc?.Kill(entireProcessTree: true); } catch { }
        try { _proc?.Dispose(); } catch { }
    }
}

/// <summary>Synthetic data in the same text protocol as the remote script, for trying the UI without a server (--demo).</summary>
internal sealed class DemoSession : ISession
{
    private readonly Random _rng = new(7);
    private double _nextAt;
    private double _up = 86_400 * 3 + 4_000;
    private ulong _total, _idle, _energy;
    private int _tick;
    private bool _header;

    public SessionState State { get; private set; } = SessionState.Connecting;
    public string Message => "";
    public bool EverConnected { get; private set; }
    public SshTarget Target { get; } = new("demo@farview.example", null);
    public Telemetry Data { get; }

    public DemoSession(Telemetry data) => Data = data;

    public void Start() { Data.ResetBaseline(); }
    public void Stop() { }

    public void Pump()
    {
        double now = Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
        if (now < _nextAt) return;
        _nextAt = now + 0.1; // ten samples a second so the demo fills up quickly

        if (!_header)
        {
            _header = true;
            Data.Feed("HOST demo-server");
            Data.Feed("KERNEL Linux 6.8.0-demo");
            Data.Feed("CORES 8");
            Data.Feed("MODEL Demo CPU @ 3.00GHz");
            Data.Feed("RAPLMAX intel-rapl:0 262143328850 package-0");
        }

        _tick++;
        double t = _tick / 10.0;
        double cpu = Math.Clamp(45 + 35 * Math.Sin(t * 0.9) + 12 * Math.Sin(t * 3.7) + _rng.NextDouble() * 6, 2, 99);
        double watts = 28 + cpu * 0.55 + _rng.NextDouble() * 2;

        _up += 1.0;
        _total += 800;
        _idle += (ulong)(800 * (1 - cpu / 100));
        _energy += (ulong)(watts * 1_000_000);
        long memTotal = 16_000_000;
        long memAvail = (long)(memTotal * (0.42 - 0.12 * Math.Sin(t * 0.3)));

        Data.Feed("U " + _up.ToString("F2", CultureInfo.InvariantCulture));
        Data.Feed($"C cpu  {_total - _idle} 0 0 {_idle} 0 0 0 0 0 0");
        Data.Feed($"M MemTotal: {memTotal}");
        Data.Feed($"M MemAvailable: {memAvail}");
        Data.Feed("M SwapTotal: 4000000");
        Data.Feed($"M SwapFree: {(long)(4_000_000 * (0.9 - 0.05 * Math.Sin(t * 0.5)))}");
        Data.Feed("D Filesystem 1024-blocks Used Available Capacity Mounted on");
        Data.Feed("D /dev/nvme0n1p2 487000000 312000000 150000000 68% /");
        Data.Feed("D /dev/sda1 3900000000 3100000000 700000000 82% /mnt/storage");
        Data.Feed("D /dev/sdb1 1000000000 120000000 830000000 13% /mnt/backup");
        Data.Feed("D tmpfs 8000000 1000 7999000 1% /dev/shm");
        Data.Feed($"P intel-rapl:0 {_energy}");
        if (Data.Feed("END")) { State = SessionState.Connected; EverConnected = true; }
    }
}
