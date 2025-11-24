#nullable enable

using ImGuiNET;
using T3.Core.DataTypes;
using T3.Core.DataTypes.Vector;
using T3.Core.Utils;
using T3.Editor.Gui;
using T3.Editor.Gui.Input;
using T3.Editor.Gui.Interaction;
using T3.Editor.Gui.Styling;
using T3.Editor.Gui.UiHelpers;
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
        //var result = ChangeSymbol.SymbolModificationResults.Nothing;

        if (!_isOpen)
            return;

        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 1);
        ImGui.SetNextWindowSize(new Vector2(500,500) * T3Ui.UiScaleFactor, ImGuiCond.Once); 
        if (ImGui.Begin("Edit skill map", ref _isOpen))
        {
            if (ImGui.IsWindowAppearing())
            {
                InitMap();
            }

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

            //ImGui.PushStyleColor(ImGuiCol.ChildBg, UiColors.WindowBackground.Fade(0.8f).Rgba);
            ImGui.BeginChild("Inner", new Vector2(-200, 0), false, ImGuiWindowFlags.NoMove);
            {
                if (ImGui.Button("Update"))
                {
                    InitMap();
                }
                ImGui.SameLine();

                if (ImGui.Button("Save"))
                {
                    SkillMap.Save();
                }

                var dl = ImGui.GetWindowDrawList();
                _canvas.UpdateCanvas(out _);
                // var c = _canvas.TransformPosition(Vector2.Zero);
                // var d = _canvas.TransformDirection(Vector2.One);

                DrawContent();
            }

            ImGui.EndChild();
            //ImGui.PopStyleColor();
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

    private static Guid _draggedTopicId;

    private static void InitMap()
    {
        _gridTopics.Clear();
        foreach (var topic in SkillMap.AllTopics)
        {
            _gridTopics[topic.MapCellHash] = topic;
        }
    }

    private static States State;
    
    private enum States
    {
        Default,
        SelectingUnlocked,
    }
    
    private static void DrawSidebar()
    {
        if (_activeTopic == null)
            return;

        var isSelectingUnlocked = State == States.SelectingUnlocked;

        if (CustomComponents.ToggleIconButton(ref isSelectingUnlocked, Icon.ConnectedOutput, Vector2.Zero))
        {
            State = isSelectingUnlocked ? States.SelectingUnlocked : States.Default;
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
                ], "##Type", x => x.Name))
        {
        }
        
        FormInputs.DrawFieldSetHeader("Namespace");
        FormInputs.AddStringInput("##NameSpace", ref _activeTopic.Namespace);

        FormInputs.DrawFieldSetHeader("Description");
        //FormInputs.AddStringInput("##Description", ref _activeTopic.Description);
        _activeTopic.Description ??= string.Empty;
        DrawMultilineTextEdit(ref _activeTopic.Description);
        
        ImGui.PopID();
    }

    
    private static bool DrawMultilineTextEdit(ref string value)
    {
        var lineCount = value.LineCount().Clamp(1, 30) + 1;
        var lineHeight = Fonts.Code.FontSize;
        ImGui.PushStyleColor(ImGuiCol.FrameBg, UiColors.BackgroundInputField.Rgba);
        var requestedContentHeight = lineCount * lineHeight;
        var clampedToWindowHeight = MathF.Min(requestedContentHeight, ImGui.GetWindowSize().Y * 0.5f);
        
        var changed = ImGui.InputTextMultiline("##textEdit", ref value, 16384, new Vector2(-10, clampedToWindowHeight));
        ImGui.PopStyleColor();
        FormInputs.AddVerticalSpace(3);
        return changed;
    }

    private static void DrawItem(ImDrawListPtr dl, float rOnScreen, int x, int y)
    {
        var cellHash = y * 16384 + x;
        var posOnScreen = ScreenPosForCell(x, y);

        var mousePos = ImGui.GetMousePos();
        var radius = rOnScreen * 0.56f * BaseScale;
        var isMouseInside = Vector2.Distance(posOnScreen, mousePos) < radius * 0.7f && !ImGui.IsMouseDown(ImGuiMouseButton.Right);
        var isCellHovered = ImGui.IsWindowHovered() && !ImGui.IsMouseDown(ImGuiMouseButton.Right) && isMouseInside;

        _gridTopics.TryGetValue(cellHash, out var topic);

        // Handle empty cell
        if (topic == null)
        {
            if (!isCellHovered)
                return;

            var hoverColor = topic == null ? UiColors.ForegroundFull.Fade(0.05f) : Color.Orange;
            dl.AddNgonRotated(posOnScreen, radius, hoverColor.Fade(isMouseInside ? 0.3f : 0.15f));

            
            if (ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
            {
                var newTopic = new QuestTopic
                                   {
                                       Id = Guid.NewGuid(),
                                       MapCoordinate = new Vector2(x, y),
                                       Title = "New topic" + SkillMap.AllTopics.Count(),
                                       ZoneId = _activeTopic?.ZoneId ?? Guid.Empty,
                                       Type = _activeTopic?.Type ?? typeof(Texture2D),
                                       Status = _activeTopic?.Status ?? QuestTopic.Statuses.Locked,
                                       Requirement = _activeTopic?.Requirement ?? QuestTopic.Requirements.AllInputPaths,
                                   };

                var relevantZone = GetActiveZone();
                relevantZone.Topics.Add(newTopic);
                newTopic.ZoneId = relevantZone.Id;

                _gridTopics[cellHash] = newTopic;

                _activeTopic = newTopic;
                _focusTopicNameInput = true;
            }
            else if (ImGui.IsMouseClicked(ImGuiMouseButton.Left))
            {
                _activeTopic = null;
            }
            else if (ImGui.IsMouseDragging(ImGuiMouseButton.Left) && _draggedTopicId != Guid.Empty)
            {
                if (_activeTopic?.Id == _draggedTopicId)
                {
                    _gridTopics.Remove(_activeTopic.MapCellHash);
                    _activeTopic.MapCoordinate = new Vector2(x, y);
                    _gridTopics[_activeTopic.MapCellHash] = _activeTopic;
                }
            }

            return;
        }

        // Existing topic....
        var typeColor = TypeUiRegistry.GetTypeOrDefaultColor(topic.Type);
        dl.AddNgonRotated(posOnScreen, radius, typeColor.Fade(isMouseInside ? 0.3f : 0.15f));
        
        var isSelected = _activeTopic == topic;
        if (isSelected)
        {
            dl.AddNgonRotated(posOnScreen, radius, UiColors.StatusActivated, false);
        }

        foreach (var unlockTargetId in topic.UnlocksTopics)
        {
            if (SkillMap.TryGetTopic(unlockTargetId, out var target))
            {
                var targetPos = ScreenPosForTopic(target);
                var delta =  posOnScreen - targetPos;
                var direction = Vector2.Normalize(delta);
                var angle = -MathF.Atan2(delta.X, delta.Y) -MathF.PI/2;
                dl.AddLine(posOnScreen - direction * radius, 
                           targetPos + direction * radius*0.9f, 
                           typeColor, 
                           2);
                dl.AddNgonRotated(targetPos + direction * radius*0.9f, 
                                  10 * _canvas.Scale.X, 
                                  typeColor, 
                                  true, 
                                  3, 
                                  startAngle:angle );
            }
        }

        if (!string.IsNullOrEmpty(topic.Title))
        {
            var labelAlpha = _canvas.Scale.X.RemapAndClamp(0.3f, 2f, 0, 1);
            if (labelAlpha > 0.01f)
            {
                AddWrappedCenteredText(dl, topic.Title, posOnScreen, 13, UiColors.ForegroundFull.Fade(labelAlpha));
            }
        }

        if (topic.Status == QuestTopic.Statuses.Locked)
        {
            Icons.DrawIconAtScreenPosition(Icon.RotateClockwise, posOnScreen);
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

            if (ImGui.IsMouseDown(ImGuiMouseButton.Left))
            {
                if (State == States.Default)
                {
                    _activeTopic = topic;
                    _draggedTopicId = topic.Id;
                }
                else if (State == States.SelectingUnlocked)
                {
                    if (_activeTopic == null)
                    {
                        State = States.Default;
                    }
                    else
                    {
                        if (_activeTopic != topic && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
                        {
                            if (_activeTopic.UnlocksTopics.Contains(topic.Id))
                            {
                                _activeTopic.UnlocksTopics.Remove(topic.Id);
                            }
                            else
                            {
                                _activeTopic.UnlocksTopics.Add(topic.Id);
                            }
                        }
                    }
                }
            }
        }
    }

    private static Vector2 ScreenPosForTopic(QuestTopic target)
    {
        var targetPos = ScreenPosForCell((int)target.MapCoordinate.X, (int)target.MapCoordinate.Y);
        return targetPos;
    }

    private static Vector2 ScreenPosForCell(int x, int y)
    {
        var offSetX = (y % 2) * 0.5f;
        var posOnScreen = _canvas.TransformPosition(new Vector2(x + offSetX, y * 0.87f) * BaseScale);
        return posOnScreen;
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

    private const float BaseScale = 136;

    private static bool _focusTopicNameInput;
    private static readonly Dictionary<int, QuestTopic> _gridTopics = new();

    private static void DrawContent()
    {
        var dl = ImGui.GetWindowDrawList();

        var rOnScreen = _canvas.TransformDirection(Vector2.One).X;
        for (int x = -15; x < 15; x++)
        {
            for (int y = -15; y < 15; y++)
            {
                DrawItem(dl, rOnScreen, x, y);
            }
        }

        if (ImGui.IsMouseDown(ImGuiMouseButton.Left))
        {
        }

        if (!ImGui.IsMouseDown(ImGuiMouseButton.Left))
        {
            _draggedTopicId = Guid.Empty;
        }

        DrawBackgroundGrids(dl);
    }

    private static void DrawBackgroundGrids(ImDrawListPtr drawList)
    {
        var minSize = MathF.Min(10, 10);
        var gridSize = Vector2.One * minSize;
        var maxOpacity = 0.25f;

        var fineGrid = _canvas.Scale.X.RemapAndClamp(0.5f, 2f, 0.0f, maxOpacity);
        if (fineGrid > 0.01f)
        {
            var color = UiColors.CanvasGrid.Fade(fineGrid);
            DrawBackgroundGrid(drawList, gridSize, color);
        }

        var roughGrid = _canvas.Scale.X.RemapAndClamp(0.1f, 2f, 0.0f, maxOpacity);
        if (roughGrid > 0.01f)
        {
            var color = UiColors.CanvasGrid.Fade(roughGrid);
            DrawBackgroundGrid(drawList, gridSize * 5, color);
        }
    }

    private static void DrawBackgroundGrid(ImDrawListPtr drawList, Vector2 gridSize, Color color)
    {
        var window = new ImRect(_canvas.WindowPos, _canvas.WindowPos + _canvas.WindowSize);

        var topLeftOnCanvas = _canvas.InverseTransformPositionFloat(_canvas.WindowPos);
        var alignedTopLeftCanvas = new Vector2((int)(topLeftOnCanvas.X / gridSize.X) * gridSize.X,
                                               (int)(topLeftOnCanvas.Y / gridSize.Y) * gridSize.Y);

        var topLeftOnScreen = _canvas.TransformPosition(alignedTopLeftCanvas);
        var screenGridSize = _canvas.TransformDirection(gridSize);

        var count = new Vector2(window.GetWidth() / screenGridSize.X, window.GetHeight() / screenGridSize.Y);

        for (int ix = 0; ix < 200 && ix <= count.X + 1; ix++)
        {
            var x = (int)(topLeftOnScreen.X + ix * screenGridSize.X);
            drawList.AddRectFilled(new Vector2(x, window.Min.Y),
                                   new Vector2(x + 1, window.Max.Y),
                                   color);
        }

        for (int iy = 0; iy < 200 && iy <= count.Y + 1; iy++)
        {
            var y = (int)(topLeftOnScreen.Y + iy * screenGridSize.Y);
            drawList.AddRectFilled(new Vector2(window.Min.X, y),
                                   new Vector2(window.Max.X, y + 1),
                                   color);
        }
    }

    private static readonly Vector2[] _pointsForNgon = new Vector2[MaxNgonCorners];
    private const int MaxNgonCorners = 8;

    private static void AddNgonRotated(this ImDrawListPtr dl, Vector2 center, float radius, uint color, bool filled = true, int count =6, float startAngle= -MathF.PI / 2f)
    {
        count = count.ClampMax(MaxNgonCorners);

        for (var i = 0; i < count; i++)
        {
            var a = startAngle + i * (2*MathF.PI / count); 
            _pointsForNgon[i] = new Vector2(
                                               center.X + MathF.Cos(a) * radius,
                                               center.Y + MathF.Sin(a) * radius
                                              );
        }

        if (filled)
        {
            dl.AddConvexPolyFilled(ref _pointsForNgon[0], count, color);
        }
        else
        {
            dl.AddPolyline(ref _pointsForNgon[0], count, color, ImDrawFlags.Closed, 2);
        }
    }

    private static readonly int[] _wrapLineIndices = new int[10];  
    
    // The method now accepts a Span of ReadOnlySpans for the wrapped lines to avoid allocations
    public static void AddWrappedCenteredText(ImDrawListPtr dl, string text, Vector2 position, int wrapCharCount, Color color)
    {
        var textLength = text.Length;
        var currentLineStart = 0;
        var lineCount = 0;

        // Step 1: Calculate wrap indices
        while (currentLineStart < textLength && lineCount < _wrapLineIndices.Length)
        {
            var lineEnd = currentLineStart + wrapCharCount;

            if (lineEnd > textLength)
            {
                _wrapLineIndices[lineCount] = currentLineStart;
                lineCount++;
                break;
            }

            // Search backwards to find the last space or punctuation within the wrap length
            var wrapPoint = (lineEnd-1).ClampMin(0);
            while (wrapPoint > 0 && wrapPoint > currentLineStart && !IsValidLineBreakCharacter(text[wrapPoint]))
            {
                wrapPoint--;
            }

            if (wrapPoint == currentLineStart)
            {
                wrapPoint = lineEnd; // Force wrap at max length if no valid break found
            }

            _wrapLineIndices[lineCount] = currentLineStart;
            currentLineStart = wrapPoint;
            lineCount++;
        }

        // Step 2: Draw wrapped text centered horizontally and vertically
        var lineHeight = ImGui.GetTextLineHeight();
        var totalHeight = lineHeight * lineCount;
        var yStart = position.Y - totalHeight / 2.0f; // Center vertically

        for (var i = 0; i < lineCount; i++)
        {
            // Calculate the slice for the line using the stored indices
            var startIdx = _wrapLineIndices[i];
            var endIdx = (i + 1 < lineCount) ? _wrapLineIndices[i + 1] : text.Length;
            var lineSpan = text.AsSpan(startIdx, endIdx - startIdx); // Slice the original text

            var textWidth = ImGui.CalcTextSize(lineSpan).X;
            var xStart = position.X - textWidth / 2.0f; // Center horizontally

            // Draw the line at the correct position
            dl.AddText(new Vector2(xStart, yStart + i * lineHeight), color, lineSpan);
        }
    }

    // Step 5: Check if a character is a valid line break character (space, special characters, etc.)
    private static bool IsValidLineBreakCharacter(char c)
    {
        // Consider spaces, hyphens, periods, commas, semicolons, etc., as line break characters.
        return char.IsWhiteSpace(c) || c == '-' || c == '.' || c == ',' || c == ';' || c == '!' || c == '?';
    }


    
    
    private static readonly ScalableCanvas _canvas = new();
}