using System;
using System.IO;
using UnityEngine;

[DefaultExecutionOrder(10000)]
public class LandfallSettingsWindow : MonoBehaviour
{
    private bool _showSettings = false;
    private Rect _windowRect = new Rect(100, 100, 600, 500);
    private Vector2 _scrollPosition = Vector2.zero;

    // Hotkey editing state
    private bool _editingToggleKey = false;
    private bool _editingNextKey = false;
    private float _lastHotkeyCheck = 0f;
    private bool StyleInitialized = false;

    private GUIStyle _headerStyle;
    private GUIStyle _sectionStyle;
    private GUIStyle _buttonStyle;
    private bool _batRecreateInProgress = false;
    private bool _batRecreateSuccess = false;
    private string _batRecreateStatus = string.Empty;
    //dynamic resolution
    private float _uiScale = 1.0f;
    private Matrix4x4 _originalMatrix;
    // Cursor state management
    private bool _wasCursorVisible;
    private CursorLockMode _previousCursorLockState;
    private bool _cursorStateForced = false;
    void Start()
    {
    }

    void Update()
    {
        if (!_showSettings && _cursorStateForced)
        {
            RestoreCursorState();
        }
        // Toggle settings window with F10 (configurable later)
        if (Input.GetKeyDown(KeyCode.F10))
        {
            ToggleVisibility();
        }
        //// Force cursor state every frame while window is open
        //if (_showSettings && !_cursorStateForced)
        //{
        //    ForceCursorState();
        //}

        // Handle hotkey capture
        CheckHotkeyCapture();
    }
    void LateUpdate()
    {
        if (_showSettings)
        {
            // Force cursor visible and unlocked every frame
            Cursor.visible = true;
            Cursor.lockState = CursorLockMode.None;
        }
    }

    void OnGUI()
    {
        CalculateUIScale();

        // Save original matrix and apply scaling
        _originalMatrix = GUI.matrix;
        GUI.matrix = Matrix4x4.Scale(new Vector3(_uiScale, _uiScale, 1f));

        if (!_showSettings) return;

        if (!StyleInitialized) InitializeStyles();
        if (!_cursorStateForced)
        {
            ForceCursorState();
        }
        try
        {
            _windowRect = GUI.Window(9999, _windowRect, DrawSettingsWindow, "HasteCustomMusic Settings<color=green>(F10)</color>");
        }
        finally
        {
            // Restore original matrix
            GUI.matrix = _originalMatrix;
        }

        // Prevent clicks from going through to the game
        if (Event.current.type == EventType.MouseDown && _windowRect.Contains(Event.current.mousePosition))
        {
            Event.current.Use();
        }
        if (_showSettings)
        {
            Cursor.visible = true;
            Cursor.lockState = CursorLockMode.None;
        }
    }
    void OnApplicationFocus(bool hasFocus)
    {
        if (hasFocus && _showSettings)
        {
            // Re-force cursor state when application regains focus
            ForceCursorState();
        }
    }
    void OnDestroy()
    {
        if (_cursorStateForced)
        {
            RestoreCursorState();
        }
    }
    private void ForceCursorState()
    {
        Cursor.visible = true;
        Cursor.lockState = CursorLockMode.None;
        _cursorStateForced = true;
    }

    private void RestoreCursorState()
    {
        Cursor.visible = _wasCursorVisible;
        Cursor.lockState = _previousCursorLockState;
        _cursorStateForced = false;

        if (LandfallConfig.CurrentConfig.ShowDebug) Debug.Log($"Restored cursor state - Visible: {_wasCursorVisible}, LockState: {_previousCursorLockState}");
    }
    
    public void ToggleVisibility()
    {
        _showSettings = !_showSettings;

        if (_showSettings)
        {
            // Save current cursor state
            _wasCursorVisible = Cursor.visible;
            _previousCursorLockState = Cursor.lockState;

            // Immediately force cursor state
            ForceCursorState();

            if (LandfallConfig.CurrentConfig.ShowDebug) Debug.Log($"Saved cursor state - Visible: {_wasCursorVisible}, LockState: {_previousCursorLockState}");
        }
        else
        {
            // Restore cursor state
            RestoreCursorState();
        }
    }
    private void CalculateUIScale()
    {
        float screenHeight = Screen.height;
        _uiScale = Mathf.Max(screenHeight / 1080f, 0.75f);

    }

    void DrawSettingsWindow(int id)
    {
        GUILayout.BeginVertical(GUILayout.ExpandWidth(false));
        _scrollPosition = GUILayout.BeginScrollView(_scrollPosition);

        DrawHotkeysSection();
        DrawPlaybackSection();
        DrawLoaderSection();
        DrawYoutubeSection();
        DrawMiniPlayerSection();
        DrawDebugSection();
        GUILayout.EndScrollView();

        // Bottom buttons
        GUILayout.BeginHorizontal();
        if (GUILayout.Button("Save & Close", _buttonStyle, GUILayout.Height(30)))
        {
            LandfallConfig.SaveConfig();
            _showSettings = false;
        }
        GUILayout.FlexibleSpace();
        if (GUILayout.Button("Reset to Defaults", _buttonStyle, GUILayout.Height(30)))
        {
            ResetToDefaults();
        }
        GUILayout.FlexibleSpace();
        if (GUILayout.Button("Close", _buttonStyle, GUILayout.Height(30)))
        {
            _showSettings = false;
        }
        GUILayout.EndHorizontal();

        GUILayout.EndVertical();

        GUI.DragWindow(new Rect(0, 0, _windowRect.width, 20));
    }

    void DrawHotkeysSection()
    {
        GUILayout.Label("HOTKEYS", _headerStyle);
        GUILayout.BeginVertical(GUI.skin.box);

        // Toggle UI Key
        GUILayout.BeginHorizontal();
        GUILayout.Label("Toggle UI:", GUILayout.Width(120));
        if (_editingToggleKey)
        {
            GUILayout.Label("Press any key FEW TIMES...", GUI.skin.textField);
            if (GUILayout.Button("Cancel", GUILayout.Width(60)))
            {
                _editingToggleKey = false;
            }
        }
        else
        {
            GUILayout.Label(LandfallConfig.CurrentConfig.ToggleUIKey.ToString(), GUI.skin.textField);
            if (GUILayout.Button("Change", GUILayout.Width(60)))
            {
                _editingToggleKey = true;
                _editingNextKey = false;
            }
        }
        GUILayout.EndHorizontal();

        // Next Track Key
        GUILayout.BeginHorizontal();
        GUILayout.Label("Next Track:", GUILayout.Width(120));
        if (_editingNextKey)
        {
            GUILayout.Label("Press any key...", GUI.skin.textField);
            if (GUILayout.Button("Cancel", GUILayout.Width(60)))
            {
                _editingNextKey = false;
            }
        }
        else
        {
            GUILayout.Label(LandfallConfig.CurrentConfig.NextTrackKey.ToString(), GUI.skin.textField);
            if (GUILayout.Button("Change", GUILayout.Width(60)))
            {
                _editingNextKey = true;
                _editingToggleKey = false;
            }
        }
        GUILayout.EndHorizontal();

        LandfallConfig.CurrentConfig.GamepadHotkeysEnabled = GUILayout.Toggle(
            LandfallConfig.CurrentConfig.GamepadHotkeysEnabled,
            "Enable Gamepad Hotkeys (D-pad Up: Toggle UI, D-pad Down: Next Track)");

        GUILayout.EndVertical();
        GUILayout.Space(10);
    }

    void DrawPlaybackSection()
    {
        GUILayout.Label("PLAYBACK", _headerStyle);
        GUILayout.BeginVertical(GUI.skin.box);

        LandfallConfig.CurrentConfig.LockEnabled = GUILayout.Toggle(
            LandfallConfig.CurrentConfig.LockEnabled,
            "Lock to Custom Playlist Mode");

        LandfallConfig.CurrentConfig.ForceLocalPlaylist = GUILayout.Toggle(
            LandfallConfig.CurrentConfig.ForceLocalPlaylist,
            "Force Load Local Playlist on Startup");

        // Play Order dropdown
        GUILayout.BeginHorizontal();
        GUILayout.Label("Play Order:", GUILayout.Width(120));
        string[] playOrders = { "Sequential", "Loop", "Random" };
        int currentIndex = Array.IndexOf(playOrders, LandfallConfig.CurrentConfig.PlayOrder);
        int newIndex = GUILayout.SelectionGrid(currentIndex, playOrders, 3);
        if (newIndex != currentIndex)
        {
            LandfallConfig.CurrentConfig.PlayOrder = playOrders[newIndex];
        }
        GUILayout.EndHorizontal();

        LandfallConfig.CurrentConfig.EachLevelNewNextTrack = GUILayout.Toggle(
            LandfallConfig.CurrentConfig.EachLevelNewNextTrack,
            "Play next track each time you enter a new level (might be buggy. DONT USE WITH RADIO)");

        GUILayout.EndVertical();
        GUILayout.Space(10);
    }

    void DrawLoaderSection()
    {
        GUILayout.Label("LOADER SETTINGS", _headerStyle);
        GUILayout.BeginVertical(GUI.skin.box);

        // Local Music Path
        GUILayout.Label("CustomMusic Path:", GUILayout.Height(20));
        GUILayout.BeginHorizontal();
        string newPath = GUILayout.TextField(LandfallConfig.CurrentConfig.LocalMusicPath, GUILayout.ExpandWidth(false), GUILayout.MaxWidth(450));
        if (newPath != LandfallConfig.CurrentConfig.LocalMusicPath)
        {
            LandfallConfig.CurrentConfig.LocalMusicPath = newPath;
        }
        if (GUILayout.Button("Open Folder"))
        {
            Application.OpenURL($"file://{LandfallConfig.CurrentConfig.LocalMusicPath}");
        }
        GUILayout.EndHorizontal();

        // Loader Priority
        GUILayout.BeginHorizontal();
        GUILayout.Label("Loader Priority:", GUILayout.Width(120));
        string[] priorities = { "BassFirst", "OnlyUnity" };
        int currentPriority = Array.IndexOf(priorities, LandfallConfig.CurrentConfig.LoaderPriority);
        int newPriority = GUILayout.SelectionGrid(currentPriority, priorities, 2);
        if (newPriority != currentPriority)
        {
            LandfallConfig.CurrentConfig.LoaderPriority = priorities[newPriority];
        }
        GUILayout.EndHorizontal();

        LandfallConfig.CurrentConfig.PreloadEntirePlaylist = GUILayout.Toggle(
            LandfallConfig.CurrentConfig.PreloadEntirePlaylist,
            "Preload Entire Playlist (More RAM, Less CPU). Use only if everything else doesnt work");

        LandfallConfig.CurrentConfig.ScanSubfolders = GUILayout.Toggle(
            LandfallConfig.CurrentConfig.ScanSubfolders,
            "Scan subfolders for audio");


        GUILayout.Space(10);
        LandfallConfig.CurrentConfig.PlaylistWindowVisible = GUILayout.Toggle(
    LandfallConfig.CurrentConfig.PlaylistWindowVisible,
    "Show Playlist Window by Default");
        GUILayout.EndVertical();
    }


    void DrawYoutubeSection()
    {
        GUILayout.Label("YOUTUBE DOWNLOADER", _headerStyle);
        GUILayout.BeginVertical(GUI.skin.box);

        LandfallConfig.CurrentConfig.YoutubeEmbedThumbnail = GUILayout.Toggle(
            LandfallConfig.CurrentConfig.YoutubeEmbedThumbnail,
            "thumbnail as an image cover. (Takes longer for download)");

        LandfallConfig.CurrentConfig.YoutubeUseCookies = GUILayout.Toggle(
            LandfallConfig.CurrentConfig.YoutubeUseCookies,
            "add --cookies cookies.txt");

        GUILayout.Label("Generated flags:", GUILayout.Height(18));
        GUILayout.TextField(GetYoutubeFlags(), GUILayout.ExpandWidth(false), GUILayout.MaxWidth(450));

        GUILayout.Space(5);
        GUILayout.BeginHorizontal();
        GUI.enabled = !_batRecreateInProgress;
        var recreateStyle = new GUIStyle(_buttonStyle);
        if (!string.IsNullOrEmpty(_batRecreateStatus))
        {
            recreateStyle.normal.textColor = _batRecreateSuccess ? Color.green : Color.red;
        }
        if (GUILayout.Button("Recreate .bat files", recreateStyle, GUILayout.Height(30)))
        {
            RecreateBatFiles();
        }
        GUI.enabled = true;

        if (GUILayout.Button("Open Folder", _buttonStyle, GUILayout.Height(30)))
        {
            Application.OpenURL($"file://{LandfallConfig.ConfigDirectory}");
        }
        GUILayout.EndHorizontal();

        if (!string.IsNullOrEmpty(_batRecreateStatus))
        {
            GUILayout.Label(_batRecreateStatus, GUI.skin.label);
        }

        GUILayout.BeginHorizontal();
        GUILayout.FlexibleSpace();
        GUILayout.Label("READ WHAT IT DOES ON WORKSHOP PAGE", GUILayout.Height(18));
        GUILayout.FlexibleSpace();
        GUILayout.EndHorizontal();

        GUILayout.EndVertical();
        GUILayout.Space(10);
    }

    private string GetYoutubeFlags()
    {
        string flags = string.Empty;
        if (LandfallConfig.CurrentConfig.YoutubeEmbedThumbnail)
            flags += "--embed-thumbnail ";
        if (LandfallConfig.CurrentConfig.YoutubeUseCookies)
            flags += "--cookies cookies.txt ";
        return flags.Trim();
    }

    private void RecreateBatFiles()
    {
        _batRecreateInProgress = true;
        _batRecreateStatus = string.Empty;
        _batRecreateSuccess = false;

        string message;
        bool result = YouTubeBatchGenerator.RecreateBatFiles(out message);
        _batRecreateSuccess = result;
        _batRecreateStatus = result ? "Batch files recreated successfully." : message;
        _batRecreateInProgress = false;
    }

    void DrawMiniPlayerSection()
    {
        GUILayout.Label("MINI PLAYER", _headerStyle);

        // Master enable toggle
        bool oldEnabled = LandfallConfig.CurrentConfig.MiniPlayerEnabled;
        LandfallConfig.CurrentConfig.MiniPlayerEnabled = GUILayout.Toggle(
            LandfallConfig.CurrentConfig.MiniPlayerEnabled,
            "Enable MiniPlayer");

        // If disabled, stop here
        if (!LandfallConfig.CurrentConfig.MiniPlayerEnabled)
        {
            GUILayout.Space(10);
            return;
        }

        GUILayout.BeginVertical(GUI.skin.box);

        GUILayout.BeginVertical(GUI.skin.box);

        GUILayout.BeginVertical(GUI.skin.box);
        GUILayout.Label("Position:");
        GUILayout.BeginHorizontal();
        GUILayout.Label("X:", GUILayout.Width(25));
        LandfallConfig.CurrentConfig.MiniPlayerPositionX = GUILayout.HorizontalSlider(
            LandfallConfig.CurrentConfig.MiniPlayerPositionX, 0f, 1920f);
        GUILayout.Label("Y:", GUILayout.Width(25));
        LandfallConfig.CurrentConfig.MiniPlayerPositionY = GUILayout.HorizontalSlider(
            LandfallConfig.CurrentConfig.MiniPlayerPositionY, 0f, 1080f);
        GUILayout.EndHorizontal();
        GUILayout.EndVertical();

        GUILayout.BeginVertical(GUI.skin.box);
        GUILayout.Label("Scale:");
        LandfallConfig.CurrentConfig.MiniPlayerScale = GUILayout.HorizontalSlider(
            LandfallConfig.CurrentConfig.MiniPlayerScale, 0.5f, 2f);
        GUILayout.EndVertical();

        GUILayout.BeginVertical(GUI.skin.box);
        GUILayout.Label("Opacity:");
        LandfallConfig.CurrentConfig.MiniPlayerOpacity = GUILayout.HorizontalSlider(
            LandfallConfig.CurrentConfig.MiniPlayerOpacity, 0f, 1f);
        GUILayout.EndVertical();
        if (GUILayout.Button("Reset Position, Scale & Opacity", GUILayout.Height(25)))
        {
            LandfallConfig.CurrentConfig.MiniPlayerPositionX = 20f;
            LandfallConfig.CurrentConfig.MiniPlayerPositionY = 350f;
            LandfallConfig.CurrentConfig.MiniPlayerScale = 1f;
            LandfallConfig.CurrentConfig.MiniPlayerOpacity = 1f;
        }
        GUILayout.EndVertical();

        // ------------------------------------------------

        // Cover image as color scheme
        GUILayout.BeginVertical(GUI.skin.box);

        LandfallConfig.CurrentConfig.MiniPlayerPopupEnabled = GUILayout.Toggle(
            LandfallConfig.CurrentConfig.MiniPlayerPopupEnabled,
            "Enable Popup Animation (slide in on track change)");

        LandfallConfig.CurrentConfig.MiniPlayerUseCoverColor = GUILayout.Toggle(
            LandfallConfig.CurrentConfig.MiniPlayerUseCoverColor,
            "Use Cover Image as Color Scheme");

        // Color scheme selection (radio‑like)
        GUILayout.Label("Color Scheme:");
        string[] schemes = { "Default", "Rainbow", "Custom" };
        int oldScheme = LandfallConfig.CurrentConfig.MiniPlayerColorScheme;
        int newScheme = GUILayout.SelectionGrid(oldScheme, schemes, 3);
        if (newScheme != oldScheme)
        {
            LandfallConfig.CurrentConfig.MiniPlayerColorScheme = newScheme;
        }

        // If Custom scheme selected, show RGBA sliders
        if (LandfallConfig.CurrentConfig.MiniPlayerColorScheme == 2)
        {
            DrawColorSlider("Background", ref LandfallConfig.CurrentConfig.MiniPlayerBackgroundColor);
            DrawColorSlider("Slider", ref LandfallConfig.CurrentConfig.MiniPlayerSliderColor);
            DrawColorSlider("Font", ref LandfallConfig.CurrentConfig.MiniPlayerFontColor);
        }
        GUILayout.EndVertical();

        GUILayout.BeginVertical(GUI.skin.box);
        GUILayout.BeginHorizontal();
        LandfallConfig.CurrentConfig.UseIconAsDefaultCover = GUILayout.Toggle(
            LandfallConfig.CurrentConfig.UseIconAsDefaultCover,
            "Enable ICON.png as default cover image");
        if (GUILayout.Button("Open Folder", GUILayout.Width(100)))
        {
            Application.OpenURL("file://" + LandfallConfig.ConfigDirectory);
        }
        GUILayout.EndHorizontal();

        GUILayout.BeginHorizontal();
        GUILayout.FlexibleSpace();
        GUILayout.Label("Feel free to replace ICON.png with own .png image. Need restart.");
        GUILayout.FlexibleSpace();
        GUILayout.EndHorizontal();
        GUILayout.EndVertical(); 

        GUILayout.EndVertical();
        GUILayout.Space(10);
    }

    // Helper to draw RGBA sliders for a Color field
    private void DrawColorSlider(string label, ref Color color)
    {
        GUILayout.BeginHorizontal();
        GUILayout.Label(label + " RGBA:", GUILayout.Width(100));

        float r = color.r, g = color.g, b = color.b, a = color.a;

        r = GUILayout.HorizontalSlider(r, 0f, 1f, GUILayout.Width(40));
        g = GUILayout.HorizontalSlider(g, 0f, 1f, GUILayout.Width(40));
        b = GUILayout.HorizontalSlider(b, 0f, 1f, GUILayout.Width(40));
        a = GUILayout.HorizontalSlider(a, 0f, 1f, GUILayout.Width(40));

        color = new Color(r, g, b, a);
        GUILayout.EndHorizontal();
    }

    void DrawDebugSection()
    {
        GUILayout.Label("DEBUG", _headerStyle);
        GUILayout.BeginVertical(GUI.skin.box);

        LandfallConfig.CurrentConfig.ShowDebug = GUILayout.Toggle(
            LandfallConfig.CurrentConfig.ShowDebug,
            "Enable Debug Logging. DO NOT ENABLE IT FOR FUN");

        // Config file info
        GUILayout.Label("Config Location:", GUILayout.Height(20));
        GUILayout.BeginHorizontal();
        GUILayout.Label(LandfallConfig.ConfigPath, GUI.skin.textField, GUILayout.ExpandWidth(false), GUILayout.MaxWidth(450));

        if (GUILayout.Button("Open Folder"))
        {
            Application.OpenURL($"file://{LandfallConfig.ConfigDirectory}");
        }
        GUILayout.EndHorizontal();


        GUILayout.Space(10);
        GUILayout.Label("PLAYLIST MANAGEMENT:", _headerStyle);

        GUILayout.Label($"Hybrid Playlist: {LandfallConfig.CurrentPlaylists.HybridPlaylist.Count} tracks");
        GUILayout.Label($"Streams Playlist: {LandfallConfig.CurrentPlaylists.StreamsPlaylist.Count} streams");

        GUILayout.BeginHorizontal();
        if (GUILayout.Button("Backup Playlists"))
        {
            BackupPlaylists();
        }
        if (GUILayout.Button("Reset Streams Playlist"))
        {
            RestoreStreamsPlaylist();
        }
        GUILayout.EndHorizontal();

        GUILayout.EndVertical();
    }

    void CheckHotkeyCapture()
    {
        if (!_editingToggleKey && !_editingNextKey) return;

        // Only check periodically to avoid capturing the same key multiple times
        if (Time.unscaledTime - _lastHotkeyCheck < 0.1f) return;
        _lastHotkeyCheck = Time.unscaledTime;

        foreach (KeyCode keyCode in Enum.GetValues(typeof(KeyCode)))
        {
            if (Input.GetKeyDown(keyCode))
            {
                // Ignore modifier keys and mouse buttons
                if (IsValidHotkey(keyCode))
                {
                    if (_editingToggleKey)
                    {
                        LandfallConfig.CurrentConfig.ToggleUIKey = keyCode;
                        _editingToggleKey = false;
                    }
                    else if (_editingNextKey)
                    {
                        LandfallConfig.CurrentConfig.NextTrackKey = keyCode;
                        _editingNextKey = false;
                    }
                    break;
                }
            }
        }
    }

    bool IsValidHotkey(KeyCode keyCode)
    {
        // Filter out invalid hotkeys
        switch (keyCode)
        {
            case KeyCode.Mouse0:
            case KeyCode.Mouse1:
            case KeyCode.Mouse2:
            case KeyCode.Mouse3:
            case KeyCode.Mouse4:
            case KeyCode.Mouse5:
            case KeyCode.Mouse6:
            case KeyCode.LeftShift:
            case KeyCode.RightShift:
            case KeyCode.LeftControl:
            case KeyCode.RightControl:
            case KeyCode.LeftAlt:
            case KeyCode.RightAlt:
            case KeyCode.LeftCommand:
            case KeyCode.RightCommand:
                return false;
            default:
                return true;
        }
    }

    void InitializeStyles()
    {
        if (_headerStyle == null)
        {
            _headerStyle = new GUIStyle(GUI.skin.label)
            {
                fontStyle = FontStyle.Bold,
                fontSize = 14,
                normal = { textColor = Color.yellow }
            };

            _sectionStyle = new GUIStyle(GUI.skin.box)
            {
                padding = new RectOffset(10, 10, 10, 10)
            };

            _buttonStyle = new GUIStyle(GUI.skin.button)
            {
                padding = new RectOffset(10, 10, 5, 5)
            };
        }
        StyleInitialized = true;
    }
    void ResetToDefaults()
    {
        // Create a new config but preserve the CustomMusic directory path
        var newConfig = new LandfallConfig.ConfigData();
        newConfig.LocalMusicPath = WorkshopHelper.DefaultMusicPath; // Force CustomMusic directory

        LandfallConfig.CurrentConfig = newConfig;
        LandfallConfig.SaveConfig();

        Debug.Log($"Settings reset to defaults. Music path: {LandfallConfig.CurrentConfig.LocalMusicPath}");
    }

    void BackupPlaylists()
    {
        string backupPath = Path.Combine(Path.GetDirectoryName(LandfallConfig.ConfigPath), $"playlists_backup_{DateTime.Now:yyyyMMdd_HHmmss}.json");
        string json = JsonUtility.ToJson(LandfallConfig.CurrentPlaylists, true);
        File.WriteAllText(backupPath, json);
        Debug.Log($"Playlists backed up to: {backupPath}");
    }

    void RestoreStreamsPlaylist()
    {
        LandfallConfig.CurrentPlaylists.StreamsPlaylist.Clear();
        LandfallConfig.CurrentPlaylists.StreamsPlaylist.AddRange(LandfallConfig.DefaultStreams);
        // Read the existing playlists file
        string json = File.ReadAllText(LandfallConfig.PlaylistsPath);

        // Parse the existing playlists
        var existingPlaylists = JsonUtility.FromJson<LandfallConfig.PlaylistData>(json);

        // Keep the existing Hybrid playlist as-is
        // Only replace the Streams playlist with defaults
        existingPlaylists.StreamsPlaylist = new List<string>(LandfallConfig.DefaultStreams);

        // Save back to file
        string updatedJson = JsonUtility.ToJson(existingPlaylists, true);
        File.WriteAllText(LandfallConfig.PlaylistsPath, updatedJson);

        // Update in-memory config to match
        LandfallConfig.CurrentPlaylists.StreamsPlaylist = new List<string>(LandfallConfig.DefaultStreams);

        Debug.Log("Streams Playlist restored to default and saved!");

        PlaylistBridge.SyncStreamsPlaylistToCustomMusicManager();
    }
}