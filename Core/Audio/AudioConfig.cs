using T3.Core.Logging;

namespace T3.Core.Audio;

/// <summary>
/// Configuration settings for the audio system.
/// These are set by the editor based on user preferences.
/// </summary>
public static class AudioConfig
{
    /// <summary>
    /// When true, Debug and Info logs from audio classes will be suppressed.
    /// </summary>
    public static bool SuppressDebugLogs { get; set; } = false;

    /// <summary>
    /// Helper method to log Debug messages that respect the suppression setting.
    /// </summary>
    public static void LogDebug(string message)
    {
        if (!SuppressDebugLogs)
            Log.Debug(message);
    }

    /// <summary>
    /// Helper method to log Info messages that respect the suppression setting.
    /// </summary>
    public static void LogInfo(string message)
    {
        if (!SuppressDebugLogs)
            Log.Info(message);
    }
}
