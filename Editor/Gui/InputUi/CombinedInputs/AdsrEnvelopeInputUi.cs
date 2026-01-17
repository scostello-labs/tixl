using ImGuiNET;
using T3.Core.Operator;
using T3.Core.Operator.Slots;
using T3.Editor.Gui.Interaction;
using T3.Editor.Gui.Styling;
using T3.Editor.Gui.UiHelpers;
using T3.Editor.UiModel.InputsAndTypes;

namespace T3.Editor.Gui.InputUi.CombinedInputs;

/// <summary>
/// Custom UI for editing ADSR envelope parameters stored in a Vector4 (X=Attack, Y=Decay, Z=Sustain, W=Release).
/// Used via MappedType attribute on Vector4 inputs.
/// </summary>
public sealed class AdsrEnvelopeInputUi : InputValueUi<Vector4>
{
    public override IInputUi Clone()
    {
        return new AdsrEnvelopeInputUi
        {
            InputDefinition = InputDefinition,
            Parent = Parent,
            PosOnCanvas = PosOnCanvas,
            Relevancy = Relevancy,
            Size = Size,
        };
    }

    protected override InputEditStateFlags DrawEditControl(string name, Symbol.Child.Input input, ref Vector4 envelope, bool readOnly)
    {
        var cloneIfModified = input.IsDefault;
        var modified = DrawAdsrControl(ref envelope, cloneIfModified);

        if (cloneIfModified && modified.HasFlag(InputEditStateFlags.Modified))
        {
            input.IsDefault = false;
        }
        return modified;
    }

    /// <summary>
    /// Draws a compact ADSR envelope editor with visual curve display.
    /// Vector4 layout: X=Attack, Y=Decay, Z=Sustain, W=Release
    /// </summary>
    public static InputEditStateFlags DrawAdsrControl(ref Vector4 envelope, bool cloneIfModified)
    {
        var modified = InputEditStateFlags.Nothing;
        var drawList = ImGui.GetWindowDrawList();
        
        var availableWidth = ImGui.GetContentRegionAvail().X;
        var frameHeight = ImGui.GetFrameHeight();
        
        // Compact layout: envelope graph on top, parameters below
        var envelopeHeight = frameHeight * 2f;
        
        var startPos = ImGui.GetCursorScreenPos();
        var envelopeArea = new ImRect(
            startPos,
            new Vector2(startPos.X + availableWidth, startPos.Y + envelopeHeight)
        );

        // Draw envelope background
        drawList.AddRectFilled(envelopeArea.Min, envelopeArea.Max, UiColors.BackgroundFull.Fade(0.3f));
        drawList.AddRect(envelopeArea.Min, envelopeArea.Max, UiColors.BackgroundFull.Fade(0.5f));

        // Draw envelope curve
        DrawEnvelopeCurve(drawList, envelopeArea, envelope);

        // Make the envelope area interactive for dragging
        ImGui.SetCursorScreenPos(envelopeArea.Min);
        ImGui.InvisibleButton("##envelope_drag", envelopeArea.GetSize());
        
        if (ImGui.IsItemActive())
        {
            modified |= HandleEnvelopeDrag(ref envelope, envelopeArea, cloneIfModified);
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Drag to adjust: A (left), D (mid-left), S (vertical), R (right)");
        }

        // Move cursor below the envelope graph
        ImGui.SetCursorScreenPos(new Vector2(startPos.X, envelopeArea.Max.Y + 2));

        // Draw compact parameter controls
        modified |= DrawParameterRow(ref envelope, availableWidth, cloneIfModified);

        return modified;
    }

    private static void DrawEnvelopeCurve(ImDrawListPtr drawList, ImRect area, Vector4 envelope)
    {
        var attack = Math.Max(0.001f, envelope.X);
        var decay = Math.Max(0.001f, envelope.Y);
        var sustain = Math.Clamp(envelope.Z, 0f, 1f);
        var release = Math.Max(0.001f, envelope.W);
        
        const float sustainDuration = 0.2f;
        var points = SampleEnvelopeCurve(attack, decay, sustain, release, 64, sustainDuration);
        var width = area.GetWidth();
        var height = area.GetHeight();
        
        // Calculate segment positions
        var totalTime = attack + decay + sustainDuration + release;
        var attackEnd = attack / totalTime;
        var decayEnd = (attack + decay) / totalTime;
        var sustainEnd = (attack + decay + sustainDuration) / totalTime;

        // Draw segment indicators
        var attackX = area.Min.X + width * attackEnd;
        var decayX = area.Min.X + width * decayEnd;
        var sustainX = area.Min.X + width * sustainEnd;

        // Subtle segment lines
        drawList.AddLine(
            new Vector2(attackX, area.Min.Y),
            new Vector2(attackX, area.Max.Y),
            UiColors.TextMuted.Fade(0.3f), 1);
        drawList.AddLine(
            new Vector2(decayX, area.Min.Y),
            new Vector2(decayX, area.Max.Y),
            UiColors.TextMuted.Fade(0.3f), 1);
        drawList.AddLine(
            new Vector2(sustainX, area.Min.Y),
            new Vector2(sustainX, area.Max.Y),
            UiColors.TextMuted.Fade(0.3f), 1);

        // Draw sustain level line
        var sustainY = area.Max.Y - height * sustain;
        drawList.AddLine(
            new Vector2(decayX, sustainY),
            new Vector2(sustainX, sustainY),
            UiColors.StatusAnimated.Fade(0.4f), 1);

        // Draw the envelope curve
        var curvePoints = new Vector2[points.Length];
        for (var i = 0; i < points.Length; i++)
        {
            var x = area.Min.X + (float)i / (points.Length - 1) * width;
            var y = area.Max.Y - points[i] * (height - 4) - 2; // Small padding
            curvePoints[i] = new Vector2(x, y);
        }

        // Draw filled area under curve
        var fillPoints = new Vector2[points.Length + 2];
        fillPoints[0] = new Vector2(area.Min.X, area.Max.Y);
        for (var i = 0; i < points.Length; i++)
        {
            fillPoints[i + 1] = curvePoints[i];
        }
        fillPoints[^1] = new Vector2(area.Max.X, area.Max.Y);
        
        drawList.AddConvexPolyFilled(ref fillPoints[0], fillPoints.Length, UiColors.StatusAnimated.Fade(0.15f));

        // Draw curve line
        drawList.AddPolyline(ref curvePoints[0], curvePoints.Length, UiColors.StatusAnimated, ImDrawFlags.None, 2);

        // Draw labels
        var labelColor = UiColors.TextMuted;
        ImGui.PushFont(Fonts.FontSmall);
        
        // Position labels at segment centers
        var labelY = area.Max.Y - 12;
        drawList.AddText(new Vector2(area.Min.X + 2, labelY), labelColor, "A");
        drawList.AddText(new Vector2(attackX + 2, labelY), labelColor, "D");
        drawList.AddText(new Vector2(decayX + 2, labelY), labelColor, "S");
        drawList.AddText(new Vector2(sustainX + 2, labelY), labelColor, "R");
        
        ImGui.PopFont();
    }

    private static float[] SampleEnvelopeCurve(float attack, float decay, float sustain, float release, int points, float sustainDuration)
    {
        var result = new float[points];
        var totalDuration = attack + decay + sustainDuration + release;
        var releaseStartTime = attack + decay + sustainDuration;

        for (var i = 0; i < points; i++)
        {
            var t = (float)i / (points - 1) * totalDuration;
            
            if (t >= releaseStartTime)
            {
                // Release phase
                var releaseTime = t - releaseStartTime;
                result[i] = releaseTime >= release ? 0f : sustain * (1f - releaseTime / release);
            }
            else if (t < attack)
            {
                // Attack phase
                result[i] = t / attack;
            }
            else if (t < attack + decay)
            {
                // Decay phase
                var decayTime = t - attack;
                result[i] = 1f - (decayTime / decay) * (1f - sustain);
            }
            else
            {
                // Sustain phase
                result[i] = sustain;
            }
        }

        return result;
    }

    private static InputEditStateFlags HandleEnvelopeDrag(ref Vector4 envelope, ImRect area, bool cloneIfModified)
    {
        var modified = InputEditStateFlags.Nothing;
        var mousePos = ImGui.GetMousePos();
        var mouseDelta = ImGui.GetIO().MouseDelta;

        if (Math.Abs(mouseDelta.X) < 0.001f && Math.Abs(mouseDelta.Y) < 0.001f)
            return modified;

        var attack = Math.Max(0.001f, envelope.X);
        var decay = Math.Max(0.001f, envelope.Y);
        var release = Math.Max(0.001f, envelope.W);
        const float sustainDuration = 0.2f;

        // Determine which segment we're in based on mouse position
        var width = area.GetWidth();
        var relX = (mousePos.X - area.Min.X) / width;

        var totalTime = attack + decay + sustainDuration + release;
        var attackEnd = attack / totalTime;
        var decayEnd = (attack + decay) / totalTime;
        var sustainEnd = (attack + decay + sustainDuration) / totalTime;

        var sensitivity = 0.01f;
        if (ImGui.GetIO().KeyShift)
            sensitivity *= 0.1f;

        if (relX < attackEnd)
        {
            // Attack segment
            envelope.X = Math.Max(0.001f, envelope.X + mouseDelta.X * sensitivity);
            modified = InputEditStateFlags.Modified;
        }
        else if (relX < decayEnd)
        {
            // Decay segment
            envelope.Y = Math.Max(0.001f, envelope.Y + mouseDelta.X * sensitivity);
            modified = InputEditStateFlags.Modified;
        }
        else if (relX < sustainEnd)
        {
            // Sustain segment - vertical drag changes sustain level
            envelope.Z = Math.Clamp(envelope.Z - mouseDelta.Y * 0.01f, 0f, 1f);
            modified = InputEditStateFlags.Modified;
        }
        else
        {
            // Release segment
            envelope.W = Math.Max(0.001f, envelope.W + mouseDelta.X * sensitivity);
            modified = InputEditStateFlags.Modified;
        }

        return modified;
    }

    private static InputEditStateFlags DrawParameterRow(ref Vector4 envelope, float availableWidth, bool cloneIfModified)
    {
        var modified = InputEditStateFlags.Nothing;
        var paramWidth = (availableWidth - 12) / 4; // 4 parameters with small gaps

        // Store original values
        var attack = envelope.X;
        var decay = envelope.Y;
        var sustain = envelope.Z;
        var release = envelope.W;

        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(3, 0));
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(2, 2));

        // Attack
        ImGui.SetNextItemWidth(paramWidth);
        if (ImGui.DragFloat("##A", ref attack, 0.001f, 0.001f, 10f, "A:%.3f"))
        {
            modified = InputEditStateFlags.Modified;
            envelope.X = Math.Max(0.001f, attack);
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Attack time (seconds)");

        ImGui.SameLine();

        // Decay
        ImGui.SetNextItemWidth(paramWidth);
        if (ImGui.DragFloat("##D", ref decay, 0.001f, 0.001f, 10f, "D:%.3f"))
        {
            modified = InputEditStateFlags.Modified;
            envelope.Y = Math.Max(0.001f, decay);
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Decay time (seconds)");

        ImGui.SameLine();

        // Sustain
        ImGui.SetNextItemWidth(paramWidth);
        if (ImGui.DragFloat("##S", ref sustain, 0.01f, 0f, 1f, "S:%.2f"))
        {
            modified = InputEditStateFlags.Modified;
            envelope.Z = Math.Clamp(sustain, 0f, 1f);
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Sustain level (0-1)");

        ImGui.SameLine();

        // Release
        ImGui.SetNextItemWidth(paramWidth);
        if (ImGui.DragFloat("##R", ref release, 0.001f, 0.001f, 10f, "R:%.3f"))
        {
            modified = InputEditStateFlags.Modified;
            envelope.W = Math.Max(0.001f, release);
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Release time (seconds)");

        ImGui.PopStyleVar(2);

        if (ImGui.IsItemDeactivatedAfterEdit())
        {
            modified |= InputEditStateFlags.Finished;
        }

        return modified;
    }

    protected override void DrawReadOnlyControl(string name, ref Vector4 value)
    {
        ImGui.TextUnformatted($"A:{value.X:F3} D:{value.Y:F3} S:{value.Z:F2} R:{value.W:F3}");
    }
}
