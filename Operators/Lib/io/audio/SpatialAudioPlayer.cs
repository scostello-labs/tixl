using System;
using System.Collections.Generic;
using System.Numerics;
using ManagedBass;
using T3.Core.Audio;
using T3.Core.Logging;

namespace Lib.io.audio
{
    [Guid("8a3c9f2e-4b7d-4e1a-9c5f-7d2e8b1a6c3f")]
    internal sealed class SpatialAudioPlayer : Instance<SpatialAudioPlayer>
    {
        [Input(Guid = "2f8a4c9e-3d7b-4a1f-8e5c-9b2d7a6e1c4f")]
        public readonly InputSlot<string> AudioFile = new();

        [Input(Guid = "5c9e2d7a-4b8f-4e3c-9a1d-6f2e8b7c3a5f")]
        public readonly InputSlot<bool> PlayAudio = new();

        [Input(Guid = "7d3f9b2e-4c8a-4f1a-9d5c-8a2b7e1f6c3d")]
        public readonly InputSlot<bool> StopAudio = new();

        [Input(Guid = "9e4c2f7a-5d8b-4a3f-8e1c-7b2d9a6f4c5e")]
        public readonly InputSlot<bool> PauseAudio = new();

        [Input(Guid = "3a7f4e2c-9d8b-4f1a-8c5e-2b7d6a9f3c1e")]
        public readonly InputSlot<float> Volume = new();
 
        [Input(Guid = "6c2e9f4a-7d3b-4e8f-9a1c-5f2e7b8d4a6c")]
        public readonly InputSlot<bool> Mute = new();

        [Input(Guid = "8f4a2e7c-3d9b-4c1f-8e5a-7b2d6f9c3a4e")]
        public readonly InputSlot<Vector3> SourcePosition = new();

        [Input(Guid = "1a2b3c4d-5e6f-7a8b-9c0d-1e2f3a4b5c6d")]
        public readonly InputSlot<Vector3> ListenerPosition = new();

        [Input(Guid = "2b3c4d5e-6f7a-8b9c-0d1e-2f3a4b5c6d7e")]
        public readonly InputSlot<Vector3> ListenerForward = new();

        [Input(Guid = "3c4d5e6f-7a8b-9c0d-1e2f-3a4b5c6d7e8f")]
        public readonly InputSlot<Vector3> ListenerUp = new();

        [Input(Guid = "4e9c2f7a-8d3b-4a6f-9c1e-2b7f5a8d6c3e")]
        public readonly InputSlot<float> MinDistance = new();

        [Input(Guid = "7a3e9f2c-4d8b-4f1a-8c5e-9b2d7a6f1c4e")]
        public readonly InputSlot<float> MaxDistance = new();

        [Input(Guid = "2c8f4e9a-7d3b-4a1f-8e5c-6b2d9a7f3c5e")]
        public readonly InputSlot<float> Speed = new();

        [Input(Guid = "9a4e2f7c-3d8b-4c1f-8e5a-7b2d6f9c4a3e")]
        public readonly InputSlot<float> Seek = new();

        // 3D Audio Advanced Parameters
        [Input(Guid = "1b2c3d4e-5f6a-7b8c-9d0e-1f2a3b4c5d6e")]
        public readonly InputSlot<Vector3> SourceOrientation = new();

        [Input(Guid = "2c3d4e5f-6a7b-8c9d-0e1f-2a3b4c5d6e7f")]
        public readonly InputSlot<float> InnerConeAngle = new();

        [Input(Guid = "3d4e5f6a-7b8c-9d0e-1f2a-3b4c5d6e7f8a")]
        public readonly InputSlot<float> OuterConeAngle = new();

        [Input(Guid = "4e5f6a7b-8c9d-0e1f-2a3b-4c5d6e7f8a9b")]
        public readonly InputSlot<float> OuterConeVolume = new();

        [Input(Guid = "5f6a7b8c-9d0e-1f2a-3b4c-5d6e7f8a9b0c", MappedType = typeof(Audio3DModes))]
        public readonly InputSlot<int> Audio3DMode = new();

#if DEBUG
        // Test/Debug inputs
        [Input(Guid = "5f2e9a4c-7d3b-4e8f-9c1a-6f2e8b7d3a5c")]
        public readonly InputSlot<bool> EnableTestMode = new();

        [Input(Guid = "8c4f2e9a-3d7b-4a1f-8e5c-9b2d6a7f4c3e")]
        public readonly InputSlot<bool> TriggerShortTest = new();

        [Input(Guid = "3e9a2f7c-4d8b-4c1f-8a5e-7b2d9f6c3a4e")]
        public readonly InputSlot<bool> TriggerLongTest = new();

        [Input(Guid = "7f4a2e9c-8d3b-4c1f-9e5a-2b7d6f8c3a5e")]
        public readonly InputSlot<float> TestFrequency = new();
#endif

        [Output(Guid = "4a8e2f7c-9d3b-4c1f-8e5a-7b2d6f9c3a4e")]
        public readonly Slot<Command> Result = new();

        [Output(Guid = "9c2f7a4e-3d8b-4a1f-8e5c-6b2d9a7f4c3e")]
        public readonly Slot<bool> IsPlaying = new();

        [Output(Guid = "6e4a2f9c-7d3b-4c8f-9a1e-2b7d5f8c6a3e")]
        public readonly Slot<bool> IsPaused = new();

        [Output(Guid = "3f9a2e7c-4d8b-4c1f-8a5e-7b2d6f9c3a4e")]
        public readonly Slot<float> GetLevel = new();

        [Output(Guid = "8a4e2f9c-3d7b-4c1f-8e5a-9b2d6f7c4a3e")]
        public readonly Slot<List<float>> GetWaveform = new();

        [Output(Guid = "2f7a4e9c-8d3b-4c1f-9e5a-6b2d7f8c3a5e")]
        public readonly Slot<List<float>> GetSpectrum = new();

#if DEBUG
        // Debug output
        [Output(Guid = "7c4a2e9f-3d8b-4c1f-8e5a-9b2d6f7c4a3e")]
        public readonly Slot<string> DebugInfo = new();
#endif

        private Guid _operatorId;
        private bool _wasPausedLastFrame;

#if DEBUG
        private bool _previousShortTestTrigger;
        private bool _previousLongTestTrigger;
        private string _testFilePath = string.Empty;
        private bool _testModeActive;
#endif

        // Expose current file path for logging
        public string CurrentFilePath { get; private set; } = string.Empty;

        // --- Export state for offline rendering ---
        private bool _exportIsPlaying = false;
        private bool _exportLastPlay = false;
        private bool _exportLastStop = false;
        private float _exportLastSeek = 0f;
        private double _exportLastTime = -1;
        private int _exportDecodeStream = 0;

        private float _lastSetVolume = 1.0f;

        public SpatialAudioPlayer()
        {
            // NOTE: Do NOT register in AudioExportSourceRegistry.
            // File-based audio players are handled through AudioEngine streams.
            // AudioExportSourceRegistry is only for procedural audio generators.

            // Attach update action to ALL outputs so Update() is called
            // when any of these outputs are evaluated
            Result.UpdateAction += Update;
            IsPlaying.UpdateAction += Update;
            IsPaused.UpdateAction += Update;
            GetLevel.UpdateAction += Update;
            GetWaveform.UpdateAction += Update;
            GetSpectrum.UpdateAction += Update;
#if DEBUG
            DebugInfo.UpdateAction += Update;
#endif
        }

        private void Update(EvaluationContext context)
        {
            if (_operatorId == Guid.Empty)
            {
                _operatorId = AudioPlayerUtils.ComputeInstanceGuid(InstancePath);
                AudioConfig.LogAudioDebug($"[SpatialAudioPlayer] Initialized: {_operatorId}");
            }

#if DEBUG
            var (filePath, shouldPlay) = HandleTestMode(context);
#else
            string filePath = AudioFile.GetValue(context);
            bool shouldPlay = PlayAudio.GetValue(context);
#endif

            var shouldStop = StopAudio.GetValue(context);
            var shouldPause = PauseAudio.GetValue(context);
            var volume = Volume.GetValue(context);
            var mute = Mute.GetValue(context);
            var sourcePosition = SourcePosition.GetValue(context);
            var listenerPosition = ListenerPosition.GetValue(context);
            var listenerForward = ListenerForward.GetValue(context);
            var listenerUp = ListenerUp.GetValue(context);

            // Normalize listener orientation
            listenerForward = listenerForward.Length() < 0.001f ? new Vector3(0, 0, 1) : Vector3.Normalize(listenerForward);
            listenerUp = listenerUp.Length() < 0.001f ? new Vector3(0, 1, 0) : Vector3.Normalize(listenerUp);

            var minDistance = MinDistance.GetValue(context);
            if (minDistance <= 0) minDistance = 1.0f;
            var maxDistance = MaxDistance.GetValue(context);
            if (maxDistance <= minDistance) maxDistance = minDistance + 10.0f;

            var speed = Speed.GetValue(context);
            var seek = Seek.GetValue(context);
            var sourceOrientation = SourceOrientation.GetValue(context);
            var innerConeAngle = Math.Clamp(InnerConeAngle.GetValue(context), 0f, 360f);
            var outerConeAngle = Math.Clamp(OuterConeAngle.GetValue(context), 0f, 360f);
            var outerConeVolume = Math.Clamp(OuterConeVolume.GetValue(context), 0f, 1f);
            var audio3DMode = Math.Clamp(Audio3DMode.GetValue(context), 0, 2);

            AudioEngine.Set3DListenerPosition(listenerPosition, listenerForward, listenerUp);

            // Handle pause/resume transitions
            if (shouldPause != _wasPausedLastFrame)
            {
                if (shouldPause)
                    AudioEngine.PauseSpatialOperator(_operatorId);
                else
                    AudioEngine.ResumeSpatialOperator(_operatorId);
            }
            _wasPausedLastFrame = shouldPause;

            AudioEngine.UpdateSpatialOperatorPlayback(
                operatorId: _operatorId,
                localFxTime: context.LocalFxTime,
                filePath: filePath,
                shouldPlay: shouldPlay,
                shouldStop: shouldStop,
                volume: volume,
                mute: mute,
                position: sourcePosition,
                minDistance: minDistance,
                maxDistance: maxDistance,
                speed: speed,
                seek: seek,
                orientation: sourceOrientation,
                innerConeAngle: innerConeAngle,
                outerConeAngle: outerConeAngle,
                outerConeVolume: outerConeVolume,
                mode3D: audio3DMode);

            IsPlaying.Value = AudioEngine.IsSpatialOperatorStreamPlaying(_operatorId);
            IsPaused.Value = AudioEngine.IsSpatialOperatorPaused(_operatorId);
            GetLevel.Value = AudioEngine.GetSpatialOperatorLevel(_operatorId);
            GetWaveform.Value = AudioEngine.GetSpatialOperatorWaveform(_operatorId);
            GetSpectrum.Value = AudioEngine.GetSpatialOperatorSpectrum(_operatorId);

#if DEBUG
            DebugInfo.Value = _testModeActive
                ? $"TEST MODE (Spatial) | Playing: {IsPlaying.Value} | Pos: {sourcePosition} | Level: {GetLevel.Value:F3}"
                : $"Playing: {IsPlaying.Value} | Pos: {sourcePosition} | Level: {GetLevel.Value:F3}";
#endif
        }

#if DEBUG
        private (string filePath, bool shouldPlay) HandleTestMode(EvaluationContext context)
        {
            var enableTestMode = EnableTestMode.GetValue(context);
            var triggerShortTest = TriggerShortTest.GetValue(context);
            var triggerLongTest = TriggerLongTest.GetValue(context);
            var testFrequency = TestFrequency.GetValue(context);
            if (testFrequency <= 0) testFrequency = 440f;

            if (!enableTestMode)
            {
                _testModeActive = false;
                return (AudioFile.GetValue(context), PlayAudio.GetValue(context));
            }

            bool shouldPlay = false;
            if (triggerShortTest && !_previousShortTestTrigger)
            {
                _testFilePath = AudioPlayerUtils.GenerateTestTone(testFrequency, 0.1f, "spatial_short", 1);
                shouldPlay = true;
                _testModeActive = true;
            }
            else if (triggerLongTest && !_previousLongTestTrigger)
            {
                _testFilePath = AudioPlayerUtils.GenerateTestTone(testFrequency, 2.0f, "spatial_long", 1);
                shouldPlay = true;
                _testModeActive = true;
            }

            _previousShortTestTrigger = triggerShortTest;
            _previousLongTestTrigger = triggerLongTest;

            return (_testFilePath, shouldPlay);
        }
#endif

        /// <summary>
        /// Render audio for export. This is called by AudioRendering during export.
        /// </summary>
        public int RenderAudio(double startTime, double duration, float[] buffer)
        {
            if (AudioEngine.TryGetSpatialOperatorStream(_operatorId, out var stream) && stream != null)
                return stream.RenderAudio(startTime, duration, buffer, AudioConfig.MixerFrequency, 2);

            Array.Clear(buffer, 0, buffer.Length);
            return buffer.Length;
        }

        public void RestoreVolumeAfterExport()
        {
            if (AudioEngine.TryGetSpatialOperatorStream(_operatorId, out var stream) && stream != null)
                Bass.ChannelSetAttribute(stream.StreamHandle, ChannelAttribute.Volume, Volume.Value);
        }

        ~SpatialAudioPlayer()
        {
            if (_operatorId != Guid.Empty)
                AudioEngine.UnregisterOperator(_operatorId);

#if DEBUG
            AudioPlayerUtils.CleanupTestFile(_testFilePath);
#endif
        }

        private enum Audio3DModes
        {
            Normal = 0,
            Relative = 1,
            Off = 2
        }
    }
}
