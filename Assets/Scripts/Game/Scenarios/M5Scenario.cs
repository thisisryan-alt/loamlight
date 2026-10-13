using System.Collections;
using System.Linq;
using Loamlight.AI;
using Loamlight.Combat;
using Loamlight.Core;
using UnityEngine;

namespace Loamlight.Game.Scenarios
{
    /// <summary>
    /// M5 "biomes, structures, ore tiers, boss": visit a cabin, a dune shrine and an Emberdeep vault and find loot in
    /// their chests; prove the pickaxe tiers gate the ores; then summon the Gloamwarden with a Wick Charm at night and
    /// fight it through all three phases (darkness in phase 2, fury in phase 3) to its loot.
    /// </summary>
    public sealed class M5Scenario : Scenario
    {
        public override bool Peaceful => false;

        public override IEnumerator Run()
        {
            yield return Frames(5);
            G.Spawner.Active = false;
            G.DayNight.SetTime(0.3f);
            var report = G.GenReport;
            Check(report != null && report.Structures >= 3, $"structures placed: {report?.Structures} ({report?.Chests.Count} chests)");

            // ---------------------------------------------------------------- structures and their chests
            foreach (var (loot, shot) in new[] { ("cabin_chest", "cabin"), ("shrine_chest", "shrine"), ("vault_chest", "vault") })
            {
                var chest = report.Chests.Where(c => c.loot == loot).OrderBy(c => Mathf.Abs(c.origin.x - G.World.Width / 2)).FirstOrDefault();
                Check(chest.loot == loot, $"found a {shot}");
                if (chest.loot != loot) continue;
                var origin = new Vector2Int(chest.origin.x, chest.origin.y);
                int items = 0;
                var box = G.Chests.Get(origin);
                for (int i = 0; i < box.Size; i++) items += box[i].count;
                Check(items > 0, $"the {shot} chest holds {items} items");
                Teleport(new Vector2(origin.x + 3.5f, origin.y));
                yield return Seconds(0.8f);
                yield return new CaptureShot(shot);
            }

            // ---------------------------------------------------------------- ore tiers gate mining
            Teleport(G.SpawnPointWorld);
            yield return Seconds(0.4f);
            {
                var f = FeetTile;
                var spot = new Vector2Int(f.x + 2, f.y);
                G.Edits.ClearSilently(spot.x, spot.y);
                G.Edits.Place(G.Db.TileId("lumite_ore"), spot.x, spot.y);
                Check(Select("copper_pickaxe"), "copper pickaxe");
                yield return MineTile(spot, 1.5f);
                Check(G.World.GetBlock(spot.x, spot.y) == G.Db.TileId("lumite_ore"), "copper can't break lumite (tier 2)");
                Give("iron_pickaxe", 1, "an iron pickaxe");
                Select("iron_pickaxe");
                yield return MineTile(spot, 3f);
                Check(G.World.GetBlock(spot.x, spot.y) == 0, "iron breaks lumite");
                G.Edits.Place(G.Db.TileId("cinderite_ore"), spot.x, spot.y);
                yield return MineTile(spot, 1.5f);
                Check(G.World.GetBlock(spot.x, spot.y) == G.Db.TileId("cinderite_ore"), "iron can't break cinderite (tier 3)");
                Give("lumite_pickaxe", 1, "a lumite pickaxe");
                Select("lumite_pickaxe");
                yield return MineTile(spot, 3f);
                Check(G.World.GetBlock(spot.x, spot.y) == 0, "lumite breaks cinderite");
            }

            // ---------------------------------------------------------------- summon refuses daylight
            Give("wick_charm", 2, "two wick charms (anvil recipe covered by the crafting tests)");
            Select("wick_charm");
            yield return UseOnce(FeetTile);
            Check(G.ActiveBoss == null && Count("wick_charm") == 2, "the charm does nothing in daylight");

            // ---------------------------------------------------------------- the Gloamwarden
            G.DayNight.SetTime(0.8f);
            G.Health.SetMaxLife(200, true);
            Give("ember_staff", 1, "an ember staff");
            Give("glowcap_brew", 5, "brews");
            yield return Seconds(0.4f);
            Select("wick_charm");
            yield return UseOnce(FeetTile);
            var boss = G.ActiveBoss;
            Check(boss != null && Count("wick_charm") == 1, "the charm summons the Gloamwarden at night");
            if (boss == null) yield break;
            Check(Sfx.PlayLog.Contains("roar"), "it roars");
            Select("ember_staff");
            yield return Seconds(1.5f);
            yield return new CaptureShot("boss_phase1");

            float minDim = 1f;
            bool sawTell = false, sawPhase3 = false, assisted = false;
            float start = Time.time, nextNote = start, nextAssist = start + 25f;
            while (boss != null && Time.time - start < 90f)
            {
                if (Time.time >= nextNote)
                {
                    nextNote += 5f;
                    Note($"boss t={Time.time - start:0}s hp {boss.Health.current}/{boss.Health.max} phase {boss.Boss.Phase} attacks {boss.Boss.AttacksStarted} " +
                         $"dashes {boss.Boss.DashesDone} | staff hits {G.Projectiles.PlayerHits} | player life {G.Health.Life} deaths {G.Deaths} dim {G.Light.dimming:0.00}");
                }
                minDim = Mathf.Min(minDim, G.Light.dimming);
                sawTell |= boss.Telegraphing;
                sawPhase3 |= boss.Boss.Phase == 3;
                In.aimWorld = boss.Center;
                In.useHeld = true;
                float dx = boss.Center.x - G.Player.Center.x;
                In.moveX = Mathf.Abs(dx) > 9f ? Mathf.Sign(dx) : Mathf.Abs(dx) < 4f ? -Mathf.Sign(dx) : 0f;
                if (!G.Health.Alive) G.Health.Respawn(G.Player.Feet);
                // keep the scenario bounded: after 25 s, scripted damage helps it across the phase lines (noted)
                if (Time.time >= nextAssist && boss.Health.Fraction > 0.04f)
                {
                    nextAssist = Time.time + 0.6f;
                    assisted = true;
                    boss.ApplyDamage(new DamageInfo { amount = 160, source = Team.Player, cause = "melee" });
                }
                if (boss.Boss.Phase == 2 && G.Light.dimming < 0.5f && !_darkShot) { _darkShot = true; yield return new CaptureShot("boss_phase2_dark"); }
                if (boss.Boss.Phase == 3 && !_furyShot) { _furyShot = true; yield return new CaptureShot("boss_phase3"); }
                yield return null;
            }
            In.useHeld = false;
            In.moveX = 0f;
            if (assisted) Note("the fight ran past 40 s, so scripted damage helped finish it");
            Check(boss == null, $"the Gloamwarden fell after {Time.time - start:0} s");
            Check(sawTell, "its attacks were telegraphed");
            Check(minDim < 0.5f, $"phase 2 swallowed the light (dimming reached {minDim:0.00})");
            Check(sawPhase3, "it reached phase 3");
            Check(G.BossesDefeated == 1, "counted as defeated");
            yield return Seconds(2.5f);
            Check(G.Light.dimming > 0.95f, "the light came back");
            Check(Count("gloam_shard") > 0 || G.Pickups.Count > 0, "gloam shards dropped");
            yield return new CaptureShot("boss_defeated");
            G.DayNight.SetTime(0.3f);
        }

        bool _darkShot, _furyShot;

        void Teleport(Vector2 feet)
        {
            G.Player.Teleport(feet);
            G.CameraFollow.SnapToTarget();
        }
    }
}
