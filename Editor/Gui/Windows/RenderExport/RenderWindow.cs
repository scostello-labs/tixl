#nullable enable
using System.IO;
using System.Text.RegularExpressions;
using ImGuiNET;
using T3.Core.Animation;
using T3.Core.Audio;
using T3.Core.DataTypes;
using T3.Core.DataTypes.Vector;
using T3.Core.SystemUi;
using T3.Core.UserData;
using T3.Core.Utils;
using T3.Editor.Gui.Input;
using T3.Editor.Gui.Interaction.Timing;
using T3.Editor.Gui.Styling;
using T3.Editor.Gui.UiHelpers;
using T3.Editor.Gui.Windows.Output;
using T3.Editor.UiModel.ProjectHandling;

namespace T3.Editor.Gui.Windows.RenderExport;

internal sealed class RenderWindow : Window
{
    public RenderWindow()
    {
        Config.Title = "Render To File";
    }

    private static int SoundtrackChannels()
    {
        var composition = ProjectView.Focused?.CompositionInstance;
        if (composition == null)
            return AudioEngine.GetClipSampleRate(null);

        PlaybackUtils.FindPlaybackSettingsForInstance(composition, out var instanceWithSettings, out var settings);
        if (settings.TryGetMainSoundtrack(instanceWithSettings, out var soundtrack))
            return AudioEngine.GetClipChannelCount(soundtrack);

        return AudioEngine.GetClipChannelCount(null);
    }

    private static int SoundtrackSampleRate()
    {
        var composition = ProjectView.Focused?.CompositionInstance;

        if (composition == null)
            return AudioEngine.GetClipSampleRate(null);

        PlaybackUtils.FindPlaybackSettingsForInstance(composition, out var instanceWithSettings, out var settings);
        return AudioEngine.GetClipSampleRate(settings.TryGetMainSoundtrack(instanceWithSettings, out var soundtrack)
                                                 ? soundtrack
                                                 : null);
    }

    private static void SetRenderingStarted()
    {
        IsToollRenderingSomething = true;
    }

    private static void RenderingFinished()
    {
        IsToollRenderingSomething = false;
    }

    public static bool IsToollRenderingSomething { get; private set; }

    private static void DrawTimeSetup()
    {
        FormInputs.SetIndentToParameters();
        FormInputs.AddSegmentedButtonWithLabel(ref _timeRange, "Render Range");
        ApplyTimeRange();

        FormInputs.AddVerticalSpace();

        // Convert times if reference time selection changed
        var oldTimeReference = _timeReference;

        if (FormInputs.AddSegmentedButtonWithLabel(ref _timeReference, "Defined as"))
        {
            _startTimeInBars = (float)ConvertReferenceTime(_startTimeInBars, oldTimeReference, _timeReference);
            _endTimeInBars = (float)ConvertReferenceTime(_endTimeInBars, oldTimeReference, _timeReference);
        }

        var changed = false;
        changed |= FormInputs.AddFloat($"Start in {_timeReference}", ref _startTimeInBars);
        changed |= FormInputs.AddFloat($"End in {_timeReference}", ref _endTimeInBars);
        if (changed)
        {
            _timeRange = TimeRanges.Custom;
        }

        FormInputs.AddVerticalSpace();

        // Change FPS if required
        FormInputs.AddFloat("FPS", ref _fps, 0);
        if (_fps < 0) _fps = -_fps;
        if (_fps != 0)
        {
            _startTimeInBars = (float)ConvertFps(_startTimeInBars, _lastValidFps, _fps);
            _endTimeInBars = (float)ConvertFps(_endTimeInBars, _lastValidFps, _fps);
            _lastValidFps = _fps;
        }

        var startTimeInSeconds = ReferenceTimeToSeconds(_startTimeInBars, _timeReference);
        var endTimeInSeconds = ReferenceTimeToSeconds(_endTimeInBars, _timeReference);
        _frameCount = (int)Math.Round((endTimeInSeconds - startTimeInSeconds) * _fps);

        FormInputs.AddFloat($"Resolution Factor", ref _resolutionFactor, 0.125f, 4, 0.1f, true, true,
                            "A factor applied to the output resolution of the rendered frames.");

        if (FormInputs.AddInt($"Motion Blur Samples", ref _overrideMotionBlurSamples, -1, 50, 1,
                              "This requires a [RenderWithMotionBlur] operator. Please check its documentation."))
        {
            _overrideMotionBlurSamples = _overrideMotionBlurSamples.Clamp(-1, 50);
        }
    }

    private static bool ValidateOrCreateTargetFolder(string targetFile)
    {
        var directory = Path.GetDirectoryName(targetFile);
        if (targetFile != directory && File.Exists(targetFile))
        {
            // FIXME: get a nicer popup window here...
            var result = BlockingWindow.Instance.ShowMessageBox("File exists. Overwrite?", "Render Video", "Yes", "No");
            return (result == "Yes");
        }

        if (directory == null || Directory.Exists(directory))
            return true;

        try
        {
            Directory.CreateDirectory(directory);
        }
        catch (Exception e)
        {
            Log.Warning($"Failed to create target folder '{directory}': {e.Message}");
            return false;
        }

        return true;
    }

    private static void ApplyTimeRange()
    {
        switch (_timeRange)
        {
            case TimeRanges.Custom:
                break;
            case TimeRanges.Loop:
            {
                var playback = Playback.Current; // TODO, this should be non-static eventually
                var startInSeconds = playback.SecondsFromBars(playback.LoopRange.Start);
                var endInSeconds = playback.SecondsFromBars(playback.LoopRange.End);
                _startTimeInBars = (float)SecondsToReferenceTime(startInSeconds, _timeReference);
                _endTimeInBars = (float)SecondsToReferenceTime(endInSeconds, _timeReference);
                break;
            }
            case TimeRanges.Soundtrack:
            {
                if (PlaybackUtils.TryFindingSoundtrack(out var handle, out _))
                {
                    var playback = Playback.Current; // TODO, this should be non-static eventually
                    var soundtrackClip = handle.Clip;
                    _startTimeInBars = (float)SecondsToReferenceTime(playback.SecondsFromBars(soundtrackClip.StartTime), _timeReference);
                    if (soundtrackClip.EndTime > 0)
                    {
                        _endTimeInBars = (float)SecondsToReferenceTime(playback.SecondsFromBars(soundtrackClip.EndTime), _timeReference);
                    }
                    else
                    {
                        _endTimeInBars = (float)SecondsToReferenceTime(soundtrackClip.LengthInSeconds, _timeReference);
                    }
                }

                break;
            }
            default:
                throw new ArgumentOutOfRangeException();
        }
    }

    private static double ConvertReferenceTime(double time,
                                               TimeReference oldTimeReference,
                                               TimeReference newTimeReference)
    {
        // Only convert time value if time reference changed
        if (oldTimeReference == newTimeReference) return time;

        var seconds = ReferenceTimeToSeconds(time, oldTimeReference);
        return SecondsToReferenceTime(seconds, newTimeReference);
    }

    private static double ConvertFps(double time, double oldFps, double newFps)
    {
        // Only convert FPS if values are valid
        if (oldFps == 0 || newFps == 0) return time;

        return time / oldFps * newFps;
    }

    private static double ReferenceTimeToSeconds(double time, TimeReference timeReference)
    {
        var playback = Playback.Current; // TODO, this should be non-static eventually
        switch (timeReference)
        {
            case TimeReference.Bars:
                return playback.SecondsFromBars(time);
            case TimeReference.Seconds:
                return time;
            case TimeReference.Frames:
                if (_fps != 0)
                    return time / _fps;
                else
                    return time / 60.0;
        }

        // This is an error, don't change the value
        return time;
    }

    private static double SecondsToReferenceTime(double timeInSeconds, TimeReference timeReference)
    {
        var playback = Playback.Current; // TODO, this should be non-static eventually
        switch (timeReference)
        {
            case TimeReference.Bars:
                return playback.BarsFromSeconds(timeInSeconds);
            case TimeReference.Seconds:
                return timeInSeconds;
            case TimeReference.Frames:
                if (_fps != 0)
                    return timeInSeconds * _fps;
                else
                    return timeInSeconds * 60.0;
        }

        // This is an error, don't change the value
        return timeInSeconds;
    }

    private static void SetPlaybackTimeForThisFrame()
    {
        // get playback settings
        var composition = ProjectView.Focused?.CompositionInstance;
        if (composition == null)
        {
            Log.Warning("Can't find focused composition instance.");
            return;
        }

        PlaybackUtils.FindPlaybackSettingsForInstance(composition, out var instanceWithSettings, out var settings);

        // change settings for all playback before calculating times
        Playback.Current.Bpm = settings.Bpm;
        Playback.Current.PlaybackSpeed = 0.0;
        Playback.Current.Settings = settings;
        Playback.Current.FrameSpeedFactor = _fps / 60.0;

        // set user time in secs for video playback
        double startTimeInSeconds = ReferenceTimeToSeconds(_startTimeInBars, _timeReference);
        double endTimeInSeconds = startTimeInSeconds + (_frameCount - 1) / _fps;
        var oldTimeInSecs = Playback.Current.TimeInSecs;
        Playback.Current.TimeInSecs = MathUtils.Lerp(startTimeInSeconds, endTimeInSeconds, Progress);
        var adaptedDeltaTime = Math.Max(Playback.Current.TimeInSecs - oldTimeInSecs + _timingOverhang, 0.0);

        // set user time in secs for audio playback
        if (settings.TryGetMainSoundtrack(instanceWithSettings, out var soundtrack))
            AudioEngine.UseAudioClip(soundtrack, Playback.Current.TimeInSecs);

        if (!_audioRecording)
        {
            _timingOverhang = 0.0;
            adaptedDeltaTime = 1.0 / _fps;

            Playback.Current.IsRenderingToFile = true;
            Playback.Current.PlaybackSpeed = 1.0;

            AudioRendering.PrepareRecording(Playback.Current, _fps);

            double requestedEndTimeInSeconds = ReferenceTimeToSeconds(_endTimeInBars, _timeReference);
            double actualEndTimeInSeconds = startTimeInSeconds + _frameCount / _fps;

            Log.Debug($"Requested recording from {startTimeInSeconds:0.0000} to {requestedEndTimeInSeconds:0.0000} seconds");
            Log.Debug($"Actually recording from {startTimeInSeconds:0.0000} to {actualEndTimeInSeconds:0.0000} seconds due to frame raster");
            Log.Debug($"Using {Playback.Current.Bpm} bpm");

            _audioRecording = true;
        }

        // update audio parameters, respecting looping etc.
        Playback.Current.Update();

        var bufferLengthInMs = (int)Math.Floor(1000.0 * adaptedDeltaTime);
        _timingOverhang = adaptedDeltaTime - bufferLengthInMs / 1000.0;
        _timingOverhang = Math.Max(_timingOverhang, 0.0);

        AudioEngine.CompleteFrame(Playback.Current, bufferLengthInMs / 1000.0);
    }

    private static void ReleasePlaybackTime()
    {
        AudioRendering.EndRecording(Playback.Current, _fps);

        Playback.Current.TimeInSecs = ReferenceTimeToSeconds(_endTimeInBars, _timeReference);
        Playback.Current.IsRenderingToFile = false;
        Playback.Current.PlaybackSpeed = 0.0;
        Playback.Current.FrameSpeedFactor = 1.0; // TODO: this should use current display frame rate
        Playback.Current.Update();

        _audioRecording = false;
    }

    internal override List<Window> GetInstances()
    {
        return new List<Window>();
    }

    private static bool FindIssueWithTexture(Texture2D? texture, List<SharpDX.DXGI.Format> supportedInputFormats, out string warning)
    {
        if (texture == null || texture.IsDisposed)
        {
            warning = "You have selected an operator that does not render. ";
            return true;
        }

        warning = string.Empty;
        return false;
    }

    protected override void DrawContent()
    {
        FormInputs.AddVerticalSpace(15);
        DrawTimeSetup();
        ImGui.Indent(5);
        DrawInnerContent();
    }

    private void DrawInnerContent()
    {
        var outputWindow = OutputWindow.GetPrimaryOutputWindow();
        if (outputWindow == null)
        {
            _lastHelpString = "No output view available";
            CustomComponents.HelpText(_lastHelpString);
            return;
        }

        // Get both the texture and the output type
        var mainTexture = OutputWindow.GetPrimaryOutputWindow()?.GetCurrentTexture();
        var outputType = outputWindow.ShownInstance?.Outputs.FirstOrDefault()?.ValueType;
        if (outputType != typeof(Texture2D))
        {
            _lastHelpString = outputType == null ? "The output view is empty" :
                              outputType != typeof(Texture2D) ? "Select or pin a Symbol with Texture2D output in order to render to file" : string.Empty;
            FormInputs.AddVerticalSpace(5);
            ImGui.Separator();
            FormInputs.AddVerticalSpace(5);
            ImGui.BeginDisabled();
            ImGui.Button("Start Render");
            CustomComponents.TooltipForLastItem("Only Symbols with a texture2D output can be rendered to file");
            ImGui.EndDisabled();
            CustomComponents.HelpText(_lastHelpString);
            return;
        }

        // Clear warning if texture is fine
        _lastHelpString = "Ready to render.";

        // Render Mode Selection
        FormInputs.AddVerticalSpace();
        FormInputs.AddSegmentedButtonWithLabel(ref _renderMode, "Render Mode");

        // Common Output Settings
        Int2 size = default;
        if (mainTexture != null)
        {
            var currentDesc = mainTexture.Description;
            size.Width = currentDesc.Width;
            size.Height = currentDesc.Height;
        }

        FormInputs.AddVerticalSpace();

        // Mode-Specific Settings
        DrawModeSpecificSettings(size);

        FormInputs.AddVerticalSpace(5);
        ImGui.Separator();
        FormInputs.AddVerticalSpace(5);

        // Rendering Logic
        HandleRenderingProcess(ref mainTexture, size);

        CustomComponents.HelpText(_lastHelpString);
    }

    private void DrawModeSpecificSettings(Int2 size)
    {
        if (_renderMode == RenderMode.Video)
        {
            DrawVideoSettings(size);
        }
        else // RenderMode.ImageSequence
        {
            DrawImageSequenceSettings();
        }
    }

    private void DrawVideoSettings(Int2 size)
    {
        FormInputs.AddInt("Bitrate", ref _bitrate, 0, 500000000, 1000);
        var duration = _frameCount / _fps;
        double bitsPerPixelSecond = _bitrate / (size.Width * size.Height * _fps);
        var q = GetQualityLevelFromRate((float)bitsPerPixelSecond);
        FormInputs.AddHint($"{q.Title} quality ({_bitrate * duration / 1024 / 1024 / 8:0} MB for {duration / 60:0}:{duration % 60:00}s at {size.Width}×{size.Height})");
        CustomComponents.TooltipForLastItem(q.Description);

        //FormInputs.AddStringInput("File name", ref UserSettings.Config.RenderVideoFilePath);
        //ImGui.SameLine();
        //FileOperations.DrawFileSelector(FileOperations.FilePickerTypes.None, ref UserSettings.Config.RenderVideoFilePath);
        FormInputs.AddFilePicker("File name",
                                 ref UserSettings.Config.RenderVideoFilePath,
                                 ".\\Render\\Title-v01.mp4 ",
                                 null,
                                 "Using v01 in the file name will enable auto incrementation and don't forget the .mp4 extension, I'm serious.",
                                 FileOperations.FilePickerTypes.Folder
                                );
        if (IsFilenameIncrementable())
        {
            ImGui.PushStyleVar(ImGuiStyleVar.Alpha, _autoIncrementVersionNumber ? 0.7f : 0.3f);
            FormInputs.AddCheckBox("Increment version after export", ref _autoIncrementVersionNumber);
            ImGui.PopStyleVar();
        }

        FormInputs.AddCheckBox("Export Audio (experimental)", ref _exportAudio);
    }

    private static void DrawImageSequenceSettings()
    {
        FormInputs.AddEnumDropdown(ref _fileFormat, "File Format");

        // Ensure the filename is trimmed and not empty
        if (FormInputs.AddStringInput("File name", ref UserSettings.Config.RenderSequenceFileName))
        {
            UserSettings.Config.RenderSequenceFileName = UserSettings.Config?.RenderSequenceFileName?.Trim() ?? string.Empty;
            if (string.IsNullOrEmpty(UserSettings.Config?.RenderSequenceFileName))
            {
                UserSettings.Config.RenderSequenceFileName = "output";
            }
        }

        // Add tooltip when hovering over the "File name" input field
        if (ImGui.IsItemHovered())
        {
            CustomComponents.TooltipForLastItem("Base filename for the image sequence (e.g., 'frame' for 'frame_0000.png').\n" +
                                                "Invalid characters (?, |, \", /, \\, :) will be replaced with underscores.\n" +
                                                "If empty, defaults to 'output'.");
        }

        // Use the existing UserSettings property for sequence path
        //FormInputs.AddStringInput("Output Path", ref UserSettings.Config.RenderSequenceFilePath);
        //if (ImGui.IsItemHovered())
        //{
        //    CustomComponents.TooltipForLastItem("Specify the folder where the image sequence will be saved.\n" +
        //                     "Must be a valid directory path.");
        //}
        //ImGui.SameLine();
        //FileOperations.DrawFileSelector(FileOperations.FilePickerTypes.Folder, ref UserSettings.Config.RenderSequenceFilePath);
        FormInputs.AddFilePicker("Output Folder",
                                 ref UserSettings.Config.RenderSequenceFilePath,
                                 ".\\ImageSequence ",
                                 null,
                                 "Specify the folder where the image sequence will be saved.",
                                 FileOperations.FilePickerTypes.Folder
                                );
    }

    private void HandleRenderingProcess(ref Texture2D mainTexture, Int2 size)
    {
        if (!IsExporting && !IsToollRenderingSomething)
        {
            if (ImGui.Button("Start Render"))
            {
                string targetPath = GetTargetPath();

                if (ValidateOrCreateTargetFolder(targetPath))
                {
                    StartRenderingProcess(targetPath, size);
                }
            }
        }
        else if (IsExporting)
        {
            bool success = ProcessCurrentFrame(ref mainTexture, size);
            DisplayRenderingProgress(success);
        }
    }

    private string GetTargetPath()
    {
        return _renderMode == RenderMode.Video
                   ? ResolveProjectRelativePath(UserSettings.Config.RenderVideoFilePath)
                   : ResolveProjectRelativePath(UserSettings.Config.RenderSequenceFilePath);
    }

    private string ResolveProjectRelativePath(string path)
    {
        // Handle project-relative paths for both video and image sequence modes
        var project = ProjectView.Focused?.OpenedProject;
        if (project != null && path.StartsWith('.'))
        {
            return Path.Combine(project.Package.Folder, path);
        }

        return path.StartsWith('.')
                   ? Path.Combine(UserSettings.Config.ProjectsFolder, FileLocations.RenderSubFolder, path)
                   : path;
    }

    private static void StartRenderingProcess(string targetPath, Int2 size)
    {
        IsExporting = true;
        _exportStartedTime = Playback.RunTimeInSecs;
        _frameIndex = 0;
        SetPlaybackTimeForThisFrame();

        if (_renderMode == RenderMode.Video && _videoWriter == null)
        {
            _videoWriter = new Mp4VideoWriter(targetPath, size, _exportAudio);
            _videoWriter.Bitrate = _bitrate;
            _videoWriter.Framerate = (int)_fps;
        }
        else if (_renderMode == RenderMode.ImageSequence)
        {
            _targetFolder = targetPath;
        }

        ScreenshotWriter.ClearQueue();
    }

    private static bool ProcessCurrentFrame(ref Texture2D mainTexture, Int2 size)
    {
        if (_renderMode == RenderMode.Video)
        {
            var audioFrame = AudioRendering.GetLastMixDownBuffer(1.0 / _fps);
            return SaveVideoFrameAndAdvance(ref mainTexture, ref audioFrame, SoundtrackChannels(), SoundtrackSampleRate());
        }
        else
        {
            AudioRendering.GetLastMixDownBuffer(Playback.LastFrameDuration);
            return SaveImageFrameAndAdvance(mainTexture);
        }
    }

    private static void DisplayRenderingProgress(bool success)
    {
        ImGui.ProgressBar((float)Progress, new Vector2(-1, 16 * T3Ui.UiScaleFactor));

        var currentTime = Playback.RunTimeInSecs;
        var durationSoFar = currentTime - _exportStartedTime;

        int effectiveFrameCount = _renderMode == RenderMode.Video ? _frameCount : _frameCount + 2;
        int currentFrame = _renderMode == RenderMode.Video ? GetRealFrame() : _frameIndex + 1;

        var completed = currentFrame >= effectiveFrameCount || !success;
        if (completed)
        {
            FinishRendering(success, durationSoFar);
        }
        else if (ImGui.Button("Cancel"))
        {
            _lastHelpString = $"Render cancelled after {StringUtils.HumanReadableDurationFromSeconds(durationSoFar)}";
            CleanupRendering();
        }
        else
        {
            UpdateProgressMessage(durationSoFar, currentFrame);
        }
    }

    private static void FinishRendering(bool success, double durationSoFar)
    {
        var successful = success ? "successfully" : "unsuccessfully";
        _lastHelpString = $"Render finished {successful} in {StringUtils.HumanReadableDurationFromSeconds(durationSoFar)}\n Ready to render.";

        if (_renderMode == RenderMode.Video)
            TryIncrementingFileName();
        CleanupRendering();
    }

    private static void CleanupRendering()
    {
        IsExporting = false;
        if (_renderMode == RenderMode.Video)
        {
            _videoWriter?.Dispose();
            _videoWriter = null;
        }

        ReleasePlaybackTime();
    }

    private static void UpdateProgressMessage(double durationSoFar, int currentFrame)
    {
        if (_videoWriter == null)
            return;
        
        var estimatedTimeLeft = durationSoFar / Progress - durationSoFar;
        _lastHelpString = _renderMode == RenderMode.Video
                              ? $"Saved {_videoWriter.FilePath} frame {currentFrame}/{_frameCount}  "
                              : $"Saved {ScreenshotWriter.LastFilename} frame {currentFrame}/{_frameCount}  ";
        _lastHelpString += $"{Progress * 100.0:0}%%  {StringUtils.HumanReadableDurationFromSeconds(estimatedTimeLeft)} left";
    }

    // Video-specific methods
    private static int GetRealFrame() => _frameIndex - MfVideoWriter.SkipImages;

    private static bool SaveVideoFrameAndAdvance(ref Texture2D mainTexture, ref byte[] audioFrame, int channels, int sampleRate)
    {
        if (Playback.OpNotReady)
        {
            Log.Debug("Waiting for operators to complete");
            return true;
        }

        try
        {
            var savedFrame = _videoWriter?.ProcessFrames(ref mainTexture, ref audioFrame, channels, sampleRate);
            _frameIndex++;
            SetPlaybackTimeForThisFrame();
            return true;
        }
        catch (Exception e)
        {
            _lastHelpString = e.ToString();
            IsExporting = false;
            _videoWriter?.Dispose();
            _videoWriter = null;
            ReleasePlaybackTime();
            return false;
        }
    }
    // Image sequence-specific methods

    private static string SanitizeFilename(string filename)
    {
        if (string.IsNullOrEmpty(filename))
            return "output";

        var invalidChars = Path.GetInvalidFileNameChars();
        var sanitized = filename;
        foreach (var c in invalidChars)
        {
            sanitized = sanitized.Replace(c.ToString(), "_");
        }

        return sanitized.Trim();
    }

    private static string GetFilePath()
    {
        var prefix = SanitizeFilename(UserSettings.Config.RenderSequenceFileName);
        return Path.Combine(_targetFolder, $"{prefix}_{_frameIndex:0000}.{_fileFormat.ToString().ToLower()}");
    }

    private static bool SaveImageFrameAndAdvance(Texture2D mainTexture)
    {
        try
        {
            var success = ScreenshotWriter.StartSavingToFile(mainTexture, GetFilePath(), _fileFormat);
            _frameIndex++;
            SetPlaybackTimeForThisFrame();
            return success;
        }
        catch (Exception e)
        {
            _lastHelpString = e.ToString();
            IsExporting = false;
            return false;
        }
    }

    // File path utilities
    private static readonly Regex _matchFileVersionPattern = new Regex(@"\bv(\d{2,4})\b");

    private static bool IsFilenameIncrementable(string path = null)
    {
        var filename = Path.GetFileName(path ?? UserSettings.Config.RenderVideoFilePath);
        return !string.IsNullOrEmpty(filename) && _matchFileVersionPattern.Match(filename).Success;
    }

    private static void TryIncrementingFileName()
    {
        if (!_autoIncrementVersionNumber)
            return;

        var filename = Path.GetFileName(UserSettings.Config.RenderVideoFilePath);
        if (string.IsNullOrEmpty(filename)) 
            return;

        var result = _matchFileVersionPattern.Match(filename);
        if (!result.Success) 
            return;

        var versionString = result.Groups[1].Value;
        if (!int.TryParse(versionString, out var versionNumber)) 
            return;

        var digits = versionString.Length.Clamp(2, 4);
        var newVersionString = "v" + (versionNumber + 1).ToString("D" + digits);
        var newFilename = filename.Replace("v" + versionString, newVersionString);

        var directoryName = Path.GetDirectoryName(UserSettings.Config.RenderVideoFilePath);
        UserSettings.Config.RenderVideoFilePath = directoryName == null
                                                      ? newFilename
                                                      : Path.Combine(directoryName, newFilename);
    }

    // Quality level for video
    private QualityLevel GetQualityLevelFromRate(float bitsPerPixelSecond)
    {
        QualityLevel q = default;
        for (var index = _qualityLevels.Length - 1; index >= 0; index--)
        {
            q = _qualityLevels[index];
            if (q.MinBitsPerPixelSecond < bitsPerPixelSecond)
                break;
        }

        return q;
    }

    private readonly QualityLevel[] _qualityLevels = new[]
                                                         {
                                                             new QualityLevel(0.01, "Poor", "Very low quality. Consider lower resolution."),
                                                             new QualityLevel(0.02, "Low", "Probable strong artifacts"),
                                                             new QualityLevel(0.05, "Medium", "Will exhibit artifacts in noisy regions"),
                                                             new QualityLevel(0.08, "Okay", "Compromise between filesize and quality"),
                                                             new QualityLevel(0.12, "Good", "Good quality. Probably sufficient for YouTube."),
                                                             new QualityLevel(0.5, "Very good", "Excellent quality, but large."),
                                                             new QualityLevel(1, "Reference", "Indistinguishable. Very large files."),
                                                         };

    private struct QualityLevel
    {
        public QualityLevel(double bits, string title, string description)
        {
            MinBitsPerPixelSecond = bits;
            Title = title;
            Description = description;
        }

        public readonly double MinBitsPerPixelSecond;
        public readonly string Title;
        public readonly string Description;
    }

    // State
    private static bool IsExporting
    {
        get => _isExporting;
        set
        {
            if (value) SetRenderingStarted();
            else RenderingFinished();
            _isExporting = value;
        }
    }

    private static bool _isExporting;

    private enum RenderMode
    {
        Video,
        ImageSequence
    }

    private static RenderMode _renderMode = RenderMode.Video;
    private static int _bitrate = 25000000;
    private static bool _autoIncrementVersionNumber = true;
    private static bool _exportAudio = true;
    private static Mp4VideoWriter? _videoWriter;
    private static ScreenshotWriter.FileFormats _fileFormat;
    private static string _targetFolder = string.Empty;
    private static double _exportStartedTime;
    private static string _lastHelpString = string.Empty;

    private const string PreferredInputFormatHint = "Ready to render.";

    private static double Progress => (_frameCount <= 1) ? 0 : (_frameIndex / (double)(_frameCount - 1)).Clamp(0, 1);

    private static TimeRanges _timeRange = TimeRanges.Custom;
    private static TimeReference _timeReference;
    private static float _startTimeInBars;
    private static float _endTimeInBars = 4.0f;
    private static float _fps = 60.0f;
    private static float _resolutionFactor = 1;
    private static float _lastValidFps = _fps;

    private static double _timingOverhang; // Time that could not be updated due to MS resolution (in seconds)
    private static bool _audioRecording;

    // ReSharper disable once InconsistentNaming
    internal static int OverrideMotionBlurSamples => _overrideMotionBlurSamples;
    private static int _overrideMotionBlurSamples = -1;

    private static int _frameIndex;
    private static int _frameCount;

    private enum TimeReference
    {
        Bars,
        Seconds,
        Frames
    }

    private enum TimeRanges
    {
        Custom,
        Loop,
        Soundtrack,
    }
}