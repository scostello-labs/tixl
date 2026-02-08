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

        public bool EnablePlaybackControlWithKeyboard = true;

        public bool SkipOptimization;
        public bool EnableDirectXDebug;
        
        public bool LogAssemblyVersionMismatches = false;
            
        public string LimitMidiDeviceCapture = null; 
        public bool EnableMidiSnapshotIndication = false;
        public WindowMode DefaultWindowMode = WindowMode.Fullscreen;
        public int DefaultOscPort = 8000;
        
        // Logging
        public bool LogCompilationDetails = false;
        public bool LogAssemblyLoadingDetails = false;
        public bool LogFileEvents = false;
        
        // Profiling
        public bool EnableBeatSyncProfiling = false;

        // Audio
        public bool GlobalMute = false;
        public float GlobalPlaybackVolume = 1;
        public bool SoundtrackMute = false;
        public float SoundtrackPlaybackVolume = 0.5f;
        public bool OperatorMute = false;
        public float OperatorPlaybackVolume = 1;
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
        public static bool LogFileEvents = false;
        public static bool EnableBeatSyncProfiling = false;
        
        // Audio
        public static bool GlobalMute = false;
        public static float GlobalPlaybackVolume = 1;
        public static bool SoundtrackMute = false;
        public static float SoundtrackPlaybackVolume = 0.5f;
        public static bool OperatorMute = false;
        public static float OperatorPlaybackVolume = 1;
    }
}

[Serializable]
public record ExportSettings(Guid OperatorId, string ApplicationTitle, WindowMode WindowMode, ProjectSettings.ConfigData ConfigData, string Author, Guid BuildId, string EditorVersion);
    
public enum WindowMode { Windowed, Fullscreen }