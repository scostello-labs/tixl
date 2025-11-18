using ImGuiNET;
using T3.Editor.Gui.Graph.Window;

namespace T3.Editor.Gui.Hub;

internal static class ProjectHub
{
    public static void Draw(GraphWindow window)
    {
        ProjectsPanel.Draw(window);
        ImGui.Separator();
        SkillQuestPanel.Draw(window);
    }
}