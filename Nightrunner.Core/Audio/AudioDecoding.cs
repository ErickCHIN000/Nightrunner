using System.Runtime.InteropServices;
using Nightrunner.Core.Logging;

namespace Nightrunner.Core.Audio;

/// <param name="Available">libvgmstream (and its libvorbis) loaded and speaks the API this code was written against.</param>
/// <param name="Reason">Why not, in a few words, for the UI. Empty when available.</param>
/// <param name="Detail">What was looked for and where, for the log.</param>
public sealed record AudioDecoderStatus(bool Available, string Reason, string Detail);

/// <summary>Decoding needs the optional native decoder and it is missing or unusable.</summary>
public sealed class AudioDecoderUnavailableException(string reason)
    : NotSupportedException($"audio decoding unavailable: {reason}");

/// <summary>
/// Audio decoding (play, WAV export, codec details) is optional: it needs <c>libvgmstream.dll</c> and <c>libvorbis.dll</c>
/// beside the app, and the Microsoft Visual C++ 2015-2022 x64 runtime they link against. Browsing archives, event names and
/// raw WEM export are managed code and work without them. The check runs once per process; callers read
/// <see cref="Status"/> instead of trying the decoder and catching the failure.
/// </summary>
public static class AudioDecoding
{
    public const string DecoderFile = "libvgmstream.dll";
    public const string VorbisFile = "libvorbis.dll";
    public const uint ApiVersion = 0x01010000;

    private static readonly Lazy<AudioDecoderStatus> Probed = new(() =>
    {
        var status = Probe(AppContext.BaseDirectory, out var handle);
        if (status.Available)
        {
            // Every libvgmstream call resolves to the library the probe checked, never another one on the search path.
            NativeLibrary.SetDllImportResolver(typeof(AudioDecoding).Assembly,
                (name, _, _) => name == "libvgmstream" ? handle : IntPtr.Zero);
            Log.Info("audio", $"Audio decoder: {status.Detail}");
        }
        else
            Log.Warn("audio", $"Audio decoding unavailable: {status.Reason}. {status.Detail}");
        return status;
    });

    public static AudioDecoderStatus Status => Probed.Value;

    /// <summary>Check a folder for a usable decoder. The library stays loaded when it is usable.</summary>
    public static AudioDecoderStatus Probe(string folder) => Probe(folder, out _);

    private static unsafe AudioDecoderStatus Probe(string folder, out IntPtr handle)
    {
        handle = IntPtr.Zero;
        string decoder = Path.Combine(folder, DecoderFile);
        string vorbis = Path.Combine(folder, VorbisFile);
        if (!File.Exists(decoder))
            return new(false, $"{DecoderFile} not found", $"looked for {decoder}");
        if (!File.Exists(vorbis))
            return new(false, $"{VorbisFile} not found", $"{DecoderFile} needs {vorbis}");
        try
        {
            handle = NativeLibrary.Load(decoder);
        }
        catch (Exception e) when (e is DllNotFoundException or BadImageFormatException)
        {
            // The two DLLs are there, so what failed is one of their own imports.
            bool runtime = NativeLibrary.TryLoad("vcruntime140.dll", out var crt);
            if (runtime) NativeLibrary.Free(crt);
            return runtime
                ? new(false, $"{DecoderFile} did not load", $"{decoder}: {e.Message}")
                : new(false, "Visual C++ x64 runtime missing",
                      $"{DecoderFile} needs VCRUNTIME140.dll from the Microsoft Visual C++ 2015-2022 x64 Redistributable ({e.Message})");
        }
        if (!NativeLibrary.TryGetExport(handle, "libvgmstream_get_version", out var export))
        {
            NativeLibrary.Free(handle);
            handle = IntPtr.Zero;
            return new(false, $"{DecoderFile} is not libvgmstream", $"{decoder} has no libvgmstream_get_version");
        }
        uint version = ((delegate* unmanaged[Cdecl]<uint>)export)();
        if (version != ApiVersion)
        {
            NativeLibrary.Free(handle);
            handle = IntPtr.Zero;
            return new(false, $"libvgmstream API 0x{version:X8} unsupported",
                       $"{decoder} reports API 0x{version:X8}; this build needs 0x{ApiVersion:X8} (1.1.0)");
        }
        return new(true, "", $"{decoder}, API 1.1.0");
    }
}
