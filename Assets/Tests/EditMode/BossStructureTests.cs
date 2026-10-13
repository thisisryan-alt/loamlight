using System.Linq;
using Loamlight.AI;
using Loamlight.Combat;
using Loamlight.Content;
using Loamlight.Core;
using Loamlight.World.Generation;
using NUnit.Framework;
using UnityEngine;

namespace Loamlight.Tests
{
    public class StructureTests
    {
        [Test]
        public void TemplatesAreDataAndEveryKindGetsPlaced()
        {
            var db = ContentDatabase.Instance;
            Assert.That(db.structures.Length, Is.GreaterThanOrEqualTo(3), "cabin, shrine, vault");
            var plan = StructurePlan.From(db);
            using var w = WorldGenerator.Generate(4200, 1200, 20261012, GenIds.From(db), GenParams.Default, out var report, plan);
            Debug.Log($"[StructureTests] {report}");
            Assert.That(report.Structures, Is.GreaterThanOrEqualTo(plan.Templates.Count), "at least one of each on a full world");
            Assert.That(report.Chests.Count, Is.GreaterThanOrEqualTo(plan.Templates.Count));
            ushort chest = db.TileId("chest");
            foreach (var c in report.Chests)
            {
                Assert.That(w.GetBlock(c.origin.x, c.origin.y), Is.EqualTo(chest), $"chest at {c.origin}");
                Assert.That(w.GetFrame(c.origin.x, c.origin.y) & 0x7f, Is.EqualTo(0), "origin cell");
                Assert.That(w.GetBlock(c.origin.x + 1, c.origin.y + 1), Is.EqualTo(chest), "whole 2x2 chest written");
                Assert.That(db.Loot(c.loot), Is.Not.Null);
            }
            foreach (var t in plan.Templates)
                Assert.That(report.Chests.Any(c => c.loot == t.loot), Is.True, $"a {t.id} was placed");
        }

        [Test]
        public void StructuresAreDeterministic()
        {
            var db = ContentDatabase.Instance;
            using var a = WorldGenerator.Generate(2100, 800, 5, GenIds.From(db), GenParams.Default, out var ra, StructurePlan.From(db));
            using var b = WorldGenerator.Generate(2100, 800, 5, GenIds.From(db), GenParams.Default, out var rb, StructurePlan.From(db));
            Assert.That(WorldHash.Compute(a), Is.EqualTo(WorldHash.Compute(b)));
            Assert.That(ra.Chests.Select(c => c.origin), Is.EqualTo(rb.Chests.Select(c => c.origin)));
        }
    }

    public class BossTests
    {
        sealed class Target : IDamageable
        {
            public Team Team => Team.Player;
            public bool Alive => true;
            public Vector2 Center { get; set; }
            public Rect Hurtbox => new Rect(Center.x - 0.6f, Center.y - 1.3f, 1.2f, 2.6f);
            public int Hits;
            public bool ApplyDamage(DamageInfo hit) { Hits++; return true; }
        }

        [SetUp]
        public void SetUp()
        {
            CombatRegistry.Clear();
            Enemy.Live.Clear();
            GameClock.Reset();
        }

        [Test]
        public void WardenTelegraphsAttacksAndChangesPhaseWithInvulnerableRoars()
        {
            var def = ContentDatabase.Instance.enemies.First(e => e.boss);
            Assert.That(def.ai, Is.EqualTo(AiKind.Boss));
            var target = new Target { Center = new Vector2(30f, 20f) };
            CombatRegistry.Register(target);
            int darkness = 0, phases = 0, shots = 0, minions = 0;
            System.Action<Enemy, float> onDark = (e, l) => darkness++;
            System.Action<Enemy, int> onPhase = (e, p) => phases++;
            System.Action<Enemy, string, Vector2, Vector2> onShot = (e, id, p, v) => shots++;
            System.Action<Enemy, string, Vector2> onMinion = (e, id, p) => minions++;
            Enemy.DarknessRequested += onDark;
            Enemy.BossPhaseChanged += onPhase;
            Enemy.ProjectileRequested += onShot;
            Enemy.MinionRequested += onMinion;
            var go = new GameObject("warden");
            try
            {
                var boss = go.AddComponent<Enemy>();
                boss.Init(def, new FakeGrid(".........."), null, new Vector2(20f, 25f), 3);
                Assert.That(boss.Boss, Is.Not.Null);
                bool told = false;
                for (int i = 0; i < 60 * 12 && boss.Boss.AttacksStarted < 3; i++)
                {
                    boss.Simulate(1f / 60f);
                    told |= boss.Telegraphing;
                }
                Assert.That(boss.Boss.AttacksStarted, Is.GreaterThanOrEqualTo(2), "attacks keep coming in phase 1");
                Assert.That(told, Is.True, "every attack is telegraphed");
                Assert.That(shots + boss.Boss.DashesDone, Is.GreaterThan(0), "volleys or dashes happened");

                // knock it below 60%: phase 2 with darkness, minions and a roar it can't be hurt through
                int toPhase2 = boss.Health.current - Mathf.FloorToInt(def.health * 0.59f);
                boss.ApplyDamage(new DamageInfo { amount = toPhase2 + def.defense, source = Team.Player, cause = "melee" });
                boss.Simulate(0.1f);
                Assert.That(boss.Boss.Phase, Is.EqualTo(2));
                Assert.That(darkness, Is.EqualTo(1), "phase 2 swallows the light");
                Assert.That(minions, Is.GreaterThanOrEqualTo(3), "phase 2 calls duskbats");
                Assert.That(boss.ApplyDamage(new DamageInfo { amount = 50, source = Team.Player, cause = "melee" }), Is.False, "invulnerable while roaring");
                boss.Simulate(2f);
                int toPhase3 = boss.Health.current - Mathf.FloorToInt(def.health * 0.24f);
                boss.ApplyDamage(new DamageInfo { amount = toPhase3 + def.defense, source = Team.Player, cause = "melee" });
                boss.Simulate(0.1f);
                Assert.That(boss.Boss.Phase, Is.EqualTo(3));
                Assert.That(phases, Is.EqualTo(2));
            }
            finally
            {
                Object.DestroyImmediate(go);
                Enemy.DarknessRequested -= onDark;
                Enemy.BossPhaseChanged -= onPhase;
                Enemy.ProjectileRequested -= onShot;
                Enemy.MinionRequested -= onMinion;
                CombatRegistry.Clear();
            }
        }

        [Test]
        public void SummonItemPointsAtTheBoss()
        {
            var db = ContentDatabase.Instance;
            var charm = db.items.First(i => i.category == ItemCategory.Summon);
            var boss = db.Enemy(charm.summons);
            Assert.That(boss, Is.Not.Null);
            Assert.That(boss.boss, Is.True);
            Assert.That(db.Loot(boss.loot).entries.Length, Is.GreaterThan(0));
        }
    }
}
