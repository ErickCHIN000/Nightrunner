using System.Text;
using Nightrunner.Core.Games;

namespace Nightrunner.Tests;

/// <summary>
/// Only NightrunnerProxy counts as installed: <c>bin\x64\dxgi.dll</c> carrying the Core path it boots, plus Core.dll,
/// Core.runtimeconfig.json, Nightrunner.Api.dll and the game's module. Leftovers of the first runtime are ignored.
/// </summary>
public class RuntimeCheckTests
{
    private static byte[] ProxyBytes => [.. "MZ\0\0"u8, .. Encoding.Unicode.GetBytes(@"Nightrunner\Core.dll")];

    [Fact]
    public void OnlyDxgiWithTheMarkerCounts()
    {
        using var tmp = new TempDir();
        var g = new FakeInstall(tmp);
        var none = RuntimeModding.Validate(g.Install);
        Assert.False(none.Found);
        Assert.StartsWith("no Nightrunner runtime next to", none.Summary);

        // leftovers of the removed first runtime (content.ini, winmm.dll): not looked at, not reported
        FakeInstall.Text(Path.Combine(g.Bin, "content.ini"), "[rpacks]\nold = 1\n");
        FakeInstall.Put(Path.Combine(g.Bin, "winmm.dll"), Encoding.ASCII.GetBytes("MZ\0\0--- nightrunner proxy attached\0nightrunner.log"));
        var leftover = RuntimeModding.Validate(g.Install);
        Assert.False(leftover.Found);
        Assert.Equal(none.Summary, leftover.Summary);
        Assert.DoesNotContain(RuntimeModding.Describe(g.Install).Paths, p => p.Path?.EndsWith("winmm.dll") == true);

        // NightrunnerProxy under another proxy name is not the runtime: dxgi.dll is the only one
        FakeInstall.Put(Path.Combine(g.Bin, "winmm.dll"), ProxyBytes);
        FakeInstall.Put(Path.Combine(g.Bin, "d3d11.dll"), ProxyBytes);
        Assert.Equal(RuntimeFlavor.None, RuntimeModding.Validate(g.Install).Flavor);

        // ReShade or the like as dxgi.dll: not ours, said so
        FakeInstall.Put(Path.Combine(g.Bin, "dxgi.dll"), Encoding.ASCII.GetBytes("MZ... ReShade ... dxgi forwarders"));
        var other = RuntimeModding.Validate(g.Install);
        Assert.False(other.Found);
        Assert.Null(other.DllPath);
        Assert.Equal($"dxgi.dll next to {FakeInstall.Exe} is not Nightrunner's", other.Summary);
        Assert.Equal("foreign", RuntimeModding.Describe(g.Install).Paths.Single(p => p.Label == "dll").State);

        // NightrunnerProxy: the marker, then every required file
        FakeInstall.Put(Path.Combine(g.Bin, "dxgi.dll"), ProxyBytes);
        var half = RuntimeModding.Validate(g.Install);
        Assert.False(half.Found);
        Assert.Equal(RuntimeFlavor.Proxy, half.Flavor);
        Assert.Contains(@"but no Nightrunner\Core.dll, Nightrunner\Core.runtimeconfig.json, Nightrunner\Nightrunner.Api.dll, Nightrunner\DLTB.dll",
                        half.Summary);
        foreach (var f in new[] { "Core.dll", "Core.runtimeconfig.json", "Nightrunner.Api.dll" })
            FakeInstall.Put(Path.Combine(g.Bin, "Nightrunner", f));
        var noModule = RuntimeModding.Validate(g.Install);
        Assert.False(noModule.Found);
        Assert.EndsWith(@"but no Nightrunner\DLTB.dll", noModule.Summary);
        FakeInstall.Put(Path.Combine(g.Bin, "Nightrunner", "DLTB.dll"));
        var proxy = RuntimeModding.Validate(g.Install);
        Assert.True(proxy.Found, proxy.Summary);
        Assert.EndsWith("dxgi.dll", proxy.DllPath);
        Assert.Equal($"dxgi.dll (Nightrunner proxy) next to {FakeInstall.Exe}", proxy.Summary);   // winmm/d3d11 not mentioned
    }

    [Theory]
    [InlineData("Core.dll")]
    [InlineData("Core.runtimeconfig.json")]
    [InlineData("Nightrunner.Api.dll")]
    [InlineData("DLTB.dll")]
    public void EachRequiredFileIsNeeded(string file)
    {
        using var tmp = new TempDir();
        var g = new FakeInstall(tmp).Proxy();
        Assert.True(RuntimeModding.Validate(g.Install).Found);
        File.Delete(Path.Combine(g.Bin, "Nightrunner", file));
        var check = RuntimeModding.Validate(g.Install);
        Assert.False(check.Found);
        Assert.EndsWith($@"but no Nightrunner\{file}", check.Summary);
        var row = RuntimeModding.Describe(g.Install, check).Paths.Single(p => p.Path == Path.Combine(g.Bin, "Nightrunner", file));
        Assert.Equal("missing", row.State);
        Assert.True(row.Bad);
    }

    [Fact]
    public void DescribeListsEveryFileWithItsState()
    {
        using var tmp = new TempDir();
        var g = new FakeInstall(tmp);
        var none = RuntimeModding.Describe(g.Install);
        Assert.Equal("none", none.Layout);
        Assert.Equal(["dll", "folder", "core", "config", "api", "module", "deps", "mods", "settings", "log", "bootlog"],
                     none.Paths.Select(p => p.Label));
        Assert.All(none.Paths, p => Assert.Equal("absent", p.State));   // nothing installed: nothing is "missing"

        g.Proxy();
        Dictionary<string, string> States() => RuntimeModding.Describe(g.Install).Paths.ToDictionary(p => p.Label, p => p.State);
        var s = States();
        Assert.Equal(("ok", "ok", "ok", "ok", "ok"), (s["dll"], s["core"], s["config"], s["api"], s["module"]));
        Assert.Equal("absent", s["deps"]);   // optional
        Assert.Equal(("absent", "absent", "absent"), (s["settings"], s["log"], s["bootlog"]));
        FakeInstall.Put(Path.Combine(g.Bin, "Nightrunner", "Core.deps.json"));
        FakeInstall.Text(g.Settings, "");
        FakeInstall.Text(Path.Combine(g.Bin, "Nightrunner", "logs", "boot.log"), "");
        s = States();
        Assert.Equal(("ok", "broken", "ok"), (s["deps"], s["settings"], s["bootlog"]));
        Assert.True(RuntimeModding.Describe(g.Install).Paths.Single(p => p.Label == "settings").Bad);
        Assert.Equal(Path.Combine(g.Bin, "Nightrunner", "logs", "boot.log"),
                     RuntimeModding.Describe(g.Install).Paths.Single(p => p.Label == "bootlog").Path);
    }

    /// <summary>Only DLTB has a runtime module: DL2 and DL1 never claim an installable runtime, whatever is on disk.</summary>
    [Theory]
    [InlineData("dl2")]
    [InlineData("dl1")]
    public void GamesWithoutAModuleHaveNoRuntime(string id)
    {
        var profile = GameProfile.ById(id)!;
        Assert.Null(profile.RuntimeModule);
        Assert.False(RuntimeModding.HasModule(profile));
        using var tmp = new TempDir();
        var g = new FakeInstall(tmp).Proxy();   // a full DLTB-style layout on disk
        var install = new GameInstall(g.Root, profile);
        var check = RuntimeModding.Validate(install);
        Assert.False(check.Found);
        Assert.Equal($"no runtime module for {id}", check.Summary);
        var report = RuntimeModding.Describe(install);
        Assert.Equal("none", report.Layout);
        Assert.Empty(report.Paths);
        var content = RuntimeContent.Read(install);
        Assert.Equal($"no runtime module for {id}", content.NoMods);
        Assert.Empty(content.Mods);
        Assert.Empty(content.Items);

        var settings = new GameSettings();
        settings.SetRuntimeModding(id, true);
        Assert.False(settings.RuntimeModdingFor(id));
        settings.SetRuntimeModding("dltb", true);
        Assert.True(settings.RuntimeModdingFor("dltb"));
        Assert.Equal("DLTB", GameProfile.Dltb.RuntimeModule);
    }

    // ---- boot.log ---------------------------------------------------------------------------------------------------

    private static string BootLog(FakeInstall g, string text)
    {
        string path = Path.Combine(g.Bin, "Nightrunner", "logs", "boot.log");
        FakeInstall.Text(path, text);
        return path;
    }

    private const string Ok =
        "2026-09-27 10:00:00.000 [100] proxy bin\\dxgi.dll in bin\\game.exe\n" +
        "2026-09-27 10:00:00.010 [100] hostfxr dotnet\\host\\fxr\\10.0.0\\hostfxr.dll\n" +
        "2026-09-27 10:00:00.100 [100] runtime started, calling Core.EntryPoint.Initialize\n" +
        "2026-09-27 10:00:00.200 [100] Core.EntryPoint.Initialize returned 0\n";

    [Fact]
    public void ABootThatFailedBeforeCoreIsReported()
    {
        using var tmp = new TempDir();
        var g = new FakeInstall(tmp).Proxy();
        Assert.Null(RuntimeModding.Validate(g.Install).Boot);   // no boot.log
        string path = BootLog(g, Ok);
        Assert.Null(RuntimeModding.Validate(g.Install).Boot);

        // the last start failed natively; a later non-game process (other pid) and an earlier good start do not matter
        BootLog(g, Ok +
            "2026-09-27 11:00:00.000 [200] proxy bin\\dxgi.dll in bin\\game.exe\n" +
            "2026-09-27 11:00:00.001 [300] not the game (CrashReporter.exe), passing through\n" +
            "2026-09-27 11:00:00.005 [200] hostfxr_initialize_for_runtime_config returned 0x80008096\n" +
            "2026-09-27 11:00:00.006 [200] boot failed: .NET runtime initialization failed; the game continues without mods\n" +
            "2026-09-27 11:00:00.007 [200] showing message box: install the .NET 10 Runtime (x64)\n");
        var boot = RuntimeModding.Validate(g.Install).Boot;
        Assert.NotNull(boot);
        Assert.True(boot.BeforeCore);
        Assert.Equal(".NET runtime initialization failed", boot.Failure);
        Assert.Equal("boot failed: .NET runtime initialization failed", boot.Text);
        Assert.Equal(path, boot.Log);
        Assert.Equal(new DateTime(2026, 9, 27, 11, 0, 0), boot.Time);

        // missing files are refused before hostfxr
        BootLog(g, "2026-09-27 12:00:00.000 [7] proxy a in b\r\n2026-09-27 12:00:00.001 [7] boot failed: Nightrunner\\Core.runtimeconfig.json was not found; the game continues without mods\r\n");
        Assert.Equal(@"Nightrunner\Core.runtimeconfig.json was not found", RuntimeModding.Validate(g.Install).Boot!.Failure);

        // Core ran and returned a failure: that is nightrunner.log's story
        BootLog(g, "2026-09-27 13:00:00.000 [8] proxy a in b\n2026-09-27 13:00:00.100 [8] runtime started, calling Core.EntryPoint.Initialize\n" +
                   "2026-09-27 13:00:00.200 [8] Core.EntryPoint.Initialize returned 2\n" +
                   "2026-09-27 13:00:00.201 [8] boot failed: managed entry point returned failure; the game continues without mods\n");
        var core = RuntimeModding.Validate(g.Install).Boot!;
        Assert.False(core.BeforeCore);
        Assert.Equal("core failed: module missing", core.Text);
        Assert.Equal(Path.Combine(g.Bin, "Nightrunner", "logs", "nightrunner.log"), core.Log);

        // a start with no outcome yet (still booting, or cut short) claims nothing
        BootLog(g, Ok + "2026-09-27 14:00:00.000 [9] proxy a in b\n2026-09-27 14:00:00.010 [9] hostfxr c\n");
        Assert.Null(RuntimeModding.Validate(g.Install).Boot);
    }
}
