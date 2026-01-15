using System;
using System.Collections.Generic;
using ManagedBass;
using T3.Core.Animation;

namespace T3.Core.Audio;

/// <summary>
/// Handles audio rendering/export functionality, managing BASS state during video export.
/// </summary>
public static class AudioRendering
{
    private static bool _isRecording;
    private static BassSettingsBeforeExport _settingsBeforeExport;
    private static readonly Dictionary<AudioClipResourceHandle, Queue<byte>> _fifoBuffersForClips = new();

    /// <summary>
    /// Prepares the audio system for recording/export by pausing live output.
    /// </summary>
    public static void PrepareRecording(Playback playback, double fps)
    {
        if (_isRecording)
            return;
        
        _isRecording = true;

        // Capture current BASS settings before modifying them
        _settingsBeforeExport = new BassSettingsBeforeExport
        {
            BassUpdateThreads = Bass.GetConfig(Configuration.UpdateThreads),
            BassUpdatePeriodInMs = Bass.GetConfig(Configuration.UpdatePeriod),
            BassGlobalStreamVolume = Bass.GetConfig(Configuration.GlobalStreamVolume)
        };
        
        // Use sensible defaults if captured values are 0
        if (_settingsBeforeExport.BassGlobalStreamVolume == 0)
            _settingsBeforeExport.BassGlobalStreamVolume = 10000;
        if (_settingsBeforeExport.BassUpdatePeriodInMs == 0)
            _settingsBeforeExport.BassUpdatePeriodInMs = AudioConfig.UpdatePeriodMs > 0 ? AudioConfig.UpdatePeriodMs : 5;
        if (_settingsBeforeExport.BassUpdateThreads == 0)
            _settingsBeforeExport.BassUpdateThreads = 1;

        // Turn off automatic sound generation for export
        Bass.Pause();
        Bass.Configure(Configuration.UpdateThreads, false);
        Bass.Configure(Configuration.UpdatePeriod, 0);
        Bass.Configure(Configuration.GlobalStreamVolume, 0);

        // Configure soundtrack clips for export
        const int tailAttribute = 16; // BASS_ATTRIB_TAIL
        foreach (var (_, clipStream) in AudioEngine.SoundtrackClipStreams)
        {
            _settingsBeforeExport.BufferLengthInSeconds = Bass.ChannelGetAttribute(clipStream.StreamHandle, ChannelAttribute.Buffer);

            Bass.ChannelSetAttribute(clipStream.StreamHandle, ChannelAttribute.Volume, 1.0);
            Bass.ChannelSetAttribute(clipStream.StreamHandle, ChannelAttribute.Buffer, 1.0 / fps);
            Bass.ChannelSetAttribute(clipStream.StreamHandle, (ChannelAttribute)tailAttribute, 2.0 / fps);
            Bass.ChannelStop(clipStream.StreamHandle);
            clipStream.UpdateTimeWhileRecording(playback, fps, true);
            Bass.ChannelPlay(clipStream.StreamHandle);
            Bass.ChannelPause(clipStream.StreamHandle);
        }

        _fifoBuffersForClips.Clear();
    }

    /// <summary>
    /// Ends recording and restores the audio system to live playback state.
    /// </summary>
    public static void EndRecording(Playback playback, double fps)
    {
        if (!_isRecording)
            return;
        
        _isRecording = false;

        const int tailAttribute = 16; // BASS_ATTRIB_TAIL

        // Restore soundtrack clip streams
        foreach (var (_, clipStream) in AudioEngine.SoundtrackClipStreams)
        {
            clipStream.UpdateTimeWhileRecording(playback, fps, false);
            Bass.ChannelSetAttribute(clipStream.StreamHandle, ChannelAttribute.NoRamp, 0);
            Bass.ChannelSetAttribute(clipStream.StreamHandle, (ChannelAttribute)tailAttribute, 0.0);
            Bass.ChannelSetAttribute(clipStream.StreamHandle, ChannelAttribute.Buffer, _settingsBeforeExport.BufferLengthInSeconds);
        }

        // Restore BASS settings
        Bass.Configure(Configuration.UpdatePeriod, _settingsBeforeExport.BassUpdatePeriodInMs);
        Bass.Configure(Configuration.GlobalStreamVolume, _settingsBeforeExport.BassGlobalStreamVolume);
        Bass.Configure(Configuration.UpdateThreads, _settingsBeforeExport.BassUpdateThreads);
        
        // Resume output device
        Bass.Start();
        
        // Clean up export sources
        foreach (var source in AudioExportSourceRegistry.Sources)
        {
            source.GetType().GetMethod("CleanupExportDecodeStream")?.Invoke(source, null);
            source.GetType().GetMethod("ClearExportMetering")?.Invoke(source, null);
        }
        
        // Restore operator audio streams
        AudioEngine.RestoreOperatorAudioStreams();
    }

    /// <summary>
    /// Exports a single audio frame for the given clip stream.
    /// </summary>
    internal static void ExportAudioFrame(Playback playback, double frameDurationInSeconds, SoundtrackClipStream clipStream)
    {
        try
        {
            if (!_fifoBuffersForClips.TryGetValue(clipStream.ResourceHandle, out var bufferQueue))
            {
                bufferQueue = new Queue<byte>();
                _fifoBuffersForClips[clipStream.ResourceHandle] = bufferQueue;
            }

            var streamPositionInBytes = clipStream.UpdateTimeWhileRecording(playback, 1.0 / frameDurationInSeconds, true);
            var bytes = (int)Math.Max(Bass.ChannelSeconds2Bytes(clipStream.StreamHandle, frameDurationInSeconds), 0);
            
            if (bytes <= 0) 
                return;

            // Add silence for negative stream position
            if (streamPositionInBytes < 0)
            {
                var silenceBytesToAdd = Math.Min(-streamPositionInBytes, bytes);
                for (int i = 0; i < silenceBytesToAdd; i++)
                    bufferQueue.Enqueue(0);
            }

            Bass.ChannelSetAttribute(clipStream.StreamHandle, ChannelAttribute.Buffer, (int)Math.Round(frameDurationInSeconds * 1000.0));
            Bass.ChannelUpdate(clipStream.StreamHandle, (int)Math.Round(frameDurationInSeconds * 1000.0));

            // Read audio data
            var info = Bass.ChannelGetInfo(clipStream.StreamHandle);
            byte[]? validData = null;
            
            if ((info.Flags & BassFlags.Float) != 0)
            {
                int floatCount = bytes / sizeof(float);
                var floatBuffer = new float[floatCount];
                int floatBytesRead = Bass.ChannelGetData(clipStream.StreamHandle, floatBuffer, bytes);
                if (floatBytesRead > 0 && floatBytesRead <= bytes)
                {
                    validData = new byte[floatBytesRead];
                    Buffer.BlockCopy(floatBuffer, 0, validData, 0, floatBytesRead);
                }
            }
            else
            {
                var newBuffer = new byte[bytes];
                var newBytes = Bass.ChannelGetData(clipStream.StreamHandle, newBuffer, bytes);
                if (newBytes > 0 && newBytes <= bytes)
                {
                    validData = new byte[newBytes];
                    Array.Copy(newBuffer, validData, newBytes);
                }
            }
            
            if (validData != null)
            {
                foreach (var b in validData)
                    bufferQueue.Enqueue(b);
                AudioEngine.UpdateFftBufferFromSoundtrack(clipStream.StreamHandle, playback);
            }

            // Ensure buffer is exactly the right size
            while (bufferQueue.Count < bytes) bufferQueue.Enqueue(0);
            while (bufferQueue.Count > bytes) bufferQueue.Dequeue();
        }
        catch (Exception ex)
        {
            Logging.Log.Error($"ExportAudioFrame error: {ex.Message}", typeof(AudioRendering));
        }
    }

    /// <summary>
    /// Gets the last mixed audio buffer for export.
    /// </summary>
    public static byte[]? GetLastMixDownBuffer(double frameDurationInSeconds)
    {
        try
        {
            if (AudioEngine.SoundtrackClipStreams.Count == 0)
            {
                var channels = AudioEngine.GetClipChannelCount(null);
                var sampleRate = AudioEngine.GetClipSampleRate(null);
                var samples = (int)Math.Max(Math.Round(frameDurationInSeconds * sampleRate), 0.0);
                return new byte[samples * channels * sizeof(float)];
            }

            foreach (var (_, clipStream) in AudioEngine.SoundtrackClipStreams)
            {
                if (!_fifoBuffersForClips.TryGetValue(clipStream.ResourceHandle, out var bufferQueue))
                    continue;

                var bytes = (int)Bass.ChannelSeconds2Bytes(clipStream.StreamHandle, frameDurationInSeconds);
                var result = new byte[bytes];
                for (int i = 0; i < bytes; i++)
                    result[i] = bufferQueue.Count > 0 ? bufferQueue.Dequeue() : (byte)0;
                return result;
            }
            
            return null;
        }
        catch (Exception ex)
        {
            Logging.Log.Error($"GetLastMixDownBuffer error: {ex.Message}", typeof(AudioRendering));
            return null;
        }
    }

    /// <summary>
    /// Gets the full mixed audio buffer including all sources for export.
    /// </summary>
    public static float[] GetFullMixDownBuffer(double frameDurationInSeconds, double localFxTime)
    {
        int mixerSampleRate = AudioConfig.MixerFrequency;
        int channels = AudioEngine.GetClipChannelCount(null);
        int sampleCount = (int)Math.Max(Math.Round(frameDurationInSeconds * mixerSampleRate), 0.0);
        int floatCount = sampleCount * channels;
        float[] mixBuffer = new float[floatCount];

        // Mix soundtrack clips
        foreach (var (_, clipStream) in AudioEngine.SoundtrackClipStreams)
        {
            var handle = clipStream.ResourceHandle;
            if (!handle.TryGetFileResource(out var file) || file.FileInfo == null)
                continue;
            
            string filePath = file.FileInfo.FullName;
            if (!System.IO.File.Exists(filePath))
                continue;
            
            int decodeStream = Bass.CreateStream(filePath, Flags: BassFlags.Decode | BassFlags.Float);
            if (decodeStream == 0)
                continue;
            
            double clipStart = handle.Clip.StartTime;
            double timeInClip = localFxTime - clipStart;
            if (timeInClip < 0 || timeInClip > handle.Clip.LengthInSeconds)
            {
                Bass.StreamFree(decodeStream);
                continue;
            }
            
            Bass.ChannelGetInfo(decodeStream, out var info);
            int clipSampleRate = info.Frequency;
            int clipChannels = info.Channels;
            
            Bass.ChannelSetPosition(decodeStream, Bass.ChannelSeconds2Bytes(decodeStream, timeInClip));
            int clipSampleCount = (int)Math.Max(Math.Round(frameDurationInSeconds * clipSampleRate), 0.0);
            int clipFloatCount = clipSampleCount * clipChannels;
            float[] temp = new float[clipFloatCount];
            int bytesRead = Bass.ChannelGetData(decodeStream, temp, clipFloatCount * sizeof(float));
            Bass.StreamFree(decodeStream);
            
            float volume = handle.Clip.Volume;
            int samplesRead = bytesRead / sizeof(float);
            
            // Resample if needed
            float[] resampled = temp;
            if (clipSampleRate != mixerSampleRate && samplesRead > 0)
            {
                int resampleSamples = (int)Math.Max(Math.Round(frameDurationInSeconds * mixerSampleRate), 0.0);
                resampled = LinearResample(temp, samplesRead / clipChannels, clipChannels, resampleSamples, channels);
                samplesRead = resampleSamples * channels;
            }
            
            for (int i = 0; i < Math.Min(samplesRead, floatCount); i++)
                mixBuffer[i] += resampled[i] * volume;
        }

        // Mix export sources
        float[] opTemp = new float[floatCount];
        foreach (var source in AudioExportSourceRegistry.Sources)
        {
            Array.Clear(opTemp, 0, opTemp.Length);
            int written = source.RenderAudio(localFxTime, frameDurationInSeconds, opTemp);
            
            source.GetType().GetMethod("UpdateFromBuffer")?.Invoke(source, new object[] { opTemp });
            
            for (int i = 0; i < written && i < floatCount; i++)
                mixBuffer[i] += opTemp[i];
        }
        
        return mixBuffer;
    }

    /// <summary>
    /// Evaluates metering outputs for all audio export sources during export.
    /// </summary>
    public static void EvaluateAllAudioMeteringOutputs(double localFxTime, float[]? audioBuffer = null)
    {
        var context = new T3.Core.Operator.EvaluationContext { LocalFxTime = localFxTime };
        
        foreach (var source in AudioExportSourceRegistry.Sources)
        {
            var type = source.GetType();
            var getLevel = type.GetProperty("GetLevel")?.GetValue(source);
            var getWaveform = type.GetProperty("GetWaveform")?.GetValue(source);
            var getSpectrum = type.GetProperty("GetSpectrum")?.GetValue(source);

            if (audioBuffer != null)
            {
                getLevel?.GetType().GetMethod("UpdateFromBuffer")?.Invoke(getLevel, new object[] { audioBuffer });
                getWaveform?.GetType().GetMethod("UpdateFromBuffer")?.Invoke(getWaveform, new object[] { audioBuffer });
                getSpectrum?.GetType().GetMethod("UpdateFromBuffer")?.Invoke(getSpectrum, new object[] { audioBuffer });
            }

            getLevel?.GetType().GetMethod("GetValue")?.Invoke(getLevel, new object[] { context });
            getWaveform?.GetType().GetMethod("GetValue")?.Invoke(getWaveform, new object[] { context });
            getSpectrum?.GetType().GetMethod("GetValue")?.Invoke(getSpectrum, new object[] { context });
        }
    }

    private static float[] LinearResample(float[] input, int inputSamples, int inputChannels, int outputSamples, int outputChannels)
    {
        float[] output = new float[outputSamples * outputChannels];
        int minChannels = Math.Min(inputChannels, outputChannels);
        
        for (int ch = 0; ch < minChannels; ch++)
        {
            for (int i = 0; i < outputSamples; i++)
            {
                float t = (float)i / (outputSamples - 1);
                float srcPos = t * (inputSamples - 1);
                int srcIndex = (int)srcPos;
                float frac = srcPos - srcIndex;
                int srcBase = srcIndex * inputChannels + ch;
                int srcNext = Math.Min(srcIndex + 1, inputSamples - 1) * inputChannels + ch;
                output[i * outputChannels + ch] = input[srcBase] + (input[srcNext] - input[srcBase]) * frac;
            }
        }
        
        return output;
    }

    private struct BassSettingsBeforeExport
    {
        public int BassUpdatePeriodInMs;
        public int BassGlobalStreamVolume;
        public int BassUpdateThreads;
        public double BufferLengthInSeconds;
    }
}