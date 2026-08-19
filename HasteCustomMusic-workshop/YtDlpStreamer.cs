using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ManagedBass;
using UnityEngine;
using Debug = UnityEngine.Debug;

public static class YtDlpStreamer
{
    // Keeps the FileProcedures and source objects alive as long as the BASS stream exists.
    private static readonly ConcurrentDictionary<int, (FileProcedures procs, YtDlpStreamSource source)> ActiveStreams =
        new ConcurrentDictionary<int, (FileProcedures, YtDlpStreamSource)>();


    /// <summary>
    /// Returns true if the URL is a YouTube video/watch link.
    /// </summary>
    public static bool IsYouTubeUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        return url.Contains("youtube.com/watch", StringComparison.OrdinalIgnoreCase) ||
               url.Contains("youtu.be/", StringComparison.OrdinalIgnoreCase);
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
            Arguments = $"--dump-json --no-playlist \"{youtubeUrl}\"",
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
                Arguments = $" -o - -f bestaudio[ext=m4a]/bestaudio --no-playlist --quiet {cookiesArg} {ffmpegArg} \"{_url}\"",
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

                // Try to get data from current chunk or queue
                if (_currentChunk == null || _currentChunkOffset >= _currentChunk.Length)
                {
                    if (!_chunks.TryDequeue(out _currentChunk))
                    {
                        // No chunk available
                        if (_eof)
                            break; // no more data

                        // Wait indefinitely for data, but wake if disposed or EOF set
                        _dataAvailable.WaitOne();

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
}