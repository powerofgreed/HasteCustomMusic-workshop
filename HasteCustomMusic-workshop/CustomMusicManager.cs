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
using Landfall.Haste.Music;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using static StreamingClip;

public class CustomMusicManager : MonoBehaviour
{
    public enum PlaybackMethod { UnityAudio, Streaming }

    public static PlaybackMethod CurrentPlaybackMethod { get; private set; } = PlaybackMethod.UnityAudio;

    // ------------------------------
    // Public collections and state
    // ------------------------------
    public static List<string> LocalTrackPaths { get; private set; } = new List<string>();
    public static List<string> HybridTrackPaths { get; private set; } = new List<string>();
    public static List<string> StreamsTrackPaths { get; private set; } = new List<string>();
    public static MusicPlaylist HybridPlaylist { get; set; }
    public static MusicPlaylist StreamsPlaylist { get; set; }
    public static List<AudioClip> LocalTracks { get; private set; } = new List<AudioClip>();
    public static MusicPlaylist LocalPlaylist { get; set; }

    public enum PlaylistType
    {
        Default,
        Local,
        Hybrid,
        Streams
    }

    public static bool IsLocalPlaylistPreloaded { get; private set; } = false;
    public static bool IsUserInitiatedChange { get; set; } = false;

    // Individual playlist active checks
    public static bool IsLocalPlaylistActive => CurrentPlaybackPlaylistType == PlaylistType.Local;
    public static bool IsHybridPlaylistActive => CurrentPlaybackPlaylistType == PlaylistType.Hybrid;
    public static bool IsStreamsPlaylistActive => CurrentPlaybackPlaylistType == PlaylistType.Streams;
    public static bool IsAnyCustomPlaylistActive => !(CurrentPlaybackPlaylistType == PlaylistType.Default);

    public static PlaylistType CurrentPlaybackPlaylistType
    {
        get => PlaylistManager.CurrentPlaylistType;
        set => PlaylistManager.CurrentPlaylistType = value;
    }

    public static int CurrentTrackIndex { get; set; } = 0;
    public static int HybridCurrentTrackIndex { get; set; } = 0;
    public static int StreamsCurrentTrackIndex { get; set; } = 0;
    public static StreamingClip _streamingInstance;

    // ------------------------------
    // Config-backed flags
    // ------------------------------
    public static bool LockCustomPlaylist
    {
        get => LandfallConfig.CurrentConfig.LockEnabled;
        set
        {
            LandfallConfig.CurrentConfig.LockEnabled = value;
        }
    }

    public enum PlayOrder { Sequential, Loop, Random }
    public static PlayOrder CurrentPlayOrder
    {
        get => LandfallConfig.CurrentPlayOrder;
        set => LandfallConfig.CurrentPlayOrder = value;
    }

    // ------------------------------
    // Loading state and progress
    // ------------------------------
    public static bool IsLoading { get; set; }
    public static float LoadProgress { get; set; } = 0f;
    public static int LoadedTracksCount { get; set; } = 0;
    public static int TotalTracksCount { get; set; } = 0;

    // ------------------------------
    // Shuffle state
    // ------------------------------
    public static List<int> shuffledOrder = new List<int>();
    public static List<int> playedTracks = new List<int>();
    public static int shuffleIndex = 0;
    private static int _lastLocalTrackIndex = 0;

    // ------------------------------
    // Events
    // ------------------------------
    public static event Action<float> OnLoadProgress;
    public static event Action<bool> OnLoadingStateChanged;
    public static event Action OnTracksLoaded;

    // ------------------------------
    // Private internals
    // ------------------------------
    private static CustomMusicManager _instance;
    private static bool _disposed;

    private static readonly Task _loadingTask;
    private static CancellationTokenSource _cts;
    private static readonly object _loadLock = new object();

    // Default playlist tracking for seamless switching
    public static MusicPlaylist _prevDefaultPlaylist;
    public static int _prevDefaultTrackIndex;
    public static MusicPlaylist _lastAttemptedDefaultPlaylist;
    public static int _lastAttemptedTrackIndex;

    public static void PlayTrack(int trackIndex) => PlaylistManager.PlayTrack(trackIndex);
    public static void PlayCurrentTrack() => PlaylistManager.PlayCurrentTrack();
    public static void PlayNextTrack() => PlaylistManager.PlayNextTrack();
    public static void PlayPreviousTrack() => PlaylistManager.PlayPreviousTrack();
    public static void InitializeShuffle() => PlaylistManager.InitializeShuffle();

    // ------------------------------
    // Singleton
    // ------------------------------
    public static CustomMusicManager Instance
    {
        get
        {
            if (_disposed) return null;
            if (_instance == null)
            {
                _instance = FindFirstObjectByType<CustomMusicManager>();
                if (_instance == null)
                {
                    var go = new GameObject("CustomMusicManager");
                    _instance = go.AddComponent<CustomMusicManager>();
                    DontDestroyOnLoad(go);
                }
            }
            return _instance;
        }
    }

    private void Awake()
    {
        if (_instance == null)
        {
            _instance = this;
            DontDestroyOnLoad(gameObject);
            _disposed = false;

        }
        else if (_instance != this)
        {
            Destroy(gameObject);
            return;
        }

        Harmony.CreateAndPatchAll(typeof(CustomMusicManager));
    }

    private void OnDestroy()
    {
        _disposed = true;
        SafeCancelAndCleanup().ConfigureAwait(false);
    }


    // ------------------------------
    // Public API: Loading
    // ------------------------------
    public static bool LoadLocalTracks(string directoryPath)
    {
        // Synchronous convenience: runs preload or on-demand metadata setup.
        // For large libraries prefer LoadTracksAsync to avoid blocking.
        try
        {
            IsLoading = true;
            UnloadLocalTracks();

            // Store preload state at load time
            IsLocalPlaylistPreloaded = LandfallConfig.CurrentConfig.PreloadEntirePlaylist;

            _lastLocalTrackIndex = 0;

            // Only create directory if it's the default path
            string defaultMusicPath = WorkshopHelper.DefaultMusicPath;
            bool isDefaultPath = string.Equals(directoryPath, defaultMusicPath, StringComparison.OrdinalIgnoreCase);

            if (!Directory.Exists(directoryPath))
            {
                if (isDefaultPath)
                {
                    try
                    {
                        Directory.CreateDirectory(directoryPath);
                        Debug.Log($"Created default directory: {directoryPath}");
                    }
                    catch (Exception ex)
                    {
                        Debug.LogError($"Failed to create default directory: {ex}");
                        return false;
                    }
                }
                else
                {
                    Debug.LogError($"Directory does not exist: {directoryPath}");
                    return false;
                }
            }

            var supported = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                // Core BASS formats
                ".wav", ".mp3", ".ogg", ".aif", ".aiff", ".wma",

                // Add‑on formats (require DLLs, but safe to list)
                ".flac",   // bassflac.dll
                ".opus",   // bassopus.dll
                ".m4a",    // bass_aac.dll
                ".aac",    // bass_aac.dll
                ".alac",   // bassalac.dll
                ".ac3",    // bass_ac3.dll
                ".spx",    // bass_spx.dll
                ".tta"     // bass_tta.dll
            };

            var files = Directory.GetFiles(directoryPath, "*.*")
                            .Where(f => supported.Contains(Path.GetExtension(f).ToLowerInvariant()))
                            .ToList();

            if (files.Count == 0)
            {
                Debug.LogWarning("No supported audio files found.");
                return false;
            }

            LocalTrackPaths.Clear();
            LocalTracks.Clear();

            if (IsLocalPlaylistPreloaded)
            {
                // Preload all tracks
                foreach (var file in files)
                {
                    try
                    {
                        if (IsFileLocked(file)) continue;
                        var clip = AudioLoader.LoadAudioFile(file);
                        if (clip != null)
                        {
                            clip.name = Path.GetFileNameWithoutExtension(file);
                            LocalTracks.Add(clip);
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.LogWarning($"Load failed for {Path.GetFileName(file)}: {ex.Message}");
                    }
                }
                TotalTracksCount = files.Count;
                LoadedTracksCount = LocalTracks.Count;
            }
            else
            {
                // On-demand mode
                LocalTracks.Clear();
                LocalTrackPaths.Clear();
                foreach (var f in files)
                {
                    LocalTrackPaths.Add(f);
                    LocalTracks.Add(null); // placeholder
                }
                TotalTracksCount = files.Count;
                LoadedTracksCount = 0;
            }

            CreateLocalPlaylistFromTracks();
            CurrentTrackIndex = 0;
            OnTracksLoaded?.Invoke();

            return IsLocalPlaylistPreloaded ? (LoadedTracksCount > 0) : (TotalTracksCount > 0);
        }
        catch (Exception e)
        {
            Debug.LogError($"Critical error loading local tracks: {e}");
            return false;
        }
        finally
        {
            IsLoading = false;
            OnLoadingStateChanged?.Invoke(false);
        }
    }

    // ------------------------------
    // Playlist building
    // ------------------------------
    public static void CreateLocalPlaylistFromTracks()
    {
        try
        {
            if (LocalPlaylist != null)
            {
                UnityEngine.Object.DestroyImmediate(LocalPlaylist);
                LocalPlaylist = null;
            }

            LocalPlaylist = ScriptableObject.CreateInstance<MusicPlaylist>();
            LocalPlaylist.name = "Local_Playlist";
            LocalPlaylist.playRandom = false;
            LocalPlaylist.tracks = new MusicPlaylist.MusicPlaylistTrack[LocalTracks.Count];

            for (int i = 0; i < LocalTracks.Count; i++)
            {
                LocalPlaylist.tracks[i] = new MusicPlaylist.MusicPlaylistTrack
                {
                    track = LocalTracks[i],
                    fadeTime = 1.0f,
                    previousTrackFadeOutTime = 1.0f,
                    totalBars = 32,
                    musicTail = 0f,
                    transitionPointsInBars = Array.Empty<int>()
                };
            }
        }
        catch (Exception ex)
        {
            Debug.LogError($"CreatePlaylistFromTracks() error: {ex}");
            throw;
        }
    }

    public static void CreateHybridPlaylistFromTracks()
    {
        try
        {
            if (HybridPlaylist != null)
            {
                UnityEngine.Object.DestroyImmediate(HybridPlaylist);
                HybridPlaylist = null;
            }

            // Create a minimal playlist for Unity integration
            // Note: Hybrid tracks are played via streaming, not preloaded
            HybridPlaylist = ScriptableObject.CreateInstance<MusicPlaylist>();
            HybridPlaylist.name = "Hybrid_Playlist";
            HybridPlaylist.playRandom = false;
            HybridPlaylist.tracks = new MusicPlaylist.MusicPlaylistTrack[HybridTrackPaths.Count];

            for (int i = 0; i < HybridTrackPaths.Count; i++)
            {
                HybridPlaylist.tracks[i] = new MusicPlaylist.MusicPlaylistTrack
                {
                    track = null, // Hybrid tracks don't have preloaded AudioClips
                    fadeTime = 1.0f,
                    previousTrackFadeOutTime = 1.0f,
                    totalBars = 32,
                    musicTail = 0f,
                    transitionPointsInBars = Array.Empty<int>()
                };
            }

            Debug.Log($"Created Hybrid playlist with {HybridTrackPaths.Count} tracks");
        }
        catch (Exception ex)
        {
            Debug.LogError($"CreateHybridPlaylistFromTracks() error: {ex}");
        }
    }

    // ------------------------------
    // Streaming
    // ------------------------------
    public static void StartStreaming(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            Debug.LogWarning("StartStreaming(): empty path");
            return;
        }

        if (Instance == null) return;

        // Stop any existing playback first
        try { MusicPlayer.Instance?.StopPlaying(); } catch { }

        // Always create a new streaming instance to avoid state issues
        if (_streamingInstance != null)
        {
            try { _streamingInstance.StopStream(); } catch { }
            try { UnityEngine.Object.Destroy(_streamingInstance); } catch { }
            _streamingInstance = null;
        }

        _streamingInstance = MusicPlayer.Instance.m_AudioSourceCurrent.gameObject.AddComponent<StreamingClip>();

        // Set mode immediately for radio-like URLs
        if (!Path.HasExtension(path))
        {
            CurrentPlaybackMode = MusicPlayerMode.RadioStream;
        }

        try
        {
            _streamingInstance.StartStreamAsync(path);
            Debug.Log($"Streaming started: {path} (mode: {CurrentPlaybackMode})");
        }
        catch (Exception ex)
        {
            Debug.LogError($"Streaming start failed: {ex}");
            StopStreaming();
        }
    }

    public static void StopStreaming()
    {
        if (_streamingInstance != null)
        {
            try { _streamingInstance.StopStream(); } catch { }
            try { UnityEngine.Object.Destroy(_streamingInstance); } catch { }
            _streamingInstance = null;
            CurrentPlaybackMode = MusicPlayerMode.None;
            Debug.Log("Streaming stopped");
        }
    }

    public static class CustomMusicManagerExtensions
    {
        // All streaming helpers now use the authoritative StreamingClip.Instance when present.
        public static float GetStreamBufferPercent()
            => StreamingClip.Instance != null ? StreamingClip.Instance.GetBufferPercent() : 0f;

        public static float GetStreamCurrentTime()
            => StreamingClip.Instance != null ? StreamingClip.Instance.QueryBassCurrentSeconds() : 0f;

        public static float GetStreamTotalTime()
            => StreamingClip.Instance != null ? StreamingClip.Instance.QueryBassTotalSeconds() : 0f;

        public static void SeekStream(float timeInSeconds)
        {
            StreamingClip.Instance?.Seek(timeInSeconds);
        }

        public static bool IsStreamPlaying()
            => StreamingClip.Instance != null && StreamingClip.Instance.IsPlaying;

        // Add buffer status for debugging
        public static string GetStreamBufferStatus()
            => StreamingClip.Instance != null ? StreamingClip.Instance.GetBufferStatus() : "No stream";
    }


    // ------------------------------
    // Playback control
    // ------------------------------
    public static void PlayDefaultTrack(int trackIndex)
    {
        if (MusicPlayer.Instance == null) return;

        var player = MusicPlayer.Instance;
        var playlist = MusicDisplayBehaviour.GetStoredDefaultPlaylist();

        if (playlist == null)
        {
            Debug.LogWarning("No default playlist available");
            return;
        }

        if (trackIndex >= playlist.tracks.Length)
        {
            Debug.LogWarning($"Default track index {trackIndex} out of range");
            return;
        }

        // Update the default playlist's current track index
        if (PlaylistManager.GetPlaylist(PlaylistType.Default) is DefaultPlaylist defaultPlaylist)
        {
            defaultPlaylist.CurrentTrackIndex = trackIndex;
        }

        // Handle default playlist with random flag
        if (playlist.playRandom)
        {
            // Find position in shuffled list
            int position = player.randomTrackIds.IndexOf(trackIndex);
            if (position >= 0)
            {
                player.currentRandomId = position;
                IsUserInitiatedChange = true;
                player.ChangePlaylist(playlist, player.randomTrackIds[position], true);
            }
        }
        else
        {
            IsUserInitiatedChange = true;
            // Direct track access
            player.ChangePlaylist(playlist, trackIndex, true);
        }

        PlaylistManager.CurrentPlaylistType = PlaylistType.Default;
        CurrentPlaybackPlaylistType = PlaylistType.Default;

        Debug.Log($"Playing default playlist track {trackIndex}");
    }

    // ------------------------------
    // Streams Playlist Management
    // ------------------------------
    public static PlaybackMethod DetectPlaybackMethod(string trackPath, PlaylistType playlistType)
    {
        if (string.IsNullOrEmpty(trackPath))
            return PlaybackMethod.UnityAudio;

        // Handle different playlist types
        switch (playlistType)
        {
            case PlaylistType.Default:
                return PlaybackMethod.UnityAudio;

            case PlaylistType.Local:
                return IsLocalPlaylistPreloaded ? PlaybackMethod.UnityAudio : PlaybackMethod.Streaming;

            case PlaylistType.Hybrid:
                // Hybrid playlist path detection
                if (trackPath.StartsWith("in-game:", StringComparison.OrdinalIgnoreCase))
                    return PlaybackMethod.UnityAudio;

                if (trackPath.StartsWith("file://", StringComparison.OrdinalIgnoreCase) ||
                    !trackPath.Contains("://"))
                    return PlaybackMethod.Streaming;

                if (trackPath.StartsWith("http", StringComparison.OrdinalIgnoreCase) ||
                    trackPath.StartsWith("ftp", StringComparison.OrdinalIgnoreCase) ||
                    trackPath.StartsWith("https", StringComparison.OrdinalIgnoreCase))
                {
                    return PlaybackMethod.Streaming;
                }
                break;

            case PlaylistType.Streams:
                return PlaybackMethod.Streaming;
        }

        return PlaybackMethod.Streaming; // Default fallback
    }

    public static void PlayTrackWithMethod(int trackIndex, PlaylistType playlistType)
    {
        if (MusicPlayer.Instance == null) return;

        try
        {
            string trackPath = "";
            PlaybackMethod method = PlaybackMethod.UnityAudio;

            // Get track path and detect method based on playlist type
            switch (playlistType)
            {
                case PlaylistType.Default:
                    method = PlaybackMethod.UnityAudio;
                    PlayDefaultTrack(trackIndex);
                    break;

                case PlaylistType.Local:
                    if (IsLocalPlaylistPreloaded)
                    {
                        method = PlaybackMethod.UnityAudio;
                        if (LocalPlaylist != null)
                        {
                            IsUserInitiatedChange = true;
                            MusicPlayer.Instance.ChangePlaylist(LocalPlaylist, trackIndex, true);
                            CurrentTrackIndex = trackIndex;
                        }
                    }
                    else
                    {
                        method = PlaybackMethod.Streaming;
                        trackPath = LocalTrackPaths[trackIndex];
                        StartStreaming(trackPath);
                        CurrentTrackIndex = trackIndex;
                    }
                    break;

                case PlaylistType.Hybrid:
                    trackPath = HybridTrackPaths[trackIndex];
                    method = DetectPlaybackMethod(trackPath, playlistType);

                    if (method == PlaybackMethod.UnityAudio)
                    {
                        // Handle in-game tracks - parse playlist/track from path
                        // Format: "in-game:PlaylistName/TrackIndex"
                        if (trackPath.StartsWith("in-game:", StringComparison.OrdinalIgnoreCase))
                        {
                            // Parse and play in-game track
                            PlayInGameTrack(trackPath);
                        }
                        else
                        {
                            // Fallback to streaming
                            method = PlaybackMethod.Streaming;
                            StartStreaming(trackPath);
                        }
                    }
                    else
                    {
                        StartStreaming(trackPath);
                    }
                    HybridCurrentTrackIndex = trackIndex;
                    break;

                case PlaylistType.Streams:
                    method = PlaybackMethod.Streaming;
                    PlayStreamsTrack(trackIndex);
                    break;
            }

            CurrentPlaybackMethod = method;
            CurrentPlaybackPlaylistType = playlistType;
            PlaylistManager.CurrentPlaylistType = playlistType;

            Debug.Log($"Playing {playlistType} track {trackIndex} with method: {method}");
        }
        catch (Exception ex)
        {
            Debug.LogError($"Error in PlayTrackWithMethod: {ex}");
            IsUserInitiatedChange = false;
        }
    }

    // Helper method for in-game tracks (placeholder for now)
    private static void PlayInGameTrack(string trackPath)
    {
        try
        {
            // Parse in-game track path: "in-game:PlaylistName/TrackIndex"
            if (trackPath.StartsWith("in-game:", StringComparison.OrdinalIgnoreCase))
            {
                string pathWithoutPrefix = trackPath[8..]; // Remove "in-game:"
                string[] parts = pathWithoutPrefix.Split('/');

                if (parts.Length == 2)
                {
                    string playlistName = parts[0];
                    int trackIndex = int.Parse(parts[1]);

                    Debug.Log($"Looking for in-game playlist: {playlistName}, track: {trackIndex}");

                    // Find the game playlist by name
                    var allPlaylists = Resources.FindObjectsOfTypeAll<MusicPlaylist>();
                    MusicPlaylist targetPlaylist = null;

                    foreach (var playlist in allPlaylists)
                    {
                        if (playlist.name.Equals(playlistName, StringComparison.OrdinalIgnoreCase))
                        {
                            targetPlaylist = playlist;
                            break;
                        }
                    }

                    if (targetPlaylist != null)
                    {
                        if (trackIndex >= 0 && trackIndex < targetPlaylist.tracks.Length)
                        {
                            IsUserInitiatedChange = true;
                            MusicPlayer.Instance.ChangePlaylist(targetPlaylist, trackIndex, true);
                            Debug.Log($"Playing in-game track: {playlistName} - {trackIndex}");
                            return;
                        }
                        else
                        {
                            Debug.LogError($"Track index {trackIndex} out of range for playlist {playlistName}");
                        }
                    }
                    else
                    {
                        Debug.LogError($"Could not find in-game playlist: {playlistName}");
                    }
                }
                else
                {
                    Debug.LogError($"Invalid in-game track format: {trackPath}");
                }
            }
        }
        catch (Exception ex)
        {
            Debug.LogError($"Error playing in-game track {trackPath}: {ex}");
        }

        // Fallback: try streaming the path directly
        Debug.Log($"Falling back to streaming for: {trackPath}");
        StartStreaming(trackPath);
    }

    public static async Task LoadStreamsPlaylist(string playlistPath)
    {
        try
        {
            IsLoading = true;
            OnLoadingStateChanged?.Invoke(true);

            Debug.Log($"Attempting to load streams playlist: {playlistPath}");

            // Decode the playlist file first
            var tracks = PlaylistDecoder.DecodePlaylist(playlistPath);

            // Only clear and replace if we successfully found tracks
            if (tracks.Count > 0)
            {
                Debug.Log($"Successfully decoded {tracks.Count} tracks, clearing existing streams playlist");

                // Clear existing streams since we have valid new tracks
                await ClearStreamsPlaylist();

                await RunOnMainThread(() =>
                {
                    // Add new tracks
                    StreamsTrackPaths.AddRange(tracks);
                    StreamsCurrentTrackIndex = 0;

                    Debug.Log($"Loaded {tracks.Count} tracks from streams playlist");

                    // Create playlist object for Unity integration
                    CreateStreamsPlaylistFromTracks();

                });

                OnTracksLoaded?.Invoke();
            }
            else
            {
                Debug.LogWarning("No tracks found in playlist, keeping existing streams");
                // Don't clear existing playlist if decoding failed
            }
        }
        catch (Exception ex)
        {
            Debug.LogError($"Error loading streams playlist: {ex}");
            // Don't clear existing playlist on error
        }
        finally
        {
            IsLoading = false;
            OnLoadingStateChanged?.Invoke(false);
        }
    }

    public static void CreateStreamsPlaylistFromTracks()
    {
        try
        {
            if (StreamsPlaylist != null)
            {
                UnityEngine.Object.DestroyImmediate(StreamsPlaylist);
                StreamsPlaylist = null;
            }

            StreamsPlaylist = ScriptableObject.CreateInstance<MusicPlaylist>();
            StreamsPlaylist.name = "Streams_Playlist";
            StreamsPlaylist.playRandom = false;
            StreamsPlaylist.tracks = new MusicPlaylist.MusicPlaylistTrack[StreamsTrackPaths.Count];

            for (int i = 0; i < StreamsTrackPaths.Count; i++)
            {
                StreamsPlaylist.tracks[i] = new MusicPlaylist.MusicPlaylistTrack
                {
                    track = null, // Streams don't have preloaded AudioClips
                    fadeTime = 1.0f,
                    previousTrackFadeOutTime = 1.0f,
                    totalBars = 32,
                    musicTail = 0f,
                    transitionPointsInBars = Array.Empty<int>()
                };
            }
        }
        catch (Exception ex)
        {
            Debug.LogError($"CreateStreamsPlaylistFromTracks() error: {ex}");
        }
    }

    public static void PlayStreamsTrack(int trackIndex)
    {
        if (trackIndex < 0 || trackIndex >= StreamsTrackPaths.Count)
        {
            Debug.LogWarning($"Streams track index {trackIndex} out of range");
            return;
        }

        string streamUrl = StreamsTrackPaths[trackIndex];
        if (string.IsNullOrEmpty(streamUrl))
        {
            Debug.LogWarning($"Empty stream URL at index {trackIndex}");
            return;
        }

        StreamsCurrentTrackIndex = trackIndex;
        CurrentPlaybackPlaylistType = PlaylistType.Streams;
        PlaylistManager.CurrentPlaylistType = PlaylistType.Streams;

        StartStreaming(streamUrl);
        Debug.Log($"Playing streams track {trackIndex}: {streamUrl}");
    }

    // ------------------------------
    // Universal Playlist Clearing
    // ------------------------------

    public static async void ClearPlaylist(PlaylistType playlistType)
    {
        // Use Task.Run to move the operation to a background thread unaffected by pause
        await Task.Run(async () =>
        {
            try
            {
                switch (playlistType)
                {
                    case PlaylistType.Local:
                        await ClearLocalPlaylist();
                        break;
                    case PlaylistType.Hybrid:
                        await ClearHybridPlaylist();
                        break;
                    case PlaylistType.Streams:
                        await ClearStreamsPlaylist();
                        break;
                    default:
                        Debug.LogWarning($"Clearing {playlistType} playlist is not supported");
                        break;
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"Error in ClearPlaylist: {ex}");
            }
        });
    }

    private static async Task ClearLocalPlaylist()
    {
        Debug.Log("Clearing Local playlist");

        // Stop playback if Local playlist is currently playing
        if (CurrentPlaybackPlaylistType == PlaylistType.Local)
        {
            StopPlaybackForPlaylist(PlaylistType.Local);
        }

        await SafeUnloadLocalTracks();

        // Reset local playlist state
        await RunOnMainThread(() =>
        {
            LocalTrackPaths?.Clear();
            CurrentTrackIndex = 0;
            _lastLocalTrackIndex = 0;

            // Reset shuffle state for local playlist
            var localPlaylist = PlaylistManager.GetPlaylist(PlaylistType.Local) as BasePlaylist;
            localPlaylist?.ResetShuffle();
        });
    }

    private static async Task ClearHybridPlaylist()
    {
        Debug.Log("Clearing Hybrid playlist");

        // Stop playback if Hybrid playlist is currently playing
        if (CurrentPlaybackPlaylistType == PlaylistType.Hybrid)
        {
            StopPlaybackForPlaylist(PlaylistType.Hybrid);
        }

        await RunOnMainThread(() =>
        {
            HybridTrackPaths?.Clear();
            HybridCurrentTrackIndex = 0;

            // Reset shuffle state for hybrid playlist
            var hybridPlaylist = PlaylistManager.GetPlaylist(PlaylistType.Hybrid) as BasePlaylist;
            hybridPlaylist?.ResetShuffle();

            // Destroy hybrid playlist asset if it exists
            if (HybridPlaylist != null)
            {
                UnityEngine.Object.DestroyImmediate(HybridPlaylist);
                HybridPlaylist = null;
            }
        });
    }

    private static async Task ClearStreamsPlaylist()
    {
        Debug.Log("ClearStreamsPlaylist - Starting clear operation");

        // Stop playback if Streams playlist is currently playing
        if (CurrentPlaybackPlaylistType == PlaylistType.Streams)
        {
            StopPlaybackForPlaylist(PlaylistType.Streams);
        }

        await RunOnMainThread(() =>
        {
            int previousCount = StreamsTrackPaths.Count;
            StreamsTrackPaths.Clear();
            StreamsCurrentTrackIndex = 0;

            Debug.Log($"Cleared {previousCount} streams from playlist");

            // Reset shuffle state for streams playlist
            var streamsPlaylist = PlaylistManager.GetPlaylist(PlaylistType.Streams) as BasePlaylist;
            streamsPlaylist?.ResetShuffle();

            // Destroy streams playlist asset if it exists
            if (StreamsPlaylist != null)
            {
                UnityEngine.Object.DestroyImmediate(StreamsPlaylist);
                StreamsPlaylist = null;
            }
        });
    }

    private static void StopPlaybackForPlaylist(PlaylistType playlistType)
    {
        // Stop streaming if active
        StopStreaming();

        // Stop Unity audio playback
        if (MusicPlayer.Instance != null)
        {
            try
            {
                MusicPlayer.Instance.StopPlaying();
            }
            catch (Exception e)
            {
                Debug.LogWarning($"Error stopping playback: {e.Message}");
            }
        }

        Debug.Log($"Stopped playback for {playlistType} playlist");
    }

    private static async Task SafeUnloadLocalTracks()
    {
        if (_disposed) return;

        try
        {
            // Cancel any ongoing load
            lock (_loadLock)
            {
                _cts?.Cancel();
                _cts?.Dispose();
                _cts = null;
            }
            if (_loadingTask != null && !_loadingTask.IsCompleted)
            {
                try { await _loadingTask; }
                catch (OperationCanceledException) { }
                catch (Exception ex) { Debug.LogError($"Loading task error: {ex}"); }
            }

            // Stop any active streaming
            StopStreaming();

            // Collect local clips to unload
            var clipsToUnload = new List<AudioClip>();
            var playlistToDestroy = LocalPlaylist;

            await RunOnMainThread(() =>
            {
                if (LocalTracks != null)
                {
                    clipsToUnload.AddRange(LocalTracks.Where(c => c != null));
                    LocalTracks.Clear();
                }


                LocalPlaylist = null;
            });

            if (clipsToUnload.Count > 0)
                await UnloadClipsAsync(clipsToUnload);

            await RunOnMainThread(() =>
            {
                if (playlistToDestroy != null)
                {
                    try { UnityEngine.Object.DestroyImmediate(playlistToDestroy); }
                    catch (Exception e) { Debug.LogWarning($"Destroy playlist error: {e.Message}"); }
                }

                LoadProgress = 0f;
                LoadedTracksCount = 0;
                TotalTracksCount = 0;
                IsLocalPlaylistPreloaded = false;
            });

            // Gentle GC for memory cleanup (only for local playlist with preloaded tracks)
            await Task.Run(async () =>
            {
                for (int i = 0; i < 2; i++)
                {
                    await Task.Delay(80);
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                    GC.Collect();
                }
            });

            await RunOnMainThread(() => Resources.UnloadUnusedAssets());
            await Task.Delay(150);

            Debug.Log("Local playlist tracks unloaded from memory");
        }
        catch (Exception ex)
        {
            Debug.LogError($"Unload error: {ex}");
        }
    }

    private static void UnloadLocalTracks()
    {
        foreach (var clip in LocalTracks) { try { clip?.UnloadAudioData(); } catch { } }
        LocalTracks.Clear();

        if (LocalPlaylist != null)
        {
            foreach (var track in LocalPlaylist.tracks)
            {
                if (track?.track != null)
                    track.track.UnloadAudioData();
            }
            UnityEngine.Object.Destroy(LocalPlaylist);
            LocalPlaylist = null;
        }

        shuffledOrder.Clear();
        playedTracks.Clear();
        IsLocalPlaylistPreloaded = false;
    }

    private static bool IsFileLocked(string filePath)
    {
        try
        {
            using (File.Open(filePath, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                return false;
            }
        }
        catch (IOException)
        {
            return true;
        }
    }

    private static async Task UnloadClipsAsync(List<AudioClip> clips)
    {
        if (clips == null || clips.Count == 0) return;

        for (int i = 0; i < clips.Count; i++)
        {
            var clip = clips[i];
            if (clip != null)
            {
                await RunOnMainThread(() =>
                {
                    try { clip.UnloadAudioData(); } catch (Exception ex) { Debug.LogWarning($"Unload audio data failed: {ex.Message}"); }
                    try { UnityEngine.Object.DestroyImmediate(clip, false); } catch (Exception ex) { Debug.LogWarning($"Destroy clip failed: {ex.Message}"); }
                });
                await Task.Delay(i % 5 == 0 ? 30 : 10);
            }
        }
    }

    private static async Task SafeCancelAndCleanup()
    {
        lock (_loadLock)
        {
            _cts?.Cancel();
            var oldCts = _cts;
            _cts = null;
            oldCts?.Dispose();
        }

        if (_loadingTask != null && !_loadingTask.IsCompleted)
        {
            try { await Task.WhenAny(_loadingTask, Task.Delay(5000)); }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Debug.LogError($"Cleanup error: {ex}"); }
        }

        await SafeUnloadLocalTracks();
    }

    // ------------------------------
    // Main-thread dispatcher
    // ------------------------------
    private static Task RunOnMainThread(Action action)
    {
        if (_disposed || Instance == null) return Task.CompletedTask;

        var tcs = new TaskCompletionSource<bool>();
        Instance.StartCoroutine(RunOnMainThreadCoroutine(action, tcs));
        return tcs.Task;
    }

    private static System.Collections.IEnumerator RunOnMainThreadCoroutine(Action action, TaskCompletionSource<bool> tcs)
    {
        yield return null;
        try { action(); tcs.SetResult(true); }
        catch (Exception ex) { Debug.LogError($"RunOnMainThread error: {ex}"); tcs.SetException(ex); }
    }

    // ------------------------------
    // Harmony patches 
    // ------------------------------
    [HarmonyPatch(typeof(MusicPlayer), "InitRandomAndPlay")]
    [HarmonyPrefix]
    private static bool InitRandomAndPlay_Prefix(MusicPlaylist newPlaylist)
    {
        try
        {
            Debug.Log($"InitRandomAndPlay called with: {newPlaylist?.name}");

            if (newPlaylist != null &&
                newPlaylist != LocalPlaylist &&
                newPlaylist != HybridPlaylist &&
                newPlaylist != StreamsPlaylist &&
                !IsUserInitiatedChange)
            {
                _lastAttemptedDefaultPlaylist = newPlaylist;
                _lastAttemptedTrackIndex = 0;
                Debug.Log($"Stored default playlist: {newPlaylist.name}");
            }

            if (LockCustomPlaylist &&
                IsAnyCustomPlaylistActive &&
                newPlaylist != GetCurrentActivePlaylist() &&
                !IsUserInitiatedChange)
            {
                Debug.Log($"Blocked InitRandomAndPlay to {newPlaylist?.name} (Locked to {CurrentPlaybackPlaylistType})");
                return false; // Block the original method from executing
            }
            IsUserInitiatedChange = false;

            if (newPlaylist != GetCurrentActivePlaylist() &&
                newPlaylist != LocalPlaylist &&
                newPlaylist != HybridPlaylist &&
                newPlaylist != StreamsPlaylist)
            {
                CurrentPlaybackPlaylistType = PlaylistType.Default;
                Debug.Log("Updated to Default playlist type");
            }

            return true;
        }
        catch (Exception ex)
        {
            // Critical: Log error but allow original method to run
            Debug.LogError($"Harmony patch InitRandomAndPlay_Prefix failed: {ex}");
            return true; // Allow original method to execute normally
        }
    }

    [HarmonyPatch(typeof(MusicPlayer), "ChangePlaylist")]
    [HarmonyPrefix]
    private static bool ChangePlaylist_Prefix(MusicPlaylist newPlaylist, int trackId = 0, bool forcePlay = false)
    {
        try
        {
            Debug.Log($"ChangePlaylist called with: {newPlaylist?.name}, track {trackId}");

            if (newPlaylist != null &&
                newPlaylist != LocalPlaylist &&
                newPlaylist != HybridPlaylist &&
                newPlaylist != StreamsPlaylist &&
                !IsUserInitiatedChange)
            {
                _lastAttemptedDefaultPlaylist = newPlaylist;
                _lastAttemptedTrackIndex = trackId;
                Debug.Log($"Stored default playlist: {newPlaylist.name}, track {trackId}");
            }

            if (LockCustomPlaylist &&
                IsAnyCustomPlaylistActive &&
                newPlaylist != GetCurrentActivePlaylist() &&
                !IsUserInitiatedChange)
            {
                Debug.Log($"Blocked ChangePlaylist to {newPlaylist?.name} (Locked to {CurrentPlaybackPlaylistType})");
                return false; // Block the original method from executing
            }
            IsUserInitiatedChange = false;

            if (newPlaylist == LocalPlaylist)
            {
                CurrentPlaybackPlaylistType = PlaylistType.Local;
                CurrentTrackIndex = trackId;
                Debug.Log("Now playing Local playlist");
            }
            else if (newPlaylist == HybridPlaylist)
            {
                CurrentPlaybackPlaylistType = PlaylistType.Hybrid;
                HybridCurrentTrackIndex = trackId;
                Debug.Log("Now playing Hybrid playlist");
            }
            else if (newPlaylist == StreamsPlaylist)
            {
                CurrentPlaybackPlaylistType = PlaylistType.Streams;
                StreamsCurrentTrackIndex = trackId;
                Debug.Log("Now playing Streams playlist");
            }
            else
            {
                CurrentPlaybackPlaylistType = PlaylistType.Default;
                Debug.Log("Now playing Default playlist");
            }

            return true;
        }
        catch (Exception ex)
        {
            // Critical: Log error but allow original method to run
            Debug.LogError($"Harmony patch ChangePlaylist_Prefix failed: {ex}");
            return true; // Allow original method to execute normally
        }
    }

    private static MusicPlaylist GetCurrentActivePlaylist()
    {
        return CurrentPlaybackPlaylistType switch
        {
            PlaylistType.Local => LocalPlaylist,
            PlaylistType.Hybrid => HybridPlaylist,
            PlaylistType.Streams => StreamsPlaylist,
            _ => null
        };
    }
}