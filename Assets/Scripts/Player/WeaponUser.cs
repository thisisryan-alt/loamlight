using System;
using System.Collections.Generic;
using Loamlight.Combat;
using Loamlight.Content;
using Loamlight.Core;
using Loamlight.Inventory;
using UnityEngine;

namespace Loamlight.Player
{
    /// <summary>
    /// Swings, stabs and shoots the held weapon. <see cref="ToolUser"/> owns the Use input and hands weapon items here,
    /// so there is exactly one consumer of Use. Melee hit tests sweep the blade between frames (no tunnelling at
    /// high swing speeds) and hit each target once per swing; every landed hit gets hitstop, knockback and shake.
    /// </summary>
    public sealed class WeaponUser : MonoBehaviour
    {
        public const float BaseHitstop = 0.055f, BaseShake = 0.12f;

        public PlayerController Player { get; set; }
        public ContentDatabase Db { get; set; }
        public ItemContainer Inventory { get; set; }
        /// <summary>The sprite drawn in the hand for a weapon (falls back to the item icon).</summary>
        public Func<ItemDef, Sprite> HeldSprite { get; set; }
        /// <summary>Spawn a projectile: (projectile id, position, velocity, damage, knockback, weapon).</summary>
        public Action<string, Vector2, Vector2, int, float, ItemDef> FireProjectile { get; set; }

        SpriteRenderer _blade;
        ItemDef _weapon;
        float _timer, _duration, _cooldown;
        Vector2 _aimDir;
        float _prevAngle;
        int _swingDir;
        readonly HashSet<IDamageable> _hit = new HashSet<IDamageable>();
        readonly List<IDamageable> _query = new List<IDamageable>();

        public bool Swinging => _timer > 0f;
        public ItemDef Current => _weapon;
        public int HitsLanded { get; private set; }
        public event Action<IDamageable, DamageInfo> Landed;

        void Awake()
        {
            var go = new GameObject("Held weapon");
            go.transform.SetParent(transform, false);
            _blade = go.AddComponent<SpriteRenderer>();
            _blade.sortingLayerName = "Entities";
            _blade.sortingOrder = 11;
            _blade.enabled = false;
        }

        /// <summary>Called every frame by ToolUser while a weapon is selected.</summary>
        public void Use(ItemDef weapon, Vector2 aimWorld, bool held, float dt)
        {
            _cooldown -= dt;
            if (!held || _timer > 0f || _cooldown > 0f || weapon == null) return;
            if (weapon.weaponKind == WeaponKind.Bow && !string.IsNullOrEmpty(weapon.ammo) && !TakeAmmo(weapon.ammo)) return;
            Begin(weapon, aimWorld);
        }

        void Begin(ItemDef weapon, Vector2 aimWorld)
        {
            _weapon = weapon;
            _duration = Mathf.Max(0.12f, weapon.useTime);
            _timer = _duration;
            _cooldown = _duration;
            _hit.Clear();
            var shoulder = Shoulder();
            _aimDir = (aimWorld - shoulder).sqrMagnitude > 0.01f ? (aimWorld - shoulder).normalized : new Vector2(Player.Facing, 0f);
            Player.Face(_aimDir.x >= 0f ? 1 : -1);
            _swingDir = Player.Facing;
            _prevAngle = BladeAngle(0f);
            var sprite = HeldSprite != null ? HeldSprite(weapon) : weapon.icon;
            _blade.sprite = sprite;
            _blade.enabled = sprite != null;
            Sfx.Play(weapon.weaponKind == WeaponKind.Bow ? "bow" : weapon.weaponKind == WeaponKind.Staff ? "cast" : "swing", shoulder, 0.55f);

            if (weapon.weaponKind == WeaponKind.Bow || weapon.weaponKind == WeaponKind.Staff)
            {
                string projectile = weapon.projectile;
                if (weapon.weaponKind == WeaponKind.Bow && Db.Item(weapon.ammo) is ItemDef ammo && !string.IsNullOrEmpty(ammo.projectile))
                    projectile = ammo.projectile;
                int dmg = weapon.damage + (weapon.weaponKind == WeaponKind.Bow && Db.Item(weapon.ammo) is ItemDef a2 ? a2.damage : 0);
                FireProjectile?.Invoke(projectile, shoulder + _aimDir * 0.8f, _aimDir * Mathf.Max(8f, weapon.projectileSpeed), dmg, weapon.knockback, weapon);
            }
        }

        bool TakeAmmo(string ammoId)
        {
            if (!Db.TryItemId(ammoId, out var id) || Inventory.Count(id) <= 0) return false;
            Inventory.Remove(id, 1);
            return true;
        }

        Vector2 Shoulder() => Player.Feet + new Vector2(0.1f * Player.Facing, 1.75f);

        /// <summary>Blade direction (degrees, 0 = +X) at swing progress u (0..1).</summary>
        float BladeAngle(float u)
        {
            float aim = Mathf.Atan2(_aimDir.y, _aimDir.x) * Mathf.Rad2Deg;
            switch (_weapon.weaponKind)
            {
                case WeaponKind.Broadsword:
                {
                    // overhead arc: from behind the head, over the top, down past the aim line
                    float eased = 1f - (1f - u) * (1f - u) * (1f - u);
                    float from = 115f, to = -70f;
                    float local = Mathf.Lerp(from, to, eased);
                    return _swingDir > 0 ? local : 180f - local;
                }
                default:
                    return aim;   // stabs, spears, bows and staves point at the aim
            }
        }

        /// <summary>How far the blade reaches out from the shoulder at progress u.</summary>
        float Extension(float u)
        {
            switch (_weapon.weaponKind)
            {
                case WeaponKind.Shortsword:
                case WeaponKind.Spear:
                {
                    // jab out fast, hold, pull back
                    float t = u < 0.3f ? u / 0.3f : u < 0.6f ? 1f : 1f - (u - 0.6f) / 0.4f;
                    return Mathf.Lerp(0.35f, 1f, Mathf.SmoothStep(0f, 1f, t));
                }
                default:
                    return 1f;
            }
        }

        bool Active(float u)
        {
            switch (_weapon.weaponKind)
            {
                case WeaponKind.Shortsword:
                case WeaponKind.Spear: return u > 0.1f && u < 0.65f;
                case WeaponKind.Broadsword: return u > 0.15f && u < 0.85f;
                default: return false;
            }
        }

        void Update()
        {
            if (_timer <= 0f || _weapon == null) return;
            float dt = GameClock.Delta;
            _timer -= dt;
            float u = Mathf.Clamp01(1f - _timer / _duration);
            var shoulder = Shoulder();
            float angle = BladeAngle(u);
            float reach = Mathf.Max(1f, _weapon.reach) * Extension(u);

            // the held sprite: item art is drawn on the diagonal (handle bottom-left), so rotate by angle - 45
            var dir = new Vector2(Mathf.Cos(angle * Mathf.Deg2Rad), Mathf.Sin(angle * Mathf.Deg2Rad));
            float spriteLen = _blade.sprite != null ? _blade.sprite.rect.width / 16f * 1.414f : 1.4f;
            _blade.transform.position = shoulder + dir * (reach - spriteLen * 0.5f);
            _blade.transform.rotation = Quaternion.Euler(0f, 0f, angle - 45f);
            _blade.flipX = false;

            if (Active(u))
            {
                // sweep: test the blade at a few angles between last frame and this one
                const int sweeps = 4;
                for (int s = 1; s <= sweeps; s++)
                {
                    float a = Mathf.LerpAngle(_prevAngle, angle, s / (float)sweeps) * Mathf.Deg2Rad;
                    var d = new Vector2(Mathf.Cos(a), Mathf.Sin(a));
                    var tip = shoulder + d * reach;
                    var mid = shoulder + d * (reach * 0.2f);
                    HitArea(Segment(mid, tip, 0.45f));
                }
            }
            _prevAngle = angle;
            if (_timer <= 0f) _blade.enabled = false;
        }

        static Rect Segment(Vector2 a, Vector2 b, float pad) =>
            Rect.MinMaxRect(Mathf.Min(a.x, b.x) - pad, Mathf.Min(a.y, b.y) - pad, Mathf.Max(a.x, b.x) + pad, Mathf.Max(a.y, b.y) + pad);

        void HitArea(Rect area)
        {
            CombatRegistry.Query(area, Team.Enemy, _query);
            foreach (var target in _query)
            {
                if (!_hit.Add(target)) continue;
                var away = (target.Center - Player.Center).normalized;
                if (away.sqrMagnitude < 0.01f) away = new Vector2(Player.Facing, 0f);
                bool crit = UnityEngine.Random.value < 0.04f;
                var hit = new DamageInfo
                {
                    amount = HealthState.ComputeDamage(_weapon.damage, 0, UnityEngine.Random.value, crit),
                    knockback = new Vector2(Mathf.Sign(away.x) * _weapon.knockback * 1.6f, 3f + _weapon.knockback * 0.6f),
                    point = target.Center - away * 0.3f, source = Team.Player, crit = crit, cause = "melee",
                };
                if (!target.ApplyDamage(hit)) continue;
                HitsLanded++;
                GameClock.Hitstop(BaseHitstop * Mathf.Max(0.2f, _weapon.hitstop) * (crit ? 1.5f : 1f));
                CombatEvents.RaiseShake(BaseShake * Mathf.Max(0.2f, _weapon.shake));
                Sfx.Play("hit", hit.point, 0.8f);
                Landed?.Invoke(target, hit);
            }
        }
    }
}
