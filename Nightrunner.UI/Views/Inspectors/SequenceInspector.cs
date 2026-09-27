using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Nightrunner.Core.Anim;

namespace Nightrunner.UI.Views.Inspectors;

public sealed record SequenceEventRow(string Frame, int Id, string Actions, int Param, string Tip);

/// <summary>
/// A SeqTrack: its bank, clip, fps, frame range, default mode and blend, and its events — frame (time/5), id, the
/// actions they run (from the bank's action lists) and the parameter. A first event that the engine reads as a
/// negative time (u16 ≥ 0x8000) is flagged: it and every later event of the track never fire.
/// </summary>
public sealed class SequenceInspector : UserControl
{
    private readonly TextBox _info = new() { IsReadOnly = true, Height = 110, Margin = new Thickness(0, 0, 0, 6), VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly ListView _events = new();
    private SeqEntry? _shown;

    public SequenceInspector()
    {
        _info.SetResourceReference(StyleProperty, "Mono");
        var grid = new GridView();
        grid.Columns.Add(Column("frame", nameof(SequenceEventRow.Frame), 60, "CellNum"));
        grid.Columns.Add(Column("id", nameof(SequenceEventRow.Id), 60, "CellNum"));
        grid.Columns.Add(Column("actions", nameof(SequenceEventRow.Actions), 300, "CellName"));
        grid.Columns.Add(Column("param", nameof(SequenceEventRow.Param), 60, "CellNum"));
        _events.View = grid;
        var root = new DockPanel();
        DockPanel.SetDock(_info, Dock.Top);
        root.Children.Add(_info);
        root.Children.Add(_events);
        Content = root;
    }

    private static GridViewColumn Column(string header, string path, double width, string style)
    {
        var text = new FrameworkElementFactory(typeof(TextBlock));
        text.SetBinding(TextBlock.TextProperty, new Binding(path));
        text.SetBinding(ToolTipProperty, new Binding(nameof(SequenceEventRow.Tip)));
        text.SetResourceReference(StyleProperty, style);
        return new GridViewColumn { Header = header, Width = width, CellTemplate = new DataTemplate { VisualTree = text } };
    }

    public SequenceInspector With(Selected.Sequence what)
    {
        if (ReferenceEquals(_shown, what.Entry)) return this;
        _shown = what.Entry;
        var r = what.Entry.Record;
        var inv = CultureInfo.InvariantCulture;
        var info = new StringBuilder();
        info.AppendLine($"{what.Entry.Bank}.scr@{r.Name}   ({what.Entry.Pack})");
        info.AppendLine(r.IsPlaceholder ? "clip     none (placeholder)"
            : $"clip     {r.Anm2Name}   ({what.Animations.ClipOf(r.Anm2Name)?.Label ?? "not shipped"})");
        info.AppendLine($"fps {r.Fps.ToString("0.##", inv)} · frames {r.StartFrame.ToString("0.##", inv)}–{r.EndFrame.ToString("0.##", inv)}" +
                        (r.Reverse ? " (reverse)" : "") + $" · mode {r.DefaultMode} · blend {r.DefaultBlend.ToString("0.###", inv)}");
        info.Append($"events {r.Events.Count}");
        if (r.FirstEventNegative) info.Append(" · first event reads as a negative time: no event of this track fires");
        _info.Text = info.ToString();
        _events.ItemsSource = r.Events.Select(e =>
        {
            string actions = "";
            if (e.ActionList >= 0 && e.ActionList < what.Entry.Source.ActionLists.Count)
                actions = string.Join("; ", what.Entry.Source.ActionLists[e.ActionList].Select(a => $"{a.Name}({string.Join(", ", a.Args)})"));
            return new SequenceEventRow(e.Negative ? $"({e.SignedTime5 * 0.2f:0.#})" : (e.Frame).ToString("0.#", inv), e.Id, actions, e.Param,
                                        e.Negative ? "negative time: never fires" : actions);
        }).ToList();
        return this;
    }
}
