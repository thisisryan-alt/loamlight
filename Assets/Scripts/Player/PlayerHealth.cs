using System;
using Loamlight.Combat;
using Loamlight.Core;
using Loamlight.World;
using UnityEngine;

namespace Loamlight.Player
{
    /// <summary>
    /// The player's life: hits from enemies and projectiles (through <see cref="CombatRegistry"/>), fall damage,
    /// lava, drowning, i-frames with a flicker, slow regeneration, death and respawn.
    /// </summary>
    public sealed class PlayerHealth : MonoBehaviour, IDamageable
    {
        public const float IFrames = 0.67f, RegenDelay = 6f, RegenPerSecond = 2f;
        public const float BreathSeconds = 7f, SafeFall = 14f, FallDamagePerTile = 4f;

        public PlayerController Player { get; set; }
        public SpriteRenderer Renderer { get; set; }
        /// <summary>False in peaceful scenarios: nothing can hurt the player.</summary>
        public bool Vulnerable { get; set; } = true;

        HealthState _health = HealthState.Full(100);
        float _regen, _breath = BreathSeconds, _drownTick;

        public Team Team => Team.Player;
        public bool Alive => _health.Alive;
        public int Life => _health.current;
        public int MaxLife => _health.max;
        public float Breath01 => _breath / BreathSeconds;
        public bool Underwater { get; private set; }
        public HealthState State => _health;
        public Vector2 Center => Player != null ? Player.Center : (Vector2)transform.position;
        public Rect Hurtbox
        {
            get
            {
                var b = Player.Body;
                return new Rect(b.Left, b.Bottom, b.size.x, b.size.y);
            }
        }

        public event Action<DamageInfo> Hurt;
        public event Action<DamageInfo> Died;
        public event Action Respawned;

        void OnEnable() => CombatRegistry.Register(this);
        void OnDisable() => CombatRegistry.Unregister(this);

        public void Init(PlayerController player, SpriteRenderer renderer)
        {
            Player = player;
            Renderer = renderer;
            player.Stepped += OnStepped;
        }

        public bool ApplyDamage(DamageInfo hit)
        {
            if (!Vulnerable || !Alive) return false;
            int taken = _health.Apply(hit.amount, hit.cause == "drown" ? 0f : IFrames);
            if (taken <= 0) return false;
            hit.amount = taken;
            if (hit.knockback != Vector2.zero) Player.AddImpulse(hit.knockback, 0.3f);
            Sfx.Play("hurt", Center, 0.8f);
            CombatEvents.RaiseDamaged(this, hit);
            CombatEvents.RaiseShake(Mathf.Clamp01(0.25f + taken / 60f));
            Hurt?.Invoke(hit);
            if (!_health.Alive)
            {
                Player.Frozen = true;
                Sfx.Play("death", Center, 0.9f);
                CombatEvents.RaiseKilled(this, hit);
                Died?.Invoke(hit);
            }
            return true;
        }

        public int Heal(int amount)
        {
            int healed = _health.Heal(amount);
            if (healed > 0) Sfx.Play("heal", Center, 0.6f);
            return healed;
        }

        public void SetMaxLife(int max, bool fill)
        {
            _health.max = Mathf.Max(20, max);
            if (fill || _health.current > _health.max) _health.current = _health.max;
        }

        /// <summary>Restore from a save: life clamped to max, never loads dead.</summary>
        public void Restore(int life, int max)
        {
            _health.max = Mathf.Max(20, max);
            _health.current = Mathf.Clamp(life, 1, _health.max);
        }

        public void Respawn(Vector2 feet)
        {
            _health = HealthState.Full(_health.max);
            _health.invulnerable = 2f;
            _breath = BreathSeconds;
            Player.Teleport(feet);
            Player.Frozen = false;
            Respawned?.Invoke();
        }

        void OnStepped(MotorEvents ev)
        {
            if (!ev.landed || ev.fallDistance <= SafeFall || Player.Submerged > 0.2f) return;
            int dmg = Mathf.RoundToInt((ev.fallDistance - SafeFall) * FallDamagePerTile);
            ApplyDamage(new DamageInfo { amount = dmg, point = Player.Feet, source = Team.Enemy, cause = "fall" });
        }

        void Update()
        {
            if (Player == null) return;
            float dt = GameClock.Delta;
            _health.Tick(dt, RegenDelay, RegenPerSecond, ref _regen);

            // lava burns on contact; the head under water uses up breath, then drowns
            if (Alive && Player.SubmergedIn == LiquidType.Lava && Player.Submerged > 0.05f)
                ApplyDamage(new DamageInfo { amount = 25, knockback = new Vector2(0f, 12f), point = Player.Center, source = Team.Enemy, cause = "lava" });
            Underwater = Player.Submerged > 0.85f && Player.SubmergedIn == LiquidType.Water;
            if (Underwater) _breath = Mathf.Max(0f, _breath - dt);
            else _breath = Mathf.Min(BreathSeconds, _breath + dt * 3f);
            if (Alive && Underwater && _breath <= 0f)
            {
                _drownTick -= dt;
                if (_drownTick <= 0f)
                {
                    _drownTick = 0.5f;
                    ApplyDamage(new DamageInfo { amount = 6, point = Player.Center, source = Team.Enemy, cause = "drown" });
                }
            }

            // i-frame flicker
            if (Renderer != null) Renderer.enabled = !Alive || _health.invulnerable <= 0f || Mathf.Repeat(Time.time * 14f, 1f) > 0.35f;
        }
    }
}
