#nullable enable
using System.Diagnostics.CodeAnalysis;
using System.IO;
using T3.Core.UserData;
using T3.Editor.Gui.Graph.Window;
using T3.Editor.Gui.UiHelpers;
using T3.Editor.SkillQuest.Data;
using T3.Editor.UiModel;
using T3.Editor.UiModel.ProjectHandling;
using T3.Serialization;

namespace T3.Editor.SkillQuest;

internal static partial class SkillManager
{
    internal static void Initialize()
    {
        InitializeLevels();

        LoadUserData();
        SaveUserData();
    }

    internal static void Update()
    {
        _stateMachine.UpdateAfterDraw(_context);
    }

    private static void InitializeLevels()
    {
        // TODO: Load from Json
        SkillQuestContext.Topics = CreateMockLevelStructure();
    }

    public static bool TryGetActiveTopic([NotNullWhen(true)] out QuestTopic? topic)
    {
        topic = null;

        if (SkillQuestContext.Topics.Count == 0)
            return false;

        topic = SkillQuestContext.Topics[0];
        return true;
    }

    public static bool TryGetActiveLevel([NotNullWhen(true)] out QuestLevel? level)
    {
        level = null;
        if (!TryGetActiveTopic(out var activeTopic))
            return false;

        if (activeTopic.Levels.Count == 0)
            return false;

        level = activeTopic.Levels[0];
        return true;
    }

    public static bool TryGetSkillsProject([NotNullWhen(true)] out EditableSymbolProject? skillProject)
    {
        skillProject = null;
        foreach (var p in EditableSymbolProject.AllProjects)
        {
            if (p.Alias == "Skills")
            {
                skillProject = p;
                return true;
            }
        }

        return false;
    }

    public static void StartGame(GraphWindow window, QuestLevel activeLevel)
    {
        if (!TryGetSkillsProject(out var skillProject))
            return;

        if (!OpenedProject.TryCreateWithExplicitHome(skillProject,
                                                     activeLevel.SymbolId,
                                                     out var openedProject,
                                                     out var failureLog))
        {
            Log.Warning(failureLog);
            return;
        }

        window.TrySetToProject(openedProject);
    }

    //private static bool isOpened;

    private static void LoadUserData()
    {
        if (!File.Exists(SkillProgressPath))
        {
            SkillQuestContext.SkillProgress = new SkillProgress(); // Fallback
        }

        try
        {
            SkillQuestContext.SkillProgress = JsonUtils.TryLoadingJson<SkillProgress>(SkillProgressPath);
            if (SkillQuestContext.SkillProgress == null)
                throw new Exception("Failed to load SkillProgress");
        }
        catch (Exception e)
        {
            Log.Error($"Failed to load {SkillProgressPath} : {e.Message}");
            SkillQuestContext.SkillProgress = new SkillProgress();
        }
    }

    private static void SaveUserData()
    {
        Directory.CreateDirectory(FileLocations.SettingsDirectory);
        JsonUtils.TrySaveJson(SkillQuestContext.SkillProgress, SkillProgressPath);
    }

    private static string SkillProgressPath => Path.Combine(FileLocations.SettingsDirectory, "SkillProgress.json");

    private static readonly SkillQuestContext _context = new();
    private static readonly StateMachine<SkillQuestContext> _stateMachine = new(SkillQuestStates.InActive);
}