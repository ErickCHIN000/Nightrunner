namespace Nightrunner.Core.Sdb;

/// <summary>A parameter a preset declares: what it is called, what type it is, and its default bytes.</summary>
public sealed record SdbPresetParameter(int Id, string Name, string Expression, string Annotation,
                                        int Type, ushort TypeFlags, byte[] Default, string? DefaultText)
{
    public string TypeName => SdbFormat.TypeName(Type);
}

/// <summary>One of the trailing groups of a preset record. Not interpreted; the key and values are kept.</summary>
public sealed record SdbPresetGroup(uint Key, uint[] Values);

/// <summary>
/// A preset (table 0xAA): the only place the database names and explains a parameter id.
/// </summary>
public sealed record SdbPreset(int Index, string Name, ulong Key, uint Flags, byte[] Masks,
                               IReadOnlyList<SdbPresetParameter> Parameters,
                               IReadOnlyList<SdbPresetGroup> Groups, bool Complete);

/// <summary>A parameter value a material sets, read out of its 0xD2 blob at the slot's offset.</summary>
/// <param name="Declared">False when the preset does not declare this id, so the bytes cannot be typed.</param>
public sealed record SdbParameter(int Id, string Name, int Offset, bool FlagBit31, uint Packed, bool Declared,
                                  int Type, string? ValueText, string? ValueHex,
                                  int? StringIndex, int? RuntimeIndex)
{
    public string TypeName => Declared ? SdbFormat.TypeName(Type) : "";
}

/// <summary>A texture bound to one of a shader's slots, either overridden by the material or the shader default.</summary>
public sealed record SdbBinding(int Slot, int ParamId, string? Parameter, string? Texture, int StringIndex,
                                bool Overridden, int? RuntimeIndex);

/// <summary>One compiled shader variant of a material: a selector, its shader record and its texture bindings.</summary>
public sealed record SdbVariant(ulong Selector, int Shader, int TextureArray, int RenderPassCount,
                                IReadOnlyList<SdbBinding> Bindings);

/// <summary>A material's route (table 0xCA): its preset, the values it overrides, and its shader variants.</summary>
public sealed record SdbRoute(int Index, int Material, int Program, int TokensIndex, int SlotsIndex,
                              int ValuesIndex, string Tokens, string Preset, IReadOnlyList<int> PresetIndices,
                              IReadOnlyList<SdbParameter> Parameters, IReadOnlyList<SdbVariant> Variants);

/// <summary>
/// A resolved material: the name plus everything reachable from it. Every material in both shipped databases has
/// exactly one route, so <see cref="Route"/> is the normal way in and <see cref="Routes"/> the rare case.
/// </summary>
public sealed record SdbMaterial(int Index, string Name, string Database, IReadOnlyList<SdbRoute> Routes,
                                 bool NonRendering)
{
    public SdbRoute? Route => Routes.Count > 0 ? Routes[0] : null;

    /// <summary>Every texture this material draws with, de-duplicated and sorted.</summary>
    public IReadOnlyList<string> Textures { get; } =
        [.. Routes.SelectMany(r => r.Variants)
                  .SelectMany(v => v.Bindings)
                  .Select(b => b.Texture)
                  .Where(t => !string.IsNullOrEmpty(t))
                  .OfType<string>()
                  .Distinct(StringComparer.OrdinalIgnoreCase)
                  .Order(StringComparer.OrdinalIgnoreCase)];
}

/// <summary>What <see cref="SdbFile"/> and the resolver together managed over a whole database.</summary>
public sealed record SdbValidation(string Path, long Seconds, int Materials, int Routes, int Parameters,
                                   int ParametersDeclared, int ParametersTyped, long Bindings,
                                   long BindingsOverride, long BindingsShaderDefault, int RuntimeNameUses,
                                   int MaterialsWithoutRoute, int MaterialsNonRendering, int RoutesPresetMissing,
                                   int RoutesPresetAmbiguous, int TextureNames, int Errors,
                                   IReadOnlyList<string> FirstErrors);
