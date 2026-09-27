using Nightrunner.Core.Games;

namespace Nightrunner.Core.Backends;

/// <summary>
/// Everything that differs per game hangs off a backend. DL1 is a different engine generation; DLTB and DL2 share
/// the Chrome Engine 6 container but differ in details (DL2 meshes decode only, the SDB layouts differ).
/// </summary>
/// <remarks>
/// A capability that a game does not have is <c>null</c> — callers check for null instead of catching. The
/// capability interfaces carry what is known now; their read/write methods land with each port (see docs/architecture.md),
/// and adding them here is what makes a game swappable rather than special-cased at the call site.
/// </remarks>
public interface IGameBackend
{
    GameProfile Profile { get; }

    /// <summary>False while a game is only a slot: paths can be stored, nothing can be opened.</summary>
    bool Implemented { get; }

    /// <summary>How this game's resources are stored and enumerated.</summary>
    IPackSource? Packs { get; }

    ITextureBackend? Textures { get; }
    IMeshBackend? Meshes { get; }
    ISdbBackend? Sdb { get; }
    IMaterialBackend? Materials { get; }
}

/// <summary>Common head of every capability, so a UI can name what it is looking at.</summary>
public interface IGameCapability
{
    /// <summary>What this implementation is, e.g. "RP6L v4" or "IMGC".</summary>
    string Name { get; }
}

/// <summary>Where a game keeps its resources, and which files hold them.</summary>
public interface IPackSource : IGameCapability
{
    /// <summary>Container family: "rp6l" for DLTB/DL2 `.rpack`, "pak" for the DL1 archives.</summary>
    string Kind { get; }

    /// <summary>The container files of an install, in load order.</summary>
    string[] Files(GameInstall install);

    /// <summary>True when <see cref="Rpack.RpackFile"/> can open these files directly.</summary>
    bool IsRpack { get; }
}

/// <summary>Texture side. Decode/encode land with the texture port; DL1 uses a different container entirely.</summary>
public interface ITextureBackend : IGameCapability
{
    /// <summary>Logical resource type that carries a texture (0x20 on Chrome Engine 6).</summary>
    byte ResourceType { get; }

    bool CanEncode { get; }
}

/// <summary>Mesh side. DLTB decodes and encodes; DL2 is decode-only; DL1 is a different format.</summary>
public interface IMeshBackend : IGameCapability
{
    byte ResourceType { get; }

    bool CanDecode { get; }

    bool CanEncode { get; }

    /// <summary>Why encoding is refused, for the message the UI shows. Null when it is available.</summary>
    string? EncodeRefusal { get; }

    /// <summary>Decode one mesh resource. The object layout (DLTB / DL2) is read from the data, never from the game.</summary>
    Mesh.MeshModel Decode(Rpack.RpackFile pack, int logicalIndex);
}

/// <summary>Shader database. Read-only everywhere by design; DLTB and DL2 have different table layouts.</summary>
public interface ISdbBackend : IGameCapability
{
    /// <summary>The runtime SDB files to read, in preference order (dx12 then dx11, typically).</summary>
    string[] Files(GameInstall install);
}

/// <summary>Materials: where a material's definition and its texture bindings come from.</summary>
public interface IMaterialBackend : IGameCapability
{
    /// <summary>True when materials are resolved through the SDB rather than stored per resource.</summary>
    bool FromSdb { get; }

    bool CanCreate { get; }
}
