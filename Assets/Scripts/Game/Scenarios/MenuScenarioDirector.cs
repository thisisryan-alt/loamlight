using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Loamlight.Core;
using UnityEngine;

namespace Loamlight.Game.Scenarios
{
    /// <summary>
    /// Lives in the MainMenu scene; with <c>-llCapture … -llScene MainMenu -llScenario m6</c> it drives the whole polish
    /// loop: title, settings (volume, a rebind, saved to disk), world creation, the tutorial in a fresh world, the pause
    /// menu, saving, quitting to the menu, the world showing in the list, and loading it back. It survives the scene
    /// changes by marking itself DontDestroyOnLoad.
    /// </summary>
    public sealed class MenuScenarioDirector : MonoBehaviour, ICaptureDirector, ICaptureVerifier
    {
        readonly List<string> _log = new List<string>();
        bool _failed;
        const string WorldName = "Scenario World";

        void Check(bool ok, string what)
        {
            _log.Add((ok ? "ok: " : "FAIL: ") + what);
            if (!ok) _failed = true;
            Debug.Log($"[Scenario] {(ok ? "ok" : "FAIL")} {what}");
        }

        public bool VerifyShot(string shotName, Texture2D frame, out string detail)
        {
            detail = _log.Count == 0 ? "(no checks)" : string.Join(" | ", _log);
            _log.Clear();
            bool ok = !_failed;
            _failed = false;
            return ok;
        }

        static IEnumerator Seconds(float s)
        {
            float end = Time.realtimeSinceStartup + s;
            while (Time.realtimeSinceStartup < end) yield return null;
        }

        public IEnumerator CaptureScript()
        {
            DontDestroyOnLoad(gameObject);
            if (LaunchArgs.Get("-llScenario", "m6") != "m6") { Debug.LogError($"[LL-ERROR] MainMenu only runs scenario m6"); yield break; }
            // start clean: no leftover test world from an earlier run
            var stale = GameSession.PathFor(WorldName);
            if (File.Exists(stale)) File.Delete(stale);

            while (MainMenu.Current == null || !MainMenu.Current.Ready) yield return null;
            yield return Seconds(1f);
            Check(Sfx.CurrentMusic >= 0, $"menu music playing (track {Sfx.CurrentMusic} of {Sfx.MusicTracks})");
            Check(Sfx.MusicTracks == 3, "three music tracks imported");
            yield return new CaptureShot("title");

            // ---------------------------------------------------------------- settings
            MainMenu.Current.OpenSettings();
            yield return Seconds(0.4f);
            float master = GameSettings.Master;
            GameSettings.Master = 0.7f;
            GameSettings.Keys["Jump"] = "<Keyboard>/w";
            GameSettings.Apply(false);
            Check(Mathf.Approximately(Sfx.MasterVolume, 0.7f), "master volume applies to the audio host");
            yield return new CaptureShot("settings");
            MainMenu.Current.CloseSettings();
            var file = Path.Combine(Application.persistentDataPath, "settings.json");
            Check(File.Exists(file) && File.ReadAllText(file).Contains("<Keyboard>/w"), "settings (with the rebind) were saved to settings.json");
            GameSettings.Keys.Remove("Jump");
            GameSettings.Master = master;
            GameSettings.Save();

            // ---------------------------------------------------------------- create a world
            MainMenu.Current.OpenWorlds();
            yield return Seconds(0.4f);
            yield return new CaptureShot("worlds");
            MainMenu.Current.CreateWorld(WorldName, 777, 0);
            float until = Time.realtimeSinceStartup + 30f;
            while ((GameBootstrap.Current == null || !GameBootstrap.Current.Ready) && Time.realtimeSinceStartup < until) yield return null;
            var g = GameBootstrap.Current;
            Check(g != null && g.Ready, "the new world loaded");
            if (g == null) yield break;
            g.SetPeaceful(true);
            Check(g.World.Width == 2100 && g.World.Height == 700 && g.World.Seed == 777, $"world is {g.World.Width}x{g.World.Height}, seed {g.World.Seed}");
            yield return Seconds(1.5f);
            Check(!string.IsNullOrEmpty(g.Hud.CurrentHint) && g.Hud.CurrentHint.Contains("Move"), $"tutorial hint: '{g.Hud.CurrentHint}'");
            yield return new CaptureShot("tutorial");
            g.Player.Teleport(g.Player.Feet + new Vector2(8f, 3f));
            yield return Seconds(3f);
            Check(g.Tutorial.Step >= 1, $"moving advanced the tutorial (step {g.Tutorial.Step})");
            Check(Sfx.CurrentMusic == MusicDirector.Day, "day music in the meadow");

            // ---------------------------------------------------------------- pause, save, quit
            g.Pause.SetOpen(true);
            yield return Seconds(0.3f);
            Check(GameClock.Paused, "the pause menu stops gameplay time");
            yield return new CaptureShot("pause");
            g.Pause.Save();
            Check(File.Exists(g.SavePath), $"saved to {Path.GetFileName(g.SavePath)}");
            g.Pause.SetOpen(false);
            Check(!GameClock.Paused, "resume restarts time");
            g.QuitToMenu();
            until = Time.realtimeSinceStartup + 15f;
            while ((MainMenu.Current == null || !MainMenu.Current.Ready || GameBootstrap.Current != null) && Time.realtimeSinceStartup < until) yield return null;
            Check(MainMenu.Current != null, "back at the main menu");
            MainMenu.Current.OpenWorlds();
            yield return Seconds(0.4f);
            var listed = MainMenu.Current.WorldFiles.FirstOrDefault(p => Path.GetFileNameWithoutExtension(p) == WorldName.Replace(' ', ' '));
            Check(listed != null, "the saved world is in the list");
            yield return new CaptureShot("worlds_after");

            // ---------------------------------------------------------------- load it back
            if (listed != null)
            {
                MainMenu.Current.LoadWorld(listed);
                until = Time.realtimeSinceStartup + 30f;
                while ((GameBootstrap.Current == null || !GameBootstrap.Current.Ready) && Time.realtimeSinceStartup < until) yield return null;
                g = GameBootstrap.Current;
                Check(g != null && g.Meta.name == WorldName, "the world loads back from the menu");
                if (g != null)
                {
                    g.SetPeaceful(true);
                    Check(g.Tutorial.Step >= 1, "tutorial progress was saved with the world");
                    yield return Seconds(1f);
                    yield return new CaptureShot("loaded");
                    File.Delete(g.SavePath);
                }
            }
        }
    }
}
