using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;

public static class YtDlpUpdater
{
    /// <summary>
    /// Runs yt-dlp --update-to nightly in background and invokes callback with success flag and message.
    /// </summary>
    public static async void UpdateAsync(string ytDlpPath, Action<bool, string> onResult)
    {
        try
        {
            await Task.Run(() =>
            {
                var psi = new ProcessStartInfo
                {
                    FileName = ytDlpPath,
                    Arguments = "-U --update-to nightly",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
                };

                string output;
                string error;
                int exitCode;

                using (var process = new Process { StartInfo = psi })
                {
                    process.Start();
                    output = process.StandardOutput.ReadToEnd();
                    error = process.StandardError.ReadToEnd();
                    process.WaitForExit();
                    exitCode = process.ExitCode;
                }

                // Parse result on background thread, then marshal to main thread.
                string combined = output + "\n" + error;
                bool success;
                string message;

                if (combined.Contains("Updated yt-dlp to ", StringComparison.OrdinalIgnoreCase))
                {
                    success = true;
                    message = "yt-dlp updated successfully.";
                }
                else if (combined.Contains("yt-dlp is up to date", StringComparison.OrdinalIgnoreCase))
                {
                    success = true;
                    message = "yt-dlp is already up-to-date.";
                }
                else
                {
                    success = false;
                    message = $"Update failed (exit code {exitCode}).\n{error}";
                }

                // Send to main thread
                MainThreadDispatcher.Enqueue(() => onResult?.Invoke(success, message));
            });
        }
        catch (Exception ex)
        {
            MainThreadDispatcher.Enqueue(() => onResult?.Invoke(false, $"Update exception: {ex.Message}"));
        }
    }
}

// Simple main-thread dispatcher
public static class MainThreadDispatcher
{
    private static readonly System.Collections.Generic.Queue<Action> _queue =
        new System.Collections.Generic.Queue<Action>();

    public static void Enqueue(Action action)
    {
        lock (_queue)
        {
            _queue.Enqueue(action);
        }
    }

    public static void Drain()
    {
        lock (_queue)
        {
            while (_queue.Count > 0)
            {
                _queue.Dequeue()?.Invoke();
            }
        }
    }
}