using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;

public class YouTubeSetupWindow : MonoBehaviour
{
    private GUIStyle _titleStyle;
    private GUIStyle _normalStyle;
    private GUIStyle _warningStyle;
    private GUIStyle _successStyle;
    private GUIStyle _infoStyle;
    private GUIStyle _codeStyle;
    public static YouTubeSetupWindow Instance { get; private set; }

    private bool _visible = false;
    private Rect _windowRect = new Rect(200, 100, 750, 770);

    // State
    private bool _batFilesCreated = false;
    private bool _checkPassed = false;
    private bool _updateSuccess = false;
    private bool _ableTryUpdate = false;
    private string _statusMessage = string.Empty;

    // Cursor state management
    private bool _wasCursorVisible;
    private CursorLockMode _previousCursorLockState;
    private bool _cursorStateForced = false;
    public static bool UpdateCompletedThisSession { get; private set; } = false;
    public static string StatusMessage => Instance?._statusMessage ?? string.Empty;
    public bool CanTryUpdate => _ableTryUpdate;

    // Dynamic UI scaling
    private float _uiScale = 1.0f;
    private Matrix4x4 _originalMatrix;

    private readonly Queue<Action> _mainThreadQueue = new Queue<Action>();
    private void InitializeStyles()
    {
        if (_titleStyle == null)
        {
            _titleStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 18,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleCenter,
                normal = { textColor = Color.yellow },
                richText = true
            };

            _normalStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 14,
                wordWrap = true,
                richText = true
            };

            _warningStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 14,
                wordWrap = true,
                richText = true,
                normal = { textColor = new Color(1f, 0.6f, 0f) }
            };

            _successStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 14,
                wordWrap = true,
                richText = true,
                normal = { textColor = Color.green }
            };

            _infoStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 14,
                wordWrap = true,
                richText = true,
                normal = { textColor = new Color(0.6f, 0.9f, 1f) }
            };

            _codeStyle = new GUIStyle(GUI.skin.textArea)
            {
                fontSize = 12,
                fontStyle = FontStyle.Normal,
                richText = true
            };
        }
    }
    void Awake()
    {
        if (Instance == null)
        {
            Instance = this;
            DontDestroyOnLoad(gameObject);
        }
        else
        {
            Destroy(gameObject);
        }
    }

    public static void Show()
    {
        if (Instance == null)
        {
            var go = new GameObject("YouTubeSetupWindow");
            Instance = go.AddComponent<YouTubeSetupWindow>();
            DontDestroyOnLoad(go);
        }
        Instance.SetVisible(true);
    }

    public static void Hide()
    {
        if (Instance != null) Instance.SetVisible(false);
    }

    private void SetVisible(bool visible)
    {
        if (_visible == visible) return;
        _visible = visible;

        if (_visible)
        {
            // Save current cursor state
            _wasCursorVisible = Cursor.visible;
            _previousCursorLockState = Cursor.lockState;
            ForceCursorState();
        }
        else
        {
            RestoreCursorState();
        }
    }

    void Update()
    {
        MainThreadDispatcher.Drain();
        // If hidden but cursor is still forced, restore it (e.g. window closed via button)
        if (!_visible && _cursorStateForced)
        {
            RestoreCursorState();
        }
    }

    void LateUpdate()
    {
        if (_visible)
        {
            // Force cursor visible and unlocked every frame while window is open
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

        if (!_visible)
        {
            GUI.matrix = _originalMatrix; // restore before early exit
            return;
        }

        ForceCursorState();

        try
        {
            _windowRect = GUI.Window(9998, _windowRect, DrawWindow, "YouTube Setup Guide");
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
    }

    void OnApplicationFocus(bool hasFocus)
    {
        if (hasFocus && _visible)
        {
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
    }

    private void CalculateUIScale()
    {
        float screenHeight = Screen.height;
        _uiScale = Mathf.Max(screenHeight / 1080f, 0.75f);
    }

    private void DrawWindow(int id)
    {
        InitializeStyles();

        GUILayout.BeginVertical();

        // Welcome message centered
        GUILayout.BeginHorizontal();
        GUILayout.FlexibleSpace();
        GUILayout.Label("Welcome to YouTube Playback Setup", _titleStyle);
        GUILayout.FlexibleSpace();
        GUILayout.EndHorizontal();

        GUILayout.Space(3);

        GUILayout.Label(
            "This guide will help you install yt-dlp and ffmpeg, which are required for YouTube streaming.",
            _normalStyle);
        GUILayout.BeginVertical(GUI.skin.box);
        GUILayout.Space(1);

        // Step 1
        GUILayout.Label("<color=#00FFFF>Step 1:</color> Create Setup .bat Files", _infoStyle);
        GUILayout.Label(
            "Click the button below to generate <color=#00FF00>setup_youtube_tools_user.bat</color> in your HasteCustomMusic folder.",
            _normalStyle);

        GUILayout.Space(1);

        // Step 2
        GUILayout.Label("<color=#00FFFF>Step 2:</color> Run the Setup Script", _infoStyle);
        GUILayout.Label(
            "Run <color=#00FF00>setup_youtube_tools_user.bat</color>. It will download the necessary tools into: \n <color=#FFA500>%APPDATA%\\HasteCustomMusic\\youtube_tools</color>.",
            _normalStyle);

        GUILayout.BeginVertical(GUI.skin.box);
        GUILayout.Label("The script performs these downloads:", _normalStyle);
        GUILayout.Label(
            "<color=#00FF00>powershell -NoProfile -Command \"try { irm https://deno.land/install.ps1 | iex }</color>\n" +
            "<color=#00FF00>curl -L https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe -o yt-dlp.exe</color>\n" +
            "<color=#00FF00>curl -L https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-win64-gpl.zip -o ffmpeg.zip</color>\n" +
            "<color=#00FF00>start %APPDATA%\\HasteCustomMusic\\youtube_tools</color>",
            _codeStyle);
        GUILayout.EndVertical();

        GUILayout.Space(1);

        // Step 3
        GUILayout.Label("<color=#00FFFF>Step 3:</color> Move the Downloaded Folders", _infoStyle);
        GUILayout.Label(
            "After the script finishes, open the folder that appears. Move the contents so that your HasteCustomMusic folder looks like this:",
            _normalStyle);

        GUILayout.BeginVertical(GUI.skin.box);
        GUILayout.Label(
            "<color=#00FFFF>\\HasteCustomMusic\\yt-dlp\\yt-dlp.exe</color>\n" +
            "<color=#00FFFF>\\HasteCustomMusic\\ffmpeg\\ffmpeg-master-latest-win64-gpl\\bin\\ffmpeg.exe</color>",
            _codeStyle);
        GUILayout.EndVertical();

        GUILayout.Space(1);

        // Step 4
        GUILayout.Label("<color=#00FFFF>Step 4:</color> Check Installation", _infoStyle);
        GUILayout.Label(
            "Click <color=#00FF00>Check Installation</color> below. If successful, the <color=#00FF00>Update yt-dlp</color> button will become available.",
            _normalStyle);

        GUILayout.Space(1);

        // Step 5
        GUILayout.Label("<color=#00FFFF>Step 5:</color> Update yt-dlp", _infoStyle);
        GUILayout.Label(
            "You must update yt-dlp before using it. Click <color=#00FF00>Update yt-dlp</color> to update to the latest nightly build.",
            _normalStyle);

        GUILayout.Space(1);

        // Step 6
        GUILayout.Label("<color=#00FFFF>Step 6:</color> Confirm", _infoStyle);
        GUILayout.Label(
            "When the update is complete, click <color=#00FF00>Confirm</color> to finish.",
            _normalStyle);

        GUILayout.Space(2);

        // Important warning
        GUILayout.Label(
            "<color=#FFA500>IMPORTANT: You must update yt-dlp before using it. This guide will enforce that.</color>",
            _warningStyle);
        GUILayout.EndVertical();
        GUILayout.Space(2);



        // Buttons
        GUILayout.BeginHorizontal();
        if (GUILayout.Button("Create Setup .bat Files", GUILayout.Height(30)))
        {
            _batFilesCreated = CreateBatFiles();
        }
        if (GUILayout.Button("Open Tools Folder", GUILayout.Height(30)))
        {
            Application.OpenURL($"file://{LandfallConfig.ConfigDirectory}");
        }
        GUILayout.EndHorizontal();

        GUILayout.Space(3);

        GUILayout.BeginHorizontal();
        if (GUILayout.Button("Check Installation", GUILayout.Height(30)))
        {
            _checkPassed = CheckYtDlp();
            _statusMessage = _checkPassed ? "yt-dlp.exe found. You can now update." : "yt-dlp.exe not found. Please complete the previous steps.";
        }
        GUI.enabled = _checkPassed && _ableTryUpdate;
        if (GUILayout.Button("Update yt-dlp", GUILayout.Height(30)))
        {
            UpdateYtDlp();
            
        }
        GUI.enabled = true;
        GUILayout.EndHorizontal();

        // Status message
        GUILayout.BeginHorizontal();
        GUILayout.FlexibleSpace();
        if (!string.IsNullOrEmpty(_statusMessage))
        {
            GUI.color = Color.green;
            GUILayout.Label(_statusMessage, _successStyle);
            GUI.color = Color.white;
            GUILayout.Space(5);
        }
        GUILayout.FlexibleSpace();
        GUILayout.EndHorizontal();

        GUILayout.FlexibleSpace();
        GUILayout.BeginHorizontal();
        GUI.enabled = _checkPassed;
        if (GUILayout.Button("Confirm", GUILayout.Height(30), GUILayout.Width(180)))
        {
            LandfallConfig.CurrentConfig.YouTubeSetupConfirmed = true;
            LandfallConfig.SaveConfig();
            _statusMessage = "Setup confirmed!";
            SetVisible(false);
        }
        GUI.enabled = true;
       
        GUILayout.FlexibleSpace();
        if (GUILayout.Button("Close", GUILayout.Height(30), GUILayout.Width(180)))
        {
            SetVisible(false);
        }

        GUILayout.EndHorizontal();

        GUILayout.EndVertical();
        GUI.DragWindow(new Rect(0, 0, _windowRect.width, 20));
    }

    private bool CreateBatFiles()
    {
        string message;
        bool success = YouTubeBatchGenerator.RecreateBatFiles(out message);
        _statusMessage = success ? "Batch files created successfully." : message;
        if (success)
        {
            Application.OpenURL($"file://{LandfallConfig.ConfigDirectory}");
        }
        return success;
    }

    public bool CheckYtDlp()
    {
        string path = Path.Combine(LandfallConfig.ConfigDirectory, "yt-dlp", "yt-dlp.exe");
        bool exist = File.Exists(path);
        _ableTryUpdate = exist;
        return exist;
    }

    private void UpdateYtDlp()
    {
        string ytDlpPath = Path.Combine(LandfallConfig.ConfigDirectory, "yt-dlp", "yt-dlp.exe");
        if (!File.Exists(ytDlpPath))
        {
            _statusMessage = "yt-dlp.exe not found. Cannot update.";
            return;
        }

        _statusMessage = "Updating yt-dlp, please wait...";
        YtDlpUpdater.UpdateAsync(ytDlpPath, (success, message) =>
        {
            _updateSuccess = success;
            _statusMessage = message;
        });
    }

    private void MainThreadInvoke(Action action)
    {
        lock (_mainThreadQueue)
        {
            _mainThreadQueue.Enqueue(action);
        }
    }
}