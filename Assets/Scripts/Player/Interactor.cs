using System;
using Loamlight.Content;
using Loamlight.Core;
using Loamlight.World;
using Loamlight.World.Collision;
using UnityEngine;

namespace Loamlight.Player
{
    /// <summary>Right-click on things: doors open and close, chests open their storage, beds set the respawn point.</summary>
    public sealed class Interactor : MonoBehaviour
    {
        public const float Reach = 5.5f;

        public IPlayerInput Input { get; set; }
        public PlayerController Player { get; set; }
        public WorldEdits Edits { get; set; }
        public ContentDatabase Db { get; set; }
        public bool Enabled { get; set; } = true;

        public event Action<Vector2Int, bool> DoorToggled;   // origin, now open
        public event Action<Vector2Int> ChestOpened;         // origin
        public event Action<Vector2Int> SpawnSet;            // bed origin

        void Update()
        {
            if (Input == null || Edits == null) return;
            if (!Input.ConsumeInteractPressed() || !Enabled) return;
            var aim = Input.AimWorld;
            Interact(new Vector2Int(Mathf.FloorToInt(aim.x), Mathf.FloorToInt(aim.y)));
        }

        /// <summary>Interacts with the tile (or the object covering it); returns true if something happened.</summary>
        public bool Interact(Vector2Int tile)
        {
            ushort b = Edits.World.GetBlock(tile.x, tile.y);
            if (b == 0) return false;
            if (Vector2.Distance(Player.Center, tile + new Vector2(0.5f, 0.5f)) > Reach + 0.5f) return false;
            var def = Db.Tile(b);
            var origin = Edits.ObjectOrigin(tile.x, tile.y);
            switch (def.interact)
            {
                case "door":
                    if (!Edits.ToggleDoor(tile.x, tile.y, (x, y) => TileCollision.Overlaps(Player.Body, x, y))) return false;
                    bool open = Edits.IsDoorOpen(origin.x, origin.y);
                    Sfx.Play(open ? "door_open" : "door_close", origin + new Vector2(0.5f, 1.5f));
                    DoorToggled?.Invoke(origin, open);
                    return true;
                case "chest":
                    Sfx.Play("chest_open", origin + Vector2.one);
                    ChestOpened?.Invoke(origin);
                    return true;
                case "bed":
                    Sfx.Play("ui_click", origin + Vector2.one, 0.8f);
                    SpawnSet?.Invoke(origin);
                    return true;
                default:
                    return false;
            }
        }
    }
}
