# Operator Audio Stale Detection

## Overview

The audio engine uses a **stale detection** mechanism to automatically stop and reset operator audio streams that are no longer being actively updated. This prevents "orphaned" audio playback from operators that have been disabled, deleted, or are no longer in the evaluation graph.

## Operator Update Contract

**Every audio operator that wants to maintain active playback MUST call its update method every frame.**

### For Stereo Operators
```csharp
AudioEngine.UpdateStereoOperatorPlayback(
    operatorId: this.SymbolChildId,
    filePath: ...,
    shouldPlay: ...,
    shouldStop: ...,
    volume: ...,
    mute: ...,
    panning: ...,
    speed: ...,
    seek: ...
);
```

### For Spatial (3D) Operators
```csharp
AudioEngine.UpdateSpatialOperatorPlayback(
    operatorId: this.SymbolChildId,
    filePath: ...,
    shouldPlay: ...,
    shouldStop: ...,
    volume: ...,
    mute: ...,
    position: ...,
    minDistance: ...,
    maxDistance: ...,
    speed: ...,
    seek: ...,
    orientation: ...,
    innerConeAngle: ...,
    outerConeAngle: ...,
    outerConeVolume: ...,
    mode3D: ...
);
```

### What Happens When an Operator is NOT Updated

If an operator does not call its update method during a frame:

1. The operator's audio state is marked as **stale** (`IsStale = true`)
2. The audio stream is **paused** and **reset to the beginning**

### What Happens When a Stale Operator is Updated Again

When a previously stale operator calls its update method:

1. The operator's state is marked as **active** (`IsStale = false`)
2. The stream is ready to resume playback
3. If the stream was playing, volume is restored to its effective level
4. Normal playback control resumes

## Internal Implementation

### Frame Token System

The stale detection uses an **internal monotonic frame token** (`_audioFrameToken`, a 64-bit `long`) instead of relying directly on `Playback.FrameCount`. This provides:

- **Decoupling**: Audio stale detection logic is self-contained within the audio engine
- **Testability**: Easier to unit test without mocking the entire Playback system
- **Consistency**: The token is incremented exactly once per audio frame, regardless of playback state
- **No overflow risk**: A 64-bit counter at 144 FPS would take over 2 billion years to overflow

### Automatic Frame Detection

The audio engine automatically detects when a new frame has started by tracking changes to `Playback.FrameCount`. The frame token is updated in two places:

1. **In operator update methods**: When an operator calls `UpdateStereoOperatorPlayback` or `UpdateSpatialOperatorPlayback`, `EnsureFrameTokenCurrent()` is called internally. If `Playback.FrameCount` has changed, `_audioFrameToken` is incremented and the operator's `LastUpdatedFrameId` is set to the new token.

2. **In `CompleteFrame()`**: After `StopStaleOperators()` runs, `EnsureFrameTokenCurrent()` is called to ensure the token increments even when no audio operators are updated in a frame. This is critical for detecting stale operators when navigating away from them.

### Stale Detection Timing

The stale check runs in `CompleteFrame()` which is called **before** operators are evaluated each frame. This means:

1. `CompleteFrame()` is called at the start of the frame
2. `StopStaleOperators()` checks which operators have `LastUpdatedFrameId != _audioFrameToken`
3. Operators that weren't updated in the **previous** frame are marked stale
4. `EnsureFrameTokenCurrent()` increments the token for the new frame
5. Operators update and set their `LastUpdatedFrameId` to the new token
6. Next frame, the cycle repeats

This one-frame delay is intentional - it gives operators a full frame to update before being marked stale.

### Per-Operator Tracking

Each operator state tracks:
- `LastUpdatedFrameId`: The frame token when the operator was last updated
- `IsStale`: Whether the operator is currently considered stale

Stale detection compares `LastUpdatedFrameId` against the current `_audioFrameToken`:
```csharp
bool isStale = (state.LastUpdatedFrameId != _audioFrameToken);
```

This replaces the previous `HashSet<Guid>` approach, reducing allocations and making the logic more explicit.

### Export Handling

During audio export (`IsRenderingToFile`):
- Stale detection is **bypassed** for normal frame processing
- Operator states are **explicitly managed** via `ResetAllOperatorStreamsForExport()` and `RestoreOperatorAudioStreams()`
- The `UpdateStaleStatesForExport()` method handles export-specific stale state updates

## Best Practices

1. **Always update in your operator's evaluation**: Call the appropriate `Update*OperatorPlayback` method in your operator's `Update` action, not in constructors or event handlers.

2. **Use consistent operator IDs**: Use `SymbolChildId` as the `operatorId` parameter to ensure consistent tracking.

3. **Handle file changes gracefully**: The audio engine handles file path changes automatically; you don't need to manually dispose streams.

4. **Unregister when disposing**: If your operator implements `IDisposable`, call `AudioEngine.UnregisterOperator(operatorId)` in your dispose method to clean up resources immediately.

## Troubleshooting

### Audio cuts out unexpectedly
- **Cause**: The operator may have missed an update frame
- **Solution**: Ensure the update method is called every frame without conditional skipping

### Audio doesn't restart after being disabled/enabled
- **Cause**: The stale detection correctly stopped the audio; re-enable triggers need to be sent
- **Solution**: Send a `shouldPlay = true` trigger when re-enabling the operator

### Audio plays briefly then stops
- **Cause**: The operator may only be updated on certain conditions
- **Solution**: Ensure unconditional updates even when no parameters change
