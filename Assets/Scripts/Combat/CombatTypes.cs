using System;
using System.Collections.Generic;
using UnityEngine;

namespace Loamlight.Combat
{
    public enum Team : byte { Player = 0, Enemy = 1 }

    /// <summary>One hit: already-final damage plus where it came from and how hard it shoves.</summary>
    public struct DamageInfo
    {
        public int amount;
        public Vector2 knockback;     // velocity impulse in tiles/s (before resistance)
        public Vector2 point;         // where the hit landed (sparks, numbers)
        public Team source;
        public bool crit;
        public string cause;          // "melee", "projectile", "contact", "fall", "lava", "drown"
    }

    /// <summary>Pure health bookkeeping: damage after defence, invulnerability frames, regeneration. EditMode-tested.</summary>
    [Serializable]
    public struct HealthState
    {
        public int max;
        public int current;
        public float invulnerable;    // seconds left of i-frames
        public float sinceHit;        // seconds since the last damage (regeneration delay)

        public bool Alive => current > 0;
        public float Fraction => max > 0 ? (float)current / max : 0f;

        public static HealthState Full(int max) => new HealthState { max = max, current = max, sinceHit = 99f };

        /// <summary>Final damage: base minus half the defence, never below 1, with a ±15% roll (roll in 0..1).</summary>
        public static int ComputeDamage(int baseDamage, int defense, float roll, bool crit = false)
        {
            float d = baseDamage * (0.85f + 0.3f * Mathf.Clamp01(roll)) * (crit ? 2f : 1f) - defense * 0.5f;
            return Mathf.Max(1, Mathf.RoundToInt(d));
        }

        /// <summary>Apply a hit unless invulnerable. Returns the damage actually taken (0 if blocked by i-frames).</summary>
        public int Apply(int amount, float iframes)
        {
            if (!Alive || invulnerable > 0f || amount <= 0) return 0;
            int taken = Mathf.Min(current, amount);
            current -= taken;
            invulnerable = iframes;
            sinceHit = 0f;
            return taken;
        }

        public int Heal(int amount)
        {
            if (!Alive) return 0;
            int healed = Mathf.Min(max - current, Mathf.Max(0, amount));
            current += healed;
            return healed;
        }

        /// <summary>Advance timers; after <paramref name="regenDelay"/> seconds unhurt, regain <paramref name="regenPerSecond"/>.</summary>
        public void Tick(float dt, float regenDelay, float regenPerSecond, ref float regenAccumulator)
        {
            invulnerable = Mathf.Max(0f, invulnerable - dt);
            sinceHit += dt;
            if (!Alive || sinceHit < regenDelay || current >= max || regenPerSecond <= 0f) return;
            regenAccumulator += regenPerSecond * dt;
            int whole = (int)regenAccumulator;
            if (whole > 0)
            {
                regenAccumulator -= whole;
                current = Mathf.Min(max, current + whole);
            }
        }
    }

    /// <summary>Anything that can be hit: the player, enemies, the boss.</summary>
    public interface IDamageable
    {
        Team Team { get; }
        bool Alive { get; }
        Vector2 Center { get; }
        /// <summary>World-space hurtbox (tile units).</summary>
        Rect Hurtbox { get; }
        /// <summary>Returns true if the hit landed (not blocked by i-frames, not already dead).</summary>
        bool ApplyDamage(DamageInfo hit);
    }

    /// <summary>
    /// Every live <see cref="IDamageable"/>, so attackers find targets without knowing what they are: enemies find the
    /// player through here (AI never references the Player assembly), weapons find enemies.
    /// </summary>
    public static class CombatRegistry
    {
        static readonly List<IDamageable> All = new List<IDamageable>();

        public static IReadOnlyList<IDamageable> Targets => All;

        public static void Register(IDamageable d)
        {
            if (!All.Contains(d)) All.Add(d);
        }

        public static void Unregister(IDamageable d) => All.Remove(d);

        public static void Clear() => All.Clear();

        /// <summary>Live targets of <paramref name="team"/> whose hurtbox overlaps <paramref name="area"/>.</summary>
        public static void Query(Rect area, Team team, List<IDamageable> results)
        {
            results.Clear();
            for (int i = 0; i < All.Count; i++)
            {
                var d = All[i];
                if (d.Team == team && d.Alive && d.Hurtbox.Overlaps(area)) results.Add(d);
            }
        }

        public static IDamageable Nearest(Team team, Vector2 from, float maxDistance)
        {
            IDamageable best = null;
            float bestD = maxDistance * maxDistance;
            for (int i = 0; i < All.Count; i++)
            {
                var d = All[i];
                if (d.Team != team || !d.Alive) continue;
                float dist = (d.Center - from).sqrMagnitude;
                if (dist <= bestD) { bestD = dist; best = d; }
            }
            return best;
        }

        public static int Count(Team team)
        {
            int n = 0;
            for (int i = 0; i < All.Count; i++) if (All[i].Team == team && All[i].Alive) n++;
            return n;
        }
    }

    /// <summary>Feedback hooks: Game turns these into camera shake, sparks, numbers and sounds. Combat stays UI-free.</summary>
    public static class CombatEvents
    {
        public static event Action<IDamageable, DamageInfo> Damaged;
        public static event Action<IDamageable, DamageInfo> Killed;
        /// <summary>Screen shake request: trauma 0..1.</summary>
        public static event Action<float> Shake;

        public static void RaiseDamaged(IDamageable target, DamageInfo hit) => Damaged?.Invoke(target, hit);
        public static void RaiseKilled(IDamageable target, DamageInfo hit) => Killed?.Invoke(target, hit);
        public static void RaiseShake(float trauma) => Shake?.Invoke(trauma);

        public static void ClearListeners()
        {
            Damaged = null;
            Killed = null;
            Shake = null;
        }
    }
}
