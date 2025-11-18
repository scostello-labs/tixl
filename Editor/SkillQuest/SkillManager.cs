using System.IO;
using T3.Core.UserData;
using T3.Editor.SkillQuest.Data;
using T3.Serialization;

namespace T3.Editor.SkillQuest;

internal static class SkillManager
{
    internal static void Initialize()
    {
        InitializeLevels();
        
        LoadUserData();
        SaveUserData(); // setup initial data.
        _initialized = true;
    }
    
    /// <summary>
    /// Load data for level structure...
    /// </summary>
    private static void InitializeLevels()
    {
        
    }
    

    private static void LoadUserData()
    {
        if (!File.Exists(SkillProgressPath))
        {
            SkillProgress = new SkillProgress();    // Fallback
        }
        try
        {
            SkillProgress = JsonUtils.TryLoadingJson<SkillProgress>(SkillProgressPath);
            if (SkillProgress == null)
                throw new Exception("Failed to load SkillProgress");
        }
        catch (Exception e)
        {
            Log.Error($"Failed to load {SkillProgressPath} : {e.Message}");
            SkillProgress = new SkillProgress();
        }
    }

    private static void SaveUserData()
    {
        Directory.CreateDirectory(FileLocations.SettingsDirectory);
        JsonUtils.TrySaveJson(SkillProgress, SkillProgressPath);
    }
    
    public static SkillProgress SkillProgress = new();

    private static bool _initialized; 
    
    private static string SkillProgressPath => Path.Combine(FileLocations.SettingsDirectory, "SkillProgress.json");

}