#nullable enable
using System.Diagnostics.CodeAnalysis;
using System.IO;
using T3.Core.UserData;
using T3.Serialization;

namespace T3.Editor.SkillQuest.Data;

internal sealed class SkillMap
{
    public readonly List<QuestZone> Zones = [];

    [Newtonsoft.Json.JsonIgnore]
    public static QuestZone FallbackZone = new() { Title = "_undefined", Id = QuestZone.FallBackZoneId };

    [Newtonsoft.Json.JsonIgnore]
    internal static IEnumerable<QuestTopic> AllTopics
    {
        get
        {
            {
                foreach (var zone in Data.Zones)
                {
                    foreach (var t in zone.Topics)
                    {
                        yield return t;
                    }
                }
            }
        }
    }

    public static bool TryGetTopicWithNamespace(string symbolNamespace, [NotNullWhen(true)] out QuestTopic? questTopic)
    {
        foreach (var t in AllTopics)
        {
            if (t.Namespace != symbolNamespace)
                continue;

            questTopic = t;
            return true;
        }

        questTopic = null;
        return false;
    }
    
    public static bool TryGetTopic(Guid id, [NotNullWhen(true)] out QuestTopic? questTopic)
    {
        foreach (var t in AllTopics)
        {
            if (t.Id != id)
                continue;

            questTopic = t;
            return true;
        }

        questTopic = null;
        return false;
    }
    

    public static bool TryGetZone(Guid id, [NotNullWhen(true)] out QuestZone? zone)
    {
        foreach (var z in Data.Zones)
        {
            if (z.Id != id)
                continue;

            zone = z;
            return true;
        }

        zone = null;
        return false;
    }

    #region serialization
    internal static void Load()
    {
        if (!File.Exists(SkillMapPath))
        {
            Data = new SkillMap(); // Fallback
        }

        try
        {
            Data = JsonUtils.TryLoadingJson<SkillMap>(SkillMapPath)!;
            if (Data == null)
                throw new Exception("Failed to load SkillProgress");
        }
        catch (Exception e)
        {
            Log.Error($"Failed to load {SkillMapPath} : {e.Message}");
            Data = new SkillMap();
        }

        foreach (var z in Data.Zones)
        {
            if (z.Id == QuestZone.FallBackZoneId)
            {
                FallbackZone = z;
                return;
            }
        }

        Data.Zones.Add(FallbackZone);
    }

    internal static void Save()
    {
        Directory.CreateDirectory(FileLocations.ReadOnlySettingsPath);
        JsonUtils.TrySaveJson(Data, SkillMapPath);
    }

    private static string SkillMapPath => Path.Combine(FileLocations.ReadOnlySettingsPath, "SkillMap.json");

    [Newtonsoft.Json.JsonIgnore]
    internal static SkillMap Data = new();
    #endregion
}

public sealed class QuestZone
{
    public Guid Id = Guid.NewGuid();
    public string Title = string.Empty;

    public readonly List<QuestTopic> Topics = [];

    [Newtonsoft.Json.JsonIgnore]
    public static readonly Guid FallBackZoneId = new Guid("717505B4-0C6E-4708-85D8-54E202F9BBDF");
}