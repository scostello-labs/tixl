using System.Diagnostics.CodeAnalysis;
using Newtonsoft.Json;
using T3.Editor.Skills.Ui;
using T3.Serialization;

namespace T3.Editor.Skills.Data;

[SuppressMessage("ReSharper", "MemberCanBeInternal")]
public sealed class QuestTopic
{
    public Guid Id = Guid.Empty;
    public string Title = string.Empty;
    public string Description = string.Empty;

    public Vector2 MapCoordinate;
    public Guid ZoneId;

    public List<Guid> UnlocksTopics = [];

    /** For linking to package levels */
    public string Namespace;

    [JsonConverter(typeof(SafeEnumConverter<TopicTypes>))]
    public TopicTypes TopicType;

    [JsonConverter(typeof(SafeEnumConverter<Statuses>))]
    public Statuses Status;

    [JsonConverter(typeof(SafeEnumConverter<Requirements>))]
    public Requirements Requirement = Requirements.None;

    /** Levels will be initialized from symbols in a Skills package */
    [JsonIgnore]
    public List<QuestLevel> Levels = [];

    public enum Requirements
    {
        None,
        IsValidStartPoint,
        AnyInputPath,
        AllInputPaths,
    }

    public enum Statuses
    {
        None,
        Locked,
        Unlocked,
        Completed,
    }

    /** We use an enum to avoid types in serialization. */
    public enum TopicTypes
    {
        Image,
        Numbers,
        Command,
        String,
        Gpu,
        ShaderGraph,
    }

    [JsonIgnore]
    internal HexCanvas.Cell Cell { get => new((int)MapCoordinate.X, (int)MapCoordinate.Y); set => MapCoordinate = new Vector2(value.X, value.Y); }
}