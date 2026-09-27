using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using Nightrunner.Core.Logging;
using Nightrunner.Core.Project;

namespace Nightrunner.UI.Views.Inspectors;

/// <summary>A project: its manifest fields, what is in it, and the buttons that act on it.</summary>
public partial class ProjectInspector : UserControl
{
    private readonly Workspace _ws;
    private ModProject? _project;

    public ProjectInspector(Workspace workspace)
    {
        _ws = workspace;
        InitializeComponent();
        _ws.ProjectChanged += () =>
        {
            if (_project is { } shown) ShowMeta(shown);    // "open" follows the workspace, not the moment of selection
        };
    }

    private bool IsOpen(ModProject project) =>
        _ws.Project is { } p && p.Folder.Equals(project.Folder, StringComparison.OrdinalIgnoreCase);

    private void ShowMeta(ModProject project) =>
        Meta.Text = $"{project.Manifest.Game ?? "any game"} · created {project.Manifest.Created:yyyy-MM-dd} · " +
                    $"modified {project.Manifest.Modified:yyyy-MM-dd HH:mm}" + (IsOpen(project) ? " · open" : "");

    public ProjectInspector With(ModProject project)
    {
        _project = project;
        ShowMeta(project);
        NameBox.Text = project.Manifest.Name;
        PathBox.Text = project.Folder;
        NotesBox.Text = project.Manifest.Notes;
        Status.Text = "";
        ShowSummary(project);
        return this;
    }

    private void ShowSummary(ModProject project)
    {
        try
        {
            var scan = project.Scan();
            var (textures, orphans) = TextureAsset.Scan(project.Folder);
            var sb = new StringBuilder();
            sb.AppendLine($"{scan.Files:N0} files · {Format.Size(scan.Bytes)}");
            sb.AppendLine($"{textures.Count} texture(s) ready to build" +
                          (orphans.Count > 0 ? $", {orphans.Count} file(s) without a sidecar" : ""));
            foreach (var group in textures.GroupBy(t => t.Asset.EffectiveFormatName).OrderBy(g => g.Key))
                sb.AppendLine($"  {group.Key,-12} {group.Count()}");
            var c = ProjectAssets.Scan(project);
            if (c.FullBuildItems + c.Problems.Count > 0)
            {
                sb.AppendLine($"{c.FullBuildItems} other item(s)");
                foreach (var (kind, n) in new[] { ("mesh", c.Meshes.Count), ("scene", c.Scenes.Count), ("model", c.Models.Count),
                                                  ("anim", c.Anims.Count), ("prefab", c.Prefabs.Count), ("problem", c.Problems.Count) })
                    if (n > 0) sb.AppendLine($"  {kind,-12} {n}");
            }
            sb.AppendLine();
            foreach (var entry in scan.Entries.Where(e => e.IsDirectory))
                sb.AppendLine($"  {entry.Name,-12} {entry.Files,6:N0} files  {Format.Size(entry.Bytes)}");
            Summary.Text = sb.ToString();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or ProjectException)
        {
            Summary.Text = e.Message;
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_project is not { } p) return;
        p.Manifest.Name = NameBox.Text.Trim();
        p.Manifest.Notes = NotesBox.Text;
        try
        {
            p.Save();
            Log.Info("project", $"saved '{p.Manifest.Name}' ({p.ManifestPath})");
            if (IsOpen(p)) _ws.SetProject(p);
            Status.Text = "saved";
            Status.Foreground = Skin.Brush("FgDim");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Error("project", $"save failed: {ex.Message}");
            Status.Text = ex.Message;
            Status.Foreground = Skin.Brush("Error");
        }
    }

    private void Reveal_Click(object sender, RoutedEventArgs e)
    {
        if (_project is { } p) Shell.Reveal(p.Folder);
    }

    private void Open_Click(object sender, RoutedEventArgs e)
    {
        if (_project is not { } p) return;
        _ws.SetProject(p);
        Log.Info("project", $"opened '{p.Name}' from {p.Folder}");
        With(p);
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        // close the project this inspector shows — not whichever one happens to be open
        if (_project is not { } shown || !IsOpen(shown)) return;
        Log.Info("project", $"closed '{shown.Name}'");
        _ws.SetProject(null);
        Status.Text = "closed";
    }
}
