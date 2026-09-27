using System.Buffers.Binary;
using Nightrunner.Core.Texture;

namespace Nightrunner.Core.Sdb;

public enum AlphaMode { Opaque, Mask, Blend }

/// <summary>
/// A preview surface for a material: either the plain <c>dif_0_tex</c> (<see cref="PlainTexture"/>, sampled as stored)
/// or a composed BGRA image, plus how its alpha is used.
/// </summary>
/// <param name="Tint">The plain texture's <c>dif_0_val</c> ("Diffuse scale") when it is not white: the viewer multiplies the
/// sampled colour by it. Null for composed images, which bake it in where their recipe uses it.</param>
public sealed record MaterialSurface(string Recipe, AlphaMode Alpha, float Cutoff, bool Hidden, DecodedImage? Image,
                                     string? PlainTexture, string? NormalTexture, IReadOnlyList<string> Warnings,
                                     float[]? Tint = null);

/// <summary>
/// Material-aware preview (port of <c>gui/matpreview.py::compose</c>). A viewer cannot run the game's shaders, so for a
/// few well-understood shader families the texture layers are baked into one image with an alpha mode:
/// <list type="bullet">
/// <item><b>eye_layers</b> — tokens ⊇ {eyes_blicks_on, od1_tex, od2_tex, off_tex}: iris <c>od2_tex</c> blended toward
/// sclera <c>od1_tex</c> by the sclera alpha (<c>of1_msk_range_min/max</c>, <c>of1_msk_opacity</c>), then toward the
/// veins map <c>dif_0_tex</c> by the red of <c>off_tex</c> (<c>off_msk_range_min/max</c>, <c>off_msk_opacity</c>).</item>
/// <item><b>dither_cutout</b> — <c>dit_0_tex</c> (hair, beards): colour <c>dif_0_tex × dif_0_val</c>, alpha the red of
/// <c>dit_0_tex</c>, tested at 0.25.</item>
/// <item><b>diffuse_opacity</b> — <c>opc_0_tex</c> (forearm hair, eye shadow, wet eye): colour as above, alpha
/// <c>(red·255 − low)/(high − low)</c> with <c>opc_3_ranges</c>, blended.</item>
/// <item><b>gradient_map</b> — no <c>dif_0_tex</c>, a "Diffuse gradient texture" <c>grd_0_tex</c> and an "Index map
/// texture" <c>idx_0_tex</c> (or <c>idx_0_const</c> under <c>idx_0_src_const_on</c>): each pixel's index (red, through
/// the "Index modifiers" <c>idx_0_range_in/out_min/max</c>) picks the column of the gradient, at the row
/// <c>grd_0_base</c>; with <c>dit_0_tex</c> (gradient-mapped hair) alpha-tested as <b>dither_cutout</b>. Not the
/// prototype's (C tier: the reading of the SDB preset annotations, and of the stock pixel shaders' dependent
/// <c>sample_l</c> of a gradient at u = index·scale + offset, v = an interpolated row). The per-instance row offset
/// (<c>grd_0_usr_on</c>: NPC colour variants) and the other row/column offset controls are runtime data and are
/// not applied; the base row is shown.</item>
/// <item><b>plain</b> — <c>dif_0_tex</c>, multiplied by <c>dif_0_val</c> ("Diffuse scale", as the composed recipes do)
/// through <see cref="MaterialSurface.Tint"/>; a material whose every variant has zero render passes is hidden.</item>
/// </list>
/// The first three recipes are the prototype's (B tier: an older tool's shader reading). Unsupported variations warn and
/// fall back to plain, as there. Layers the viewer does not compose are named in the warnings: the dye gradient
/// (<c>idx_tex</c> → <c>grd_tex</c>, which multiplies the colour but reads its index on UV1 by default,
/// <c>grd_uv_1_on</c>, so no UV0 image can hold it), and a gradient map over <c>dif_0_tex</c> (blended by the
/// gradient's alpha over a base whose texture slot is not pinned down).
/// </summary>
public static class MaterialPreview
{
    public const float HairCutoff = 0.25f;
    private static readonly string[] EyeTokens = ["eyes_blicks_on", "od1_tex", "od2_tex", "off_tex"];
    private static readonly string[] EyeUnsupported = ["od3_tex", "od4_tex", "off_frs_on"];
    private static readonly string[] OpcUnsupported = ["opc_uv_1_on", "opc_frs_on", "opc_usr_on", "opc_noise_tex", "opc_ovr_on"];
    private static readonly string[] DitUnsupported = ["dit_1_tex", "dit_usr_on", "dit_threshold_usr_on", "dit_0_clamp_u_on", "dit_0_clamp_v_on"];

    /// <summary>Tokens, parameter values (floats), and texture per parameter merged over every route and variant.</summary>
    public static (HashSet<string> Tokens, Dictionary<string, float[]> Values, Dictionary<string, string> Textures, List<string> Warnings)
        Facts(SdbMaterial m, IReadOnlyDictionary<string, string>? overrides = null)
    {
        var tokens = new HashSet<string>(StringComparer.Ordinal);
        var values = new Dictionary<string, float[]>(StringComparer.Ordinal);
        var strings = new Dictionary<string, string>(StringComparer.Ordinal);
        var tex = new Dictionary<string, string>(StringComparer.Ordinal);
        var warnings = new List<string>();
        foreach (var r in m.Routes)
        {
            foreach (var t in r.Tokens.Split(';', StringSplitOptions.RemoveEmptyEntries)) tokens.Add(t);
            foreach (var p in r.Parameters)
            {
                if (p.Name.Length == 0 || values.ContainsKey(p.Name) || strings.ContainsKey(p.Name)) continue;
                if (p.Type == 7 && p.ValueText is { Length: > 0 } s) strings[p.Name] = s;
                else if (p.Type is 2 or 3 or 4 or 5 && p.ValueHex is { } hex)
                {
                    var raw = Convert.FromHexString(hex);
                    var f = new float[raw.Length / 4];
                    for (int i = 0; i < f.Length; i++) f[i] = BinaryPrimitives.ReadSingleLittleEndian(raw.AsSpan(i * 4));
                    values[p.Name] = f;
                }
            }
            foreach (var b in r.Variants.SelectMany(v => v.Bindings))
            {
                if (string.IsNullOrEmpty(b.Parameter) || string.IsNullOrEmpty(b.Texture)) continue;
                if (tex.TryGetValue(b.Parameter, out var have) && have != b.Texture)
                {
                    string msg = $"variants disagree on {b.Parameter}";
                    if (!warnings.Contains(msg)) warnings.Add(msg);
                    continue;
                }
                tex.TryAdd(b.Parameter, b.Texture);
            }
        }
        foreach (var (name, s) in strings)
            if (name.EndsWith("_tex", StringComparison.Ordinal)) tex.TryAdd(name, s);
        foreach (var (p, t) in overrides ?? new Dictionary<string, string>())
            if (p.Length > 0 && t.Length > 0) tex[p] = t;
        return (tokens, values, tex, warnings);
    }

    /// <summary>
    /// Compose the preview. <paramref name="fetch"/> decodes a texture by name to BGRA (null when unavailable); it is
    /// only called for composed recipes — plain returns the texture name for the caller to load as it likes.
    /// </summary>
    public static MaterialSurface Compose(SdbMaterial? m, IReadOnlyDictionary<string, string>? overrides,
                                          Func<string, DecodedImage?> fetch)
    {
        if (m is null) return new("none", AlphaMode.Opaque, 0.5f, false, null, overrides?.GetValueOrDefault("dif_0_tex"), overrides?.GetValueOrDefault("nrm_0_tex"), ["material not in SDB"]);
        if (m.NonRendering) return new("non_rendering", AlphaMode.Opaque, 0.5f, true, null, null, null, []);
        var (tokens, values, tex, warnings) = Facts(m, overrides);
        string? dif = tex.GetValueOrDefault("dif_0_tex");
        string? nrm = tex.GetValueOrDefault("nrm_0_tex");
        MaterialSurface Plain(string? why = null)
        {
            if (why is not null) warnings.Add(why);
            if (dif is null) warnings.Add(NoColour(m, tex));
            else foreach (var layer in Unshown(tex)) warnings.Add(layer);
            float[]? tint = dif is not null && Tint(values) is var t && t.Any(x => Math.Abs(x - 1) > 1e-4f) ? t : null;
            return new(dif is null ? "none" : "plain", AlphaMode.Opaque, 0.5f, false, null, dif, nrm, warnings, tint);
        }

        if (EyeTokens.All(tokens.Contains) && new[] { "dif_0_tex", "od1_tex", "od2_tex", "off_tex" }.All(tex.ContainsKey))
            if (Eye(tokens, values, tex, fetch, warnings) is { } eye) return new("eye_layers", AlphaMode.Opaque, 0.5f, false, eye, null, nrm, warnings);
        if (tex.ContainsKey("dit_0_tex") && !tokens.Contains("opc_0_tex") && dif is not null)
            if (Cutout(tokens, values, tex, fetch, warnings) is { } cut) return new("dither_cutout", AlphaMode.Mask, HairCutoff, false, cut, null, nrm, warnings);
        if (tex.ContainsKey("opc_0_tex") && dif is not null)
            if (Opacity(tokens, values, tex, fetch, warnings) is { } opc) return new("diffuse_opacity", AlphaMode.Blend, 0.5f, false, opc, null, nrm, warnings);
        if (dif is null && tex.ContainsKey("grd_0_tex"))
            if (GradientMap(tokens, values, tex, fetch, warnings) is { } grd)
                return tex.ContainsKey("dit_0_tex") && !tokens.Contains("opc_0_tex")
                    ? new("gradient_map+dither_cutout", AlphaMode.Mask, HairCutoff, false, grd, null, nrm, warnings)
                    : new("gradient_map", AlphaMode.Opaque, 0.5f, false, grd, null, nrm, warnings);
        return Plain();
    }

    /// <summary>
    /// Why a material without <c>dif_0_tex</c> has no colour the viewer can show: the SDB material has no colour texture
    /// at all (glass, metal trims, threads: only normal / clip / occlusion maps), or its colour comes from a layer the
    /// viewer does not compose.
    /// </summary>
    public static string NoColour(SdbMaterial m, IReadOnlyDictionary<string, string> tex)
    {
        if (m.Routes.SelectMany(r => r.Variants).SelectMany(v => v.Bindings)
            .Any(b => b.Parameter == "dif_0_tex" && b.Texture is null && b.RuntimeIndex is not null))
            return "dif_0_tex is bound at runtime";
        if (tex.ContainsKey("grd_0_tex")) return "gradient map not composed";
        if (tex.ContainsKey("dif_1_tex")) return "colour only in the 2nd layer (dif_1_tex), not composed";
        if (tex.ContainsKey("grf_dif_tex")) return "colour only in the Fresnel gradient (grf_dif_tex), not composed";
        if (tex.ContainsKey("dye_tex")) return "colour only in the dye layer (dye_tex), not composed";
        var maps = tex.Keys.Where(k => !k.StartsWith("blood", StringComparison.Ordinal) && !k.StartsWith("carbon", StringComparison.Ordinal) &&
                                       !k.StartsWith("det_", StringComparison.Ordinal)).Order(StringComparer.Ordinal).ToList();
        return $"no colour texture ({m.Route?.Preset}{(maps.Count > 0 ? ": " + string.Join(", ", maps) : "")})";
    }

    /// <summary>Colour layers the plain recipe leaves out, as warnings.</summary>
    private static IEnumerable<string> Unshown(IReadOnlyDictionary<string, string> tex)
    {
        if (tex.ContainsKey("idx_tex") && tex.ContainsKey("grd_tex")) yield return "dye gradient (idx_tex on UV1, grd_tex) not applied";
        if (tex.ContainsKey("dye_tex")) yield return "dye layer (dye_tex) not applied";
        if (tex.ContainsKey("grd_0_tex")) yield return "gradient map (grd_0_tex) over dif_0_tex not applied";
    }

    private static float Num(Dictionary<string, float[]> v, string name, float fallback) =>
        v.TryGetValue(name, out var f) && f.Length > 0 ? f[0] : fallback;

    private static float[] Tint(Dictionary<string, float[]> v) =>
        v.TryGetValue("dif_0_val", out var f) && f.Length >= 3 && f.Take(3).All(float.IsFinite) ? f[..3] : [1, 1, 1];

    private static DecodedImage[]? Layers(Func<string, DecodedImage?> fetch, string[] names, List<string> warnings)
    {
        var imgs = names.Select(fetch).ToArray();
        var bad = names.Where((n, i) => imgs[i] is null).ToList();
        if (bad.Count > 0)
        {
            warnings.Add("missing " + string.Join(", ", bad));
            return null;
        }
        return imgs!;
    }

    /// <summary>Bilinear sample of one channel (0 B, 1 G, 2 R, 3 A) at the centre of target pixel (x, y) of w×h, as 0..1.</summary>
    private sealed class Sampler(DecodedImage img, int w, int h)
    {
        private readonly bool _same = img.Width == w && img.Height == h;

        public float this[int x, int y, int c]
        {
            get
            {
                if (_same) return img.Bgra[(y * w + x) * 4 + c] / 255f;
                float fx = (x + 0.5f) * img.Width / w - 0.5f, fy = (y + 0.5f) * img.Height / h - 0.5f;
                int x0 = Math.Clamp((int)MathF.Floor(fx), 0, img.Width - 1), y0 = Math.Clamp((int)MathF.Floor(fy), 0, img.Height - 1);
                int x1 = Math.Min(x0 + 1, img.Width - 1), y1 = Math.Min(y0 + 1, img.Height - 1);
                float tx = Math.Clamp(fx - x0, 0, 1), ty = Math.Clamp(fy - y0, 0, 1);
                float P(int px, int py) => img.Bgra[(py * img.Width + px) * 4 + c] / 255f;
                return (P(x0, y0) * (1 - tx) + P(x1, y0) * tx) * (1 - ty) + (P(x0, y1) * (1 - tx) + P(x1, y1) * tx) * ty;
            }
        }
    }

    private static DecodedImage? Eye(HashSet<string> tokens, Dictionary<string, float[]> v, Dictionary<string, string> tex,
                                     Func<string, DecodedImage?> fetch, List<string> warnings)
    {
        if (EyeUnsupported.Any(tokens.Contains)) warnings.Add("eye: extra offset layers / Fresnel not baked");
        var layers = Layers(fetch, [tex["dif_0_tex"], tex["od1_tex"], tex["od2_tex"], tex["off_tex"]], warnings);
        if (layers is null) return null;
        float amin = Num(v, "of1_msk_range_min", 0), amax = Num(v, "of1_msk_range_max", 1), opacity = Num(v, "of1_msk_opacity", 1);
        float mmin = Num(v, "off_msk_range_min", 0), mmax = Num(v, "off_msk_range_max", 1), mopacity = Num(v, "off_msk_opacity", 1);
        if (!new[] { amin, amax, opacity, mmin, mmax, mopacity }.All(float.IsFinite) || amax <= amin || mmax <= mmin)
        {
            warnings.Add("eye: invalid mask range");
            return null;
        }
        int w = layers.Max(l => l.Width), h = layers.Max(l => l.Height);
        var (veins, sclera, iris, mask) = (new Sampler(layers[0], w, h), new Sampler(layers[1], w, h), new Sampler(layers[2], w, h), new Sampler(layers[3], w, h));
        var outp = new byte[w * h * 4];
        Parallel.For(0, h, y =>
        {
            for (int x = 0; x < w; x++)
            {
                float alpha = Math.Clamp((sclera[x, y, 3] - amin) / (amax - amin), 0, 1) * opacity;
                float weight = 1 - mopacity + Math.Clamp((mask[x, y, 2] - mmin) / (mmax - mmin), 0, 1) * mopacity;
                int o = (y * w + x) * 4;
                for (int c = 0; c < 3; c++)
                {
                    float col = iris[x, y, c] * (1 - alpha) + sclera[x, y, c] * alpha;
                    col = col * (1 - weight) + veins[x, y, c] * weight;
                    outp[o + c] = (byte)MathF.Round(Math.Clamp(col, 0, 1) * 255);
                }
                outp[o + 3] = 255;
            }
        });
        return new DecodedImage(w, h, outp);
    }

    private static DecodedImage? Cutout(HashSet<string> tokens, Dictionary<string, float[]> v, Dictionary<string, string> tex,
                                        Func<string, DecodedImage?> fetch, List<string> warnings)
    {
        if (DitUnsupported.Any(tokens.Contains)) warnings.Add("cutout: runtime / clamped dither not evaluated");
        var layers = Layers(fetch, [tex["dif_0_tex"], tex["dit_0_tex"]], warnings);
        return layers is null ? null : Tinted(layers[0], layers[1], Tint(v), a => a);
    }

    private static DecodedImage? Opacity(HashSet<string> tokens, Dictionary<string, float[]> v, Dictionary<string, string> tex,
                                         Func<string, DecodedImage?> fetch, List<string> warnings)
    {
        if (OpcUnsupported.Any(tokens.Contains))
        {
            warnings.Add("opacity: UV1 / runtime mask not evaluated — shown opaque");
            return null;
        }
        float low = 0, high = 255;
        if (v.TryGetValue("opc_3_ranges", out var r) && r.Length >= 2) (low, high) = (r[0], r[1]);
        if (!float.IsFinite(low) || !float.IsFinite(high) || high <= low)
        {
            warnings.Add("opacity: invalid opc_3_ranges");
            return null;
        }
        var layers = Layers(fetch, [tex["dif_0_tex"], tex["opc_0_tex"]], warnings);
        return layers is null ? null : Tinted(layers[0], layers[1], Tint(v), a => Math.Clamp((a * 255 - low) / (high - low), 0, 1));
    }

    private static readonly string[] IndexSources = ["idx_0_src_vtx_on", "idx_0_src_ocl_on", "idx_0_src_rgh_on"];
    private static readonly string[] RowOffsets = ["grd_0_ov_tex", "grd_0_pos_on", "grd_0_dir_on", "grd_0_dir_h_on", "grd_0_vol_tex"];

    /// <summary>
    /// The gradient map: gradient row <c>grd_0_base</c>, column = the index (idx_0_tex red, or idx_0_const) after the
    /// index modifiers, sampled linearly between texel centres. Colour only; alpha is opaque, or the dither coverage
    /// (red of dit_0_tex) when the material has one.
    /// </summary>
    private static DecodedImage? GradientMap(HashSet<string> tokens, Dictionary<string, float[]> v, Dictionary<string, string> tex,
                                             Func<string, DecodedImage?> fetch, List<string> warnings)
    {
        if (IndexSources.FirstOrDefault(tokens.Contains) is { } source)
        {
            warnings.Add($"gradient map: index source {source} is per vertex / per pixel runtime data");
            return null;
        }
        bool constant = tokens.Contains("idx_0_src_const_on");
        if (!constant && !tex.ContainsKey("idx_0_tex"))
        {
            warnings.Add("gradient map: no idx_0_tex");
            return null;
        }
        if (RowOffsets.Where(tokens.Contains).ToList() is { Count: > 0 } offsets)
            warnings.Add($"gradient map: row offsets not applied ({string.Join(", ", offsets)})");
        warnings.Add("gradient map: base row (per-instance row offset not applied)");
        var names = new List<string> { tex["grd_0_tex"] };
        if (!constant) names.Add(tex["idx_0_tex"]);
        if (tex.ContainsKey("dit_0_tex")) names.Add(tex["dit_0_tex"]);
        var layers = Layers(fetch, [.. names], warnings);
        if (layers is null) return null;
        var grad = layers[0];
        float inMin = Num(v, "idx_0_range_in_min", 0), inMax = Num(v, "idx_0_range_in_max", 1);
        float outMin = Num(v, "idx_0_range_out_min", 0), outMax = Num(v, "idx_0_range_out_max", 1);
        float baseRow = Num(v, "grd_0_base", 0), constIndex = Num(v, "idx_0_const", 0);
        if (!new[] { inMin, inMax, outMin, outMax, baseRow, constIndex }.All(float.IsFinite) || inMax <= inMin)
        {
            warnings.Add("gradient map: invalid index modifiers");
            return null;
        }
        int row = Math.Clamp((int)MathF.Round(baseRow), 0, grad.Height - 1);
        if (row != (int)MathF.Round(baseRow)) warnings.Add($"gradient map: base row {baseRow} outside the {grad.Height}-row gradient");
        var index = constant ? null : layers[1];
        var dither = tex.ContainsKey("dit_0_tex") ? layers[^1] : null;
        int w = Math.Max(index?.Width ?? 1, dither?.Width ?? 1), h = Math.Max(index?.Height ?? 1, dither?.Height ?? 1);
        var (idx, cov) = (index is null ? null : new Sampler(index, w, h), dither is null ? null : new Sampler(dither, w, h));
        var outp = new byte[w * h * 4];
        Parallel.For(0, h, y =>
        {
            for (int x = 0; x < w; x++)
            {
                float i = idx is null ? constIndex : idx[x, y, 2];
                i = outMin + (outMax - outMin) * Math.Clamp((i - inMin) / (inMax - inMin), 0, 1);
                float gx = Math.Clamp(i, 0, 1) * (grad.Width - 1);
                int x0 = (int)MathF.Floor(gx), x1 = Math.Min(x0 + 1, grad.Width - 1);
                float t = gx - x0;
                int o = (y * w + x) * 4, a = (row * grad.Width + x0) * 4, b = (row * grad.Width + x1) * 4;
                for (int c = 0; c < 3; c++)
                    outp[o + c] = (byte)MathF.Round(grad.Bgra[a + c] * (1 - t) + grad.Bgra[b + c] * t);
                outp[o + 3] = cov is null ? (byte)255 : (byte)MathF.Round(cov[x, y, 2] * 255);
            }
        });
        return new DecodedImage(w, h, outp);
    }

    /// <summary>Colour × tint with alpha from the red channel of <paramref name="alphaSource"/> through <paramref name="alpha"/>.</summary>
    private static DecodedImage Tinted(DecodedImage color, DecodedImage alphaSource, float[] tint, Func<float, float> alpha)
    {
        int w = Math.Max(color.Width, alphaSource.Width), h = Math.Max(color.Height, alphaSource.Height);
        var (c, a) = (new Sampler(color, w, h), new Sampler(alphaSource, w, h));
        var outp = new byte[w * h * 4];
        Parallel.For(0, h, y =>
        {
            for (int x = 0; x < w; x++)
            {
                int o = (y * w + x) * 4;
                outp[o] = (byte)MathF.Round(Math.Clamp(c[x, y, 0] * tint[2], 0, 1) * 255);
                outp[o + 1] = (byte)MathF.Round(Math.Clamp(c[x, y, 1] * tint[1], 0, 1) * 255);
                outp[o + 2] = (byte)MathF.Round(Math.Clamp(c[x, y, 2] * tint[0], 0, 1) * 255);
                outp[o + 3] = (byte)MathF.Round(alpha(a[x, y, 2]) * 255);
            }
        });
        return new DecodedImage(w, h, outp);
    }
}
