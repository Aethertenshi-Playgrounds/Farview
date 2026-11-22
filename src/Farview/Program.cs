using Cellray;
using Farview;

bool demo = false;
string? startTarget = null;   // farview [user@host[:port]]: connect right away
int maxFrames = int.MaxValue; // --frames N: run N frames and exit (smoke tests)

for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--demo": demo = true; break;
        case "--frames" when i + 1 < args.Length && int.TryParse(args[i + 1], out int n): maxFrames = n; i++; break;
        case "-h" or "--help":
            Console.WriteLine("""
                farview - monitor a Linux server over SSH (cpu, memory, disks, power)

                usage: farview [user@host[:port]] [--demo]

                  user@host   connect straight away instead of showing the server prompt
                  --demo      show made-up data instead of connecting (try the UI)

                Uses your installed `ssh` client with key/agent login (it never asks for a password).
                Saved servers are kept in your user config folder (Farview/hosts.txt).
                """);
            return;
        case var t when !t.StartsWith('-') && startTarget is null: startTarget = t; break;
    }
}

var store = HostStore.Load();

Tui.InitTerminal();
Tui.SetTargetFPS(30);
Tui.SetExitKey((ConsoleKey)0); // Esc is "back" inside the app; Ctrl+C always quits
try
{
    new App(store, demo, startTarget).Run(maxFrames);
}
finally
{
    Tui.CloseTerminal();
}
