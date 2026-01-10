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
    }

    public static class Defaults
    {
        public static bool TimeClipSuspending = true;
        public static float AudioResyncThreshold = 0.04f;
        public static float SoundtrackPlaybackVolume = 1;
        public static float GlobalPlaybackVolume = 1; // New global volume
        public static bool SoundtrackMute; // Renamed from AudioMuted
        public static bool GlobalMute; // New global mute
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
    }
}

[Serializable]
public record ExportSettings(Guid OperatorId, string ApplicationTitle, WindowMode WindowMode, ProjectSettings.ConfigData ConfigData, string Author, Guid BuildId, string EditorVersion);
    
public enum WindowMode { Windowed, Fullscreen }