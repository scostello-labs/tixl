#nullable enable
using System;
using System.Collections.Generic;
using ManagedBass;
using ManagedBass.Mix;
using T3.Core.Logging;

namespace T3.Core.Audio;

/// <summary>
/// Manages the audio mixer architecture with separate paths for operator clips and soundtrack clips.
/// 
/// Architecture: 
///   Live Playback:
///     Operator Clip(s) > Operator Mixer (decode) > Global Mixer > Soundcard
///     Soundtrack Clip(s) > Soundtrack Mixer (decode) > Global Mixer > Soundcard
///   
///   Export:
///     GlobalMixer is PAUSED during export, so we can read directly from:
///     OperatorMixer and SoundtrackMixer using Bass.ChannelGetData()
///   
///   Analysis (offline):
///     Analysis streams > Offline Mixer (no output, decode only)
/// </summary>
public static class AudioMixerManager
{
    private static int _globalMixerHandle;
    private static int _operatorMixerHandle;
    private static int _soundtrackMixerHandle;
    private static int _offlineMixerHandle;
    private static bool _initialized;
    private static int _flacPluginHandle;
    private static readonly object _offlineMixerLock = new();

    public static int GlobalMixerHandle => _globalMixerHandle;
    public static int OperatorMixerHandle => _operatorMixerHandle;
    public static int SoundtrackMixerHandle => _soundtrackMixerHandle;
    
    /// <summary>
    /// Offline mixer for analysis tasks (waveform image generation, FFT analysis, etc.)
    /// This mixer does NOT output to the soundcard and is completely isolated from playback.
    /// </summary>
    public static int OfflineMixerHandle => _offlineMixerHandle;
    
    public static void Initialize()
    {
        if (_initialized)
        {
            AudioConfig.LogAudioDebug("[AudioMixer] Already initialized, skipping.");
            return;
        }

        AudioConfig.LogAudioDebug("[AudioMixer] Starting initialization...");

        // Check if BASS is already initialized
        DeviceInfo deviceInfo;
        var bassIsInitialized = Bass.GetDeviceInfo(Bass.CurrentDevice, out deviceInfo) && deviceInfo.IsInitialized;
        
        if (bassIsInitialized)
        {
            Log.Warning("[AudioMixer] BASS was already initialized by something else - our low-latency config may not apply!");
            Log.Warning("[AudioMixer] To fix this, ensure AudioMixerManager.Initialize() is called BEFORE any Bass.Init() calls.");
            Bass.GetInfo(out var info);
            AudioConfig.LogAudioInfo($"[AudioMixer] Existing BASS - SampleRate: {info.SampleRate}Hz, MinBuffer: {info.MinBufferLength}ms, Latency: {info.Latency}ms");
        }
        else
        {
            AudioConfig.LogAudioDebug("[AudioMixer] BASS not initialized, configuring for low latency...");
            
            // Configure BASS for low latency BEFORE initialization
            Bass.Configure(Configuration.UpdatePeriod, AudioConfig.UpdatePeriodMs);
            Bass.Configure(Configuration.UpdateThreads, AudioConfig.UpdateThreads);
            Bass.Configure(Configuration.PlaybackBufferLength, AudioConfig.PlaybackBufferLengthMs);
            Bass.Configure(Configuration.DeviceBufferLength, AudioConfig.DeviceBufferLengthMs);
            
            AudioConfig.LogAudioDebug($"[AudioMixer] Config - UpdatePeriod: {AudioConfig.UpdatePeriodMs}ms, UpdateThreads: {AudioConfig.UpdateThreads}, PlaybackBuffer: {AudioConfig.PlaybackBufferLengthMs}ms, DeviceBuffer: {AudioConfig.DeviceBufferLengthMs}ms");
            
            // Try to initialize with latency flag first
            var initFlags = DeviceInitFlags.Latency | DeviceInitFlags.Stereo;
            
            AudioConfig.LogAudioDebug($"[AudioMixer] Attempting BASS.Init with Latency flag at {AudioConfig.MixerFrequency}Hz...");
            if (!Bass.Init(-1, AudioConfig.MixerFrequency, initFlags, IntPtr.Zero))
            {
                var error1 = Bass.LastError;
                Log.Warning($"[AudioMixer] Init with Latency flag failed: {error1}, trying without...");
                
                // Fallback without latency flag
                if (!Bass.Init(-1, AudioConfig.MixerFrequency, DeviceInitFlags.Stereo, IntPtr.Zero))
                {
                    var error2 = Bass.LastError;
                    Log.Warning($"[AudioMixer] Init with Stereo flag failed: {error2}, trying basic init...");
                    
                    // Last resort - basic init
                    if (!Bass.Init(-1, AudioConfig.MixerFrequency, DeviceInitFlags.Default, IntPtr.Zero))
                    {
                        var error3 = Bass.LastError;
                        Log.Error($"[AudioMixer] Failed to initialize BASS with all methods: {error3}");
                        return;
                    }
                    else
                    {
                        Log.Warning("[AudioMixer] BASS initialized with DEFAULT flags (no latency optimization)");
                    }
                }
                else
                {
                    Log.Warning("[AudioMixer] BASS initialized with STEREO flag (no latency optimization)");
                }
            }
            else
            {
                AudioConfig.LogAudioDebug("[AudioMixer] BASS initialized with LATENCY flag (optimized)");
            }
            
            // Get actual device info after init
            Bass.GetInfo(out var info);
            AudioConfig.LogAudioInfo($"[AudioMixer] BASS Info - Device: {Bass.CurrentDevice}, SampleRate: {info.SampleRate}Hz, MinBuffer: {info.MinBufferLength}ms, Latency: {info.Latency}ms");
        }

        // Load BASS FLAC plugin for native FLAC support (better than Media Foundation)
        _flacPluginHandle = Bass.PluginLoad("bassflac.dll");
        if (_flacPluginHandle == 0)
        {
            Log.Warning($"[AudioMixer] Failed to load BASS FLAC plugin: {Bass.LastError}. FLAC files will use Media Foundation fallback.");
        }
        else
        {
            AudioConfig.LogAudioDebug($"[AudioMixer] BASS FLAC plugin loaded successfully: Handle={_flacPluginHandle}");
        }

        // Create global mixer (stereo output to soundcard)
        AudioConfig.LogAudioDebug("[AudioMixer] Creating global mixer stream...");
        _globalMixerHandle = BassMix.CreateMixerStream(AudioConfig.MixerFrequency, 2, BassFlags.Float | BassFlags.MixerNonStop);
        if (_globalMixerHandle == 0)
        {
            Log.Error($"[AudioMixer] Failed to create global mixer: {Bass.LastError}");
            return;
        }
        AudioConfig.LogAudioDebug($"[AudioMixer] Global mixer created: Handle={_globalMixerHandle}");

        // Create operator mixer (decode stream that feeds into global mixer)
        AudioConfig.LogAudioDebug("[AudioMixer] Creating operator mixer stream...");
        _operatorMixerHandle = BassMix.CreateMixerStream(AudioConfig.MixerFrequency, 2, BassFlags.MixerNonStop | BassFlags.Decode | BassFlags.Float);
        if (_operatorMixerHandle == 0)
        {
            Log.Error($"[AudioMixer] Failed to create operator mixer: {Bass.LastError}");
            return;
        }
        AudioConfig.LogAudioDebug($"[AudioMixer] Operator mixer created: Handle={_operatorMixerHandle}");

        // Create soundtrack mixer (decode stream that feeds into global mixer)
        AudioConfig.LogAudioDebug("[AudioMixer] Creating soundtrack mixer stream...");
        _soundtrackMixerHandle = BassMix.CreateMixerStream(AudioConfig.MixerFrequency, 2, BassFlags.MixerNonStop | BassFlags.Decode | BassFlags.Float);
        if (_soundtrackMixerHandle == 0)
        {
            Log.Error($"[AudioMixer] Failed to create soundtrack mixer: {Bass.LastError}");
            return;
        }
        AudioConfig.LogAudioDebug($"[AudioMixer] Soundtrack mixer created: Handle={_soundtrackMixerHandle}");

        // Create offline mixer for analysis (decode only, no output to soundcard)
        AudioConfig.LogAudioDebug("[AudioMixer] Creating offline analysis mixer stream...");
        _offlineMixerHandle = BassMix.CreateMixerStream(AudioConfig.MixerFrequency, 2, BassFlags.Decode | BassFlags.Float);
        if (_offlineMixerHandle == 0)
        {
            Log.Warning($"[AudioMixer] Failed to create offline mixer: {Bass.LastError}. Analysis tasks may interfere with playback.");
        }
        else
        {
            AudioConfig.LogAudioDebug($"[AudioMixer] Offline analysis mixer created: Handle={_offlineMixerHandle}");
        }

        // Add operator mixer to global mixer with buffer flag for smooth mixing
        AudioConfig.LogAudioDebug("[AudioMixer] Adding operator mixer to global mixer...");
        if (!BassMix.MixerAddChannel(_globalMixerHandle, _operatorMixerHandle, BassFlags.MixerChanBuffer))
        {
            Log.Error($"[AudioMixer] Failed to add operator mixer to global mixer: {Bass.LastError}");
        }
        else
        {
            AudioConfig.LogAudioDebug("[AudioMixer] Operator mixer added to global mixer successfully");
        }

        // Add soundtrack mixer to global mixer
        // Use MixerChanBuffer to enable level metering via BassMix.ChannelGetLevel
        AudioConfig.LogAudioDebug("[AudioMixer] Adding soundtrack mixer to global mixer...");
        if (!BassMix.MixerAddChannel(_globalMixerHandle, _soundtrackMixerHandle, BassFlags.MixerChanBuffer))
        {
            Log.Error($"[AudioMixer] Failed to add soundtrack mixer to global mixer: {Bass.LastError}");
        }
        else
        {
            AudioConfig.LogAudioDebug("[AudioMixer] Soundtrack mixer added to global mixer successfully");
        }

        // Note: Offline mixer is NOT added to global mixer - it's completely isolated

        // Start the global mixer playing (outputs to soundcard)
        AudioConfig.LogAudioDebug("[AudioMixer] Starting global mixer playback...");
        if (!Bass.ChannelPlay(_globalMixerHandle, false))
        {
            Log.Error($"[AudioMixer] Failed to start global mixer: {Bass.LastError}");
        }
        else
        {
            var playbackState = Bass.ChannelIsActive(_globalMixerHandle);
            AudioConfig.LogAudioDebug($"[AudioMixer] Global mixer started, State: {playbackState}");
        }

        _initialized = true;
        AudioConfig.LogAudioInfo("[AudioMixer] ✓ Audio mixer system initialized successfully with low-latency settings.");
    }

    public static void Shutdown()
    {
        if (!_initialized)
            return;

        AudioConfig.LogAudioDebug("[AudioMixer] Shutting down...");
        
        Bass.StreamFree(_operatorMixerHandle);
        Bass.StreamFree(_soundtrackMixerHandle);
        Bass.StreamFree(_offlineMixerHandle);
        Bass.StreamFree(_globalMixerHandle);
        
        // Unload FLAC plugin
        if (_flacPluginHandle != 0)
        {
            Bass.PluginFree(_flacPluginHandle);
        }
        
        Bass.Free();

        _initialized = false;
        AudioConfig.LogAudioDebug("[AudioMixer] Audio mixer system shut down.");
    }

    public static void SetOperatorMixerVolume(float volume)
    {
        if (!_initialized) return;
        Bass.ChannelSetAttribute(_operatorMixerHandle, ChannelAttribute.Volume, volume);
    }

    public static void SetSoundtrackMixerVolume(float volume)
    {
        if (!_initialized) return;
        Bass.ChannelSetAttribute(_soundtrackMixerHandle, ChannelAttribute.Volume, volume);
    }

    public static void SetGlobalVolume(float volume)
    {
        if (!_initialized) return;
        Bass.ChannelSetAttribute(_globalMixerHandle, ChannelAttribute.Volume, volume);
    }

    /// <summary>
    /// Creates a decode-only stream for offline analysis (waveform image generation, FFT, etc.)
    /// This stream is NOT connected to any output and will not interfere with playback.
    /// The caller is responsible for freeing the stream with Bass.StreamFree() when done.
    /// </summary>
    /// <param name="filePath">Absolute path to the audio file</param>
    /// <returns>Stream handle, or 0 if creation failed</returns>
    public static int CreateOfflineAnalysisStream(string filePath)
    {
        // Ensure BASS is initialized
        if (!_initialized)
        {
            Initialize();
        }

        lock (_offlineMixerLock)
        {
            // Create a decode-only stream (no output to soundcard)
            var stream = Bass.CreateStream(filePath, 0, 0, BassFlags.Decode | BassFlags.Prescan | BassFlags.Float);
            if (stream == 0)
            {
                var error = Bass.LastError;
                Log.Warning($"[AudioMixer] Failed to create offline analysis stream for '{filePath}': {error}");
                return 0;
            }

            AudioConfig.LogAudioDebug($"[AudioMixer] Created offline analysis stream: Handle={stream} for '{filePath}'");
            return stream;
        }
    }

    /// <summary>
    /// Frees an offline analysis stream created by CreateOfflineAnalysisStream.
    /// </summary>
    public static void FreeOfflineAnalysisStream(int streamHandle)
    {
        if (streamHandle == 0)
            return;

        lock (_offlineMixerLock)
        {
            Bass.StreamFree(streamHandle);
            AudioConfig.LogAudioDebug($"[AudioMixer] Freed offline analysis stream: Handle={streamHandle}");
        }
    }

    /// <summary>
    /// Gets the current audio level from the global mixer (0.0 to 1.0 normalized).
    /// Returns the maximum of left and right channels.
    /// Uses non-destructive level reading to avoid consuming audio data.
    /// </summary>
    public static float GetGlobalMixerLevel()
    {
        if (!_initialized || _globalMixerHandle == 0)
            return 0f;

        // Use ChannelGetLevelEx with a short time window for responsive metering
        // This doesn't consume audio data like ChannelGetLevel does
        float[] levels = new float[2];
        if (!Bass.ChannelGetLevel(_globalMixerHandle, levels, 0.05f, LevelRetrievalFlags.Stereo))
            return 0f;

        return Math.Max(levels[0], levels[1]);
    }

    /// <summary>
    /// Gets the current audio level from the operator mixer (0.0 to 1.0 normalized).
    /// Returns the maximum of left and right channels.
    /// Uses BassMix.ChannelGetLevel for decode streams with MixerChanBuffer.
    /// </summary>
    public static float GetOperatorMixerLevel()
    {
        if (!_initialized || _operatorMixerHandle == 0)
            return 0f;

        // For decode streams with MixerChanBuffer, use BassMix.ChannelGetLevel
        // This reads from the mixer's buffer without consuming data
        var level = BassMix.ChannelGetLevel(_operatorMixerHandle);
        if (level == -1)
            return 0f;

        var left = (level & 0xFFFF) / 32768f;
        var right = ((level >> 16) & 0xFFFF) / 32768f;
        return Math.Max(left, right);
    }

    /// <summary>
    /// Gets the current audio level from the soundtrack mixer (0.0 to 1.0 normalized).
    /// Returns the maximum of left and right channels.
    /// Uses BassMix.ChannelGetLevel for decode streams with MixerChanBuffer.
    /// </summary>
    public static float GetSoundtrackMixerLevel()
    {
        if (!_initialized || _soundtrackMixerHandle == 0)
            return 0f;

        // For decode streams with MixerChanBuffer, use BassMix.ChannelGetLevel
        // This reads from the mixer's buffer without consuming data
        var level = BassMix.ChannelGetLevel(_soundtrackMixerHandle);
        if (level == -1)
            return 0f;

        var left = (level & 0xFFFF) / 32768f;
        var right = ((level >> 16) & 0xFFFF) / 32768f;
        return Math.Max(left, right);
    }
}
