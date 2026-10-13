using System.Collections.Generic;
using Loamlight.Combat;
using Loamlight.Core;
using Loamlight.UI;
using UnityEngine;

namespace Loamlight.Game
{
    /// <summary>
    /// Turns combat events into feedback: floating damage numbers (pixel font, crits larger and gold), hit sparks,
    /// death puffs and impact bursts. Everything is pooled and runs on real time so it keeps moving during hitstop.
    /// </summary>
    public sealed class CombatFx : MonoBehaviour
    {
        sealed class Number
        {
            public TextMesh text;
            public MeshRenderer renderer;
            public Vector2 pos, vel;
            public float age, life;
        }

        sealed class Flash
        {
            public SpriteRenderer sr;
            public Sprite[] frames;
            public float age, fps;
            public Vector2 vel;
        }

        readonly List<Number> _numbers = new List<Number>();
        readonly List<Flash> _flashes = new List<Flash>();
        Sprite[] _spark, _puff, _slash;
        Font _font;

        public int NumbersShown { get; private set; }

        public static CombatFx Create()
        {
            var fx = new GameObject("Combat FX").AddComponent<CombatFx>();
            fx._spark = SpriteSheetIndex.Get("fx")?.Find("spark")?.frames;
            fx._puff = SpriteSheetIndex.Get("fx")?.Find("puff")?.frames;
            fx._slash = SpriteSheetIndex.Get("fx")?.Find("slash")?.frames;
            fx._font = UiKit.Font;
            CombatEvents.Damaged += fx.OnDamaged;
            CombatEvents.Killed += fx.OnKilled;
            return fx;
        }

        void OnDestroy()
        {
            CombatEvents.Damaged -= OnDamaged;
            CombatEvents.Killed -= OnKilled;
        }

        void OnDamaged(IDamageable target, DamageInfo hit)
        {
            bool toPlayer = target.Team == Team.Player;
            ShowNumber(target.Center + new Vector2(0f, 1f), hit.amount.ToString(), hit.crit ? new Color(1f, 0.85f, 0.3f) : toPlayer ? new Color(1f, 0.4f, 0.35f) : Color.white, hit.crit);
            if (hit.cause == "melee" || hit.cause == "projectile" || hit.cause == "contact") Burst(_spark, hit.point, 24f, Vector2.zero);
        }

        void OnKilled(IDamageable target, DamageInfo hit)
        {
            if (target.Team == Team.Enemy) Burst(_puff, target.Center, 14f, new Vector2(0f, 0.6f), 1.5f);
        }

        /// <summary>A short animated sprite (sparks, puffs, explosions) at a point.</summary>
        public void Burst(Sprite[] frames, Vector2 at, float fps, Vector2 drift, float scale = 1f)
        {
            if (frames == null || frames.Length == 0) return;
            Flash f = null;
            foreach (var x in _flashes) if (!x.sr.enabled) { f = x; break; }
            if (f == null)
            {
                var go = new GameObject("Fx");
                go.transform.SetParent(transform, false);
                var sr = go.AddComponent<SpriteRenderer>();
                sr.sortingLayerName = "Overlay";
                sr.sharedMaterial = Resources.Load<Material>("Materials/SpriteUnlit");
                f = new Flash { sr = sr };
                _flashes.Add(f);
            }
            f.frames = frames;
            f.fps = fps;
            f.age = 0f;
            f.vel = drift;
            f.sr.enabled = true;
            f.sr.sprite = frames[0];
            f.sr.transform.position = at;
            f.sr.transform.localScale = Vector3.one * scale;
        }

        public void Explosion(Vector2 at)
        {
            Burst(_puff, at, 16f, Vector2.zero, 2.5f);
            Burst(_spark, at, 18f, Vector2.zero, 2f);
        }

        public void Slash(Vector2 at, float angle)
        {
            Burst(_slash, at, 20f, Vector2.zero, 1.6f);
            if (_flashes.Count > 0) _flashes[_flashes.Count - 1].sr.transform.rotation = Quaternion.Euler(0, 0, angle - 45f);
        }

        void ShowNumber(Vector2 at, string text, Color color, bool big)
        {
            Number n = null;
            foreach (var x in _numbers) if (!x.renderer.enabled) { n = x; break; }
            if (n == null)
            {
                var go = new GameObject("Damage number");
                go.transform.SetParent(transform, false);
                var tm = go.AddComponent<TextMesh>();
                tm.font = _font;
                tm.fontSize = 0;   // bitmap font: any size override spams a warning per property set
                tm.anchor = TextAnchor.MiddleCenter;
                tm.alignment = TextAlignment.Center;
                var mr = go.GetComponent<MeshRenderer>();
                mr.sharedMaterial = _font != null ? _font.material : null;
                mr.sortingLayerName = "Overlay";
                mr.sortingOrder = 50;
                n = new Number { text = tm, renderer = mr };
                _numbers.Add(n);
            }
            n.text.text = text;
            n.text.color = color;
            // one font pixel = one world pixel (1/16 tile): characterSize scales the 8 px glyphs to 8 world pixels
            n.text.characterSize = big ? 0.19f : 0.125f;
            n.pos = at + new Vector2(Random.Range(-0.3f, 0.3f), 0f);
            n.vel = new Vector2(Random.Range(-0.8f, 0.8f), 5f);
            n.age = 0f;
            n.life = big ? 1.0f : 0.75f;
            n.renderer.enabled = true;
            NumbersShown++;
        }

        void Update()
        {
            float dt = Time.deltaTime;
            foreach (var n in _numbers)
            {
                if (!n.renderer.enabled) continue;
                n.age += dt;
                n.vel.y -= 9f * dt;
                n.pos += n.vel * dt;
                n.text.transform.position = new Vector3(Mathf.Round(n.pos.x * 16f) / 16f, Mathf.Round(n.pos.y * 16f) / 16f, 0f);
                var c = n.text.color;
                c.a = Mathf.Clamp01((n.life - n.age) / 0.25f);
                n.text.color = c;
                if (n.age >= n.life) n.renderer.enabled = false;
            }
            foreach (var f in _flashes)
            {
                if (!f.sr.enabled) continue;
                f.age += dt;
                int frame = (int)(f.age * f.fps);
                if (frame >= f.frames.Length) { f.sr.enabled = false; continue; }
                f.sr.sprite = f.frames[frame];
                f.sr.transform.position += (Vector3)(f.vel * dt);
            }
        }
    }
}
