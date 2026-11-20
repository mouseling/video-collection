namespace Vcm.Core;

/// <summary>
/// A text log beside the library database.
///
/// Deliberately the smallest thing that works: one file, appended, truncated when it gets large.
/// The app ships as a Windows GUI executable, so there is no console and anything written to
/// stderr is lost — which previously meant a crash or a silently-stopped play queue left no
/// evidence at all on a machine nobody can attach a debugger to. A logging framework would be
/// more infrastructure than one user on one machine needs.
/// </summary>
public static class Log
{
    private const long MaxBytes = 1 << 20;   // 1 MiB, then start over

    private static readonly object Gate = new();
    private static string? _path;

    /// <summary>Points the log at a directory. Until called, writes are discarded.</summary>
    public static void UseDirectory(string directory)
    {
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(directory);
                _path = System.IO.Path.Combine(directory, "vcm.log");
            }
            catch (Exception)
            {
                _path = null;   // unwritable location; logging is never worth failing startup for
            }
        }
    }

    public static string? Path
    {
        get { lock (Gate) return _path; }
    }

    public static void Write(string message, Exception? error = null)
    {
        lock (Gate)
        {
            if (_path is null) return;

            try
            {
                if (File.Exists(_path) && new FileInfo(_path).Length > MaxBytes)
                    File.Delete(_path);

                var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {message}";
                if (error is not null)
                    line += $"{Environment.NewLine}    {error.GetType().Name}: {error.Message}" +
                            $"{Environment.NewLine}{Indent(error.StackTrace)}";

                File.AppendAllText(_path, line + Environment.NewLine);
            }
            catch (Exception)
            {
                // Logging must never become the reason something fails.
            }
        }
    }

    private static string Indent(string? stackTrace) =>
        string.IsNullOrEmpty(stackTrace)
            ? "    (no stack trace)"
            : string.Join(Environment.NewLine,
                stackTrace.Split(Environment.NewLine).Select(l => "    " + l.Trim()));

    /// <summary>
    /// Catches the two classes of failure that otherwise vanish: an exception on a threadpool
    /// thread that kills the process, and an unobserved task exception that kills nothing but
    /// silently stops whatever it was doing.
    /// </summary>
    public static void CatchUnhandled()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Write("unhandled exception", e.ExceptionObject as Exception);

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Write("unobserved task exception", e.Exception);
            e.SetObserved();
        };
    }
}
