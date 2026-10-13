using System;
using System.Collections.Generic;
using Loamlight.Content;
using Loamlight.Core;
using Loamlight.Inventory;
using Loamlight.World;
using Loamlight.World.Collision;
using Loamlight.World.Rendering;
using UnityEngine;

namespace Loamlight.Player
{
    /// <summary>Receives items produced by world edits (item pickups in the game, direct-to-inventory in tests).</summary>
    public interface IDropSink
    {
        void Drop(Drop drop);
    }

    /// <summary>One undoable placement batch: what went where, and which item paid for it.</summary>
    public sealed class UndoBatch
    {
        public struct Entry
        {
            public Vector2Int tile;
            public bool wall;
            public ushort id;
            public int item;
        }

        public readonly List<Entry> entries = new List<Entry>();
        public float time;
    }

    /// <summary>
    /// What the player does with the selected item.
    /// <list type="bullet">
    /// <item>Pickaxe / axe: mine (progress, crack overlay, tier gating; axes fell trees, pickaxes everything else).</item>
    /// <item>Hammer: cycle a block's shape full → half → slope right → slope left, or knock down a wall.</item>
    /// <item>Blocks and walls (pillar 3): ghost preview; click places one, drag places a line, Shift-drag fills a
    /// rectangle; every batch is undoable with Ctrl+Z (items refunded) while the tiles are untouched.</item>
    /// <item>Objects (furniture, stations, torches): ghost preview of the whole object, placed on press.</item>
    /// </list>
    /// </summary>
    public sealed class ToolUser : MonoBehaviour
    {
        public const int MaxUndo = 20, MaxBatch = 400;

        public IPlayerInput Input { get; set; }
        public PlayerController Player { get; set; }
        public WorldEdits Edits { get; set; }
        public ItemContainer Inventory { get; set; }
        public IDropSink Drops { get; set; }
        public ContentDatabase Db { get; set; }
        public SpriteRenderer Crack { get; set; }
        public Sprite[] CrackFrames { get; set; }
        public AtlasSprites Atlas { get; set; }
        public Material GhostMaterial { get; set; }
        public Func<Vector2Int, bool> CanRemove { get; set; }   // e.g. refuse to break non-empty chests
        /// <summary>Weapon items are handed to this (ToolUser stays the only reader of the Use input).</summary>
        public WeaponUser Weapons { get; set; }
        /// <summary>Consumables (healing brews, summon items): returns true if the item was used up.</summary>
        public Func<ItemDef, bool> Consume { get; set; }
        public float TimeScale { get; set; } = 1f;
        /// <summary>False while a menu has the mouse.</summary>
        public bool Enabled { get; set; } = true;

        Vector2Int _target = new Vector2Int(int.MinValue, 0);
        float _progress, _cooldown, _swing;
        bool _dragging;
        Vector2Int _dragStart;
        bool _useWasHeld;
        readonly List<Vector2Int> _dragTiles = new List<Vector2Int>();
        readonly List<UndoBatch> _undo = new List<UndoBatch>();
        readonly List<SpriteRenderer> _ghosts = new List<SpriteRenderer>();
        int _ghostsUsed;

        public Vector2Int Target => _target;
        public float MiningProgress => _progress;
        public bool Swinging => _swing > 0f && (Weapons == null || !Weapons.Swinging);
        public bool Attacking => Weapons != null && Weapons.Swinging;
        public ItemDef HeldItem => Db.Item(Inventory.SelectedStack.item);
        public int UndoDepth => _undo.Count;
        public IReadOnlyList<Vector2Int> DragPreview => _dragTiles;

        /// <summary>A tile or object was mined: (origin tile, tile id).</summary>
        public event Action<Vector2Int, ushort> Mined;
        public event Action<Vector2Int, ushort> Placed;
        public event Action<Vector2Int, TileShape> Hammered;
        public event Action<Vector2Int> TooHard;
        public event Action<int> Undone;   // number of tiles reverted

        void Update()
        {
            BeginGhosts();
            try { Tick(); }
            finally { EndGhosts(); }
        }

        void Tick()
        {
            if (Input == null || Edits == null) return;
            float dt = Time.deltaTime * TimeScale * GameClock.Scale;
            _cooldown -= dt;
            _swing = Mathf.Max(0f, _swing - dt);

            int pick = Input.ConsumeHotbarSelect();
            if (pick >= 0) Inventory.Selected = pick;
            int scroll = Input.ConsumeHotbarScroll();
            if (scroll != 0 && !_dragging) Inventory.Selected = Inventory.Selected + scroll;
            if (Input.ConsumeUndo()) Undo();

            var aim = Input.AimWorld;
            var tile = new Vector2Int(Mathf.FloorToInt(aim.x), Mathf.FloorToInt(aim.y));
            var item = HeldItem;
            float reach = item != null ? item.reach : 5.5f;
            bool inReach = InReach(tile, reach);
            bool pressed = Input.ConsumeUsePressed();
            bool held = Enabled && Input.UseHeld;
            bool use = Enabled && (held || pressed);

            if (item == null) { ResetMining(); CancelDrag(); _useWasHeld = held; return; }

            bool isBlock = !string.IsNullOrEmpty(item.placesTile) && Db.TryTileId(item.placesTile, out var bid) && Db.Tile(bid).size == Vector2Int.one
                           && (Db.Tile(bid).kind == TileKind.Terrain || Db.Tile(bid).kind == TileKind.Platform);
            bool isWall = !string.IsNullOrEmpty(item.placesWall);

            if (isBlock || isWall)
            {
                ResetMining();
                HandleBuild(item, isWall, tile, held, pressed);
            }
            else if (!string.IsNullOrEmpty(item.placesTile))
            {
                ResetMining();
                CancelDrag();
                if (Db.TryTileId(item.placesTile, out var oid)) GhostObject(Db.Tile(oid), tile, inReach);
                if (use && inReach) TryPlaceObject(tile, item);
            }
            else if (item.weaponKind != WeaponKind.None)
            {
                ResetMining();
                CancelDrag();
                Weapons?.Use(item, aim, held || pressed, dt);
            }
            else if ((item.category == ItemCategory.Consumable || item.category == ItemCategory.Summon) && Consume != null)
            {
                ResetMining();
                CancelDrag();
                if ((pressed || (held && !_useWasHeld)) && Enabled && _cooldown <= 0f && Consume(item))
                {
                    Inventory.ConsumeFromSlot(Inventory.Selected, 1);
                    _cooldown = Mathf.Max(0.3f, item.useTime);
                }
            }
            else if (!string.IsNullOrEmpty(item.bucket))
            {
                ResetMining();
                CancelDrag();
                if ((pressed || (held && !_useWasHeld)) && Enabled && inReach && _cooldown <= 0f) UseBucket(tile, item);
            }
            else if (item.toolType == ToolType.Hammer)
            {
                CancelDrag();
                ResetMining();
                if (use && inReach && _cooldown <= 0f) Hammer(tile, item);
            }
            else if (item.toolType == ToolType.Pickaxe || item.toolType == ToolType.Axe)
            {
                CancelDrag();
                if (use && inReach) Mine(tile, item, dt);
                else ResetMining();
            }
            else
            {
                ResetMining();
                CancelDrag();
            }
            _useWasHeld = held;
        }

        /// <summary>
        /// Empty bucket: scoop a tile's worth of water or lava (gathered from the target cell and its neighbours, so a
        /// thin layer still fills it). Full bucket: pour a full tile into an open cell. The bucket item is swapped.
        /// </summary>
        void UseBucket(Vector2Int tile, ItemDef bucket)
        {
            var world = Edits.World;
            if (bucket.bucket == "empty")
            {
                byte here = world.GetLiquid(tile.x, tile.y);
                if (here == 0) return;
                var type = (LiquidType)world.LiquidKind[world.Index(tile.x, tile.y)];
                int need = 255, got = 0;
                for (int r = 0; r <= 3 && got < need; r++)
                for (int dy = -r; dy <= r && got < need; dy++)
                for (int dx = -r; dx <= r && got < need; dx++)
                {
                    if (Mathf.Max(Mathf.Abs(dx), Mathf.Abs(dy)) != r) continue;
                    int x = tile.x + dx, y = tile.y + dy;
                    byte a = world.GetLiquid(x, y);
                    if (a == 0 || world.LiquidKind[world.Index(x, y)] != (byte)type) continue;
                    int take = Mathf.Min(a, need - got);
                    world.SetLiquid(x, y, (byte)(a - take), a - take == 0 ? LiquidType.None : type);
                    got += take;
                }
                if (got < 200) return;   // not enough to fill: put nothing back, the puddle is gone either way
                SwapHeld(type == LiquidType.Lava ? "lava_bucket" : "water_bucket");
                Sfx.Play("splash", tile + new Vector2(0.5f, 0.5f), 0.7f);
            }
            else
            {
                ushort b = world.GetBlock(tile.x, tile.y);
                if (b != 0 && Edits.Props(b).solid != 0) return;
                if (world.GetLiquid(tile.x, tile.y) > 40) return;
                var type = bucket.bucket == "lava" ? LiquidType.Lava : LiquidType.Water;
                world.SetLiquid(tile.x, tile.y, 255, type);
                SwapHeld("bucket");
                Sfx.Play("splash", tile + new Vector2(0.5f, 0.5f), 0.7f);
            }
            _cooldown = bucket.useTime;
            _swing = 0.25f;
        }

        void SwapHeld(string itemId)
        {
            if (!Db.TryItemId(itemId, out var id)) return;
            int slot = Inventory.Selected;
            Inventory.Set(slot, new ItemStack(id, 1));
        }

        bool InReach(Vector2Int tile, float reach) => Vector2.Distance(Player.Center, tile + new Vector2(0.5f, 0.5f)) <= reach + 0.5f;

        // ------------------------------------------------------------------------------------------- mining
        void ResetMining()
        {
            _progress = 0f;
            _target = new Vector2Int(int.MinValue, 0);
            if (Crack != null) Crack.enabled = false;
        }

        static bool ToolFits(ToolType need, ToolType have) =>
            need == ToolType.None ? have == ToolType.Pickaxe || have == ToolType.Axe : need == have;

        void Mine(Vector2Int tile, ItemDef tool, float dt)
        {
            if (_swing <= 0f)
            {
                _swing = tool.useTime;
                Sfx.Play("swing", Player.Center, 0.25f);
            }
            var world = Edits.World;
            ushort b = world.GetBlock(tile.x, tile.y);
            if (b == 0) { ResetMining(); return; }
            var def = Db.Tile(b);
            if (!ToolFits(def.tool, tool.toolType)) { ResetMining(); return; }
            var origin = Edits.ObjectOrigin(tile.x, tile.y);
            if (tool.toolPower < def.tier || (CanRemove != null && !CanRemove(origin)))
            {
                if (_cooldown <= 0f)
                {
                    _cooldown = 0.35f;
                    Sfx.Play("cant", tile + Vector2.one * 0.5f);
                    TooHard?.Invoke(tile);
                }
                ResetMining();
                return;
            }
            if (origin != _target) { _target = origin; _progress = 0f; }

            float needed = Mathf.Max(0.02f, def.hardness / Mathf.Max(0.05f, tool.toolSpeed));
            float before = _progress;
            _progress += dt / needed;
            if (Mathf.FloorToInt(before / 0.34f) != Mathf.FloorToInt(_progress / 0.34f) && _progress < 1f)
                Sfx.Play(def.sound, tile + Vector2.one * 0.5f, 0.6f);

            if (_progress >= 1f)
            {
                var drops = Edits.RemoveBlock(tile.x, tile.y);
                foreach (var d in drops) Drops?.Drop(d);
                Sfx.Play(def.sound, tile + Vector2.one * 0.5f, 1f);
                Mined?.Invoke(origin, b);
                ResetMining();
                return;
            }
            if (Crack != null && CrackFrames != null && CrackFrames.Length > 0)
            {
                Crack.enabled = true;
                Crack.transform.position = new Vector3(tile.x + 0.5f, tile.y + 0.5f, 0f);
                Crack.sprite = CrackFrames[Mathf.Clamp((int)(_progress * CrackFrames.Length), 0, CrackFrames.Length - 1)];
            }
        }

        // ------------------------------------------------------------------------------------------- hammer
        void Hammer(Vector2Int tile, ItemDef hammer)
        {
            _cooldown = hammer.useTime;
            _swing = hammer.useTime;
            var w = Edits.World;
            ushort b = w.GetBlock(tile.x, tile.y);
            if (b != 0)
            {
                var p = Edits.Props(b);
                if (p.kind != (byte)TileKind.Terrain || p.solid == 0 || p.width != 1) return;
                var next = (TileShape)(((int)w.GetShape(tile.x, tile.y) + 1) % 4);
                w.SetShape(tile.x, tile.y, next);
                Sfx.Play("dig_hard", tile + Vector2.one * 0.5f, 0.7f);
                Hammered?.Invoke(tile, next);
                return;
            }
            if (w.GetWall(tile.x, tile.y) != 0)
            {
                foreach (var d in Edits.RemoveWall(tile.x, tile.y)) Drops?.Drop(d);
                Sfx.Play("dig_soft", tile + Vector2.one * 0.5f, 0.8f);
                Hammered?.Invoke(tile, TileShape.Full);
            }
        }

        // ------------------------------------------------------------------------------------------- building
        void CancelDrag()
        {
            _dragging = false;
            _dragTiles.Clear();
        }

        void HandleBuild(ItemDef item, bool wall, Vector2Int tile, bool held, bool pressed)
        {
            if (!_dragging && held && (!_useWasHeld || pressed))
            {
                _dragging = true;
                _dragStart = tile;
            }
            if (_dragging)
            {
                if (!held)
                {
                    // release commits exactly what the ghost showed last frame (modifier keys may already be up)
                    CommitDrag(item, wall);
                    CancelDrag();
                    return;
                }
                ComputeDrag(_dragStart, tile, Input.ShiftHeld);
                int have = Inventory.Count(Inventory.SelectedStack.item);
                for (int i = 0; i < _dragTiles.Count; i++)
                    GhostBuild(item, wall, _dragTiles[i], i < have && Valid(item, wall, _dragTiles[i]));
            }
            else
            {
                GhostBuild(item, wall, tile, Valid(item, wall, tile));
            }
        }

        void ComputeDrag(Vector2Int a, Vector2Int b, bool rect)
        {
            _dragTiles.Clear();
            if (rect)
            {
                int x0 = Mathf.Min(a.x, b.x), x1 = Mathf.Max(a.x, b.x), y0 = Mathf.Min(a.y, b.y), y1 = Mathf.Max(a.y, b.y);
                // fill from the bottom up so each row anchors on the one below
                for (int y = y0; y <= y1; y++)
                for (int x = x0; x <= x1 && _dragTiles.Count < MaxBatch; x++)
                    _dragTiles.Add(new Vector2Int(x, y));
                return;
            }
            // Bresenham line
            int dx = Mathf.Abs(b.x - a.x), dy = -Mathf.Abs(b.y - a.y);
            int sx = a.x < b.x ? 1 : -1, sy = a.y < b.y ? 1 : -1, err = dx + dy;
            int cx = a.x, cy = a.y;
            while (_dragTiles.Count < MaxBatch)
            {
                _dragTiles.Add(new Vector2Int(cx, cy));
                if (cx == b.x && cy == b.y) break;
                int e2 = 2 * err;
                if (e2 >= dy) { err += dy; cx += sx; }
                if (e2 <= dx) { err += dx; cy += sy; }
            }
        }

        bool Valid(ItemDef item, bool wall, Vector2Int t)
        {
            if (!InReach(t, Mathf.Max(item.reach, 5.5f) + 6f)) return false;   // drags may reach a little further
            if (wall) return Edits.CanPlaceWall(t.x, t.y);
            return Db.TryTileId(item.placesTile, out var id) && Edits.CanPlace(Db.Tile(id), t.x, t.y, PlayerBlocks);
        }

        bool PlayerBlocks(int x, int y) => TileCollision.Overlaps(Player.Body, x, y);

        void CommitDrag(ItemDef item, bool wall)
        {
            int itemId = Db.ItemId(item.id);
            var batch = new UndoBatch { time = Time.time };
            ushort tileId = wall ? Db.WallId(item.placesWall) : Db.TileId(item.placesTile);
            foreach (var t in _dragTiles)
            {
                if (Inventory.Count(itemId) <= 0) break;
                if (!Valid(item, wall, t)) continue;
                if (!Inventory.ConsumeFromSlot(Inventory.Selected) && !Inventory.Remove(itemId, 1)) break;
                if (wall) Edits.World.SetWall(t.x, t.y, tileId);
                else Edits.Place(tileId, t.x, t.y);
                batch.entries.Add(new UndoBatch.Entry { tile = t, wall = wall, id = tileId, item = itemId });
                if (!wall) Placed?.Invoke(t, tileId);
            }
            if (batch.entries.Count == 0) return;
            Sfx.Play("place", batch.entries[batch.entries.Count - 1].tile + Vector2.one * 0.5f, wall ? 0.7f : 1f);
            _swing = 0.15f;
            _undo.Add(batch);
            if (_undo.Count > MaxUndo) _undo.RemoveAt(0);
        }

        void TryPlaceObject(Vector2Int tile, ItemDef item)
        {
            if (_cooldown > 0f) return;
            if (!Db.TryTileId(item.placesTile, out var id)) return;
            var def = Db.Tile(id);
            var origin = WorldEdits.OriginForCursor(def, tile.x, tile.y);
            if (!Edits.CanPlace(def, origin.x, origin.y, PlayerBlocks)) return;
            if (!Inventory.ConsumeFromSlot(Inventory.Selected)) return;
            Edits.Place(id, origin.x, origin.y);
            _cooldown = Mathf.Min(item.useTime, 0.2f);
            _swing = _cooldown;
            Sfx.Play("place", tile + Vector2.one * 0.5f);
            var batch = new UndoBatch { time = Time.time };
            batch.entries.Add(new UndoBatch.Entry { tile = origin, wall = false, id = id, item = Db.ItemId(item.id) });
            _undo.Add(batch);
            if (_undo.Count > MaxUndo) _undo.RemoveAt(0);
            Placed?.Invoke(origin, id);
        }

        /// <summary>Reverts the most recent batch: tiles still as placed are removed and their items refunded.</summary>
        public int Undo()
        {
            if (_undo.Count == 0) return 0;
            var batch = _undo[_undo.Count - 1];
            _undo.RemoveAt(_undo.Count - 1);
            int reverted = 0;
            var w = Edits.World;
            for (int i = batch.entries.Count - 1; i >= 0; i--)
            {
                var e = batch.entries[i];
                if (e.wall)
                {
                    if (w.GetWall(e.tile.x, e.tile.y) != e.id) continue;
                    w.SetWall(e.tile.x, e.tile.y, 0);
                }
                else
                {
                    if (w.GetBlock(e.tile.x, e.tile.y) != e.id) continue;
                    Edits.ClearSilently(e.tile.x, e.tile.y);
                    foreach (var d in Edits.LastCascadeDrops) Drops?.Drop(d);
                }
                if (Inventory.Add(e.item, 1) > 0 && Drops != null)
                    Drops.Drop(new Drop { item = Db.Item(e.item).id, count = 1, position = e.tile + Vector2.one * 0.5f });
                reverted++;
            }
            if (reverted > 0) Sfx.Play("dig_soft", Player.Center, 0.6f);
            Undone?.Invoke(reverted);
            return reverted;
        }

        // ------------------------------------------------------------------------------------------- ghosts
        void BeginGhosts() => _ghostsUsed = 0;

        void EndGhosts()
        {
            for (int i = _ghostsUsed; i < _ghosts.Count; i++)
                if (_ghosts[i].enabled) _ghosts[i].enabled = false;
        }

        SpriteRenderer NextGhost()
        {
            if (_ghostsUsed >= _ghosts.Count)
            {
                var go = new GameObject("Ghost");
                go.transform.SetParent(transform.parent, false);
                var sr = go.AddComponent<SpriteRenderer>();
                sr.sortingLayerName = "Foreground";
                if (GhostMaterial != null) sr.sharedMaterial = GhostMaterial;
                _ghosts.Add(sr);
            }
            var g = _ghosts[_ghostsUsed++];
            g.enabled = true;
            return g;
        }

        static readonly Color Ok = new Color(0.55f, 1f, 0.6f, 0.55f), Bad = new Color(1f, 0.35f, 0.3f, 0.45f);

        void GhostBuild(ItemDef item, bool wall, Vector2Int t, bool valid)
        {
            if (Atlas == null || _ghostsUsed > MaxBatch) return;
            var sprite = wall ? Atlas.Wall(item.placesWall) : Atlas.TileCell(item.placesTile, 0);
            if (sprite == null) return;
            var g = NextGhost();
            g.sprite = sprite;
            g.color = valid ? Ok : Bad;
            g.transform.position = new Vector3(t.x, t.y, 0f);
        }

        void GhostObject(TileDef def, Vector2Int cursor, bool inReach)
        {
            if (Atlas == null) return;
            var origin = WorldEdits.OriginForCursor(def, cursor.x, cursor.y);
            bool valid = inReach && Edits.CanPlace(def, origin.x, origin.y, PlayerBlocks);
            for (int cy = 0; cy < def.size.y; cy++)
            for (int cx = 0; cx < def.size.x; cx++)
            {
                var sprite = Atlas.TileCell(def.id, cx + cy * def.size.x);
                if (sprite == null) continue;
                var g = NextGhost();
                g.sprite = sprite;
                g.color = valid ? Ok : Bad;
                g.transform.position = new Vector3(origin.x + cx, origin.y + cy, 0f);
            }
        }
    }

    /// <summary>Drop sink that puts items straight into an inventory (tests, and overflow-free contexts).</summary>
    public sealed class InventoryDropSink : IDropSink
    {
        readonly ItemContainer _inventory;
        readonly ContentDatabase _db;

        public InventoryDropSink(ItemContainer inventory, ContentDatabase db)
        {
            _inventory = inventory;
            _db = db;
        }

        public void Drop(Drop drop)
        {
            if (_db.TryItemId(drop.item, out var id)) _inventory.Add(id, drop.count);
        }
    }
}
