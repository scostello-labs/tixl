## Audio Engine Technical Review (tixl / T3.Core.Audio)

### Status Updates

#### ✅ Completed (January 22, 2026)
- **Recommendation #1**: Centralized BASS initialization in `AudioMixerManager` and simplified `AudioEngine.EnsureBassInitialized`
  - Removed duplicate initialization logic (Bass.Free/Bass.Init fallback)
  - AudioMixerManager is now the sole owner of BASS lifecycle
  - Added `_bassInitialized` flag reset in `OnAudioDeviceChanged()`
  - This fixes the multiple initialization warnings and ensures consistent low-latency configuration

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
  - FFT and waveform analysis done via `UpdateFftBufferFromSoundtrack` into shared static buffers.

- **Operator Audio (Clip Operators)** (`AudioEngine.cs`, `StereoOperatorAudioStream.cs`, `SpatialOperatorAudioStream.cs`, `OperatorAudioStreamBase.cs`)
  - Two dictionaries of operator state:
    - `_stereoOperatorStates: Guid → OperatorAudioState<StereoOperatorAudioStream>`
    - `_spatialOperatorStates: Guid → OperatorAudioState<SpatialOperatorAudioStream>`
  - Per frame, operators call `UpdateStereoOperatorPlayback` / `UpdateSpatialOperatorPlayback` with parameters:
    - file path, play/stop triggers, volume/mute, panning or 3D position, speed, normalized seek.
  - `OperatorAudioState<T>` tracks stream instance, current file path, play/stop edges, seek, pause, stale flag.
  - Streams feed into `OperatorMixerHandle` which mixes into the global mixer.

- **Stale / Lifetime Management** (`AudioEngine.cs`, `STALE_DETECTION.md`)
  - `_operatorsUpdatedThisFrame` holds GUIDs updated in current frame.
  - Each frame: `CheckAndMuteStaleOperators` marks operator streams as stale (muted) if no update.
  - During export, special export/reset functions mark streams stale and restore after export.

- **Audio Rendering / Export Path** (`AudioRendering.cs`)
  - `PrepareRecording`: pauses global mixer, saves state, clears export registry, resets operator streams.
  - Removes soundtrack streams from `SoundtrackMixerHandle`, configures them for reverse-direction reading.
  - `GetFullMixDownBuffer` constructs a per-frame stereo float buffer by:
    - Mixing soundtrack clips via manual `Bass.ChannelGetData` + custom resampler.
    - Mixing operator audio via `Bass.ChannelGetData` from `OperatorMixerHandle`.
  - Logs per-frame stats, updates meter levels for operator streams.
  - `EndRecording`: re-adds soundtrack streams to mixer, restores saved state, resumes playback.

- **Analysis / Metering / Input**
  - `AudioAnalysis`, `WaveFormProcessing`, `AudioImageGenerator`, `WasapiAudioInput` provide FFT, waveform, input capture, and offline analysis.
  - Export metering uses `AudioExportSourceRegistry` and `AudioRendering.EvaluateAllAudioMeteringOutputs` to evaluate operator graph outputs on offline buffers.

Overall, the design is clear and reasonably modular: mixer management is centralized in `AudioMixerManager`, higher-level orchestration lives in `AudioEngine`, and export logic is isolated in `AudioRendering`. Latency has been explicitly considered via BASS config.

---

### (b) Specific Findings (with impact) 

#### 1. ✅ RESOLVED: `AudioEngine.EnsureBassInitialized` can double-initialize BASS and mixes responsibilities

- **Location**: `Core\Audio\AudioEngine.cs`
  - `EnsureBassInitialized()`
- **Resolution Date**: January 22, 2026
- **Previous Code Pattern**:
  ```csharp
  private static void EnsureBassInitialized()
  {
      if (_bassInitialized) return;

      AudioMixerManager.Initialize();
      if (AudioMixerManager.OperatorMixerHandle != 0)
      {
          _bassInitialized = true;
          InitializeGlobalVolumeFromSettings();
      }
      else
      {
          Bass.Free();  // ❌ REMOVED - caused double initialization
          Bass.Init();  // ❌ REMOVED - bypassed low-latency config
          AudioMixerManager.Initialize();  // ❌ REMOVED - second init attempt
          _bassInitialized = true;
          InitializeGlobalVolumeFromSettings();
      }
  }
  ```
- **Current Implementation**:
  ```csharp
  private static void EnsureBassInitialized()
  {
      if (_bassInitialized) return;

      AudioMixerManager.Initialize();
      if (AudioMixerManager.OperatorMixerHandle == 0)
      {
          Log.Error("[AudioEngine] Failed to initialize AudioMixerManager; audio disabled.");
          return;  // ✅ Fail gracefully without retry
      }

      _bassInitialized = true;
      InitializeGlobalVolumeFromSettings();
  }
  ```
- **Additional Change**: Updated `OnAudioDeviceChanged()` to reset `_bassInitialized = false;` after `AudioMixerManager.Shutdown()`
- **Issues (NOW FIXED)**:
  1. ✅ `AudioMixerManager` is now the sole owner of BASS initialization
  2. ✅ Removed direct `Bass.Free()` and `Bass.Init()` calls from `AudioEngine`
  3. ✅ Single source of truth - `_bassInitialized` properly synchronized with AudioMixerManager state
- **Benefits Achieved**:
  - ✅ Consistent low-latency configuration always applied
  - ✅ Clear single owner for device lifecycle
  - ✅ Easier debugging - no hidden fallback paths
  - ✅ Proper device change handling

#### 2. Manual resampling in `AudioRendering.ResampleAndMix` is simple but potentially suboptimal

- **Location**: `Core\Audio\AudioRendering.cs`
  - `ResampleAndMix(...)`
- **Code Pattern**:
  ```csharp
  private static void ResampleAndMix(float[] source, int sourceFloatCount, float sourceRate, int sourceChannels,
      float[] target, int targetFloatCount, int targetRate, int targetChannels, float volume)
  {
      int targetSampleCount = targetFloatCount / targetChannels;
      int sourceSampleCount = sourceFloatCount / sourceChannels;
      double ratio = sourceRate / targetRate;

      for (int t = 0; t < targetSampleCount; t++)
      {
          double sourcePos = t * ratio;
          int s0 = (int)sourcePos;
          int s1 = s0 + 1;
          double frac = sourcePos - s0;

          for (int c = 0; c < targetChannels; c++)
          {
              int sc = c % sourceChannels;
              int idx0 = s0 * sourceChannels + sc;
              int idx1 = s1 * sourceChannels + sc;

              float v0 = (idx0 >= 0 && idx0 < sourceFloatCount) ? source[idx0] : 0;
              float v1 = (idx1 >= 0 && idx1 < sourceFloatCount) ? source[idx1] : 0;

              float interpolated = (float)(v0 * (1.0 - frac) + v1 * frac);
              int targetIdx = t * targetChannels + c;

              if (targetIdx < target.Length && !float.IsNaN(interpolated))
                  target[targetIdx] += interpolated * volume;
          }
      }
  }
  ```
- **Issues**:
  1. **Quality**: simple linear interpolation is OK for many cases but can introduce noticeable aliasing and dullness for large resampling ratios (especially with pitch changes or large sample-rate differences).
  2. **Performance**: tight nested loops in C# over per-sample operations may incur overhead compared to BASS’s internal resampling (which is often highly optimized and possibly vectorized).
  3. **Duplication**: BASS/Mixer already provides resampling, channel conversion, and mixing; the engine is re-implementing this only for the export path.
- **Impact / Risk**:
  - **CPU usage**: export of long sequences at high FPS can be heavier than necessary.
  - **Audio quality**: resampling artifacts in export that differ from live playback.
  - **Maintenance**: any improvements to resampling have to be done manually here.

#### 3. `AudioRendering.GetFullMixDownBuffer` allocates buffers per frame, which can increase GC pressure

- **Location**: `Core\Audio\AudioRendering.cs`
  - `GetFullMixDownBuffer(double frameDurationInSeconds, double localFxTime)`
- **Code Pattern**:
  ```csharp
  int sampleCount = (int)Math.Max(Math.Round(frameDurationInSeconds * AudioConfig.MixerFrequency), 1);
  int floatCount = sampleCount * 2; // stereo
  float[] mixBuffer = new float[floatCount];
  ...
  float[] operatorBuffer = new float[floatCount];
  ...
  float[] sourceBuffer = new float[sourceFloatCount]; // in MixSoundtrackClip
  ```
- **Issues**:
  - Every export frame allocates several new arrays: `mixBuffer`, `operatorBuffer`, and per-soundtrack `sourceBuffer`. For long exports (e.g., thousands of frames), this can generate substantial managed allocations, increasing GC activity and possibly causing intermittent pauses.
  - GC pressure during export is less critical than for real-time playback, but can still impact total export time (especially with high FPS or many clips).
- **Impact / Risk**:
  - **CPU / throughput**: increased GC may slow down offline export.
  - **Predictability**: memory allocation pattern may cause inconsistent export durations between scenes.

#### 4. Shared static buffers for FFT / waveform introduce hidden coupling and potential race issues

- **Location**:
  - `AudioEngine.UpdateFftBufferFromSoundtrack` (in `AudioEngine.cs`)
  - `AudioAnalysis`, `WaveFormProcessing` (other files in `Core\Audio`)
- **Code Pattern**:
  ```csharp
  _ = BassMix.ChannelGetData(soundStreamHandle, AudioAnalysis.FftGainBuffer, dataFlags);
  ...
  WaveFormProcessing.LastFetchResultCode = BassMix.ChannelGetData(soundStreamHandle,
      WaveFormProcessing.InterleavenSampleBuffer, lengthInBytes);
  ```
- **Issues**:
  1. `AudioAnalysis.FftGainBuffer` and `WaveFormProcessing.InterleavenSampleBuffer` are static arrays shared across the system. The code assumes single-threaded access from the main update / export loop.
  2. If future changes introduce multi-threaded evaluation (e.g., operator graph or analysis in background tasks) and call `UpdateFftBufferFromSoundtrack` concurrently, data races and inconsistent FFT results are likely.
  3. `WaveFormProcessing.RequestedOnce` is a global static flag determining whether waveform data is fetched; the behaviour may be non-obvious from operator code.
- **Impact / Risk**:
  - **Thread-safety**: current design is sensitive to concurrency changes; small architectural updates can introduce subtle bugs.
  - **Maintainability**: static buffers obscure data flow; it’s not obvious who owns or uses them.

#### 5. Operator stale detection depends on per-frame `Update*` calls and global state

- **Location**: `Core\Audio\AudioEngine.cs`
  - Fields: `_operatorsUpdatedThisFrame`, `_lastStaleCheckFrame`.
  - Methods: `UpdateStereoOperatorPlayback`, `UpdateSpatialOperatorPlayback`, `CheckAndMuteStaleOperators`, `UpdateStaleStates`, export-related stale methods.
- **Issues**:
  1. Stale detection is implicit: it requires that operator instances call `UpdateStereoOperatorPlayback` / `UpdateSpatialOperatorPlayback` every frame in which they are “logically active.” If a caller misses a frame or changes update frequency, streams can be incorrectly muted.
  2. `_lastStaleCheckFrame` / `Playback.FrameCount` coupling ties the stale logic to the global `Playback` system, which may complicate reuse or testing.
  3. A stream being stale is indicated by both `OperatorAudioState<T>.IsStale` and `stream.SetStaleMuted(bool)`; there is duplication of state.
- **Impact / Risk**:
  - **Glitch resilience**: if an operator is updated a frame late due to performance hitches or scheduling, its audio might be muted for a frame.
  - **Maintainability**: reliance on global frame count and implicit update contracts makes the behaviour harder to reason about.

#### 6. `AudioRendering.PrepareRecording` / `EndRecording` manipulate BASS state with limited error handling

- **Location**: `Core\Audio\AudioRendering.cs`
  - `PrepareRecording`, `EndRecording`.
- **Code Patterns**:
  ```csharp
  Bass.ChannelPause(AudioMixerManager.GlobalMixerHandle);
  ...
  BassMix.MixerRemoveChannel(clipStream.StreamHandle);
  ...
  if (!BassMix.MixerAddChannel(AudioMixerManager.SoundtrackMixerHandle, clipStream.StreamHandle, BassFlags.MixerChanPause))
  {
      Log.Warning($"[AudioRendering] Failed to re-add soundtrack: {Bass.LastError}");
  }
  ```
- **Issues**:
  1. If calls like `MixerRemoveChannel` or `ChannelPause` fail, there is no rollback logic; the system continues assuming the desired state.
  2. `PrepareRecording` modifies channel attributes (frequency, volume, `NoRamp`, `ReverseDirection`) on soundtrack streams; `EndRecording` only calls `clipStream.UpdateTimeWhileRecording(...)` and re-adds them, but does not explicitly restore attributes.
  3. If export is aborted unexpectedly or if multiple overlapping `PrepareRecording` / `EndRecording` are triggered by UI, engine state may become inconsistent.
- **Impact / Risk**:
  - **Glitch / state corruption**: after export, soundtrack streams might have unexpected attributes or be missing from mixers if error conditions were hit.
  - **Debugging difficulty**: distortions or silence after export could be hard to trace.

#### 7. `AudioMixerManager.Initialize` uses `Bass.ChannelGetLevel` for levels in `GetGlobalMixerLevel`

- **Location**: `Core\Audio\AudioMixerManager.cs`
  - `GetGlobalMixerLevel()`
- **Code Pattern**:
  ```csharp
  float[] levels = new float[2];
  if (!Bass.ChannelGetLevel(_globalMixerHandle, levels, 0.05f, LevelRetrievalFlags.Stereo))
      return 0f;
  ```
- **Issues / Observation**:
  - Uses `ChannelGetLevel` overload with `float[]` (level-ex variant) and a 50ms window, which is ok for metering; just ensure that the `Bass.dll` version supports this overload and that it does not conflict with how the engine wants “instant” vs “windowed” levels.
- **Impact / Risk**:
  - Low, but any mismatch in BASS version or flags could produce incorrect metering.

#### 8. `AudioMixerManager.CreateOfflineAnalysisStream` does not use the `OfflineMixerHandle`

- **Location**: `Core\Audio\AudioMixerManager.cs`
  - `CreateOfflineAnalysisStream`, `OfflineMixerHandle` property.
- **Code Pattern**:
  ```csharp
  var stream = Bass.CreateStream(filePath, 0, 0, BassFlags.Decode | BassFlags.Prescan | BassFlags.Float);
  ```
- **Issues**:
  - The offline mixer (`_offlineMixerHandle`) is created but not used here. Offline streams are standalone decode streams, not added to the offline mixer. This is not incorrect, but it contradicts the comment in the class docstring about an “offline mixer for analysis tasks.”
- **Impact / Risk**:
  - **Maintainability / clarity**: future contributors may assume offline analysis uses `_offlineMixerHandle` for mixing multiple streams and could mis-use it.

#### 9. Operator file path resolution lacks caching of failures and clearer error semantics

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

#### 10. Operator seek handling is edge-triggered but state only tracks last normalized seek value

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
  - If calling code uses 0 as “no seek” value but sometimes legitimately wants to seek to 0, there is ambiguity.
  - Continuous changes around a value might cause repeated `Seek` calls per frame if not rate-limited by caller.
- **Impact / Risk**:
  - **Performance**: repeated seek operations (which may be expensive) if upstream code is noisy.
  - **Clarity**: semantics of `seek` parameter (edge-trigger vs absolute desire) may not be obvious from the interface.

#### 11. Exception handling in `AudioRendering.ExportAudioFrame` is too coarse and hides error details

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

#### 12. `AudioMixerManager.Shutdown` frees all streams and calls `Bass.Free` without notifying higher layers

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

Below is a prioritized list of recommended changes to improve reliability, performance, and maintainability.

#### 1. ✅ COMPLETED: Centralize BASS initialization in `AudioMixerManager` and simplify `AudioEngine.EnsureBassInitialized` (High priority)

**Status**: ✅ Implemented on January 22, 2026

**Goals**: remove duplicate initialization logic, ensure latency config is always applied, reduce surprises.

**Implementation Summary**:

1. ✅ In `AudioEngine.cs`, replaced `EnsureBassInitialized()` logic with simplified version:
   ```csharp
   private static void EnsureBassInitialized()
   {
       if (_bassInitialized)
           return;

       AudioMixerManager.Initialize();

       if (AudioMixerManager.OperatorMixerHandle == 0)
       {
           Log.Error("[AudioEngine] Failed to initialize AudioMixerManager; audio disabled.");
           return;
       }

       _bassInitialized = true;
       InitializeGlobalVolumeFromSettings();
   }
   ```

2. ✅ Removed direct calls to `Bass.Free()` and `Bass.Init()` from `AudioEngine`.

3. ✅ Added `_bassInitialized = false;` reset in `OnAudioDeviceChanged()` after `AudioMixerManager.Shutdown()`.

**Benefits Achieved**:
- ✅ Consistent low-latency configuration.
- ✅ Clear single owner for device lifecycle.
- ✅ Easier debugging in initialization failures.
- ✅ No more multiple initialization warnings.

#### 2. Reduce per-frame allocations in `AudioRendering.GetFullMixDownBuffer` by using reusable buffers (High priority for heavy export use)

**Goals**: reduce GC pressure / CPU usage during export.

**Steps**:

1. Add private static reusable buffers in `AudioRendering`:
   ```csharp
   private static float[] _mixBuffer = Array.Empty<float>();
   private static float[] _operatorBuffer = Array.Empty<float>();
   private static float[] _soundtrackSourceBuffer = Array.Empty<float>();
   ```

2. Add a helper to ensure capacity:
   ```csharp
   private static float[] EnsureBuffer(ref float[] buffer, int requiredLength)
   {
       if (buffer.Length < requiredLength)
           buffer = new float[requiredLength];
       Array.Clear(buffer, 0, requiredLength);
       return buffer;
   }
   ```

3. In `GetFullMixDownBuffer`, use:
   ```csharp
   var mixBuffer = EnsureBuffer(ref _mixBuffer, floatCount);
   var operatorBuffer = EnsureBuffer(ref _operatorBuffer, floatCount);
   ```

4. In `MixSoundtrackClip`, use the shared source buffer:
   ```csharp
   float[] sourceBuffer = EnsureBuffer(ref _soundtrackSourceBuffer, sourceFloatCount);
   ```

5. Ensure that buffer lengths are sufficient for worst-case `frameDurationInSeconds` used in exports.

**Benefits**:
- Fewer allocations, lower GC activity.
- More predictable export performance.

#### 3. Consider leveraging BASS/Mixer for export resampling instead of manual `ResampleAndMix` (Medium–High priority)

**Goals**: better performance and audio quality; unify live and export mixing behaviour.

**Concept**:

Instead of manually resampling each soundtrack clip into `mixBuffer`, you can create a dedicated export mixer stream (decode-only) at `AudioConfig.MixerFrequency`, add the soundtrack and operator streams (or copies) at their native rate with appropriate flags, and read from that export mixer via `BassMix.ChannelGetData`.

**Possible implementation sketch**:

1. In `AudioMixerManager`, add:
   ```csharp
   private static int _exportMixerHandle;
   public static int ExportMixerHandle => _exportMixerHandle;
   ```

2. Create `_exportMixerHandle` in `Initialize()`:
   ```csharp
   _exportMixerHandle = BassMix.CreateMixerStream(AudioConfig.MixerFrequency, 2,
       BassFlags.MixerNonStop | BassFlags.Decode | BassFlags.Float);
   ```

3. During `AudioRendering.PrepareRecording`, instead of removing soundtrack streams from `SoundtrackMixerHandle` and manually resampling, add them into `_exportMixerHandle` as decode sources (with suitable flags, e.g., `MixerChanBuffer | MixerChanMatrix` for channel routing).

4. `GetFullMixDownBuffer` would then:
   ```csharp
   int bytesToRead = floatCount * sizeof(float);
   int bytesRead = Bass.ChannelGetData(AudioMixerManager.ExportMixerHandle, mixBuffer, bytesToRead);
   // Optionally gather operator streams or use separate operator export path.
   ```

5. Ensure that the export mixer is not connected to the soundcard and is paused / cleared appropriately before and after exports.

**Benefits**:
- Uses BASS’s optimized resampler and channel mixer.
- Removes custom `ResampleAndMix`, reducing code complexity.
- Ensures export sound is consistent with live playback (same core mixing path).

#### 4. Make FFT / waveform buffers explicitly owned and avoid global static coupling (Medium priority)

**Goals**: improve thread-safety and clarity.

**Steps**:

1. In `AudioAnalysis` and `WaveFormProcessing`, introduce instance-level buffers and a small “analysis context” struct/class that holds them.

2. Change `AudioEngine.UpdateFftBufferFromSoundtrack` to receive an analysis context (or explicit buffers) instead of writing into static arrays. For example:
   ```csharp
   internal static void UpdateFftBufferFromSoundtrack(int soundStreamHandle, Playback playback, AudioAnalysisContext context)
   {
       ...
       BassMix.ChannelGetData(soundStreamHandle, context.FftBuffer, dataFlags);
       ...
   }
   ```

3. For global visualizations, you can still keep a single shared context but the design will make it explicit and easier to refactor to multiple contexts later.

4. Add comments documenting that currently all analysis is assumed to run on the main thread; warn that multi-threaded usage requires extra synchronization.

**Benefits**:
- Clearer ownership of buffers.
- Easier future refactoring to multi-threaded or multi-consumer analysis.

#### 5. Harden export state transitions in `AudioRendering.PrepareRecording` / `EndRecording` (Medium priority)

**Goals**: robustness against errors and double-calls, easier debugging.

**Steps**:

1. Guard against nested calls with a simple state machine:
   ```csharp
   private static bool _isRecording;
   private static int _recordingNesting;

   public static void PrepareRecording(...)
   {
       if (_recordingNesting++ > 0)
           return; // Already in recording state
       _isRecording = true;
       ...
   }

   public static void EndRecording(...)
   {
       if (--_recordingNesting > 0)
           return;
       if (!_isRecording) return;
       _isRecording = false;
       ...
   }
   ```

2. Add checks for critical BASS calls and log detailed errors:
   ```csharp
   if (!Bass.ChannelPause(AudioMixerManager.GlobalMixerHandle))
   {
       Log.Warning($"[AudioRendering] Failed to pause global mixer: {Bass.LastError}");
   }
   ```

3. When changing attributes (`Frequency`, `Volume`, `NoRamp`, `ReverseDirection`) on soundtrack streams, store the original values in a per-stream export state (e.g., dictionary keyed by stream handle) and restore them in `EndRecording` instead of relying only on `clipStream.UpdateTimeWhileRecording`.

4. Log more context in errors (clip path, handle, device) to simplify debugging.

**Benefits**:
- Fewer surprises after export.
- Safer integration with UI or scripting where export commands may be spammed.

#### 6. Clarify and slightly decouple stale detection from global frame count (Medium priority)

**Goals**: improve clarity and reduce accidental muting.

**Steps**:

1. Document clearly in `STALE_DETECTION.md` and at the public methods that 
   - Operators must call `UpdateStereoOperatorPlayback` / `UpdateSpatialOperatorPlayback` every frame they should be audible.

2. Optionally, change `CheckAndMuteStaleOperators` to use a monotonic frame token instead of `Playback.FrameCount`, e.g., an internal `long _staleFrameCounter` incremented in `CompleteFrame`:
   ```csharp
   private static long _currentFrameId;

   public static void CompleteFrame(...)
   {
       _currentFrameId++;
       ...
       CheckAndMuteStaleOperators();
   }

   private static void CheckAndMuteStaleOperators()
   {
       if (Playback.Current.IsRenderingToFile) return;
       if (_lastStaleCheckFrame == _currentFrameId) return;
       _lastStaleCheckFrame = (int)_currentFrameId;
       ...
   }
   ```

3. Consider storing a `LastUpdatedFrameId` inside `OperatorAudioState<T>` instead of relying on the `_operatorsUpdatedThisFrame` hash set — this can reduce allocations and make logic more explicit.

**Benefits**:
- More robust detection of stale operators.
- Easier to change update frequency of operators if needed.

#### 7. Improve error and logging detail in key areas (Medium priority)

**Goals**: easier troubleshooting of audio issues.

**Targets**:

1. `AudioRendering.ExportAudioFrame`:
   ```csharp
   catch (Exception ex)
   {
       Log.Error($"ExportAudioFrame error: {ex}", typeof(AudioRendering));
   }
   ```
   This includes stack trace.

2. `AudioMixerManager.Initialize`:
   - When BASS initialization fails in all variants, log more environment info (device index, MixerFrequency, configuration values).

3. `HandleFileChange` (AudioEngine):
   - When failing to load `state.Stream`, also log whether the file exists, size if possible, and BASS last error (if available from the loader).

**Benefits**:
- Faster diagnosis of missing plugins, unsupported formats, and invalid states.

#### 8. Cache failed operator file loads or mark invalid paths (Low–Medium priority)

**Goals**: reduce repeated error logging and load attempts.

**Steps**:

1. Extend `OperatorAudioState<T>` with a flag or string for last load error:
   ```csharp
   public string? LastLoadError;
   ```

2. In `HandleFileChange`, if `loadFunc(resolvedPath)` returns null, store `LastLoadError` and avoid attempting to reload the same path every frame unless the path itself changes.

3. Optionally, provide a public helper to clear the error when the user explicitly retries or edits the path.

**Benefits**:
- Cleaner logs when users experiment with invalid files.
- Less redundant work.

#### 9. Clarify and refine seek semantics for operators (Low–Medium priority)

**Goals**: avoid unnecessary seeks and clarify API usage.

**Steps**:

1. Document in `UpdateStereoOperatorPlayback` / `UpdateSpatialOperatorPlayback` XML docs how `seek` is interpreted (absolute normalized position, where a change of more than 0.001 triggers a seek).

2. If needed, add an explicit boolean `seekTrigger` flag separate from the normalized position to avoid ambiguity between “no seek” and “seek to 0”. Example signature change:
   ```csharp
   float seek = 0f, bool seekTriggered = false
   ```
   and interpret `seek` only when `seekTriggered` is true.

3. Add rate limiting if profiling shows repeated seeks are an issue (e.g., only allow one seek per N frames or per M milliseconds for the same operator, unless forced).

**Benefits**:
- More predictable operator behaviour when upstream controls seek via UI or automation.

#### 10. Either use `_offlineMixerHandle` for multi-stream analysis or simplify the abstraction (Low priority)

**Goals**: reduce conceptual unused complexity.

**Options**:

1. **Use it**: For cases where multiple offline analysis streams need to be mixed (e.g., multi-track analysis), add offline streams to `_offlineMixerHandle` with `MixerAddChannel` and read them via `BassMix.ChannelGetData`.

2. **Simplify**: If all offline analysis is done via single decode streams, remove `_offlineMixerHandle` and related lock and simplify comments accordingly.

**Benefits**:
- Clearer understanding of the analysis path.

---

### Summary

The audio engine in `T3.Core.Audio` is already well-structured and shows careful thought regarding low-latency playback, separation of live vs export paths, and mixer management. The main opportunities lie in:

- Centralizing BASS initialization and lifecycle strictly in `AudioMixerManager`.
- Reducing GC and CPU overhead in export by reusing buffers and possibly leveraging BASS’s own resampling/mixing.
- Making shared analysis buffers and stale operator logic more explicit and robust.
- Hardening export state transitions and improving error logging.

Implementing the high-priority items (1–3) will immediately improve robustness and performance; the medium and low priority items will pay off in future maintainability and scalability (especially if the engine evolves toward more multi-threaded or complex audio features).