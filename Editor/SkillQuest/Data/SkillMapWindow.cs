#nullable enable

using ImGuiNET;
using T3.Core.DataTypes;
using T3.Core.DataTypes.Vector;
using T3.Core.Utils;
using T3.Editor.Gui;
using T3.Editor.Gui.Input;
using T3.Editor.Gui.Styling;
using T3.Editor.UiModel.InputsAndTypes;

namespace T3.Editor.SkillQuest.Data;

internal static class SkillMapPopup
{
    private static bool _isOpen;

    internal static void ShowNextFrame()
    {
        _isOpen = true;
    }

    private static QuestZone? _activeZone;
    private static QuestTopic? _activeTopic;

    internal static void Draw()
    {
        if (!_isOpen)
            return;

        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 1);
        ImGui.SetNextWindowSize(new Vector2(500, 500) * T3Ui.UiScaleFactor, ImGuiCond.Once);
        if (ImGui.Begin("Edit skill map", ref _isOpen))
        {
            ImGui.BeginChild("LevelList", new Vector2(120 * T3Ui.UiScaleFactor, 0));
            {
                foreach (var zone in SkillMap.Data.Zones)
                {
                    ImGui.PushID(zone.Id.GetHashCode());
                    if (ImGui.Selectable($"{zone.Title}", zone == _activeZone))
                    {
                        _activeZone = zone;
                        _activeTopic = null;
                    }

                    ImGui.Indent(10);

                    for (var index = 0; index < zone.Topics.Count; index++)
                    {
                        var t = zone.Topics[index];
                        ImGui.PushID(index);
                        if (ImGui.Selectable($"{t.Title}", t == _activeTopic))
                        {
                            _activeTopic = t;
                        }

                        ImGui.PopID();
                    }

                    ImGui.Unindent(10);
                    FormInputs.AddVerticalSpace();

                    ImGui.PopID();
                }
            }
            ImGui.EndChild();

            ImGui.SameLine();
            ImGui.BeginChild("Inner", new Vector2(-200, 0), false, ImGuiWindowFlags.NoMove);
            {
                ImGui.SameLine();

                if (ImGui.Button("Save"))
                {
                    SkillMap.Save();
                }

                _canvas.UpdateCanvas(out _);
                DrawContent();
            }

            ImGui.EndChild();
            ImGui.SameLine();
            ImGui.BeginChild("SidePanel", new Vector2(200, 0));
            {
                DrawSidebar();
            }
            ImGui.EndChild();
        }

        ImGui.PopStyleColor();

        ImGui.End();
        ImGui.PopStyleVar();
    }

    private enum States
    {
        Default,
        LinkingItems,
        DraggingItems,
    }

    private static void DrawSidebar()
    {
        if (_activeTopic == null)
            return;

        if (ImGui.IsKeyDown(ImGuiKey.A) && !ImGui.IsAnyItemActive())
        {
            _state = States.LinkingItems;
        }

        var isSelectingUnlocked = _state == States.LinkingItems;

        if (CustomComponents.ToggleIconButton(ref isSelectingUnlocked, Icon.ConnectedOutput, Vector2.Zero))
        {
            _state = isSelectingUnlocked ? States.LinkingItems : States.Default;
        }

        ImGui.Indent(5);
        var autoFocus = false;
        if (_focusTopicNameInput)
        {
            autoFocus = true;
            _focusTopicNameInput = false;
        }

        FormInputs.DrawFieldSetHeader("Topic");
        ImGui.PushID(_activeTopic.Id.GetHashCode());
        FormInputs.AddStringInput("##Topic", ref _activeTopic.Title, autoFocus: autoFocus);
        FormInputs.AddVerticalSpace();

        if (FormInputs.AddDropdown<Type>(ref _activeTopic.Type,
                [
                    typeof(Texture2D),
                    typeof(float),
                    typeof(Command),
                    typeof(string),
                    typeof(BufferWithViews),
                    typeof(ShaderGraphNode),
                ], "##Type", x => x.Name))
        {
        }

        FormInputs.DrawFieldSetHeader("Namespace");
        FormInputs.AddStringInput("##NameSpace", ref _activeTopic.Namespace);

        FormInputs.DrawFieldSetHeader("Description");
        _activeTopic.Description ??= string.Empty;
        CustomComponents.DrawMultilineTextEdit(ref _activeTopic.Description);

        ImGui.PopID();
    }

    private static void DrawContent()
    {
        var dl = ImGui.GetWindowDrawList();

        var mousePos = ImGui.GetMousePos();
        var mouseCell = _canvas.CellFromScreenPos(mousePos);

        var isAnyItemHovered = false;
        foreach (var topic in SkillMap.AllTopics)
        {
            isAnyItemHovered |= DrawTopicCell(dl, topic, mouseCell);
        }

        if (!isAnyItemHovered && ImGui.IsWindowHovered() && !ImGui.IsMouseDown(ImGuiMouseButton.Right))
        {
            DrawHoveredEmptyCell(dl, mouseCell);
        }
    }

    private static void DrawHoveredEmptyCell(ImDrawListPtr dl, HexCanvas.Cell cell)
    {
        var hoverCenter = _canvas.ScreenPosFromCell(cell);
        dl.AddNgonRotated(hoverCenter, _canvas.HexRadiusOnScreen, Color.Orange, false);

        if (ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
        {
            var newTopic = new QuestTopic
                               {
                                   Id = Guid.NewGuid(),
                                   MapCoordinate = new Vector2(cell.X, cell.Y),
                                   Title = "New topic" + SkillMap.AllTopics.Count(),
                                   ZoneId = _activeTopic?.ZoneId ?? Guid.Empty,
                                   Type = _lastType,
                                   Status = _activeTopic?.Status ?? QuestTopic.Statuses.Locked,
                                   Requirement = _activeTopic?.Requirement ?? QuestTopic.Requirements.AllInputPaths,
                               };

            var relevantZone = GetActiveZone();
            relevantZone.Topics.Add(newTopic);
            newTopic.ZoneId = relevantZone.Id;
            _activeTopic = newTopic;
            _focusTopicNameInput = true;
        }
        else if (ImGui.IsMouseClicked(ImGuiMouseButton.Left))
        {
            _activeTopic = null;
        }
    }

    /// <returns>
    /// true if under mouse
    /// </returns>
    private static bool DrawTopicCell(ImDrawListPtr dl, QuestTopic topic, HexCanvas.Cell cellUnderMouse)
    {
        var cell = new HexCanvas.Cell(topic.MapCoordinate);
        var isMouseInside = cell == cellUnderMouse;
        var isCellHovered = ImGui.IsWindowHovered() && !ImGui.IsMouseDown(ImGuiMouseButton.Right) && isMouseInside;

        var posOnScreen = _canvas.MapCoordsToScreenPos(topic.MapCoordinate);
        var radius = _canvas.HexRadiusOnScreen;

        var typeColor = TypeUiRegistry.GetTypeOrDefaultColor(topic.Type);
        dl.AddNgonRotated(posOnScreen, radius * 0.95f, typeColor.Fade(isMouseInside ? 0.3f : 0.15f));

        var isSelected = _activeTopic == topic;
        if (isSelected)
        {
            dl.AddNgonRotated(posOnScreen, radius, UiColors.StatusActivated, false);
        }

        foreach (var unlockTargetId in topic.UnlocksTopics)
        {
            if (!SkillMap.TryGetTopic(unlockTargetId, out var targetTopic))
                continue;

            var targetPos = _canvas.MapCoordsToScreenPos(targetTopic.MapCoordinate);
            var delta = posOnScreen - targetPos;
            var direction = Vector2.Normalize(delta);
            var angle = -MathF.Atan2(delta.X, delta.Y) - MathF.PI / 2;
            var fadeLine = (delta.Length() / _canvas.Scale.X).RemapAndClamp(0f, 1000f, 1, 0.06f);

            dl.AddLine(posOnScreen - direction * radius,
                       targetPos + direction * radius * 0.9f,
                       typeColor.Fade(fadeLine),
                       2);
            dl.AddNgonRotated(targetPos + direction * radius * 0.9f,
                              10 * _canvas.Scale.X,
                              typeColor.Fade(fadeLine),
                              true,
                              3,
                              startAngle: angle);
        }

        if (!string.IsNullOrEmpty(topic.Title))
        {
            var labelAlpha = _canvas.Scale.X.RemapAndClamp(0.3f, 0.8f, 0, 1);
            if (labelAlpha > 0.01f)
            {
                ImGui.PushFont(_canvas.Scale.X < 0.6f ? Fonts.FontSmall : Fonts.FontNormal);
                CustomDraw.AddWrappedCenteredText(dl, topic.Title, posOnScreen, 13, UiColors.ForegroundFull.Fade(labelAlpha));
                ImGui.PopFont();

                if (topic.Status == QuestTopic.Statuses.Locked)
                {
                    Icons.DrawIconAtScreenPosition(Icon.Locked, (posOnScreen + new Vector2(-Icons.FontSize / 2, 25f * _canvas.Scale.Y)).Floor(),
                                                   dl,
                                                   UiColors.ForegroundFull.Fade(0.4f * labelAlpha));
                }
            }
        }

        if (isCellHovered)
        {
            ImGui.BeginTooltip();
            ImGui.TextUnformatted(topic.Title);
            if (!string.IsNullOrEmpty(topic.Description))
            {
                CustomComponents.StylizedText(topic.Description, Fonts.FontSmall, UiColors.TextMuted);
            }

            ImGui.EndTooltip();

            if (!ImGui.IsMouseDown(ImGuiMouseButton.Left)) 
                return isMouseInside;
            
            
            switch (_state)
            {
                case States.Default:
                    _activeTopic = topic;
                    _lastType = topic.Type;
                    break;
                case States.LinkingItems when _activeTopic == null:
                    _state = States.Default;
                    break;
                case States.LinkingItems when _activeTopic == topic || !ImGui.IsMouseClicked(ImGuiMouseButton.Left):
                    return isMouseInside;
                case States.LinkingItems:
                {
                    if (!_activeTopic.UnlocksTopics.Remove(topic.Id))
                    {
                        _activeTopic.UnlocksTopics.Add(topic.Id);
                    }

                    if (!ImGui.GetIO().KeyShift)
                    {
                        _state = States.Default;
                    }

                    break;
                }
            }
        }

        return isMouseInside;
    }
    
    private static QuestZone GetActiveZone()
    {
        if (_activeZone != null)
            return _activeZone;

        if (_activeTopic == null)
            return SkillMap.FallbackZone;

        return SkillMap.TryGetZone(_activeTopic.Id, out var zone)
                   ? zone
                   : SkillMap.FallbackZone;
    }

    private static bool _focusTopicNameInput;
    private static Type _lastType = typeof(float);
    private static States _state;

    private static readonly HexCanvas _canvas = new();
}