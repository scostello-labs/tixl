#nullable enable
using System;
using System.Collections.Generic;
using ManagedBass;
using ManagedBass.Mix;
using T3.Core.Animation;
using T3.Core.IO;
using T3.Core.Logging;
using T3.Core.Operator;

namespace T3.Core.Audio;

/// <summary>
/// Handles audio rendering/export functionality.
/// For export, we temporarily remove soundtrack streams from the mixer and read directly from them.
/// </summary>
public static class AudioRendering
{
    private static bool _isRecording;
    private static readonly ExportState _exportState = new();
    private static double _exportStartTime;
    private static int _frameCount;
    private static bool _warnedAboutExternalAudio;

    public static void PrepareRecording(Playback playback, double fps)
    {
        if (_isRecording) return;

        _isRecording = true;
        _frameCount = 0;
        _exportStartTime = playback.TimeInSecs;
        _warnedAboutExternalAudio = false;

        // Check if we're in external audio mode and warn the user
        if (playback.Settings?.AudioSource == Operator.PlaybackSettings.AudioSources.ExternalDevice)
        {
            Log.Warning("[AudioRendering] External audio source detected - external audio cannot be monitored during export. Only operator audio will be included in the export.");
            _warnedAboutExternalAudio = true;
        }

        _exportState.SaveState();
        AudioExportSourceRegistry.Clear();

        // Reset export buffers for clean state
        WaveFormProcessing.ResetExportBuffer();

        Bass.ChannelPause(AudioMixerManager.GlobalMixerHandle);
        AudioConfig.LogAudioRenderDebug("[AudioRendering] GlobalMixer PAUSED for export");

        AudioEngine.ResetAllOperatorStreamsForExport();

        // Remove soundtrack streams from mixer for direct reading
        foreach (var (handle, clipStream) in AudioEngine.SoundtrackClipStreams)
        {
            float nativeFrequency = clipStream.GetDefaultFrequency();
            BassMix.MixerRemoveChannel(clipStream.StreamHandle);

            Bass.ChannelSetAttribute(clipStream.StreamHandle, ChannelAttribute.Frequency, nativeFrequency);
            Bass.ChannelSetAttribute(clipStream.StreamHandle, ChannelAttribute.Volume, handle.Clip.Volume);
            Bass.ChannelSetAttribute(clipStream.StreamHandle, ChannelAttribute.NoRamp, 1);
            Bass.ChannelSetAttribute(clipStream.StreamHandle, ChannelAttribute.ReverseDirection, 1);

            AudioConfig.LogAudioRenderDebug($"[AudioRendering] Soundtrack '{handle.Clip.FilePath}' removed from mixer");
        }

        AudioConfig.LogAudioRenderDebug($"[AudioRendering] PrepareRecording: startTime={_exportStartTime:F3}s, fps={fps}");
    }

    public static void EndRecording(Playback playback, double fps)
    {
        if (!_isRecording) return;

        _isRecording = false;
        AudioConfig.LogAudioRenderDebug($"[AudioRendering] EndRecording: Exported {_frameCount} frames");

        // Re-add soundtrack streams to mixer
        foreach (var (handle, clipStream) in AudioEngine.SoundtrackClipStreams)
        {
            if (!BassMix.MixerAddChannel(AudioMixerManager.SoundtrackMixerHandle, clipStream.StreamHandle, BassFlags.MixerChanPause))
            {
                Log.Warning($"[AudioRendering] Failed to re-add soundtrack: {Bass.LastError}");
            }
            clipStream.UpdateTimeWhileRecording(playback, fps, true);
        }

        _exportState.RestoreState();
        AudioEngine.RestoreOperatorAudioStreams();
    }

    internal static void ExportAudioFrame(Playback playback, double frameDurationInSeconds, SoundtrackClipStream clipStream)
    {
        try
        {
            AudioEngine.UpdateFftBufferFromSoundtrack(playback);
        }
        catch (Exception ex)
        {
            Log.Error($"ExportAudioFrame error: {ex.Message}", typeof(AudioRendering));
        }
    }

    public static float[] GetFullMixDownBuffer(double frameDurationInSeconds, double localFxTime)
    {
        _frameCount++;
        AudioEngine.UpdateStaleStatesForExport();

        int sampleCount = (int)Math.Max(Math.Round(frameDurationInSeconds * AudioConfig.MixerFrequency), 1);
        int floatCount = sampleCount * 2; // stereo
        float[] mixBuffer = new float[floatCount];
        double currentTime = Playback.Current.TimeInSecs;

        // Check audio source mode - skip soundtrack mixing in external audio mode
        bool isExternalAudioMode = Playback.Current.Settings?.AudioSource == PlaybackSettings.AudioSources.ExternalDevice;

        // Mix soundtrack streams only in soundtrack mode
        if (!isExternalAudioMode)
        {
            foreach (var (handle, clipStream) in AudioEngine.SoundtrackClipStreams)
            {
                MixSoundtrackClip(handle, clipStream, mixBuffer, currentTime, frameDurationInSeconds);
            }
        }

        // Mix operator audio (always included)
        MixOperatorAudio(mixBuffer, floatCount);

        LogMixStats(mixBuffer, floatCount, currentTime);
        UpdateOperatorMetering();
        
        // Populate waveform and FFT buffers for audio analysis operators during export
        // This ensures AudioWaveform, PlaybackFFT, AudioReaction, etc. work correctly during rendering
        WaveFormProcessing.PopulateFromExportBuffer(mixBuffer);
        AudioAnalysis.ComputeFftFromBuffer(mixBuffer);

        return mixBuffer;
    }

    private static void MixSoundtrackClip(AudioClipResourceHandle handle, SoundtrackClipStream clipStream,
        float[] mixBuffer, double currentTime, double frameDuration)
    {
        double clipStart = Playback.Current.SecondsFromBars(handle.Clip.StartTime);
        double timeInClip = currentTime - clipStart;

        if (timeInClip < 0 || timeInClip > handle.Clip.LengthInSeconds)
            return;

        Bass.ChannelGetInfo(clipStream.StreamHandle, out var streamInfo);
        float nativeFreq = clipStream.GetDefaultFrequency();

        long targetBytes = Bass.ChannelSeconds2Bytes(clipStream.StreamHandle, timeInClip);
        Bass.ChannelSetPosition(clipStream.StreamHandle, targetBytes, PositionFlags.Bytes);

        int sourceSampleCount = (int)Math.Ceiling(frameDuration * nativeFreq);
        int sourceFloatCount = sourceSampleCount * streamInfo.Channels;
        float[] sourceBuffer = new float[sourceFloatCount];

        int bytesRead = Bass.ChannelGetData(clipStream.StreamHandle, sourceBuffer, sourceFloatCount * sizeof(float));
        if (bytesRead > 0)
        {
            // Apply the same volume calculation as normal playback:
            // clip.Volume * SoundtrackPlaybackVolume * GlobalPlaybackVolume
            float effectiveVolume = handle.Clip.Volume 
                                    * ProjectSettings.Config.SoundtrackPlaybackVolume
                                    * ProjectSettings.Config.GlobalPlaybackVolume;
            
            ResampleAndMix(sourceBuffer, bytesRead / sizeof(float), nativeFreq, streamInfo.Channels,
                mixBuffer, mixBuffer.Length, AudioConfig.MixerFrequency, 2, effectiveVolume);
        }
    }

    private static void MixOperatorAudio(float[] mixBuffer, int floatCount)
    {
        float[] operatorBuffer = new float[floatCount];
        int bytesRead = Bass.ChannelGetData(AudioMixerManager.OperatorMixerHandle, operatorBuffer, floatCount * sizeof(float));

        if (bytesRead > 0)
        {
            int samplesRead = bytesRead / sizeof(float);
            for (int i = 0; i < Math.Min(samplesRead, mixBuffer.Length); i++)
            {
                if (!float.IsNaN(operatorBuffer[i]))
                    mixBuffer[i] += operatorBuffer[i];
            }
        }
    }

    private static void LogMixStats(float[] mixBuffer, int floatCount, double currentTime)
    {
        if (_frameCount <= 3 || _frameCount % 60 == 0)
        {
            float peak = 0;
            for (int i = 0; i < floatCount; i++)
                if (!float.IsNaN(mixBuffer[i]))
                    peak = Math.Max(peak, Math.Abs(mixBuffer[i]));

            AudioConfig.LogAudioRenderDebug($"[AudioRendering] Frame {_frameCount}: peak={peak:F4}, time={currentTime:F3}s");
        }
    }

    private static void ResampleAndMix(float[] source, int sourceFloatCount, float sourceRate, int sourceChannels,
        float[] target, int targetFloatCount, int targetRate, int targetChannels, float volume)
    {
        int targetSampleCount = targetFloatCount / targetChannels;
        int sourceSampleCount = sourceFloatCount / sourceChannels;
        double ratio = sourceRate / targetRate;

        for (int t = 0; t < targetSampleCount; t++)
        {
            double sourcePos = t * ratio;
            int s0 = (int)sourcePos;
            int s1 = s0 + 1;
            double frac = sourcePos - s0;

            for (int c = 0; c < targetChannels; c++)
            {
                int sc = c % sourceChannels;
                int idx0 = s0 * sourceChannels + sc;
                int idx1 = s1 * sourceChannels + sc;

                float v0 = (idx0 >= 0 && idx0 < sourceFloatCount) ? source[idx0] : 0;
                float v1 = (idx1 >= 0 && idx1 < sourceFloatCount) ? source[idx1] : 0;

                float interpolated = (float)(v0 * (1.0 - frac) + v1 * frac);
                int targetIdx = t * targetChannels + c;

                if (targetIdx < target.Length && !float.IsNaN(interpolated))
                    target[targetIdx] += interpolated * volume;
            }
        }
    }

    private static void UpdateOperatorMetering()
    {
        UpdateMeteringForStates(AudioEngine.GetAllStereoOperatorStates());
        UpdateMeteringForStates(AudioEngine.GetAllSpatialOperatorStates());
    }

    private static void UpdateMeteringForStates<T>(IEnumerable<KeyValuePair<Guid, (T? Stream, bool IsStale)>> states)
        where T : OperatorAudioStreamBase
    {
        foreach (var kvp in states)
        {
            var stream = kvp.Value.Stream;
            if (stream == null || !stream.IsPlaying || stream.IsPaused || kvp.Value.IsStale)
                continue;

            var level = BassMix.ChannelGetLevel(stream.StreamHandle);
            if (level != -1)
            {
                float left = (level & 0xFFFF) / 32768f;
                float right = ((level >> 16) & 0xFFFF) / 32768f;
                stream.UpdateFromBuffer([left, right]);
            }
        }
    }

    public static void GetLastMixDownBuffer(double frameDurationInSeconds)
    {
        AudioEngine.UpdateFftBufferFromSoundtrack(Playback.Current);
    }

    public static void EvaluateAllAudioMeteringOutputs(double localFxTime, float[]? audioBuffer = null)
    {
        var context = new EvaluationContext { LocalFxTime = localFxTime };

        foreach (var source in AudioExportSourceRegistry.Sources)
        {
            if (source is not Instance operatorInstance) continue;

            foreach (var input in operatorInstance.Inputs)
                input.DirtyFlag.ForceInvalidate();

            foreach (var output in operatorInstance.Outputs)
            {
                try { output.Update(context); }
                catch (Exception ex)
                {
                    AudioConfig.LogAudioRenderDebug($"Failed to evaluate output: {ex.Message}");
                }
            }
        }
    }

    private sealed class ExportState
    {
        private float _savedVolume;
        private bool _wasPlaying;

        public void SaveState()
        {
            Bass.ChannelGetAttribute(AudioMixerManager.GlobalMixerHandle, ChannelAttribute.Volume, out _savedVolume);
            if (_savedVolume <= 0) _savedVolume = 1.0f;
            _wasPlaying = Bass.ChannelIsActive(AudioMixerManager.GlobalMixerHandle) == PlaybackState.Playing;
        }

        public void RestoreState()
        {
            Bass.ChannelSetAttribute(AudioMixerManager.GlobalMixerHandle, ChannelAttribute.Volume, _savedVolume);
            if (_wasPlaying)
            {
                Bass.ChannelPlay(AudioMixerManager.GlobalMixerHandle, false);
                AudioConfig.LogAudioRenderDebug("[AudioRendering] GlobalMixer RESUMED");
            }
        }
    }
}