using System.Diagnostics;

namespace Nightrunner.Core.Logging;

public enum LogLevel
{
    Info,
    Warn,
    Error,
}

/// <summary>One line in the log.</summary>
/// <param name="Source">Where it came from — "export", "project", "texture", …</param>
/// <param name="Duration">Set on the closing line of an operation.</param>
public readonly record struct LogEntry(DateTimeOffset Time, LogLevel Level, string Source, string Message,
                                       TimeSpan? Duration)
{
    public string TimeText => Time.ToString("HH:mm:ss");

    public string DurationText => Duration is { } d
        ? d.TotalSeconds >= 1 ? $"{d.TotalSeconds:0.00} s" : $"{d.TotalMilliseconds:0} ms"
        : "";
}

/// <summary>
/// Where everything the app does gets written: exports, project changes, failures. The Log window subscribes to
/// <see cref="Written"/>; nothing else needs to know a window exists.
/// </summary>
/// <remarks>
/// A ring buffer of the last <see cref="Capacity"/> entries is kept so a window opened later still shows history.
/// Safe to call from any thread — <see cref="Written"/> is raised on the calling thread, so subscribers marshal.
/// </remarks>
public static class Log
{
    public const int Capacity = 5000;

    private static readonly object Gate = new();
    private static readonly Queue<LogEntry> Buffer = new(Capacity);

    /// <summary>Raised for every entry, on whichever thread wrote it.</summary>
    public static event Action<LogEntry>? Written;

    public static LogEntry[] Recent
    {
        get
        {
            lock (Gate) return Buffer.ToArray();
        }
    }

    public static void Info(string source, string message) => Write(LogLevel.Info, source, message, null);
    public static void Warn(string source, string message) => Write(LogLevel.Warn, source, message, null);
    public static void Error(string source, string message) => Write(LogLevel.Error, source, message, null);

    public static void Write(LogLevel level, string source, string message, TimeSpan? duration)
    {
        var entry = new LogEntry(DateTimeOffset.Now, level, source, message, duration);
        lock (Gate)
        {
            if (Buffer.Count == Capacity) Buffer.Dequeue();
            Buffer.Enqueue(entry);
        }
        Written?.Invoke(entry);
    }

    public static void Clear()
    {
        lock (Gate) Buffer.Clear();
    }

    /// <summary>
    /// Log the start of something and, when the scope is disposed, how it ended and how long it took. Set
    /// <see cref="Operation.Result"/> before disposing to say what happened.
    /// </summary>
    public static Operation Start(string source, string what) => new(source, what);

    public sealed class Operation : IDisposable
    {
        private readonly Stopwatch _sw = Stopwatch.StartNew();
        private readonly string _source;
        private readonly string _what;
        private bool _done;

        internal Operation(string source, string what, bool silent = false)
        {
            _source = source;
            _what = what;
            _done = silent;
            if (!silent) Info(source, $"{what}: start");
        }

        /// <summary>An operation that writes nothing: for callers whose work logs its own.</summary>
        public static Operation None => new("", "", silent: true);

        /// <summary>What the closing line should say; defaults to "done".</summary>
        public string Result { get; set; } = "done";

        public LogLevel Level { get; set; } = LogLevel.Info;

        public void Failed(string why)
        {
            Result = why;
            Level = LogLevel.Error;
        }

        public void Dispose()
        {
            if (_done) return;
            _done = true;
            Write(Level, _source, $"{_what}: {Result}", _sw.Elapsed);
        }
    }
}
