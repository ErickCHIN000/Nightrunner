using System.Text;
using System.Windows.Controls;
using Nightrunner.Core.Rpack;

namespace Nightrunner.UI.Views.Inspectors;

/// <summary>A pack: its header, and the storage table that drives every offset in it.</summary>
public partial class PackInspector : UserControl
{
    public PackInspector() => InitializeComponent();

    public PackInspector With(Selected.Pack selection)
    {
        var entry = selection.Entry;
        if (entry.Pack is not { } pack)
        {
            Info.Text = $"{entry.Label}\n{entry.Error ?? "not indexed yet"}\n{entry.Path}";
            Storages.Text = "";
            StorageHeader.Text = "storage table";
            return this;
        }

        var h = pack.Header;
        var sb = new StringBuilder();
        sb.AppendLine(entry.Label);
        sb.AppendLine($"RP6L v{h.Version}   field08 0x{h.Field08:X8}{(h.OnDemand ? " (on-demand)" : "")}   " +
                      $"flags 0x{h.Flags:X8}");
        sb.AppendLine($"logical {h.LogicalCount:N0}   physical {h.PhysicalCount:N0}   storages {h.StorageCount}   " +
                      $"names {h.NameCount:N0} / {Format.Size(h.NameBytes)}");
        sb.AppendLine($"tables end 0x{pack.TableEnd:X}   file {Format.Size(pack.Length)}");
        sb.Append(pack.Path);
        Info.Text = sb.ToString();

        StorageHeader.Text = $"storage table ({h.StorageCount})";
        Storages.Text = Table(pack);
        return this;
    }

    private static string Table(RpackFile pack)
    {
        var sb = new StringBuilder();
        sb.AppendLine("  #  type                       align  method  codec  ver  flags  count       size          base");
        for (int i = 0; i < pack.Storages.Length; i++)
        {
            var s = pack.Storages[i];
            sb.AppendLine($"{i,3}  {ResTypes.Label(s.Type),-24}  {s.Alignment,5}  {s.Method,6}  {s.Codec,5}  " +
                          $"{s.FormatVersion,3}  0x{s.Flags:X2}   {s.Count,6:N0}  {s.Size,14:N0}  0x{s.BaseOffset:X}");
        }
        return sb.ToString();
    }
}
