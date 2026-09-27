using System.Windows.Controls;
using Nightrunner.Core.Audio;
using Nightrunner.Core.Logging;

namespace Nightrunner.UI.Views.Inspectors;

public partial class AudioInspector : UserControl
{
    private CancellationTokenSource? _probeCts;

    public AudioInspector() => InitializeComponent();

    public AudioInspector With(Selected.AudioArchive selection)
    {
        CancelProbe();
        var archive = selection.Catalog.Archives[selection.ArchiveId];
        Info.Text = $"{archive.Label}\n" +
                    $"{archive.Source.RelativePath}\n" +
                    $"header name  {archive.HeaderName}\n" +
                    $"language     {archive.Source.Language}\n" +
                    $"custom       {archive.Source.IsCustom}\n" +
                    $"priority     {archive.Priority}\n" +
                    $"table offset 0x{archive.TableOffset:X}\n" +
                    $"table rows   {archive.TableRowCount:N0}\n" +
                    $"WEMs         {archive.EntryCount:N0}\n" +
                    $"file size    {archive.FileLength:N0}\n" +
                    (archive.Error is null ? "" : $"error        {archive.Error}\n");
        return this;
    }

    public AudioInspector With(Selected.AudioEntry selection)
    {
        CancelProbe();
        Info.Text = EntryText(selection, null);
        var cts = new CancellationTokenSource();
        _probeCts = cts;
        _ = ProbeAsync(selection, cts);
        return this;
    }

    public void CancelProbe()
    {
        _probeCts?.Cancel();
        _probeCts = null;
    }

    private readonly record struct AudioDetails(TimeSpan Duration, string Codec, int Channels, int SampleRate);

    private async Task ProbeAsync(Selected.AudioEntry selection, CancellationTokenSource cts)
    {
        try
        {
            var details = await Task.Run(() =>
            {
                using var decoder = new VgmstreamDecoder(selection.Catalog, selection.EntryIndex);
                cts.Token.ThrowIfCancellationRequested();
                return new AudioDetails(decoder.Duration, decoder.CodecName, decoder.Channels, decoder.SampleRate);
            }, cts.Token);
            // An older probe must not repaint a newer selection.
            if (ReferenceEquals(_probeCts, cts) && !cts.IsCancellationRequested)
                Info.Text = EntryText(selection, details);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (ReferenceEquals(_probeCts, cts) && !cts.IsCancellationRequested)
            {
                Info.Text = EntryText(selection, null);
                Log.Warn("audio", $"Could not inspect WEM {selection.Catalog.Entries[selection.EntryIndex].WemId}: {ex}");
            }
        }
        finally
        {
            if (ReferenceEquals(_probeCts, cts)) _probeCts = null;
            cts.Dispose();
        }
    }

    private static string EntryText(Selected.AudioEntry selection, AudioDetails? details)
    {
        var entry = selection.Catalog.Entries[selection.EntryIndex];
        var archive = selection.Catalog.Archives[entry.ArchiveId];
        var events = selection.Names?.EventsFor(entry) ?? [];
        var banks = selection.Names?.BanksFor(entry) ?? (entry.BankName is null ? [] : [entry.BankName]);
        string bankLabel = banks.Count > 1 ? "banks" : "bank";
        string bankNames = banks.Count == 0 ? "—" : string.Join("\n             ", banks);
        string duration = details is { } audio
            ? audio.Duration.TotalHours >= 1 ? audio.Duration.ToString(@"h\:mm\:ss") : audio.Duration.ToString(@"m\:ss")
            : "—";
        return $"{entry.Name}\n" +
                    $"duration     {duration}\n" +
                    $"codec        {details?.Codec ?? "—"}\n" +
                    $"channels     {details?.Channels.ToString() ?? "—"}\n" +
                    $"sample rate  {(details is { } format ? $"{format.SampleRate / 1000.0:0.###} kHz" : "—")}\n" +
                    (events.Count == 0 ? "" : $"event names  {string.Join(", ", events)}\n") +
                    $"id           {entry.WemId} (0x{entry.WemId:X})\n" +
                    $"kind         {(entry.Kind == AespEntryKind.LooseWem ? "loose WEM" : "bank-embedded WEM")}\n" +
                    $"{bankLabel,-12} {bankNames}\n" +
                    $"{(entry.BankName is null ? "AESP row" : "DIDX index"),-12} {entry.RowIndex:N0}\n" +
                    $"offset       0x{entry.Offset:X}\n" +
                    $"size         {entry.Size:N0}\n" +
                    $"archive      {archive.Label}\n" +
                    $"language     {archive.Source.Language}\n" +
                    $"source       {archive.Source.RelativePath}";
    }
}
