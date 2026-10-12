using System;
using UnityEngine;

namespace Loamlight.Content
{
    public sealed class ItemDef : ScriptableObject
    {
        public string id;
        public string displayName;
        [TextArea] public string description;
        public ItemCategory category;
        public int maxStack = 999;
        public Sprite icon;

        [Header("Placing")]
        public string placesTile;
        public string placesWall;

        [Header("Tools")]
        public ToolType toolType;
        public int toolPower;          // pickaxe tier / axe power
        public float toolSpeed = 1f;   // multiplier on mining speed
        public float useTime = 0.25f;  // seconds between uses
        public float reach = 5.5f;

        [Header("Weapons")]
        public WeaponKind weaponKind;
        public int damage;
        public float knockback;
        public float hitstop = 1f;     // multiplier on base hitstop
        public float shake = 1f;       // multiplier on base camera shake
        public int manaCost;
        public string projectile;
        public string ammo;            // ammo item id consumed by bows
        public float projectileSpeed;

        [Header("Buckets")]
        [Tooltip("'' = not a bucket, 'empty' scoops liquid, 'water' / 'lava' pours it.")]
        public string bucket;

        [Header("Consumables")]
        public int heal;
        public int restoreMana;
        public string summons;         // enemy id (boss) this item summons
    }
}
