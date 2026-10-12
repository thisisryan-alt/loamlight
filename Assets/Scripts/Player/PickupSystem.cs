using System;
using System.Collections.Generic;
using Loamlight.Content;
using Loamlight.Core;
using Loamlight.Inventory;
using Loamlight.World;
using Loamlight.World.Collision;
using Unity.Mathematics;
using UnityEngine;

namespace Loamlight.Player
{
    /// <summary>
    /// Items lying in the world. Dropped items pop out, fall and settle on tiles, merge with identical stacks nearby,
    /// and fly to the player when close (if the inventory has room); whatever does not fit stays on the ground.
    /// </summary>
    public sealed class PickupSystem : MonoBehaviour, IDropSink
    {
        public const float MagnetRadius = 4.5f, CollectRadius = 1.1f, Lifetime = 600f;
        const int MaxPickups = 400;

        public ContentDatabase Db { get; set; }
        public ICollisionGrid Grid { get; set; }
        public PlayerController Player { get; set; }
        public ItemContainer Inventory { get; set; }
        public float TimeScale { get; set; } = 1f;

        sealed class Pickup
        {
            public int item, count;
            public Body body;
            public float age;
            public SpriteRenderer sr;
        }

        readonly List<Pickup> _pickups = new List<Pickup>();
        readonly Stack<SpriteRenderer> _pool = new Stack<SpriteRenderer>();
        System.Random _rng = new System.Random(11);
        float _mergeTimer;

        public int Count => _pickups.Count;
        public event Action<int, int> Collected;

        public void Drop(Drop d)
        {
            if (Db.TryItemId(d.item, out var id))
                Spawn(id, d.count, d.position, new Vector2((float)(_rng.NextDouble() * 4 - 2), 6f + (float)_rng.NextDouble() * 3f));
        }

        public void Spawn(int item, int count, Vector2 position, Vector2 velocity)
        {
            if (item == 0 || count <= 0) return;
            if (_pickups.Count >= MaxPickups) Remove(0);
            var p = new Pickup
            {
                item = item, count = count,
                body = new Body { position = new float2(position.x, position.y - 0.3f), size = new float2(0.6f, 0.6f), velocity = new float2(velocity.x, velocity.y) },
            };
            p.sr = _pool.Count > 0 ? _pool.Pop() : CreateRenderer();
            p.sr.gameObject.SetActive(true);
            p.sr.sprite = Db.Item(item)?.icon;
            _pickups.Add(p);
        }

        /// <summary>Total of an item lying in the world (tests and scenarios).</summary>
        public int CountOnGround(int item)
        {
            int n = 0;
            foreach (var p in _pickups) if (p.item == item) n += p.count;
            return n;
        }

        SpriteRenderer CreateRenderer()
        {
            var go = new GameObject("Pickup");
            go.transform.SetParent(transform, false);
            var sr = go.AddComponent<SpriteRenderer>();
            sr.sortingLayerName = "Entities";
            sr.sortingOrder = 5;
            go.transform.localScale = Vector3.one * 0.8f;
            return sr;
        }

        void Remove(int i)
        {
            var p = _pickups[i];
            p.sr.gameObject.SetActive(false);
            _pool.Push(p.sr);
            _pickups.RemoveAt(i);
        }

        void Update()
        {
            if (Player == null || Grid == null) return;
            float dt = Mathf.Min(Time.deltaTime * TimeScale, 0.05f);
            var center = (Vector2)Player.Center;
            for (int i = _pickups.Count - 1; i >= 0; i--)
            {
                var p = _pickups[i];
                p.age += dt;
                if (p.age > Lifetime) { Remove(i); continue; }
                var pc = (Vector2)p.body.Center;
                var to = center - pc;
                float dist = to.magnitude;
                bool fits = Inventory.CanAdd(p.item, 1);
                if (p.age > 0.5f && fits && dist < MagnetRadius)
                {
                    // homing: accelerate straight at the player, ignoring tiles (it is being pulled in)
                    float speed = 9f + (MagnetRadius - dist) * 5f;
                    var v = Vector2.Lerp(new Vector2(p.body.velocity.x, p.body.velocity.y), to.normalized * speed, 10f * dt);
                    p.body.velocity = new float2(v.x, v.y);
                    p.body.position += p.body.velocity * dt;
                    p.body.grounded = false;
                }
                else
                {
                    p.body.velocity.y = Mathf.Max(p.body.velocity.y - 40f * dt, -25f);
                    if (p.body.grounded) p.body.velocity.x *= Mathf.Pow(0.02f, dt);
                    TileCollision.Move(ref p.body, dt, Grid, 0.3f);
                }
                if (p.age > 0.5f && dist < CollectRadius && fits)
                {
                    int left = Inventory.Add(p.item, p.count);
                    int got = p.count - left;
                    if (got > 0)
                    {
                        Sfx.Play("pickup", pc, 0.6f);
                        Collected?.Invoke(p.item, got);
                    }
                    p.count = left;
                    if (p.count <= 0) { Remove(i); continue; }
                }
                float bob = p.body.grounded ? Mathf.Sin((p.age + i) * 3f) * 0.06f : 0f;
                p.sr.transform.position = new Vector3(p.body.position.x, p.body.position.y + 0.3f + bob, 0f);
            }

            _mergeTimer -= dt;
            if (_mergeTimer <= 0f)
            {
                _mergeTimer = 0.5f;
                MergeNearby();
            }
        }

        void MergeNearby()
        {
            for (int i = _pickups.Count - 1; i >= 0; i--)
            for (int j = i - 1; j >= 0; j--)
            {
                var a = _pickups[i];
                var b = _pickups[j];
                if (a.item != b.item) continue;
                if (math.distancesq(a.body.position, b.body.position) > 1.5f * 1.5f) continue;
                int max = Db.Item(a.item)?.maxStack ?? 999;
                if (a.count + b.count > max) continue;
                b.count += a.count;
                Remove(i);
                break;
            }
        }
    }
}
