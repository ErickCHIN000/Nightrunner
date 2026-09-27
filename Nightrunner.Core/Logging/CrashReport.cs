using System.Reflection;
using System.Text;
using Nightrunner.Core.Games;

namespace Nightrunner.Core.Logging;

/// <summary>
/// What gets written when something goes unhandled: an error line in the <see cref="Log"/> and a crash file beside
/// settings.json with the exception, what the app was doing, the process and the log's tail.
/// </summary>
public static class CrashReport
{
    /// <summary>Where crash files go; the settings folder unless a test points it elsewhere.</summary>
    public static string Folder { get; set; } = Path.GetDirectoryName(GameSettings.FilePath)!;

    public static string Version { get; } =
        Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "?";

    /// <summary>Version, bitness, runtime and working set: logged at startup and repeated in every crash file.</summary>
    public static string Process =>
        $"Nightrunner {Version} · {(Environment.Is64BitProcess ? "x64" : "x86")} · .NET {Environment.Version} · " +
        $"working set {Environment.WorkingSet / (1024 * 1024)} MB";

    /// <summary>Log <paramref name="e"/> and write a crash file; returns its path, or null when it could not be written.</summary>
    /// <param name="kind">Which hook caught it: "ui", "unhandled", "task".</param>
    /// <param name="context">What was active: panel, job, game.</param>
    public static string? Write(string kind, Exception e, string? context)
    {
        Log.Error("crash", $"{kind}: {e.GetType().Name}: {e.Message}" + (context is { Length: > 0 } ? $" · {context}" : ""));
        var text = new StringBuilder()
            .AppendLine($"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz} {kind}")
            .AppendLine(Process)
            .AppendLine(context ?? "")
            .AppendLine()
            .AppendLine(e.ToString())
            .AppendLine()
            .AppendLine("log:");
        foreach (var entry in Log.Recent.TakeLast(100))
            text.AppendLine($"{entry.TimeText} {entry.Level} {entry.Source} {entry.Message}");
        try
        {
            Directory.CreateDirectory(Folder);
            string path = Path.Combine(Folder, $"crash-{DateTime.Now:yyyyMMdd-HHmmss-fff}.txt");
            File.WriteAllText(path, text.ToString());
            Log.Error("crash", $"written to {path}");
            return path;
        }
        catch (Exception io) when (io is IOException or UnauthorizedAccessException)
        {
            Log.Error("crash", $"crash file not written: {io.Message}");
            return null;
        }
    }
}
