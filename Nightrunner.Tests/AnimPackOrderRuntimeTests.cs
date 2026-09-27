using Nightrunner.Core.Anim;
using Nightrunner.Core.Games;
using Nightrunner.Core.Rpack;

namespace Nightrunner.Tests;

// On the merged catalog: clip registration follows the same runtime snapshot the pack catalog is built from,
// NightrunnerProxy's mod.json.
public class AnimPackOrderRuntimeTests
{
    [Fact]
    public void ModItemsBeforeAPackAreEarlyAndTheRestLate()
    {
        using var tmp = new TempDir();
        var g = new FakeInstall(tmp);
        g.Mod("m", """
            { "id": "m", "items": [
              { "kind": "rpack", "file": "anim_pc.rpack", "at": "before:common_anims_pc" },
              { "kind": "rpack", "file": "mesh_pc.rpack" } ] }
            """, "anim_pc.rpack", "mesh_pc.rpack");
        g.Mod("off", """{ "id": "off", "items": [ { "kind": "rpack", "file": "off_pc.rpack", "at": "before:common_anims_pc" } ] }""", "off_pc.rpack");
        FakeInstall.Text(Path.Combine(g.Bin, "Nightrunner", RuntimeContent.SettingsName), """{ "mods": [ { "id": "off", "enabled": false } ] }""");
        var order = AnimPackOrder.FromRuntime(RuntimeContent.Read(g.Install));
        Assert.Equal(-1, order.OrderOf("anim_pc.rpack"));
        Assert.Equal(1, order.OrderOf("mesh_pc.rpack"));
        Assert.Null(order.OrderOf("off_pc.rpack"));                  // a disabled mod does not load
    }

    [Fact]
    public void EarlyModPacksRegisterBeforeStockAndLateAfter()
    {
        using var tmp = new TempDir();
        var g = new FakeInstall(tmp);
        g.Mod("m", """
            { "id": "m", "items": [
              { "kind": "rpack", "file": "late_pc.rpack" },
              { "kind": "rpack", "file": "early_pc.rpack", "at": "before:common_anims_pc" } ] }
            """, "late_pc.rpack", "early_pc.rpack");
        FakeInstall.Put(Path.Combine(g.Mods, "stray", "unlisted_pc.rpack"));
        var order = AnimPackOrder.FromRuntime(RuntimeContent.Read(g.Install));
        PackEntry P(int id, string path) => new(id, path, Path.GetFileName(path), 0);
        var packs = new[]
        {
            P(0, Path.Combine(g.Mods, "m", "late_pc.rpack")), P(1, Path.Combine(g.Mods, "m", "early_pc.rpack")),
            P(2, Path.Combine(g.Mods, "stray", "unlisted_pc.rpack")), P(3, Path.Combine(g.Assets, "common_anims_pc.rpack")),
            P(4, Path.Combine(g.Assets, "player_anims_pc.rpack")),
        };
        var arranged = AnimPackOrder.Arrange(packs, g.Install.Paths.IsCustom, order).Select(p => p.Label).ToList();
        Assert.Equal(["early_pc.rpack", "common_anims_pc.rpack", "player_anims_pc.rpack", "late_pc.rpack"], arranged);
        // without the runtime's orders the catalog order stands
        Assert.Equal(packs, AnimPackOrder.Arrange(packs, g.Install.Paths.IsCustom, null));
    }
}
