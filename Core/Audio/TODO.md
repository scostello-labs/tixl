## Audio Engine Technical Review (tixl / T3.Core.Audio)

**Last Updated:** 2026-02-05

---

### (a) Overview of Current Audio Engine Design

The audio engine in is built around ManagedBass / BassMix and a mixer-centric architecture managed by `AudioMixerManager`:

- **Mixing Architecture** (`AudioMixerManager.cs`)
  - Initializes BASS with low-latency configuration (UpdatePeriod, buffer lengths, LATENCY flag).
  - Creates:
    - `GlobalMixerHandle` (stereo, float, non-stop) → connected to sound device.
    - `OperatorMixerHandle` (decode, float, non-stop) → added to global mixer.
    - `SoundtrackMixerHandle` (decode, float, non-stop) → added to global mixer.
    - `OfflineMixerHandle` (decode, float) for analysis, not connected to output.
  - Loads `bassflac.dll` plugin.
  - Provides volume/mute and mixer level accessors.

- **Soundtrack / Timeline Audio** (`AudioEngine.cs`, `SoundtrackClipStream`, `SoundtrackClipDefinition.cs`)
  - `AudioEngine.SoundtrackClipStreams` maps `AudioClipResourceHandle` → `SoundtrackClipStream`.
  - Per-frame: `UseSoundtrackClip` marks clips used; `CompleteFrame` calls `ProcessSoundtrackClips`.
  - Soundtrack clips are played via `SoundtrackMixerHandle` for live playback.
  - For export, `AudioRendering` temporarily removes soundtrack streams from mixer and reads directly.
  - FFT and waveform analysis done via `UpdateFftBufferFromSoundtrack` into `AudioAnalysisContext` buffers.

- **Operator Audio (Clip Operators)** (`AudioEngine.cs`, `StereoOperatorAudioStream.cs`, `SpatialOperatorAudioStream.cs`, `OperatorAudioStreamBase.cs`)
  - Two dictionaries of operator state:
    - `_stereoOperatorStates: Guid → OperatorAudioState<StereoOperatorAudioStream>`
    - `_spatialOperatorStates: Guid → OperatorAudioState<SpatialOperatorAudioStream>`
  - Per frame, operators call `UpdateStereoOperatorPlayback` / `UpdateSpatialOperatorPlayback` with parameters:
    - file path, play/stop triggers, volume/mute, panning or 3D position, speed, normalized seek.
  - `OperatorAudioState<T>` tracks stream instance, current file path, play/stop edges, seek, pause, stale flag.
  - Streams feed into `OperatorMixerHandle` which mixes into the global mixer.

- **Stale / Lifetime Management** (`AudioEngine.cs`, `STALE_DETECTION.md`)
  - Uses internal monotonic frame token (`_audioFrameToken`) for stale detection.
  - Each operator state tracks `LastUpdatedFrameId` to determine if it was updated this frame.
  - `StopStaleOperators()` runs in `CompleteFrame()` before operators are evaluated, marking operators that weren't updated in the previous frame as stale.
  - `EnsureFrameTokenCurrent()` is called in `CompleteFrame()` **after** stale checking to ensure the token increments even when no audio operators are updated.
  - This guarantees stale detection works correctly when navigating away from audio operators.
  - During export, special export/reset functions mark streams stale and restore after export.

- **Audio Rendering / Export Path** (`AudioRendering.cs`)
  - `PrepareRecording`: pauses global mixer, saves state, clears export registry, resets operator streams.
  - Creates dedicated export mixer for sample-accurate seeking and BASS-handled resampling.
  - Removes soundtrack streams from `SoundtrackMixerHandle`, adds them to export mixer.
  - `GetFullMixDownBuffer` reads from export mixer (BASS handles resampling), mixes operator audio.
  - Uses reusable static buffers to minimize per-frame allocations.
  - Logs per-frame stats, updates meter levels for operator streams.
  - `EndRecording`: re-adds soundtrack streams to mixer, restores saved state, resumes playback.

- **Analysis / Metering / Input**
  - `AudioAnalysis`, `WaveFormProcessing`, `AudioImageGenerator`, `WasapiAudioInput` provide FFT, waveform, input capture, and offline analysis.
  - Export metering uses `AudioExportSourceRegistry` and `AudioRendering.EvaluateAllAudioMeteringOutputs` to evaluate operator graph outputs on offline buffers.

Overall, the design is clear and reasonably modular: mixer management is centralized in `AudioMixerManager`, higher-level orchestration lives in `AudioEngine`, and export logic is isolated in `AudioRendering`. Latency has been explicitly considered via BASS config.

---

### (b) Specific Findings (with impact)

1. `AudioMixerManager.Initialize` uses `Bass.ChannelGetLevel` for levels in `GetGlobalMixerLevel`

- **Location**: `Core\Audio\AudioMixerManager.cs`
  - `GetGlobalMixerLevel()`
- **Code Pattern**:
  ```csharp
  float[] levels = new float[2];
  if (!Bass.ChannelGetLevel(_globalMixerHandle, levels, 0.05f, LevelRetrievalFlags.Stereo))
      return 0f;
  ```
- **Issues / Observation**:
  - Uses `ChannelGetLevel` overload with `float[]` (level-ex variant) and a 50ms window, which is ok for metering; just ensure that the `Bass.dll` version supports this overload and that it does not conflict with how the engine wants "instant" vs "windowed" levels.
- **Impact / Risk**:
  - Low, but any mismatch in BASS version or flags could produce incorrect metering.

2. `AudioMixerManager.CreateOfflineAnalysisStream` does not use the `OfflineMixerHandle`

- **Location**: `Core\Audio\AudioMixerManager.cs`
  - `CreateOfflineAnalysisStream`, `OfflineMixerHandle` property.
- **Code Pattern**:
  ```csharp
  var stream = Bass.CreateStream(filePath, 0, 0, BassFlags.Decode | BassFlags.Prescan | BassFlags.Float);
  ```
- **Issues**:
  - The offline mixer (`_offlineMixerHandle`) is created but not used here. Offline streams are standalone decode streams, not added to the offline mixer. This is not incorrect, but it contradicts the comment in the class docstring about an "offline mixer for analysis tasks."
- **Impact / Risk**:
  - **Maintainability / clarity**: future contributors may assume offline analysis uses `_offlineMixerHandle` for mixing multiple streams and could mis-use it.

3. Operator file path resolution lacks caching of failures and clearer error semantics

- **Location**: `Core\Audio\AudioEngine.cs`
  - `ResolveFilePath`, `HandleFileChange`.
- **Code Pattern**:
  ```csharp
  if (ResourceManager.TryResolveRelativePath(filePath, null, out var absolutePath, out _))
  {
      AudioConfig.LogAudioDebug($"[AudioEngine] Resolved: {filePath} → {absolutePath}");
      return absolutePath;
  }
  return filePath;
  ```
  and
  ```csharp
  if (!string.IsNullOrEmpty(resolvedPath))
  {
      state.Stream = loadFunc(resolvedPath);
      if (state.Stream == null)
          Log.Error($"[AudioEngine] Failed to load stream for {operatorId}: {resolvedPath}");
  }
  ```
- **Issues**:
  - If a path repeatedly fails to load (e.g., missing file), the engine attempts to load it every time the path is set, logging errors repeatedly.
  - `ResolveFilePath` returns the original string even if it does not exist; `HandleFileChange` will attempt to load it.
- **Impact / Risk**:
  - **CPU / logging overhead**: repeated load attempts and logging for missing files.
  - **UX**: noisy logs for users experimenting with paths.

4. Operator seek handling is edge-triggered but state only tracks last normalized seek value

- **Location**: `Core\Audio\AudioEngine.cs`
  - `HandleSeek`.
- **Code Pattern**:
  ```csharp
  if (Math.Abs(seek - state.PreviousSeek) > 0.001f && seek >= 0f && seek <= 1f)
  {
      var seekTime = (float)(seek * state.Stream!.Duration);
      state.Stream.Seek(seekTime);
      state.PreviousSeek = seek;
      AudioConfig.LogAudioDebug($"[AudioEngine] Seek to {seek:F3} ({seekTime:F3}s) for {operatorId}");
  }
  ```
- **Issues**:
  - If calling code uses 0 as "no seek" value but sometimes legitimately wants to seek to 0, there is ambiguity.
  - Continuous changes around a value might cause repeated `Seek` calls per frame if not rate-limited by caller.
- **Impact / Risk**:
  - **Performance**: repeated seek operations (which may be expensive) if upstream code is noisy.
  - **Clarity**: semantics of `seek` parameter (edge-trigger vs absolute desire) may not be obvious from the interface.

5. Exception handling in `AudioRendering.ExportAudioFrame` is too coarse and hides error details

- **Location**: `Core\Audio\AudioRendering.cs`
  - `ExportAudioFrame`.
- **Code Pattern**:
  ```csharp
  try
  {
      AudioEngine.UpdateFftBufferFromSoundtrack(clipStream.StreamHandle, playback);
  }
  catch (Exception ex)
  {
      Log.Error($"ExportAudioFrame error: {ex.Message}", typeof(AudioRendering));
  }
  ```
- **Issues**:
  - Only logs `ex.Message`, losing stack trace and inner exceptions.
  - If this happens frequently, export may continue in a half-broken state.
- **Impact / Risk**:
  - **Debuggability**: less context makes it harder to diagnose issues in FFT/export path.

6. `AudioMixerManager.Shutdown` frees all streams and calls `Bass.Free` without notifying higher layers

- **Location**: `Core\Audio\AudioMixerManager.cs`
  - `Shutdown()`.
- **Code Pattern**:
  ```csharp
  Bass.StreamFree(_operatorMixerHandle);
  Bass.StreamFree(_soundtrackMixerHandle);
  Bass.StreamFree(_offlineMixerHandle);
  Bass.StreamFree(_globalMixerHandle);
  ...
  Bass.Free();
  ```
- **Issues**:
  - Higher-level components like `AudioEngine.SoundtrackClipStreams` and operator states may still hold `StreamHandle` / `Bass` stream handles that are no longer valid after `Bass.Free()`.
  - Devices changing (via `AudioEngine.OnAudioDeviceChanged`) result in `AudioMixerManager.Shutdown(); AudioMixerManager.Initialize();`, but operator and soundtrack dictionaries are only partially reset.
- **Impact / Risk**:
  - **Glitches / invalid handle errors** if code tries to access or reuse stream handles across device changes.

---

### (c) Prioritized Recommendations (implementation-oriented)

Below are the remaining prioritized recommendations grouped and ordered by priority (Medium → Low).

#### Medium priority

1. Improve error and logging detail in key areas

- **Goals**: easier troubleshooting of audio issues.

- **Targets**:
  - `AudioRendering.ExportAudioFrame`: log full exception (`{ex}`) to keep stack trace.
  - `AudioMixerManager.Initialize`: log environment info on full failure.
  - `HandleFileChange` (AudioEngine): when failing to load, also log file existence and loader errors.

- **Benefits**:
  - Faster diagnosis of missing plugins, unsupported formats, and invalid states.

#### Low priority

2. Cache failed operator file loads or mark invalid paths

- **Goals**: reduce repeated error logging and load attempts.

- **Steps**:
  1. Extend `OperatorAudioState<T>` with a `LastLoadError` field.
  2. When `loadFunc(resolvedPath)` returns null, store the error and avoid retrying until the path changes.
  3. Optionally provide a helper to clear the error when the user retries.

- **Benefits**:
  - Cleaner logs when users experiment with invalid files.
  - Less redundant work.

3. Clarify and refine seek semantics for operators

- **Goals**: avoid unnecessary seeks and clarify API usage.

- **Steps**:
  1. Document `seek` semantics in the XML docs for `UpdateStereoOperatorPlayback` / `UpdateSpatialOperatorPlayback`.
  2. If needed, add an explicit boolean `seekTriggered` flag separate from the normalized position.
  3. Add rate limiting if profiling shows repeated seeks are an issue.

- **Benefits**:
  - More predictable operator behaviour when upstream controls seek via UI or automation.

4. Either use `_offlineMixerHandle` for multi-stream analysis or simplify the abstraction

- **Goals**: reduce conceptual unused complexity.

- **Options**:
  1. **Use it**: add offline streams to `_offlineMixerHandle` and read via `BassMix.ChannelGetData`.
  2. **Simplify**: remove `_offlineMixerHandle` if offline analysis is always single-stream decode.

- **Benefits**:
  - Clearer understanding of the analysis path.
