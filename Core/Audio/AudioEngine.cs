#nullable enable
using System;
using System.Collections.Generic;
using ManagedBass;
using T3.Core.Animation;
using T3.Core.IO;
using T3.Core.Logging;
using T3.Core.Operator;
using T3.Core.Resource;

namespace T3.Core.Audio;

/// <summary>
/// Controls loading, playback and discarding of audio clips.
/// </summary>
public static class AudioEngine
{
    // --- Soundtrack (Timeline) Audio ---
    internal static readonly Dictionary<AudioClipResourceHandle, SoundtrackClipStream> SoundtrackClipStreams = new();
    private static readonly Dictionary<AudioClipResourceHandle, double> _updatedSoundtrackClipTimes = new();
    private static readonly List<AudioClipResourceHandle> _obsoleteSoundtrackHandles = new();

    public static void UseSoundtrackClip(AudioClipResourceHandle handle, double time)
    {
        _updatedSoundtrackClipTimes[handle] = time;
    }

    public static void ReloadSoundtrackClip(AudioClipResourceHandle handle)
    {
        if (SoundtrackClipStreams.TryGetValue(handle, out var stream))
        {
            Bass.StreamFree(stream.StreamHandle);
            SoundtrackClipStreams.Remove(handle);
        }

        UseSoundtrackClip(handle, 0);
    }

    public static void CompleteFrame(Playback playback, double frameDurationInSeconds)
    {
        if (!_bassInitialized)
        {
            // Initialize audio mixer FIRST - this will configure BASS with low-latency settings
            AudioMixerManager.Initialize();
            if (AudioMixerManager.OperatorMixerHandle != 0)
            {
                _bassInitialized = true;
                InitializeGlobalVolumeFromSettings(); // Ensure global mixer volume is set from settings
            }
            else
            {
                // Fallback: Initialize BASS if mixer failed
                Bass.Free();
                Bass.Init();
                AudioMixerManager.Initialize();
                _bassInitialized = true;
                InitializeGlobalVolumeFromSettings(); // Ensure global mixer volume is set from settings
            }
        }
        
        // For audio-soundtrack we update once every frame. For Wasapi-inputs, we process directly in the new data callback
        if(playback.Settings is { 
               Enabled: true, 
               AudioSource: PlaybackSettings.AudioSources.ProjectSoundTrack })
            AudioAnalysis.ProcessUpdate(playback.Settings.AudioGainFactor, playback.Settings.AudioDecayFactor);

        // Create new soundtrack streams
        foreach (var (handle, time) in _updatedSoundtrackClipTimes)
        {
            if (SoundtrackClipStreams.TryGetValue(handle, out var clip))
            {
                clip.TargetTime = time;
            }
            else if(!string.IsNullOrEmpty(handle.Clip.FilePath))
            {
                if (SoundtrackClipStream.TryLoadSoundtrackClip(handle, out var soundtrackClipStream))
                {
                    SoundtrackClipStreams[handle] = soundtrackClipStream;
                }
            }
        }

        var playbackSpeedChanged = Math.Abs(_lastPlaybackSpeed - playback.PlaybackSpeed) > 0.001f;
        _lastPlaybackSpeed = playback.PlaybackSpeed;

        var handledMainSoundtrack = false;
        foreach (var (handle, clipStream) in SoundtrackClipStreams)
        {
            clipStream.IsInUse = _updatedSoundtrackClipTimes.ContainsKey(clipStream.ResourceHandle);
            if (!clipStream.IsInUse && clipStream.ResourceHandle.Clip.DiscardAfterUse)
            {
                _obsoleteSoundtrackHandles.Add(handle);
            }
            else
            {
                if (!playback.IsRenderingToFile && playbackSpeedChanged)
                    clipStream.UpdateSoundtrackPlaybackSpeed(playback.PlaybackSpeed);

                if (handledMainSoundtrack || !clipStream.ResourceHandle.Clip.IsSoundtrack)
                    continue;

                handledMainSoundtrack = true;

                if (playback.IsRenderingToFile)
                {
                    AudioRendering.ExportAudioFrame(playback, frameDurationInSeconds, clipStream);
                }
                else
                {
                    UpdateFftBufferFromSoundtrack(clipStream.StreamHandle, playback);
                    clipStream.UpdateSoundtrackTime(playback);
                }
            }
        }

        foreach (var handle in _obsoleteSoundtrackHandles)
        {
            SoundtrackClipStreams[handle].DisableSoundtrackStream();
            SoundtrackClipStreams.Remove(handle);
        }
        
        // STALE DETECTION: Check all operator streams and mute those that weren't updated this frame
        CheckAndMuteStaleOperators(playback.FxTimeInBars);
        
        // Clear after loop to avoid keeping open references
        _obsoleteSoundtrackHandles.Clear();
        _updatedSoundtrackClipTimes.Clear();
    }

    public static void SetMute(bool configSoundtrackMute)
    {
        IsMuted = configSoundtrackMute;
    }

    public static bool IsMuted { get; private set; }
    // Optionally, add a new property for global mute if you want to use it in the engine
    public static bool IsGlobalMuted => ProjectSettings.Config.GlobalMute;



    internal static void UpdateFftBufferFromSoundtrack(int soundStreamHandle, Playback playback)
    {
        var dataFlags = (int)DataFlags.FFT2048; // This will return 1024 values
        
        
        // Do not advance playback if we are not in live mode
        if (playback.IsRenderingToFile)
        {
            // ReSharper disable once InconsistentNaming
            const int DataFlag_BASS_DATA_NOREMOVE = 268435456; // Internal id from ManagedBass
            dataFlags |= DataFlag_BASS_DATA_NOREMOVE;
        }

        if (playback.Settings is not { AudioSource: PlaybackSettings.AudioSources.ProjectSoundTrack }) 
            return;
        
        // Get FftGainBuffer
        _ = Bass.ChannelGetData(soundStreamHandle, AudioAnalysis.FftGainBuffer, dataFlags);

        
        // If requested, also fetch WaveFormData 
        if (!WaveFormProcessing.RequestedOnce) 
            return;
        
        const int lengthInBytes = AudioConfig.WaveformSampleCount << 2 << 1;
        
        // This will later be processed in WaveFormProcessing
        WaveFormProcessing.LastFetchResultCode = Bass.ChannelGetData(soundStreamHandle, 
                                                                     WaveFormProcessing.InterleavenSampleBuffer,  
                                                                     lengthInBytes);
    }

    public static int GetClipChannelCount(AudioClipResourceHandle? handle)
    {
        // By default, use stereo
        if (handle == null || !SoundtrackClipStreams.TryGetValue(handle, out var clipStream))
            return 2;

        Bass.ChannelGetInfo(clipStream.StreamHandle, out var info);
        return info.Channels;
    }

    // TODO: Rename to GetClipOrDefaultSampleRate
    public static int GetClipSampleRate(AudioClipResourceHandle? clip)
    {
        if (clip == null || !SoundtrackClipStreams.TryGetValue(clip, out var stream))
            return 48000;

        Bass.ChannelGetInfo(stream.StreamHandle, out var info);
        return info.Frequency;
    }

    #region Operator Audio Playback
    private static readonly Dictionary<Guid, StereoOperatorAudioState> _operatorAudioStates = new();
    private static readonly Dictionary<Guid, SpatialOperatorAudioState> _spatialOperatorAudioStates = new();
    
    // Track which operators were updated this frame for stale detection
    private static readonly HashSet<Guid> _operatorsUpdatedThisFrame = new();
    private static int _lastStaleCheckFrame = -1; // Track which frame we last checked for stale operators

    // 3D Listener position and orientation
    private static Vector3 _listenerPosition = Vector3.Zero;
    private static Vector3 _listenerForward = new Vector3(0, 0, 1);
    private static Vector3 _listenerUp = new Vector3(0, 1, 0);
    private static bool _3dInitialized = false;

    private class StereoOperatorAudioState
    {
        public StereoOperatorAudioStream? Stream;
        public string CurrentFilePath = string.Empty;
        public bool IsPaused;
        public float PreviousSeek = 0f;
        public bool PreviousPlay;
        public bool PreviousStop;
        public bool IsStale; // Track stale state to avoid redundant SetStaleMuted calls
    }

    private class SpatialOperatorAudioState
    {
        public SpatialOperatorAudioStream? Stream;
        public string CurrentFilePath = string.Empty;
        public bool IsPaused;
        public float PreviousSeek = 0f;
        public bool PreviousPlay;
        public bool PreviousStop;
        public bool IsStale; // Track stale state to avoid redundant SetStaleMuted calls
    }

    /// <summary>
    /// Set the 3D listener position and orientation for spatial audio
    /// </summary>
    public static void Set3DListenerPosition(Vector3 position, Vector3 forward, Vector3 up)
    {
        _listenerPosition = position;
        _listenerForward = forward;
        _listenerUp = up;

        if (!_3dInitialized)
        {
            _3dInitialized = true;
            AudioConfig.LogInfo($"[AudioEngine] 3D audio listener initialized | Position: {position} | Forward: {forward} | Up: {up}");
        }
    }

    /// <summary>
    /// Get the current 3D listener position
    /// </summary>
    public static Vector3 Get3DListenerPosition() => _listenerPosition;

    /// <summary>
    /// Get the current 3D listener forward direction
    /// </summary>
    public static Vector3 Get3DListenerForward() => _listenerForward;

    /// <summary>
    /// Get the current 3D listener up direction
    /// </summary>
    public static Vector3 Get3DListenerUp() => _listenerUp;

    public static void UpdateStereoOperatorPlayback(
        Guid operatorId,
        double localFxTime,
        string filePath,
        bool shouldPlay,
        bool shouldStop,
        float volume,
        bool mute,
        float panning,
        float speed = 1.0f,
        float seek = 0f)
    {
        // Mark this operator as updated this frame (active, not stale)
        _operatorsUpdatedThisFrame.Add(operatorId);
        
        // Ensure mixer is initialized
        if (AudioMixerManager.OperatorMixerHandle == 0)
        {
            AudioConfig.LogDebug($"[AudioEngine] UpdateOperatorPlayback called but mixer not initialized, initializing now...");
            AudioMixerManager.Initialize();
            
            if (AudioMixerManager.OperatorMixerHandle == 0)
            {
                Log.Warning("[AudioEngine] AudioMixerManager failed to initialize, cannot play audio");
                return;
            }
        }

        if (!_operatorAudioStates.TryGetValue(operatorId, out var state))
        {
            state = new StereoOperatorAudioState();
            _operatorAudioStates[operatorId] = state;
            AudioConfig.LogDebug($"[AudioEngine] Created new audio state for operator: {operatorId}");
        }

        // Resolve file path if it's relative
        string? resolvedFilePath = filePath;
        if (!string.IsNullOrEmpty(filePath) && !System.IO.File.Exists(filePath))
        {
            // Try to resolve as relative path
            if (ResourceManager.TryResolvePath(filePath, null, out var absolutePath, out _))
            {
                resolvedFilePath = absolutePath;
                AudioConfig.LogDebug($"[AudioEngine] Resolved path: {filePath} → {absolutePath}");
            }
        }

        // Handle file change
        if (state.CurrentFilePath != resolvedFilePath)
        {
            AudioConfig.LogDebug($"[AudioEngine] File path changed for operator {operatorId}: '{state.CurrentFilePath}' → '{resolvedFilePath}'");
            
            state.Stream?.Dispose();
            state.Stream = null;
            state.CurrentFilePath = resolvedFilePath ?? string.Empty;
            state.PreviousPlay = false;
            state.PreviousStop = false;

            if (!string.IsNullOrEmpty(resolvedFilePath))
            {
                var loadStartTime = DateTime.Now;
                if (StereoOperatorAudioStream.TryLoadStream(resolvedFilePath, AudioMixerManager.OperatorMixerHandle, out var stream))
                {
                    var loadTime = (DateTime.Now - loadStartTime).TotalMilliseconds;
                    state.Stream = stream;
                    AudioConfig.LogDebug($"[AudioEngine] Stream loaded in {loadTime:F2}ms for operator {operatorId}");
                }
                else
                {
                    Log.Error($"[AudioEngine] Failed to load stream for operator {operatorId}: {resolvedFilePath}");
                }
            }
        }

        if (state.Stream == null)
            return;

        // Detect play trigger (rising edge)
        var playTrigger = shouldPlay && !state.PreviousPlay;
        state.PreviousPlay = shouldPlay;

        // Detect stop trigger (rising edge)
        var stopTrigger = shouldStop && !state.PreviousStop;
        state.PreviousStop = shouldStop;
        
        // Log trigger events
        if (playTrigger)
            AudioConfig.LogDebug($"[AudioEngine] ▶ Play TRIGGER for operator {operatorId}");
        if (stopTrigger)
            AudioConfig.LogDebug($"[AudioEngine] ■ Stop TRIGGER for operator {operatorId}");

        // Handle stop trigger
        if (stopTrigger)
        {
            state.Stream.Stop();
            state.IsPaused = false;
            state.PreviousSeek = 0f;
            return;
        }

        // Handle play trigger - restart playback
        if (playTrigger)
        {
            var playStartTime = DateTime.Now;
            
            // Stop and restart from beginning
            state.Stream.Stop();
            state.Stream.Play();
            state.IsPaused = false;
            
            var playTime = (DateTime.Now - playStartTime).TotalMilliseconds;
            AudioConfig.LogInfo($"[AudioEngine] ▶ Play executed in {playTime:F2}ms for operator {operatorId}");
        }

        // Always update volume, mute, panning, and speed when stream is active
        if (state.Stream.IsPlaying)
        {
            state.Stream.SetVolume(volume, mute);
            state.Stream.SetPanning(panning);
            state.Stream.SetSpeed(speed);

            // Handle seek (0-1 normalized position)
            if (Math.Abs(seek - state.PreviousSeek) > 0.001f && seek >= 0f && seek <= 1f)
            {
                var seekTimeInSeconds = (float)(seek * state.Stream.Duration);
                state.Stream.Seek(seekTimeInSeconds);
                state.PreviousSeek = seek;
                AudioConfig.LogDebug($"[AudioEngine] Seek to {seek:F3} ({seekTimeInSeconds:F3}s) for operator {operatorId}");
            }
        }
    }

    public static void PauseOperator(Guid operatorId)
    {
        if (_operatorAudioStates.TryGetValue(operatorId, out var state) && state.Stream != null)
        {
            state.Stream.Pause();
            state.IsPaused = true;
        }
    }

    public static void ResumeOperator(Guid operatorId)
    {
        if (_operatorAudioStates.TryGetValue(operatorId, out var state) && state.Stream != null)
        {
            state.Stream.Resume();
            state.IsPaused = false;
        }
    }

    public static bool IsOperatorStreamPlaying(Guid operatorId)
    {
        return _operatorAudioStates.TryGetValue(operatorId, out var state) 
               && state.Stream != null 
               && state.Stream.IsPlaying 
               && !state.Stream.IsPaused;
    }

    public static bool IsOperatorPaused(Guid operatorId)
    {
        return _operatorAudioStates.TryGetValue(operatorId, out var state) 
               && state.Stream != null 
               && state.IsPaused;
    }

    public static float GetOperatorLevel(Guid operatorId)
    {
        if (_operatorAudioStates.TryGetValue(operatorId, out var state) && state.Stream != null)
        {
            return state.Stream.GetLevel();
        }
        return 0f;
    }

    public static List<float> GetOperatorWaveform(Guid operatorId)
    {
        if (_operatorAudioStates.TryGetValue(operatorId, out var state) && state.Stream != null)
        {
            return state.Stream.GetWaveform();
        }
        return new List<float>();
    }

    public static List<float> GetOperatorSpectrum(Guid operatorId)
    {
        if (_operatorAudioStates.TryGetValue(operatorId, out var state) && state.Stream != null)
        {
            return state.Stream.GetSpectrum();
        }
        return new List<float>();
    }

    public static void UnregisterOperator(Guid operatorId)
    {
        if (_operatorAudioStates.TryGetValue(operatorId, out var state))
        {
            state.Stream?.Dispose();
            _operatorAudioStates.Remove(operatorId);
        }
        
        if (_spatialOperatorAudioStates.TryGetValue(operatorId, out var spatialState))
        {
            spatialState.Stream?.Dispose();
            _spatialOperatorAudioStates.Remove(operatorId);
        }
    }

    #region Spatial Audio Playback
    /// <summary>
    /// Update spatial audio playback with 3D positioning
    /// </summary>
    public static void UpdateSpatialOperatorPlayback(
        Guid operatorId,
        double localFxTime,
        string filePath,
        bool shouldPlay,
        bool shouldStop,
        float volume,
        bool mute,
        Vector3 position,
        float minDistance,
        float maxDistance,
        float speed = 1.0f,
        float seek = 0f,
        Vector3? orientation = null,
        float innerConeAngle = 360f,
        float outerConeAngle = 360f,
        float outerConeVolume = 1.0f,
        int mode3D = 0)
    {
        // Mark this operator as updated this frame (active, not stale)
        _operatorsUpdatedThisFrame.Add(operatorId);
        
        // Ensure mixer is initialized
        if (AudioMixerManager.OperatorMixerHandle == 0)
        {
            AudioConfig.LogDebug($"[AudioEngine] UpdateSpatialOperatorPlayback called but mixer not initialized");
            AudioMixerManager.Initialize();
            
            if (AudioMixerManager.OperatorMixerHandle == 0)
            {
                Log.Warning("[AudioEngine] AudioMixerManager failed to initialize for spatial audio");
                return;
            }
        }

        // Ensure 3D audio is initialized
        if (!_3dInitialized)
        {
            Set3DListenerPosition(Vector3.Zero, new Vector3(0, 0, 1), new Vector3(0, 1, 0));
        }

        if (!_spatialOperatorAudioStates.TryGetValue(operatorId, out var state))
        {
            state = new SpatialOperatorAudioState();
            _spatialOperatorAudioStates[operatorId] = state;
            AudioConfig.LogDebug($"[AudioEngine] Created new spatial audio state for operator: {operatorId}");
        }

        // Resolve file path
        string? resolvedFilePath = filePath;
        if (!string.IsNullOrEmpty(filePath) && !System.IO.File.Exists(filePath))
        {
            if (ResourceManager.TryResolvePath(filePath, null, out var absolutePath, out _))
            {
                resolvedFilePath = absolutePath;
                AudioConfig.LogDebug($"[AudioEngine] Resolved spatial audio path: {filePath} → {absolutePath}");
            }
        }

        // Handle file change
        if (state.CurrentFilePath != resolvedFilePath)
        {
            AudioConfig.LogDebug($"[AudioEngine] Spatial audio file changed for operator {operatorId}: '{state.CurrentFilePath}' → '{resolvedFilePath}'");
            
            state.Stream?.Dispose();
            state.Stream = null;
            state.CurrentFilePath = resolvedFilePath ?? string.Empty;
            state.PreviousPlay = false;
            state.PreviousStop = false;

            if (!string.IsNullOrEmpty(resolvedFilePath))
            {
                var loadStartTime = DateTime.Now;
                if (SpatialOperatorAudioStream.TryLoadStream(resolvedFilePath, AudioMixerManager.OperatorMixerHandle, out var stream))
                {
                    var loadTime = (DateTime.Now - loadStartTime).TotalMilliseconds;
                    state.Stream = stream;
                    AudioConfig.LogDebug($"[AudioEngine] Spatial stream loaded in {loadTime:F2}ms for operator {operatorId}");
                }
                else
                {
                    Log.Error($"[AudioEngine] Failed to load spatial stream for operator {operatorId}: {resolvedFilePath}");
                }
            }
        }

        if (state.Stream == null)
            return;

        // Update 3D position ALWAYS (even when not playing, for smooth transitions)
        state.Stream.Update3DPosition(position, minDistance, maxDistance);

        // Detect play trigger
        var playTrigger = shouldPlay && !state.PreviousPlay;
        state.PreviousPlay = shouldPlay;

        // Detect stop trigger
        var stopTrigger = shouldStop && !state.PreviousStop;
        state.PreviousStop = shouldStop;
        
        if (playTrigger)
            AudioConfig.LogDebug($"[AudioEngine] ▶ Spatial Play TRIGGER for operator {operatorId}");
        if (stopTrigger)
            AudioConfig.LogDebug($"[AudioEngine] ■ Spatial Stop TRIGGER for operator {operatorId}");

        // Handle stop
        if (stopTrigger)
        {
            state.Stream.Stop();
            state.IsPaused = false;
            state.PreviousSeek = 0f;
            return;
        }

        // Handle play
        if (playTrigger)
        {
            var playStartTime = DateTime.Now;
            
            state.Stream.Stop();
            state.Stream.Play();
            state.IsPaused = false;
            
            var playTime = (DateTime.Now - playStartTime).TotalMilliseconds;
            AudioConfig.LogInfo($"[AudioEngine] ▶ Spatial Play executed in {playTime:F2}ms for operator {operatorId} at position {position}");
        }

        // Update volume, mute, and speed when stream is active
        if (state.Stream.IsPlaying)
        {
            state.Stream.SetVolume(volume, mute);
            state.Stream.SetSpeed(speed);

            // Update advanced 3D parameters if provided
            if (orientation.HasValue && orientation.Value.Length() > 0.001f)
            {
                state.Stream.Set3DOrientation(orientation.Value);
            }

            // Update cone parameters if they differ from defaults (360° = omnidirectional)
            if (Math.Abs(innerConeAngle - 360f) > 0.1f || Math.Abs(outerConeAngle - 360f) > 0.1f || Math.Abs(outerConeVolume - 1.0f) > 0.001f)
            {
                state.Stream.Set3DCone(innerConeAngle, outerConeAngle, outerConeVolume);
            }

            // Update 3D mode if not default (0 = Normal)
            if (mode3D != 0)
            {
                state.Stream.Set3DMode((Mode3D)mode3D);
            }

            // Handle seek
            if (Math.Abs(seek - state.PreviousSeek) > 0.001f && seek >= 0f && seek <= 1f)
            {
                var seekTimeInSeconds = (float)(seek * state.Stream.Duration);
                state.Stream.Seek(seekTimeInSeconds);
                state.PreviousSeek = seek;
                AudioConfig.LogDebug($"[AudioEngine] Spatial seek to {seek:F3} ({seekTimeInSeconds:F3}s) for operator {operatorId}");
            }
        }
    }

    public static void PauseSpatialOperator(Guid operatorId)
    {
        if (_spatialOperatorAudioStates.TryGetValue(operatorId, out var state) && state.Stream != null)
        {
            state.Stream.Pause();
            state.IsPaused = true;
        }
    }

    public static void ResumeSpatialOperator(Guid operatorId)
    {
        if (_spatialOperatorAudioStates.TryGetValue(operatorId, out var state) && state.Stream != null)
        {
            state.Stream.Resume();
            state.IsPaused = false;
        }
    }

    public static bool IsSpatialOperatorStreamPlaying(Guid operatorId)
    {
        return _spatialOperatorAudioStates.TryGetValue(operatorId, out var state) 
               && state.Stream != null 
               && state.Stream.IsPlaying 
               && !state.Stream.IsPaused;
    }

    public static bool IsSpatialOperatorPaused(Guid operatorId)
    {
        return _spatialOperatorAudioStates.TryGetValue(operatorId, out var state) 
               && state.Stream != null 
               && state.IsPaused;
    }

    public static float GetSpatialOperatorLevel(Guid operatorId)
    {
        if (_spatialOperatorAudioStates.TryGetValue(operatorId, out var state) && state.Stream != null)
        {
            return state.Stream.GetLevel();
        }
        return 0f;
    }

    public static List<float> GetSpatialOperatorWaveform(Guid operatorId)
    {
        if (_spatialOperatorAudioStates.TryGetValue(operatorId, out var state) && state.Stream != null)
        {
            return state.Stream.GetWaveform();
        }
        return new List<float>();
    }

    public static List<float> GetSpatialOperatorSpectrum(Guid operatorId)
    {
        if (_spatialOperatorAudioStates.TryGetValue(operatorId, out var state) && state.Stream != null)
        {
            return state.Stream.GetSpectrum();
        }
        return new List<float>();
    }
    #endregion
    #endregion

    private static double _lastPlaybackSpeed = 1;
    private static bool _bassInitialized;

    /// <summary>
    /// Check all operator audio streams and mute those that weren't updated this frame.
    /// Skipped during export rendering.
    /// </summary>
    private static void CheckAndMuteStaleOperators(double currentTime)
    {
        // Skip during export - audio is handled separately
        if (Playback.Current?.IsRenderingToFile == true)
        {
            _operatorsUpdatedThisFrame.Clear();
            return;
        }
        
        // Prevent double-execution per frame
        var currentFrame = Playback.FrameCount;
        if (_lastStaleCheckFrame == currentFrame)
            return;
        _lastStaleCheckFrame = currentFrame;
        
        // Check stereo operators
        foreach (var (operatorId, state) in _operatorAudioStates)
        {
            if (state.Stream == null) continue;

            bool isStale = !_operatorsUpdatedThisFrame.Contains(operatorId);
            if (state.IsStale != isStale)
            {
                state.Stream.SetStaleMuted(isStale);
                state.IsStale = isStale;
            }
        }

        // Check spatial operators
        foreach (var (operatorId, state) in _spatialOperatorAudioStates)
        {
            if (state.Stream == null) continue;

            bool isStale = !_operatorsUpdatedThisFrame.Contains(operatorId);
            if (state.IsStale != isStale)
            {
                state.Stream.SetStaleMuted(isStale);
                state.IsStale = isStale;
            }
        }

        _operatorsUpdatedThisFrame.Clear();
    }

    public static void OnAudioDeviceChanged()
    {
        // Dispose and clear all stereo operator streams
        foreach (var state in _operatorAudioStates.Values)
        {
            state.Stream?.Dispose();
        }
        _operatorAudioStates.Clear();

        // Dispose and clear all spatial operator streams
        foreach (var state in _spatialOperatorAudioStates.Values)
        {
            state.Stream?.Dispose();
        }
        _spatialOperatorAudioStates.Clear();

        // Reinitialize the mixer for the new device
        AudioMixerManager.Shutdown();
        AudioMixerManager.Initialize();

        // Optionally, log the device change
        AudioConfig.LogInfo("[AudioEngine] Audio device changed: all operator streams and mixer reinitialized.");
    }

    /// <summary>
    /// Restores operator audio streams to live playback state after export.
    /// Called by AudioRendering.EndRecording().
    /// </summary>
    internal static void RestoreOperatorAudioStreams()
    {
        // Ensure global mixer is playing
        if (AudioMixerManager.GlobalMixerHandle != 0)
        {
            if (Bass.ChannelIsActive(AudioMixerManager.GlobalMixerHandle) != PlaybackState.Playing)
                Bass.ChannelPlay(AudioMixerManager.GlobalMixerHandle, false);
            Bass.ChannelUpdate(AudioMixerManager.GlobalMixerHandle, 0);
        }
        
        // Restore all stereo streams
        foreach (var state in _operatorAudioStates.Values)
        {
            if (state.Stream != null)
            {
                state.Stream.ClearExportMetering();
                state.Stream.RestartAfterExport();
                state.Stream.SetStaleMuted(false);
                state.IsStale = false;
            }
        }
        
        // Restore all spatial streams
        foreach (var state in _spatialOperatorAudioStates.Values)
        {
            if (state.Stream != null)
            {
                state.Stream.ClearExportMetering();
                state.Stream.RestartAfterExport();
                state.Stream.SetStaleMuted(false);
                state.IsStale = false;
            }
        }
        
        // Clear stale tracking to prevent immediate re-muting
        _operatorsUpdatedThisFrame.Clear();
        
        // Final mixer update
        if (AudioMixerManager.GlobalMixerHandle != 0)
            Bass.ChannelUpdate(AudioMixerManager.GlobalMixerHandle, 0);
    }

    /// <summary>
    /// Get a stereo operator audio stream by operator ID.
    /// Used by export sources to access operator audio states.
    /// </summary>
    public static bool TryGetStereoOperatorStream(Guid operatorId, out StereoOperatorAudioStream? stream)
    {
        stream = null;
        if (_operatorAudioStates.TryGetValue(operatorId, out var state) && state.Stream != null)
        {
            stream = state.Stream;
            return true;
        }
        return false;
    }

    /// <summary>
    /// Get a spatial operator audio stream by operator ID.
    /// Used by export sources to access operator audio states.
    /// </summary>
    public static bool TryGetSpatialOperatorStream(Guid operatorId, out SpatialOperatorAudioStream? stream)
    {
        stream = null;
        if (_spatialOperatorAudioStates.TryGetValue(operatorId, out var state) && state.Stream != null)
        {
            stream = state.Stream;
            return true;
        }
        return false;
    }

    /// <summary>
    /// Sets the global volume for the audio engine and updates the global mixer.
    /// </summary>
    public static void SetGlobalVolume(float volume)
    {
        ProjectSettings.Config.GlobalPlaybackVolume = volume;
        AudioMixerManager.SetGlobalVolume(volume);
    }

    /// <summary>
    /// Call this to initialize the global mixer volume from settings (e.g., on startup or settings load).
    /// </summary>
    public static void InitializeGlobalVolumeFromSettings()
    {
        AudioMixerManager.SetGlobalVolume(ProjectSettings.Config.GlobalPlaybackVolume);
    }
}