using System;
using Loamlight.Content;

namespace Loamlight.Inventory
{
    [Serializable]
    public struct ItemStack
    {
        public int item;   // numeric item id (ContentDatabase.ItemId), 0 = empty
        public int count;

        public bool IsEmpty => item == 0 || count <= 0;
        public static readonly ItemStack Empty = default;

        public ItemStack(int item, int count)
        {
            this.item = count > 0 ? item : 0;
            this.count = item != 0 ? count : 0;
        }

        public override string ToString() => IsEmpty ? "(empty)" : $"{item} x{count}";
    }

    /// <summary>
    /// Fixed-size slot inventory; the first <see cref="HotbarSize"/> slots are the hotbar. Pure logic, no Unity
    /// objects, so it is fully unit-tested. Stack limits come from <see cref="ItemDef.maxStack"/>.
    /// </summary>
    public sealed class ItemContainer
    {
        public const int HotbarSize = 10;

        readonly ItemStack[] _slots;
        readonly Func<int, int> _maxStack;
        int _selected;

        public event Action Changed;

        public ItemContainer(int size, Func<int, int> maxStack)
        {
            _slots = new ItemStack[size];
            _maxStack = maxStack ?? (_ => 999);
        }

        public static ItemContainer ForContent(int size, ContentDatabase db) => new ItemContainer(size, id => db.Item(id)?.maxStack ?? 999);

        public int Size => _slots.Length;
        public ItemStack this[int slot] => _slots[slot];

        public int Selected
        {
            get => _selected;
            set
            {
                int v = ((value % HotbarSize) + HotbarSize) % HotbarSize;
                if (v == _selected) return;
                _selected = v;
                Changed?.Invoke();
            }
        }

        public ItemStack SelectedStack => _slots[_selected];

        public void Set(int slot, ItemStack stack)
        {
            _slots[slot] = stack.IsEmpty ? ItemStack.Empty : stack;
            Changed?.Invoke();
        }

        /// <summary>Adds items, topping up existing stacks first (hotbar order), then empty slots. Returns leftovers.</summary>
        public int Add(int item, int count)
        {
            if (item == 0 || count <= 0) return 0;
            int max = Math.Max(1, _maxStack(item));
            int left = count;
            for (int i = 0; i < _slots.Length && left > 0; i++)
            {
                if (_slots[i].item != item || _slots[i].count >= max) continue;
                int take = Math.Min(max - _slots[i].count, left);
                _slots[i].count += take;
                left -= take;
            }
            for (int i = 0; i < _slots.Length && left > 0; i++)
            {
                if (!_slots[i].IsEmpty) continue;
                int take = Math.Min(max, left);
                _slots[i] = new ItemStack(item, take);
                left -= take;
            }
            if (left != count) Changed?.Invoke();
            return left;
        }

        /// <summary>True if the whole amount could be added (does not modify the inventory).</summary>
        public bool CanAdd(int item, int count)
        {
            int max = Math.Max(1, _maxStack(item));
            int room = 0;
            foreach (var s in _slots)
            {
                if (s.IsEmpty) room += max;
                else if (s.item == item) room += max - s.count;
                if (room >= count) return true;
            }
            return room >= count;
        }

        public int Count(int item)
        {
            int n = 0;
            foreach (var s in _slots) if (s.item == item) n += s.count;
            return n;
        }

        /// <summary>Removes exactly <paramref name="count"/> items (from the end, so the hotbar keeps its stacks) or nothing.</summary>
        public bool Remove(int item, int count)
        {
            if (count <= 0) return true;
            if (Count(item) < count) return false;
            int left = count;
            for (int i = _slots.Length - 1; i >= 0 && left > 0; i--)
            {
                if (_slots[i].item != item) continue;
                int take = Math.Min(_slots[i].count, left);
                _slots[i].count -= take;
                left -= take;
                if (_slots[i].count == 0) _slots[i] = ItemStack.Empty;
            }
            Changed?.Invoke();
            return true;
        }

        /// <summary>Consumes one item from a specific slot (e.g. the selected hotbar slot when placing).</summary>
        public bool ConsumeFromSlot(int slot, int count = 1)
        {
            if (_slots[slot].IsEmpty || _slots[slot].count < count) return false;
            _slots[slot].count -= count;
            if (_slots[slot].count == 0) _slots[slot] = ItemStack.Empty;
            Changed?.Invoke();
            return true;
        }

        /// <summary>Moves/merges/swaps the stack in <paramref name="from"/> onto <paramref name="to"/>.</summary>
        public void MoveSlot(int from, int to)
        {
            if (from == to) return;
            var a = _slots[from];
            var b = _slots[to];
            if (!a.IsEmpty && a.item == b.item)
            {
                int max = Math.Max(1, _maxStack(a.item));
                int take = Math.Min(max - b.count, a.count);
                _slots[to].count += take;
                _slots[from].count -= take;
                if (_slots[from].count == 0) _slots[from] = ItemStack.Empty;
            }
            else
            {
                _slots[from] = b;
                _slots[to] = a;
            }
            Changed?.Invoke();
        }

        public void Clear()
        {
            for (int i = 0; i < _slots.Length; i++) _slots[i] = ItemStack.Empty;
            Changed?.Invoke();
        }
    }
}
