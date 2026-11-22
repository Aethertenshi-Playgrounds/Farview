namespace Farview;

/// <summary>Remembers the last server and a short list of saved ones in a plain text file in the user's config folder.</summary>
internal sealed class HostStore
{
    private const int MaxHosts = 20;
    private readonly string _path;

    public List<string> Hosts { get; } = new();
    public string? Last { get; private set; }

    private HostStore(string path) => _path = path;

    public static HostStore Load()
    {
        string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Farview");
        var store = new HostStore(Path.Combine(dir, "hosts.txt"));
        store.Read();
        return store;
    }

    private void Read()
    {
        try
        {
            if (!File.Exists(_path)) return;
            foreach (string raw in File.ReadAllLines(_path))
            {
                string line = raw.Trim();
                if (line.StartsWith("last=")) Last = line.Substring(5);
                else if (line.StartsWith("host="))
                {
                    string h = line.Substring(5);
                    if (h.Length > 0 && !Hosts.Contains(h)) Hosts.Add(h);
                }
            }
        }
        catch { /* unreadable file: start empty */ }
    }

    /// <summary>Moves a server to the front of the list and marks it as the last one used.</summary>
    public void Remember(string display)
    {
        Hosts.Remove(display);
        Hosts.Insert(0, display);
        if (Hosts.Count > MaxHosts) Hosts.RemoveRange(MaxHosts, Hosts.Count - MaxHosts);
        Last = display;
        Save();
    }

    public void Forget(string display)
    {
        Hosts.Remove(display);
        if (Last == display) Last = Hosts.Count > 0 ? Hosts[0] : null;
        Save();
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var lines = new List<string>();
            if (Last != null) lines.Add("last=" + Last);
            foreach (string h in Hosts) lines.Add("host=" + h);

            string tmp = _path + ".tmp";
            File.WriteAllLines(tmp, lines);
            File.Move(tmp, _path, overwrite: true);
        }
        catch { /* read-only profile etc.: not fatal */ }
    }
}
