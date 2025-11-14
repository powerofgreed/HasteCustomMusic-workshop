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
    private static string _lastError = string.Empty;

    [DllImport("kernel32", SetLastError = true)]
    private static extern bool SetDllDirectory(string lpPathName);

    [DllImport("kernel32", SetLastError = true)]
    private static extern IntPtr LoadLibrary(string lpFileName);

    public static bool Initialize()
    {
        if (_initialized) return true;

        try
        {
            Debug.Log("[ManagedBassLoader] Initializing BASS for Steam Workshop...");

            // Get the correct plugins path for Steam Workshop
            string pluginsPath = GetSteamWorkshopPluginsPath();

            if (!string.IsNullOrEmpty(pluginsPath) && Directory.Exists(pluginsPath))
            {
                // Set DLL directory - this is crucial for Steam Workshop
                if (SetDllDirectory(pluginsPath))
                {
                    Debug.Log($"[ManagedBassLoader] Successfully set DLL directory to: {pluginsPath}");
                }
                else
                {
                    int error = Marshal.GetLastWin32Error();
                    Debug.LogError($"[ManagedBassLoader] Failed to set DLL directory. Error code: {error}");
                    return false;
                }

                // Also try to manually load critical DLLs as fallback
                PreloadCriticalDlls(pluginsPath);
            }
            else
            {
                Debug.LogError($"[ManagedBassLoader] Plugins directory not found: {pluginsPath}");
                return false;
            }

            // Wait a moment for DLL directory to take effect
            System.Threading.Thread.Sleep(100);

            // Check if BASS is available
            if (!CheckNativeLibrary())
            {
                Debug.LogError("[ManagedBassLoader] Native BASS library not available after setting DLL directory.");
                return false;
            }

            // Initialize BASS
            if (!Bass.Init(-1, 44100, DeviceInitFlags.Default, IntPtr.Zero))
            {
                Errors error = Bass.LastError;
                if (error != Errors.Already)
                {
                    Debug.LogError($"[ManagedBassLoader] BASS initialization failed. Error: {error}");
                    return false;
                }
            }

            _initialized = true;
            Debug.Log("[ManagedBassLoader] BASS initialized successfully!");

            // Test loading different formats
            TestFormatSupport();

            return true;
        }
        catch (Exception ex)
        {
            Debug.LogError($"[ManagedBassLoader] Initialization error: {ex}");
            return false;
        }
    }

    private static string GetSteamWorkshopPluginsPath()
    {
        try
        {
            // Method 1: Use WorkshopHelper if available
            if (WorkshopHelper.ModDirectory != null)
            {
                string workshopPath = Path.Combine(WorkshopHelper.ModDirectory, "Plugins", "x86_64");
                if (Directory.Exists(workshopPath))
                {
                    Debug.Log($"[ManagedBassLoader] Found Workshop path: {workshopPath}");
                    return workshopPath;
                }
            }

            // Method 2: Look for Plugins/x86_64 relative to current assembly
            string assemblyDir = Path.GetDirectoryName(typeof(ManagedBassLoader).Assembly.Location);
            if (!string.IsNullOrEmpty(assemblyDir))
            {
                // Try different possible locations
                string[] possiblePaths = {
                    Path.Combine(assemblyDir, "Plugins", "x86_64"),
                    Path.Combine(assemblyDir, "..", "Plugins", "x86_64"),
                    Path.Combine(assemblyDir, "..", "..", "Plugins", "x86_64"),
                    Path.Combine(assemblyDir, "x86_64"),
                    Path.Combine(assemblyDir, "Plugins")
                };

                foreach (string path in possiblePaths)
                {
                    string fullPath = Path.GetFullPath(path);
                    if (Directory.Exists(fullPath))
                    {
                        Debug.Log($"[ManagedBassLoader] Found assembly-relative path: {fullPath}");
                        return fullPath;
                    }
                }
            }

            // Method 3: Current directory
            string currentDir = Environment.CurrentDirectory;
            string currentDirPath = Path.Combine(currentDir, "Plugins", "x86_64");
            if (Directory.Exists(currentDirPath))
            {
                Debug.Log($"[ManagedBassLoader] Found current directory path: {currentDirPath}");
                return currentDirPath;
            }

            Debug.LogError("[ManagedBassLoader] Could not find plugins directory in any location");
            return null;
        }
        catch (Exception ex)
        {
            Debug.LogError($"[ManagedBassLoader] Error getting plugins path: {ex}");
            return null;
        }
    }

    private static void PreloadCriticalDlls(string pluginsPath)
    {
        try
        {
            string[] criticalDlls = {
                "bass.dll",
                "bassflac.dll",
                "bassopus.dll",
                "basshls.dll",
                "bassmix.dll",
                "bass_aac.dll",
                "bassalac.dll",
                "bass_spx.dll",
                "bass_tta.dll"
            };

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
                        int error = Marshal.GetLastWin32Error();
                        Debug.LogWarning($"[ManagedBassLoader] Failed to preload {dllName}. Error code: {error}");
                    }
                }
                else
                {
                    Debug.LogWarning($"[ManagedBassLoader] DLL not found: {dllPath}");
                }
            }
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[ManagedBassLoader] Preload warning: {ex.Message}");
        }
    }

    private static bool CheckNativeLibrary()
    {
        _nativeLibChecked = true;
        try
        {
            // Try to get BASS version - this will fail if native DLLs aren't loaded
            var version = Bass.Version;
            Debug.Log($"[ManagedBassLoader] BASS version: {version}");

            // Test if specific codecs are available
            TestCodecAvailability();

            _nativeLibAvailable = true;
            return true;
        }
        catch (DllNotFoundException dllEx)
        {
            Debug.LogError($"[ManagedBassLoader] bass.dll not found: {dllEx.Message}");
            _nativeLibAvailable = false;
            return false;
        }
        catch (BadImageFormatException badImageEx)
        {
            Debug.LogError($"[ManagedBassLoader] Architecture mismatch: {badImageEx.Message}");
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

    private static void TestCodecAvailability()
    {
        try
        {
            Debug.Log("[ManagedBassLoader] Testing codec availability...");

            // Test FLAC
            try
            {
                Bass.PluginLoad("bassflac.dll");
                Debug.Log("[ManagedBassLoader] FLAC codec: AVAILABLE");
            }
            catch
            {
                Debug.LogWarning("[ManagedBassLoader] FLAC codec: UNAVAILABLE");
            }

            // Test Opus
            try
            {
                Bass.PluginLoad("bassopus.dll");
                Debug.Log("[ManagedBassLoader] Opus codec: AVAILABLE");
            }
            catch
            {
                Debug.LogWarning("[ManagedBassLoader] Opus codec: UNAVAILABLE");
            }

            // Test HLS
            try
            {
                Bass.PluginLoad("basshls.dll");
                Debug.Log("[ManagedBassLoader] HLS codec: AVAILABLE");
            }
            catch
            {
                Debug.LogWarning("[ManagedBassLoader] HLS codec: UNAVAILABLE");
            }

            // Test AAC
            try
            {
                Bass.PluginLoad("bass_aac.dll");
                Debug.Log("[ManagedBassLoader] AAC codec: AVAILABLE");
            }
            catch
            {
                Debug.LogWarning("[ManagedBassLoader] AAC codec: UNAVAILABLE");
            }
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[ManagedBassLoader] Codec test error: {ex.Message}");
        }
    }

    private static void TestFormatSupport()
    {
        try
        {
            Debug.Log("[ManagedBassLoader] Testing format support...");

            // Test basic format support flags
            var supported = Bass.SupportedFormats;

            Debug.Log($"[ManagedBassLoader] BASS Supported Formats: {supported}");
       

            // Note: Opus support might not be in the flags enum, so we test differently
            Debug.Log("[ManagedBassLoader] - Opus: (testing via plugin load)");
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[ManagedBassLoader] Format support test error: {ex.Message}");
        }
    }

    public static AudioClip LoadWithManagedBass(string filePath)
    {
        if (!_initialized && !Initialize())
        {
            Debug.LogWarning("[ManagedBassLoader] BASS not initialized. Cannot load file.");
            return null;
        }

        return LoadWithManagedBassInternal(filePath);
    }

    private static AudioClip LoadWithManagedBassInternal(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            Debug.LogError($"[ManagedBassLoader] File not found: {filePath}");
            return null;
        }

        int stream = 0;
        try
        {
            // Use appropriate flags based on file type
            BassFlags flags = BassFlags.Decode | BassFlags.Float | BassFlags.Prescan;

            // For remote files or certain formats, you might need different flags
            if (filePath.StartsWith("http", StringComparison.OrdinalIgnoreCase) ||
                filePath.StartsWith("ftp", StringComparison.OrdinalIgnoreCase))
            {
                flags |= BassFlags.StreamDownloadBlocks;
            }

            Debug.Log($"[ManagedBassLoader] Creating stream for: {Path.GetFileName(filePath)}");
            stream = Bass.CreateStream(filePath, 0, 0, flags);

            if (stream == 0)
            {
                Errors error = Bass.LastError;
                Debug.LogError($"[ManagedBassLoader] CreateStream failed for {Path.GetFileName(filePath)}. Error: {error}");
                return null;
            }

            // ChannelInfo from the same stream we will read
            if (!Bass.ChannelGetInfo(stream, out ChannelInfo info))
            {
                Errors e = Bass.LastError;
                Debug.LogWarning($"ChannelGetInfo failed: {e}");
                return null;
            }

            Debug.Log($"[ManagedBassLoader] Loading audio: {Path.GetFileName(filePath)}, Channels: {info.Channels}, Frequency: {info.Frequency}");

            // Authoritative length in bytes (decoded float bytes)
            long lengthBytes = Bass.ChannelGetLength(stream, PositionFlags.Bytes);
            if (lengthBytes <= 0)
            {
                Debug.LogWarning("[ManagedBassLoader] Invalid audio length");
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
                Debug.LogError($"[ManagedBassLoader] ChannelGetData failed: {err}");
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
                    Debug.Log($"[ManagedBassLoader] Downmixing {srcChannels}-channel -> stereo for {Path.GetFileName(filePath)}");
                    outputSamples = DownmixToStereo(samples, srcChannels);
                    outputChannels = 2;
                }
                else
                {
                    // Keep original channel count for formats you trust (e.g., 8 for 7.1)
                    outputSamples = samples;
                    outputChannels = srcChannels;
                    Debug.Log($"[ManagedBassLoader] Keeping {srcChannels}-channel audio: {Path.GetFileName(filePath)}");
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
                Debug.LogWarning("[ManagedBassLoader] Computed zero samples per channel");
                return null;
            }

            var clip = AudioClip.Create(Path.GetFileNameWithoutExtension(filePath), samplesPerChannel, outputChannels, info.Frequency, false);
            clip.SetData(outputSamples, 0);

            Debug.Log($"[ManagedBassLoader] ✓ Loaded: {filePath} (srcCh={srcChannels} -> outCh={outputChannels}, freq={info.Frequency}, frames={samplesPerChannel})");
            return clip;
        }
        catch (Exception ex)
        {
            Debug.LogError($"[ManagedBassLoader] Error loading {filePath}: {ex}");
            return null;
        }
        finally
        {
            if (stream != 0)
            {
                Bass.StreamFree(stream);
            }
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

    public static bool IsInitialized => _initialized;
    public static bool IsNativeLibraryAvailable => _nativeLibAvailable;

    public static void Cleanup()
    {
        if (_initialized)
        {
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
    }

    public static string GetLastError()
    {
        return _lastError;
    }
}