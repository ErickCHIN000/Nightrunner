// Port of the Cast library by DTZxPorter (https://github.com/dtzxporter/cast), MIT licence, Copyright (c) 2020 Nick.
// The full licence text is at the top of CastFile.cs (and licenses/Cast.MIT.txt).
//
// The typed nodes of castlib.py, one class each, same names and the same property keys. Getters return null when the
// property is absent (Python None) and the prototype's defaults where it has one. Vector getters return the flat
// float array; integer buffers return uint[] whatever width was stored. Setters take floats (callers round from double
// exactly as struct.pack('f') does) and flat arrays, with Vector/Quaternion conveniences.

using System.Numerics;
using System.Runtime.InteropServices;

namespace Nightrunner.Core.Cast;

/// <summary>A 3d model with meshes, materials, and a skeleton.</summary>
public sealed class Model(CastHashSequence hashes) : CastNode(Id, hashes)
{
    public const uint Id = 0x6C646F6D;   // 'modl'

    public string? Name() => GetString("n");
    public void SetName(string name) => SetStringProperty("n", name);
    public float[]? Position() => GetFloats("p");
    public void SetPosition(ReadOnlySpan<float> position) => SetVector("p", CastPropertyType.Vector3, position);
    public void SetPosition(Vector3 position) => SetPosition([position.X, position.Y, position.Z]);
    public float[]? Rotation() => GetFloats("r");
    public void SetRotation(ReadOnlySpan<float> rotation) => SetVector("r", CastPropertyType.Vector4, rotation);
    public void SetRotation(Quaternion rotation) => SetRotation([rotation.X, rotation.Y, rotation.Z, rotation.W]);
    public float[]? Scale() => GetFloats("s");
    public void SetScale(ReadOnlySpan<float> scale) => SetVector("s", CastPropertyType.Vector3, scale);
    public void SetScale(Vector3 scale) => SetScale([scale.X, scale.Y, scale.Z]);

    public Skeleton? Skeleton() => ChildOfType<Skeleton>();
    public Skeleton CreateSkeleton() => CreateChild(new Skeleton(Hashes));
    public List<Mesh> Meshes() => ChildrenOfType<Mesh>();
    public Mesh CreateMesh() => CreateChild(new Mesh(Hashes));
    public List<Hair> Hairs() => ChildrenOfType<Hair>();
    public Hair CreateHair() => CreateChild(new Hair(Hashes));
    public List<Material> Materials() => ChildrenOfType<Material>();
    public Material CreateMaterial() => CreateChild(new Material(Hashes));
    public List<BlendShape> BlendShapes() => ChildrenOfType<BlendShape>();
    public BlendShape CreateBlendShape() => CreateChild(new BlendShape(Hashes));
}

/// <summary>A 3d animation and its collection of curves.</summary>
public sealed class Animation(CastHashSequence hashes) : CastNode(Id, hashes)
{
    public const uint Id = 0x6D696E61;   // 'anim'

    public string? Name() => GetString("n");
    public void SetName(string name) => SetStringProperty("n", name);
    public Skeleton? Skeleton() => ChildOfType<Skeleton>();
    public Skeleton CreateSkeleton() => CreateChild(new Skeleton(Hashes));
    public List<Curve> Curves() => ChildrenOfType<Curve>();
    public List<CurveModeOverride> CurveModeOverrides() => ChildrenOfType<CurveModeOverride>();
    public Curve CreateCurve() => CreateChild(new Curve(Hashes));
    public CurveModeOverride CreateCurveModeOverride() => CreateChild(new CurveModeOverride(Hashes));
    public List<NotificationTrack> Notifications() => ChildrenOfType<NotificationTrack>();
    public NotificationTrack CreateNotification() => CreateChild(new NotificationTrack(Hashes));
    public float? Framerate() => Property("fr") is { } p ? (float)p.NumberAt(0) : null;
    public void SetFramerate(float framerate) => SetFloat("fr", framerate);
    public bool Looping() => GetFlag("lo", false);
    public void SetLooping(bool enabled) => SetFlag("lo", enabled);
}

/// <summary>A curve from an animation that animates a node's property.</summary>
public sealed class Curve(CastHashSequence hashes) : CastNode(Id, hashes)
{
    public const uint Id = 0x76727563;   // 'curv'

    public string? NodeName() => GetString("nn");
    public void SetNodeName(string name) => SetStringProperty("nn", name);
    public string? KeyPropertyName() => GetString("kp");
    public void SetKeyPropertyName(string name) => SetStringProperty("kp", name);
    public uint[]? KeyFrameBuffer() => GetUInts("kb");
    public void SetKeyFrameBuffer(ReadOnlySpan<uint> values) => SetIntegerBuffer("kb", values);
    /// <summary>The keyframe values as stored: <c>float[]</c> (<c>f</c>, <c>4v</c>) or <c>byte[]</c> (<c>b</c>).</summary>
    public Array? KeyValueBuffer() => Property("kv")?.Values;
    public void SetFloatKeyValueBuffer(float[] values) => CreateProperty("kv", CastPropertyType.Float).SetValues(values);
    public void SetVec4KeyValueBuffer(float[] values) => CreateProperty("kv", CastPropertyType.Vector4).SetValues(values);
    public void SetVec4KeyValueBuffer(ReadOnlySpan<Quaternion> values) => SetVec4KeyValueBuffer(Flat(values));
    public void SetByteKeyValueBuffer(byte[] values) => CreateProperty("kv", CastPropertyType.Byte).SetValues(values);
    public string? Mode() => GetString("m");
    public void SetMode(string mode) => SetStringProperty("m", mode);
    public float AdditiveBlendWeight() => Property("ab") is { } p ? (float)p.NumberAt(0) : 1.0f;
    public void SetAdditiveBlendWeight(float value) => SetFloat("ab", value);

    internal static float[] Flat<T>(ReadOnlySpan<T> values) where T : struct => MemoryMarshal.Cast<T, float>(values).ToArray();
}

/// <summary>An override for an animation curve's mode.</summary>
public sealed class CurveModeOverride(CastHashSequence hashes) : CastNode(Id, hashes)
{
    public const uint Id = 0x564F4D43;   // 'CMOV'

    public string? NodeName() => GetString("nn");
    public void SetNodeName(string name) => SetStringProperty("nn", name);
    public string? Mode() => GetString("m");
    public void SetMode(string mode) => SetStringProperty("m", mode);
    public bool OverrideTranslationCurves() => GetFlag("ot", false);
    public void SetOverrideTranslationCurves(bool enabled) => SetFlag("ot", enabled);
    public bool OverrideRotationCurves() => GetFlag("or", false);
    public void SetOverrideRotationCurves(bool enabled) => SetFlag("or", enabled);
    public bool OverrideScaleCurves() => GetFlag("os", false);
    public void SetOverrideScaleCurves(bool enabled) => SetFlag("os", enabled);
}

/// <summary>The notification track for an animation.</summary>
public sealed class NotificationTrack(CastHashSequence hashes) : CastNode(Id, hashes)
{
    public const uint Id = 0x6669746E;   // 'ntif'

    public string? Name() => GetString("n");
    public void SetName(string name) => SetStringProperty("n", name);
    public uint[]? KeyFrameBuffer() => GetUInts("kb");
    public void SetKeyFrameBuffer(ReadOnlySpan<uint> values) => SetIntegerBuffer("kb", values);
}

/// <summary>A 3d mesh for a model.</summary>
public sealed class Mesh(CastHashSequence hashes) : CastNode(Id, hashes)
{
    public const uint Id = 0x6873656D;   // 'mesh'

    public string? Name() => GetString("n");
    public void SetName(string name) => SetStringProperty("n", name);
    /// <summary><c>len(vp) / 3</c>; null without positions.</summary>
    public int? VertexCount() => Property("vp") is { } p ? p.ValueCount / 3 : null;
    public int? FaceCount() => Property("f") is { } p ? p.ValueCount / 3 : null;
    public int UVLayerCount() => Property("ul") is { } p ? (int)p.IntegerAt(0) : 0;
    public void SetUVLayerCount(int count) => CreateProperty("ul", CastPropertyType.Byte).SetValues([checked((byte)count)]);

    /// <summary>The <c>cl</c> count; without it, 1 when the legacy <c>vc</c> buffer exists, else 0.</summary>
    public int ColorLayerCount() => Property("cl") is { } p ? (int)p.IntegerAt(0) : HasProperty("vc") ? 1 : 0;
    public void SetColorLayerCount(int count) => CreateProperty("cl", CastPropertyType.Byte).SetValues([checked((byte)count)]);
    public int MaximumWeightInfluence() => Property("mi") is { } p ? (int)p.IntegerAt(0) : 0;
    public void SetMaximumWeightInfluence(int maximum) => CreateProperty("mi", CastPropertyType.Byte).SetValues([checked((byte)maximum)]);
    public string SkinningMethod() => GetString("sm") ?? "linear";
    public void SetSkinningMethod(string method) => SetStringProperty("sm", method);

    public uint[]? FaceBuffer() => GetUInts("f");
    public void SetFaceBuffer(ReadOnlySpan<uint> values) => SetIntegerBuffer("f", values);
    public float[]? VertexPositionBuffer() => GetFloats("vp");
    public void SetVertexPositionBuffer(float[] values) => CreateProperty("vp", CastPropertyType.Vector3).SetValues(values);
    public void SetVertexPositionBuffer(ReadOnlySpan<Vector3> values) => SetVertexPositionBuffer(Curve.Flat(values));
    public float[]? VertexNormalBuffer() => GetFloats("vn");
    public void SetVertexNormalBuffer(float[] values) => CreateProperty("vn", CastPropertyType.Vector3).SetValues(values);
    public void SetVertexNormalBuffer(ReadOnlySpan<Vector3> values) => SetVertexNormalBuffer(Curve.Flat(values));
    public float[]? VertexTangentBuffer() => GetFloats("vt");
    public void SetVertexTangentBuffer(float[] values) => CreateProperty("vt", CastPropertyType.Vector3).SetValues(values);
    public void SetVertexTangentBuffer(ReadOnlySpan<Vector3> values) => SetVertexTangentBuffer(Curve.Flat(values));

    /// <summary>Colour layer <c>c{index}</c> as stored (<c>uint[]</c> packed or <c>float[]</c> rgba); layer 0 falls back to the
    /// legacy <c>vc</c> buffer.</summary>
    public Array? VertexColorLayerBuffer(int index)
    {
        if (Property($"c{index}") is { } cl) return cl.Values;
        if (index == 0 && Property("vc") is { } vc) return vc.Values;
        return null;
    }

    /// <summary>Packed colours (<c>i</c>). As the prototype: an empty list has no first element to test and is written as
    /// an empty <c>4v</c>.</summary>
    public void SetVertexColorBuffer(int index, uint[] values)
    {
        if (values.Length == 0) CreateProperty($"c{index}", CastPropertyType.Vector4).SetValues(Array.Empty<float>());
        else CreateProperty($"c{index}", CastPropertyType.Integer).SetValues(values);
    }

    /// <summary>Float rgba colours (<c>4v</c>), flattened.</summary>
    public void SetVertexColorBuffer(int index, float[] values) => CreateProperty($"c{index}", CastPropertyType.Vector4).SetValues(values);
    public void SetVertexColorBuffer(int index, ReadOnlySpan<Vector4> values) => SetVertexColorBuffer(index, Curve.Flat(values));

    /// <summary>True when the layer is packed integers; also true when it is absent (the legacy format was packed).</summary>
    public bool VertexColorLayerBufferPacked(int index) => Property($"c{index}") is not { } cl || cl.IsType("i");

    public float[]? VertexUVLayerBuffer(int index) => GetFloats($"u{index}");
    public void SetVertexUVLayerBuffer(int index, float[] values) => CreateProperty($"u{index}", CastPropertyType.Vector2).SetValues(values);
    public void SetVertexUVLayerBuffer(int index, ReadOnlySpan<Vector2> values) => SetVertexUVLayerBuffer(index, Curve.Flat(values));
    public uint[]? VertexWeightBoneBuffer() => GetUInts("wb");
    public void SetVertexWeightBoneBuffer(ReadOnlySpan<uint> values) => SetIntegerBuffer("wb", values);
    public float[]? VertexWeightValueBuffer() => GetFloats("wv");
    public void SetVertexWeightValueBuffer(float[] values) => CreateProperty("wv", CastPropertyType.Float).SetValues(values);

    /// <summary>The sibling (child of this mesh's parent) whose hash <c>m</c> names.</summary>
    public CastNode? Material() => Referenced("m", ParentNode);
    public void SetMaterial(ulong hash) => SetHash("m", hash);
}

/// <summary>A 3d hair definition for a model.</summary>
public sealed class Hair(CastHashSequence hashes) : CastNode(Id, hashes)
{
    public const uint Id = 0x72696168;   // 'hair'

    public string? Name() => GetString("n");
    public void SetName(string name) => SetStringProperty("n", name);
    public int? StrandCount() => Property("se")?.ValueCount;
    public uint[]? SegmentsBuffer() => GetUInts("se");
    public void SetSegmentBuffer(ReadOnlySpan<uint> values) => SetIntegerBuffer("se", values);
    public float[]? ParticleBuffer() => GetFloats("pt");
    public void SetParticleBuffer(float[] values) => CreateProperty("pt", CastPropertyType.Vector3).SetValues(values);
    public void SetParticleBuffer(ReadOnlySpan<Vector3> values) => SetParticleBuffer(Curve.Flat(values));
    public CastNode? Material() => Referenced("m", ParentNode);
    public void SetMaterial(ulong hash) => SetHash("m", hash);
}

/// <summary>A blend shape: a base mesh and target vertex values.</summary>
public sealed class BlendShape(CastHashSequence hashes) : CastNode(Id, hashes)
{
    public const uint Id = 0x68736C62;   // 'blsh'

    public string? Name() => GetString("n");
    public void SetName(string name) => SetStringProperty("n", name);
    public CastNode? BaseShape() => Referenced("b", ParentNode);
    public void SetBaseShape(ulong hash) => SetHash("b", hash);
    public uint[]? TargetShapeVertexIndices() => GetUInts("vi");
    public void SetTargetShapeVertexIndices(ReadOnlySpan<uint> indices) => SetIntegerBuffer("vi", indices);
    public float[]? TargetShapeVertexPositions() => GetFloats("vp");
    public void SetTargetShapeVertexPositions(float[] positions) => CreateProperty("vp", CastPropertyType.Vector3).SetValues(positions);
    public void SetTargetShapeVertexPositions(ReadOnlySpan<Vector3> positions) => SetTargetShapeVertexPositions(Curve.Flat(positions));
    public float? TargetWeightScale() => Property("ts") is { } p ? (float)p.NumberAt(0) : null;
    public void SetTargetWeightScale(float scale) => SetFloat("ts", scale);
}

/// <summary>A collection of bones for a model or animation.</summary>
public sealed class Skeleton(CastHashSequence hashes) : CastNode(Id, hashes)
{
    public const uint Id = 0x6C656B73;   // 'skel'

    public List<Bone> Bones() => ChildrenOfType<Bone>();
    public Bone CreateBone() => CreateChild(new Bone(Hashes));
    public List<IKHandle> IKHandles() => ChildrenOfType<IKHandle>();
    public IKHandle CreateIKHandle() => CreateChild(new IKHandle(Hashes));
    public List<Constraint> Constraints() => ChildrenOfType<Constraint>();
    public Constraint CreateConstraint() => CreateChild(new Constraint(Hashes));
}

/// <summary>A 3d bone that belongs to a skeleton.</summary>
public sealed class Bone(CastHashSequence hashes) : CastNode(Id, hashes)
{
    public const uint Id = 0x656E6F62;   // 'bone'

    public string? Name() => GetString("n");
    public void SetName(string name) => SetStringProperty("n", name);

    /// <summary>Index of the parent bone, −1 for a root: the stored u32 reinterpreted as signed.</summary>
    public int ParentIndex() => Property("p") is { } p ? unchecked((int)(uint)(p.IntegerAt(0) & 0xFFFFFFFF)) : -1;

    /// <summary>Stored as <c>i</c>; a negative index is written as <c>index + 2^32</c>.</summary>
    public void SetParentIndex(int index) => CreateProperty("p", CastPropertyType.Integer).SetValues([unchecked((uint)index)]);

    public bool SegmentScaleCompensate() => GetFlag("ssc", true);
    public void SetSegmentScaleCompensate(bool enabled) => SetFlag("ssc", enabled);
    public float[]? LocalPosition() => GetFloats("lp");
    public void SetLocalPosition(ReadOnlySpan<float> position) => SetVector("lp", CastPropertyType.Vector3, position);
    public void SetLocalPosition(Vector3 position) => SetLocalPosition([position.X, position.Y, position.Z]);
    public float[]? LocalRotation() => GetFloats("lr");
    public void SetLocalRotation(ReadOnlySpan<float> rotation) => SetVector("lr", CastPropertyType.Vector4, rotation);
    public void SetLocalRotation(Quaternion rotation) => SetLocalRotation([rotation.X, rotation.Y, rotation.Z, rotation.W]);
    public float[]? WorldPosition() => GetFloats("wp");
    public void SetWorldPosition(ReadOnlySpan<float> position) => SetVector("wp", CastPropertyType.Vector3, position);
    public void SetWorldPosition(Vector3 position) => SetWorldPosition([position.X, position.Y, position.Z]);
    public float[]? WorldRotation() => GetFloats("wr");
    public void SetWorldRotation(ReadOnlySpan<float> rotation) => SetVector("wr", CastPropertyType.Vector4, rotation);
    public void SetWorldRotation(Quaternion rotation) => SetWorldRotation([rotation.X, rotation.Y, rotation.Z, rotation.W]);
    public float[]? Scale() => GetFloats("s");
    public void SetScale(ReadOnlySpan<float> scale) => SetVector("s", CastPropertyType.Vector3, scale);
    public void SetScale(Vector3 scale) => SetScale([scale.X, scale.Y, scale.Z]);
}

/// <summary>An ik chain and its constraints in the skeleton.</summary>
public sealed class IKHandle(CastHashSequence hashes) : CastNode(Id, hashes)
{
    public const uint Id = 0x64686B69;   // 'ikhd'

    public string? Name() => GetString("n");
    public void SetName(string name) => SetStringProperty("n", name);
    public CastNode? StartBone() => Referenced("sb", ParentNode);
    public void SetStartBone(ulong hash) => SetHash("sb", hash);
    public CastNode? EndBone() => Referenced("eb", ParentNode);
    public void SetEndBone(ulong hash) => SetHash("eb", hash);
    public CastNode? TargetBone() => Referenced("tb", ParentNode);
    public void SetTargetBone(ulong hash) => SetHash("tb", hash);
    public float[]? TargetOffset() => GetFloats("to");
    public void SetTargetOffset(ReadOnlySpan<float> offset) => SetVector("to", CastPropertyType.Vector3, offset);
    public void SetTargetOffset(Vector3 offset) => SetTargetOffset([offset.X, offset.Y, offset.Z]);
    public CastNode? PoleVectorBone() => Referenced("pv", ParentNode);
    public void SetPoleVectorBone(ulong hash) => SetHash("pv", hash);
    public CastNode? PoleBone() => Referenced("pb", ParentNode);
    public void SetPoleBone(ulong hash) => SetHash("pb", hash);
    public bool UseTargetRotation() => GetFlag("tr", false);
    public void SetUseTargetRotation(bool enabled) => SetFlag("tr", enabled);
}

/// <summary>A bone constraint in a skeleton.</summary>
public sealed class Constraint(CastHashSequence hashes) : CastNode(Id, hashes)
{
    public const uint Id = 0x74736E63;   // 'cnst'

    public string? Name() => GetString("n");
    public void SetName(string name) => SetStringProperty("n", name);
    public string? ConstraintType() => GetString("ct");
    public void SetConstraintType(string type) => SetStringProperty("ct", type);
    public CastNode? ConstraintBone() => Referenced("cb", ParentNode);
    public void SetConstraintBone(ulong hash) => SetHash("cb", hash);
    public CastNode? TargetBone() => Referenced("tb", ParentNode);
    public void SetTargetBone(ulong hash) => SetHash("tb", hash);
    public bool MaintainOffset() => GetFlag("mo", false);
    public void SetMaintainOffset(bool enabled) => SetFlag("mo", enabled);
    public float[]? CustomOffset() => GetFloats("co");

    /// <summary>3 values → <c>3v</c>, 4 → <c>4v</c>. As the prototype, any other length writes nothing.</summary>
    public void SetCustomOffset(ReadOnlySpan<float> offset)
    {
        if (offset.Length == 3) SetVector("co", CastPropertyType.Vector3, offset);
        else if (offset.Length == 4) SetVector("co", CastPropertyType.Vector4, offset);
    }

    public float Weight() => Property("wt") is { } p ? (float)p.NumberAt(0) : 1.0f;
    public void SetWeight(float weight) => SetFloat("wt", weight);
    public bool SkipX() => GetFlag("sx", false);
    public void SetSkipX(bool enabled) => SetFlag("sx", enabled);
    public bool SkipY() => GetFlag("sy", false);
    public void SetSkipY(bool enabled) => SetFlag("sy", enabled);
    public bool SkipZ() => GetFlag("sz", false);
    public void SetSkipZ(bool enabled) => SetFlag("sz", enabled);
}

/// <summary>A material: a collection of slot → file (or colour) mappings.</summary>
public sealed class Material(CastHashSequence hashes) : CastNode(Id, hashes)
{
    public const uint Id = 0x6C74616D;   // 'matl'

    public string? Name() => GetString("n");
    public void SetName(string name) => SetStringProperty("n", name);
    public string? Type() => GetString("t");
    public void SetType(string type) => SetStringProperty("t", type);

    /// <summary>
    /// Every property except <c>n</c> and <c>t</c>, in order, mapped to the child its hash names. As the prototype, that
    /// includes custom properties such as <c>bp_material_slot</c>, which map to null.
    /// </summary>
    public OrderedDictionary<string, CastNode?> Slots()
    {
        var slots = new OrderedDictionary<string, CastNode?>(StringComparer.Ordinal);
        foreach (var (slot, _) in Properties)
            if (slot != "n" && slot != "t") slots[slot] = Referenced(slot, this);
        return slots;
    }

    public void SetSlot(string slot, ulong hash) => SetHash(slot, hash);
    public File CreateFile() => CreateChild(new File(Hashes));
}

/// <summary>An external file reference.</summary>
public sealed class File(CastHashSequence hashes) : CastNode(Id, hashes)
{
    public const uint Id = 0x656C6966;   // 'file'

    public string? Path() => GetString("p");
    public void SetPath(string path) => SetStringProperty("p", path);
}

/// <summary>An rgba colour value node.</summary>
public sealed class Color(CastHashSequence hashes) : CastNode(Id, hashes)
{
    public const uint Id = 0x726C6F63;   // 'colr'

    public string? Name() => GetString("n");
    public void SetName(string name) => SetStringProperty("n", name);
    public string ColorSpace() => GetString("cs") ?? "srgb";
    public void SetColorSpace(string value) => SetStringProperty("cs", value);
    public float[]? Rgba() => GetFloats("rgba");
    public void SetRgba(ReadOnlySpan<float> rgba) => SetVector("rgba", CastPropertyType.Vector4, rgba);
    public void SetRgba(Vector4 rgba) => SetRgba([rgba.X, rgba.Y, rgba.Z, rgba.W]);
}

/// <summary>An instance of a cast scene.</summary>
public sealed class Instance(CastHashSequence hashes) : CastNode(Id, hashes)
{
    public const uint Id = 0x74736E69;   // 'inst'

    public string? Name() => GetString("n");
    public void SetName(string name) => SetStringProperty("n", name);
    /// <summary>The referenced file, looked up among this node's own children (the prototype does the same).</summary>
    public CastNode? ReferenceFile() => Referenced("rf", this);
    public void SetReferenceFile(ulong hash) => SetHash("rf", hash);
    public float[]? Position() => GetFloats("p");
    public void SetPosition(ReadOnlySpan<float> position) => SetVector("p", CastPropertyType.Vector3, position);
    public void SetPosition(Vector3 position) => SetPosition([position.X, position.Y, position.Z]);
    public float[]? Rotation() => GetFloats("r");
    public void SetRotation(ReadOnlySpan<float> rotation) => SetVector("r", CastPropertyType.Vector4, rotation);
    public void SetRotation(Quaternion rotation) => SetRotation([rotation.X, rotation.Y, rotation.Z, rotation.W]);
    public float[]? Scale() => GetFloats("s");
    public void SetScale(ReadOnlySpan<float> scale) => SetVector("s", CastPropertyType.Vector3, scale);
    public void SetScale(Vector3 scale) => SetScale([scale.X, scale.Y, scale.Z]);
}

/// <summary>Scene metadata.</summary>
public sealed class Metadata(CastHashSequence hashes) : CastNode(Id, hashes)
{
    public const uint Id = 0x6174656D;   // 'meta'

    public string? Author() => GetString("a");
    public void SetAuthor(string author) => SetStringProperty("a", author);
    public string? Software() => GetString("s");
    public void SetSoftware(string software) => SetStringProperty("s", software);
    public string? UpAxis() => GetString("up");
    public void SetUpAxis(string up) => SetStringProperty("up", up);
    public string? SceneRoot() => GetString("sr");
    public void SetSceneRoot(string root) => SetStringProperty("sr", root);
}

/// <summary>A root node.</summary>
public sealed class Root(CastHashSequence hashes) : CastNode(Id, hashes)
{
    public const uint Id = 0x746F6F72;   // 'root'

    public Model CreateModel() => CreateChild(new Model(Hashes));
    public Animation CreateAnimation() => CreateChild(new Animation(Hashes));
    public Instance CreateInstance() => CreateChild(new Instance(Hashes));
    public Metadata CreateMetadata() => CreateChild(new Metadata(Hashes));
}
