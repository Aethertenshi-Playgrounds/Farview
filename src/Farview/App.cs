using Cellray;

namespace Farview;

internal enum Screen { Connect, Monitor }

internal sealed class App
{
    private readonly HostStore _store;
    private readonly bool _demo;

    private Screen _screen = Screen.Connect;
    private string _hostInput;
    private int _savedIdx;
    private string _connectError = "";
    private bool _focusPending = true;

    private ISession? _session;
    private string? _pendingRemember;
    private double _retryAt; // 0 = no reconnect scheduled
    private int _spin;

    private static Color ErrorColor => Theme.Heat.At(100);

    private readonly string? _startTarget;

    public App(HostStore store, bool demo, string? startTarget = null)
    {
        _store = store;
        _demo = demo;
        _startTarget = startTarget;
        _hostInput = startTarget ?? store.Last ?? "";
        _savedIdx = Math.Max(0, store.Hosts.IndexOf(_hostInput));
    }

    public void Run(int maxFrames)
    {
        if (_demo)
        {
            _session = new DemoSession(new Telemetry());
            _session.Start();
            _screen = Screen.Monitor;
        }
        else if (_startTarget is not null) TryConnect(); // on a bad target this stays on the connect screen with the error

        for (int frame = 0; !Tui.ShouldClose() && frame < maxFrames; frame++)
        {
            Tui.BeginDrawing();
            Tui.ClearBackground(Theme.MainBg);
            if (_screen == Screen.Connect) DrawConnect();
            else DrawMonitor();
            Tui.EndDrawing();
        }
        _session?.Stop();
    }

    // ---- connect screen -------------------------------------------------

    private void DrawConnect()
    {
        int W = Tui.GetScreenWidth(), H = Tui.GetScreenHeight();
        if (W < 44 || H < 19)
        {
            Gui.Label(1, 1, $"Terminal too small ({W}x{H}); need 44x19.");
            if (Tui.IsEscapePressed()) Tui.RequestClose();
            return;
        }

        int pw = Math.Min(66, W - 2), ph = 17;
        int x = (W - pw) / 2, y = Math.Max(0, (H - ph) / 2);
        int ix = x + 3, iw = pw - 6;

        Gui.Panel(new Rect(x, y, pw, ph), "farview");
        Gui.Label(ix, y + 2, "Monitor a Linux server over SSH");
        Gui.Label(ix, y + 4, "Server  [user@]host[:port]", Theme.InactiveFg);

        var box = new Rect(ix, y + 5, iw, 3);
        if (_focusPending) { Gui.FocusTextBox(box); _focusPending = false; }
        Gui.TextBox(box, ref _hostInput, out bool enter, "user@192.168.1.10", 120);
        bool go = enter;

        if (Gui.Button(new Rect(ix, y + 9, 14, 3), "Connect")) go = true;

        if (_store.Hosts.Count > 0)
        {
            int ddX = ix + 18, ddW = Math.Max(10, iw - 18 - 12);
            Gui.Label(ddX, y + 9, "Saved servers", Theme.InactiveFg);
            if (Gui.Dropdown(ddX, y + 10, ddW, _store.Hosts, ref _savedIdx, 6))
                _hostInput = _store.Hosts[_savedIdx];
            if (Gui.Button(new Rect(ix + iw - 10, y + 9, 10, 3), "Forget"))
            {
                if (_savedIdx >= 0 && _savedIdx < _store.Hosts.Count) _store.Forget(_store.Hosts[_savedIdx]);
                _savedIdx = Math.Clamp(_savedIdx, 0, Math.Max(0, _store.Hosts.Count - 1));
            }
        }
        else Gui.Label(ix + 18, y + 10, "no saved servers yet", Theme.InactiveFg);

        if (_connectError.Length > 0)
        {
            var lines = _connectError.Split('\n');
            for (int i = 0; i < lines.Length && i < 2; i++)
                Gui.Label(ix, y + 13 + i, Fmt.Fit(lines[i], iw), ErrorColor);
        }

        Gui.Label(ix, y + ph - 2, "Enter connect  ·  Tab move  ·  Esc quit", Theme.InactiveFg);

        if (go) TryConnect();
        else if (Tui.IsEscapePressed()) Tui.RequestClose();
    }

    private void TryConnect()
    {
        if (!SshTarget.TryParse(_hostInput, out var target, out string error))
        {
            _connectError = error;
            return;
        }
        _connectError = "";
        _pendingRemember = target.Display;
        _session = new SshSession(target, new Telemetry());
        _session.Start();
        _retryAt = 0;
        _screen = Screen.Monitor;
    }

    private void GoToConnect()
    {
        _session?.Stop();
        _session = null;
        _pendingRemember = null;
        _retryAt = 0;
        _connectError = "";
        _focusPending = true;
        _hostInput = _store.Last ?? _hostInput;
        _savedIdx = Math.Max(0, _store.Hosts.IndexOf(_hostInput));
        _screen = Screen.Connect;
    }

    private void Restart()
    {
        var old = _session!;
        old.Stop();
        _session = new SshSession(old.Target, old.Data);
        _session.Start();
        _retryAt = 0;
    }

    // ---- monitor screen -------------------------------------------------

    private void DrawMonitor()
    {
        var s = _session!;
        s.Pump();
        if (s.EverConnected && _pendingRemember != null)
        {
            _store.Remember(_pendingRemember);
            _pendingRemember = null;
        }

        int W = Tui.GetScreenWidth(), H = Tui.GetScreenHeight();
        double now = Tui.GetTime();

        if (Tui.IsEscapePressed()) { GoToConnect(); return; }
        if (Tui.IsKeyPressed(ConsoleKey.Q)) { Tui.RequestClose(); return; }

        if (s.State == SessionState.Disconnected && !_demo)
        {
            if (_retryAt == 0) _retryAt = now + 5;
            if (now >= _retryAt) { Restart(); s = _session!; }
        }
        if (s.State == SessionState.Failed && Tui.IsKeyPressed(ConsoleKey.R)) { Restart(); s = _session!; }

        if (s.State == SessionState.Failed) DrawFailed(s, W, H);
        else if (s.Data.Samples == 0) DrawConnecting(s, W, H);
        else DrawDashboard(s, W, H, now);
    }

    private void DrawConnecting(ISession s, int W, int H)
    {
        int pw = Math.Min(56, W - 2), ph = 7;
        int x = (W - pw) / 2, y = Math.Max(0, (H - ph) / 2);
        Gui.Panel(new Rect(x, y, pw, ph), "connecting");
        string[] frames = { "⠋", "⠙", "⠹", "⠸", "⠼", "⠴", "⠦", "⠧", "⠇", "⠏" };
        Gui.Label(x + 3, y + 2, frames[(_spin++ / 3) % frames.Length] + " " + Fmt.Fit("Connecting to " + s.Target.Display, pw - 8), Theme.HiFg);
        Gui.Label(x + 3, y + 3, "waiting for the first sample...", Theme.InactiveFg);
        Gui.Label(x + 3, y + 5, "Esc cancel", Theme.InactiveFg);
    }

    private void DrawFailed(ISession s, int W, int H)
    {
        var lines = s.Message.Split('\n');
        int pw = Math.Min(78, W - 2), ph = Math.Min(H, 6 + lines.Length);
        int x = (W - pw) / 2, y = Math.Max(0, (H - ph) / 2);
        Gui.Panel(new Rect(x, y, pw, ph), "connection failed");
        Gui.Label(x + 3, y + 2, "Could not monitor " + s.Target.Display, Theme.MainFg);
        for (int i = 0; i < lines.Length && y + 3 + i < y + ph - 2; i++)
            Gui.Label(x + 3, y + 3 + i, Fmt.Fit(lines[i], pw - 6), i == 0 ? ErrorColor : Theme.InactiveFg);
        Gui.Label(x + 3, y + ph - 2, "R retry  ·  Esc change server", Theme.InactiveFg);
    }

    private void DrawDashboard(ISession s, int W, int H, double now)
    {
        if (W < 60 || H < 20)
        {
            Gui.Label(1, 1, $"Terminal too small ({W}x{H}); need at least 60x20.");
            return;
        }
        var d = s.Data;

        // header: name, server, status
        string status; Color statusColor;
        if (s.State == SessionState.Disconnected)
        {
            status = $"● lost - retrying in {Math.Max(0, (int)Math.Ceiling(_retryAt - now))}s";
            statusColor = ErrorColor;
        }
        else { status = "● live"; statusColor = Theme.Ok; }

        Tui.DrawText("Farview", 1, 0, Theme.HiFg);
        string info = $"  {s.Target.Display}  ·  {d.Host}  ·  {d.Kernel}  ·  up {Fmt.Uptime(d.UptimeSeconds)}";
        Tui.DrawText(Fmt.Fit(info, W - 9 - status.Length - 2), 8, 0, Theme.InactiveFg);
        Tui.DrawText(status, W - status.Length - 1, 0, statusColor);

        // layout: cpu on top, memory, then disks | power
        int top = 1, avail = H - 2, memH = 6;
        int botH = Math.Clamp(2 + Math.Max(d.Disks.Count, 3), 6, Math.Max(6, avail - memH - 6));
        int cpuH = avail - memH - botH;
        int botY = top + cpuH + memH;
        int disksW = W * 3 / 5;

        DrawCpu(new Rect(0, top, W, cpuH), d);
        DrawMemory(new Rect(0, top + cpuH, W, memH), d);
        DrawDisks(new Rect(0, botY, disksW, botH), d);
        DrawPower(new Rect(disksW, botY, W - disksW, botH), d);

        Gui.Label(1, H - 1, "Esc change server  ·  q quit", Theme.InactiveFg);
    }

    private static void DrawCpu(Rect r, Telemetry d)
    {
        Gui.Panel(r, "cpu", 1);

        string info = Fmt.Fit($"{d.Cores} cores · {d.Model}", r.Width / 2);
        if (d.Cores > 0 && info.Length > 0 && r.Width > 30)
        {
            int px = r.X + r.Width - info.Length - 4;
            Tui.DrawText("┐", px, r.Y, Theme.Box);
            Tui.DrawText(info, px + 1, r.Y, Theme.InactiveFg);
            Tui.DrawText("┌", px + 1 + info.Length, r.Y, Theme.Box);
        }

        int ix = r.X + 1, iy = r.Y + 1, iw = r.Width - 2, ih = r.Height - 2;
        Gui.Graph(ix, iy, iw, ih - 1, d.CpuHistory);
        MeterRow(ix, iy + ih - 1, iw, "cpu", d.CpuPercent, $"{d.CpuPercent,3:F0}%");
    }

    private static void DrawMemory(Rect r, Telemetry d)
    {
        Gui.Panel(r, "memory", 2);
        int ix = r.X + 1, iy = r.Y + 1, iw = r.Width - 2;
        Gui.Graph(ix, iy, iw, 2, d.MemHistory);

        long used = d.MemTotalKb - d.MemAvailKb;
        MeterRow(ix, iy + 2, iw, "used", d.MemPercent, $"{d.MemPercent,3:F0}%  {Fmt.Kb(used)} / {Fmt.Kb(d.MemTotalKb)}");

        if (d.SwapTotalKb > 0)
            MeterRow(ix, iy + 3, iw, "swap", d.SwapPercent,
                     $"{d.SwapPercent,3:F0}%  {Fmt.Kb(d.SwapTotalKb - d.SwapFreeKb)} / {Fmt.Kb(d.SwapTotalKb)}");
        else
            MeterRow(ix, iy + 3, iw, "swap", 0, "none");
    }

    /// <summary>label, then a ■ meter, then right-aligned text.</summary>
    private static void MeterRow(int x, int y, int w, string label, double pct, string text)
    {
        Gui.Label(x, y, label, Theme.InactiveFg);
        int tw = text.Length;
        int mw = w - 6 - tw - 1;
        if (mw < 3) { mw = Math.Max(0, w - 6); tw = 0; }
        Gui.Meter(x + 6, y, mw, (float)pct);
        if (tw > 0) Gui.Label(x + w - tw, y, text, Theme.MainFg);
    }

    private static void DrawDisks(Rect r, Telemetry d)
    {
        Gui.Panel(r, "disks", 3);
        int ix = r.X + 1, iy = r.Y + 1, iw = r.Width - 2, rows = r.Height - 2;

        if (d.Disks.Count == 0)
        {
            Gui.Label(ix, iy, "no block devices found", Theme.InactiveFg);
            return;
        }

        int labelW = Math.Min(24, Math.Max(8, iw / 3));
        for (int i = 0; i < rows && i < d.Disks.Count; i++)
        {
            int y = iy + i;
            if (i == rows - 1 && d.Disks.Count > rows)
            {
                Gui.Label(ix, y, $"+{d.Disks.Count - rows + 1} more", Theme.InactiveFg);
                break;
            }
            var dk = d.Disks[i];
            Gui.Label(ix, y, Fmt.Fit(dk.Mount, labelW), Theme.MainFg);

            string text = $"{dk.Percent,3:F0}%  {Fmt.Kb(dk.UsedKb)}/{Fmt.Kb(dk.TotalKb)}";
            int tw = text.Length;
            int mx = ix + labelW + 1;
            int mw = iw - labelW - 1 - tw - 1;
            if (mw < 4) { tw = 0; mw = Math.Max(0, iw - labelW - 1); }
            Gui.Meter(mx, y, mw, (float)dk.Percent);
            if (tw > 0) Gui.Label(ix + iw - tw, y, text, Theme.MainFg);
        }
    }

    private static void DrawPower(Rect r, Telemetry d)
    {
        Gui.Panel(r, "power", 4);
        int ix = r.X + 1, iy = r.Y + 1, iw = r.Width - 2, ih = r.Height - 2;

        if (d.PowerWatts is null && d.Samples < 3)
        {
            Gui.Label(ix, iy, "measuring...", Theme.InactiveFg); // watts need two samples to compute
            return;
        }

        if (d.PowerWatts is not double watts)
        {
            string[] help =
            {
                "no readable power sensor",
                "",
                "needs RAPL, battery or hwmon power",
                "data that this ssh user can read",
            };
            for (int i = 0; i < help.Length && i < ih; i++)
                Gui.Label(ix, iy + i, Fmt.Fit(help[i], iw), i == 0 ? Theme.MainFg : Theme.InactiveFg);
            return;
        }

        Gui.Label(ix, iy, $"{watts:F1} W", Theme.HiFg);
        if (d.PowerLabel.Length > 0) Gui.Label(ix + $"{watts:F1} W".Length + 2, iy, d.PowerLabel, Theme.InactiveFg);

        int partLines = Math.Min(Math.Min(d.PowerParts.Count, 3), Math.Max(0, ih - 4));
        int graphH = ih - 1 - partLines;
        if (graphH >= 1)
        {
            float max = 10f;
            for (int i = 0; i < d.PowerHistory.Count; i++) max = Math.Max(max, d.PowerHistory[i] * 1.2f);
            Gui.Graph(ix, iy + 1, iw, graphH, d.PowerHistory, 0f, max);
        }
        for (int i = 0; i < partLines; i++)
        {
            var p = d.PowerParts[i];
            int y = iy + ih - partLines + i;
            string val = $"{p.Watts:F1} W";
            Gui.Label(ix, y, Fmt.Fit(p.Name, Math.Max(0, iw - val.Length - 1)), Theme.InactiveFg);
            Gui.Label(ix + iw - val.Length, y, val, Theme.MainFg);
        }
    }
}
