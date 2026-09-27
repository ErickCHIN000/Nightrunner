using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using NAudio.Wave;
using Nightrunner.Core.Audio;
using Nightrunner.Core.Export;
using Nightrunner.Core.Logging;

namespace Nightrunner.UI.Views;

public partial class AudioExplorerView : UserControl
{
    private readonly Workspace _workspace;
    private readonly Selection _selection;
    private readonly DispatcherTimer _debounce;
    private readonly DispatcherTimer _playerTimer;
    private CancellationTokenSource? _scanCts;
    private CancellationTokenSource? _searchCts;
    private CancellationTokenSource? _namesCts;
    private CancellationTokenSource? _playerLoadCts;
    private AespCatalog? _catalog;
    private AudioNameIndex? _names;
    private AudioPlaybackSession? _player;
    private bool _scrubbing;
    private bool _ready;
    private bool _detached;

    private sealed record KindChoice(AespEntryKind? Kind, string Text)
    {
        public override string ToString() => Text;
    }

    public AudioExplorerView(PanelContext context)
    {
        _workspace = context.Workspace;
        _selection = context.Selection;
        InitializeComponent();
        KindFilter.ItemsSource = new[]
        {
            new KindChoice(null, "all WEMs"),
            new KindChoice(AespEntryKind.LooseWem, "loose WEM"),
            new KindChoice(AespEntryKind.BankWem, "bank WEM"),
        };
        KindFilter.SelectedIndex = 0;
        _debounce = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(150),
        };
        _debounce.Tick += (_, _) =>
        {
            _debounce.Stop();
            _ = RunSearchAsync();
        };
        _playerTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(200),
        };
        _playerTimer.Tick += (_, _) => UpdatePlayerBar();
        _workspace.Opened += OnWorkspaceOpened;
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_ready) return;
        _ready = true;
        if (!AudioDecoding.Status.Available) ShowNoDecoder();
        Rows_SizeChanged(Rows, null!);
        _ = ScanAsync();
    }

    private void OnWorkspaceOpened()
    {
        _scanCts?.Cancel();
        _searchCts?.Cancel();
        _namesCts?.Cancel();
        _playerLoadCts?.Cancel();
        _debounce.Stop();
        DisposePlayer();
        _catalog = null;
        _names = null;
        Rows.ItemsSource = null;
        ArchiveList.ItemsSource = null;
        ArchiveHeader.Text = "archives";
        Timing.Text = "";
        _selection.ClearFrom("Audio");
        Status.Text = "no game";
        if (_ready && !_detached) _ = ScanAsync();
    }

    public void Detach()
    {
        _detached = true;
        _workspace.Opened -= OnWorkspaceOpened;
        Loaded -= OnLoaded;
        _scanCts?.Cancel();
        _searchCts?.Cancel();
        _namesCts?.Cancel();
        _playerLoadCts?.Cancel();
        _debounce.Stop();
        DisposePlayer();
        _selection.ClearFrom("Audio");
    }

    public void FocusSearch()
    {
        Search.Focus();
        Search.SelectAll();
    }

    private async Task ScanAsync()
    {
        var install = _workspace.Install;
        if (install is null || install.Audio is null)
        {
            Status.Text = "no game audio";
            return;
        }
        var cts = new CancellationTokenSource();
        _scanCts = cts;
        Status.Text = "indexing audio";
        var clock = Stopwatch.StartNew();
        try
        {
            var catalog = await Task.Run(() => AespCatalog.Scan(install, cts.Token), cts.Token);
            if (cts.IsCancellationRequested || _detached || !ReferenceEquals(_scanCts, cts) ||
                !ReferenceEquals(_workspace.Install, install)) return;

            _catalog = catalog;
            _names = null;
            var visibleArchives = catalog.Archives.Where(a => a.EntryCount > 0 || a.Error is not null).ToArray();
            ArchiveList.ItemsSource = visibleArchives.Select(a => new AudioArchiveRow(a)).ToArray();
            ArchiveHeader.Text = $"archives ({visibleArchives.Length})";
            Status.Text = $"{catalog.Entries.Count:N0} WEMs" +
                          (catalog.ErrorCount > 0 ? $" · {catalog.ErrorCount} failed" : "") +
                          (catalog.Warnings.Count > 0 ? $" · {catalog.Warnings.Count} bank warnings" : "") +
                          (AudioDecoding.Status.Available ? "" : " · no decoder");
            Status.Foreground = Skin.Brush(catalog.ErrorCount > 0 || catalog.Warnings.Count > 0 ? "Warn" : "FgDim");
            Timing.Text = $"{clock.Elapsed.TotalMilliseconds:0.#} ms";
            foreach (var archive in catalog.Archives.Where(a => a.Error is not null))
                Log.Warn("audio", $"{archive.Source.RelativePath}: {archive.Error}");
            foreach (var warning in catalog.Warnings)
                Log.Warn("audio", warning);
            // Show IDs first; bank and pinhead name resolution runs separately.
            _ = RunSearchAsync();
            _ = ResolveNamesAsync(catalog);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (ReferenceEquals(_scanCts, cts) && !_detached)
            {
                Status.Text = ex.Message;
                Status.Foreground = Skin.Brush("Error");
                Log.Error("audio", ex.ToString());
            }
        }
        finally
        {
            if (ReferenceEquals(_scanCts, cts)) _scanCts = null;
            cts.Dispose();
        }
    }

    private async Task ResolveNamesAsync(AespCatalog catalog)
    {
        _namesCts?.Cancel();
        var cts = new CancellationTokenSource();
        _namesCts = cts;
        try
        {
            var names = await Task.Run(() => AudioNameIndex.Build(catalog, cts.Token), cts.Token);
            if (cts.IsCancellationRequested || _detached || !ReferenceEquals(_namesCts, cts) ||
                !ReferenceEquals(_catalog, catalog)) return;
            _names = names;
            Log.Info("audio", $"Resolved {names.MediaCount:N0} media IDs from {names.EventCount:N0} event names in {names.BankCount:N0} banks");
            Status.Text += $" · {names.NamedEntryCount:N0} named";
            foreach (var warning in names.Warnings) Log.Warn("audio", $"Name index: {warning}");
            _ = RunSearchAsync();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (ReferenceEquals(_namesCts, cts) && !_detached)
                Log.Warn("audio", $"Event names unavailable: {ex}");
        }
        finally
        {
            if (ReferenceEquals(_namesCts, cts)) _namesCts = null;
            cts.Dispose();
        }
    }

    private void Search_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_ready) return;
        _debounce.Stop();
        _debounce.Start();
    }

    private void Filter_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_ready) _ = RunSearchAsync();
    }

    private void ArchiveList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready) return;
        Rows.SelectedItem = null;
        _ = RunSearchAsync();
        ShowSelection();
    }

    private void ClearFilter_Click(object sender, RoutedEventArgs e) => ArchiveList.UnselectAll();

    private async Task RunSearchAsync()
    {
        var catalog = _catalog;
        var names = _names;
        if (catalog is null || _detached) return;
        _searchCts?.Cancel();
        var cts = new CancellationTokenSource();
        _searchCts = cts;
        string query = Search.Text;
        var kind = (KindFilter.SelectedItem as KindChoice)?.Kind;
        int[] archives = ArchiveList.SelectedItems.Cast<AudioArchiveRow>().Select(a => a.Id).ToArray();
        try
        {
            var result = await Task.Run(() => catalog.Search(query, kind, archives, cts.Token, names), cts.Token);
            // Ignore searches completed against an older request or catalog.
            if (cts.IsCancellationRequested || _detached || !ReferenceEquals(_searchCts, cts) ||
                !ReferenceEquals(_catalog, catalog)) return;
            int selectedIndex = (Rows.SelectedItem as AudioEntryRow)?.Index ?? -1;
            Rows.ItemsSource = new AudioEntryRowList(catalog, result.EntryIndices, names);
            int selectedPosition = Array.IndexOf(result.EntryIndices, selectedIndex);
            if (selectedPosition >= 0) Rows.SelectedIndex = selectedPosition;
            Timing.Text = $"{result.Count:N0} rows · {result.Elapsed.TotalMilliseconds:0.#} ms";
            ShowSelection();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (ReferenceEquals(_searchCts, cts) && !_detached)
            {
                Status.Text = ex.Message;
                Status.Foreground = Skin.Brush("Error");
                Log.Error("audio", ex.ToString());
            }
        }
        finally
        {
            if (ReferenceEquals(_searchCts, cts)) _searchCts = null;
            cts.Dispose();
        }
    }

    private void Rows_SelectionChanged(object sender, SelectionChangedEventArgs e) => ShowSelection();

    private void Rows_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source &&
            ItemsControl.ContainerFromElement(Rows, source) is ListViewItem item && !item.IsSelected)
        {
            Rows.SelectedItems.Clear();
            item.IsSelected = true;
        }
        if (_catalog is null || Rows.SelectedItems.Count == 0) e.Handled = true;
        ExportWavItem.IsEnabled = AudioDecoding.Status.Available;
    }

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        if (_catalog is not { } catalog || sender is not MenuItem { Tag: string format }) return;
        if (format == "wav" && !AudioDecoding.Status.Available) return;
        int[] indices = Rows.SelectedItems.Cast<AudioEntryRow>().Select(row => row.Index).ToArray();
        if (indices.Length == 0) return;
        var duplicateIds = indices.GroupBy(index => catalog.Entries[index].WemId)
                                  .Where(group => group.Count() > 1)
                                  .Select(group => group.Key).ToHashSet();
        var names = _names;
        string gameName = _workspace.Install?.Name ?? "Unknown Game";
        string? file = null, folder = null;
        if (indices.Length == 1)
        {
            var entry = catalog.Entries[indices[0]];
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Title = $"Export {format.ToUpperInvariant()}",
                Filter = $"{format.ToUpperInvariant()} (*.{format})|*.{format}",
                FileName = $"{entry.WemId}.{format}",
                DefaultExt = format,
                AddExtension = true,
                OverwritePrompt = true,
            };
            if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
            file = dialog.FileName;
        }
        else if ((folder = Shell.PickFolder(this, $"Export {indices.Length} {format.ToUpperInvariant()} files into...")) is null)
            return;

        string? targetFile = file, targetFolder = folder;
        Jobs.Run($"export {format.ToUpperInvariant()} {indices.Length} audio file(s)", "audio", ctx =>
        {
            int written = 0;
            long bytes = 0;
            for (int i = 0; i < indices.Length; i++)
            {
                ctx.Token.ThrowIfCancellationRequested();
                int index = indices[i];
                var entry = catalog.Entries[index];
                string path = targetFile ?? Path.Combine(targetFolder!, BatchFileName(catalog, index, format, duplicateIds));
                int current = i;
                ctx.Step(i, indices.Length, $"{entry.WemId}.{format}");
                try
                {
                    if (targetFile is null) Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    Action<long, long> progress = (done, total) =>
                        ctx.Report((current + (total > 0 ? Math.Min(1, (double)done / total) : 0)) / indices.Length,
                                   $"{entry.WemId}.{format}: {Format.Size(done)}");
                    if (format == "wem")
                        AudioExporter.ExportWem(catalog, index, path, targetFile is not null,
                                                ctx.Token, progress);
                    else
                        AudioExporter.ExportWav(catalog, index, path, targetFile is not null,
                                                ctx.Token, progress, ExportMetadata(catalog, index, names, gameName));
                    bytes += new FileInfo(path).Length;
                    written++;
                }
                catch (Exception ex) when (indices.Length > 1 && ex is not OperationCanceledException)
                {
                    Log.Warn("audio", $"Could not export {entry.WemId}.{format}: {ex}");
                }
                ctx.Step(i + 1, indices.Length, $"{written} of {indices.Length} exported");
            }
            if (written == 0) throw new IOException("No audio files were exported; see the audio log for details");
            return $"{written} of {indices.Length} {format.ToUpperInvariant()} files exported, {Format.Size(bytes)} -> {targetFile ?? targetFolder}";
        });
    }

    private static string BatchFileName(AespCatalog catalog, int index, string format, HashSet<ulong> duplicateIds)
    {
        var entry = catalog.Entries[index];
        string fileName = $"{entry.WemId}.{format}";
        if (!duplicateIds.Contains(entry.WemId)) return fileName;
        // Duplicate IDs keep their WEM filename but need separate batch paths.
        string archive = Path.GetFileNameWithoutExtension(catalog.Archives[entry.ArchiveId].Label);
        return Path.Combine("duplicate IDs", $"{RawExporter.SafeName(archive)}_{index}", fileName);
    }

    private static WavMetadata ExportMetadata(AespCatalog catalog, int index, AudioNameIndex? names, string gameName)
    {
        var entry = catalog.Entries[index];
        var archive = catalog.Archives[entry.ArchiveId];
        var events = names?.EventsFor(entry) ?? [];
        string title = events.Count > 0 ? events[0] : entry.BankName ?? entry.WemId.ToString();
        string artist = events.Count > 0 ? string.Join("; ", events) : entry.BankName ?? "N/A";
        string album = $"{gameName} - {Path.GetFileNameWithoutExtension(archive.Label)}";
        var banks = names?.BanksFor(entry) ?? [];
        string bank = entry.BankName ?? (banks.Count > 0 ? string.Join("; ", banks) : "N/A");
        string comment = $"Sound ID: {entry.WemId}; Bank: {bank}; AESP: {archive.Source.RelativePath}";
        return new WavMetadata(title, artist, album, comment);
    }

    private void Rows_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ItemsControl.ContainerFromElement(Rows, e.OriginalSource as DependencyObject) is not ListViewItem item ||
            item.Content is not AudioEntryRow row) return;
        _ = PlayEntryAsync(row);
    }

    private async Task PlayEntryAsync(AudioEntryRow row)
    {
        var catalog = _catalog;
        if (catalog is null || _detached) return;
        if (!AudioDecoding.Status.Available)
        {
            // Said once in the log when the decoder was checked; here only the player bar says it.
            ShowNoDecoder();
            return;
        }
        _playerLoadCts?.Cancel();
        var cts = new CancellationTokenSource();
        _playerLoadCts = cts;
        DisposePlayer();
        PlayerTitle.Text = row.Name == "—" ? row.IdText : row.Name;
        PlayerStatus.Text = "loading";
        PlayerStatus.ToolTip = null;

        try
        {
            var decoder = await Task.Run(() => new VgmstreamDecoder(catalog, row.Index, downmixToStereo: true), cts.Token);
            if (cts.IsCancellationRequested || _detached || !ReferenceEquals(_playerLoadCts, cts) ||
                !ReferenceEquals(_catalog, catalog))
            {
                decoder.Dispose();
                return;
            }

            try
            {
                _player = new AudioPlaybackSession(decoder, (float)VolumeSlider.Value);
                _player.Play();
            }
            catch
            {
                _player?.Dispose();
                _player = null;
                throw;
            }
            SeekSlider.Maximum = Math.Max(1, _player.Duration.TotalSeconds);
            SeekSlider.IsEnabled = true;
            PlayPauseButton.IsEnabled = true;
            StopButton.IsEnabled = true;
            PlayerTitle.Text = row.Name == "—" ? row.IdText : row.Name;
            _playerTimer.Start();
            UpdatePlayerBar();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (ReferenceEquals(_playerLoadCts, cts) && !_detached)
            {
                PlayerStatus.Text = "error";
                PlayerStatus.ToolTip = ex.Message;
                Log.Warn("audio", $"Could not play {row.Name}: {ex}");
            }
        }
        finally
        {
            if (ReferenceEquals(_playerLoadCts, cts)) _playerLoadCts = null;
            cts.Dispose();
        }
    }

    private void PlayPause_Click(object sender, RoutedEventArgs e)
    {
        if (_player is null) return;
        try
        {
            if (_player.State == PlaybackState.Playing) _player.Pause();
            else _player.Play();
            UpdatePlayerBar();
        }
        catch (Exception ex) { ShowPlayerError(ex); }
    }

    private void Stop_Click(object sender, RoutedEventArgs e)
    {
        if (_player is null) return;
        try { _player.Stop(); UpdatePlayerBar(); }
        catch (Exception ex) { ShowPlayerError(ex); }
    }

    private void Volume_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_player is not null) _player.Volume = (float)e.NewValue;
    }

    private void Seek_PointerDown(object sender, MouseButtonEventArgs e) => _scrubbing = true;

    private void Seek_PointerUp(object sender, MouseButtonEventArgs e) => CommitSeek();

    private void Seek_LostCapture(object sender, MouseEventArgs e) => CommitSeek();

    private void Seek_KeyUp(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Left or Key.Right or Key.Home or Key.End or Key.PageUp or Key.PageDown)
            SeekToSlider();
    }

    private void Seek_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_scrubbing && _player is not null)
            PlayerTime.Text = $"{FormatTime(TimeSpan.FromSeconds(e.NewValue))} / {FormatTime(_player.Duration)}";
    }

    private void CommitSeek()
    {
        if (!_scrubbing) return;
        _scrubbing = false;
        SeekToSlider();
    }

    private void SeekToSlider()
    {
        if (_player is null) return;
        try { _player.Seek(TimeSpan.FromSeconds(SeekSlider.Value)); UpdatePlayerBar(); }
        catch (Exception ex) { ShowPlayerError(ex); }
    }

    private void UpdatePlayerBar()
    {
        if (_player is not { } player) return;
        if (player.Error is { } error)
        {
            ShowPlayerError(error);
            _playerTimer.Stop();
            return;
        }
        var position = player.Position;
        if (!_scrubbing) SeekSlider.Value = Math.Min(SeekSlider.Maximum, position.TotalSeconds);
        PlayerTime.Text = $"{FormatTime(position)} / {FormatTime(player.Duration)}";
        PlayerStatus.Text = player.State switch
        {
            PlaybackState.Playing => "playing",
            PlaybackState.Paused => "paused",
            _ => "stopped",
        };
        PlayPauseButton.Content = player.State == PlaybackState.Playing ? "Pause" : "Play";
    }

    private static string FormatTime(TimeSpan value) =>
        value.TotalHours >= 1 ? value.ToString(@"h\:mm\:ss") : value.ToString(@"m\:ss");

    private void ShowPlayerError(Exception ex)
    {
        PlayerStatus.Text = "error";
        PlayerStatus.ToolTip = ex.Message;
        Log.Warn("audio", $"Audio playback failed: {ex}");
    }

    private void DisposePlayer()
    {
        _playerTimer.Stop();
        _player?.Dispose();
        _player = null;
        _scrubbing = false;
        SeekSlider.IsEnabled = false;
        SeekSlider.Value = 0;
        PlayPauseButton.IsEnabled = false;
        PlayPauseButton.Content = "Play";
        StopButton.IsEnabled = false;
        PlayerTitle.Text = "";
        PlayerStatus.Text = "ready";
        PlayerStatus.ToolTip = null;
        PlayerTime.Text = "0:00 / 0:00";
        if (!AudioDecoding.Status.Available) ShowNoDecoder();
    }

    private void ShowNoDecoder()
    {
        PlayerStatus.Text = "no decoder";
        PlayerStatus.ToolTip = AudioDecoding.Status.Reason;
    }

    private void Rows_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (Rows.View is not GridView grid) return;
        double others = grid.Columns.Where(c => c != ColName)
            .Sum(c => double.IsNaN(c.Width) ? c.ActualWidth : c.Width);
        ColName.Width = Math.Max(150, Rows.ActualWidth - others - 40);
    }

    private void ShowSelection()
    {
        if (_catalog is not { } catalog) return;
        if (Rows.SelectedItem is AudioEntryRow entry)
            _selection.Set("Audio", new Selected.AudioEntry(catalog, entry.Index, _names));
        else if (ArchiveList.SelectedItems.Count == 1 && ArchiveList.SelectedItem is AudioArchiveRow archive)
            _selection.Set("Audio", new Selected.AudioArchive(catalog, archive.Id));
        else
            _selection.ClearFrom("Audio");
    }
}
