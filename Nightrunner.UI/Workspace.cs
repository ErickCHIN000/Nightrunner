using System.IO;
using Nightrunner.Core.Backends;
using Nightrunner.Core.Games;
using Nightrunner.Core.Logging;
using Nightrunner.Core.Mesh;
using Nightrunner.Core.Model;
using Nightrunner.Core.Project;
using Nightrunner.Core.Rpack;

namespace Nightrunner.UI;

/// <summary>
/// What every view works against: the open game, its backend, and its pack catalog. One Workspace per shell;
/// opening another game swaps the backend and the catalog and raises <see cref="Opened"/> so each view rebinds.
/// </summary>
public sealed class Workspace : IDisposable
{
    /// <summary>The open game, or null when only loose packs were opened.</summary>
    public GameInstall? Install { get; private set; }

    /// <summary>The game-specific half of the app: packs, textures, meshes, SDB, materials.</summary>
    public IGameBackend? Backend { get; private set; }

    public RpackCatalog Catalog { get; private set; } = new();

    /// <summary>The shader database of the open game. Opened on first use, not with the game.</summary>
    public SdbService Sdb { get; } = new();

    /// <summary>Roots the user typed in, remembered between runs.</summary>
    public GameSettings Settings { get; } = GameSettings.Load();

    /// <summary>A label for the shell header: the game name, or what was opened instead.</summary>
    public string Title { get; private set; } = "no game";

    public string? GameId => Install?.Id;

    /// <summary>Raised on the UI thread after the catalog is replaced and loading has started.</summary>
    public event Action? Opened;

    /// <summary>The open mod project, or null. Views that read or write project files watch this.</summary>
    public ModProject? Project { get; private set; }

    /// <summary>Raised when the open project changes (opened, created, closed or renamed).</summary>
    public event Action? ProjectChanged;

    /// <summary>Raised when files were written into the open project, so the windows showing it re-read it.</summary>
    public event Action? ProjectContentChanged;

    /// <summary>Tell the project windows that what is on disk has changed.</summary>
    public void NotifyProjectContentChanged() => ProjectContentChanged?.Invoke();

    public void SetProject(ModProject? project)
    {
        Project = project;
        if (project is not null)
        {
            Settings.TouchProject(project.Folder);
            Settings.Save();
        }
        ProjectChanged?.Invoke();
    }

    private CancellationTokenSource? _cts;

    /// <summary>True when NightrunnerProxy's mods are shown (Nightrunner Runtime Modding is on).</summary>
    public bool RuntimeModding => Settings.RuntimeModdingFor(GameId);

    /// <summary>
    /// What the runtime loads, read once per (re)open so the pack catalog and the <c>.model</c> catalog agree
    /// (a pak read later than the packs showed a mod's model over stock meshes). Empty with runtime modding off.
    /// </summary>
    public RuntimeContent Runtime { get; private set; } = RuntimeContent.Empty;

    /// <summary>Stock vs modded packs and paks of the open game, fixed at (re)open.</summary>
    public PackOrigins Origins { get; private set; } = new(null);

    /// <summary>The paks the open game's <c>.model</c> catalog reads: stock, then the runtime's in mount order.</summary>
    private string[] _paks = [];

    public Task OpenGame(GameInstall install)
    {
        Install = install;
        Backend = GameBackends.For(install.Profile);
        Title = install.Name;
        return Reset(PackFiles(install), install.Assets);
    }

    private string[] PackFiles(GameInstall install)
    {
        Runtime = Settings.RuntimeModdingFor(install.Id) ? RuntimeContent.Read(install) : RuntimeContent.Empty;
        Origins = new PackOrigins(install, BuiltNames());
        _paks = install.Paks(Runtime);
        var runtime = Runtime.Items.Where(i => i.Loads).ToList();
        if (runtime.Count > 0)
            Log.Info("runtime", $"{runtime.Count(i => i.Kind == RuntimeKind.Rpack)} rpack(s), {runtime.Count(i => i.Kind == RuntimeKind.Pak)} pak(s) " +
                                $"from {string.Join(", ", runtime.Select(i => i.Source).Distinct(StringComparer.OrdinalIgnoreCase))}");
        foreach (var problem in Runtime.Problems) Log.Warn("runtime", problem);
        if (Settings.RuntimeModdingFor(install.Id) &&
            Nightrunner.Core.Games.RuntimeModding.LastBoot(Path.Combine(install.Paths.RuntimeFolder, "logs", Nightrunner.Core.Games.RuntimeModding.BootLogName),
                                    Path.Combine(install.Paths.RuntimeFolder, "logs", Nightrunner.Core.Games.RuntimeModding.LogName)) is { } boot)
            Log.Warn("runtime", $"last start: {boot.Text} · {boot.Log}");
        return Backend?.Packs?.IsRpack == true
            ? install.Rpacks(Runtime)
            : Backend?.Packs?.Files(install) ?? [];
    }

    /// <summary>Output names the known projects (recent ones and the open one) recorded writing.</summary>
    private IReadOnlyList<string> BuiltNames() =>
        PackOrigins.BuiltNames(Settings.RecentProjects.Concat(Project is { } p ? [p.Folder] : []));

    /// <summary>
    /// Re-open the current game's packs — used when runtime modding is switched on or off, and when the game's
    /// packs, paks or runtime content change on disk (see <see cref="Watch"/>).
    /// </summary>
    public Task Reload()
    {
        if (Install is not { } install) return Task.CompletedTask;
        return Reset(PackFiles(install), install.Assets);
    }

    /// <summary>Open loose packs with no game behind them.</summary>
    public Task OpenPacks(string[] paths)
    {
        Install = null;
        Backend = null;
        Title = paths.Length == 1 ? Path.GetFileName(paths[0]) : $"{paths.Length} packs";
        Runtime = RuntimeContent.Empty;
        Origins = new PackOrigins(null, BuiltNames());
        _paks = [];
        return Reset(paths, Path.GetDirectoryName(paths[0]));
    }

    /// <summary>
    /// The open catalog without modded packs (<see cref="Origins"/>), same global ids: what "Add to project" takes its
    /// templates from. A snapshot of the packs indexed when it is asked for.
    /// </summary>
    public RpackCatalog StockCatalog() => StockCopy.Catalog(Catalog, Origins);

    // ---- reload on change -------------------------------------------------------------------------------------

    private readonly List<FileSystemWatcher> _watchers = [];
    private Timer? _changed;
    private SynchronizationContext? _ui;

    /// <summary>
    /// Watch what decides the open game's catalog — the stock packs and paks, NightrunnerProxy's mods and
    /// nightrunner.json — and reload two seconds after the last change. A pack copied in while
    /// the app is open then shows without re-opening the game. Read-only: watching writes nothing.
    /// </summary>
    private void Watch(GameInstall? install)
    {
        foreach (var w in _watchers) w.Dispose();
        _watchers.Clear();
        if (install is null) return;
        _ui = SynchronizationContext.Current;
        var bin = install.Paths.Bin;
        var runtime = install.Paths.RuntimeFolder;
        bool Relevant(string path)
        {
            string name = Path.GetFileName(path);
            string ext = Path.GetExtension(path);
            if (ext.Equals(".rpack", StringComparison.OrdinalIgnoreCase) || ext.Equals(".pak", StringComparison.OrdinalIgnoreCase) ||
                ext.Equals(".mpak", StringComparison.OrdinalIgnoreCase))
                return true;
            if (path.StartsWith(runtime + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                return !path.Contains($"{Path.DirectorySeparatorChar}logs{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) &&
                       (ext.Equals(".sdb", StringComparison.OrdinalIgnoreCase) || name.Equals(RuntimeContent.ManifestName, StringComparison.OrdinalIgnoreCase) ||
                        name.Equals(RuntimeContent.SettingsName, StringComparison.OrdinalIgnoreCase));
            return false;
        }
        foreach (var folder in new[] { install.Assets, install.Source, bin }.OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!Directory.Exists(folder)) continue;
            try
            {
                var w = new FileSystemWatcher(folder)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
                };
                FileSystemEventHandler on = (_, e) => { if (Relevant(e.FullPath)) Changed(e.FullPath); };
                w.Created += on;
                w.Changed += on;
                w.Deleted += on;
                w.Renamed += (_, e) => { if (Relevant(e.FullPath) || Relevant(e.OldFullPath)) Changed(e.FullPath); };
                w.EnableRaisingEvents = true;
                _watchers.Add(w);
            }
            catch (Exception e) when (e is IOException or ArgumentException or UnauthorizedAccessException or PlatformNotSupportedException)
            {
                Log.Warn("runtime", $"cannot watch {folder}: {e.Message}");
            }
        }
    }

    private void Changed(string path)
    {
        var install = Install;
        lock (_watchers)
        {
            _changed?.Dispose();
            _changed = new Timer(_ => _ui?.Post(_ =>
            {
                if (!ReferenceEquals(install, Install)) return;
                if (OwnWrite(path)) return;   // a mod switch from this app: it reloaded already
                Log.Info("runtime", $"{Path.GetFileName(path)} changed: reloading packs");
                _ = Reload();
            }, null), null, 2000, Timeout.Infinite);
        }
    }

    // ---- mod switches -------------------------------------------------------------------------------------------

    /// <summary>Raised on the UI thread after a mod switch was written, refused or found unchanged.</summary>
    public event Action<RuntimeSwitch>? ModSwitched;

    /// <summary>The last switch on the open game, so a Mods window re-read after the reload can still say what happened.</summary>
    public RuntimeSwitch? LastSwitch => _lastSwitch is { } l && ReferenceEquals(l.Game, Install) ? l.Switch : null;

    private (GameInstall? Game, RuntimeSwitch Switch)? _lastSwitch;

    /// <summary>nightrunner.json as this app last wrote it (time, size): the watcher skips that change, the switch reloads itself.</summary>
    private (string Path, DateTime Time, long Size)? _ownWrite;

    /// <summary>
    /// Switch a mod on or off in NightrunnerProxy's <c>nightrunner.json</c> — the one write into a game folder
    /// (<see cref="RuntimeSettingsWriter"/>) — then re-read the open game (<see cref="Reload"/>), so the Mods window,
    /// the catalogs and the viewer follow. Refused with runtime modding off, without the runtime, or while the game runs.
    /// </summary>
    public async Task<RuntimeSwitch> SetModEnabled(string modId, bool enabled)
    {
        RuntimeSwitch result;
        if (Install is not { } install) result = RuntimeSwitch.Refuse(modId, enabled, "no game");
        else
        {
            bool on = RuntimeModding;
            result = await Task.Run(() => RuntimeSettingsWriter.SetEnabled(install, modId, enabled, on));
            if (result.Written)
            {
                string path = Path.Combine(install.Paths.RuntimeFolder, RuntimeContent.SettingsName);
                var info = new FileInfo(path);
                _ownWrite = (path, info.LastWriteTimeUtc, info.Length);
            }
            Log.Info("runtime", $"{result.ModId} {(result.Enabled ? "on" : "off")}: {result.Message}");
        }
        _lastSwitch = (Install, result);
        ModSwitched?.Invoke(result);
        if (result.Written && Install is not null) _ = Reload();
        return result;
    }

    private bool OwnWrite(string path)
    {
        if (_ownWrite is not { } w || !w.Path.Equals(path, StringComparison.OrdinalIgnoreCase)) return false;
        try
        {
            var info = new FileInfo(path);
            return info.Exists && info.LastWriteTimeUtc == w.Time && info.Length == w.Size;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>Add more packs to what is already open (keeps the catalog and the current game).</summary>
    public Task AddPacks(string[] paths)
    {
        DropViews();
        var catalog = Catalog;
        var load = catalog.LoadAsync(paths, Install?.Assets ?? Path.GetDirectoryName(paths[0]), _cts?.Token ?? default);
        _ = load.ContinueWith(_ => { if (ReferenceEquals(catalog, Catalog)) DropViews(); }, TaskScheduler.Default);
        return load;
    }

    private Task Reset(string[] paths, string? root)
    {
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        _usageCts?.Cancel();
        _usage = null;
        _models?.Dispose();
        _models = null;
        _animations = null;
        _rigs = null;
        _animObjects = null;
        _prefabs = null;
        _prefabRig?.Dispose();
        _prefabRig = null;
        Surfaces = NewSurfaceCache();
        Textures = NewTextureCache();
        var old = Catalog;
        Catalog = new RpackCatalog();
        DropViews();
        Sdb.Reset(Install, Backend);
        Watch(Install);
        GameAccent.Apply(GameId);
        Opened?.Invoke();       // views drop their old rows and subscribe to the new catalog first
        old.Dispose();
        return _loading = Catalog.LoadAsync(paths, root, _cts.Token);
    }

    private Task _loading = Task.CompletedTask;

    /// <summary>Completes when the open game's packs are indexed (failures surface where the packs are used).</summary>
    public Task WaitLoaded() => _loading.ContinueWith(_ => { }, TaskScheduler.Default);

    // ---- models and material usage -----------------------------------------------------------------------------

    private ModelCatalog? _models;
    private Nightrunner.Core.Anim.AnimCatalog? _animations;
    private Nightrunner.Core.Prefab.PrefabCatalog? _prefabs;
    private Nightrunner.Core.Prefab.PrefabRigSource? _prefabRig;

    /// <summary>Meshes and vehicle scripts the prefab placement reads for rig-driven parts. Null without a game.</summary>
    public Nightrunner.Core.Prefab.PrefabRigSource? PrefabRig
    {
        get
        {
            lock (_prefabsLock)
                if (_prefabRig is null && Install is { } install && Catalog.IndexedPacks.Length > 0)
                    _prefabRig = new Nightrunner.Core.Prefab.PrefabRigSource(Catalog, _paks.Length > 0 ? _paks : install.Paks(Runtime));
            return _prefabRig;
        }
    }

    /// <summary>The skeleton the Viewport shows (names animation tracks on export, and is the .glb armature), or null.</summary>
    public Nightrunner.Core.Model.ModelSkeleton? ShownSkeleton { get; set; }
    public IReadOnlyList<string>? ShownBones => ShownSkeleton?.Names;
    private readonly Lock _prefabsLock = new();

    /// <summary>
    /// Every prefab of the open game, decoded on first use: the binary prefabs of the packs (first registration wins),
    /// then the text <c>.prefab</c> members of the paks (read when used; a binary prefab of the same name shadows one).
    /// Null without packs.
    /// </summary>
    public Nightrunner.Core.Prefab.PrefabCatalog? Prefabs
    {
        get
        {
            lock (_prefabsLock)
                if (_prefabs is null && Catalog.IndexedPacks.Length > 0)
                {
                    using var op = Log.Start("prefab", "decode prefabs");
                    _prefabs = Nightrunner.Core.Prefab.PrefabCatalog.Build(Catalog, Models);
                    op.Result = $"{_prefabs.BinaryCount:N0} binary + {_prefabs.TextCount:N0} text prefabs, {_prefabs.Prefabs.Count(p => !p.Wins):N0} shadowed" +
                                (_prefabs.Errors.Count > 0 ? $", {_prefabs.Errors.Count} pack(s) not decoded" : "");
                }
            return _prefabs;
        }
    }
    private readonly Lock _animationsLock = new();

    /// <summary>
    /// Every sequence bank of the open game (first registration wins), read on first use. Null without packs. With
    /// runtime modding on, mod packs register in the runtime's order (mod.json, <see cref="Runtime"/>) (<see cref="Nightrunner.Core.Anim.AnimPackOrder"/>).
    /// </summary>
    public Nightrunner.Core.Anim.AnimCatalog? Animations
    {
        get
        {
            lock (_animationsLock)
                if (_animations is null && Catalog.IndexedPacks.Length > 0)
                {
                    using var op = Log.Start("anim", "read sequence banks");
                    // the same runtime snapshot the pack catalog was built from (mod.json)
                    var order = RuntimeModding && Install is not null ? Nightrunner.Core.Anim.AnimPackOrder.FromRuntime(Runtime) : null;
                    _animations = Nightrunner.Core.Anim.AnimCatalog.Build(Catalog, Install is { } i2 ? i2.Paths.IsCustom : null, order);
                    if (order is { Orders.Count: > 0 })
                        Log.Info("anim", $"clip registration orders: {string.Join(", ", order.Orders.Select(kv => $"{kv.Key} = {kv.Value}"))}");
                    op.Result = $"{_animations.Banks.Count} banks, {_animations.Sequences.Count:N0} sequences" +
                                (_animations.Shadowed.Count > 0 ? $", {_animations.Shadowed.Count} shadowed by an earlier pack" : "");
                }
            return _animations;
        }
    }

    private Nightrunner.Core.Anim.AnimRigs? _rigs;

    private Nightrunner.Core.Anim.AnimObjects? _animObjects;
    private readonly Lock _objectsLock = new();

    /// <summary>Every mesh by entity-name hash, for clips that play on an object (built on first use). Null without packs.</summary>
    public Nightrunner.Core.Anim.AnimObjects? AnimObjects
    {
        get
        {
            lock (_objectsLock)
                if (_animObjects is null && Catalog.IndexedPacks.Length > 0)
                {
                    using var op = Log.Start("anim", "index object meshes");
                    _animObjects = Nightrunner.Core.Anim.AnimObjects.Build(Catalog);
                    op.Result = $"{_animObjects.Meshes:N0} meshes";
                }
            return _animObjects;
        }
    }

    /// <summary>The skeletons models name, with their bone hashes (which model a clip plays on). Null without packs.</summary>
    public Nightrunner.Core.Anim.AnimRigs? Rigs
    {
        get
        {
            lock (_animationsLock)
                if (_rigs is null && Catalog.IndexedPacks.Length > 0 && Models is { } models)
                {
                    using var op = Log.Start("anim", "read model rigs");
                    _rigs = Nightrunner.Core.Anim.AnimRigs.Build(Catalog, models);
                    op.Result = $"{_rigs.Rigs.Count} rigs" + (_rigs.Missing.Count > 0 ? $", {_rigs.Missing.Count} skeleton(s) in no loaded pack" : "");
                }
            return _rigs;
        }
    }

    /// <summary>
    /// Viewport surfaces and GPU-ready textures, shared by every viewport (reset with the game). Bounded by bytes,
    /// least recently used out: unbounded, they grew ~12 MB per model shown and reached tens of GB on DL2.
    /// A surface is weighed with the textures it holds, so its budget also caps what it keeps alive.
    /// </summary>
    public Nightrunner.Core.ByteBudgetCache<string, Viewport.SceneMaterial> Surfaces { get; private set; } = NewSurfaceCache();
    public Nightrunner.Core.ByteBudgetCache<string, Viewport.SceneTexture?> Textures { get; private set; } = NewTextureCache();

    public const long SurfaceCacheBytes = 512L << 20;
    public const long TextureCacheBytes = 768L << 20;

    private static long Weigh(Viewport.SceneTexture? t) => t is null ? 0 : (t.Dds?.LongLength ?? 0) + (t.Bgra?.LongLength ?? 0);

    private static Nightrunner.Core.ByteBudgetCache<string, Viewport.SceneMaterial> NewSurfaceCache() =>
        new(SurfaceCacheBytes, m => Weigh(m.Albedo) + Weigh(m.Normal), StringComparer.OrdinalIgnoreCase);

    private static Nightrunner.Core.ByteBudgetCache<string, Viewport.SceneTexture?> NewTextureCache() =>
        new(TextureCacheBytes, Weigh, StringComparer.OrdinalIgnoreCase);
    private readonly Lock _modelsLock = new();

    /// <summary>
    /// Every <c>.model</c> of the open game's paks (data0 first; the runtime's paks only with runtime modding on),
    /// opened on first use from the pak list read with the packs. A pak that is not stock is marked custom. Null
    /// without a game.
    /// </summary>
    public ModelCatalog? Models
    {
        get
        {
            lock (_modelsLock)
            if (_models is null && Install is { } install)
            {
                using var op = Log.Start("model", "index .model documents");
                var origins = Origins;
                _models = new ModelCatalog(_paks.Length > 0 ? _paks : install.Paks(Runtime), p => !origins.IsStock(p));
                op.Result = $"{_models.Models.Count:N0} models in {_models.PakPaths.Count} paks";
                foreach (var e in _models.Errors) Log.Warn("model", e);
            }
            return _models;
        }
    }

    private Task<MaterialUsage?>? _usage;
    private CancellationTokenSource? _usageCts;

    /// <summary>"Used by": scanned once per session in the background (after the packs are indexed), cancellable.</summary>
    public Task<MaterialUsage?> Usage()
    {
        // a stopped scan starts again on the next request
        if (_usage is not null && !(_usage.IsCompletedSuccessfully && _usage.Result is null)) return _usage;
        _usageCts = new CancellationTokenSource();
        var ct = _usageCts.Token;
        var catalog = Catalog;
        var models = Models;
        var loading = _loading;
        return _usage = Task.Run(async () =>
        {
            using var op = Log.Start("sdb", "scan meshes and models for material use");
            try
            {
                await loading;
                var u = MaterialUsage.Scan(catalog, models, ct);
                op.Result = $"{u.MeshesScanned:N0} meshes, {u.ModelsScanned:N0} models, {u.Elapsed.TotalSeconds:F1} s";
                return u;
            }
            catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException)
            {
                op.Result = "cancelled";
                return null;
            }
        });
    }

    public void CancelUsage() => _usageCts?.Cancel();

    // ---- content views ----------------------------------------------------------------------------------------

    private ContentView? _allView, _stockView;
    private readonly Lock _viewsLock = new();

    private void DropViews()
    {
        lock (_viewsLock)
        {
            _allView = null;
            _stockView = null;
        }
    }

    /// <summary>
    /// What the viewport resolves names against: with <paramref name="mods"/> everything the game loads (the catalog's
    /// order, mods first), without them the stock packs and paks only (<see cref="Origins"/>). With nothing modded
    /// loaded both are the same view. The stock view snapshots the packs indexed when it is first asked for after the
    /// packs finished loading; asked earlier it is built fresh each time.
    /// </summary>
    public ContentView Content(bool mods)
    {
        lock (_viewsLock)
        {
            _allView ??= new ContentView(this, null);
            if (mods) return _allView;
            if (_stockView is not null) return _stockView;
            var origins = Origins;
            var catalog = Catalog;
            bool loaded = _loading.IsCompleted;
            bool modded = catalog.IndexedPacks.Any(p => !origins.IsStock(p.Path)) || _paks.Any(p => !origins.IsStock(p));
            var view = modded ? new ContentView(this, origins) : _allView;
            if (loaded) _stockView = view;
            return view;
        }
    }

    // ---- meshes -----------------------------------------------------------------------------------------------

    private readonly List<(RpackCatalog Catalog, int Gid, Task<MeshModel> Model)> _meshes = [];

    /// <summary>
    /// A mesh decoded through the game's mesh backend, on a worker. The last few are kept, so the Viewport and the
    /// Inspector asking for the same mesh decode it once. Loose packs (no game) use the Chrome Engine backend: the
    /// object layout is read from the data either way.
    /// </summary>
    /// <summary>Decoded meshes kept: enough for a whole character model plus its skeleton, opened by viewport and inspector.</summary>
    public const int MeshCacheSize = 64;

    /// <summary>
    /// Start decoding every mesh (and the skeleton) a model draws, in parallel; resolution then hits the cache. Waits
    /// for the packs to be indexed first: a lookup made earlier sees only the packs indexed so far, and a custom pack
    /// still indexing would lose to the stock one.
    /// </summary>
    public async Task Prefetch(Nightrunner.Core.Model.ModelDocument doc, RpackCatalog? lookup = null)
    {
        await WaitLoaded();
        var catalog = lookup ?? Catalog;
        var names = doc.Slots.Select(s => s.Drawn?.MeshName).Append(doc.Skeleton is { } k ? (k.EndsWith(".msh", StringComparison.OrdinalIgnoreCase) ? k[..^4] : k) : null)
            .Where(n => !string.IsNullOrEmpty(n)).Distinct(StringComparer.OrdinalIgnoreCase);
        var tasks = new List<Task>();
        foreach (var n in names)
            if (catalog.Lookup(n!, 0x10) is { Length: > 0 } g) tasks.Add(Mesh(g[0]));
        await Task.WhenAll(tasks).ContinueWith(_ => { }, TaskScheduler.Default);   // failures surface where the mesh is used
    }

    public Task<MeshModel> Mesh(int gid)
    {
        var catalog = Catalog;
        lock (_meshes)
        {
            foreach (var m in _meshes)
                if (ReferenceEquals(m.Catalog, catalog) && m.Gid == gid) return m.Model;
            var backend = Backend?.Meshes ?? GameBackends.For(GameProfile.Dltb).Meshes!;
            var task = Task.Run(() =>
            {
                var (entry, index) = catalog.Split(gid);
                return backend.Decode(entry.Pack!, index);
            });
            _meshes.Add((catalog, gid, task));
            if (_meshes.Count > MeshCacheSize) _meshes.RemoveAt(0);   // ponytail: FIFO by count, weigh by bytes if memory matters
            return task;
        }
    }

    /// <summary>Memory soak only (<c>NIGHTRUNNER_MEMCHECK</c>): drop the decoded meshes so their share can be measured.</summary>
    internal void DropMeshCache()
    {
        lock (_meshes) _meshes.Clear();
    }

    // ---- skins --------------------------------------------------------------------------------------------------

    private readonly Dictionary<(RpackCatalog, int), int> _skins = [];

    /// <summary>Raised when a window picks a skin for a mesh; the Viewport and the Inspector follow each other.</summary>
    public event Action<RpackCatalog, int, int>? SkinSelected;

    /// <summary>The skin last picked for a mesh this session, or null.</summary>
    public int? SkinOf(RpackCatalog catalog, int gid) => _skins.TryGetValue((catalog, gid), out int s) ? s : null;

    /// <summary>Raised when a bone is picked (in the Viewport or an Inspector bone list); carries the bone name.</summary>
    public event Action<string>? BoneSelected;

    public void SelectBone(string name) => BoneSelected?.Invoke(name);

    public void SelectSkin(RpackCatalog catalog, int gid, int skin)
    {
        if (SkinOf(catalog, gid) == skin) return;
        _skins[(catalog, gid)] = skin;
        SkinSelected?.Invoke(catalog, gid, skin);
    }

    public void Dispose()
    {
        foreach (var w in _watchers) w.Dispose();
        _watchers.Clear();
        _changed?.Dispose();
        _cts?.Cancel();
        _usageCts?.Cancel();
        _models?.Dispose();
        _prefabRig?.Dispose();
        Sdb.Dispose();
        Catalog.Dispose();
    }
}

/// <summary>
/// One way of resolving names for the viewport (<see cref="Workspace.Content"/>): everything the game loads, or the stock
/// packs and paks only. Meshes decode by global id, the same in both (a gid names one pack's resource), so the decoded
/// mesh cache is shared; the surface and texture caches are keyed by name, so the stock view has its own scope of them.
/// </summary>
public sealed class ContentView
{
    private readonly Workspace _ws;
    private readonly PackOrigins? _origins;
    private readonly RpackCatalog _full;
    private readonly Func<ModelCatalog?> _models;
    private readonly Func<Nightrunner.Core.Anim.AnimRigs?> _rigs;

    /// <param name="origins">Null: everything loaded. Else the stock view by these origins.</param>
    internal ContentView(Workspace ws, PackOrigins? origins)
    {
        _ws = ws;
        _origins = origins;
        _full = ws.Catalog;
        Stock = origins is not null;
        if (origins is null)
        {
            Catalog = _full;
            Surfaces = ws.Surfaces;
            Textures = ws.Textures;
            _models = () => ws.Models;
            _rigs = () => ws.Rigs;
        }
        else
        {
            Catalog = StockCopy.Catalog(_full, origins);
            Surfaces = ws.Surfaces.Scope(k => "stock|" + k);
            Textures = ws.Textures.Scope(k => "stock|" + k);
            var models = new Lazy<ModelCatalog?>(() => ws.Models?.Subset(origins.IsStock));
            var rigs = new Lazy<Nightrunner.Core.Anim.AnimRigs?>(() =>
            {
                if (Models is not { } m || Catalog.IndexedPacks.Length == 0) return null;
                using var op = Log.Start("anim", "read stock model rigs");
                var built = Nightrunner.Core.Anim.AnimRigs.Build(Catalog, m);
                op.Result = $"{built.Rigs.Count} rigs" + (built.Missing.Count > 0 ? $", {built.Missing.Count} skeleton(s) in no stock pack" : "");
                return built;
            });
            _models = () => models.Value;
            _rigs = () => rigs.Value;
        }
    }

    /// <summary>True for the stock view (Mods off with modded content loaded).</summary>
    public bool Stock { get; }

    /// <summary>Name lookups: the catalog itself, or its stock subset (same global ids).</summary>
    public RpackCatalog Catalog { get; }

    /// <summary>The <c>.model</c> documents: every pak, or the stock paks only (a mod pak neither shows nor overrides).</summary>
    public ModelCatalog? Models => _models();

    /// <summary>The rigs <see cref="Models"/> name, built from <see cref="Catalog"/>'s skeletons.</summary>
    public Nightrunner.Core.Anim.AnimRigs? Rigs => _rigs();

    public Nightrunner.Core.ByteBudgetCache<string, Viewport.SceneMaterial> Surfaces { get; }
    public Nightrunner.Core.ByteBudgetCache<string, Viewport.SceneTexture?> Textures { get; }

    /// <summary>Whether a global id is a resource of this view's packs.</summary>
    public bool Has(int gid)
    {
        if (_origins is null) return true;
        try { return _origins.IsStock(_full.Split(gid).Entry.Path); }
        catch (ArgumentOutOfRangeException) { return false; }
    }

    /// <summary>A pack or pak path of this view.</summary>
    public bool HasPath(string path) => _origins?.IsStock(path) ?? true;

    /// <summary>
    /// The resource this view shows for <paramref name="gid"/>: itself when its pack is in the view, else the stock copy
    /// (same name and type, first stock pack), else null — only a mod has it.
    /// </summary>
    public int? Resource(int gid) => _origins is null ? gid : StockCopy.TryResource(_full, gid, _origins);

    /// <summary>The <c>.model</c> this view shows for <paramref name="entry"/>: its winning provider here, or null (only a mod has it).</summary>
    public ModelEntry? Model(ModelEntry entry)
    {
        if (_origins is null) return entry;
        if (HasPath(entry.Pak) && Models is { } m && m.Models.FirstOrDefault(x => x.Pak == entry.Pak && x.Member.Index == entry.Member.Index) is { } same)
            return same;
        return Models?.Models.LastOrDefault(x => x.Basename == entry.Basename);
    }

    /// <summary>
    /// The clip a SeqTrack plays: the game's pick (<see cref="Nightrunner.Core.Anim.AnimCatalog.ClipOf"/>) with mods, the
    /// stock packs' in pack order (streamable first) without. Null when the view has no copy.
    /// </summary>
    public Nightrunner.Core.Anim.ClipRef? Clip(Nightrunner.Core.Anim.AnimCatalog animations, string anm2Name)
    {
        if (_origins is null) return animations.ClipOf(anm2Name);
        if (Nightrunner.Core.Anim.AnimCatalog.Clip(Catalog, anm2Name) is not { } hit) return null;
        var label = Catalog.IndexedPacks.FirstOrDefault(p => ReferenceEquals(p.Pack, hit.Pack))?.Label ?? "";
        return new Nightrunner.Core.Anim.ClipRef(hit.Pack, hit.Index, label);
    }
}
