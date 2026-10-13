using Loamlight.Player;
using UnityEngine;
using UnityEngine.UI;

namespace Loamlight.UI
{
    /// <summary>
    /// Top-right life hearts (20 life each, half hearts for 10), breath stars while underwater, a boss bar along the
    /// bottom, and the death overlay with its respawn countdown.
    /// </summary>
    public sealed class HealthHud : MonoBehaviour
    {
        const int PerRow = 10, Size = 11;

        PlayerHealth _health;
        Image[] _hearts;
        Image[] _breath;
        Sprite _full, _half, _empty, _starFull, _starEmpty;
        Text _lifeText, _deathText;
        Image _deathShade;
        RectTransform _bossRoot;
        Image _bossFill;
        Text _bossName;

        public float RespawnCountdown { get; set; }
        public bool ShowingDeath => _deathShade != null && _deathShade.enabled;

        public static HealthHud Create(Transform hudRoot, PlayerHealth health)
        {
            var hud = hudRoot.gameObject.AddComponent<HealthHud>();
            hud._health = health;
            hud.Build(hudRoot);
            return hud;
        }

        void Build(Transform root)
        {
            _full = UiSprites.Get("ui_heart_full");
            _half = UiSprites.Get("ui_heart_half");
            _empty = UiSprites.Get("ui_heart_empty");
            _starFull = UiSprites.Get("ui_star_full");
            _starEmpty = UiSprites.Get("ui_star_empty");
            _hearts = new Image[20];
            for (int i = 0; i < _hearts.Length; i++)
            {
                int row = i / PerRow, col = i % PerRow;
                _hearts[i] = UiKit.Panel($"Heart {i}", root, new Vector2(1, 1), new Vector2(1, 1),
                    new Vector2(-6 - (PerRow - 1 - col) * (Size + 1), -16 - row * (Size + 1)), new Vector2(Size, Size), Color.white, _full);
            }
            _lifeText = UiKit.Label("Life", root, new Vector2(1, 1), new Vector2(1, 1), new Vector2(-6, -4), new Vector2(120, 10), "", 8, TextAnchor.MiddleRight);
            _breath = new Image[10];
            for (int i = 0; i < _breath.Length; i++)
                _breath[i] = UiKit.Panel($"Breath {i}", root, new Vector2(1, 1), new Vector2(1, 1),
                    new Vector2(-6 - (9 - i) * (Size + 1), -16 - 2 * (Size + 1) - 2), new Vector2(Size, Size), Color.white, _starFull);

            _deathShade = UiKit.Panel("Death shade", root, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(4000, 4000),
                new Color(0.25f, 0f, 0f, 0.45f));
            _deathText = UiKit.Label("Death", root, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0, 10), new Vector2(300, 30), "", 16,
                TextAnchor.MiddleCenter, new Color(1f, 0.55f, 0.5f));
            _deathShade.enabled = false;
            _deathText.enabled = false;

            _bossRoot = UiKit.Rect("Boss bar", root, new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(0, 14), new Vector2(240, 16));
            UiKit.Panel("Back", _bossRoot, new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), Vector2.zero, new Vector2(240, 8), Color.white, UiSprites.Get("ui_bar_back"));
            _bossFill = UiKit.Panel("Fill", _bossRoot, new Vector2(0f, 0f), new Vector2(0f, 0f), new Vector2(0, 0), new Vector2(240, 8), Color.white, UiSprites.Get("ui_bar_red"));
            _bossName = UiKit.Label("Name", _bossRoot, new Vector2(0.5f, 1f), new Vector2(0.5f, 0f), new Vector2(0, -6), new Vector2(240, 10), "", 8, TextAnchor.MiddleCenter);
            _bossRoot.gameObject.SetActive(false);
        }

        /// <summary>Show a boss bar (fraction 0..1), or hide it with a null name.</summary>
        public void SetBoss(string name, float fraction)
        {
            bool show = !string.IsNullOrEmpty(name);
            if (_bossRoot.gameObject.activeSelf != show) _bossRoot.gameObject.SetActive(show);
            if (!show) return;
            _bossName.text = name;
            _bossFill.rectTransform.sizeDelta = new Vector2(Mathf.Max(0f, 240f * Mathf.Clamp01(fraction)), 8f);
        }

        void LateUpdate()
        {
            if (_health == null) return;
            int life = _health.Life, max = _health.MaxLife;
            int hearts = Mathf.CeilToInt(max / 20f);
            for (int i = 0; i < _hearts.Length; i++)
            {
                bool show = i < hearts;
                _hearts[i].enabled = show;
                if (!show) continue;
                int lo = i * 20;
                _hearts[i].sprite = life >= lo + 20 ? _full : life >= lo + 10 ? _half : _empty;
            }
            _lifeText.text = $"Life {life}/{max}";
            bool under = _health.Underwater || _health.Breath01 < 0.999f;
            int bubbles = Mathf.CeilToInt(_health.Breath01 * _breath.Length);
            for (int i = 0; i < _breath.Length; i++)
            {
                _breath[i].enabled = under;
                _breath[i].sprite = i < bubbles ? _starFull : _starEmpty;
            }
            bool dead = !_health.Alive;
            _deathShade.enabled = dead;
            _deathText.enabled = dead;
            if (dead) _deathText.text = RespawnCountdown > 0f ? $"You were slain\n{Mathf.CeilToInt(RespawnCountdown)}" : "You were slain";
        }
    }
}
