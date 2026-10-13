using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Loamlight.Core;
using Loamlight.Lighting;
using Loamlight.UI;
using Loamlight.World.Saving;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem.UI;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace Loamlight.Game
{
    /// <summary>
    /// The title screen: Play (choose a saved world or create one with a name, seed and size), Settings, Quit.
    /// The backdrop is the game's own sky running a slow day/night cycle.
    /// </summary>
    public sealed class MainMenu : MonoBehaviour
    {
        public const string SceneName = "MainMenu";

        [SerializeField] Camera cam;
        RectTransform _title, _worlds, _create;
        SettingsPanel _settings;
        RectTransform _list;
        InputField _name, _seed;
        Button _size;
        int _sizeIndex = 1;
        string _pendingDelete;
        Text _status;

        static readonly (string label, int w, int h)[] Sizes = { ("Small  2100 x 700", 2100, 700), ("Medium 4200 x 1200", 4200, 1200) };

        public static MainMenu Current { get; private set; }
        public bool Ready { get; private set; }
        public IReadOnlyList<string> WorldFiles => ListWorlds().Select(w => w.path).ToList();

        public void Configure(Camera camera) => cam = camera;

        void Awake() => Current = this;

        void Start()
        {
            GameSettings.Apply(true);
            GameClock.Reset();
            var cycle = new GameObject("Menu sky cycle").AddComponent<DayNightCycle>();
            cycle.dayLength = 240f;
            cycle.SetTime(0.56f);
            SkyBackdrop.Create(cam, cycle, cam.transform.position.y - 5f);
            Sfx.PlayMusic(1);

            if (FindAnyObjectByType<EventSystem>() == null)
            {
                var es = new GameObject("EventSystem");
                es.AddComponent<EventSystem>();
                es.AddComponent<InputSystemUIInputModule>();
            }
            var canvas = UiKit.CreateCanvas("Menu", 10).transform;
            BuildTitle(canvas);
            BuildWorlds(canvas);
            BuildCreate(canvas);
            _settings = SettingsPanel.Create(canvas);
            _settings.Closed += () => Show(_title);
            Show(_title);
            Ready = true;
        }

        void BuildTitle(Transform canvas)
        {
            _title = UiKit.Rect("Title screen", canvas, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(400, 300));
            MenuKit.Title("LOAMLIGHT", _title, new Vector2(0.5f, 0.5f), new Vector2(0, 92), 32);
            UiKit.Label("Tagline", _title, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0, 62), new Vector2(300, 10),
                "dig - build - light the dark", 8, TextAnchor.MiddleCenter, MenuKit.Text);
            MenuKit.Button("Play", _title, new Vector2(0.5f, 0.5f), new Vector2(0, 10), new Vector2(120, 18), () => { RefreshWorlds(); Show(_worlds); });
            MenuKit.Button("Settings", _title, new Vector2(0.5f, 0.5f), new Vector2(0, -14), new Vector2(120, 18), OpenSettings);
            MenuKit.Button("Quit", _title, new Vector2(0.5f, 0.5f), new Vector2(0, -38), new Vector2(120, 18), Application.Quit);
            UiKit.Label("Version", _title, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0, -120), new Vector2(300, 10),
                "Unity 6000.4 - every asset made from scripts in /Tools", 8, TextAnchor.MiddleCenter, MenuKit.Dim);
        }

        void BuildWorlds(Transform canvas)
        {
            var panel = MenuKit.Panel("Worlds", canvas, new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(300, 230));
            _worlds = panel.rectTransform;
            MenuKit.Title("Choose a world", _worlds, new Vector2(0.5f, 1f), new Vector2(0, -14), 8);
            _list = UiKit.Rect("List", _worlds, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0, -30), new Vector2(280, 150));
            _status = UiKit.Label("Status", _worlds, new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(0, 34), new Vector2(280, 10), "", 8,
                TextAnchor.MiddleCenter, MenuKit.Dim);
            MenuKit.Button("New world", _worlds, new Vector2(0.5f, 0f), new Vector2(-62, 16), new Vector2(110, 16), () =>
            {
                _name.text = $"World {ListWorlds().Count() + 1}";
                _seed.text = UnityEngine.Random.Range(1, 999999999).ToString();
                Show(_create);
            });
            MenuKit.Button("Back", _worlds, new Vector2(0.5f, 0f), new Vector2(62, 16), new Vector2(110, 16), () => Show(_title));
        }

        void BuildCreate(Transform canvas)
        {
            var panel = MenuKit.Panel("Create", canvas, new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(260, 160));
            _create = panel.rectTransform;
            MenuKit.Title("New world", _create, new Vector2(0.5f, 1f), new Vector2(0, -14), 8);
            UiKit.Label("Name label", _create, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(-80, 34), new Vector2(60, 10), "Name", 8, TextAnchor.MiddleLeft, MenuKit.Text);
            _name = MenuKit.Field(_create, new Vector2(30, 34), new Vector2(150, 16), "World", 24);
            UiKit.Label("Seed label", _create, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(-80, 12), new Vector2(60, 10), "Seed", 8, TextAnchor.MiddleLeft, MenuKit.Text);
            _seed = MenuKit.Field(_create, new Vector2(30, 12), new Vector2(150, 16), "1", 10);
            _seed.contentType = InputField.ContentType.IntegerNumber;
            _size = MenuKit.Button(Sizes[_sizeIndex].label, _create, new Vector2(0.5f, 0.5f), new Vector2(0, -12), new Vector2(200, 16), () =>
            {
                _sizeIndex = (_sizeIndex + 1) % Sizes.Length;
                MenuKit.SetLabel(_size, Sizes[_sizeIndex].label);
            });
            MenuKit.Button("Create", _create, new Vector2(0.5f, 0f), new Vector2(-56, 16), new Vector2(100, 16), () =>
            {
                uint.TryParse(_seed.text, out var seed);
                CreateWorld(string.IsNullOrWhiteSpace(_name.text) ? "World" : _name.text.Trim(), seed == 0 ? 1u : seed, _sizeIndex);
            });
            MenuKit.Button("Back", _create, new Vector2(0.5f, 0f), new Vector2(56, 16), new Vector2(100, 16), () => Show(_worlds));
        }

        void Show(RectTransform screen)
        {
            foreach (var s in new[] { _title, _worlds, _create })
                if (s != null) s.gameObject.SetActive(s == screen);
        }

        public void OpenSettings()
        {
            Show(null);
            _settings.Open();
        }

        public void CloseSettings() => _settings.Close();

        public void OpenWorlds()
        {
            RefreshWorlds();
            Show(_worlds);
        }

        IEnumerable<(string path, SaveMeta meta)> ListWorlds()
        {
            if (!Directory.Exists(GameSession.SaveFolder)) yield break;
            foreach (var path in Directory.GetFiles(GameSession.SaveFolder, "*" + WorldSave.Extension).OrderByDescending(File.GetLastWriteTimeUtc))
            {
                SaveMeta meta = null;
                try { meta = WorldSave.ReadMeta(path); }
                catch (Exception e) { Debug.LogWarning($"[Menu] unreadable save {path}: {e.Message}"); }
                if (meta != null) yield return (path, meta);
            }
        }

        void RefreshWorlds()
        {
            for (int i = _list.childCount - 1; i >= 0; i--) Destroy(_list.GetChild(i).gameObject);
            var worlds = ListWorlds().Take(7).ToList();
            _status.text = worlds.Count == 0 ? "No worlds yet. Make one!" : "";
            for (int i = 0; i < worlds.Count; i++)
            {
                var (path, meta) = worlds[i];
                var t = TimeSpan.FromSeconds(meta.playSeconds);
                string label = $"{meta.name}   {meta.width}x{meta.height}   {(int)t.TotalHours}h {t.Minutes:00}m";
                float y = -10 - i * 20;
                MenuKit.Button(label, _list, new Vector2(0.5f, 1f), new Vector2(-14, y), new Vector2(240, 18), () => LoadWorld(path));
                Button del = null;
                del = MenuKit.Button("x", _list, new Vector2(0.5f, 1f), new Vector2(126, y), new Vector2(20, 18), () =>
                {
                    if (_pendingDelete != path) { _pendingDelete = path; MenuKit.SetLabel(del, "?"); _status.text = $"Click ? again to delete '{meta.name}'"; return; }
                    File.Delete(path);
                    _pendingDelete = null;
                    RefreshWorlds();
                });
            }
        }

        public void CreateWorld(string name, uint seed, int sizeIndex)
        {
            GameSession.LoadPath = null;
            GameSession.WorldName = name;
            GameSession.Seed = seed;
            GameSession.Width = Sizes[Mathf.Clamp(sizeIndex, 0, Sizes.Length - 1)].w;
            GameSession.Height = Sizes[Mathf.Clamp(sizeIndex, 0, Sizes.Length - 1)].h;
            Sfx.PlayUi("craft", 0.6f);
            SceneManager.LoadScene("Game");
        }

        public void LoadWorld(string path)
        {
            GameSession.LoadPath = path;
            SceneManager.LoadScene("Game");
        }
    }
}
