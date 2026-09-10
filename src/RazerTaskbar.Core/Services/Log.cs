// Crash/diagnostic logging. .NET semantics differ from the Rust build here:
// an unhandled exception on ANY thread (watcher, UIA, threadpool) terminates
// the whole process, and stderr of a windowed app goes nowhere. Background
// entry points funnel failures through Log, which persists to a file next to
// settings.json and mirrors to stderr.

namespace RazerTaskbar.Core;

public static class Log
{
    private static readonly object Lock = new();

    /// <summary>Rollover threshold: the log records periodic TTL notes
    /// (taskbar cache refreshes, per-poll reads) and would grow without
    /// bound otherwise. Past this size the file becomes .old (one deep
    /// history kept) and a fresh file starts.</summary>
    private const long MaxBytes = 5 * 1024 * 1024;

    private static bool _installed;

    public static string FilePath
    {
        get
        {
            var appData = Environment.GetEnvironmentVariable("APPDATA");
            var dir = Path.Combine(string.IsNullOrEmpty(appData) ? "." : appData, "razer-taskbar");
            return Path.Combine(dir, "csharp-debug.log");
        }
    }

    public static void Install()
    {
        lock (Lock)
        {
            if (_installed)
            {
                return;
            }
            _installed = true;
        }
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Error($"AppDomain.UnhandledException (terminating={e.IsTerminating}): {e.ExceptionObject}");
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Error($"UnobservedTaskException: {e.Exception}");
            e.SetObserved();
        };
        Info($"log started, pid={Environment.ProcessId}, exe={Environment.ProcessPath}");
    }

    public static void Info(string line) => Write("INFO", line);

    public static void Error(string line) => Write("ERROR", line);

    public static void Error(string line, Exception e)
        => Write("ERROR", $"{line}: {e.GetType().Name}: {e.Message}\n{e.StackTrace}");

    private static void Write(string level, string line)
    {
        var stamped = $"{DateTime.Now:HH:mm:ss.fff} [{level}] {line}";
        lock (Lock)
        {
            try
            {
                var path = FilePath;
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                if (new FileInfo(path) is { Exists: true, Length: >= MaxBytes })
                {
                    File.Move(path, path + ".old", overwrite: true);
                }
                File.AppendAllText(path, stamped + Environment.NewLine);
            }
            catch (Exception)
            {
                // Log sink failures must never take the app down.
            }
        }
        try
        {
            // Mirrored to stderr for console runs; a GUI process has no
            // console — writes must never throw into a message loop.
            Console.Error.WriteLine(stamped);
        }
        catch (Exception)
        {
        }
    }
}
