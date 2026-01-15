#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using ManagedBass;
using ManagedBass.Mix;
using T3.Core.Logging;

namespace T3.Core.Audio;

/// <summary>
/// Represents a stereo audio stream for operator-based playback.
/// </summary>
public sealed class StereoOperatorAudioStream
{
    public double Duration { get; private set; }
    public int StreamHandle { get; private set; }
    public int MixerStreamHandle { get; private set; }
    public bool IsPaused { get; set; }
    public bool IsPlaying { get; set; }
    public string FilePath { get; private set; } = string.Empty;
    
    private float DefaultPlaybackFrequency { get; set; }
    private float _currentVolume = 1.0f;
    private float _currentSpeed = 1.0f;
    private int _cachedChannels;
    private int _cachedFrequency;
    private bool _isStaleMuted;
    private bool _isUserMuted;

    private readonly List<float> _waveformBuffer = new();
    private readonly List<float> _spectrumBuffer = new();
    private const int WaveformSamples = 512;
    private const int WaveformWindowSamples = 1024;
    private const int SpectrumBands = 512;

    // Export metering
    private float? _exportLevel;
    private List<float>? _exportWaveform;
    private List<float>? _exportSpectrum;

    private StereoOperatorAudioStream() { }

    internal static bool TryLoadStream(string filePath, int mixerHandle, [NotNullWhen(true)] out StereoOperatorAudioStream? stream)
    {
        stream = null;

        if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
            return false;

        var streamHandle = Bass.CreateStream(filePath, 0, 0, BassFlags.Decode | BassFlags.Float | BassFlags.AsyncFile);
        if (streamHandle == 0)
        {
            Log.Error($"[StereoAudio] Error loading '{Path.GetFileName(filePath)}': {Bass.LastError}");
            return false;
        }

        Bass.ChannelGetAttribute(streamHandle, ChannelAttribute.Frequency, out var defaultPlaybackFrequency);
        var info = Bass.ChannelGetInfo(streamHandle);
        var bytes = Bass.ChannelGetLength(streamHandle);
        
        if (bytes <= 0)
        {
            Bass.StreamFree(streamHandle);
            return false;
        }

        var duration = Bass.ChannelBytes2Seconds(streamHandle, bytes);
        if (duration <= 0 || duration > 36000)
        {
            Bass.StreamFree(streamHandle);
            return false;
        }

        if (!BassMix.MixerAddChannel(mixerHandle, streamHandle, BassFlags.MixerChanBuffer))
        {
            Log.Error($"[StereoAudio] Failed to add stream to mixer: {Bass.LastError}");
            Bass.StreamFree(streamHandle);
            return false;
        }

        BassMix.ChannelFlags(streamHandle, BassFlags.MixerChanPause, BassFlags.MixerChanPause);
        Bass.ChannelUpdate(mixerHandle, 0);

        stream = new StereoOperatorAudioStream
        {
            StreamHandle = streamHandle,
            MixerStreamHandle = mixerHandle,
            DefaultPlaybackFrequency = defaultPlaybackFrequency,
            Duration = duration,
            FilePath = filePath,
            IsPlaying = true,
            IsPaused = false,
            _cachedChannels = info.Channels,
            _cachedFrequency = info.Frequency
        };

        return true;
    }

    public void Play()
    {
        _isStaleMuted = false;
        BassMix.ChannelFlags(StreamHandle, 0, BassFlags.MixerChanPause);
        IsPlaying = true;
        IsPaused = false;
        Bass.ChannelUpdate(MixerStreamHandle, 0);
    }

    public void Pause()
    {
        if (!IsPlaying || IsPaused) return;
        BassMix.ChannelFlags(StreamHandle, BassFlags.MixerChanPause, BassFlags.MixerChanPause);
        IsPaused = true;
    }

    public void Resume()
    {
        if (!IsPaused) return;
        BassMix.ChannelFlags(StreamHandle, 0, BassFlags.MixerChanPause);
        IsPaused = false;
        Bass.ChannelUpdate(MixerStreamHandle, 0);
    }

    public void Stop()
    {
        IsPlaying = false;
        IsPaused = false;
        _isStaleMuted = false;
        BassMix.ChannelFlags(StreamHandle, BassFlags.MixerChanPause, BassFlags.MixerChanPause);
        var position = Bass.ChannelSeconds2Bytes(StreamHandle, 0);
        BassMix.ChannelSetPosition(StreamHandle, position, PositionFlags.Bytes | PositionFlags.MixerReset);
    }

    public void SetStaleMuted(bool muted, string reason = "")
    {
        if (_isStaleMuted == muted) return;
        _isStaleMuted = muted;

        if (muted)
        {
            Bass.ChannelSetAttribute(StreamHandle, ChannelAttribute.Volume, 0.0f);
        }
        else if (IsPlaying && !IsPaused && !_isUserMuted)
        {
            Bass.ChannelSetAttribute(StreamHandle, ChannelAttribute.Volume, _currentVolume);
        }
    }

    public void SetVolume(float volume, bool mute)
    {
        _currentVolume = volume;
        _isUserMuted = mute;
        
        if (!IsPlaying) return;
        
        float finalVolume = (!mute && !_isStaleMuted) ? volume : 0.0f;
        Bass.ChannelSetAttribute(StreamHandle, ChannelAttribute.Volume, finalVolume);
    }

    public void SetPanning(float panning)
    {
        Bass.ChannelSetAttribute(StreamHandle, ChannelAttribute.Pan, panning);
    }

    public void SetSpeed(float speed)
    {
        if (Math.Abs(speed - _currentSpeed) < 0.001f) return;

        var clampedSpeed = Math.Clamp(speed, 0.1f, 4f);
        Bass.ChannelGetAttribute(StreamHandle, ChannelAttribute.Frequency, out var currentFreq);
        var newFreq = (currentFreq / _currentSpeed) * clampedSpeed;
        Bass.ChannelSetAttribute(StreamHandle, ChannelAttribute.Frequency, newFreq);
        _currentSpeed = clampedSpeed;
    }

    public void Seek(float timeInSeconds)
    {
        var position = Bass.ChannelSeconds2Bytes(StreamHandle, timeInSeconds);
        BassMix.ChannelSetPosition(StreamHandle, position, PositionFlags.Bytes | PositionFlags.MixerReset);
    }

    /// <summary>
    /// Restarts playback after export ends. Resets position and clears mute state.
    /// </summary>
    public void RestartAfterExport()
    {
        _isStaleMuted = false;
        
        // Reset position to start
        var resetPosition = Bass.ChannelSeconds2Bytes(StreamHandle, 0);
        BassMix.ChannelSetPosition(StreamHandle, resetPosition, PositionFlags.Bytes | PositionFlags.MixerReset);
        
        // Clear pause flag
        BassMix.ChannelFlags(StreamHandle, 0, BassFlags.MixerChanPause);
        
        // Restore volume
        if (!_isUserMuted)
            Bass.ChannelSetAttribute(StreamHandle, ChannelAttribute.Volume, _currentVolume);
        
        IsPlaying = true;
        IsPaused = false;
        
        Bass.ChannelUpdate(MixerStreamHandle, 0);
    }

    public void UpdateFromBuffer(float[] buffer)
    {
        float peak = 0f;
        for (int i = 0; i < buffer.Length; i++)
            peak = Math.Max(peak, Math.Abs(buffer[i]));
        _exportLevel = Math.Min(peak, 1f);

        _exportWaveform = new List<float>(WaveformSamples);
        int step = Math.Max(1, buffer.Length / WaveformSamples);
        for (int i = 0; i < WaveformSamples; i++)
        {
            int start = i * step;
            int end = Math.Min(start + step, buffer.Length);
            float sum = 0f;
            for (int j = start; j < end; j++)
                sum += Math.Abs(buffer[j]);
            _exportWaveform.Add((end > start) ? sum / (end - start) : 0f);
        }

        _exportSpectrum = new List<float>(new float[SpectrumBands]);
    }

    public void ClearExportMetering()
    {
        _exportLevel = null;
        _exportWaveform = null;
        _exportSpectrum = null;
    }

    public float GetLevel()
    {
        if (_exportLevel.HasValue) return _exportLevel.Value;
        if (!IsPlaying || (IsPaused && !_isStaleMuted)) return 0f;
        
        var level = BassMix.ChannelGetLevel(StreamHandle);
        if (level == -1) return 0f;
        
        var left = (level & 0xFFFF) / 32768f;
        var right = ((level >> 16) & 0xFFFF) / 32768f;
        return Math.Min(Math.Max(left, right), 1f);
    }

    public List<float> GetWaveform()
    {
        if (_exportWaveform != null) return _exportWaveform;
        if (!IsPlaying || (IsPaused && !_isStaleMuted)) return EnsureBuffer(_waveformBuffer, WaveformSamples);
        
        UpdateWaveformFromPcm();
        return _waveformBuffer;
    }

    public List<float> GetSpectrum()
    {
        if (_exportSpectrum != null) return _exportSpectrum;
        if (!IsPlaying || (IsPaused && !_isStaleMuted)) return EnsureBuffer(_spectrumBuffer, SpectrumBands);
        
        UpdateSpectrum();
        return _spectrumBuffer;
    }

    private static List<float> EnsureBuffer(List<float> buffer, int size)
    {
        if (buffer.Count == 0)
            for (int i = 0; i < size; i++) buffer.Add(0f);
        return buffer;
    }

    private void UpdateWaveformFromPcm()
    {
        if (_cachedChannels <= 0) return;

        int sampleCount = WaveformWindowSamples * _cachedChannels;
        var buffer = new short[sampleCount];
        int bytesReceived = BassMix.ChannelGetData(StreamHandle, buffer, sampleCount * sizeof(short));

        if (bytesReceived <= 0) return;

        int frames = (bytesReceived / sizeof(short)) / _cachedChannels;
        if (frames <= 0) return;

        _waveformBuffer.Clear();
        float step = frames / (float)WaveformSamples;

        for (int i = 0; i < WaveformSamples; i++)
        {
            int frameIndex = Math.Min((int)(i * step), frames - 1);
            int frameBase = frameIndex * _cachedChannels;
            float sum = 0f;

            for (int ch = 0; ch < _cachedChannels; ch++)
                sum += Math.Abs(buffer[frameBase + ch] / 32768f);

            _waveformBuffer.Add(sum / _cachedChannels);
        }
    }

    private void UpdateSpectrum()
    {
        float[] spectrum = new float[SpectrumBands];
        int bytes = BassMix.ChannelGetData(StreamHandle, spectrum, (int)DataFlags.FFT512);
        if (bytes <= 0) return;

        _spectrumBuffer.Clear();
        for (int i = 0; i < SpectrumBands; i++)
        {
            var db = 20f * Math.Log10(Math.Max(spectrum[i], 1e-5f));
            _spectrumBuffer.Add((float)Math.Clamp((db + 60f) / 60f, 0f, 1f));
        }
    }

    public int RenderAudio(double startTime, double duration, float[] outputBuffer, int targetSampleRate, int targetChannels)
    {
        int nativeSampleRate = _cachedFrequency > 0 ? _cachedFrequency : 44100;
        int nativeChannels = _cachedChannels > 0 ? _cachedChannels : 2;
        OperatorAudioUtils.FillAndResample(
            (s, d, buf) => RenderNativeAudio(s, d, buf),
            startTime, duration, outputBuffer,
            nativeSampleRate, nativeChannels, targetSampleRate, targetChannels);
        return outputBuffer.Length;
    }

    private int RenderNativeAudio(double startTime, double duration, float[] buffer)
    {
        int channels = _cachedChannels > 0 ? _cachedChannels : 2;
        int bytesToRead = buffer.Length * sizeof(float);
        int bytesRead = Bass.ChannelGetData(StreamHandle, buffer, bytesToRead);
        return bytesRead > 0 ? bytesRead / sizeof(float) : 0;
    }

    public void Dispose()
    {
        Bass.ChannelStop(StreamHandle);
        BassMix.MixerRemoveChannel(StreamHandle);
        Bass.StreamFree(StreamHandle);
    }
}
