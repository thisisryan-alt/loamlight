using System;
using System.Collections.Generic;
using System.Linq;
using Loamlight.Content;
using Loamlight.Core;
using Loamlight.Crafting;
using Loamlight.Inventory;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace Loamlight.UI
{
    /// <summary>
    /// The inventory screen: 50-slot backpack (first row = hotbar), trash, an open chest, and the crafting panel
    /// (recipes available at nearby stations; dimmed when ingredients are missing). Built in code, pixel font,
    /// integer-scaled. Item handling lives in <see cref="InventoryModel"/>.
    /// </summary>
    public sealed class InventoryScreen : MonoBehaviour
    {
        const int Pitch = 22, SlotSize = 20, RecipeCols = 8, RecipePitch = 20;

        public InventoryModel Model { get; private set; }
        public bool IsOpen { get; private set; }
        public bool ChestOpen => Model.Chest != null;
        public Vector2Int ChestOrigin { get; private set; }
        public RecipeDef SelectedRecipe { get; private set; }

        /// <summary>Supplied by the game: stations within reach of the player.</summary>
        public Func<ISet<string>> NearbyStations { get; set; }
        public Func<Vector2> PlayerPosition { get; set; }
        public Action<ItemStack> DropOverflow { get; set; }
        public Func<Vector2Int, ItemContainer> ChestFor { get; set; }

        public event Action<RecipeDef> Crafted;
        public event Action<bool> OpenChanged;

        ContentDatabase _db;
        CraftingBook _book;
        Canvas _canvas;
        RectTransform _root, _inventoryPanel, _chestPanel, _craftPanel, _tooltip, _recipeGrid;
        SlotView[] _playerSlots, _chestSlots;
        SlotView _trash;
        readonly List<SlotView> _recipeViews = new List<SlotView>();
        List<RecipeDef> _visibleRecipes = new List<RecipeDef>();
        Text _tooltipText, _detailText, _craftLabel, _chestTitle;
        Image _cursorIcon;
        Text _cursorCount;
        Image _craftButton;
        Image[] _ingredientIcons;
        Text[] _ingredientTexts;
        Hud _hud;
        Sprite _slot, _slotHot, _panel, _button, _buttonHot, _tooltipSprite, _trashIcon;
        float _stationPoll;
        ISet<string> _stations = new HashSet<string>();
        bool _dirty = true;
        string _hover;
        InputAction _toggle, _back;

        public static InventoryScreen Create(ItemContainer inventory, ContentDatabase db, Hud hud)
        {
            var canvas = UiKit.CreateCanvas("Inventory", 20);
            var screen = canvas.gameObject.AddComponent<InventoryScreen>();
            screen._canvas = canvas;
            screen._db = db;
            screen._book = new CraftingBook(db);
            screen._hud = hud;
            screen.Model = new InventoryModel(inventory, id => db.Item(id)?.maxStack ?? 999);
            screen.Model.Changed += () => screen._dirty = true;
            inventory.Changed += () => screen._dirty = true;
            screen.Build();
            screen.SetOpen(false);
            return screen;
        }

        void Build()
        {
            _slot = UiSprites.Get("ui_slot"); _slotHot = UiSprites.Get("ui_slot_hot"); _panel = UiSprites.Get("ui_panel");
            _button = UiSprites.Get("ui_button"); _buttonHot = UiSprites.Get("ui_button_hot"); _tooltipSprite = UiSprites.Get("ui_tooltip");
            _trashIcon = UiSprites.Get("ui_trash");
            _root = (RectTransform)_canvas.transform;

            // backpack
            _inventoryPanel = UiKit.Panel("Backpack", _root, new Vector2(0, 1), new Vector2(0, 1), new Vector2(2, -2),
                new Vector2(10 * Pitch + 30, 5 * Pitch + 8), Color.white, _panel).rectTransform;
            _playerSlots = new SlotView[Model.Player.Size];
            for (int i = 0; i < Model.Player.Size; i++)
            {
                int slot = i;
                _playerSlots[i] = MakeSlot(_inventoryPanel, new Vector2(4 + (i % 10) * Pitch, -4 - (i / 10) * Pitch));
                _playerSlots[i].onClick = b => Click(Model.Player, slot, b);
                _playerSlots[i].onEnter = () => _hover = Tip(Model.Player[slot]);
                _playerSlots[i].onExit = () => _hover = null;
            }
            _trash = MakeSlot(_inventoryPanel, new Vector2(4 + 10 * Pitch + 4, -4 - 4 * Pitch));
            _trash.onClick = b => Click(Model.Trash, 0, b);
            _trash.onEnter = () => _hover = Model.Trash[0].IsEmpty ? "Trash\nAnything left here is destroyed when replaced." : Tip(Model.Trash[0]);
            _trash.onExit = () => _hover = null;
            var trashGlyph = UiKit.Panel("Trash icon", _trash.transform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero,
                new Vector2(16, 16), new Color(1, 1, 1, 0.35f), _trashIcon);
            trashGlyph.transform.SetAsFirstSibling();
            trashGlyph.transform.SetSiblingIndex(1);

            // chest
            _chestPanel = UiKit.Panel("Chest", _root, new Vector2(0, 1), new Vector2(0, 1), new Vector2(2, -2 - (5 * Pitch + 8) - 14),
                new Vector2(10 * Pitch + 8, 3 * Pitch + 8), Color.white, _panel).rectTransform;
            _chestTitle = UiKit.Label("Title", _chestPanel, new Vector2(0, 1), new Vector2(0, 0), new Vector2(4, 1), new Vector2(100, 10), "Chest", 8);
            _chestSlots = new SlotView[ChestStore.ChestSize];
            for (int i = 0; i < _chestSlots.Length; i++)
            {
                int slot = i;
                _chestSlots[i] = MakeSlot(_chestPanel, new Vector2(4 + (i % 10) * Pitch, -4 - (i / 10) * Pitch));
                _chestSlots[i].onClick = b => { if (Model.Chest != null) Click(Model.Chest, slot, b); };
                _chestSlots[i].onEnter = () => _hover = Model.Chest != null ? Tip(Model.Chest[slot]) : null;
                _chestSlots[i].onExit = () => _hover = null;
            }

            // crafting
            _craftPanel = UiKit.Panel("Crafting", _root, new Vector2(0, 1), new Vector2(0, 1), new Vector2(10 * Pitch + 40, -2),
                new Vector2(RecipeCols * RecipePitch + 8, 220), Color.white, _panel).rectTransform;
            UiKit.Label("Title", _craftPanel, new Vector2(0, 1), new Vector2(0, 1), new Vector2(5, -4), new Vector2(120, 10), "Crafting", 8,
                TextAnchor.UpperLeft, new Color(1f, 0.86f, 0.5f));
            _recipeGrid = UiKit.Rect("Recipes", _craftPanel, new Vector2(0, 1), new Vector2(0, 1), new Vector2(4, -16), new Vector2(RecipeCols * RecipePitch, 100));
            _detailText = UiKit.Label("Detail", _craftPanel, new Vector2(0, 1), new Vector2(0, 1), new Vector2(5, -122), new Vector2(160, 40), "", 8,
                TextAnchor.UpperLeft);
            _ingredientIcons = new Image[4];
            _ingredientTexts = new Text[4];
            for (int i = 0; i < 4; i++)
            {
                _ingredientIcons[i] = UiKit.Panel($"Ingredient {i}", _craftPanel, new Vector2(0, 1), new Vector2(0, 1), new Vector2(5, -146 - i * 14),
                    new Vector2(12, 12), Color.white);
                _ingredientIcons[i].preserveAspect = true;
                _ingredientTexts[i] = UiKit.Label($"Ingredient text {i}", _craftPanel, new Vector2(0, 1), new Vector2(0, 1), new Vector2(20, -148 - i * 14),
                    new Vector2(140, 10), "", 8, TextAnchor.UpperLeft);
            }
            _craftButton = UiKit.Panel("Craft button", _craftPanel, new Vector2(0, 0), new Vector2(0, 0), new Vector2(5, 5), new Vector2(60, 14), Color.white, _button);
            _craftButton.raycastTarget = true;
            _craftLabel = UiKit.Label("Craft label", _craftButton.transform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0, 1),
                new Vector2(60, 10), "Craft", 8, TextAnchor.MiddleCenter);
            var btn = _craftButton.gameObject.AddComponent<SlotView>();
            btn.onClick = _ => CraftSelected(Keyboard.current != null && Keyboard.current.shiftKey.isPressed ? 10 : 1);
            btn.onEnter = () => _hover = SelectedRecipe != null ? "Craft (Shift: up to 10)" : null;
            btn.onExit = () => _hover = null;

            // tooltip + cursor
            var tip = UiKit.Panel("Tooltip", _root, new Vector2(0, 1), new Vector2(0, 1), Vector2.zero, new Vector2(150, 30), Color.white, _tooltipSprite);
            _tooltip = tip.rectTransform;
            _tooltipText = UiKit.Label("Text", _tooltip, new Vector2(0, 1), new Vector2(0, 1), new Vector2(5, -4), new Vector2(140, 20), "", 8, TextAnchor.UpperLeft);
            _cursorIcon = UiKit.Panel("Cursor item", _root, new Vector2(0, 1), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(16, 16), Color.white);
            _cursorIcon.preserveAspect = true;
            _cursorCount = UiKit.Label("Cursor count", _cursorIcon.transform, new Vector2(1, 0), new Vector2(1, 0), new Vector2(4, -4), new Vector2(20, 8), "", 8,
                TextAnchor.LowerRight);

            _toggle = new InputAction("Inventory", InputActionType.Button, GameSettings.Key("Inventory") ?? "<Keyboard>/e");
            GameSettings.Changed += () =>
            {
                if (_toggle == null) return;
                _toggle.Disable();
                _toggle.ApplyBindingOverride(0, GameSettings.Key("Inventory"));
                _toggle.Enable();
            };
            _toggle.AddBinding("<Keyboard>/tab");
            _toggle.AddBinding("<Gamepad>/buttonNorth");
            _back = new InputAction("Back", InputActionType.Button, "<Keyboard>/escape");
            _back.AddBinding("<Gamepad>/buttonEast");
            _toggle.Enable();
            _back.Enable();
        }

        /// <summary>Rebindable UI actions (settings menu).</summary>
        public InputAction ToggleAction => _toggle;

        SlotView MakeSlot(RectTransform parent, Vector2 pos)
        {
            var frame = UiKit.Panel("Slot", parent, new Vector2(0, 1), new Vector2(0, 1), pos, new Vector2(SlotSize, SlotSize), Color.white, _slot);
            frame.raycastTarget = true;
            var view = frame.gameObject.AddComponent<SlotView>();
            view.frame = frame;
            view.icon = UiKit.Panel("Icon", frame.transform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(16, 16), Color.white);
            view.icon.preserveAspect = true;
            view.count = UiKit.Label("Count", frame.transform, new Vector2(1, 0), new Vector2(1, 0), new Vector2(-1, 0), new Vector2(18, 9), "", 8, TextAnchor.LowerRight);
            return view;
        }

        void OnDestroy()
        {
            _toggle?.Dispose();
            _back?.Dispose();
        }

        // ------------------------------------------------------------------------------------------- open / close
        public void SetOpen(bool open)
        {
            if (!open)
            {
                var rest = Model.ReturnCursor();
                if (!rest.IsEmpty) DropOverflow?.Invoke(rest);
                CloseChest();
            }
            IsOpen = open;
            _inventoryPanel.gameObject.SetActive(open);
            _craftPanel.gameObject.SetActive(open);
            _chestPanel.gameObject.SetActive(open && Model.Chest != null);
            _tooltip.gameObject.SetActive(false);
            _cursorIcon.gameObject.SetActive(false);
            _hud?.SetHotbarVisible(!open);
            _stationPoll = 0f;
            _dirty = true;
            Sfx.PlayUi(open ? "ui_open" : "ui_close", 0.6f);
            OpenChanged?.Invoke(open);
        }

        public void OpenChest(Vector2Int origin)
        {
            ChestOrigin = origin;
            Model.Chest = ChestFor?.Invoke(origin);
            _chestTitle.text = "Chest";
            if (!IsOpen) SetOpen(true);
            _chestPanel.gameObject.SetActive(true);
            _dirty = true;
        }

        public void CloseChest()
        {
            if (Model.Chest == null) return;
            Model.Chest = null;
            if (_chestPanel != null) _chestPanel.gameObject.SetActive(false);
            Sfx.PlayUi("chest_close", 0.6f);
            _dirty = true;
        }

        void Click(ItemContainer c, int slot, PointerEventData.InputButton button)
        {
            bool shift = Keyboard.current != null && Keyboard.current.shiftKey.isPressed;
            if (button == PointerEventData.InputButton.Right) Model.RightClick(c, slot);
            else Model.LeftClick(c, slot, shift);
            Sfx.PlayUi("ui_click", 0.4f);
        }

        // ------------------------------------------------------------------------------------------- crafting
        public void SelectRecipe(RecipeDef r)
        {
            SelectedRecipe = r;
            _dirty = true;
        }

        /// <summary>Crafts the selected recipe up to <paramref name="times"/> times; returns how many were made.</summary>
        public int CraftSelected(int times = 1)
        {
            if (SelectedRecipe == null) return 0;
            int made = 0;
            for (int i = 0; i < times; i++)
            {
                if (!_book.Craft(SelectedRecipe, Model.Player, _stations)) break;
                made++;
                Crafted?.Invoke(SelectedRecipe);
            }
            Sfx.PlayUi(made > 0 ? "craft" : "cant", 0.7f);
            _dirty = true;
            return made;
        }

        public IReadOnlyList<RecipeDef> VisibleRecipes => _visibleRecipes;
        public ISet<string> Stations => _stations;

        public void RefreshStations()
        {
            _stations = NearbyStations?.Invoke() ?? new HashSet<string>();
            var list = _book.Available(_stations);
            list.Sort((a, b) =>
            {
                int ca = _book.HasIngredients(a, Model.Player) ? 0 : 1, cb = _book.HasIngredients(b, Model.Player) ? 0 : 1;
                return ca != cb ? ca.CompareTo(cb) : string.CompareOrdinal(a.id, b.id);
            });
            _visibleRecipes = list;
            if (SelectedRecipe != null && !list.Contains(SelectedRecipe)) SelectedRecipe = null;
            _dirty = true;
        }

        // ------------------------------------------------------------------------------------------- update
        void Update()
        {
            if (_toggle.WasPressedThisFrame()) SetOpen(!IsOpen);
            else if (IsOpen && _back.WasPressedThisFrame()) SetOpen(false);
            if (!IsOpen) return;

            _stationPoll -= Time.unscaledDeltaTime;
            if (_stationPoll <= 0f)
            {
                _stationPoll = 0.5f;
                RefreshStations();
            }
            if (Model.Chest != null && PlayerPosition != null &&
                Vector2.Distance(PlayerPosition(), ChestOrigin + Vector2.one) > 7f) CloseChest();
            if (_dirty) Refresh();
            FollowMouse();
        }

        string Tip(ItemStack s)
        {
            if (s.IsEmpty) return null;
            var d = _db.Item(s.item);
            if (d == null) return null;
            var lines = new List<string> { d.displayName };
            if (d.toolType == ToolType.Pickaxe) lines.Add($"Pickaxe power {d.toolPower}");
            if (d.toolType == ToolType.Axe) lines.Add($"Axe power {d.toolPower}");
            if (d.damage > 0 && d.weaponKind != WeaponKind.None) lines.Add($"{d.damage} damage");
            if (!string.IsNullOrEmpty(d.description)) lines.Add(d.description);
            return string.Join("\n", lines);
        }

        void FollowMouse()
        {
            if (Mouse.current == null) return;
            var screen = Mouse.current.position.ReadValue();
            RectTransformUtility.ScreenPointToLocalPointInRectangle(_root, screen, null, out var local);
            var topLeft = local + new Vector2(_root.rect.width * 0.5f, -_root.rect.height * 0.5f);
            _cursorIcon.gameObject.SetActive(!Model.Cursor.IsEmpty);
            _cursorIcon.rectTransform.anchoredPosition = topLeft + new Vector2(6, -6);
            bool tip = !string.IsNullOrEmpty(_hover) && Model.Cursor.IsEmpty;
            _tooltip.gameObject.SetActive(tip);
            if (tip)
            {
                _tooltipText.text = _hover;
                var size = new Vector2(Mathf.Max(60, _tooltipText.preferredWidth + 10), _tooltipText.preferredHeight + 9);
                _tooltip.sizeDelta = size;
                var pos = topLeft + new Vector2(12, -12);
                pos.x = Mathf.Min(pos.x, _root.rect.width - size.x - 2);
                pos.y = Mathf.Max(pos.y, -_root.rect.height + size.y + 2);
                _tooltip.anchoredPosition = pos;
            }
        }

        void Refresh()
        {
            _dirty = false;
            for (int i = 0; i < _playerSlots.Length; i++) Draw(_playerSlots[i], Model.Player[i], i < ItemContainer.HotbarSize && i == Model.Player.Selected);
            Draw(_trash, Model.Trash[0], false);
            if (Model.Chest != null)
                for (int i = 0; i < _chestSlots.Length; i++) Draw(_chestSlots[i], Model.Chest[i], false);
            var cur = Model.Cursor;
            if (!cur.IsEmpty)
            {
                _cursorIcon.sprite = _db.Item(cur.item)?.icon;
                _cursorCount.text = cur.count > 1 ? cur.count.ToString() : "";
            }

            // recipe grid
            while (_recipeViews.Count < _visibleRecipes.Count)
            {
                int index = _recipeViews.Count;
                var v = MakeSlot(_recipeGrid, new Vector2((index % RecipeCols) * RecipePitch, -(index / RecipeCols) * RecipePitch));
                v.frame.rectTransform.sizeDelta = new Vector2(18, 18);
                v.onClick = _ => { if (index < _visibleRecipes.Count) SelectRecipe(_visibleRecipes[index]); };
                v.onEnter = () => _hover = index < _visibleRecipes.Count ? RecipeTip(_visibleRecipes[index]) : null;
                v.onExit = () => _hover = null;
                _recipeViews.Add(v);
            }
            for (int i = 0; i < _recipeViews.Count; i++)
            {
                var v = _recipeViews[i];
                bool show = i < _visibleRecipes.Count;
                v.gameObject.SetActive(show);
                if (!show) continue;
                var r = _visibleRecipes[i];
                var item = _db.Item(r.result);
                v.icon.sprite = item?.icon;
                v.icon.enabled = item?.icon != null;
                bool can = _book.HasIngredients(r, Model.Player);
                v.icon.color = can ? Color.white : new Color(1, 1, 1, 0.3f);
                v.count.text = r.resultCount > 1 ? r.resultCount.ToString() : "";
                v.frame.sprite = r == SelectedRecipe ? _slotHot : _slot;
            }

            // detail
            var sel = SelectedRecipe;
            bool craftable = sel != null && _book.CanCraft(sel, Model.Player, _stations);
            _craftButton.sprite = craftable ? _buttonHot : _button;
            _craftLabel.color = craftable ? Color.white : new Color(1, 1, 1, 0.4f);
            if (sel == null)
            {
                _detailText.text = _visibleRecipes.Count == 0 ? "Nothing to craft here." : "Pick a recipe.";
                for (int i = 0; i < 4; i++) { _ingredientIcons[i].enabled = false; _ingredientTexts[i].text = ""; }
                return;
            }
            var res = _db.Item(sel.result);
            string station = string.IsNullOrEmpty(sel.station) ? "By hand" : "At: " + (_db.Tile(_db.TileId(sel.station))?.displayName ?? sel.station);
            _detailText.text = $"{res.displayName}{(sel.resultCount > 1 ? " x" + sel.resultCount : "")}\n{station}";
            for (int i = 0; i < 4; i++)
            {
                bool has = i < sel.ingredients.Length;
                _ingredientIcons[i].enabled = has;
                if (!has) { _ingredientTexts[i].text = ""; continue; }
                var ing = sel.ingredients[i];
                var idef = _db.Item(ing.item);
                int have = Model.Player.Count(_db.ItemId(ing.item));
                _ingredientIcons[i].sprite = idef.icon;
                _ingredientTexts[i].text = $"{idef.displayName} {Mathf.Min(have, 999)}/{ing.count}";
                _ingredientTexts[i].color = have >= ing.count ? new Color(0.85f, 1f, 0.8f) : new Color(1f, 0.6f, 0.55f);
            }
        }

        string RecipeTip(RecipeDef r)
        {
            var item = _db.Item(r.result);
            var parts = r.ingredients.Select(i => $"{i.count} {_db.Item(i.item).displayName}");
            return $"{item.displayName}{(r.resultCount > 1 ? " x" + r.resultCount : "")}\n{string.Join(", ", parts)}";
        }

        void Draw(SlotView v, ItemStack s, bool selected)
        {
            var def = s.IsEmpty ? null : _db.Item(s.item);
            v.icon.enabled = def?.icon != null;
            if (def != null) v.icon.sprite = def.icon;
            v.count.text = s.count > 1 ? s.count.ToString() : "";
            v.frame.sprite = selected ? _slotHot : _slot;
        }
    }

    /// <summary>Lookup for the generated UI kit sprites (sheet "ui").</summary>
    public static class UiSprites
    {
        static Dictionary<string, Sprite> _map;

        public static Sprite Get(string name)
        {
            if (_map == null)
            {
                _map = new Dictionary<string, Sprite>();
                var set = SpriteSheetIndex.Get("ui");
                if (set != null)
                    foreach (var clip in set.clips)
                        foreach (var f in clip.frames)
                            if (f != null) _map[f.name] = f;
            }
            return _map.TryGetValue(name, out var s) ? s : UiKit.White;
        }
    }
}
