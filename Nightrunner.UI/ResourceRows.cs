using System.Collections;
using Nightrunner.Core.Rpack;

namespace Nightrunner.UI;

/// <summary>One row of the resource list. Built only for rows the view actually realises.</summary>
public sealed class ResourceRow(RpackCatalog catalog, int gid)
{
    private readonly (PackEntry Entry, int Index) _at = catalog.Split(gid);

    public int Gid { get; } = gid;
    public PackEntry Entry => _at.Entry;
    public int LogicalIndex => _at.Index;
    public RpackFile Pack => _at.Entry.Pack!;
    public Logical Logical => Pack.Logicals[_at.Index];

    public string Name => Pack.Name(_at.Index);
    public string PackLabel => _at.Entry.Label;
    public string TypeLabel => ResTypes.Label(Logical.Type);
    public string IndexText => _at.Index.ToString("N0");
    public string PartsText => Logical.PartCount.ToString();
    public string SizeText => Format.Size(Pack.ResourceSize[_at.Index]);
    public string OffsetText => Logical.PartCount == 0 ? "" : $"0x{Pack.PartOffset((int)Logical.FirstPart):X}";
}

/// <summary>
/// Data-virtualised view over a search result: WPF only asks for the rows it shows, so a 300k-row result costs
/// an int array and nothing else. Never capped, never paged.
/// </summary>
public sealed class ResourceRowList(RpackCatalog catalog, int[] gids) : IList, IReadOnlyList<ResourceRow>
{
    private readonly Dictionary<int, ResourceRow> _cache = [];

    public int[] Gids { get; } = gids;
    public int Count => Gids.Length;

    public ResourceRow this[int index]
    {
        get
        {
            if (_cache.TryGetValue(index, out var row)) return row;
            if (_cache.Count > 20_000) _cache.Clear();   // only realised + selected rows are ever held
            return _cache[index] = new ResourceRow(catalog, Gids[index]);
        }
    }

    object? IList.this[int index]
    {
        get => this[index];
        set => throw new NotSupportedException();
    }

    public IEnumerator<ResourceRow> GetEnumerator()
    {
        for (int i = 0; i < Count; i++) yield return this[i];
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public int IndexOf(object? value) => value is ResourceRow r ? Array.IndexOf(Gids, r.Gid) : -1;
    public bool Contains(object? value) => IndexOf(value) >= 0;

    bool IList.IsFixedSize => true;
    bool IList.IsReadOnly => true;
    bool ICollection.IsSynchronized => false;
    object ICollection.SyncRoot => this;

    void ICollection.CopyTo(Array array, int index)
    {
        for (int i = 0; i < Count; i++) array.SetValue(this[i], index + i);
    }

    int IList.Add(object? value) => throw new NotSupportedException();
    void IList.Clear() => throw new NotSupportedException();
    void IList.Insert(int index, object? value) => throw new NotSupportedException();
    void IList.Remove(object? value) => throw new NotSupportedException();
    void IList.RemoveAt(int index) => throw new NotSupportedException();
}

/// <summary>One physical part of the selected resource.</summary>
public sealed class PartRow(RpackFile pack, int ordinal, int physIndex)
{
    public int Ordinal => ordinal;
    public int PhysIndex => physIndex;
    public Physical Physical => pack.Physicals[physIndex];
    public Storage Storage => pack.PartStorage(physIndex);
    public long Offset => pack.PartOffset(physIndex);
    public bool Direct => pack.PartIsDirect(physIndex);

    public string OrdinalText => ordinal.ToString();
    public string TypeText => ResTypes.Label(Storage.Type);
    public string OffsetText => $"0x{Offset:X10}";
    public string SizeText => Format.Size(Physical.Size);
    public string BytesText => Physical.Size.ToString("N0");
    public string MethodText => $"{Storage.Method}/{Storage.Codec}";
    public string AlignText => Storage.Alignment.ToString();
    public string FlagsText
    {
        get
        {
            var p = Physical;
            List<string> f = [];
            if (p.Bit8) f.Add("bit8");
            if (p.Priority != 0) f.Add($"prio{p.Priority}");
            if (p.Special) f.Add("special");
            if (p.Child) f.Add("child");
            if (p.Bit14) f.Add("bit14");
            if (p.Bit15) f.Add("bit15");
            if (Storage.Stream) f.Add("stream");
            return string.Join(' ', f);
        }
    }

    /// <summary>Red when the payload is not readable here, yellow when the engine treats the part specially.</summary>
    public System.Windows.Media.Brush FlagsBrush =>
        Skin.Brush(!Direct ? "Red" : Physical.Special || Physical.Priority != 0 ? "Yellow" : "FgDim");
}

/// <summary>Theme brushes by key, so code-behind never hard-codes a colour.</summary>
public static class Skin
{
    public static System.Windows.Media.Brush Brush(string key) =>
        System.Windows.Application.Current?.TryFindResource(key) as System.Windows.Media.Brush
        ?? System.Windows.Media.Brushes.Gray;
}

public static class Format
{
    private static readonly string[] Units = ["B", "KB", "MB", "GB", "TB"];

    public static string Size(long bytes)
    {
        double v = bytes;
        int u = 0;
        while (v >= 1024 && u < Units.Length - 1)
        {
            v /= 1024;
            u++;
        }
        return u == 0 ? $"{bytes:N0} B" : $"{v:0.#} {Units[u]}";
    }

    /// <summary>Classic 16-byte hex dump with an ASCII gutter, offsets relative to the file.</summary>
    public static string HexDump(ReadOnlySpan<byte> data, long baseOffset)
    {
        var sb = new System.Text.StringBuilder(data.Length / 16 * 78 + 64);
        for (int i = 0; i < data.Length; i += 16)
        {
            int n = Math.Min(16, data.Length - i);
            sb.Append((baseOffset + i).ToString("X10")).Append("  ");
            for (int k = 0; k < 16; k++)
            {
                sb.Append(k < n ? data[i + k].ToString("X2") : "  ").Append(k == 7 ? "  " : " ");
            }
            sb.Append(' ');
            for (int k = 0; k < n; k++)
            {
                byte b = data[i + k];
                sb.Append(b is >= 0x20 and < 0x7F ? (char)b : '.');
            }
            sb.Append('\n');
        }
        return sb.ToString();
    }
}
