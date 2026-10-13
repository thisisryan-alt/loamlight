using Loamlight.Core;
using UnityEngine;

namespace Loamlight.Game
{
    /// <summary>Picks the music track from the game state (boss, night or underground, day) and crossfades to it.</summary>
    public sealed class MusicDirector : MonoBehaviour
    {
        public const int Day = 0, Deep = 1, Boss = 2;

        GameBootstrap _g;
        float _next;

        public int Wanted { get; private set; } = -1;

        public void Init(GameBootstrap g) => _g = g;

        void Update()
        {
            if (_g == null || !_g.Ready || Time.unscaledTime < _next) return;
            _next = Time.unscaledTime + 1f;
            float surface = World.Generation.WorldGenerator.SurfaceLevel(_g.World.Height, World.Generation.GenParams.Default);
            Wanted = _g.ActiveBoss != null ? Boss : _g.DayNight.IsNight || _g.Player.Feet.y < surface - 35f ? Deep : Day;
            Sfx.PlayMusic(Wanted);
        }
    }
}
