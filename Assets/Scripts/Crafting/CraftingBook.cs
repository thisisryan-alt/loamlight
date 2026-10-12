using System;
using System.Collections.Generic;
using Loamlight.Content;
using Loamlight.Inventory;

namespace Loamlight.Crafting
{
    /// <summary>
    /// Recipe logic, pure and unit-tested: which recipes the player can see (station nearby), which they can make
    /// (ingredients present, room for the result), and the craft itself (all-or-nothing).
    /// </summary>
    public sealed class CraftingBook
    {
        readonly ContentDatabase _db;
        readonly List<RecipeDef> _recipes;

        public CraftingBook(ContentDatabase db)
        {
            _db = db;
            _recipes = new List<RecipeDef>(db.recipes);
        }

        public IReadOnlyList<RecipeDef> All => _recipes;

        public RecipeDef Find(string id) => _recipes.Find(r => r.id == id);

        /// <summary>Recipes whose station requirement is met (by hand, or the station tile is nearby).</summary>
        public List<RecipeDef> Available(ISet<string> nearbyStations)
        {
            var list = new List<RecipeDef>();
            foreach (var r in _recipes)
                if (StationOk(r, nearbyStations)) list.Add(r);
            return list;
        }

        public static bool StationOk(RecipeDef r, ISet<string> nearbyStations) =>
            string.IsNullOrEmpty(r.station) || (nearbyStations != null && nearbyStations.Contains(r.station));

        public bool HasIngredients(RecipeDef r, ItemContainer inv)
        {
            foreach (var ing in r.ingredients)
                if (inv.Count(_db.ItemId(ing.item)) < ing.count) return false;
            return true;
        }

        /// <summary>How many times the recipe could be made right now (ingredients only).</summary>
        public int MaxCrafts(RecipeDef r, ItemContainer inv)
        {
            int max = int.MaxValue;
            foreach (var ing in r.ingredients) max = Math.Min(max, inv.Count(_db.ItemId(ing.item)) / Math.Max(1, ing.count));
            return max == int.MaxValue ? 0 : max;
        }

        public bool CanCraft(RecipeDef r, ItemContainer inv, ISet<string> nearbyStations) =>
            StationOk(r, nearbyStations) && HasIngredients(r, inv) && inv.CanAdd(_db.ItemId(r.result), r.resultCount);

        /// <summary>Crafts once. Ingredients are removed only if the result fits; returns false and changes nothing otherwise.</summary>
        public bool Craft(RecipeDef r, ItemContainer inv, ISet<string> nearbyStations)
        {
            if (!CanCraft(r, inv, nearbyStations)) return false;
            foreach (var ing in r.ingredients) inv.Remove(_db.ItemId(ing.item), ing.count);
            int left = inv.Add(_db.ItemId(r.result), r.resultCount);
            if (left > 0)
            {
                // cannot happen after CanAdd unless ingredient removal freed nothing; roll back defensively
                inv.Remove(_db.ItemId(r.result), r.resultCount - left);
                foreach (var ing in r.ingredients) inv.Add(_db.ItemId(ing.item), ing.count);
                return false;
            }
            return true;
        }

        /// <summary>Every recipe that uses <paramref name="itemId"/> as an ingredient (tooltips: "used in").</summary>
        public List<RecipeDef> UsesOf(string itemId)
        {
            var list = new List<RecipeDef>();
            foreach (var r in _recipes)
                foreach (var ing in r.ingredients)
                    if (ing.item == itemId) { list.Add(r); break; }
            return list;
        }
    }
}
