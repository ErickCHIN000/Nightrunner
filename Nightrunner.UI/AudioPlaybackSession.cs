using NAudio.CoreAudioApi;
using NAudio.Wave;
using Nightrunner.Core.Audio;

namespace Nightrunner.UI;

internal sealed class AudioPlaybackSession : IDisposable
{
    private readonly VgmstreamDecoder _decoder;
    private readonly MMDevice _device;
    private readonly WasapiPlayer _output;
    private bool _pausedAfterSeek;
    private bool _disposed;

    public Exception? Error { get; private set; }
    public TimeSpan Duration => _decoder.Duration;
    public TimeSpan Position => _decoder.Position;

    /// <summary>A seek flushes the output, which leaves it stopped; a seek made while paused stays paused.</summary>
    public PlaybackState State => _pausedAfterSeek ? PlaybackState.Paused : _output.PlaybackState;

    public float Volume
    {
        get => _output.Volume;
        set => _output.Volume = Math.Clamp(value, 0, 1);
    }

    public AudioPlaybackSession(VgmstreamDecoder decoder, float volume)
    {
        _decoder = decoder;
        MMDevice? device = null;
        WasapiPlayer? output = null;
        try
        {
            // The session owns the device: a player the builder gives a device of its own never releases it, and the
            // volume's session objects on that device kept audio threads alive after every played sound.
            using (var devices = new MMDeviceEnumerator())
                device = devices.GetDefaultAudioEndpoint(DataFlow.Render, Role.Console);
            output = new WasapiPlayerBuilder().WithDevice(device).Build();
            output.PlaybackStopped += (_, e) =>
            {
                if (e.Exception is not null) Error = e.Exception;
            };
            output.Init(new DecoderWaveProvider(decoder));
            _device = device;
            _output = output;
            Volume = volume;
        }
        catch
        {
            output?.Dispose();
            device?.Dispose();
            decoder.Dispose();
            throw;
        }
    }

    public void Play()
    {
        Error = null;
        _pausedAfterSeek = false;
        if (_decoder.Position >= _decoder.Duration)
            _decoder.Seek(TimeSpan.Zero);
        _output.Play();
    }

    public void Pause()
    {
        if (_pausedAfterSeek) return;
        _output.Pause();
    }

    public void Stop()
    {
        _pausedAfterSeek = false;
        _output.Stop();
        _decoder.Seek(TimeSpan.Zero);
    }

    public void Seek(TimeSpan position)
    {
        Error = null;
        var state = State;
        // Flush queued WASAPI samples before moving the decoder.
        _output.Stop();
        _decoder.Seek(position);
        _pausedAfterSeek = state == PlaybackState.Paused;
        if (state == PlaybackState.Playing) _output.Play();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _output.Dispose();
        _device.Dispose();
        _decoder.Dispose();
    }

    private sealed class DecoderWaveProvider(VgmstreamDecoder decoder) : IWaveProvider
    {
        public WaveFormat WaveFormat { get; } = new(decoder.SampleRate, 16, decoder.Channels);
        public int Read(Span<byte> buffer) => decoder.Read(buffer);
    }
}
