using ImGuiNET;
using T3.Core.DataTypes.Vector;
using T3.Core.Operator;
using T3.Editor.Gui.Input;
using T3.Editor.Gui.MagGraph.Interaction;
using T3.Editor.Gui.Styling;
using T3.Editor.UiModel;
using T3.Editor.UiModel.Modification;
using T3.Editor.UiModel.ProjectHandling;
using T3.Editor.UiModel.Selection;

namespace T3.Editor.Gui.Graph.Dialogs;

internal static class EditTourPointsDialog
{
    private static bool _isOpen;

    internal static void ShowNextFrame()
    {
        _isOpen = true;
    }

    internal static ChangeSymbol.SymbolModificationResults Draw(Symbol operatorSymbol, ProjectView projectView)
    {
        var result = ChangeSymbol.SymbolModificationResults.Nothing;

        if (!_isOpen)
            return ChangeSymbol.SymbolModificationResults.Nothing;

        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 1);
        if (ImGui.Begin("Edit tour points", ref _isOpen))
        {
            ImGui.PushStyleColor(ImGuiCol.ChildBg, UiColors.WindowBackground.Fade(0.8f).Rgba);

            ImGui.BeginChild("Inner", Vector2.Zero, false, ImGuiWindowFlags.NoMove);
            {
                _compositionUi = operatorSymbol.GetSymbolUi();

                // Handle selection
                _selectedChildIds.Clear();
                _firstSelectedChildId = Guid.Empty;
                ImGui.Indent(20);
                FormInputs.AddVerticalSpace(20);

                foreach (var c in projectView.NodeSelection.GetSelectedChildUis())
                {
                    if (_firstSelectedChildId == Guid.Empty)
                        _firstSelectedChildId = c.Id;

                    _selectedChildIds.Add(c.Id);
                }

                var modified = false;
                _completedDragging = false;

                // List...
                if (!_isDragging && _listOrderWhileDragging.Count != _compositionUi.TourPoints.Count)
                {
                    _listOrderWhileDragging.Clear();
                    for (var index = 0; index < _compositionUi.TourPoints.Count; index++)
                    {
                        _listOrderWhileDragging.Add(index);
                    }
                }

                for (var index = 0; index < _compositionUi.TourPoints.Count; index++)
                {
                    var tourPoint = _compositionUi.TourPoints[index];

                    ImGui.PushID(tourPoint.Id.GetHashCode());
                    // Draw floating + button...
                    {
                        var keepCursorPos = ImGui.GetCursorPos();
                        var h = ImGui.GetFrameHeight();
                        ImGui.SetCursorPos(keepCursorPos - new Vector2(h, h * 0.5f));

                        if (CustomComponents.TransparentIconButton(Icon.Plus, Vector2.Zero, CanAdd
                                                                                                ? CustomComponents.ButtonStates.Normal
                                                                                                : CustomComponents.ButtonStates.Disabled) && CanAdd)
                        {
                            InsertNewTourPoint(index);
                        }

                        ImGui.SetCursorPos(keepCursorPos);
                    }

                    modified |= DrawItem(tourPoint, index);
                    FormInputs.AddVerticalSpace(2);

                    ImGui.PopID();
                }

                if (_completedDragging)
                {
                    _isDragging = false;
                    _listOrderWhileDragging.Clear();
                    modified = true;
                }

                if (CustomComponents.DisablableButton("Add Tour Point", CanAdd))
                {
                    InsertNewTourPoint(_compositionUi.TourPoints.Count);
                }

                if (modified)
                    _compositionUi.FlagAsModified();
            }

            ImGui.EndChild();
            ImGui.PopStyleColor();
        }

        ImGui.PopStyleColor();

        ImGui.End();
        ImGui.PopStyleVar();
        return result;
    }

    private static bool DrawItem(TourPoint tourPoint, int index)
    {
        var modified = false;

        var isSelected = _selectedChildIds.Contains(tourPoint.ChildId);

        ImGui.PushStyleColor(ImGuiCol.ChildBg, isSelected
                                                   ? Color.Mix(UiColors.BackgroundButton, UiColors.BackgroundActive, 0.1f).Rgba
                                                   : UiColors.BackgroundButton);

        ImGui.PushStyleVar(ImGuiStyleVar.ChildRounding, 5);
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(5, 5)); // inner spacing
        ImGui.BeginChild("item", new Vector2(-5, 100 * T3Ui.UiScaleFactor), true);
        {
            FormInputs.AddVerticalSpace(3);
            var padding = 10;
            ImGui.PushFont(Fonts.FontSmall);
            {
                ImGui.Indent(6);
                ImGui.SetNextItemWidth(10);
                ImGui.AlignTextToFramePadding();
                ImGui.PushStyleColor(ImGuiCol.Text, UiColors.TextMuted.Rgba);
                ImGui.PushStyleColor(ImGuiCol.Button, Color.Transparent.Rgba);

                var dragIndex = _isDragging
                                    ? _listOrderWhileDragging[index]
                                    : index;

                ImGui.Button($"{dragIndex + 1}.");
                ImGui.PopStyleColor(2);

                if (ImGui.IsItemActive())
                {
                    _isDragging = true;
                    var itemMin = ImGui.GetWindowPos();
                    var itemMax = ImGui.GetWindowPos() + ImGui.GetWindowSize();
                    ImGui.GetForegroundDrawList().AddRect(itemMin, itemMax, Color.Gray);

                    var mouseY = ImGui.GetMousePos().Y;
                    var halfHeight = ImGui.GetWindowSize().Y / 2;
                    var indexDelta = 0;
                    if (mouseY < itemMin.Y - halfHeight && index > 0)
                    {
                        indexDelta = -1;
                    }
                    else if (mouseY > itemMax.Y + halfHeight && index < _compositionUi.TourPoints.Count - 1)
                    {
                        indexDelta = 1;
                    }

                    if (indexDelta != 0)
                    {
                        var newIndex = index + indexDelta;
                        if (newIndex >= 0 && index < _compositionUi.TourPoints.Count && newIndex < _compositionUi.TourPoints.Count)
                        {
                            (_compositionUi.TourPoints[newIndex], _compositionUi.TourPoints[index]) =
                                (_compositionUi.TourPoints[index], _compositionUi.TourPoints[newIndex]);
                            (_listOrderWhileDragging[newIndex], _listOrderWhileDragging[index]) =
                                (_listOrderWhileDragging[index], _listOrderWhileDragging[newIndex]);
                        }
                    }
                }

                if (ImGui.IsItemDeactivated())
                {
                    _completedDragging = true;
                }

                ImGui.SameLine(0, padding);

                ImGui.SetNextItemWidth(120);
                modified |= FormInputs.DrawEnumDropdown(ref tourPoint.Style, "style");

                ImGui.SameLine(0, padding);
                ImGui.TextUnformatted("on");
                ImGui.SameLine(0, padding);

                if (_compositionUi.ChildUis.TryGetValue(tourPoint.ChildId, out var childUi))
                {
                    if (ImGui.Button(childUi.SymbolChild.ReadableName))
                    {
                        var comp = ProjectView.Focused!.CompositionInstance;
                        if (comp != null && comp.Children.TryGetChildInstance(tourPoint.ChildId, out var instance))
                        {
                            ProjectView.Focused!.GraphView.OpenAndFocusInstance(instance.InstancePath);
                        }
                    }

                    if (ImGui.IsItemHovered())
                    {
                        FrameStats.AddHoveredId(tourPoint.ChildId);
                    }
                }
                else
                {
                    if (ImGui.Button("Op missing?"))
                    {
                    }

                    CustomComponents.TooltipForLastItem("" + tourPoint.ChildId);
                }

                var x = ImGui.GetContentRegionAvail().X - ImGui.GetFrameHeight();
                ImGui.SameLine(x);
            }
            ImGui.PopFont();

            if (CustomComponents.TransparentIconButton(Icon.Trash, Vector2.Zero, CustomComponents.ButtonStates.Dimmed))
            {
                _compositionUi.TourPoints.Remove(tourPoint);
                modified = true;
            }

            if (_editedTourPointId == tourPoint.Id)
            {
                if (ImGui.InputTextMultiline("##Text", ref tourPoint.Title, 16384, new Vector2(-10, 60) * T3Ui.UiScaleFactor))
                {
                    modified = true;
                }

                if (ImGui.IsItemDeactivated())
                {
                    _editedTourPointId = Guid.Empty;
                }
            }
            else
            {
                var text = string.IsNullOrEmpty(tourPoint.Title) ? "Add description..." : tourPoint.Title;

                ImGui.TextWrapped(text);
                if (ImGui.IsItemClicked())
                {
                    _editedTourPointId = tourPoint.Id;
                }
            }

            ImGui.Unindent(10);

            // Activate tour point
            if (ImGui.IsWindowHovered() && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
            {
                TourInteraction.SetProgressIndex(_compositionUi.Symbol.Id, index);
            }
        }
        ImGui.EndChild();
        ImGui.PopStyleVar(2);
        ImGui.PopStyleColor();

        return modified;
    }

    private static void InsertNewTourPoint(int index)
    {
        _compositionUi.TourPoints.Insert(index, new TourPoint
                                                    {
                                                        Title = string.Empty,
                                                        Id = Guid.NewGuid(),
                                                        ChildId = _firstSelectedChildId,
                                                        Style = TourPoint.Styles.Comment
                                                    });
    }

    private static bool CanAdd => _selectedChildIds.Count == 1;

    private static bool _isDragging;
    private static readonly List<int> _listOrderWhileDragging = [];
    private static bool _completedDragging;

    private static Guid _editedTourPointId;
    private static SymbolUi _compositionUi;
    private static Guid _firstSelectedChildId;
    private static readonly HashSet<Guid> _selectedChildIds = [];
}