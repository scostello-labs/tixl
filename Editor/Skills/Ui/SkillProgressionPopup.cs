using System.Diagnostics;
using ImGuiNET;
using T3.Core.Utils;
using T3.Editor.Gui;
using T3.Editor.Gui.Input;
using T3.Editor.Gui.Styling;
using T3.Editor.Gui.UiHelpers;
using T3.Editor.Skills.Training;
using T3.Editor.Skills.Data;
using T3.Editor.Skills.Ui;
using Color = T3.Core.DataTypes.Vector.Color;
using Vector2 = System.Numerics.Vector2;
using Vector4 = System.Numerics.Vector4;

namespace T3.Editor.Skills;

/// <summary>
/// A dialog that is shown after level completed.
/// </summary>
internal static class SkillProgressionPopup
{
    internal static void Show()
    {
        ImGui.OpenPopup(ProgressionPopupId);
        StarShowerEffect.Reset();
        _appearTime = ImGui.GetTime();
        
        if (!SkillTraining.TryGetActiveTopicAndLevel(out var topic, out var previousLevel))
            return;

        _topicSelection.Clear();
        _topicSelection.Add(topic);
        _mapCanvas.FocusToActiveTopics(_topicSelection);
    }    
    
    internal static void Draw()
    {
        var popUpSize = new Vector2(700, 260) * T3Ui.UiScaleFactor;

        // Center the popup in the main viewport
        var vp = ImGui.GetMainViewport();
        var pos = vp.Pos + (vp.Size - popUpSize) * 0.5f;
        ImGui.SetNextWindowSize(popUpSize, ImGuiCond.Always);
        ImGui.SetNextWindowPos(pos);

        if (!SkillTraining.TryGetActiveTopicAndLevel(out var topic, out var previousLevel))
            return;

        ImGui.PushStyleColor(ImGuiCol.PopupBg, UiColors.BackgroundFull.Rgba);
        if (ImGui.BeginPopup("ProgressionPopup", ImGuiWindowFlags.NoResize |
                                                 ImGuiWindowFlags.NoMove |
                                                 ImGuiWindowFlags.Modal))
        {
            var index = topic.Levels.IndexOf(previousLevel);
            Debug.Assert(index >= 0);
            // if (ImGui.Button("Test"))
            // {
            //     StarShowerEffect.Reset();
            // }

            if (index < topic.Levels.Count - 1)
            {
                var nextLevel = topic.Levels[index + 1];
                DrawNextLevelContent(topic, previousLevel, nextLevel, index);
            }

            DrawActions();

            ImGui.EndPopup();
            StarShowerEffect.DrawAndUpdate();
        }

        ImGui.PopStyleColor();
    }

    private static HashSet<QuestTopic>  _topicSelection = [];
    
    private static void DrawNextLevelContent(QuestTopic topic, QuestLevel previousLevel, QuestLevel nextLevel, int index)
    {
        var uiScale = T3Ui.UiScaleFactor;
        var dl = ImGui.GetWindowDrawList();

        var leftWidth = 240 * uiScale;
        ImGui.BeginChild("UpperArea", new Vector2(0, -40 * uiScale), false, ImGuiWindowFlags.NoBackground);
        {
            var area = ImRect.RectWithSize(ImGui.GetWindowPos(), ImGui.GetWindowSize());
            area.Expand(-10);
            dl.AddRectFilled(area.Min + new Vector2(leftWidth,0), area.Max, UiColors.WindowBackground, 7 * uiScale);

            ImGui.BeginChild("Map", new Vector2(leftWidth, 0), false, ImGuiWindowFlags.NoBackground);
            {
                _topicSelection??= new();
                _mapCanvas.DrawContent(null, out _, _topicSelection);
            }
            ImGui.EndChild();
            ImGui.SameLine();
            
            // ImGui.BeginChild("Left", new Vector2(leftWidth, 0), false, ImGuiWindowFlags.NoBackground);
            // {
            //     var donutSize = 60 * uiScale;
            //
            //     // center the donut in the left column
            //     var cp = ImGui.GetCursorPos();
            //     cp.X += (leftWidth - donutSize) * 0.5f;
            //     cp.Y += 10 * uiScale;
            //     ImGui.SetCursorPos(cp);
            //
            //     var torusCenter = ImGui.GetCursorScreenPos() + new Vector2(100, 120);
            //     var progress = (index + 1f) / topic.Levels.Count;
            //     DrawTorusProgress(dl, torusCenter, donutSize, 1, UiColors.BackgroundFull.Fade(0.6f));
            //     DrawTorusProgress(dl, torusCenter, donutSize, progress, UiColors.StatusActivated);
            //
            //     ImGui.SetCursorPos(cp + new Vector2(0, donutSize * 0.5f - Fonts.FontNormal.FontSize * 0.5f));
            //     ImGui.PushFont(Fonts.FontLarge);
            //     CenteredText($"{index + 1} / {topic.Levels.Count}");
            //     ImGui.PopFont();
            //
            //     ImGui.PushFont(Fonts.FontNormal);
            //     CenteredText(topic.Title);
            //     ImGui.PopFont();
            // }
            // ImGui.EndChild();

            ImGui.SameLine(0, 4);

            ImGui.BeginChild("Right", new Vector2(0, 0), false, ImGuiWindowFlags.NoBackground);
            {
                FormInputs.AddVerticalSpace(30);
                ImGui.Indent(20 * T3Ui.UiScaleFactor);

                // COMPLETED section
                CustomComponents.StylizedText("COMPLETED", Fonts.FontSmall, UiColors.Text.Fade(0.3f));
                CustomComponents.StylizedText(previousLevel.Title, Fonts.FontLarge, UiColors.Text.Fade(0.3f));

                FormInputs.AddVerticalSpace();
                ImGui.Separator();

                FormInputs.AddVerticalSpace();
                CustomComponents.StylizedText("NEXT UP", Fonts.FontSmall, UiColors.Text.Fade(0.3f));

                ImGui.PushFont(Fonts.FontLarge);
                ImGui.TextWrapped(nextLevel.Title);
                ImGui.PopFont();
            }
            ImGui.EndChild();
        }
        ImGui.EndChild();
    }

    private static void DrawActions()
    {
        var indent = 10;
        ImGui.Indent(indent);
        var style = ImGui.GetStyle();
        var btnH = ImGui.GetFrameHeight();
        //var wBack = ImGui.CalcTextSize("Back to Hub").X + style.FramePadding.X * 2;
        var wSkip = ImGui.CalcTextSize("Skip").X + style.FramePadding.X * 2;
        var wCont = ImGui.CalcTextSize("Continue").X + style.FramePadding.X * 2;
        var totalW = wSkip + wCont + style.ItemSpacing.X * 2 + indent;

        ImGui.PushStyleColor(ImGuiCol.Button, Color.Transparent.Rgba);
        if (ImGui.Button("Back to Hub", Vector2.Zero))
        {
            SkillTraining.SaveResult(SkillProgress.LevelResult.States.Skipped);
            SkillTraining.ExitPlayMode();
        }

        var right = ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X;
        ImGui.SetCursorPosX(right - totalW);

        ImGui.SameLine(ImGui.GetWindowWidth() - totalW);
        if (ImGui.Button("Skip", new Vector2(wSkip, btnH)))
        {
            //SkillManager.CompleteAndProgressToNextLevel(SkillProgression.LevelResult.States.Skipped);
            SkillTraining.SaveResult(SkillProgress.LevelResult.States.Skipped);
            SkillTraining.UpdateActiveTopicAndLevel();
        }

        ImGui.PopStyleColor();

        ImGui.SameLine();
        ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.20f, 0.45f, 0.95f, 1f));
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.25f, 0.55f, 1.00f, 1f));
        if (ImGui.Button("Continue", new Vector2(wCont, btnH)))
        {
            SkillTraining.CompleteAndProgressToNextLevel(SkillProgress.LevelResult.States.Completed);
        }

        ImGui.PopStyleColor(2);
    }

    private static void CenteredText(string text)
    {
        var labelSize = ImGui.CalcTextSize(text);
        var availableSize = ImGui.GetWindowSize().X;
        ImGui.SetCursorPosX(availableSize / 2 - labelSize.X / 2);
        ImGui.TextUnformatted(text);
    }

    private static void DrawTorusProgress(ImDrawListPtr dl, Vector2 center, float radius, float progress, Color color)
    {
        dl.PathClear();
        var opening = 0.5f;

        var aMin = 0.5f * MathF.PI + opening;
        var aMax = 2.5f * MathF.PI - opening;
        dl.PathArcTo(center, radius, aMin, MathUtils.Lerp(aMin, aMax, progress), 64);
        dl.PathStroke(color, ImDrawFlags.None, 6);
    }



    private const string ProgressionPopupId = "ProgressionPopup";
    private static double _appearTime;
    private static SkillMapCanvas _mapCanvas = new();
}

/// <summary>
/// A simple "celebration" effect that can be shown on events like "level completed".
/// </summary>
internal static class StarShowerEffect
{
    static internal void DrawAndUpdate()
    {
        var center = GetCenter();
        var dl = ImGui.GetForegroundDrawList();

        for (var index = 0; index < _positions.Length; index++)
        {
            // Update position
            var rand = MathUtils.Hash01((uint)index);
            var p = _positions[index];
            var dFromCenter = p - center;
            var l = dFromCenter.Length();
            var dNorm = Vector2.Normalize(dFromCenter);
            var f = 50f / (l + 5f) + 0.2f * rand;
            p += f * dNorm * 40f * f + new Vector2(0, 6f) * (f + 0.7f);

            _positions[index] = p;

            Icons.DrawIconAtScreenPosition(Icon.Star, p, dl, Color.Orange.Fade((2 * f).Clamp(0, 1)));
        }
    }

    internal static void Reset()
    {
        float radius = 30;
        var center = GetCenter() + new Vector2(0, -10);
        for (var index = 0; index < _positions.Length; index++)
        {
            var f = (float)index / Count + 0.223f;
            _positions[index] = center + new Vector2(MathF.Sin(f * MathF.Tau),
                                                     MathF.Cos(f * MathF.Tau)) * radius;
        }
    }

    private static Vector2 GetCenter()
    {
        var vp = ImGui.GetMainViewport();
        var windowSize = vp.Size;
        var center = windowSize * 0.5f;
        return center + new Vector2(-220, -40) * T3Ui.UiScaleFactor;
    }

    private static readonly Vector2[] _positions = new Vector2[Count];

    private const int Count = 30;
}