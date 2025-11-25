#nullable enable
using ImGuiNET;
using T3.Core.DataTypes;
using T3.Core.Utils;
using T3.Editor.Gui.Styling;
using T3.Editor.Gui.UiHelpers;
using T3.Editor.Skills.Data;
using T3.Editor.UiModel.InputsAndTypes;

namespace T3.Editor.Skills.Ui;

internal sealed class SkillMapCanvas :HexCanvas
{
    public bool DrawContent(HandleTopicInteraction? topicAction, out HexCanvas.Cell mouseCell, HashSet<QuestTopic> selection)
    {
        UpdateCanvas(out _);
        
        var dl = ImGui.GetWindowDrawList();

        var mousePos = ImGui.GetMousePos();
        mouseCell = CellFromScreenPos(mousePos);

        var isAnyItemHovered = false;
        foreach (var topic in SkillMapData.Data.Topics)
        {
            isAnyItemHovered |= DrawTopicCell(dl, topic, mouseCell, selection, topicAction);
        }

        return isAnyItemHovered;
    }

    public void FocusToActiveTopics(HashSet<QuestTopic> selection)
    {
        if (selection.Count == 0)
            return;
        
        var isFirst = true;
        ImRect canvasArea = new ImRect();
        foreach (var topic in selection)
        {
            var posOnCanvas = CanvasPosFromCell(topic.Cell);
            if (isFirst)
            {
                canvasArea= ImRect.RectWithSize(posOnCanvas, Vector2.One);
                isFirst = false;
            }
            else
            {
                canvasArea.Add(posOnCanvas);
            }
        }
        
        canvasArea.Expand(200);
        FitAreaOnCanvas(canvasArea);
    }

    /// <returns>
    /// return true if hovered
    /// </returns>
    private bool DrawTopicCell(ImDrawListPtr dl, QuestTopic topic, HexCanvas.Cell cellUnderMouse, HashSet<QuestTopic> selection,
                                      HandleTopicInteraction? topicAction)
    {
        var cell = new HexCanvas.Cell(topic.MapCoordinate);

        var isHovered = ImGui.IsWindowHovered() && !ImGui.IsMouseDown(ImGuiMouseButton.Right) && cell == cellUnderMouse;

        var posOnScreen = MapCoordsToScreenPos(topic.MapCoordinate);
        var radius = HexRadiusOnScreen;

        var type = topic.TopicType switch
                       {
                           QuestTopic.TopicTypes.Image       => typeof(Texture2D),
                           QuestTopic.TopicTypes.Numbers     => typeof(float),
                           QuestTopic.TopicTypes.Command     => typeof(Command),
                           QuestTopic.TopicTypes.String      => typeof(string),
                           QuestTopic.TopicTypes.Gpu         => typeof(BufferWithViews),
                           QuestTopic.TopicTypes.ShaderGraph => typeof(ShaderGraphNode),
                           _                                 => throw new ArgumentOutOfRangeException()
                       };

        var typeColor = TypeUiRegistry.GetTypeOrDefaultColor(type);
        dl.AddNgonRotated(posOnScreen, radius * 0.95f, typeColor.Fade(isHovered ? 0.3f : 0.15f));

        var isSelected = selection.Contains(topic);
        if (isSelected)
        {
            dl.AddNgonRotated(posOnScreen, radius, UiColors.StatusActivated, false);
        }

        foreach (var unlockTargetId in topic.UnlocksTopics)
        {
            if (!SkillMapData.TryGetTopic(unlockTargetId, out var targetTopic))
                continue;

            var targetPos = MapCoordsToScreenPos(targetTopic.MapCoordinate);
            var delta = posOnScreen - targetPos;
            var direction = Vector2.Normalize(delta);
            var angle = -MathF.Atan2(delta.X, delta.Y) - MathF.PI / 2;
            var fadeLine = (delta.Length() / Scale.X).RemapAndClamp(0f, 1000f, 1, 0.06f);

            dl.AddLine(posOnScreen - direction * radius * 0.83f,
                       targetPos + direction * radius * 0.83f,
                       typeColor.Fade(fadeLine),
                       2);
            dl.AddNgonRotated(targetPos + direction * radius * 0.83f,
                              10 * Scale.X,
                              typeColor.Fade(fadeLine),
                              true,
                              3,
                              startAngle: angle);
        }

        if (!string.IsNullOrEmpty(topic.Title))
        {
            var labelAlpha = Scale.X.RemapAndClamp(0.3f, 0.8f, 0, 1);
            if (labelAlpha > 0.01f)
            {
                ImGui.PushFont(Scale.X < 0.6f ? Fonts.FontSmall : Fonts.FontNormal);
                CustomImguiDraw.AddWrappedCenteredText(dl, topic.Title, posOnScreen, 13, UiColors.ForegroundFull.Fade(labelAlpha));
                ImGui.PopFont();

                if (topic.Status == QuestTopic.Statuses.Locked)
                {
                    Icons.DrawIconAtScreenPosition(Icon.Locked, (posOnScreen + new Vector2(-Icons.FontSize / 2, 25f * Scale.Y)).Floor(),
                                                   dl,
                                                   UiColors.ForegroundFull.Fade(0.4f * labelAlpha));
                }
            }
        }

        if (!isHovered)
            return isHovered;

        // Mouse interactions ----------------

        ImGui.BeginTooltip();
        ImGui.TextUnformatted(topic.Title);
        if (!string.IsNullOrEmpty(topic.Description))
        {
            CustomComponents.StylizedText(topic.Description, Fonts.FontSmall, UiColors.TextMuted);
        }

        ImGui.EndTooltip();

        topicAction?.Invoke(topic, isSelected);
        return isHovered;
    }

    internal delegate void HandleTopicInteraction(QuestTopic topic, bool isSelected);
}