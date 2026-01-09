#nullable enable
using System;
using System.Collections.Generic;
using ManagedBass;
using ManagedBass.Mix;
using T3.Core.Logging;

namespace T3.Core.Audio;

/// <summary>
/// Manages the audio mixer architecture with separate paths for operator clips and soundtrack clips
/// Architecture: Operator Clip(s) > Operator Mixer > Global Mixer > Soundcard
///               Soundtrack Clip(s) > Soundtrack Mixer > Global Mixer > Soundcard
/// </summary>
public static class AudioMixerManager
{
    private static int _globalMixerHandle;
    private static int _operatorMixerHandle;
    private static int _soundtrackMixerHandle;
    private static bool _initialized;
    private static int _flacPluginHandle;

    public static int GlobalMixerHandle => _globalMixerHandle;
    public static int OperatorMixerHandle => _operatorMixerHandle;
    public static int SoundtrackMixerHandle => _soundtrackMixerHandle;
    
    private const int MixerFrequency = 48000;

    public static void Initialize()
    {
        if (_initialized)
        {
            AudioConfig.LogDebug("[AudioMixer] Already initialized, skipping.");
            return;
        }

        AudioConfig.LogDebug("[AudioMixer] Starting initialization...");

        // Check if BASS is already initialized
        DeviceInfo deviceInfo;
        var bassIsInitialized = Bass.GetDeviceInfo(Bass.CurrentDevice, out deviceInfo) && deviceInfo.IsInitialized;
        
        if (bassIsInitialized)
        {
            Log.Warning("[AudioMixer] BASS was already initialized by something else - our low-latency config may not apply!");
            Log.Warning("[AudioMixer] To fix this, ensure AudioMixerManager.Initialize() is called BEFORE any Bass.Init() calls.");
            Bass.GetInfo(out var info);
            AudioConfig.LogInfo($"[AudioMixer] Existing BASS - SampleRate: {info.SampleRate}Hz, MinBuffer: {info.MinBufferLength}ms, Latency: {info.Latency}ms");
        }
        else
        {
            AudioConfig.LogDebug("[AudioMixer] BASS not initialized, configuring for low latency...");
            
            // Configure BASS for low latency BEFORE initialization
            Bass.Configure(Configuration.UpdatePeriod, 10);
            Bass.Configure(Configuration.UpdateThreads, 2);
            Bass.Configure(Configuration.PlaybackBufferLength, 100);
            Bass.Configure(Configuration.DeviceBufferLength, 20);
            
            AudioConfig.LogDebug($"[AudioMixer] Config - UpdatePeriod: 10ms, UpdateThreads: 2, PlaybackBuffer: 100ms, DeviceBuffer: 20ms");
            
            // Try to initialize with latency flag first
            var initFlags = DeviceInitFlags.Latency | DeviceInitFlags.Stereo;
            
            AudioConfig.LogDebug($"[AudioMixer] Attempting BASS.Init with Latency flag at {MixerFrequency}Hz...");
            if (!Bass.Init(-1, MixerFrequency, initFlags, IntPtr.Zero))
            {
                var error1 = Bass.LastError;
                Log.Warning($"[AudioMixer] Init with Latency flag failed: {error1}, trying without...");
                
                // Fallback without latency flag
                if (!Bass.Init(-1, MixerFrequency, DeviceInitFlags.Stereo, IntPtr.Zero))
                {
                    var error2 = Bass.LastError;
                    Log.Warning($"[AudioMixer] Init with Stereo flag failed: {error2}, trying basic init...");
                    
                    // Last resort - basic init
                    if (!Bass.Init(-1, MixerFrequency, DeviceInitFlags.Default, IntPtr.Zero))
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
                AudioConfig.LogDebug("[AudioMixer] BASS initialized with LATENCY flag (optimized)");
            }
            
            // Get actual device info after init
            Bass.GetInfo(out var info);
            AudioConfig.LogInfo($"[AudioMixer] BASS Info - Device: {Bass.CurrentDevice}, SampleRate: {info.SampleRate}Hz, MinBuffer: {info.MinBufferLength}ms, Latency: {info.Latency}ms");
        }

        // Load BASS FLAC plugin for native FLAC support (better than Media Foundation)
        _flacPluginHandle = Bass.PluginLoad("bassflac.dll");
        if (_flacPluginHandle == 0)
        {
            Log.Warning($"[AudioMixer] Failed to load BASS FLAC plugin: {Bass.LastError}. FLAC files will use Media Foundation fallback.");
        }
        else
        {
            AudioConfig.LogDebug($"[AudioMixer] BASS FLAC plugin loaded successfully: Handle={_flacPluginHandle}");
        }

        // Create global mixer (stereo output to soundcard)
        AudioConfig.LogDebug("[AudioMixer] Creating global mixer stream...");
        _globalMixerHandle = BassMix.CreateMixerStream(MixerFrequency, 2, BassFlags.Float | BassFlags.MixerNonStop);
        if (_globalMixerHandle == 0)
        {
            Log.Error($"[AudioMixer] Failed to create global mixer: {Bass.LastError}");
            return;
        }
        AudioConfig.LogDebug($"[AudioMixer] Global mixer created: Handle={_globalMixerHandle}");

        // Create operator mixer (decode stream that feeds into global mixer)
        AudioConfig.LogDebug("[AudioMixer] Creating operator mixer stream...");
        _operatorMixerHandle = BassMix.CreateMixerStream(MixerFrequency, 2, BassFlags.MixerNonStop | BassFlags.Decode);
        if (_operatorMixerHandle == 0)
        {
            Log.Error($"[AudioMixer] Failed to create operator mixer: {Bass.LastError}");
            return;
        }
        AudioConfig.LogDebug($"[AudioMixer] Operator mixer created: Handle={_operatorMixerHandle}");

        // Create soundtrack mixer (decode stream that feeds into global mixer)
        AudioConfig.LogDebug("[AudioMixer] Creating soundtrack mixer stream...");
        _soundtrackMixerHandle = BassMix.CreateMixerStream(MixerFrequency, 2, BassFlags.MixerNonStop | BassFlags.Decode);
        if (_soundtrackMixerHandle == 0)
        {
            Log.Error($"[AudioMixer] Failed to create soundtrack mixer: {Bass.LastError}");
            return;
        }
        AudioConfig.LogDebug($"[AudioMixer] Soundtrack mixer created: Handle={_soundtrackMixerHandle}");

        // Add operator mixer to global mixer with buffer flag for smooth mixing
        AudioConfig.LogDebug("[AudioMixer] Adding operator mixer to global mixer...");
        if (!BassMix.MixerAddChannel(_globalMixerHandle, _operatorMixerHandle, BassFlags.MixerChanBuffer))
        {
            Log.Error($"[AudioMixer] Failed to add operator mixer to global mixer: {Bass.LastError}");
        }
        else
        {
            AudioConfig.LogDebug("[AudioMixer] Operator mixer added to global mixer successfully");
        }

        // Add soundtrack mixer to global mixer with buffer flag
        AudioConfig.LogDebug("[AudioMixer] Adding soundtrack mixer to global mixer...");
        if (!BassMix.MixerAddChannel(_globalMixerHandle, _soundtrackMixerHandle, BassFlags.MixerChanBuffer))
        {
            Log.Error($"[AudioMixer] Failed to add soundtrack mixer to global mixer: {Bass.LastError}");
        }
        else
        {
            AudioConfig.LogDebug("[AudioMixer] Soundtrack mixer added to global mixer successfully");
        }

        // Start the global mixer playing (outputs to soundcard)
        AudioConfig.LogDebug("[AudioMixer] Starting global mixer playback...");
        if (!Bass.ChannelPlay(_globalMixerHandle, false))
        {
            Log.Error($"[AudioMixer] Failed to start global mixer: {Bass.LastError}");
        }
        else
        {
            var playbackState = Bass.ChannelIsActive(_globalMixerHandle);
            AudioConfig.LogDebug($"[AudioMixer] Global mixer started, State: {playbackState}");
        }

        _initialized = true;
        AudioConfig.LogInfo("[AudioMixer] ✓ Audio mixer system initialized successfully with low-latency settings.");
    }

    public static void Shutdown()
    {
        if (!_initialized)
            return;

        AudioConfig.LogDebug("[AudioMixer] Shutting down...");
        
        Bass.StreamFree(_operatorMixerHandle);
        Bass.StreamFree(_soundtrackMixerHandle);
        Bass.StreamFree(_globalMixerHandle);
        
        // Unload FLAC plugin
        if (_flacPluginHandle != 0)
        {
            Bass.PluginFree(_flacPluginHandle);
        }
        
        Bass.Free();

        _initialized = false;
        AudioConfig.LogDebug("[AudioMixer] Audio mixer system shut down.");
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
}
