using Newtonsoft.Json;

namespace T3.Editor.SkillQuest.Data;

public struct QuestTopicLink
{
    public Guid SourceTopicId;
    public Guid TargetTopicId;
    
    [JsonIgnore]
    public QuestTopic SourceTopic;
    
    [JsonIgnore]
    public QuestTopic TargetTopic;

    [JsonIgnore]
    public bool Unlocked;
}