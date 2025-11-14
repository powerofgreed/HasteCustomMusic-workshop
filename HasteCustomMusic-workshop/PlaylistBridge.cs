using System.Collections;
using UnityEngine;

public static class PlaylistBridge
{
    // Sync from LandfallConfig to CustomMusicManager
    public static void SyncToCustomMusicManager()
    {
        try
        {
            // Sync Hybrid playlist
            CustomMusicManager.HybridTrackPaths.Clear();
            if (LandfallConfig.CurrentPlaylists.HybridPlaylist != null)
            {
                CustomMusicManager.HybridTrackPaths.AddRange(LandfallConfig.CurrentPlaylists.HybridPlaylist);
            }

            // Sync Streams playlist
            CustomMusicManager.StreamsTrackPaths.Clear();
            if (LandfallConfig.CurrentPlaylists.StreamsPlaylist != null)
            {
                CustomMusicManager.StreamsTrackPaths.AddRange(LandfallConfig.CurrentPlaylists.StreamsPlaylist);
            }

            // Recreate playlist objects
            CustomMusicManager.CreateHybridPlaylistFromTracks();
            CustomMusicManager.CreateStreamsPlaylistFromTracks();

            if(LandfallConfig.CurrentConfig.ShowDebug)Debug.Log($"Playlist sync: {CustomMusicManager.HybridTrackPaths.Count} hybrid, {CustomMusicManager.StreamsTrackPaths.Count} streams");
        }
        catch (System.Exception ex)
        {
            Debug.LogError($"Error syncing playlists to CustomMusicManager: {ex}");
        }
    }

    // Sync from CustomMusicManager to LandfallConfig
    public static void SaveHybridPlaylistToConfig()
    {
        try
        {
            // Sync Hybrid playlist
            LandfallConfig.CurrentPlaylists.HybridPlaylist.Clear();
            if (CustomMusicManager.HybridTrackPaths != null)
            {
                LandfallConfig.CurrentPlaylists.HybridPlaylist.AddRange(CustomMusicManager.HybridTrackPaths);
            }
            LandfallConfig.SavePlaylists();
            if (LandfallConfig.CurrentConfig.ShowDebug) Debug.Log($"Playlist sync saved: {LandfallConfig.CurrentPlaylists.HybridPlaylist.Count} hybrid, {LandfallConfig.CurrentPlaylists.StreamsPlaylist.Count} streams");
        }
        catch (System.Exception ex)
        {
            Debug.LogError($"Error syncing playlists from CustomMusicManager: {ex}");
        }
    }
    


    // Save playlists with coroutine support
    public static IEnumerator SavePlaylistsCoroutine()
    {
        SaveHybridPlaylistToConfig();
        yield return null;
    }

    public static bool SyncHybridPlaylistToCustomMusicManager()
    {
        try
        {
            // Only sync Hybrid playlist - clear and replace
            CustomMusicManager.HybridTrackPaths.Clear();
            if (LandfallConfig.CurrentPlaylists.HybridPlaylist != null &&
                LandfallConfig.CurrentPlaylists.HybridPlaylist.Count > 0)
            {
                CustomMusicManager.HybridTrackPaths.AddRange(LandfallConfig.CurrentPlaylists.HybridPlaylist);
                CustomMusicManager.CreateHybridPlaylistFromTracks();
                Debug.Log($"Hybrid playlist sync: {CustomMusicManager.HybridTrackPaths.Count} tracks");
                return true;
            }

            Debug.Log("No Hybrid tracks to sync");
            return false;
        }
        catch (System.Exception ex)
        {
            Debug.LogError($"Error syncing Hybrid playlist to CustomMusicManager: {ex}");
            return false;
        }
    }

    public static bool SyncStreamsPlaylistToCustomMusicManager()
    {
        try
        {
            // Only sync Streams playlist - clear and replace
            CustomMusicManager.StreamsTrackPaths.Clear();
            if (LandfallConfig.CurrentPlaylists.StreamsPlaylist != null &&
                LandfallConfig.CurrentPlaylists.StreamsPlaylist.Count > 0)
            {
                CustomMusicManager.StreamsTrackPaths.AddRange(LandfallConfig.CurrentPlaylists.StreamsPlaylist);
                CustomMusicManager.CreateStreamsPlaylistFromTracks();
                Debug.Log($"Streams playlist sync: {CustomMusicManager.StreamsTrackPaths.Count} streams");
                return true;
            }

            Debug.Log("No Streams tracks to sync");
            return false;
        }
        catch (System.Exception ex)
        {
            Debug.LogError($"Error syncing Streams playlist to CustomMusicManager: {ex}");
            return false;
        }
    }

   
}