using System.IO;
using Nightrunner.Core.Backends;
using Nightrunner.Core.Games;
using Nightrunner.Core.Logging;
using Nightrunner.Core.Sdb;

namespace Nightrunner.UI;

/// <summary>
/// The one open shader database, shared by every window that reads it.
/// </summary>
/// <remarks>
/// Opened on first use rather than with the game: it costs ~25 MB of tables and a second of cold disk, and a
/// session that never looks at a material should not pay for it. The file and its index are built on a worker
/// and published together by reference assignment, so a panel that holds an <see cref="SdbIndex"/> holds a
/// consistent one. Switching the API or the game hands out a different instance instead of mutating this one.
///
/// Read-only throughout: there is no SDB writer.
/// </remarks>
public sealed class SdbService : IDisposable
{
    private SdbFile? _file;
    private CancellationTokenSource? _cts;
    private Task<SdbIndex?>? _opening;

    /// <summary>The databases this game ships, dx11 first — that is the one the Python tooling defaults to.</summary>
    public string[] Files { get; private set; } = [];

    /// <summary>Which file is open (or about to be), or null when the game has no SDB.</summary>
    public string? Path { get; private set; }

    /// <summary>The snapshot the panels read, or null while it is still being built.</summary>
    public SdbIndex? Index { get; private set; }

    /// <summary>What went wrong, when the database could not be opened.</summary>
    public string? Error { get; private set; }

    public bool Available => Files.Length > 0;

    /// <summary>The API label of the open file ("dx11" / "dx12"), for the header and the switch.</summary>
    public string Api => Path is null ? "" : ApiOf(Path);

    public static string ApiOf(string path)
    {
        string name = System.IO.Path.GetFileNameWithoutExtension(path);
        int under = name.LastIndexOf('_');
        return under >= 0 ? name[(under + 1)..] : name;
    }

    /// <summary>Raised on the UI thread whenever <see cref="Index"/> or <see cref="Error"/> changed.</summary>
    public event Action? Changed;

    /// <summary>Point at another game. Nothing is read until someone asks.</summary>
    public void Reset(GameInstall? install, IGameBackend? backend)
    {
        Close();
        Files = install is null ? [] : backend?.Sdb?.Files(install) ?? [];
        Array.Sort(Files, (a, b) => string.CompareOrdinal(ApiOf(a), ApiOf(b)));   // dx11 before dx12
        Path = Files.FirstOrDefault();
        Changed?.Invoke();
    }

    /// <summary>Open a different one of this game's databases.</summary>
    public Task<SdbIndex?> Use(string path)
    {
        if (string.Equals(path, Path, StringComparison.OrdinalIgnoreCase) && _opening is not null)
            return _opening;
        Close();
        Path = path;
        Changed?.Invoke();
        return EnsureAsync();
    }

    /// <summary>The snapshot, opening and indexing the file the first time it is asked for.</summary>
    public Task<SdbIndex?> EnsureAsync()
    {
        if (_opening is not null) return _opening;
        if (Path is not { } path) return Task.FromResult<SdbIndex?>(null);

        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        return _opening = Load(path, ct);
    }

    private async Task<SdbIndex?> Load(string path, CancellationToken ct)
    {
        using var op = Log.Start("sdb", $"open {System.IO.Path.GetFileName(path)}");
        try
        {
            var (file, index) = await Task.Run(() =>
            {
                var f = SdbFile.Open(path);
                try
                {
                    return (f, SdbIndex.Build(f, ct));
                }
                catch
                {
                    f.Dispose();
                    throw;
                }
            }, ct);

            if (ct.IsCancellationRequested)
            {
                file.Dispose();
                return null;
            }

            _file = file;
            Index = index;
            Error = null;
            op.Result = $"{file.Layout.Name} layout, {index.Count:N0} materials, " +
                        $"{file.PresetCount:N0} presets, index {index.BuildTime.TotalMilliseconds:F0} ms";
            Changed?.Invoke();
            return index;
        }
        catch (OperationCanceledException)
        {
            op.Result = "cancelled";
            return null;
        }
        catch (Exception e) when (e is SdbFormatException or IOException or UnauthorizedAccessException)
        {
            Error = e.Message;
            op.Failed(e.Message);
            Changed?.Invoke();
            return null;
        }
    }

    private void Close()
    {
        _cts?.Cancel();
        _cts = null;
        _opening = null;
        Index = null;
        Error = null;
        _file?.Dispose();
        _file = null;
    }

    public void Dispose() => Close();
}
