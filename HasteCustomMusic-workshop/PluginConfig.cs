using BepInEx.Configuration;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;

public static class PluginConfig
{
    // Existing config entries
    public static ConfigEntry<bool> LockEnabled;
    public static ConfigEntry<string> LocalMusicPath;
    public static ConfigEntry<bool> ProxyEnabled;
    public static ConfigEntry<string> ProxyPath;
    public static ConfigEntry<KeyboardShortcut> ToggleUIKey;
    public static ConfigEntry<KeyboardShortcut> NextTrackKey;
    public static ConfigEntry<MusicDisplayPlugin.LoaderPriority> LoaderPriority;
    public static ConfigEntry<string> PlayOrder;
    public static ConfigEntry<bool> ForceLocalPlaylist;
    public static ConfigEntry<bool> ShowDebug;

    // NEW CONFIG OPTIONS
    public static ConfigEntry<bool> PreloadEntirePlaylist;
    public static ConfigEntry<bool> EnableStreaming;
    public static ConfigEntry<bool> GamepadHotkeysEnabled;

    public static void Init(ConfigFile config)
    {
        // Existing configurations

        ToggleUIKey = config.Bind("-------Hotkeys-------", "ToggleUI",
            new KeyboardShortcut(KeyCode.F2),
            "Toggle UI visibility");

        NextTrackKey = config.Bind("-------Hotkeys-------", "NextTrack",
            new KeyboardShortcut(KeyCode.F3),
            "Play next track");

        GamepadHotkeysEnabled = config.Bind("-------Hotkeys-------", "GamepadHotkeys", true,
            "Enable XInput gamepad hotkeys (D-pad Up: Toggle UI, D-pad Right: Next Track)");
        LockEnabled = config.Bind("-------Playback-------", "Lock", true,

            "Lock to custom playlist mode");
        ForceLocalPlaylist = config.Bind("-------Playback-------", "ForceLocalPlaylist", false,
            "Always load Local directory path with chosen loader on startup and locks it");

        PlayOrder = config.Bind("-------Playback-------", "PlayOrder",
            CustomMusicManager.PlayOrder.Sequential.ToString(),
            new ConfigDescription("Initial playback mode: Sequential, Loop, or Random",
                new AcceptableValueList<string>(
                    System.Enum.GetNames(typeof(CustomMusicManager.PlayOrder)))));

        LocalMusicPath = config.Bind("-------Loader-------", "LocalPath:",
            Path.Combine(Application.dataPath, "CustomMusic"),
            "Local music directory path");

        LoaderPriority = config.Bind("-------Loader-------", "Priority",
            MusicDisplayPlugin.LoaderPriority.BassFirst,
            "Audio loader priority: BassFirst or OnlyUnity(low RAM usage,less formats)");

        PreloadEntirePlaylist = config.Bind("-------Loader-------", "PreloadEntirePlaylist", false,
            "Preload whole folder via chosen loader. Less CPU usage, but RAM heavy");

        ShowDebug = config.Bind("-------Debug-------", "Extra debug logging", false,
            new ConfigDescription("More robust logging for debugging", null,
                new ConfigurationManagerAttributes { Order = 10, IsAdvanced = true }));

        string configPath = Path.GetDirectoryName(config.ConfigFilePath);
        PlaylistConfig.Init(configPath);
        Debug.Log("PluginConfig initialized successfully");
        //ProxyEnabled = config.Bind("Streaming", "Enable Proxy", false, "Use Proxy connection");
        //ProxyPath = config.Bind("Streaming", "Proxy:", "", "Proxy connection path");

    }

    public static class PlaylistConfig
    {
        private static string _configDirectory;
        private static readonly string FavoritePlaylistFile = "favorite_playlist.txt";
        private static readonly string StreamsPlaylistFile = "streams_playlist.txt";

        // Default streams for first-time users
        private static readonly string[] DefaultStreams = new string[]
        {
        "https://c22.radioboss.fm/stream/144",
        "https://stream.radio.co/s8d0d5b6b9/listen",
        "https://icecast.radiofrance.fr/fip-hifi.aac",
        "https://stream.live.vc.bbcmedia.co.uk/bbc_radio_one"
        };

        public static List<string> HybridPlaylistPaths = new List<string>();
        public static List<string> StreamPlaylistPaths = new List<string>();

        public static void Init(string configPath)
        {
            _configDirectory = configPath;

            Debug.Log($"Initializing playlist config in: {_configDirectory}");

            // Ensure directory exists
            if (!Directory.Exists(_configDirectory))
            {
                Directory.CreateDirectory(_configDirectory);
            }

            // Load or create playlists
            LoadHybridPlaylistFromFile();
            LoadStreamsPlaylistFromFile();

            Debug.Log($"Initialized playlists: {HybridPlaylistPaths.Count} hybrid, {StreamPlaylistPaths.Count} streams");
        }

        public static void LoadHybridPlaylistFromFile()
        {
            try
            {
                string filePath = Path.Combine(_configDirectory, FavoritePlaylistFile);
                HybridPlaylistPaths.Clear();

                if (File.Exists(filePath))
                {
                    var lines = File.ReadAllLines(filePath)
                        .Where(line => !string.IsNullOrWhiteSpace(line))
                        .Where(line => !line.Trim().StartsWith("#"))
                        .Select(line => line.Trim())
                        .ToList();

                    HybridPlaylistPaths.AddRange(lines);
                    Debug.Log($"Loaded {HybridPlaylistPaths.Count} tracks from hybrid playlist file");
                }
                else
                {
                    Debug.Log("Hybrid playlist file not found - creating empty file");
                    var defaultContent = new[]
                    {
                "# Favorite Playlist - Add your Favorite tracks here",
                "# One track path per line",
                "# Lines starting with '#' are comments and will be ignored",
                ""
            };
                    File.WriteAllLines(filePath, defaultContent);
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"Error loading hybrid playlist: {ex}");
                HybridPlaylistPaths.Clear();
                // On corruption, do nothing - keep empty list
            }
        }

        public static void SaveHybridPlaylistToFile()
        {
            try
            {
                string filePath = Path.Combine(_configDirectory, FavoritePlaylistFile);

                // Write all tracks to file, one per line
                File.WriteAllLines(filePath, HybridPlaylistPaths);
                Debug.Log($"Saved {HybridPlaylistPaths.Count} tracks to hybrid playlist file: {filePath}");
            }
            catch (Exception ex)
            {
                Debug.LogError($"Error saving hybrid playlist: {ex}");
            }
        }

        public static void LoadStreamsPlaylistFromFile()
        {
            try
            {
                string filePath = Path.Combine(_configDirectory, StreamsPlaylistFile);
                StreamPlaylistPaths.Clear();

                if (File.Exists(filePath))
                {
                    var lines = File.ReadAllLines(filePath)
                        .Where(line => !string.IsNullOrWhiteSpace(line))
                        .Where(line => !line.Trim().StartsWith("#")) // Skip comment lines
                        .Select(line => line.Trim())
                        .ToList();

                    StreamPlaylistPaths.AddRange(lines);
                    Debug.Log($"Loaded {StreamPlaylistPaths.Count} streams from streams playlist file");
                }
                else
                {
                    Debug.Log("Streams playlist file not found, creating with defaults");
                    // Create file with default streams and comments
                    var defaultContent = new[]
                    {
                "# Streams Playlist - Add your radio streams here",
                "# One stream URL per line",
                "# Lines starting with '#' are comments and will be ignored",
                ""
            };
                    File.WriteAllLines(filePath, defaultContent);

                    // Only add non-comment lines to the actual playlist
                    StreamPlaylistPaths.AddRange(DefaultStreams);

                    Debug.Log($"Created default streams playlist with {DefaultStreams.Length} streams");
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"Error loading streams playlist: {ex}");
                StreamPlaylistPaths.Clear();
                // On corruption, do nothing - keep empty list
            }
        }
    }
}
internal sealed class ConfigurationManagerAttributes
{
    /// <summary>
    /// Should the setting be shown as a percentage (only use with value range settings).
    /// </summary>
    public bool? ShowRangeAsPercent;

    /// <summary>
    /// Custom setting editor (OnGUI code that replaces the default editor provided by ConfigurationManager).
    /// See below for a deeper explanation. Using a custom drawer will cause many of the other fields to do nothing.
    /// </summary>
    public System.Action<BepInEx.Configuration.ConfigEntryBase> CustomDrawer;

    /// <summary>
    /// Custom setting editor that allows polling keyboard input with the Input (or UnityInput) class.
    /// Use either CustomDrawer or CustomHotkeyDrawer, using both at the same time leads to undefined behaviour.
    /// </summary>
    public CustomHotkeyDrawerFunc CustomHotkeyDrawer;

    /// <summary>
    /// Custom setting draw action that allows polling keyboard input with the Input class.
    /// Note: Make sure to focus on your UI control when you are accepting input so user doesn't type in the search box or in another setting (best to do this on every frame).
    /// If you don't draw any selectable UI controls You can use `GUIUtility.keyboardControl = -1;` on every frame to make sure that nothing is selected.
    /// </summary>
    /// <example>
    /// CustomHotkeyDrawer = (ConfigEntryBase setting, ref bool isEditing) =>
    /// {
    ///     if (isEditing)
    ///     {
    ///         // Make sure nothing else is selected since we aren't focusing on a text box with GUI.FocusControl.
    ///         GUIUtility.keyboardControl = -1;
    ///                     
    ///         // Use Input.GetKeyDown and others here, remember to set isEditing to false after you're done!
    ///         // It's best to check Input.anyKeyDown and set isEditing to false immediately if it's true,
    ///         // so that the input doesn't have a chance to propagate to the game itself.
    /// 
    ///         if (GUILayout.Button("Stop"))
    ///             isEditing = false;
    ///     }
    ///     else
    ///     {
    ///         if (GUILayout.Button("Start"))
    ///             isEditing = true;
    ///     }
    /// 
    ///     // This will only be true when isEditing is true and you hold any key
    ///     GUILayout.Label("Any key pressed: " + Input.anyKey);
    /// }
    /// </example>
    /// <param name="setting">
    /// Setting currently being set (if available).
    /// </param>
    /// <param name="isCurrentlyAcceptingInput">
    /// Set this ref parameter to true when you want the current setting drawer to receive Input events.
    /// The value will persist after being set, use it to see if the current instance is being edited.
    /// Remember to set it to false after you are done!
    /// </param>
    public delegate void CustomHotkeyDrawerFunc(BepInEx.Configuration.ConfigEntryBase setting, ref bool isCurrentlyAcceptingInput);

    /// <summary>
    /// Show this setting in the settings screen at all? If false, don't show.
    /// </summary>
    public bool? Browsable;

    /// <summary>
    /// Category the setting is under. Null to be directly under the plugin.
    /// </summary>
    public string Category;

    /// <summary>
    /// If set, a "Default" button will be shown next to the setting to allow resetting to default.
    /// </summary>
    public object DefaultValue;

    /// <summary>
    /// Force the "Reset" button to not be displayed, even if a valid DefaultValue is available. 
    /// </summary>
    public bool? HideDefaultButton;

    /// <summary>
    /// Force the setting name to not be displayed. Should only be used with a <see cref="CustomDrawer"/> to get more space.
    /// Can be used together with <see cref="HideDefaultButton"/> to gain even more space.
    /// </summary>
    public bool? HideSettingName;

    /// <summary>
    /// Optional description shown when hovering over the setting.
    /// Not recommended, provide the description when creating the setting instead.
    /// </summary>
    public string Description;

    /// <summary>
    /// Name of the setting.
    /// </summary>
    public string DispName;

    /// <summary>
    /// Order of the setting on the settings list relative to other settings in a category.
    /// 0 by default, higher number is higher on the list.
    /// </summary>
    public int? Order;

    /// <summary>
    /// Only show the value, don't allow editing it.
    /// </summary>
    public bool? ReadOnly;

    /// <summary>
    /// If true, don't show the setting by default. User has to turn on showing advanced settings or search for it.
    /// </summary>
    public bool? IsAdvanced;

    /// <summary>
    /// Custom converter from setting type to string for the built-in editor textboxes.
    /// </summary>
    public System.Func<object, string> ObjToStr;

    /// <summary>
    /// Custom converter from string to setting type for the built-in editor textboxes.
    /// </summary>
    public System.Func<string, object> StrToObj;
}