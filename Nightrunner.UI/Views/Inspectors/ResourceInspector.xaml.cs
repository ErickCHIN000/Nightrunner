using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using Nightrunner.Core.Logging;
using Nightrunner.Core.Rpack;

namespace Nightrunner.UI.Views.Inspectors;

/// <summary>One resource: what it is, the parts it is made of, and the bytes of whichever part is picked.</summary>
public partial class ResourceInspector : UserControl
{
    private const int HexChunk = 4096;

    private RpackFile? _pack;
    private PartRow? _hexPart;
    private int _hexShown;

    public ResourceInspector() => InitializeComponent();

    /// <summary>Show one resource.</summary>
    public ResourceInspector With(Selected.Resource selection)
    {
        var (entry, index) = selection.Catalog.Split(selection.Gid);
        var pack = entry.Pack!;
        _pack = pack;
        var lg = pack.Logicals[index];

        var sb = new StringBuilder();
        sb.AppendLine(pack.Name(index));
        sb.AppendLine($"{entry.Label}   gid {selection.Gid:N0}   logical #{index:N0}");
        sb.AppendLine($"type {ResTypes.Label(lg.Type)}   flags 0x{lg.Flags:X2}   parts {lg.PartCount}   " +
                      $"{Format.Size(pack.ResourceSize[index])}");
        int first = (int)lg.FirstPart;
        if (lg.PartCount > 0)
            sb.AppendLine($"first physical #{first:N0}   owner #{pack.Physicals[first].Owner:N0}   " +
                          $"name index {lg.NameIndex:N0}");
        sb.Append(pack.Path);
        Detail.Text = sb.ToString();

        var parts = new List<PartRow>(lg.PartCount);
        for (int k = 0; k < lg.PartCount; k++) parts.Add(new PartRow(pack, k, first + k));
        Parts.ItemsSource = parts;
        Parts.SelectedIndex = parts.Count > 0 ? 0 : -1;
        return this;
    }

    /// <summary>Show the totals of a multiple selection — there is nothing to open.</summary>
    public ResourceInspector With(Selected.Resources selection)
    {
        long bytes = 0;
        int parts = 0;
        foreach (int gid in selection.Gids)
        {
            var (entry, index) = selection.Catalog.Split(gid);
            bytes += entry.Pack!.ResourceSize[index];
            parts += entry.Pack.Logicals[index].PartCount;
        }
        Detail.Text = $"{selection.Gids.Length:N0} resources selected\n{parts:N0} parts · {Format.Size(bytes)}";
        Parts.ItemsSource = null;
        SetHex(null);
        return this;
    }

    // ---- bytes ---------------------------------------------------------------------------------------------

    private void Parts_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        SetHex(Parts.SelectedItem as PartRow);

    private void SetHex(PartRow? part)
    {
        _hexPart = part;
        _hexShown = 0;
        if (part is null)
        {
            Hex.Text = "";
            HexHeader.Text = "bytes";
            return;
        }
        ShowMore(HexChunk);
    }

    private void HexMore_Click(object sender, RoutedEventArgs e) => ShowMore(HexChunk);

    private void ShowMore(int more)
    {
        if (_hexPart is not { } part || _pack is not { } pack) return;
        long size = part.Physical.Size;
        if (pack.PartUnreadableReason(part.PhysIndex) is { } why)
        {
            Hex.Text = $"no payload here: {why}";
            HexHeader.Text = $"part {part.Ordinal} {ResTypes.Name(part.Storage.Type)}";
            return;
        }
        int want = (int)Math.Min(size, _hexShown + more);
        if (want == _hexShown && _hexShown > 0) return;
        try
        {
            var data = pack.ReadPart(part.PhysIndex, 0, want);
            _hexShown = data.Length;
            Hex.Text = Format.HexDump(data, part.Offset);
            HexHeader.Text = $"part {part.Ordinal} {ResTypes.Name(part.Storage.Type)} · " +
                             $"{_hexShown:N0} of {size:N0} bytes at 0x{part.Offset:X}";
        }
        catch (Exception ex) when (ex is RpackFormatException or IOException)
        {
            Hex.Text = $"{ex.GetType().Name}: {ex.Message}";
        }
    }

    /// <summary>Write the selected part's bytes out on their own.</summary>
    private void SavePart_Click(object sender, RoutedEventArgs e)
    {
        if (_hexPart is not { } part || _pack is not { } pack) return;
        var dlg = new SaveFileDialog
        {
            Title = "Save this part",
            FileName = $"{RawPartName(part)}",
            Filter = "Binary (*.bin)|*.bin|All files (*.*)|*.*",
        };
        if (dlg.ShowDialog(Window.GetWindow(this)) != true) return;
        using var op = Log.Start("export", $"save part {part.Ordinal} -> {dlg.FileName}");
        try
        {
            using var source = pack.OpenRange(part.Offset, part.Physical.Size);
            using var target = File.Create(dlg.FileName);
            source.CopyTo(target, 1 << 20);
            op.Result = $"{part.Physical.Size:N0} bytes";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or RpackFormatException)
        {
            op.Failed(ex.Message);
        }
    }

    private static string RawPartName(PartRow part) =>
        Nightrunner.Core.Export.RawExporter.PartFileName(part.Storage.Type, 0);
}
