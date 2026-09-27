using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Nightrunner.Core.Games;

/// <summary>Whether <c>bin\x64\dxgi.dll</c> is NightrunnerProxy's, or somebody else's.</summary>
public enum RuntimeFlavor
{
    None,

    /// <summary>NightrunnerProxy: a native proxy that boots <c>Nightrunner\Core.dll</c>; content in <c>Nightrunner\mods</c>.</summary>
    Proxy,
}

/// <summary>What was found next to a game's executable.</summary>
/// <param name="Found">NightrunnerProxy is installed with every file it needs and can run for this game.</param>
/// <param name="DllPath">The proxy, when <c>dxgi.dll</c> is NightrunnerProxy's.</param>
public sealed record RuntimeCheck(bool Found, string? DllPath, long Bytes, DateTimeOffset? Modified,
                                  string? LogPath, string Summary)
{
    public RuntimeFlavor Flavor { get; init; }

    /// <summary>The last game start in <c>logs\boot.log</c>, when it failed; null otherwise.</summary>
    public RuntimeBoot? Boot { get; init; }
}

/// <summary>One path of the runtime layout and what is there: <c>ok</c>, <c>absent</c> (optional or nothing installed),
/// <c>missing</c> (needed), <c>foreign</c> (a <c>dxgi.dll</c> that is not Nightrunner's), <c>broken</c> (a
/// <c>nightrunner.json</c> the runtime refuses).</summary>
public sealed record RuntimePath(string Label, string? Path, string State)
{
    public bool Bad => State is "missing" or "broken";
}

/// <summary>What is installed for Nightrunner Runtime Modding, path by path, for one game.</summary>
/// <param name="Layout"><c>proxy</c> (NightrunnerProxy) or <c>none</c>.</param>
public sealed record RuntimeReport(RuntimeCheck Check, string Layout, IReadOnlyList<RuntimePath> Paths);

/// <summary>
/// A failed game start, read from NightrunnerProxy's <c>logs\boot.log</c>. <paramref name="BeforeCore"/>: the native
/// bootstrap gave up before .NET reached <c>Core.EntryPoint.Initialize</c> (no <c>nightrunner.log</c> for that start),
/// <paramref name="Log"/> is <c>boot.log</c>; otherwise Core ran and returned a failure, <paramref name="Log"/> is
/// <c>nightrunner.log</c>.
/// </summary>
public sealed record RuntimeBoot(string Log, DateTime? Time, string Failure, bool BeforeCore)
{
    /// <summary>UI text: <c>boot failed: &lt;reason&gt;</c> or <c>core failed: &lt;status&gt;</c>.</summary>
    public string Text => $"{(BeforeCore ? "boot" : "core")} failed: {Failure}";
}

/// <summary>
/// Nightrunner Runtime Modding — NightrunnerProxy, the separate runtime component that loads what this tool builds.
/// This project does not ship it or talk to it; it only checks whether it is installed, by content rather than by file
/// name (a ReShade or other <c>dxgi.dll</c> is not the runtime). Reads only.
/// </summary>
/// <remarks>
/// NightrunnerProxy rc1 (<c>deploy.ps1</c>, <c>Proxy/DotNetBootstrap.ixx</c>, <c>Core/Boot.cs</c>): the native proxy ships as
/// <c>bin\x64\dxgi.dll</c> — the only proxy name — and carries the UTF-16 path <c>Nightrunner\Core.dll</c> it boots.
/// Required beside it in <c>bin\x64\Nightrunner</c>: <c>Core.dll</c>, <c>Core.runtimeconfig.json</c>,
/// <c>Nightrunner.Api.dll</c> and the game's module (<see cref="GameProfile.RuntimeModule"/>: <c>DLTB.dll</c>; no other game
/// has one). <c>Core.deps.json</c> is optional. The bootstrap appends to <c>logs\boot.log</c> (rolled to <c>boot.prev.log</c>
/// past 1 MiB), Core writes <c>logs\nightrunner.log</c> (the previous run in <c>nightrunner.prev.log</c>). DLLs of the removed
/// first runtime (winmm.dll and the like) are not looked at. With runtime modding off, nothing the runtime would load (see
/// <see cref="RuntimeContent"/>) is shown anywhere in the app.
/// </remarks>
public static partial class RuntimeModding
{
    /// <summary>The one name NightrunnerProxy's native proxy ships as, beside the game's executable.</summary>
    public const string ProxyName = "dxgi.dll";

    /// <summary>The log Core writes, in <c>Nightrunner\logs</c>.</summary>
    public const string LogName = "nightrunner.log";

    /// <summary>The log the native bootstrap appends to, in <c>Nightrunner\logs</c>.</summary>
    public const string BootLogName = "boot.log";

    /// <summary>What NightrunnerProxy's native proxy boots (UTF-16 in the DLL).</summary>
    public const string ProxyMarker = @"Nightrunner\Core.dll";

    /// <summary>Anything larger is not a proxy DLL; it is not read.</summary>
    private const long MaxDllBytes = 64L << 20;

    /// <summary>True when NightrunnerProxy has a module for this game.</summary>
    public static bool HasModule(GameProfile profile) => profile.RuntimeModule is not null;

    /// <summary>
    /// The files in <c>Nightrunner\</c> the runtime needs to start (label, file name), then the optional
    /// <c>Core.deps.json</c>. Empty for a game with no module.
    /// </summary>
    public static IReadOnlyList<(string Label, string File, bool Required)> Files(GameProfile profile) =>
        profile.RuntimeModule is not { } module ? [] :
        [
            ("core", "Core.dll", true),
            ("config", "Core.runtimeconfig.json", true),
            ("api", "Nightrunner.Api.dll", true),
            ("module", module + ".dll", true),
            ("deps", "Core.deps.json", false),
        ];

    public static RuntimeFlavor Identify(string dll)
    {
        var info = new FileInfo(dll);
        if (!info.Exists || info.Length > MaxDllBytes) return RuntimeFlavor.None;
        byte[] bytes;
        try { bytes = File.ReadAllBytes(dll); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return RuntimeFlavor.None; }
        return bytes.AsSpan().IndexOf(Encoding.Unicode.GetBytes(ProxyMarker)) >= 0 ? RuntimeFlavor.Proxy : RuntimeFlavor.None;
    }

    public static RuntimeCheck Validate(GameInstall install)
    {
        if (!HasModule(install.Profile))
            return new RuntimeCheck(false, null, 0, null, null, RuntimeContent.NoModule(install.Id));
        var exe = install.Exe();
        if (exe is null)
            return new RuntimeCheck(false, null, 0, null, null,
                                    "no game executable found — set the game path first");

        string exeName = Path.GetFileName(exe);
        string dll = Path.Combine(Path.GetDirectoryName(exe)!, ProxyName);
        if (!File.Exists(dll))
            return new RuntimeCheck(false, null, 0, null, null, $"no Nightrunner runtime next to {exeName}");
        if (Identify(dll) == RuntimeFlavor.None)
            return new RuntimeCheck(false, null, 0, null, null, $"{ProxyName} next to {exeName} is not Nightrunner's");

        var info = new FileInfo(dll);
        string folder = install.Paths.RuntimeFolder;
        var missing = Files(install.Profile).Where(f => f.Required && !File.Exists(Path.Combine(folder, f.File)))
                                            .Select(f => $"{RuntimeContent.RuntimeFolderName}\\{f.File}").ToList();
        string log = Path.Combine(folder, "logs", LogName);
        return new RuntimeCheck(missing.Count == 0, dll, info.Length, info.LastWriteTime, File.Exists(log) ? log : null,
            $"{ProxyName} (Nightrunner proxy) next to {exeName}" + (missing.Count == 0 ? "" : $", but no {string.Join(", ", missing)}"))
        {
            Flavor = RuntimeFlavor.Proxy,
            Boot = LastBoot(Path.Combine(folder, "logs", BootLogName), log),
        };
    }

    /// <summary>
    /// The installed runtime, path by path: <c>dxgi.dll</c>, NightrunnerProxy's folder and each file it needs
    /// (<see cref="Files"/>), <c>mods</c>, <c>nightrunner.json</c>, and both logs. Nothing for a game with no module. Reads only.
    /// </summary>
    public static RuntimeReport Describe(GameInstall install, RuntimeCheck? check = null)
    {
        check ??= Validate(install);
        if (!HasModule(install.Profile)) return new RuntimeReport(check, "none", []);
        var p = install.Paths;
        var list = new List<RuntimePath>();
        string Has(string path, bool needed) => File.Exists(path) || Directory.Exists(path) ? "ok" : needed ? "missing" : "absent";
        bool proxy = check.Flavor == RuntimeFlavor.Proxy;

        string dll = Path.Combine(p.Bin, ProxyName);
        list.Add(new("dll", dll, proxy ? "ok" : File.Exists(dll) ? "foreign" : "absent"));
        string folder = p.RuntimeFolder;
        list.Add(new("folder", folder, Has(folder, proxy)));
        foreach (var (label, file, required) in Files(install.Profile))
            list.Add(new(label, Path.Combine(folder, file), Has(Path.Combine(folder, file), proxy && required)));
        list.Add(new("mods", Path.Combine(folder, RuntimeContent.ModsFolderName), Has(Path.Combine(folder, RuntimeContent.ModsFolderName), false)));
        string settings = Path.Combine(folder, RuntimeContent.SettingsName);
        list.Add(new("settings", settings, !File.Exists(settings) ? "absent" : RuntimeSettingsFile.Load(settings).IsBroken ? "broken" : "ok"));
        string log = Path.Combine(folder, "logs", LogName);
        list.Add(new("log", log, Has(log, false)));
        string boot = Path.Combine(folder, "logs", BootLogName);
        list.Add(new("bootlog", boot, Has(boot, false)));
        return new RuntimeReport(check, proxy ? "proxy" : "none", list);
    }

    // ---- boot.log ---------------------------------------------------------------------------------------------------

    /// <summary>
    /// The last game start in <paramref name="bootLog"/>, when it failed. The bootstrap (<c>DotNetBootstrap.ixx</c>) writes
    /// <c>&lt;time&gt; [&lt;pid&gt;] proxy &lt;dll&gt; in &lt;exe&gt;</c> when the game starts, then, for that pid, either
    /// <c>boot failed: &lt;reason&gt;; ...</c> before .NET is up, or <c>runtime started, calling Core.EntryPoint.Initialize</c>
    /// and <c>Core.EntryPoint.Initialize returned &lt;n&gt;</c>. Only that start is read (other processes' lines have another
    /// pid). Null when there is no log, the start succeeded, or it has no outcome yet (still booting, or cut short).
    /// </summary>
    public static RuntimeBoot? LastBoot(string bootLog, string coreLog)
    {
        string text;
        try
        {
            if (!File.Exists(bootLog)) return null;
            using var fs = new FileStream(bootLog, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(fs, new UTF8Encoding(false));
            text = reader.ReadToEnd();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }

        var lines = text.Split('\n').Select(l => BootLineRx().Match(l.TrimEnd('\r'))).Where(m => m.Success).ToList();
        int start = lines.FindLastIndex(m => m.Groups["msg"].Value.StartsWith("proxy ", StringComparison.Ordinal));
        if (start < 0) return null;
        string pid = lines[start].Groups["pid"].Value;
        DateTime? time = DateTime.TryParseExact(lines[start].Groups["time"].Value, "yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture,
                                                DateTimeStyles.AssumeLocal, out var t) ? t : null;
        var session = lines.Skip(start + 1).Where(m => m.Groups["pid"].Value == pid).Select(m => m.Groups["msg"].Value).ToList();

        bool coreReached = session.Contains("runtime started, calling Core.EntryPoint.Initialize");
        const string failed = "boot failed: ", returned = "Core.EntryPoint.Initialize returned ";
        if (!coreReached && session.FirstOrDefault(s => s.StartsWith(failed, StringComparison.Ordinal)) is { } f)
        {
            string reason = f[failed.Length..];
            int cut = reason.IndexOf("; ", StringComparison.Ordinal);
            return new RuntimeBoot(bootLog, time, cut >= 0 ? reason[..cut] : reason, BeforeCore: true);
        }
        if (coreReached && session.FirstOrDefault(s => s.StartsWith(returned, StringComparison.Ordinal)) is { } r &&
            int.TryParse(r[returned.Length..].Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int status) && status != 0)
        {
            // Core/EntryPoint.cs
            string what = status switch { 1 => "not the game", 2 => "module missing", 3 => "module failed", -1 => "Core threw", _ => $"status {status}" };
            return new RuntimeBoot(coreLog, time, what, BeforeCore: false);
        }
        return null;
    }

    [GeneratedRegex(@"^(?<time>\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3}) \[(?<pid>\d+)\] (?<msg>.*)$")]
    private static partial Regex BootLineRx();
}
