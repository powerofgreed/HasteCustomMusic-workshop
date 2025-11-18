using System;
using System.IO;
using System.Collections.Generic;
using UnityEngine;

public static class LandfallConfig
{
    public static string ConfigDirectory => WorkshopHelper._persistentDataDirectory;
    public static string ConfigPath => WorkshopHelper.ConfigPath;
    public static string PlaylistsPath => WorkshopHelper.PlaylistsPath;

    [Serializable]
    public class ConfigData
    {
        // Hotkeys
        public KeyCode ToggleUIKey = KeyCode.F2;
        public KeyCode NextTrackKey = KeyCode.F3;
        public bool GamepadHotkeysEnabled = true;

        // Playback
        public bool LockEnabled = true;
        public bool ForceLocalPlaylist = false;
        public string PlayOrder = "Sequential";

        // Loader
        public string LocalMusicPath = "";
        public string LoaderPriority = "BassFirst";
        public bool PreloadEntirePlaylist = false;
        public bool ScanSubfolders = false;

        // Debug
        public bool ShowDebug = false;

        // UI
        public bool PlaylistWindowVisible = true;
        public float PlaylistWindowHeight = 160f;
        public float UIScale = 1.0f;
    }

    [Serializable]
    public class PlaylistData
    {
        public List<string> HybridPlaylist = new List<string>();
        public List<string> StreamsPlaylist = new List<string>();
    }

    public static ConfigData CurrentConfig { get; set; } = new ConfigData();
    public static PlaylistData CurrentPlaylists { get; private set; } = new PlaylistData();

    private static bool _isInitialized = false;
    private static bool _defaultPlaylistsCreated = false;
    private static float _lastSaveTime = 0f;
    private static float _lastPlaylistSaveTime = 0f;

    private static readonly string[] DefaultStreams = new string[]
    {
        "https://c22.radioboss.fm/stream/144",
        "http://funkyunclefm.net:8000/fufm",
        "https://icecast.radiofrance.fr/fip-hifi.aac",
        "http://stream.animeradio.de/animeradio.mp3"
    };
    public static void Initialize()
    {
        if (_isInitialized) return;

        try
        {
            // Initialize persistent directory structure
            WorkshopHelper.InitializePersistentDirectory();

            LoadConfig();
            LoadPlaylists();
            PlaylistBridge.SyncToCustomMusicManager();

            _isInitialized = true;
            Debug.Log("LandfallConfig initialized successfully");
        }
        catch (Exception ex)
        {
            Debug.LogError($"Failed to initialize LandfallConfig: {ex}");
            // Continue with default values
            CurrentConfig = new ConfigData();
            CurrentPlaylists = new PlaylistData();
            _isInitialized = true;
        }
    }




    public static void LoadConfig()
    {
        try
        {
            if (File.Exists(ConfigPath))
            {
                string json = File.ReadAllText(ConfigPath);
                CurrentConfig = JsonUtility.FromJson<ConfigData>(json) ?? new ConfigData();
                Debug.Log("Config loaded from: " + ConfigPath);
            }
            else
            {
                // First-time setup: set default music path
                CurrentConfig.LocalMusicPath = WorkshopHelper.DefaultMusicPath;
                SaveConfig();
                Debug.Log("Created default config with music path: " + WorkshopHelper.DefaultMusicPath);
            }
        }
        catch (Exception ex)
        {
            Debug.LogError($"Error loading config: {ex}");
            CurrentConfig = new ConfigData();
            CurrentConfig.LocalMusicPath = WorkshopHelper.DefaultMusicPath;
        }
    }



    public static void LoadPlaylists()
    {
        try
        {
            if (File.Exists(PlaylistsPath))
            {
                string json = File.ReadAllText(PlaylistsPath);
                CurrentPlaylists = JsonUtility.FromJson<PlaylistData>(json) ?? new PlaylistData();
                Debug.Log($"Playlists loaded: {CurrentPlaylists.HybridPlaylist.Count} hybrid, {CurrentPlaylists.StreamsPlaylist.Count} streams");
                _defaultPlaylistsCreated = true;
            }
            else
            {
                // First-time setup: create with empty Hybrid and default Streams
                Debug.Log("First run: Creating default playlists file");

                // Ensure we start with clean lists
                CurrentPlaylists.HybridPlaylist.Clear();
                CurrentPlaylists.StreamsPlaylist.Clear();

                // Add default streams
                CurrentPlaylists.StreamsPlaylist.AddRange(DefaultStreams);

                // Save the new playlists file
                SavePlaylists();
                _defaultPlaylistsCreated = true;

                Debug.Log($"Created default playlists: {CurrentPlaylists.HybridPlaylist.Count} hybrid, {CurrentPlaylists.StreamsPlaylist.Count} streams");
            }
        }
        catch (Exception ex)
        {
            Debug.LogError($"Error loading playlists: {ex}");
            // Ensure we have valid defaults even on error
            CurrentPlaylists.HybridPlaylist.Clear();
            CurrentPlaylists.StreamsPlaylist.Clear();
            CurrentPlaylists.StreamsPlaylist.AddRange(DefaultStreams);
            _defaultPlaylistsCreated = true;
        }
    }
    public static void LoadHybridPlaylist()
    {
        try
        {
            if (File.Exists(PlaylistsPath))
            {
                string json = File.ReadAllText(PlaylistsPath);
                var playlists = JsonUtility.FromJson<PlaylistData>(json) ?? new PlaylistData();

                // Only update hybrid playlist, leave streams untouched
                CurrentPlaylists.HybridPlaylist.Clear();
                CurrentPlaylists.HybridPlaylist.AddRange(playlists.HybridPlaylist);

                Debug.Log($"Hybrid playlist loaded: {CurrentPlaylists.HybridPlaylist.Count} tracks");
            }
            else
            {
                Debug.Log("No playlists file found - starting with empty hybrid playlist");
                CurrentPlaylists.HybridPlaylist.Clear();
            }
        }
        catch (Exception ex)
        {
            Debug.LogError($"Error loading hybrid playlist: {ex}");
            CurrentPlaylists.HybridPlaylist.Clear();
        }
    }
    public static void LoadStreamsPlaylist()
    {
        try
        {
            if (File.Exists(PlaylistsPath))
            {
                string json = File.ReadAllText(PlaylistsPath);
                var playlists = JsonUtility.FromJson<PlaylistData>(json) ?? new PlaylistData();

                // Only update streams playlist, leave hybrid untouched
                CurrentPlaylists.StreamsPlaylist.Clear();
                CurrentPlaylists.StreamsPlaylist.AddRange(playlists.StreamsPlaylist);

                Debug.Log($"Streams playlist loaded: {CurrentPlaylists.StreamsPlaylist.Count} tracks");
            }
            else
            {
                Debug.Log("No playlists file found - starting with empty streams playlist");
                CurrentPlaylists.StreamsPlaylist.Clear();
            }
        }
        catch (Exception ex)
        {
            Debug.LogError($"Error loading streams playlist: {ex}");
            CurrentPlaylists.StreamsPlaylist.Clear();
        }
    }

    public static void SaveConfig()
    {

        try
        {
            string json = JsonUtility.ToJson(CurrentConfig, true);
            File.WriteAllText(ConfigPath, json);
            _lastSaveTime = Time.unscaledTime;

            if (CurrentConfig.ShowDebug)
                Debug.Log($"Config saved to: {ConfigPath}");
        }
        catch (Exception ex)
        {
            Debug.LogError($"Error saving config: {ex}");
        }
    }

    public static void SavePlaylists()
    {
        try
        {
            string json = JsonUtility.ToJson(CurrentPlaylists, true);
            File.WriteAllText(PlaylistsPath, json);
            _lastPlaylistSaveTime = Time.unscaledTime;

            if (CurrentConfig.ShowDebug)
                Debug.Log($"Playlists saved to: {PlaylistsPath}");
        }
        catch (Exception ex)
        {
            Debug.LogError($"Error saving playlists: {ex}");
        }
    }

    // Helper methods to mimic your existing LandfallConfig.CurrentConfig.properties
    public static MusicDisplayBehaviour.LoaderPriority CurrentLoaderPriority
    {
        get
        {
            try
            {
                return (MusicDisplayBehaviour.LoaderPriority)Enum.Parse(
                    typeof(MusicDisplayBehaviour.LoaderPriority),
                    CurrentConfig.LoaderPriority);
            }
            catch
            {
                return MusicDisplayBehaviour.LoaderPriority.BassFirst;
            }
        }
    }

    public static CustomMusicManager.PlayOrder CurrentPlayOrder
    {
        get
        {
            try
            {
                return (CustomMusicManager.PlayOrder)Enum.Parse(
                    typeof(CustomMusicManager.PlayOrder),
                    CurrentConfig.PlayOrder);
            }
            catch
            {
                return CustomMusicManager.PlayOrder.Sequential;
            }
        }
        set
        {
            CurrentConfig.PlayOrder = value.ToString();
        }
    }
    public static bool LoadHybridPlaylistOnly()
    {
        try
        {
            if (!File.Exists(PlaylistsPath))
            {
                Debug.LogWarning("No playlists file found - cannot load Hybrid playlist");
                return false;
            }

            string json = File.ReadAllText(PlaylistsPath);
            PlaylistData filePlaylists = JsonUtility.FromJson<PlaylistData>(json) ?? new PlaylistData();

            // Check if we have any tracks to load
            if (filePlaylists.HybridPlaylist == null || filePlaylists.HybridPlaylist.Count == 0)
            {
                Debug.Log("Hybrid playlist file is empty - no tracks to load");
                return false;
            }

            // Clear current and replace with loaded tracks
            CurrentPlaylists.HybridPlaylist = new List<string>(filePlaylists.HybridPlaylist);

            Debug.Log($"Hybrid playlist loaded: {CurrentPlaylists.HybridPlaylist.Count} tracks");
            return true;
        }
        catch (Exception ex)
        {
            Debug.LogError($"Error loading Hybrid playlist: {ex}");
            return false;
        }
    }

    public static bool LoadStreamsPlaylistOnly()
    {
        try
        {
            if (!File.Exists(PlaylistsPath))
            {
                Debug.LogWarning("No playlists file found - cannot load Streams playlist");
                return false;
            }

            string json = File.ReadAllText(PlaylistsPath);
            PlaylistData filePlaylists = JsonUtility.FromJson<PlaylistData>(json) ?? new PlaylistData();

            // Check if we have any streams to load
            if (filePlaylists.StreamsPlaylist == null || filePlaylists.StreamsPlaylist.Count == 0)
            {
                Debug.Log("Streams playlist file is empty - no streams to load");
                return false;
            }

            // Clear current and replace with loaded streams
            CurrentPlaylists.StreamsPlaylist = [.. filePlaylists.StreamsPlaylist];

            Debug.Log($"Streams playlist loaded: {CurrentPlaylists.StreamsPlaylist.Count} streams");
            return true;
        }
        catch (Exception ex)
        {
            Debug.LogError($"Error loading Streams playlist: {ex}");
            return false;
        }
    }
}