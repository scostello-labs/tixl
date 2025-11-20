#nullable enable
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using ImGuiNET;
using T3.Editor.Gui;
using T3.Editor.Gui.Input;
using T3.Editor.Gui.MagGraph.Interaction;
using T3.Editor.Gui.Styling;
using T3.Editor.Gui.Window;
using T3.Editor.Gui.UiHelpers;
using T3.Editor.Gui.Windows.Layouts;
using T3.Editor.Gui.Windows.Output;
using T3.Editor.SkillQuest.Data;
using T3.Editor.UiModel;
using T3.Editor.UiModel.ProjectHandling;

namespace T3.Editor.SkillQuest;

internal static partial class SkillManager
{
    internal static void Initialize()
    {
        InitializeLevels();
        SkillProgression.LoadUserData();
        UpdateActiveTopicAndLevel();
    }

    public static void StartPlayModeFromHub(GraphWindow graphWindow)
    {
        _context.GraphWindow = graphWindow;
        StartActiveLevel();
    }

    private static void StartActiveLevel()
    {
        Debug.Assert(_context.GraphWindow != null);
        Debug.Assert(_context.ActiveLevel != null);

        if (!TryGetSkillsProject(out var skillProject) || _context.ActiveLevel == null)
            return;

        if (!OpenedProject.TryCreateWithExplicitHome(skillProject,
                                                     _context.ActiveLevel.SymbolId,
                                                     out var openedProject,
                                                     out var failureLog))
        {
            Log.Warning(failureLog);
            return;
        }

        _context.GraphWindow.TrySetToProject(openedProject);
        _context.OpenedProject = openedProject;
        _context.ProjectView = _context.GraphWindow.ProjectView;
        
        Debug.Assert(_context.OpenedProject != null);
        
        // Keep and apply a new UI state
        _context.PreviousUiState = UiState.KeepUiState();
        LayoutHandling.LoadAndApplyLayoutOrFocusMode(LayoutHandling.Layouts.SkillQuest);

        if (!OutputWindow.TryGetPrimaryOutputWindow(out var outputWindow))
        {
            Log.Debug("Can't access primary output window");
            UiState.ApplyUiState(_context.PreviousUiState);
            _context.StateMachine.SetState(SkillQuestStates.Inactive,_context);
            return;
        }
        
                         
        UiState.HideAllUiElements();
                         
        // Pin output
        var rootInstance = _context.OpenedProject.Structure.GetRootInstance();
        if (rootInstance == null)
        {
            Log.Debug("Failed to load root");
            UiState.ApplyUiState(_context.PreviousUiState);
            _context.StateMachine.SetState(SkillQuestStates.Inactive,_context);
            return;
        }
        outputWindow.Pinning.PinInstance(rootInstance);
        TourInteraction.SetProgressIndex(rootInstance.Symbol.Id, 0);
        
        _context.StateMachine.SetState(SkillQuestStates.Playing, _context);
    }

    internal static void Update()
    {
        var playmodeEnded = _context.ProjectView?.GraphView is { Destroyed: true };
        if (_context.StateMachine.CurrentState != SkillQuestStates.Inactive && playmodeEnded)
        {
            _context.StateMachine.SetState(SkillQuestStates.Inactive, _context);
        }

        _context.StateMachine.UpdateAfterDraw(_context);
    }

    /// <summary>
    /// This is called after processing of a frame and can be used to access the output evaluation context
    /// </summary>
    public static void PostUpdate()
    {
        if (_context.StateMachine.CurrentState != SkillQuestStates.Playing)
            return;

        if (!OutputWindow.TryGetPrimaryOutputWindow(out var outputWindow))
        {
            Log.Warning("Can't find output window for playmode?!");
            return;
        }

        if (!outputWindow.EvaluationContext.FloatVariables.TryGetValue(PlayModeProgressVariableId, out var progress))
        {
            Log.Warning($"Can't find progress variable '{PlayModeProgressVariableId}' after evaluation?");
            return;
        }

        if (_context.StateMachine.StateTime > 1 && progress >= 1.0f)
        {
            SaveResult(SkillProgression.LevelResult.States.Completed);
            ExitPlayMode();
            UpdateActiveTopicAndLevel(); // Progress to the next level...
            StartActiveLevel();
        }
    }

    private static void SaveResult(SkillProgression.LevelResult.States resultState)
    {
        if (_context.ActiveTopic == null || _context.ActiveLevel == null)
            return;

        SkillProgression.SaveLevelResult(_context.ActiveLevel, new SkillProgression.LevelResult
                                                                   {
                                                                       TopicId = _context.ActiveTopic.Id,
                                                                       LevelSymbolId = _context.ActiveLevel.SymbolId,
                                                                       StartTime = DateTime.Now,
                                                                       EndTime = DateTime.Now,
                                                                       State = resultState,
                                                                       Rating = -1,
                                                                   });
    }


    private static void InitializeLevels()
    {
        //SkillQuestContext.Topics = CreateMockLevelStructure();
        SkillQuestContext.Topics = CreateLevelStructureFromSymbols();
    }

    /// <summary>
    /// Update active topic and level from the last completed or skipped level in skill progression 
    /// </summary>
    private static bool UpdateActiveTopicAndLevel()
    {
        if (SkillQuestContext.Topics.Count == 0)
            return false;

        if (!SkillProgression.TryGetLastResult(out var lastResult)
            || !TryGetTopicAndLevelForResult(lastResult, out var lastCompletedTopic, out var lastCompletedLevel))
        {
            _context.ActiveTopic = SkillQuestContext.Topics[0];
            if (_context.ActiveTopic.Levels.Count == 0)
            {
                return false;
            }

            _context.ActiveLevel = _context.ActiveTopic.Levels[0];
            return true;
        }

        var lastLevelIndex = lastCompletedTopic.Levels.IndexOf(lastCompletedLevel);
        Debug.Assert(lastLevelIndex != -1);

        // TODO: we later should also check for non-linear progression....
        var topicStillHasLevels = lastLevelIndex < lastCompletedTopic.Levels.Count - 1;
        if (topicStillHasLevels)
        {
            _context.ActiveTopic = lastCompletedTopic;
            _context.ActiveLevel = lastCompletedTopic.Levels[lastLevelIndex + 1];
            return true;
        }

        // TODO: For properly advancing between topic we will need more logic and interactions later...
        var topicIndex = SkillQuestContext.Topics.IndexOf(lastCompletedTopic);
        var hasMoreTopics = topicIndex < SkillQuestContext.Topics.Count - 1;
        if (hasMoreTopics)
        {
            _context.ActiveTopic = SkillQuestContext.Topics[topicIndex + 1];
            _context.ActiveLevel = _context.ActiveTopic.Levels[0];
            return true;
        }

        // Restart from the beginning...
        _context.ActiveTopic = SkillQuestContext.Topics[0];
        _context.ActiveLevel = _context.ActiveTopic.Levels[0];
        return true;
    }

    private static bool TryGetTopicAndLevelForResult(SkillProgression.LevelResult result,
                                                     [NotNullWhen(true)] out QuestTopic? topic,
                                                     [NotNullWhen(true)] out QuestLevel? level)
    {
        topic = null;
        level = null;

        foreach (var t in SkillQuestContext.Topics)
        {
            if (t.Id != result.TopicId)
                continue;

            foreach (var l in t.Levels)
            {
                if (l.SymbolId == result.LevelSymbolId)
                {
                    topic = t;
                    level = l;
                    return true;
                }
            }

            return false;
        }

        return false;
    }

    private static bool TryGetSkillsProject([NotNullWhen(true)] out EditableSymbolProject? skillProject)
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

    private static void ExitPlayMode()
    {
        Debug.Assert(_context.OpenedProject != null);

        if (!_context.OpenedProject.Package.SymbolUis.TryGetValue(_context.OpenedProject.Package.HomeSymbolId, out var homeSymbolId))
        {
            Log.Warning($"Can't find symbol to revert changes?");
            return;
        }

        _context.ProjectView?.Close();
        _context.OpenedProject.Package.Reload(homeSymbolId);
        _context.StateMachine.SetState(SkillQuestStates.Inactive, _context);
    }

    public static void DrawLevelHeader()
    {
        if (!IsInPlaymode)
            return;

        var topic = _context.ActiveTopic;
        var level = _context.ActiveLevel;
        if (topic == null || level == null)
            return;

        var levelIndex = topic.Levels.IndexOf(level);

        var indentation = 40 * T3Ui.UiScaleFactor;

        FormInputs.AddVerticalSpace();
        ImGui.Indent(indentation);

        ImGui.PushFont(Fonts.FontSmall);
        ImGui.PushStyleColor(ImGuiCol.Text, UiColors.TextMuted.Rgba);
        ImGui.TextUnformatted($"{level.Title}  {levelIndex + 1}/{topic.Levels.Count} ");
        ImGui.PopStyleColor();
        ImGui.PopFont();

        var keepCursor = ImGui.GetCursorPos();
        ImGui.SetCursorPos(keepCursor - new Vector2(1f, -0.15f) * ImGui.GetFrameHeight());

        if (CustomComponents.TransparentIconButton(Icon.Exit, Vector2.Zero))
        {
            _context.ProjectView?.Close();
        }

        ImGui.SetCursorPos(keepCursor);

        //ImGui.SameLine(0,10);

        ImGui.PushFont(Fonts.FontLarge);
        ImGui.TextUnformatted(level.Title);
        ImGui.PopFont();

        ImGui.Unindent(indentation);
    }

    public static bool TryGetActiveTopicAndLevel([NotNullWhen(true)] out QuestTopic? topic,
                                                 [NotNullWhen(true)] out QuestLevel? level)
    {
        level = null;
        topic = null;
        if (_context.ActiveTopic == null || _context.ActiveLevel == null)
            return false;

        topic = _context.ActiveTopic;
        level = _context.ActiveLevel;
        return true;
    }

    private static bool IsInPlaymode => _context.StateMachine.CurrentState == SkillQuestStates.Playing;
    private const string PlayModeProgressVariableId = "_PlayModeProgress";

    private static readonly SkillQuestContext _context = new()
                                                             {
                                                                 StateMachine = new
                                                                     StateMachine<SkillQuestContext>(typeof(SkillQuestStates),
                                                                                                     SkillQuestStates.Inactive
                                                                                                    ),
                                                             };

    public static void ResetProgress()
    {
        SkillProgression.Data.Results.Clear();
        SkillProgression.SaveUserData();
        UpdateActiveTopicAndLevel();
    }
}