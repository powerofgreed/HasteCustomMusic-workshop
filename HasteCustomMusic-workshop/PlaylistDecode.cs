using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using UnityEngine;

public static class PlaylistDecoder
{
    public static List<string> DecodePlaylist(string filePathOrUrl, string content = null)
    {
        if (string.IsNullOrEmpty(content))
        {
            if (File.Exists(filePathOrUrl))
            {
                content = File.ReadAllText(filePathOrUrl);
            }
            else if (Uri.TryCreate(filePathOrUrl, UriKind.Absolute, out _))
            {
                // For URLs, we'll handle downloading in a coroutine
                Debug.Log($"URL playlist detected: {filePathOrUrl}");
                return new List<string> { filePathOrUrl }; // Fallback to treating as single stream
            }
            else
            {
                Debug.LogError($"Playlist file not found: {filePathOrUrl}");
                return new List<string>();
            }
        }

        string extension = Path.GetExtension(filePathOrUrl)?.ToLowerInvariant();

        try
        {
            return extension switch
            {
                ".m3u" => ParseM3U(content),
                ".pls" => ParsePLS(content),
                ".asx" => ParseASX(content),
                ".xspf" => ParseXSPF(content),
                _ => AutoDetectFormat(content, filePathOrUrl)
            };
        }
        catch (Exception ex)
        {
            Debug.LogError($"Error decoding playlist {filePathOrUrl}: {ex}");
            return new List<string>();
        }
    }

    private static List<string> ParseM3U(string content)
    {
        var tracks = new List<string>();
        var lines = content.Split('\n', '\r')
                          .Select(line => line.Trim())
                          .Where(line => !string.IsNullOrEmpty(line))
                          .ToArray();

        bool isExtended = lines.Any(line => line.StartsWith("#EXTM3U"));

        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];

            // Skip extended info lines and comments
            if (line.StartsWith("#EXT") || line.StartsWith("#"))
                continue;

            // Valid track line
            if (!string.IsNullOrWhiteSpace(line) && !line.StartsWith("#"))
            {
                tracks.Add(line);
            }
        }

        Debug.Log($"Parsed M3U playlist: {tracks.Count} tracks found");
        return tracks;
    }

    private static List<string> ParsePLS(string content)
    {
        var tracks = new List<string>();
        var lines = content.Split('\n', '\r')
                          .Select(line => line.Trim())
                          .Where(line => !string.IsNullOrEmpty(line))
                          .ToArray();

        bool inPlaylistSection = false;

        foreach (string line in lines)
        {
            if (line.Equals("[playlist]", StringComparison.OrdinalIgnoreCase))
            {
                inPlaylistSection = true;
                continue;
            }

            if (inPlaylistSection && line.StartsWith("File", StringComparison.OrdinalIgnoreCase))
            {
                int equalsIndex = line.IndexOf('=');
                if (equalsIndex > 0)
                {
                    string url = line[(equalsIndex + 1)..].Trim();
                    if (!string.IsNullOrEmpty(url))
                    {
                        tracks.Add(url);
                    }
                }
            }
        }

        Debug.Log($"Parsed PLS playlist: {tracks.Count} tracks found");
        return tracks;
    }

    private static List<string> ParseASX(string content)
    {
        var tracks = new List<string>();

        // Simple ASX parser - look for <ref> tags with href attributes
        var refMatches = Regex.Matches(content, @"<ref\s+[^>]*href\s*=\s*[""']([^""']+)[""'][^>]*>",
                                     RegexOptions.IgnoreCase);

        foreach (Match match in refMatches)
        {
            if (match.Groups.Count > 1)
            {
                string url = match.Groups[1].Value.Trim();
                if (!string.IsNullOrEmpty(url))
                {
                    tracks.Add(url);
                }
            }
        }

        // Also try entry/ref combinations
        var entryMatches = Regex.Matches(content, @"<entry>.*?<ref\s+[^>]*href\s*=\s*[""']([^""']+)[""'][^>]*>.*?</entry>",
                                       RegexOptions.IgnoreCase | RegexOptions.Singleline);

        foreach (Match match in entryMatches)
        {
            if (match.Groups.Count > 1)
            {
                string url = match.Groups[1].Value.Trim();
                if (!string.IsNullOrEmpty(url) && !tracks.Contains(url))
                {
                    tracks.Add(url);
                }
            }
        }

        Debug.Log($"Parsed ASX playlist: {tracks.Count} tracks found");
        return tracks;
    }

    private static List<string> ParseXSPF(string content)
    {
        var tracks = new List<string>();

        // XSPF parser - look for <location> elements inside <track> elements
        var locationMatches = Regex.Matches(content, @"<track>.*?<location>([^<]+)</location>.*?</track>",
                                          RegexOptions.IgnoreCase | RegexOptions.Singleline);

        foreach (Match match in locationMatches)
        {
            if (match.Groups.Count > 1)
            {
                string url = match.Groups[1].Value.Trim();
                // XSPF may have URL-encoded locations
                try { url = System.Net.WebUtility.UrlDecode(url); } catch { }

                if (!string.IsNullOrEmpty(url))
                {
                    tracks.Add(url);
                }
            }
        }

        Debug.Log($"Parsed XSPF playlist: {tracks.Count} tracks found");
        return tracks;
    }

    private static List<string> AutoDetectFormat(string content, string sourcePath)
    {
        if (sourcePath is null)
        {
            throw new ArgumentNullException(nameof(sourcePath));
        }
        // Try to auto-detect format based on content
        content = content.Trim();

        if (content.StartsWith("#EXTM3U"))
            return ParseM3U(content);
        else if (content.StartsWith("[playlist]", StringComparison.OrdinalIgnoreCase))
            return ParsePLS(content);
        else if (content.StartsWith("<asx", StringComparison.OrdinalIgnoreCase))
            return ParseASX(content);
        else if (content.StartsWith("<?xml") && content.Contains("xspf", StringComparison.OrdinalIgnoreCase))
            return ParseXSPF(content);
        else
        {
            // Treat as simple URL list (one per line)
            return content.Split('\n', '\r')
                         .Select(line => line.Trim())
                         .Where(line => !string.IsNullOrEmpty(line) && !line.StartsWith("#"))
                         .ToList();
        }
    }

    public static string GetTrackDisplayName(string trackPath)
    {
        if (string.IsNullOrEmpty(trackPath))
            return "Unknown Track";

        try
        {
            // Try to parse as URI first
            if (Uri.TryCreate(trackPath, UriKind.Absolute, out Uri uri))
            {
                // For HTTP URLs, use the last path segment or hostname
                if (!string.IsNullOrEmpty(uri.LocalPath) && uri.LocalPath != "/")
                {
                    string fileName = Path.GetFileName(uri.LocalPath);
                    if (!string.IsNullOrEmpty(fileName))
                    {
                        // URL decode the file name for better readability
                        try
                        {
                            string decodedName = System.Net.WebUtility.UrlDecode(fileName);
                            // Remove extension for cleaner display
                            return Path.GetFileNameWithoutExtension(decodedName) ?? decodedName;
                        }
                        catch
                        {
                            // Fallback to non-decoded name without extension
                            return Path.GetFileNameWithoutExtension(fileName) ?? fileName;
                        }
                    }
                }
                return uri.Host ?? trackPath;
            }
            else
            {
                // Local file path - just remove extension
                return Path.GetFileNameWithoutExtension(trackPath) ?? trackPath;
            }
        }
        catch
        {
            // Fallback: use the original path without extension
            return Path.GetFileNameWithoutExtension(trackPath) ?? trackPath;
        }
    }
}