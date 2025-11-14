using System;
using System.IO;
using System.Reflection;
using UnityEngine;

public static class WorkshopHelper
{
    private static string _modDirectory;
    private static string _customMusicDirectory;
    private static string _gameRoot;
    public static string _persistentDataDirectory;

    public static string ModDirectory = GetModDirectory();
    public static string GetModDirectory()
    {
        try
        {
            var assemblyLocation = Assembly.GetExecutingAssembly().Location;
            return Path.GetDirectoryName(assemblyLocation);
        }
        catch (Exception ex)
        {
            Debug.LogError($"Error getting mod directory: {ex}");
            return PersistentDataPath; // Fallback
        }
    }
    public static string GameRoot
    {
        get
        {
            if (_gameRoot == null)
            {
                // Application.dataPath = .../Haste_Data
                _gameRoot = Directory.GetParent(Application.dataPath)?.FullName;

                if (string.IsNullOrEmpty(_gameRoot) || !Directory.Exists(_gameRoot))
                {
                    // Fallback to executable path
                    _gameRoot = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                }

                Debug.Log($"Game root: {_gameRoot}");
            }
            return _gameRoot;
        }
    }
    public static string PersistentDataPath
    {
        get
        {
            if (_persistentDataDirectory == null)
            {
                _persistentDataDirectory = Path.Combine(GameRoot, "HasteCustomMusic");

                // Create directory if it doesn't exist
                if (!Directory.Exists(_persistentDataDirectory))
                {
                    Directory.CreateDirectory(_persistentDataDirectory);
                    Debug.Log($"Created persistent data directory: {_persistentDataDirectory}");
                }

                Debug.Log($"Persistent data path: {_persistentDataDirectory}");
            }
            return _persistentDataDirectory;
        }
    }

    public static string DefaultMusicPath => Path.Combine(PersistentDataPath, "MusicHere");

    // Config and playlist paths
    public static string ConfigPath => Path.Combine(PersistentDataPath, "HasteCustomMusic_config.json");
    public static string PlaylistsPath => Path.Combine(PersistentDataPath, "HasteCustomMusic_playlists.json");

    public static void InitializePersistentDirectory()
    {
        // Ensure all directories exist
        if (!Directory.Exists(PersistentDataPath))
            Directory.CreateDirectory(PersistentDataPath);

        if (!Directory.Exists(DefaultMusicPath))
            Directory.CreateDirectory(DefaultMusicPath);

        Debug.Log("Persistent directories initialized");
    }


}