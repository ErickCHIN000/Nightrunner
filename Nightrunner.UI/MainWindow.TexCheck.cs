using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using AvalonDock.Layout;
using Nightrunner.Core.Games;
using Nightrunner.Core.Logging;
using Nightrunner.UI.Views;

namespace Nightrunner.UI;

/// <summary>
/// The viewport texture check (<c>NIGHTRUNNER_TEXCHECK=&lt;folder&gt;</c>): shows each named model (or mesh) of each game in
/// the viewport, textures on, and writes a capture of the 3D view plus the viewport's warnings for that load.
/// <c>NIGHTRUNNER_TEXCHECK_ITEMS</c> lists them as <c>dltb=a.model,mesh:b;dl2=c.model</c>.
/// </summary>
public sealed partial class MainWindow
{
    public async Task RunTexCheck(string folder)
    {
        Directory.CreateDirectory(folder);
        var report = new List<string>();
        string spec = Environment.GetEnvironmentVariable("NIGHTRUNNER_TEXCHECK_ITEMS") ?? "";
        foreach (var part in spec.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = part.Split('=', 2);
            if (kv.Length != 2 || !GameInstall.FindInstalls().TryGetValue(kv[0].Trim(), out var install)) continue;
            ApplyView("Meshes");
            OpenWorkspace(() => Task.CompletedTask);
            await _workspace.OpenGame(install);
            GameLabel.Text = _workspace.Title;
            await _workspace.WaitLoaded();
            var models = await Task.Run(() => _workspace.Models);
            Open("viewport");
            await WaitRender();
            var view = (ViewportView)_open["viewport"].Content;
            foreach (var raw in kv[1].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                int mark = Log.Recent.Length;
                string before = view.StatusText;
                if (raw.StartsWith("mesh:"))
                    Reveal("meshes", raw[5..]);
                else if (models?.Models.FirstOrDefault(m => m.Wins && m.Basename.Equals(raw, StringComparison.OrdinalIgnoreCase)) is { } entry)
                    Reveal("models", entry.Name);
                else
                {
                    report.Add($"{install.Id} {raw}: not found");
                    continue;
                }
                ((LayoutContent)_open["viewport"]).IsSelected = true;
                await Until(() => view.StatusText != before && view.StatusText.Contains(" ms") || view.View.RenderError is not null, 60000);
                await Task.Delay(1500);
                string name = $"{install.Id}_{Path.GetFileNameWithoutExtension(raw.Replace("mesh:", "mesh_"))}";
                if (PresentationSource.FromVisual(view) is HwndSource src)
                {
                    var bmp = Capture(src.Handle, out var origin);
                    var tl = view.View.PointToScreen(new Point(0, 0));
                    var br = view.View.PointToScreen(new Point(view.View.ActualWidth, view.View.ActualHeight));
                    var rect = new Int32Rect((int)(tl.X - origin.X), (int)(tl.Y - origin.Y), (int)(br.X - tl.X), (int)(br.Y - tl.Y));
                    rect.Width = Math.Min(rect.Width, bmp.PixelWidth - rect.X);
                    rect.Height = Math.Min(rect.Height, bmp.PixelHeight - rect.Y);
                    using var fs = File.Create(Path.Combine(folder, name + ".png"));
                    var enc = new PngBitmapEncoder();
                    enc.Frames.Add(BitmapFrame.Create(rect.Width > 0 && rect.Height > 0 ? new CroppedBitmap(bmp, rect) : bmp));
                    enc.Save(fs);
                }
                var notes = Log.Recent.Skip(mark).Where(e => e.Source == "viewport").Select(e => $"{e.Level} {e.Message} {e.DurationText}");
                File.WriteAllLines(Path.Combine(folder, name + ".txt"), notes);
                report.Add($"{install.Id} {raw}: status '{view.StatusText}' error {view.View.RenderError ?? "none"}");
            }
        }
        File.WriteAllLines(Path.Combine(folder, "report.txt"), report);
        Close();
    }
}
