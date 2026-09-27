using System.Collections;
using Nightrunner.Core.Audio;

namespace Nightrunner.UI;

public sealed class AudioArchiveRow(AespArchive archive)
{
    public int Id => archive.Id;
    public string Label => archive.Label;
    public string Path => archive.Source.Path;
    public string Summary => archive.Error ??
        $"{archive.EntryCount:N0} WEM · {Format.Size(archive.FileLength)}" +
        (archive.Source.Language.Length > 0 ? $" · {archive.Source.Language}" : archive.Source.IsCustom ? " · custom" : "");
    public System.Windows.Media.Brush SummaryBrush => Skin.Brush(archive.Error is null ? "FgDim" : "Error");
}

public sealed class AudioEntryRow(AespCatalog catalog, int index, AudioNameIndex? names)
{
    private readonly AespEntry _entry = catalog.Entries[index];
    private readonly AespArchive _archive = catalog.Archives[catalog.Entries[index].ArchiveId];

    public int Index => index;
    public string Name => names?.NameFor(_entry) ?? "—";
    public string IdText => _entry.WemId.ToString();
    public string KindText => _entry.Kind == AespEntryKind.LooseWem ? "loose" : "bank";
    public string SizeText => Format.Size(_entry.Size);
    public string ArchiveLabel => _entry.BankName is null ? _archive.Label : $"{_entry.BankName} · {_archive.Label}";
    public string LanguageText => _archive.Source.Language.Length > 0 ? _archive.Source.Language : _archive.Source.IsCustom ? "custom" : "";
}

public sealed class AudioEntryRowList(AespCatalog catalog, int[] indices, AudioNameIndex? names) : IList, IReadOnlyList<AudioEntryRow>
{
    private readonly Dictionary<int, AudioEntryRow> _cache = [];
    public int Count => indices.Length;

    public AudioEntryRow this[int index]
    {
        get
        {
            if (_cache.TryGetValue(index, out var row)) return row;
            // Virtualized scrolling should not retain every row ever visited.
            if (_cache.Count > 20_000) _cache.Clear();
            return _cache[index] = new AudioEntryRow(catalog, indices[index], names);
        }
    }

    object? IList.this[int index] { get => this[index]; set => throw new NotSupportedException(); }
    public IEnumerator<AudioEntryRow> GetEnumerator()
    {
        for (int i = 0; i < Count; i++) yield return this[i];
    }
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    public int IndexOf(object? value) => value is AudioEntryRow row ? Array.IndexOf(indices, row.Index) : -1;
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
