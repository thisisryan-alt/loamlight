using System.Collections.Generic;
using Loamlight.Content;
using Unity.Collections;
using UnityEngine;

namespace Loamlight.World
{
    public struct Drop
    {
        public string item;
        public int count;
        public Vector2 position;
    }

    /// <summary>
    /// Content-aware edits shared by the player, building tools, explosions and worldgen post-passes:
    /// multi-tile objects are placed and removed as a unit, placement rules (floor / wall-or-solid support,
    /// free space) are enforced, and anything left unsupported by a removal breaks and drops.
    /// </summary>
    public sealed class WorldEdits
    {
        public readonly WorldData World;
        public readonly ContentDatabase Content;
        readonly NativeArray<TileProps> _props;
        readonly List<Drop> _drops = new List<Drop>();

        public WorldEdits(WorldData world, ContentDatabase content, NativeArray<TileProps> props)
        {
            World = world;
            Content = content;
            _props = props;
        }

        public TileProps Props(ushort id) => _props[id];

        public bool IsSolidTerrain(int x, int y)
        {
            if (!World.InBounds(x, y)) return true;
            ushort b = World.GetBlock(x, y);
            return b != 0 && _props[b].kind == (byte)TileKind.Terrain && _props[b].solid != 0;
        }

        /// <summary>Bottom-left origin of the object covering (x, y) (itself for 1x1 tiles).</summary>
        public Vector2Int ObjectOrigin(int x, int y)
        {
            ushort b = World.GetBlock(x, y);
            if (b == 0) return new Vector2Int(x, y);
            var p = _props[b];
            if (p.width <= 1 && p.height <= 1) return new Vector2Int(x, y);
            int cell = World.GetFrame(x, y) & 0x7f;
            return new Vector2Int(x - cell % p.width, y - cell / p.width);
        }

        // ---------------------------------------------------------------------------------------------- remove
        /// <summary>Removes the tile (whole object) at (x, y), returns what it drops, then breaks anything unsupported.</summary>
        public IReadOnlyList<Drop> RemoveBlock(int x, int y)
        {
            _drops.Clear();
            RemoveInternal(x, y);
            return _drops;
        }

        void RemoveInternal(int x, int y)
        {
            ushort b = World.GetBlock(x, y);
            if (b == 0) return;
            var def = Content.Tile(b);
            var p = _props[b];
            var origin = ObjectOrigin(x, y);
            for (int cy = 0; cy < p.height; cy++)
            for (int cx = 0; cx < p.width; cx++)
                if (World.GetBlock(origin.x + cx, origin.y + cy) == b) World.SetBlock(origin.x + cx, origin.y + cy, 0);
            if (!string.IsNullOrEmpty(def.drop))
                _drops.Add(new Drop { item = def.drop, count = Mathf.Max(1, def.dropCount), position = new Vector2(origin.x + p.width * 0.5f, origin.y + p.height * 0.5f) });
            // neighbours that relied on this tile
            for (int cy = -1; cy <= p.height; cy++)
            for (int cx = -1; cx <= p.width; cx++)
                BreakIfUnsupported(origin.x + cx, origin.y + cy);
        }

        public IReadOnlyList<Drop> RemoveWall(int x, int y)
        {
            _drops.Clear();
            ushort w = World.GetWall(x, y);
            if (w == 0) return _drops;
            var def = Content.Wall(w);
            World.SetWall(x, y, 0);
            if (!string.IsNullOrEmpty(def.drop)) _drops.Add(new Drop { item = def.drop, count = 1, position = new Vector2(x + 0.5f, y + 0.5f) });
            BreakIfUnsupported(x, y);
            return _drops;
        }

        void BreakIfUnsupported(int x, int y)
        {
            if (!World.InBounds(x, y)) return;
            ushort b = World.GetBlock(x, y);
            if (b == 0) return;
            var def = Content.Tile(b);
            if (def.kind == TileKind.Terrain) return;
            var o = ObjectOrigin(x, y);
            if (!Supported(def, o.x, o.y)) RemoveInternal(x, y);
        }

        /// <summary>Would an object of this kind be held up at origin (bottom-left)?</summary>
        public bool Supported(TileDef def, int ox, int oy)
        {
            switch (def.placement)
            {
                case Placement.Floor:
                    for (int cx = 0; cx < def.size.x; cx++)
                    {
                        if (IsSolidTerrain(ox + cx, oy - 1) || IsPlatform(ox + cx, oy - 1)) continue;
                        ushort below = World.GetBlock(ox + cx, oy - 1);
                        if (def.kind == TileKind.Tree && below != 0 && _props[below].kind == (byte)TileKind.Tree) continue;
                        return false;
                    }
                    return true;
                case Placement.WallOrSolid:
                    return World.GetWall(ox, oy) != 0 || IsSolidTerrain(ox - 1, oy) || IsSolidTerrain(ox + 1, oy) || IsSolidTerrain(ox, oy - 1);
                case Placement.Trunk:
                {
                    // canopies sit on the trunk cell under their bottom-centre
                    ushort below = World.GetBlock(ox + def.size.x / 2, oy - 1);
                    return below != 0 && _props[below].kind == (byte)TileKind.Tree;
                }
                case Placement.Ceiling:
                    for (int cx = 0; cx < def.size.x; cx++)
                        if (!IsSolidTerrain(ox + cx, oy + def.size.y)) return false;
                    return true;
                default:
                    return true;
            }
        }

        bool IsPlatform(int x, int y)
        {
            ushort b = World.GetBlock(x, y);
            return b != 0 && _props[b].kind == (byte)TileKind.Platform;
        }

        // ---------------------------------------------------------------------------------------------- place
        /// <summary>Origin used when the cursor is over (x, y): objects are centred horizontally on the cursor.</summary>
        public static Vector2Int OriginForCursor(TileDef def, int x, int y) => new Vector2Int(x - (def.size.x - 1) / 2, y);

        /// <summary>
        /// Can <paramref name="def"/> be placed with its origin at (ox, oy)? Terrain needs an empty cell and something to
        /// attach to (any neighbour block or a wall), objects need all cells free plus their placement support.
        /// <paramref name="blocked"/> lets callers veto cells (e.g. occupied by the player).
        /// </summary>
        public bool CanPlace(TileDef def, int ox, int oy, System.Func<int, int, bool> blocked = null)
        {
            for (int cy = 0; cy < def.size.y; cy++)
            for (int cx = 0; cx < def.size.x; cx++)
            {
                int x = ox + cx, y = oy + cy;
                if (!World.InBounds(x, y) || World.GetBlock(x, y) != 0) return false;
                if (World.GetLiquid(x, y) > 0 && World.GetLiquidType(x, y) == LiquidType.Lava && def.kind != TileKind.Terrain) return false;
                if (blocked != null && def.solid && blocked(x, y)) return false;
            }
            if (def.kind == TileKind.Terrain || def.kind == TileKind.Platform)
                return HasAnchor(ox, oy);
            return Supported(def, ox, oy);
        }

        bool HasAnchor(int x, int y) =>
            World.GetWall(x, y) != 0 || World.GetBlock(x - 1, y) != 0 || World.GetBlock(x + 1, y) != 0 ||
            World.GetBlock(x, y - 1) != 0 || World.GetBlock(x, y + 1) != 0 || !World.InBounds(x, y - 1);

        public void Place(ushort id, int ox, int oy, TileShape shape = TileShape.Full)
        {
            var p = _props[id];
            for (int cy = 0; cy < p.height; cy++)
            for (int cx = 0; cx < p.width; cx++)
            {
                World.SetBlock(ox + cx, oy + cy, id, p.width == 1 && p.height == 1 ? shape : TileShape.Full, (byte)(cx + cy * p.width));
                if (p.solid != 0 && World.GetLiquid(ox + cx, oy + cy) > 0) World.SetLiquid(ox + cx, oy + cy, 0, LiquidType.None);
            }
        }

        /// <summary>Ids of crafting-station tiles within <paramref name="radius"/> tiles of a point.</summary>
        public HashSet<string> StationsNear(Vector2 center, int radius)
        {
            var set = new HashSet<string>();
            int cx = Mathf.FloorToInt(center.x), cy = Mathf.FloorToInt(center.y);
            for (int y = cy - radius; y <= cy + radius; y++)
            for (int x = cx - radius; x <= cx + radius; x++)
            {
                ushort b = World.GetBlock(x, y);
                if (b == 0) continue;
                var def = Content.Tile(b);
                if (def.isStation) set.Add(def.id);
            }
            return set;
        }

        /// <summary>Clears an object/tile without dropping anything (undo refunds the item instead).</summary>
        public void ClearSilently(int x, int y)
        {
            ushort b = World.GetBlock(x, y);
            if (b == 0) return;
            var p = _props[b];
            var o = ObjectOrigin(x, y);
            for (int cy = 0; cy < p.height; cy++)
            for (int cx = 0; cx < p.width; cx++)
                if (World.GetBlock(o.x + cx, o.y + cy) == b) World.SetBlock(o.x + cx, o.y + cy, 0);
            _drops.Clear();
            for (int cy = -1; cy <= p.height; cy++)
            for (int cx = -1; cx <= p.width; cx++)
                BreakIfUnsupported(o.x + cx, o.y + cy);
        }

        /// <summary>Drops produced by the last <see cref="ClearSilently"/> cascade (things that lost support).</summary>
        public IReadOnlyList<Drop> LastCascadeDrops => _drops;

        /// <summary>Toggles a door's open bit on all its cells. Refuses to close on top of a blocked cell.</summary>
        public bool ToggleDoor(int x, int y, System.Func<int, int, bool> blocked)
        {
            ushort b = World.GetBlock(x, y);
            if (b == 0 || _props[b].kind != (byte)TileKind.Door) return false;
            var o = ObjectOrigin(x, y);
            var p = _props[b];
            bool open = (World.GetFrame(o.x, o.y) & 0x80) != 0;
            if (open && blocked != null)
                for (int cy = 0; cy < p.height; cy++)
                    if (blocked(o.x, o.y + cy)) return false;
            for (int cy = 0; cy < p.height; cy++)
            for (int cx = 0; cx < p.width; cx++)
            {
                byte f = World.GetFrame(o.x + cx, o.y + cy);
                World.SetFrame(o.x + cx, o.y + cy, (byte)(open ? f & 0x7f : f | 0x80));
            }
            return true;
        }

        public bool IsDoorOpen(int x, int y) => (World.GetFrame(x, y) & 0x80) != 0;

        public bool CanPlaceWall(int x, int y) =>
            World.InBounds(x, y) && World.GetWall(x, y) == 0 &&
            (World.GetWall(x - 1, y) != 0 || World.GetWall(x + 1, y) != 0 || World.GetWall(x, y - 1) != 0 ||
             World.GetWall(x, y + 1) != 0 || World.GetBlock(x, y) != 0 || World.GetBlock(x - 1, y) != 0 ||
             World.GetBlock(x + 1, y) != 0 || World.GetBlock(x, y - 1) != 0 || World.GetBlock(x, y + 1) != 0);
    }
}
