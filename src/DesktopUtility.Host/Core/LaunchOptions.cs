namespace DesktopUtility.Host.Core;

/// <summary>Command-line options: <c>--port N</c>, <c>--mode remote|extended</c>, <c>--minimized</c>, <c>--quit</c> (ask a running instance to exit).</summary>
public sealed record LaunchOptions(int? Port, DisplayMode? Mode, bool StartMinimized, bool Quit)
{
    public static LaunchOptions Parse(string[] args)
    {
        int? port = null;
        DisplayMode? mode = null;
        bool minimized = false;
        bool quit = false;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i].ToLowerInvariant())
            {
                case "--port" when i + 1 < args.Length && int.TryParse(args[i + 1], out int p) && p is > 0 and < 65536:
                    port = p;
                    i++;
                    break;
                case "--mode" when i + 1 < args.Length:
                    mode = args[++i].ToLowerInvariant() switch
                    {
                        "remote" => DisplayMode.RemoteControl,
                        "extended" => DisplayMode.ExtendedDisplay,
                        _ => null
                    };
                    break;
                case "--minimized":
                    minimized = true;
                    break;
                case "--quit":
                    quit = true;
                    break;
            }
        }

        return new LaunchOptions(port, mode, minimized, quit);
    }
}
