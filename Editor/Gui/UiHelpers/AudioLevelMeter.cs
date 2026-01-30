using ImGuiNET;
using T3.Editor.Gui.Input;
using T3.Editor.Gui.Styling;

namespace T3.Editor.Gui.UiHelpers;

/// <summary>
/// Provides shared audio level meter (VU meter) drawing functionality.
/// Used by SettingsWindow and PlaybackSettingsPopup.
/// </summary>
public static class AudioLevelMeter
{
    /// <summary>
    /// Draws an audio level meter with clipping indicator.
    /// </summary>
    /// <param name="label">Optional label for the meter (can be empty)</param>
    /// <param name="currentLevel">The current audio level (0.0 to 1.0+ range, values >= 1.0 trigger clipping indicator)</param>
    /// <param name="smoothedLevel">Reference to smoothed level state for decay animation</param>
    /// <param name="decayRate">Rate at which the level decays (default 2.0)</param>
    public static void Draw(string label, float currentLevel, ref float smoothedLevel, float decayRate = 2f)
    {
        var dl = ImGui.GetWindowDrawList();
        FormInputs.DrawInputLabel(label);
        
        var uniqueId = string.IsNullOrEmpty(label) ? "##levelMeter" : "##" + label + "Meter";
        ImGui.InvisibleButton(uniqueId, new Vector2(-1, ImGui.GetFrameHeight()));

        // Smooth the level (decay slower than attack)
        smoothedLevel = currentLevel > smoothedLevel 
            ? currentLevel 
            : Math.Max(currentLevel, smoothedLevel - decayRate * ImGui.GetIO().DeltaTime);

        var min = ImGui.GetItemRectMin();
        var max = ImGui.GetItemRectMax();
        var paddedWidth = (max.X - min.X) * 0.80f;
        var paddedHeight = (max.Y - min.Y) / 3f;
        max.X = min.X + paddedWidth;

        // Full gradient bar: green on left, orange on right
        dl.AddRectFilledMultiColor(
            new Vector2(min.X, min.Y + paddedHeight),
            new Vector2(min.X + paddedWidth, max.Y - paddedHeight),
            UiColors.StatusControlled, UiColors.StatusWarning,
            UiColors.StatusWarning, UiColors.StatusControlled);

        // Cover the unfilled portion (draw from level position to right edge)
        // Clamp the display level to 1.0 to prevent the gradient from bleeding into the LED area
        var clampedLevel = Math.Min(smoothedLevel, 1f);
        var levelPosition = min.X + paddedWidth * clampedLevel;
        dl.AddRectFilled(
            new Vector2(levelPosition, min.Y + paddedHeight),
            new Vector2(max.X, max.Y - paddedHeight),
            UiColors.BackgroundHover);

        // Peak/clipping LED indicator
        dl.AddRectFilled(
            new Vector2(max.X + 5f * T3Ui.UiScaleFactor, min.Y + paddedHeight),
            new Vector2(max.X + 25f * T3Ui.UiScaleFactor, max.Y - paddedHeight),
            currentLevel >= 1f ? UiColors.StatusError : UiColors.BackgroundHover);
    }
}
