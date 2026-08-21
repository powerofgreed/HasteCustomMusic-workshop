using ManagedBass;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using UnityEngine;
using Debug = UnityEngine.Debug;

public static class YtDlpStreamer
{
    private static readonly HttpClient MetadataHttpClient = new HttpClient
    {
        Timeout = TimeSpan.FromSeconds(20)
    };

    [Serializable]
    public sealed class YouTubeMetadata
    {
        public string Title;
        public string Artist;
        public string Album;
        public double Duration;
        public string ThumbnailUrl;
        public byte[] ThumbnailData;
        public string DirectAudioUrl;
        public Dictionary<string, string> HttpHeaders;
    }

    [Serializable]
    private sealed class MetadataJson
    {
        public string url;
        public Dictionary<string, string> http_headers;
        public string title;
        public string uploader;
        public string channel;
        public double duration;
        public string artist;
        public string creator;
        public string album;
        public string thumbnail;
        public ThumbnailJson[] thumbnails;
    }

    [Serializable]
    private sealed class PlaylistJson
    {
        public string id;
        public string webpage_url;
        public PlaylistEntryJson[] entries;
    }

    [Serializable]
    private sealed class PlaylistEntryJson
    {
        public string id;
        public string webpage_url;
        public string url;
        public string original_url;
        public string title;
        public string uploader;
    }

    [Serializable]
    private sealed class ThumbnailJson
    {
        public string url;
        public int width;
        public int height;
    }
    public class YouTubePlaylistTrack
    {
        public string Url;
        public string Title;
        public string Artist;
    }

    // Keeps the FileProcedures and source objects alive as long as the BASS stream exists.
    private static readonly ConcurrentDictionary<int, (FileProcedures procs, YtDlpStreamSource source)> ActiveStreams =
        new ConcurrentDictionary<int, (FileProcedures, YtDlpStreamSource)>();

    private static readonly Dictionary<string, YouTubeMetadata> MetadataCache = new Dictionary<string, YouTubeMetadata>();


    /// <summary>
    /// Returns true if the URL is a YouTube video/watch link.
    /// </summary>
    public static bool IsYouTubeUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        return url.Contains("youtube.com/watch", StringComparison.OrdinalIgnoreCase) ||
               url.Contains("youtu.be/", StringComparison.OrdinalIgnoreCase) ||
               url.Contains("youtube.com/shorts/", StringComparison.OrdinalIgnoreCase) ||
               url.Contains("youtube.com/live/", StringComparison.OrdinalIgnoreCase) ||
               url.Contains("youtube.com/embed/", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsYouTubePlaylistUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri uri)) return false;

        bool isYouTubeHost = uri.Host.Equals("youtube.com", StringComparison.OrdinalIgnoreCase) ||
                             uri.Host.EndsWith(".youtube.com", StringComparison.OrdinalIgnoreCase) ||
                             uri.Host.Equals("youtu.be", StringComparison.OrdinalIgnoreCase);
        return isYouTubeHost &&
               (uri.AbsolutePath.Equals("/playlist", StringComparison.OrdinalIgnoreCase) ||
                uri.Query.Contains("list=", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Creates a BASS stream handle from a YouTube URL using yt-dlp direct streaming.
    /// Returns 0 on failure.
    /// </summary>
    public static int CreateStream(string youtubeUrl)
    {
        var source = new YtDlpStreamSource(youtubeUrl);
        if (!source.Start())
            return 0;

        var fileProcs = new FileProcedures
        {
            Read = source.Read,
            Close = source.Close,
            Length = source.Length,
            Seek = source.Seek
        };

        int handle = Bass.CreateStream(StreamSystem.Buffer,
            BassFlags.Decode | BassFlags.Float | BassFlags.StreamStatus,
            fileProcs,
            IntPtr.Zero);

        if (handle == 0)
        {
            Debug.LogError($"[YtDlpStreamer] Bass.CreateStream failed: {Bass.LastError}");
            source.Dispose();
            return 0;
        }

        // Store the source and procedures so they are not garbage collected while the stream is alive.
        ActiveStreams[handle] = (fileProcs, source);

        return handle;
    }

    /// <summary>
    /// Frees a BASS stream and cleans up its associated yt-dlp source.
    /// </summary>
    public static void FreeStream(int handle)
    {
        if (handle == 0) return;

        if (ActiveStreams.TryRemove(handle, out var entry))
        {
            entry.source.Dispose();
        }

        Bass.StreamFree(handle);
    }

    /// <summary>
    /// Fetches metadata as a JSON string (one line) for a YouTube video.
    /// Returns null on failure.
    /// </summary>
    public static async Task<string> FetchMetadataJsonAsync(string youtubeUrl)
    {
        string ytDlpPath = GetYtDlpPath();
        if (!File.Exists(ytDlpPath)) return null;

        var psi = new ProcessStartInfo
        {
            FileName = ytDlpPath,
            Arguments = $"--dump-single-json --skip-download --no-playlist --no-warnings --ignore-config \"{youtubeUrl}\"",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        try
        {
            using (var process = new Process { StartInfo = psi })
            {
                process.Start();
                string output = await process.StandardOutput.ReadToEndAsync();
                string error = await process.StandardError.ReadToEndAsync();
                // Use Task.Run to wait asynchronously, since WaitForExitAsync may not exist.
                await Task.Run(() => process.WaitForExit());

                if (process.ExitCode != 0)
                {
                    Debug.LogWarning($"[YtDlpStreamer] Metadata fetch failed: {error}");
                    return null;
                }
                return output.Trim();
            }
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[YtDlpStreamer] Metadata fetch exception: {ex.Message}");
            return null;
        }
    }

    public static async Task<YouTubeMetadata> FetchMetadataAsync(string youtubeUrl)
    {
        try
        {
            string json = await FetchMetadataJsonAsync(youtubeUrl).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(json)) return null;

            MetadataJson parsed = JsonUtility.FromJson<MetadataJson>(json);
            if (parsed == null || string.IsNullOrWhiteSpace(parsed.title)) return null;

            // ALWAYS prefer direct JPEG thumbnail from video ID
            string videoId = ExtractVideoId(youtubeUrl);
            string thumbnailUrl = !string.IsNullOrEmpty(videoId)
                ? $"https://i.ytimg.com/vi/{videoId}/mqdefault.jpg"
                : SelectThumbnailUrl(parsed);

            var metadata = new YouTubeMetadata
            {
                Title = parsed.title.Trim(),
                Artist = FirstNonEmpty(parsed.artist, parsed.creator, parsed.uploader, parsed.channel),
                Album = parsed.album?.Trim(),
                ThumbnailUrl = thumbnailUrl,
                DirectAudioUrl = parsed.url,
                HttpHeaders = parsed.http_headers,
                Duration = parsed.duration

            };

            if (!string.IsNullOrWhiteSpace(metadata.ThumbnailUrl))
            {
                try
                {
                    metadata.ThumbnailData = await MetadataHttpClient
                        .GetByteArrayAsync(metadata.ThumbnailUrl)
                        .ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[YtDlpStreamer] Thumbnail download failed: {ex.Message}");
                    // fallback to parsed thumbnail if direct fails
                    string fallback = SelectThumbnailUrl(parsed);
                    if (!string.IsNullOrWhiteSpace(fallback) && fallback != thumbnailUrl)
                    {
                        try
                        {
                            metadata.ThumbnailData = await MetadataHttpClient
                                .GetByteArrayAsync(fallback)
                                .ConfigureAwait(false);
                            metadata.ThumbnailUrl = fallback;
                        }
                        catch { }
                    }
                }
            }

            return metadata;
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[YtDlpStreamer] Metadata read failed: {ex.Message}");
            return null;
        }
    }

    private static string ExtractVideoId(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;

        if (Uri.TryCreate(url, UriKind.Absolute, out Uri uri))
        {
            // youtu.be/VIDEO_ID
            if (uri.Host.EndsWith("youtu.be", StringComparison.OrdinalIgnoreCase))
            {
                return uri.Segments.LastOrDefault()?.Trim('/');
            }

            // youtube.com/watch?v=VIDEO_ID or shorts/live/embed
            string query = uri.Query.TrimStart('?');
            var parameters = query.Split('&');
            foreach (string param in parameters)
            {
                if (param.StartsWith("v=", StringComparison.OrdinalIgnoreCase))
                    return param[2..].Split('/')[0];
            }

            // youtube.com/shorts/VIDEO_ID, /live/VIDEO_ID, /embed/VIDEO_ID
            string path = uri.AbsolutePath.Trim('/');
            if (path.Contains("/shorts/") || path.Contains("/live/") || path.Contains("/embed/"))
            {
                return path.Split('/').LastOrDefault();
            }
        }
        return null;
    }

    private static string SelectThumbnailUrl(MetadataJson parsed)
    {
        // Preferred JPEG suffixes in order of reliability (smaller first)
        string[] preferredSuffixes = {
        "mqdefault.jpg",
        "maxresdefault.jpg"
        };

        if (parsed.thumbnails != null)
        {
            foreach (string suffix in preferredSuffixes)
            {
                var match = parsed.thumbnails.FirstOrDefault(
                    t => t != null && !string.IsNullOrWhiteSpace(t.url) &&
                         t.url.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
                if (match != null)
                    return match.url;
            }

            // Fallback: any .jpg thumbnail
            var jpg = parsed.thumbnails.FirstOrDefault(
                t => t != null && !string.IsNullOrWhiteSpace(t.url) &&
                     t.url.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase));
            if (jpg != null)
                return jpg.url;
        }

        // Final fallback: the 'thumbnail' field (often a default JPG)
        return parsed.thumbnail;
    }

    public static async Task<List<string>> FetchPlaylistUrlsAsync(string playlistUrl)
    {
        var tracks = new List<string>();
        string ytDlpPath = GetYtDlpPath();
        if (!File.Exists(ytDlpPath))
        {
            Debug.LogWarning("[YtDlpStreamer] yt-dlp.exe not found for playlist.");
            return tracks;
        }

        var psi = new ProcessStartInfo
        {
            FileName = ytDlpPath,
            // --flat-playlist gives basic info (id, title, uploader) and is fast
            // --dump-json outputs one JSON object per video
            // --playlist-end 100 limits to 100 videos
            Arguments = $"--yes-playlist --flat-playlist --dump-json --skip-download --no-warnings --ignore-config --playlist-end {LandfallConfig.CurrentConfig.PlaylistRange} \"{playlistUrl}\"",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        try
        {
            using (var process = new Process { StartInfo = psi })
            {
                process.Start();
                string output = await process.StandardOutput.ReadToEndAsync();
                string error = await process.StandardError.ReadToEndAsync();
                await Task.Run(() => process.WaitForExit());

                if (process.ExitCode != 0)
                {
                    Debug.LogWarning($"[YtDlpStreamer] Playlist fetch failed: {error}");
                    return tracks;
                }

                // Each line is a separate JSON object
                foreach (string line in output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    try
                    {
                        PlaylistEntryJson entry = JsonUtility.FromJson<PlaylistEntryJson>(line.TrimStart('\uFEFF'));
                        if (entry == null) continue;

                        // Always prefer the ID – it always maps to a valid watch URL,
                        // even if webpage_url/original_url is missing or not a standard watch link.
                        string videoUrl = !string.IsNullOrWhiteSpace(entry.id)
                            ? $"https://www.youtube.com/watch?v={entry.id}"
                            : entry.webpage_url ?? entry.original_url ?? entry.url;

                        if (!IsYouTubeUrl(videoUrl) || tracks.Contains(videoUrl))
                            continue;

                        tracks.Add(videoUrl);

                        // Cache metadata
                        if (!string.IsNullOrWhiteSpace(entry.title))
                        {
                            var metadata = new YouTubeMetadata
                            {
                                Title = entry.title.Trim(),
                                Artist = !string.IsNullOrWhiteSpace(entry.uploader) ? entry.uploader.Trim() : null,
                                Album = null,
                                ThumbnailUrl = !string.IsNullOrWhiteSpace(entry.id)
                                    ? $"https://i.ytimg.com/vi/{entry.id}/mqdefault.jpg"
                                    : null,
                                ThumbnailData = null
                            };

                            lock (MetadataCache)
                            {
                                MetadataCache[videoUrl] = metadata;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.LogWarning($"[YtDlpStreamer] Could not parse playlist entry: {ex.Message}");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[YtDlpStreamer] Playlist fetch exception: {ex.Message}");
        }

        return tracks;
    }

    public static bool TryGetCachedMetadata(string url, out YouTubeMetadata metadata)
    {
        lock (MetadataCache)
        {
            return MetadataCache.TryGetValue(url, out metadata);
        }
    }
    public static int CreateStreamFromDirectUrl(YouTubeMetadata info)
    {
        if (info == null || string.IsNullOrEmpty(info.DirectAudioUrl))
            return 0;

        string bassUrl = BuildBassUrlWithHeaders(info.DirectAudioUrl, info.HttpHeaders);
        if (string.IsNullOrEmpty(bassUrl)) return 0;
        Debug.Log($"[YtDlpStreamer] Direct URL: {info.DirectAudioUrl}");
        Debug.Log($"[YtDlpStreamer] Headers count: {info.HttpHeaders?.Count ?? 0}");
        int handle = Bass.CreateStream(
            bassUrl,
            0,
            BassFlags.Decode | BassFlags.Float | BassFlags.StreamStatus,
            null);

        if (handle == 0)
        {
            Debug.LogWarning($"[YtDlpStreamer] Direct URL stream failed: {Bass.LastError}");
            return 0;
        }
        return handle;
    }

    private static string FirstNonEmpty(params string[] values)
    {
        return values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim();
    }

    private static string GetYtDlpPath()
    {
        return Path.Combine(LandfallConfig.ConfigDirectory, "yt-dlp", "yt-dlp.exe");
    }


    // ============================================================
    // Internal stream source that reads from yt-dlp stdout
    // ============================================================
    private class YtDlpStreamSource : IDisposable
    {
        private readonly string _url;
        private Process _process;
        private readonly ConcurrentQueue<byte[]> _chunks = new ConcurrentQueue<byte[]>();
        private byte[] _currentChunk = null;
        private int _currentChunkOffset = 0;
        private long _totalBytesWritten = 0;
        private volatile bool _eof = false;
        private volatile bool _disposed = false;
        private readonly object _readLock = new object();
        private readonly AutoResetEvent _dataAvailable = new AutoResetEvent(false);

        public YtDlpStreamSource(string url)
        {
            _url = url;
        }

        public bool Start()
        {
            string ytDlpPath = GetYtDlpPath();
            if (!File.Exists(ytDlpPath))
            {
                Debug.LogError("[YtDlpStreamer] yt-dlp.exe not found.");
                return false;
            }
            string ffmpegPath = Path.Combine(
                LandfallConfig.ConfigDirectory,
                "ffmpeg",
                "ffmpeg-master-latest-win64-gpl",
                "bin",
                "ffmpeg.exe");

            string ffmpegArg = File.Exists(ffmpegPath)
                ? $"--ffmpeg-location \"{ffmpegPath}\""
                : string.Empty;

            string cookiesPath = Path.Combine(
                LandfallConfig.ConfigDirectory,
                "cookies.txt");

            string cookiesArg = File.Exists(cookiesPath)
                ? $"--cookies \"{cookiesPath}\""
                : string.Empty;

            var psi = new ProcessStartInfo
            {
                FileName = ytDlpPath,
                Arguments = $"--output - -f bestaudio --no-playlist --quiet --no-warnings --ignore-config {cookiesArg} {ffmpegArg} \"{_url}\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            try
            {
                _process = new Process { StartInfo = psi };
                _process.Start();

                // Read stdout asynchronously and enqueue chunks
                Task.Run(() =>
                {
                    try
                    {
                        using (var stdout = _process.StandardOutput.BaseStream)
                        {
                            byte[] buffer = new byte[81920];
                            int read;
                            while ((read = stdout.Read(buffer, 0, buffer.Length)) > 0)
                            {
                                byte[] chunk = new byte[read];
                                Array.Copy(buffer, chunk, read);
                                _chunks.Enqueue(chunk);
                                Interlocked.Add(ref _totalBytesWritten, read);
                                _dataAvailable.Set();
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.LogWarning($"[YtDlpStreamer] stdout read error: {ex.Message}");
                    }
                    finally
                    {
                        _eof = true;
                        _dataAvailable.Set();
                    }
                });

                // Read stderr asynchronously to avoid deadlock (we don't need it)
                Task.Run(() =>
                {
                    try
                    {
                        string err = _process.StandardError.ReadToEnd();
                        if (!string.IsNullOrEmpty(err))
                            Debug.LogWarning($"[YtDlpStreamer] yt-dlp stderr: {err}");
                    }
                    catch { }
                });

                return true;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[YtDlpStreamer] Failed to start yt-dlp: {ex.Message}");
                return false;
            }
        }

        // BASS Read callback (signature corrected)
        public int Read(IntPtr buffer, int length, IntPtr user)
        {
            if (_disposed) return 0;

            int totalRead = 0;

            while (totalRead < length)
            {
                if (_disposed) return 0;

                // Move to next chunk if needed
                if (_currentChunk == null || _currentChunkOffset >= _currentChunk.Length)
                {
                    if (!_chunks.TryDequeue(out _currentChunk))
                    {
                        // No chunk available
                        if (_eof)
                            break; // truly end of stream

                        // Wait for data with a timeout to prevent indefinite block
                        if (!_dataAvailable.WaitOne(1000))
                        {
                            // Timeout – check if process died or no data will ever come
                            if (_process != null && _process.HasExited)
                            {
                                _eof = true;  // mark as end of stream so we don't hang forever
                                break;
                            }

                            // Still alive but no data; continue loop (maybe next time data arrives)
                            continue;
                        }

                        // After waking, check state again
                        if (_disposed) return 0;
                        if (_eof && _chunks.IsEmpty)
                            break;

                        continue;
                    }
                    _currentChunkOffset = 0;
                }

                int available = _currentChunk.Length - _currentChunkOffset;
                int bytesToCopy = Math.Min(available, length - totalRead);
                System.Runtime.InteropServices.Marshal.Copy(
                    _currentChunk,
                    _currentChunkOffset,
                    new IntPtr(buffer.ToInt64() + totalRead),
                    bytesToCopy);
                _currentChunkOffset += bytesToCopy;
                totalRead += bytesToCopy;
            }

            return totalRead;
        }

        public long Length(IntPtr user)
        {
            // Return 0 to indicate unknown length (non-seekable)
            return 0;
        }

        public bool Seek(long offset, IntPtr user)
        {
            // Seeking not supported for live YouTube stream
            return false;
        }

        public void Close(IntPtr user)
        {
            // Called by BASS when the stream is freed.
            Dispose();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _eof = true;

            try
            {
                if (_process != null && !_process.HasExited)
                {
                    _process.Kill();
                    _process.WaitForExit(5000); // give it a moment to exit
                }
            }
            catch { }

            // Wake any waiting Read callback
            _dataAvailable.Set();
        }

    }
    public static async Task<string> GetDirectAudioUrlAsync(string youtubeUrl)
    {
        string ytDlpPath = GetYtDlpPath();
        if (!File.Exists(ytDlpPath)) return null;

        string cookiesPath = Path.Combine(LandfallConfig.ConfigDirectory, "cookies.txt");
        string cookiesArg = File.Exists(cookiesPath) ? $"--cookies \"{cookiesPath}\"" : string.Empty;

        var psi = new ProcessStartInfo
        {
            FileName = ytDlpPath,
            // Force audio-only M4A (AAC) with fallback to any audio-only format.
            // Use "youtube:client=web" extractor arg for better compatibility.
            Arguments = $"-f bestaudio --extractor-args \"youtube:client=web\" --no-playlist --no-warnings {cookiesArg} -g \"{youtubeUrl}\"",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        try
        {
            using (var process = new Process { StartInfo = psi })
            {
                process.Start();
                string url = (await process.StandardOutput.ReadToEndAsync()).Trim();
                string error = await process.StandardError.ReadToEndAsync();
                await Task.Run(() => process.WaitForExit());

                if (process.ExitCode == 0 && !string.IsNullOrWhiteSpace(url))
                    return url;
                return null;
            }
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[YtDlpStreamer] GetDirectAudioUrlAsync exception: {ex.Message}");
            return null;
        }
    }
    public static int CreateStreamFromUrl(string directUrl)
    {
        if (string.IsNullOrEmpty(directUrl)) return 0;

        // Same flags as StreamingClip.CreateStreamBackground for HTTP URLs
        int handle = Bass.CreateStream(
            directUrl,
            0,
            BassFlags.Decode | BassFlags.Float | BassFlags.StreamStatus | BassFlags.Prescan | BassFlags.AsyncFile,
            null);

        if (handle == 0)
        {
            Debug.LogWarning($"[YtDlpStreamer] CreateStreamFromUrl failed: {Bass.LastError}");
            return 0;
        }
        return handle;
    }
    private static string BuildBassUrlWithHeaders(string url, Dictionary<string, string> headers)
    {
        if (string.IsNullOrEmpty(url)) return null;
        if (headers == null || headers.Count == 0) return url;

        var sb = new StringBuilder(url);
        sb.Append("\r\n");
        foreach (var kvp in headers)
        {
            if (!string.IsNullOrWhiteSpace(kvp.Value))
                sb.Append(kvp.Key).Append(": ").Append(kvp.Value).Append("\r\n");
        }
        return sb.ToString();
    }
    public static string BuildYtDlpArguments(string url, bool forPlaylist = false)
    {
        var sb = new StringBuilder();

        // Audio format: prefer WebM audio (Opus) if available
        sb.Append("-f bestaudio ");

        // Extractor args for better compatibility
        sb.Append("--extractor-args \"youtube:client=web\" ");

        if (forPlaylist)
            sb.Append("--yes-playlist --flat-playlist --dump-json --skip-download ");
        else
            sb.Append("--no-playlist ");

        // Always quiet, no warnings, ignore config for consistent behavior
        sb.Append("--no-warnings --ignore-config ");

        // Cookies source
        switch (LandfallConfig.CurrentConfig.YouTubeCookiesSource)
        {
            case 1: // Chrome
                sb.Append("--cookies-from-browser chrome ");
                break;
            case 2: // Firefox
                sb.Append("--cookies-from-browser firefox ");
                break;
            case 3: // cookies.txt file
                string cookiesPath = Path.Combine(LandfallConfig.ConfigDirectory, "cookies.txt");
                if (File.Exists(cookiesPath))
                    sb.Append($"--cookies \"{cookiesPath}\" ");
                break;
                // 0 = none, no argument
        }

        // Custom user arguments (if any)
        if (!string.IsNullOrWhiteSpace(LandfallConfig.CurrentConfig.YouTubeCustomArgs))
        {
            sb.Append(LandfallConfig.CurrentConfig.YouTubeCustomArgs.Trim());
            sb.Append(' ');
        }

        // If saving listened tracks (future), we'll add download flags later.
        // For now, no extra.

        // Final URL
        sb.Append($"\"{url}\"");

        return sb.ToString();
    }
    private sealed class CachedDirectUrl
    {
        public string Url;
        public DateTime ExpiresAt;
    }
    public static async Task<byte[]> DownloadThumbnailAsync(string thumbnailUrl)
    {
        if (string.IsNullOrWhiteSpace(thumbnailUrl)) return null;
        try
        {
            return await MetadataHttpClient.GetByteArrayAsync(thumbnailUrl).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[YtDlpStreamer] Thumbnail download failed: {ex.Message}");
            return null;
        }
    }

    private static readonly Dictionary<string, CachedDirectUrl> DirectUrlCache =
        new Dictionary<string, CachedDirectUrl>();

    /// <summary>
    /// Returns cached direct URL if it exists and hasn't expired.
    /// </summary>
    public static bool TryGetCachedDirectUrl(string youtubeUrl, out string directUrl)
    {
        lock (DirectUrlCache)
        {
            if (DirectUrlCache.TryGetValue(youtubeUrl, out var cached))
            {
                if (DateTime.UtcNow < cached.ExpiresAt)
                {
                    directUrl = cached.Url;
                    return true;
                }
                DirectUrlCache.Remove(youtubeUrl); // expired
            }
        }
        directUrl = null;
        return false;
    }
    public static async Task PrefetchDirectUrlAsync(string youtubeUrl)
    {
        await GetOrPrefetchDirectUrlAsync(youtubeUrl).ConfigureAwait(false);
    }

    /// <summary>
    /// Gets a direct URL, using cache if available, otherwise fetches and caches it.
    /// </summary>
    public static async Task<string> GetOrPrefetchDirectUrlAsync(string youtubeUrl)
    {
        if (TryGetCachedDirectUrl(youtubeUrl, out var cached))
            return cached;

        string directUrl = await GetDirectAudioUrlAsync(youtubeUrl).ConfigureAwait(false);
        if (!string.IsNullOrEmpty(directUrl))
        {
            lock (DirectUrlCache)
            {
                DirectUrlCache[youtubeUrl] = new CachedDirectUrl
                {
                    Url = directUrl,
                    ExpiresAt = DateTime.UtcNow.AddHours(6)
                };
            }
        }
        return directUrl;
    }
}