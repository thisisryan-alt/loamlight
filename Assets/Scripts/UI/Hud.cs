using Loamlight.Content;
using Loamlight.Inventory;
using UnityEngine;
using UnityEngine.UI;

namespace Loamlight.UI
{
    /// <summary>Shared helpers for building pixel-art uGUI from code with integer scaling.</summary>
    public static class UiKit
    {
        static Sprite _white;
        static Font _font;

        public static Sprite White
        {
            get
            {
                if (_white == null)
                {
                    var t = new Texture2D(2, 2) { filterMode = FilterMode.Point };
                    var w = new Color32(255, 255, 255, 255);
                    t.SetPixels32(new[] { w, w, w, w });
                    t.Apply();
                    _white = Sprite.Create(t, new Rect(0, 0, 2, 2), Vector2.one * 0.5f, 100f);
                }
                return _white;
            }
        }

        public static Font Font
        {
            get
            {
                if (_font == null) _font = Resources.Load<Font>("UI/PixelFont");
                if (_font == null) _font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
                return _font;
            }
        }

        public static Canvas CreateCanvas(string name, int sortOrder)
        {
            var go = new GameObject(name);
            var canvas = go.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = sortOrder;
            canvas.pixelPerfect = true;
            var scaler = go.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ConstantPixelSize;
            scaler.referencePixelsPerUnit = 16;   // sprite borders (16 PPU) map 1:1 to canvas pixels
            go.AddComponent<IntegerUiScale>();
            go.AddComponent<GraphicRaycaster>();
            return canvas;
        }

        public static RectTransform Rect(string name, Transform parent, Vector2 anchor, Vector2 pivot, Vector2 pos, Vector2 size)
        {
            var go = new GameObject(name, typeof(RectTransform));
            var rt = (RectTransform)go.transform;
            rt.SetParent(parent, false);
            rt.anchorMin = rt.anchorMax = anchor;
            rt.pivot = pivot;
            rt.anchoredPosition = pos;
            rt.sizeDelta = size;
            return rt;
        }

        public static Image Panel(string name, Transform parent, Vector2 anchor, Vector2 pivot, Vector2 pos, Vector2 size, Color color, Sprite sprite = null)
        {
            var rt = Rect(name, parent, anchor, pivot, pos, size);
            var img = rt.gameObject.AddComponent<Image>();
            img.sprite = sprite != null ? sprite : White;
            img.type = sprite != null && sprite.border != Vector4.zero ? Image.Type.Sliced : Image.Type.Simple;
            img.color = color;
            img.raycastTarget = false;
            return img;
        }

        public static Text Label(string name, Transform parent, Vector2 anchor, Vector2 pivot, Vector2 pos, Vector2 size, string text,
            int fontSize = 8, TextAnchor align = TextAnchor.MiddleLeft, Color? color = null)
        {
            var rt = Rect(name, parent, anchor, pivot, pos, size);
            var t = rt.gameObject.AddComponent<Text>();
            t.font = Font;
            t.fontSize = fontSize;
            t.alignment = align;
            t.color = color ?? new Color(0.95f, 0.93f, 0.88f);
            t.text = text;
            t.raycastTarget = false;
            t.horizontalOverflow = HorizontalWrapMode.Overflow;
            t.verticalOverflow = VerticalWrapMode.Overflow;
            var shadow = rt.gameObject.AddComponent<Shadow>();
            shadow.effectColor = new Color(0f, 0f, 0f, 0.85f);
            shadow.effectDistance = new Vector2(1f, -1f);
            return t;
        }
    }

    /// <summary>Top-left hotbar: 10 slots with icon, count and selection frame, plus the held item's name.</summary>
    public sealed class Hud : MonoBehaviour
    {
        const int Slot = 20, Gap = 2;

        ItemContainer _inventory;
        ContentDatabase _db;
        RectTransform _hotbarRoot;
        Sprite _slot, _slotHot;
        Image[] _frames, _icons;
        Text[] _counts;
        Text _itemName;
        bool _dirty = true;

        public static Hud Create(ItemContainer inventory, ContentDatabase db)
        {
            var canvas = UiKit.CreateCanvas("HUD", 10);
            var hud = canvas.gameObject.AddComponent<Hud>();
            hud._inventory = inventory;
            hud._db = db;
            hud.Build(canvas.transform);
            inventory.Changed += () => hud._dirty = true;
            return hud;
        }

        public void SetHotbarVisible(bool visible) => _hotbarRoot.gameObject.SetActive(visible);

        Text _toast;
        float _toastTime;

        /// <summary>Short message in the middle-top of the screen that fades out.</summary>
        public void Toast(string text, float seconds = 2.5f)
        {
            if (_toast == null)
                _toast = UiKit.Label("Toast", transform, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0, -60), new Vector2(300, 10), "", 8,
                    TextAnchor.MiddleCenter, new Color(1f, 0.92f, 0.7f));
            _toast.text = text;
            _toastTime = seconds;
            LastToast = text;
        }

        public string LastToast { get; private set; }

        Image _hintPanel;
        Text _hint;

        /// <summary>A tutorial hint under the hotbar (null hides it).</summary>
        public void SetHint(string text)
        {
            if (_hint == null)
            {
                _hintPanel = UiKit.Panel("Hint", transform, new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(0, 30), new Vector2(380, 18), Color.white,
                    UiSprites.Get("ui_tooltip"));
                _hint = UiKit.Label("Hint text", _hintPanel.transform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(370, 12), "", 8,
                    TextAnchor.MiddleCenter, new Color(1f, 0.95f, 0.8f));
            }
            bool show = !string.IsNullOrEmpty(text);
            _hintPanel.enabled = show;
            _hint.enabled = show;
            if (show) _hint.text = text;
            CurrentHint = show ? text : null;
        }

        public string CurrentHint { get; private set; }

        void Update()
        {
            if (_toast == null) return;
            _toastTime -= Time.unscaledDeltaTime;
            var c = _toast.color;
            c.a = Mathf.Clamp01(_toastTime / 0.6f);
            _toast.color = c;
        }

        void Build(Transform root)
        {
            _slot = UiSprites.Get("ui_slot");
            _slotHot = UiSprites.Get("ui_slot_hot");
            _hotbarRoot = UiKit.Rect("Hotbar", root, new Vector2(0, 1), new Vector2(0, 1), Vector2.zero, new Vector2(240, 40));
            root = _hotbarRoot;
            _frames = new Image[ItemContainer.HotbarSize];
            _icons = new Image[ItemContainer.HotbarSize];
            _counts = new Text[ItemContainer.HotbarSize];
            for (int i = 0; i < ItemContainer.HotbarSize; i++)
            {
                var pos = new Vector2(6 + i * (Slot + Gap), -6);
                _frames[i] = UiKit.Panel($"Slot {i}", root, new Vector2(0, 1), new Vector2(0, 1), pos, new Vector2(Slot, Slot), Color.white, _slot);
                _icons[i] = UiKit.Panel("Icon", _frames[i].transform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(16, 16), Color.white);
                _icons[i].preserveAspect = true;
                _counts[i] = UiKit.Label("Count", _frames[i].transform, new Vector2(1, 0), new Vector2(1, 0), new Vector2(-1, 0), new Vector2(18, 8), "",
                    8, TextAnchor.LowerRight);
                UiKit.Label("Key", _frames[i].transform, new Vector2(0, 1), new Vector2(0, 1), new Vector2(2, -1), new Vector2(8, 8),
                    ((i + 1) % 10).ToString(), 8, TextAnchor.UpperLeft, new Color(0.8f, 0.8f, 0.85f, 0.7f));
            }
            _itemName = UiKit.Label("Item name", root, new Vector2(0, 1), new Vector2(0, 1), new Vector2(7, -6 - Slot - 2), new Vector2(200, 10), "", 8);
        }

        void LateUpdate()
        {
            if (!_dirty) return;
            _dirty = false;
            for (int i = 0; i < ItemContainer.HotbarSize; i++)
            {
                var stack = _inventory[i];
                var def = stack.IsEmpty ? null : _db.Item(stack.item);
                _icons[i].enabled = def != null && def.icon != null;
                if (def != null) _icons[i].sprite = def.icon;
                _counts[i].text = stack.count > 1 ? stack.count.ToString() : "";
                _frames[i].sprite = i == _inventory.Selected ? _slotHot : _slot;
            }
            var sel = _inventory.SelectedStack;
            _itemName.text = sel.IsEmpty ? "" : _db.Item(sel.item)?.displayName ?? "";
        }

        public void MarkDirty() => _dirty = true;
    }
}
