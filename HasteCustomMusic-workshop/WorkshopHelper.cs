using System;
using System.IO;
using System.Reflection;
using UnityEngine;

public static class WorkshopHelper
{
    private static string _modDirectory;
    private static string _customMusicDirectory;

    public static string ModDirectory
    {
        get
        {
            if (_modDirectory == null)
            {
                // Method 1: Try assembly location first (for workshop/mod structure)
                try
                {
                    var assemblyLocation = Assembly.GetExecutingAssembly().Location;
                    if (!string.IsNullOrEmpty(assemblyLocation))
                    {
                        _modDirectory = Path.GetDirectoryName(assemblyLocation);
                        Debug.Log($"Mod directory from assembly: {_modDirectory}");

                        // If this looks like a workshop/mod path, use it
                        if (_modDirectory.Contains("workshop") || _modDirectory.Contains("Workshop") || _modDirectory.Contains("plugins"))
                        {
                            return _modDirectory;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"Could not get assembly location: {ex}");
                }

                // Method 2: Use persistent data path (fallback for development)
                _modDirectory = Path.Combine(Application.persistentDataPath, "HasteCustomMusic");

                Debug.Log($"Using persistent data path: {_modDirectory}");
            }

            // Ensure the mod directory exists
            if (!Directory.Exists(_modDirectory))
            {
                Directory.CreateDirectory(_modDirectory);
            }

            return _modDirectory;
        }
    }

    public static string CustomMusicDirectory
    {
        get
        {
            if (_customMusicDirectory == null)
            {
                // CustomMusic folder should be in the same directory as Managed/
                _customMusicDirectory = Path.Combine(ModDirectory, "CustomMusic");

                // Ensure the CustomMusic directory exists
                if (!Directory.Exists(_customMusicDirectory))
                {
                    try
                    {
                        Directory.CreateDirectory(_customMusicDirectory);
                        Debug.Log($"Created CustomMusic directory: {_customMusicDirectory}");
                    }
                    catch (Exception ex)
                    {
                        Debug.LogError($"Failed to create CustomMusic directory: {ex}");
                        // Fallback to mod directory if creation fails
                        _customMusicDirectory = ModDirectory;
                    }
                }
            }
            return _customMusicDirectory;
        }
    }

    
}