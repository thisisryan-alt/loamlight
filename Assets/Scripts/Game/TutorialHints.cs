using System;
using Loamlight.Core;
using UnityEngine;

namespace Loamlight.Game
{
    /// <summary>
    /// First-world hints that advance as the player does each thing (move, dig, gather wood, craft a workbench, light a
    /// torch, prepare for night, go deeper). Progress is saved with the world; hints can be turned off in Settings.
    /// </summary>
    public sealed class TutorialHints : MonoBehaviour
    {
        GameBootstrap _g;
        Vector2 _start;
        bool _mined, _torch, _crafted;
        float _done = -1f;

        public int Step { get; set; }
        public string Current => Step < Steps.Length ? Steps[Step].text : null;

        struct HintStep
        {
            public string text;
            public Func<TutorialHints, bool> done;
        }

        static readonly HintStep[] Steps =
        {
            new HintStep { text = "Move with {MoveLeft}/{MoveRight}, jump with {Jump}.", done = h => Vector2.Distance(h._g.Player.Feet, h._start) > 6f },
            new HintStep { text = "Hold the left mouse button on dirt to dig with your pickaxe (1).", done = h => h._mined },
            new HintStep { text = "Fell a tree with the axe (2): hold the mouse on its trunk. Collect 10 wood.", done = h => h.Count("wood") >= 10 },
            new HintStep { text = "Press {Inventory} to open your inventory, then craft a Workbench.", done = h => h._crafted || h.Count("workbench") > 0 },
            new HintStep { text = "Torches push back the dark. Place one with the mouse (4).", done = h => h._torch },
            new HintStep { text = "Night brings the Hollows. Build walls and a door, and use a bed to set your spawn.", done = h => h._g.DayNight.IsNight || h._g.Player.Feet.y < h._start.y - 25f },
            new HintStep { text = "Dig deeper for copper and iron. Smelt at a Furnace, forge at an Anvil. Good luck!", done = h => h.Count("copper_ore") + h.Count("iron_ore") > 0 },
        };

        public void Init(GameBootstrap g, int step)
        {
            _g = g;
            Step = step;
            _start = g.Player.Feet;
            g.Tools.Mined += (tile, id) => _mined = true;
            g.Tools.Placed += (tile, id) => { if (g.Db.Tile(id)?.id == "torch") _torch = true; if (g.Db.Tile(id)?.id == "workbench") _crafted = true; };
            g.InventoryUi.Crafted += _ => _crafted = true;
        }

        int Count(string item) => _g.Db.TryItemId(item, out var id) ? _g.Inventory.Count(id) : 0;

        void Update()
        {
            if (_g == null || !_g.Ready) return;
            if (!GameSettings.Hints || Step >= Steps.Length) { _g.Hud.SetHint(null); return; }
            if (_done >= 0f)
            {
                // a short pause between hints so each one registers
                if (Time.time - _done > 1.2f) { _done = -1f; Step++; }
                _g.Hud.SetHint(null);
                return;
            }
            _g.Hud.SetHint(Format(Steps[Step].text));
            if (Steps[Step].done(this))
            {
                _done = Time.time;
                Sfx.PlayUi("pickup", 0.5f);
            }
        }

        static string Format(string text)
        {
            foreach (var (action, _, _) in GameSettings.Rebindable)
                text = text.Replace("{" + action + "}", GameSettings.KeyLabel(GameSettings.Key(action)));
            return text;
        }
    }
}
