namespace Farview;

/// <summary>A validated "[user@]host[:port]" destination for the system ssh client.</summary>
internal readonly record struct SshTarget(string Destination, int? Port)
{
    public string Display => Port is int p ? $"{Destination}:{p}" : Destination;

    public static bool TryParse(string input, out SshTarget target, out string error)
    {
        target = default;
        error = "";
        string s = (input ?? "").Trim();

        if (s.Length == 0) { error = "Enter a server, e.g. user@192.168.1.10"; return false; }
        if (s[0] == '-') { error = "A server cannot start with '-'"; return false; }

        // "host:port" (a single colon). More colons are treated as a bare IPv6 address.
        int? port = null;
        int colon = s.LastIndexOf(':');
        if (colon >= 0 && s.IndexOf(':') == colon)
        {
            if (!int.TryParse(s.AsSpan(colon + 1), out int p) || p < 1 || p > 65535)
            {
                error = "Port must be a number from 1 to 65535";
                return false;
            }
            port = p;
            s = s.Substring(0, colon);
        }

        foreach (char c in s)
        {
            bool ok = char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_' or '@' or '%' or ':' or '[' or ']';
            if (!ok) { error = $"Unexpected character '{c}' in server name"; return false; }
        }

        int at = s.IndexOf('@');
        if (at == 0 || at == s.Length - 1) { error = "Use the form user@host"; return false; }
        if (s.Length == 0) { error = "Enter a server, e.g. user@192.168.1.10"; return false; }

        target = new SshTarget(s, port);
        return true;
    }
}
