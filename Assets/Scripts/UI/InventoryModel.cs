using System;
using Loamlight.Inventory;

namespace Loamlight.UI
{
    /// <summary>
    /// Mouse-driven item handling, independent of any view (unit-tested; the scenario runner drives it directly):
    /// left click picks up / puts down / merges / swaps; right click splits a stack or drops one item; shift-click
    /// quick-moves between the inventory and an open chest (or between hotbar and backpack); anything put in the
    /// trash slot is gone when something else replaces it.
    /// </summary>
    public sealed class InventoryModel
    {
        public readonly ItemContainer Player;
        public readonly ItemContainer Trash;
        public ItemContainer Chest { get; set; }
        public ItemStack Cursor { get; private set; }
        readonly Func<int, int> _maxStack;

        public event Action Changed;

        public InventoryModel(ItemContainer player, Func<int, int> maxStack)
        {
            Player = player;
            _maxStack = maxStack ?? (_ => 999);
            Trash = new ItemContainer(1, _maxStack);
        }

        int Max(int item) => Math.Max(1, _maxStack(item));

        public void LeftClick(ItemContainer c, int slot, bool shift)
        {
            if (shift && Cursor.IsEmpty)
            {
                QuickMove(c, slot);
                return;
            }
            var s = c[slot];
            if (Cursor.IsEmpty)
            {
                if (s.IsEmpty) return;
                Cursor = s;
                c.Set(slot, ItemStack.Empty);
            }
            else if (s.IsEmpty)
            {
                c.Set(slot, Cursor);
                Cursor = ItemStack.Empty;
            }
            else if (s.item == Cursor.item)
            {
                int room = Max(s.item) - s.count;
                int move = Math.Min(room, Cursor.count);
                c.Set(slot, new ItemStack(s.item, s.count + move));
                Cursor = new ItemStack(Cursor.item, Cursor.count - move);
            }
            else
            {
                c.Set(slot, Cursor);
                Cursor = s;
            }
            Changed?.Invoke();
        }

        public void RightClick(ItemContainer c, int slot)
        {
            var s = c[slot];
            if (Cursor.IsEmpty)
            {
                if (s.IsEmpty) return;
                int take = (s.count + 1) / 2;
                Cursor = new ItemStack(s.item, take);
                c.Set(slot, new ItemStack(s.item, s.count - take));
            }
            else if (!s.IsEmpty && s.item == Cursor.item)
            {
                if (Cursor.count >= Max(s.item)) return;
                Cursor = new ItemStack(Cursor.item, Cursor.count + 1);
                c.Set(slot, new ItemStack(s.item, s.count - 1));
            }
            else if (s.IsEmpty)
            {
                c.Set(slot, new ItemStack(Cursor.item, 1));
                Cursor = new ItemStack(Cursor.item, Cursor.count - 1);
            }
            Changed?.Invoke();
        }

        void QuickMove(ItemContainer c, int slot)
        {
            var s = c[slot];
            if (s.IsEmpty) return;
            ItemContainer target;
            if (Chest != null) target = c == Chest ? Player : Chest;
            else if (c == Player) target = null;
            else target = Player;
            int left;
            if (target != null) left = target.Add(s.item, s.count);
            else
            {
                // no chest: hotbar <-> backpack
                left = slot < ItemContainer.HotbarSize ? AddRange(Player, s, ItemContainer.HotbarSize, Player.Size) : AddRange(Player, s, 0, ItemContainer.HotbarSize);
            }
            c.Set(slot, new ItemStack(s.item, left));
            Changed?.Invoke();
        }

        int AddRange(ItemContainer c, ItemStack s, int from, int to)
        {
            int left = s.count, max = Max(s.item);
            for (int i = from; i < to && left > 0; i++)
            {
                var t = c[i];
                if (t.item != s.item || t.count >= max) continue;
                int move = Math.Min(max - t.count, left);
                c.Set(i, new ItemStack(s.item, t.count + move));
                left -= move;
            }
            for (int i = from; i < to && left > 0; i++)
            {
                if (!c[i].IsEmpty) continue;
                int move = Math.Min(max, left);
                c.Set(i, new ItemStack(s.item, move));
                left -= move;
            }
            return left;
        }

        /// <summary>Puts the cursor stack back into the inventory; returns what did not fit (caller drops it).</summary>
        public ItemStack ReturnCursor()
        {
            if (Cursor.IsEmpty) return ItemStack.Empty;
            int left = Player.Add(Cursor.item, Cursor.count);
            var rest = new ItemStack(Cursor.item, left);
            Cursor = ItemStack.Empty;
            Changed?.Invoke();
            return rest;
        }

        public void ClearCursor()
        {
            Cursor = ItemStack.Empty;
            Changed?.Invoke();
        }
    }
}
