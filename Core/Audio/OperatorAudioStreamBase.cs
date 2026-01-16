#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using ManagedBass;
using ManagedBass.Mix;
using T3.Core.Logging;

namespace T3.Core.Audio;

/// <summary>
/// Base class for operator audio streams (stereo and spatial).
/// Contains common playback, metering, and export functionality.
/// </summary>
public abstract class OperatorAudioStreamBase
{
    public double Duration { get; protected set; }
    public int StreamHandle { get; protected set; }
    public int MixerStreamHandle { get; protected set; }
    public bool IsPaused { get; set; }
    public bool IsPlaying { get; set; }
    public string FilePath { get; protected set; } = string.Empty;

    protected float DefaultPlaybackFrequency { get; set; }
    protected float CurrentVolume = 1.0f;
    protected float CurrentSpeed = 1.0f;
    protected int CachedChannels;
    protected int CachedFrequency;
    protected bool IsStaleMuted;
    protected bool IsUserMuted;

    protected readonly List<float> WaveformBuffer = new();
    protected readonly List<float> SpectrumBuffer = new();
    protected const int WaveformSamples = 512;
    protected const int WaveformWindowSamples = 1024;
    protected const int SpectrumBands = 512;

    // Export metering
    protected float? ExportLevel;
    protected List<float>? ExportWaveform;
    protected List<float>? ExportSpectrum;

    protected OperatorAudioStreamBase() { }

    /// <summary>
    /// Loads a stream from file and adds it to the mixer.
    /// </summary>
    protected static bool TryLoadStreamCore(
        string filePath, 
        int mixerHandle, 
        BassFlags additionalFlags,
        out int streamHandle,
        out float defaultFreq,
        out ChannelInfo info,
        out double duration)
    {
        streamHandle = 0;
        defaultFreq = 0;
        info = default;
        duration = 0;

        if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
            return false;

        streamHandle = Bass.CreateStream(filePath, 0, 0, 
            BassFlags.Decode | BassFlags.Float | BassFlags.AsyncFile | additionalFlags);
        
        if (streamHandle == 0)
        {
            Log.Error($"[Audio] Error loading '{Path.GetFileName(filePath)}': {Bass.LastError}");
            return false;
        }

        Bass.ChannelGetAttribute(streamHandle, ChannelAttribute.Frequency, out defaultFreq);
        info = Bass.ChannelGetInfo(streamHandle);
        var bytes = Bass.ChannelGetLength(streamHandle);

        if (bytes <= 0)
        {
            Bass.StreamFree(streamHandle);
            streamHandle = 0;
            return false;
        }

        duration = Bass.ChannelBytes2Seconds(streamHandle, bytes);
        if (duration <= 0 || duration > 36000)
        {
            Bass.StreamFree(streamHandle);
            streamHandle = 0;
            return false;
        }

        if (!BassMix.MixerAddChannel(mixerHandle, streamHandle, BassFlags.MixerChanBuffer | BassFlags.MixerChanPause))
        {
            Log.Error($"[Audio] Failed to add stream to mixer: {Bass.LastError}");
            Bass.StreamFree(streamHandle);
            streamHandle = 0;
            return false;
        }

        // Start with volume at 0 - stream will be unmuted when Play() is triggered
        Bass.ChannelSetAttribute(streamHandle, ChannelAttribute.Volume, 0.0f);
        return true;
    }

    public virtual void Play()
    {
        IsStaleMuted = false;
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

    public virtual void Resume()
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
        IsStaleMuted = false;
        BassMix.ChannelFlags(StreamHandle, BassFlags.MixerChanPause, BassFlags.MixerChanPause);
        var position = Bass.ChannelSeconds2Bytes(StreamHandle, 0);
        BassMix.ChannelSetPosition(StreamHandle, position, PositionFlags.Bytes | PositionFlags.MixerReset);
    }

    public void SetStaleMuted(bool muted, string reason = "")
    {
        if (IsStaleMuted == muted) return;
        IsStaleMuted = muted;

        if (muted)
        {
            Bass.ChannelSetAttribute(StreamHandle, ChannelAttribute.Volume, 0.0f);
        }
        else if (IsPlaying && !IsPaused && !IsUserMuted)
        {
            Bass.ChannelSetAttribute(StreamHandle, ChannelAttribute.Volume, CurrentVolume);
        }
    }

    public void SetVolume(float volume, bool mute)
    {
        CurrentVolume = volume;
        IsUserMuted = mute;

        if (!IsPlaying) return;

        float finalVolume = (!mute && !IsStaleMuted) ? volume : 0.0f;
        Bass.ChannelSetAttribute(StreamHandle, ChannelAttribute.Volume, finalVolume);
    }

    public void SetSpeed(float speed)
    {
        if (Math.Abs(speed - CurrentSpeed) < 0.001f) return;

        var clampedSpeed = Math.Clamp(speed, 0.1f, 4f);
        Bass.ChannelGetAttribute(StreamHandle, ChannelAttribute.Frequency, out var currentFreq);
        var newFreq = (currentFreq / CurrentSpeed) * clampedSpeed;
        Bass.ChannelSetAttribute(StreamHandle, ChannelAttribute.Frequency, newFreq);
        CurrentSpeed = clampedSpeed;
    }

    public void Seek(float timeInSeconds)
    {
        var position = Bass.ChannelSeconds2Bytes(StreamHandle, timeInSeconds);
        BassMix.ChannelSetPosition(StreamHandle, position, PositionFlags.Bytes | PositionFlags.MixerReset);
    }

    public virtual void RestartAfterExport()
    {
        IsStaleMuted = false;

        var resetPosition = Bass.ChannelSeconds2Bytes(StreamHandle, 0);
        BassMix.ChannelSetPosition(StreamHandle, resetPosition, PositionFlags.Bytes | PositionFlags.MixerReset);
        BassMix.ChannelFlags(StreamHandle, 0, BassFlags.MixerChanPause);

        if (!IsUserMuted)
            Bass.ChannelSetAttribute(StreamHandle, ChannelAttribute.Volume, CurrentVolume);

        IsPlaying = true;
        IsPaused = false;
        Bass.ChannelUpdate(MixerStreamHandle, 0);
    }

    public void UpdateFromBuffer(float[] buffer)
    {
        float peak = 0f;
        for (int i = 0; i < buffer.Length; i++)
            peak = Math.Max(peak, Math.Abs(buffer[i]));
        ExportLevel = Math.Min(peak, 1f);

        ExportWaveform = new List<float>(WaveformSamples);
        int step = Math.Max(1, buffer.Length / WaveformSamples);
        for (int i = 0; i < WaveformSamples; i++)
        {
            int start = i * step;
            int end = Math.Min(start + step, buffer.Length);
            float sum = 0f;
            for (int j = start; j < end; j++)
                sum += Math.Abs(buffer[j]);
            ExportWaveform.Add((end > start) ? sum / (end - start) : 0f);
        }

        ExportSpectrum = new List<float>(new float[SpectrumBands]);
    }

    public void ClearExportMetering()
    {
        ExportLevel = null;
        ExportWaveform = null;
        ExportSpectrum = null;
    }

    public float GetLevel()
    {
        if (ExportLevel.HasValue) return ExportLevel.Value;
        if (!IsPlaying || (IsPaused && !IsStaleMuted)) return 0f;

        var level = BassMix.ChannelGetLevel(StreamHandle);
        if (level == -1) return 0f;

        var left = (level & 0xFFFF) / 32768f;
        var right = ((level >> 16) & 0xFFFF) / 32768f;
        return Math.Min(Math.Max(left, right), 1f);
    }

    public List<float> GetWaveform()
    {
        if (ExportWaveform != null) return ExportWaveform;
        if (!IsPlaying || (IsPaused && !IsStaleMuted)) return EnsureBuffer(WaveformBuffer, WaveformSamples);

        UpdateWaveformFromPcm();
        return WaveformBuffer;
    }

    public List<float> GetSpectrum()
    {
        if (ExportSpectrum != null) return ExportSpectrum;
        if (!IsPlaying || (IsPaused && !IsStaleMuted)) return EnsureBuffer(SpectrumBuffer, SpectrumBands);

        UpdateSpectrum();
        return SpectrumBuffer;
    }

    protected static List<float> EnsureBuffer(List<float> buffer, int size)
    {
        if (buffer.Count == 0)
            for (int i = 0; i < size; i++) buffer.Add(0f);
        return buffer;
    }

    protected void UpdateWaveformFromPcm()
    {
        if (CachedChannels <= 0) return;

        int sampleCount = WaveformWindowSamples * CachedChannels;
        var buffer = new short[sampleCount];
        int bytesReceived = BassMix.ChannelGetData(StreamHandle, buffer, sampleCount * sizeof(short));

        if (bytesReceived <= 0) return;

        int frames = (bytesReceived / sizeof(short)) / CachedChannels;
        if (frames <= 0) return;

        WaveformBuffer.Clear();
        float step = frames / (float)WaveformSamples;

        for (int i = 0; i < WaveformSamples; i++)
        {
            int frameIndex = Math.Min((int)(i * step), frames - 1);
            int frameBase = frameIndex * CachedChannels;
            float sum = 0f;

            for (int ch = 0; ch < CachedChannels; ch++)
                sum += Math.Abs(buffer[frameBase + ch] / 32768f);

            WaveformBuffer.Add(sum / CachedChannels);
        }
    }

    protected void UpdateSpectrum()
    {
        float[] spectrum = new float[SpectrumBands];
        int bytes = BassMix.ChannelGetData(StreamHandle, spectrum, (int)DataFlags.FFT512);
        if (bytes <= 0) return;

        SpectrumBuffer.Clear();
        for (int i = 0; i < SpectrumBands; i++)
        {
            var db = 20f * Math.Log10(Math.Max(spectrum[i], 1e-5f));
            SpectrumBuffer.Add((float)Math.Clamp((db + 60f) / 60f, 0f, 1f));
        }
    }

    public int RenderAudio(double startTime, double duration, float[] outputBuffer, int targetSampleRate, int targetChannels)
    {
        int nativeSampleRate = CachedFrequency > 0 ? CachedFrequency : 44100;
        int nativeChannels = GetNativeChannelCount();
        OperatorAudioUtils.FillAndResample(
            (s, d, buf) => RenderNativeAudio(s, d, buf),
            startTime, duration, outputBuffer,
            nativeSampleRate, nativeChannels, targetSampleRate, targetChannels);
        return outputBuffer.Length;
    }

    protected virtual int GetNativeChannelCount() => CachedChannels > 0 ? CachedChannels : 2;

    private int RenderNativeAudio(double startTime, double duration, float[] buffer)
    {
        int bytesToRead = buffer.Length * sizeof(float);
        int bytesRead = Bass.ChannelGetData(StreamHandle, buffer, bytesToRead);
        return bytesRead > 0 ? bytesRead / sizeof(float) : 0;
    }

    public double GetCurrentPosition()
    {
        long positionBytes = BassMix.ChannelGetPosition(StreamHandle);
        if (positionBytes < 0) positionBytes = 0;
        return Bass.ChannelBytes2Seconds(StreamHandle, positionBytes);
    }

    public void Dispose()
    {
        Bass.ChannelStop(StreamHandle);
        BassMix.MixerRemoveChannel(StreamHandle);
        Bass.StreamFree(StreamHandle);
    }

    public virtual void PrepareForExport()
    {
        IsPlaying = false;
        IsPaused = false;
        IsStaleMuted = true;

        Bass.ChannelSetAttribute(StreamHandle, ChannelAttribute.Volume, 0.0f);
        BassMix.ChannelFlags(StreamHandle, BassFlags.MixerChanPause, BassFlags.MixerChanPause);

        var resetPosition = Bass.ChannelSeconds2Bytes(StreamHandle, 0);
        BassMix.ChannelSetPosition(StreamHandle, resetPosition, PositionFlags.Bytes | PositionFlags.MixerReset);

        ClearExportMetering();
    }
}
