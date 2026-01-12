#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using ManagedBass;
using ManagedBass.Mix;
using T3.Core.Logging;
using T3.Core.Resource;

namespace T3.Core.Audio;

/// <summary>
/// Represents a stereo audio stream for operator-based playback (plays at normal speed, not synchronized to timeline)
/// </summary>
public sealed class StereoOperatorAudioStream
{
    private StereoOperatorAudioStream()
    {
    }

    public double Duration;
    public int StreamHandle;
    public int MixerStreamHandle;
    public bool IsPaused;
    public bool IsPlaying;
    private float DefaultPlaybackFrequency { get; set; }
    private string FilePath = string.Empty;
    private float _currentVolume = 1.0f;
    private float _currentPanning = 0.0f;
    private float _currentSpeed = 1.0f;
    
    // Cached channel info to avoid calling Bass.ChannelGetInfo in hot paths (can deadlock)
    private int _cachedChannels;
    private int _cachedFrequency;

    // Waveform and spectrum buffers
    private readonly List<float> _waveformBuffer = new();
    private readonly List<float> _spectrumBuffer = new();
    private const int WaveformSamples = 512;
    private const int WaveformWindowSamples = 1024;
    private const int SpectrumBands = 512;

    // Stale detection - managed by AudioEngine
    private bool _isStaleMuted;
    private bool _isUserMuted; // Track user mute state

    public void SetStaleMuted(bool muted, string reason = "")
    {
        if (_isStaleMuted == muted)
            return; // No change

        _isStaleMuted = muted;
        
        var fileName = Path.GetFileName(FilePath);
        
        if (muted)
        {
            // Mute by setting volume to 0 (stream continues playing in background)
            Bass.ChannelSetAttribute(StreamHandle, ChannelAttribute.Volume, 0.0f);
            AudioConfig.LogDebug($"[StereoAudio] MUTED (stale): {fileName} | Reason: {reason}");
        }
        else
        {
            // Unmute: only restore volume if stream is playing, not user-paused, AND not user-muted
            if (IsPlaying && !IsPaused && !_isUserMuted)
            {
                Bass.ChannelSetAttribute(StreamHandle, ChannelAttribute.Volume, _currentVolume);
                AudioConfig.LogDebug($"[StereoAudio] UNMUTED (active): {fileName} | Reason: {reason}");
            }
            else if (_isUserMuted)
            {
                AudioConfig.LogDebug($"[StereoAudio] UNMUTED (active) but user muted: {fileName} | Reason: {reason}");
            }
        }
    }
    
    // Diagnostic tracking
    private int _updateCount;

    internal static bool TryLoadStream(string filePath, int mixerHandle, [NotNullWhen(true)] out StereoOperatorAudioStream? stream)
    {
        stream = null;

        if (string.IsNullOrEmpty(filePath))
            return false;

        if (!File.Exists(filePath))
        {
            Log.Error($"Audio file '{filePath}' does not exist.");
            return false;
        }

        var fileName = Path.GetFileName(filePath);
        var fileSize = new FileInfo(filePath).Length;
        AudioConfig.LogDebug($"[StereoAudio] Loading: {fileName} ({fileSize} bytes)");

        // Create stream as a DECODE stream for mixer compatibility
        // With BASS FLAC plugin loaded, FLAC files will use native decoding (CType=FLAC)
        // instead of Media Foundation (CType=MF), which provides better length detection
        var startTime = DateTime.Now;
        var streamHandle = Bass.CreateStream(filePath, 0, 0, BassFlags.Decode | BassFlags.Float | BassFlags.AsyncFile);
        var createTime = (DateTime.Now - startTime).TotalMilliseconds;

        if (streamHandle == 0)
        {
            var error = Bass.LastError;
            Log.Error($"[StereoAudio] Error loading audio stream '{fileName}': {error}. CreateTime: {createTime:F2}ms");
            return false;
        }

        AudioConfig.LogDebug($"[StereoAudio] Stream created: Handle={streamHandle}, CreateTime: {createTime:F2}ms");

        Bass.ChannelGetAttribute(streamHandle, ChannelAttribute.Frequency, out var defaultPlaybackFrequency);

        // Get channel info for diagnostics - CACHE IT to avoid deadlocks later
        var info = Bass.ChannelGetInfo(streamHandle);

        // Log format information - with FLAC plugin, CType should be FLAC instead of MF
        AudioConfig.LogDebug($"[StereoAudio] Stream info for {fileName}: Channels={info.Channels}, Freq={info.Frequency}, CType={info.ChannelType}, Flags={info.Flags}");

        // Get length - with FLAC plugin, this should work reliably
        var bytes = Bass.ChannelGetLength(streamHandle);
        
        if (bytes <= 0)
        {
            Log.Error($"[StereoAudio] Failed to get valid length for audio stream {fileName} (bytes={bytes}, error={Bass.LastError}).");
            Bass.StreamFree(streamHandle);
            return false;
        }

        var duration = Bass.ChannelBytes2Seconds(streamHandle, bytes);
        
        // Sanity check
        if (duration <= 0 || duration > 36000) // Max 10 hours
        {
            Log.Error($"[StereoAudio] Invalid duration for audio stream {fileName}: {duration:F3} seconds (bytes={bytes})");
            Bass.StreamFree(streamHandle);
            return false;
        }

        AudioConfig.LogDebug($"[StereoAudio] Stream length: {duration:F3}s ({bytes} bytes)");

        // Add stream to mixer - decode streams are required for mixer sources
        // Use MixerChanBuffer for smoother playback and lower latency
        startTime = DateTime.Now;
        if (!BassMix.MixerAddChannel(mixerHandle, streamHandle, BassFlags.MixerChanBuffer))
        {
            Log.Error($"[StereoAudio] Failed to add stream to mixer: {Bass.LastError}");
            Bass.StreamFree(streamHandle);
            return false;
        }
        var mixerAddTime = (DateTime.Now - startTime).TotalMilliseconds;
        
        // Start paused - we'll unpause when Play() is called
        BassMix.ChannelFlags(streamHandle, BassFlags.MixerChanPause, BassFlags.MixerChanPause);

        // Force the mixer to start buffering data from this stream immediately
        startTime = DateTime.Now;
        Bass.ChannelUpdate(mixerHandle, 0);
        var updateTime = (DateTime.Now - startTime).TotalMilliseconds;

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

        var streamActive = Bass.ChannelIsActive(streamHandle);
        var mixerActive = Bass.ChannelIsActive(mixerHandle);
        var flags = BassMix.ChannelFlags(streamHandle, 0, 0);
        
        AudioConfig.LogInfo($"[StereoAudio] ✓ Loaded: {fileName} | Duration: {duration:F3}s | Handle: {streamHandle} | Channels: {info.Channels} | Freq: {info.Frequency} | MixerAdd: {mixerAddTime:F2}ms | Update: {updateTime:F2}ms | StreamActive: {streamActive} | MixerActive: {mixerActive} | Flags: {flags}");

        return true;
    }

    public void Play()
    {
        var fileName = Path.GetFileName(FilePath);
        
        if (IsPlaying && !IsPaused && !_isStaleMuted)
        {
            AudioConfig.LogDebug($"[StereoAudio] Play() - already playing: {fileName}");
            return;
        }

        var wasStale = _isStaleMuted;
        var wasPaused = IsPaused;
        
        AudioConfig.LogDebug($"[StereoAudio] Play() - Starting: {fileName} | WasStale: {wasStale} | WasPaused: {wasPaused}");
        
        // Clear stale-muted state when explicitly playing
        _isStaleMuted = false;
        
        // For mixer channels: clear the pause flag to unpause
        var startTime = DateTime.Now;
        var flagResult = BassMix.ChannelFlags(StreamHandle, 0, BassFlags.MixerChanPause);
        var flagTime = (DateTime.Now - startTime).TotalMilliseconds;
        
        IsPlaying = true;
        IsPaused = false;
        
        // Force the mixer to buffer data immediately after unpausing
        // This ensures short sounds start playing right away with minimal latency
        startTime = DateTime.Now;
        Bass.ChannelUpdate(MixerStreamHandle, 0);
        var updateTime = (DateTime.Now - startTime).TotalMilliseconds;
        
        var streamActive = Bass.ChannelIsActive(StreamHandle);
        var mixerActive = Bass.ChannelIsActive(MixerStreamHandle);
        var currentFlags = BassMix.ChannelFlags(StreamHandle, 0, 0);
        var isPausedInMixer = (currentFlags & BassFlags.MixerChanPause) != 0;
        
        AudioConfig.LogInfo($"[StereoAudio] ▶ Play(): {fileName} | FlagResult: {flagResult} | FlagTime: {flagTime:F2}ms | UpdateTime: {updateTime:F2}ms | StreamActive: {streamActive} | MixerActive: {mixerActive} | PausedInMixer: {isPausedInMixer}");
    }

    public void Pause()
    {
        if (!IsPlaying || IsPaused)
            return;

        // For mixer channels, set the paused flag
        BassMix.ChannelFlags(StreamHandle, BassFlags.MixerChanPause, BassFlags.MixerChanPause);
        IsPaused = true;
        
        var fileName = Path.GetFileName(FilePath);
        AudioConfig.LogDebug($"[StereoAudio] Paused: {fileName}");
    }

    public void Resume()
    {
        if (!IsPaused)
            return;

        // For mixer channels: clear the pause flag to unpause
        BassMix.ChannelFlags(StreamHandle, 0, BassFlags.MixerChanPause);
        IsPaused = false;
        
        // Force immediate buffer update for responsive playback
        Bass.ChannelUpdate(MixerStreamHandle, 0);
        
        var fileName = Path.GetFileName(FilePath);
        AudioConfig.LogDebug($"[StereoAudio] Resumed: {fileName}");
    }

    public void Stop()
    {
        IsPlaying = false;
        IsPaused = false;
        
        // Reset stale tracking when stopped
        _isStaleMuted = false;
        
        // For mixer channels, DON'T remove and re-add for short sounds
        // Instead, just pause and seek to start - this preserves the mixer connection
        BassMix.ChannelFlags(StreamHandle, BassFlags.MixerChanPause, BassFlags.MixerChanPause);
        
        // Seek to start while still in mixer - this is safe with the pause flag set
        var position = Bass.ChannelSeconds2Bytes(StreamHandle, 0);
        BassMix.ChannelSetPosition(StreamHandle, position, PositionFlags.Bytes | PositionFlags.MixerReset);
        
        var fileName = Path.GetFileName(FilePath);
        AudioConfig.LogDebug($"[StereoAudio] Stopped: {fileName}");
    }

    public void SetVolume(float volume, bool mute)
    {
        _currentVolume = volume;
        _isUserMuted = mute; // Track user mute state
        
        // Don't apply volume changes if we're not playing
        if (!IsPlaying)
            return;
        
        // Determine final volume based on mute states
        float finalVolume = 0.0f;
        
        if (!mute && !_isStaleMuted)
        {
            // Not muted by user or stale detection - use current volume
            finalVolume = volume;
        }
        // else: volume stays at 0 (muted by user or stale detection)
        
        Bass.ChannelSetAttribute(StreamHandle, ChannelAttribute.Volume, finalVolume);
    }

    public void SetPanning(float panning)
    {
        _currentPanning = panning;
        Bass.ChannelSetAttribute(StreamHandle, ChannelAttribute.Pan, panning);
    }

    public void SetSpeed(float speed)
    {
        if (Math.Abs(speed - _currentSpeed) < 0.001f)
            return;

        var clampedSpeed = Math.Max(0.1f, Math.Min(4f, speed));
        Bass.ChannelGetAttribute(StreamHandle, ChannelAttribute.Frequency, out var currentFreq);
        var newFreq = (currentFreq / _currentSpeed) * clampedSpeed;
        Bass.ChannelSetAttribute(StreamHandle, ChannelAttribute.Frequency, newFreq);
        _currentSpeed = clampedSpeed;
    }

    public void Seek(float timeInSeconds)
    {
        // For mixer channels, use ChannelSetPosition with MixerReset flag
        // This is safer and doesn't require removing/re-adding the stream
        var position = Bass.ChannelSeconds2Bytes(StreamHandle, timeInSeconds);
        BassMix.ChannelSetPosition(StreamHandle, position, PositionFlags.Bytes | PositionFlags.MixerReset);
        
        var fileName = Path.GetFileName(FilePath);
        AudioConfig.LogDebug($"[StereoAudio] Seeked: {fileName} to {timeInSeconds:F3}s");
    }

    // --- Metering for offline rendering/export ---
    private float? _exportLevel;
    private List<float>? _exportWaveform;
    private List<float>? _exportSpectrum;

    /// <summary>
    /// Update metering values from a rendered/export buffer (for offline export)
    /// </summary>
    public void UpdateFromBuffer(float[] buffer)
    {
        // Compute level (peak of all samples)
        float peak = 0f;
        for (int i = 0; i < buffer.Length; i++)
        {
            float abs = Math.Abs(buffer[i]);
            if (abs > peak)
                peak = abs;
        }
        _exportLevel = Math.Min(peak, 1f);

        // Compute waveform (downsample to WaveformSamples)
        _exportWaveform = new List<float>(WaveformSamples);
        int step = Math.Max(1, buffer.Length / WaveformSamples);
        for (int i = 0; i < WaveformSamples; i++)
        {
            int start = i * step;
            int end = Math.Min(start + step, buffer.Length);
            float sum = 0f;
            for (int j = start; j < end; j++)
                sum += Math.Abs(buffer[j]);
            float avg = (end > start) ? sum / (end - start) : 0f;
            _exportWaveform.Add(avg);
        }

        // Compute spectrum (simple FFT, fallback to zeros if not available)
        _exportSpectrum = new List<float>(SpectrumBands);
        // NOTE: For a real FFT, use a library like MathNet.Numerics. Here, just fill with zeros for now.
        for (int i = 0; i < SpectrumBands; i++)
            _exportSpectrum.Add(0f);
    }

    /// <summary>
    /// Clears export metering values so live metering resumes after export.
    /// Also triggers a live metering update if stream is playing.
    /// </summary>
    public void ClearExportMetering()
    {
        _exportLevel = null;
        _exportWaveform = null;
        _exportSpectrum = null;
        // Force live metering update if stream is playing
        if (IsPlaying && !IsPaused)
        {
            GetLevel();
            GetWaveform();
            GetSpectrum();
        }
    }

    public float GetLevel()
    {
        if (_exportLevel.HasValue)
            return _exportLevel.Value;
        // Debug log for troubleshooting metering after export
        //T3.Core.Logging.Log.Debug($"[Metering] IsPlaying={IsPlaying}, IsPaused={IsPaused}, StreamHandle={StreamHandle}, StaleMuted={_isStaleMuted}");
        var level = BassMix.ChannelGetLevel(StreamHandle);
        //T3.Core.Logging.Log.Debug($"[Metering] BassMix.ChannelGetLevel={level}");
        if (!IsPlaying || (IsPaused && !_isStaleMuted))
            return 0f;
        if (level == -1)
            return 0f;
        var left = level & 0xFFFF;
        var right = (level >> 16) & 0xFFFF;
        var leftLevel = left / 32768f;
        var rightLevel = right / 32768f;
        var peak = Math.Max(leftLevel, rightLevel);
        return Math.Min(peak, 1f);
    }

    public List<float> GetWaveform()
    {
        // Always fall back to live BASS data if export metering is not set
        if (_exportWaveform != null)
            return _exportWaveform;
        if (!IsPlaying || (IsPaused && !_isStaleMuted))
            return EnsureWaveformBuffer();
        UpdateWaveformFromPcm();
        return _waveformBuffer;
    }

    public List<float> GetSpectrum()
    {
        // Always fall back to live BASS data if export metering is not set
        if (_exportSpectrum != null)
            return _exportSpectrum;
        if (!IsPlaying || (IsPaused && !_isStaleMuted))
            return EnsureSpectrumBuffer();
        UpdateSpectrum();
        return _spectrumBuffer;
    }

    private List<float> EnsureWaveformBuffer()
    {
        if (_waveformBuffer.Count == 0)
        {
            for (int i = 0; i < WaveformSamples; i++)
                _waveformBuffer.Add(0f);
        }
        return _waveformBuffer;
    }

    private List<float> EnsureSpectrumBuffer()
    {
        if (_spectrumBuffer.Count == 0)
        {
            for (int i = 0; i < SpectrumBands; i++)
                _spectrumBuffer.Add(0f);
        }
        return _spectrumBuffer;
    }

    private void UpdateWaveformFromPcm()
    {
        // CRITICAL FIX: Use cached channel info instead of calling Bass.ChannelGetInfo
        // Bass.ChannelGetInfo can deadlock when called on mixer source channels
        // because it may try to acquire locks while BASS's mixer thread holds them
        
        var fileName = Path.GetFileName(FilePath);
        
        if (_cachedChannels <= 0)
        {
            Log.Warning($"[StereoAudio] UpdateWaveformFromPcm: Invalid cached channels ({_cachedChannels}) for {fileName}");
            return;
        }

        int sampleCount = WaveformWindowSamples * _cachedChannels;
        var buffer = new short[sampleCount];

        int bytesRequested = sampleCount * sizeof(short);
        
        // For mixer source channels, use BassMix.ChannelGetData
        AudioConfig.LogDebug($"[StereoAudio] UpdateWaveformFromPcm: About to call BassMix.ChannelGetData for {fileName} | Channels: {_cachedChannels} | BytesRequested: {bytesRequested}");
        
        int bytesReceived;
        try
        {
            bytesReceived = BassMix.ChannelGetData(StreamHandle, buffer, bytesRequested);
        }
        catch (Exception ex)
        {
            Log.Error($"[StereoAudio] UpdateWaveformFromPcm: Exception calling BassMix.ChannelGetData for {fileName}: {ex.Message}");
            return;
        }
        
        AudioConfig.LogDebug($"[StereoAudio] UpdateWaveformFromPcm: BassMix.ChannelGetData returned {bytesReceived} bytes for {fileName}");

        if (bytesReceived <= 0)
        {
            if (_updateCount < 10 || _updateCount % 100 == 0)
            {
                AudioConfig.LogDebug($"[StereoAudio] UpdateWaveformFromPcm: No data received for {fileName} | Error: {Bass.LastError}");
            }
            return;
        }

        int samplesReceived = bytesReceived / sizeof(short);
        int frames = samplesReceived / _cachedChannels;

        if (frames <= 0)
        {
            AudioConfig.LogDebug($"[StereoAudio] UpdateWaveformFromPcm: No frames for {fileName} | SamplesReceived: {samplesReceived} | Channels: {_cachedChannels}");
            return;
        }

        _waveformBuffer.Clear();

        float step = frames / (float)WaveformSamples;
        float pos = 0f;

        for (int i = 0; i < WaveformSamples; i++)
        {
            int frameIndex = (int)pos;
            if (frameIndex >= frames)
                frameIndex = frames - 1;

            int frameBase = frameIndex * _cachedChannels;
            float sum = 0f;

            for (int ch = 0; ch < _cachedChannels; ch++)
            {
                short s = buffer[frameBase + ch];
                sum += Math.Abs(s / 32768f);
            }

            float amp = sum / _cachedChannels;
            _waveformBuffer.Add(amp);

            pos += step;
        }
        
        if (_updateCount < 20)
        {
            AudioConfig.LogDebug($"[StereoAudio] UpdateWaveformFromPcm: SUCCESS for {fileName} | Frames: {frames} | WaveformSamples: {_waveformBuffer.Count}");
        }
    }

    private void UpdateSpectrum()
    {
        float[] spectrum = new float[SpectrumBands];
        
        // For mixer source channels, use BassMix.ChannelGetData
        int bytes = BassMix.ChannelGetData(StreamHandle, spectrum, (int)DataFlags.FFT512);

        if (bytes <= 0)
            return;

        _spectrumBuffer.Clear();

        for (int i = 0; i < SpectrumBands; i++)
        {
            var db = 20f * Math.Log10(Math.Max(spectrum[i], 1e-5f));
            var normalized = Math.Max(0f, Math.Min(1f, (db + 60f) / 60f));
            _spectrumBuffer.Add((float)normalized);
        }
    }

    /// <summary>
    /// Render audio for export, filling the buffer at the requested sample rate and channel count.
    /// </summary>
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

    // Native render: fill buffer at native sample rate/channels
    private int RenderNativeAudio(double startTime, double duration, float[] buffer)
    {
        int sampleCount = buffer.Length / (_cachedChannels > 0 ? _cachedChannels : 2);
        int bytesToRead = sampleCount * (_cachedChannels > 0 ? _cachedChannels : 2) * sizeof(float);
        int bytesRead = Bass.ChannelGetData(StreamHandle, buffer, bytesToRead);
        return bytesRead > 0 ? bytesRead / sizeof(float) : 0;
    }

    public void Dispose()
    {
        var fileName = Path.GetFileName(FilePath);
        AudioConfig.LogDebug($"[StereoAudio] Disposing: {fileName} | TotalUpdates: {_updateCount}");
        
        Bass.ChannelStop(StreamHandle);
        BassMix.MixerRemoveChannel(StreamHandle);
        Bass.StreamFree(StreamHandle);
    }
}
