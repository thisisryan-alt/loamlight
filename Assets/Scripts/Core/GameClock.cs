using UnityEngine;

namespace Loamlight.Core
{
    /// <summary>
    /// Gameplay time, separate from Time.timeScale so UI, audio and the camera keep running. Hitstop freezes it for a
    /// few real milliseconds on a solid hit; pausing (menus, death screen) freezes it until resumed. Every gameplay
    /// system (player, enemies, projectiles, tools) multiplies its delta by <see cref="Scale"/>.
    /// </summary>
    public static class GameClock
    {
        static float _hitstopEnd;

        public static bool Paused { get; set; }
        public static bool InHitstop => Time.unscaledTime < _hitstopEnd;
        public static float Scale => Paused || InHitstop ? 0f : 1f;
        public static float Delta => Time.deltaTime * Scale;

        /// <summary>Freeze gameplay for <paramref name="seconds"/> of real time (overlapping hits extend, never stack).</summary>
        public static void Hitstop(float seconds)
        {
            if (seconds <= 0f) return;
            _hitstopEnd = Mathf.Max(_hitstopEnd, Time.unscaledTime + Mathf.Min(seconds, 0.2f));
        }

        public static void Reset()
        {
            _hitstopEnd = 0f;
            Paused = false;
        }
    }
}
