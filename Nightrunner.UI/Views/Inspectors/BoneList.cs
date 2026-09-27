using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Nightrunner.Core.Model;

namespace Nightrunner.UI.Views.Inspectors;

public sealed record BoneRow(int Index, string Name, string Parent, string Source, string Override);

/// <summary>
/// A skeleton's bones: index, name, parent, who supplied the rest pose, and by how much it moved off the skeleton.
/// Selecting a row selects the bone everywhere (the Viewport highlights it); a bone clicked in the Viewport is selected here.
/// </summary>
public sealed class BoneList : ListView
{
    private Workspace? _ws;
    private bool _syncing;

    public BoneList()
    {
        SetResourceReference(StyleProperty, typeof(ListView));       // a subclass does not pick up the ListView theme on its own
        VirtualizingPanel.SetIsVirtualizing(this, true);
        VirtualizingPanel.SetVirtualizationMode(this, VirtualizationMode.Recycling);
        SelectionMode = SelectionMode.Single;
        var grid = new GridView();
        grid.Columns.Add(Column("#", nameof(BoneRow.Index), 44, "CellNum"));
        grid.Columns.Add(Column("bone", nameof(BoneRow.Name), 190, "CellName"));
        grid.Columns.Add(Column("parent", nameof(BoneRow.Parent), 150, "CellDim"));
        grid.Columns.Add(Column("rest", nameof(BoneRow.Source), 170, "CellDim"));
        grid.Columns.Add(Column("moved", nameof(BoneRow.Override), 110, "CellDim"));
        View = grid;
        SelectionChanged += (_, _) =>
        {
            if (!_syncing && SelectedItem is BoneRow r) _ws?.SelectBone(r.Name);
        };
    }

    private GridViewColumn Column(string header, string path, double width, string style)
    {
        var text = new FrameworkElementFactory(typeof(TextBlock));
        text.SetBinding(TextBlock.TextProperty, new Binding(path));
        text.SetBinding(ToolTipProperty, new Binding(path));
        text.SetResourceReference(StyleProperty, style);
        return new GridViewColumn { Header = header, Width = width, CellTemplate = new DataTemplate { VisualTree = text } };
    }

    public void Attach(Workspace ws)
    {
        _ws = ws;
        ws.BoneSelected += OnBoneSelected;
    }

    public void Show(ModelSkeleton? s)
    {
        if (s is null)
        {
            ItemsSource = null;
            return;
        }
        var moved = s.Overrides.ToDictionary(o => o.Bone, StringComparer.OrdinalIgnoreCase);
        ItemsSource = s.Names.Select((n, i) => new BoneRow(i, n, s.Parents[i] >= 0 ? s.Names[s.Parents[i]] : "",
            s.Source[i], moved.TryGetValue(n, out var o) ? $"{o.Millimetres:0.###} mm {o.Degrees:0.##}°" : "")).ToList();
    }

    private void OnBoneSelected(string name)
    {
        if (ItemsSource is not List<BoneRow> rows) return;
        var hit = rows.FirstOrDefault(r => r.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (hit is null || ReferenceEquals(hit, SelectedItem)) return;
        _syncing = true;
        SelectedItem = hit;
        ScrollIntoView(hit);
        _syncing = false;
    }
}
