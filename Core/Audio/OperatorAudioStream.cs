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
/// Represents an audio stream for operator-based playback (plays at normal speed, not synchronized to timeline)
/// </summary>
public sealed class OperatorAudioStream
{
    private OperatorAudioStream()
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

    // Stale detection for performance optimization
    private double _lastUpdateTime = double.NegativeInfinity;
    private bool _isMuted;
    private const double StaleThresholdSeconds = 0.1;
    
    // Diagnostic tracking
    private int _updateCount;
    private int _staleMuteCount;
    private double _streamStartTime = double.NegativeInfinity;

    internal static bool TryLoadStream(string filePath, int mixerHandle, [NotNullWhen(true)] out OperatorAudioStream? stream)
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
        Log.Debug($"[OperatorAudio] Loading: {fileName} ({fileSize} bytes)");

        // Create stream as a DECODE stream for mixer compatibility
        // With BASS FLAC plugin loaded, FLAC files will use native decoding (CType=FLAC)
        // instead of Media Foundation (CType=MF), which provides better length detection
        var startTime = DateTime.Now;
        var streamHandle = Bass.CreateStream(filePath, 0, 0, BassFlags.Decode | BassFlags.Float | BassFlags.AsyncFile);
        var createTime = (DateTime.Now - startTime).TotalMilliseconds;

        if (streamHandle == 0)
        {
            var error = Bass.LastError;
            Log.Error($"[OperatorAudio] Error loading audio stream '{fileName}': {error}. CreateTime: {createTime:F2}ms");
            return false;
        }

        Log.Debug($"[OperatorAudio] Stream created: Handle={streamHandle}, CreateTime: {createTime:F2}ms");

        Bass.ChannelGetAttribute(streamHandle, ChannelAttribute.Frequency, out var defaultPlaybackFrequency);

        // Get channel info for diagnostics - CACHE IT to avoid deadlocks later
        var info = Bass.ChannelGetInfo(streamHandle);

        // Log format information - with FLAC plugin, CType should be FLAC instead of MF
        Log.Debug($"[OperatorAudio] Stream info for {fileName}: Channels={info.Channels}, Freq={info.Frequency}, CType={info.ChannelType}, Flags={info.Flags}");

        // Get length - with FLAC plugin, this should work reliably
        var bytes = Bass.ChannelGetLength(streamHandle);
        
        if (bytes <= 0)
        {
            Log.Error($"[OperatorAudio] Failed to get valid length for audio stream {fileName} (bytes={bytes}, error={Bass.LastError}).");
            Bass.StreamFree(streamHandle);
            return false;
        }

        var duration = Bass.ChannelBytes2Seconds(streamHandle, bytes);
        
        // Sanity check
        if (duration <= 0 || duration > 36000) // Max 10 hours
        {
            Log.Error($"[OperatorAudio] Invalid duration for audio stream {fileName}: {duration:F3} seconds (bytes={bytes})");
            Bass.StreamFree(streamHandle);
            return false;
        }

        Log.Debug($"[OperatorAudio] Stream length: {duration:F3}s ({bytes} bytes)");

        // Add stream to mixer - decode streams are required for mixer sources
        // Use MixerChanBuffer for smoother playback and lower latency
        startTime = DateTime.Now;
        if (!BassMix.MixerAddChannel(mixerHandle, streamHandle, BassFlags.MixerChanBuffer))
        {
            Log.Error($"[OperatorAudio] Failed to add stream to mixer: {Bass.LastError}");
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

        stream = new OperatorAudioStream
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
        
        Log.Info($"[OperatorAudio] ✓ Loaded: {fileName} | Duration: {duration:F3}s | Handle: {streamHandle} | Channels: {info.Channels} | Freq: {info.Frequency} | MixerAdd: {mixerAddTime:F2}ms | Update: {updateTime:F2}ms | StreamActive: {streamActive} | MixerActive: {mixerActive} | Flags: {flags}");

        return true;
    }

    public void UpdateStaleDetection(double currentTime)
    {
        var timeSinceLastUpdate = currentTime - _lastUpdateTime;
        
        // On first update after stream creation, record start time
        if (_streamStartTime == double.NegativeInfinity)
        {
            _streamStartTime = currentTime;
        }
        
        // On first update, just record the time and don't check staleness
        if (_lastUpdateTime == double.NegativeInfinity)
        {
            _lastUpdateTime = currentTime;
            _updateCount++;
            
            var fileName = Path.GetFileName(FilePath);
            Log.Debug($"[OperatorAudio] First update: {fileName} | Time: {currentTime:F3}");
            return;
        }
        
        var isStale = timeSinceLastUpdate > StaleThresholdSeconds;
        var wasStale = _isMuted;
        
        _lastUpdateTime = currentTime;
        _updateCount++;

        // Only apply stale muting if the stream is actually playing
        // Don't interfere with stopped or manually paused streams
        if (!IsPlaying || IsPaused)
            return;

        // Handle stale -> active transition (unmute)
        if (wasStale && !isStale)
        {
            _isMuted = false;
            // Unmute by removing pause flag
            BassMix.ChannelFlags(StreamHandle, BassFlags.MixerChanBuffer, BassFlags.MixerChanPause);
            
            var fileName = Path.GetFileName(FilePath);
            Log.Debug($"[OperatorAudio] UNMUTED (stale->active): {fileName} | Updates: {_updateCount} | TimeSinceUpdate: {timeSinceLastUpdate:F3}s");
        }
        // Handle active -> stale transition (mute but keep stream alive)
        else if (!wasStale && isStale)
        {
            _isMuted = true;
            _staleMuteCount++;
            
            // Mute by setting pause flag
            BassMix.ChannelFlags(StreamHandle, BassFlags.MixerChanPause, BassFlags.MixerChanPause);
            
            var fileName = Path.GetFileName(FilePath);
            var timeSinceStart = currentTime - _streamStartTime;
            Log.Warning($"[OperatorAudio] MUTED (active->stale): {fileName} | Duration: {Duration:F3}s | TimeSinceStart: {timeSinceStart:F3}s | TimeSinceUpdate: {timeSinceLastUpdate:F3}s | Updates: {_updateCount} | MuteCount: {_staleMuteCount}");
        }
    }

    public void Play()
    {
        var fileName = Path.GetFileName(FilePath);
        
        if (IsPlaying && !IsPaused && !_isMuted)
        {
            Log.Debug($"[OperatorAudio] Play() - already playing: {fileName}");
            return;
        }

        var wasStale = _isMuted;
        var wasPaused = IsPaused;
        
        Log.Debug($"[OperatorAudio] Play() - Starting: {fileName} | WasStale: {wasStale} | WasPaused: {wasPaused}");
        
        // Clear stale-muted state when explicitly playing
        _isMuted = false;
        
        // Reset stale tracking timers when explicitly playing
        _lastUpdateTime = double.NegativeInfinity;
        _streamStartTime = double.NegativeInfinity;
        
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
        
        Log.Info($"[OperatorAudio] ▶ Play(): {fileName} | FlagResult: {flagResult} | FlagTime: {flagTime:F2}ms | UpdateTime: {updateTime:F2}ms | StreamActive: {streamActive} | MixerActive: {mixerActive} | PausedInMixer: {isPausedInMixer}");
    }

    public void Pause()
    {
        if (!IsPlaying || IsPaused)
            return;

        // For mixer channels, set the paused flag
        BassMix.ChannelFlags(StreamHandle, BassFlags.MixerChanPause, BassFlags.MixerChanPause);
        IsPaused = true;
        
        var fileName = Path.GetFileName(FilePath);
        Log.Debug($"[OperatorAudio] Paused: {fileName}");
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
        Log.Debug($"[OperatorAudio] Resumed: {fileName}");
    }

    public void Stop()
    {
        IsPlaying = false;
        IsPaused = false;
        
        // Reset stale tracking when stopped
        _isMuted = false;
        _lastUpdateTime = double.NegativeInfinity;
        _streamStartTime = double.NegativeInfinity;
        
        // For mixer channels, DON'T remove and re-add for short sounds
        // Instead, just pause and seek to start - this preserves the mixer connection
        BassMix.ChannelFlags(StreamHandle, BassFlags.MixerChanPause, BassFlags.MixerChanPause);
        
        // Seek to start while still in mixer - this is safe with the pause flag set
        var position = Bass.ChannelSeconds2Bytes(StreamHandle, 0);
        BassMix.ChannelSetPosition(StreamHandle, position, PositionFlags.Bytes | PositionFlags.MixerReset);
        
        var fileName = Path.GetFileName(FilePath);
        Log.Debug($"[OperatorAudio] Stopped: {fileName}");
    }

    public void SetVolume(float volume, bool mute)
    {
        _currentVolume = volume;
        
        // Don't apply muting if we're not playing
        if (!IsPlaying)
            return;
            
        if (mute || _isMuted)
        {
            // User requested mute OR stale-muted - use pause flag
            BassMix.ChannelFlags(StreamHandle, BassFlags.MixerChanPause, BassFlags.MixerChanPause);
        }
        else
        {
            // Not muted - remove pause flag and set volume
            BassMix.ChannelFlags(StreamHandle, 0, BassFlags.MixerChanPause);
            Bass.ChannelSetAttribute(StreamHandle, ChannelAttribute.Volume, volume);
        }
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
        Log.Debug($"[OperatorAudio] Seeked: {fileName} to {timeInSeconds:F3}s");
    }

    public float GetLevel()
    {
        // Return 0 if stream is not playing or user-paused (but not if just stale-muted)
        if (!IsPlaying || (IsPaused && !_isMuted))
            return 0f;
            
        // Use BassMix.ChannelGetLevel for much better performance than ChannelGetData
        // This is specifically optimized for mixer channels and has lower latency
        var level = BassMix.ChannelGetLevel(StreamHandle);
        
        if (level == -1)
        {
            // Log data read failures for debugging (only occasionally)
            if (_updateCount < 10 || _updateCount % 100 == 0)
            {
                var fileName = Path.GetFileName(FilePath);
                var channelActive = Bass.ChannelIsActive(StreamHandle);
                var mixerActive = Bass.ChannelIsActive(MixerStreamHandle);
                Log.Debug($"[OperatorAudio] GetLevel() failed: {fileName} | Updates: {_updateCount} | IsMuted: {_isMuted} | StreamActive: {channelActive} | MixerActive: {mixerActive} | Error: {Bass.LastError}");
            }
            return 0f;
        }

        // Extract left and right channel levels from the combined value
        var left = level & 0xFFFF;
        var right = (level >> 16) & 0xFFFF;
        
        // Convert to 0-1 range and take the maximum of both channels
        var leftLevel = left / 32768f;
        var rightLevel = right / 32768f;
        var peak = Math.Max(leftLevel, rightLevel);
        
        // Log successful level reads for short sounds (debugging)
        if (Duration < 1.0 && peak > 0f && _updateCount < 20)
        {
            var fileName = Path.GetFileName(FilePath);
            Log.Debug($"[OperatorAudio] GetLevel() SUCCESS: {fileName} | Peak: {peak:F3} | Updates: {_updateCount}");
        }
        
        return Math.Min(peak, 1f);
    }

    public List<float> GetWaveform()
    {
        // Return empty buffer if not playing or user-paused (but allow if just stale-muted)
        if (!IsPlaying || (IsPaused && !_isMuted))
            return EnsureWaveformBuffer();

        UpdateWaveformFromPcm();
        return _waveformBuffer;
    }

    public List<float> GetSpectrum()
    {
        // Return empty buffer if not playing or user-paused (but allow if just stale-muted)
        if (!IsPlaying || (IsPaused && !_isMuted))
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
            Log.Warning($"[OperatorAudio] UpdateWaveformFromPcm: Invalid cached channels ({_cachedChannels}) for {fileName}");
            return;
        }

        int sampleCount = WaveformWindowSamples * _cachedChannels;
        var buffer = new short[sampleCount];

        int bytesRequested = sampleCount * sizeof(short);
        
        // For mixer source channels, use BassMix.ChannelGetData
        Log.Debug($"[OperatorAudio] UpdateWaveformFromPcm: About to call BassMix.ChannelGetData for {fileName} | Channels: {_cachedChannels} | BytesRequested: {bytesRequested}");
        
        int bytesReceived;
        try
        {
            bytesReceived = BassMix.ChannelGetData(StreamHandle, buffer, bytesRequested);
        }
        catch (Exception ex)
        {
            Log.Error($"[OperatorAudio] UpdateWaveformFromPcm: Exception calling BassMix.ChannelGetData for {fileName}: {ex.Message}");
            return;
        }
        
        Log.Debug($"[OperatorAudio] UpdateWaveformFromPcm: BassMix.ChannelGetData returned {bytesReceived} bytes for {fileName}");

        if (bytesReceived <= 0)
        {
            if (_updateCount < 10 || _updateCount % 100 == 0)
            {
                Log.Debug($"[OperatorAudio] UpdateWaveformFromPcm: No data received for {fileName} | Error: {Bass.LastError}");
            }
            return;
        }

        int samplesReceived = bytesReceived / sizeof(short);
        int frames = samplesReceived / _cachedChannels;

        if (frames <= 0)
        {
            Log.Debug($"[OperatorAudio] UpdateWaveformFromPcm: No frames for {fileName} | SamplesReceived: {samplesReceived} | Channels: {_cachedChannels}");
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
            Log.Debug($"[OperatorAudio] UpdateWaveformFromPcm: SUCCESS for {fileName} | Frames: {frames} | WaveformSamples: {_waveformBuffer.Count}");
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

    public void Dispose()
    {
        var fileName = Path.GetFileName(FilePath);
        Log.Debug($"[OperatorAudio] Disposing: {fileName} | TotalUpdates: {_updateCount} | TotalStaleMutes: {_staleMuteCount}");
        
        Bass.ChannelStop(StreamHandle);
        BassMix.MixerRemoveChannel(StreamHandle);
        Bass.StreamFree(StreamHandle);
    }
}
