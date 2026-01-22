using System;
using System.Collections.Generic;
using System.IO;
using ImGuiNET;
using Operators.Utils;
using T3.Core.Audio;
using T3.Core.IO;
using T3.Core.UserData;
using T3.Core.Utils;
using T3.Editor.Gui.Input;
using T3.Editor.Gui.Interaction.Keyboard;
using T3.Editor.Gui.Interaction.Midi;
using T3.Editor.Gui.Styling;
using T3.Editor.Gui.UiHelpers;
using T3.Editor.Skills.Data;
using T3.Editor.Skills.Training;
using T3.Editor.UiModel.Helpers;

namespace T3.Editor.Gui.Windows;

internal sealed partial class SettingsWindow : Window
{
    // Audio level meter smoothing
    private static float _smoothedGlobalLevel = 0f;
    private static float _smoothedOperatorLevel = 0f;
    private static float _smoothedSoundtrackLevel = 0f;

    private void DrawAudioPanel(ref bool changed)
    {
        FormInputs.AddSectionHeader("Audio System");
        FormInputs.AddVerticalSpace();
        
        // Global Mixer - compact version with volume and mute
        changed |= DrawMixerSection(
            "Global Mixer",
            "Volume",
            ref ProjectSettings.Config.GlobalPlaybackVolume,
            0.0f, 1.0f,
            ProjectSettings.Defaults.GlobalPlaybackVolume,
            "Affects all audio output at the global mixer level.",
            AudioMixerManager.GetGlobalMixerLevel(),
            ref _smoothedGlobalLevel,
            ref ProjectSettings.Config.GlobalMute,
            ProjectSettings.Defaults.GlobalMute,
            "Mute all audio output at the global mixer level."
        );
        AudioEngine.SetGlobalMute(ProjectSettings.Config.GlobalMute);
        
        // Operator Mixer - with volume and mute
        changed |= DrawMixerSection(
            "Operator Mixer",
            "Volume",
            ref ProjectSettings.Config.OperatorPlaybackVolume,
            0.0f, 1.0f,
            ProjectSettings.Defaults.OperatorPlaybackVolume,
            "Affects all operator audio output at the operator mixer level.",
            AudioMixerManager.GetOperatorMixerLevel(),
            ref _smoothedOperatorLevel,
            ref ProjectSettings.Config.OperatorMute,
            ProjectSettings.Defaults.OperatorMute,
            "Mute all operator audio output at the operator mixer level."
        );
        // Apply volume first, then mute (so mute can override volume if needed)
        AudioMixerManager.SetOperatorMixerVolume(ProjectSettings.Config.OperatorPlaybackVolume);
        AudioEngine.SetOperatorMute(ProjectSettings.Config.OperatorMute);
        
        // Soundtrack Mixer - compact version with volume and mute
        changed |= DrawMixerSection(
            "Soundtrack Mixer",
            "Volume",
            ref ProjectSettings.Config.SoundtrackPlaybackVolume,
            0.0f, 10f,
            ProjectSettings.Defaults.SoundtrackPlaybackVolume,
            "Limit the audio playback volume for the soundtrack",
            AudioMixerManager.GetSoundtrackMixerLevel(),
            ref _smoothedSoundtrackLevel,
            ref ProjectSettings.Config.SoundtrackMute,
            ProjectSettings.Defaults.SoundtrackMute,
            "Mute soundtrack audio only."
        );
    }
    
    /// <summary>
    /// Draws a compact mixer section with volume, level meter, and mute controls
    /// </summary>
    private static bool DrawMixerSection(
        string sectionLabel,
        string volumeLabel,
        ref float volume,
        float minVolume,
        float maxVolume,
        float defaultVolume,
        string volumeTooltip,
        float currentLevel,
        ref float smoothedLevel,
        ref bool mute,
        bool defaultMute,
        string muteTooltip)
    {
        var changed = false;
        
        ImGui.PushID(sectionLabel);
        
        // Section header - aligned to left with minimal padding
        FormInputs.AddVerticalSpace(3);
        ImGui.PushFont(Fonts.FontSmall);
        ImGui.PushStyleColor(ImGuiCol.Text, UiColors.TextMuted.Rgba);
        var leftPadding = 5 * T3Ui.UiScaleFactor;
        ImGui.SetCursorPosX(leftPadding);
        ImGui.TextUnformatted(sectionLabel.ToUpperInvariant());
        ImGui.PopStyleColor();
        ImGui.PopFont();
        FormInputs.AddVerticalSpace(2);
        
        // Volume slider and Mute checkbox on same line
        ImGui.SetCursorPosX(leftPadding);
        ImGui.AlignTextToFramePadding();
        ImGui.Text(volumeLabel);
        ImGui.SameLine();
        
        var sliderWidth = 80 * T3Ui.UiScaleFactor;
        var spacing = 10 * T3Ui.UiScaleFactor;
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 5);
        ImGui.PushStyleColor(ImGuiCol.FrameBg, UiColors.BackgroundInputField.Rgba);
        ImGui.SetNextItemWidth(sliderWidth);
        var volumeChanged = ImGui.DragFloat("##volume", ref volume, 0.01f, minVolume, maxVolume, "%.2f");
        ImGui.PopStyleColor();
        ImGui.PopStyleVar();
        
        // Clamp the value
        if (volume < minVolume) volume = minVolume;
        if (volume > maxVolume) volume = maxVolume;
        
        if (volumeChanged)
            changed = true;
        
        if (ImGui.IsItemHovered() && !string.IsNullOrEmpty(volumeTooltip))
        {
            ImGui.BeginTooltip();
            ImGui.TextUnformatted(volumeTooltip);
            ImGui.EndTooltip();
        }
        
        // Mute checkbox on same line with spacing
        ImGui.SameLine(0, spacing);
        ImGui.PushStyleColor(ImGuiCol.FrameBg, UiColors.BackgroundButton.Rgba);
        var muteChanged = ImGui.Checkbox("Mute", ref mute);
        ImGui.PopStyleColor();
        
        if (muteChanged)
            changed = true;
        
        if (ImGui.IsItemHovered() && !string.IsNullOrEmpty(muteTooltip))
        {
            ImGui.BeginTooltip();
            ImGui.TextUnformatted(muteTooltip);
            ImGui.EndTooltip();
        }
        
        // Use the standard audio level meter
        DrawAudioLevelMeter("", currentLevel, ref smoothedLevel);
        
        // Section separator
        FormInputs.AddVerticalSpace(6);
        var p = ImGui.GetCursorScreenPos();
        var width = ImGui.GetContentRegionAvail().X - 10 * T3Ui.UiScaleFactor;
        ImGui.GetWindowDrawList().AddRectFilled(
            p + new Vector2(leftPadding, 0),
            p + new Vector2(leftPadding + width, 1),
            UiColors.ForegroundFull.Fade(0.05f));
        FormInputs.AddVerticalSpace(6);
        
        ImGui.PopID();
        
        return changed;
    }
    
    /// <summary>
    /// Draws a minimal mixer section with just a level meter
    /// </summary>
    private static void DrawMixerSectionMinimal(
        string sectionLabel,
        float currentLevel,
        ref float smoothedLevel)
    {
        ImGui.PushID(sectionLabel);
        
        var leftPadding = 5 * T3Ui.UiScaleFactor;
        
        // Section header - aligned to left with minimal padding
        FormInputs.AddVerticalSpace(3);
        ImGui.PushFont(Fonts.FontSmall);
        ImGui.PushStyleColor(ImGuiCol.Text, UiColors.TextMuted.Rgba);
        ImGui.SetCursorPosX(leftPadding);
        ImGui.TextUnformatted(sectionLabel.ToUpperInvariant());
        ImGui.PopStyleColor();
        ImGui.PopFont();
        FormInputs.AddVerticalSpace(2);
        
        // Use the standard audio level meter
        DrawAudioLevelMeter("", currentLevel, ref smoothedLevel);
        
        // Section separator
        FormInputs.AddVerticalSpace(6);
        var p = ImGui.GetCursorScreenPos();
        var width = ImGui.GetContentRegionAvail().X - 10 * T3Ui.UiScaleFactor;
        ImGui.GetWindowDrawList().AddRectFilled(
            p + new Vector2(leftPadding, 0),
            p + new Vector2(leftPadding + width, 1),
            UiColors.ForegroundFull.Fade(0.05f));
        FormInputs.AddVerticalSpace(6);
        
        ImGui.PopID();
    }
}
