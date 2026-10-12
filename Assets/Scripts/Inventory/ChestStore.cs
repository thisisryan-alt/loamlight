using System;
using System.Collections.Generic;
using Loamlight.Content;
using UnityEngine;

namespace Loamlight.Inventory
{
    /// <summary>Contents of every placed chest, keyed by the chest's origin tile. Saved with the world.</summary>
    public sealed class ChestStore
    {
        public const int ChestSize = 30;

        readonly Dictionary<Vector2Int, ItemContainer> _chests = new Dictionary<Vector2Int, ItemContainer>();
        readonly ContentDatabase _db;

        public ChestStore(ContentDatabase db) => _db = db;

        public ItemContainer Get(Vector2Int origin)
        {
            if (!_chests.TryGetValue(origin, out var c))
            {
                c = ItemContainer.ForContent(ChestSize, _db);
                _chests[origin] = c;
            }
            return c;
        }

        public bool IsEmpty(Vector2Int origin)
        {
            if (!_chests.TryGetValue(origin, out var c)) return true;
            for (int i = 0; i < c.Size; i++) if (!c[i].IsEmpty) return false;
            return true;
        }

        public void Remove(Vector2Int origin) => _chests.Remove(origin);

        public IEnumerable<KeyValuePair<Vector2Int, ItemContainer>> All => _chests;

        /// <summary>Serialized as {"x":..,"y":..,"items":["id*count", ...]} entries.</summary>
        public List<object> Capture()
        {
            var list = new List<object>();
            foreach (var kv in _chests)
            {
                if (IsEmpty(kv.Key)) continue;
                list.Add(new Dictionary<string, object>
                {
                    ["x"] = (double)kv.Key.x, ["y"] = (double)kv.Key.y, ["items"] = ContainerCodec.Capture(kv.Value, _db),
                });
            }
            return list;
        }

        public void Restore(List<object> data)
        {
            _chests.Clear();
            if (data == null) return;
            foreach (var o in data)
            {
                if (!(o is Dictionary<string, object> d)) continue;
                var origin = new Vector2Int((int)Convert.ToDouble(d["x"]), (int)Convert.ToDouble(d["y"]));
                ContainerCodec.Restore(Get(origin), d["items"] as List<object>, _db);
            }
        }
    }

    /// <summary>Item containers as ["item_id*count" | "" per slot]: readable, and survives content id changes.</summary>
    public static class ContainerCodec
    {
        public static List<object> Capture(ItemContainer c, ContentDatabase db)
        {
            var slots = new List<object>(c.Size);
            for (int i = 0; i < c.Size; i++)
            {
                var s = c[i];
                slots.Add(s.IsEmpty ? "" : $"{db.Item(s.item).id}*{s.count}");
            }
            return slots;
        }

        public static void Restore(ItemContainer c, List<object> slots, ContentDatabase db)
        {
            c.Clear();
            if (slots == null) return;
            for (int i = 0; i < slots.Count && i < c.Size; i++)
            {
                var s = slots[i] as string;
                if (string.IsNullOrEmpty(s)) continue;
                var parts = s.Split('*');
                if (parts.Length == 2 && db.TryItemId(parts[0], out var id) && int.TryParse(parts[1], out var count))
                    c.Set(i, new ItemStack(id, count));
            }
        }
    }
}
