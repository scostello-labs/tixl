# Audio System Architecture

**Version:** 2.0  
**Last Updated:** 2025-01-11  
**Status:** Production Ready (Refactored)

---

## Table of Contents
1. [Introduction](#introduction)
2. [Architecture Overview](#architecture-overview)
3. [Class Hierarchy](#class-hierarchy)
4. [Audio Operators](#audio-operators)
5. [Configuration System](#configuration-system)
6. [Performance Characteristics](#performance-characteristics)
7. [Documentation Index](#documentation-index)
8. [Future Enhancement Opportunities](#future-enhancement-opportunities)

---

## Introduction

The T3 audio system is a high-performance, low-latency audio engine built on ManagedBass, supporting stereo and 3D spatial audio playback within operator graphs.

### Key Features
- **Dual-mode playback**: Stereo and 3D spatial audio operators
- **Ultra-low latency**: ~20-60ms typical latency
- **Professional audio**: 48kHz sample rate with hardware acceleration
- **Native 3D audio**: BASS 3D engine with directional cones, Doppler effects
- **Real-time analysis**: FFT spectrum, waveform, and level metering
- **Centralized configuration**: Single source of truth for all audio settings
- **Debug control**: Suppressible logging for cleaner development experience
- **Isolated offline analysis**: Waveform image generation without interfering with playback
- **Stale detection**: Automatic muting of inactive operator streams
- **Export support**: Direct stream reading for video export with audio
- **Unified codebase**: Common base class eliminates code duplication

---

## Architecture Overview

### Core Components

```
                              AUDIO ENGINE (API)
    ┌───────────────────────────────────────────────────────────────┐
    │  UpdateStereoOperatorPlayback()    Set3DListenerPosition()    │
    │  UpdateSpatialOperatorPlayback()   CompleteFrame()            │
    │  Generic helpers: GetOrCreateState<T>, HandleFileChange<T>    │
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
    └─────────────────────────────────────────────────────────────────┘
                      │
          ┌───────────┴───────────┐
          ▼                       ▼
    ┌─────────────────┐    ┌─────────────────┐
    │  STEREO STREAM  │    │  SPATIAL STREAM │
    ├─────────────────┤    ├─────────────────┤
    │  • SetPanning() │    │  • 3D Position  │
    │                 │    │  • Orientation  │
    │                 │    │  • Cone/Doppler │
    │                 │    │  • Apply3D()    │
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
├── ComputeInstanceGuid(IEnumerable<Guid>)
├── GenerateTestTone(float, float, string, int)  [DEBUG]
└── CleanupTestFile(string)  [DEBUG]
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
        └──► UpdateOperatorMetering<T>()
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
- Test mode support via `AudioPlayerUtils.GenerateTestTone()` (DEBUG only)

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
```

### User Settings Integration

Audio configuration is accessible through the Editor Settings window:

**Location:** `Settings → Profiling and Debugging → Audio System`

**Available Settings:**
- ✅ Show Audio Debug Logs (real-time toggle)
- ✅ Show Audio Render Logs (for export debugging)

**Persistence:** All settings are saved to `UserSettings.json` and restored on startup.

---

## Performance Characteristics

### Latency
- **Typical latency:** ~20-60ms
- **Components:** File I/O (~5ms) + Buffering (~15-55ms) + Device (~5ms)

### CPU Usage
- **Stereo stream:** ~2-3% per active stream
- **Spatial stream:** ~5-10% per active stream (includes 3D calculations)
- **FFT analysis:** ~1-2% overhead (when enabled)

### Memory Usage
- **Stereo stream:** ~200-500KB per stream (depends on file size)
- **Spatial stream:** ~100-250KB per stream (mono requirement = 50% reduction)
- **Analysis buffers:** ~16KB per stream (FFT + waveform)

### Scalability
- **Update frequency:** 60Hz position updates for spatial audio
- **Hardware acceleration:** Utilized where available (3D audio, mixing)

---

## Documentation Index

### Core Files

| File | Purpose | Lines |
|------|---------|-------|
| `AudioEngine.cs` | Central API for operator playback | ~420 |
| `OperatorAudioStreamBase.cs` | Common stream functionality | ~270 |
| `StereoOperatorAudioStream.cs` | Stereo-specific stream | ~45 |
| `SpatialOperatorAudioStream.cs` | 3D spatial stream | ~120 |
| `AudioRendering.cs` | Export/recording functionality | ~210 |
| `AudioMixerManager.cs` | BASS mixer setup | ~280 |
| `AudioConfig.cs` | Centralized configuration | ~90 |

### Operator Files

| File | Purpose | Lines |
|------|---------|-------|
| `StereoAudioPlayer.cs` | Stereo operator | ~145 |
| `SpatialAudioPlayer.cs` | Spatial operator | ~220 |
| `AudioPlayerUtils.cs` | Shared utilities | ~120 |

### Implementation Guides
- **[STEREO_AUDIO_IMPLEMENTATION.md](STEREO_AUDIO_IMPLEMENTATION.md)** - StereoAudioPlayer documentation
- **[SPATIAL_AUDIO_IMPLEMENTATION.md](SPATIAL_AUDIO_IMPLEMENTATION.md)** - SpatialAudioPlayer documentation
- **[STALE_DETECTION.md](STALE_DETECTION.md)** - Stale detection system

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
- Geometry-based occlusion

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

## System Architecture Details

### State Management

**Generic Operator State (used for both stereo and spatial):**
```csharp
private sealed class OperatorAudioState<T> where T : OperatorAudioStreamBase
{
    public T? Stream;
    public string CurrentFilePath = string.Empty;
    public bool IsPaused;
    public float PreviousSeek;
    public bool PreviousPlay;      // Rising edge detection
    public bool PreviousStop;      // Rising edge detection
    public bool IsStale;
}
```

**Stale Detection Tracking:**
```csharp
private static readonly HashSet<Guid> _operatorsUpdatedThisFrame = new();
private static int _lastStaleCheckFrame = -1;
```

**3D Listener State:**
```csharp
private static Vector3 _listenerPosition = Vector3.Zero;
private static Vector3 _listenerForward = new(0, 0, 1);
private static Vector3 _listenerUp = new(0, 1, 0);
private static bool _3dInitialized = false;
```

### Stream Lifecycle

| Step | Action |
|------|--------|
| 1. INIT | `TryLoadStreamCore()` → Create decode stream → Add to mixer (paused) → Volume=0 |
| 2. PLAY | Rising edge on Play → `Play()` → Unmute → Unpause → IsPlaying=true |
| 3. UPDATE | `UpdateOperatorPlayback()` → Mark as active → Apply Volume/Panning/Speed |
| 4. STALE | `CompleteFrame()` → Check set → If missing → `SetStaleMuted(true)` |
| 5. STOP | Rising edge on Stop → `Stop()` → Pause → Reset position |
| 6. CLEANUP | `UnregisterOperator()` → Dispose stream → Remove from dictionary |

### AudioEngine Generic Helpers

The refactored AudioEngine uses generic methods to eliminate code duplication:

```csharp
// State management
GetOrCreateState<T>(Dictionary<Guid, OperatorAudioState<T>>, Guid)

// File handling
HandleFileChange<T>(OperatorAudioState<T>, string?, Guid, Func<string, T?>)

// Playback control
HandlePlaybackTriggers<T>(OperatorAudioState<T>, bool, bool, Guid)
HandleSeek<T>(OperatorAudioState<T>, float, Guid)
PauseOperatorInternal<T>(Dictionary<...>, Guid)
ResumeOperatorInternal<T>(Dictionary<...>, Guid)

// Queries
IsOperatorPlaying<T>(Dictionary<...>, Guid)
IsOperatorPausedInternal<T>(Dictionary<...>, Guid)
GetOperatorLevelInternal<T>(Dictionary<...>, Guid)
GetOperatorWaveformInternal<T>(Dictionary<...>, Guid)
GetOperatorSpectrumInternal<T>(Dictionary<...>, Guid)

// Stale detection
UpdateStaleStates<T>(Dictionary<...>)
ResetOperatorStreamsForExport<T>(Dictionary<...>)
RestoreOperatorStreams<T>(Dictionary<...>)
DisposeAllOperatorStreams<T>(Dictionary<...>)
```

### Stale Detection System

**Purpose:** Automatically mute audio streams when operators stop being evaluated.

**Flow:**
```
Player.Update() ──► UpdateOperatorPlayback() ──► Add operator ID to HashSet
                                                         │
CompleteFrame() ◄────────────────────────────────────────┘
        │
        ├──► UpdateStaleStates<T>() for each operator type:
        │        ├──► If IN set → SetStaleMuted(false) → Restore volume
        │        └──► If NOT in set → SetStaleMuted(true) → Mute (volume=0)
        │
        └──► Clear HashSet for next frame

