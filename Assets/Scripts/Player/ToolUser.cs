using System;
using Loamlight.Content;
using Loamlight.Core;
using Loamlight.Inventory;
using Loamlight.World;
using Loamlight.World.Collision;
using UnityEngine;

namespace Loamlight.Player
{
    /// <summary>Receives items produced by world edits (direct-to-inventory in M1, item pickups from M2 on).</summary>
    public interface IDropSink
    {
        void Drop(Drop drop);
    }

    /// <summary>
    /// What the player does with the selected hotbar item: pickaxes mine (progress, crack overlay, tier gating),
    /// blocks / walls / torches place where the rules allow. Fires events the scenario runner and HUD observe.
    /// </summary>
    public sealed class ToolUser : MonoBehaviour
    {
        public IPlayerInput Input { get; set; }
        public PlayerController Player { get; set; }
        public WorldEdits Edits { get; set; }
        public ItemContainer Inventory { get; set; }
        public IDropSink Drops { get; set; }
        public ContentDatabase Db { get; set; }
        public SpriteRenderer Crack { get; set; }
        public Sprite[] CrackFrames { get; set; }
        public float TimeScale { get; set; } = 1f;

        Vector2Int _target = new Vector2Int(int.MinValue, 0);
        float _progress;
        float _cooldown;
        float _swing;

        public Vector2Int Target => _target;
        public float MiningProgress => _progress;
        public bool Swinging => _swing > 0f;
        public ItemDef HeldItem => Db.Item(Inventory.SelectedStack.item);

        public event Action<Vector2Int, ushort> Mined;
        public event Action<Vector2Int, ushort> Placed;
        public event Action<Vector2Int> TooHard;

        void Update()
        {
            if (Input == null || Edits == null) return;
            float dt = Time.deltaTime * TimeScale;
            _cooldown -= dt;
            _swing = Mathf.Max(0f, _swing - dt);

            int pick = Input.ConsumeHotbarSelect();
            if (pick >= 0) Inventory.Selected = pick;
            int scroll = Input.ConsumeHotbarScroll();
            if (scroll != 0) Inventory.Selected = Inventory.Selected + scroll;

            var aim = Input.AimWorld;
            var tile = new Vector2Int(Mathf.FloorToInt(aim.x), Mathf.FloorToInt(aim.y));
            var item = HeldItem;
            float reach = item != null ? item.reach : 5.5f;
            bool inReach = Vector2.Distance(Player.Center, tile + new Vector2(0.5f, 0.5f)) <= reach + 0.5f;
            bool use = Input.UseHeld | Input.ConsumeUsePressed();

            if (!use || item == null || !inReach)
            {
                ResetMining();
                return;
            }

            if (item.toolType == ToolType.Pickaxe) Mine(tile, item, dt);
            else if (!string.IsNullOrEmpty(item.placesTile)) TryPlaceTile(tile, item);
            else if (!string.IsNullOrEmpty(item.placesWall)) TryPlaceWall(tile, item);
            else ResetMining();
        }

        void ResetMining()
        {
            _progress = 0f;
            _target = new Vector2Int(int.MinValue, 0);
            if (Crack != null) Crack.enabled = false;
        }

        void Mine(Vector2Int tile, ItemDef tool, float dt)
        {
            if (_swing <= 0f) _swing = tool.useTime;
            var world = Edits.World;
            ushort b = world.GetBlock(tile.x, tile.y);
            if (b == 0) { ResetMining(); return; }
            var def = Db.Tile(b);
            if (def.tool != ToolType.Pickaxe && def.tool != ToolType.None)
            {
                ResetMining();
                return;
            }
            if (tool.toolPower < def.tier)
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
            var origin = Edits.ObjectOrigin(tile.x, tile.y);
            if (origin != _target) { _target = origin; _progress = 0f; }

            float needed = Mathf.Max(0.02f, def.hardness / Mathf.Max(0.05f, tool.toolSpeed));
            float before = _progress;
            _progress += dt / needed;
            // a hit sound per swing while chipping away
            if (Mathf.FloorToInt(before / 0.34f) != Mathf.FloorToInt(_progress / 0.34f) && _progress < 1f)
                Sfx.Play(def.sound, tile + Vector2.one * 0.5f, 0.6f);

            if (_progress >= 1f)
            {
                var drops = Edits.RemoveBlock(tile.x, tile.y);
                foreach (var d in drops) Drops?.Drop(d);
                Sfx.Play(def.sound, tile + Vector2.one * 0.5f, 1f);
                Mined?.Invoke(tile, b);
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

        bool PlayerBlocks(int x, int y) => TileCollision.Overlaps(Player.Body, x, y);

        void TryPlaceTile(Vector2Int tile, ItemDef item)
        {
            ResetMining();
            if (_cooldown > 0f) return;
            if (!Db.TryTileId(item.placesTile, out var id)) return;
            var def = Db.Tile(id);
            var origin = WorldEdits.OriginForCursor(def, tile.x, tile.y);
            if (!Edits.CanPlace(def, origin.x, origin.y, PlayerBlocks)) return;
            if (!Inventory.ConsumeFromSlot(Inventory.Selected)) return;
            Edits.Place(id, origin.x, origin.y);
            _cooldown = Mathf.Min(item.useTime, 0.15f);
            _swing = _cooldown;
            Sfx.Play("place", tile + Vector2.one * 0.5f);
            Placed?.Invoke(origin, id);
        }

        void TryPlaceWall(Vector2Int tile, ItemDef item)
        {
            ResetMining();
            if (_cooldown > 0f) return;
            ushort wall = Db.WallId(item.placesWall);
            if (!Edits.CanPlaceWall(tile.x, tile.y)) return;
            if (!Inventory.ConsumeFromSlot(Inventory.Selected)) return;
            Edits.World.SetWall(tile.x, tile.y, wall);
            _cooldown = Mathf.Min(item.useTime, 0.1f);
            _swing = _cooldown;
            Sfx.Play("place", tile + Vector2.one * 0.5f, 0.7f);
        }
    }

    /// <summary>M1 drop sink: items go straight into the inventory (pickups replace this in M2).</summary>
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
