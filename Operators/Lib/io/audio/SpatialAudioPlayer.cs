using ManagedBass;
using T3.Core.Audio;
// ReSharper disable MemberCanBePrivate.Global
// ReSharper disable UnusedMember.Local

namespace Lib.io.audio
{
    /// <summary>
    /// A spatial audio player operator that provides 3D positional audio playback capabilities.
    /// Supports sound positioning, listener orientation, distance-based attenuation, and directional sound cones.
    /// </summary>
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

        /// <summary>
        /// The orientation direction of the audio source for directional sound.
        /// </summary>
        [Input(Guid = "1b2c3d4e-5f6a-7b8c-9d0e-1f2a3b4c5d6e")]
        public readonly InputSlot<Vector3> SourceOrientation = new();

        /// <summary>
        /// The inner cone angle in degrees (0-360). Within this angle, the sound is at full volume.
        /// </summary>
        [Input(Guid = "2c3d4e5f-6a7b-8c9d-0e1f-2a3b4c5d6e7f")]
        public readonly InputSlot<float> InnerConeAngle = new();

        /// <summary>
        /// The outer cone angle in degrees (0-360). Outside this angle, the sound is at OuterConeVolume.
        /// </summary>
        [Input(Guid = "3d4e5f6a-7b8c-9d0e-1f2a-3b4c5d6e7f8a")]
        public readonly InputSlot<float> OuterConeAngle = new();

        /// <summary>
        /// The volume level outside the outer cone (0.0 to 1.0).
        /// </summary>
        [Input(Guid = "4e5f6a7b-8c9d-0e1f-2a3b-4c5d6e7f8a9b")]
        public readonly InputSlot<float> OuterConeVolume = new();

        /// <summary>
        /// The 3D audio processing mode: Normal (0), Relative (1), or Off (2).
        /// </summary>
        [Input(Guid = "5f6a7b8c-9d0e-1f2a-3b4c-5d6e7f8a9b0c", MappedType = typeof(Audio3DModes))]
        public readonly InputSlot<int> Audio3DMode = new();

        /// <summary>
        /// Command output for chaining with other operators.
        /// </summary>
        [Output(Guid = "4a8e2f7c-9d3b-4c1f-8e5a-7b2d6f9c3a4e")]
        public readonly Slot<Command> Result = new();

        /// <summary>
        /// Indicates whether the audio is currently playing.
        /// </summary>
        [Output(Guid = "9c2f7a4e-3d8b-4a1f-8e5c-6b2d9a7f4c3e")]
        public readonly Slot<bool> IsPlaying = new();

        /// <summary>
        /// Indicates whether the audio is currently paused.
        /// </summary>
        [Output(Guid = "6e4a2f9c-7d3b-4c8f-9a1e-2b7d5f8c6a3e")]
        public readonly Slot<bool> IsPaused = new();

        /// <summary>
        /// Returns the current audio level/amplitude.
        /// </summary>
        [Output(Guid = "3f9a2e7c-4d8b-4c1f-8a5e-7b2d6f9c3a4e")]
        public readonly Slot<float> GetLevel = new();

        private Guid _operatorId;
        private bool _wasPausedLastFrame;

        /// <summary>
        /// Gets the current audio file path being played.
        /// </summary>
        public string CurrentFilePath { get; private set; } = string.Empty;

        /// <summary>
        /// Initializes a new instance of the <see cref="SpatialAudioPlayer"/> class.
        /// </summary>
        public SpatialAudioPlayer()
        {
            Result.UpdateAction += Update;
            IsPlaying.UpdateAction += Update;
            IsPaused.UpdateAction += Update;
            
            // Do not update on GetLevel - it overrides stale state when result is not evaluating
            //GetLevel.UpdateAction += Update;
        }

        /// <summary>
        /// Updates the spatial audio player state and processes playback.
        /// </summary>
        /// <param name="context">The evaluation context for the current frame.</param>
        private void Update(EvaluationContext context)
        {
            if (_operatorId == Guid.Empty)
            {
                _operatorId = AudioPlayerUtils.ComputeInstanceGuid(InstancePath);
                AudioConfig.LogAudioDebug($"[SpatialAudioPlayer] Initialized: {_operatorId}");
            }

            string filePath = AudioFile.GetValue(context);
            bool shouldPlay = PlayAudio.GetValue(context);

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
        }

        /// <summary>
        /// Render audio for export. This is called by AudioRendering during export.
        /// </summary>
        /// <param name="startTime">The start time in seconds.</param>
        /// <param name="duration">The duration to render in seconds.</param>
        /// <param name="buffer">The buffer to write audio samples to.</param>
        /// <returns>The number of samples written to the buffer.</returns>
        public int RenderAudio(double startTime, double duration, float[] buffer)
        {
            if (AudioEngine.TryGetSpatialOperatorStream(_operatorId, out var stream) && stream != null)
                return stream.RenderAudio(startTime, duration, buffer, AudioConfig.MixerFrequency, 2);

            Array.Clear(buffer, 0, buffer.Length);
            return buffer.Length;
        }

        /// <summary>
        /// Restores the volume level after an audio export operation.
        /// </summary>
        public void RestoreVolumeAfterExport()
        {
            if (AudioEngine.TryGetSpatialOperatorStream(_operatorId, out var stream) && stream != null)
                Bass.ChannelSetAttribute(stream.StreamHandle, ChannelAttribute.Volume, Volume.Value);
        }

        /// <summary>
        /// Finalizer that unregisters the operator from the audio engine.
        /// </summary>
        ~SpatialAudioPlayer()
        {
            if (_operatorId != Guid.Empty)
                AudioEngine.UnregisterOperator(_operatorId);
        }

        /// <summary>
        /// Defines the 3D audio processing modes.
        /// </summary>
        private enum Audio3DModes
        {
            /// <summary>
            /// Normal 3D audio processing with absolute positioning.
            /// </summary>
            Normal = 0,

            /// <summary>
            /// Relative 3D audio processing where the source position is relative to the listener.
            /// Example: footsteps, breathing, or equipment sounds attached to the listener's position.
            /// </summary>
            Relative = 1,

            /// <summary>
            /// Disables 3D audio processing.
            /// </summary>
            Off = 2
        }
    }
}
