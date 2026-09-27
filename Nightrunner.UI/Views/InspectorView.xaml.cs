using System.Windows;
using System.Windows.Controls;
using Nightrunner.UI.Views.Inspectors;

namespace Nightrunner.UI.Views;

/// <summary>
/// The Inspector: one window that shows whatever the active panel has selected. Each kind of selection has its
/// own view, built once and reused, so switching back and forth keeps its scroll position and toggles.
/// </summary>
public partial class InspectorView : UserControl
{
    private readonly PanelContext _ctx;
    private PackInspector? _pack;
    private ResourceInspector? _resource;
    private TextureInspector? _texture;
    private ProjectInspector? _project;
    private MaterialInspector? _material;
    private MeshInspector? _mesh;
    private ModelInspector? _model;
    private ProjectModelInspector? _projectModel;
    private ProjectSceneInspector? _projectScene;
    private SequenceInspector? _sequence;
    private PrefabInspector? _prefab;
    private ModInspector? _mod;
    private BuildItemInspector? _buildItem;
    private AudioInspector? _audio;

    public InspectorView(PanelContext ctx)
    {
        _ctx = ctx;
        InitializeComponent();
        ctx.Selection.Changed += Show;
        Loaded += (_, _) => Show(ctx.Selection.Current, ctx.Selection.Source);
    }

    /// <summary>Stop following the selection — called when this window is closed for good.</summary>
    public void Detach()
    {
        _ctx.Selection.Changed -= Show;
        _audio?.CancelProbe();
    }

    private void Show(Selected? what, string source)
    {
        if (what is not Selected.AudioEntry) _audio?.CancelProbe();
        Origin.Text = what is null ? "" : source;
        switch (what)
        {
            case Selected.Pack pack:
                Kind.Text = "pack";
                Host.Content = (_pack ??= new PackInspector()).With(pack);
                break;
            case Selected.Resource resource:
                Kind.Text = "resource";
                Host.Content = (_resource ??= new ResourceInspector()).With(resource);
                break;
            case Selected.Resources many:
                Kind.Text = $"{many.Gids.Length:N0} resources";
                Host.Content = (_resource ??= new ResourceInspector()).With(many);
                break;
            case Selected.Texture texture:
                Kind.Text = "texture";
                Host.Content = (_texture ??= new TextureInspector()).With(texture);
                break;
            case Selected.Mesh mesh:
                Kind.Text = "mesh";
                Host.Content = (_mesh ??= new MeshInspector(_ctx.Workspace, _ctx.Host)).With(mesh);
                break;
            case Selected.Model model:
                Kind.Text = "model";
                Host.Content = (_model ??= new ModelInspector(_ctx.Workspace, _ctx.Host)).With(model);
                break;
            case Selected.Prefab pf:
                Kind.Text = "prefab";
                Host.Content = (_prefab ??= new PrefabInspector(_ctx)).With(pf);
                break;
            case Selected.Sequence seq:
                Kind.Text = "sequence";
                Host.Content = (_sequence ??= new SequenceInspector()).With(seq);
                break;
            case Selected.ProjectModel pm:
                Kind.Text = "model override";
                Host.Content = (_projectModel ??= new ProjectModelInspector()).With(pm);
                break;
            case Selected.ProjectScene ps:
                Kind.Text = "scene";
                Host.Content = (_projectScene ??= new ProjectSceneInspector()).With(ps);
                break;
            case Selected.BuildItem bi:
                Kind.Text = bi.Info.Kind;
                Host.Content = (_buildItem ??= new BuildItemInspector(_ctx)).With(bi);
                break;
            case Selected.Project project:
                Kind.Text = "project";
                Host.Content = (_project ??= new ProjectInspector(_ctx.Workspace)).With(project.Value);
                break;
            case Selected.Mod mod:
                Kind.Text = "mod";
                Host.Content = (_mod ??= new ModInspector(_ctx.Workspace)).With(mod);
                break;
            case Selected.Material material:
                Kind.Text = "material";
                Host.Content = (_material ??= new MaterialInspector(_ctx.Workspace, _ctx.Host)).With(material);
                break;
            case Selected.AudioArchive archive:
                Kind.Text = "audio archive";
                Host.Content = (_audio ??= new AudioInspector()).With(archive);
                break;
            case Selected.AudioEntry entry:
                Kind.Text = "audio entry";
                Host.Content = (_audio ??= new AudioInspector()).With(entry);
                break;
            default:
                Kind.Text = "nothing selected";
                Host.Content = null;
                break;
        }
        Empty.Visibility = Host.Content is null ? Visibility.Visible : Visibility.Collapsed;
    }
}
