using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using ManagedBass;
using ManagedBass.Mix;
using T3.Core.Audio;
using T3.Core.Logging;
using System.Numerics; // Required for Vector4

namespace Lib.io.audio
{
    /// <summary>
    /// Generates test tones procedurally in real-time via the operator mixer.
    /// No external files are created - audio is synthesized on-the-fly.
    /// Supports two input modes:
    /// - Trigger: A pulse (0→1) starts playback for the specified duration
    /// - Gate: Sound plays while input is true, releases when false (duration ignored)
    /// Includes ADSR envelope for shaping the amplitude over time.
    /// </summary>
    [Guid("7c8f3a2e-9d4b-4e1f-8a5c-6b2d9f7e4c3a")]
    internal sealed class TestToneGenerator : Instance<TestToneGenerator>, IAudioExportSource
    {
        [Input(Guid = "3e9a7f2c-4d8b-4c1f-9e5a-2b7d6f8c4a5e")]
        public readonly InputSlot<bool> Trigger = new();

        [Input(Guid = "8f4a2e9c-7d3b-4e1f-8c5a-9b2d6f7c3a4e")]
        public readonly InputSlot<float> Frequency = new();

        [Input(Guid = "2c9f4e7a-3d8b-4a1f-9e5c-6b2d7f8c4a3e")]
        public readonly InputSlot<float> Duration = new();

        [Input(Guid = "c0645e37-db4e-4658-9d65-96478851f6f6")]
        public readonly InputSlot<float> Volume = new();

        [Input(Guid = "1a3f4b7c-12d3-4a5b-9c7d-8e1f2a3b4c5d")]
        public readonly InputSlot<bool> Mute = new();

        [Input(Guid = "53d1622e-b1d5-4b1c-acd0-ebceb7064043")]
        public readonly InputSlot<float> Panning = new();

        [Input(Guid = "5a7e9f2c-8d4b-4c1f-9a5e-3b2d6f7c8a4e", MappedType = typeof(WaveformTypes))]
        public readonly InputSlot<int> WaveformType = new();

        [Input(Guid = "d4e5f6a7-b8c9-4d0e-1f2a-3b4c5d6e7f8a", MappedType = typeof(TriggerModes))]
        public readonly InputSlot<int> TriggerMode = new();

        // ADSR Envelope as Vector4: X=Attack, Y=Decay, Z=Sustain, W=Release
        [Input(Guid = "a1b2c3d4-e5f6-4a7b-8c9d-0e1f2a3b4c5d", MappedType = typeof(AdsrMapping))]
        public readonly InputSlot<Vector4> Envelope = new();

        [Output(Guid = "b7e2c1a4-5d3f-4e8a-9c2f-1e4b7a6c3d8f")]
        public readonly Slot<Command> Result = new();

        [Output(Guid = "960aa0a3-89b4-4eff-8b52-36ff6965cf8f")]
        public readonly Slot<bool> IsPlaying = new();

        [Output(Guid = "b09d215a-bcf0-479a-a649-56f9c698ecb1")]
        public readonly Slot<float> GetLevel = new();

        [Output(Guid = "8f4e2d1a-3b7c-4d89-9e12-7a5b8c9d0e1f")]
        public readonly Slot<List<float>> GetWaveform = new();

        [Output(Guid = "7f8e9d2a-4b5c-3e89-8f12-6a5b9c8d0e2f")]
        public readonly Slot<List<float>> GetSpectrum = new();

        [Output(Guid = "e5f6a7b8-c9d0-4e1f-2a3b-4c5d6e7f8a9b")]
        public readonly Slot<float> EnvelopeValue = new();

        private Guid _operatorId;
        private ProceduralToneStream? _toneStream;
        private bool _previousTrigger;
        private bool _isRegistered;

        public TestToneGenerator()
        {
            Result.UpdateAction += Update;
            IsPlaying.UpdateAction += Update;
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
                AudioConfig.LogAudioDebug($"[TestToneGenerator] Initialized: {_operatorId}");
            }

            var trigger = Trigger.GetValue(context);
            var frequency = Frequency.GetValue(context);
            var duration = Duration.GetValue(context);
            var volume = Volume.GetValue(context);
            var mute = Mute.GetValue(context);
            var panning = Panning.GetValue(context);
            var waveformType = WaveformType.GetValue(context);
            var triggerMode = (TriggerModes)TriggerMode.GetValue(context);

            // ADSR envelope from Vector4: X=Attack, Y=Decay, Z=Sustain, W=Release
            var envelope = Envelope.GetValue(context);
            var attack = envelope.X > 0 ? envelope.X : 0.01f;
            var decay = envelope.Y > 0 ? envelope.Y : 0.1f;
            var sustain = envelope.Z >= 0 ? Math.Clamp(envelope.Z, 0f, 1f) : 0.7f;
            var release = envelope.W > 0 ? envelope.W : 0.3f;

            // Apply defaults
            if (frequency <= 0) frequency = 440f;
            if (duration <= 0) duration = float.MaxValue; // 0 or negative = infinite

            // Ensure stream is created
            EnsureToneStream();

            if (_toneStream == null)
            {
                IsPlaying.Value = false;
                GetLevel.Value = 0;
                GetWaveform.Value = null;
                GetSpectrum.Value = null;
                EnvelopeValue.Value = 0;
                return;
            }

            // Update parameters (can be changed while playing)
            _toneStream.Frequency = frequency;
            _toneStream.WaveformType = (WaveformTypes)waveformType;
            _toneStream.SetAdsr(attack, decay, sustain, release);
            _toneStream.SetPanning(panning); // Always update panning

            // Detect edges
            var risingEdge = trigger && !_previousTrigger;
            var fallingEdge = !trigger && _previousTrigger;
            _previousTrigger = trigger;

            if (triggerMode == TriggerModes.Trigger)
            {
                // TRIGGER MODE: Rising edge starts playback for specified duration
                // Duration controls when release phase starts automatically
                if (risingEdge)
                {
                    _toneStream.DurationSeconds = duration;
                    _toneStream.UseDurationLimit = true;
                    _toneStream.Play();
                    AudioConfig.LogAudioDebug($"[TestToneGenerator] ▶ Trigger @ {frequency}Hz for {(duration < float.MaxValue ? $"{duration}s" : "∞")}");
                }
            }
            else // Gate mode
            {
                // GATE MODE: Sound plays while input is true, releases when false
                // Duration is ignored - envelope follows the gate signal
                if (risingEdge)
                {
                    _toneStream.DurationSeconds = float.MaxValue;
                    _toneStream.UseDurationLimit = false;
                    _toneStream.Play();
                    AudioConfig.LogAudioDebug($"[TestToneGenerator] ▶ Gate ON @ {frequency}Hz");
                }
                else if (fallingEdge && _toneStream.IsPlaying)
                {
                    _toneStream.StartRelease();
                    AudioConfig.LogAudioDebug($"[TestToneGenerator] ~ Gate OFF - Release started");
                }
            }

            // Check if envelope finished (release completed)
            if (_toneStream.HasFinished)
            {
                _toneStream.Stop();
                AudioConfig.LogAudioDebug($"[TestToneGenerator] ■ Finished");
            }

            // Update volume/mute while playing
            if (_toneStream.IsPlaying)
            {
                _toneStream.SetVolume(volume, mute);
            }

            // Register for export if playing
            if (_toneStream.IsPlaying && !_isRegistered)
            {
                AudioExportSourceRegistry.Register(this);
                _isRegistered = true;
            }
            else if (!_toneStream.IsPlaying && _isRegistered)
            {
                AudioExportSourceRegistry.Unregister(this);
                _isRegistered = false;
            }

            IsPlaying.Value = _toneStream.IsPlaying;
            GetLevel.Value = _toneStream.GetLevel();
            GetWaveform.Value = _toneStream.GetWaveform();
            GetSpectrum.Value = _toneStream.GetSpectrum();
            EnvelopeValue.Value = _toneStream.GetEnvelopeValue();
        }

        private void EnsureToneStream()
        {
            if (_toneStream != null) return;

            if (AudioMixerManager.OperatorMixerHandle == 0)
            {
                AudioMixerManager.Initialize();
                if (AudioMixerManager.OperatorMixerHandle == 0)
                {
                    Log.Warning("[TestToneGenerator] Mixer not available");
                    return;
                }
            }

            _toneStream = ProceduralToneStream.Create(AudioMixerManager.OperatorMixerHandle);
            if (_toneStream != null)
                AudioConfig.LogAudioDebug($"[TestToneGenerator] Created procedural tone stream");
        }

        public int RenderAudio(double startTime, double duration, float[] buffer)
        {
            if (_toneStream == null || !_toneStream.IsPlaying)
            {
                Array.Clear(buffer, 0, buffer.Length);
                return buffer.Length;
            }

            return _toneStream.RenderAudio(startTime, duration, buffer);
        }

        ~TestToneGenerator()
        {
            if (_isRegistered)
                AudioExportSourceRegistry.Unregister(this);

            _toneStream?.Dispose();
        }

        private enum WaveformTypes
        {
            Sine = 0,
            Square = 1,
            Sawtooth = 2,
            Triangle = 3,
            WhiteNoise = 4,
            PinkNoise = 5
        }

        private enum TriggerModes
        {
            Trigger = 0,
            Gate = 1
        }

        /// <summary>
        /// Marker enum for MappedType to trigger ADSR envelope UI for Vector4
        /// </summary>
        public enum AdsrMapping { }

        /// <summary>
        /// ADSR envelope stages
        /// </summary>
        private enum EnvelopeStage
        {
            Idle,
            Attack,
            Decay,
            Sustain,
            Release
        }

        /// <summary>
        /// Procedural tone stream that generates waveforms in real-time via BASS callback stream.
        /// Uses a StreamProcedure callback to ensure continuous, accurate sample generation.
        /// Includes ADSR envelope for amplitude shaping.
        /// </summary>
        private sealed class ProceduralToneStream
        {
            private const int SampleRate = 48000;
            private const int Channels = 2;

            // Thread-safe properties using volatile/interlocked
            private volatile float _frequency = 440f;
            private volatile int _waveformType;
            private volatile int _isPlaying;
            private volatile float _currentVolume = 1.0f;
            private volatile float _currentPanning;
            private volatile bool _isMuted;

            // Duration tracking (sample-accurate)
            private volatile float _durationSeconds = float.MaxValue;
            private volatile bool _useDurationLimit = true;
            private long _samplesGenerated;
            private long _totalSamplesToGenerate;
            private volatile int _hasFinished;

            // ADSR envelope parameters (in seconds)
            private volatile float _attackTime = 0.01f;
            private volatile float _decayTime = 0.1f;
            private volatile float _sustainLevel = 0.7f;
            private volatile float _releaseTime = 0.3f;

            // ADSR state
            private volatile int _envelopeStage = (int)EnvelopeStage.Idle;
            private double _envelopeValue;
            private long _envelopeSampleCount;
            private double _releaseStartValue;

            public float Frequency
            {
                get => _frequency;
                set => _frequency = Math.Max(20f, Math.Min(20000f, value));
            }

            public float DurationSeconds
            {
                get => _durationSeconds;
                set => _durationSeconds = value;
            }

            public bool UseDurationLimit
            {
                get => _useDurationLimit;
                set => _useDurationLimit = value;
            }

            public WaveformTypes WaveformType
            {
                get => (WaveformTypes)_waveformType;
                set => _waveformType = (int)value;
            }

            public bool IsPlaying => _isPlaying == 1;
            public bool HasFinished => _hasFinished == 1;

            private int _streamHandle;
            private readonly int _mixerHandle;
            private double _phase;

            private readonly List<float> _waveformBuffer = new();
            private readonly List<float> _spectrumBuffer = new();
            private float _lastLevel;

            // Must keep a reference to prevent GC from collecting the delegate
            private readonly StreamProcedure _streamProc;
            private GCHandle _gcHandle;

            private ProceduralToneStream(int mixerHandle)
            {
                _mixerHandle = mixerHandle;
                _streamProc = StreamCallback;
            }

            public void SetAdsr(float attack, float decay, float sustain, float release)
            {
                _attackTime = Math.Max(0.001f, attack);
                _decayTime = Math.Max(0.001f, decay);
                _sustainLevel = Math.Clamp(sustain, 0f, 1f);
                _releaseTime = Math.Max(0.001f, release);
            }

            public float GetEnvelopeValue() => (float)_envelopeValue;

            public void StartRelease()
            {
                if (_envelopeStage != (int)EnvelopeStage.Idle && _envelopeStage != (int)EnvelopeStage.Release)
                {
                    _releaseStartValue = _envelopeValue;
                    _envelopeSampleCount = 0;
                    Interlocked.Exchange(ref _envelopeStage, (int)EnvelopeStage.Release);
                }
            }

            public static ProceduralToneStream? Create(int mixerHandle)
            {
                var instance = new ProceduralToneStream(mixerHandle);

                // Pin the instance to prevent GC issues with the callback
                instance._gcHandle = GCHandle.Alloc(instance);

                // Create a callback stream for procedural audio
                var streamHandle = Bass.CreateStream(
                    SampleRate,
                    Channels,
                    BassFlags.Float | BassFlags.Decode,
                    instance._streamProc,
                    GCHandle.ToIntPtr(instance._gcHandle));

                if (streamHandle == 0)
                {
                    Log.Error($"[ProceduralToneStream] Failed to create stream: {Bass.LastError}");
                    instance._gcHandle.Free();
                    return null;
                }

                instance._streamHandle = streamHandle;

                // Add to mixer (paused initially)
                if (!BassMix.MixerAddChannel(mixerHandle, streamHandle, BassFlags.MixerChanBuffer | BassFlags.MixerChanPause))
                {
                    Log.Error($"[ProceduralToneStream] Failed to add to mixer: {Bass.LastError}");
                    Bass.StreamFree(streamHandle);
                    instance._gcHandle.Free();
                    return null;
                }

                Bass.ChannelSetAttribute(streamHandle, ChannelAttribute.Volume, 0f);
                return instance;
            }

            private int StreamCallback(int handle, IntPtr buffer, int length, IntPtr user)
            {
                int floatCount = length / sizeof(float);
                int sampleCount = floatCount / Channels;

                var floatBuffer = new float[floatCount];

                bool playing = _isPlaying == 1;
                float freq = _frequency;
                int waveType = _waveformType;
                float pan = _currentPanning;

                // Check duration limit (only applies in Trigger mode)
                long totalLimit = _totalSamplesToGenerate;
                bool useDurationLimit = _useDurationLimit;
                bool hasLimit = useDurationLimit && totalLimit > 0 && totalLimit < long.MaxValue;

                // Determine effective volume (0 if not playing or muted)
                float baseVol = playing && !_isMuted ? _currentVolume : 0f;

                double phaseIncrement = 2.0 * Math.PI * freq / SampleRate;

                // ADSR parameters
                float attackTime = _attackTime;
                float decayTime = _decayTime;
                float sustainLevel = _sustainLevel;
                float releaseTime = _releaseTime;

                long attackSamples = (long)(attackTime * SampleRate);
                long decaySamples = (long)(decayTime * SampleRate);
                long releaseSamples = (long)(releaseTime * SampleRate);

                // Always generate the full buffer (envelope needs to complete release)
                for (int i = 0; i < sampleCount; i++)
                {
                    // Track samples generated toward duration (only when playing and not in release/idle)
                    bool inActivePhase = _envelopeStage != (int)EnvelopeStage.Release && _envelopeStage != (int)EnvelopeStage.Idle;
                    
                    if (playing && inActivePhase)
                    {
                        _samplesGenerated++;
                        
                        // Check if duration reached and we need to start release (only in Trigger mode)
                        if (hasLimit && _samplesGenerated >= totalLimit)
                        {
                            _releaseStartValue = _envelopeValue;
                            _envelopeSampleCount = 0;
                            Interlocked.Exchange(ref _envelopeStage, (int)EnvelopeStage.Release);
                        }
                    }

                    // Calculate envelope (this internally manages _envelopeSampleCount)
                    float envelopeGain = CalculateEnvelope(
                        attackSamples, decaySamples, sustainLevel, releaseSamples);

                    float sample = GenerateSampleStatic(_phase, waveType) * baseVol * envelopeGain;
                    _phase += phaseIncrement;

                    // Keep phase in reasonable range to avoid precision loss
                    if (_phase >= 2.0 * Math.PI)
                        _phase -= 2.0 * Math.PI;

                    // Apply panning
                    float leftGain = pan <= 0 ? 1f : 1f - pan;
                    float rightGain = pan >= 0 ? 1f : 1f + pan;

                    floatBuffer[i * 2] = sample * leftGain;
                    floatBuffer[i * 2 + 1] = sample * rightGain;
                }

                // Check if release phase completed
                if (_envelopeStage == (int)EnvelopeStage.Release && _envelopeValue <= 0.0001)
                {
                    Interlocked.Exchange(ref _hasFinished, 1);
                }

                // Also finish if we're idle after release
                if (_envelopeStage == (int)EnvelopeStage.Idle && _hasFinished == 0 && _samplesGenerated > 0)
                {
                    Interlocked.Exchange(ref _hasFinished, 1);
                }

                // Copy to unmanaged buffer
                Marshal.Copy(floatBuffer, 0, buffer, floatCount);

                return length;
            }

            private float CalculateEnvelope(long attackSamples, long decaySamples, float sustainLevel, long releaseSamples)
            {
                var stage = (EnvelopeStage)_envelopeStage;

                switch (stage)
                {
                    case EnvelopeStage.Idle:
                        _envelopeValue = 0;
                        break;

                    case EnvelopeStage.Attack:
                        if (_envelopeSampleCount < attackSamples)
                        {
                            // Linear attack from 0 to 1
                            _envelopeValue = (double)_envelopeSampleCount / attackSamples;
                            _envelopeSampleCount++;
                        }
                        else
                        {
                            // Move to decay
                            _envelopeValue = 1.0;
                            _envelopeSampleCount = 0;
                            Interlocked.Exchange(ref _envelopeStage, (int)EnvelopeStage.Decay);
                        }
                        break;

                    case EnvelopeStage.Decay:
                        if (_envelopeSampleCount < decaySamples)
                        {
                            // Linear decay from 1 to sustain level
                            double decayProgress = (double)_envelopeSampleCount / decaySamples;
                            _envelopeValue = 1.0 - decayProgress * (1.0 - sustainLevel);
                            _envelopeSampleCount++;
                        }
                        else
                        {
                            // Move to sustain
                            _envelopeValue = sustainLevel;
                            _envelopeSampleCount = 0;
                            Interlocked.Exchange(ref _envelopeStage, (int)EnvelopeStage.Sustain);
                        }
                        break;

                    case EnvelopeStage.Sustain:
                        _envelopeValue = sustainLevel;
                        // Stay in sustain until release is triggered (no sample counting needed)
                        break;

                    case EnvelopeStage.Release:
                        if (_envelopeSampleCount < releaseSamples)
                        {
                            // Linear release from release start value to 0
                            double releaseProgress = (double)_envelopeSampleCount / releaseSamples;
                            _envelopeValue = _releaseStartValue * (1.0 - releaseProgress);
                            _envelopeSampleCount++;
                        }
                        else
                        {
                            _envelopeValue = 0;
                            Interlocked.Exchange(ref _envelopeStage, (int)EnvelopeStage.Idle);
                        }
                        break;
                }

                return (float)Math.Max(0, Math.Min(1, _envelopeValue));
            }

            private static readonly Random _noiseRng = new();
            // Pink noise filter state
            private static double _pink_b0, _pink_b1, _pink_b2;

            private static float GenerateSampleStatic(double phase, int waveType)
            {
                // Normalize phase to [0, 2π)
                double normalizedPhase = phase - Math.Floor(phase / (2.0 * Math.PI)) * 2.0 * Math.PI;
                double t = normalizedPhase / (2.0 * Math.PI); // t in [0, 1)

                switch (waveType)
                {
                    case 0: // Sine
                        return (float)Math.Sin(normalizedPhase);
                    case 1: // Square
                        return t < 0.5 ? 0.8f : -0.8f;
                    case 2: // Sawtooth
                        return (float)(2.0 * t - 1.0) * 0.8f;
                    case 3: // Triangle
                        return (float)(4.0 * Math.Abs(t - 0.5) - 1.0) * 0.8f;
                    case 4: // WhiteNoise
                        return (float)(_noiseRng.NextDouble() * 2.0 - 1.0) * 0.5f;
                    case 5: // PinkNoise (simple filter)
                        {
                            // White noise input
                            double white = _noiseRng.NextDouble() * 2.0 - 1.0;
                            // Paul Kellet's simple pink filter
                            _pink_b0 = 0.99765 * _pink_b0 + white * 0.0990460;
                            _pink_b1 = 0.96300 * _pink_b1 + white * 0.2965164;
                            _pink_b2 = 0.57000 * _pink_b2 + white * 1.0526913;
                            double pink = _pink_b0 + _pink_b1 + _pink_b2 + white * 0.1848;
                            return (float)(pink * 0.15); // scale to avoid clipping
                        }
                    default:
                        return (float)Math.Sin(normalizedPhase);
                }
            }

            public void Play()
            {
                // Always restart on trigger (even if already playing)
                Interlocked.Exchange(ref _isPlaying, 1);

                _phase = 0;
                _samplesGenerated = 0; // Ensure duration starts fresh
                Interlocked.Exchange(ref _hasFinished, 0);

                // Reset envelope to attack phase
                _envelopeValue = 0;
                _envelopeSampleCount = 0;
                Interlocked.Exchange(ref _envelopeStage, (int)EnvelopeStage.Attack);

                // Calculate total samples based on duration
                float dur = _durationSeconds;
                _totalSamplesToGenerate = dur >= float.MaxValue / 2f
                    ? long.MaxValue
                    : (long)(dur * SampleRate);

                // Unpause the channel
                BassMix.ChannelFlags(_streamHandle, 0, BassFlags.MixerChanPause);
                Bass.ChannelSetAttribute(_streamHandle, ChannelAttribute.Volume, _isMuted ? 0f : _currentVolume);
            }

            public void Stop()
            {
                if (Interlocked.Exchange(ref _isPlaying, 0) == 0)
                    return; // Already stopped

                // Mute but keep channel running (don't pause - avoids buffer issues)
                Bass.ChannelSetAttribute(_streamHandle, ChannelAttribute.Volume, 0f);
                _phase = 0;
                _samplesGenerated = 0;
                _envelopeValue = 0;
                _envelopeSampleCount = 0;
                Interlocked.Exchange(ref _envelopeStage, (int)EnvelopeStage.Idle);
                Interlocked.Exchange(ref _hasFinished, 0);
            }

            public void SetVolume(float volume, bool mute)
            {
                _currentVolume = Math.Max(0f, Math.Min(1f, volume));
                _isMuted = mute;

                if (_isPlaying == 1)
                    Bass.ChannelSetAttribute(_streamHandle, ChannelAttribute.Volume, mute ? 0f : _currentVolume);
            }

            public void SetPanning(float panning)
            {
                _currentPanning = Math.Clamp(panning, -1f, 1f);
                // Note: Don't use Bass.ChannelSetAttribute for pan on decode streams
                // Panning is applied manually in StreamCallback
            }

            public float GetLevel()
            {
                if (_isPlaying != 1) return 0f;

                var level = BassMix.ChannelGetLevel(_streamHandle);
                if (level == -1) return _lastLevel;

                var left = (level & 0xFFFF) / 32768f;
                var right = ((level >> 16) & 0xFFFF) / 32768f;
                _lastLevel = Math.Min(Math.Max(left, right), 1f);
                return _lastLevel;
            }

            public List<float> GetWaveform()
            {
                if (_isPlaying != 1)
                {
                    EnsureBuffer(_waveformBuffer, 512);
                    return _waveformBuffer;
                }

                // Generate waveform preview based on current frequency
                _waveformBuffer.Clear();
                int waveType = _waveformType;
                for (int i = 0; i < 512; i++)
                {
                    double t = i / 512.0 * 4 * Math.PI; // Show ~2 cycles
                    float sample = GenerateSampleStatic(t, waveType);
                    _waveformBuffer.Add(Math.Abs(sample));
                }
                return _waveformBuffer;
            }

            public List<float> GetSpectrum()
            {
                if (_isPlaying != 1)
                {
                    EnsureBuffer(_spectrumBuffer, 512);
                    return _spectrumBuffer;
                }

                // Simple spectrum visualization for a pure tone
                _spectrumBuffer.Clear();
                float freq = _frequency;
                int freqBin = (int)(freq / (SampleRate / 2f) * 512);
                freqBin = Math.Clamp(freqBin, 0, 511);

                for (int i = 0; i < 512; i++)
                {
                    // Peak at the frequency bin, fall off around it
                    int dist = Math.Abs(i - freqBin);
                    float val = dist == 0 ? 1f : Math.Max(0f, 1f - dist * 0.1f);
                    _spectrumBuffer.Add(val * (_isMuted ? 0f : _currentVolume));
                }
                return _spectrumBuffer;
            }

            public int RenderAudio(double startTime, double duration, float[] buffer)
            {
                int sampleCount = buffer.Length / Channels;
                float freq = _frequency;
                int waveType = _waveformType;
                double phaseIncrement = 2.0 * Math.PI * freq / SampleRate;

                for (int i = 0; i < sampleCount; i++)
                {
                    float envelopeGain = (float)_envelopeValue;
                    float sample = GenerateSampleStatic(_phase, waveType) * _currentVolume * envelopeGain;
                    _phase += phaseIncrement;

                    if (_phase >= 2.0 * Math.PI)
                        _phase -= 2.0 * Math.PI;

                    // Apply panning
                    float pan = _currentPanning;
                    float leftGain = pan <= 0 ? 1f : 1f - pan;
                    float rightGain = pan >= 0 ? 1f : 1f + pan;

                    buffer[i * 2] = sample * leftGain;
                    buffer[i * 2 + 1] = sample * rightGain;
                }

                return buffer.Length;
            }

            private static void EnsureBuffer(List<float> buffer, int size)
            {
                if (buffer.Count == 0)
                    for (int i = 0; i < size; i++) buffer.Add(0f);
            }

            public void Dispose()
            {
                Interlocked.Exchange(ref _isPlaying, 0);
                BassMix.MixerRemoveChannel(_streamHandle);
                Bass.StreamFree(_streamHandle);

                if (_gcHandle.IsAllocated)
                    _gcHandle.Free();
            }
        }
    }
}
