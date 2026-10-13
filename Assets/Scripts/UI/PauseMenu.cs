using System;
using Loamlight.Core;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace Loamlight.UI
{
    /// <summary>Escape in game: Resume, Save, Settings, Save &amp; quit to the menu. Gameplay time stops while it is open.</summary>
    public sealed class PauseMenu : MonoBehaviour
    {
        RectTransform _root;
        Image _shade;
        SettingsPanel _settings;

        public Action Save { get; set; }
        public Action QuitToMenu { get; set; }
        /// <summary>Another screen (inventory) has Escape first.</summary>
        public Func<bool> Blocked { get; set; }
        public bool IsOpen => _root != null && _root.gameObject.activeSelf;
        public event Action<bool> OpenChanged;

        public static PauseMenu Create()
        {
            var canvas = UiKit.CreateCanvas("Pause", 40);
            var menu = canvas.gameObject.AddComponent<PauseMenu>();
            menu.Build(canvas.transform);
            return menu;
        }

        void Build(Transform canvas)
        {
            _shade = UiKit.Panel("Shade", canvas, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(4000, 4000), new Color(0f, 0f, 0.03f, 0.55f));
            _shade.raycastTarget = true;
            var panel = MenuKit.Panel("Pause", canvas, new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(160, 130));
            _root = panel.rectTransform;
            MenuKit.Title("Paused", _root, new Vector2(0.5f, 1f), new Vector2(0, -14), 8);
            MenuKit.Button("Resume", _root, new Vector2(0.5f, 0.5f), new Vector2(0, 28), new Vector2(120, 16), () => SetOpen(false));
            MenuKit.Button("Save", _root, new Vector2(0.5f, 0.5f), new Vector2(0, 8), new Vector2(120, 16), () => Save?.Invoke());
            MenuKit.Button("Settings", _root, new Vector2(0.5f, 0.5f), new Vector2(0, -12), new Vector2(120, 16), () => _settings.Open());
            MenuKit.Button("Save & quit", _root, new Vector2(0.5f, 0.5f), new Vector2(0, -32), new Vector2(120, 16), () => QuitToMenu?.Invoke());
            _settings = SettingsPanel.Create(canvas);
            SetOpen(false, silent: true);
        }

        void Update()
        {
            if (Keyboard.current == null || !Keyboard.current.escapeKey.wasPressedThisFrame) return;
            if (_settings.IsOpen) { _settings.Close(); return; }
            if (!IsOpen && Blocked != null && Blocked()) return;
            SetOpen(!IsOpen);
        }

        public void SetOpen(bool open, bool silent = false)
        {
            _root.gameObject.SetActive(open);
            _shade.enabled = open;
            if (!open && _settings != null) _settings.Close();
            GameClock.Paused = open;
            if (!silent)
            {
                Sfx.PlayUi(open ? "ui_open" : "ui_close", 0.5f);
                OpenChanged?.Invoke(open);
            }
        }
    }
}
