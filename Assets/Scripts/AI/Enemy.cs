using System;
using System.Collections.Generic;
using Loamlight.Combat;
using Loamlight.Content;
using Loamlight.Core;
using Loamlight.World;
using Loamlight.World.Collision;
using Unity.Mathematics;
using UnityEngine;

namespace Loamlight.AI
{
    /// <summary>
    /// One enemy: a body on the shared tile collision, a small state machine per <see cref="AiKind"/>, contact damage,
    /// knockback, hit flash and death. Runs on a fixed 60 Hz step scaled by <see cref="GameClock"/> (hitstop freezes it).
    /// Every attack has a readable telegraph: Hopper squashes, Flyer flares its wings and screeches, Walker raises its
    /// arms, Caster glows while charging. Targets come from <see cref="CombatRegistry"/>; AI never sees the Player type.
    /// </summary>
    public sealed class Enemy : MonoBehaviour, IDamageable
    {
        public const float Step = 1f / 60f, HitFlash = 0.12f, DespawnDistance = 110f;

        public enum Phase : byte { Idle, Windup, Act, Recover }

        public EnemyDef Def { get; private set; }
        public ICollisionGrid Grid { get; private set; }
        public ILiquidSampler Liquids { get; private set; }
        public Phase State => _phase;
        public float PhaseTime => _phaseTime;
        public Body Body => _body;
        public HealthState Health => _health;
        public bool Telegraphing => _phase == Phase.Windup;
        public int Facing => _facing;

        /// <summary>Caster/boss shots: (enemy, projectile id, position, velocity).</summary>
        public static event Action<Enemy, string, Vector2, Vector2> ProjectileRequested;
        /// <summary>Raised once when an enemy dies (loot, counters).</summary>
        public static event Action<Enemy> Died;
        public static readonly List<Enemy> Live = new List<Enemy>();

        Body _body;
        HealthState _health;
        Phase _phase;
        float _phaseTime, _accum, _flash, _cooldown, _stuck;
        int _facing = 1;
        Vector2 _diveTarget;
        SpriteRenderer _sr;
        SpriteAnimator _anim;
        uint _rng;
        bool _gravity;
        readonly List<IDamageable> _query = new List<IDamageable>();

        public Team Team => Team.Enemy;
        public bool Alive => _health.Alive;
        public Vector2 Center => new Vector2(_body.position.x, _body.position.y + _body.size.y * 0.5f);
        public Rect Hurtbox => new Rect(_body.Left, _body.Bottom, _body.size.x, _body.size.y);

        public void Init(EnemyDef def, ICollisionGrid grid, ILiquidSampler liquids, Vector2 feet, uint seed)
        {
            Def = def;
            Grid = grid;
            Liquids = liquids;
            _health = HealthState.Full(def.health);
            _body = new Body { position = new float2(feet.x, feet.y), size = new float2(def.size.x, def.size.y) };
            _gravity = def.ai == AiKind.Hopper || def.ai == AiKind.Walker;
            _rng = seed | 1u;
            _sr = GetComponent<SpriteRenderer>();
            _anim = GetComponent<SpriteAnimator>();
            _cooldown = 0.5f + Rand() * 1.5f;
            SetPhase(Phase.Idle);
            transform.position = new Vector3(feet.x, feet.y, 0f);
            CombatRegistry.Register(this);
            Live.Add(this);
        }

        void OnDestroy()
        {
            CombatRegistry.Unregister(this);
            Live.Remove(this);
        }

        float Rand()
        {
            _rng ^= _rng << 13; _rng ^= _rng >> 17; _rng ^= _rng << 5;
            return (_rng & 0xffffff) / (float)0x1000000;
        }

        void SetPhase(Phase p)
        {
            _phase = p;
            _phaseTime = 0f;
        }

        // ------------------------------------------------------------------------------------------- damage
        public bool ApplyDamage(DamageInfo hit)
        {
            if (!Alive) return false;
            int taken = _health.Apply(HealthState.ComputeDamage(hit.amount, Def.defense, 0.5f, false) , 0.08f);
            if (taken <= 0) return false;
            hit.amount = taken;
            float resist = 1f - Mathf.Clamp01(Def.knockbackResist);
            _body.velocity = new float2(hit.knockback.x * resist, math.max(_body.velocity.y, hit.knockback.y * resist));
            if (_gravity && hit.knockback.y > 0f) _body.grounded = false;
            _flash = HitFlash;
            // a hard hit interrupts a wind-up (rewarding aggression), never a lunge already under way
            if (_phase == Phase.Windup && resist > 0.5f) SetPhase(Phase.Recover);
            CombatEvents.RaiseDamaged(this, hit);
            if (!_health.Alive) Die(hit);
            return true;
        }

        void Die(DamageInfo hit)
        {
            Sfx.Play("enemy_die", Center, 0.7f);
            CombatEvents.RaiseKilled(this, hit);
            Died?.Invoke(this);
            Destroy(gameObject);
        }

        // ------------------------------------------------------------------------------------------- update
        void Update()
        {
            if (Def == null) return;
            _accum += Time.deltaTime * GameClock.Scale;
            if (_accum > 0.25f) _accum = 0.25f;
            while (_accum >= Step && Alive)
            {
                _accum -= Step;
                Tick(Step);
            }
            if (!Alive) return;
            transform.position = new Vector3(_body.position.x, _body.position.y, 0f);
            _flash = Mathf.Max(0f, _flash - Time.deltaTime);
            if (_sr != null)
            {
                _sr.flipX = _facing < 0;
                _sr.color = _flash > 0f ? new Color(1f, 0.45f, 0.45f) : Telegraphing && Mathf.Repeat(Time.time * 10f, 1f) > 0.5f ? new Color(1f, 0.9f, 0.75f) : Color.white;
            }
            Animate();
        }

        /// <summary>Run the AI and physics for dt seconds in fixed steps (tests drive enemies through this).</summary>
        public void Simulate(float seconds)
        {
            for (float t = 0f; t < seconds && Alive && Def != null; t += Step) Tick(Step);
        }

        void Tick(float dt)
        {
            _phaseTime += dt;
            _cooldown -= dt;
            float noRegen = 0f;
            _health.Tick(dt, float.MaxValue, 0f, ref noRegen);   // counts down the hit i-frames
            var target = CombatRegistry.Nearest(Team.Player, Center, DespawnDistance);
            if (target == null)
            {
                // nobody near: drift and eventually despawn
                _stuck += dt;
                if (_stuck > 3f) { Destroy(gameObject); return; }
            }
            else _stuck = 0f;

            switch (Def.ai)
            {
                case AiKind.Hopper: Hopper(target, dt); break;
                case AiKind.Flyer: Flyer(target, dt); break;
                case AiKind.Walker: Walker(target, dt); break;
                case AiKind.Caster: Caster(target, dt); break;
                default: Walker(target, dt); break;
            }

            // physics
            if (_gravity)
            {
                float wet = TileCollision.Submersion(_body, Liquids, out _);
                float g = wet > 0.4f ? 24f : 70f;
                _body.velocity.y = math.max(_body.velocity.y - g * dt, wet > 0.4f ? -6f : -40f);
            }
            TileCollision.Move(ref _body, dt, Grid, _gravity ? 1.05f : 0f);

            // contact damage to the player
            if (target != null && Hurtbox.Overlaps(target.Hurtbox))
            {
                var away = target.Center - Center;
                float dir = away.x >= 0f ? 1f : -1f;
                target.ApplyDamage(new DamageInfo
                {
                    amount = HealthState.ComputeDamage(Def.damage, 0, Rand()), knockback = new Vector2(dir * 9f, 7f),
                    point = (Center + target.Center) * 0.5f, source = Team.Enemy, cause = "contact",
                });
            }
        }

        int DirTo(IDamageable t) => t == null ? _facing : (t.Center.x >= Center.x ? 1 : -1);

        void Hopper(IDamageable target, float dt)
        {
            if (!_body.grounded)
            {
                return;   // airborne: no control
            }
            _body.velocity.x = Mathf.MoveTowards(_body.velocity.x, 0f, 40f * dt);
            switch (_phase)
            {
                case Phase.Idle:
                case Phase.Recover:
                    if (_phaseTime > 0.5f + Rand() * 0.04f && _cooldown <= 0f)
                    {
                        _facing = DirTo(target);
                        SetPhase(Phase.Windup);
                    }
                    break;
                case Phase.Windup:    // squash: the tell before the leap
                    if (_phaseTime >= 0.38f)
                    {
                        bool big = target != null && (target.Center.y > Center.y + 1.5f || Mathf.Abs(target.Center.x - Center.x) < 8f);
                        _body.velocity = new float2(_facing * Def.speed * (big ? 1.3f : 1f), big ? 22f : 15f);
                        _body.grounded = false;
                        Sfx.Play("hop", Center, 0.45f);
                        _cooldown = 0.6f + Rand() * 0.9f;
                        SetPhase(Phase.Recover);
                    }
                    break;
            }
        }

        void Flyer(IDamageable target, float dt)
        {
            var c = Center;
            switch (_phase)
            {
                case Phase.Idle:
                {
                    // erratic drift toward a point above the target
                    var goal = target != null ? target.Center + new Vector2(Mathf.Sin(Time.time * 1.3f + _rng % 7) * 5f, 4.5f) : c;
                    var want = (goal - c).normalized * Def.speed + new Vector2(0f, Mathf.Sin(Time.time * 6f + _rng % 5) * 2f);
                    _body.velocity = Vector2.MoveTowards(_body.velocity, want, 20f * dt);
                    _facing = _body.velocity.x >= 0f ? 1 : -1;
                    if (target != null && _cooldown <= 0f && Vector2.Distance(target.Center, c) < 9f)
                    {
                        SetPhase(Phase.Windup);
                        Sfx.Play("screech", c, 0.5f);
                    }
                    break;
                }
                case Phase.Windup:    // wings flare, hovering in place
                    _body.velocity = Vector2.MoveTowards(_body.velocity, Vector2.zero, 40f * dt);
                    if (target != null) _facing = DirTo(target);
                    if (_phaseTime >= 0.45f)
                    {
                        _diveTarget = target != null ? target.Center : c + Vector2.down * 4f;
                        var d = (_diveTarget - c).normalized;
                        _body.velocity = d * Def.speed * 2.6f;
                        SetPhase(Phase.Act);
                    }
                    break;
                case Phase.Act:       // the dive
                    if (_phaseTime >= 0.55f || Vector2.Distance(c, _diveTarget) < 0.6f)
                    {
                        _cooldown = 1.6f + Rand();
                        SetPhase(Phase.Recover);
                    }
                    break;
                case Phase.Recover:
                    _body.velocity = Vector2.MoveTowards(_body.velocity, new Vector2(-_facing * 3f, Def.speed), 30f * dt);
                    if (_phaseTime >= 0.8f) SetPhase(Phase.Idle);
                    break;
            }
        }

        void Walker(IDamageable target, float dt)
        {
            float dist = target != null ? Mathf.Abs(target.Center.x - Center.x) : 99f;
            switch (_phase)
            {
                case Phase.Idle:
                {
                    _facing = DirTo(target);
                    float run = _body.grounded ? 30f : 12f;
                    _body.velocity.x = Mathf.MoveTowards(_body.velocity.x, _facing * Def.speed, run * dt);
                    // hop small walls the step-up can't take
                    if (_body.grounded && Mathf.Abs(_body.velocity.x) < 0.2f && _phaseTime > 0.3f) { _body.velocity.y = 20f; _body.grounded = false; }
                    if (target != null && _cooldown <= 0f && dist < 3.2f && Mathf.Abs(target.Center.y - Center.y) < 2f && _body.grounded)
                        SetPhase(Phase.Windup);
                    break;
                }
                case Phase.Windup:    // arms raised, standing still
                    _body.velocity.x = Mathf.MoveTowards(_body.velocity.x, 0f, 60f * dt);
                    if (_phaseTime >= 0.4f)
                    {
                        _body.velocity = new float2(_facing * Def.speed * 2.4f, 9f);
                        _body.grounded = false;
                        SetPhase(Phase.Act);
                    }
                    break;
                case Phase.Act:
                    if (_phaseTime >= 0.3f && _body.grounded) { _cooldown = 1.2f + Rand(); SetPhase(Phase.Recover); }
                    break;
                case Phase.Recover:
                    _body.velocity.x = Mathf.MoveTowards(_body.velocity.x, 0f, 30f * dt);
                    if (_phaseTime >= 0.6f) SetPhase(Phase.Idle);
                    break;
            }
        }

        void Caster(IDamageable target, float dt)
        {
            var c = Center;
            var hover = target != null ? target.Center + new Vector2((c.x < target.Center.x ? -1f : 1f) * 6f, 3f + Mathf.Sin(Time.time * 2f) * 0.8f) : c;
            switch (_phase)
            {
                case Phase.Idle:
                    _body.velocity = Vector2.MoveTowards(_body.velocity, (hover - c) * 2f, 18f * dt);
                    if (target != null) _facing = DirTo(target);
                    if (target != null && _cooldown <= 0f && Vector2.Distance(target.Center, c) < 16f) SetPhase(Phase.Windup);
                    break;
                case Phase.Windup:    // charge glow (the light and sprite pulse) before the bolt
                    _body.velocity = Vector2.MoveTowards(_body.velocity, Vector2.zero, 30f * dt);
                    if (_phaseTime >= 0.7f)
                    {
                        if (target != null)
                        {
                            var from = c + new Vector2(_facing * 0.6f, 0.4f);
                            var d = (target.Center - from).normalized;
                            ProjectileRequested?.Invoke(this, string.IsNullOrEmpty(Def.projectile) ? "fireball" : Def.projectile, from, d * 13f);
                            Sfx.Play("cast", c, 0.5f);
                        }
                        _cooldown = 2.2f + Rand() * 1.2f;
                        SetPhase(Phase.Recover);
                    }
                    break;
                default:
                    _body.velocity = Vector2.MoveTowards(_body.velocity, (hover - c) * 2f, 18f * dt);
                    if (_phaseTime >= 0.8f) SetPhase(Phase.Idle);
                    break;
            }
        }

        // ------------------------------------------------------------------------------------------- visuals
        void Animate()
        {
            if (_anim == null || _anim.Animations == null) return;
            string clip;
            switch (Def.ai)
            {
                case AiKind.Hopper:
                    clip = _phase == Phase.Windup ? "squash" : !_body.grounded ? (_body.velocity.y > 0f ? "jump" : "fall") : "idle";
                    break;
                case AiKind.Flyer:
                    clip = _phase == Phase.Windup ? "flare" : _phase == Phase.Act ? "dive" : "fly";
                    break;
                case AiKind.Caster:
                    clip = _phase == Phase.Windup ? "cast" : "hover";
                    break;
                default:
                    clip = _phase == Phase.Windup ? "windup" : _phase == Phase.Act ? "lunge" : !_body.grounded ? "fall" : Mathf.Abs(_body.velocity.x) > 0.3f ? "walk" : "idle";
                    break;
            }
            if (_anim.Animations.Find(clip) == null) clip = _anim.Animations.clips.Length > 0 ? _anim.Animations.clips[0].name : clip;
            _anim.Play(clip);
        }
    }
}
