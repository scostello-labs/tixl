#nullable enable
using System;
using System.Collections.Generic;
using ManagedBass;
using T3.Core.Animation;
using T3.Core.IO;
using T3.Core.Logging;
using T3.Core.Operator;
using T3.Core.Resource.Assets;

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

    // --- Operator Audio ---
    private static readonly Dictionary<Guid, OperatorAudioState<StereoOperatorAudioStream>> _stereoOperatorStates = new();
    private static readonly Dictionary<Guid, SpatialOperatorState> _spatialOperatorStates = new();
    private static readonly HashSet<Guid> _operatorsUpdatedThisFrame = new();
    private static int _lastStaleCheckFrame = -1;
    
    // Export state
    private static bool _isExporting;

    // 3D Listener
    private static Vector3 _listenerPosition = Vector3.Zero;
    private static Vector3 _listenerVelocity = Vector3.Zero;
    private static Vector3 _listenerForward = new(0, 0, 1);
    private static Vector3 _listenerUp = new(0, 1, 0);
    private static bool _3dInitialized;
    private static bool _3dListenerDirty;
    private static bool _3dApplyNeeded;

    private static double _lastPlaybackSpeed = 1;
    private static bool _bassInitialized;
    private static bool _bassInitFailed;

    /// <summary>
    /// Common state for operator audio streams using mixer (stereo).
    /// </summary>
    private sealed class OperatorAudioState<T> where T : OperatorAudioStreamBase
    {
        public T? Stream;
        public string CurrentFilePath = string.Empty;
        public bool IsPaused;
        public float PreviousSeek;
        public bool PreviousPlay;
        public bool PreviousStop;
        public bool IsStale;
    }

    /// <summary>
    /// State for spatial operator audio streams (native 3D, not using mixer).
    /// </summary>
    private sealed class SpatialOperatorState
    {
        public SpatialOperatorAudioStream? Stream;
        public string CurrentFilePath = string.Empty;
        public bool IsPaused;
        public float PreviousSeek;
        public bool PreviousPlay;
        public bool PreviousStop;
        public bool IsStale;
    }

    #region Soundtrack Management

    /// <summary>
    /// Registers a soundtrack clip to be used at the specified time during the current frame.
    /// </summary>
    /// <param name="handle">The audio clip resource handle.</param>
    /// <param name="time">The playback time in seconds.</param>
    public static void UseSoundtrackClip(AudioClipResourceHandle handle, double time)
    {
        _updatedSoundtrackClipTimes[handle] = time;
    }

    /// <summary>
    /// Reloads a soundtrack clip by freeing the existing stream and re-registering it.
    /// </summary>
    /// <param name="handle">The audio clip resource handle to reload.</param>
    public static void ReloadSoundtrackClip(AudioClipResourceHandle handle)
    {
        if (SoundtrackClipStreams.TryGetValue(handle, out var stream))
        {
            Bass.StreamFree(stream.StreamHandle);
            SoundtrackClipStreams.Remove(handle);
        }
        UseSoundtrackClip(handle, 0);
    }

    /// <summary>
    /// Completes the audio processing for the current frame, handling soundtrack clips,
    /// FFT analysis, and stale operator detection.
    /// </summary>
    /// <param name="playback">The current playback state.</param>
    /// <param name="frameDurationInSeconds">The duration of the current frame in seconds.</param>
    public static void CompleteFrame(Playback playback, double frameDurationInSeconds)
    {
        EnsureBassInitialized();

        ProcessSoundtrackClips(playback, frameDurationInSeconds);

        // Process FFT data after filling the buffer from soundtrack
        if (playback.Settings is { Enabled: true, AudioSource: PlaybackSettings.AudioSources.ProjectSoundTrack })
            AudioAnalysis.ProcessUpdate(playback.Settings.AudioGainFactor, playback.Settings.AudioDecayFactor);

        CheckAndMuteStaleOperators();
        
        // Apply all 3D audio changes once per frame (batched for performance)
        Apply3DChanges();

        _obsoleteSoundtrackHandles.Clear();
        _updatedSoundtrackClipTimes.Clear();
    }

    private static void EnsureBassInitialized()
    {
        if (_bassInitialized || _bassInitFailed) return;

        AudioMixerManager.Initialize();
        if (AudioMixerManager.OperatorMixerHandle == 0)
        {
            Log.Error("[AudioEngine] Failed to initialize AudioMixerManager; audio disabled.");
            _bassInitFailed = true;
            return;
        }

        _bassInitialized = true;
        InitializeGlobalVolumeFromSettings();
    }

    private static void ProcessSoundtrackClips(Playback playback, double frameDurationInSeconds)
    {
        // In external audio mode during export, skip soundtrack processing entirely
        // Only operator audio is exported in external mode
        bool isExternalAudioMode = playback.Settings?.AudioSource == PlaybackSettings.AudioSources.ExternalDevice;
        if (playback.IsRenderingToFile && isExternalAudioMode)
        {
            // Still need to mark clips as obsolete if they should be discarded
            foreach (var (handle, clipStream) in SoundtrackClipStreams)
            {
                clipStream.IsInUse = _updatedSoundtrackClipTimes.ContainsKey(clipStream.ResourceHandle);
                if (!clipStream.IsInUse && clipStream.ResourceHandle.Clip.DiscardAfterUse)
                {
                    _obsoleteSoundtrackHandles.Add(handle);
                }
            }
            
            foreach (var handle in _obsoleteSoundtrackHandles)
            {
                SoundtrackClipStreams[handle].DisableSoundtrackStream();
                SoundtrackClipStreams.Remove(handle);
            }
            return;
        }

        foreach (var (handle, time) in _updatedSoundtrackClipTimes)
        {
            if (SoundtrackClipStreams.TryGetValue(handle, out var clip))
            {
                clip.TargetTime = time;
            }
            else if (!string.IsNullOrEmpty(handle.Clip.FilePath) && 
                     SoundtrackClipStream.TryLoadSoundtrackClip(handle, out var soundtrackClipStream))
            {
                SoundtrackClipStreams[handle] = soundtrackClipStream;
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
                continue;
            }

            if (!playback.IsRenderingToFile && playbackSpeedChanged)
                clipStream.UpdateSoundtrackPlaybackSpeed(playback.PlaybackSpeed);

            if (handledMainSoundtrack || !clipStream.ResourceHandle.Clip.IsSoundtrack)
                continue;

            handledMainSoundtrack = true;

            if (playback.IsRenderingToFile)
                AudioRendering.ExportAudioFrame(playback, frameDurationInSeconds, clipStream);
            else
            {
                UpdateFftBufferFromSoundtrack(playback);
                clipStream.UpdateSoundtrackTime(playback);
            }
        }

        foreach (var handle in _obsoleteSoundtrackHandles)
        {
            SoundtrackClipStreams[handle].DisableSoundtrackStream();
            SoundtrackClipStreams.Remove(handle);
        }
        
        // Always update FFT buffer from mixer when in soundtrack mode, even if no soundtrack is loaded.
        // This ensures audio metering operators (AudioWaveform, PlaybackFFT, AudioReaction, etc.)
        // continue to work with operator-generated audio when no soundtrack is loaded.
        if (!handledMainSoundtrack && !playback.IsRenderingToFile)
        {
            UpdateFftBufferFromSoundtrack(playback);
        }
    }

    /// <summary>
    /// Sets the mute state for the soundtrack audio.
    /// </summary>
    /// <param name="configSoundtrackMute">True to mute the soundtrack, false to unmute.</param>
    public static void SetSoundtrackMute(bool configSoundtrackMute) => IsSoundtrackMuted = configSoundtrackMute;
    
    /// <summary>
    /// Gets a value indicating whether the soundtrack is currently muted.
    /// </summary>
    public static bool IsSoundtrackMuted { get; private set; }
    
    /// <summary>
    /// Gets a value indicating whether global audio is currently muted.
    /// </summary>
    public static bool IsGlobalMuted => ProjectSettings.Config.GlobalMute;

    internal static void UpdateFftBufferFromSoundtrack(Playback playback)
    {
        if (playback.Settings is not { AudioSource: PlaybackSettings.AudioSources.ProjectSoundTrack })
            return;

        // During export, the GlobalMixer is paused and empty, so FFT/waveform data
        // is populated via WaveFormProcessing.PopulateFromExportBuffer() in AudioRendering.GetFullMixDownBuffer()
        if (playback.IsRenderingToFile)
            return;

        // Get FFT data from the GlobalMixer to include both soundtrack AND operator audio
        // This ensures all audio metering (AudioWaveform, AudioFrequencies, AudioReaction, etc.)
        // monitors the complete audio output including operator-generated sounds
        var mixerHandle = AudioMixerManager.GlobalMixerHandle;
        if (mixerHandle == 0)
            return;

        const int dataFlags = (int)DataFlags.FFT2048;
        _ = Bass.ChannelGetData(mixerHandle, AudioAnalysis.FftGainBuffer, dataFlags);

        if (!WaveFormProcessing.RequestedOnce)
            return;

        int lengthInBytes = AudioConfig.WaveformSampleCount << 2 << 1;
        WaveFormProcessing.LastFetchResultCode = Bass.ChannelGetData(mixerHandle,
            WaveFormProcessing.InterleavenSampleBuffer, lengthInBytes);
    }

    /// <summary>
    /// Gets the number of audio channels for a soundtrack clip.
    /// </summary>
    /// <param name="handle">The audio clip resource handle.</param>
    /// <returns>The number of channels, or 2 (stereo) if the clip is not found.</returns>
    public static int GetClipChannelCount(AudioClipResourceHandle? handle)
    {
        if (handle == null || !SoundtrackClipStreams.TryGetValue(handle, out var clipStream))
            return 2;
        Bass.ChannelGetInfo(clipStream.StreamHandle, out var info);
        return info.Channels;
    }

    /// <summary>
    /// Gets the sample rate for a soundtrack clip.
    /// </summary>
    /// <param name="clip">The audio clip resource handle.</param>
    /// <returns>The sample rate in Hz, or 48000 if the clip is not found.</returns>
    public static int GetClipSampleRate(AudioClipResourceHandle? clip)
    {
        if (clip == null || !SoundtrackClipStreams.TryGetValue(clip, out var stream))
            return 48000;
        Bass.ChannelGetInfo(stream.StreamHandle, out var info);
        return info.Frequency;
    }

    #endregion

    #region 3D Listener

    /// <summary>
    /// Converts a System.Numerics.Vector3 to a ManagedBass.Vector3D.
    /// </summary>
    private static Vector3D ToBassVector(Vector3 v) => new(v.X, v.Y, v.Z);

    /// <summary>
    /// Sets the 3D listener position and orientation for spatial audio.
    /// </summary>
    /// <param name="position">The world position of the listener.</param>
    /// <param name="forward">The forward direction vector of the listener.</param>
    /// <param name="up">The up direction vector of the listener.</param>
    public static void Set3DListenerPosition(Vector3 position, Vector3 forward, Vector3 up)
    {
        // Compute velocity from position delta (assumes ~60fps, will be refined by actual frame time)
        var deltaPos = position - _listenerPosition;
        _listenerVelocity = deltaPos * 60.0f;
        
        _listenerPosition = position;
        _listenerForward = forward;
        _listenerUp = up;
        _3dListenerDirty = true;

        if (!_3dInitialized)
        {
            Initialize3DAudio();
            _3dInitialized = true;
            AudioConfig.LogAudioInfo($"[AudioEngine] 3D audio initialized | Pos: {position}");
        }
    }

    /// <summary>
    /// Initializes BASS 3D audio with configured factors.
    /// </summary>
    private static void Initialize3DAudio()
    {
        // Set 3D factors for proper world scaling
        Bass.Set3DFactors(
            AudioConfig.DistanceFactor,
            AudioConfig.RolloffFactor,
            AudioConfig.DopplerFactor);
        
        // Apply initial listener position
        ApplyListenerPosition();
    }

    /// <summary>
    /// Applies the current listener position to BASS.
    /// </summary>
    private static void ApplyListenerPosition()
    {
        var pos = ToBassVector(_listenerPosition);
        var vel = ToBassVector(_listenerVelocity);
        var front = ToBassVector(_listenerForward);
        var top = ToBassVector(_listenerUp);
        
        Bass.Set3DPosition(pos, vel, front, top);
        _3dListenerDirty = false;
    }

    /// <summary>
    /// Marks that a 3D apply is needed at the end of the frame.
    /// Called by spatial streams when their positions are updated.
    /// </summary>
    internal static void Mark3DApplyNeeded() => _3dApplyNeeded = true;

    /// <summary>
    /// Applies all pending 3D changes (called once per frame in CompleteFrame).
    /// </summary>
    private static void Apply3DChanges()
    {
        if (_3dListenerDirty)
        {
            ApplyListenerPosition();
        }
        
        if (_3dApplyNeeded || _3dListenerDirty)
        {
            Bass.Apply3D();
            _3dApplyNeeded = false;
        }
    }

    /// <summary>
    /// Gets the current 3D listener position.
    /// </summary>
    /// <returns>The world position of the listener.</returns>
    public static Vector3 Get3DListenerPosition() => _listenerPosition;
    
    /// <summary>
    /// Gets the current 3D listener forward direction.
    /// </summary>
    /// <returns>The forward direction vector of the listener.</returns>
    public static Vector3 Get3DListenerForward() => _listenerForward;
    
    /// <summary>
    /// Gets the current 3D listener up direction.
    /// </summary>
    /// <returns>The up direction vector of the listener.</returns>
    public static Vector3 Get3DListenerUp() => _listenerUp;

    #endregion

    #region Stereo Operator Playback

    /// <summary>
    /// Updates the playback state of a stereo audio stream for an operator.
    /// </summary>
    /// <param name="operatorId">The unique identifier of the operator.</param>
    /// <param name="filePath">The file path of the audio file to play.</param>
    /// <param name="shouldPlay">True to trigger playback on rising edge.</param>
    /// <param name="shouldStop">True to trigger stop on rising edge.</param>
    /// <param name="volume">The volume level (0.0 to 1.0).</param>
    /// <param name="mute">True to mute the stream.</param>
    /// <param name="panning">The stereo panning value (-1.0 left to 1.0 right).</param>
    /// <param name="speed">The playback speed multiplier (default 1.0).</param>
    /// <param name="seek">The normalized seek position (0.0 to 1.0).</param>
    public static void UpdateStereoOperatorPlayback(
        Guid operatorId, string filePath, bool shouldPlay, bool shouldStop,
        float volume, bool mute, float panning, float speed = 1.0f, float seek = 0f)
    {
        _operatorsUpdatedThisFrame.Add(operatorId);

        if (!EnsureMixerInitialized()) return;

        var state = GetOrCreateState(_stereoOperatorStates, operatorId);
        var resolvedPath = ResolveFilePath(filePath);

        if (!HandleFileChange(state, resolvedPath, operatorId,
            path => StereoOperatorAudioStream.TryLoadStream(path, AudioMixerManager.OperatorMixerHandle, out var s) ? s : null))
            return;

        if (state.Stream == null) return;

        if (HandlePlaybackTriggers(state, shouldPlay, shouldStop, operatorId))
            return;

        if (state.Stream.IsPlaying)
        {
            state.Stream.SetVolume(volume, mute);
            state.Stream.SetPanning(panning);
            state.Stream.SetSpeed(speed);
            HandleSeek(state, seek, operatorId);
        }
    }

    /// <summary>Pauses the audio stream for the specified stereo operator.</summary>
    /// <param name="operatorId">The unique identifier of the operator.</param>
    public static void PauseOperator(Guid operatorId) => PauseOperatorInternal(_stereoOperatorStates, operatorId);
    
    /// <summary>Resumes the audio stream for the specified stereo operator.</summary>
    /// <param name="operatorId">The unique identifier of the operator.</param>
    public static void ResumeOperator(Guid operatorId) => ResumeOperatorInternal(_stereoOperatorStates, operatorId);
    
    /// <summary>Checks if the audio stream is currently playing for the specified stereo operator.</summary>
    /// <param name="operatorId">The unique identifier of the operator.</param>
    /// <returns>True if the stream is playing and not paused; otherwise, false.</returns>
    public static bool IsOperatorStreamPlaying(Guid operatorId) => IsOperatorPlaying(_stereoOperatorStates, operatorId);
    
    /// <summary>Checks if the audio stream is currently paused for the specified stereo operator.</summary>
    /// <param name="operatorId">The unique identifier of the operator.</param>
    /// <returns>True if the stream is paused; otherwise, false.</returns>
    public static bool IsOperatorPaused(Guid operatorId) => IsOperatorPausedInternal(_stereoOperatorStates, operatorId);
    
    /// <summary>Gets the current audio level for the specified stereo operator.</summary>
    /// <param name="operatorId">The unique identifier of the operator.</param>
    /// <returns>The current audio level, or 0 if the stream is not found.</returns>
    public static float GetOperatorLevel(Guid operatorId) => GetOperatorLevelInternal(_stereoOperatorStates, operatorId);

    /// <summary>
    /// Attempts to retrieve the stereo audio stream for the specified operator.
    /// </summary>
    /// <param name="operatorId">The unique identifier of the operator.</param>
    /// <param name="stream">When this method returns, contains the stream if found; otherwise, null.</param>
    /// <returns>True if the stream was found; otherwise, false.</returns>
    public static bool TryGetStereoOperatorStream(Guid operatorId, out StereoOperatorAudioStream? stream)
    {
        stream = null;
        if (_stereoOperatorStates.TryGetValue(operatorId, out var state) && state.Stream != null)
        {
            stream = state.Stream;
            return true;
        }
        return false;
    }

    #endregion

    #region Spatial Operator Playback

    /// <summary>
    /// Updates the playback state of a spatial (3D) audio stream for an operator.
    /// Spatial streams play directly to BASS output for hardware-accelerated 3D audio.
    /// </summary>
    /// <param name="operatorId">The unique identifier of the operator.</param>
    /// <param name="filePath">The file path of the audio file to play.</param>
    /// <param name="shouldPlay">True to trigger playback on rising edge.</param>
    /// <param name="shouldStop">True to trigger stop on rising edge.</param>
    /// <param name="volume">The volume level (0.0 to 1.0).</param>
    /// <param name="mute">True to mute the stream.</param>
    /// <param name="position">The 3D world position of the audio source.</param>
    /// <param name="minDistance">The distance at which the volume starts to attenuate.</param>
    /// <param name="maxDistance">The distance at which the volume reaches minimum.</param>
    /// <param name="speed">The playback speed multiplier (default 1.0).</param>
    /// <param name="seek">The normalized seek position (0.0 to 1.0).</param>
    /// <param name="orientation">The orientation vector of the sound source for directional audio.</param>
    /// <param name="innerConeAngle">The inner cone angle in degrees for directional audio.</param>
    /// <param name="outerConeAngle">The outer cone angle in degrees for directional audio.</param>
    /// <param name="outerConeVolume">The volume level outside the outer cone.</param>
    /// <param name="mode3D">The 3D processing mode.</param>
    public static void UpdateSpatialOperatorPlayback(
        Guid operatorId, string filePath, bool shouldPlay, bool shouldStop,
        float volume, bool mute, Vector3 position, float minDistance, float maxDistance,
        float speed = 1.0f, float seek = 0f, Vector3? orientation = null,
        float innerConeAngle = 360f, float outerConeAngle = 360f, float outerConeVolume = 1.0f, int mode3D = 0)
    {
        _operatorsUpdatedThisFrame.Add(operatorId);

        if (!EnsureMixerInitialized()) return;

        if (!_3dInitialized)
            Set3DListenerPosition(Vector3.Zero, new Vector3(0, 0, 1), new Vector3(0, 1, 0));

        var state = GetOrCreateSpatialState(operatorId);
        var resolvedPath = ResolveFilePath(filePath);

        if (!HandleSpatialFileChange(state, resolvedPath, operatorId))
            return;

        if (state.Stream == null) return;

        // Always update 3D position
        state.Stream.Update3DPosition(position, minDistance, maxDistance);

        if (HandleSpatialPlaybackTriggers(state, shouldPlay, shouldStop, operatorId))
            return;

        if (state.Stream.IsPlaying)
        {
            state.Stream.SetVolume(volume, mute);
            state.Stream.SetSpeed(speed);

            if (orientation.HasValue && orientation.Value.Length() > 0.001f)
                state.Stream.Set3DOrientation(orientation.Value);

            if (Math.Abs(innerConeAngle - 360f) > 0.1f || Math.Abs(outerConeAngle - 360f) > 0.1f || Math.Abs(outerConeVolume - 1.0f) > 0.001f)
                state.Stream.Set3DCone(innerConeAngle, outerConeAngle, outerConeVolume);

            if (mode3D != 0)
                state.Stream.Set3DMode((Mode3D)mode3D);

            HandleSpatialSeek(state, seek, operatorId);
        }
    }

    /// <summary>Pauses the audio stream for the specified spatial operator.</summary>
    /// <param name="operatorId">The unique identifier of the operator.</param>
    public static void PauseSpatialOperator(Guid operatorId)
    {
        if (_spatialOperatorStates.TryGetValue(operatorId, out var state) && state.Stream != null)
        {
            state.Stream.Pause();
            state.IsPaused = true;
        }
    }
    
    /// <summary>Resumes the audio stream for the specified spatial operator.</summary>
    /// <param name="operatorId">The unique identifier of the operator.</param>
    public static void ResumeSpatialOperator(Guid operatorId)
    {
        if (_spatialOperatorStates.TryGetValue(operatorId, out var state) && state.Stream != null)
        {
            state.Stream.Resume();
            state.IsPaused = false;
        }
    }
    
    /// <summary>Checks if the audio stream is currently playing for the specified spatial operator.</summary>
    /// <param name="operatorId">The unique identifier of the operator.</param>
    /// <returns>True if the stream is playing and not paused; otherwise, false.</returns>
    public static bool IsSpatialOperatorStreamPlaying(Guid operatorId)
    {
        return _spatialOperatorStates.TryGetValue(operatorId, out var state)
               && state.Stream != null
               && state.Stream.IsPlaying
               && !state.Stream.IsPaused;
    }
    
    /// <summary>Checks if the audio stream is currently paused for the specified spatial operator.</summary>
    /// <param name="operatorId">The unique identifier of the operator.</param>
    /// <returns>True if the stream is paused; otherwise, false.</returns>
    public static bool IsSpatialOperatorPaused(Guid operatorId)
    {
        return _spatialOperatorStates.TryGetValue(operatorId, out var state)
               && state.Stream != null
               && state.IsPaused;
    }
    
    /// <summary>Gets the current audio level for the specified spatial operator.</summary>
    /// <param name="operatorId">The unique identifier of the operator.</param>
    /// <returns>The current audio level, or 0 if the stream is not found.</returns>
    public static float GetSpatialOperatorLevel(Guid operatorId)
    {
        return _spatialOperatorStates.TryGetValue(operatorId, out var state) && state.Stream != null
            ? state.Stream.GetLevel()
            : 0f;
    }

    /// <summary>
    /// Attempts to retrieve the spatial audio stream for the specified operator.
    /// </summary>
    /// <param name="operatorId">The unique identifier of the operator.</param>
    /// <param name="stream">When this method returns, contains the stream if found; otherwise, null.</param>
    /// <returns>True if the stream was found; otherwise, false.</returns>
    public static bool TryGetSpatialOperatorStream(Guid operatorId, out SpatialOperatorAudioStream? stream)
    {
        stream = null;
        if (_spatialOperatorStates.TryGetValue(operatorId, out var state) && state.Stream != null)
        {
            stream = state.Stream;
            return true;
        }
        return false;
    }

    #endregion

    #region Common Operator Helpers

    private static bool EnsureMixerInitialized()
    {
        if (AudioMixerManager.IsInitialized) return true;

        AudioConfig.LogAudioDebug("[AudioEngine] Mixer not initialized, initializing...");
        AudioMixerManager.Initialize();

        if (AudioMixerManager.OperatorMixerHandle == 0)
        {
            // Don't log every time - Initialize() already logs the failure once
            return false;
        }
        return true;
    }

    private static OperatorAudioState<T> GetOrCreateState<T>(Dictionary<Guid, OperatorAudioState<T>> states, Guid operatorId)
        where T : OperatorAudioStreamBase
    {
        if (!states.TryGetValue(operatorId, out var state))
        {
            state = new OperatorAudioState<T>();
            states[operatorId] = state;
            AudioConfig.LogAudioDebug($"[AudioEngine] Created audio state for operator: {operatorId}");
        }
        return state;
    }

    private static string ResolveFilePath(string filePath)
    {
        if (string.IsNullOrEmpty(filePath) || System.IO.File.Exists(filePath)) 
            return filePath;

        if (!AssetRegistry.TryResolveAddress(filePath, null, out var absolutePath, out _))
        {
            Log.Error($"[AudioEngine] Could not resolve file path: {filePath}");
        }
        else
        {
            AudioConfig.LogAudioDebug($"[AudioEngine] Resolved: {filePath} → {absolutePath}");
            return absolutePath;
        }

        return filePath;
    }

    private static bool HandleFileChange<T>(OperatorAudioState<T> state, string? resolvedPath, Guid operatorId,
        Func<string, T?> loadFunc) where T : OperatorAudioStreamBase
    {
        if (state.CurrentFilePath == resolvedPath) return true;

        AudioConfig.LogAudioDebug($"[AudioEngine] File changed for {operatorId}: '{state.CurrentFilePath}' → '{resolvedPath}'");

        state.Stream?.Dispose();
        state.Stream = null;
        state.CurrentFilePath = resolvedPath ?? string.Empty;
        state.PreviousPlay = false;
        state.PreviousStop = false;

        if (!string.IsNullOrEmpty(resolvedPath))
        {
            state.Stream = loadFunc(resolvedPath);
            if (state.Stream == null)
                Log.Error($"[AudioEngine] Failed to load stream for {operatorId}: {resolvedPath}");
        }

        // During export, mark new streams as stale
        if (state.Stream == null || !Playback.Current.IsRenderingToFile) 
            return true;
        
        state.IsStale = true;
        state.Stream.SetStaleMuted(true);
        AudioConfig.LogAudioDebug($"[AudioEngine] New stream during export - marking stale: {resolvedPath}");
        return false;

    }

    private static bool HandlePlaybackTriggers<T>(OperatorAudioState<T> state, bool shouldPlay, bool shouldStop, Guid operatorId)
        where T : OperatorAudioStreamBase
    {
        var playTrigger = shouldPlay && !state.PreviousPlay;
        var stopTrigger = shouldStop && !state.PreviousStop;
        
        state.PreviousPlay = shouldPlay;
        state.PreviousStop = shouldStop;

        if (stopTrigger)
        {
            state.Stream!.Stop();
            state.IsPaused = false;
            state.PreviousSeek = 0f;
            return true;
        }

        if (playTrigger)
        {
            state.Stream!.Stop();
            state.Stream.Play();
            state.IsPaused = false;
            state.IsStale = false;
        }

        return false;
    }

    private static void HandleSeek<T>(OperatorAudioState<T> state, float seek, Guid operatorId)
        where T : OperatorAudioStreamBase
    {
        if (Math.Abs(seek - state.PreviousSeek) > 0.001f && seek >= 0f && seek <= 1f)
        {
            var seekTime = (float)(seek * state.Stream!.Duration);
            state.Stream.Seek(seekTime);
            state.PreviousSeek = seek;
            AudioConfig.LogAudioDebug($"[AudioEngine] Seek to {seek:F3} ({seekTime:F3}s) for {operatorId}");
        }
    }

    private static void PauseOperatorInternal<T>(Dictionary<Guid, OperatorAudioState<T>> states, Guid operatorId)
        where T : OperatorAudioStreamBase
    {
        if (states.TryGetValue(operatorId, out var state) && state.Stream != null)
        {
            state.Stream.Pause();
            state.IsPaused = true;
        }
    }

    private static void ResumeOperatorInternal<T>(Dictionary<Guid, OperatorAudioState<T>> states, Guid operatorId)
        where T : OperatorAudioStreamBase
    {
        if (states.TryGetValue(operatorId, out var state) && state.Stream != null)
        {
            state.Stream.Resume();
            state.IsPaused = false;
        }
    }

    private static bool IsOperatorPlaying<T>(Dictionary<Guid, OperatorAudioState<T>> states, Guid operatorId)
        where T : OperatorAudioStreamBase
    {
        return states.TryGetValue(operatorId, out var state)
               && state.Stream != null
               && state.Stream.IsPlaying
               && !state.Stream.IsPaused;
    }

    private static bool IsOperatorPausedInternal<T>(Dictionary<Guid, OperatorAudioState<T>> states, Guid operatorId)
        where T : OperatorAudioStreamBase
    {
        return states.TryGetValue(operatorId, out var state)
               && state.Stream != null
               && state.IsPaused;
    }

    private static float GetOperatorLevelInternal<T>(Dictionary<Guid, OperatorAudioState<T>> states, Guid operatorId)
        where T : OperatorAudioStreamBase
    {
        return states.TryGetValue(operatorId, out var state) && state.Stream != null
            ? state.Stream.GetLevel()
            : 0f;
    }

    #endregion

    #region Spatial Operator Helpers (Non-Generic)

    private static SpatialOperatorState GetOrCreateSpatialState(Guid operatorId)
    {
        if (!_spatialOperatorStates.TryGetValue(operatorId, out var state))
        {
            state = new SpatialOperatorState();
            _spatialOperatorStates[operatorId] = state;
            AudioConfig.LogAudioDebug($"[AudioEngine] Created spatial audio state for operator: {operatorId}");
        }
        return state;
    }

    private static bool HandleSpatialFileChange(SpatialOperatorState state, string? resolvedPath, Guid operatorId)
    {
        if (state.CurrentFilePath == resolvedPath) return true;

        AudioConfig.LogAudioDebug($"[AudioEngine] File changed for spatial {operatorId}: '{state.CurrentFilePath}' → '{resolvedPath}'");

        state.Stream?.Dispose();
        state.Stream = null;
        state.CurrentFilePath = resolvedPath ?? string.Empty;
        state.PreviousPlay = false;
        state.PreviousStop = false;

        if (!string.IsNullOrEmpty(resolvedPath))
        {
            // Note: mixerHandle parameter is ignored for spatial streams - they play directly to BASS
            if (!SpatialOperatorAudioStream.TryLoadStream(resolvedPath, 0, out var stream))
            {
                Log.Error($"[AudioEngine] Failed to load spatial stream for {operatorId}: {resolvedPath}");
            }
            else
            {
                state.Stream = stream;
            }
        }

        // During export, mark new streams as stale
        if (state.Stream == null || !Playback.Current.IsRenderingToFile) 
            return true;
        
        state.IsStale = true;
        state.Stream.SetStaleMuted(true);
        AudioConfig.LogAudioDebug($"[AudioEngine] New spatial stream during export - marking stale: {resolvedPath}");
        return false;
    }

    private static bool HandleSpatialPlaybackTriggers(SpatialOperatorState state, bool shouldPlay, bool shouldStop, Guid operatorId)
    {
        var playTrigger = shouldPlay && !state.PreviousPlay;
        var stopTrigger = shouldStop && !state.PreviousStop;
        
        state.PreviousPlay = shouldPlay;
        state.PreviousStop = shouldStop;

        if (stopTrigger)
        {
            state.Stream!.Stop();
            state.IsPaused = false;
            state.PreviousSeek = 0f;
            return true;
        }

        if (playTrigger)
        {
            state.Stream!.Stop();
            state.Stream.Play();
            state.IsPaused = false;
            state.IsStale = false;
        }

        return false;
    }

    private static void HandleSpatialSeek(SpatialOperatorState state, float seek, Guid operatorId)
    {
        if (Math.Abs(seek - state.PreviousSeek) > 0.001f && seek >= 0f && seek <= 1f)
        {
            var seekTime = (float)(seek * state.Stream!.Duration);
            state.Stream.Seek(seekTime);
            state.PreviousSeek = seek;
            AudioConfig.LogAudioDebug($"[AudioEngine] Seek to {seek:F3} ({seekTime:F3}s) for spatial {operatorId}");
        }
    }

    #endregion


    /// <summary>
    /// Unregisters an operator from audio playback and disposes its associated streams.
    /// </summary>
    /// <param name="operatorId">The unique identifier of the operator to unregister.</param>
    public static void UnregisterOperator(Guid operatorId)
    {
        if (_stereoOperatorStates.TryGetValue(operatorId, out var stereoState))
        {
            stereoState.Stream?.Dispose();
            _stereoOperatorStates.Remove(operatorId);
        }

        if (_spatialOperatorStates.TryGetValue(operatorId, out var spatialState))
        {
            spatialState.Stream?.Dispose();
            _spatialOperatorStates.Remove(operatorId);
        }
    }

    #region Stale Detection & Export

    private static void CheckAndMuteStaleOperators()
    {
        if (Playback.Current.IsRenderingToFile) return;

        var currentFrame = Playback.FrameCount;
        if (_lastStaleCheckFrame == currentFrame) return;
        _lastStaleCheckFrame = currentFrame;

        UpdateStaleStates(_stereoOperatorStates);
        UpdateSpatialStaleStates();

        _operatorsUpdatedThisFrame.Clear();
    }

    private static void UpdateStaleStates<T>(Dictionary<Guid, OperatorAudioState<T>> states)
        where T : OperatorAudioStreamBase
    {
        foreach (var (operatorId, state) in states)
        {
            if (state.Stream == null) continue;

            bool isStale = !_operatorsUpdatedThisFrame.Contains(operatorId);
            if (state.IsStale != isStale)
            {
                state.Stream.SetStaleMuted(isStale);
                state.IsStale = isStale;
            }
        }
    }

    private static void UpdateSpatialStaleStates()
    {
        foreach (var (operatorId, state) in _spatialOperatorStates)
        {
            if (state.Stream == null) continue;

            bool isStale = !_operatorsUpdatedThisFrame.Contains(operatorId);
            if (state.IsStale != isStale)
            {
                state.Stream.SetStaleMuted(isStale);
                state.IsStale = isStale;
            }
        }
    }

    /// <summary>
    /// Resets all operator audio streams in preparation for audio export.
    /// </summary>
    internal static void ResetAllOperatorStreamsForExport()
    {
        _isExporting = true;
        ResetOperatorStreamsForExport(_stereoOperatorStates);
        ResetSpatialOperatorStreamsForExport();
        _operatorsUpdatedThisFrame.Clear();
    }

    private static void ResetOperatorStreamsForExport<T>(Dictionary<Guid, OperatorAudioState<T>> states)
        where T : OperatorAudioStreamBase
    {
        foreach (var (_, state) in states)
        {
            state.Stream?.PrepareForExport();
            state.IsStale = true;
        }
    }

    private static void ResetSpatialOperatorStreamsForExport()
    {
        foreach (var (_, state) in _spatialOperatorStates)
        {
            state.Stream?.PrepareForExport();
            state.IsStale = true;
        }
    }

    /// <summary>
    /// Updates the stale states for all operator streams during export.
    /// </summary>
    internal static void UpdateStaleStatesForExport()
    {
        UpdateStaleStates(_stereoOperatorStates);
        UpdateSpatialStaleStates();
        _operatorsUpdatedThisFrame.Clear();
    }

    /// <summary>
    /// Restores all operator audio streams after export has completed.
    /// </summary>
    internal static void RestoreOperatorAudioStreams()
    {
        _isExporting = false;
        
        if (AudioMixerManager.GlobalMixerHandle != 0)
        {
            if (Bass.ChannelIsActive(AudioMixerManager.GlobalMixerHandle) != PlaybackState.Playing)
                Bass.ChannelPlay(AudioMixerManager.GlobalMixerHandle);
            Bass.ChannelUpdate(AudioMixerManager.GlobalMixerHandle, 0);
        }

        RestoreOperatorStreams(_stereoOperatorStates);
        RestoreSpatialOperatorStreams();
        _operatorsUpdatedThisFrame.Clear();

        if (AudioMixerManager.GlobalMixerHandle != 0)
            Bass.ChannelUpdate(AudioMixerManager.GlobalMixerHandle, 0);
    }

    private static void RestoreOperatorStreams<T>(Dictionary<Guid, OperatorAudioState<T>> states)
        where T : OperatorAudioStreamBase
    {
        foreach (var state in states.Values)
        {
            if (state.Stream != null)
            {
                state.Stream.ClearExportMetering();
                state.Stream.RestartAfterExport();
                state.Stream.SetStaleMuted(false);
                state.IsStale = false;
            }
        }
    }

    private static void RestoreSpatialOperatorStreams()
    {
        foreach (var state in _spatialOperatorStates.Values)
        {
            if (state.Stream != null)
            {
                state.Stream.ClearExportMetering();
                state.Stream.RestartAfterExport();
                state.Stream.SetStaleMuted(false);
                state.IsStale = false;
            }
        }
    }

    #endregion

    #region Device & Volume Management

    /// <summary>
    /// Handles audio device changes by reinitializing all audio streams.
    /// </summary>
    public static void OnAudioDeviceChanged()
    {
        DisposeAllOperatorStreams(_stereoOperatorStates);
        DisposeSpatialOperatorStreams();

        AudioMixerManager.Shutdown();
        _bassInitialized = false; // Reset flag to allow proper reinitialization
        _bassInitFailed = false;  // Reset failure flag to allow retry
        AudioMixerManager.Initialize();

        AudioConfig.LogAudioInfo("[AudioEngine] Audio device changed: reinitialized.");
    }

    private static void DisposeAllOperatorStreams<T>(Dictionary<Guid, OperatorAudioState<T>> states)
        where T : OperatorAudioStreamBase
    {
        foreach (var state in states.Values)
            state.Stream?.Dispose();
        states.Clear();
    }

    private static void DisposeSpatialOperatorStreams()
    {
        foreach (var state in _spatialOperatorStates.Values)
            state.Stream?.Dispose();
        _spatialOperatorStates.Clear();
    }

    /// <summary>
    /// Sets the global audio volume level.
    /// </summary>
    /// <param name="volume">The volume level (0.0 to 1.0).</param>
    public static void SetGlobalVolume(float volume)
    {
        ProjectSettings.Config.GlobalPlaybackVolume = volume;
        AudioMixerManager.SetGlobalVolume(volume);
    }

    /// <summary>
    /// Initializes the global volume from the stored project settings.
    /// </summary>
    public static void InitializeGlobalVolumeFromSettings()
    {
        AudioMixerManager.SetGlobalVolume(ProjectSettings.Config.GlobalPlaybackVolume);
    }

    /// <summary>
    /// Sets the global audio mute state.
    /// </summary>
    /// <param name="mute">True to mute all audio, false to unmute.</param>
    public static void SetGlobalMute(bool mute)
    {
        AudioMixerManager.SetGlobalMute(mute);
        ProjectSettings.Config.GlobalMute = mute;
    }

    /// <summary>
    /// Sets the mute state for all operator audio streams.
    /// </summary>
    /// <param name="mute">True to mute operator audio, false to unmute.</param>
    public static void SetOperatorMute(bool mute)
    {
        AudioMixerManager.SetOperatorMute(mute);
        ProjectSettings.Config.OperatorMute = mute;
    }

    #endregion

    #region Export Metering Accessors

    /// <summary>
    /// Gets all stereo operator states for export metering purposes.
    /// </summary>
    /// <returns>An enumerable of operator ID and stream state pairs.</returns>
    public static IEnumerable<KeyValuePair<Guid, (StereoOperatorAudioStream? Stream, bool IsStale)>> GetAllStereoOperatorStates()
    {
        foreach (var kvp in _stereoOperatorStates)
            yield return new KeyValuePair<Guid, (StereoOperatorAudioStream?, bool)>(kvp.Key, (kvp.Value.Stream, kvp.Value.IsStale));
    }

    /// <summary>
    /// Gets all spatial operator states for export metering purposes.
    /// </summary>
    /// <returns>An enumerable of operator ID and stream state pairs.</returns>
    public static IEnumerable<KeyValuePair<Guid, (SpatialOperatorAudioStream? Stream, bool IsStale)>> GetAllSpatialOperatorStates()
    {
        foreach (var kvp in _spatialOperatorStates)
            yield return new KeyValuePair<Guid, (SpatialOperatorAudioStream?, bool)>(kvp.Key, (kvp.Value.Stream, kvp.Value.IsStale));
    }

    #endregion
}

