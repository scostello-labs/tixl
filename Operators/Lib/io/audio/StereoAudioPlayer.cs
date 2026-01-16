using System;
using System.Collections.Generic;
using ManagedBass;
using T3.Core.Audio;
using T3.Core.Logging;

namespace Lib.io.audio
{
    [Guid("65e95f77-4743-437f-ab31-f34b831d28d7")]
    internal sealed class StereoAudioPlayer : Instance<StereoAudioPlayer>
    {
        [Input(Guid = "505139a0-71ce-4297-8440-5bf84488902e")]
        public readonly InputSlot<string> AudioFile = new();

        [Input(Guid = "726bc4d3-df8b-4abe-a38e-2e09cf44ca10")]
        public readonly InputSlot<bool> PlayAudio = new();

        [Input(Guid = "59b659c6-ca1f-4c2b-8dff-3a1da9abd352")]
        public readonly InputSlot<bool> StopAudio = new();

        [Input(Guid = "7e42f2a8-3c5d-4f6e-9b8a-1d2e3f4a5b6c")]
        public readonly InputSlot<bool> PauseAudio = new();

        [Input(Guid = "c0645e37-db4e-4658-9d65-96478851f6f6")]
        public readonly InputSlot<float> Volume = new();

        [Input(Guid = "1a3f4b7c-12d3-4a5b-9c7d-8e1f2a3b4c5d")]
        public readonly InputSlot<bool> Mute = new();

        [Input(Guid = "53d1622e-b1d5-4b1c-acd0-ebceb7064043")]
        public readonly InputSlot<float> Panning = new();

        [Input(Guid = "d1a11c4c-9526-4f6b-873e-1798b9dd2b48")]
        public readonly InputSlot<float> Speed = new();

        [Input(Guid = "a5de0d72-5924-4f3a-a02f-d5de7c03f07f")]
        public readonly InputSlot<float> Seek = new();

#if DEBUG
        [Input(Guid = "e1f2a3b4-c5d6-4e7f-8a9b-0c1d2e3f4a5b")]
        public readonly InputSlot<bool> EnableTestMode = new();

        [Input(Guid = "f2a3b4c5-d6e7-4f8a-9b0c-1d2e3f4a5b6c")]
        public readonly InputSlot<bool> TriggerShortTest = new();

        [Input(Guid = "a3b4c5d6-e7f8-4a9b-0c1d-2e3f4a5b6c7d")]
        public readonly InputSlot<bool> TriggerLongTest = new();

        [Input(Guid = "b4c5d6e7-f8a9-4b0c-1d2e-3f4a5b6c7d8e")]
        public readonly InputSlot<float> TestFrequency = new();
#endif

        [Output(Guid = "2433f838-a8ba-4f3a-809e-2d41c404bb84")]
        public readonly Slot<Command> Result = new();

        [Output(Guid = "960aa0a3-89b4-4eff-8b52-36ff6965cf8f")]
        public readonly Slot<bool> IsPlaying = new();

        [Output(Guid = "3f8a9c2e-5d7b-4e1f-a6c8-9d2e1f3b5a7c")]
        public readonly Slot<bool> IsPaused = new();

        [Output(Guid = "b09d215a-bcf0-479a-a649-56f9c698ecb1")]
        public readonly Slot<float> GetLevel = new();

        [Output(Guid = "8f4e2d1a-3b7c-4d89-9e12-7a5b8c9d0e1f")]
        public readonly Slot<List<float>> GetWaveform = new();

        [Output(Guid = "7f8e9d2a-4b5c-3e89-8f12-6a5b9c8d0e2f")]
        public readonly Slot<List<float>> GetSpectrum = new();

#if DEBUG
        [Output(Guid = "c5d6e7f8-a9b0-4c1d-2e3f-4a5b6c7d8e9f")]
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

        public StereoAudioPlayer()
        {
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
                AudioConfig.LogAudioDebug($"[StereoAudioPlayer] Initialized: {_operatorId}");
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
            var panning = Panning.GetValue(context);
            var speed = Speed.GetValue(context);
            var seek = Seek.GetValue(context);

            // Handle pause/resume transitions
            if (shouldPause != _wasPausedLastFrame)
            {
                if (shouldPause)
                    AudioEngine.PauseOperator(_operatorId);
                else
                    AudioEngine.ResumeOperator(_operatorId);
            }
            _wasPausedLastFrame = shouldPause;

            AudioEngine.UpdateStereoOperatorPlayback(
                operatorId: _operatorId,
                localFxTime: context.LocalFxTime,
                filePath: filePath,
                shouldPlay: shouldPlay,
                shouldStop: shouldStop,
                volume: volume,
                mute: mute,
                panning: panning,
                speed: speed,
                seek: seek);

            IsPlaying.Value = AudioEngine.IsOperatorStreamPlaying(_operatorId);
            IsPaused.Value = AudioEngine.IsOperatorPaused(_operatorId);
            GetLevel.Value = AudioEngine.GetOperatorLevel(_operatorId);
            GetWaveform.Value = AudioEngine.GetOperatorWaveform(_operatorId);
            GetSpectrum.Value = AudioEngine.GetOperatorSpectrum(_operatorId);

#if DEBUG
            DebugInfo.Value = _testModeActive
                ? $"TEST MODE | File: {System.IO.Path.GetFileName(filePath)} | Playing: {IsPlaying.Value} | Level: {GetLevel.Value:F3}"
                : $"File: {System.IO.Path.GetFileName(filePath)} | Playing: {IsPlaying.Value} | Level: {GetLevel.Value:F3}";
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
                _testFilePath = AudioPlayerUtils.GenerateTestTone(testFrequency, 0.1f, "short", 2);
                shouldPlay = true;
                _testModeActive = true;
            }
            else if (triggerLongTest && !_previousLongTestTrigger)
            {
                _testFilePath = AudioPlayerUtils.GenerateTestTone(testFrequency, 2.0f, "long", 2);
                shouldPlay = true;
                _testModeActive = true;
            }

            _previousShortTestTrigger = triggerShortTest;
            _previousLongTestTrigger = triggerLongTest;

            return (_testFilePath, shouldPlay);
        }
#endif

        public int RenderAudio(double startTime, double duration, float[] buffer)
        {
            if (AudioEngine.TryGetStereoOperatorStream(_operatorId, out var stream) && stream != null)
                return stream.RenderAudio(startTime, duration, buffer, AudioConfig.MixerFrequency, 2);

            Array.Clear(buffer, 0, buffer.Length);
            return buffer.Length;
        }

        public void RestoreVolumeAfterExport()
        {
            if (AudioEngine.TryGetStereoOperatorStream(_operatorId, out var stream) && stream != null)
                Bass.ChannelSetAttribute(stream.StreamHandle, ChannelAttribute.Volume, Volume.Value);
        }

        ~StereoAudioPlayer()
        {
            if (_operatorId != Guid.Empty)
                AudioEngine.UnregisterOperator(_operatorId);

#if DEBUG
            AudioPlayerUtils.CleanupTestFile(_testFilePath);
#endif
        }
    }
}