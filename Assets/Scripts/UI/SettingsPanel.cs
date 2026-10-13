using System;
using System.Collections.Generic;
using System.Linq;
using Loamlight.Core;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Utilities;
using UnityEngine.UI;

namespace Loamlight.UI
{
    /// <summary>
    /// Volume sliders, resolution and fullscreen, hints on/off and key rebinding (press a button, then the new key;
    /// Escape cancels). Changes apply at once and are saved to settings.json when the panel closes.
    /// </summary>
    public sealed class SettingsPanel : MonoBehaviour
    {
        RectTransform _root;
        Button _resolution, _fullscreen, _hints;
        readonly Dictionary<string, Button> _keyButtons = new Dictionary<string, Button>();
        Resolution[] _modes = Array.Empty<Resolution>();
        int _mode;
        string _listening;
        IDisposable _listener;

        public bool IsOpen => _root != null && _root.gameObject.activeSelf;
        public event Action Closed;

        public static SettingsPanel Create(Transform canvas)
        {
            var p = canvas.gameObject.AddComponent<SettingsPanel>();
            p.Build(canvas);
            p._root.gameObject.SetActive(false);
            return p;
        }

        void Build(Transform canvas)
        {
            GameSettings.Load();
            var panel = MenuKit.Panel("Settings", canvas, new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(280, 270));
            panel.raycastTarget = true;
            _root = panel.rectTransform;
            MenuKit.Title("Settings", _root, new Vector2(0.5f, 1f), new Vector2(0, -14), 8);
            MenuKit.Slider("Master", _root, new Vector2(0, 98), GameSettings.Master, v => { GameSettings.Master = v; GameSettings.Apply(false); });
            MenuKit.Slider("Music", _root, new Vector2(0, 82), GameSettings.Music, v => { GameSettings.Music = v; GameSettings.Apply(false); });
            MenuKit.Slider("Effects", _root, new Vector2(0, 66), GameSettings.Sfx, v => { GameSettings.Sfx = v; GameSettings.Apply(false); });

            _modes = Screen.resolutions.Where(r => r.width >= 960).GroupBy(r => (r.width, r.height)).Select(g => g.First()).ToArray();
            _mode = Mathf.Max(0, Array.FindIndex(_modes, r => r.width == Screen.width && r.height == Screen.height));
            _resolution = MenuKit.Button("", _root, new Vector2(0.5f, 0.5f), new Vector2(-62, 44), new Vector2(116, 16), CycleResolution);
            _fullscreen = MenuKit.Button("", _root, new Vector2(0.5f, 0.5f), new Vector2(62, 44), new Vector2(116, 16), () =>
            {
                GameSettings.Fullscreen = !GameSettings.Fullscreen;
                ApplyWindow();
            });
            _hints = MenuKit.Button("", _root, new Vector2(0.5f, 0.5f), new Vector2(0, 24), new Vector2(240, 16), () =>
            {
                GameSettings.Hints = !GameSettings.Hints;
                Refresh();
            });

            UiKit.Label("Keys", _root, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0, 6), new Vector2(240, 10), "Keys (click, then press a key)", 8,
                TextAnchor.MiddleCenter, MenuKit.Dim);
            int i = 0;
            foreach (var (action, label, _) in GameSettings.Rebindable)
            {
                float y = -12 - i * 18;
                UiKit.Label(label, _root, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(-60, y), new Vector2(110, 10), label, 8, TextAnchor.MiddleLeft, MenuKit.Text);
                var a = action;
                _keyButtons[action] = MenuKit.Button("", _root, new Vector2(0.5f, 0.5f), new Vector2(70, y), new Vector2(90, 16), () => Listen(a));
                i++;
            }
            MenuKit.Button("Reset keys", _root, new Vector2(0.5f, 0f), new Vector2(-60, 16), new Vector2(100, 16), () =>
            {
                GameSettings.Keys.Clear();
                Refresh();
            });
            MenuKit.Button("Done", _root, new Vector2(0.5f, 0f), new Vector2(60, 16), new Vector2(100, 16), Close);
            Refresh();
        }

        void CycleResolution()
        {
            if (_modes.Length == 0) return;
            _mode = (_mode + 1) % _modes.Length;
            GameSettings.Width = _modes[_mode].width;
            GameSettings.Height = _modes[_mode].height;
            ApplyWindow();
        }

        void ApplyWindow()
        {
            GameSettings.Apply(true);
            Refresh();
        }

        void Listen(string action)
        {
            _listener?.Dispose();
            _listening = action;
            Refresh();
            _listener = InputSystem.onAnyButtonPress.CallOnce(control =>
            {
                if (_listening == null) return;
                if (control.device is Keyboard && control.name != "escape") GameSettings.Keys[_listening] = control.path.Replace("/Keyboard/", "<Keyboard>/");
                _listening = null;
                Refresh();
            });
        }

        void Refresh()
        {
            int w = GameSettings.Width > 0 ? GameSettings.Width : Screen.width, h = GameSettings.Height > 0 ? GameSettings.Height : Screen.height;
            MenuKit.SetLabel(_resolution, $"{w} x {h}");
            MenuKit.SetLabel(_fullscreen, GameSettings.Fullscreen ? "Fullscreen" : "Windowed");
            MenuKit.SetLabel(_hints, GameSettings.Hints ? "Tutorial hints: on" : "Tutorial hints: off");
            foreach (var kv in _keyButtons)
                MenuKit.SetLabel(kv.Value, _listening == kv.Key ? "press a key..." : GameSettings.KeyLabel(GameSettings.Key(kv.Key)));
        }

        public void Open()
        {
            Refresh();
            _root.gameObject.SetActive(true);
            _root.SetAsLastSibling();
            Sfx.PlayUi("ui_open", 0.5f);
        }

        public void Close()
        {
            if (!IsOpen) return;
            _listener?.Dispose();
            _listening = null;
            _root.gameObject.SetActive(false);
            GameSettings.Save();
            Sfx.PlayUi("ui_close", 0.5f);
            Closed?.Invoke();
        }

        void OnDestroy() => _listener?.Dispose();
    }
}
