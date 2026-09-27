using Nightrunner.Core.Games;
using Nightrunner.Core.Mesh;
using Nightrunner.Core.Rpack;

namespace Nightrunner.Core.Backends;

/// <summary>The backend for a game. One instance per profile; they hold no state.</summary>
public static class GameBackends
{
    private static readonly Dictionary<string, IGameBackend> ById =
        new(StringComparer.OrdinalIgnoreCase)
        {
            [GameProfile.Dltb.Id] = new ChromeBackend(GameProfile.Dltb, meshEncode: true),
            [GameProfile.Dl2.Id] = new ChromeBackend(GameProfile.Dl2, meshEncode: false),
            [GameProfile.Dl1.Id] = new DyingLight1Backend(),
        };

    public static IGameBackend For(GameProfile profile) => For(profile.Id);

    public static IGameBackend For(string gameId) =>
        ById.TryGetValue(gameId, out var b) ? b : throw new ArgumentException($"no backend for '{gameId}'");

    public static IGameBackend For(GameInstall install) => For(install.Profile);
}

/// <summary>Chrome Engine 6: RP6L `.rpack` containers, IMGC textures, an SDB. DLTB and DL2 differ in details.</summary>
public sealed class ChromeBackend(GameProfile profile, bool meshEncode) : IGameBackend
{
    public GameProfile Profile { get; } = profile;
    public bool Implemented => true;

    public IPackSource? Packs { get; } = new RpackSource();
    public ITextureBackend? Textures { get; } = new ImgcTextures();
    public IMeshBackend? Meshes { get; } = new ChromeMeshes(meshEncode);
    public ISdbBackend? Sdb { get; } = new RuntimeSdb();
    public IMaterialBackend? Materials { get; } = new SdbMaterials();

    private sealed class RpackSource : IPackSource
    {
        public string Name => "RP6L v4";
        public string Kind => "rp6l";
        public bool IsRpack => true;
        public string[] Files(GameInstall install) => install.Rpacks();
    }

    private sealed class ImgcTextures : ITextureBackend
    {
        public string Name => "IMGC";
        public byte ResourceType => 0x20;
        public bool CanEncode => true;   // PNG/DDS/HDR -> IMGC (BC1-7, BC6H, uncompressed and float formats)
    }

    private sealed class ChromeMeshes(bool encode) : IMeshBackend
    {
        public string Name => "ClassReader mesh";
        public byte ResourceType => 0x10;
        public bool CanDecode => true;
        public bool CanEncode => encode;
        public string? EncodeRefusal => encode ? null : "mesh writing is not implemented for this game";
        public MeshModel Decode(RpackFile pack, int logicalIndex) => MeshDecoder.Decode(pack, logicalIndex);
    }

    private sealed class RuntimeSdb : ISdbBackend
    {
        public string Name => "runtime SDB";

        public string[] Files(GameInstall install) =>
            new[] { install.Sdb("dx12"), install.Sdb("dx11") }.Where(File.Exists).ToArray();
    }

    private sealed class SdbMaterials : IMaterialBackend
    {
        public string Name => "SDB material";
        public bool FromSdb => true;
        public bool CanCreate => false;   // no SDB writer, deliberately
    }
}

/// <summary>
/// Dying Light 1 — the slot. Its root can be stored and validated down to the executable; nothing reads its
/// `DW/Data*.pak` archives yet, so every capability is null and <see cref="Implemented"/> is false.
/// </summary>
public sealed class DyingLight1Backend : IGameBackend
{
    public GameProfile Profile => GameProfile.Dl1;
    public bool Implemented => false;
    public IPackSource? Packs => null;
    public ITextureBackend? Textures => null;
    public IMeshBackend? Meshes => null;
    public ISdbBackend? Sdb => null;
    public IMaterialBackend? Materials => null;
}
