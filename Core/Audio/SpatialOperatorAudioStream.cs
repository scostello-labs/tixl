#nullable enable
using System;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Numerics;
using ManagedBass;

namespace T3.Core.Audio;

/// <summary>
/// Represents a 3D spatial audio stream for operator-based playback with 3D positioning.
/// </summary>
public sealed class SpatialOperatorAudioStream : OperatorAudioStreamBase
{
    // 3D positioning
    private Vector3 _position = Vector3.Zero;
    private Vector3 _velocity = Vector3.Zero;
    private Vector3 _orientation = new(0, 0, -1);
    private float _minDistance = 1.0f;
    private float _maxDistance = 100.0f;
    private Mode3D _3dMode = Mode3D.Normal;
    private float _innerAngleDegrees = 360.0f;
    private float _outerAngleDegrees = 360.0f;
    private float _outerVolume = 1.0f;

    private SpatialOperatorAudioStream() { }

    internal static bool TryLoadStream(string filePath, int mixerHandle, [NotNullWhen(true)] out SpatialOperatorAudioStream? stream)
    {
        stream = null;

        // Load as mono for 3D audio
        if (!TryLoadStreamCore(filePath, mixerHandle, BassFlags.Mono,
            out var streamHandle, out var defaultFreq, out var info, out var duration))
        {
            return false;
        }

        stream = new SpatialOperatorAudioStream
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

        stream.Initialize3DAudio();
        AudioConfig.LogAudioDebug($"[SpatialAudio] Loaded: '{Path.GetFileName(filePath)}' ({info.Channels}ch, {info.Frequency}Hz, {duration:F2}s)");
        return true;
    }

    private void Initialize3DAudio()
    {
        Bass.ChannelSet3DAttributes(StreamHandle, _3dMode, _minDistance, _maxDistance,
            (int)_innerAngleDegrees, (int)_outerAngleDegrees, _outerVolume);
        Bass.ChannelSet3DPosition(StreamHandle, To3DVector(_position), To3DVector(_orientation), To3DVector(_velocity));
    }

    private static ManagedBass.Vector3D To3DVector(Vector3 v) => new(v.X, v.Y, v.Z);

    public void Update3DPosition(Vector3 position, float minDistance, float maxDistance)
    {
        var deltaPos = position - _position;
        _velocity = deltaPos * 60.0f; // Assume ~60fps
        _position = position;
        _minDistance = Math.Max(0.1f, minDistance);
        _maxDistance = Math.Max(_minDistance + 0.1f, maxDistance);

        Bass.ChannelSet3DAttributes(StreamHandle, _3dMode, _minDistance, _maxDistance,
            (int)_innerAngleDegrees, (int)_outerAngleDegrees, _outerVolume);
        Bass.ChannelSet3DPosition(StreamHandle, To3DVector(_position), To3DVector(_orientation), To3DVector(_velocity));
        Bass.Apply3D();
    }

    public void Set3DOrientation(Vector3 orientation)
    {
        _orientation = Vector3.Normalize(orientation);
        Bass.ChannelSet3DPosition(StreamHandle, To3DVector(_position), To3DVector(_orientation), To3DVector(_velocity));
        Bass.Apply3D();
    }

    public void Set3DCone(float innerAngleDegrees, float outerAngleDegrees, float outerVolume)
    {
        _innerAngleDegrees = Math.Clamp(innerAngleDegrees, 0f, 360f);
        _outerAngleDegrees = Math.Clamp(outerAngleDegrees, 0f, 360f);
        _outerVolume = Math.Clamp(outerVolume, 0f, 1f);

        Bass.ChannelSet3DAttributes(StreamHandle, _3dMode, _minDistance, _maxDistance,
            (int)_innerAngleDegrees, (int)_outerAngleDegrees, _outerVolume);
        Bass.Apply3D();
    }

    public void Set3DMode(Mode3D mode)
    {
        _3dMode = mode;
        Bass.ChannelSet3DAttributes(StreamHandle, _3dMode, _minDistance, _maxDistance,
            (int)_innerAngleDegrees, (int)_outerAngleDegrees, _outerVolume);
        Bass.Apply3D();
    }

    public override void Play()
    {
        base.Play();
        Bass.Apply3D();
    }

    public override void Resume()
    {
        base.Resume();
        Bass.Apply3D();
    }

    public override void RestartAfterExport()
    {
        base.RestartAfterExport();
        Bass.Apply3D();
    }

    public override void PrepareForExport()
    {
        base.PrepareForExport();
        Bass.Apply3D();
    }

    protected override int GetNativeChannelCount() => CachedChannels > 0 ? CachedChannels : 1;
}
