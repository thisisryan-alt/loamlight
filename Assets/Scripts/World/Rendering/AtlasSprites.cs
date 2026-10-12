using System.Collections.Generic;
using Loamlight.Content;
using UnityEngine;

namespace Loamlight.World.Rendering
{
    /// <summary>Sprites cut from the world atlas on demand (placement ghosts, previews), cached per frame index.</summary>
    public sealed class AtlasSprites
    {
        readonly WorldAtlas _atlas;
        readonly ContentDatabase _db;
        readonly Dictionary<int, Sprite> _cache = new Dictionary<int, Sprite>();

        public AtlasSprites(WorldAtlas atlas, ContentDatabase db)
        {
            _atlas = atlas;
            _db = db;
        }

        public Sprite Frame(int frame)
        {
            if (frame < 0 || frame >= _atlas.uvRects.Length) return null;
            if (_cache.TryGetValue(frame, out var s)) return s;
            var uv = _atlas.uvRects[frame];
            var tex = _atlas.albedo;
            var rect = new Rect(uv.x * tex.width, uv.y * tex.height, (uv.z - uv.x) * tex.width, (uv.w - uv.y) * tex.height);
            s = Sprite.Create(tex, rect, Vector2.zero, 16f, 0, SpriteMeshType.FullRect);
            s.name = $"atlas_{frame}";
            _cache[frame] = s;
            return s;
        }

        AtlasEntry? Entry(string id, AtlasEntry[] list)
        {
            foreach (var e in list) if (e.id == id) return e;
            return null;
        }

        /// <summary>Frames for each cell of a tile as it would look placed alone (terrain: isolated mask).</summary>
        public Sprite TileCell(string tileId, int cell)
        {
            var e = Entry(tileId, _atlas.tiles);
            if (!e.HasValue) return null;
            var a = e.Value;
            switch (a.layout)
            {
                case AtlasLayout.Terrain: return Frame(a.start + 15 * a.variants);
                case AtlasLayout.Object: return Frame(a.start + Mathf.Clamp(cell, 0, a.count - 1));
                default: return Frame(a.start);
            }
        }

        public Sprite Wall(string wallId)
        {
            var e = Entry(wallId, _atlas.walls);
            return e.HasValue ? Frame(e.Value.start) : null;
        }
    }
}
