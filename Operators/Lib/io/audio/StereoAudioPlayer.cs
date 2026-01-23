using System;
using System.Collections.Generic;
using System.Numerics;
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

        [Input(Guid = "905d9e01-b1fb-47c0-801c-fc920ed36884", MappedType = typeof(AdsrCalculator.TriggerMode))]
        public readonly InputSlot<int> TriggerMode = new();

        [Input(Guid = "f7a8b9c0-d1e2-4f3a-5b6c-7d8e9f0a1b2c")]
        public readonly InputSlot<float> Duration = new();

        [Input(Guid = "b2c3d4e5-f6a7-4b8c-9d0e-1f2a3b4c5d6e")]
        public readonly InputSlot<bool> UseEnvelope = new();

        // ADSR Envelope as Vector4: X=Attack, Y=Decay, Z=Sustain, W=Release
        [Input(Guid = "3dbcbbe6-a8b4-4b83-a2c0-e22b24b91b42", MappedType = typeof(AdsrCalculator.AdsrMapping))]
        public readonly InputSlot<Vector4> Envelope = new();

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

        [Output(Guid = "c46c2799-ed04-4ec5-9175-dfcfc488525a")]
        public readonly Slot<float> EnvelopeValue = new();

        private Guid _operatorId;
        private bool _wasPausedLastFrame;
        private bool _previousPlayTrigger;
        private readonly AdsrCalculator _calculator = new();

        public StereoAudioPlayer()
        {
            Result.UpdateAction += Update;
            IsPlaying.UpdateAction += Update;
            IsPaused.UpdateAction += Update;
            GetLevel.UpdateAction += Update;
            GetWaveform.UpdateAction += Update;
            GetSpectrum.UpdateAction += Update;
            EnvelopeValue.UpdateAction += Update;
        }

        private void Update(EvaluationContext context)
        {
            if (_operatorId == Guid.Empty)
            {
                _operatorId = AudioPlayerUtils.ComputeInstanceGuid(InstancePath);
                AudioConfig.LogAudioDebug($"[StereoAudioPlayer] Initialized: {_operatorId}");
            }

            string filePath = AudioFile.GetValue(context);
            bool shouldPlay = PlayAudio.GetValue(context);

            var shouldStop = StopAudio.GetValue(context);
            var shouldPause = PauseAudio.GetValue(context);
            var volume = Volume.GetValue(context);
            var mute = Mute.GetValue(context);
            var panning = Panning.GetValue(context);
            var speed = Speed.GetValue(context);
            var seek = Seek.GetValue(context);
            var triggerMode = (AdsrCalculator.TriggerMode)TriggerMode.GetValue(context);
            var duration = Duration.GetValue(context);
            var useEnvelope = UseEnvelope.GetValue(context);
            var envelope = Envelope.GetValue(context);

            // Apply defaults
            if (duration <= 0) duration = float.MaxValue;

            // Extract ADSR from Vector4: X=Attack, Y=Decay, Z=Sustain, W=Release
            var attack = envelope.X > 0 ? envelope.X : 0.01f;
            var decay = envelope.Y > 0 ? envelope.Y : 0.1f;
            var sustain = envelope.Z >= 0 ? Math.Clamp(envelope.Z, 0f, 1f) : 0.7f;
            var release = envelope.W > 0 ? envelope.W : 0.3f;

            // Update ADSR calculator parameters
            _calculator.SetParameters(attack, decay, sustain, release);
            _calculator.SetMode(triggerMode);
            _calculator.SetDuration(duration);

            // Detect play trigger edges for ADSR
            var risingEdge = shouldPlay && !_previousPlayTrigger;
            var fallingEdge = !shouldPlay && _previousPlayTrigger;
            _previousPlayTrigger = shouldPlay;

            if (useEnvelope)
            {
                if (risingEdge)
                {
                    _calculator.TriggerAttack();
                }
                else if (fallingEdge && triggerMode == AdsrCalculator.TriggerMode.Gate)
                {
                    _calculator.TriggerRelease();
                }

                // Update envelope (frame-based for UI display)
                _calculator.Update(shouldPlay, context.LocalFxTime, attack, decay, sustain, release, triggerMode, duration);
            }

            // Apply envelope to volume only if UseEnvelope is enabled
            var envelopeModulatedVolume = useEnvelope ? volume * _calculator.Value : volume;

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
                filePath: filePath,
                shouldPlay: shouldPlay,
                shouldStop: shouldStop,
                volume: envelopeModulatedVolume,
                mute: mute,
                panning: panning,
                speed: speed,
                seek: seek);

            IsPlaying.Value = AudioEngine.IsOperatorStreamPlaying(_operatorId);
            IsPaused.Value = AudioEngine.IsOperatorPaused(_operatorId);
            GetLevel.Value = AudioEngine.GetOperatorLevel(_operatorId);
            GetWaveform.Value = AudioEngine.GetOperatorWaveform(_operatorId);
            GetSpectrum.Value = AudioEngine.GetOperatorSpectrum(_operatorId);
            EnvelopeValue.Value = useEnvelope ? _calculator.Value : 1f;
        }

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
        }
    }
}