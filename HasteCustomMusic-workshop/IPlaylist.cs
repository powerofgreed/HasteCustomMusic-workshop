using Landfall.Haste.Music;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEngine;
using static CustomMusicManager;

public interface IPlaylist
{
    string Name { get; }
    PlaylistType Type { get; }
    int TrackCount { get; }
    int CurrentTrackIndex { get; set; }
    List<int> ShuffledOrder { get; }
    List<int> PlayedTracks { get; }

    string GetTrackDisplayName(int index);
    string GetTrackPath(int index);
    AudioClip GetAudioClip(int index); // For preloaded playlists

    void PlayTrack(int index);
    void PlayNext();
    void PlayPrevious();

    void InitializeShuffle();
    void UpdateShuffleState();

    bool CanPlayTrack(int index);
    bool IsTrackAvailable(int index);
}
public abstract class BasePlaylist : IPlaylist
{

    public abstract string Name { get; }
    public abstract PlaylistType Type { get; }
    public abstract int TrackCount { get; }
    public int CurrentTrackIndex { get; set; }
    public List<int> ShuffledOrder { get; set; } = new List<int>();
    public List<int> PlayedTracks { get; protected set; } = new List<int>();

    public abstract string GetTrackDisplayName(int index);
    public abstract string GetTrackPath(int index);
    public abstract AudioClip GetAudioClip(int index);

    public virtual void PlayTrack(int index)
    {
        if (!CanPlayTrack(index)) return;

        CurrentTrackIndex = index;
        UpdateShuffleState();

        if (IsStreamingPlaylist())
        {
            string path = GetTrackPath(index);
            if (!string.IsNullOrEmpty(path))
            {
                CustomMusicManager.StartStreaming(path);
            }
        }
        else
        {
            // For preloaded playlists, use Unity's MusicPlaylist system
            var playlist = GetUnityPlaylist();
            if (playlist != null)
            {
                IsUserInitiatedChange = true;
                MusicPlayer.Instance?.ChangePlaylist(playlist, index, true);
            }
        }
    }

    public virtual void PlayNext()
    {
        if (TrackCount == 0) return; // Prevent division by zero

        switch (CustomMusicManager.CurrentPlayOrder)
        {
            case CustomMusicManager.PlayOrder.Sequential:
                PlayNextSequential();
                break;
            case CustomMusicManager.PlayOrder.Loop:
                PlayTrack(CurrentTrackIndex);
                break;
            case CustomMusicManager.PlayOrder.Random:
                PlayNextRandom();
                break;
        }
    }

    public virtual void PlayPrevious()
    {
        if (TrackCount == 0) return;

        int previousIndex = (CurrentTrackIndex - 1 + TrackCount) % TrackCount;
        PlayTrack(previousIndex);
    }

    public virtual void ResetShuffle()
    {
        ShuffledOrder.Clear();
        PlayedTracks.Clear();
        _shuffleIndex = 0;
        Debug.Log($"{Name} shuffle state reset");
    }

    protected virtual void PlayNextSequential()
    {
        if (TrackCount == 0) return;

        if (ShuffledOrder.Count > 0)
        {
            // Use shuffle order for sequential playback
            _shuffleIndex = (_shuffleIndex + 1) % ShuffledOrder.Count;
            PlayTrack(ShuffledOrder[_shuffleIndex]);
        }
        else
        {
            // Fallback to normal sequential
            int nextIndex = (CurrentTrackIndex + 1) % TrackCount;
            PlayTrack(nextIndex);
        }
    }


    protected virtual void PlayNextRandom()
    {
        if (TrackCount == 0) return;

        if (PlayedTracks.Count >= TrackCount)
            PlayedTracks.Clear();

        var available = new List<int>();
        for (int i = 0; i < TrackCount; i++)
            if (!PlayedTracks.Contains(i)) available.Add(i);

        if (available.Count > 0)
        {
            int rnd = UnityEngine.Random.Range(0, available.Count);
            PlayTrack(available[rnd]);
            PlayedTracks.Add(available[rnd]);

            // Update shuffle index if we're using shuffle order
            if (ShuffledOrder.Count > 0)
            {
                _shuffleIndex = ShuffledOrder.IndexOf(available[rnd]);
                if (_shuffleIndex < 0)
                {
                    ShuffledOrder.Clear();
                    _shuffleIndex = 0;
                }
            }
        }
        else
        {
            PlayNextSequential();
        }
    }

    public virtual void InitializeShuffle()
    {
        if (TrackCount == 0) return;

        int currentTrackId = CurrentTrackIndex;
        ShuffledOrder = Enumerable.Range(0, TrackCount).ToList();

        var rng = new System.Random();
        for (int i = ShuffledOrder.Count - 1; i > 0; i--)
        {
            int j = rng.Next(i + 1);
            (ShuffledOrder[i], ShuffledOrder[j]) = (ShuffledOrder[j], ShuffledOrder[i]);
        }

        // Find current track position in shuffled order
        _shuffleIndex = ShuffledOrder.IndexOf(currentTrackId);
        if (_shuffleIndex < 0) _shuffleIndex = 0;

        PlayedTracks.Clear();
        Debug.Log($"{Name} shuffled; {TrackCount} tracks, current position {_shuffleIndex}");
    }
    protected int _shuffleIndex = 0;
    public int ShuffleIndex
    {
        get => _shuffleIndex;
        set => _shuffleIndex = value;
    }


    public virtual void UpdateShuffleState()
    {
        if (ShuffledOrder.Count > 0)
        {
            _shuffleIndex = ShuffledOrder.IndexOf(CurrentTrackIndex);
            if (_shuffleIndex < 0)
            {
                // Current track not in shuffle order, reset shuffle
                ShuffledOrder.Clear();
                _shuffleIndex = 0;
            }
        }

        if (CustomMusicManager.CurrentPlayOrder == CustomMusicManager.PlayOrder.Random &&
            !PlayedTracks.Contains(CurrentTrackIndex))
        {
            PlayedTracks.Add(CurrentTrackIndex);
        }
    }
    public virtual void SyncShuffleFrom(BasePlaylist sourcePlaylist)
    {
        if (sourcePlaylist == null) return;

        // Copy the shuffle state internally
        ShuffledOrder = new List<int>(sourcePlaylist.ShuffledOrder);
        _shuffleIndex = sourcePlaylist._shuffleIndex;
        CurrentTrackIndex = sourcePlaylist.CurrentTrackIndex;

        Debug.Log($"{Name} shuffle state synced from {sourcePlaylist.Name}");
    }

    public virtual void SetShuffleIndex(int newIndex)
    {
        if (newIndex >= 0 && newIndex < ShuffledOrder.Count)
        {
            _shuffleIndex = newIndex;
            CurrentTrackIndex = ShuffledOrder[newIndex];
        }
    }
    public virtual void CopyShuffleStateFrom(BasePlaylist sourcePlaylist)
    {
        if (sourcePlaylist == null) return;

        ShuffledOrder = new List<int>(sourcePlaylist.ShuffledOrder);
        PlayedTracks = new List<int>(sourcePlaylist.PlayedTracks);
        _shuffleIndex = sourcePlaylist._shuffleIndex;
        CurrentTrackIndex = sourcePlaylist.CurrentTrackIndex;
    }

    public virtual bool CanPlayTrack(int index)
    {
        return index >= 0 && index < TrackCount && IsTrackAvailable(index);
    }

    public virtual bool IsTrackAvailable(int index)
    {
        return true; // Override in derived classes for specific checks
    }

    protected virtual bool IsStreamingPlaylist()
    {
        return true; // Override in preloaded playlists
    }

    protected virtual MusicPlaylist GetUnityPlaylist()
    {
        return null; // Override in playlists that use Unity's MusicPlaylist system
    }
}
public class DefaultPlaylist : BasePlaylist
{
    public override string Name => "Default Playlist";
    public override PlaylistType Type => PlaylistType.Default;

    public override int TrackCount
    {
        get
        {
            // Use stored default playlist instead of current game playlist
            var playlist = GetStoredDefaultPlaylist();
            return playlist?.tracks?.Length ?? 0;
        }
    }

    public override string GetTrackDisplayName(int index)
    {
        var track = GetTrack(index);
        return track?.track?.name ?? $"Track {index + 1}";
    }

    public override string GetTrackPath(int index) => null;

    public override AudioClip GetAudioClip(int index)
    {
        return GetTrack(index)?.track;
    }

    public override void PlayTrack(int index)
    {
        CustomMusicManager.PlayDefaultTrack(index);
        CurrentTrackIndex = index;
    }

    public override bool IsTrackAvailable(int index)
    {
        return GetTrack(index)?.track != null;
    }

    protected override bool IsStreamingPlaylist() => false;

    protected override MusicPlaylist GetUnityPlaylist()
    {
        return GetStoredDefaultPlaylist();
    }

    private MusicPlaylist.MusicPlaylistTrack GetTrack(int index)
    {
        var playlist = GetStoredDefaultPlaylist();
        return (playlist?.tracks != null && index < playlist.tracks.Length) ? playlist.tracks[index] : null;
    }

    private MusicPlaylist GetStoredDefaultPlaylist()
    {
        // Try to get the stored default playlist first
        if (CustomMusicManager._lastAttemptedDefaultPlaylist != null)
            return CustomMusicManager._lastAttemptedDefaultPlaylist;
        if (CustomMusicManager._prevDefaultPlaylist != null)
            return CustomMusicManager._prevDefaultPlaylist;

        // Fallback to current game playlist
        return MusicPlayer.Instance?.currentlyPlaying?.playlist;
    }

    // Override shuffle for default playlist to use game's random system
    public override void InitializeShuffle()
    {
        // Default playlist uses game's internal shuffle, so we don't override it
        Debug.Log("Default playlist uses game's internal shuffle system");
    }
}
// CustomPlaylist.cs - Use stored preload state instead of config
public class LocalPlaylist : BasePlaylist
{
    public override string Name => "Local Playlist";
    public override PlaylistType Type => PlaylistType.Local;

    public override int TrackCount
    {
        get
        {
            if (CustomMusicManager.IsLocalPlaylistPreloaded)
            {
                return CustomMusicManager.LocalTracks?.Count ?? 0;
            }
            else
            {
                return CustomMusicManager.LocalTrackPaths?.Count ?? 0;
            }
        }
    }

    public override string GetTrackDisplayName(int index)
    {
        if (index < 0 || index >= TrackCount) return "Invalid Track";

        if (CustomMusicManager.IsLocalPlaylistPreloaded)
        {
            var clip = CustomMusicManager.LocalTracks?[index];
            return clip?.name ?? $"Track {index + 1}";
        }
        else
        {
            var path = CustomMusicManager.LocalTrackPaths?[index];
            return path != null ? Path.GetFileNameWithoutExtension(path) : $"Track {index + 1}";
        }
    }

    public override string GetTrackPath(int index)
    {
        if (!CustomMusicManager.IsLocalPlaylistPreloaded &&
            CustomMusicManager.LocalTrackPaths != null &&
            index < CustomMusicManager.LocalTrackPaths.Count)
        {
            return CustomMusicManager.LocalTrackPaths[index];
        }
        return null;
    }

    public override AudioClip GetAudioClip(int index)
    {
        if (CustomMusicManager.IsLocalPlaylistPreloaded &&
            CustomMusicManager.LocalTracks != null &&
            index < CustomMusicManager.LocalTracks.Count)
        {
            return CustomMusicManager.LocalTracks[index];
        }
        return null;
    }

    public override void PlayTrack(int index)
    {
        if (!CanPlayTrack(index))
        {
            Debug.LogWarning($"Cannot play track {index} from Local playlist");
            return;
        }

        CurrentTrackIndex = index;
        UpdateShuffleState();

        if (!CustomMusicManager.IsLocalPlaylistPreloaded)
        {
            // On-demand mode - stream the file
            string path = GetTrackPath(index);
            if (!string.IsNullOrEmpty(path))
            {
                CustomMusicManager.StartStreaming(path);
                CustomMusicManager.CurrentPlaybackPlaylistType = PlaylistType.Local;
                CustomMusicManager.CurrentTrackIndex = index;
            }
        }
        else
        {
            // Preload mode - use Unity's MusicPlaylist system
            if (CustomMusicManager.LocalPlaylist != null)
            {
                IsUserInitiatedChange = true;
                MusicPlayer.Instance?.ChangePlaylist(CustomMusicManager.LocalPlaylist, index, true);
                CustomMusicManager.CurrentPlaybackPlaylistType = PlaylistType.Local;
                CustomMusicManager.CurrentTrackIndex = index;
            }
        }
    }

    public override bool CanPlayTrack(int index)
    {
        return index >= 0 && index < TrackCount && IsTrackAvailable(index);
    }

    public override bool IsTrackAvailable(int index)
    {
        if (CustomMusicManager.IsLocalPlaylistPreloaded)
        {
            return CustomMusicManager.LocalTracks?[index] != null;
        }
        else
        {
            return CustomMusicManager.LocalTrackPaths?[index] != null;
        }
    }

    protected override bool IsStreamingPlaylist() => !CustomMusicManager.IsLocalPlaylistPreloaded;

    protected override MusicPlaylist GetUnityPlaylist()
    {
        return CustomMusicManager.IsLocalPlaylistPreloaded ? CustomMusicManager.LocalPlaylist : null;
    }
}
public class HybridPlaylist : BasePlaylist
{
    public override string Name => "My Playlist";
    public override PlaylistType Type => PlaylistType.Hybrid;

    public override int TrackCount => CustomMusicManager.HybridTrackPaths.Count;

    public override string GetTrackDisplayName(int index)
    {
        if (index < 0 || index >= CustomMusicManager.HybridTrackPaths.Count)
            return "Unknown Track";

        var url = CustomMusicManager.HybridTrackPaths[index];
        try
        {
            if (url.StartsWith("in-game:", StringComparison.OrdinalIgnoreCase))
            {
                string pathWithoutPrefix = url[8..]; // Remove "in-game:"
                string[] parts = pathWithoutPrefix.Split('/');
                if (parts.Length == 2)
                {
                    string playlistName = parts[0];
                    int trackId = int.Parse(parts[1]);
                    return $"In-game №{trackId} from {playlistName}";
                }
                return $"In-game track: {pathWithoutPrefix}";
            }
            else
            {
                var uri = new Uri(url);
                return Path.GetFileNameWithoutExtension(uri.LocalPath) ?? uri.Host;
            }
        }
        catch
        {
            return Path.GetFileNameWithoutExtension(url) ?? url;
        }
    }

    public override string GetTrackPath(int index)
    {
        return CustomMusicManager.HybridTrackPaths[index];
    }

    public override AudioClip GetAudioClip(int index) => null; // URLs are always streamed

    public override bool IsTrackAvailable(int index)
    {
        return index >= 0 && index < CustomMusicManager.HybridTrackPaths.Count &&
               !string.IsNullOrWhiteSpace(CustomMusicManager.HybridTrackPaths[index]);
    }
}
public class StreamsPlaylist : BasePlaylist
{
    public override string Name => "Stream Playlist";
    public override PlaylistType Type => PlaylistType.Streams;

    public override int TrackCount => CustomMusicManager.StreamsTrackPaths.Count;

    public override string GetTrackDisplayName(int index)
    {
        if (index < 0 || index >= CustomMusicManager.StreamsTrackPaths.Count)
            return "Unknown Stream";

        return PlaylistDecoder.GetTrackDisplayName(CustomMusicManager.StreamsTrackPaths[index]);
    }

    public override string GetTrackPath(int index)
    {
        return CustomMusicManager.StreamsTrackPaths[index];
    }

    public override AudioClip GetAudioClip(int index) => null; // Streams are always streamed

    public override bool IsTrackAvailable(int index)
    {
        return index >= 0 && index < CustomMusicManager.StreamsTrackPaths.Count &&
               !string.IsNullOrWhiteSpace(CustomMusicManager.StreamsTrackPaths[index]);
    }

    public override void PlayTrack(int index)
    {
        if (!CanPlayTrack(index))
        {
            Debug.LogWarning($"Cannot play stream track {index}");
            return;
        }

        CurrentTrackIndex = index;
        CustomMusicManager.PlayStreamsTrack(index);
    }

}
public static class PlaylistManager
{
    private static readonly Dictionary<PlaylistType, IPlaylist> _playlists = new Dictionary<PlaylistType, IPlaylist>();
    private static IPlaylist _currentPlaylist;

    static PlaylistManager()
    {
        InitializePlaylists();
    }

    public static void InitializePlaylists()
    {
        _playlists.Clear();

        _playlists[PlaylistType.Default] = new DefaultPlaylist();
        _playlists[PlaylistType.Local] = new LocalPlaylist();
        _playlists[PlaylistType.Hybrid] = new HybridPlaylist();
        _playlists[PlaylistType.Streams] = new StreamsPlaylist();

        _currentPlaylist = _playlists[PlaylistType.Default];
    }

    public static IPlaylist CurrentPlaylist
    {
        get => _currentPlaylist;
        set
        {
            if (value != null && value != _currentPlaylist)
            {
                _currentPlaylist = value;
                CustomMusicManager.CurrentPlaybackPlaylistType = value.Type;
            }
        }
    }

    public static PlaylistType CurrentPlaylistType
    {
        get => _currentPlaylist?.Type ?? PlaylistType.Default;
        set => CurrentPlaylist = GetPlaylist(value);
    }

    public static IPlaylist GetPlaylist(PlaylistType type)
    {
        return _playlists.TryGetValue(type, out var playlist) ? playlist : _playlists[PlaylistType.Default];
    }

    // Unified methods that work with any playlist
    public static void PlayTrack(int trackIndex)
    {
        if (_currentPlaylist == null)
        {
            Debug.LogWarning("No current playlist set in PlaylistManager");
            return;
        }

        if (_currentPlaylist.CanPlayTrack(trackIndex))
        {
            _currentPlaylist.PlayTrack(trackIndex);
            _currentPlaylist.UpdateShuffleState();

            // Update the appropriate current index in CustomMusicManager
            switch (_currentPlaylist.Type)
            {
                case PlaylistType.Local:
                    CustomMusicManager.CurrentTrackIndex = trackIndex;
                    break;
                case PlaylistType.Hybrid:
                    CustomMusicManager.HybridCurrentTrackIndex = trackIndex;
                    break;
                case PlaylistType.Streams:
                    CustomMusicManager.StreamsCurrentTrackIndex = trackIndex;
                    break;
            }
        }
        else
        {
            Debug.LogWarning($"Cannot play track {trackIndex} from {_currentPlaylist.Name}");
        }
    }

    public static void PlayCurrentTrack()
    {
        _currentPlaylist?.PlayTrack(_currentPlaylist.CurrentTrackIndex);
    }

    public static void PlayNextTrack()
    {
        if (_currentPlaylist == null || _currentPlaylist.TrackCount == 0)
        {
            Debug.LogWarning("No tracks available in current playlist");
            return;
        }

        _currentPlaylist.PlayNext();
    }

    public static void PlayPreviousTrack()
    {
        _currentPlaylist?.PlayPrevious();
    }

    public static void InitializeShuffle()
    {
        if (_currentPlaylist != null)
        {
            _currentPlaylist.InitializeShuffle();

            // Update UI to reflect shuffle state
            GUI.changed = true;
        }
    }

    public static string GetTrackDisplayName(int index)
    {
        return _currentPlaylist?.GetTrackDisplayName(index) ?? "Unknown";
    }

    public static int GetTrackCount()
    {
        return _currentPlaylist?.TrackCount ?? 0;
    }

    public static int GetCurrentTrackIndex()
    {
        return _currentPlaylist?.CurrentTrackIndex ?? 0;
    }
}
