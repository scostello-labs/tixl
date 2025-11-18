#nullable enable

using ImGuiNET;
using T3.Editor.Gui.Graph.Window;
using T3.Editor.Gui.Styling;
using T3.Editor.SkillQuest;

namespace T3.Editor.Gui.Hub;

internal static class SkillQuestPanel
{
    internal static void Draw(GraphWindow window)
    {
        if (!SkillManager.TryGetActiveTopic(out var activeTopic)
            || !SkillManager.TryGetActiveLevel(out var activeLevel))
        {
            ImGui.TextUnformatted("non skill quest data");
            return;
        }

        ContentPanel.Begin("Skill Quest", "some sub title", DrawIcons, Height);
        {
            ImGui.BeginChild("Map", new Vector2(100, 0));
            ImGui.Text("Dragons\nbe here");
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
                        SkillManager.StartGame(window, activeLevel);
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

    internal static float Height => 120 * T3Ui.UiScaleFactor;
}