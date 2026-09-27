using System.Numerics;
using System.Text;
using Nightrunner.Core.Mesh;
using Nightrunner.Core.Mesh.ClassReader;

namespace Nightrunner.Core.Anim;

/// <summary>One pose element of a face bone: the pose (index into <see cref="FaceRig.Poses"/>) and its delta.</summary>
/// <remarks>Element 0 of every bone is its base (neutral) local transform, pose 0; the others are deltas.</remarks>
public readonly record struct FacePoseElement(int Pose, Quaternion Rotation, Vector3 Translation);

/// <summary>
/// A face bone (<c>SFaceBone</c>, 0x20 bytes): skeleton bone by h41 name hash, its pose elements and how many of them
/// each of the five detail levels uses (<c>GetNumPoseElements</c>; index 0 = the highest detail, all elements).
/// </summary>
public sealed record FaceBone(uint Hash, uint[] DetailCounts, FacePoseElement[] Elements)
{
    public FacePoseElement Base => Elements[0];
}

/// <summary>A face pose (<c>SFacePose</c>): name, case-kept h41 of the name, and its kind word.</summary>
/// <remarks>
/// Kinds seen: 1 neutral, 2..5 eyeball roll/pitch, 6 blink, 7 plain, 8 corrective (<c>cs*</c>), 9 <c>sw_*</c>,
/// 10 <c>*_eye_blink_mY</c>. The per-detail bone bitsets that follow are not kept.
/// </remarks>
public sealed record FacePose(string Name, uint Hash, uint Kind);

/// <summary>
/// The face rig a solid-head mesh carries (<c>SFaceData</c>, mesh image class 16 → poses class 17, bones class 18, pose
/// elements class 20), read from the mesh image as the engine's <c>IMeshFile::GetFaceData</c> returns it.
/// </summary>
/// <remarks>
/// <c>SFaceData</c> (0x28): +0x00 → poses, +0x08 → face bones, +0x10 → a second bone list (bones with only a base
/// element; not used by the mix, see <see cref="ExtraBones"/>), +0x18 u32 pose count, +0x1C u32 bone count, +0x20 u32
/// second-list count. A pose record is 0x10 bytes (name pointer, u32 hash, u32 kind) plus five per-detail bone bitsets
/// (DLTB 0xD8 per pose, DL2 0x60). A face bone is an element pointer, five u32 detail counts and the u32 bone hash
/// (lowercased h41, the list is sorted by it). An element is quaternion xyzw, translation xyz and a u32 pose index.
/// Pose 0 is <c>neutral_pose</c>; poses 1.. are sorted by hash and are the weight streams 0.. of a pose-weight clip.
/// </remarks>
public sealed class FaceRig
{
    public const uint ClassFaceData = 16, ClassPoses = 17, ClassBones = 18, ClassElements = 20;
    public const int FaceDataSize = 0x28, BoneSize = 0x20, ElementSize = 0x20, PoseHeader = 0x10, Details = 5;

    public required FacePose[] Poses { get; init; }
    public required FaceBone[] Bones { get; init; }
    public FaceBone[] ExtraBones { get; init; } = [];

    /// <summary>Pose count without the neutral pose: the number of bone-driving weights (105 → 104, 192 → 191).</summary>
    public int WeightCount => Poses.Length - 1;

    /// <summary>The face rig of <paramref name="mesh"/>, or null when it has none (no class-16 record).</summary>
    public static FaceRig? FromMesh(MeshModel mesh) => FromImage(mesh.Image);

    public static FaceRig? FromImage(Image img)
    {
        var fds = img.Fixups.RecordsOfClass(ClassFaceData).ToList();
        if (fds.Count == 0) return null;
        if (fds.Count != 1 || fds[0].Record.Count != 1)
            throw new MeshFormatException($"expected one face-data object, found {fds.Count} record(s)");
        int fd = (int)fds[0].Record.Offset;
        img.Check(fd, FaceDataSize);
        uint np = img.U32(fd + 0x18), nb = img.U32(fd + 0x1C), nx = img.U32(fd + 0x20);
        if (np < 1 || np > 4096 || nb > 4096 || nx > 4096)
            throw new MeshFormatException($"face data counts out of range: {np} poses, {nb} bones, {nx} extra");
        int poses = Array(img, fd, ClassPoses, (int)np, out int stride);
        if (stride < PoseHeader || (stride - PoseHeader) % (Details * 8) != 0)
            throw new MeshFormatException($"face pose record of 0x{stride:X} bytes is not 0x10 + 5 bitsets");
        var list = new FacePose[np];
        for (int p = 0; p < np; p++)
        {
            int o = poses + p * stride;
            var name = img.StringAt(o) ?? throw new MeshFormatException($"face pose {p} has no name");
            list[p] = new FacePose(Encoding.UTF8.GetString(name), img.U32(o + 8), img.U32(o + 12));
        }
        return new FaceRig
        {
            Poses = list,
            Bones = ReadBones(img, fd + 0x08, (int)nb, (int)np),
            ExtraBones = ReadBones(img, fd + 0x10, (int)nx, (int)np),
        };
    }

    private static FaceBone[] ReadBones(Image img, int ptr, int count, int poses)
    {
        if (count == 0) return [];
        int at = Array(img, ptr, ClassBones, count, out int stride);
        if (stride != BoneSize) throw new MeshFormatException($"face bone record of 0x{stride:X} bytes (expected 0x20)");
        var bones = new FaceBone[count];
        for (int b = 0; b < count; b++)
        {
            int o = at + b * BoneSize;
            var counts = img.U32s(o + 8, Details);
            uint hash = img.U32(o + 0x1C);
            int elems = img.Target(o) ?? throw new MeshFormatException($"face bone {b} has no elements");
            int n = RecordCount(img, elems, ClassElements, ElementSize);
            if (counts[0] != n || n < 1)
                throw new MeshFormatException($"face bone {b}: {n} elements, detail counts {string.Join("/", counts)}");
            var e = new FacePoseElement[n];
            for (int j = 0; j < n; j++)
            {
                int eo = elems + j * ElementSize;
                var f = img.F32s(eo, 7);
                uint pose = img.U32(eo + 0x1C);
                if (pose >= poses || (j > 0 && pose == 0))
                    throw new MeshFormatException($"face bone {b} element {j}: pose {pose} of {poses}");
                e[j] = new FacePoseElement((int)pose, new Quaternion(f[0], f[1], f[2], f[3]), new Vector3(f[4], f[5], f[6]));
            }
            bones[b] = new FaceBone(hash, counts, e);
        }
        return bones;
    }

    /// <summary>Offset of the array a face-data pointer names; the stride is its record span over the count.</summary>
    private static int Array(Image img, int ptr, uint cls, int count, out int stride)
    {
        int at = img.Target(ptr) ?? throw new MeshFormatException($"face data: null class-{cls} pointer");
        int rec = img.RecordAt(at) ?? throw new MeshFormatException($"face data: 0x{at:X} starts no record");
        var r = img.Records[rec];
        if (r.ClassId != cls || r.Count != count)
            throw new MeshFormatException($"face data: record at 0x{at:X} is class {r.ClassId} ×{r.Count}, expected class {cls} ×{count}");
        var (a, b) = img.RecordSpan(rec);
        if ((b - a) % count != 0) throw new MeshFormatException($"face data: class-{cls} span {b - a} is not {count} records");
        stride = (b - a) / count;
        return at;
    }

    private static int RecordCount(Image img, int at, uint cls, int size)
    {
        int rec = img.RecordAt(at) ?? throw new MeshFormatException($"face data: 0x{at:X} starts no record");
        var r = img.Records[rec];
        var (a, b) = img.RecordSpan(rec);
        if (r.ClassId != cls || b - a != r.Count * size)
            throw new MeshFormatException($"face data: record at 0x{at:X} is class {r.ClassId} ×{r.Count} ({b - a} bytes), expected class {cls} of 0x{size:X}");
        return r.Count;
    }
}
