using System.IO;
using System.Numerics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using HelixToolkit;
using HelixToolkit.Maths;
using HelixToolkit.SharpDX;
using HelixToolkit.Wpf.SharpDX;
using Media = System.Windows.Media;
using Media3D = System.Windows.Media.Media3D;

namespace Nightrunner.UI.Viewport;

/// <summary>
/// The 3D view: a Direct3D 11 viewport (HelixToolkit.Wpf.SharpDX, presented through D3DImage so it docks, floats
/// and tabs like any WPF element). This file and <see cref="Gpu"/> are the only places that know about Helix;
/// callers hand it a <see cref="SceneModel"/>.
/// </summary>
/// <remarks>
/// Left drag orbits, middle (or shift + left) pans, the wheel zooms, F frames the model.
/// </remarks>
public sealed class MeshViewport : UserControl
{
    private readonly Viewport3DX _view;
    private readonly GroupModel3D _group = new();
    private readonly List<(ScenePart Part, MeshGeometryModel3D Model)> _models = [];
    private readonly HashSet<MeshGeometryModel3D> _hidden = [];
    private bool _wireframe;
    private readonly PointGeometryModel3D _joints = new() { Color = Media.Color.FromRgb(0xF2, 0xC0, 0x4E), Size = new Size(4, 4), DepthBias = -10_000_000 };
    private readonly LineGeometryModel3D _links = new() { Color = Media.Color.FromRgb(0x5E, 0xB5, 0xE8), Thickness = 1.2, DepthBias = -10_000_000 };
    private readonly PointGeometryModel3D _picked = new() { Color = Media.Color.FromRgb(0xFF, 0x55, 0x55), Size = new Size(9, 9), DepthBias = -10_100_000 };
    private SceneSkeleton? _skeleton;
    private Point _down;
    private Media3D.Rect3D _bounds = Media3D.Rect3D.Empty;

    public MeshViewport()
    {
        _view = new Viewport3DX
        {
            EffectsManager = Gpu.Effects,
            BackgroundColor = Media.Color.FromRgb(0x1B, 0x1D, 0x21),
            Camera = new PerspectiveCamera
            {
                Position = new Media3D.Point3D(0, 1, 4), LookDirection = new Media3D.Vector3D(0, -0.5, -4),
                UpDirection = new Media3D.Vector3D(0, 1, 0), NearPlaneDistance = 0.01, FarPlaneDistance = 10000,
            },
            ShowViewCube = false,
            ShowCoordinateSystem = true,
            CoordinateSystemLabelForeground = Media.Colors.Gray,
            ShowFrameRate = false,
            ShowTriangleCountInfo = false,
            IsShadowMappingEnabled = false,
            MSAA = MSAALevel.Four,
            FXAALevel = FXAALevel.None,
            EnableSwapChainRendering = false,
            ZoomExtentsWhenLoaded = false,
            IsInertiaEnabled = false,
            Focusable = true,
        };
        _view.InputBindings.Add(new MouseBinding(ViewportCommands.Rotate, new MouseGesture(MouseAction.LeftClick)));
        _view.InputBindings.Add(new MouseBinding(ViewportCommands.Pan, new MouseGesture(MouseAction.MiddleClick)));
        _view.InputBindings.Add(new MouseBinding(ViewportCommands.Pan, new MouseGesture(MouseAction.LeftClick, ModifierKeys.Shift)));
        _view.KeyDown += (_, e) =>
        {
            if (e.Key != Key.F || Keyboard.Modifiers != ModifierKeys.None) return;   // KeyGesture refuses a bare letter
            Frame();
            e.Handled = true;
        };
        _view.Items.Add(new AmbientLight3D { Color = Media.Color.FromRgb(0x50, 0x50, 0x55) });
        _view.Items.Add(new DirectionalLight3D { Color = Media.Color.FromRgb(0xD8, 0xD8, 0xD8), Direction = new Media3D.Vector3D(-0.4, -0.7, -1) });
        _view.Items.Add(new DirectionalLight3D { Color = Media.Color.FromRgb(0x50, 0x50, 0x58), Direction = new Media3D.Vector3D(0.6, 0.3, 1) });
        _view.Items.Add(_group);
        foreach (var overlay in new Element3D[] { _links, _joints, _picked })
        {
            overlay.Visibility = Visibility.Collapsed;
            overlay.IsHitTestVisible = false;
            _view.Items.Add(overlay);
        }
        _view.PreviewMouseLeftButtonDown += (_, e) => _down = e.GetPosition(_view);
        _view.PreviewMouseLeftButtonUp += (_, e) =>
        {
            var p = e.GetPosition(_view);
            if (!Bones || (p - _down).Length > 3) return;       // a drag orbits; only a click picks
            if (PickJoint(p) is { } j) JointClicked?.Invoke(j);
        };
        _view.MouseMove += (_, e) =>
        {
            if (!Bones || e.LeftButton == System.Windows.Input.MouseButtonState.Pressed) return;
            var j = PickJoint(e.GetPosition(_view));
            _view.ToolTip = j is { } i && _skeleton is { } sk ? sk.Names[i] : null;
        };
        _view.MouseDown += (_, _) => _view.Focus();
        Content = _view;
    }

    /// <summary>Why the renderer could not draw, or null.</summary>
    public string? RenderError => _view.RenderException?.Message;

    /// <summary>Replace what is shown. Null clears the view.</summary>
    public void Show(SceneModel? scene)
    {
        foreach (var (_, m) in _models) m.Dispose();
        _group.Children.Clear();
        _models.Clear();
        _hidden.Clear();
        _bounds = Media3D.Rect3D.Empty;
        _scene = scene;
        if (scene is null) return;
        _bounds = Bounds(scene);

        var materials = new MaterialSet();
        foreach (var part in scene.Parts)
        {
            var mat = materials.For(scene.Materials.GetValueOrDefault((part.Mesh, part.Slot)));
            var model = new MeshGeometryModel3D
            {
                Geometry = Geometry(part),
                Material = mat,
                CullMode = SharpDX.Direct3D11.CullMode.None,
                RenderWireframe = _wireframe,
                WireframeColor = Media.Color.FromRgb(0xE0, 0xE0, 0xE0),
            };
            Surface(model, scene.Materials.GetValueOrDefault((part.Mesh, part.Slot)));
            _models.Add((part, model));
            _group.Children.Add(model);
        }
        Isolate(null);
    }

    /// <summary>Alpha-tested and blended surfaces go through the transparent pass; non-rendering ones are hidden.</summary>
    private void Surface(MeshGeometryModel3D model, SceneMaterial? m)
    {
        model.IsTransparent = m?.Alpha is Nightrunner.Core.Sdb.AlphaMode.Blend;
        if (m?.Hidden == true) _hidden.Add(model);
        else _hidden.Remove(model);
    }

    public bool Wireframe
    {
        get => _wireframe;
        set
        {
            _wireframe = value;
            foreach (var (_, m) in _models) m.RenderWireframe = value;
        }
    }

    /// <summary>Swap the materials of the meshes named in <paramref name="materials"/> (a new skin); geometry stays.</summary>
    public void SetMaterials(IReadOnlyDictionary<(int Mesh, int Slot), SceneMaterial> materials)
    {
        var made = new MaterialSet();
        foreach (var (p, m) in _models)
        {
            if (!materials.TryGetValue((p.Mesh, p.Slot), out var sm)) continue;
            m.Material = made.For(sm);
            Surface(m, sm);
        }
        ApplyVisibility();
    }

    /// <summary>Show only one submesh (mesh, entry, submesh), or everything again with null.</summary>
    public void Isolate((int Mesh, int Entry, int Submesh)? only)
    {
        _only = only;
        ApplyVisibility();
    }

    private (int Mesh, int Entry, int Submesh)? _only;
    private SceneModel? _scene;

    /// <summary>True when some shown part carries skin weights (a pose can move it).</summary>
    public bool CanPose => _scene is { } sc && sc.Skins.Count > 0 && _models.Any(m => m.Part.Joints is not null);

    /// <summary>
    /// Move the skinned parts to a pose — bone globals (4×4 row-major) of the skeleton the scene's skins index — or back
    /// to the stored geometry with null. CPU skinning, <c>v' = Σ w · Pose[bone] · InvBind · v</c>; normals and the
    /// tangent frame follow the blended 3×3.
    /// </summary>
    public void Pose(IReadOnlyList<double[]>? globals)
    {
        if (_scene is not { } scene) return;
        var palettes = new System.Collections.Concurrent.ConcurrentDictionary<int, double[][]>();
        var work = _models.Where(m => m.Part.Joints is not null && scene.Skins.ContainsKey(m.Part.Mesh)).ToList();
        var results = new (Vector3Collection P, Vector3Collection N, Vector3Collection T, Vector3Collection B)[work.Count];
        Parallel.For(0, work.Count, w =>
        {
            var p = work[w].Part;
            if (globals is null)
            {
                results[w] = (Vec(p.Positions), Vec(p.Normals), Vec(p.Tangents), Vec(p.Bitangents));
                return;
            }
            var skin = scene.Skins[p.Mesh];
            var pal = palettes.GetOrAdd(p.Mesh, _ => skin.Bone.Select((b, e) =>
                b >= 0 && b < globals.Count ? Nightrunner.Core.Cast.ModelCast.Mul(globals[b], skin.InvBind[e]) : Identity).ToArray());
            int n = p.Positions.Length / 3;
            var pos = new Vector3Collection(n);
            var nrm = new Vector3Collection(n);
            var tan = new Vector3Collection(n);
            var bit = new Vector3Collection(n);
            Span<double> m = stackalloc double[12];
            for (int i = 0; i < n; i++)
            {
                m.Clear();
                double total = 0;
                for (int k = 0; k < 4; k++)
                {
                    float wt = p.Weights![i * 4 + k];
                    if (wt <= 0) continue;
                    var mat = pal[Math.Min(p.Joints![i * 4 + k], pal.Length - 1)];
                    for (int j = 0; j < 12; j++) m[j] += wt * mat[j];
                    total += wt;
                }
                if (total <= 0) { m.Clear(); m[0] = m[5] = m[10] = 1; }
                else if (Math.Abs(total - 1) > 1e-6) for (int j = 0; j < 12; j++) m[j] /= total;
                pos.Add(Apply(m, p.Positions, i, true));
                nrm.Add(Vector3.Normalize(Apply(m, p.Normals, i, false)));
                tan.Add(Vector3.Normalize(Apply(m, p.Tangents, i, false)));
                bit.Add(Vector3.Normalize(Apply(m, p.Bitangents, i, false)));
            }
            results[w] = (pos, nrm, tan, bit);
        });
        for (int w = 0; w < work.Count; w++)
            if (work[w].Model.Geometry is MeshGeometry3D geo)
            {
                geo.Positions = results[w].P;
                geo.Normals = results[w].N;
                geo.Tangents = results[w].T;
                geo.BiTangents = results[w].B;
            }
    }

    private static readonly double[] Identity = [1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1];

    private static Vector3 Apply(Span<double> m, float[] v, int i, bool translate)
    {
        double x = v[i * 3], y = v[i * 3 + 1], z = v[i * 3 + 2];
        return new Vector3(
            (float)(m[0] * x + m[1] * y + m[2] * z + (translate ? m[3] : 0)),
            (float)(m[4] * x + m[5] * y + m[6] * z + (translate ? m[7] : 0)),
            (float)(m[8] * x + m[9] * y + m[10] * z + (translate ? m[11] : 0)));
    }

    private static Vector3Collection Vec(float[] a)
    {
        var c = new Vector3Collection(a.Length / 3);
        for (int i = 0; i + 2 < a.Length; i += 3) c.Add(new Vector3(a[i], a[i + 1], a[i + 2]));
        return c;
    }

    private void ApplyVisibility()
    {
        foreach (var (p, m) in _models)
            m.Visibility = Visible(p) && !_hidden.Contains(m) && (_only is not { } o || (p.Mesh == o.Mesh && p.Entry == o.Entry && p.Submesh == o.Submesh))
                ? Visibility.Visible : Visibility.Collapsed;
    }

    private Func<ScenePart, bool> _visible = _ => true;

    /// <summary>Which parts may show at all (per-slot toggles of an assembled model); isolation narrows it further.</summary>
    public Func<ScenePart, bool> Visible
    {
        get => _visible;
        set
        {
            _visible = value;
            ApplyVisibility();
        }
    }

    // ---- bones ---------------------------------------------------------------------------------------------------

    /// <summary>Raised when a joint is clicked (index into the shown skeleton).</summary>
    public event Action<int>? JointClicked;

    /// <summary>The skeleton the Bones overlay draws; null clears it.</summary>
    public void ShowSkeleton(SceneSkeleton? skeleton)
    {
        _skeleton = skeleton;
        HighlightJoint(null);
        if (skeleton is null)
        {
            _joints.Geometry = null;
            _links.Geometry = null;
            return;
        }
        int n = skeleton.Names.Length;
        var pts = new Vector3Collection(n);
        for (int i = 0; i < n; i++) pts.Add(new Vector3(skeleton.Positions[i * 3], skeleton.Positions[i * 3 + 1], skeleton.Positions[i * 3 + 2]));
        _joints.Geometry = new PointGeometry3D { Positions = pts };
        var linePts = new Vector3Collection();
        var idx = new IntCollection();
        for (int i = 0; i < n; i++)
        {
            int p = skeleton.Parents[i];
            if (p < 0 || p >= n) continue;
            idx.Add(linePts.Count); linePts.Add(pts[p]);
            idx.Add(linePts.Count); linePts.Add(pts[i]);
        }
        _links.Geometry = linePts.Count == 0 ? null : new LineGeometry3D { Positions = linePts, Indices = idx };
        Bones = _bones;
    }

    private bool _bones;

    /// <summary>Draw the skeleton over the model (depth-biased so it reads through the mesh).</summary>
    public bool Bones
    {
        get => _bones;
        set
        {
            _bones = value;
            var v = value && _skeleton is not null ? Visibility.Visible : Visibility.Collapsed;
            _joints.Visibility = _links.Visibility = v;
            _picked.Visibility = value && _picked.Geometry is not null ? Visibility.Visible : Visibility.Collapsed;
            if (!value) _view.ToolTip = null;
        }
    }

    public void HighlightJoint(int? index)
    {
        if (index is not { } i || _skeleton is not { } s || i < 0 || i >= s.Names.Length)
        {
            _picked.Geometry = null;
            _picked.Visibility = Visibility.Collapsed;
            return;
        }
        _picked.Geometry = new PointGeometry3D
        {
            Positions = new Vector3Collection { new Vector3(s.Positions[i * 3], s.Positions[i * 3 + 1], s.Positions[i * 3 + 2]) },
        };
        _picked.Visibility = _bones ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>The joint nearest the point, within 8 px on screen.</summary>
    public int? PickJoint(Point p)
    {
        if (_skeleton is not { } s) return null;
        var m = _view.GetScreenViewProjectionMatrix3D();
        int? best = null;
        double bestD = 8 * 8;
        for (int i = 0; i < s.Names.Length; i++)
        {
            var q = m.Transform(new Media3D.Point4D(s.Positions[i * 3], s.Positions[i * 3 + 1], s.Positions[i * 3 + 2], 1));
            if (q.W <= 0) continue;
            double dx = q.X / q.W - p.X, dy = q.Y / q.W - p.Y, d = dx * dx + dy * dy;
            if (d < bestD) { bestD = d; best = i; }
        }
        return best;
    }

    /// <summary>
    /// Reset the view: centred on the mesh's own finite positions, straight on from the front. Helix's ZoomExtents is
    /// not used: in 3.1.2 it put the camera off the model (target y −0.58 for a torso spanning y 0.88–1.56).
    /// </summary>
    public void Frame()
    {
        if (_bounds.IsEmpty) return;
        var b = _bounds;
        var center = new Media3D.Point3D(b.X + b.SizeX / 2, b.Y + b.SizeY / 2, b.Z + b.SizeZ / 2);
        FrameOn(center, Math.Sqrt(b.SizeX * b.SizeX + b.SizeY * b.SizeY + b.SizeZ * b.SizeZ) / 2);
    }

    /// <summary>Frame the scene from a direction turned <paramref name="yaw"/>° about Y and raised <paramref name="pitch"/>° (dock check).</summary>
    public void FrameFrom(double yaw, double pitch)
    {
        Frame();
        if (_view.Camera is not PerspectiveCamera cam || _bounds.IsEmpty) return;
        var b = _bounds;
        var center = new Media3D.Point3D(b.X + b.SizeX / 2, b.Y + b.SizeY / 2, b.Z + b.SizeZ / 2);
        double distance = (center - cam.Position).Length;
        double y = yaw * Math.PI / 180, p = pitch * Math.PI / 180;
        var dir = new Media3D.Vector3D(-Math.Sin(y) * Math.Cos(p), -Math.Sin(p), -Math.Cos(y) * Math.Cos(p));
        cam.Position = center - dir * distance;
        cam.LookDirection = dir * distance;
        cam.UpDirection = new Media3D.Vector3D(0, 1, 0);
    }

    /// <summary>
    /// Look straight at <paramref name="center"/> from the front (down −Z, Y up — the way characters face) from far
    /// enough to fit a sphere of <paramref name="radius"/>: Frame is also the view reset.
    /// </summary>
    public void FrameOn(Media3D.Point3D center, double radius)
    {
        if (_view.Camera is not PerspectiveCamera cam) return;
        radius = Math.Max(1e-3, radius);
        cam.FieldOfView = Fov;
        double distance = radius / Math.Sin(cam.FieldOfView * Math.PI / 360) * 1.05;
        var dir = new Media3D.Vector3D(0, 0, -1);
        cam.Position = center - dir * distance;
        cam.LookDirection = dir * distance;
        cam.UpDirection = new Media3D.Vector3D(0, 1, 0);
        cam.NearPlaneDistance = Math.Max(1e-4, distance / 1000);
        cam.FarPlaneDistance = distance * 100;
    }

    /// <summary>The orbit view's field of view (vertical, degrees), restored by every frame.</summary>
    public const double Fov = 45;

    /// <summary>
    /// The first-person field of view (vertical, degrees): 90° across at <paramref name="aspect"/>, as a first-person
    /// game shows its arms, kept within 60–75° so a tall or square panel still frames them.
    /// </summary>
    public static double EyeFov(double aspect) =>
        Math.Clamp(2 * Math.Atan(1 / Math.Max(0.1, aspect)) * 180 / Math.PI, 60, 75);

    /// <summary>The near plane in Eye: the FPP hands and a held weapon come within a few centimetres of the camera.</summary>
    public const double EyeNear = 0.005;

    /// <summary>
    /// Look through a camera bone (4×4 row-major global): its −Y column is the view direction and −Z the up
    /// direction — measured on <c>eyecamera</c>, whose −Y points where the FPP body faces (+Z) and −Z up in every
    /// idle; X is then the right hand side. The bone's scale (≈1.07 in some clips) is normalised away. The view widens
    /// to <see cref="EyeFov"/> with a <see cref="EyeNear"/> near plane (the 45° orbit view and 1 cm plane showed only
    /// the middle of the hands and cut them off).
    /// </summary>
    public void Eye(double[] g)
    {
        if (_view.Camera is not PerspectiveCamera cam) return;
        var look = new Media3D.Vector3D(-g[1], -g[5], -g[9]);
        var up = new Media3D.Vector3D(-g[2], -g[6], -g[10]);
        if (look.Length < 1e-9 || up.Length < 1e-9) return;
        look.Normalize();
        up.Normalize();
        cam.Position = new Media3D.Point3D(g[3], g[7], g[11]);
        cam.LookDirection = look;
        cam.UpDirection = up;
        cam.FieldOfView = EyeFov(_view.ActualHeight > 0 ? _view.ActualWidth / _view.ActualHeight : 16.0 / 9);
        cam.NearPlaneDistance = EyeNear;
        cam.FarPlaneDistance = 200;
    }

    private static Media3D.Rect3D Bounds(SceneModel scene)
    {
        double x0 = double.MaxValue, y0 = double.MaxValue, z0 = double.MaxValue;
        double x1 = double.MinValue, y1 = double.MinValue, z1 = double.MinValue;
        foreach (var p in scene.Parts)
            for (int i = 0; i + 2 < p.Positions.Length; i += 3)
            {
                float x = p.Positions[i], y = p.Positions[i + 1], z = p.Positions[i + 2];
                if (!float.IsFinite(x) || !float.IsFinite(y) || !float.IsFinite(z)) continue;   // 17 shipped entries hold non-finite values
                x0 = Math.Min(x0, x); y0 = Math.Min(y0, y); z0 = Math.Min(z0, z);
                x1 = Math.Max(x1, x); y1 = Math.Max(y1, y); z1 = Math.Max(z1, z);
            }
        return x0 > x1 ? Media3D.Rect3D.Empty : new Media3D.Rect3D(x0, y0, z0, x1 - x0, y1 - y0, z1 - z0);
    }

    public new void Focus() => _view.Focus();

    private static MeshGeometry3D Geometry(ScenePart p)
    {
        int n = p.Positions.Length / 3;
        var pos = new Vector3Collection(n);
        var nrm = new Vector3Collection(n);
        var tan = new Vector3Collection(n);
        var bit = new Vector3Collection(n);
        var uv = new Vector2Collection(n);
        for (int i = 0; i < n; i++)
        {
            pos.Add(new Vector3(p.Positions[i * 3], p.Positions[i * 3 + 1], p.Positions[i * 3 + 2]));
            nrm.Add(new Vector3(p.Normals[i * 3], p.Normals[i * 3 + 1], p.Normals[i * 3 + 2]));
            tan.Add(new Vector3(p.Tangents[i * 3], p.Tangents[i * 3 + 1], p.Tangents[i * 3 + 2]));
            bit.Add(new Vector3(p.Bitangents[i * 3], p.Bitangents[i * 3 + 1], p.Bitangents[i * 3 + 2]));
            uv.Add(new Vector2(p.Uvs[i * 2], p.Uvs[i * 2 + 1]));
        }
        return new MeshGeometry3D
        {
            Positions = pos, Normals = nrm, Tangents = tan, BiTangents = bit, TextureCoordinates = uv,
            Indices = new IntCollection(p.Indices),
        };
    }

    /// <summary>
    /// One Phong material per <see cref="SceneMaterial"/> and one GPU texture per <see cref="SceneTexture"/> within a
    /// scene. Keyed per part, a prefab placing 565 meshes uploaded the same albedo and normal map once per
    /// placement and committed ~15 GB; the surface caches hand out one instance per material, so sharing by reference
    /// uploads each texture once.
    /// </summary>
    private sealed class MaterialSet
    {
        private readonly Dictionary<SceneMaterial, PhongMaterial> _materials = new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<SceneTexture, TextureModel?> _textures = new(ReferenceEqualityComparer.Instance);
        private PhongMaterial? _plain;

        public PhongMaterial For(SceneMaterial? m)
        {
            if (m is null) return _plain ??= Material(null, this);
            if (!_materials.TryGetValue(m, out var mat)) _materials[m] = mat = Material(m, this);
            return mat;
        }

        public TextureModel? Texture(SceneTexture t)
        {
            if (!_textures.TryGetValue(t, out var tm)) _textures[t] = tm = MeshViewport.Texture(t);
            return tm;
        }
    }

    private static PhongMaterial Material(SceneMaterial? m, MaterialSet set)
    {
        var mat = new PhongMaterial
        {
            DiffuseColor = new Color4(0.72f, 0.72f, 0.74f, 1f),
            AmbientColor = new Color4(0.25f, 0.25f, 0.27f, 1f),
            SpecularColor = new Color4(0.08f, 0.08f, 0.08f, 1f),
            SpecularShininess = 24,
        };
        if (m?.Albedo is { } a && set.Texture(a) is { } at)
        {
            mat.DiffuseMap = at;
            mat.DiffuseColor = m.Tint is [var r, var g, var b, ..] ? new Color4(r, g, b, 1f) : new Color4(1f, 1f, 1f, 1f);
        }
        if (m?.Normal is { } n && set.Texture(n) is { } nt) mat.NormalMap = nt;
        return mat;
    }

    private static TextureModel? Texture(SceneTexture t) =>
        t.Dds is null && t.Bgra is null ? null : new TextureModel(Guid.NewGuid(), new Loader(t));

    /// <summary>
    /// Hands Helix the texture every time it asks — first upload and again after a device reset. A stream passed
    /// once (Helix's autoClose path) cannot be re-read, and the texture would come back empty after device loss.
    /// </summary>
    private sealed class Loader(SceneTexture t) : ITextureInfoLoader
    {
        public TextureInfo Load(Guid id) => t.Dds is { } dds
            ? new TextureInfo(new MemoryStream(dds, writable: false), false)
            : new TextureInfo(t.Bgra!, SharpDX.DXGI.Format.B8G8R8A8_UNorm, t.Width, t.Height, true);

        public void Complete(Guid id, TextureInfo? info, bool succeeded) => (info?.Texture as IDisposable)?.Dispose();
    }
}
