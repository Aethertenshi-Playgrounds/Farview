namespace Farview;

internal static class Fmt
{
    /// <summary>Kibibytes to a short human string: 512K, 3.25G, 12.0T ...</summary>
    public static string Kb(long kb)
    {
        string[] units = { "K", "M", "G", "T", "P" };
        double v = Math.Max(0, kb);
        int i = 0;
        while (v >= 1024 && i < units.Length - 1) { v /= 1024; i++; }
        return v >= 100 ? $"{v:F0}{units[i]}" : v >= 10 ? $"{v:F1}{units[i]}" : $"{v:F2}{units[i]}";
    }

    public static string Uptime(double seconds)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0, seconds));
        if (t.TotalDays >= 1) return $"{(int)t.TotalDays}d {t.Hours}h";
        if (t.TotalHours >= 1) return $"{t.Hours}h {t.Minutes}m";
        return $"{t.Minutes}m {t.Seconds}s";
    }

    public static string Fit(string text, int width) =>
        width <= 0 ? "" : text.Length <= width ? text : width == 1 ? text.Substring(0, 1) : text.Substring(0, width - 1) + "…";
}
