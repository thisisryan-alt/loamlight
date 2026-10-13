using System.Collections;
using System.Collections.Generic;
using Loamlight.Core;
using Loamlight.Player;
using UnityEngine;

namespace Loamlight.Game.Scenarios
{
    /// <summary>
    /// A scripted play-through of the real Game scene, driven through <see cref="ScriptedPlayerInput"/> exactly like
    /// a human would (aim, hold, press). Checks record state assertions; the capture runner fails the run if any
    /// check failed. This is what proves a feature is wired into the game, not only unit-tested.
    /// </summary>
    public abstract class Scenario
    {
        protected GameBootstrap G;
        protected ScriptedPlayerInput In;
        readonly List<string> _log = new List<string>();
        readonly List<string> _failures = new List<string>();

        public void Attach(GameBootstrap game)
        {
            G = game;
            In = new ScriptedPlayerInput();
            G.Player.Input = In;
            G.Tools.Input = In;
            if (G.Interactor != null) G.Interactor.Input = In;
            if (G.HumanInput != null) G.HumanInput.Enabled = false;
        }

        public abstract IEnumerator Run();

        /// <summary>Spawns off and the player can't be hurt (true for every scenario that isn't about fighting).</summary>
        public virtual bool Peaceful => true;

        protected void Check(bool ok, string what)
        {
            _log.Add((ok ? "ok: " : "FAIL: ") + what);
            if (!ok)
            {
                _failures.Add(what);
                Debug.LogWarning("[Scenario] FAIL " + what);
            }
            else Debug.Log("[Scenario] ok " + what);
        }

        protected void Note(string what) => _log.Add("note: " + what);

        /// <summary>Checks since the previous call, and whether any of them failed.</summary>
        public bool Drain(out string detail)
        {
            detail = _log.Count == 0 ? "(no checks)" : string.Join(" | ", _log);
            bool ok = _failures.Count == 0;
            _log.Clear();
            _failures.Clear();
            return ok;
        }

        // ------------------------------------------------------------------------------------------- helpers
        protected static IEnumerator Seconds(float s)
        {
            float end = Time.time + s;
            while (Time.time < end) yield return null;
        }

        protected static IEnumerator Frames(int n)
        {
            for (int i = 0; i < n; i++) yield return null;
        }

        protected Vector2 Feet => G.Player.Feet;

        protected int Count(string item) => G.Inventory.Count(G.Db.ItemId(item));

        protected void Give(string item, int count, string why)
        {
            G.Inventory.Add(G.Db.ItemId(item), count);
            Note($"gave {count} {item}: {why}");
        }

        protected int SlotOf(string itemId)
        {
            int id = G.Db.ItemId(itemId);
            for (int i = 0; i < G.Inventory.Size; i++)
                if (G.Inventory[i].item == id) return i;
            return -1;
        }

        /// <summary>Moves an item stack into the hotbar (if needed) and selects it.</summary>
        protected bool Select(string itemId)
        {
            int slot = SlotOf(itemId);
            if (slot < 0) return false;
            if (slot >= 10)
            {
                G.Inventory.MoveSlot(slot, 9);
                slot = 9;
            }
            In.SelectHotbar(slot);
            return true;
        }

        protected void Aim(Vector2Int tile) => In.aimWorld = tile + new Vector2(0.5f, 0.5f);

        /// <summary>Holds Use on a tile until it is empty (or the time runs out).</summary>
        protected IEnumerator MineTile(Vector2Int tile, float timeout = 4f)
        {
            Aim(tile);
            In.useHeld = true;
            float end = Time.time + timeout;
            while (Time.time < end && G.World.GetBlock(tile.x, tile.y) != 0) yield return null;
            In.useHeld = false;
            yield return null;
        }

        /// <summary>Waits out any use cooldown, then holds Use on the tile briefly (time-based: frame counts mean
        /// nothing at 1000 fps).</summary>
        protected IEnumerator UseOnce(Vector2Int tile)
        {
            Aim(tile);
            yield return Seconds(0.25f);
            In.useHeld = true;
            yield return Seconds(0.06f);
            In.useHeld = false;
            yield return Seconds(0.05f);
        }

        /// <summary>Tile columns the player's body currently overlaps.</summary>
        protected System.Collections.Generic.IEnumerable<int> BodyColumns()
        {
            var b = G.Player.Body;
            for (int x = Mathf.FloorToInt(b.Left + 0.01f); x <= Mathf.FloorToInt(b.Right - 0.01f); x++) yield return x;
        }

        /// <summary>The nearest empty tile within 5 tiles that a block could legally go on (anchored, and not
        /// inside the player's body unless <paramref name="allowBodyOverlap"/>).</summary>
        protected Vector2Int? FindPlaceSpot(System.Func<Vector2Int, bool> extra = null, bool allowBodyOverlap = false)
        {
            var c = FeetTile;
            Vector2Int? best = null;
            float bestD = float.MaxValue;
            for (int dy = -2; dy <= 5; dy++)
            for (int dx = -5; dx <= 5; dx++)
            {
                var t = new Vector2Int(c.x + dx, c.y + dy);
                if (G.World.GetBlock(t.x, t.y) != 0) continue;
                bool anchored = G.World.GetBlock(t.x, t.y - 1) != 0 || G.World.GetBlock(t.x - 1, t.y) != 0 ||
                                G.World.GetBlock(t.x + 1, t.y) != 0 || G.World.GetWall(t.x, t.y) != 0;
                if (!anchored || (!allowBodyOverlap && Loamlight.World.Collision.TileCollision.Overlaps(G.Player.Body, t.x, t.y))) continue;
                if (extra != null && !extra(t)) continue;
                float d = Vector2.Distance(G.Player.Center, t + Vector2.one * 0.5f);
                if (d > 5f || d >= bestD) continue;
                bestD = d;
                best = t;
            }
            return best;
        }

        protected Vector2Int FeetTile => new Vector2Int(Mathf.FloorToInt(Feet.x), Mathf.FloorToInt(Feet.y + 0.01f));
    }

}
