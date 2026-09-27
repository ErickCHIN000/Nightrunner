using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Nightrunner.Core.Rpack;
using Nightrunner.Core.Texture;

namespace Nightrunner.UI.Views.Inspectors;

/// <summary>A texture: the decoded surface, a mip and face picker, and everything in its IMGC header.</summary>
public partial class TextureInspector : UserControl
{
    private RpackCatalog? _catalog;
    private int _gid = -1;
    private TextureResource? _texture;
    private DecodedImage? _decoded;
    private CancellationTokenSource? _cts;
    private bool _ready = true;

    public TextureInspector() => InitializeComponent();

    public TextureInspector With(Selected.Texture selection)
    {
        if (_catalog == selection.Catalog && _gid == selection.Gid) return this;
        _catalog = selection.Catalog;
        _gid = selection.Gid;

        var (entry, index) = selection.Catalog.Split(selection.Gid);
        try
        {
            _texture = TextureResource.Open(entry.Pack!, index);
        }
        catch (Exception e) when (e is ImgcException or RpackFormatException)
        {
            _texture = null;
            Info.Text = $"{entry.Pack!.Name(index)}\n{e.Message}";
            PreviewNote.Text = e.Message;
            Preview.Source = null;
            MipPicker.ItemsSource = null;
            return this;
        }

        BuildPickers();
        ShowInfo(entry.Label, index);
        _ = DecodeAsync();
        return this;
    }

    private void BuildPickers()
    {
        bool was = _ready;
        _ready = false;
        var mips = new List<string>();
        if (_texture is { } tex)
            for (int m = 0; m < tex.Header.MipCount; m++)
            {
                var (w, h, _) = ImgcHeader.MipDims(tex.Header.Width, tex.Header.Height, tex.Header.Depth, m);
                mips.Add($"{m}  {w}x{h}");
            }
        MipPicker.ItemsSource = mips;
        MipPicker.SelectedIndex = mips.Count > 0 ? 0 : -1;

        int faces = _texture?.Header.Faces ?? 1;
        FacePicker.ItemsSource = Enumerable.Range(0, faces).Select(i => i.ToString()).ToList();
        FacePicker.SelectedIndex = 0;
        var show = faces > 1 ? Visibility.Visible : Visibility.Collapsed;
        FacePicker.Visibility = show;
        FaceLabel.Visibility = show;
        _ready = was;
    }

    private void Mip_Changed(object sender, RoutedEventArgs e)
    {
        if (_ready) _ = DecodeAsync();
    }

    private void Mip_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_ready) _ = DecodeAsync();
    }

    private void Render_Click(object sender, RoutedEventArgs e) => Render();

    private async Task DecodeAsync()
    {
        _cts?.Cancel();
        var cts = new CancellationTokenSource();
        _cts = cts;
        _decoded = null;
        Preview.Source = null;

        if (_texture is not { } tex) return;
        if (tex.Header.HeaderOnly)
        {
            PreviewNote.Text = $"header-only record → {tex.Header.Reference}";
            return;
        }
        if (tex.PayloadProblem is { } problem)
        {
            PreviewNote.Text = problem;
            return;
        }
        if (TextureDecoder.Reason(tex.Header.Format) is { } why)
        {
            PreviewNote.Text = why;
            return;
        }

        int mip = Math.Max(0, MipPicker.SelectedIndex);
        int face = Math.Max(0, FacePicker.SelectedIndex);
        var level = tex.Levels.FirstOrDefault(l => l.Mip == mip && l.Face == face);
        if (level.Width == 0)
        {
            PreviewNote.Text = "no such level";
            return;
        }

        bool normalZ = BtnNormal.IsChecked == true;
        PreviewNote.Text = "decoding…";
        try
        {
            var img = await Task.Run(() => tex.Decode(level, normalZ), cts.Token);
            if (cts.IsCancellationRequested) return;
            _decoded = img;
            PreviewNote.Text = "";
            Render();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            PreviewNote.Text = $"{ex.GetType().Name}: {ex.Message}";
        }
    }

    private void Render()
    {
        if (_decoded is not { } img)
        {
            Preview.Source = null;
            return;
        }
        var pixels = img.Bgra;
        if (BtnAlpha.IsChecked != true)
        {
            pixels = (byte[])pixels.Clone();
            for (int i = 3; i < pixels.Length; i += 4) pixels[i] = 255;
        }
        var bmp = BitmapSource.Create(img.Width, img.Height, 96, 96, PixelFormats.Bgra32, null, pixels, img.Stride);
        bmp.Freeze();
        Preview.Source = bmp;

        if (BtnFit.IsChecked == true)
        {
            // bound, not assigned: the viewport is not measured the first time a texture is shown
            Preview.SetBinding(MaxWidthProperty, new Binding("ViewportWidth") { Source = Scroll });
            Preview.SetBinding(MaxHeightProperty, new Binding("ViewportHeight") { Source = Scroll });
            Preview.Stretch = Stretch.Uniform;
            Preview.StretchDirection = StretchDirection.DownOnly;
        }
        else
        {
            BindingOperations.ClearBinding(Preview, MaxWidthProperty);
            BindingOperations.ClearBinding(Preview, MaxHeightProperty);
            Preview.MaxWidth = double.PositiveInfinity;
            Preview.MaxHeight = double.PositiveInfinity;
            Preview.Stretch = Stretch.None;
        }
    }

    private void ShowInfo(string packLabel, int index)
    {
        if (_texture is not { } tex) return;
        var h = tex.Header;
        var (min, max, mean) = h.Stats();
        var f = ImgcFormats.IsKnown(h.Format) ? ImgcFormats.Get(h.Format) : default;
        var sb = new StringBuilder();
        sb.AppendLine(tex.Name);
        sb.AppendLine($"{packLabel}   gid {_gid:N0}   logical #{index:N0}");
        sb.AppendLine($"{h.Width}x{h.Height}{(h.Depth > 1 ? "x" + h.Depth : "")}   {h.FormatName} " +
                      $"(id {h.Format}, {(f.Block ? $"{f.Unit} B/block" : $"{f.Unit} B/px")})   {h.Type}   " +
                      $"mips {h.MipCount}   flags 0x{h.Flags:X2}");
        sb.AppendLine(tex.BitmapPart is null
            ? "no bitmap part"
            : $"bitmap {Format.Size(tex.BitmapSize)}   level padding {tex.LevelPadding?.ToString() ?? "?"}   " +
              $"levels {tex.Levels.Count}");
        sb.Append($"min [{Fmt(min)}]  max [{Fmt(max)}]  mean [{Fmt(mean)}]");
        if (tex.PayloadProblem is { } p) sb.Append($"\n{p}");
        Info.Text = sb.ToString();

        static string Fmt(float[] v) => string.Join(" ", v.Select(x => x.ToString("0.###")));
    }
}
