using Nightrunner.Core.Project;

namespace Nightrunner.Tests;

// Output names are remembered per project, stock names are refused, destinations follow runtime modding.
public class OutputNameTests
{
    [Fact]
    public void NamesAreRememberedInTheManifest()
    {
        using var tmp = new TempDir();
        var project = ModProject.Create(tmp.File("p"), "p", "dltb");
        Assert.Null(project.Manifest.RpackName);
        project.SetOutputNames("my_mod.rpack", "data3.pak");

        var reopened = ModProject.Open(project.Folder);
        Assert.Equal(("my_mod.rpack", "data3.pak"), (reopened.Manifest.RpackName, reopened.Manifest.PakName));
    }

    [Fact]
    public void StockAndBadNamesAreRefused()
    {
        using var tmp = new TempDir();
        Assert.NotNull(ProjectBuild.PakRefusal("data0.pak"));
        Assert.NotNull(ProjectBuild.PakRefusal("DATA1.pak"));
        Assert.Null(ProjectBuild.PakRefusal("data2.pak"));
        Assert.NotNull(ProjectBuild.RpackRefusal(@"..\common_meshes_pc.rpack", null));
        Assert.Null(ProjectBuild.RpackRefusal("assets_2_pc.rpack", null));
        Assert.Throws<Nightrunner.Core.Rpack.RpackBuildException>(() =>
            ProjectBuilder.BuildTextures(ModProject.Create(tmp.File("q"), "q", "dltb"), "../x.rpack",
                                         _ => throw new InvalidOperationException(), TestContext.Current.CancellationToken));
    }

    [Fact]
    public void StockPackNamesOfTheInstallAreRefused()
    {
        var install = Installs.Require("dltb");
        Assert.NotNull(ProjectBuild.RpackRefusal("common_meshes_pc.rpack", install));
        Assert.Null(ProjectBuild.RpackRefusal("assets_2_pc.rpack", install));
    }

    [Fact]
    public void DestinationsAreTheStockFoldersOrTheModFolder()
    {
        Assert.Equal(("ph_ft/work/data_platform/pc/assets", "ph_ft/source"), ProjectBuild.Destinations(null));
        Assert.Equal("ph_ft/work/bin/x64/Nightrunner/mods", ProjectBuild.ModsDestination(null));
    }
}
