# Stale Detection System

**Version:** 1.0  
**Last Updated:** 2025-01-10  
**Status:** Production Ready

---

## Overview

The stale detection system automatically mutes audio streams when operators are no longer being evaluated (e.g., disabled nodes in the operator graph). This prevents audible audio from playing when operators are bypassed or disabled, while allowing streams to continue playing silently in the background. When operators become active again, audio is instantly unmuted.

**Note:** The system uses volume-based muting (setting volume to 0) rather than pausing. This allows streams to continue advancing at their playback speed. Operator audio streams play at normal speed (or modified by the `speed` parameter) and do not sync with the project timeline.

---

## Problem Statement

### The Challenge

When an operator node is disabled or bypassed in the operator graph:
- The operator's `Update()` method is **never called**
- The operator cannot detect its own inactive state
- Audio streams continue playing in the background
- No automatic cleanup or pausing occurs

### Traditional Approaches (Don't Work)

❌ **Operator-level time tracking**: Fails because `Update()` isn't called when disabled  
❌ **Manual cleanup**: Requires user intervention, error-prone  
❌ **Stream timeouts**: Too aggressive, causes audio dropouts during normal playback

---

## Solution: Frame-Based Stale Detection

### Architecture

The stale detection system uses a **frame-based tracking approach** in `AudioEngine.CompleteFrame()` which runs every frame 
regardless of operator state.

**All detection logic is centralized in AudioEngine** - streams are passive receivers of mute commands.

```
┌─────────────────────────────────────────────────────────────┐
│  AudioEngine.CompleteFrame() - Runs EVERY frame             │
│  ├── CheckAndMuteStaleOperators(currentTime)                │
│  │   ├── For each registered operator:                      │
│  │   │   ├── If in _operatorsUpdatedThisFrame → Active      │
│  │   │   │   └── stream.SetStaleMuted(false, "active")      │
│  │   │   └── If NOT in set → Stale                          │
│  │   │       └── stream.SetStaleMuted(true, "not evaluated")│
│  │   └── Clear _operatorsUpdatedThisFrame                   │
│  └── Continue frame processing...                           │
└─────────────────────────────────────────────────────────────┘
```

**Note:** `SetStaleMuted(true)` sets the stream volume to 0 (mutes it) but keeps it playing in the background at its normal playback speed. `SetStaleMuted(false)` restores the volume (unmutes it). The stream continues advancing through the audio file even when muted.

---

## Use Cases

### 1. Disabled Operator Nodes

**Scenario:** User disables an audio player node in the graph.

**Behavior:**
1. Node's `Update()` stops being called
2. Next frame: `CheckAndMuteStaleOperators()` detects missing operator
3. Stream **muted** immediately (volume set to 0, continues playing silently)
4. Re-enable node → instant unmute on next frame, audio at its current playback position

### 2. Bypassed Graph Sections

**Scenario:** Entire section of graph is bypassed via switch operator.

**Behavior:**
- All audio operators in bypassed section become stale
- Streams **muted** automatically on the next frame (play silently in background)
- Resume graph section → all streams **unmuted** on the next frame

### 3. Conditional Playback

**Scenario:** Audio player controlled by conditional logic (e.g., "play only if value > 0.5").

**Behavior:**
- When condition is false, operator not evaluated
- Stream **muted** on the next frame (continues playing silently)
- When condition becomes true, stream **unmuted** at its current playback position

---

## Tests

**Test 1: Basic Stale Detection**
1. Create audio player operator
2. Do not update it in the audio engine
3. **Expected**: Audio stream mutes (volume = 0) but continues playing in background

**Test 2: Graph Switching**
1. Create two StereoAudioPlayers in separate graph branches
2. Use switch to toggle between them
3. **Expected**: Only active branch is audible
4. **Expected**: Inactive branch mutes on the next frame but maintains time position

**Test 3: Rapid Toggling**
1. Rapidly enable/disable audio operator
2. **Expected**: No clicks, pops, or dropouts
3. **Expected**: Smooth mute/unmute transitions

**Test 4: User Mute Priority**
1. Create audio player operator with playing audio
2. Enable user mute (checkbox)
3. Disable the operator node (becomes stale)
4. Re-enable the operator node (no longer stale)
5. **Expected**: Audio remains muted because user mute is still enabled
6. Disable user mute
7. **Expected**: Audio now plays
 
---

## Troubleshooting

**Solution:**
- Check graph evaluation flow (is operator being evaluated every frame?)
- Verify conditional logic is correct
- Use `AudioConfig.SuppressDebugLogs = false` to see pause/unpause events

---

## Conclusion

The stale detection system provides automatic, reliable, and performant muting of inactive audio operators. By leveraging frame-based tracking in the AudioEngine, it solves the fundamental problem of detecting inactive operators without relying on operator-level Update() calls.

**Key Benefits:**
- ✅ Zero user intervention required
- ✅ Instant response to graph changes (same frame)
- ✅ CPU and memory efficient
- ✅ Non-destructive (streams stay loaded and continue playing silently)
- ✅ **Smooth mute/unmute** (no pops or clicks)
- ✅ **Respects all UI settings** (volume, mute, panning, speed, seek)
- ✅ No time-based tuning required
- ✅ Frame rate independent
- ✅ Comprehensive diagnostic logging
- ✅ Production-tested and reliable

**Architecture Highlights:**
- Simple boolean-based detection (not time-based)
- Centralized logic in AudioEngine
- Volume-based muting (streams continue in background)
- Immediate response (no delays or thresholds)
- Optimized state tracking (only call streams on state change)
- Zero overhead for stable operators (active or stale)

**Implementation Note:**
The system uses volume-based muting (`ChannelAttribute.Volume = 0`) rather than pausing. This allows streams to continue advancing through their audio data at their normal playback speed. When an operator becomes active again, the audio unmutes instantly at its current playback position.

**UI Parameter Handling:**
When an operator returns from being stale, **all current UI parameters are automatically applied** on the next frame:
- ✅ **Volume**: Current slider value is applied
- ✅ **Mute**: User mute checkbox is respected (stale unmute won't override it)
- ✅ **Panning**: Current pan value is applied (stereo audio only)
- ✅ **Speed**: Current speed multiplier is applied
- ✅ **Seek**: Current seek position is applied

This happens automatically through the normal update flow in `AudioEngine.UpdateStereoOperatorPlayback()` and `AudioEngine.UpdateSpatialOperatorPlayback()`, which apply all parameters every frame when the stream is playing. The stale detection system does not interfere with this process.

**Note on Playback Behavior:**
Operator audio streams (both stereo and spatial) play at normal speed (or modified by the `speed` parameter) and do not automatically sync with the project timeline. They continue playing from their current position when unmuted. For timeline-synchronized audio, use the project soundtrack feature instead.

For more information on the audio system architecture, see [AUDIO_ARCHITECTURE.md](AUDIO_ARCHITECTURE.md).
