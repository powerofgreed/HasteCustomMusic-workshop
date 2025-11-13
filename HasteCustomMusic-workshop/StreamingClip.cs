// StreamingClip.cs
// ManagedBass v4.0.2 integrated streaming clip for Unity.
// - Stereo-first mixer policy with downmix for odd channel counts (3,5,7 -> 2)
// - Uses TagReader when available, falls back to ChannelGetTags for ICY/META
// - Aggressive prescan watch for .opus/.ogg: accept after 70% download or authoritative length
// - Encapsulated HLS support via P/Invoke when needed
// - Minimal main-thread work; heavy probing runs off-main thread

using Landfall.Haste.Music;
using ManagedBass;
using ManagedBass.Mix;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using UnityEngine;

public class StreamingClip : MonoBehaviour
{
    // HLS add-on P/Invoke (encapsulated)
    private static class BassHls
    {
        [DllImport("basshls", CharSet = CharSet.Ansi)]
        public static extern int BASS_HLS_StreamCreateURL(string url, BassFlags flags, IntPtr proc, IntPtr user);
    }

    // --- Playback mode (mirrors earlier code expectations) ---
    public enum MusicPlayerMode
    {
        None,
        PlaylistPreload,
        PlaylistOnDemand,
        UrlFinite,
        RadioStream
    }
    public enum PathType { Unknown, LocalFile, HttpHttps, HlsM3U8, Ftp, PlaylistFile }  
    public static MusicPlayerMode CurrentPlaybackMode { get; set; } = MusicPlayerMode.None;

    // --- Public state & events ---
    public static event Action<string> OnTitleChanged;
    public volatile string PublicTrackTitle;
    public string LastKnownTitle => PublicTrackTitle;

    public float CurrentTime { get; private set; } = 0f;
    public float TotalTime { get; private set; } = 0f;
    public bool IsPlaying => _source != null && _source.isPlaying;
    public bool IsFullyDownloaded => _isFullyDownloaded;
    public bool HasRealLength => _hasRealLength;
    public bool HasRecordedRealLength => _hasRecordedRealLength;

    // --- Internal state ---
    private int _stream = 0;
    private int _mixer = 0;
    private int _deviceRate = 48000;
    private int _decoderChannels = 2;
    private int _mixerChannels = 2;
    private AudioClip _clip;
    private AudioSource _source;
    private string _currentPath;
    private float _lastMetaCheck = 0f;

    // prescan/watch
    private long _initialPrescanLengthBytes = 0;
    private bool _watchingForRealLength = false;
    private bool _hasRecordedRealLength = false;
    private bool _hasRealLength = false;
    private bool _isFullyDownloaded = false;
    private const float PrescanAcceptThreshold = 0.70f;
    private float _watchStartTime = 0f;
    private readonly float _watchTimeoutSeconds = 8f;
    private float _lastObservedDownloadPercent = 0f;
    private float _watchLastProgressTime = 0f;
    private readonly float _watchProgressStallSeconds = 3f;

    // misc
    private volatile bool _suppressOnPCMSetPosition = false;
    private bool _usingDummyClip;
    private const int DummyMinutes = 15;
    private readonly Queue<Action> _mainThreadQueue = new Queue<Action>();
    private float _lastTitleEmitTime = 0f;
    private const float TitleEmitDebounce = 0.5f;

    // safety
    private static readonly object _initLock = new object();
    private static bool _bassInitialized = false;
    private static bool _pluginsLoaded = false;

    // public toggle used elsewhere
    public static bool TreatInputAsPlaylist { get; set; } = false;

    // --- Unity lifecycle ---
    private void Awake()
    {
        Instance = this;
        _deviceRate = AudioSettings.outputSampleRate > 0 ? AudioSettings.outputSampleRate : 48000;
    }

    private void Update()
    {
        DrainMainThreadQueue();
        PollForLengthChange();

        if (Time.realtimeSinceStartup - _lastMetaCheck > 0.5f)
        {
            _lastMetaCheck = Time.realtimeSinceStartup;
            UpdateMetadata(false);
        }

        if (_stream != 0 && IsPlaying)
        {
            try
            {
                long pos = Bass.ChannelGetPosition(_stream, PositionFlags.Bytes);
                if (pos > 0) CurrentTime = (float)Bass.ChannelBytes2Seconds(_stream, pos);
                else if (_source != null && _source.clip != null) CurrentTime = _source.time;
            }
            catch { }
        }
    }

    // --- Main-thread invoker ---
    private void MainThreadInvoke(Action a)
    {
        if (a == null) return;
        lock (_mainThreadQueue) _mainThreadQueue.Enqueue(a);
    }

    private void DrainMainThreadQueue()
    {
        Action a = null;
        lock (_mainThreadQueue) if (_mainThreadQueue.Count > 0) a = _mainThreadQueue.Dequeue();
        while (a != null)
        {
            try { a(); } catch (Exception e) { Debug.LogWarning($"[StreamingClip] MainThread action error: {e}"); }
            lock (_mainThreadQueue) a = _mainThreadQueue.Count > 0 ? _mainThreadQueue.Dequeue() : null;
        }
    }

    // --- External instance convenience ---
    public static StreamingClip Instance { get; private set; }

    // --- Public API used by CustomMusicManager ---
    public void StartStreamAsync(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) { Debug.LogWarning("[StreamingClip] StartStreamAsync: empty path"); return; }

        try { StopStream(); } catch { }

        _currentPath = path;
        CurrentTime = 0f;
        _usingDummyClip = false;
        _watchingForRealLength = false;
        _hasRecordedRealLength = false;
        _hasRealLength = false;
        _isFullyDownloaded = false;
        PublicTrackTitle = null;

        Task.Run(async () =>
        {
            ReadResult rr = default;
            try
            {
                int handle = await CreateStreamBackground(path).ConfigureAwait(false);
                rr.StreamHandle = handle;
                rr.Path = path;

                var (format, lengthSeconds, lengthBytes, tags, isRadio) = ReadStreamFormatAndLength(handle, path);
                rr.Format = format;
                rr.LengthSeconds = lengthSeconds;
                rr.LengthBytes = lengthBytes;
                rr.Tags = tags;
                rr.IsRadio = isRadio;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[StreamingClip] Background create error: {ex}");
                rr.StreamHandle = 0;
            }

            MainThreadInvoke(() =>
            {
                try
                {
                    if (rr.StreamHandle == 0)
                    {
                        Debug.LogWarning("[StreamingClip] Background stream creation failed");
                        return;
                    }

                    FinishStartStream(rr);
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[StreamingClip] FinishStartStream error: {ex}");
                }
            });
        });
    }
    // --- Helpers for title emission ---
    private void SetClipTitle(string title, bool sourceIsStream)
    {
        if (string.IsNullOrWhiteSpace(title)) return;
        string candidate = title.Trim();
        if (candidate.Length < 3) return;

        // Filter obvious encoder noise
        string lc = candidate.ToLowerInvariant();
        var noisyTokens = new[] { "encoder=", "lavc", "libopus", "ffmpeg", "libvorbis" };
        foreach (var tok in noisyTokens) if (lc.Contains(tok)) return;

        // ICY key=value guard
        if (candidate.Contains("=") && candidate.Split('=').Length == 2 && candidate.Length < 64)
            return;

        bool isChange = !string.Equals(PublicTrackTitle, candidate, StringComparison.Ordinal);
        bool allowOverwrite = sourceIsStream || string.IsNullOrEmpty(PublicTrackTitle);

        if (!isChange && Time.realtimeSinceStartup - _lastTitleEmitTime < TitleEmitDebounce) return;
        if (!allowOverwrite && !isChange) return;

        PublicTrackTitle = candidate;
        _lastTitleEmitTime = Time.realtimeSinceStartup;

        Debug.Log($"[StreamingClip] Title {(sourceIsStream ? "stream" : "tags")} -> {candidate}");
        MainThreadInvoke(() => { OnTitleChanged?.Invoke(candidate); GUI.changed = true; });
    }


    public void StopStream()
    {
        _currentPath = null;
        try { _source?.Stop(); } catch { }
        if (_mixer != 0) { try { Bass.StreamFree(_mixer); } catch { } _mixer = 0; }
        if (_stream != 0) { try { Bass.StreamFree(_stream); } catch { } _stream = 0; }
        if (_clip != null) { try { Destroy(_clip); } catch { } _clip = null; }
        PublicTrackTitle = null;
        _hasRealLength = false;
        _isFullyDownloaded = false;
    }

    public void Seek(float timeInSeconds)
    {
        if (_stream == 0) { Debug.LogWarning("[StreamingClip] Seek ignored: no active stream"); return; }

        try
        {
            long posBytes = Bass.ChannelSeconds2Bytes(_stream, timeInSeconds);
            if (!Bass.ChannelSetPosition(_stream, posBytes))
            {
                Debug.LogWarning($"[StreamingClip] Seek failed: {Bass.LastError}");
                return;
            }

            try { float[] prime = new float[1024]; Bass.ChannelGetData(_mixer, prime, prime.Length * sizeof(float)); } catch { }

            MainThreadInvoke(() =>
            {
                try
                {
                    if (_source != null && _source.clip != null)
                    {
                        _suppressOnPCMSetPosition = true;
                        _source.time = Mathf.Clamp(timeInSeconds, 0f, _source.clip.length);
                    }
                }
                catch { }
            });

            if (LandfallConfig.CurrentConfig.ShowDebug) Debug.Log($"[StreamingClip] Seek -> {timeInSeconds:F2}s (BASS)");
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[StreamingClip] Seek exception: {ex}");
        }
    }

    public float QueryBassCurrentSeconds()
    {
        if (_stream == 0) return 0f;
        try
        {
            long pos = Bass.ChannelGetPosition(_stream, PositionFlags.Bytes);
            if (pos > 0) return (float)Bass.ChannelBytes2Seconds(_stream, pos);
        }
        catch { }
        return CurrentTime;
    }

    public float QueryBassTotalSeconds()
    {
        // Force "Live" presentation for radio
        if (CurrentPlaybackMode == MusicPlayerMode.RadioStream)
            return 0f;

        if (_hasRealLength || _isFullyDownloaded)
            return TotalTime;

        if (_stream == 0) return TotalTime;

        try
        {
            long len = Bass.ChannelGetLength(_stream, PositionFlags.Bytes);
            if (len > 0) return (float)Bass.ChannelBytes2Seconds(_stream, len);

            long end = Bass.StreamGetFilePosition(_stream, FileStreamPosition.End);
            if (end > 0) return (float)Bass.ChannelBytes2Seconds(_stream, end);
        }
        catch { }

        return TotalTime;
    }

    public float GetBufferPercent()
    {
        if (_stream == 0) return 0f;
        try
        {
            long dl = Bass.StreamGetFilePosition(_stream, FileStreamPosition.Download);
            long end = Bass.StreamGetFilePosition(_stream, FileStreamPosition.End);
            if (end > 0 && dl >= 0) return Math.Clamp((float)dl / end, 0f, 1f);
        }
        catch { }
        return 0f;
    }

    // --- Internal helpers & read loop ---

    private struct ReadResult
    {
        public int StreamHandle;
        public string Path;
        public string Format;
        public float LengthSeconds;
        public long LengthBytes;
        public string Tags;
        public bool IsRadio;
    }
    private (string format, float lengthSeconds, long lengthBytes, string tags, bool isRadio) ReadStreamFormatAndLength(int streamHandle, string path)
    {
        string format = "unknown";
        long lengthBytes = 0;
        float lengthSeconds = 0f;
        string tags = null;
        bool isRadio = false;

        if (streamHandle == 0) return (format, lengthSeconds, lengthBytes, tags, isRadio);

        try
        {
            // EARLY RADIO DETECTION - Check basic indicators immediately
            bool isHttp = path.StartsWith("http", StringComparison.OrdinalIgnoreCase)|| path.StartsWith("ftp", StringComparison.OrdinalIgnoreCase);


            if (isHttp)
            {
                // Quick radio detection using basic BASS checks
                try
                {
                    long endPos = Bass.StreamGetFilePosition(streamHandle, FileStreamPosition.End);
                    long bassLength = Bass.ChannelGetLength(streamHandle, PositionFlags.Bytes);

                    bool hasNoKnownEnd = (endPos <= 0);
                    bool hasInvalidBassLength = (bassLength <= 0);
                    bool hasSmallEndPosition = (endPos > 0 && endPos < 1024 * 1024);

                    // Early radio detection - be more aggressive for URL streams
                    if (hasNoKnownEnd && hasInvalidBassLength)
                    {
                        isRadio = true;
                        if (LandfallConfig.CurrentConfig.ShowDebug) Debug.Log($"[StreamingClip] Early radio detection: No known end + invalid BASS length");
                    }
                    else if (hasSmallEndPosition && hasInvalidBassLength)
                    {
                        isRadio = true;
                        if (LandfallConfig.CurrentConfig.ShowDebug) Debug.Log($"[StreamingClip] Early radio detection: Small buffer ({endPos} bytes) + invalid BASS length");
                    }
                }
                catch { }
            }

            // Continue with existing tag reading and ICY detection...
            var tagReader = TagReader.Read(streamHandle);

            if (tagReader != null && !string.IsNullOrWhiteSpace(tagReader.Title))
            {
                tags = string.IsNullOrWhiteSpace(tagReader.Artist)
                    ? tagReader.Title
                    : $"{tagReader.Artist} - {tagReader.Title}";
            }
            try
            {
                IntPtr pmeta = Bass.ChannelGetTags(streamHandle, TagType.META);
                if (pmeta != IntPtr.Zero)
                {
                    string meta = Marshal.PtrToStringAnsi(pmeta);
                    var icy = ParseIcyStreamTitle(meta);
                    if (!string.IsNullOrWhiteSpace(icy))
                    {
                        tags = icy;
                        if (!isRadio)
                        {
                            isRadio = true;
                            if (LandfallConfig.CurrentConfig.ShowDebug) Debug.Log($"[StreamingClip] Radio confirmed: ICY metadata");
                        }
                        format = "icy";
                    }
                }
            }
            catch { }

            // If we haven't detected radio yet, do more thorough checks
            if (!isRadio && isHttp)
            {
                try
                {
                    long endPos = Bass.StreamGetFilePosition(streamHandle, FileStreamPosition.End);
                    long downloadPos = Bass.StreamGetFilePosition(streamHandle, FileStreamPosition.Download);
                    long bassLength = Bass.ChannelGetLength(streamHandle, PositionFlags.Bytes);

                    bool hasNoKnownEnd = (endPos <= 0);
                    bool hasInvalidBassLength = (bassLength <= 0);
                    bool hasSmallEndPosition = (endPos > 0 && endPos < 1024 * 1024);
                    bool hasVerySmallEndPosition = (endPos > 0 && endPos < 300 * 1024);
                    bool hasContinuousDownload = (downloadPos > 0 && endPos > 0 && downloadPos < endPos);

                    // Final radio determination
                    if (hasNoKnownEnd && hasInvalidBassLength)
                    {
                        isRadio = true;
                        if (LandfallConfig.CurrentConfig.ShowDebug) Debug.Log($"[StreamingClip] Radio detected: No known end + invalid BASS length");
                    }
                    else if (hasVerySmallEndPosition && hasInvalidBassLength)
                    {
                        isRadio = true;
                        if (LandfallConfig.CurrentConfig.ShowDebug) Debug.Log($"[StreamingClip] Radio detected: Very small buffer ({endPos} bytes) + invalid BASS length");
                    }
                    else if (hasSmallEndPosition && hasContinuousDownload && hasInvalidBassLength)
                    {
                        isRadio = true;
                        if (LandfallConfig.CurrentConfig.ShowDebug) Debug.Log($"[StreamingClip] Radio detected: Small buffer + continuous download + invalid length");
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[StreamingClip] Stream analysis error: {ex.Message}");
                }
            }


            // Get length information
            try
            {
                lengthBytes = Bass.ChannelGetLength(streamHandle, PositionFlags.Bytes);
                if (lengthBytes <= 0)
                {
                    lengthBytes = Bass.StreamGetFilePosition(streamHandle, FileStreamPosition.End);
                }

                if (lengthBytes > 0)
                {
                    lengthSeconds = (float)Bass.ChannelBytes2Seconds(streamHandle, lengthBytes);
                }
            }
            catch { }


        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[StreamingClip] ReadStreamFormatAndLength error: {ex.Message}");
        }

        return (format, lengthSeconds, lengthBytes, tags, isRadio);
    }

    private void FinishStartStream(ReadResult read)
    {
        _stream = read.StreamHandle;
        string path = read.Path;
        if (_stream == 0) return;

        if (_currentPath != path)
        {
            try { Bass.StreamFree(_stream); } catch { }
            _stream = 0;
            return;
        }

        if (LandfallConfig.CurrentConfig.ShowDebug) Debug.Log("[StreamingClip] StartStream: finishing on main thread");

        // Syncs for metadata changes (safe duplicates)
        try { Bass.ChannelSetSync(_stream, SyncFlags.Downloaded, 0, DownloadedSyncProc, IntPtr.Zero); } catch { }
        try
        {
            Bass.ChannelSetSync(_stream, SyncFlags.MetadataReceived, 0, (h, c, d, u) =>
            {
                MainThreadInvoke(() => { try { UpdateMetadata(force: true); } catch { } });
            }, IntPtr.Zero);
        }
        catch { }

        // Enhanced playback mode decision with better radio detection
        bool isUrl = path.StartsWith("http", StringComparison.OrdinalIgnoreCase)|| path.StartsWith("ftp", StringComparison.OrdinalIgnoreCase);

        if (read.IsRadio)
        {
            CurrentPlaybackMode = MusicPlayerMode.RadioStream;
            if (LandfallConfig.CurrentConfig.ShowDebug) Debug.Log($"[StreamingClip] Setting RadioStream mode - Confirmed radio stream");
        }
        else if (isUrl)
        {
            CurrentPlaybackMode = MusicPlayerMode.UrlFinite;
            if (LandfallConfig.CurrentConfig.ShowDebug) Debug.Log($"[StreamingClip] Setting UrlFinite mode - Remote file with known length");

            // Additional verification for URL finite files
            try
            {
                long endPos = Bass.StreamGetFilePosition(_stream, FileStreamPosition.End);
                long bassLength = Bass.ChannelGetLength(_stream, PositionFlags.Bytes);

                if (endPos <= 0 && bassLength <= 0)
                {
                    // This might actually be radio, re-classify
                    CurrentPlaybackMode = MusicPlayerMode.RadioStream;
                    read.IsRadio = true; 
                    if (LandfallConfig.CurrentConfig.ShowDebug) Debug.Log($"[StreamingClip] Re-classified as RadioStream - URL with no detectable length");
                }
            }
            catch { }
        }
        else
        {
            // Local files
            CurrentPlaybackMode = MusicPlayerMode.UrlFinite;
            if (LandfallConfig.CurrentConfig.ShowDebug) Debug.Log($"[StreamingClip] Setting UrlFinite mode - Local file");
        }

        // CRITICAL: Set radio mode immediately for proper clip creation
        if (read.IsRadio)
        {
            CurrentPlaybackMode = MusicPlayerMode.RadioStream;
        }

        var chInfo = Bass.ChannelGetInfo(_stream);
        _decoderChannels = Math.Max(1, chInfo.Channels);
        _mixerChannels = (_decoderChannels == 1) ? 1 : ((_decoderChannels % 2) == 1 ? 2 : Math.Min(8, _decoderChannels));

        // Create mixer
        _mixer = BassMix.CreateMixerStream(_deviceRate, _mixerChannels, BassFlags.Decode | BassFlags.Float);
        if (_mixer == 0 && _mixerChannels != 2)
        {
            _mixerChannels = 2;
            _mixer = BassMix.CreateMixerStream(_deviceRate, _mixerChannels, BassFlags.Decode | BassFlags.Float);
        }
        if (_mixer == 0) throw new Exception($"BassMix.CreateMixerStream failed: {Bass.LastError}");

        // Add stream to mixer
        BassFlags addFlags = BassFlags.MixerChanNoRampin;
        if (_decoderChannels != _mixerChannels) addFlags |= BassFlags.MixerChanDownMix;
        if (!BassMix.MixerAddChannel(_mixer, _stream, addFlags))
            throw new Exception($"MixerAddChannel failed: {Bass.LastError}");

        _source = MusicPlayer.Instance?.m_AudioSourceCurrent ?? throw new Exception("MusicPlayer has no current AudioSource!");

        // ENHANCED CLIP SIZING LOGIC - FIX FOR RADIO INTERRUPTION
        int frames = GetClipFramesForStreamUsingRead(read, _stream, _deviceRate);
        bool isUrlStream = path.StartsWith("http", StringComparison.OrdinalIgnoreCase) || path.StartsWith("ftp", StringComparison.OrdinalIgnoreCase);
        string ext = Path.GetExtension(path)?.ToLowerInvariant() ?? "";
        bool looksOpusLike = ext == ".opus" || ext == ".ogg";

        // Determine if we need a dummy clip and for how long
        bool needsDummyClip = false;
        int dummyMinutes = DummyMinutes; // Default 15 minutes

        if (read.IsRadio)
        {
            // RADIO STREAMS: Always use 60-minute dummy clip to prevent interruptions
            needsDummyClip = true;
            dummyMinutes = 60; // 60 minutes for radio
            if (LandfallConfig.CurrentConfig.ShowDebug) Debug.Log($"[StreamingClip] Radio stream detected - using {dummyMinutes}-minute dummy clip");
        }
        else if (isUrlStream && looksOpusLike)
        {
            // REMOTE OGG/OPUS: Use 15-minute dummy clip (existing behavior)
            needsDummyClip = true;
            dummyMinutes = DummyMinutes; // 15 minutes
            if (LandfallConfig.CurrentConfig.ShowDebug) Debug.Log($"[StreamingClip] Remote OGG/Opus stream - using {dummyMinutes}-minute dummy clip");
        }
        else if (isUrlStream && frames < _deviceRate * 60 * 5) // Less than 5 minutes
        {
            // Other URL streams with suspiciously short length - use 15-minute dummy
            needsDummyClip = true;
            dummyMinutes = DummyMinutes; // 15 minutes
            if (LandfallConfig.CurrentConfig.ShowDebug) Debug.Log($"[StreamingClip] Short URL stream ({frames / _deviceRate:F0}s) - using {dummyMinutes}-minute dummy clip");
        }

        if (needsDummyClip)
        {
            int desiredFrames = _deviceRate * 60 * dummyMinutes;
            if (frames < desiredFrames)
            {
                frames = desiredFrames;
                _usingDummyClip = true;
                _watchingForRealLength = true;
                _initialPrescanLengthBytes = read.LengthBytes > 0 ? read.LengthBytes : 0;
                _watchStartTime = Time.realtimeSinceStartup;
                _watchLastProgressTime = _watchStartTime;
                _lastObservedDownloadPercent = 0f;

                if (LandfallConfig.CurrentConfig.ShowDebug) Debug.Log($"[StreamingClip] Created {dummyMinutes}-minute dummy clip ({frames} frames) to prevent interruptions");
            }
        }

        frames = Math.Max(_deviceRate, frames);
        if (CurrentPlaybackMode == MusicPlayerMode.RadioStream)
        {
            // Prime the mixer with a small prebuffer to avoid early underruns
            try
            {
                // Wait until some data is available from the stream (up to ~200 ms)
                float startWait = Time.realtimeSinceStartup;
                while (Time.realtimeSinceStartup - startWait < 0.05f)
                {
                    int avail = 0;
                    try { avail = Bass.ChannelGetData(_stream, IntPtr.Zero, (int)DataFlags.Available); } catch { }
                    if (avail > (_mixerChannels * sizeof(float) * _deviceRate * 50 / 1000)) // ~50ms PCM threshold
                        break;
                }

                // Pull a few frames through the mixer to fill its internal buffers
                float[] warm = new float[_mixerChannels * 1024];
                for (int i = 0; i < 3; i++)
                {
                    try { Bass.ChannelGetData(_mixer, warm, warm.Length * sizeof(float)); } catch { }
                }
            }
            catch { }
        }
        _clip = AudioClip.Create(SafeClipName(path), frames, _mixerChannels, _deviceRate, true, OnPCMRead, OnPCMSetPosition);
        _source.clip = _clip;
        _source.loop = true;
        _source.playOnAwake = false;
        _source.volume = 1f;
        _source.Play();

        // Emit initial title (fallbacks allowed)
        var initialTitle = !string.IsNullOrWhiteSpace(read.Tags) ? read.Tags : SafeClipName(path);
        SetClipTitle(initialTitle, sourceIsStream: isUrl);

        if (LandfallConfig.CurrentConfig.ShowDebug)
        {
            DebugDumpHeadTags(_stream);
        }

        UpdateMetadata(force: true);
    }

    private int GetClipFramesForStreamUsingRead(ReadResult read, int stream, int deviceRate)
    {
        // If it's a radio stream, return a minimum of 1 hour immediately
        if (read.IsRadio)
        {
            if (LandfallConfig.CurrentConfig.ShowDebug) Debug.Log($"[StreamingClip] Radio stream - using minimum 1-hour clip frames");
            return deviceRate * 60 * 60; // 1 hour minimum for radio
        }

        try
        {
            if (read.LengthBytes > 0)
            {
                double secs = Bass.ChannelBytes2Seconds(stream, read.LengthBytes);
                int frames = Math.Max(deviceRate, (int)(secs * deviceRate));
                if (LandfallConfig.CurrentConfig.ShowDebug) Debug.Log($"[StreamingClip] Using ReadResult length: {secs:F2}s -> {frames} frames");
                return frames;
            }

            long lenBytes = Bass.ChannelGetLength(stream, PositionFlags.Bytes);
            if (lenBytes > 0)
            {
                double secs = Bass.ChannelBytes2Seconds(stream, lenBytes);
                int frames = Math.Max(deviceRate, (int)(secs * deviceRate));
                if (LandfallConfig.CurrentConfig.ShowDebug) Debug.Log($"[StreamingClip] Using BASS Channel length: {secs:F2}s -> {frames} frames");
                return frames;
            }

            long end = Bass.StreamGetFilePosition(stream, FileStreamPosition.End);
            if (end > 0)
            {
                double secs = Bass.ChannelBytes2Seconds(stream, end);
                int frames = Math.Max(deviceRate, (int)(secs * deviceRate));
                if (LandfallConfig.CurrentConfig.ShowDebug) Debug.Log($"[StreamingClip] Using stream end position: {secs:F2}s -> {frames} frames");
                return frames;
            }
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[StreamingClip] GetClipFrames error: {ex.Message}");
        }

        // Fallback for unknown length - use 15 minutes
        if (LandfallConfig.CurrentConfig.ShowDebug) Debug.Log($"[StreamingClip] Unknown length - using 15-minute fallback");
        return deviceRate * 60 * 15;
    }

    // --- Audio callbacks ---

    private void OnPCMRead(float[] data)
    {
        if (_mixer == 0)
        {
            Array.Clear(data, 0, data.Length);
            return;
        }

        int bytesNeeded = data.Length * sizeof(float);
        int bytesRead;
        try { bytesRead = Bass.ChannelGetData(_mixer, data, bytesNeeded); } catch { bytesRead = 0; }

        if (bytesRead <= 0)
        {
            Array.Clear(data, 0, data.Length);
            if (LandfallConfig.CurrentConfig.ShowDebug) if (Time.frameCount % 300 == 0) Debug.Log($"[StreamingClip] Audio underrun: needed={bytesNeeded} got={bytesRead}");
            return;
        }

        int floatsRead = Math.Max(0, bytesRead / sizeof(float));
        if (floatsRead < data.Length)
        {
            for (int i = floatsRead; i < data.Length; i++) data[i] = 0f;
        }
    }

    private void OnPCMSetPosition(int position)
    {
        try
        {
            if (_suppressOnPCMSetPosition)
            {
                _suppressOnPCMSetPosition = false;
                return;
            }

            double seconds = (double)position / _deviceRate;

            bool likelySeekable = false;
            try
            {
                long lenBytes = Bass.ChannelGetLength(_stream, PositionFlags.Bytes);
                if (lenBytes > 0) likelySeekable = true;
                if (_hasRealLength) likelySeekable = true;
            }
            catch { likelySeekable = false; }

            if (!likelySeekable) return;
            MainThreadInvoke(() => Seek((float)seconds));
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[StreamingClip] OnPCMSetPosition error: {ex}");
        }
    }

    // --- Downloaded sync ---

    private void DownloadedSyncProc(int handle, int channel, int data, IntPtr user)
    {
        MainThreadInvoke(() => HandleDownloadedSync(handle));
    }

    private void HandleDownloadedSync(int handle)
    {
        // RADIO PROTECTION
        if (CurrentPlaybackMode == MusicPlayerMode.RadioStream)
        {
            if (LandfallConfig.CurrentConfig.ShowDebug) Debug.Log($"[StreamingClip] Ignoring DownloadedSync for radio stream");
            return;
        }
        try
        {
            long finalLenBytes = 0;
            try { finalLenBytes = Bass.ChannelGetLength(handle, PositionFlags.Bytes); } catch { finalLenBytes = 0; }

            if (finalLenBytes <= 0)
            {
                long end = Bass.StreamGetFilePosition(handle, FileStreamPosition.End);
                long dl = Bass.StreamGetFilePosition(handle, FileStreamPosition.Download);
                finalLenBytes = Math.Max(end, dl);
            }

            if (finalLenBytes > 0)
            {
                double secs = Bass.ChannelBytes2Seconds(handle, finalLenBytes);
                TotalTime = (float)secs;
                _isFullyDownloaded = true;
                _hasRealLength = true;
                _hasRecordedRealLength = true;
                _watchingForRealLength = false;
                _initialPrescanLengthBytes = 0;
                if (LandfallConfig.CurrentConfig.ShowDebug) Debug.Log($"[StreamingClip] DownloadedSync final length: {secs:F3}s (bytes={finalLenBytes})");

                try
                {
                    long pos = Bass.ChannelGetPosition(_stream, PositionFlags.Bytes);
                    if (pos > 0)
                    {
                        float now = (float)Bass.ChannelBytes2Seconds(_stream, pos);
                        if (_source != null && _source.clip != null) _source.time = Mathf.Clamp(now, 0f, TotalTime);
                    }
                }
                catch { }

                MainThreadInvoke(() => { OnTitleChanged?.Invoke(PublicTrackTitle); });
            }
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[StreamingClip] HandleDownloadedSync error: {ex.Message}");
        }
    }

    // --- Polling for length changes (prescan watch) ---

    private float _lastLengthPoll = 0f;
    private const float LengthPollInterval = 0.2f;

    private void PollForLengthChange()
    {
        if (_stream == 0) return;
        if (CurrentPlaybackMode == MusicPlayerMode.RadioStream && _hasRealLength)
        {
            // Once radio, always radio - ignore any finite length discoveries
            return;
        }
        if (Time.realtimeSinceStartup - _lastLengthPoll < LengthPollInterval) return;
        _lastLengthPoll = Time.realtimeSinceStartup;

        try
        {
            long dl = Bass.StreamGetFilePosition(_stream, FileStreamPosition.Download);
            long end = Bass.StreamGetFilePosition(_stream, FileStreamPosition.End);
            float downloadPercent = 0f;
            if (end > 0 && dl >= 0) downloadPercent = Math.Clamp((float)dl / end, 0f, 1f);

            long lenBytes = 0;
            try { lenBytes = Bass.ChannelGetLength(_stream, PositionFlags.Bytes); } catch { lenBytes = 0; }

            if (_hasRecordedRealLength)
            {
                if (lenBytes > 0 && lenBytes != _initialPrescanLengthBytes)
                {
                    _initialPrescanLengthBytes = lenBytes;
                    double secs = Bass.ChannelBytes2Seconds(_stream, lenBytes);
                    TotalTime = (float)secs;
                    _hasRealLength = true;
                    if (LandfallConfig.CurrentConfig.ShowDebug) Debug.Log($"[StreamingClip] PollForLengthChange (post-recorded): authoritative length -> {secs:F3}s");
                }
                return;
            }

            if (_watchingForRealLength)
            {
                if (downloadPercent > _lastObservedDownloadPercent + 0.001f)
                {
                    _lastObservedDownloadPercent = downloadPercent;
                    _watchLastProgressTime = Time.realtimeSinceStartup;
                }

                long authoritativeLen = 0;
                try { authoritativeLen = Bass.ChannelGetLength(_stream, PositionFlags.Bytes); } catch { authoritativeLen = 0; }
                if (authoritativeLen <= 0)
                {
                    long e = Bass.StreamGetFilePosition(_stream, FileStreamPosition.End);
                    long d = Bass.StreamGetFilePosition(_stream, FileStreamPosition.Download);
                    authoritativeLen = Math.Max(e, d);
                }

                if (authoritativeLen > 0 && authoritativeLen != _initialPrescanLengthBytes)
                {
                    _lastObservedDownloadPercent = downloadPercent;
                    _hasRecordedRealLength = true;
                    _watchingForRealLength = false;
                    double secs = Bass.ChannelBytes2Seconds(_stream, authoritativeLen);
                    TotalTime = (float)secs;
                    _hasRealLength = true;
                    _isFullyDownloaded = (downloadPercent >= 0.999f);
                    if (LandfallConfig.CurrentConfig.ShowDebug) Debug.Log($"[StreamingClip] Prescan diverged -> recorded real length = {secs:F3}s (bytes={authoritativeLen}). DownloadPct={downloadPercent * 100f:F1}%");
                    MainThreadInvoke(() => { OnTitleChanged?.Invoke(PublicTrackTitle); });
                    return;
                }

                if (downloadPercent >= PrescanAcceptThreshold)
                {
                    long best = authoritativeLen > 0 ? authoritativeLen : Math.Max(Bass.StreamGetFilePosition(_stream, FileStreamPosition.End), Bass.StreamGetFilePosition(_stream, FileStreamPosition.Download));
                    if (best <= 0) best = _initialPrescanLengthBytes;
                    if (best > 0)
                    {
                        _hasRecordedRealLength = true;
                        _watchingForRealLength = false;
                        double secs = Bass.ChannelBytes2Seconds(_stream, best);
                        TotalTime = (float)secs;
                        _hasRealLength = true;
                        _isFullyDownloaded = (downloadPercent >= 0.999f);
                        if (LandfallConfig.CurrentConfig.ShowDebug) Debug.Log($"[StreamingClip] Prescan accepted after download threshold. DownloadPct={downloadPercent * 100f:F1}%; TotalTime={TotalTime:F2}s");
                        MainThreadInvoke(() => { OnTitleChanged?.Invoke(PublicTrackTitle); });
                        return;
                    }
                }

                if (Time.realtimeSinceStartup - _watchLastProgressTime >= _watchProgressStallSeconds)
                {
                    long best = authoritativeLen > 0 ? authoritativeLen : Math.Max(Bass.StreamGetFilePosition(_stream, FileStreamPosition.End), Bass.StreamGetFilePosition(_stream, FileStreamPosition.Download));
                    if (best > 0)
                    {
                        _hasRecordedRealLength = true;
                        _watchingForRealLength = false;
                        double secs = Bass.ChannelBytes2Seconds(_stream, best);
                        TotalTime = (float)secs;
                        _hasRealLength = true;
                        _isFullyDownloaded = (downloadPercent >= 0.999f);
                        if (LandfallConfig.CurrentConfig.ShowDebug) Debug.Log($"[StreamingClip] Prescan watch timeout/stall accepted. DownloadPct={downloadPercent * 100f:F1}%; TotalTime={TotalTime:F2}s");
                        MainThreadInvoke(() => { OnTitleChanged?.Invoke(PublicTrackTitle); });
                        return;
                    }
                }

                if (Time.realtimeSinceStartup - _watchStartTime >= _watchTimeoutSeconds)
                {
                    long best = authoritativeLen > 0 ? authoritativeLen : Math.Max(Bass.StreamGetFilePosition(_stream, FileStreamPosition.End), Bass.StreamGetFilePosition(_stream, FileStreamPosition.Download));
                    if (best > 0)
                    {
                        _hasRecordedRealLength = true;
                        _watchingForRealLength = false;
                        double secs = Bass.ChannelBytes2Seconds(_stream, best);
                        TotalTime = (float)secs;
                        _hasRealLength = true;
                        _isFullyDownloaded = (downloadPercent >= 0.999f);
                        if (LandfallConfig.CurrentConfig.ShowDebug) Debug.Log($"[StreamingClip] Prescan watch timeout reached. Using best estimate: TotalTime={TotalTime:F2}s, DownloadPct={downloadPercent * 100f:F1}%");
                        MainThreadInvoke(() => { OnTitleChanged?.Invoke(PublicTrackTitle); });
                        return;
                    }
                }

                return;
            }

            if (lenBytes > 0 && !_hasRealLength)
            {
                _hasRealLength = true;
                _hasRecordedRealLength = true;
                double secs = Bass.ChannelBytes2Seconds(_stream, lenBytes);
                TotalTime = (float)secs;
                if (LandfallConfig.CurrentConfig.ShowDebug) Debug.Log($"[StreamingClip] PollForLengthChange authoritative length -> {secs:F3}s (bytes={lenBytes})");
            }
        }
        catch (Exception ex)
        {
            if (_stream == 0) return;
            Debug.LogWarning($"[StreamingClip] PollForLengthChange error: {ex.Message}");
        }
    }

    // --- Metadata reading & emitting ---

    private void UpdateMetadata(bool force = false)
    {
        if (_stream == 0) return;
        if (!force && Time.realtimeSinceStartup - _lastMetaCheck < 1.0f) return;
        _lastMetaCheck = Time.realtimeSinceStartup;

        try
        {
            string newTitle = null;
            bool titleFromStream = false;

            // 1. Try ICY metadata first (radio streams)
            try
            {
                IntPtr pmeta = Bass.ChannelGetTags(_stream, TagType.META);
                if (pmeta != IntPtr.Zero)
                {
                    string meta = Marshal.PtrToStringAnsi(pmeta);
                    if (!string.IsNullOrEmpty(meta))
                    {
                        string icy = ParseIcyStreamTitle(meta);
                        if (!string.IsNullOrEmpty(icy))
                        {
                            newTitle = icy;
                            titleFromStream = true;
                        }
                    }
                }
            }
            catch { }

            // 2. Try ID3v2 tags using ManagedBass ID3v2Tag class
            if (string.IsNullOrEmpty(newTitle))
            {
                try
                {
                    var id3v2 = new ID3v2Tag(_stream);
                    if (id3v2.TextFrames != null && id3v2.TextFrames.Count > 0)
                    {
                        // Try to get title and artist from ID3v2 frames
                        string artist = null;

                        // Common ID3v2 frame names for title and artist
                        if (id3v2.TextFrames.TryGetValue("TIT2", out string title) ||  // Title
                            id3v2.TextFrames.TryGetValue("TT2", out title))     // Legacy title frame
                        {
                            id3v2.TextFrames.TryGetValue("TPE1", out artist);  // Lead performer
                            id3v2.TextFrames.TryGetValue("TP1", out artist);   // Legacy artist frame

                            if (!string.IsNullOrWhiteSpace(title))
                            {
                                newTitle = string.IsNullOrWhiteSpace(artist)
                                    ? title.Trim()
                                    : $"{artist.Trim()} - {title.Trim()}";
                                titleFromStream = true;

                                if (LandfallConfig.CurrentConfig.ShowDebug) Debug.Log($"[StreamingClip] ID3v2 tags found: Title='{title}', Artist='{artist}'");
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    // This is normal if no ID3v2 tags are present
                    if (force) // Only log in forced updates to reduce spam
                        if (LandfallConfig.CurrentConfig.ShowDebug) Debug.Log($"[StreamingClip] No ID3v2 tags or parsing failed: {ex.Message}");
                }
            }

            // 3. Fall back to TagReader for other tag formats
            if (string.IsNullOrEmpty(newTitle))
            {
                try
                {
                    var tr = TagReader.Read(_stream);
                    if (tr != null)
                    {
                        if (!string.IsNullOrWhiteSpace(tr.Title))
                        {
                            newTitle = string.IsNullOrWhiteSpace(tr.Artist)
                                ? tr.Title
                                : $"{tr.Artist} - {tr.Title}";
                            titleFromStream = true;
                        }
                        else if (tr.Other != null && tr.Other.TryGetValue("TITLE", out var otitle))
                        {
                            newTitle = otitle;
                            titleFromStream = true;
                        }
                    }
                }
                catch { }
            }

            
            if (!string.IsNullOrEmpty(newTitle) && !IsNoiseTitle(newTitle))
            {
                SetClipTitle(newTitle, sourceIsStream: titleFromStream);
            }
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[StreamingClip] UpdateMetadata error: {ex.Message}");
        }
    }

    // Helper to filter out noise/encoder metadata
    private bool IsNoiseTitle(string title)
    {
        if (string.IsNullOrWhiteSpace(title)) return true;

        string lc = title.ToLowerInvariant();
        var noisyTokens = new[] {
        "encoder=", "lavc", "libopus", "ffmpeg", "libvorbis",
        "unknown", "stream", "radio", "untitled"
    };

        return noisyTokens.Any(token => lc.Contains(token));
    }

    // --- Utilities ---

    private static string SafeClipName(string path)
    {
        try { return Path.GetFileNameWithoutExtension(path); } catch { return "Stream"; }
    }

    private static string ParseIcyStreamTitle(string meta)
    {
        if (string.IsNullOrEmpty(meta)) return null;
        int start = meta.IndexOf("StreamTitle='", StringComparison.OrdinalIgnoreCase);
        if (start < 0) return null;
        int end = meta.IndexOf("';", start);
        if (end <= start) return null;
        string title = meta[(start + 13)..end];
        return string.IsNullOrWhiteSpace(title) ? null : title.Trim();
    }
    // --- Off-main stream creation ---

    private async Task<int> CreateStreamBackground(string path)
    {
        return await Task.Run(() =>
        {
            try
            {
                EnsureBassInitializedAtDeviceRate(_deviceRate);

                // Let BASS use default buffering - remove all custom BufferLength/PreBuffer settings
                BassFlags commonFlags = BassFlags.Decode | BassFlags.Float | BassFlags.StreamStatus;

                if (path.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase))
                {
                    int hs = BassHls.BASS_HLS_StreamCreateURL(path,
                        commonFlags | BassFlags.StreamDownloadBlocks,
                        IntPtr.Zero, IntPtr.Zero);
                    return hs;
                }

                if (path.StartsWith("http", StringComparison.OrdinalIgnoreCase)|| path.StartsWith("ftp", StringComparison.OrdinalIgnoreCase))
                {
                    int s = Bass.CreateStream(path, 0, commonFlags | BassFlags.Prescan | BassFlags.AsyncFile, null, IntPtr.Zero);
                    return s;
                }

                // Local files
                return Bass.CreateStream(path, 0, 0, commonFlags | BassFlags.Prescan);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[StreamingClip] CreateStreamBackground exception: {ex}");
                return 0;
            }
        }).ConfigureAwait(false);
    }
    // --- Bass init & plugins ---

    public static bool TryInitBassOnce()
    {
        //if (_bassInitialized) return;

        //lock (_initLock)
        //{
        //    if (_bassInitialized) return;
        //    string modDir = Path.GetDirectoryName(typeof(StreamingClip).Assembly.Location);
        //    try { SetDllDirectory(modDir); } catch { }
        //    Bass.Configure(Configuration.NetPlaylist, true);

        //    try
        //    {
        //        if (!Bass.Init(-1, AudioSettings.outputSampleRate > 0 ? AudioSettings.outputSampleRate : 48000, DeviceInitFlags.Default))
        //        {
        //            var err = Bass.LastError;
        //            if (err != Errors.Already) throw new Exception($"Bass.Init failed: {err}");
        //        }
        //        _bassInitialized = true;
        //        Debug.Log($"[StreamingClip] BASS initialized (v{Bass.Version})");
        //    }
        //    catch (Exception ex)
        //    {
        //        Debug.LogError($"[StreamingClip] BASS init failed: {ex}");
        //        return;
        //    }

        //    var pluginDlls = new[] { "bass_aac.dll", "bassflac.dll", "bassopus.dll", "basshls.dll", "bassalac.dll", "bass_spx.dll", "bass_tta.dll" };
        //    foreach (var dll in pluginDlls)
        //    {
        //        string p = Path.Combine(modDir, dll);
        //        if (!File.Exists(p)) continue;
        //        int h = Bass.PluginLoad(p);
        //        if (h == 0) Debug.LogWarning($"[StreamingClip] Plugin {dll} load failed: {Bass.LastError}");
        //    }
        //    _pluginsLoaded = true;
        //}

        // Use the ManagedBassLoader instead of direct BASS calls
        return ManagedBassLoader.IsInitialized || ManagedBassLoader.Initialize();
    }
    /// <summary>
    /// Dump all head / tag sources BASS exposes for the current stream handle.
    /// Call immediately after CreateStream / FinishStartStream to inspect server-provided metadata.
    /// </summary>
    public void DebugDumpHeadTags(int streamHandle)
    {
        
        try
        {
            if (streamHandle == 0)
            {
                Debug.Log("[StreamingClip] DebugDumpHeadTags: streamHandle==0");
                return;
            }

            // Stream file positions
            long dl = Bass.StreamGetFilePosition(streamHandle, FileStreamPosition.Download);
            long end = Bass.StreamGetFilePosition(streamHandle, FileStreamPosition.End);
            long start = Bass.StreamGetFilePosition(streamHandle, FileStreamPosition.Start);
            long curPos = Bass.ChannelGetPosition(streamHandle, PositionFlags.Bytes);
            long connected = Bass.StreamGetFilePosition(streamHandle, FileStreamPosition.Connected);
            long buffer = Bass.StreamGetFilePosition(streamHandle, FileStreamPosition.Buffer);
            long socket = Bass.StreamGetFilePosition(streamHandle, FileStreamPosition.Socket);

            Debug.Log($"[StreamingClip][HEAD] Stream positions:");
            Debug.Log($"[StreamingClip][HEAD]   Download: {dl} bytes");
            Debug.Log($"[StreamingClip][HEAD]   End: {end} bytes");
            Debug.Log($"[StreamingClip][HEAD]   Start: {start} bytes");
            Debug.Log($"[StreamingClip][HEAD]   Current: {curPos} bytes");
            Debug.Log($"[StreamingClip][HEAD]   Connected: {connected}");
            Debug.Log($"[StreamingClip][HEAD]   Buffer: {buffer} bytes");
            Debug.Log($"[StreamingClip][HEAD]   Socket: {socket}");

            // Channel Information
            try
            {
                var info = Bass.ChannelGetInfo(streamHandle);
                Debug.Log($"[StreamingClip][HEAD] ChannelInfo:");
                Debug.Log($"[StreamingClip][HEAD]   Type: {info.ChannelType}");
                Debug.Log($"[StreamingClip][HEAD]   Flags: {info.Flags}");
                Debug.Log($"[StreamingClip][HEAD]   Channels: {info.Channels}");
                Debug.Log($"[StreamingClip][HEAD]   Frequency: {info.Frequency} Hz");
                Debug.Log($"[StreamingClip][HEAD]   Name: {info.FileName ?? "<null>"}");
            }
            catch (Exception ex)
            {
                Debug.Log($"[StreamingClip][HEAD] ChannelInfo error: {ex.Message}");
            }

            // Channel Attributes
            try
            {
                Debug.Log($"[StreamingClip][HEAD] Channel Attributes:");

                // Bitrate
                float bitrate = (float)Bass.ChannelGetAttribute(streamHandle, ChannelAttribute.Bitrate);
                Debug.Log($"[StreamingClip][HEAD]   Bitrate: {bitrate} kbps");

                // Frequency
                float freq = (float)Bass.ChannelGetAttribute(streamHandle, ChannelAttribute.Frequency);
                Debug.Log($"[StreamingClip][HEAD]   Current Freq: {freq} Hz");

                // Volume
                float volume = (float)Bass.ChannelGetAttribute(streamHandle, ChannelAttribute.Volume);
                Debug.Log($"[StreamingClip][HEAD]   Volume: {volume}");

                // Pan
                float pan = (float)Bass.ChannelGetAttribute(streamHandle, ChannelAttribute.Pan);
                Debug.Log($"[StreamingClip][HEAD]   Pan: {pan}");

                // CPU usage
                float cpu = (float)Bass.ChannelGetAttribute(streamHandle, ChannelAttribute.CPUUsage);
                Debug.Log($"[StreamingClip][HEAD]   CPU Usage: {cpu:P2}");
            }
            catch (Exception ex)
            {
                Debug.Log($"[StreamingClip][HEAD] Channel attributes error: {ex.Message}");
            }

            // Buffering Information
            try
            {
                int buffered = Bass.ChannelGetData(streamHandle, IntPtr.Zero, (int)DataFlags.Available);
                Debug.Log($"[StreamingClip][HEAD] Buffering:");
                Debug.Log($"[StreamingClip][HEAD]   Available data: {buffered} bytes");

                // Calculate buffer percentage if we have download info
                if (end > 0 && dl > 0)
                {
                    float bufferPercent = (float)dl / end * 100f;
                    Debug.Log($"[StreamingClip][HEAD]   Download progress: {bufferPercent:F1}% ({dl}/{end} bytes)");
                }
            }
            catch (Exception ex)
            {
                Debug.Log($"[StreamingClip][HEAD] Buffering info error: {ex.Message}");
            }

            // Use TagReader for comprehensive tag analysis
            try
            {
                var tagReader = TagReader.Read(streamHandle);
                if (tagReader != null)
                {
                    Debug.Log($"[StreamingClip][HEAD] TagReader Analysis:");

                    // Core properties
                    Debug.Log($"[StreamingClip][HEAD]   Title: {tagReader.Title ?? "<null>"}");
                    Debug.Log($"[StreamingClip][HEAD]   Artist: {tagReader.Artist ?? "<null>"}");
                    Debug.Log($"[StreamingClip][HEAD]   Album: {tagReader.Album ?? "<null>"}");
                    Debug.Log($"[StreamingClip][HEAD]   AlbumArtist: {tagReader.AlbumArtist ?? "<null>"}");
                    Debug.Log($"[StreamingClip][HEAD]   Genre: {tagReader.Genre ?? "<null>"}");
                    Debug.Log($"[StreamingClip][HEAD]   Year: {tagReader.Year}");
                    Debug.Log($"[StreamingClip][HEAD]   Track: {tagReader.Track ?? "<null>"}");
                    Debug.Log($"[StreamingClip][HEAD]   Comment: {tagReader.Comment ?? "<null>"}");
                    Debug.Log($"[StreamingClip][HEAD]   Composer: {tagReader.Composer ?? "<null>"}");
                    Debug.Log($"[StreamingClip][HEAD]   Copyright: {tagReader.Copyright ?? "<null>"}");
                    Debug.Log($"[StreamingClip][HEAD]   Encoder: {tagReader.Encoder ?? "<null>"}");
                    Debug.Log($"[StreamingClip][HEAD]   BPM: {tagReader.BPM ?? "<null>"}");
                    Debug.Log($"[StreamingClip][HEAD]   Lyrics: {(!string.IsNullOrEmpty(tagReader.Lyrics) ? $"{tagReader.Lyrics.Length} chars" : "<null>")}");

                    // Other tags
                    if (tagReader.Other != null && tagReader.Other.Count > 0)
                    {
                        Debug.Log($"[StreamingClip][HEAD]   Other Tags ({tagReader.Other.Count}):");
                        var importantOthers = tagReader.Other
                            .Where(kv => !string.IsNullOrEmpty(kv.Value))
                            .OrderBy(kv => kv.Key)
                            .Take(15); // Limit to prevent spam

                        foreach (var tag in importantOthers)
                        {
                            string valuePreview = tag.Value.Length > 50
                                ? tag.Value[..47] + "..."
                                : tag.Value;
                            Debug.Log($"[StreamingClip][HEAD]     {tag.Key} = {valuePreview}");
                        }

                        if (tagReader.Other.Count > 15)
                            Debug.Log($"[StreamingClip][HEAD]     ... and {tagReader.Other.Count - 15} more tags");
                    }

                    // Picture information
                    if (tagReader.Pictures != null && tagReader.Pictures.Count > 0)
                    {
                        Debug.Log($"[StreamingClip][HEAD]   Pictures: {tagReader.Pictures.Count}");
                        foreach (var pic in tagReader.Pictures.Take(3))
                        {
                            Debug.Log($"[StreamingClip][HEAD]     - {pic.PictureType}: {pic.MimeType} ({pic.Data.Length} bytes)");
                        }
                        if (tagReader.Pictures.Count > 3)
                            Debug.Log($"[StreamingClip][HEAD]     ... and {tagReader.Pictures.Count - 3} more pictures");
                    }
                }
                else
                {
                    Debug.Log("[StreamingClip][HEAD] TagReader: <null>");
                }
            }
            catch (Exception ex)
            {
                Debug.Log($"[StreamingClip][HEAD] TagReader error: {ex.Message}");
            }

            // Direct tag type analysis (backup method)
            Debug.Log($"[StreamingClip][HEAD] Direct Tag Analysis:");

            // ICY Metadata (most important for radio)
            try
            {
                IntPtr icyPtr = Bass.ChannelGetTags(streamHandle, TagType.META);
                if (icyPtr != IntPtr.Zero)
                {
                    string icy = Marshal.PtrToStringAnsi(icyPtr);
                    if (!string.IsNullOrEmpty(icy))
                    {
                        Debug.Log($"[StreamingClip][HEAD]   ICY Meta: {icy}");
                        string streamTitle = ParseIcyStreamTitle(icy);
                        if (!string.IsNullOrEmpty(streamTitle))
                            Debug.Log($"[StreamingClip][HEAD]   ICY Title: {streamTitle}");
                    }
                    else
                    {
                        Debug.Log($"[StreamingClip][HEAD]   ICY Meta: <empty>");
                    }
                }
                else
                {
                    Debug.Log($"[StreamingClip][HEAD]   ICY Meta: <none>");
                }
            }
            catch (Exception ex)
            {
                Debug.Log($"[StreamingClip][HEAD]   ICY Meta error: {ex.Message}");
            }

            // ID3v2 Tags using ManagedBass ID3v2Tag class
            try
            {
                var id3v2 = new ID3v2Tag(streamHandle);
                if (id3v2.TextFrames != null && id3v2.TextFrames.Count > 0)
                {
                    Debug.Log($"[StreamingClip][HEAD]   ID3v2 Frames: {id3v2.TextFrames.Count}");

                    // Show important frames
                    var importantFrames = new[] { "TIT2", "TT2", "TPE1", "TP1", "TALB", "TAL", "TYER", "TRCK", "TCON", "COMM" };
                    foreach (var frame in importantFrames)
                    {
                        if (id3v2.TextFrames.TryGetValue(frame, out string value) && !string.IsNullOrEmpty(value))
                        {
                            Debug.Log($"[StreamingClip][HEAD]     {frame}: {value}");
                        }
                    }
                }
                else
                {
                    Debug.Log($"[StreamingClip][HEAD]   ID3v2 Frames: <none>");
                }
            }
            catch (Exception ex)
            {
                Debug.Log($"[StreamingClip][HEAD]   ID3v2 error: {ex.Message}");
            }

            // Stream status and level
            try
            {
                var level = Bass.ChannelGetLevel(streamHandle);
                Debug.Log($"[StreamingClip][HEAD] Audio Level: L={level.LoWord()} R={level.HiWord()}");

                // Check if stream is active
                bool isActive = Bass.ChannelIsActive(streamHandle) != PlaybackState.Stopped;
                Debug.Log($"[StreamingClip][HEAD] Playback State: {Bass.ChannelIsActive(streamHandle)}");

                // Get stream length in seconds
                long lengthBytes = Bass.ChannelGetLength(streamHandle, PositionFlags.Bytes);
                if (lengthBytes > 0)
                {
                    double lengthSeconds = Bass.ChannelBytes2Seconds(streamHandle, lengthBytes);
                    Debug.Log($"[StreamingClip][HEAD] Stream Length: {lengthSeconds:F2}s ({lengthBytes} bytes)");
                }
                else
                {
                    Debug.Log($"[StreamingClip][HEAD] Stream Length: <unknown>");
                }
            }
            catch (Exception ex)
            {
                Debug.Log($"[StreamingClip][HEAD] Stream status error: {ex.Message}");
            }

            // Radio stream detection analysis
            try
            {
                Debug.Log($"[StreamingClip][HEAD] Radio Stream Analysis (Behavior-Based):");

                bool hasIcy = Bass.ChannelGetTags(streamHandle, TagType.META) != IntPtr.Zero;

                long endPos = Bass.StreamGetFilePosition(streamHandle, FileStreamPosition.End);
                long downloadPos = Bass.StreamGetFilePosition(streamHandle, FileStreamPosition.Download);
                long bassLength = Bass.ChannelGetLength(streamHandle, PositionFlags.Bytes);

                bool hasNoKnownEnd = endPos <= 0;
                bool hasInvalidBassLength = bassLength <= 0;
                bool hasSmallEndPosition = endPos > 0 && endPos < 1024 * 1024; // Less than 1MB
                bool hasVerySmallEndPosition = endPos > 0 && endPos < 300 * 1024; // Less than 300KB
                bool hasContinuousDownload = downloadPos > 0 && endPos > 0 && downloadPos < endPos;
                bool isHttp = _currentPath?.StartsWith("http", StringComparison.OrdinalIgnoreCase) == true || _currentPath?.StartsWith("ftp", StringComparison.OrdinalIgnoreCase) == true;

                Debug.Log($"[StreamingClip][HEAD]   Has ICY: {hasIcy}");
                Debug.Log($"[StreamingClip][HEAD]   End Position: {endPos} bytes");
                Debug.Log($"[StreamingClip][HEAD]   Download Position: {downloadPos} bytes");
                Debug.Log($"[StreamingClip][HEAD]   BASS Length: {bassLength} bytes");
                Debug.Log($"[StreamingClip][HEAD]   No Known End: {hasNoKnownEnd}");
                Debug.Log($"[StreamingClip][HEAD]   Invalid BASS Length: {hasInvalidBassLength}");
                Debug.Log($"[StreamingClip][HEAD]   Small End Position (<1MB): {hasSmallEndPosition}");
                Debug.Log($"[StreamingClip][HEAD]   Very Small End Position (<300KB): {hasVerySmallEndPosition}");
                Debug.Log($"[StreamingClip][HEAD]   Continuous Download: {hasContinuousDownload}");
                Debug.Log($"[StreamingClip][HEAD]   Is HTTP: {isHttp}");

                // Pure behavior-based radio detection
                bool isRadio = hasIcy ||
                              (isHttp && hasNoKnownEnd && hasInvalidBassLength) ||
                              (isHttp && hasVerySmallEndPosition && hasInvalidBassLength) ||
                              (isHttp && hasSmallEndPosition && hasContinuousDownload && hasInvalidBassLength);

                Debug.Log($"[StreamingClip][HEAD]   Should be Radio: {isRadio}");

                // Detailed reasoning
                if (isRadio)
                {
                    if (hasIcy)
                        Debug.Log($"[StreamingClip][HEAD]   Reasoning: ICY metadata present");
                    else if (isHttp && hasNoKnownEnd && hasInvalidBassLength)
                        Debug.Log($"[StreamingClip][HEAD]   Reasoning: No known end + invalid BASS length");
                    else if (isHttp && hasVerySmallEndPosition && hasInvalidBassLength)
                        Debug.Log($"[StreamingClip][HEAD]   Reasoning: Very small buffer ({endPos} bytes) + invalid BASS length");
                    else if (isHttp && hasSmallEndPosition && hasContinuousDownload && hasInvalidBassLength)
                        Debug.Log($"[StreamingClip][HEAD]   Reasoning: Small buffer + continuous download + invalid length");
                }
                else
                {
                    Debug.Log($"[StreamingClip][HEAD]   Reasoning: Finite file behavior or insufficient radio indicators");
                }
            }
            catch (Exception ex)
            {
                Debug.Log($"[StreamingClip][HEAD] Radio analysis error: {ex.Message}");
            }

        }
        catch (Exception e)
        {
            Debug.LogWarning($"[StreamingClip][HEAD] DebugDumpHeadTags unexpected error: {e}");
        }
    }


    private static void EnsureBassInitializedAtDeviceRate(int deviceRate)
    {
        if (_bassInitialized) return;
        TryInitBassOnce();
    }

    [DllImport("kernel32", SetLastError = true)]
    private static extern bool SetDllDirectory(string lpPathName);

    // --- Minimal ID3v2 wrapper placeholder (we prefer TagReader) ---
    public string GetBufferStatus()
    {
        if (_stream == 0) return "No stream";
        try
        {
            long dl = Bass.StreamGetFilePosition(_stream, FileStreamPosition.Download);
            long end = Bass.StreamGetFilePosition(_stream, FileStreamPosition.End);
            if (end > 0) return $"{(float)dl / end:P0}";
            return "Streaming…";
        }
        catch { return "Unknown"; }
    }

    private static string SafePtrToAnsi(IntPtr p) => p == IntPtr.Zero ? null : Marshal.PtrToStringAnsi(p);

    private void OnDestroy() => StopStream();
}
