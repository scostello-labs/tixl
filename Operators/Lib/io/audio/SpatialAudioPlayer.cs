using ManagedBass;
using ManagedBass.Mix;
using System.Numerics;
using System.Collections.Generic;
using T3.Core.Audio;
using T3.Core.Logging;
using System;

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

        // Test/Debug inputs
        [Input(Guid = "5f2e9a4c-7d3b-4e8f-9c1a-6f2e8b7d3a5c")]
        public readonly InputSlot<bool> EnableTestMode = new();

        [Input(Guid = "8c4f2e9a-3d7b-4a1f-8e5c-9b2d6a7f4c3e")]
        public readonly InputSlot<bool> TriggerShortTest = new();

        [Input(Guid = "3e9a2f7c-4d8b-4c1f-8a5e-7b2d9f6c3a4e")]
        public readonly InputSlot<bool> TriggerLongTest = new();

        [Input(Guid = "7f4a2e9c-8d3b-4c1f-9e5a-2b7d6f8c3a5e")]
        public readonly InputSlot<float> TestFrequency = new();

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

        // Debug output
        [Output(Guid = "7c4a2e9f-3d8b-4c1f-8e5a-9b2d6f7c4a3e")]
        public readonly Slot<string> DebugInfo = new();

        private Guid _operatorId;
        private bool _wasPausedLastFrame;
        private bool _previousShortTestTrigger;
        private bool _previousLongTestTrigger;
        private string _testFilePath = string.Empty;
        private bool _testModeActive;

        public SpatialAudioPlayer()
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
                AudioConfig.LogDebug($"[SpatialAudioPlayer] Initialized with operator ID: {_operatorId}");
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
                    AudioConfig.LogInfo("[SpatialAudioPlayer] ▶ Generating SHORT test tone (0.1s) - TRIGGER DETECTED");
                    var genStart = DateTime.Now;
                    _testFilePath = GenerateTestTone(testFrequency, 0.1f, "short");
                    var genTime = (DateTime.Now - genStart).TotalMilliseconds;
                    AudioConfig.LogInfo($"[SpatialAudioPlayer] Test tone generated in {genTime:F2}ms");
                    shouldPlay = true;
                    _testModeActive = true;
                }
                // Detect rising edge on long test trigger
                else if (triggerLongTest && !_previousLongTestTrigger)
                {
                    AudioConfig.LogInfo("[SpatialAudioPlayer] ▶ Generating LONG test tone (2.0s) - TRIGGER DETECTED");
                    var genStart = DateTime.Now;
                    _testFilePath = GenerateTestTone(testFrequency, 2.0f, "long");
                    var genTime = (DateTime.Now - genStart).TotalMilliseconds;
                    AudioConfig.LogInfo($"[SpatialAudioPlayer] Test tone generated in {genTime:F2}ms");
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
            var sourcePosition = SourcePosition.GetValue(context);
            var listenerPosition = ListenerPosition.GetValue(context);
            var listenerForward = ListenerForward.GetValue(context);
            var listenerUp = ListenerUp.GetValue(context);
            
            // Ensure listener orientation vectors are normalized and valid
            if (listenerForward.Length() < 0.001f)
                listenerForward = new Vector3(0, 0, 1); // Default forward (Z+)
            else
                listenerForward = Vector3.Normalize(listenerForward);
                
            if (listenerUp.Length() < 0.001f)
                listenerUp = new Vector3(0, 1, 0); // Default up (Y+)
            else
                listenerUp = Vector3.Normalize(listenerUp);
            
            var minDistance = MinDistance.GetValue(context);
            if (minDistance <= 0) minDistance = 1.0f; // Default min distance
            var maxDistance = MaxDistance.GetValue(context);
            if (maxDistance <= minDistance) maxDistance = minDistance + 10.0f; // Default max distance
            var speed = Speed.GetValue(context);
            var seek = Seek.GetValue(context);

            // Get advanced 3D audio parameters
            var sourceOrientation = SourceOrientation.GetValue(context);
            var innerConeAngle = InnerConeAngle.GetValue(context);
            var outerConeAngle = OuterConeAngle.GetValue(context);
            var outerConeVolume = OuterConeVolume.GetValue(context);
            var audio3DMode = Audio3DMode.GetValue(context);

            // Clamp cone parameters to valid ranges
            innerConeAngle = Math.Clamp(innerConeAngle, 0f, 360f);
            outerConeAngle = Math.Clamp(outerConeAngle, 0f, 360f);
            outerConeVolume = Math.Clamp(outerConeVolume, 0f, 1f);
            audio3DMode = Math.Clamp(audio3DMode, 0, 2); // 0=Normal, 1=Relative, 2=Off

            // Update the listener position in the AudioEngine
            // This allows the spatial audio system to calculate distance and panning
            AudioEngine.Set3DListenerPosition(
                position: listenerPosition,
                forward: listenerForward,
                up: listenerUp);

            // Handle pause/resume transitions
            var pauseStateChanged = shouldPause != _wasPausedLastFrame;
            if (pauseStateChanged)
            {
                if (shouldPause)
                {
                    AudioConfig.LogDebug($"[SpatialAudioPlayer] Pausing operator {_operatorId}");
                    AudioEngine.PauseSpatialOperator(_operatorId);
                }
                else
                {
                    AudioConfig.LogDebug($"[SpatialAudioPlayer] Resuming operator {_operatorId}");
                    AudioEngine.ResumeSpatialOperator(_operatorId);
                }
            }
            _wasPausedLastFrame = shouldPause;

            // Send all state to AudioEngine with 3D positioning
            var updateStart = DateTime.Now;
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
                mode3D: audio3DMode
            );
            var updateTime = (DateTime.Now - updateStart).TotalMilliseconds;
            
            // Log timing if significant
            if (updateTime > 1.0)
            {
                AudioConfig.LogDebug($"[SpatialAudioPlayer] UpdateSpatialOperatorPlayback took {updateTime:F2}ms");
            }

            // Get outputs from engine
            IsPlaying.Value = AudioEngine.IsSpatialOperatorStreamPlaying(_operatorId);
            IsPaused.Value = AudioEngine.IsSpatialOperatorPaused(_operatorId);
            GetLevel.Value = AudioEngine.GetSpatialOperatorLevel(_operatorId);
            GetWaveform.Value = AudioEngine.GetSpatialOperatorWaveform(_operatorId);
            GetSpectrum.Value = AudioEngine.GetSpatialOperatorSpectrum(_operatorId);

            // Build debug info
            if (_testModeActive)
            {
                DebugInfo.Value = $"TEST MODE (Spatial) | File: {System.IO.Path.GetFileName(filePath)} | " +
                                 $"Playing: {IsPlaying.Value} | Paused: {IsPaused.Value} | " +
                                 $"Level: {GetLevel.Value:F3} | Source: {sourcePosition} | Listener: {listenerPosition} | " +
                                 $"Orient: {sourceOrientation} | Cone: {innerConeAngle:F0}°/{outerConeAngle:F0}° | Mode: {(Audio3DModes)audio3DMode} | Time: {context.LocalFxTime:F3}";
            }
            else
            {
                DebugInfo.Value = $"File: {System.IO.Path.GetFileName(filePath)} | " +
                                 $"Playing: {IsPlaying.Value} | Paused: {IsPaused.Value} | " +
                                 $"Level: {GetLevel.Value:F3} | Source: {sourcePosition} | Listener: {listenerPosition} | " +
                                 $"MinDist: {minDistance:F1} | MaxDist: {maxDistance:F1} | " +
                                 $"Orient: {sourceOrientation} | Cone: {innerConeAngle:F0}°/{outerConeAngle:F0}° | Mode: {(Audio3DModes)audio3DMode}";
            }
        }

        private string GenerateTestTone(float frequency, float durationSeconds, string label)
        {
            const int sampleRate = 48000;
            const int channels = 1; // Mono for 3D spatial audio
            int sampleCount = (int)(sampleRate * durationSeconds);

            // Create a temporary WAV file
            var tempPath = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"t3_test_tone_spatial_{label}_{frequency}hz_{durationSeconds}s_{DateTime.Now.Ticks}.wav");

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

                    // Write mono
                    writer.Write(sampleValue);
                }

                AudioConfig.LogDebug($"Generated spatial test tone: {tempPath} ({durationSeconds}s @ {frequency}Hz, MONO)");
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

        ~SpatialAudioPlayer()
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

        private enum Audio3DModes
        {
            Normal = 0,    // BASS_3DMODE_NORMAL
            Relative = 1,  // BASS_3DMODE_RELATIVE
            Off = 2        // BASS_3DMODE_OFF
        }
    }
}
