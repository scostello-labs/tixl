using T3.Core.Operator;
using T3.Core.Operator.Attributes;
using T3.Core.Operator.Slots;
using System.Runtime.InteropServices;
using ManagedBass;
using ManagedBass.Wasapi;
using T3.Core.Logging;

namespace Lib.io.audio{
    [Guid("65e95f77-4743-437f-ab31-f34b831d28d7")]
    internal sealed class BassAudioPlayer : Instance<BassAudioPlayer>
    {
        [Input(Guid = "505139a0-71ce-4297-8440-5bf84488902e")]
        private readonly InputSlot<string> AudioFile = new InputSlot<string>();

        [Input(Guid = "726bc4d3-df8b-4abe-a38e-2e09cf44ca10")]
        private readonly InputSlot<bool> PlayAudio = new InputSlot<bool>();
        
        [Input(Guid = "59b659c6-ca1f-4c2b-8dff-3a1da9abd352")]
        private readonly InputSlot<bool> StopAudio = new InputSlot<bool>();
        
        
        [Input(Guid = "c0645e37-db4e-4658-9d65-96478851f6f6")]
        private readonly InputSlot<float> Volume = new InputSlot<float>();
        
        [Input(Guid = "d1a11c4c-9526-4f6b-873e-1798b9dd2b48")]
        private readonly InputSlot<float> Speed = new InputSlot<float>();
        
        [Input(Guid = "a5de0d72-5924-4f3a-a02f-d5de7c03f07f")]
        private readonly InputSlot<float> Seek = new InputSlot<float>();

        [Output(Guid = "2433f838-a8ba-4f3a-809e-2d41c404bb84")]
        private readonly Slot<Command> Result = new Slot<T3.Core.DataTypes.Command>();

        private int _stream;
        private static bool _bassInitialized;
        private bool _prevPlay = false;
        private bool _prevStop = false;

        public BassAudioPlayer()
        {
            Result.UpdateAction += Update; 
        }

        private void Update(EvaluationContext context)
        {
            var filePath = AudioFile.GetValue(context);
            var shouldPlay = PlayAudio.GetValue(context);
            var shouldStop = StopAudio.GetValue(context);

            bool playTrigger = shouldPlay && !_prevPlay;
            _prevPlay = shouldPlay;

            bool stopTrigger = shouldStop && !_prevStop;
            _prevStop = shouldStop;

            if (!_bassInitialized)
            {
                Log.Debug("Initializing BASS...");
                if (!Bass.Init())
                {
                    if (Bass.LastError == Errors.Already)
                    {
                        Log.Debug($"BASS Already Initialized");
                        _bassInitialized = true;
                    }
                    else Log.Debug($"BASS.Init failed: {Bass.LastError}");
                    return;
                }
                Log.Debug("BASS initialized successfully");
                _bassInitialized = true;
            }

            // Auto-free if stream completed playback
            if (_stream != 0 && Bass.ChannelIsActive(_stream) == PlaybackState.Stopped)
            {
                Log.Debug($"Stream {_stream} ended, freeing");
                Bass.StreamFree(_stream);
                _stream = 0;
            }

            // Stop trigger: stop and dispose the stream
            if (stopTrigger && _stream != 0)
            {
                Log.Debug($"Stop trigger: Stopping and freeing stream {_stream}");
                Bass.ChannelStop(_stream);
                Bass.StreamFree(_stream);
                _stream = 0;
            }

            // Play trigger: if playing, restart from beginning; else load new stream
            if (playTrigger && !string.IsNullOrEmpty(filePath))
            {
                if (_stream != 0)
                {
                    // Restart existing stream from beginning
                    Log.Debug($"Play trigger while playing: restarting stream {_stream}");
                    Bass.ChannelSetPosition(_stream, 0, PositionFlags.MusicPositionReset); // Reset to start
                    Bass.ChannelPlay(_stream, true); // true = restart
                }
                else
                {
                    // Create new stream
                    Log.Debug($"Play trigger: Loading and playing {filePath}");
                    _stream = Bass.CreateStream(filePath);
                    
                    if (_stream != 0)
                    {
                        Bass.ChannelPlay(_stream, false);
                        Log.Debug($"Started full playback on stream {_stream}");
                    }
                    else Log.Debug($"Failed to create stream: {Bass.LastError}");
                }
            }
        }

        ~BassAudioPlayer()
        {
            if (_stream != 0)
            {
                Bass.StreamFree(_stream);
                _stream = 0;
            }
            if (_bassInitialized)
            {
                Log.Debug("Freeing BASS");
                Bass.Free();
                _bassInitialized = false;
            }
        }
    }
}
