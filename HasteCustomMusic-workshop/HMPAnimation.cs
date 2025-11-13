using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using static CustomMusicManager;

namespace HasteModPlaylist
{
    public static class HMPAnimation
    {
        private static string _animatedTrackName = "";
        private static string _originalTrackName = "";
        private static float _animationStartTime = 0f;
        private static float _animationDuration = 1.5f;
        private static bool _isAnimating = false;
        private static PlaylistType _animatingPlaylistType;
        private static int _animatingTrackIndex;

        // Animation frames (4 frames per second for 1.5 seconds = 6 frames)
        private readonly static string[] _animationFrames = new string[]
        {
            "->  ❤ ",
            " -> ❤ ",
            "  ->❤ ",
            "   -❤ ",
            "    ❤ ",
            "   [❤]"
        };
        public static void StartAddAnimation(PlaylistType playlistType, int trackIndex, string originalName)
        {
            // Prevent animation spam
            if (_isAnimating && Time.realtimeSinceStartup - _animationStartTime < _animationDuration)
                return;

            _isAnimating = true;
            _animationStartTime = Time.realtimeSinceStartup;
            _animatingPlaylistType = playlistType;
            _animatingTrackIndex = trackIndex;
            _originalTrackName = originalName;
            _animatedTrackName = _animationFrames[0];

            Debug.Log($"Started animation for {originalName}");
        }

        public static void  UpdateAnimation()
        {
            if (!_isAnimating) return;

            float elapsed = Time.realtimeSinceStartup - _animationStartTime;
            if (elapsed >= _animationDuration)
            {
                // Animation complete
                _isAnimating = false;
                _animatedTrackName = "";
                _originalTrackName = "";
                GUI.changed = true;
                return;
            }

            // Calculate current frame (4 FPS = 0.25 seconds per frame)
            int frameIndex = Mathf.FloorToInt(elapsed / 0.25f);
            frameIndex = Mathf.Clamp(frameIndex, 0, _animationFrames.Length - 1);

            _animatedTrackName = _animationFrames[frameIndex];
            GUI.changed = true; // Force GUI update
        }

        public static string GetAnimatedTrackName(PlaylistType playlistType, int trackIndex, string defaultName)
        {
            if (_isAnimating &&
                _animatingPlaylistType == playlistType &&
                _animatingTrackIndex == trackIndex)
            {
                return _animatedTrackName;
            }
            return defaultName;
        }
    }
}
