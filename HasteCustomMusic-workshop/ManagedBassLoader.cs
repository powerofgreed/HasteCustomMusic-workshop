using ManagedBass;
using System;
using System.IO;
using System.Runtime.InteropServices;
using UnityEngine;

public static class ManagedBassLoader
{
    private static bool _initialized = false;
    private static bool _nativeLibAvailable = false;
    private static string _nativeLibsDir = null;
    private static string _lastError = string.Empty;
    private static readonly object _initLock = new();

    [DllImport("kernel32", SetLastError = true)]
    private static extern bool SetDllDirectory(string lpPathName);

    [DllImport("kernel32", SetLastError = true)]
    private static extern IntPtr LoadLibrary(string lpFileName);

    public static bool Initialize()
    {
        lock (_initLock)
        {
            if (_initialized)
            {
                Debug.Log("[ManagedBassLoader] Already initialized");
                return true;
            }

            try
            {
                Debug.Log("[ManagedBassLoader] Initializing BASS for Steam Workshop...");

                // Get the mod's persistent data directory
                string modDataDir = WorkshopHelper.PersistentDataPath;
                if (string.IsNullOrEmpty(modDataDir) || !Directory.Exists(modDataDir))
                {
                    Debug.LogError("[ManagedBassLoader] Mod data directory not found");
                    return false;
                }

                // Create native libraries directory
                _nativeLibsDir = Path.Combine(modDataDir, "NativeLibraries");
                Directory.CreateDirectory(_nativeLibsDir);
                Debug.Log($"[ManagedBassLoader] Native libraries directory: {_nativeLibsDir}");

                // Get source plugins path
                string pluginsPath = GetSteamWorkshopPluginsPath();
                if (string.IsNullOrEmpty(pluginsPath) || !Directory.Exists(pluginsPath))
                {
                    Debug.LogError("[ManagedBassLoader] Plugins directory not found");
                    return false;
                }

                // Copy .native files to .dll in our directory and load them
                if (!CopyAndLoadNativeLibraries(pluginsPath, _nativeLibsDir))
                {
                    Debug.LogError("[ManagedBassLoader] Failed to copy and load native libraries");
                    return false;
                }

                // Set DLL directory to our native libs directory
                if (!SetDllDirectory(_nativeLibsDir))
                {
                    int error = Marshal.GetLastWin32Error();
                    Debug.LogWarning($"[ManagedBassLoader] Failed to set DLL directory. Error: {GetWin32ErrorMessage(error)}");
                }

                // Wait for DLL loading to settle
                System.Threading.Thread.Sleep(200);

                // Verify BASS is working
                if (!VerifyBassInitialization())
                {
                    Debug.LogError("[ManagedBassLoader] BASS verification failed");
                    return false;
                }

                _initialized = true;
                Debug.Log("[ManagedBassLoader] BASS initialized successfully!");
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[ManagedBassLoader] Initialization error: {ex}");
                return false;
            }
        }
    }
    private static bool CopyAndLoadNativeLibraries(string sourceDir, string targetDir)
    {
        try
        {
            // Get .native files from source
            string[] nativeFiles = Directory.GetFiles(sourceDir, "*.native");
            if (nativeFiles.Length == 0)
            {
                Debug.LogWarning("[ManagedBassLoader] No .native files found in source directory");
                return false;
            }

            Debug.Log($"[ManagedBassLoader] Found {nativeFiles.Length} .native files");

            int copiedCount = 0;
            int loadedCount = 0;
            var loadedLibraries = new System.Collections.Generic.List<string>();

            foreach (string nativeFile in nativeFiles)
            {
                string fileName = Path.GetFileName(nativeFile);
                string dllName = Path.GetFileNameWithoutExtension(nativeFile) + ".dll";
                string targetDllPath = Path.Combine(targetDir, dllName);

                try
                {
                    // Check if we need to copy (file doesn't exist or is different)
                    bool needsCopy = !File.Exists(targetDllPath);
                    if (!needsCopy)
                    {
                        // Check file size and last write time
                        var sourceInfo = new FileInfo(nativeFile);
                        var targetInfo = new FileInfo(targetDllPath);

                        needsCopy = sourceInfo.Length != targetInfo.Length ||
                                   sourceInfo.LastWriteTime > targetInfo.LastWriteTime;
                    }

                    if (needsCopy)
                    {
                        File.Copy(nativeFile, targetDllPath, true);
                        copiedCount++;
                        Debug.Log($"[ManagedBassLoader] Copied: {fileName} -> {dllName}");
                    }
                    else
                    {
                        Debug.Log($"[ManagedBassLoader] Skipped copy (up-to-date): {fileName}");
                    }

                    // Try to load the DLL
                    if (LoadLibraryAndVerify(targetDllPath, dllName))
                    {
                        loadedCount++;
                        loadedLibraries.Add(dllName);
                        Debug.Log($"[ManagedBassLoader] ✓ Loaded: {dllName}");
                    }
                    else
                    {
                        Debug.LogWarning($"[ManagedBassLoader] ✗ Failed to load: {dllName}");
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[ManagedBassLoader] Error processing {fileName}: {ex.Message}");
                }
            }

            Debug.Log($"[ManagedBassLoader] Copy summary: {copiedCount} copied, {loadedCount}/{nativeFiles.Length} loaded successfully");

            // Log detailed loading results
            if (loadedLibraries.Count > 0)
            {
                Debug.Log("[ManagedBassLoader] Successfully loaded libraries:");
                foreach (string lib in loadedLibraries)
                {
                    Debug.Log($"  - {lib}");
                }
            }

            return loadedCount > 0;
        }
        catch (Exception ex)
        {
            Debug.LogError($"[ManagedBassLoader] Copy and load failed: {ex}");
            return false;
        }
    }
    private static bool LoadLibraryAndVerify(string dllPath, string dllName)
    {
        try
        {
            // Load the library
            IntPtr handle = LoadLibrary(dllPath);
            if (handle == IntPtr.Zero)
            {
                int error = Marshal.GetLastWin32Error();
                string errorMsg = GetWin32ErrorMessage(error);
                Debug.LogWarning($"[ManagedBassLoader] LoadLibrary failed for {dllName}: {errorMsg}");
                return false;
            }

            // Additional verification for critical libraries
            return VerifyLibraryLoad(dllName, handle);
        }
        catch (Exception ex)
        {
            Debug.LogError($"[ManagedBassLoader] Exception loading {dllName}: {ex.Message}");
            return false;
        }
    }
    private static bool VerifyLibraryLoad(string dllName, IntPtr handle)
    {
        // Special verification for bass.dll
        if (dllName.Equals("bass.dll", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                // Try to get BASS version to verify it's loaded correctly
                var version = Bass.Version;
                Debug.Log($"[ManagedBassLoader] BASS library verified: {version}");
                _nativeLibAvailable = true;
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[ManagedBassLoader] BASS verification failed: {ex.Message}");
                return false;
            }
        }

        // For other libraries, we can't easily verify, so assume success if handle is valid
        return handle != IntPtr.Zero;
    }
    private static bool VerifyBassInitialization()
    {
        try
        {
            // Double-check BASS version
            var version = Bass.Version;
            Debug.Log($"[ManagedBassLoader] BASS Version: {version}");

            int freq = AudioSettings.outputSampleRate;
            if (freq <= 0) freq = 44100; // Fallback

            if (!Bass.Init(-1, freq, DeviceInitFlags.Default, IntPtr.Zero))
            {
                Errors error = Bass.LastError;
                if (error == Errors.Already)
                {
                    Debug.Log("[ManagedBassLoader] BASS already initialized");
                    return true;
                }

                // Try fallback initialization
                Debug.LogWarning($"[ManagedBassLoader] Primary BASS init failed: {error}, trying fallback...");

                if (!Bass.Init(0, 44100, DeviceInitFlags.Default, IntPtr.Zero))
                {
                    Debug.LogError($"[ManagedBassLoader] Fallback BASS init failed: {Bass.LastError}");
                    return false;
                }
            }

            // Test critical plugins
            return PreloadPlugins();
        }
        catch (Exception ex)
        {
            Debug.LogError($"[ManagedBassLoader] BASS verification exception: {ex}");
            return false;
        }
    }
    private static bool PreloadPlugins()
    {
        try
        {
            string[] Plugins = { "bassflac.dll", "bassopus.dll", "bass_aac.dll", "bass_ac3.dll", "bass_mpc.dll", "bass_spx.dll", "bass_tta.dll", "bassalac.dll", "basshls.dll", "bassmidi.dll", "bassmix.dll" };
            int loadedCount = 0;

            foreach (string plugin in Plugins)
            {
                if (Bass.PluginLoad(plugin)!=0)
                {
                    loadedCount++;
                    Debug.Log($"[ManagedBassLoader] ✓ {plugin} loaded");
                }
                else
                {
                    Debug.LogWarning($"[ManagedBassLoader] ⚠ {plugin} not available: {Bass.LastError}");
                }
            }

            Debug.Log($"[ManagedBassLoader] Loaded {loadedCount}/{Plugins.Length}  plugins");
            return loadedCount > 0; // At least one plugin should work
        }
        catch (Exception ex)
        {
            Debug.LogError($"[ManagedBassLoader] Plugin test failed: {ex}");
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

            // Method 2: Look relative to current assembly
            string assemblyDir = Path.GetDirectoryName(typeof(ManagedBassLoader).Assembly.Location);
            if (!string.IsNullOrEmpty(assemblyDir))
            {
                string[] possiblePaths = {
                    Path.Combine(assemblyDir, "Plugins", "x86_64"),
                    Path.Combine(assemblyDir, "..", "Plugins", "x86_64"),
                    Path.Combine(assemblyDir, "..", "..", "Plugins", "x86_64"),
                    Path.Combine(assemblyDir, "x86_64"),
                    Path.Combine(assemblyDir, "Plugins"),
                    assemblyDir
                };

                foreach (string path in possiblePaths)
                {
                    string fullPath = Path.GetFullPath(path);
                    if (Directory.Exists(fullPath))
                    {
                        Debug.Log($"[ManagedBassLoader] Found path: {fullPath}");
                        return fullPath;
                    }
                }
            }

            Debug.LogError("[ManagedBassLoader] Could not find plugins directory");
            return null;
        }
        catch (Exception ex)
        {
            Debug.LogError($"[ManagedBassLoader] Error getting plugins path: {ex}");
            return null;
        }
    }

    private static string GetWin32ErrorMessage(int errorCode)
    {
        try
        {
            var win32Exception = new System.ComponentModel.Win32Exception(errorCode);
            return win32Exception.Message;
        }
        catch
        {
            return $"Windows error 0x{errorCode:X}";
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
    [RuntimeInitializeOnLoadMethod]
    private static void RegisterAppExitHandler()
    {
        Application.quitting += Cleanup;
    }

    public static string GetLastError()
    {
        return _lastError;
    }
}