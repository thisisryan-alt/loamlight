using System;
using Loamlight.Core;
using UnityEngine;
using UnityEngine.UI;

namespace Loamlight.UI
{
    /// <summary>Menu widgets built from the generated UI sheet: buttons, sliders, text fields, toggles.</summary>
    public static class MenuKit
    {
        public static readonly Color Text = new Color(0.95f, 0.93f, 0.88f);
        public static readonly Color Dim = new Color(0.7f, 0.68f, 0.64f);
        public static readonly Color Gold = new Color(1f, 0.85f, 0.45f);

        public static Image Panel(string name, Transform parent, Vector2 anchor, Vector2 pos, Vector2 size) =>
            UiKit.Panel(name, parent, anchor, new Vector2(0.5f, 0.5f), pos, size, Color.white, UiSprites.Get("ui_panel"));

        public static Button Button(string label, Transform parent, Vector2 anchor, Vector2 pos, Vector2 size, Action onClick)
        {
            var img = UiKit.Panel("Button " + label, parent, anchor, new Vector2(0.5f, 0.5f), pos, size, Color.white, UiSprites.Get("ui_button"));
            img.raycastTarget = true;
            var btn = img.gameObject.AddComponent<Button>();
            btn.transition = Selectable.Transition.SpriteSwap;
            btn.spriteState = new SpriteState { highlightedSprite = UiSprites.Get("ui_button_hot"), pressedSprite = UiSprites.Get("ui_button_hot"), selectedSprite = UiSprites.Get("ui_button") };
            btn.targetGraphic = img;
            var text = UiKit.Label("Label", img.transform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, size, label, 8, TextAnchor.MiddleCenter, Text);
            text.name = "Label";
            btn.onClick.AddListener(() =>
            {
                Sfx.PlayUi("ui_click", 0.6f);
                onClick?.Invoke();
            });
            return btn;
        }

        public static void SetLabel(Button b, string label) => b.GetComponentInChildren<Text>().text = label;

        public static Slider Slider(string label, Transform parent, Vector2 pos, float value, Action<float> onChange)
        {
            var row = UiKit.Rect("Slider " + label, parent, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), pos, new Vector2(220, 14));
            UiKit.Label("Label", row, new Vector2(0, 0.5f), new Vector2(0, 0.5f), Vector2.zero, new Vector2(80, 10), label, 8, TextAnchor.MiddleLeft, Text);
            var back = UiKit.Panel("Track", row, new Vector2(1, 0.5f), new Vector2(1, 0.5f), new Vector2(-24, 0), new Vector2(110, 6), Color.white, UiSprites.Get("ui_bar_back"));
            var fillArea = UiKit.Rect("Fill area", back.transform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(110, 6));
            var fill = UiKit.Panel("Fill", fillArea, new Vector2(0, 0.5f), new Vector2(0, 0.5f), Vector2.zero, new Vector2(0, 6), Color.white, UiSprites.Get("ui_bar_red"));
            fill.rectTransform.anchorMin = new Vector2(0, 0);
            fill.rectTransform.anchorMax = new Vector2(0, 1);
            var handle = UiKit.Panel("Handle", back.transform, new Vector2(0, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(6, 10), Gold);
            handle.raycastTarget = true;
            var slider = back.gameObject.AddComponent<Slider>();
            back.raycastTarget = true;
            slider.fillRect = fill.rectTransform;
            slider.handleRect = handle.rectTransform;
            slider.targetGraphic = handle;
            slider.minValue = 0f;
            slider.maxValue = 1f;
            slider.value = value;
            var pct = UiKit.Label("Value", row, new Vector2(1, 0.5f), new Vector2(1, 0.5f), new Vector2(6, 0), new Vector2(30, 10), Mathf.RoundToInt(value * 100) + "%", 8,
                TextAnchor.MiddleRight, Dim);
            slider.onValueChanged.AddListener(v =>
            {
                pct.text = Mathf.RoundToInt(v * 100) + "%";
                onChange?.Invoke(v);
            });
            return slider;
        }

        public static InputField Field(Transform parent, Vector2 pos, Vector2 size, string value, int maxChars)
        {
            var img = UiKit.Panel("Field", parent, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), pos, size, Color.white, UiSprites.Get("ui_slot"));
            img.raycastTarget = true;
            var text = UiKit.Label("Text", img.transform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, size - new Vector2(10, 2), value, 8,
                TextAnchor.MiddleLeft, Text);
            text.supportRichText = false;
            var field = img.gameObject.AddComponent<InputField>();
            field.textComponent = text;
            field.characterLimit = maxChars;
            field.text = value;
            field.caretWidth = 1;
            field.customCaretColor = true;
            field.caretColor = Gold;
            return field;
        }

        public static Text Title(string text, Transform parent, Vector2 anchor, Vector2 pos, int size = 16) =>
            UiKit.Label("Title", parent, anchor, new Vector2(0.5f, 0.5f), pos, new Vector2(400, size + 4), text, size, TextAnchor.MiddleCenter, Gold);
    }
}
