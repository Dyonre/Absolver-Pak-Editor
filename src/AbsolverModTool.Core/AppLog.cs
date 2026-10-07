namespace AbsolverModTool.Core;

/// <summary>
/// A persistent, append-only log file next to whichever executable is running (CLI or GUI) -
/// every line the app logs anywhere (console output, caught-and-handled exceptions, unhandled
/// crashes) also lands here, so a bug report never depends on someone having scrolled up far
/// enough or copy-pasted the right lines before closing the window. One file per process run,
/// named with a timestamp so old ones are never overwritten.
/// </summary>
public static class AppLog
{
    static readonly string LogDir = Path.Combine(AppContext.BaseDirectory, "logs");
    public static readonly string LogPath = Path.Combine(LogDir, $"session-{DateTime.Now:yyyyMMdd-HHmmss}.log");
    static readonly object Gate = new();

    /// <summary>Appends one line, prefixed with a millisecond timestamp. Never throws - logging
    /// itself going wrong (a locked file, a missing directory) must never take down the app it's
    /// trying to help debug.</summary>
    public static void Write(string message)
    {
        try
        {
            Directory.CreateDirectory(LogDir);
            lock (Gate)
                File.AppendAllText(LogPath, $"[{DateTime.Now:HH:mm:ss.fff}] {message}{Environment.NewLine}");
        }
        catch
        {
            // Logging must never itself be a source of crashes.
        }
    }

    /// <summary>Full exception detail (message, type, stack trace, and any inner exceptions) in
    /// one call - use this instead of just ex.Message whenever an exception is caught anywhere,
    /// so the log file alone is enough to diagnose it without reproducing the bug again.</summary>
    public static void WriteException(string context, Exception ex)
    {
        var lines = new List<string> { $"{context}: {ex.GetType().Name}: {ex.Message}" };
        var inner = ex.InnerException;
        while (inner != null)
        {
            lines.Add($"  caused by {inner.GetType().Name}: {inner.Message}");
            inner = inner.InnerException;
        }
        lines.Add(ex.StackTrace ?? "(no stack trace)");
        Write(string.Join(Environment.NewLine, lines));
    }
}
