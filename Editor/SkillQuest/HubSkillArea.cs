using ImGuiNET;
using T3.Editor.Gui.Styling;

namespace T3.Editor.SkillQuest;

public static class HubSkillArea
{
    public static void Draw()
    {
        // TODO: Header
        
            // TODO: Toolbar
        
        //
        
        ImGui.Text("Skill Quest");
        CustomComponents.SmallGroupHeader("Annotation");
        ImGui.BeginChild("Child");
        {
            ImGui.BeginGroup();
            ImGui.Text("Dragons be here");
            ImGui.EndGroup();
            
            ImGui.SameLine();
            ImGui.BeginGroup();
            ImGui.Text("Active level name");

            ImGui.Button("Skip");
            ImGui.Button("Start");
            
            ImGui.EndGroup();
        }
        ImGui.EndChild();
    }
}