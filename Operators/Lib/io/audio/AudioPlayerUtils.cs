using System;
using System.Collections.Generic;
using T3.Core.Audio;
using T3.Core.Logging;

namespace Lib.io.audio
{
    /// <summary>
    /// Shared utility methods for audio player operators.
    /// </summary>
    internal static class AudioPlayerUtils
    {
        /// <summary>
        /// Computes a stable GUID from an instance path for operator identification.
        /// </summary>
        public static Guid ComputeInstanceGuid(IEnumerable<Guid> instancePath)
        {
            unchecked
            {
                ulong hash = 0xCBF29CE484222325;
                const ulong prime = 0x100000001B3;

                foreach (var id in instancePath)
                {
                    var bytes = id.ToByteArray();
                    foreach (var b in bytes)
                    {
                        hash ^= b;
                        hash *= prime;
                    }
                }

                var guidBytes = new byte[16];
                var hashBytes = BitConverter.GetBytes(hash);
                Array.Copy(hashBytes, 0, guidBytes, 0, 8);
                Array.Copy(hashBytes, 0, guidBytes, 8, 8);
                return new Guid(guidBytes);
            }
        }

#if DEBUG
        /// <summary>
        /// Generates a test tone WAV file for audio testing.
        /// </summary>
        /// <param name="frequency">Frequency in Hz</param>
        /// <param name="durationSeconds">Duration in seconds</param>
        /// <param name="label">Label for the temp file name</param>
        /// <param name="channels">Number of audio channels (1 for mono, 2 for stereo)</param>
        /// <returns>Path to the generated WAV file</returns>
        public static string GenerateTestTone(float frequency, float durationSeconds, string label, int channels = 2)
        {
            const int sampleRate = 48000;
            int sampleCount = (int)(sampleRate * durationSeconds);

            var tempPath = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"t3_test_tone_{label}_{frequency}hz_{durationSeconds}s_{DateTime.Now.Ticks}.wav");

            try
            {
                using var fileStream = new System.IO.FileStream(tempPath, System.IO.FileMode.Create);
                using var writer = new System.IO.BinaryWriter(fileStream);

                int dataSize = sampleCount * channels * sizeof(short);
                int fileSize = 36 + dataSize;

                // Write WAV header
                writer.Write(new[] { 'R', 'I', 'F', 'F' });
                writer.Write(fileSize);
                writer.Write(new[] { 'W', 'A', 'V', 'E' });
                writer.Write(new[] { 'f', 'm', 't', ' ' });
                writer.Write(16);
                writer.Write((short)1);
                writer.Write((short)channels);
                writer.Write(sampleRate);
                writer.Write(sampleRate * channels * sizeof(short));
                writer.Write((short)(channels * sizeof(short)));
                writer.Write((short)16);
                writer.Write(new[] { 'd', 'a', 't', 'a' });
                writer.Write(dataSize);

                // Generate sine wave with fade envelope
                const float envelopeDuration = 0.005f;
                int envelopeSamples = (int)(sampleRate * envelopeDuration);

                for (int i = 0; i < sampleCount; i++)
                {
                    float t = i / (float)sampleRate;
                    float sample = (float)Math.Sin(2.0 * Math.PI * frequency * t);

                    // Apply envelope
                    if (i < envelopeSamples)
                        sample *= i / (float)envelopeSamples;
                    else if (i > sampleCount - envelopeSamples)
                        sample *= (sampleCount - i) / (float)envelopeSamples;

                    short sampleValue = (short)(sample * 16384);

                    for (int ch = 0; ch < channels; ch++)
                        writer.Write(sampleValue);
                }

                AudioConfig.LogAudioDebug($"Generated test tone: {tempPath} ({durationSeconds}s @ {frequency}Hz, {channels}ch)");
                return tempPath;
            }
            catch (Exception ex)
            {
                Log.Error($"Failed to generate test tone: {ex.Message}");
                return string.Empty;
            }
        }

        /// <summary>
        /// Cleans up a test file if it exists.
        /// </summary>
        public static void CleanupTestFile(string filePath)
        {
            if (string.IsNullOrEmpty(filePath) || !System.IO.File.Exists(filePath))
                return;

            try
            {
                System.IO.File.Delete(filePath);
            }
            catch
            {
                // Ignore cleanup errors
            }
        }
#endif
    }
}
