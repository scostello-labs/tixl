#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Numerics;
using ManagedBass;
using ManagedBass.Mix;
using T3.Core.Logging;

namespace T3.Core.Audio;

/// <summary>
/// Represents a 3D spatial audio stream for operator-based playback with 3D positioning
/// </summary>
public sealed class SpatialOperatorAudioStream
{
    private SpatialOperatorAudioStream()
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
    private float _currentSpeed = 1.0f;
    
    // 3D positioning parameters
    private Vector3 _position = Vector3.Zero;
    private Vector3 _velocity = Vector3.Zero;
    private Vector3 _orientation = new Vector3(0, 0, -1); // Default: facing forward
    private float _minDistance = 1.0f;
    private float _maxDistance = 100.0f;
    private Mode3D _3dMode = Mode3D.Normal; // Default 3D mode from ManagedBass
    
    // 3D processing parameters
    private float _innerAngleDegrees = 360.0f; // Full omnidirectional by default
    private float _outerAngleDegrees = 360.0f;
    private float _outerVolume = 1.0f; // Volume multiplier outside outer cone
    
    // Cached channel info
    private int _cachedChannels;
    private int _cachedFrequency;

    // Waveform and spectrum buffers
    private readonly List<float> _waveformBuffer = new();
    private readonly List<float> _spectrumBuffer = new();
    private const int WaveformSamples = 512;
    private const int WaveformWindowSamples = 1024;
    private const int SpectrumBands = 512;

    // Stale detection
    private double _lastUpdateTime = double.NegativeInfinity;
    private bool _isMuted;
    private const double StaleThresholdSeconds = 0.1;
    
    // Diagnostic tracking
    private int _updateCount;
    private int _staleMuteCount;
    private double _streamStartTime = double.NegativeInfinity;

    internal static bool TryLoadStream(string filePath, int mixerHandle, [NotNullWhen(true)] out SpatialOperatorAudioStream? stream)
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
        AudioConfig.LogDebug($"[SpatialAudio] Loading: {fileName} ({fileSize} bytes)");

        var startTime = DateTime.Now;
        // Create as mono stream with 3D flags for BASS 3D audio support
        // BASS 3D audio requires mono streams - if the file is stereo, we'll need to convert or use only first channel
        var streamHandle = Bass.CreateStream(filePath, 0, 0, 
            BassFlags.Decode | BassFlags.Float | BassFlags.AsyncFile | BassFlags.Mono);
        var createTime = (DateTime.Now - startTime).TotalMilliseconds;

        if (streamHandle == 0)
        {
            var error = Bass.LastError;
            Log.Error($"[SpatialAudio] Error loading audio stream '{fileName}': {error}. CreateTime: {createTime:F2}ms");
            return false;
        }

        AudioConfig.LogDebug($"[SpatialAudio] Stream created: Handle={streamHandle}, CreateTime: {createTime:F2}ms");

        Bass.ChannelGetAttribute(streamHandle, ChannelAttribute.Frequency, out var defaultPlaybackFrequency);

        // Get channel info - cache it
        var info = Bass.ChannelGetInfo(streamHandle);

        // Verify it's mono (required for 3D audio)
        if (info.Channels != 1)
        {
            Log.Warning($"[SpatialAudio] Audio file '{fileName}' has {info.Channels} channels. 3D audio requires mono. Will use first channel only.");
        }

        AudioConfig.LogDebug($"[SpatialAudio] Stream info for {fileName}: Channels={info.Channels}, Freq={info.Frequency}, CType={info.ChannelType}, Flags={info.Flags}");

        // Get length
        var bytes = Bass.ChannelGetLength(streamHandle);
        
        if (bytes <= 0)
        {
            Log.Error($"[SpatialAudio] Failed to get valid length for audio stream {fileName} (bytes={bytes}, error={Bass.LastError}).");
            Bass.StreamFree(streamHandle);
            return false;
        }

        var duration = Bass.ChannelBytes2Seconds(streamHandle, bytes);
        
        if (duration <= 0 || duration > 36000)
        {
            Log.Error($"[SpatialAudio] Invalid duration for audio stream {fileName}: {duration:F3} seconds (bytes={bytes})");
            Bass.StreamFree(streamHandle);
            return false;
        }

        AudioConfig.LogDebug($"[SpatialAudio] Stream length: {duration:F3}s ({bytes} bytes)");

        // Create stream as mono for better 3D positioning control
        // We'll handle 3D positioning through volume attenuation and panning
        startTime = DateTime.Now;
        if (!BassMix.MixerAddChannel(mixerHandle, streamHandle, BassFlags.MixerChanBuffer))
        {
            Log.Error($"[SpatialAudio] Failed to add stream to mixer: {Bass.LastError}");
            Bass.StreamFree(streamHandle);
            return false;
        }
        var mixerAddTime = (DateTime.Now - startTime).TotalMilliseconds;
        
        // Start paused
        BassMix.ChannelFlags(streamHandle, BassFlags.MixerChanPause, BassFlags.MixerChanPause);

        // Force mixer to buffer
        startTime = DateTime.Now;
        Bass.ChannelUpdate(mixerHandle, 0);
        var updateTime = (DateTime.Now - startTime).TotalMilliseconds;

        stream = new SpatialOperatorAudioStream
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

        // Initialize 3D attributes with default values
        stream.Initialize3DAudio();

        var streamActive = Bass.ChannelIsActive(streamHandle);
        var mixerActive = Bass.ChannelIsActive(mixerHandle);
        var flags = BassMix.ChannelFlags(streamHandle, 0, 0);
        
        AudioConfig.LogInfo($"[SpatialAudio] ✓ Loaded: {fileName} | Duration: {duration:F3}s | Handle: {streamHandle} | Channels: {info.Channels} | Freq: {info.Frequency} | MixerAdd: {mixerAddTime:F2}ms | Update: {updateTime:F2}ms | StreamActive: {streamActive} | MixerActive: {mixerActive} | Flags: {flags} | 3D: Enabled");

        return true;
    }

    /// <summary>
    /// Initialize BASS 3D audio attributes for this stream
    /// </summary>
    private void Initialize3DAudio()
    {
        // Set initial 3D attributes using BASS native 3D audio
        // Parameters: mode, min distance, max distance, inner angle, outer angle, outer volume
        if (!Bass.ChannelSet3DAttributes(StreamHandle, _3dMode, _minDistance, _maxDistance, 
            (int)_innerAngleDegrees, (int)_outerAngleDegrees, _outerVolume))
        {
            var error = Bass.LastError;
            Log.Warning($"[SpatialAudio] Failed to set 3D attributes: {error}");
        }
        else
        {
            AudioConfig.LogDebug($"[SpatialAudio] 3D attributes initialized | Mode: {_3dMode} | MinDist: {_minDistance} | MaxDist: {_maxDistance} | InnerAngle: {_innerAngleDegrees}° | OuterAngle: {_outerAngleDegrees}°");
        }

        // Set initial 3D position
        if (!Bass.ChannelSet3DPosition(StreamHandle, To3DVector(_position), To3DVector(_orientation), To3DVector(_velocity)))
        {
            var error = Bass.LastError;
            Log.Warning($"[SpatialAudio] Failed to set initial 3D position: {error}");
        }
    }

    /// <summary>
    /// Convert System.Numerics.Vector3 to ManagedBass.Vector3D
    /// </summary>
    private static ManagedBass.Vector3D To3DVector(Vector3 v)
    {
        return new ManagedBass.Vector3D(v.X, v.Y, v.Z);
    }

    public void UpdateStaleDetection(double currentTime)
    {
        var timeSinceLastUpdate = currentTime - _lastUpdateTime;
        
        if (_streamStartTime == double.NegativeInfinity)
        {
            _streamStartTime = currentTime;
        }
        
        if (_lastUpdateTime == double.NegativeInfinity)
        {
            _lastUpdateTime = currentTime;
            _updateCount++;
            
            var fileName = Path.GetFileName(FilePath);
            AudioConfig.LogDebug($"[SpatialAudio] First update: {fileName} | Time: {currentTime:F3}");
            return;
        }
        
        var isStale = timeSinceLastUpdate > StaleThresholdSeconds;
        var wasStale = _isMuted;
        
        _lastUpdateTime = currentTime;
        _updateCount++;

        if (!IsPlaying || IsPaused)
            return;

        if (wasStale && !isStale)
        {
            _isMuted = false;
            BassMix.ChannelFlags(StreamHandle, BassFlags.MixerChanBuffer, BassFlags.MixerChanPause);
            
            var fileName = Path.GetFileName(FilePath);
            AudioConfig.LogDebug($"[SpatialAudio] UNMUTED (stale->active): {fileName} | Updates: {_updateCount}");
        }
        else if (!wasStale && isStale)
        {
            _isMuted = true;
            _staleMuteCount++;
            
            BassMix.ChannelFlags(StreamHandle, BassFlags.MixerChanPause, BassFlags.MixerChanPause);
            
            var fileName = Path.GetFileName(FilePath);
            var timeSinceStart = currentTime - _streamStartTime;
            Log.Warning($"[SpatialAudio] MUTED (active->stale): {fileName} | Duration: {Duration:F3}s | TimeSinceStart: {timeSinceStart:F3}s | Updates: {_updateCount}");
        }
    }

    /// <summary>
    /// Update 3D position using native BASS 3D audio
    /// </summary>
    public void Update3DPosition(Vector3 position, float minDistance, float maxDistance)
    {
        // Update position for velocity calculation
        var deltaPos = position - _position;
        var timeDelta = 1.0f / 60.0f; // Assume ~60fps, could be improved with actual time delta
        _velocity = deltaPos / timeDelta;
        
        _position = position;
        _minDistance = Math.Max(0.1f, minDistance);
        _maxDistance = Math.Max(_minDistance + 0.1f, maxDistance);

        // Update 3D attributes if distance parameters changed
        if (!Bass.ChannelSet3DAttributes(StreamHandle, _3dMode, _minDistance, _maxDistance, 
            (int)_innerAngleDegrees, (int)_outerAngleDegrees, _outerVolume))
        {
            var error = Bass.LastError;
            if (_updateCount % 300 == 0) // Log errors occasionally to avoid spam
            {
                Log.Warning($"[SpatialAudio] Failed to update 3D attributes: {error}");
            }
        }

        // Update 3D position using BASS native 3D positioning
        if (!Bass.ChannelSet3DPosition(StreamHandle, To3DVector(_position), To3DVector(_orientation), To3DVector(_velocity)))
        {
            var error = Bass.LastError;
            if (_updateCount % 300 == 0) // Log errors occasionally to avoid spam
            {
                Log.Warning($"[SpatialAudio] Failed to update 3D position: {error}");
            }
        }

        // Apply 3D processing - this calculates the actual 3D effect based on listener position
        // Note: Bass.Apply3D() should be called once per frame, typically in the AudioEngine
        // We'll call it here for each stream update to ensure immediate response
        Bass.Apply3D();

        // Log position updates occasionally
        if (_updateCount % 60 == 0) // Every 60 updates (~1 second at 60fps)
        {
            var fileName = Path.GetFileName(FilePath);
            var listenerPos = AudioEngine.Get3DListenerPosition();
            var distance = Vector3.Distance(listenerPos, position);
            AudioConfig.LogDebug($"[SpatialAudio] Position update: {fileName} | Pos: {position} | Vel: {_velocity.Length():F2} | Dist: {distance:F2} | MinD: {_minDistance:F2} | MaxD: {_maxDistance:F2}");
        }
    }

    /// <summary>
    /// Set the 3D orientation (direction the sound source is facing)
    /// </summary>
    public void Set3DOrientation(Vector3 orientation)
    {
        _orientation = Vector3.Normalize(orientation);
        
        if (!Bass.ChannelSet3DPosition(StreamHandle, To3DVector(_position), To3DVector(_orientation), To3DVector(_velocity)))
        {
            var error = Bass.LastError;
            Log.Warning($"[SpatialAudio] Failed to update 3D orientation: {error}");
        }
        
        Bass.Apply3D();
    }

    /// <summary>
    /// Set the 3D cone angles for directional sound
    /// </summary>
    public void Set3DCone(float innerAngleDegrees, float outerAngleDegrees, float outerVolume)
    {
        _innerAngleDegrees = Math.Clamp(innerAngleDegrees, 0f, 360f);
        _outerAngleDegrees = Math.Clamp(outerAngleDegrees, 0f, 360f);
        _outerVolume = Math.Clamp(outerVolume, 0f, 1f);
        
        if (!Bass.ChannelSet3DAttributes(StreamHandle, _3dMode, _minDistance, _maxDistance, 
            (int)_innerAngleDegrees, (int)_outerAngleDegrees, _outerVolume))
        {
            var error = Bass.LastError;
            Log.Warning($"[SpatialAudio] Failed to update 3D cone: {error}");
        }
        
        Bass.Apply3D();
        
        var fileName = Path.GetFileName(FilePath);
        AudioConfig.LogDebug($"[SpatialAudio] Cone updated: {fileName} | Inner: {_innerAngleDegrees}° | Outer: {_outerAngleDegrees}° | OuterVol: {_outerVolume:F2}");
    }

    /// <summary>
    /// Set the 3D processing mode
    /// </summary>
    public void Set3DMode(Mode3D mode)
    {
        _3dMode = mode;
        
        if (!Bass.ChannelSet3DAttributes(StreamHandle, _3dMode, _minDistance, _maxDistance, 
            (int)_innerAngleDegrees, (int)_outerAngleDegrees, _outerVolume))
        {
            var error = Bass.LastError;
            Log.Warning($"[SpatialAudio] Failed to update 3D mode: {error}");
        }
        
        Bass.Apply3D();
        
        var fileName = Path.GetFileName(FilePath);
        AudioConfig.LogDebug($"[SpatialAudio] 3D mode updated: {fileName} | Mode: {_3dMode}");
    }

    public void Play()
    {
        var fileName = Path.GetFileName(FilePath);
        
        if (IsPlaying && !IsPaused && !_isMuted)
        {
            AudioConfig.LogDebug($"[SpatialAudio] Play() - already playing: {fileName}");
            return;
        }

        _isMuted = false;
        _lastUpdateTime = double.NegativeInfinity;
        _streamStartTime = double.NegativeInfinity;
        
        var startTime = DateTime.Now;
        var flagResult = BassMix.ChannelFlags(StreamHandle, 0, BassFlags.MixerChanPause);
        var flagTime = (DateTime.Now - startTime).TotalMilliseconds;
        
        IsPlaying = true;
        IsPaused = false;
        
        startTime = DateTime.Now;
        Bass.ChannelUpdate(MixerStreamHandle, 0);
        var updateTime = (DateTime.Now - startTime).TotalMilliseconds;
        
        // Apply 3D after starting playback
        Bass.Apply3D();
        
        AudioConfig.LogInfo($"[SpatialAudio] ▶ Play(): {fileName} | FlagTime: {flagTime:F2}ms | UpdateTime: {updateTime:F2}ms | Pos: {_position} | 3D: Enabled");
    }

    public void Pause()
    {
        if (!IsPlaying || IsPaused)
            return;

        BassMix.ChannelFlags(StreamHandle, BassFlags.MixerChanPause, BassFlags.MixerChanPause);
        IsPaused = true;
        
        var fileName = Path.GetFileName(FilePath);
        AudioConfig.LogDebug($"[SpatialAudio] Paused: {fileName}");
    }

    public void Resume()
    {
        if (!IsPaused)
            return;

        BassMix.ChannelFlags(StreamHandle, 0, BassFlags.MixerChanPause);
        IsPaused = false;
        
        Bass.ChannelUpdate(MixerStreamHandle, 0);
        Bass.Apply3D();
        
        var fileName = Path.GetFileName(FilePath);
        AudioConfig.LogDebug($"[SpatialAudio] Resumed: {fileName}");
    }

    public void Stop()
    {
        IsPlaying = false;
        IsPaused = false;
        
        _isMuted = false;
        _lastUpdateTime = double.NegativeInfinity;
        _streamStartTime = double.NegativeInfinity;
        
        BassMix.ChannelFlags(StreamHandle, BassFlags.MixerChanPause, BassFlags.MixerChanPause);
        
        var position = Bass.ChannelSeconds2Bytes(StreamHandle, 0);
        BassMix.ChannelSetPosition(StreamHandle, position, PositionFlags.Bytes | PositionFlags.MixerReset);
        
        var fileName = Path.GetFileName(FilePath);
        AudioConfig.LogDebug($"[SpatialAudio] Stopped: {fileName}");
    }

    public void SetVolume(float volume, bool mute)
    {
        _currentVolume = volume;
        
        if (!IsPlaying)
            return;
            
        if (mute || _isMuted)
        {
            BassMix.ChannelFlags(StreamHandle, BassFlags.MixerChanPause, BassFlags.MixerChanPause);
        }
        else
        {
            BassMix.ChannelFlags(StreamHandle, 0, BassFlags.MixerChanPause);
            Bass.ChannelSetAttribute(StreamHandle, ChannelAttribute.Volume, volume);
        }
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
        var position = Bass.ChannelSeconds2Bytes(StreamHandle, timeInSeconds);
        BassMix.ChannelSetPosition(StreamHandle, position, PositionFlags.Bytes | PositionFlags.MixerReset);
        
        var fileName = Path.GetFileName(FilePath);
        AudioConfig.LogDebug($"[SpatialAudio] Seeked: {fileName} to {timeInSeconds:F3}s");
    }

    public float GetLevel()
    {
        if (!IsPlaying || (IsPaused && !_isMuted))
            return 0f;
            
        var level = BassMix.ChannelGetLevel(StreamHandle);
        
        if (level == -1)
            return 0f;

        // For mono, both bytes contain the same level
        var monoLevel = level & 0xFFFF;
        var normalizedLevel = monoLevel / 32768f;
        
        return Math.Min(normalizedLevel, 1f);
    }

    public List<float> GetWaveform()
    {
        if (!IsPlaying || (IsPaused && !_isMuted))
            return EnsureWaveformBuffer();

        UpdateWaveformFromPcm();
        return _waveformBuffer;
    }

    public List<float> GetSpectrum()
    {
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
        var fileName = Path.GetFileName(FilePath);
        
        if (_cachedChannels <= 0)
        {
            Log.Warning($"[SpatialAudio] UpdateWaveformFromPcm: Invalid cached channels ({_cachedChannels}) for {fileName}");
            return;
        }

        int sampleCount = WaveformWindowSamples * _cachedChannels;
        var buffer = new short[sampleCount];
        int bytesRequested = sampleCount * sizeof(short);
        
        int bytesReceived = BassMix.ChannelGetData(StreamHandle, buffer, bytesRequested);

        if (bytesReceived <= 0)
            return;

        int samplesReceived = bytesReceived / sizeof(short);
        int frames = samplesReceived / _cachedChannels;

        if (frames <= 0)
            return;

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
    }

    private void UpdateSpectrum()
    {
        float[] spectrum = new float[SpectrumBands];
        
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
        AudioConfig.LogDebug($"[SpatialAudio] Disposing: {fileName} | TotalUpdates: {_updateCount} | TotalStaleMutes: {_staleMuteCount}");
        
        Bass.ChannelStop(StreamHandle);
        BassMix.MixerRemoveChannel(StreamHandle);
        Bass.StreamFree(StreamHandle);
    }
}
