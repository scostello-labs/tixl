using ManagedBass;
using System.Numerics;
using System.Collections.Generic;

namespace Lib.io.audio{
    [Guid("65e95f77-4743-437f-ab31-f34b831d28d7")]
    internal sealed class BassAudioPlayer : Instance<BassAudioPlayer>
    {
        [Input(Guid = "505139a0-71ce-4297-8440-5bf84488902e")]
        private readonly InputSlot<string> AudioFile = new();

        [Input(Guid = "726bc4d3-df8b-4abe-a38e-2e09cf44ca10")]
        private readonly InputSlot<bool> PlayAudio = new();
        
        [Input(Guid = "59b659c6-ca1f-4c2b-8dff-3a1da9abd352")]
        private readonly InputSlot<bool> StopAudio = new();
        
        [Input(Guid = "c0645e37-db4e-4658-9d65-96478851f6f6")]
        private readonly InputSlot<float> Volume = new();
        
        [Input(Guid = "53d1622e-b1d5-4b1c-acd0-ebceb7064043")]
        private readonly InputSlot<float> Panning = new();
        
        [Input(Guid = "d1a11c4c-9526-4f6b-873e-1798b9dd2b48")]
        private readonly InputSlot<float> Speed = new();
        
        [Input(Guid = "a5de0d72-5924-4f3a-a02f-d5de7c03f07f")]
        private readonly InputSlot<float> Seek = new();

        [Output(Guid = "2433f838-a8ba-4f3a-809e-2d41c404bb84")]
        private readonly Slot<Command> Result = new();
        
        [Output(Guid = "960aa0a3-89b4-4eff-8b52-36ff6965cf8f")]
        private readonly Slot<bool> IsPlaying = new();

        [Output(Guid = "b09d215a-bcf0-479a-a649-56f9c698ecb1")]
        private readonly Slot<float> GetLevel = new();

        // Waveform: simple oscilloscope of the most recent audio window
        [Output(Guid = "8f4e2d1a-3b7c-4d89-9e12-7a5b8c9d0e1f")]
        private readonly Slot<List<float>> GetWaveform = new();

        // Spectrum: frequency spectrum analysis of the most recent audio window
        [Output(Guid = "7f8e9d2a-4b5c-3e89-8f12-6a5b9c8d0e2f")]
        private readonly Slot<List<float>> GetSpectrum = new();

        private int _stream;
        private bool _prevPlay;
        private bool _prevStop;
        private float _prevVolume = 1f;
        private float _prevPanning;
        private float _prevSpeed = 1f;
        private float _prevSeek;

        // Internal buffers
        private readonly List<float> _waveformBuffer = new();
        private readonly List<float> _spectrumBuffer = new();
        private const int WaveformSamples = 512;          // samples exposed to UI
        private const int WaveformWindowSamples = 1024;   // raw samples to read from BASS
        private const int SpectrumBands = 512;            // FFT bands exposed to UI

        public BassAudioPlayer()
        {
            Result.UpdateAction += Update; 
        }

        private void Update(EvaluationContext context)
        {
            var filePath = AudioFile.GetValue(context);
            var shouldPlay = PlayAudio.GetValue(context);
            var shouldStop = StopAudio.GetValue(context);
            var volume = Volume.GetValue(context);
            var panning = Panning.GetValue(context);
            var speed = Speed.GetValue(context);
            var seek = Seek.GetValue(context);

            var playTrigger = shouldPlay && !_prevPlay;
            _prevPlay = shouldPlay;

            var stopTrigger = shouldStop && !_prevStop;
            _prevStop = shouldStop;

            // Auto-free if stream completed playback
            if (_stream != 0 && Bass.ChannelIsActive(_stream) == PlaybackState.Stopped)
            {
                Log.Debug($"Stream {_stream} ended, freeing");
                Bass.StreamFree(_stream);
                _stream = 0;
                _waveformBuffer.Clear();
                _spectrumBuffer.Clear();
            }

            // Stop trigger: stop and dispose the stream
            if (stopTrigger && _stream != 0)
            {
                Log.Debug($"Stop trigger: Stopping and freeing stream {_stream}");
                Bass.ChannelStop(_stream);
                Bass.StreamFree(_stream);
                _stream = 0;
                _waveformBuffer.Clear();
                _spectrumBuffer.Clear();
            }

            // Apply controls only if stream exists and parameters changed
            if (_stream != 0)
            {
                // Volume (0-1+ range, only if changed)
                if (Math.Abs(volume - _prevVolume) > 0.001f)
                {
                    var clampedVolume = Math.Max(0f, volume);
                    Bass.ChannelSetAttribute(_stream, ChannelAttribute.Volume, clampedVolume);
                    _prevVolume = volume;
                    Log.Debug($"Volume: {clampedVolume}");
                }
                
                // Panning (-1 to +1 range, only if changed)
                if (Math.Abs(panning - _prevPanning) > 0.001f)
                {
                    var clampedPanning = Math.Max(-1f, Math.Min(1f, panning));
                    Bass.ChannelSetAttribute(_stream, ChannelAttribute.Pan, clampedPanning);
                    _prevPanning = panning;
                    Log.Debug($"Panning: {clampedPanning}");
                }
                
                // Speed via frequency adjustment
                if (Math.Abs(speed - _prevSpeed) > 0.001f)
                {
                    var clampedSpeed = Math.Max(0.1f, Math.Min(4f, speed));
                    var freq = Bass.ChannelGetAttribute(_stream, ChannelAttribute.Frequency);
                    var newFreq = freq * clampedSpeed / _prevSpeed;
                    Bass.ChannelSetAttribute(_stream, ChannelAttribute.Frequency, newFreq);
                    _prevSpeed = clampedSpeed;
                    Log.Debug($"Speed: {clampedSpeed} (freq: {newFreq})");
                }
                
                // Seek (0-1 normalized position, only if changed significantly)
                if (Math.Abs(seek - _prevSeek) > 0.001f && seek >= 0f && seek <= 1f)
                {
                    var length = Bass.ChannelGetLength(_stream);
                    var seekPos = (long)(seek * length);
                    Bass.ChannelSetPosition(_stream, seekPos);
                    _prevSeek = seek;
                    Log.Debug($"Seek to {seek} ({seekPos}/{length})");
                }
            }

            // Play trigger: load/create stream OR restart
            if (playTrigger && !string.IsNullOrEmpty(filePath)) 
            {
                // Always recreate stream on play trigger (eliminates all restart issues)
                if (_stream != 0)
                {
                    Log.Debug($"Recreating stream {_stream} for restart");
                    Bass.StreamFree(_stream);
                    _stream = 0;
                    _waveformBuffer.Clear();
                    _spectrumBuffer.Clear();
                }

                Log.Debug($"Creating new stream {filePath}");
                
                // Create standard playback stream
                _stream = Bass.CreateStream(filePath, 0, 0, BassFlags.Default);

                if (_stream == 0)
                {
                    Log.Debug($"Stream creation failed ({Bass.LastError}). Trying mono...");
                    // Fallback to mono
                    _stream = Bass.CreateStream(filePath, 0, 0, BassFlags.Mono);
                }

                if (_stream != 0)
                {
                    // Initial attributes
                    Bass.ChannelSetAttribute(_stream, ChannelAttribute.Volume, Math.Max(0f, volume));
                    Bass.ChannelSetAttribute(_stream, ChannelAttribute.Pan, Math.Max(-1f, Math.Min(1f, panning)));
                    var baseFreq = Bass.ChannelGetAttribute(_stream, ChannelAttribute.Frequency);
                    Bass.ChannelSetAttribute(_stream, ChannelAttribute.Frequency, baseFreq * Math.Max(0.1f, Math.Min(4f, speed)));

                    // Play the stream
                    var ok = Bass.ChannelPlay(_stream, false);
                    if (!ok)
                    {
                        Log.Debug($"ChannelPlay failed: {Bass.LastError}");
                        Bass.StreamFree(_stream);
                        _stream = 0;
                        _waveformBuffer.Clear();
                        _spectrumBuffer.Clear();
                    }
                    else
                    {
                        Log.Debug($"Stream {_stream} started successfully");
                    }

                    _prevVolume = volume;
                    _prevPanning = panning;
                    _prevSpeed = speed;
                    _prevSeek = 0f;
                }
                else 
                {
                    Log.Debug($"All stream creation failed. Last error: {Bass.LastError}");
                }
            }

            // Always update outputs at end to reflect current state
            UpdateOutputs();
        }

        private void UpdateOutputs()
        {
            var isPlaying = _stream != 0 && Bass.ChannelIsActive(_stream) == PlaybackState.Playing;
            IsPlaying.Value = isPlaying;

            // Update audio levels (peak amplitude for left/right channels, normalized 0-1)

            
            
            if (_stream != 0 && Bass.ChannelIsActive(_stream) != PlaybackState.Stopped)
            {
                var level = Bass.ChannelGetLevel(_stream);
                
                if (level != -1) // exactly -1 is a capture error, do not measure it
                {
                    GetLevel.Value = (float)(((level & 0xffff) + ((level >> 16) & 0xffff))
                                             * short.MaxValue * 0.00001);
                }
            }
            else
            {
                GetLevel.Value = 0f;
            }

            // Waveform: grab recent PCM data if playing
            if (_stream != 0 && Bass.ChannelIsActive(_stream) == PlaybackState.Playing)
            {
                UpdateWaveformFromPcm();
            }
            else
            {
                if (_waveformBuffer.Count == 0)
                {
                    // Ensure non-null, non-changing reference
                    for (int i = 0; i < WaveformSamples; i++)
                        _waveformBuffer.Add(0f);
                }
            }

            // Spectrum: compute frequency spectrum if playing
            if (_stream != 0 && Bass.ChannelIsActive(_stream) == PlaybackState.Playing)
            {
                UpdateSpectrum();
            }
            else
            {
                if (_spectrumBuffer.Count == 0)
                {
                    // Ensure non-null, non-changing reference
                    for (int i = 0; i < SpectrumBands; i++)
                        _spectrumBuffer.Add(0f);
                }
            }

            GetWaveform.Value = _waveformBuffer;
            GetSpectrum.Value = _spectrumBuffer;
        }

        private void UpdateWaveformFromPcm()
        {
            // Get channel info to know format & channels
            var info = Bass.ChannelGetInfo(_stream);

            // Use 16-bit integer PCM buffer (BASS will convert if needed)
            int sampleCount = WaveformWindowSamples * info.Channels;
            var buffer = new short[sampleCount];

            int bytesRequested = sampleCount * sizeof(short);
            int bytesReceived = Bass.ChannelGetData(_stream, buffer, bytesRequested);

            if (bytesReceived <= 0)
                return;

            int samplesReceived = bytesReceived / sizeof(short);
            int frames = samplesReceived / info.Channels;

            if (frames <= 0)
                return;

            _waveformBuffer.Clear();

            // Downsample to WaveformSamples by stepping
            float step = frames / (float)WaveformSamples;
            float pos = 0f;

            for (int i = 0; i < WaveformSamples; i++)
            {
                int frameIndex = (int)pos;
                if (frameIndex >= frames)
                    frameIndex = frames - 1;

                // Compute mono amplitude as average abs across channels
                int frameBase = frameIndex * info.Channels;
                float sum = 0f;

                for (int ch = 0; ch < info.Channels; ch++)
                {
                    short s = buffer[frameBase + ch];
                    sum += Math.Abs(s / 32768f);
                }

                float amp = sum / info.Channels;
                _waveformBuffer.Add(amp);

                pos += step;
            }
        }

        private void UpdateSpectrum()
        {
            // Use BASS_DATA_FFT256 for efficient 256-band FFT analysis
            float[] spectrum = new float[SpectrumBands];
            int bytes = Bass.ChannelGetData(_stream, spectrum, (int)DataFlags.FFT512);

            if (bytes <= 0)
                return;

            _spectrumBuffer.Clear();

            // Convert FFT magnitudes to dB-normalized values (0-1 range)
            for (int i = 0; i < SpectrumBands; i++)
            {
                // Logarithmic scaling for perceptual uniformity
                var db = 20f * Math.Log10(Math.Max(spectrum[i], 1e-5f));
                // Normalize to 0-1 range (-60dB to 0dB -> 0-1)
                var normalized = Math.Max(0f, Math.Min(1f, (db + 60f) / 60f));
                _spectrumBuffer.Add((float)normalized);
            }
        }

        ~BassAudioPlayer()
        {
            if (_stream != 0)
            {
                Bass.StreamFree(_stream);
                _stream = 0;
            }
        }
    }
}
