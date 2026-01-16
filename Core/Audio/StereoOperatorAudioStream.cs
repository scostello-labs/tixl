#nullable enable
using System.Diagnostics.CodeAnalysis;
using System.IO;
using ManagedBass;
using T3.Core.Logging;

namespace T3.Core.Audio;

/// <summary>
/// Represents a stereo audio stream for operator-based playback.
/// </summary>
public sealed class StereoOperatorAudioStream : OperatorAudioStreamBase
{
    private StereoOperatorAudioStream() { }

    internal static bool TryLoadStream(string filePath, int mixerHandle, [NotNullWhen(true)] out StereoOperatorAudioStream? stream)
    {
        stream = null;

        if (!TryLoadStreamCore(filePath, mixerHandle, BassFlags.Default,
            out var streamHandle, out var defaultFreq, out var info, out var duration))
        {
            return false;
        }

        stream = new StereoOperatorAudioStream
        {
            StreamHandle = streamHandle,
            MixerStreamHandle = mixerHandle,
            DefaultPlaybackFrequency = defaultFreq,
            Duration = duration,
            FilePath = filePath,
            IsPlaying = false,
            IsPaused = false,
            CachedChannels = info.Channels,
            CachedFrequency = info.Frequency,
            IsStaleMuted = true
        };

        AudioConfig.LogAudioDebug($"[StereoAudio] Loaded: '{Path.GetFileName(filePath)}' ({info.Channels}ch, {info.Frequency}Hz, {duration:F2}s)");
        return true;
    }

    public void SetPanning(float panning)
    {
        Bass.ChannelSetAttribute(StreamHandle, ChannelAttribute.Pan, panning);
    }
}
