// SPDX-License-Identifier: LGPL-3.0-or-later
// Copyright (C) 2025 PoWeRofGreeD
//
// This file is part of the HasteCustomMusic plugin.
//
// HasteCustomMusic is free software: you can redistribute it and/or modify
// it under the terms of the GNU Lesser General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// HasteCustomMusic is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU Lesser General Public License for more details.
//
// You should have received a copy of the GNU Lesser General Public License
// along with this program.  If not, see <https://www.gnu.org/licenses/>.
using HarmonyLib;
using HasteModPlaylist;
using Landfall.Haste.Music;
using Landfall.Modding;
using System.Collections;
using UnityEngine;
using UnityEngine.InputSystem;
using static CustomMusicManager;
using static StreamingClip;
using Color = UnityEngine.Color;
using Input = UnityEngine.Input;
using Object = UnityEngine.Object;

[LandfallPlugin]
public static class HasteCustomMusicLandfall
{
    private static MusicDisplayBehaviour _behaviour;
    private static LandfallSettingsWindow _settingsWindow;
    private static Harmony _harmony;
    private static bool _isInitialized = false;

    static HasteCustomMusicLandfall()
    {
        Debug.Log("HasteCustomMusic: Loading Landfall/Steam Workshop version...");

        // Initialize BASS FIRST using your centralized loader
        if (!ManagedBassLoader.Initialize())
        {
            Debug.LogError("BASS initialization failed! Audio features will be disabled.");
            // Don't return - let other features work without audio
        }

        // Initialize config system
        LandfallConfig.Initialize();

        // Initialize Harmony
        _harmony = new Harmony("com.PoG.HasteCustomMusic");
        _harmony.PatchAll(typeof(CustomMusicManager)); 

        // Create main behaviour
        var gameObject = new GameObject("HasteCustomMusicController");
        Object.DontDestroyOnLoad(gameObject);
        _behaviour = gameObject.AddComponent<MusicDisplayBehaviour>();

        // Create settings window
        var settingsObject = new GameObject("HasteCustomMusicSettings");
        Object.DontDestroyOnLoad(settingsObject);
        _settingsWindow = settingsObject.AddComponent<LandfallSettingsWindow>();

        Debug.Log("HasteCustomMusic: Landfall version initialized successfully!");
    }
}
public class MusicDisplayBehaviour : MonoBehaviour
{

    private bool _showGUI = true;
    private bool StyleInitialized = false;

    private Rect _windowRect = new Rect(20, 20, 400, 300);
    private Texture2D _backgroundTexture;
    private string _localMusicPath;
    private string _customHybridPath = "";
    private string _customStreamPath = "";
    private MusicPlaylist _lastKnownPlaylist;
    private int _selectedTrackIndex = -1;
    private int _lastClickedTrack = -1;
    private float _singleClickExpireAt = 0f;
    private const float DoubleClickThreshold = 0.6f;
    private Rect _playlistWindowRect;
    private bool _playlistWindowVisible = true;
    private bool _isResizing = false;
    private Rect _resizeHandle = new Rect(0, 0, 100, 5);
    private Vector2 _resizeStartMouse;
    private float _resizeStartHeight;
    private static float _currentVolume = 1.0f;
    private float _lastAutoAdvanceAt = 0f;
    private const float AutoAdvanceCooldown = 1f; // seconds; prevents multiple advances in quick succession
    private float _lastSeenStreamTotal = 0f;

    //dynamic resolution
    private float _uiScale = 1.0f;
    private Matrix4x4 _originalMatrix;




    //Removal Cooldown Protection
    private float _lastRemovalTime = 0f;
    private const float RemovalCooldown = 0.15f;

    // Timer state variables
    private string _activeTimerType = ""; // "save" or "load"
    private float _timerStartTime = 0f;
    private const float TIMER_DURATION = 3f;
    // Animation state


    public enum AudioSourceTab { Local, Hybrid, Streams }
    private AudioSourceTab _activeTab = AudioSourceTab.Local;
    private AudioSourceTab ActiveTab
    {
        get => _activeTab;
        set
        {
            if (_activeTab != value)
            {
                _activeTab = value;

                // Only change viewing playlist if we're NOT viewing default
                // This allows tab switching while keeping default view
                if (_viewingPlaylistType != PlaylistType.Default)
                {
                    _viewingPlaylistType = _activeTab switch
                    {
                        AudioSourceTab.Local => PlaylistType.Local,
                        AudioSourceTab.Hybrid => PlaylistType.Hybrid,
                        AudioSourceTab.Streams => PlaylistType.Streams,
                        _ => PlaylistType.Local
                    };
                }

                _selectedTrackIndex = -1;
                _lastClickedTrack = -1;
                if(LandfallConfig.CurrentConfig.ShowDebug) Debug.Log($"Active tab: {_activeTab}, Viewing: {_viewingPlaylistType}, Playing: {CustomMusicManager.CurrentPlaybackPlaylistType}");
            }
        }
    }

    private List<string> _savedUrls = new List<string>();
    private PlaylistType _playingPlaylistType = PlaylistType.Default;
    private PlaylistType _viewingPlaylistType = PlaylistType.Default;
    private Dictionary<AudioSourceTab, PlaylistType> _tabToPlaylistMap = new Dictionary<AudioSourceTab, PlaylistType>
    {
        { AudioSourceTab.Local, PlaylistType.Local },
        { AudioSourceTab.Hybrid, PlaylistType.Hybrid },
        { AudioSourceTab.Streams, PlaylistType.Streams }
    };
    private Dictionary<PlaylistType, Vector2> _playlistScrollPositions = new Dictionary<PlaylistType, Vector2>
    {
        { PlaylistType.Default, Vector2.zero },
        { PlaylistType.Local, Vector2.zero },
        { PlaylistType.Hybrid, Vector2.zero },
        { PlaylistType.Streams, Vector2.zero }
    };

    public static float GetCurrentMusicVolume() => _currentVolume;
    private bool _forcePlaylistPending = false;
    private GUIStyle _labelStyle;
    private GUIStyle _labelClipName;
    private GUIStyle _buttonStyle20;
    private GUIStyle _buttonStyle16;
    private GUIStyle _toggleStyle;
    private GUIStyle _sliderStyle;
    private GUIStyle _thumbStyle;
    private GUIStyle _cachedNameStyle;
    private GUIStyle _cachedTimeStyle;
    private GUIStyle _cachedRightAlignedStyle;


    public enum LoaderPriority { BassFirst, OnlyUnity }

    // Property that reads from config on every access
    public static LoaderPriority CurrentLoaderPriority
    {
        get => LandfallConfig.CurrentLoaderPriority;
    }
    private Vector2 GetScroll(PlaylistType type)
    {
        if (_playlistScrollPositions.TryGetValue(type, out var v)) return v;
        _playlistScrollPositions[type] = Vector2.zero;
        return Vector2.zero;
    }

    private void SetScroll(PlaylistType type, Vector2 pos)
    {
        _playlistScrollPositions[type] = pos;
    }

    void Awake()
    {
        _backgroundTexture = new Texture2D(1, 1);
        _backgroundTexture.SetPixel(0, 0, new Color(0.1f, 0.1f, 0.85f, 0.7f));
        _backgroundTexture.Apply();



        Harmony.CreateAndPatchAll(typeof(CustomMusicManager));

        // Validate path before using
        _localMusicPath = GetValidatedMusicPath();
    }

    void Start()
    {
        CalculateUIScale();
        _localMusicPath = LandfallConfig.CurrentConfig.LocalMusicPath;

        CustomMusicManager.OnLoadingStateChanged += OnLoadingStateChanged;
        CustomMusicManager.OnLoadProgress += OnLoadProgress;
        StreamingClip.TryInitBassOnce();

        // Handle forced custom playlist from new config
        if (LandfallConfig.CurrentConfig.ForceLocalPlaylist)
        {
            _localMusicPath = GetValidatedMusicPath();
            StartCoroutine(LoadAndForceLocalPlaylist());
        }

        StreamingClip.OnTitleChanged += HandleStreamingTitleChanged;
        Debug.Log($"Audio Loader Priority: {LandfallConfig.CurrentLoaderPriority}");

        var volumeController = MusicVolumeController.Instance;
        _currentVolume = volumeController.Volume;
    }



    void OnGUI()
    {
        CalculateUIScale();

        // Save original matrix and apply scaling
        _originalMatrix = GUI.matrix;
        GUI.matrix = Matrix4x4.Scale(new Vector3(_uiScale, _uiScale, 1f));

        if (_forcePlaylistPending) return;
        if (!_showGUI || MusicPlayer.Instance == null) return;
        if (!StyleInitialized) InitStyles();

        try
        {
            // Use original window rects 
            _windowRect = GUI.Window(0, _windowRect, DrawMusicWindow, "˚✩*‧₊༺ Music Player " + $"({LandfallConfig.CurrentConfig.ToggleUIKey}) ༻₊‧*✩˚");

            // Playlist window below main window
            if (_playlistWindowVisible)
            {
                // Only set initial position once
                if (_playlistWindowRect.width == 0)
                {
                    _playlistWindowRect = new Rect(
                          _windowRect.x,
                          _windowRect.y + _windowRect.height + 5,
                          _windowRect.width,
                          160
                      );
                }
                else
                {
                    // Preserve custom height but update position relative to main window
                    _playlistWindowRect.x = _windowRect.x;
                    _playlistWindowRect.y = _windowRect.y + _windowRect.height;
                    _playlistWindowRect.width = _windowRect.width;
                }
                _playlistWindowRect = GUI.Window(1, _playlistWindowRect, DrawPlaylistWindow, "⋆⋆✮♪♫ Playlist ♫♪✮⋆⋆");
            }
        }
        catch (System.Exception e)
        {
            Debug.LogError($"GUI Error: {e}");
            _showGUI = false;
        }
        finally
        {
            // Restore original matrix
            GUI.matrix = _originalMatrix;
        }

        // Handle resizing outside of window drawing (use original coordinates)
        HandleResizing();
    }
    private void HandleStreamingTitleChanged(string newTitle)
    {
        // Force immediate repaint of GUI so the changed title appears without delay
        GUI.changed = true;
        // Optionally log for debug
        Debug.Log($"[MusicDisplayPlugin] Streaming title updated -> {newTitle}");
    }
    private string GetAuthoritativeTrackName(string fallback)
    {
        // If we have a StreamingClip instance and it has a stored track title, prefer it
        try
        {
            if (StreamingClip.Instance != null)
            {
                var s = StreamingClip.Instance.LastKnownTitle;
                if (!string.IsNullOrEmpty(s)) return s;
            }
        }
        catch { /* ignore */ }

        // Fallback to Unity audiosource / provided fallback
        return fallback;
    }

    void OnDestroy()
    {
        CustomMusicManager.OnLoadingStateChanged -= OnLoadingStateChanged;
        CustomMusicManager.OnLoadProgress -= OnLoadProgress;
        StreamingClip.OnTitleChanged -= HandleStreamingTitleChanged;
    }

    private void OnLoadingStateChanged(bool isLoading)
    {
        // Force UI repaint when loading state changes
        GUI.changed = true;
    }

    private void OnLoadProgress(float progress)
    {
        // Force UI repaint when progress updates
        GUI.changed = true;
    }

    private void HandleResizing()
    {
        // Convert resize handle to screen coordinates using our scale
        Rect absoluteResizeHandle = new Rect(
            _playlistWindowRect.x * _uiScale + _resizeHandle.x * _uiScale,
            _playlistWindowRect.y * _uiScale + _resizeHandle.y * _uiScale,
            _resizeHandle.width * _uiScale,
            _resizeHandle.height * _uiScale
        );

        // Convert mouse position to screen coordinates (GUI.matrix affects Event.current.mousePosition)
        Vector2 mousePosition = new Vector2(Input.mousePosition.x, Screen.height - Input.mousePosition.y);

        if (Event.current.type == EventType.MouseDown && absoluteResizeHandle.Contains(mousePosition))
        {
            _isResizing = true;
            _resizeStartMouse = mousePosition;
            _resizeStartHeight = _playlistWindowRect.height;
            Event.current.Use(); // Mark event as handled
        }

        if (_isResizing)
        {
            // Get current mouse position in screen coordinates
            Vector2 currentMousePos = new Vector2(Input.mousePosition.x, Screen.height - Input.mousePosition.y);

            float heightDelta = (currentMousePos.y - _resizeStartMouse.y) / _uiScale;
            float newHeight = Mathf.Clamp(
                _resizeStartHeight + heightDelta,
                160, // Minimum height
                700  // Maximum height
            );

            // Only update if height actually changed
            if (Math.Abs(newHeight - _playlistWindowRect.height) > 0.1f)
            {
                _playlistWindowRect.height = newHeight;
            }

            // End resizing on mouse up
            if (Event.current.type == EventType.MouseUp)
            {
                _isResizing = false;
            }

            // Repaint GUI to show changes immediately
            GUI.changed = true;
        }
    }

    private void InitStyles()
    {
        _labelStyle = new GUIStyle(GUI.skin.box)
        {
            border = GUI.skin.label.border,
            normal = GUI.skin.label.normal,
            alignment = TextAnchor.MiddleLeft,
            wordWrap = false
        };

        _labelClipName = new GUIStyle(GUI.skin.box)
        {
            border = GUI.skin.label.border,
            normal = GUI.skin.label.normal,
            alignment = GUI.skin.label.alignment,
            wordWrap = false,
            clipping = TextClipping.Overflow
        };

        _buttonStyle20 = new GUIStyle(GUI.skin.button)
        {
            fontSize = 20,
            fontStyle = FontStyle.Normal,
            alignment = TextAnchor.MiddleCenter
        };

        _buttonStyle16 = new GUIStyle(GUI.skin.button)
        {
            fontSize = 16,
            fontStyle = FontStyle.Normal,
            alignment = TextAnchor.MiddleCenter
        };

        _toggleStyle = new GUIStyle(GUI.skin.toggle)
        {
            fontSize = 14,
            fontStyle = FontStyle.Normal,
            alignment = TextAnchor.LowerCenter
        };
        _labelStyle = new GUIStyle(GUI.skin.box)
        {
            border = GUI.skin.label.border,
            normal = GUI.skin.label.normal,
            alignment = TextAnchor.MiddleLeft,
            wordWrap = false
        };

        _labelClipName = new GUIStyle(GUI.skin.box)
        {
            border = GUI.skin.label.border,
            normal = GUI.skin.label.normal,
            alignment = GUI.skin.label.alignment,
            wordWrap = false,
            clipping = TextClipping.Overflow
        };
        _cachedNameStyle = new GUIStyle(_labelStyle)
        {
            clipping = TextClipping.Overflow
        };

        _cachedTimeStyle = new GUIStyle(_labelStyle)
        {
            alignment = TextAnchor.MiddleRight,
            clipping = TextClipping.Overflow
        };

        _cachedRightAlignedStyle = new GUIStyle(_labelStyle)
        {
            alignment = TextAnchor.MiddleRight,
            clipping = TextClipping.Overflow
        };
        _sliderStyle = new GUIStyle(GUI.skin.horizontalSlider);
        _thumbStyle = new GUIStyle(GUI.skin.horizontalSliderThumb);

        // Copy normal state into disabled so it looks the same
        _sliderStyle.onNormal = _sliderStyle.normal;
        _thumbStyle.onNormal = _thumbStyle.normal;

        StyleInitialized = true;
    }

    private IEnumerator LoadAndForceLocalPlaylist()
    {
        _forcePlaylistPending = true;

        // Load tracks
        bool success = false;
        yield return null;

        try
        {
            CustomMusicManager.LoadLocalTracks(_localMusicPath);
            success = true;
        }
        catch (Exception e)
        {
            Debug.LogError($"Load error: {e}");
        }

        yield return new WaitUntil(() => !CustomMusicManager.IsLoading);

        if (success && CustomMusicManager.LocalTracks.Count > 0)
        {
            // Wait for 1 second and one additional frame
            yield return new WaitForSecondsRealtime(0.2f);
            yield return null;

            // Use the unified playback method
            CustomMusicManager.PlayTrackWithMethod(0, PlaylistType.Local);
            CustomMusicManager.LockCustomPlaylist = true;

            Debug.Log("Force-loaded custom playlist after delay");
        }
        else
        {
            Debug.LogError("Failed to force-load custom playlist");
        }
        _viewingPlaylistType = PlaylistType.Local;
        _forcePlaylistPending = false;
    }

    private string GetValidatedMusicPath()
    {
        // Get configured path
        string configPath = LandfallConfig.CurrentConfig.LocalMusicPath;

        // Check if path exists and is accessible
        try
        {
            if (Directory.Exists(configPath))
            {
                // Test read access
                Directory.GetFiles(configPath, "*.*", SearchOption.TopDirectoryOnly);
                return configPath;
            }
            else
            {
                // If path doesn't exist, check if it's the default CustomMusic directory
                string defaultCustomMusicPath = WorkshopHelper.DefaultMusicPath;
                if (string.Equals(configPath, defaultCustomMusicPath, StringComparison.OrdinalIgnoreCase))
                {
                    // This should already exist from WorkshopHelper, but double-check
                    if (!Directory.Exists(defaultCustomMusicPath))
                    {
                        try
                        {
                            Directory.CreateDirectory(defaultCustomMusicPath);
                            Debug.Log($"Created CustomMusic directory: {defaultCustomMusicPath}");
                        }
                        catch (Exception ex)
                        {
                            Debug.LogWarning($"Failed to create CustomMusic directory: {ex.Message}");
                        }
                    }
                    return defaultCustomMusicPath;
                }
            }
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"Invalid music path: {ex.Message}");
        }

        // Fallback to the CustomMusic directory
        Debug.LogWarning($"Falling back to default CustomMusic directory: {WorkshopHelper.DefaultMusicPath}");
        return WorkshopHelper.DefaultMusicPath;
    }

    void DrawMusicWindow(int windowID)
    {
        // Save original background color
        Color originalColor = GUI.backgroundColor;

        // Set semi-transparent background
        GUI.backgroundColor = new Color(0.1f, 0.1f, 0.85f, 0.7f);
        GUI.Box(new Rect(0, 0, _windowRect.width, _windowRect.height), "", new GUIStyle(GUI.skin.box));
        GUI.backgroundColor = originalColor;



        // Main content area using GUILayout
        GUILayout.BeginArea(new Rect(0, 20, _windowRect.width, _windowRect.height - 25));
        GUILayout.BeginVertical();
        {

            // Music player section
            DrawMusicPlayerSection(_labelStyle, _labelClipName);

            // Control buttons section
            DrawControlButtonsSection(_buttonStyle20);

            // Tabbed interface section
            DrawTabbedInterfaceSection();

            GUILayout.FlexibleSpace();

            // Bottom controls section
            DrawBottomControlsSection(_buttonStyle16, _toggleStyle);
        }
        GUILayout.EndVertical();
        GUILayout.EndArea();
        //tab cycle fix
        if (
                (Event.current.type == EventType.KeyDown || Event.current.type == EventType.KeyUp) &&
                Event.current.keyCode == KeyCode.Tab)
        {
            // Skip focus
            GUI.FocusControl(null);
            Event.current.Use();
        }

        GUI.DragWindow(new Rect(0, 0, _windowRect.width, 20));
    }

    private void DrawMusicPlayerSection(GUIStyle labelStyle, GUIStyle labelClipName)
    {
        if (labelStyle is null)
        {
            throw new ArgumentNullException(nameof(labelStyle));
        }

        var nameStyle = _cachedNameStyle;
        var timeStyle = _cachedTimeStyle;
        var rightAlignedStyle = _cachedRightAlignedStyle;

        bool hasAudioPlaying = false;
        string trackName = "Unknown";
        float currentTime = 0f;
        float totalTime = 0f;
        bool isRadioStream = false;
        var sc = StreamingClip.Instance;

        // Use CurrentPlaybackMethod to determine track info source
        switch (CustomMusicManager.CurrentPlaybackMethod)
        {
            case PlaybackMethod.UnityAudio:
                // For Default and Local preloaded - use Unity AudioSource
                var audioSource = MusicPlayer.Instance?.m_AudioSourceCurrent;
                if (audioSource != null && audioSource.clip != null)
                {
                    hasAudioPlaying = true;
                    trackName = audioSource.clip.name ?? GetCurrentTrackNameFromPlaylist();
                    currentTime = audioSource.time;
                    totalTime = audioSource.clip.length;
                }
                else
                {
                    // Fallback to playlist name
                    trackName = GetCurrentTrackNameFromPlaylist();
                    hasAudioPlaying = !string.IsNullOrEmpty(trackName) && trackName != "Unknown";
                }
                break;

            case PlaybackMethod.Streaming:
                // For streaming tracks - use StreamingClip/BASS
                if (CustomMusicManagerExtensions.IsStreamPlaying())
                {
                    hasAudioPlaying = true;

                    trackName = GetAuthoritativeTrackName("Streaming Track");
                    currentTime = CustomMusicManagerExtensions.GetStreamCurrentTime();
                    totalTime = CustomMusicManagerExtensions.GetStreamTotalTime();
                    isRadioStream = (StreamingClip.CurrentPlaybackMode == MusicPlayerMode.RadioStream);
                }
                else
                {
                    // Fallback to playlist name
                    trackName = GetCurrentTrackNameFromPlaylist();
                    hasAudioPlaying = !string.IsNullOrEmpty(trackName) && trackName != "Unknown";
                }
                break;
        }

        if (hasAudioPlaying)
        {
            GUILayout.BeginVertical(GUILayout.MinHeight(65), GUILayout.Height(65), (GUILayout.MinHeight(65)));
            {
                // Track name and time display
                GUILayout.BeginHorizontal(GUILayout.MaxHeight(20));
                {
                    GUILayout.Space(5);

                    // Track name
                    GUILayout.Label(
                        trackName.Length > 30 ? trackName[..30] : trackName,
                        labelClipName,
                        GUILayout.Height(20),
                        GUILayout.ExpandWidth(true),
                        GUILayout.MaxWidth(280)
                    );

                    GUILayout.FlexibleSpace();

                    // Time display
                    string totalTimeDisplay = isRadioStream ? "Live" : FormatTime(totalTime);

                    GUILayout.Label(
                        $"{FormatTime(currentTime)} / {totalTimeDisplay}",
                        timeStyle,
                        GUILayout.Width(100),
                        GUILayout.MaxHeight(20)
                    );

                    GUILayout.Space(5);
                }
                GUILayout.EndHorizontal();


                // Progress bar
                GUILayout.BeginHorizontal();
                {
                    GUILayout.Space(5);

                    // Resolve times depending on mode
                    bool isStream = StreamingClip.CurrentPlaybackMode == MusicPlayerMode.PlaylistOnDemand || StreamingClip.CurrentPlaybackMode == MusicPlayerMode.UrlFinite || StreamingClip.CurrentPlaybackMode == MusicPlayerMode.RadioStream;

                    // Derive progress safely
                    float progress = 0f;
                    if (totalTime > Mathf.Epsilon)
                        progress = Mathf.Clamp01(currentTime / totalTime);


                    // Decide seekability:
                    // - If we are presenting a streaming clip, only allow seek when the stream has a trustworthy length:
                    //   HasRecordedRealLength (finalized) or IsFullyDownloaded.
                    // - If we're showing a preloaded Unity clip, allow seek when totalTime > 0.
                    bool canSeek;
                    if (sc != null && isStream)
                    {
                        canSeek = (totalTime > Mathf.Epsilon) && (sc.HasRecordedRealLength || sc.IsFullyDownloaded || sc.HasRealLength);
                    }
                    else
                    {
                        // Unity preloaded clips or other local playback
                        canSeek = (totalTime > Mathf.Epsilon);
                    }

                    GUI.enabled = canSeek;
                    float newProgress = GUILayout.HorizontalSlider(progress, 0f, 1f, _sliderStyle, _thumbStyle, GUILayout.Width(390), GUILayout.MaxHeight(10));
                    GUI.enabled = true;

                    // If changed, compute new time and perform seek through the appropriate API
                    if (canSeek && Math.Abs(newProgress - progress) > 0.0001f)
                    {
                        float newTime = Mathf.Clamp01(newProgress) * totalTime;
                        if (sc != null && isStream)
                            CustomMusicManagerExtensions.SeekStream(newTime);
                        else
                            MusicPlayer.Instance.m_AudioSourceCurrent.time = newTime;
                    }

                    GUILayout.Space(5);
                }
                GUILayout.EndHorizontal();

                // Playlist and volume section (unchanged)
                GUILayout.BeginHorizontal(GUILayout.MaxHeight(20));
                {
                    GUILayout.Space(5);
                    // Playlist name 
                    string playlistName = GetCurrentPlaylistName();
                    GUILayout.Label(
                        $"Playlist: {playlistName}",
                        nameStyle,
                        GUILayout.Height(20),
                        GUILayout.ExpandHeight(false),
                        GUILayout.Width(180)
                    );

                    GUILayout.FlexibleSpace();



                    // Volume label 
                    GUILayout.Label("Volume:", rightAlignedStyle, GUILayout.Height(15), GUILayout.Width(55));

                    // Volume slider
                    GUILayout.BeginVertical();
                    GUILayout.FlexibleSpace();
                    float newVolume = GUILayout.HorizontalSlider(_currentVolume, 0f, 1f, GUILayout.Height(10), GUILayout.Width(100));
                    if (Mathf.Abs(newVolume - _currentVolume) > 0.01f)
                    {
                        _currentVolume = newVolume;
                        MusicVolumeController.Instance.Volume = _currentVolume;
                    }
                    GUILayout.Space(2);
                    GUILayout.EndVertical();

                    // Percentage 
                    GUILayout.Label($"{Mathf.RoundToInt(_currentVolume * 100)}%",
                        rightAlignedStyle,
                        GUILayout.Height(20), GUILayout.MaxWidth(35), GUILayout.Width(35));
                    GUILayout.Space(5);
                }
                GUILayout.EndHorizontal();
            }
            GUILayout.EndVertical();
        }
        else
        {
            GUILayout.BeginHorizontal(GUILayout.MinHeight(65), GUILayout.Height(65));
            GUILayout.Space(5);
            GUILayout.Label("No music playing");
            GUILayout.EndHorizontal();
        }
    }

    private void DrawControlButtonsSection(GUIStyle buttonStyle20)
    {
        GUILayout.BeginHorizontal(GUILayout.Width(400));
        {
            GUILayout.Space(5);

            if (GUILayout.Button(GetPlayOrderLabel(), buttonStyle20, GUILayout.Height(25), GUILayout.MaxWidth(125f), GUILayout.Width(125f)))
            {
                CyclePlayOrder();
            }

            GUILayout.FlexibleSpace();

            //GUI.enabled = CustomMusicManager.IsCustomPlaylistActive;
            GUI.enabled = _viewingPlaylistType != PlaylistType.Default;
            if (GUILayout.Button("SHUFFLE", buttonStyle20, GUILayout.Height(25), GUILayout.MaxWidth(125f), GUILayout.Width(125f)))
            {
                ShuffleViewedPlaylist();
            }
            GUI.enabled = true;

            GUILayout.FlexibleSpace();

            if (GUILayout.Button("▶▶I", buttonStyle20, GUILayout.Height(25), GUILayout.MaxWidth(125f), GUILayout.Width(125f)))
            {
                CustomMusicManager.PlayNextTrack();
            }
            GUILayout.Space(5);
        }
        GUILayout.EndHorizontal();
    }

    private void DrawTabbedInterfaceSection()
    {
        GUILayout.BeginVertical(GUI.skin.box, GUILayout.ExpandHeight(true), GUILayout.ExpandWidth(false));
        {
            // Define tab styles
            var normalTabStyle = new GUIStyle(GUI.skin.button)
            {
                fontSize = 13,
                fontStyle = FontStyle.Normal,
                alignment = TextAnchor.MiddleCenter,
                fixedHeight = 25
            };

            var selectedTabStyle = new GUIStyle(normalTabStyle)
            {
                fontSize = 16,
                fontStyle = FontStyle.Bold,
                fixedHeight = 30
            };

            // Tab toolbar 
            GUILayout.BeginHorizontal();
            {
                string[] tabNames = { "Local Files", "❤Favorite❤", "Streams" };
                for (int i = 0; i < tabNames.Length; i++)
                {
                    var tab = (AudioSourceTab)i;
                    var style = (int)ActiveTab == i ? selectedTabStyle : normalTabStyle;
                    GUILayout.BeginVertical();
                    {
                        GUILayout.FlexibleSpace();
                        if (GUILayout.Button(tabNames[i], style, GUILayout.Width(125)))
                        {
                            ActiveTab = tab; // This only changes viewing, not playing
                        }
                    }
                    GUILayout.EndVertical();
                }
            }
            GUILayout.EndHorizontal();

            // Tab content
            GUILayout.BeginVertical(GUI.skin.box, GUILayout.ExpandHeight(true), GUILayout.MaxHeight(95), GUILayout.Height(95), GUILayout.MinHeight(95));
            {
                //string playbackStatus = CustomMusicManager.CurrentPlaybackPlaylistType == PlaylistType.Default
                //    ? "Playing: DEFAULT"
                //    : $"Playing: {CustomMusicManager.CurrentPlaybackPlaylistType}";

                //string viewingStatus = $"Viewing: {CurrentViewTab}";

                //GUILayout.Label(playbackStatus, GUILayout.Height(15));
                //GUILayout.Label(viewingStatus, GUILayout.Height(15));

                switch (ActiveTab)
                {
                    case AudioSourceTab.Local:
                        DrawLocalTab();
                        break;
                    case AudioSourceTab.Hybrid:
                        DrawHybridTab();
                        break;
                    case AudioSourceTab.Streams:
                        DrawStreamTab();
                        break;
                }
            }
            GUILayout.EndVertical();
        }
        GUILayout.EndVertical();
    }

    private void DrawBottomControlsSection(GUIStyle buttonStyle16, GUIStyle toggleStyle)
    {
        GUILayout.BeginHorizontal(GUILayout.Height(25), GUILayout.MaxWidth(400), GUILayout.ExpandWidth(false));
        {
            GUILayout.Space(5);

            // Playlist visibility toggle
            if (GUILayout.Button(_playlistWindowVisible ? "Hide Playlist" : "Show Playlist", buttonStyle16, GUILayout.Height(25), GUILayout.MinWidth(150)))
            {
                _playlistWindowVisible = !_playlistWindowVisible;
            }

            GUILayout.FlexibleSpace();

            // Lock toggle
            CustomMusicManager.LockCustomPlaylist = GUILayout.Toggle(
                CustomMusicManager.LockCustomPlaylist,
                "Lock Playlist",
                toggleStyle,
                GUILayout.Height(20)
            );
            GUI.enabled = true;

            // Smart playlist switch button
            string buttonText = GetPlaylistSwitchButtonText();
            if (GUILayout.Button(buttonText, buttonStyle16, GUILayout.Width(90), GUILayout.Height(25)))
            {
                ToggleBetweenDefaultAndCurrentTabPlaylist();
            }
            GUILayout.Space(5);
        }
        GUILayout.EndHorizontal();
    }

    private string GetPlaylistSwitchButtonText()
    {
        // Show what we would switch TO, not what's playing
        if (_viewingPlaylistType == PlaylistType.Default)
        {
            return ActiveTab switch
            {
                AudioSourceTab.Local => "Local ▶",
                AudioSourceTab.Hybrid => "Favorite ▶",
                AudioSourceTab.Streams => "Stream ▶",
                _ => "Local ▶"
            };
        }
        else
        {
            return "◀ Default";
        }
    }

    private void ToggleBetweenDefaultAndCurrentTabPlaylist()
    {
        PlaylistType targetPlaylistType = ActiveTab switch
        {
            AudioSourceTab.Local => PlaylistType.Local,
            AudioSourceTab.Hybrid => PlaylistType.Hybrid,
            AudioSourceTab.Streams => PlaylistType.Streams,
            _ => PlaylistType.Local
        };

        if (LandfallConfig.CurrentConfig.ShowDebug) Debug.Log($"Toggle: Current viewing={_viewingPlaylistType}, Target={targetPlaylistType}");

        // If we're currently viewing the target playlist, switch to view default
        if (_viewingPlaylistType == targetPlaylistType)
        {
            if (LandfallConfig.CurrentConfig.ShowDebug) Debug.Log("Toggling to view default playlist");
            SwitchToViewPlaylist(PlaylistType.Default);
        }
        // Otherwise, switch to view the target playlist
        else
        {
            if (LandfallConfig.CurrentConfig.ShowDebug) Debug.Log($"Toggling to view {targetPlaylistType} playlist");
            SwitchToViewPlaylist(targetPlaylistType);
        }

        // Reset selection
        _selectedTrackIndex = -1;
        _lastClickedTrack = -1;
        GUI.changed = true;
    }
    private void SwitchToViewPlaylist(PlaylistType playlistType)
    {
        _viewingPlaylistType = playlistType;


        // Keep current tab when switching to default
        if (playlistType != PlaylistType.Default)
        {
            _activeTab = playlistType switch
            {
                PlaylistType.Local => AudioSourceTab.Local,
                PlaylistType.Hybrid => AudioSourceTab.Hybrid,
                PlaylistType.Streams => AudioSourceTab.Streams,
                _ => AudioSourceTab.Local
            };
        }

        _selectedTrackIndex = -1;
        _lastClickedTrack = -1;
        GUI.changed = true;
        if (LandfallConfig.CurrentConfig.ShowDebug) Debug.Log($"Now viewing: {_viewingPlaylistType}");
    }

    private void DrawLocalTab()
    {
        var nameStyle = new GUIStyle(GUI.skin.box)
        {
            border = GUI.skin.label.border,
            normal = GUI.skin.label.normal,
            alignment = TextAnchor.MiddleLeft,
            wordWrap = false,
            clipping = TextClipping.Overflow

        };
        GUILayout.BeginVertical();
        {
            GUILayout.Label("Custom music path:", nameStyle, GUILayout.Height(10));

            // Path input
            string newPath = GUILayout.TextField(
                _localMusicPath,
                GUILayout.ExpandWidth(true),
                GUILayout.Height(20)
            );


            // Only update the actual path when not loading
            if (!CustomMusicManager.IsLoading)
            {
                _localMusicPath = newPath;
            }

            // Loading progress - visual bar only
            if (CustomMusicManager.IsLoading)
            {
                // Progress bar with percentage
                GUILayout.BeginVertical();
                {
                    float progress = CustomMusicManager.LoadProgress;

                    // Progress bar background
                    Rect progressRect = GUILayoutUtility.GetRect(200, 20);
                    GUI.Box(progressRect, "");

                    // Progress fill
                    Rect fillRect = new Rect(progressRect.x, progressRect.y,
                                           progressRect.width * progress, progressRect.height);
                    GUI.DrawTexture(fillRect, Texture2D.whiteTexture);

                    // Progress text
                    string progressText = $"{CustomMusicManager.LoadedTracksCount}/{CustomMusicManager.TotalTracksCount} ({progress * 100:F0}%)";
                    GUI.Label(progressRect, progressText, new GUIStyle(GUI.skin.label)
                    {
                        alignment = TextAnchor.MiddleCenter,
                        normal = { textColor = Color.black }
                    });
                }
                GUILayout.EndVertical();
            }
            else
            {
                GUILayout.BeginHorizontal();
                {
                    // Clear playlist button
                    GUI.enabled = CustomMusicManager.LocalTracks != null && CustomMusicManager.LocalTracks.Count > 0;
                    if (GUILayout.Button("Clear Playlist", GUILayout.ExpandWidth(false), GUILayout.MinWidth(1), GUILayout.Height(20)))
                    {
                        CustomMusicManager.ClearPlaylist(PlaylistType.Local);
                    }
                    GUI.enabled = true;

                    GUILayout.FlexibleSpace();
                    // Playlist preload and subfolder flags
                    LandfallConfig.CurrentConfig.PreloadEntirePlaylist = GUILayout.Toggle(
                        LandfallConfig.CurrentConfig.PreloadEntirePlaylist,
                        "Preload",
                        _toggleStyle,
                        GUILayout.Height(20)
                    );
                    GUILayout.Space(5);
                    LandfallConfig.CurrentConfig.ScanSubfolders = GUILayout.Toggle(
                        LandfallConfig.CurrentConfig.ScanSubfolders,
                        "Scan Subfolders",
                        _toggleStyle,
                        GUILayout.Height(20)
                    );
                    GUILayout.Space(5);

                    // Load button
                    if (GUILayout.Button("Load", GUILayout.ExpandWidth(false), GUILayout.MinWidth(1), GUILayout.Height(20)))
                    {
                        if (CustomMusicManager.LoadLocalTracks(_localMusicPath))
                        {
                            SwitchToViewPlaylist(PlaylistType.Local);
                        }
                    }
                }
                GUILayout.EndHorizontal();
            }

            // Simple memory status only
            GUILayout.Space(5);
            try
            {
                long memory = UnityEngine.Profiling.Profiler.GetTotalAllocatedMemoryLong() / 1024 / 1024;
                GUILayout.Label($"Memory: {memory}MB", nameStyle, GUILayout.Height(10));
            }
            catch
            {
                // Ignore memory display errors
            }
        }
        GUILayout.EndVertical();
    }


    private void DrawHybridTab()
    {
        var nameStyle = new GUIStyle(GUI.skin.box)
        {
            border = GUI.skin.label.border,
            normal = GUI.skin.label.normal,
            alignment = TextAnchor.MiddleLeft,
            wordWrap = false,
            clipping = TextClipping.Overflow
        };
        var overflowButton = new GUIStyle(GUI.skin.button)
        {
            clipping = TextClipping.Overflow
        };
        var hintStyle = nameStyle;
        hintStyle.fontSize = 11;
        GUILayout.BeginVertical();
        {
            GUILayout.Label("Audio path:", nameStyle, GUILayout.Height(10));
            //  input row 
            _customHybridPath = GUILayout.TextField(_customHybridPath, GUILayout.ExpandWidth(true), GUILayout.Height(20));

            GUILayout.BeginHorizontal();
            {
                bool canSave = CustomMusicManager.HybridTrackPaths.Count > 0 && string.IsNullOrEmpty(_activeTimerType);
                GUI.enabled = canSave;
                if (GUILayout.Button("Save Playlist", overflowButton, GUILayout.ExpandWidth(false), GUILayout.Width(80), GUILayout.Height(20)))
                {
                    StartSaveTimer();
                }
                GUI.enabled = true;

                // Timer button for save (only shown when save timer is active)
                if (_activeTimerType == "save")
                {
                    float timeLeft = TIMER_DURATION - (Time.realtimeSinceStartup - _timerStartTime);
                    if (timeLeft > 0)
                    {
                        string timerText = Mathf.CeilToInt(timeLeft).ToString();
                        if (GUILayout.Button($"<color=green> {timerText} </color >", GUILayout.Width(20), GUILayout.Height(20)))
                        {
                            ExecuteSave();
                            CancelTimer();
                        }
                    }
                    else
                    {
                        // Timer expired
                        CancelTimer();
                    }
                }
                else
                {
                    GUILayout.Space(20);
                }
                GUILayout.Label("SHIFT + DOUBLE CLICK TO ADD", hintStyle, GUILayout.Width(100));
                GUILayout.FlexibleSpace();

                // Play button - plays immediately without adding to playlist
                GUI.enabled = !string.IsNullOrEmpty(_customHybridPath);
                if (GUILayout.Button("Play", GUILayout.ExpandWidth(false), GUILayout.Height(20)))
                {
                    PlayHybridTrackDirect(_customHybridPath);
                }
                GUI.enabled = true;
            }
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            {
                bool canLoad = string.IsNullOrEmpty(_activeTimerType);
                GUI.enabled = canLoad;
                if (GUILayout.Button("Load Playlist", overflowButton, GUILayout.ExpandWidth(false), GUILayout.Width(80), GUILayout.Height(20)))
                {
                    StartLoadTimer();
                }
                GUI.enabled = true;

                // Timer button for load (only shown when load timer is active)
                if (_activeTimerType == "load")
                {
                    float timeLeft = TIMER_DURATION - (Time.realtimeSinceStartup - _timerStartTime);
                    if (timeLeft > 0)
                    {
                        string timerText = Mathf.CeilToInt(timeLeft).ToString();
                        if (GUILayout.Button($"<color=green> {timerText} </color >", GUILayout.Width(20), GUILayout.Height(20)))
                        {
                            LoadHybridPlaylistAndSwitchView();
                            CancelTimer();
                        }
                    }
                    else
                    {
                        // Timer expired
                        CancelTimer();
                    }
                }
                else
                {
                    GUILayout.Space(20);
                }

                GUILayout.Label("CTRL + DOUBLE CLICK TO DEL", hintStyle, GUILayout.Width(100));
                GUILayout.FlexibleSpace();
                GUI.enabled = !string.IsNullOrEmpty(_customHybridPath) && IsValidPath(_customHybridPath);
                if (GUILayout.Button("Add to Playlist", overflowButton, GUILayout.ExpandWidth(false), GUILayout.Width(90), GUILayout.Height(20)))
                {
                    AddToHybridPlaylist(_customHybridPath);
                }
                GUI.enabled = true;
            }
            GUILayout.EndHorizontal();
        }
        GUILayout.EndVertical();
    }

    private void DrawStreamTab()
    {
        var nameStyle = new GUIStyle(GUI.skin.box)
        {
            border = GUI.skin.label.border,
            normal = GUI.skin.label.normal,
            alignment = TextAnchor.MiddleLeft,
            wordWrap = false,
            clipping = TextClipping.Overflow
        };
        GUILayout.BeginVertical();
        {
            bool connected = CustomMusicManager._streamingInstance != null;
            GUILayout.Label("Stream URL:", nameStyle, GUILayout.Height(10));
            // Stream input row
            _customStreamPath = GUILayout.TextField(_customStreamPath, GUILayout.ExpandWidth(true), GUILayout.Height(20));
            // Connection status and controls
            GUILayout.BeginHorizontal();
            {

                GUILayout.Label(connected ? "Status: Valid Path" : "Status: No Instance", GUILayout.ExpandWidth(false), GUILayout.Height(20));



                GUILayout.FlexibleSpace();

                StreamingClip.TreatInputAsPlaylist = GUILayout.Toggle(StreamingClip.TreatInputAsPlaylist, "Playlist(.m3u/...)", GUILayout.Height(20), GUILayout.Width(120));

                GUI.enabled = !connected && !string.IsNullOrEmpty(_customStreamPath);
                if (GUILayout.Button("Start", GUILayout.ExpandWidth(false), GUILayout.Height(20), GUILayout.Width(45)))
                {
                    StartStreamFromTab();
                }
                GUI.enabled = true;
            }
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            {
                float buf = CustomMusicManagerExtensions.GetStreamBufferPercent();
                GUILayout.Label($"Buffer: {(buf * 100f):F0}%", GUILayout.ExpandWidth(false));
                GUILayout.FlexibleSpace();

                // Load saved streams button
                if (GUILayout.Button("Load Saved", GUILayout.ExpandWidth(false), GUILayout.Height(20), GUILayout.Width(95)))
                {
                    LoadStreamsPlaylistAndSwitchView();
                }
                GUILayout.Space(25);

                connected = CustomMusicManager._streamingInstance != null;
                GUI.enabled = connected;
                if (GUILayout.Button("Stop", GUILayout.ExpandWidth(false), GUILayout.Height(20), GUILayout.Width(45)))
                {
                    CustomMusicManager.StopStreaming();
                }
                GUI.enabled = true;
            }
            GUILayout.EndHorizontal();
        }
        GUILayout.EndVertical();
    }

    private void DrawPlaylistWindow(int windowID)
    {
        // Save original background color
        Color originalColor = GUI.backgroundColor;

        // Set semi-transparent background (slightly darker blue)
        GUI.backgroundColor = new Color(0.08f, 0.08f, 0.8f, 0.7f);

        // Create a box that fills the window for background
        GUI.Box(new Rect(0, 0, _playlistWindowRect.width, _playlistWindowRect.height), "", new GUIStyle(GUI.skin.box));

        // Reset background color for other elements
        GUI.backgroundColor = originalColor;

        // Draw playlist content
        DrawPlaylistContents(5, 20, _playlistWindowRect.width - 10, _playlistWindowRect.height - 25);


        _resizeHandle = new Rect(0, _playlistWindowRect.height - 5, _playlistWindowRect.width, 10);
        GUI.Box(_resizeHandle, "", GUI.skin.button);

        // Allow dragging the window (scaled coordinates are handled automatically by GUI.Window)
        GUI.DragWindow(new Rect(0, 0, _playlistWindowRect.width, 20));
    }

    private string GetCurrentPlaylistName()
    {
        // Always show the currently PLAYING playlist name
        switch (CustomMusicManager.CurrentPlaybackPlaylistType)
        {
            case PlaylistType.Default:
                // Use stored default playlist name
                var defaultPl = GetStoredDefaultPlaylist();
                return defaultPl != null ? defaultPl.name : "Default Playlist";

            case PlaylistType.Local:
                return "Local Playlist";

            case PlaylistType.Hybrid:
                return "Favorite Playlist";

            case PlaylistType.Streams:
                return "Stream Playlist";

            default:
                return "Unknown Playlist";
        }
    }
    public static MusicPlaylist GetStoredDefaultPlaylist()
    {
        // Access the stored default playlist from CustomMusicManager
        if (CustomMusicManager._lastAttemptedDefaultPlaylist != null)
            return CustomMusicManager._lastAttemptedDefaultPlaylist;
        if (CustomMusicManager._prevDefaultPlaylist != null)
            return CustomMusicManager._prevDefaultPlaylist;

        return MusicPlayer.Instance?.currentlyPlaying?.playlist;
    }

    // Helper methods
    private string GetPlayOrderLabel()
    {
        return CustomMusicManager.CurrentPlayOrder switch
        {
            CustomMusicManager.PlayOrder.Sequential => "▶▶▶",
            CustomMusicManager.PlayOrder.Loop => "↳↰",
            CustomMusicManager.PlayOrder.Random => "❹▶❶▶❸",
            _ => "▶▶",
        };
    }

    private void CyclePlayOrder()
    {
        int next = (int)CustomMusicManager.CurrentPlayOrder + 1;
        if (next > 2) next = 0;
        CustomMusicManager.CurrentPlayOrder = (CustomMusicManager.PlayOrder)next;

        // When switching to Sequential from Random in shuffled playlist, 
        // find current track position in shuffled order
        if (CustomMusicManager.CurrentPlayOrder == CustomMusicManager.PlayOrder.Sequential &&
            CustomMusicManager.shuffledOrder.Count > 0)
        {
            int currentTrackIndex = CustomMusicManager.CurrentTrackIndex;
            int positionInShuffled = CustomMusicManager.shuffledOrder.IndexOf(currentTrackIndex);

            if (positionInShuffled >= 0)
            {
                CustomMusicManager.shuffleIndex = positionInShuffled;
            }
            else
            {
                // If current track not found in shuffled order, start from beginning
                CustomMusicManager.shuffleIndex = 0;
            }
        }

        // Reset track history when switching to random
        if (CustomMusicManager.CurrentPlayOrder == CustomMusicManager.PlayOrder.Random)
        {
            CustomMusicManager.playedTracks.Clear();
        }
    }

    private void DrawPlaylistContents(float x, float y, float width, float height)
    {
        IPlaylist playlistToDisplay = PlaylistManager.GetPlaylist(_viewingPlaylistType);

        if (playlistToDisplay == null || playlistToDisplay.TrackCount == 0)
        {
            GUI.Label(new Rect(x + 5, y + 5, width - 10, 20), "No tracks in current playlist");
            return;
        }

        var scrollPos = GetScroll(_viewingPlaylistType);

        // Determine display order - use shuffled order if available, otherwise natural order
        List<int> displayOrder;
        if (playlistToDisplay.ShuffledOrder.Count > 0 && playlistToDisplay.ShuffledOrder.Count == playlistToDisplay.TrackCount)
        {
            displayOrder = playlistToDisplay.ShuffledOrder;
        }
        else
        {
            displayOrder = Enumerable.Range(0, playlistToDisplay.TrackCount).ToList();
        }

        float contentHeight = displayOrder.Count * 20f;

        scrollPos = GUI.BeginScrollView(
            new Rect(x, y, width, height),
            scrollPos,
            new Rect(0, 0, width - 20, contentHeight),
            false,
            true
        );

        for (int displayIndex = 0; displayIndex < displayOrder.Count; displayIndex++)
        {
            int actualTrackIndex = displayOrder[displayIndex];

            int playingIndexInViewed = GetPlayingIndexForViewed();
            int selectedIndexInViewed = _selectedTrackIndex;
            bool isPlayingHere = (displayIndex == playingIndexInViewed);
            bool isSelectedHere = (displayIndex == selectedIndexInViewed);
            // Use animated name if this track is being animated
            string trackName = HMPAnimation.GetAnimatedTrackName(_viewingPlaylistType, actualTrackIndex,
                playlistToDisplay.GetTrackDisplayName(actualTrackIndex));

            // Row color status
            if (isPlayingHere) GUI.contentColor = Color.yellow;
            else if (isSelectedHere) GUI.contentColor = Color.cyan;
            else GUI.contentColor = Color.white;

            Rect trackRect = new Rect(0, displayIndex * 20, width - 20, 20);
            GUI.Label(trackRect, $"{displayIndex + 1}. {trackName}", _labelClipName);

            // Mouse handling: single-select and double-click play
            if (Event.current.type == EventType.MouseDown && trackRect.Contains(Event.current.mousePosition))
            {
                float now = Time.realtimeSinceStartup;

                if (displayIndex == _lastClickedTrack && now < _singleClickExpireAt)
                {
                    // Double-click detected
                    if (Event.current.shift) // SHIFT+Double-click: Add to Hybrid
                    {
                        // Get the actual track index (considering shuffle)
                        int trackIndexToAdd = actualTrackIndex;

                        // If we're viewing a shuffled playlist, get the actual track from shuffle order
                        var viewedPlaylist = PlaylistManager.GetPlaylist(_viewingPlaylistType) as BasePlaylist;
                        if (viewedPlaylist?.ShuffledOrder.Count > 0 &&
                            viewedPlaylist.ShuffledOrder.Count == viewedPlaylist.TrackCount)
                        {
                            trackIndexToAdd = viewedPlaylist.ShuffledOrder[displayIndex];
                        }


                        AddTrackToHybridPlaylist(_viewingPlaylistType, trackIndexToAdd);

                        _lastClickedTrack = -1;
                        _selectedTrackIndex = -1;
                        _singleClickExpireAt = 0f;
                        Event.current.Use();
                    }
                    else if (Event.current.control) // CTRL+Double-click: Remove track
                    {
                        if (_viewingPlaylistType != PlaylistType.Default)
                        {
                            RemoveTrackFromPlaylist(_viewingPlaylistType, displayIndex, actualTrackIndex);
                        }

                        _lastClickedTrack = -1;
                        _selectedTrackIndex = -1;
                        _singleClickExpireAt = 0f;
                        Event.current.Use();
                    }
                    else // Normal double-click: play track
                    {
                        // Double-click: play the track from the viewed playlist
                        if (LandfallConfig.CurrentConfig.ShowDebug) Debug.Log($"Double-click: Playing track at display index {displayIndex} from viewed {_viewingPlaylistType}");

                        // Get the actual track index (considering shuffle)
                        int trackIndexToPlay = actualTrackIndex;

                        // If we're viewing a shuffled playlist, get the actual track from shuffle order
                        var viewedPlaylist = PlaylistManager.GetPlaylist(_viewingPlaylistType) as BasePlaylist;
                        if (viewedPlaylist?.ShuffledOrder.Count > 0 &&
                            viewedPlaylist.ShuffledOrder.Count == viewedPlaylist.TrackCount)
                        {
                            trackIndexToPlay = viewedPlaylist.ShuffledOrder[displayIndex];
                            if (LandfallConfig.CurrentConfig.ShowDebug) Debug.Log($"Shuffled playlist: display index {displayIndex} = track index {trackIndexToPlay}");
                        }

                        // Use the unified playback method
                        CustomMusicManager.PlayTrackWithMethod(trackIndexToPlay, _viewingPlaylistType);


                        if (viewedPlaylist != null)
                        {
                            viewedPlaylist.CurrentTrackIndex = trackIndexToPlay;

                            // If shuffled, update shuffle index
                            if (viewedPlaylist.ShuffledOrder.Count > 0)
                            {
                                viewedPlaylist.SetShuffleIndex(displayIndex);
                            }
                        }

                        _lastClickedTrack = -1;
                        _selectedTrackIndex = -1;
                        _singleClickExpireAt = 0f;
                        Event.current.Use();
                    }
                }
                else
                {
                    // Single click: select display position
                    _selectedTrackIndex = displayIndex;
                    _lastClickedTrack = displayIndex;
                    _singleClickExpireAt = now + DoubleClickThreshold;
                    Event.current.Use();
                }
            }
        }

        GUI.EndScrollView();
        GUI.contentColor = Color.white;
        SetScroll(_viewingPlaylistType, scrollPos);

        // Reset selection if double-click window expired
        if (_selectedTrackIndex != -1 && Time.realtimeSinceStartup >= _singleClickExpireAt)
        {
            _selectedTrackIndex = -1;
            _lastClickedTrack = -1;
            _singleClickExpireAt = 0f;
        }
    }
    private void AddTrackToHybridPlaylist(PlaylistType sourcePlaylistType, int trackIndex, string trackPath = null)
    {
        try
        {
            string hybridTrackPath = "";
            string originalTrackName = "";

            // Format track path based on source playlist type
            switch (sourcePlaylistType)
            {
                case PlaylistType.Default:
                    // Format: "in-game:PlaylistName/TrackIndex"
                    var defaultPlaylist = GetStoredDefaultPlaylist();
                    string playlistName = defaultPlaylist?.name ?? "Default";
                    hybridTrackPath = $"in-game:{playlistName}/{trackIndex}";
                    originalTrackName = $"In-game №{trackIndex} from {playlistName}";
                    break;

                case PlaylistType.Local:
                    if (string.IsNullOrEmpty(trackPath) && trackIndex < CustomMusicManager.LocalTrackPaths.Count)
                    {
                        hybridTrackPath = CustomMusicManager.LocalTrackPaths[trackIndex];
                        originalTrackName = Path.GetFileNameWithoutExtension(hybridTrackPath);
                    }
                    else
                    {
                        hybridTrackPath = trackPath;
                        originalTrackName = Path.GetFileNameWithoutExtension(trackPath);
                    }
                    break;

                case PlaylistType.Streams:
                    if (string.IsNullOrEmpty(trackPath) && trackIndex < CustomMusicManager.StreamsTrackPaths.Count)
                    {
                        hybridTrackPath = CustomMusicManager.StreamsTrackPaths[trackIndex];
                        originalTrackName = PlaylistDecoder.GetTrackDisplayName(hybridTrackPath);
                    }
                    else
                    {
                        hybridTrackPath = trackPath;
                        originalTrackName = PlaylistDecoder.GetTrackDisplayName(trackPath);
                    }
                    break;

                case PlaylistType.Hybrid:
                    // Don't allow adding from Hybrid to itself
                    Debug.LogWarning("Cannot add Hybrid track to Hybrid playlist");
                    return;
            }

            if (string.IsNullOrEmpty(hybridTrackPath))
            {
                Debug.LogWarning($"Could not determine track path for {sourcePlaylistType} track {trackIndex}");
                return;
            }

            // Check for duplicates
            if (CustomMusicManager.HybridTrackPaths.Contains(hybridTrackPath))
            {
                Debug.Log("Track is already in Hybrid playlist");
                return;
            }

            // Start animation before adding (so we capture the original state)
            HMPAnimation.StartAddAnimation(sourcePlaylistType, trackIndex, originalTrackName);

            // Add to Hybrid playlist
            CustomMusicManager.HybridTrackPaths.Add(hybridTrackPath);
            CustomMusicManager.CreateHybridPlaylistFromTracks();


            Debug.Log($"Added {sourcePlaylistType} track to Hybrid playlist: {hybridTrackPath}");
        }
        catch (Exception ex)
        {
            Debug.LogError($"Error adding track to Hybrid playlist: {ex}");
        }
    }

    private void RemoveTrackFromPlaylist(PlaylistType playlistType, int displayIndex, int actualTrackIndex)
    {
        // Cooldown protection
        if (Time.realtimeSinceStartup - _lastRemovalTime < RemovalCooldown)
        {
            Debug.Log("Removal cooldown active, please wait");
            return;
        }

        if (playlistType == PlaylistType.Default)
        {
            Debug.LogWarning("Cannot remove tracks from Default playlist");
            return;
        }

        try
        {
            // Get the playlist to determine if we're in shuffle view
            var playlist = PlaylistManager.GetPlaylist(playlistType) as BasePlaylist;
            bool isShuffled = playlist?.ShuffledOrder.Count > 0;

            int trackIndexToRemove = actualTrackIndex;

            // If shuffled, we need special handling
            if (isShuffled && playlist.ShuffledOrder.Count > 0)
            {
                // Get the actual track index from the shuffled order
                if (displayIndex < playlist.ShuffledOrder.Count)
                {
                    trackIndexToRemove = playlist.ShuffledOrder[displayIndex];

                    // Remove from shuffled order first
                    playlist.ShuffledOrder.RemoveAt(displayIndex);

                    // Update all shuffled indices that point to tracks after the removed one
                    for (int i = 0; i < playlist.ShuffledOrder.Count; i++)
                    {
                        if (playlist.ShuffledOrder[i] > trackIndexToRemove)
                        {
                            playlist.ShuffledOrder[i]--;
                        }
                    }

                    // Update shuffle index if needed
                    if (playlist.ShuffleIndex >= displayIndex)
                    {
                        playlist.ShuffleIndex = Math.Max(0, playlist.ShuffleIndex - 1);
                    }

                    // Also update played tracks
                    for (int i = 0; i < playlist.PlayedTracks.Count; i++)
                    {
                        if (playlist.PlayedTracks[i] > trackIndexToRemove)
                        {
                            playlist.PlayedTracks[i]--;
                        }
                        else if (playlist.PlayedTracks[i] == trackIndexToRemove)
                        {
                            playlist.PlayedTracks.RemoveAt(i);
                            i--; // Adjust index after removal
                        }
                    }
                }
            }

            // Remove from the actual playlist storage
            switch (playlistType)
            {
                case PlaylistType.Local:
                    if (trackIndexToRemove < CustomMusicManager.LocalTrackPaths.Count)
                    {
                        CustomMusicManager.LocalTrackPaths.RemoveAt(trackIndexToRemove);
                        if (CustomMusicManager.IsLocalPlaylistPreloaded && trackIndexToRemove < CustomMusicManager.LocalTracks.Count)
                        {
                            CustomMusicManager.LocalTracks.RemoveAt(trackIndexToRemove);
                        }
                        CustomMusicManager.CreateLocalPlaylistFromTracks();
                    }
                    break;

                case PlaylistType.Hybrid:
                    if (trackIndexToRemove < CustomMusicManager.HybridTrackPaths.Count)
                    {
                        CustomMusicManager.HybridTrackPaths.RemoveAt(trackIndexToRemove);
                        CustomMusicManager.CreateHybridPlaylistFromTracks();
                    }
                    break;

                case PlaylistType.Streams:
                    if (trackIndexToRemove < CustomMusicManager.StreamsTrackPaths.Count)
                    {
                        CustomMusicManager.StreamsTrackPaths.RemoveAt(trackIndexToRemove);
                        CustomMusicManager.CreateStreamsPlaylistFromTracks();
                    }
                    break;
            }

            // Update current track indices if needed
            UpdateTrackIndicesAfterRemoval(playlistType, trackIndexToRemove);

            // Reset selection
            _selectedTrackIndex = -1;
            _lastClickedTrack = -1;

            Debug.Log($"Removed track {trackIndexToRemove} from {playlistType} playlist (display index: {displayIndex})");
            GUI.changed = true;
        }
        catch (Exception ex)
        {
            Debug.LogError($"Error removing track from playlist: {ex}");
        }
        _lastRemovalTime = Time.realtimeSinceStartup;
    }

    private void UpdateTrackIndicesAfterRemoval(PlaylistType playlistType, int removedIndex)
    {
        if (!(PlaylistManager.GetPlaylist(playlistType) is BasePlaylist playlist)) return;

        // Update the playlist's current track index
        if (playlist.CurrentTrackIndex >= removedIndex)
        {
            playlist.CurrentTrackIndex = Math.Max(0, playlist.CurrentTrackIndex - 1);
        }

        // Update the global current track indices
        switch (playlistType)
        {
            case PlaylistType.Local:
                if (CustomMusicManager.CurrentTrackIndex >= removedIndex)
                {
                    CustomMusicManager.CurrentTrackIndex = Math.Max(0, CustomMusicManager.CurrentTrackIndex - 1);
                }
                break;

            case PlaylistType.Hybrid:
                if (CustomMusicManager.HybridCurrentTrackIndex >= removedIndex)
                {
                    CustomMusicManager.HybridCurrentTrackIndex = Math.Max(0, CustomMusicManager.HybridCurrentTrackIndex - 1);
                }
                break;

            case PlaylistType.Streams:
                if (CustomMusicManager.StreamsCurrentTrackIndex >= removedIndex)
                {
                    CustomMusicManager.StreamsCurrentTrackIndex = Math.Max(0, CustomMusicManager.StreamsCurrentTrackIndex - 1);
                }
                break;
        }

        // If we're using shuffle order, make sure the current track index is valid in the shuffled order
        if (playlist.ShuffledOrder.Count > 0)
        {
            if (!playlist.ShuffledOrder.Contains(playlist.CurrentTrackIndex))
            {
                // Current track is no longer in shuffle order, find the closest valid one
                if (playlist.ShuffledOrder.Count > 0)
                {
                    playlist.CurrentTrackIndex = playlist.ShuffledOrder[0];
                    playlist.ShuffleIndex = 0;
                }
                else
                {
                    playlist.CurrentTrackIndex = 0;
                    playlist.ShuffleIndex = 0;
                }
            }
            else
            {
                // Update shuffle index to point to the current track
                playlist.ShuffleIndex = playlist.ShuffledOrder.IndexOf(playlist.CurrentTrackIndex);
            }
        }
    }
    private void ShuffleViewedPlaylist()
    {
        if (_viewingPlaylistType == PlaylistType.Default) return;

        try
        {
            if (PlaylistManager.GetPlaylist(_viewingPlaylistType) is BasePlaylist viewedPlaylist)
            {
                viewedPlaylist.InitializeShuffle();

                if (_viewingPlaylistType == CustomMusicManager.CurrentPlaybackPlaylistType)
                {
                    if (PlaylistManager.GetPlaylist(CustomMusicManager.CurrentPlaybackPlaylistType) is BasePlaylist playingPlaylist && playingPlaylist != viewedPlaylist)
                    {
                        playingPlaylist.SyncShuffleFrom(viewedPlaylist);
                    }
                }

                if (LandfallConfig.CurrentConfig.ShowDebug) Debug.Log($"Shuffled {_viewingPlaylistType} playlist");
                GUI.changed = true;
            }
        }
        catch (Exception ex)
        {
            Debug.LogError($"Error shuffling viewed playlist: {ex}");
        }
    }
    private void StartSaveTimer()
    {
        _activeTimerType = "save";
        _timerStartTime = Time.realtimeSinceStartup;
        if (LandfallConfig.CurrentConfig.ShowDebug) Debug.Log("Save timer started - click the countdown button to confirm");
    }

    private void StartLoadTimer()
    {
        _activeTimerType = "load";
        _timerStartTime = Time.realtimeSinceStartup;
        if (LandfallConfig.CurrentConfig.ShowDebug) Debug.Log("Load timer started - click the countdown button to confirm");
    }

    private void CancelTimer()
    {
        _activeTimerType = "";
        _timerStartTime = 0f;
        if (LandfallConfig.CurrentConfig.ShowDebug) Debug.Log("Timer cancelled");
    }

    private void ExecuteSave()
    {
        try
        {
            // Save only Hybrid playlist
            StartCoroutine(PlaylistBridge.SavePlaylistsCoroutine());
            if (LandfallConfig.CurrentConfig.ShowDebug) Debug.Log($"Saved {CustomMusicManager.HybridTrackPaths.Count} tracks to Hybrid playlist");
        }
        catch (Exception ex)
        {
            Debug.LogError($"Error saving Hybrid playlist: {ex}");
        }
    }

    private void LoadHybridPlaylistAndSwitchView()
    {
        try
        {
            // Load from file
            bool loadSuccess = LandfallConfig.LoadHybridPlaylistOnly();

            if (loadSuccess)
            {
                // Sync to manager
                bool syncSuccess = PlaylistBridge.SyncHybridPlaylistToCustomMusicManager();

                // Only switch view if we have tracks and no errors
                if (syncSuccess && CustomMusicManager.HybridTrackPaths.Count > 0)
                {
                    SwitchToViewPlaylist(PlaylistType.Hybrid);
                    Debug.Log($"Loaded Hybrid playlist with {CustomMusicManager.HybridTrackPaths.Count} tracks and switched view");
                    return;
                }
            }

            // If we get here, loading failed or no tracks
            Debug.LogWarning("Hybrid playlist load failed or empty - view not changed");
        }
        catch (Exception ex)
        {
            Debug.LogError($"Error loading Hybrid playlist: {ex}");
        }
    }


    private void LoadStreamsPlaylistAndSwitchView()
    {
        try
        {
            // Load from file
            bool loadSuccess = LandfallConfig.LoadStreamsPlaylistOnly();

            if (loadSuccess)
            {
                // Sync to manager
                bool syncSuccess = PlaylistBridge.SyncStreamsPlaylistToCustomMusicManager();

                // Only switch view if we have streams and no errors
                if (syncSuccess && CustomMusicManager.StreamsTrackPaths.Count > 0)
                {
                    SwitchToViewPlaylist(PlaylistType.Streams);
                    Debug.Log($"Loaded Streams playlist with {CustomMusicManager.StreamsTrackPaths.Count} streams and switched view");
                    return;
                }
            }

            // If we get here, loading failed or no streams
            Debug.LogWarning("Streams playlist load failed or empty - view not changed");
        }
        catch (Exception ex)
        {
            Debug.LogError($"Error loading Streams playlist: {ex}");
        }
    }



    private bool IsValidPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;

        // Basic validation - check if it looks like a file path or URL
        path = path.Trim();

        // Check for URL-like patterns
        if (path.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("ftp://", StringComparison.OrdinalIgnoreCase))
            return true;

        // Check for local file path patterns (basic check)
        if (path.Contains("/") || path.Contains("\\") || path.Contains("."))
            return true;


        return false;
    }

    private void AddToHybridPlaylist(string path)
    {
        if (!IsValidPath(path))
        {
            Debug.LogWarning($"Invalid path format: {path}");
            return;
        }

        try
        {
            CustomMusicManager.HybridTrackPaths.Add(path);

            // Update the playlist object
            CustomMusicManager.CreateHybridPlaylistFromTracks();

            // Get the display name for logging
            string displayName = PlaylistDecoder.GetTrackDisplayName(path);
            if (LandfallConfig.CurrentConfig.ShowDebug) Debug.Log($"Added track to Hybrid playlist: {displayName}");

            GUI.changed = true;
        }
        catch (Exception ex)
        {
            Debug.LogError($"Error adding track to Hybrid playlist: {ex}");
        }
    }

    private void PlayHybridTrackDirect(string path)
    {
        if (!IsValidPath(path))
        {
            Debug.LogWarning($"Invalid path format: {path}");
            return;
        }

        try
        {
            // Switch to Hybrid playlist type
            CustomMusicManager.CurrentPlaybackMethod = PlaybackMethod.Streaming;
            CustomMusicManager.CurrentPlaybackPlaylistType = PlaylistType.Hybrid;
            PlaylistManager.CurrentPlaylistType = PlaylistType.Hybrid;

            // Start streaming the track directly
            CustomMusicManager.StartStreaming(path);

            if (LandfallConfig.CurrentConfig.ShowDebug) Debug.Log($"Playing Hybrid track: {path}");
        }
        catch (Exception ex)
        {
            Debug.LogError($"Error playing Hybrid track: {ex}");
        }
    }

    private int GetPlayingIndexForViewed()
    {
        // Only highlight if we're viewing the same playlist that's playing
        if (CustomMusicManager.CurrentPlaybackPlaylistType != _viewingPlaylistType)
            return -1;

        // Get the appropriate playlist
        var playlist = PlaylistManager.GetPlaylist(_viewingPlaylistType);
        if (playlist == null) return -1;

        // For shuffled playlists, find display position of current track
        if (playlist.ShuffledOrder.Count > 0)
        {
            int currentTrackId = playlist.CurrentTrackIndex;
            return playlist.ShuffledOrder.IndexOf(currentTrackId);
        }
        else
        {
            // Natural order
            return playlist.CurrentTrackIndex;
        }
    }
    private void StartStreamFromTab()
    {
        if (string.IsNullOrEmpty(_customStreamPath))
        {
            Debug.LogWarning("Stream path is empty");
            return;
        }

        if (StreamingClip.TreatInputAsPlaylist)
        {
            // Load and decode playlist - it will handle clearing only if successful
            _ = CustomMusicManager.LoadStreamsPlaylist(_customStreamPath);

            // Auto-switch to view streams playlist after loading
            StartCoroutine(SwitchToStreamsAfterLoad());
        }
        else
        {
            // Direct stream connection - no clearing needed
            CustomMusicManager.StartStreaming(_customStreamPath);
            CustomMusicManager.CurrentPlaybackMethod = PlaybackMethod.Streaming;
            CustomMusicManager.CurrentPlaybackPlaylistType = PlaylistType.Streams;
        }
    }

    private System.Collections.IEnumerator SwitchToStreamsAfterLoad()
    {
        // Wait for loading to complete
        float timeout = Time.realtimeSinceStartup + 10f; // 10 second timeout
        while (CustomMusicManager.IsLoading && Time.realtimeSinceStartup < timeout)
        {
            yield return new WaitForSecondsRealtime(0.1f);
        }

        // Only switch if we successfully loaded tracks
        if (CustomMusicManager.StreamsTrackPaths.Count > 0)
        {
            SwitchToViewPlaylist(PlaylistType.Streams);
            if (LandfallConfig.CurrentConfig.ShowDebug) Debug.Log($"Switched to view streams playlist with {CustomMusicManager.StreamsTrackPaths.Count} tracks");
        }
    }
    void Update()
    {
        if (LandfallConfig.CurrentConfig.ToggleUIKey.IsDown())
            _showGUI = !_showGUI;

        if (LandfallConfig.CurrentConfig.NextTrackKey.IsDown())
            CustomMusicManager.PlayNextTrack();

        if (LandfallConfig.CurrentConfig.GamepadHotkeysEnabled && Gamepad.current != null)
            CheckXInputGamepad();

        HMPAnimation.UpdateAnimation();

        // Detect playlist changes
        var current = MusicPlayer.Instance?.currentlyPlaying?.playlist;
        if (current != _lastKnownPlaylist)
        {
            _lastKnownPlaylist = current;
        }

        // Auto-advance with safety checks
        if (CustomMusicManager.IsAnyCustomPlaylistActive &&
            CustomMusicManager.CurrentPlayOrder != CustomMusicManager.PlayOrder.Loop)
        {
            bool shouldAdvance = false;
            bool isStreamingMode = CustomMusicManagerExtensions.IsStreamPlaying();

            if (isStreamingMode)
            {
                // Use authoritative BASS-backed values
                float currentTime = CustomMusicManagerExtensions.GetStreamCurrentTime();
                float totalTime = CustomMusicManagerExtensions.GetStreamTotalTime();

                // Reset cooldown if the reported total changes significantly (prescan -> final length)
                if (Mathf.Abs(totalTime - _lastSeenStreamTotal) > 0.05f)
                {
                    _lastAutoAdvanceAt = 0f; // allow immediate re-evaluation after total changes
                    _lastSeenStreamTotal = totalTime;
                }

                // Require a trustworthy total before auto-advancing:
                // - totalTime > 0 and either HasRealLength or IsFullyDownloaded
                var stream = CustomMusicManager._streamingInstance;
                bool streamHasAuth = stream != null && (stream.HasRealLength || stream.IsFullyDownloaded);

                if (totalTime > 0f && streamHasAuth)
                {
                    // Advance if we're near the end (within 0.05s to be tolerant)
                    if (currentTime >= totalTime - 0.01f)
                    {
                        shouldAdvance = true;
                    }
                }
            }
            else
            {
                // Preloaded mode auto-advance (existing logic) with a small tolerance
                var player = MusicPlayer.Instance;
                if (player != null &&
                    player.m_AudioSourceCurrent != null &&
                    player.m_AudioSourceCurrent.clip != null)
                {
                    float clipLen = player.m_AudioSourceCurrent.clip.length;
                    // If clip length is tiny or invalid skip
                    if (clipLen > 0.26f)
                    {
                        float endThreshold = Mathf.Max(0.1f, clipLen - 0.26f);
                        if (player.m_AudioSourceCurrent.time >= endThreshold)
                        {
                            shouldAdvance = true;
                        }
                    }
                }
            }

            // Debounce / cooldown to avoid multiple calls across frames
            if (shouldAdvance)
            {
                if (Time.realtimeSinceStartup - _lastAutoAdvanceAt >= AutoAdvanceCooldown)
                {
                    _lastAutoAdvanceAt = Time.realtimeSinceStartup;
                    CustomMusicManager.PlayNextTrack();
                }
            }
        }

        // Reset double-click timer (unchanged)
        if (_selectedTrackIndex != -1 && Time.realtimeSinceStartup >= _singleClickExpireAt)
        {
            _selectedTrackIndex = -1;
            _lastClickedTrack = -1;
            _singleClickExpireAt = 0f;
        }
    }
    private string GetCurrentTrackNameFromPlaylist()
    {
        try
        {
            var currentPlaylist = PlaylistManager.CurrentPlaylist;
            if (currentPlaylist != null)
            {
                int currentTrackIndex = currentPlaylist.CurrentTrackIndex;
                if (currentTrackIndex >= 0 && currentTrackIndex < currentPlaylist.TrackCount)
                {
                    return currentPlaylist.GetTrackDisplayName(currentTrackIndex);
                }
            }

            // Fallback for Default playlist
            if (CustomMusicManager.CurrentPlaybackPlaylistType == PlaylistType.Default)
            {
                if (PlaylistManager.GetPlaylist(PlaylistType.Default) is DefaultPlaylist defaultPlaylist)
                {
                    return defaultPlaylist.GetTrackDisplayName(defaultPlaylist.CurrentTrackIndex);
                }
            }
        }
        catch (Exception ex)
        {
            Debug.LogError($"Error getting track name from playlist: {ex}");
        }

        return "Unknown Track";
    }

    private string FormatTime(float seconds)
    {
        int minutes = (int)(seconds / 60);
        int secs = (int)(seconds % 60);
        return $"{minutes}:{secs:00}";
    }
    private void CalculateUIScale()
    {
        float screenHeight = Screen.height;
        _uiScale = Mathf.Max(screenHeight / 1080f, 0.75f);

        // Optional: Add debug logging
        if (LandfallConfig.CurrentConfig.ShowDebug && Time.frameCount % 60 == 0)
        {
            Debug.Log($"UI Scale: {_uiScale} (Screen: {Screen.width}x{Screen.height})");
        }
    }
    private void CheckXInputGamepad()
    {
        // Safe gamepad check
        if (Gamepad.current == null) return;

        // D-pad Up: Toggle UI (Xbox controller D-pad up)
        if (Gamepad.current.dpad.up.wasPressedThisFrame)
        {
            _showGUI = !_showGUI;
        }

        // D-pad Down: Next Track (Xbox controller D-pad down)  
        if (Gamepad.current.dpad.down.wasPressedThisFrame)
        {
            CustomMusicManager.PlayNextTrack();
        }
    }
}