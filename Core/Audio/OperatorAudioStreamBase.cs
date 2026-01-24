#nullable enable
using System;
using System.IO;
using System.Linq;
using ManagedBass;
using ManagedBass.Mix;
using T3.Core.Logging;
// ReSharper disable UnusedAutoPropertyAccessor.Global
// ReSharper disable UnusedAutoPropertyAccessor.Local
// ReSharper disable UnusedParameter.Local
// ReSharper disable InconsistentNaming

namespace T3.Core.Audio;

/// <summary>
/// Base class for operator audio streams (stereo and spatial).
/// Contains common playback, metering, and export functionality.
/// </summary>
public abstract class OperatorAudioStreamBase
{
    /// <summary>
    /// Gets the duration of the audio stream in seconds.
    /// </summary>
    internal double Duration { get; init; }
    
    /// <summary>
    /// Gets the BASS stream handle for this audio stream.
    /// </summary>
    public int StreamHandle { get; protected init; }
    
    /// <summary>
    /// Gets the BASS mixer stream handle that this stream is connected to.
    /// </summary>
    internal int MixerStreamHandle { get; init; }
    
    /// <summary>
    /// Gets or sets whether the stream is currently paused.
    /// </summary>
    internal bool IsPaused { get; set; }
    
    /// <summary>
    /// Gets or sets whether the stream is currently playing.
    /// </summary>
    internal bool IsPlaying { get; set; }
    
    /// <summary>
    /// Gets the file path of the loaded audio file.
    /// </summary>
    public string FilePath { get; protected set; } = string.Empty;

    /// <summary>
    /// Gets or sets the default playback frequency of the stream.
    /// </summary>
    protected float DefaultPlaybackFrequency { get; set; }
    
    /// <summary>
    /// The current volume level (0.0 to 1.0).
    /// </summary>
    private float CurrentVolume = 1.0f;
    
    /// <summary>
    /// The current playback speed multiplier.
    /// </summary>
    private float CurrentSpeed = 1.0f;
    
    /// <summary>
    /// Cached number of channels in the audio stream.
    /// </summary>
    protected int CachedChannels;
    
    /// <summary>
    /// Cached sample frequency of the audio stream.
    /// </summary>
    protected int CachedFrequency;
    
    /// <summary>
    /// Indicates whether the stream is muted due to being stale (not actively used).
    /// </summary>
    protected bool IsStaleMuted;
    
    /// <summary>
    /// Indicates whether the stream is muted by user request.
    /// </summary>
    private bool IsUserMuted;

    /// <summary>
    /// The audio level during export, if available.
    /// </summary>
    private float? ExportLevel;

    /// <summary>
    /// Attempts to load an audio stream from a file and add it to the mixer.
    /// </summary>
    /// <param name="filePath">The path to the audio file to load.</param>
    /// <param name="mixerHandle">The BASS mixer handle to add the stream to.</param>
    /// <param name="additionalFlags">Additional BASS flags to apply when creating the stream.</param>
    /// <param name="streamHandle">When successful, contains the created stream handle.</param>
    /// <param name="defaultFreq">When successful, contains the default frequency of the stream.</param>
    /// <param name="info">When successful, contains the channel information.</param>
    /// <param name="duration">When successful, contains the duration of the stream in seconds.</param>
    /// <returns><c>true</c> if the stream was successfully loaded and added to the mixer; otherwise, <c>false</c>.</returns>
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

    /// <summary>
    /// Starts playback of the audio stream.
    /// </summary>
    internal virtual void Play()
    {
        IsStaleMuted = false;
        BassMix.ChannelFlags(StreamHandle, 0, BassFlags.MixerChanPause);
        IsPlaying = true;
        IsPaused = false;
        Bass.ChannelUpdate(MixerStreamHandle, 0);
    }

    /// <summary>
    /// Pauses playback of the audio stream.
    /// </summary>
    internal void Pause()
    {
        if (!IsPlaying || IsPaused) return;
        BassMix.ChannelFlags(StreamHandle, BassFlags.MixerChanPause, BassFlags.MixerChanPause);
        IsPaused = true;
    }

    /// <summary>
    /// Resumes playback of a paused audio stream.
    /// </summary>
    internal virtual void Resume()
    {
        if (!IsPaused) return;
        BassMix.ChannelFlags(StreamHandle, 0, BassFlags.MixerChanPause);
        IsPaused = false;
        Bass.ChannelUpdate(MixerStreamHandle, 0);
    }

    /// <summary>
    /// Stops playback and resets the stream position to the beginning.
    /// </summary>
    internal void Stop()
    {
        IsPlaying = false;
        IsPaused = false;
        IsStaleMuted = false;
        BassMix.ChannelFlags(StreamHandle, BassFlags.MixerChanPause, BassFlags.MixerChanPause);
        var position = Bass.ChannelSeconds2Bytes(StreamHandle, 0);
        BassMix.ChannelSetPosition(StreamHandle, position, PositionFlags.Bytes | PositionFlags.MixerReset);
    }

    /// <summary>
    /// Sets the stale-muted state of the stream. Stale streams are muted to prevent audio artifacts.
    /// </summary>
    /// <param name="muted">Whether the stream should be muted due to being stale.</param>
    /// <param name="reason">Optional reason for the mute state change (for debugging).</param>
    internal void SetStaleMuted(bool muted, string reason = "")
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

    /// <summary>
    /// Sets the volume and mute state of the stream.
    /// </summary>
    /// <param name="volume">The volume level (0.0 to 1.0).</param>
    /// <param name="mute">Whether the stream should be muted.</param>
    internal void SetVolume(float volume, bool mute)
    {
        CurrentVolume = volume;
        IsUserMuted = mute;

        if (!IsPlaying) return;

        float finalVolume = (!mute && !IsStaleMuted) ? volume : 0.0f;
        Bass.ChannelSetAttribute(StreamHandle, ChannelAttribute.Volume, finalVolume);
    }

    /// <summary>
    /// Sets the playback speed of the stream.
    /// </summary>
    /// <param name="speed">The playback speed multiplier (clamped between 0.1 and 4.0).</param>
    internal void SetSpeed(float speed)
    {
        if (Math.Abs(speed - CurrentSpeed) < 0.001f) return;

        var clampedSpeed = Math.Clamp(speed, 0.1f, 4f);
        Bass.ChannelGetAttribute(StreamHandle, ChannelAttribute.Frequency, out var currentFreq);
        var newFreq = (currentFreq / CurrentSpeed) * clampedSpeed;
        Bass.ChannelSetAttribute(StreamHandle, ChannelAttribute.Frequency, newFreq);
        CurrentSpeed = clampedSpeed;
    }

    /// <summary>
    /// Seeks to a specific position in the audio stream.
    /// </summary>
    /// <param name="timeInSeconds">The position to seek to, in seconds.</param>
    internal void Seek(float timeInSeconds)
    {
        var position = Bass.ChannelSeconds2Bytes(StreamHandle, timeInSeconds);
        BassMix.ChannelSetPosition(StreamHandle, position, PositionFlags.Bytes | PositionFlags.MixerReset);
    }

    /// <summary>
    /// Restarts the stream after an export operation, resetting position and restoring playback state.
    /// </summary>
    internal virtual void RestartAfterExport()
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

    /// <summary>
    /// Updates the export level metering from an audio buffer.
    /// </summary>
    /// <param name="buffer">The audio buffer containing sample data.</param>
    internal void UpdateFromBuffer(float[] buffer)
    {
        float peak = buffer.Select(Math.Abs).Prepend(0f).Max();

        ExportLevel = Math.Min(peak, 1f);
    }

    /// <summary>
    /// Clears the export metering level.
    /// </summary>
    internal void ClearExportMetering()
    {
        ExportLevel = null;
    }

    /// <summary>
    /// Gets the current audio level of the stream for metering purposes.
    /// </summary>
    /// <returns>The peak audio level (0.0 to 1.0).</returns>
    internal float GetLevel()
    {
        if (ExportLevel.HasValue) return ExportLevel.Value;
        if (!IsPlaying || (IsPaused && !IsStaleMuted)) return 0f;

        var level = BassMix.ChannelGetLevel(StreamHandle);
        if (level == -1) return 0f;

        var left = (level & 0xFFFF) / 32768f;
        var right = ((level >> 16) & 0xFFFF) / 32768f;
        return Math.Min(Math.Max(left, right), 1f);
    }

    /// <summary>
    /// Renders audio data from this stream into an output buffer with resampling support.
    /// </summary>
    /// <param name="startTime">The start time in seconds.</param>
    /// <param name="duration">The duration to render in seconds.</param>
    /// <param name="outputBuffer">The buffer to write the rendered audio data to.</param>
    /// <param name="targetSampleRate">The target sample rate for the output.</param>
    /// <param name="targetChannels">The target number of channels for the output.</param>
    /// <returns>The number of samples written to the output buffer.</returns>
    public int RenderAudio(double startTime, double duration, float[] outputBuffer, int targetSampleRate, int targetChannels)
    {
        int nativeSampleRate = CachedFrequency > 0 ? CachedFrequency : 44100;
        int nativeChannels = GetNativeChannelCount();
        OperatorAudioUtils.FillAndResample(
            RenderNativeAudio,
            startTime, duration, outputBuffer,
            nativeSampleRate, nativeChannels, targetSampleRate, targetChannels);
        return outputBuffer.Length;
    }

    /// <summary>
    /// Gets the native channel count of the audio stream.
    /// </summary>
    /// <returns>The number of channels in the native audio stream.</returns>
    protected virtual int GetNativeChannelCount() => CachedChannels > 0 ? CachedChannels : 2;

    /// <summary>
    /// Renders audio data in the native format of the stream.
    /// </summary>
    /// <param name="startTime">The start time in seconds.</param>
    /// <param name="duration">The duration to render in seconds.</param>
    /// <param name="buffer">The buffer to write the audio data to.</param>
    /// <returns>The number of samples read.</returns>
    private int RenderNativeAudio(double startTime, double duration, float[] buffer)
    {
        int bytesToRead = buffer.Length * sizeof(float);
        int bytesRead = Bass.ChannelGetData(StreamHandle, buffer, bytesToRead);
        return bytesRead > 0 ? bytesRead / sizeof(float) : 0;
    }

    /// <summary>
    /// Gets the current playback position of the stream.
    /// </summary>
    /// <returns>The current position in seconds.</returns>
    public double GetCurrentPosition()
    {
        long positionBytes = BassMix.ChannelGetPosition(StreamHandle);
        if (positionBytes < 0) positionBytes = 0;
        return Bass.ChannelBytes2Seconds(StreamHandle, positionBytes);
    }

    /// <summary>
    /// Disposes of the audio stream, releasing all BASS resources.
    /// </summary>
    internal void Dispose()
    {
        Bass.ChannelStop(StreamHandle);
        BassMix.MixerRemoveChannel(StreamHandle);
        Bass.StreamFree(StreamHandle);
    }

    /// <summary>
    /// Prepares the stream for export by pausing playback, muting, and resetting position.
    /// </summary>
    internal virtual void PrepareForExport()
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
