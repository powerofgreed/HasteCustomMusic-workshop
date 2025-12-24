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
            // Check if it's an in-game track first
            if (trackPath.StartsWith("in-game:", StringComparison.OrdinalIgnoreCase))
            {
                string pathWithoutPrefix = trackPath[8..];
                string[] parts = pathWithoutPrefix.Split('/');
                if (parts.Length == 2)
                {
                    string playlistName = parts[0];
                    int trackId = int.Parse(parts[1]);
                    return $"In-game №{trackId} from {playlistName}";
                }
                return $"In-game track: {pathWithoutPrefix}";
            }

            // Try to parse as URI first
            if (Uri.TryCreate(trackPath, UriKind.Absolute, out Uri uri))
            {
                // For radio streams/URLs
                if (uri.Scheme.StartsWith("http", StringComparison.OrdinalIgnoreCase) ||
                    uri.Scheme.StartsWith("ftp", StringComparison.OrdinalIgnoreCase) ||
                    uri.Scheme.StartsWith("rtmp", StringComparison.OrdinalIgnoreCase) ||
                    uri.Scheme.StartsWith("rtsp", StringComparison.OrdinalIgnoreCase))
                {
                    // Build a display name from the host and path
                    string displayName = "";

                    // Add host (without www. prefix for cleaner display)
                    string host = uri.Host;
                    if (host.StartsWith("www.", StringComparison.OrdinalIgnoreCase))
                        host = host[4..];

                    displayName = host;

                    // Add port if not default
                    if (!uri.IsDefaultPort)
                        displayName += $":{uri.Port}";

                    // Add first non-empty path segment if it exists and isn't just a number
                    if (!string.IsNullOrEmpty(uri.AbsolutePath) && uri.AbsolutePath != "/")
                    {
                        var pathSegments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
                        if (pathSegments.Length > 0)
                        {
                            string firstSegment = pathSegments[0];

                            // Skip numeric-only segments (like "144" in your example)
                            if (!int.TryParse(firstSegment, out _))
                            {
                                // Clean up the segment
                                string cleanSegment = firstSegment;

                                // Remove common streaming path names for cleaner display
                                string[] commonStreamNames = { "stream", "listen", "live", "radio", "audio" };
                                foreach (var commonName in commonStreamNames)
                                {
                                    if (cleanSegment.Equals(commonName, StringComparison.OrdinalIgnoreCase))
                                    {
                                        cleanSegment = "";
                                        break;
                                    }
                                }

                                if (!string.IsNullOrEmpty(cleanSegment))
                                    displayName += $"/{cleanSegment}";
                            }
                            else if (pathSegments.Length > 1)
                            {
                                // If first segment is numeric but there's a second, use that
                                string secondSegment = pathSegments[1];
                                if (!int.TryParse(secondSegment, out _))
                                {
                                    string cleanSegment = secondSegment;
                                    string[] commonStreamNames = { "stream", "listen", "live", "radio", "audio" };
                                    foreach (var commonName in commonStreamNames)
                                    {
                                        if (cleanSegment.Equals(commonName, StringComparison.OrdinalIgnoreCase))
                                        {
                                            cleanSegment = "";
                                            break;
                                        }
                                    }

                                    if (!string.IsNullOrEmpty(cleanSegment))
                                        displayName += $"/{cleanSegment}";
                                }
                            }
                        }
                    }

                    // If we still have a very short name, try to get more info
                    if (displayName.Length < 10 && !string.IsNullOrEmpty(uri.AbsolutePath))
                    {
                        // Use the last non-numeric segment
                        var pathSegments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
                        for (int i = pathSegments.Length - 1; i >= 0; i--)
                        {
                            if (!int.TryParse(pathSegments[i], out _) &&
                                !string.IsNullOrEmpty(pathSegments[i]) &&
                                pathSegments[i].Length > 2)
                            {
                                displayName += $"/{pathSegments[i]}";
                                break;
                            }
                        }
                    }

                    // Final fallback - if still too short, use the full host
                    if (displayName.Length < 6)
                        displayName = uri.Host;

                    return displayName.Trim('/');
                }
                else
                {
                    // For non-web URIs (like file://)
                    if (!string.IsNullOrEmpty(uri.LocalPath) && uri.LocalPath != "/")
                    {
                        string fileName = Path.GetFileName(uri.LocalPath);
                        if (!string.IsNullOrEmpty(fileName))
                        {
                            try
                            {
                                string decodedName = System.Net.WebUtility.UrlDecode(fileName);
                                return Path.GetFileNameWithoutExtension(decodedName) ?? decodedName;
                            }
                            catch
                            {
                                return Path.GetFileNameWithoutExtension(fileName) ?? fileName;
                            }
                        }
                    }
                    return uri.Host ?? trackPath;
                }
            }
            else
            {
                // Local file path (not a URI)
                return Path.GetFileNameWithoutExtension(trackPath) ?? trackPath;
            }
        }
        catch
        {
            // Fallback: use the original path without extension
            string fallback = Path.GetFileNameWithoutExtension(trackPath) ?? trackPath;

            // Remove protocol prefixes for cleaner display
            string[] prefixes = { "https://", "http://", "ftp://", "rtmp://", "rtsp://" };
            foreach (var prefix in prefixes)
            {
                if (fallback.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    fallback = fallback[prefix.Length..];
                    break;
                }
            }

            // Truncate if too long
            if (fallback.Length > 50)
                fallback = fallback[..47] + "...";

            return fallback;
        }
    }
}