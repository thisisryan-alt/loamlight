using System.Collections.Generic;
using System.Linq;
using Loamlight.AI;
using Loamlight.Combat;
using Loamlight.Content;
using Loamlight.Core;
using NUnit.Framework;
using UnityEngine;

namespace Loamlight.Tests
{
    public class CombatTests
    {
        sealed class Dummy : IDamageable
        {
            public Team Team { get; set; } = Team.Player;
            public bool Alive => Health.Alive;
            public Vector2 Center { get; set; }
            public Rect Hurtbox => new Rect(Center.x - 0.6f, Center.y - 1.3f, 1.2f, 2.6f);
            public HealthState Health = HealthState.Full(100);
            public readonly List<DamageInfo> Hits = new List<DamageInfo>();
            public bool ApplyDamage(DamageInfo hit)
            {
                if (Health.Apply(hit.amount, 0.5f) <= 0) return false;
                Hits.Add(hit);
                return true;
            }
        }

        [SetUp]
        public void SetUp()
        {
            CombatRegistry.Clear();
            Enemy.Live.Clear();
            GameClock.Reset();
        }

        [TearDown]
        public void TearDown() => CombatRegistry.Clear();

        [Test]
        public void DamageFormulaAppliesDefenceRollAndFloor()
        {
            Assert.That(HealthState.ComputeDamage(20, 0, 0.5f), Is.EqualTo(20));
            Assert.That(HealthState.ComputeDamage(20, 0, 0f), Is.EqualTo(17));
            Assert.That(HealthState.ComputeDamage(20, 0, 1f), Is.EqualTo(23));
            Assert.That(HealthState.ComputeDamage(20, 10, 0.5f), Is.EqualTo(15), "half the defence is subtracted");
            Assert.That(HealthState.ComputeDamage(3, 50, 0.5f), Is.EqualTo(1), "never below 1");
            Assert.That(HealthState.ComputeDamage(20, 0, 0.5f, crit: true), Is.EqualTo(40));
        }

        [Test]
        public void IFramesBlockRepeatedHitsUntilTheyExpire()
        {
            var h = HealthState.Full(100);
            Assert.That(h.Apply(10, 0.67f), Is.EqualTo(10));
            Assert.That(h.Apply(10, 0.67f), Is.EqualTo(0), "inside i-frames");
            float regen = 0f;
            h.Tick(0.7f, 6f, 2f, ref regen);
            Assert.That(h.Apply(10, 0.67f), Is.EqualTo(10), "after i-frames");
            Assert.That(h.current, Is.EqualTo(80));
            Assert.That(h.Apply(500, 0f), Is.EqualTo(0), "still in i-frames from the last hit");
        }

        [Test]
        public void RegenerationWaitsForTheDelay()
        {
            var h = HealthState.Full(100);
            h.Apply(50, 0f);
            float acc = 0f;
            h.Tick(5f, 6f, 2f, ref acc);
            Assert.That(h.current, Is.EqualTo(50), "no regen during the delay");
            h.Tick(2f, 6f, 2f, ref acc);
            for (int i = 0; i < 100; i++) h.Tick(0.05f, 6f, 2f, ref acc);
            Assert.That(h.current, Is.InRange(62, 66), "about 2 life per second once past the delay");
        }

        [Test]
        public void HealingNeverExceedsMaxAndDeathIsFinal()
        {
            var h = HealthState.Full(100);
            h.Apply(30, 0f);
            Assert.That(h.Heal(50), Is.EqualTo(30));
            h.Apply(200, 0f);
            Assert.That(h.Alive, Is.False);
            Assert.That(h.Heal(50), Is.EqualTo(0), "the dead do not heal");
        }

        [Test]
        public void RegistryQueriesByTeamAndArea()
        {
            var a = new Dummy { Center = new Vector2(0, 0) };
            var b = new Dummy { Center = new Vector2(10, 0), Team = Team.Enemy };
            var c = new Dummy { Center = new Vector2(1, 0), Team = Team.Enemy };
            CombatRegistry.Register(a);
            CombatRegistry.Register(b);
            CombatRegistry.Register(c);
            var hits = new List<IDamageable>();
            CombatRegistry.Query(new Rect(-2, -2, 4, 4), Team.Enemy, hits);
            Assert.That(hits, Is.EquivalentTo(new[] { c }));
            Assert.That(CombatRegistry.Nearest(Team.Enemy, Vector2.zero, 50f), Is.SameAs(c));
            Assert.That(CombatRegistry.Nearest(Team.Player, new Vector2(9, 0), 5f), Is.Null, "out of range");
        }

        [Test]
        public void HitstopFreezesGameplayTimeOnly()
        {
            Assert.That(GameClock.Scale, Is.EqualTo(1f));
            GameClock.Hitstop(5f);   // clamped to 0.2 s
            Assert.That(GameClock.InHitstop, Is.True);
            Assert.That(GameClock.Scale, Is.EqualTo(0f));
            GameClock.Reset();
            GameClock.Paused = true;
            Assert.That(GameClock.Scale, Is.EqualTo(0f));
            GameClock.Paused = false;
        }

        [Test]
        public void ArrowsDropAndStopAtWallsAndHitTheFirstEnemy()
        {
            var go = new GameObject("proj");
            try
            {
                var sys = go.AddComponent<ProjectileSystem>();
                //                 col 0123456789...
                sys.Grid = new FakeGrid(
                    "....................#",
                    "....................#",
                    "....................#",
                    "....................#",
                    "#####################");
                sys.Register(new ProjectileSpec { id = "arrow", gravity = 22f, life = 5f, radius = 0.3f });
                sys.Fire("arrow", new Vector2(1f, 2.5f), new Vector2(40f, 0f), 10, 2f, Team.Player);
                Assert.That(sys.LiveCount, Is.EqualTo(1));
                for (int i = 0; i < 120 && sys.LiveCount > 0; i++) sys.Step(1f / 60f);
                Assert.That(sys.LiveCount, Is.EqualTo(0), "stopped by the wall or the floor");

                var enemy = new Dummy { Center = new Vector2(8f, 2.5f), Team = Team.Enemy };
                CombatRegistry.Register(enemy);
                sys.Fire("arrow", new Vector2(1f, 2.5f), new Vector2(40f, 2f), 10, 2f, Team.Player);
                for (int i = 0; i < 60 && sys.LiveCount > 0; i++) sys.Step(1f / 60f);
                Assert.That(enemy.Hits.Count, Is.EqualTo(1), "the arrow hit the enemy in its path");
                Assert.That(enemy.Hits[0].knockback.x, Is.GreaterThan(0f), "knocked away from the shooter");
            }
            finally { Object.DestroyImmediate(go); }
        }

        [Test]
        public void HopperTelegraphsThenLeapsAtThePlayer()
        {
            var def = ContentDatabase.Instance.Enemy("gloop");
            Assert.That(def, Is.Not.Null);
            var player = new Dummy { Center = new Vector2(12f, 2.3f), Team = Team.Player };
            CombatRegistry.Register(player);
            var go = new GameObject("gloop");
            try
            {
                var e = go.AddComponent<Enemy>();
                e.Init(def, new FakeGrid(
                    "..............................",
                    "..............................",
                    "..............................",
                    "..............................",
                    "##############################"), null, new Vector2(4f, 1f), 1);
                bool telegraphed = false, leapt = false;
                for (int i = 0; i < 400 && !leapt; i++)
                {
                    e.Simulate(1f / 60f);
                    if (e.Telegraphing) telegraphed = true;
                    if (telegraphed && e.Body.velocity.y > 5f) leapt = true;
                    Assert.That(!leapt || telegraphed, Is.True, "never leaps without the squash first");
                }
                Assert.That(telegraphed && leapt, Is.True);
                Assert.That(e.Body.velocity.x, Is.GreaterThan(0f), "leaps toward the player");
            }
            finally { Object.DestroyImmediate(go); }
        }

        [Test]
        public void EnemiesCanBeHitAgainOnceTheirIFramesPass()
        {
            var def = ContentDatabase.Instance.Enemy("gloop");
            var go = new GameObject("gloop");
            try
            {
                var e = go.AddComponent<Enemy>();
                e.Init(def, new FakeGrid("..........", "##########"), null, new Vector2(4f, 1f), 1);
                var hit = new DamageInfo { amount = 5, source = Team.Player, cause = "melee" };
                Assert.That(e.ApplyDamage(hit), Is.True);
                Assert.That(e.ApplyDamage(hit), Is.False, "same instant: i-frames");
                e.Simulate(0.2f);
                Assert.That(e.ApplyDamage(hit), Is.True, "i-frames ran out");
                Assert.That(e.Health.current, Is.EqualTo(def.health - 10));
            }
            finally { Object.DestroyImmediate(go); }
        }

        [Test]
        public void EveryEnemyHasAnArtSheetAndLootThatExists()
        {
            var db = ContentDatabase.Instance;
            Assert.That(db.enemies.Count(e => !e.boss), Is.GreaterThanOrEqualTo(3));
            foreach (var e in db.enemies)
            {
                Assert.That(SpriteSheetIndex.Get(e.sheet), Is.Not.Null, $"{e.id}: sheet '{e.sheet}'");
                if (!string.IsNullOrEmpty(e.loot)) Assert.That(db.Loot(e.loot), Is.Not.Null, $"{e.id}: loot '{e.loot}'");
            }
            int weapons = db.items.Count(i => i.weaponKind != WeaponKind.None);
            Assert.That(weapons, Is.GreaterThanOrEqualTo(4), "at least four distinct weapons");
            Assert.That(db.items.Where(i => i.weaponKind != WeaponKind.None).Select(i => i.weaponKind).Distinct().Count(), Is.GreaterThanOrEqualTo(4),
                "the weapons play differently (distinct kinds)");
        }
    }
}
