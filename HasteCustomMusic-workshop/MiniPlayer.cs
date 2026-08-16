using Landfall.Haste.Music;
using System.Collections;
using System.Drawing;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using static CustomMusicManager;
using static StreamingClip;
using Color = UnityEngine.Color;
using DrawingBitmap = System.Drawing.Bitmap;
using DrawingImage = System.Drawing.Image;
using DrawingImageLockMode = System.Drawing.Imaging.ImageLockMode;
using DrawingPixelFormat = System.Drawing.Imaging.PixelFormat;
using DrawingRectangle = System.Drawing.Rectangle;
using Image = UnityEngine.UI.Image;
using Object = UnityEngine.Object;

public class MiniPlayerManager : MonoBehaviour
{
    private float _smoothedProgress = 0f;
    private Texture2D _currentCoverTexture;
    private string _lastUnityTrackName;
    private static readonly Queue<Action> _mainThreadQueue = new Queue<Action>();
    private Coroutine _coverResetCoroutine;
    private string _currentTrackId = string.Empty;
    private bool _coverReceivedForCurrentTrack = false;
    private string _currentFilePath = string.Empty;
    private byte[] _pendingPictureData;
    private string _pendingPicturePath;
    private bool _lastUseIconFlag = false;
    private Coroutine _popupAnimationCoroutine;
    private bool _popupAnimationActive = false;
    private bool _popupEnabled = false;
    private string _lastRadioTitle = string.Empty;

    private void OnEnable()
    {
        _lastUseIconFlag = LandfallConfig.CurrentConfig.UseIconAsDefaultCover;
        _popupEnabled = LandfallConfig.CurrentConfig.MiniPlayerPopupEnabled;
        if (_popupEnabled)
            MiniPlayer.SetAnchoredPosition(MiniPlayer.GetOffscreenAnchoredPosition());
        else
            MiniPlayer.SetAnchoredPosition(MiniPlayer.GetTargetAnchoredPosition());
        StreamingClip.OnPictureChanged += HandlePictureChanged;
    }

    private void OnDisable()
    {
        StreamingClip.OnPictureChanged -= HandlePictureChanged;
    }

    private void Update()
    {
        // Drain main thread queue (actions from background threads)
        lock (_mainThreadQueue)
        {
            while (_mainThreadQueue.Count > 0)
            {
                var action = _mainThreadQueue.Dequeue();
                action?.Invoke();
            }
        }

        // Visibility
        bool miniPlayerVisible = LandfallConfig.CurrentConfig.MiniPlayerEnabled && !MusicDisplayBehaviour.ShowGUI;
        if (MiniPlayer.CanvasGO != null)
            MiniPlayer.CanvasGO.SetActive(miniPlayerVisible);

        if (!miniPlayerVisible)
            return;
        MiniPlayer.ApplyScaleAndOpacity(
            LandfallConfig.CurrentConfig.MiniPlayerScale,
            LandfallConfig.CurrentConfig.MiniPlayerOpacity);

        // Handle popup mode position
        bool popupEnabledNow = LandfallConfig.CurrentConfig.MiniPlayerPopupEnabled;
        if (popupEnabledNow != _popupEnabled)
        {
            _popupEnabled = popupEnabledNow;
            if (_popupAnimationCoroutine != null)
            {
                StopCoroutine(_popupAnimationCoroutine);
                _popupAnimationCoroutine = null;
                _popupAnimationActive = false;
            }
            if (_popupEnabled)
                MiniPlayer.SetAnchoredPosition(MiniPlayer.GetOffscreenAnchoredPosition());
            else
                MiniPlayer.SetAnchoredPosition(MiniPlayer.GetTargetAnchoredPosition());
        }

        if (!_popupEnabled)
        {
            // If popup disabled, always stay at target position
            if (!_popupAnimationActive)
                MiniPlayer.SetAnchoredPosition(MiniPlayer.GetTargetAnchoredPosition());
        }

        UpdateMiniPlayerFromPlaybackState();
        UpdateColorScheme();
        bool currentFlag = LandfallConfig.CurrentConfig.UseIconAsDefaultCover;
        if (currentFlag != _lastUseIconFlag)
        {
            _lastUseIconFlag = currentFlag;

            // Only update if we're currently showing a placeholder/default cover
            if (MiniPlayer.IsCoverPlaceholder)
            {
                MiniPlayer.ResetAlbumCoverToDefault();
            }
        }
    }

    // Static method to enqueue actions from background threads
    public static void MainThreadInvoke(Action action)
    {
        lock (_mainThreadQueue)
            _mainThreadQueue.Enqueue(action);
    }

    private void HandlePictureChanged(string filePath, byte[] pictureData)
    {
        if (string.IsNullOrEmpty(_currentFilePath) || filePath != _currentFilePath)
        {
            _pendingPicturePath = filePath;
            _pendingPictureData = pictureData;
            return;
        }

        ApplyPictureData(pictureData);
    }
    private void ApplyPictureData(byte[] pictureData)
    {
        _coverReceivedForCurrentTrack = true;
        if (_coverResetCoroutine != null)
        {
            StopCoroutine(_coverResetCoroutine);
            _coverResetCoroutine = null;
        }
        SetCoverFromPictureDataAsync(pictureData);
    }



    private void UpdateMiniPlayerFromPlaybackState()
    {
        switch (CustomMusicManager.CurrentPlaybackMethod)
        {
            case CustomMusicManager.PlaybackMethod.UnityAudio:
                UpdateUnityAudio();
                break;
            case CustomMusicManager.PlaybackMethod.Streaming:
                UpdateStreaming();
                break;
        }
    }

    private void UpdateUnityAudio()
    {
        var audioSource = MusicPlayer.Instance?.m_AudioSourceCurrent;
        if (audioSource == null || audioSource.clip == null)
            return;

        string trackName = audioSource.clip.name;
        if (trackName != _lastUnityTrackName)
        {
            _lastUnityTrackName = trackName;
            MiniPlayer.SetTrackName(Truncate(trackName, 50));
            OnNewTrackStarted(trackName);
        }

        MiniPlayer.SetArtist(string.Empty);
        MiniPlayer.SetAlbum(string.Empty);

        // Time and progress
        float currentTime = audioSource.time;
        float totalTime = audioSource.clip.length;
        MiniPlayer.SetTime($"{FormatTime(currentTime)} / {FormatTime(totalTime)}");

        float progress = (totalTime > Mathf.Epsilon) ? Mathf.Clamp01(currentTime / totalTime) : 0f;
        _smoothedProgress = Mathf.Lerp(_smoothedProgress, progress, Time.deltaTime * 8);
        MiniPlayer.SetProgress(_smoothedProgress);
    }

    private void OnNewTrackStarted(string trackIdentifier)
    {
        if (trackIdentifier == _currentFilePath)
            return;

        if (_popupEnabled)
        {
            TriggerPopupSlideIn();
        }

        _currentFilePath = trackIdentifier;
        _coverReceivedForCurrentTrack = false;

        // If a picture arrived earlier for this path, apply it now
        if (_pendingPicturePath != null && _pendingPicturePath == _currentFilePath && _pendingPictureData != null)
        {
            ApplyPictureData(_pendingPictureData);
            _pendingPicturePath = null;
            _pendingPictureData = null;
        }

        if (_coverResetCoroutine != null)
            StopCoroutine(_coverResetCoroutine);

        _coverResetCoroutine = StartCoroutine(ResetCoverAfterDelay(trackIdentifier, 2f));
    }

    private void TriggerPopupSlideIn()
    {
        if (_popupAnimationCoroutine != null)
            StopCoroutine(_popupAnimationCoroutine);

        _popupAnimationCoroutine = StartCoroutine(PopupAnimationCoroutine());
    }

    private IEnumerator PopupAnimationCoroutine()
    {
        _popupAnimationActive = true;

        Vector2 offscreen = MiniPlayer.GetOffscreenAnchoredPosition();
        Vector2 target = MiniPlayer.GetTargetAnchoredPosition();

        // Ensure start position
        MiniPlayer.SetAnchoredPosition(offscreen);

        // Phase 1: Slide in (ease-out)
        float slideInDuration = 3f;
        float elapsed = 0f;
        while (elapsed < slideInDuration)
        {
            elapsed += Time.deltaTime;
            float t = Mathf.Clamp01(elapsed / slideInDuration);
            // Ease-out: fast start, slow end
            float eased = 1f - Mathf.Pow(1f - t, 20f); 
            MiniPlayer.SetAnchoredPosition(Vector2.Lerp(offscreen, target, eased));
            yield return null;
        }
        MiniPlayer.SetAnchoredPosition(target);

        // Seconds to stay visible
        yield return new WaitForSeconds(4f);

        // Phase 2: Slide out (ease-in)
        float slideOutDuration = 5f;
        elapsed = 0f;
        while (elapsed < slideOutDuration)
        {
            elapsed += Time.deltaTime;
            float t = Mathf.Clamp01(elapsed / slideOutDuration);
            // Ease-in: slow start, fast end
            float eased = MathF.Pow(t,15f);
            MiniPlayer.SetAnchoredPosition(Vector2.Lerp(target, offscreen, eased));
            yield return null;
        }
        MiniPlayer.SetAnchoredPosition(offscreen);

        _popupAnimationActive = false;
        _popupAnimationCoroutine = null;
    }

    private IEnumerator ResetCoverAfterDelay(string trackIdentifier, float delay)
    {
        yield return new WaitForSeconds(delay);

        if (_currentFilePath == trackIdentifier && !_coverReceivedForCurrentTrack)
        {
            MiniPlayer.ResetAlbumCoverToDefault();
        }
    }

    private void UpdateStreaming()
    {
        if (!CustomMusicManagerExtensions.IsStreamPlaying())
            return;

        StreamingClip sc = StreamingClip.Instance;
        if (sc == null) return;

        // Detect new track by file path change
        string currentPath = sc.CurrentPath;
        if (!string.IsNullOrEmpty(currentPath) && currentPath != _currentFilePath)
            OnNewTrackStarted(currentPath);

        // Update text fields
        string title = StreamingClip.CurrentStreamTitle;
        if (string.IsNullOrWhiteSpace(title))
            title = sc.PublicTrackTitle;
        bool isRadioStream = (StreamingClip.CurrentPlaybackMode == MusicPlayerMode.RadioStream);
        if (isRadioStream)
        {
            if (!string.IsNullOrWhiteSpace(title) && title != _lastRadioTitle)
            {
                _lastRadioTitle = title;
                if (_popupEnabled)
                    TriggerPopupSlideIn();
            }
        }
        else
        {
            _lastRadioTitle = string.Empty; 
        }
        if (!string.IsNullOrWhiteSpace(title))
            MiniPlayer.SetTrackName(Truncate(title, 50));
        MiniPlayer.SetArtist(StreamingClip.CurrentStreamArtist ?? string.Empty);
        MiniPlayer.SetAlbum(StreamingClip.CurrentStreamAlbum ?? string.Empty);

        // Time and progress

        float currentTime = CustomMusicManagerExtensions.GetStreamCurrentTime();
        float totalTime = CustomMusicManagerExtensions.GetStreamTotalTime();

        string totalDisplay = isRadioStream ? "Live" : FormatTime(totalTime);
        MiniPlayer.SetTime($"{FormatTime(currentTime)} / {totalDisplay}");

        float progress = (totalTime > Mathf.Epsilon) ? Mathf.Clamp01(currentTime / totalTime) : 0f;
        _smoothedProgress = Mathf.Lerp(_smoothedProgress, progress, Time.deltaTime * 8);
        MiniPlayer.SetProgress(_smoothedProgress);
    }

    // ---- Asynchronous cover loading ----
    private void SetCoverFromPictureDataAsync(byte[] pictureData)
    {
        if (pictureData == null || pictureData.Length == 0)
            return;

        Task.Run(() =>
        {
            try
            {
                using (var ms = new MemoryStream(pictureData))
                using (DrawingImage original = DrawingImage.FromStream(ms))
                {
                    const int targetSize = 140;
                    int width, height;

                    using (DrawingBitmap resized = new DrawingBitmap(original, new Size(targetSize, targetSize)))
                    {
                        width = resized.Width;
                        height = resized.Height;
                        var rect = new DrawingRectangle(0, 0, width, height);
                        var bmpData = resized.LockBits(rect, DrawingImageLockMode.ReadOnly, DrawingPixelFormat.Format32bppArgb);
                        int stride = Math.Abs(bmpData.Stride);
                        int bytes = stride * height;
                        byte[] rgba = new byte[bytes];
                        System.Runtime.InteropServices.Marshal.Copy(bmpData.Scan0, rgba, 0, bytes);
                        resized.UnlockBits(bmpData);

                        // Flip vertically (bottom‑up to top‑down)
                        byte[] flipped = new byte[rgba.Length];
                        for (int y = 0; y < height; y++)
                        {
                            int srcY = height - 1 - y;
                            Buffer.BlockCopy(rgba, srcY * stride, flipped, y * stride, stride);
                        }
                        rgba = flipped;

                        // Convert BGRA to RGBA
                        for (int i = 0; i < rgba.Length; i += 4)
                        {
                            byte b = rgba[i];
                            byte g = rgba[i + 1];
                            byte r = rgba[i + 2];
                            byte a = rgba[i + 3];
                            rgba[i] = r;
                            rgba[i + 1] = g;
                            rgba[i + 2] = b;
                            rgba[i + 3] = a;
                        }

                        // Send to main thread
                        MainThreadInvoke(() =>
                        {
                            var tex = new Texture2D(width, height, TextureFormat.RGBA32, false);
                            tex.LoadRawTextureData(rgba);
                            tex.Apply();

                            MiniPlayer.SetAlbumCover(tex);
                            _currentCoverTexture = tex;

                            if (LandfallConfig.CurrentConfig.MiniPlayerUseCoverColor)
                            {
                                Color avg = MiniPlayer.ComputeAverageColor(tex);
                                MiniPlayer.ApplyColorSchemeFromAccent(avg);
                            }
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"Cover decode error: {ex.Message}");
            }
        });
    }

    // ---- Color scheme update ----
    private void UpdateColorScheme()
    {
        if (!LandfallConfig.CurrentConfig.MiniPlayerEnabled) return;

        bool useCoverColor = LandfallConfig.CurrentConfig.MiniPlayerUseCoverColor && !MiniPlayer.IsCoverPlaceholder && _currentCoverTexture != null;

        if (useCoverColor)
        {
            Color avg = MiniPlayer.ComputeAverageColor(_currentCoverTexture);
            MiniPlayer.ApplyColorSchemeFromAccent(avg);
            MiniPlayer.ApplyAlphaOverrides(isCustom: false);
            return;
        }

        switch (LandfallConfig.CurrentConfig.MiniPlayerColorScheme)
        {
            case 0: // Default
                MiniPlayer.ApplyDefaultColors();
                MiniPlayer.ApplyAlphaOverrides(isCustom: false);
                break;

            case 1: // Rainbow
                float hue = (Time.time * 0.01f) % 1f;
                Color accent = Color.HSVToRGB(hue, 0.6f, 0.8f);
                MiniPlayer.ApplyColorSchemeFromAccent(accent);
                MiniPlayer.ApplyAlphaOverrides(isCustom: false);
                break;

            case 2: // Custom
                MiniPlayer.ApplyCustomColors(
                    LandfallConfig.CurrentConfig.MiniPlayerBackgroundColor,
                    LandfallConfig.CurrentConfig.MiniPlayerBackgroundColor,
                    new Color(0f, 0f, 0f, 0.3f),
                    LandfallConfig.CurrentConfig.MiniPlayerSliderColor,
                    LandfallConfig.CurrentConfig.MiniPlayerFontColor);
                MiniPlayer.ApplyAlphaOverrides(isCustom: true, LandfallConfig.CurrentConfig.MiniPlayerFontColor);
                break;
        }
    }

    // ---- Helpers ----
    private static string FormatTime(float seconds)
    {
        int totalSecs = Mathf.FloorToInt(seconds);
        int mins = totalSecs / 60;
        int secs = totalSecs % 60;
        return $"{mins:00}:{secs:00}";
    }

    private static string Truncate(string text, int maxLength)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= maxLength)
            return text;
        return text[..maxLength];
    }
}
public static class MiniPlayer
{
    private static GameObject _canvasGO;
    public static GameObject CanvasGO => _canvasGO;
    private static Image _backgroundImage;
    private static Image _albumCoverImage;
    private static Image _albumBorderCover;
    private static Image _progressBarBackground;
    private static RectTransform _progressBarFillRect;
    private static Image _progressBarFillImage;
    private static TextMeshProUGUI _albumText, _nameText, _artistText, _timeText;
    private static CanvasGroup _canvasGroup;
    private static RectTransform _bgRect;

    private static Material _backgroundMaterial;
    private static Material _progressBarMaterial;
    private static Material _coverBorderMaterial;
    private static Material _backProgressBarMaterial;
    private static TMP_FontAsset _font;
    private static Texture2D _currentAlbumTexture;
    // Default colors
    public static Color DefaultBackgroundColor = new Color(1f, 0.3f, 0.5f, 0.7f);
    public static Color DefaultCoverBorderColor = Color.white;
    public static Color DefaultProgressBackgroundColor = new Color(0, 0, 0, 0.3f);
    public static Color DefaultProgressFillColor = Color.white;
    public static Color DefaultFontColor = Color.white;
    private static Color _currentFontColor = Color.white;
    private static Coroutine _fontLerpCoroutine;
    private static MonoBehaviour _coroutineRunner;

    private static bool _isCoverPlaceholder = true;
    public static bool IsCoverPlaceholder => _isCoverPlaceholder;
    private static string ModDirectory => WorkshopHelper.ModDirectory;
    private static string DefaultCoverPath => Path.Combine(ModDirectory, "HasteMusicModLogo.png");
    private static string IconCoverPath => Path.Combine(LandfallConfig.ConfigDirectory, "ICON.png");
    private static Texture2D _defaultLogoTex;
    private static Texture2D _defaultIconTex;
    private static bool Colored = false;


    static void awake()
    {
    }

    public static void Create()
    {
        _canvasGO = new GameObject("HasteCustomMusic_MiniPlayerCanvas", typeof(Canvas), typeof(CanvasScaler));
        Object.DontDestroyOnLoad(_canvasGO);

        var canvas = _canvasGO.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 9999;

        var scaler = _canvasGO.GetComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920, 1080);
        scaler.matchWidthOrHeight = 0.5f;

        BuildUIStructure(_canvasGO.transform);
        _defaultLogoTex = LoadTextureFromFile(Path.Combine(WorkshopHelper.ModDirectory, "HasteMusicModLogo.png"));
        _defaultIconTex = LoadTextureFromFile(Path.Combine(LandfallConfig.ConfigDirectory, "ICON.png"));

        // If logo fails, create a gray placeholder
        if (_defaultLogoTex == null)
        {
            _defaultLogoTex = CreatePlaceholderTexture();
        }
        // If icon fails, fallback to logo
        if (_defaultIconTex == null)
            _defaultIconTex = _defaultLogoTex;

        var finderGO = new GameObject("HasteCustomMusic_MiniPlayerAssetFinder");
        Object.DontDestroyOnLoad(finderGO);
        var finder = finderGO.AddComponent<AssetFinder>();
        finder.StartSearching();
        _coroutineRunner = finder;
        ApplyDefaultColors();
    }

    private static void BuildUIStructure(Transform canvasTransform)
    {
        // Background
        var bgGO = new GameObject("Background", typeof(RectTransform), typeof(Image));
        bgGO.transform.SetParent(canvasTransform, false);
        var bgRect = bgGO.GetComponent<RectTransform>();
        bgRect.anchorMin = new Vector2(0, 1);
        bgRect.anchorMax = new Vector2(0, 1);
        bgRect.pivot = new Vector2(0, 0);
        bgRect.anchoredPosition = new Vector2(20, -350);
        bgRect.sizeDelta = new Vector2(400, 100);
        _bgRect = bgRect;
        _canvasGroup = bgGO.AddComponent<CanvasGroup>();
        _canvasGroup.alpha = 1f;
        _canvasGroup.interactable = false;
        _canvasGroup.blocksRaycasts = false;

        _backgroundImage = bgGO.GetComponent<Image>();
        _backgroundImage.raycastTarget = false;
        var placeholderSprite = CreatePlaceholderSprite();
        _backgroundImage.sprite = placeholderSprite;
        _backgroundImage.material = null;


        // ===== Album Cover Border  =====
        var coverBorderGO = new GameObject("CoverBorder", typeof(RectTransform), typeof(Image));
        coverBorderGO.transform.SetParent(bgGO.transform, false);
        var coverBorderRect = coverBorderGO.GetComponent<RectTransform>();
        coverBorderRect.anchorMin = Vector2.zero;
        coverBorderRect.anchorMax = Vector2.zero;
        coverBorderRect.pivot = Vector2.zero;
        coverBorderRect.anchoredPosition = new Vector2(0, 0);
        coverBorderRect.sizeDelta = new Vector2(150, 150);
        _albumBorderCover = coverBorderGO.GetComponent<Image>();
        _albumBorderCover.raycastTarget = false;

        // Album cover
        var albumGO = new GameObject("AlbumCover", typeof(RectTransform), typeof(Image));
        albumGO.transform.SetParent(bgGO.transform, false);
        var albumRect = albumGO.GetComponent<RectTransform>();
        albumRect.anchorMin = Vector2.zero;
        albumRect.anchorMax = Vector2.zero;
        albumRect.pivot = Vector2.zero;
        albumRect.anchoredPosition = new Vector2(5, 5);
        albumRect.sizeDelta = new Vector2(140, 140);
        _albumCoverImage = albumGO.GetComponent<Image>();
        _albumCoverImage.raycastTarget = false;
        ResetAlbumCoverToDefault();
        coverBorderGO.transform.SetSiblingIndex(0);
        albumGO.transform.SetSiblingIndex(1);

        // Progress bar (on top of album cover)
        var progressBgGO = new GameObject("ProgressBarBackground", typeof(RectTransform), typeof(Image));
        progressBgGO.transform.SetParent(bgGO.transform, false);
        var progressBgRect = progressBgGO.GetComponent<RectTransform>();
        progressBgRect.anchorMin = Vector2.zero;
        progressBgRect.anchorMax = Vector2.zero;
        progressBgRect.pivot = Vector2.zero;
        progressBgRect.anchoredPosition = new Vector2(3, 3);
        progressBgRect.sizeDelta = new Vector2(394, 14);
        _progressBarBackground = progressBgGO.GetComponent<Image>();
        _progressBarBackground.raycastTarget = false;


        // Fill rectangle (stretches horizontally)
        var progressFillGO = new GameObject("ProgressBarFill", typeof(RectTransform), typeof(Image));
        progressFillGO.transform.SetParent(bgGO.transform, false);
        var progressFillRect = progressFillGO.GetComponent<RectTransform>();
        progressFillRect.anchorMin = Vector2.zero;
        progressFillRect.anchorMax = Vector2.zero;
        progressFillRect.pivot = Vector2.zero;
        progressFillRect.anchoredPosition = new Vector2(5, 5);
        progressFillRect.sizeDelta = new Vector2(0, 10);
        _progressBarFillRect = progressFillRect;
        _progressBarFillImage = progressFillGO.GetComponent<Image>();
        _progressBarFillImage.raycastTarget = false;

        progressBgGO.transform.SetSiblingIndex(2);
        progressFillGO.transform.SetSiblingIndex(3);

        // --- Text Elements (updated) ---
        _albumText = CreateText(
            parent: bgGO.transform,
            initialText: "Album",
            anchoredPos: new Vector2(150, 140),
            pivot: new Vector2(0, 1),
            alignment: TMPro.TextAlignmentOptions.TopLeft,
            fontSize: 26,
            color: Color.white,
            size: new Vector2(240, 30)
        );
        _albumText.characterSpacing = -0.5f;
        _albumText.enableAutoSizing = true;
        _albumText.fontSizeMin = 14;
        _albumText.fontSizeMax = 36;

        _nameText = CreateText(
            parent: bgGO.transform,
            initialText: "Name",
            anchoredPos: new Vector2(150, 97),
            pivot: new Vector2(0, 1),
            alignment: TMPro.TextAlignmentOptions.TopLeft,
            fontSize: 18,
            color: Color.white,
            size: new Vector2(240, 30)
        );
        _nameText.characterSpacing = -0.5f;
        _nameText.enableAutoSizing = true;
        _nameText.fontSizeMin = 12;
        _nameText.fontSizeMax = 30;

        _artistText = CreateText(
            parent: _nameText.transform,
            initialText: "Artist",
            anchoredPos: new Vector2(5, -5),
            pivot: new Vector2(0, 1),
            alignment: TMPro.TextAlignmentOptions.TopLeft,
            fontSize: 16,
            color: Color.white,
            size: new Vector2(240, 30)
        );
        _artistText.characterSpacing = -0.5f;
        _artistText.enableAutoSizing = true;
        _artistText.fontSizeMin = 10;
        _artistText.fontSizeMax = 26;

        _timeText = CreateText(
            parent: bgGO.transform,
            initialText: "00:00/00:00",
            anchoredPos: new Vector2(390, 10),
            pivot: new Vector2(1, 0),
            alignment: TMPro.TextAlignmentOptions.Right,
            fontSize: 18,
            color: Color.white,
            size: new Vector2(150, 30)
        );
    }

    private static TextMeshProUGUI CreateText(
        Transform parent,
        string initialText,
        Vector2 anchoredPos,
        Vector2 pivot,
        TMPro.TextAlignmentOptions alignment,
        int fontSize,
        Color color,
        Vector2 size)
    {
        var textGO = new GameObject(initialText, typeof(RectTransform), typeof(TextMeshProUGUI));
        textGO.transform.SetParent(parent, false);

        var text = textGO.GetComponent<TextMeshProUGUI>();
        text.text = initialText;
        text.fontSize = fontSize;
        text.color = color;
        text.alignment = alignment;
        text.raycastTarget = false;
        text.fontStyle = FontStyles.Bold;

        if (_font != null)
            text.font = _font;

        var outline = textGO.AddComponent<Outline>();
        outline.effectColor = Color.black;
        outline.effectDistance = new Vector2(1, -1);

        var rect = textGO.GetComponent<RectTransform>();
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.zero;
        rect.pivot = pivot;
        rect.anchoredPosition = anchoredPos;
        rect.sizeDelta = size;

        return text;
    }

    public static void SetTrackName(string newName)
    {
        if (_nameText != null)
            _nameText.text = SanitizeText(newName);
    }

    public static void SetAlbum(string albumName)
    {
        if (_albumText != null)
            _albumText.text = SanitizeText(albumName);
    }

    public static void SetArtist(string artistName)
    {
        if (_artistText != null)
            _artistText.text = SanitizeText(artistName);
    }

    public static void SetTime(string timeString)
    {
        if (_timeText != null)
            _timeText.text = timeString;
    }



    public static void SetProgress(float progress01)
    {
        if (_progressBarFillRect == null) return;
        float clamped = Mathf.Clamp01(progress01);
        // Max width available is 390 (the original fill area)
        float width = clamped * 390f;
        _progressBarFillRect.sizeDelta = new Vector2(width, 10);
    }

    public static void SetAlbumCover(Texture2D texture)
    {
        if (_albumCoverImage == null || texture == null) return;
        _isCoverPlaceholder = false;

        if (_currentAlbumTexture != null && _currentAlbumTexture != texture)
            Object.Destroy(_currentAlbumTexture);
        _currentAlbumTexture = texture;

        var sprite = Sprite.Create(texture, new Rect(0, 0, texture.width, texture.height), new Vector2(0.5f, 0.5f));
        _albumCoverImage.sprite = sprite;
        _albumCoverImage.color = Color.white;
    }

    private static Sprite CreatePlaceholderSprite()
    {
        var tex = new Texture2D(1, 1);
        tex.SetPixel(0, 0, Color.white);
        tex.Apply();
        return Sprite.Create(tex, new Rect(0, 0, 1, 1), Vector2.one * 0.5f);
    }

    public static void ResetAlbumCoverToDefault()
    {
        _isCoverPlaceholder = true;
        Texture2D selectedTex = LandfallConfig.CurrentConfig.UseIconAsDefaultCover ? _defaultIconTex : _defaultLogoTex;
        if (selectedTex == null) selectedTex = _defaultLogoTex; // safety fallback

        if (selectedTex == null) return;

        _albumCoverImage.sprite = Sprite.Create(selectedTex, new Rect(0, 0, selectedTex.width, selectedTex.height), new Vector2(0.5f, 0.5f));
        _albumCoverImage.color = Color.white;
    }


    private class AssetFinder : MonoBehaviour
    {
        public void StartSearching() => StartCoroutine(SearchLoop());

        private IEnumerator SearchLoop()
        {
            while (_backgroundMaterial == null || _progressBarMaterial == null || _font == null || _coverBorderMaterial == null || _backProgressBarMaterial == null)
            {
                if (_backgroundMaterial == null)
                {
                    _backgroundMaterial = FindMaterialByName("M_UI_ItemTooltip_Legendary");
                    if (_backgroundMaterial != null) ApplyBackgroundMaterial();
                }

                if (_progressBarMaterial == null)
                {
                    _progressBarMaterial = FindMaterialByName("M_UI_SkinSelectionBackground");
                    if (_progressBarMaterial != null) ApplyProgressBarMaterial();
                }

                if (_coverBorderMaterial == null)
                {
                    _coverBorderMaterial = FindMaterialByName("M_UI_ItemTooltip_Legendary");
                    if (_coverBorderMaterial != null) ApplyCoverBorderMaterial();
                }

                if (_backProgressBarMaterial == null)
                {
                    _backProgressBarMaterial = FindMaterialByName("M_UI_SkinSelectionBackground");
                    if (_backProgressBarMaterial != null) ApplyBackProgressBarMaterial();
                }

                if (_font == null)
                {
                    _font = FindFontByName("AkzidenzGroteskPro-Bold SDF");
                    if (_font != null) ApplyFont();
                }

                yield return new WaitForSeconds(1f);
            }
            Debug.Log("MiniPlayer: All assets found.");
        }

        private void ApplyBackgroundMaterial()
        {
            if (_backgroundImage == null || _backgroundMaterial == null) return;
            _backgroundImage.sprite = null;
            _backgroundImage.material = _backgroundMaterial;
        }
        private void ApplyCoverBorderMaterial()
        {
            if (_albumBorderCover == null || _coverBorderMaterial == null) return;
            _albumBorderCover.sprite = null;
            _albumBorderCover.material = _coverBorderMaterial;
        }

        private void ApplyProgressBarMaterial()
        {
            if (_progressBarFillImage == null || _progressBarMaterial == null) return;
            _progressBarFillImage.material = _progressBarMaterial;
        }
        private void ApplyBackProgressBarMaterial()
        {
            if (_progressBarBackground == null || _backProgressBarMaterial == null) return;
            _progressBarBackground.material = _backProgressBarMaterial;
        }

        private void ApplyFont()
        {
            if (_font == null) return;
            if (_albumText != null) _albumText.font = _font;
            if (_nameText != null) _nameText.font = _font;
            if (_artistText != null) _artistText.font = _font;
            if (_timeText != null) _timeText.font = _font;
        }
    }

    public static void ApplyTransformSettings(float posX, float posY, float scale, float opacity)
    {
        if (_bgRect != null)
        {
            _bgRect.anchoredPosition = new Vector2(posX, -posY); // posY is distance from top
            _bgRect.localScale = Vector3.one * scale;
        }
        if (_canvasGroup != null)
        {
            _canvasGroup.alpha = opacity;
        }
    }

    private static Material FindMaterialByName(string namePart)
    {
        foreach (var mat in Resources.FindObjectsOfTypeAll<Material>())
            if (mat != null && mat.name.Contains(namePart))
                return mat;
        return null;
    }

    private static TMP_FontAsset FindFontByName(string namePart)
    {
        foreach (var font in Resources.FindObjectsOfTypeAll<TMP_FontAsset>())
            if (font.name.Contains(namePart))
                return font;
        return null;
    }

    public static void ApplyCustomColors(
        Color? background,
        Color? coverBorder,
        Color? progressBackground,
        Color? progressFill,
        Color? font)
    {
        _backgroundImage.color = background.Value;
        _albumBorderCover.color = coverBorder.Value;
        _progressBarBackground.color = progressBackground.Value;
        _progressBarFillImage.color = progressFill.Value;
        ApplyFontColor(font);
    }
    public static void ApplyDefaultColors()
    {
        ApplyCustomColors(
            DefaultBackgroundColor,
            DefaultCoverBorderColor,
            DefaultProgressBackgroundColor,
            DefaultProgressFillColor,
            DefaultFontColor);
        _currentFontColor = DefaultFontColor;
    }
    public static void ApplyFontColor(Color? color)
    {
        if (!color.HasValue) return;
        _currentFontColor = color.Value;
        if (_albumText != null) _albumText.color = color.Value;
        if (_nameText != null) _nameText.color = color.Value;
        if (_artistText != null) _artistText.color = color.Value;
        if (_timeText != null) _timeText.color = color.Value;
    }

    private static IEnumerator FontLerpCoroutine(Color target)
    {
        float duration = 0.3f; // seconds
        float elapsed = 0f;
        Color startColor = _currentFontColor;

        while (elapsed < duration)
        {
            elapsed += Time.deltaTime;
            float t = Mathf.Clamp01(elapsed / duration);
            Color newColor = Color.Lerp(startColor, target, t);
            ApplyFontColor(newColor);
            yield return null;
        }
        ApplyFontColor(target);
        _fontLerpCoroutine = null;
    }

    private static void StartFontLerp(Color target)
    {
        if (_coroutineRunner == null)
        {
            ApplyFontColor(target);
            return;
        }

        if (_fontLerpCoroutine != null)
            _coroutineRunner.StopCoroutine(_fontLerpCoroutine);

        _fontLerpCoroutine = _coroutineRunner.StartCoroutine(FontLerpCoroutine(target));
    }

    public static Color ComputeAverageColor(Texture2D texture, int samplesPerAxis = 16)
    {
        if (texture == null) return Color.white;

        int width = texture.width;
        int height = texture.height;
        int stepX = Mathf.Max(1, width / samplesPerAxis);
        int stepY = Mathf.Max(1, height / samplesPerAxis);

        float r = 0f, g = 0f, b = 0f;
        int count = 0;

        for (int y = 0; y < height; y += stepY)
        {
            for (int x = 0; x < width; x += stepX)
            {
                Color pixel = texture.GetPixel(x, y);
                r += pixel.r;
                g += pixel.g;
                b += pixel.b;
                count++;
            }
        }

        if (count == 0) return Color.white;
        return new Color(r / count, g / count, b / count);
    }
    private static float GetLuminance(Color c)
    {
        return 0.2126f * c.r + 0.7152f * c.g + 0.0722f * c.b;
    }

    private static Color WithAlpha(Color c, float alpha)
    {
        return new Color(c.r, c.g, c.b, alpha);
    }
    public static void ApplyColorSchemeFromAccent(Color accent)
    {
        Color contrast = _albumText.color;
        Color background = Color.Lerp(accent, contrast, 0.15f);
        Color font = GetToxicFontColor(background);

        // Apply all colors except font; font is smoothed separately
        ApplyCustomColors(
            background,
            accent,
            WithAlpha(contrast, 0.3f),
            contrast,
            null          // font handled by StartFontLerp below
        );
        StartFontLerp(font);


    }
    private static string SanitizeText(string input)
    {
        if (string.IsNullOrEmpty(input))
            return input;

        // Remove Unicode replacement character
        string cleaned = input.Replace("\uFFFD", string.Empty);
        cleaned = cleaned.Replace("\uFEFF", string.Empty);

        // Remove other control characters (keeping common line breaks if ever needed)
        cleaned = new string(cleaned.Where(c => !char.IsControl(c) || c == '\n' || c == '\r' || c == '\t').ToArray());

        // Trim leading/trailing whitespace
        return cleaned.Trim();
    }

    private static readonly Color[] ToxicCandidates = new Color[]
    {
        new Color(1f, 0f, 0.15f),    // hot red
        new Color(1f, 0.4f, 0f),     // orange
        new Color(1f, 0.95f, 0f),    // yellow
        new Color(0.15f, 1f, 0.3f),  // neon green
        new Color(0f, 1f, 1f),       // cyan
        new Color(0.6f, 0f, 1f)      // purple (bright enough, not dark blue)
    };

    private static float ColorDistanceSquared(Color a, Color b)
    {
        float dr = a.r - b.r;
        float dg = a.g - b.g;
        float db = a.b - b.b;
        return dr * dr + dg * dg + db * db;
    }

    private static float CalculateContrastRatio(Color a, Color b)
    {
        float lumA = GetLuminance(a);
        float lumB = GetLuminance(b);
        float lighter = Mathf.Max(lumA, lumB);
        float darker = Mathf.Min(lumA, lumB);
        return (lighter + 0.05f) / (darker + 0.05f);
    }

    private static Color GetToxicFontColor(Color background)
    {
        Color best = ToxicCandidates[0];
        float bestScore = float.MinValue;

        foreach (var candidate in ToxicCandidates)
        {
            float contrast = CalculateContrastRatio(background, candidate);
            float distance = ColorDistanceSquared(background, candidate);

            // Penalize candidates that are too similar in luminance (poor contrast)
            float score = contrast * 0.4f + distance * 0.6f;
            if (contrast < 1.5f) score -= 100f;

            if (score > bestScore)
            {
                bestScore = score;
                best = candidate;
            }
        }
        return best;
    }

    public static void ApplyAlphaOverrides(bool isCustom, Color? customFontColor = null)
    {
        if (!isCustom)
        {
            if (_backgroundImage != null)
                _backgroundImage.color = new Color(_backgroundImage.color.r, _backgroundImage.color.g, _backgroundImage.color.b, 0.7f);
            if (_albumBorderCover != null)
                _albumBorderCover.color = new Color(_albumBorderCover.color.r, _albumBorderCover.color.g, _albumBorderCover.color.b, 0.7f);
            if (_albumCoverImage != null)
                _albumCoverImage.color = new Color(_albumCoverImage.color.r, _albumCoverImage.color.g, _albumCoverImage.color.b, 0.95f);
            if (_progressBarFillImage != null)
                _progressBarFillImage.color = new Color(_progressBarFillImage.color.r, _progressBarFillImage.color.g, _progressBarFillImage.color.b, 0.85f);
            if (_progressBarBackground != null)
                _progressBarBackground.color = new Color(_progressBarBackground.color.r, _progressBarBackground.color.g, _progressBarBackground.color.b, 0.3f);
        }

        // Text alphas
        if (isCustom)
        {
            Color fontColor = customFontColor ?? Color.white;
            float albumAlpha = fontColor.a * 0.85f;
            SetTextAlpha(albumAlpha, forAlbum: true);
            SetTextAlpha(fontColor.a, forAlbum: false);
        }
        else
        {
            SetTextAlpha(0.85f, forAlbum: true);
            SetTextAlpha(1f, forAlbum: false);
        }
    }

    private static void SetTextAlpha(float alpha, bool forAlbum)
    {
        if (forAlbum)
        {
            if (_albumText != null)
                _albumText.color = new Color(_albumText.color.r, _albumText.color.g, _albumText.color.b, alpha);
        }
        else
        {
            if (_nameText != null)
                _nameText.color = new Color(_nameText.color.r, _nameText.color.g, _nameText.color.b, alpha);
            if (_artistText != null)
                _artistText.color = new Color(_artistText.color.r, _artistText.color.g, _artistText.color.b, alpha);
            if (_timeText != null)
                _timeText.color = new Color(_timeText.color.r, _timeText.color.g, _timeText.color.b, alpha);
        }
    }
    private static Texture2D LoadTextureFromFile(string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            var bytes = File.ReadAllBytes(path);
            var tex = new Texture2D(2, 2);
            if (tex.LoadImage(bytes))
                return tex;
            return null;
        }
        catch { return null; }
    }

    private static Texture2D CreatePlaceholderTexture()
    {
        var tex = new Texture2D(1, 1);
        tex.SetPixel(0, 0, Color.gray);
        tex.Apply();
        return tex;
    }

    public static Vector2 GetTargetAnchoredPosition()
    {
        return new Vector2(
            LandfallConfig.CurrentConfig.MiniPlayerPositionX,
            -LandfallConfig.CurrentConfig.MiniPlayerPositionY);
    }

    public static Vector2 GetOffscreenAnchoredPosition()
    {
        float scale = LandfallConfig.CurrentConfig.MiniPlayerScale;
        float offscreenX = -(_bgRect.sizeDelta.x * scale) - 50f; // width + margin
        return new Vector2(offscreenX, -LandfallConfig.CurrentConfig.MiniPlayerPositionY);
    }

    public static void SetAnchoredPosition(Vector2 pos)
    {
        if (_bgRect != null)
            _bgRect.anchoredPosition = pos;
    }

    public static void ApplyScaleAndOpacity(float scale, float opacity)
    {
        if (_bgRect != null)
            _bgRect.localScale = Vector3.one * scale;
        if (_canvasGroup != null)
            _canvasGroup.alpha = opacity;
    }

}