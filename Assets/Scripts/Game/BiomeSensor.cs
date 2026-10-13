using Loamlight.Content;
using Loamlight.World;
using UnityEngine;

namespace Loamlight.Game
{
    /// <summary>
    /// Which biome the player is in, judged from the tiles around them (like the world itself, it follows what the
    /// player has built or dug): "emberdeep" near the floor, "glowmire" among moss and glowcaps, "dunes" on sand,
    /// "frostfell" on snow and ice, "caverns" underground, else "meadow". Cached for half a second.
    /// </summary>
    public sealed class BiomeSensor
    {
        readonly WorldData _world;
        readonly ushort _sand, _sandstone, _snow, _ice, _moss, _glowcap;
        readonly float _surfaceY;
        Vector2Int _lastCell = new Vector2Int(int.MinValue, 0);
        float _lastTime = -1f;
        string _last = "meadow";

        public BiomeSensor(WorldData world, ContentDatabase db, float surfaceY)
        {
            _world = world;
            _surfaceY = surfaceY;
            ushort T(string id) => db.TryTileId(id, out var v) ? v : (ushort)0;
            _sand = T("sand"); _sandstone = T("sandstone"); _snow = T("snow"); _ice = T("ice");
            _moss = T("mossy_stone"); _glowcap = T("glowcap");
        }

        public string At(Vector2 feet)
        {
            var cell = Vector2Int.FloorToInt(feet);
            if (Time.unscaledTime - _lastTime < 0.5f && (cell - _lastCell).sqrMagnitude < 16) return _last;
            _lastCell = cell;
            _lastTime = Time.unscaledTime;
            _last = Classify(cell);
            return _last;
        }

        string Classify(Vector2Int c)
        {
            if (c.y < _world.Height * 0.11f) return "emberdeep";
            int desert = 0, frost = 0, glow = 0;
            for (int y = c.y - 15; y <= c.y + 15; y += 1)
            for (int x = c.x - 30; x <= c.x + 30; x += 2)
            {
                ushort b = _world.GetBlock(x, y);
                if (b == 0) continue;
                if (b == _sand || b == _sandstone) desert++;
                else if (b == _snow || b == _ice) frost++;
                else if (b == _moss || b == _glowcap) glow++;
            }
            bool underground = c.y < _surfaceY - 30f;
            if (glow > 30 && underground) return "glowmire";
            if (desert > 120 && desert > frost) return "dunes";
            if (frost > 120) return "frostfell";
            return underground ? "caverns" : "meadow";
        }
    }
}
