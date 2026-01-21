#nullable enable
using System;
using System.Collections.Generic;
using System.Numerics;
using ManagedBass;
using ManagedBass.Mix;
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

    // --- Operator Audio ---
    private static readonly Dictionary<Guid, OperatorAudioState<StereoOperatorAudioStream>> _stereoOperatorStates = new();
    private static readonly Dictionary<Guid, OperatorAudioState<SpatialOperatorAudioStream>> _spatialOperatorStates = new();
    private static readonly HashSet<Guid> _operatorsUpdatedThisFrame = new();
    private static int _lastStaleCheckFrame = -1;

    // 3D Listener
    private static Vector3 _listenerPosition = Vector3.Zero;
    private static Vector3 _listenerForward = new(0, 0, 1);
    private static Vector3 _listenerUp = new(0, 1, 0);
    private static bool _3dInitialized;

    private static double _lastPlaybackSpeed = 1;
    private static bool _bassInitialized;

    /// <summary>
    /// Common state for operator audio streams.
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

    #region Soundtrack Management

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
        EnsureBassInitialized();

        ProcessSoundtrackClips(playback, frameDurationInSeconds);

        // Process FFT data after filling the buffer from soundtrack
        if (playback.Settings is { Enabled: true, AudioSource: PlaybackSettings.AudioSources.ProjectSoundTrack })
            AudioAnalysis.ProcessUpdate(playback.Settings.AudioGainFactor, playback.Settings.AudioDecayFactor);

        CheckAndMuteStaleOperators(playback.FxTimeInBars);

        _obsoleteSoundtrackHandles.Clear();
        _updatedSoundtrackClipTimes.Clear();
    }

    private static void EnsureBassInitialized()
    {
        if (_bassInitialized) return;

        AudioMixerManager.Initialize();
        if (AudioMixerManager.OperatorMixerHandle != 0)
        {
            _bassInitialized = true;
            InitializeGlobalVolumeFromSettings();
        }
        else
        {
            Bass.Free();
            Bass.Init();
            AudioMixerManager.Initialize();
            _bassInitialized = true;
            InitializeGlobalVolumeFromSettings();
        }
    }

    private static void ProcessSoundtrackClips(Playback playback, double frameDurationInSeconds)
    {
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
    }

    public static void SetSoundtrackMute(bool configSoundtrackMute) => IsSoundtrackMuted = configSoundtrackMute;
    public static bool IsSoundtrackMuted { get; private set; }
    public static bool IsGlobalMuted => ProjectSettings.Config.GlobalMute;

    internal static void UpdateFftBufferFromSoundtrack(Playback playback)
    {
        const int DataFlagNoRemove = 268435456;
        var dataFlags = (int)DataFlags.FFT2048;

        if (playback.IsRenderingToFile)
            dataFlags |= DataFlagNoRemove;

        if (playback.Settings is not { AudioSource: PlaybackSettings.AudioSources.ProjectSoundTrack })
            return;

        // Get FFT data from the SoundtrackMixer
        var mixerHandle = AudioMixerManager.SoundtrackMixerHandle;
        if (mixerHandle == 0)
            return;

        _ = BassMix.ChannelGetData(mixerHandle, AudioAnalysis.FftGainBuffer, dataFlags);

        if (!WaveFormProcessing.RequestedOnce)
            return;

        int lengthInBytes = AudioConfig.WaveformSampleCount << 2 << 1;
        if (playback.IsRenderingToFile)
            lengthInBytes |= DataFlagNoRemove;

        WaveFormProcessing.LastFetchResultCode = BassMix.ChannelGetData(mixerHandle,
            WaveFormProcessing.InterleavenSampleBuffer, lengthInBytes);
    }

    public static int GetClipChannelCount(AudioClipResourceHandle? handle)
    {
        if (handle == null || !SoundtrackClipStreams.TryGetValue(handle, out var clipStream))
            return 2;
        Bass.ChannelGetInfo(clipStream.StreamHandle, out var info);
        return info.Channels;
    }

    public static int GetClipSampleRate(AudioClipResourceHandle? clip)
    {
        if (clip == null || !SoundtrackClipStreams.TryGetValue(clip, out var stream))
            return 48000;
        Bass.ChannelGetInfo(stream.StreamHandle, out var info);
        return info.Frequency;
    }

    #endregion

    #region 3D Listener

    public static void Set3DListenerPosition(Vector3 position, Vector3 forward, Vector3 up)
    {
        _listenerPosition = position;
        _listenerForward = forward;
        _listenerUp = up;

        if (!_3dInitialized)
        {
            _3dInitialized = true;
            AudioConfig.LogAudioInfo($"[AudioEngine] 3D listener initialized | Pos: {position}");
        }
    }

    public static Vector3 Get3DListenerPosition() => _listenerPosition;
    public static Vector3 Get3DListenerForward() => _listenerForward;
    public static Vector3 Get3DListenerUp() => _listenerUp;

    #endregion

    #region Stereo Operator Playback

    public static void UpdateStereoOperatorPlayback(
        Guid operatorId, double localFxTime, string filePath, bool shouldPlay, bool shouldStop,
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

    public static void PauseOperator(Guid operatorId) => PauseOperatorInternal(_stereoOperatorStates, operatorId);
    public static void ResumeOperator(Guid operatorId) => ResumeOperatorInternal(_stereoOperatorStates, operatorId);
    public static bool IsOperatorStreamPlaying(Guid operatorId) => IsOperatorPlaying(_stereoOperatorStates, operatorId);
    public static bool IsOperatorPaused(Guid operatorId) => IsOperatorPausedInternal(_stereoOperatorStates, operatorId);
    public static float GetOperatorLevel(Guid operatorId) => GetOperatorLevelInternal(_stereoOperatorStates, operatorId);
    public static List<float> GetOperatorWaveform(Guid operatorId) => GetOperatorWaveformInternal(_stereoOperatorStates, operatorId);
    public static List<float> GetOperatorSpectrum(Guid operatorId) => GetOperatorSpectrumInternal(_stereoOperatorStates, operatorId);

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

    public static void UpdateSpatialOperatorPlayback(
        Guid operatorId, double localFxTime, string filePath, bool shouldPlay, bool shouldStop,
        float volume, bool mute, Vector3 position, float minDistance, float maxDistance,
        float speed = 1.0f, float seek = 0f, Vector3? orientation = null,
        float innerConeAngle = 360f, float outerConeAngle = 360f, float outerConeVolume = 1.0f, int mode3D = 0)
    {
        _operatorsUpdatedThisFrame.Add(operatorId);

        if (!EnsureMixerInitialized()) return;

        if (!_3dInitialized)
            Set3DListenerPosition(Vector3.Zero, new Vector3(0, 0, 1), new Vector3(0, 1, 0));

        var state = GetOrCreateState(_spatialOperatorStates, operatorId);
        var resolvedPath = ResolveFilePath(filePath);

        if (!HandleFileChange(state, resolvedPath, operatorId,
            path => SpatialOperatorAudioStream.TryLoadStream(path, AudioMixerManager.OperatorMixerHandle, out var s) ? s : null))
            return;

        if (state.Stream == null) return;

        // Always update 3D position
        state.Stream.Update3DPosition(position, minDistance, maxDistance);

        if (HandlePlaybackTriggers(state, shouldPlay, shouldStop, operatorId))
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

            HandleSeek(state, seek, operatorId);
        }
    }

    public static void PauseSpatialOperator(Guid operatorId) => PauseOperatorInternal(_spatialOperatorStates, operatorId);
    public static void ResumeSpatialOperator(Guid operatorId) => ResumeOperatorInternal(_spatialOperatorStates, operatorId);
    public static bool IsSpatialOperatorStreamPlaying(Guid operatorId) => IsOperatorPlaying(_spatialOperatorStates, operatorId);
    public static bool IsSpatialOperatorPaused(Guid operatorId) => IsOperatorPausedInternal(_spatialOperatorStates, operatorId);
    public static float GetSpatialOperatorLevel(Guid operatorId) => GetOperatorLevelInternal(_spatialOperatorStates, operatorId);
    public static List<float> GetSpatialOperatorWaveform(Guid operatorId) => GetOperatorWaveformInternal(_spatialOperatorStates, operatorId);
    public static List<float> GetSpatialOperatorSpectrum(Guid operatorId) => GetOperatorSpectrumInternal(_spatialOperatorStates, operatorId);

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
        if (AudioMixerManager.OperatorMixerHandle != 0) return true;

        AudioConfig.LogAudioDebug("[AudioEngine] Mixer not initialized, initializing...");
        AudioMixerManager.Initialize();

        if (AudioMixerManager.OperatorMixerHandle == 0)
        {
            Log.Warning("[AudioEngine] AudioMixerManager failed to initialize");
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

    private static string? ResolveFilePath(string filePath)
    {
        if (string.IsNullOrEmpty(filePath)) return filePath;
        if (System.IO.File.Exists(filePath)) return filePath;

        if (ResourceManager.TryResolveRelativePath(filePath, null, out var absolutePath, out _))
        {
            AudioConfig.LogAudioDebug($"[AudioEngine] Resolved: {filePath} → {absolutePath}");
            return absolutePath;
        } else
        {
            Log.Error($"[AudioEngine] Could not resolve file path: {filePath}");
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
        if (state.Stream != null && Playback.Current.IsRenderingToFile)
        {
            state.IsStale = true;
            state.Stream.SetStaleMuted(true);
            AudioConfig.LogAudioDebug($"[AudioEngine] New stream during export - marking stale: {resolvedPath}");
            return false;
        }

        return true;
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
            AudioConfig.LogAudioDebug($"[AudioEngine] ■ Stop TRIGGER for {operatorId}");
            state.Stream!.Stop();
            state.IsPaused = false;
            state.PreviousSeek = 0f;
            return true;
        }

        if (playTrigger)
        {
            AudioConfig.LogAudioDebug($"[AudioEngine] ▶ Play TRIGGER for {operatorId}");
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

    private static List<float> GetOperatorWaveformInternal<T>(Dictionary<Guid, OperatorAudioState<T>> states, Guid operatorId)
        where T : OperatorAudioStreamBase
    {
        return states.TryGetValue(operatorId, out var state) && state.Stream != null
            ? state.Stream.GetWaveform()
            : new List<float>();
    }

    private static List<float> GetOperatorSpectrumInternal<T>(Dictionary<Guid, OperatorAudioState<T>> states, Guid operatorId)
        where T : OperatorAudioStreamBase
    {
        return states.TryGetValue(operatorId, out var state) && state.Stream != null
            ? state.Stream.GetSpectrum()
            : new List<float>();
    }

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

    #endregion

    #region Stale Detection & Export

    private static void CheckAndMuteStaleOperators(double currentTime)
    {
        if (Playback.Current.IsRenderingToFile) return;

        var currentFrame = Playback.FrameCount;
        if (_lastStaleCheckFrame == currentFrame) return;
        _lastStaleCheckFrame = currentFrame;

        UpdateStaleStates(_stereoOperatorStates);
        UpdateStaleStates(_spatialOperatorStates);

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

    internal static void ResetAllOperatorStreamsForExport()
    {
        ResetOperatorStreamsForExport(_stereoOperatorStates);
        ResetOperatorStreamsForExport(_spatialOperatorStates);
        _operatorsUpdatedThisFrame.Clear();
        AudioConfig.LogAudioDebug("[AudioEngine] Reset all operator streams for export");
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

    internal static void UpdateStaleStatesForExport()
    {
        UpdateStaleStates(_stereoOperatorStates);
        UpdateStaleStates(_spatialOperatorStates);
        _operatorsUpdatedThisFrame.Clear();
    }

    internal static void RestoreOperatorAudioStreams()
    {
        if (AudioMixerManager.GlobalMixerHandle != 0)
        {
            if (Bass.ChannelIsActive(AudioMixerManager.GlobalMixerHandle) != PlaybackState.Playing)
                Bass.ChannelPlay(AudioMixerManager.GlobalMixerHandle, false);
            Bass.ChannelUpdate(AudioMixerManager.GlobalMixerHandle, 0);
        }

        RestoreOperatorStreams(_stereoOperatorStates);
        RestoreOperatorStreams(_spatialOperatorStates);
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

    #endregion

    #region Device & Volume Management

    public static void OnAudioDeviceChanged()
    {
        DisposeAllOperatorStreams(_stereoOperatorStates);
        DisposeAllOperatorStreams(_spatialOperatorStates);

        AudioMixerManager.Shutdown();
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

    public static void SetGlobalVolume(float volume)
    {
        ProjectSettings.Config.GlobalPlaybackVolume = volume;
        AudioMixerManager.SetGlobalVolume(volume);
    }

    public static void InitializeGlobalVolumeFromSettings()
    {
        AudioMixerManager.SetGlobalVolume(ProjectSettings.Config.GlobalPlaybackVolume);
    }

    public static void SetGlobalMute(bool mute)
    {
        AudioMixerManager.SetGlobalMute(mute);
        ProjectSettings.Config.GlobalMute = mute;
    }

    public static void SetOperatorMute(bool mute)
    {
        AudioMixerManager.SetOperatorMute(mute);
        ProjectSettings.Config.OperatorMute = mute;
    }

    #endregion

    #region Export Metering Accessors

    public static IEnumerable<KeyValuePair<Guid, (StereoOperatorAudioStream? Stream, bool IsStale)>> GetAllStereoOperatorStates()
    {
        foreach (var kvp in _stereoOperatorStates)
            yield return new KeyValuePair<Guid, (StereoOperatorAudioStream?, bool)>(kvp.Key, (kvp.Value.Stream, kvp.Value.IsStale));
    }

    public static IEnumerable<KeyValuePair<Guid, (SpatialOperatorAudioStream? Stream, bool IsStale)>> GetAllSpatialOperatorStates()
    {
        foreach (var kvp in _spatialOperatorStates)
            yield return new KeyValuePair<Guid, (SpatialOperatorAudioStream?, bool)>(kvp.Key, (kvp.Value.Stream, kvp.Value.IsStale));
    }

    #endregion
}