using NAudio.Wave;
using Nightrunner.Core.Audio;

namespace Nightrunner.UI;

internal sealed class AudioPlaybackSession : IDisposable
{
    private readonly VgmstreamDecoder _decoder;
    private readonly WasapiPlayer _output;
    private bool _disposed;

    public Exception? Error { get; private set; }
    public TimeSpan Duration => _decoder.Duration;
    public TimeSpan Position => _decoder.Position;
    public PlaybackState State => _output.PlaybackState;

    public float Volume
    {
        get => _output.Volume;
        set => _output.Volume = Math.Clamp(value, 0, 1);
    }

    public AudioPlaybackSession(VgmstreamDecoder decoder, float volume)
    {
        _decoder = decoder;
        WasapiPlayer? output = null;
        try
        {
            output = new WasapiPlayerBuilder().Build();
            output.PlaybackStopped += (_, e) =>
            {
                if (e.Exception is not null) Error = e.Exception;
            };
            output.Init(new DecoderWaveProvider(decoder));
            _output = output;
            Volume = volume;
        }
        catch
        {
            output?.Dispose();
            decoder.Dispose();
            throw;
        }
    }

    public void Play()
    {
        Error = null;
        if (_decoder.Position >= _decoder.Duration)
            _decoder.Seek(TimeSpan.Zero);
        _output.Play();
    }

    public void Pause() => _output.Pause();

    public void Stop()
    {
        _output.Stop();
        _decoder.Seek(TimeSpan.Zero);
    }

    public void Seek(TimeSpan position)
    {
        Error = null;
        bool playing = _output.PlaybackState == PlaybackState.Playing;
        // Flush queued WASAPI samples before moving the decoder.
        _output.Stop();
        _decoder.Seek(position);
        if (playing) _output.Play();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _output.Dispose();
        _decoder.Dispose();
    }

    private sealed class DecoderWaveProvider(VgmstreamDecoder decoder) : IWaveProvider
    {
        public WaveFormat WaveFormat { get; } = new(decoder.SampleRate, 16, decoder.Channels);
        public int Read(Span<byte> buffer) => decoder.Read(buffer);
    }
}
