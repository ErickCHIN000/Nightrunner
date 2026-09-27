using System.Buffers.Binary;
using System.Globalization;
using System.Runtime.InteropServices;

namespace Nightrunner.Core.Sdb;

/// <summary>
/// The resolver: material name → route → preset declarations → typed parameter values and texture bindings.
/// </summary>
/// <remarks>
/// The joins are the renderer's: ResolveTextureBindings +0x131D40 for the bindings, ApplyParameterOverrides
/// +0xD89F0 for the value widths. A slot descriptor gives a parameter id and a byte offset into the material's
/// 0xD2 blob; the preset says what type lives there. A texture with no slot keeps the shader's default.
/// </remarks>
public sealed partial class SdbFile
{
    private readonly Lock _indexLock = new();
    private Dictionary<string, List<int>>? _byName;
    private Dictionary<int, List<int>>? _routesByMaterial;
    private Dictionary<string, List<int>>? _presetsByName;
    private readonly Dictionary<int, SdbPreset> _presetCache = [];

    // ---- indices ---------------------------------------------------------------------------------------------

    private void BuildIndices()
    {
        if (_byName is not null) return;
        lock (_indexLock)
        {
            if (_byName is not null) return;

            int materials = MaterialCount;
            var byName = new Dictionary<string, List<int>>(materials, StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < materials; i++)
            {
                string name = MaterialName(i);
                if (!byName.TryGetValue(name, out var hits)) byName[name] = hits = [];
                hits.Add(i);
            }

            var routes = new Dictionary<int, List<int>>(materials);
            for (int r = 0; r < RouteCount; r++)
            {
                int m = RouteRow(r).Material;
                if (!routes.TryGetValue(m, out var hits)) routes[m] = hits = [];
                hits.Add(r);
            }

            var presets = new Dictionary<string, List<int>>(PresetCount, StringComparer.Ordinal);
            for (int i = 0; i < PresetCount; i++)
            {
                string name = Text(SdbFormat.TableStrings, PresetNameId(i));
                if (!presets.TryGetValue(name, out var hits)) presets[name] = hits = [];
                hits.Add(i);
            }

            _routesByMaterial = routes;
            _presetsByName = presets;
            _byName = byName;                    // published last: it is what the null check above reads
        }
    }

    /// <summary>All 0xB2 indices whose name matches, case-insensitively; a missing ".mat" suffix is tolerated.</summary>
    public IReadOnlyList<int> FindMaterial(string name)
    {
        BuildIndices();
        if (_byName!.TryGetValue(name, out var hits)) return hits;
        return _byName.TryGetValue(name + ".mat", out var suffixed) ? suffixed : [];
    }

    public int MaterialIndex(string name)
    {
        var hits = FindMaterial(name);
        if (hits.Count != 1)
            throw new SdbFormatException($"'{name}': {hits.Count} material matches in {Name}");
        return hits[0];
    }

    public IReadOnlyList<int> RoutesOf(int materialIndex)
    {
        BuildIndices();
        return _routesByMaterial!.TryGetValue(materialIndex, out var hits) ? hits : [];
    }

    public IReadOnlyList<int> PresetsNamed(string name)
    {
        BuildIndices();
        return _presetsByName!.TryGetValue(name, out var hits) ? hits : [];
    }

    /// <summary>The preset name a route uses, without resolving anything else (this is the hot list column).</summary>
    public string PresetNameOfRoute(int routeIndex)
    {
        string tokens = Text(SdbFormat.TableTokens, RouteRow(routeIndex).Tokens);
        int semi = tokens.IndexOf(';');
        return semi < 0 ? tokens : tokens[..semi];
    }

    // ---- presets ---------------------------------------------------------------------------------------------

    /// <summary>A decoded preset record. Cached: a handful of presets cover most of the database.</summary>
    public SdbPreset Preset(int index)
    {
        lock (_indexLock)
        {
            if (_presetCache.TryGetValue(index, out var cached)) return cached;
        }
        var preset = DecodePreset(index);
        lock (_indexLock)
        {
            if (_presetCache.Count > 256) _presetCache.Clear();
            _presetCache[index] = preset;
        }
        return preset;
    }

    private SdbPreset DecodePreset(int index)
    {
        var table = Table(SdbFormat.TablePresets);
        if ((uint)index >= (uint)table.Count)
            throw new SdbFormatException($"0xAA[{index}] out of range (count {table.Count})");
        int start = table.Offset(index);
        int end = start + table.Length(index);
        var c = new SdbCursor(_a, start, end, new int[4]);

        int at = c.Skip(16);
        ulong key = BinaryPrimitives.ReadUInt64LittleEndian(_a.AsSpan(at));
        int nameId = BinaryPrimitives.ReadInt32LittleEndian(_a.AsSpan(at + 8));
        uint flags = BinaryPrimitives.ReadUInt32LittleEndian(_a.AsSpan(at + 12));
        var masks = _a.AsSpan(c.Skip(7), 7).ToArray();

        int paramCount = c.Count(9);
        var parameters = new SdbPresetParameter[paramCount];
        for (int i = 0; i < paramCount; i++)
        {
            int p = c.Skip(8);
            int pid = BinaryPrimitives.ReadUInt16LittleEndian(_a.AsSpan(p));
            int expr = BinaryPrimitives.ReadUInt16LittleEndian(_a.AsSpan(p + 2));
            int ann = BinaryPrimitives.ReadUInt16LittleEndian(_a.AsSpan(p + 4));
            ushort typeFlags = BinaryPrimitives.ReadUInt16LittleEndian(_a.AsSpan(p + 6));
            int length = c.Count(1);
            var dflt = _a.AsSpan(c.Skip(length), length).ToArray();
            int type = typeFlags & 0x7FFF;
            parameters[i] = new SdbPresetParameter(pid, Text(SdbFormat.TableSharedNames, pid),
                                                   Text(SdbFormat.TableStrings, expr),
                                                   Text(SdbFormat.TableStrings, ann),
                                                   type, typeFlags, dflt, DefaultText(type, dflt));
        }

        int groupCount = c.Count(5);
        var groups = new SdbPresetGroup[groupCount];
        for (int i = 0; i < groupCount; i++)
        {
            uint gkey = BinaryPrimitives.ReadUInt32LittleEndian(_a.AsSpan(c.Skip(4)));
            int n = c.Count(4);
            var values = MemoryMarshal.Cast<byte, uint>(_a.AsSpan(c.Skip(4 * n), 4 * n)).ToArray();
            groups[i] = new SdbPresetGroup(gkey, values);
        }

        return new SdbPreset(index, Text(SdbFormat.TableStrings, nameId), key, flags, masks,
                             parameters, groups, c.Position == end);
    }

    /// <summary>A preset default decoded with its parameter's type; null when the bytes do not fit the type.</summary>
    private string? DefaultText(int type, ReadOnlySpan<byte> raw)
    {
        int size = SdbFormat.ValueSize(type);
        return size < 0 || raw.Length != size ? null : ValueText(type, raw);
    }

    // ---- routes ----------------------------------------------------------------------------------------------

    public SdbRoute Route(int routeIndex)
    {
        var (material, program, tokensIndex, slotsIndex, valuesIndex) = RouteRow(routeIndex);
        string tokens = Text(SdbFormat.TableTokens, tokensIndex);
        int semi = tokens.IndexOf(';');
        string presetName = semi < 0 ? tokens : tokens[..semi];
        var presetIndices = PresetsNamed(presetName);

        // A route names its preset by string. One match is the normal case; zero or several means the values
        // cannot be typed, and the parameters come back undeclared rather than guessed.
        Dictionary<int, SdbPresetParameter>? declared = null;
        if (presetIndices.Count == 1)
        {
            declared = [];
            foreach (var p in Preset(presetIndices[0]).Parameters) declared.TryAdd(p.Id, p);
        }

        var valuesTable = Table(SdbFormat.TableValues);
        int blobAt = valuesTable.Offset(valuesIndex);
        int blobLength = valuesTable.Length(valuesIndex);
        var blob = _a.AsSpan(blobAt, blobLength);

        var slots = Record(SdbFormat.TableSlots, slotsIndex);
        var packedSlots = MemoryMarshal.Cast<byte, uint>(slots);
        var parameters = new SdbParameter[packedSlots.Length];
        var byId = new Dictionary<int, SdbParameter>(packedSlots.Length);
        for (int i = 0; i < packedSlots.Length; i++)
        {
            uint packed = packedSlots[i];
            int pid = (int)(packed & 0xFFFF);
            int offset = (int)((packed >> 16) & 0x7FFF);
            string name = Text(SdbFormat.TableSharedNames, pid);
            var declaration = declared?.GetValueOrDefault(pid);

            string? text = null, hex = null;
            int? stringIndex = null, runtimeIndex = null;
            int type = declaration?.Type ?? -1;
            if (declaration is not null)
            {
                int size = SdbFormat.ValueSize(type);
                if (size >= 0)
                {
                    if (offset + size > blobLength)
                        throw new SdbFormatException(
                            $"route {routeIndex}: parameter {pid} value at {offset}+{size} exceeds the " +
                            $"0xD2 blob ({blobLength} bytes)");
                    var raw = blob.Slice(offset, size);
                    hex = Convert.ToHexString(raw).ToLowerInvariant();
                    if (type == 7)
                    {
                        int v = BinaryPrimitives.ReadUInt16LittleEndian(raw);
                        stringIndex = v;
                        (text, runtimeIndex) = StringValue(v);
                    }
                    else
                    {
                        text = ValueText(type, raw);
                    }
                }
            }

            var parameter = new SdbParameter(pid, name, offset, (packed & 0x80000000) != 0, packed,
                                             declaration is not null, type, text, hex, stringIndex, runtimeIndex);
            parameters[i] = parameter;
            byId.TryAdd(pid, parameter);
        }

        var selectors = MemoryMarshal.Cast<byte, ulong>(Record(SdbFormat.TableSelectors, program));
        var variants = new SdbVariant[selectors.Length];
        for (int i = 0; i < selectors.Length; i++)
        {
            ulong selector = selectors[i];
            int shader = (int)(selector >> 48);
            var record = Record(SdbFormat.TableShaders, shader);
            int textureArray = BinaryPrimitives.ReadUInt16LittleEndian(record[0x12..]);

            IReadOnlyList<SdbBinding> bindings = [];
            if (textureArray != 0)
            {
                var pairs = MemoryMarshal.Cast<byte, ushort>(Record(SdbFormat.TableTextureArrays, textureArray));
                var list = new SdbBinding[pairs.Length / 2];
                for (int b = 0; b < list.Length; b++)
                {
                    int pid = pairs[b * 2];
                    int fallback = pairs[b * 2 + 1];
                    var slot = pid != 0 ? byId.GetValueOrDefault(pid) : null;
                    int value;
                    bool overridden;
                    if (slot is not null)
                    {
                        if (slot.Offset + 2 > blobLength)
                            throw new SdbFormatException(
                                $"route {routeIndex}: texture index for parameter {pid} exceeds the 0xD2 blob");
                        value = BinaryPrimitives.ReadUInt16LittleEndian(blob[slot.Offset..]);
                        overridden = true;
                    }
                    else
                    {
                        value = fallback;
                        overridden = false;
                    }
                    var (texture, runtime) = StringValue(value);
                    list[b] = new SdbBinding(b, pid, pid != 0 ? Text(SdbFormat.TableSharedNames, pid) : null,
                                             texture, value, overridden, runtime);
                }
                bindings = list;
            }

            variants[i] = new SdbVariant(selector, shader, textureArray, record[0x17] & 15, bindings);
        }

        return new SdbRoute(routeIndex, material, program, tokensIndex, slotsIndex, valuesIndex, tokens,
                            presetName, presetIndices, parameters, variants);
    }

    // ---- materials -------------------------------------------------------------------------------------------

    public SdbMaterial Material(int index)
    {
        if ((uint)index >= (uint)MaterialCount)
            throw new SdbFormatException($"material index {index} out of range (count {MaterialCount})");
        var routes = RoutesOf(index).Select(Route).ToArray();
        var variants = routes.SelectMany(r => r.Variants).ToArray();
        return new SdbMaterial(index, MaterialName(index), Name, routes,
                               variants.Length > 0 && variants.All(v => v.RenderPassCount == 0));
    }

    public SdbMaterial Material(string name) => Material(MaterialIndex(name));

    // ---- values ----------------------------------------------------------------------------------------------

    /// <summary>
    /// A 0xAE name, or — at or above 0xFBFF — a name the engine resolves at runtime, which this reader reports
    /// as unknown rather than guessing at.
    /// </summary>
    public (string? Name, int? RuntimeIndex) StringValue(int value) =>
        value >= SdbFormat.RuntimeStringBase
            ? (null, value - SdbFormat.RuntimeStringBase)
            : (Text(SdbFormat.TableSharedNames, value), null);

    private string ValueText(int type, ReadOnlySpan<byte> raw) => type switch
    {
        0 => raw[0] != 0 ? "true" : "false",
        1 => BinaryPrimitives.ReadInt32LittleEndian(raw).ToString(CultureInfo.InvariantCulture),
        2 => Number(raw),
        3 or 4 or 5 or 6 => Vector(raw),
        7 => StringValue(BinaryPrimitives.ReadUInt16LittleEndian(raw)) is var (name, runtime)
            ? name ?? $"<runtime {runtime}>"
            : "",
        _ => Convert.ToHexString(raw).ToLowerInvariant(),
    };

    private static string Vector(ReadOnlySpan<byte> raw)
    {
        var text = new System.Text.StringBuilder("(");
        for (int i = 0; i + 4 <= raw.Length; i += 4)
        {
            if (i > 0) text.Append(", ");
            text.Append(Number(raw[i..]));
        }
        return text.Append(')').ToString();
    }

    private static string Number(ReadOnlySpan<byte> raw) =>
        BinaryPrimitives.ReadSingleLittleEndian(raw).ToString("G6", CultureInfo.InvariantCulture);
}
