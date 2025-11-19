using ImGuiNET;
using T3.Core.DataTypes.Vector;
using T3.Editor.Gui.Input;
using T3.Editor.Gui.Styling;
using T3.Editor.UiModel;
using T3.Editor.UiModel.ProjectHandling;

namespace T3.Editor.Gui.MagGraph.Interaction;

/// <summary>
/// Displays and handles the navigation through tour points
/// </summary>
internal static class TourInteraction
{
    internal static void Draw(ProjectView projectView)
    {
        if (projectView.CompositionInstance == null)
            return;

        _symbolTourProgress ??= new Dictionary<Guid, int>();

        var compositionUi = projectView.CompositionInstance.Symbol.GetSymbolUi();
        if (compositionUi.TourPoints.Count == 0)
            return;

        var dl = ImGui.GetWindowDrawList();

        FormInputs.AddVerticalSpace(10);
        
        var cursorPos2 = ImGui.GetCursorScreenPos();
        ImGui.Indent(20);
        
        var timeSinceInteraction = (float)(ImGui.GetTime() - _lastClickTime);
        
        if (!_symbolTourProgress.TryGetValue(compositionUi.Symbol.Id, out var progressIndex))
        {
            progressIndex = 0;
        }

        var completed = progressIndex < 0 || progressIndex >= compositionUi.TourPoints.Count;

        if (completed)
        {
            if (CustomComponents.TransparentIconButton(Icon.HelpOutline, Vector2.Zero))
            {
                _symbolTourProgress[compositionUi.Symbol.Id] = 0;
                _lastClickTime = ImGui.GetTime();
            }

            CustomComponents.TooltipForLastItem("Start tour");
        }
        else
        {
            FormInputs.AddVerticalSpace(10);
            var point = compositionUi.TourPoints[progressIndex];
            

            // Draw tip
            ImGui.SameLine(0, 4);
            ImGui.SetNextItemWidth(300 * T3Ui.UiScaleFactor);
            ImGui.TextWrapped(point.Title);

            // Draw progress
            var tourPointsCount = compositionUi.TourPoints.Count;

            var h = ImGui.GetFrameHeight();
            cursorPos2 += new Vector2(0.5f,0.3f) * h;
            for (int dotIndex = 0; dotIndex < tourPointsCount; dotIndex++)
            {
                var pos = cursorPos2 
                          + new Vector2(0, 40 * dotIndex / (float)tourPointsCount);

                var isCurrent = dotIndex == progressIndex;
                var radius = 3 + (isCurrent ? 3/(timeSinceInteraction * 2f + 1) :0);
                
                var color = isCurrent ? UiColors.StatusActivated : UiColors.ForegroundFull.Fade(0.3f);
                dl.AddCircleFilled(pos, radius, color, 23);
            }

            // Draw graph indicator...
            if (compositionUi.ChildUis.TryGetValue(point.ChildId, out var child))
            {
                var posOnScreen = projectView.GraphView.Canvas.TransformPosition(child.PosOnCanvas);
                if (_lastCompositionId != compositionUi.Symbol.Id)
                {
                    _lastCompositionId = compositionUi.Symbol.Id;
                    _dampedCanvasPos = posOnScreen;
                }
                else
                {
                    _dampedCanvasPos = Vector2.Lerp(_dampedCanvasPos, posOnScreen, 0.1f);
                }

                var fadeCount = 4;
                var t = ImGui.GetTime();

                
                var dotRadius =  40 + (100 / (timeSinceInteraction * 0.5f + 1));
                
                for (int fadeIndex = 0; fadeIndex < fadeCount; fadeIndex++)
                {
                    var xx = (float)((t * 0.1f + fadeIndex / (float)fadeCount) % 1);
                    xx = MathF.Pow(1 - xx, 2.5f);
                    
                    dl.AddCircleFilled(_dampedCanvasPos, (1 - xx) * dotRadius, UiColors.StatusActivated.Fade(0.2f * xx));
                }
            }

            ImGui.PushStyleColor(ImGuiCol.Button, Color.Transparent.Rgba);
            ImGui.PushStyleColor(ImGuiCol.Text, UiColors.StatusActivated.Rgba);
            if (ImGui.Button("Continue"))
            {
                progressIndex++;
                _symbolTourProgress[compositionUi.Symbol.Id] = progressIndex;
                _lastClickTime = ImGui.GetTime();
            }

            ImGui.PopStyleColor(2);
        }
    }

    private static Vector2 _dampedCanvasPos;
    private static Guid _lastCompositionId;
    private static double _lastClickTime;

    public static void SetProgressIndex(Guid compositionId, int index)
    {
        _symbolTourProgress[compositionId] = index;
    }

    // -1 means completed or hidden
    private static Dictionary<Guid, int> _symbolTourProgress = new();
}