using ImGuiNET;
using T3.Core.SystemUi;
using T3.Editor.Gui.Graph.Window;
using T3.Editor.Gui.Input;
using T3.Editor.Gui.Styling;
using T3.Editor.UiModel;
using T3.Editor.UiModel.ProjectHandling;

namespace T3.Editor.Gui.Hub;

internal sealed class ProjectHub
{
    public static void DrawProjectList(GraphWindow window)
    {
        var projectItemSize = new Vector2(400, 65) * T3Ui.UiScaleFactor;
        
        ImGui.Indent(30);
        FormInputs.AddVerticalSpace(20);
        FormInputs.AddSectionHeader("Project Hub");
        ImGui.SameLine();
        
        var iconSize = new Vector2(Fonts.FontLarge.FontSize);
        // set cursor to the right
        ImGui.SetCursorPosX(projectItemSize.X);
        if (CustomComponents.IconButton(Icon.Plus, iconSize))
        {
            T3Ui.NewProjectDialog.ShowNextFrame();
        }

        FormInputs.AddVerticalSpace(20);
        
        ImGui.BeginChild("content", new Vector2(0, 0), true, ImGuiWindowFlags.NoBackground);
        {
            var dl = ImGui.GetWindowDrawList();
            ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(5, 5));
            foreach (var package in EditableSymbolProject.AllProjects)
            {
                if (!package.HasHome)
                    continue;

                ImGui.PushID(package.DisplayName);
                var isOpened = OpenedProject.OpenedProjects.TryGetValue(package, out var openedProject);
                var name = package.DisplayName;
                var clicked = ImGui.InvisibleButton(name, projectItemSize);
                var isHovered = ImGui.IsItemHovered();
                var backgroundColor = isHovered
                                          ? UiColors.ForegroundFull.Fade(0.1f)
                                          : UiColors.ForegroundFull.Fade(0.05f);

                var min = ImGui.GetItemRectMin();
                var max = ImGui.GetItemRectMax();
                dl.AddRectFilled(min, max, backgroundColor, 6);

                var padding = 3f * T3Ui.UiScaleFactor;
                if (isOpened)
                {
                    dl.AddRectFilled(min + Vector2.One * padding,
                                     new Vector2(min.X + padding + 4, max.Y - padding),
                                     UiColors.BackgroundActive, 2);
                }

                var rootName = package.RootNamespace.Split(".")[^1];
                if (isOpened)
                    rootName += " (loaded)";

                var y = padding;
                var x = 20f;
                dl.AddText(Fonts.FontBold,
                           Fonts.FontBold.FontSize,
                           min + new Vector2(x, y),
                           UiColors.Text, rootName);

                y += Fonts.FontNormal.FontSize + 5;

                dl.AddText(Fonts.FontSmall,
                           Fonts.FontSmall.FontSize,
                           min + new Vector2(x, y),
                           UiColors.TextMuted, package.RootNamespace);

                y += Fonts.FontSmall.FontSize + 5;

                dl.AddText(Fonts.FontSmall,
                           Fonts.FontSmall.FontSize,
                           min + new Vector2(x, y),
                           UiColors.TextMuted, package.Folder);


                if (clicked)
                {
                    if (!isOpened)
                    {
                        if (!OpenedProject.TryCreate(package, out openedProject, out var error))
                        {
                            Log.Warning($"Failed to load project: {error}");
                            continue;
                        }
                    }

                    if (openedProject != null)
                    {
                        window.TrySetToProject(openedProject);
                    }
                }

                ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(5, 5));
                if (ImGui.BeginPopupContextItem("windows_context_menu"))
                {
                    if (ImGui.MenuItem("Reveal in Explorer"))
                    {
                        CoreUi.Instance.OpenWithDefaultApplication(package.Folder);
                    }

                    if (ImGui.MenuItem("Unload project", "", isOpened))
                    {
                        Log.Warning("Not implemented yet");
                    }

                    ImGui.EndPopup();
                }

                ImGui.PopStyleVar();

                ImGui.PopID();

            }
        }
        ImGui.EndChild();
        ImGui.PopStyleVar(2);
        ImGui.Unindent();
    }
}