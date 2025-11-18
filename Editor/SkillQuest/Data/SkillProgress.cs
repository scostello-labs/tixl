using Newtonsoft.Json;
using T3.Serialization;

namespace T3.Editor.SkillQuest.Data;

// Maybe useful for later structuring
// public sealed class QuestZone
// {
//     public string Title = string.Empty;
//     public List<QuestTopic> Topics = [];
//     
//     
//     public static List<QuestZone> CreateZones()
//     {
//
//
// }

public sealed class SkillProgress
{
    public QuestTopic ActiveTopicId;
    public List<LevelResult> Results =[];
    
    public sealed class LevelResult
    {
        public DateTime StartTime;
        public DateTime EndTime;
        
        [JsonConverter(typeof(SafeEnumConverter<Results>))]
        public Results Result;
        public int Rating;
        
        public enum Results {
            Started,
            Skipped,
            Completed,
        }
    }
}

