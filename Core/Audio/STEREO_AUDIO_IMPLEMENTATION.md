# Stereo Audio Implementation for StereoAudioPlayer

## Overview
Implemented full-featured stereo audio playback for the `StereoAudioPlayer` operator with comprehensive playback controls, real-time analysis, and test tone generation capabilities.

## Components Created/Modified

### 1. StereoAudioPlayer.cs
**Location:** `Operators/Lib/io/audio/StereoAudioPlayer.cs`

**Purpose:** Dedicated operator for stereo audio playback with advanced controls and real-time analysis

**Key Features:**
- **Playback Controls**: Play, Pause, Stop, Resume
- **Audio Parameters**: Volume, Mute, Panning (-1 to +1), Speed (0.1x to 4.0x), Seek (0-1 normalized)
- **Real-time Analysis**: Level metering, waveform visualization, spectrum analysis
- **Test Mode**: Built-in test tone generator for debugging and verification
- **Status Outputs**: IsPlaying, IsPaused flags for operator graph logic
- **Low-Latency Design**: Optimized for ~20-60ms latency through buffer management

### 2. StereoOperatorAudioStream.cs
**Location:** `Core/Audio/StereoOperatorAudioStream.cs`

**Purpose:** Core audio stream class for operator-based stereo audio playback

**Key Features:**
- **Stereo/Mono Support**: Handles 1-channel (mono) and 2-channel (stereo) audio files
- **Format Support**: WAV, MP3, OGG, FLAC (via BASS plugins)
- **Smart Buffering**: Optimized buffer sizes for short and long audio clips
- **Stale Detection**: Automatically mutes inactive streams to save CPU
- **Real-time Parameters**: Dynamic volume, panning, speed, and seek control
- **Sample-Accurate Seeking**: Precise playback positioning
- **Comprehensive Analysis**: Level metering, waveform data, spectrum data

### 3. AudioEngine.cs (MODIFIED)
**Location:** `Core/Audio/AudioEngine.cs`

**New Methods for Stereo Operators:**
