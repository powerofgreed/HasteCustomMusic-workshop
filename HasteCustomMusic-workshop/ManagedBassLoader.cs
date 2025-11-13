using ManagedBass;
using System;
using System.IO;
using System.Runtime.InteropServices;
using UnityEngine;

public static class ManagedBassLoader
{
    private static bool _initialized = false;
    private static bool _nativeLibChecked = false;
    private static bool _nativeLibAvailable = false;

    [DllImport("kernel32", SetLastError = true)]
    private static extern bool SetDllDirectory(string lpPathName);

    [DllImport("kernel32", SetLastError = true)]
    private static extern IntPtr LoadLibrary(string lpFileName);
    // Initialization / probes

    // Last error (for debugging if needed)
    private static string _lastError = string.Empty;

    /// <summary>
    /// Stable path: decode entire file with BASS (float), optional downmix, create Unity AudioClip in one shot.
    /// </summary>
    public static AudioClip LoadWithManagedBass(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            Debug.LogError($"[ManagedBassLoader] File not found: {filePath}");
            return null;
        }

        if (!_nativeLibChecked)
        {
            if (!CheckNativeLibrary())
            {
                Debug.LogWarning("[ManagedBassLoader] Native BASS unavailable. Skipping.");
                return null;
            }
        }
        if (!_nativeLibAvailable) return null;

        if (!_initialized)
        {
            try
            {
                if (!Bass.Init(-1, 44100, DeviceInitFlags.Default))
                {
                    var err = Bass.LastError;
                    if (err != Errors.Already)
                    {
                        _lastError = $"BASS.Init failed: {err}";
                        Debug.LogWarning($"[ManagedBassLoader] {_lastError}");
                        _initialized = true;
                        return null;
                    }
                }
                _initialized = true;
            }
            catch (Exception ex)
            {
                _lastError = $"ManagedBass init exception: {ex}";
                Debug.LogError($"[ManagedBassLoader] {_lastError}");
                return null;
            }
        }

        int stream = 0;
        try
        {
            // Use Decode + Float (Prescan optional for remote/prescan-prone)
            stream = Bass.CreateStream(filePath, 0, 0, BassFlags.Decode | BassFlags.Float | BassFlags.Prescan);
            if (stream == 0)
            {
                Errors e = Bass.LastError;
                Debug.LogWarning($"CreateStream failed: {e}");
                return null;
            }

            // ChannelInfo from the same stream we will read
            if (!Bass.ChannelGetInfo(stream, out ChannelInfo info))
            {
                Errors e = Bass.LastError;
                Bass.StreamFree(stream);
                Debug.LogWarning($"ChannelGetInfo failed: {e}");
                return null;
            }

            Debug.Log($"Loading audio: {Path.GetFileName(filePath)}, Channels: {info.Channels}, Frequency: {info.Frequency}");

            // Authoritative length in bytes (decoded float bytes)
            long lengthBytes = Bass.ChannelGetLength(stream, PositionFlags.Bytes);
            if (lengthBytes <= 0)
            {
                Bass.StreamFree(stream);
                Debug.LogWarning("Invalid audio length");
                return null;
            }

            // Number of floats in the stream (total floats across all channels)
            int totalFloatCount = (int)(lengthBytes / sizeof(float));
            var samples = new float[totalFloatCount];

            // ChannelGetData returns bytes when using float[] overload; ask for lengthBytes bytes
            int bytesRead = Bass.ChannelGetData(stream, samples, (int)lengthBytes);
            if (bytesRead <= 0)
            {
                Errors err = Bass.LastError;
                Bass.StreamFree(stream);
                Debug.LogError($"ChannelGetData failed: {err}");
                return null;
            }

            int actualFloatCount = bytesRead / sizeof(float);
            if (actualFloatCount < samples.Length)
                Array.Resize(ref samples, actualFloatCount);

            int srcChannels = Math.Max(1, info.Channels);
            float[] outputSamples;
            int outputChannels;

            if (srcChannels > 2)
            {
                // Downmix problematic formats to stereo. Keep known-working formats if you want.
                bool isProblematic = srcChannels == 3 || srcChannels == 4 || srcChannels == 5 || srcChannels == 7;
                if (isProblematic)
                {
                    Debug.Log($"Downmixing {srcChannels}-channel -> stereo for {Path.GetFileName(filePath)}");
                    outputSamples = DownmixToStereo(samples, srcChannels);
                    outputChannels = 2;
                }
                else
                {
                    // Keep original channel count for formats you trust (e.g., 8 for 7.1)
                    outputSamples = samples;
                    outputChannels = srcChannels;
                    Debug.Log($"Keeping {srcChannels}-channel audio: {Path.GetFileName(filePath)}");
                }
            }
            else
            {
                outputSamples = samples;
                outputChannels = srcChannels;
            }

            int samplesPerChannel = outputSamples.Length / outputChannels;
            if (samplesPerChannel <= 0)
            {
                Debug.LogWarning("Computed zero samples per channel");
                Bass.StreamFree(stream);
                return null;
            }

            var clip = AudioClip.Create(Path.GetFileNameWithoutExtension(filePath), samplesPerChannel, outputChannels, info.Frequency, false);
            clip.SetData(outputSamples, 0);

            Debug.Log($"Loaded: {filePath} (srcCh={srcChannels} -> outCh={outputChannels}, freq={info.Frequency}, frames={samplesPerChannel})");
            return clip;
        }
        finally
        {
            if (stream != 0) Bass.StreamFree(stream);
        }
    }

    /// <summary>
    /// Downmix common surround layouts to stereo with gentle center/side weighting.
    /// </summary>
    private static float[] DownmixToStereo(float[] data, int sourceChannels)
    {
        int frames = data.Length / sourceChannels;
        var stereo = new float[frames * 2];

        for (int f = 0; f < frames; f++)
        {
            int src = f * sourceChannels;
            int dst = f * 2;

            float L = 0f, R = 0f;
            switch (sourceChannels)
            {
                case 3: // L, R, C
                    L = data[src] + data[src + 2] * 0.707f;
                    R = data[src + 1] + data[src + 2] * 0.707f;
                    break;

                case 4: // L, R, Ls, Rs
                    L = data[src] + data[src + 2] * 0.707f;
                    R = data[src + 1] + data[src + 3] * 0.707f;
                    break;

                case 5: // L, R, C, Ls, Rs
                    L = data[src] + data[src + 2] * 0.707f + data[src + 3] * 0.5f;
                    R = data[src + 1] + data[src + 2] * 0.707f + data[src + 4] * 0.5f;
                    break;

                case 7: // 6.1: L, R, C, LFE, Sl, Sr, Bc (approx)
                    L = data[src] + data[src + 2] * 0.707f + data[src + 4] * 0.5f + data[src + 5] * 0.5f;
                    R = data[src + 1] + data[src + 2] * 0.707f + data[src + 4] * 0.5f + data[src + 6] * 0.5f;
                    break;

                default:
                    // Fallback: average all channels equally
                    for (int c = 0; c < sourceChannels; c++)
                    {
                        L += data[src + c];
                        R += data[src + c];
                    }
                    L /= sourceChannels;
                    R /= sourceChannels;
                    Debug.LogWarning($"[ManagedBassLoader] Unknown downmix layout: {sourceChannels}, using equal average.");
                    break;
            }

            // Avoid clipping
            float m = Mathf.Max(Mathf.Abs(L), Mathf.Abs(R));
            if (m > 1f) { L /= m; R /= m; }

            stereo[dst] = Mathf.Clamp(L, -1f, 1f);
            stereo[dst + 1] = Mathf.Clamp(R, -1f, 1f);
        }

        return stereo;
    }
    public static bool Initialize()
    {
        if (_initialized) return true;

        try
        {
            Debug.Log("[ManagedBassLoader] Initializing BASS for Workshop...");

            // Set the DLL directory to our Plugins folder
            string pluginsPath = Path.Combine(WorkshopHelper.ModDirectory, "Plugins", "x86_64");

            if (Directory.Exists(pluginsPath))
            {
                // Method 1: Set DLL directory (affects entire process)
                SetDllDirectory(pluginsPath);
                Debug.Log($"[ManagedBassLoader] Set DLL directory to: {pluginsPath}");

                // Method 2: Also add to PATH environment variable
                string currentPath = Environment.GetEnvironmentVariable("PATH") ?? "";
                if (!currentPath.Contains(pluginsPath))
                {
                    Environment.SetEnvironmentVariable("PATH", currentPath + Path.PathSeparator + pluginsPath);
                }

                // Method 3: Manually load critical DLLs as fallback
                PreloadCriticalDlls(pluginsPath);
            }
            else
            {
                Debug.LogWarning($"[ManagedBassLoader] Plugins directory not found: {pluginsPath}");
                // Fallback: try loading from same directory as mod
                string fallbackPath = WorkshopHelper.ModDirectory;
                SetDllDirectory(fallbackPath);
                Debug.Log($"[ManagedBassLoader] Using fallback path: {fallbackPath}");
            }

            // Now check if native library is available
            if (!CheckNativeLibrary())
            {
                Debug.LogError("[ManagedBassLoader] Native library check failed. Audio features will be disabled.");
                return false;
            }

            // Initialize BASS
            if (!Bass.Init(-1, 44100, DeviceInitFlags.Default, IntPtr.Zero))
            {
                int error = (int)Bass.LastError;
                Debug.LogError($"[ManagedBassLoader] BASS initialization failed. Error code: {error}");
                return false;
            }

            _initialized = true;
            Debug.Log("[ManagedBassLoader] BASS initialized successfully.");
            return true;
        }
        catch (Exception ex)
        {
            Debug.LogError($"[ManagedBassLoader] Initialization error: {ex}");
            return false;
        }
    }
    private static void PreloadCriticalDlls(string pluginsPath)
    {
        try
        {
            string[] criticalDlls = { "bass.dll", "bassflac.dll", "bassopus.dll" };

            foreach (string dllName in criticalDlls)
            {
                string dllPath = Path.Combine(pluginsPath, dllName);
                if (File.Exists(dllPath))
                {
                    IntPtr handle = LoadLibrary(dllPath);
                    if (handle != IntPtr.Zero)
                    {
                        Debug.Log($"[ManagedBassLoader] Preloaded: {dllName}");
                    }
                    else
                    {
                        Debug.LogWarning($"[ManagedBassLoader] Failed to preload: {dllName}");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[ManagedBassLoader] Preload warning: {ex.Message}");
        }
    }

    /// <summary>
    /// Probe bass.dll once. Caches availability to avoid repeated exceptions.
    /// </summary>
    private static bool CheckNativeLibrary()
    {
        _nativeLibChecked = true;
        try
        {
            var ver = Bass.Version;
            Debug.Log($"[ManagedBassLoader] BASS version: {ver}");
            _nativeLibAvailable = true;
            return true;
        }
        catch (DllNotFoundException)
        {
            Debug.LogError("[ManagedBassLoader] bass.dll not found (ensure correct location and architecture).");
            _nativeLibAvailable = false;
            return false;
        }
        catch (BadImageFormatException ex)
        {
            Debug.LogError($"[ManagedBassLoader] Architecture mismatch for bass.dll: {ex.Message}");
            _nativeLibAvailable = false;
            return false;
        }
        catch (Exception ex)
        {
            Debug.LogError($"[ManagedBassLoader] Error checking native library: {ex}");
            _nativeLibAvailable = false;
            return false;
        }
    }

    /// <summary>
    /// Call on plugin shutdown if you need to free BASS explicitly.
    /// </summary>
    public static void Cleanup(bool final = true)
    {
        if (!_initialized) return;
        if (!final) return;

        try
        {
            Bass.Free();
            _initialized = false;
            Debug.Log("[ManagedBassLoader] BASS freed");
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[ManagedBassLoader] Cleanup error: {ex.Message}");
        }
    }

    public static bool IsNativeLibraryAvailable()
    {
        if (!_nativeLibChecked)
        {
            CheckNativeLibrary();
        }
        return _nativeLibAvailable;
    }

    public static bool IsInitialized => _initialized;
}
