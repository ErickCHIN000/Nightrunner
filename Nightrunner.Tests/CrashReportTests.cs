using Nightrunner.Core.Logging;

namespace Nightrunner.Tests;

public class CrashReportTests
{
    [Fact]
    public void WritesExceptionContextAndProcess()
    {
        string folder = Path.Combine(Path.GetTempPath(), "nr-crash-" + Guid.NewGuid().ToString("N"));
        string old = CrashReport.Folder;
        CrashReport.Folder = folder;
        try
        {
            Exception e;
            try { throw new InvalidOperationException("boom"); }
            catch (Exception caught) { e = caught; }

            string path = CrashReport.Write("ui", e, "panel Textures · job none · game dltb")!;

            Assert.Equal(folder, Path.GetDirectoryName(path));
            string text = File.ReadAllText(path);
            Assert.Contains("InvalidOperationException: boom", text);
            Assert.Contains("panel Textures", text);
            Assert.Contains(Environment.Is64BitProcess ? "x64" : "x86", text);
            Assert.Contains("working set", text);
            Assert.Contains(Log.Recent, l => l.Source == "crash" && l.Message.Contains("boom"));
        }
        finally
        {
            CrashReport.Folder = old;
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
        }
    }
}
