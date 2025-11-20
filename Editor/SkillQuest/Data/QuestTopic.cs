using Newtonsoft.Json;
using T3.Serialization;

namespace T3.Editor.SkillQuest.Data;

public sealed class QuestTopic
{
    // TODO: Color, style, etc. 
    
    public Guid Id = Guid.Empty;
    public string Title= string.Empty;
    public List<QuestLevel> Levels = [];
    public List<Guid> PathsFromTopicIds=[];
    
    [JsonConverter(typeof(SafeEnumConverter<Requirements>))]
    public Requirements Requirement = Requirements.None;
    
    [JsonIgnore]
    public List<SkillProgress.LevelResult>  ResultsForTopic=[];

    public enum Requirements
    {
        None,
        IsValidStartPoint,
        AnyInputPath,
        AllInputPaths,
    }
}