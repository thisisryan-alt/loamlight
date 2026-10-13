using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace Loamlight.Core
{
    /// <summary>
    /// Player settings, stored as JSON in persistentDataPath/settings.json: volumes, resolution and fullscreen,
    /// hints on/off, and key overrides (action name → input control path, e.g. "Jump" → "&lt;Keyboard&gt;/w").
    /// </summary>
    public static class GameSettings
    {
        public static float Master = 1f, Music = 0.55f, Sfx = 0.8f;
        public static int Width, Height;
        public static bool Fullscreen;
        public static bool Hints = true;
        public static readonly Dictionary<string, string> Keys = new Dictionary<string, string>();

        /// <summary>The rebindable keyboard actions, with their defaults.</summary>
        public static readonly (string action, string label, string path)[] Rebindable =
        {
            ("MoveLeft", "Move left", "<Keyboard>/a"),
            ("MoveRight", "Move right", "<Keyboard>/d"),
            ("Jump", "Jump", "<Keyboard>/space"),
            ("Down", "Drop down", "<Keyboard>/s"),
            ("Inventory", "Inventory", "<Keyboard>/e"),
        };

        public static event Action Changed;
        static bool _loaded;

        static string FilePath => Path.Combine(Application.persistentDataPath, "settings.json");

        public static string Key(string action)
        {
            Load();
            if (Keys.TryGetValue(action, out var p) && !string.IsNullOrEmpty(p)) return p;
            foreach (var r in Rebindable) if (r.action == action) return r.path;
            return null;
        }

        /// <summary>"&lt;Keyboard&gt;/space" → "SPACE".</summary>
        public static string KeyLabel(string path)
        {
            if (string.IsNullOrEmpty(path)) return "-";
            int slash = path.LastIndexOf('/');
            return (slash >= 0 ? path.Substring(slash + 1) : path).ToUpperInvariant();
        }

        public static void Load()
        {
            if (_loaded) return;
            _loaded = true;
            try
            {
                if (!File.Exists(FilePath)) return;
                if (!(MiniJson.Parse(File.ReadAllText(FilePath)) is Dictionary<string, object> root)) return;
                float F(string k, float d) => root.TryGetValue(k, out var v) ? Convert.ToSingle(v) : d;
                Master = Mathf.Clamp01(F("master", Master));
                Music = Mathf.Clamp01(F("music", Music));
                Sfx = Mathf.Clamp01(F("sfx", Sfx));
                Width = (int)F("width", 0);
                Height = (int)F("height", 0);
                Fullscreen = root.TryGetValue("fullscreen", out var fs) && fs is bool b && b;
                Hints = !root.TryGetValue("hints", out var h) || !(h is bool hb) || hb;
                if (root.TryGetValue("keys", out var k) && k is Dictionary<string, object> keys)
                    foreach (var kv in keys) Keys[kv.Key] = kv.Value as string;
            }
            catch (Exception e)
            {
                Debug.LogWarning("[Settings] could not read settings.json: " + e.Message);
            }
        }

        public static void Save()
        {
            var keys = new Dictionary<string, object>();
            foreach (var kv in Keys) keys[kv.Key] = kv.Value;
            var root = new Dictionary<string, object>
            {
                ["master"] = (double)Master, ["music"] = (double)Music, ["sfx"] = (double)Sfx, ["width"] = (double)Width, ["height"] = (double)Height,
                ["fullscreen"] = Fullscreen, ["hints"] = Hints, ["keys"] = keys,
            };
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                File.WriteAllText(FilePath, MiniJson.Serialize(root));
            }
            catch (Exception e)
            {
                Debug.LogWarning("[Settings] could not write settings.json: " + e.Message);
            }
            Changed?.Invoke();
        }

        /// <summary>Push volumes to the audio host and, if chosen, the window size.</summary>
        public static void Apply(bool resolution)
        {
            Load();
            Loamlight.Core.Sfx.MasterVolume = Master;
            Loamlight.Core.Sfx.MusicVolume = Music;
            Loamlight.Core.Sfx.SfxVolume = Sfx;
            Loamlight.Core.Sfx.RefreshMusicVolume();
            if (resolution && Width > 0 && Height > 0 && !Application.isEditor && !LaunchArgs.Has("-llCapture"))
                Screen.SetResolution(Width, Height, Fullscreen ? FullScreenMode.FullScreenWindow : FullScreenMode.Windowed);
        }
    }
}
