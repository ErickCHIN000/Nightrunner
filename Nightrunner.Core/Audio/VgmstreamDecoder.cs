using System.Runtime.InteropServices;
using System.Numerics;
using System.Text;

namespace Nightrunner.Core.Audio;

public sealed class VgmstreamDecoder : IDisposable
{
    private readonly object _gate = new();
    private readonly FileStream _file;
    private readonly long _offset;
    private readonly long _length;
    private readonly string _name;
    private readonly IntPtr _namePointer;
    private GCHandle _self;
    private IntPtr _native;
    private bool _disposed;
    private Exception? _readError;

    public int Channels { get; }
    public int SampleRate { get; }
    public uint ChannelMask { get; }
    public long? LoopStartSample { get; }
    public long? LoopEndSample { get; }
    public bool HasLoop => LoopStartSample.HasValue;
    public string CodecName { get; }
    public int BlockAlign => Channels * 2;
    public long TotalSamples { get; }
    public TimeSpan Duration => TimeSpan.FromSeconds((double)TotalSamples / SampleRate);

    public TimeSpan Position
    {
        get
        {
            lock (_gate)
            {
                ThrowIfDisposed();
                long samples = Native.GetPosition(_native);
                return TimeSpan.FromSeconds((double)Math.Max(0, samples) / SampleRate);
            }
        }
    }

    /// <exception cref="AudioDecoderUnavailableException">The optional native decoder is missing or unusable (<see cref="AudioDecoding.Status"/>).</exception>
    public VgmstreamDecoder(AespCatalog catalog, int entryIndex, bool downmixToStereo = false)
    {
        if (AudioDecoding.Status is { Available: false } status)
            throw new AudioDecoderUnavailableException(status.Reason);
        var entry = catalog.Entries[entryIndex];
        var archive = catalog.Archives[entry.ArchiveId];
        _file = new FileStream(archive.Source.Path, FileMode.Open, FileAccess.Read,
                               FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.RandomAccess);
        _offset = entry.Offset;
        _length = entry.Size;
        _name = entry.Name;
        _namePointer = Marshal.StringToCoTaskMemUTF8(_name);
        // Weak: the native streamfile only calls back while one of this decoder's methods runs, and a strong handle would
        // keep a decoder nobody disposed alive (with its native state and archive handle) for the life of the process.
        _self = GCHandle.Alloc(this, GCHandleType.Weak);

        try
        {
            if (_file.Length != archive.FileLength || _offset < 0 || _length < 12 ||
                _offset > _file.Length || _length > _file.Length - _offset ||
                !BankMediaReader.IsCompleteWem(_file, _offset, _length))
                throw new IOException("Audio archive changed after scanning; reload the Explorer");

            // Playback can downmix; exports leave the original channel count intact.
            var config = new Native.Config
            {
                IgnoreLoop = 1,
                ForceSampleFormat = 1,
                AutoDownmixChannels = downmixToStereo ? 2 : 0,
            };
            IntPtr streamfile = CreateStreamfile();
            try { _native = Native.Create(streamfile, 0, ref config); }
            finally { Native.CloseStreamfile(streamfile); }
            if (_native == IntPtr.Zero || _readError is not null)
                throw new InvalidDataException(_readError is null ? "vgmstream could not decode this WEM" :
                                               $"vgmstream could not read this WEM: {_readError.Message}");

            // These native field offsets are guarded by the API version check above.
            IntPtr format = Marshal.ReadIntPtr(_native, IntPtr.Size);
            Channels = Marshal.ReadInt32(format, 0);
            SampleRate = Marshal.ReadInt32(format, 4);
            uint channelMask = unchecked((uint)Marshal.ReadInt32(format, 16));
            ChannelMask = BitOperations.PopCount(channelMask) == Channels ? channelMask : 0;
            int sampleFormat = Marshal.ReadInt32(format, 8);
            int sampleSize = Marshal.ReadInt32(format, 12);
            long streamSamples = Marshal.ReadInt64(format, 32);
            long loopStart = Marshal.ReadInt64(format, 40);
            long loopEnd = Marshal.ReadInt64(format, 48);
            TotalSamples = Marshal.ReadInt64(format, 64);
            if (Channels is < 1 or > 16 || SampleRate is < 8000 or > 384000 ||
                sampleFormat != 1 || sampleSize != 2 || TotalSamples < 0)
                throw new InvalidDataException("vgmstream returned an unsupported PCM format");
            if (streamSamples > 0 && loopStart >= 0 && loopEnd > loopStart &&
                loopEnd <= streamSamples && loopEnd <= TotalSamples)
            {
                LoopStartSample = loopStart;
                LoopEndSample = loopEnd;
            }
            byte[] codecBytes = new byte[128];
            Marshal.Copy(IntPtr.Add(format, 76), codecBytes, 0, codecBytes.Length);
            int codecLength = Array.IndexOf(codecBytes, (byte)0);
            CodecName = codecLength > 0 ? Encoding.UTF8.GetString(codecBytes, 0, codecLength) : "Unknown";
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public unsafe int Read(Span<byte> destination)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            int samples = destination.Length / BlockAlign;
            if (samples == 0) return 0;
            int status;
            fixed (byte* buffer = destination)
                status = Native.Fill(_native, (IntPtr)buffer, samples);
            // vgmstream fills silence when the streamfile comes up short, so a failed archive read shows only here
            if (status < 0 || _readError is not null)
                throw new IOException(_readError is null ? "WEM decoding failed" : _readError.Message, _readError);
            IntPtr decoder = Marshal.ReadIntPtr(_native, 2 * IntPtr.Size);
            int bytes = Marshal.ReadInt32(decoder, IntPtr.Size + 4);
            if (bytes < 0 || bytes > samples * BlockAlign)
                throw new InvalidDataException("vgmstream returned an invalid PCM byte count");
            return bytes;
        }
    }

    public void Seek(TimeSpan position)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            long sample = (long)Math.Clamp(position.TotalSeconds * SampleRate, 0, TotalSamples);
            Native.Reset(_native);
            if (sample > 0) Native.Seek(_native, sample);
        }
    }

    public void Dispose()
    {
        lock (_gate) Release(disposing: true);
        GC.SuppressFinalize(this);
    }

    /// <summary>A decoder nobody disposed still frees its native state; the archive handle finalizes on its own.</summary>
    ~VgmstreamDecoder() => Release(disposing: false);

    private void Release(bool disposing)
    {
        if (_disposed) return;
        _disposed = true;
        // libvgmstream_free only calls the streamfile close callback, which needs no managed state.
        if (_native != IntPtr.Zero) Native.Free(_native);
        _native = IntPtr.Zero;
        if (disposing) _file.Dispose();
        if (_self.IsAllocated) _self.Free();
        Marshal.FreeCoTaskMem(_namePointer);
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private IntPtr CreateStreamfile()
    {
        // Serve the WEM's archive slice without extracting a temporary file.
        IntPtr pointer = Marshal.AllocHGlobal(6 * IntPtr.Size);
        Marshal.WriteIntPtr(pointer, 0, GCHandle.ToIntPtr(_self));
        Marshal.WriteIntPtr(pointer, IntPtr.Size, Marshal.GetFunctionPointerForDelegate(ReadCallback));
        Marshal.WriteIntPtr(pointer, 2 * IntPtr.Size, Marshal.GetFunctionPointerForDelegate(SizeCallback));
        Marshal.WriteIntPtr(pointer, 3 * IntPtr.Size, Marshal.GetFunctionPointerForDelegate(NameCallback));
        Marshal.WriteIntPtr(pointer, 4 * IntPtr.Size, Marshal.GetFunctionPointerForDelegate(OpenCallback));
        Marshal.WriteIntPtr(pointer, 5 * IntPtr.Size, Marshal.GetFunctionPointerForDelegate(CloseCallback));
        return pointer;
    }

    private static VgmstreamDecoder Context(IntPtr userData) =>
        (VgmstreamDecoder)GCHandle.FromIntPtr(userData).Target!;

    private static unsafe int ReadBytes(IntPtr userData, IntPtr destination, long offset, int length)
    {
        var decoder = Context(userData);
        try
        {
            if (offset < 0 || length <= 0 || offset >= decoder._length) return 0;
            int count = (int)Math.Min(length, decoder._length - offset);
            return RandomAccess.Read(decoder._file.SafeFileHandle,
                                     new Span<byte>((void*)destination, count), decoder._offset + offset);
        }
        catch (Exception ex)
        {
            // Native callbacks cannot propagate managed exceptions across the ABI.
            decoder._readError = ex;
            return 0;
        }
    }

    private static long GetSize(IntPtr userData) => Context(userData)._length;
    private static IntPtr GetName(IntPtr userData) => Context(userData)._namePointer;

    private static IntPtr Open(IntPtr userData, IntPtr filename)
    {
        var decoder = Context(userData);
        string? requested = Marshal.PtrToStringUTF8(filename);
        return requested is not null &&
               Path.GetFileName(requested).Equals(decoder._name, StringComparison.OrdinalIgnoreCase)
            ? decoder.CreateStreamfile() : IntPtr.Zero;
    }

    private static void Close(IntPtr streamfile) => Marshal.FreeHGlobal(streamfile);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int ReadFunction(IntPtr userData, IntPtr destination, long offset, int length);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate long SizeFunction(IntPtr userData);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr NameFunction(IntPtr userData);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr OpenFunction(IntPtr userData, IntPtr filename);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void CloseFunction(IntPtr streamfile);

    private static readonly ReadFunction ReadCallback = ReadBytes;
    private static readonly SizeFunction SizeCallback = GetSize;
    private static readonly NameFunction NameCallback = GetName;
    private static readonly OpenFunction OpenCallback = Open;
    private static readonly CloseFunction CloseCallback = Close;

    private static class Native
    {
        [StructLayout(LayoutKind.Sequential)]
        internal struct Config
        {
            public byte DisableConfigOverride;
            public byte AllowPlayForever;
            public byte PlayForever;
            public byte IgnoreLoop;
            public byte ForceLoop;
            public byte ReallyForceLoop;
            public byte IgnoreFade;
            public double LoopCount;
            public double FadeTime;
            public double FadeDelay;
            public int StereoTrack;
            public int AutoDownmixChannels;
            public int ForceSampleFormat;
        }

        [DllImport("libvgmstream", EntryPoint = "libvgmstream_create", CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr Create(IntPtr streamfile, int subsong, ref Config config);
        [DllImport("libvgmstream", EntryPoint = "libstreamfile_close", CallingConvention = CallingConvention.Cdecl)]
        internal static extern void CloseStreamfile(IntPtr streamfile);
        [DllImport("libvgmstream", EntryPoint = "libvgmstream_fill", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int Fill(IntPtr decoder, IntPtr buffer, int samples);
        [DllImport("libvgmstream", EntryPoint = "libvgmstream_get_play_position", CallingConvention = CallingConvention.Cdecl)]
        internal static extern long GetPosition(IntPtr decoder);
        [DllImport("libvgmstream", EntryPoint = "libvgmstream_seek", CallingConvention = CallingConvention.Cdecl)]
        internal static extern void Seek(IntPtr decoder, long sample);
        [DllImport("libvgmstream", EntryPoint = "libvgmstream_reset", CallingConvention = CallingConvention.Cdecl)]
        internal static extern void Reset(IntPtr decoder);
        [DllImport("libvgmstream", EntryPoint = "libvgmstream_free", CallingConvention = CallingConvention.Cdecl)]
        internal static extern void Free(IntPtr decoder);
    }
}
