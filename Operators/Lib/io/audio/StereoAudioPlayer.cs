using ManagedBass;
using ManagedBass.Mix;
using System.Numerics;
using System.Collections.Generic;
using T3.Core.Audio;
using T3.Core.Logging;
using System;

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

        // Test/Debug inputs
        [Input(Guid = "e1f2a3b4-c5d6-4e7f-8a9b-0c1d2e3f4a5b")]
        public readonly InputSlot<bool> EnableTestMode = new();

        [Input(Guid = "f2a3b4c5-d6e7-4f8a-9b0c-1d2e3f4a5b6c")]
        public readonly InputSlot<bool> TriggerShortTest = new();

        [Input(Guid = "a3b4c5d6-e7f8-4a9b-0c1d-2e3f4a5b6c7d")]
        public readonly InputSlot<bool> TriggerLongTest = new();

        [Input(Guid = "b4c5d6e7-f8a9-4b0c-1d2e-3f4a5b6c7d8e")]
        public readonly InputSlot<float> TestFrequency = new();

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

        // Debug output
        [Output(Guid = "c5d6e7f8-a9b0-4c1d-2e3f-4a5b6c7d8e9f")]
        public readonly Slot<string> DebugInfo = new();

        private Guid _operatorId;
        private bool _wasPausedLastFrame;
        private bool _previousShortTestTrigger;
        private bool _previousLongTestTrigger;
        private string _testFilePath = string.Empty;
        private bool _testModeActive;

        public StereoAudioPlayer()
        {
            // Attach update action to ALL outputs so Update() is called
            // when any of these outputs are evaluated
            Result.UpdateAction += Update;
            IsPlaying.UpdateAction += Update;
            IsPaused.UpdateAction += Update;
            GetLevel.UpdateAction += Update;
            GetWaveform.UpdateAction += Update;
            GetSpectrum.UpdateAction += Update;
            DebugInfo.UpdateAction += Update;
        }

        private void Update(EvaluationContext context)
        {
            if (_operatorId == Guid.Empty)
            {
                _operatorId = ComputeInstanceGuid();
                AudioConfig.LogDebug($"[StereoAudioPlayer] Initialized with operator ID: {_operatorId}");
            }

            var enableTestMode = EnableTestMode.GetValue(context);
            var triggerShortTest = TriggerShortTest.GetValue(context);
            var triggerLongTest = TriggerLongTest.GetValue(context);
            var testFrequency = TestFrequency.GetValue(context);
            if (testFrequency <= 0) testFrequency = 440f; // Default to A4

            string filePath;
            bool shouldPlay;

            // Test mode handling
            if (enableTestMode)
            {
                // Detect rising edge on short test trigger
                if (triggerShortTest && !_previousShortTestTrigger)
                {
                    AudioConfig.LogInfo("[StereoAudioPlayer] ▶ Generating SHORT test tone (0.1s) - TRIGGER DETECTED");
                    var genStart = DateTime.Now;
                    _testFilePath = GenerateTestTone(testFrequency, 0.1f, "short");
                    var genTime = (DateTime.Now - genStart).TotalMilliseconds;
                    AudioConfig.LogInfo($"[StereoAudioPlayer] Test tone generated in {genTime:F2}ms");
                    shouldPlay = true;
                    _testModeActive = true;
                }
                // Detect rising edge on long test trigger
                else if (triggerLongTest && !_previousLongTestTrigger)
                {
                    AudioConfig.LogInfo("[StereoAudioPlayer] ▶ Generating LONG test tone (2.0s) - TRIGGER DETECTED");
                    var genStart = DateTime.Now;
                    _testFilePath = GenerateTestTone(testFrequency, 2.0f, "long");
                    var genTime = (DateTime.Now - genStart).TotalMilliseconds;
                    AudioConfig.LogInfo($"[StereoAudioPlayer] Test tone generated in {genTime:F2}ms");
                    shouldPlay = true;
                    _testModeActive = true;
                }
                else
                {
                    shouldPlay = false;
                }

                _previousShortTestTrigger = triggerShortTest;
                _previousLongTestTrigger = triggerLongTest;

                filePath = _testFilePath;
            }
            else
            {
                _testModeActive = false;
                filePath = AudioFile.GetValue(context);
                shouldPlay = PlayAudio.GetValue(context);
            }

            var shouldStop = StopAudio.GetValue(context);
            var shouldPause = PauseAudio.GetValue(context);
            var volume = Volume.GetValue(context);
            var mute = Mute.GetValue(context);
            var panning = Panning.GetValue(context);
            var speed = Speed.GetValue(context);
            var seek = Seek.GetValue(context);

            // Handle pause/resume transitions
            var pauseStateChanged = shouldPause != _wasPausedLastFrame;
            if (pauseStateChanged)
            {
                if (shouldPause)
                {
                    AudioConfig.LogDebug($"[StereoAudioPlayer] Pausing operator {_operatorId}");
                    AudioEngine.PauseOperator(_operatorId);
                }
                else
                {
                    AudioConfig.LogDebug($"[StereoAudioPlayer] Resuming operator {_operatorId}");
                    AudioEngine.ResumeOperator(_operatorId);
                }
            }
            _wasPausedLastFrame = shouldPause;

            // Send all state to AudioEngine - let it handle the logic
            var updateStart = DateTime.Now;
            AudioEngine.UpdateOperatorPlayback(
                operatorId: _operatorId,
                localFxTime: context.LocalFxTime,
                filePath: filePath,
                shouldPlay: shouldPlay,
                shouldStop: shouldStop,
                volume: volume,
                mute: mute,
                panning: panning,
                speed: speed,
                seek: seek
            );
            var updateTime = (DateTime.Now - updateStart).TotalMilliseconds;
            
            // Log timing if significant
            if (updateTime > 1.0)
            {
                AudioConfig.LogDebug($"[StereoAudioPlayer] UpdateOperatorPlayback took {updateTime:F2}ms");
            }

            // Get outputs from engine
            IsPlaying.Value = AudioEngine.IsOperatorStreamPlaying(_operatorId);
            IsPaused.Value = AudioEngine.IsOperatorPaused(_operatorId);
            GetLevel.Value = AudioEngine.GetOperatorLevel(_operatorId);
            GetWaveform.Value = AudioEngine.GetOperatorWaveform(_operatorId);
            GetSpectrum.Value = AudioEngine.GetOperatorSpectrum(_operatorId);

            // Build debug info
            if (_testModeActive)
            {
                DebugInfo.Value = $"TEST MODE | File: {System.IO.Path.GetFileName(filePath)} | " +
                                 $"Playing: {IsPlaying.Value} | Paused: {IsPaused.Value} | " +
                                 $"Level: {GetLevel.Value:F3} | Time: {context.LocalFxTime:F3}";
            }
            else
            {
                DebugInfo.Value = $"File: {System.IO.Path.GetFileName(filePath)} | " +
                                 $"Playing: {IsPlaying.Value} | Paused: {IsPaused.Value} | " +
                                 $"Level: {GetLevel.Value:F3}";
            }
        }

        private string GenerateTestTone(float frequency, float durationSeconds, string label)
        {
            const int sampleRate = 48000;
            const int channels = 2;
            int sampleCount = (int)(sampleRate * durationSeconds);

            // Create a temporary WAV file
            var tempPath = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"t3_test_tone_{label}_{frequency}hz_{durationSeconds}s_{DateTime.Now.Ticks}.wav");

            try
            {
                using var fileStream = new System.IO.FileStream(tempPath, System.IO.FileMode.Create);
                using var writer = new System.IO.BinaryWriter(fileStream);

                // Write WAV header
                int dataSize = sampleCount * channels * sizeof(short);
                int fileSize = 36 + dataSize;

                writer.Write(new[] { 'R', 'I', 'F', 'F' });
                writer.Write(fileSize);
                writer.Write(new[] { 'W', 'A', 'V', 'E' });
                writer.Write(new[] { 'f', 'm', 't', ' ' });
                writer.Write(16); // fmt chunk size
                writer.Write((short)1); // PCM format
                writer.Write((short)channels);
                writer.Write(sampleRate);
                writer.Write(sampleRate * channels * sizeof(short)); // byte rate
                writer.Write((short)(channels * sizeof(short))); // block align
                writer.Write((short)16); // bits per sample
                writer.Write(new[] { 'd', 'a', 't', 'a' });
                writer.Write(dataSize);

                // Generate sine wave with envelope to avoid clicks
                const float envelopeDuration = 0.005f; // 5ms fade in/out
                int envelopeSamples = (int)(sampleRate * envelopeDuration);

                for (int i = 0; i < sampleCount; i++)
                {
                    float t = i / (float)sampleRate;
                    float sample = (float)Math.Sin(2.0 * Math.PI * frequency * t);

                    // Apply envelope
                    if (i < envelopeSamples)
                    {
                        sample *= i / (float)envelopeSamples;
                    }
                    else if (i > sampleCount - envelopeSamples)
                    {
                        sample *= (sampleCount - i) / (float)envelopeSamples;
                    }

                    short sampleValue = (short)(sample * 16384); // 50% amplitude

                    // Write stereo
                    writer.Write(sampleValue); // Left
                    writer.Write(sampleValue); // Right
                }

                AudioConfig.LogDebug($"Generated test tone: {tempPath} ({durationSeconds}s @ {frequency}Hz)");
                return tempPath;
            }
            catch (Exception ex)
            {
                Log.Error($"Failed to generate test tone: {ex.Message}");
                return string.Empty;
            }
        }

        private Guid ComputeInstanceGuid()
        {
            unchecked
            {
                ulong hash = 0xCBF29CE484222325;
                const ulong prime = 0x100000001B3;

                foreach (var id in InstancePath)
                {
                    var bytes = id.ToByteArray();
                    foreach (var b in bytes)
                    {
                        hash ^= b;
                        hash *= prime;
                    }
                }

                var guidBytes = new byte[16];
                var hashBytes = BitConverter.GetBytes(hash);
                Array.Copy(hashBytes, 0, guidBytes, 0, 8);
                Array.Copy(hashBytes, 0, guidBytes, 8, 8);
                return new Guid(guidBytes);
            }
        }

        ~StereoAudioPlayer()
        {
            if (_operatorId != Guid.Empty)
            {
                AudioEngine.UnregisterOperator(_operatorId);
            }

            // Clean up test files
            if (!string.IsNullOrEmpty(_testFilePath) && System.IO.File.Exists(_testFilePath))
            {
                try
                {
                    System.IO.File.Delete(_testFilePath);
                }
                catch
                {
                    // Ignore cleanup errors
                }
            }
        }
    }
}