using System.IO;
using System.Text;

namespace DesktopUtility.Host.Core;

/// <summary>
/// Console replacement: keeps a bounded in-memory history for the diagnostics panel
/// and mirrors everything to a rolling log file in %LOCALAPPDATA%\DesktopUtility\logs.
/// Installed once at startup via <see cref="Install"/>, so plain Console.WriteLine keeps working everywhere.
/// </summary>
public sealed class AppLog : TextWriter
{
    private const int MaxHistory = 400;
    private const long MaxFileBytes = 2 * 1024 * 1024;

    public static AppLog Instance { get; } = new();

    private readonly object _gate = new();
    private readonly StringBuilder _pending = new();
    private readonly Queue<string> _history = new();
    private StreamWriter? _file;

    /// <summary>Raised (on the writing thread) for every completed line.</summary>
    public event Action<string>? LineWritten;

    public override System.Text.Encoding Encoding => System.Text.Encoding.UTF8;

    public static void Install()
    {
        Instance.OpenFile();
        Console.SetOut(Instance);
        Console.SetError(Instance);
    }

    public string[] Snapshot()
    {
        lock (_gate) return _history.ToArray();
    }

    private void OpenFile()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.LogsDir);
            var info = new FileInfo(AppPaths.LogFile);
            if (info.Exists && info.Length > MaxFileBytes)
                File.Move(AppPaths.LogFile, Path.ChangeExtension(AppPaths.LogFile, ".old.log"), overwrite: true);

            _file = new StreamWriter(new FileStream(AppPaths.LogFile, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
            {
                AutoFlush = true
            };
            _file.WriteLine($"──── {DateTime.Now:yyyy-MM-dd HH:mm:ss} session start ────");
        }
        catch
        {
            _file = null; // logging must never break the app
        }
    }

    public override void Write(char value) => Append(value.ToString());
    public override void Write(string? value) => Append(value);
    public override void Write(char[] buffer, int index, int count) => Append(new string(buffer, index, count));
    public override void WriteLine(string? value) => Append((value ?? "") + "\n");
    public override void WriteLine() => Append("\n");

    private void Append(string? text)
    {
        if (string.IsNullOrEmpty(text)) return;

        List<string>? completed = null;
        lock (_gate)
        {
            foreach (char c in text)
            {
                if (c == '\r') continue;
                if (c != '\n') { _pending.Append(c); continue; }

                string line = _pending.ToString();
                _pending.Clear();
                (completed ??= new()).Add(line);

                _history.Enqueue(line);
                while (_history.Count > MaxHistory) _history.Dequeue();
                try { _file?.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {line}"); } catch { }
            }
        }

        if (completed == null) return;
        foreach (var line in completed)
        {
            try { LineWritten?.Invoke(line); } catch { }
        }
    }
}
