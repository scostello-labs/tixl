using System;

namespace T3.Core.IO;

/// <summary>
/// Saves view layout and currently open node 
/// </summary>
public class ProjectSettings : Settings<ProjectSettings.ConfigData>
{
    public ProjectSettings(bool saveOnQuit) : base("projectSettings.json", saveOnQuit)
    {
    }
        
    public class ConfigData
    {
        public bool TimeClipSuspending = true;
        public float AudioResyncThreshold = 0.04f;
        public float SoundtrackPlaybackVolume = 1; // Renamed from PlaybackVolume
        public float GlobalPlaybackVolume = 1; // New global volume
        public bool SoundtrackMute; // Renamed from AudioMuted
        public bool GlobalMute; // New global mute
        public float OperatorPlaybackVolume = 1; // New operator mixer volume
        public bool OperatorMute; // New operator mixer mute

        public bool EnablePlaybackControlWithKeyboard = true;

        public bool SkipOptimization;
        public bool EnableDirectXDebug;
        
        public bool LogAssemblyVersionMismatches = false;
            
        public string LimitMidiDeviceCapture = null; 
        public bool EnableMidiSnapshotIndication = false;
        public WindowMode DefaultWindowMode = WindowMode.Fullscreen;
        public int DefaultOscPort = 8000;
        
        public bool LogCompilationDetails = false;
        public bool LogAssemblyLoadingDetails = false;
        
        // Profiling
        public bool EnableBeatSyncProfiling = false;
        
        // Audio
        public bool ShowAudioDebugLogs = false;
        public bool ShowAudioRenderingDebugLogs = false;
        public bool ShowVideoRenderingDebugLogs = false;
        
        // Audio Advanced Settings
        public int AudioMixerFrequency = 48000;
        public int AudioUpdatePeriodMs = 10;
        public int AudioUpdateThreads = 2;
        public int AudioPlaybackBufferLengthMs = 100;
        public int AudioDeviceBufferLengthMs = 20;
        public int AudioFftBufferSize = 1024;
        public int AudioFrequencyBandCount = 32;
        public int AudioWaveformSampleCount = 1024;
        public float AudioLowPassCutoffFrequency = 250f;
        public float AudioHighPassCutoffFrequency = 2000f;
    }

    public static class Defaults
    {
        public static bool TimeClipSuspending = true;
        public static float AudioResyncThreshold = 0.04f;
        public static bool EnablePlaybackControlWithKeyboard = true;
        public static bool SkipOptimization;
        public static bool EnableDirectXDebug;
        public static bool LogAssemblyVersionMismatches = false;
        public static string LimitMidiDeviceCapture = null;
        public static bool EnableMidiSnapshotIndication = false;
        public static WindowMode DefaultWindowMode = WindowMode.Fullscreen;
        public static int DefaultOscPort = 8000;
        public static bool LogCompilationDetails = false;
        public static bool LogAssemblyLoadingDetails = false;
        public static bool EnableBeatSyncProfiling = false;
        
        // Logging
        public static bool ShowAudioDebugLogs = false;
        public static bool ShowAudioRenderingDebugLogs = false;
        public static bool ShowVideoRenderingDebugLogs = false;

        // Audio
        public static bool GlobalMute = false;
        public static float GlobalPlaybackVolume = 1;
        public static bool SoundtrackMute = false;
        public static float SoundtrackPlaybackVolume = 1;
        public static bool OperatorMute = false;
        public static float OperatorPlaybackVolume = 1;

        // Audio Advanced Settings
        public static int AudioMixerFrequency = 48000;
        public static int AudioUpdatePeriodMs = 10;
        public static int AudioUpdateThreads = 2;
        public static int AudioPlaybackBufferLengthMs = 100;
        public static int AudioDeviceBufferLengthMs = 20;
        public static int AudioFftBufferSize = 1024;
        public static int AudioFrequencyBandCount = 32;
        public static int AudioWaveformSampleCount = 1024;
        public static float AudioLowPassCutoffFrequency = 250f;
        public static float AudioHighPassCutoffFrequency = 2000f;
    }
}

[Serializable]
public record ExportSettings(Guid OperatorId, string ApplicationTitle, WindowMode WindowMode, ProjectSettings.ConfigData ConfigData, string Author, Guid BuildId, string EditorVersion);
    
public enum WindowMode { Windowed, Fullscreen }