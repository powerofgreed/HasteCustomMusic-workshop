using System;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;
using static MusicDisplayBehaviour;

public static class AudioLoader
{
    private static bool? _managedBassAvailable;
    private static bool _dependenciesLogged = false;

    private enum LoaderType
    {
        ManagedBass,
        Unity
    }

    public static AudioClip LoadAudioFile(string filePath)
    {
        if (!_dependenciesLogged)
        {
            LogDependencyStatus();
            _dependenciesLogged = true;
        }

        if (!File.Exists(filePath))
        {
            Debug.LogError($"File not found: {filePath}");
            return null;
        }

        string extension = Path.GetExtension(filePath).ToLower();
        string fileName = Path.GetFileName(filePath);

        var currentPriority = GetLoaderPriority();
        Debug.Log($"=== Loading Audio: {fileName} ===");
        Debug.Log($"Format: {extension}, Priority: {currentPriority}");

        AudioClip clip = null;
        string usedLoader = "None";

        var loaders = GetLoadersInPriorityOrder(currentPriority);

        foreach (var loader in loaders)
        {
            try
            {
                switch (loader)
                {
                    case LoaderType.ManagedBass:
                        if (IsManagedBassAvailable())
                        {
                            clip = TryManagedBassLoader(filePath);
                            if (clip != null)
                            {
                                usedLoader = "ManagedBass";
                                break;
                            }
                        }
                        else
                        {
                            Debug.LogWarning("ManagedBass loader skipped - not available");
                        }
                        continue;

                    case LoaderType.Unity:
                        clip = TryUnityLoader(filePath, extension);
                        if (clip != null)
                        {
                            usedLoader = "Unity";
                            break;
                        }
                        continue;
                }

                if (clip != null) break;
            }
            catch (Exception e)
            {
                Debug.LogError($"{loader} loader exception: {e.Message}");
            }
        }

        if (clip == null)
        {
            Debug.LogError($"All loaders failed for {fileName}, final Unity fallback...");
            clip = TryUnityLoader(filePath, extension);
            if (clip != null) usedLoader = "Unity (Fallback)";
        }

        if (clip != null)
            Debug.Log($"✓ Loaded {fileName} with {usedLoader}");
        else
            Debug.LogError($"✗ Failed to load {fileName}");

        return clip;
    }

    private static LoaderType[] GetLoadersInPriorityOrder(LoaderPriority priority)
    {
        return priority switch
        {
            LoaderPriority.BassFirst => [LoaderType.ManagedBass, LoaderType.Unity],
            LoaderPriority.OnlyUnity => [LoaderType.Unity],
            _ => [LoaderType.ManagedBass, LoaderType.Unity]
        };
    }

    private static void LogDependencyStatus()
    {
        var sb = new StringBuilder();
        sb.AppendLine("===== AUDIO LOADER DEPENDENCY STATUS =====");
        bool mb = IsManagedBassAvailable();
        sb.AppendLine($"ManagedBass: {(mb ? "✓ AVAILABLE" : "✗ MISSING")}");
        sb.AppendLine("Unity: ✓ ALWAYS AVAILABLE (Final fallback)");
        sb.AppendLine($"Current Priority: {GetLoaderPriority()}");
        sb.AppendLine("==========================================");
        Debug.Log(sb.ToString());
    }

    private static AudioClip TryManagedBassLoader(string filePath)
    {
        try
        {
            var clip = ManagedBassLoader.LoadWithManagedBass(filePath);
            if (clip != null)
            {
                Debug.Log($"ManagedBass: Loaded {Path.GetFileName(filePath)}");
            }
            return clip;
        }
        catch (Exception e)
        {
            Debug.LogWarning($"ManagedBass loader failed: {e.Message}");
            return null;
        }
    }

    private static AudioClip TryUnityLoader(string filePath, string extension)
    {
        try
        {
            string url = "file://" + filePath;
            AudioType audioType = extension switch
            {
                ".ogg" => AudioType.OGGVORBIS,
                ".mp3" => AudioType.MPEG,
                ".aif" => AudioType.AIFF,
                ".aiff" => AudioType.AIFF,
                ".wav" => AudioType.WAV,
                ".m4a" => AudioType.ACC,
                ".aac" => AudioType.ACC,
                _ => AudioType.UNKNOWN
            };

            if (audioType == AudioType.UNKNOWN)
            {
                Debug.LogWarning($"Unity: Unsupported format {extension}");
                return null;
            }

            using UnityWebRequest www = UnityWebRequestMultimedia.GetAudioClip(url, audioType);
            var op = www.SendWebRequest();
            while (!op.isDone) { System.Threading.Thread.Sleep(5); }

            if (www.result != UnityWebRequest.Result.Success)
            {
                Debug.LogError($"UnityWebRequest error: {www.error}");
                return null;
            }

            return DownloadHandlerAudioClip.GetContent(www);
        }
        catch (Exception e)
        {
            Debug.LogWarning($"Unity loader failed: {e.Message}");
            return null;
        }
    }

    private static LoaderPriority GetLoaderPriority() => MusicDisplayBehaviour.CurrentLoaderPriority;

    private static bool IsManagedBassAvailable()
    {
        if (_managedBassAvailable == null)
        {
            try
            {
                _managedBassAvailable = ManagedBassLoader.IsNativeLibraryAvailable;
                Debug.Log(_managedBassAvailable == true
                    ? "ManagedBass: ✓ Fully available"
                    : "ManagedBass: ✗ Native bass.dll not available");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"ManagedBass availability check failed: {ex.Message}");
                _managedBassAvailable = false;
            }
        }
        return _managedBassAvailable.Value;
    }
}
