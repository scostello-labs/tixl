#nullable enable

using ImGuiNET;
using T3.Editor.Gui.Window;
using T3.Editor.Gui.Styling;
using T3.Editor.Gui.UiHelpers;
using T3.Editor.Skills;
using T3.Editor.Skills.Data;
using T3.Editor.Skills.Ui;
using SkillTraining = T3.Editor.Skills.Training.SkillTraining;

namespace T3.Editor.Gui.Hub;

internal static class SkillQuestPanel
{
    internal static void Draw(GraphWindow window, bool projectViewJustClosed)
    {
        if (!SkillTraining.TryGetActiveTopicAndLevel(out var activeTopic, out var activeLevel))
        {
            ImGui.TextUnformatted("non skill quest data");
            return;
        }

        if (projectViewJustClosed)
        {
            _selectedTopic.Clear();
            _selectedTopic.Add(activeTopic);
            _mapCanvas.FocusToActiveTopics(_selectedTopic);
        }

        ContentPanel.Begin("Skill Quest", "some sub title", DrawIcons, Height);
        {
            ImGui.BeginChild("Map", new Vector2(400, 0), false, ImGuiWindowFlags.NoBackground);
            //ImGui.Text("Dragons\nbe here");
            _mapCanvas.DrawContent(null, out _, _selectedTopic);
            ImGui.EndChild();

            ImGui.SameLine(0, 10);

            ImGui.BeginGroup();
            {
                ImGui.BeginChild("Content", new Vector2(0, -30), false);
                {
                    ImGui.PushFont(Fonts.FontSmall);
                    ImGui.PushStyleColor(ImGuiCol.Text, UiColors.TextMuted.Rgba);
                    ImGui.TextUnformatted(activeTopic.Title);
                    ImGui.PopStyleColor();
                    ImGui.PopFont();

                    ImGui.Text(activeLevel.Title);
                }
                ImGui.EndChild();

                ImGui.BeginChild("actions");
                {
                    ImGui.Button("Skip");
                    ImGui.SameLine(0, 10);
                    if (ImGui.Button("Start"))
                    {
                        SkillTraining.StartPlayModeFromHub(window);
                    }

                    ImGui.SameLine(0, 10);
                    if (ImGui.Button("Reset progress"))
                    {
                        SkillTraining.ResetProgress();
                    }
                }
                ImGui.EndChild();
            }
            ImGui.EndGroup();
        }

        ContentPanel.End();
    }

    private static void DrawIcons()
    {
        ImGui.Button("New Project");
        ImGui.SameLine(0, 10);

        Icon.AddFolder.DrawAtCursor();
    }

    internal static float Height => 220 * T3Ui.UiScaleFactor;

    private static readonly HashSet<QuestTopic> _selectedTopic = [];
    private static readonly SkillMapCanvas _mapCanvas = new();
}