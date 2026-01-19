# Audio System Architecture

**Version:** 1.0  
**Last Updated:** 2025-01-18
**Status:** Production Ready

---

## Table of Contents
1. [Introduction](#introduction)
2. [Architecture Overview](#architecture-overview)
3. [Class Hierarchy](#class-hierarchy)
4. [Audio Operators](#audio-operators)
5. [Configuration System](#configuration-system)
7. [Documentation Index](#documentation-index)
8. [Future Enhancement Opportunities](#future-enhancement-opportunities)
9. [Technical Review: Steps Needed](#technical-review-steps-needed)

---

## Introduction

The TiXL audio system is a high-performance, low-latency audio engine built on ManagedBass, supporting stereo and 3D spatial audio playback within operator graphs.

### Key Features
- **Dual-mode playback**: Stereo and 3D spatial audio operators
- **Professional audio**: 48kHz sample rate with hardware acceleration
- **Native 3D audio**: BASS 3D engine with directional cones, Doppler effects
- **Real-time analysis**: FFT spectrum, waveform, and level metering
- **Centralized configuration**: Single source of truth for all audio settings
- **Debug control**: Suppressible logging for cleaner development experience
- **Isolated offline analysis**: Waveform image generation without interfering with playback
- **Stale detection**: Automatic muting of inactive operator streams
- **Export support**: Direct stream reading for video export with audio
- **Unified codebase**: Common base class eliminates code duplication
- **FLAC support**: Native BASS FLAC plugin for high-quality audio files

---

## Architecture Overview

### Core Components

```
                              AUDIO ENGINE (API)
    ┌───────────────────────────────────────────────────────────────┐
    │  UpdateStereoOperatorPlayback()    Set3DListenerPosition()    │
    │  UpdateSpatialOperatorPlayback()   CompleteFrame()            │
    │  Generic helpers: GetOrCreateState<T>, HandleFileChange<T>    │
    │  UseSoundtrackClip()               ReloadSoundtrackClip()     │
    └───────────────────────────────────────────────────────────────┘
                       │                              │
                       ▼                              ▼
    ┌─────────────────────────────┐    ┌─────────────────────────────┐
    │   AUDIO MIXER MANAGER       │    │      AUDIO CONFIG           │
    │   (BASS Mixer)              │    │      (Configuration)        │
    ├─────────────────────────────┤    ├─────────────────────────────┤
    │  GlobalMixerHandle          │    │  MixerFrequency = 48000 Hz  │
    │  OperatorMixerHandle        │    │  UpdatePeriodMs = 10        │
    │  SoundtrackMixerHandle      │    │  PlaybackBufferLengthMs=100 │
    │  OfflineMixerHandle         │    │  FftBufferSize = 1024       │
    │  CreateOfflineAnalysisStream│    │  LogAudioDebug/Info/Render  │
    │  GetGlobalMixerLevel()      │    │                             │
    │  GetOperatorMixerLevel()    │    │                             │
    │  GetSoundtrackMixerLevel()  │    │                             │
    └─────────────────────────────┘    └─────────────────────────────┘
                       │
                       ▼
    ┌─────────────────────────────────────────────────────────────────┐
    │              OPERATOR AUDIO STREAM BASE (Abstract)              │
    ├─────────────────────────────────────────────────────────────────┤
    │  • Play/Pause/Resume/Stop      • Volume/Speed/Seek              │
    │  • Stale/User muting           • Level/Waveform/Spectrum        │
    │  • Export metering             • RenderAudio for export         │
    │  • PrepareForExport            • RestartAfterExport             │
    │  • UpdateFromBuffer            • ClearExportMetering            │
    │  • GetCurrentPosition          • Dispose                        │
    └─────────────────────────────────────────────────────────────────┘
                      │
          ┌───────────┴───────────┐
          ▼                       ▼
    ┌─────────────────┐    ┌─────────────────┐
    │  STEREO STREAM  │    │  SPATIAL STREAM │
    ├─────────────────┤    ├─────────────────┤
    │  • SetPanning() │    │  • 3D Position  │
    │  • TryLoadStream│    │  • Orientation  │
    │                 │    │  • Cone/Doppler │
    │                 │    │  • Apply3D()    │
    │                 │    │  • Set3DMode()  │
    └─────────────────┘    └─────────────────┘
                       │
                       ▼
    ┌─────────────────────────────────────────────────────────────────┐
    │                    OPERATOR GRAPH INTEGRATION                   │
    ├────────────────────────────────┬────────────────────────────────┤
    │     StereoAudioPlayer          │       SpatialAudioPlayer       │
    │     (Uses AudioPlayerUtils)    │       (Uses AudioPlayerUtils)  │
    └────────────────────────────────┴────────────────────────────────┘
```

### Class Hierarchy

```
OperatorAudioStreamBase (abstract)
├── StereoOperatorAudioStream
│   └── SetPanning(float)
└── SpatialOperatorAudioStream
    ├── Update3DPosition(Vector3, float, float)
    ├── Set3DOrientation(Vector3)
    ├── Set3DCone(float, float, float)
    └── Set3DMode(Mode3D)

AudioPlayerUtils (static utility)
└── ComputeInstanceGuid(IEnumerable<Guid>)
```

### Mixer Architecture

**Live Playback Path:**
```
Operator Clips ──► OperatorMixer (Decode) ──┐
                                            ├──► GlobalMixer ──► Soundcard
Soundtrack Clips ──► SoundtrackMixer ───────┘
```

**Export Path (GlobalMixer Paused):**
```
Soundtrack Clips ──► Direct ChannelGetData() ──┐
                                               ├──► ResampleAndMix() ──► Video Encoder
OperatorMixer ──► ChannelGetData() ────────────┘
```

**Isolated Analysis (No Output):**
```
AudioFile ──► CreateOfflineAnalysisStream() ──► FFT/Waveform ──► Image Generation
```

### Signal Flow

**Stereo Audio:**
```
AudioFile ──► Decode ──► MixerAddChannel ──► OperatorMixer ──► GlobalMixer ──► Soundcard
                │
                └──► Volume/Panning/Speed ──► StaleMute ──► FFT ──► Level/Waveform/Spectrum
```

**Spatial Audio:**
```
AudioFile ──► Decode (Mono) ──► MixerAddChannel ──► OperatorMixer ──► GlobalMixer ──► Soundcard
                │
                └──► 3D Position ──► Distance/Cone/Doppler ──► Apply3D()
```

**Export:**
```
PrepareRecording() ──► Pause GlobalMixer ──► Remove Soundtracks from Mixer
        │
        ▼
GetFullMixDownBuffer()
        │
        ├──► MixSoundtrackClip() ──► Seek + Read + ResampleAndMix()
        ├──► MixOperatorAudio() ──► Read OperatorMixer
        └──► UpdateOperatorMetering()
        │
        ▼
EndRecording() ──► Re-add Soundtracks ──► Restore Streams ──► Resume GlobalMixer
```

---

## Audio Operators

### StereoAudioPlayer
**Purpose:** High-quality stereo audio playback with real-time control and analysis.

**Key Parameters:**
- Playback control: Play, Stop, Pause (trigger-based, rising edge detection)
- Audio parameters: Volume, Mute, Panning (-1 to 1), Speed (0.1x to 4x), Seek (0-1 normalized)
- Analysis outputs: IsPlaying, IsPaused, Level (0-1), Waveform (512 samples), Spectrum (512 bands)

**Implementation Details:**
- Uses `AudioPlayerUtils.ComputeInstanceGuid()` for stable operator identification
- Delegates all audio logic to `AudioEngine.UpdateStereoOperatorPlayback()`
- Supports `RenderAudio()` for export functionality
- Finalizer unregisters operator from AudioEngine

**Use Cases:**
- Background music playback
- Sound effect triggering
- Audio-reactive visuals
- Beat detection integration

### SpatialAudioPlayer
**Purpose:** 3D spatial audio with native BASS 3D engine for immersive soundscapes.

**Key Parameters:**
- All StereoAudioPlayer features (except Panning) plus:
- 3D Position: SourcePosition, ListenerPosition, ListenerForward, ListenerUp (Vector3)
- Distance: MinDistance, MaxDistance for attenuation
- Directionality: SourceOrientation, InnerConeAngle, OuterConeAngle (0-360°), OuterConeVolume
- Advanced: Audio3DMode (Normal/Relative/Off)

**Implementation Details:**
- Listener orientation auto-normalized if invalid
- 3D position updated every frame via `AudioEngine.Set3DListenerPosition()`
- Uses mono sources for optimal 3D positioning
- Supports `RenderAudio()` for export functionality

**Use Cases:**
- 3D environments and games
- Spatial audio installations
- Directional speakers/emitters
- Doppler effect simulations

---

## Configuration System

### AudioConfig (Centralized Configuration)

All audio parameters are managed through `Core/Audio/AudioConfig.cs`:

**Mixer Configuration:**
```csharp
MixerFrequency = 48000           // Professional audio quality (Hz)
UpdatePeriodMs = 10              // Low-latency BASS updates
UpdateThreads = 2                // BASS update thread count
PlaybackBufferLengthMs = 100     // Balanced buffering
DeviceBufferLengthMs = 20        // Minimal device latency
```

**FFT and Analysis:**
```csharp
FftBufferSize = 1024             // FFT resolution
BassFftDataFlag = DataFlags.FFT2048  // Returns 1024 values
FrequencyBandCount = 32          // Spectrum bands
WaveformSampleCount = 1024       // Waveform resolution
LowPassCutoffFrequency = 250f    // Low frequency separation (Hz)
HighPassCutoffFrequency = 2000f  // High frequency separation (Hz)
```

**Logging Control:**
```csharp
ShowAudioLogs = false            // Toggle audio debug/info logs
ShowAudioRenderLogs = false      // Toggle audio rendering logs
LogAudioDebug(message)           // Suppressible debug logging
LogAudioInfo(message)            // Suppressible info logging
LogAudioRenderDebug(message)     // Suppressible render debug logging
LogAudioRenderInfo(message)      // Suppressible render info logging
Initialize(showAudioLogs, showAudioRenderLogs)  // Editor initialization
```

### User Settings Integration

Audio configuration is accessible through the Editor Settings window:

**Location:** `Settings → Profiling and Debugging → Audio System`

**Available Settings:**
- ✅ Show Audio Debug Logs (real-time toggle)
- ✅ Show Audio Render Logs (for export debugging)

**Persistence:** All settings are saved to `UserSettings.json` and restored on startup.

---

## Documentation Index

### Core Files

| File                            | Purpose                                          |
|---------------------------------|--------------------------------------------------|
| `AudioEngine.cs`                | Central API for operator and soundtrack playback |
| `OperatorAudioStreamBase.cs`    | Common stream functionality                      |
| `StereoOperatorAudioStream.cs`  | Stereo-specific stream                           |
| `SpatialOperatorAudioStream.cs` | 3D spatial stream                                |
| `AudioRendering.cs`             | Export/recording functionality                   |
| `AudioMixerManager.cs`          | BASS mixer setup and level metering              |
| `AudioConfig.cs`                | Centralized configuration                        |
| `AudioAnalysis.cs`              | FFT processing and frequency bands               |

### Operator Files

| File                    | Purpose          |
|-------------------------|------------------|
| `StereoAudioPlayer.cs`  | Stereo operator  |
| `SpatialAudioPlayer.cs` | Spatial operator |
| `AudioPlayerUtils.cs`   | Shared utilities |

### Guides
- **[STALE_DETECTION.md](STALE_DETECTION.md)** - Stale detection system
- **[TODO.md](TODO.md)** - Technical review and next steps
- 
---

## Future Enhancement Opportunities

### Environmental Audio
- EAX effects integration (reverb, echo, chorus)
- Room acoustics simulation
- Environmental audio zones

### Advanced 3D Audio
- Custom distance rolloff curves
- Adjustable Doppler factor
- HRTF for headphone spatialization
- Geometry-based occlusion (maybe)

### Performance Optimizations
- Centralized `Apply3D()` batching
- Stream pooling and recycling
- Async file loading
- Multi-threaded FFT processing

### Current Limitations
1. No EAX environmental effects (BASS supports, not yet exposed)
2. Single Doppler factor (not yet adjustable)
3. No custom distance rolloff curves
4. `Apply3D()` called per stream (could be centralized)

---

## Technical Review: Steps Needed

A brief checklist of recommended improvements from the latest technical review:

- Centralize BASS initialization in `AudioMixerManager` and simplify `AudioEngine.EnsureBassInitialized`
- Reduce per-frame allocations in export by reusing buffers in `AudioRendering.GetFullMixDownBuffer`
- Consider using BASS/Mixer for export resampling instead of manual `ResampleAndMix`
- Make FFT/waveform buffers explicitly owned, not global statics
- Harden export state transitions in `AudioRendering.PrepareRecording`/`EndRecording`
- Clarify and decouple stale detection from global frame count
- Improve error and logging detail in key areas
- Cache failed operator file loads or mark invalid paths
- Clarify and refine seek semantics for operators
- Refine or simplify use of `_offlineMixerHandle` for analysis

See `Core/Audio/TODO.md` for full details and rationale.

# Gating Checklist

✓ - Switching external input devices and AudioReaction
✓ - Adding a soundtrack to a project
✓ - Rendering a project with soundtrack duration to mp4
✓ - PlayVideo with audio (and audio level)
✓ - Toggling audio mute button
✓ - Changing audio level in Settings
✓ - Exporting a project to the player

# Immediate TODO:
- Finish implementing SpatialAudioPlayer
- Re-think the seek logic / probably should only seek on play
- Add unit tests for AudioEngine methods
- **[TODO.md](TODO.md)** - Implement remaining technical review items