using System;
using System.Collections.Generic;
using Loamlight.Core;
using Loamlight.World.Collision;
using UnityEngine;

namespace Loamlight.Combat
{
    /// <summary>How a kind of projectile flies and what it does on impact.</summary>
    public sealed class ProjectileSpec
    {
        public string id;
        public Sprite sprite;
        public float gravity;          // tiles/s^2 (arrows drop, fireballs don't)
        public float life = 3f;
        public float radius = 0.25f;   // hit radius against bodies
        public float explodeRadius;    // > 0: area damage on impact
        public int pierce;             // extra targets it passes through
        public bool rotateToVelocity = true;
        public Color glow;             // a light follows it when alpha > 0
        public string impactSound = "hit";
    }

    /// <summary>
    /// Pooled projectiles on gameplay time. They hit solid tiles (stop or explode) and the first body of the opposing
    /// team (or several, for piercing ones). Specs are registered by name; a light can be attached by the host.
    /// </summary>
    public sealed class ProjectileSystem : MonoBehaviour
    {
        sealed class Shot
        {
            public ProjectileSpec spec;
            public Vector2 pos, vel;
            public int damage, pierceLeft;
            public float knockback, age;
            public Team team;
            public SpriteRenderer sr;
            public Component light;
            public readonly HashSet<IDamageable> hit = new HashSet<IDamageable>();
        }

        public ICollisionGrid Grid { get; set; }
        /// <summary>Optional: create a light that follows a glowing projectile (returns a component to move).</summary>
        public Func<Color, Component> MakeLight { get; set; }
        /// <summary>Impact feedback: (spec, point, exploded).</summary>
        public event Action<ProjectileSpec, Vector2, bool> Impact;

        readonly Dictionary<string, ProjectileSpec> _specs = new Dictionary<string, ProjectileSpec>();
        readonly List<Shot> _live = new List<Shot>();
        readonly Stack<Shot> _pool = new Stack<Shot>();
        readonly List<IDamageable> _query = new List<IDamageable>();

        public int LiveCount => _live.Count;
        /// <summary>Hits that landed on the enemy team (scenario diagnostics).</summary>
        public int PlayerHits { get; private set; }

        public void Register(ProjectileSpec spec) => _specs[spec.id] = spec;
        public bool Has(string id) => _specs.ContainsKey(id);

        public void Fire(string id, Vector2 pos, Vector2 vel, int damage, float knockback, Team team)
        {
            if (!_specs.TryGetValue(id ?? "", out var spec)) { Debug.LogWarning($"[Projectiles] unknown projectile '{id}'"); return; }
            var s = _pool.Count > 0 ? _pool.Pop() : NewShot();
            s.spec = spec;
            s.pos = pos;
            s.vel = vel;
            s.damage = damage;
            s.knockback = knockback;
            s.team = team;
            s.age = 0f;
            s.pierceLeft = spec.pierce;
            s.hit.Clear();
            s.sr.sprite = spec.sprite;
            s.sr.enabled = true;
            s.sr.transform.position = pos;
            if (spec.glow.a > 0f && MakeLight != null)
            {
                if (s.light == null) s.light = MakeLight(spec.glow);
                if (s.light != null) { s.light.gameObject.SetActive(true); s.light.transform.position = pos; }
            }
            _live.Add(s);
        }

        Shot NewShot()
        {
            var go = new GameObject("Projectile");
            go.transform.SetParent(transform, false);
            var sr = go.AddComponent<SpriteRenderer>();
            sr.sortingLayerName = "Entities";
            sr.sortingOrder = 12;
            return new Shot { sr = sr };
        }

        void Update() => Step(GameClock.Delta);

        /// <summary>Advance every live projectile by dt (public for tests).</summary>
        public void Step(float dt)
        {
            if (dt <= 0f) return;
            for (int i = _live.Count - 1; i >= 0; i--)
            {
                var s = _live[i];
                s.age += dt;
                s.vel.y -= s.spec.gravity * dt;
                bool done = s.age >= s.spec.life;
                // sub-step so fast shots never skip a tile or a body
                float dist = s.vel.magnitude * dt;
                int steps = Mathf.Max(1, Mathf.CeilToInt(dist / 0.3f));
                var step = s.vel * dt / steps;
                for (int k = 0; k < steps && !done; k++)
                {
                    s.pos += step;
                    if (Grid != null && Grid.Cell(Mathf.FloorToInt(s.pos.x), Mathf.FloorToInt(s.pos.y), out _) == CellCollision.Solid)
                    {
                        Explode(s);
                        done = true;
                        break;
                    }
                    var area = new Rect(s.pos.x - s.spec.radius, s.pos.y - s.spec.radius, s.spec.radius * 2f, s.spec.radius * 2f);
                    CombatRegistry.Query(area, s.team == Team.Player ? Team.Enemy : Team.Player, _query);
                    foreach (var t in _query)
                    {
                        if (!s.hit.Add(t)) continue;
                        if (s.spec.explodeRadius > 0f) { Explode(s); done = true; break; }
                        Hit(s, t);
                        if (s.pierceLeft-- <= 0) { done = true; Impact?.Invoke(s.spec, s.pos, false); break; }
                    }
                }
                if (done)
                {
                    Recycle(i);
                    continue;
                }
                s.sr.transform.position = s.pos;
                if (s.spec.rotateToVelocity) s.sr.transform.rotation = Quaternion.Euler(0f, 0f, Mathf.Atan2(s.vel.y, s.vel.x) * Mathf.Rad2Deg - 45f);
                if (s.light != null) s.light.transform.position = s.pos;
            }
        }

        void Hit(Shot s, IDamageable t)
        {
            var dir = s.vel.sqrMagnitude > 0.01f ? s.vel.normalized : Vector2.right;
            var hit = new DamageInfo
            {
                amount = HealthState.ComputeDamage(s.damage, 0, UnityEngine.Random.value), point = s.pos, source = s.team, cause = "projectile",
                knockback = new Vector2(Mathf.Sign(dir.x) * s.knockback * 1.4f, 2f + s.knockback * 0.5f),
            };
            if (t.ApplyDamage(hit))
            {
                if (s.team == Team.Player) PlayerHits++;
                Sfx.Play(s.spec.impactSound, s.pos, 0.6f);
                if (s.team == Team.Player) GameClock.Hitstop(0.035f);
            }
        }

        void Explode(Shot s)
        {
            if (s.spec.explodeRadius > 0f)
            {
                float r = s.spec.explodeRadius;
                CombatRegistry.Query(new Rect(s.pos.x - r, s.pos.y - r, r * 2f, r * 2f), s.team == Team.Player ? Team.Enemy : Team.Player, _query);
                foreach (var t in _query)
                {
                    if ((t.Center - s.pos).sqrMagnitude > r * r * 1.5f) continue;
                    Hit(s, t);
                }
                Sfx.Play("boom", s.pos, 0.7f);
                CombatEvents.RaiseShake(0.35f);
                Impact?.Invoke(s.spec, s.pos, true);
            }
            else
            {
                Sfx.Play("dig", s.pos, 0.3f);
                Impact?.Invoke(s.spec, s.pos, false);
            }
        }

        void Recycle(int index)
        {
            var s = _live[index];
            _live.RemoveAt(index);
            s.sr.enabled = false;
            if (s.light != null) s.light.gameObject.SetActive(false);
            _pool.Push(s);
        }

        public void ClearAll()
        {
            for (int i = _live.Count - 1; i >= 0; i--) Recycle(i);
        }
    }
}
