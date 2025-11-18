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

/// <summary>
/// The state of the active user progress for serialization to settings.
/// </summary>
public sealed class SkillProgress
{
    public QuestTopic ActiveTopicId;
    
    public List<LevelResult> Results =[];
    
    public sealed class LevelResult
    {
        public Guid LevelId;
        
        public DateTime StartTime;
        public DateTime EndTime;
        
        [JsonConverter(typeof(SafeEnumConverter<States>))]
        public States State;
        public int Rating;
        
        public enum States {
            Started,
            Skipped,
            Completed,
        }
    }
}

