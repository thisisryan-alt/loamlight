using System.Collections;
using Loamlight.AI;
using Loamlight.Combat;
using Loamlight.Core;
using Loamlight.World;
using UnityEngine;

namespace Loamlight.Game.Scenarios
{
    /// <summary>
    /// M4 "fight", end to end in the real game: each enemy is spawned beside the player and fought with a different
    /// weapon (telegraphs observed before attacks land), hits show hitstop, knockback and damage numbers, kills drop
    /// loot; the player takes contact damage with i-frames, heals with a brew, drowns a little, dies and respawns.
    /// </summary>
    public sealed class M4Scenario : Scenario
    {
        public override bool Peaceful => false;

        public override IEnumerator Run()
        {
            yield return Frames(5);
            G.Spawner.Active = false;                  // the scenario places its own enemies
            G.DayNight.SetTime(0.3f);
            var arena = FlatArena();
            Teleport(arena);
            yield return Seconds(0.5f);
            Check(G.Health.Alive && G.Health.Life == G.Health.MaxLife, $"player starts healthy ({G.Health.Life}/{G.Health.MaxLife})");

            // ---------------------------------------------------------------- gloop vs shortsword
            Check(Select("copper_shortsword"), "copper shortsword in the starting kit");
            var gloop = SpawnBeside("gloop", 5f);
            Check(gloop != null, "spawned a gloop");
            if (gloop != null)
            {
                bool sawTelegraph = false;
                float t = 0f;
                while (t < 3f && gloop != null && !sawTelegraph) { sawTelegraph = gloop.Telegraphing; t += Time.deltaTime; yield return null; }
                Check(sawTelegraph, "the gloop squashes (telegraph) before it leaps");
                yield return Fight(gloop, 10f);
                Check(gloop == null, "the gloop died to the shortsword");
            }
            Check(G.Weapons.HitsLanded > 0, $"landed {G.Weapons.HitsLanded} sword hits");
            Check(G.Fx.NumbersShown > 0, "damage numbers appeared");
            yield return Seconds(1.2f);
            Check(Count("gloop_gel") > 0 || G.Pickups.Count > 0, "the gloop dropped gel");
            yield return new CaptureShot("gloop_fight");

            // ---------------------------------------------------------------- hollow vs broadsword, at night
            G.DayNight.SetTime(0.8f);
            yield return Recover();
            Give("iron_broadsword", 1, "an iron broadsword (anvil recipe covered by the crafting tests)");
            Check(Select("iron_broadsword"), "holding the broadsword");
            var hollow = SpawnBeside("hollow", 6f);
            bool hollowTell = false, sawHitstop = false;
            // let it come: watch for the tell first, then answer it
            float watch = Time.time + 6f;
            while (hollow != null && Time.time < watch && !hollowTell)
            {
                hollowTell |= hollow.Telegraphing;
                In.aimWorld = hollow.Center;
                yield return null;
            }
            yield return Fight(hollow, 14f, 0.9f, e => { hollowTell |= e.Telegraphing; sawHitstop |= GameClock.InHitstop; });
            Check(hollow == null, "the hollow fell to the broadsword");
            Check(hollowTell, "the hollow raised its arms (telegraph) before lunging");
            Check(sawHitstop, "broadsword hits freeze the action briefly (hitstop)");
            yield return new CaptureShot("hollow_broadsword");

            // ---------------------------------------------------------------- duskbat vs bow
            yield return Recover();
            Give("wooden_bow", 1, "a bow");
            Give("wooden_arrow", 60, "arrows");
            int arrows = Count("wooden_arrow");
            Check(Select("wooden_bow"), "holding the bow");
            var bat = SpawnBeside("duskbat", 7f, 4f);
            bool batTell = false;
            yield return Fight(bat, 16f, 5.5f, e => batTell |= e.Telegraphing);
            Check(bat == null, "shot the duskbat down");
            Check(batTell, "the duskbat flared its wings (telegraph) before diving");
            Check(Count("wooden_arrow") < arrows, $"the bow used arrows ({arrows - Count("wooden_arrow")})");
            yield return new CaptureShot("duskbat_bow");

            // ---------------------------------------------------------------- two gloops vs glaive (reach)
            yield return Recover();
            Give("lumite_glaive", 1, "a lumite glaive");
            Check(Select("lumite_glaive"), "holding the glaive");
            var g1 = SpawnBeside("gloop", 4f);
            var g2 = SpawnBeside("gloop", -4f);
            yield return Fight(g1, 10f, 2.6f, null);
            yield return Fight(g2, 10f, 2.6f, null);
            Check(g1 == null && g2 == null, "the glaive cleared both gloops");

            // ---------------------------------------------------------------- cinder imp vs ember staff
            yield return Recover();
            Give("ember_staff", 1, "an ember staff");
            Check(Select("ember_staff"), "holding the ember staff");
            var imp = SpawnBeside("cinder_imp", 7f, 3f);
            bool impTell = false, impShot = false;
            yield return Fight(imp, 18f, 6f, e => { impTell |= e.Telegraphing; impShot |= G.Projectiles.LiveCount > 0 && e.State == Enemy.Phase.Recover; });
            Check(imp == null, "the ember staff burned the imp down");
            Check(impTell, "the imp's orb charged (telegraph) before it fired");
            Check(Sfx.PlayLog.Contains("boom"), "fireballs explode");
            yield return new CaptureShot("imp_staff");

            // ---------------------------------------------------------------- brew, heartroot, fall damage
            yield return Recover();
            {
                G.Health.ApplyDamage(new DamageInfo { amount = 40, point = G.Player.Center, source = Team.Enemy, cause = "contact" });
                int hurt = G.Health.Life;
                Give("glowcap_brew", 1, "a healing brew");
                Select("glowcap_brew");
                yield return UseOnce(FeetTile);
                Check(G.Health.Life > hurt, $"the brew healed ({hurt} -> {G.Health.Life})");
                Give("heartroot", 1, "a heartroot");
                Select("heartroot");
                yield return Seconds(0.4f);
                yield return UseOnce(FeetTile);
                Check(G.Health.MaxLife == 120, $"heartroot raised max life to {G.Health.MaxLife}");
                yield return Recover();
                int before = G.Health.Life;
                G.Player.Teleport(G.Player.Feet + new Vector2(0f, 26f));
                yield return Seconds(2.2f);
                Check(G.Health.Life < before, $"a 26-tile fall hurts ({before} -> {G.Health.Life})");
            }

            // ---------------------------------------------------------------- the night spawns enemies by itself
            yield return Recover();
            G.Spawner.Active = true;
            G.DayNight.SetTime(0.8f);
            float until = Time.time + 12f;
            while (Time.time < until && Enemy.Live.Count == 0) yield return null;
            Check(Enemy.Live.Count > 0, $"night spawns: {Enemy.Live.Count} enemies near the player ({string.Join(", ", Enemy.Live.ConvertAll(e => e.Def.id))})");
            yield return Seconds(1.5f);
            yield return new CaptureShot("night_spawns");
            G.Spawner.Active = false;
            foreach (var e in Enemy.Live.ToArray()) Object.Destroy(e.gameObject);
            G.DayNight.SetTime(0.3f);

            // ---------------------------------------------------------------- taking a hit: knockback + i-frames
            if (!G.Health.Alive) { Note("the gloop won; waiting for the respawn"); yield return Seconds(5f); }
            G.Health.Heal(999);
            float wait = Time.time + 4f;
            while (G.Health.State.invulnerable > 0f && Time.time < wait) yield return null;   // fight / respawn i-frames
            yield return Seconds(0.2f);
            {
                int before = G.Health.Life;
                var hit = new DamageInfo { amount = 10, knockback = new Vector2(8f, 6f), point = G.Player.Center, source = Team.Enemy, cause = "contact" };
                bool first = G.Health.ApplyDamage(hit);
                bool second = G.Health.ApplyDamage(hit);
                Check(first && !second, "a second hit inside the i-frames is ignored");
                Check(G.Health.Life == before - 10, $"took exactly one hit ({before} -> {G.Health.Life})");
                yield return Seconds(0.1f);
                Check(G.Player.Velocity.x > 1f, $"knocked back ({G.Player.Velocity.x:0.0} tiles/s)");
                Check(Sfx.PlayLog.Contains("hurt"), "hurt sound");
            }
            yield return Seconds(1f);

            // ---------------------------------------------------------------- death and respawn
            {
                int deaths = G.Deaths;
                var killing = new DamageInfo { amount = 999, point = G.Player.Center, source = Team.Enemy, cause = "contact" };
                G.Health.ApplyDamage(killing);
                Check(!G.Health.Alive && G.Deaths == deaths + 1, "a big hit kills the player");
                yield return Seconds(0.6f);
                Check(G.HealthUi.ShowingDeath, "the death overlay shows");
                yield return new CaptureShot("slain");
                yield return Seconds(4f);
                Check(G.Health.Alive && G.Health.Life == G.Health.MaxLife, "respawned at full life");
                Check(Vector2.Distance(G.Player.Feet, G.SpawnPointWorld) < 2f, "respawned at the spawn point");
            }
        }

        // ------------------------------------------------------------------------------------------- helpers
        Vector2Int FlatArena()
        {
            var w = G.World;
            int cx = Mathf.FloorToInt(G.SpawnPointWorld.x);
            // flatten a 30-wide strip at the spawn height so fights are not decided by terrain
            int y = Mathf.FloorToInt(G.SpawnPointWorld.y);
            ushort dirt = G.Db.TileId("dirt");
            for (int x = cx - 15; x <= cx + 15; x++)
            {
                for (int yy = y; yy < y + 12; yy++) G.Edits.ClearSilently(x, yy);
                for (int yy = y - 3; yy < y; yy++) if (w.GetBlock(x, yy) == 0) G.Edits.Place(dirt, x, yy);
            }
            return new Vector2Int(cx, y);
        }

        void Teleport(Vector2Int feet)
        {
            G.Player.Teleport(new Vector2(feet.x + 0.5f, feet.y));
            G.CameraFollow.SnapToTarget();
        }

        Enemy SpawnBeside(string id, float dx, float dy = 0.5f)
        {
            var def = G.Db.Enemy(id);
            if (def == null) return null;
            return G.Spawner.Spawn(def, G.Player.Feet + new Vector2(dx, dy));
        }

        IEnumerator Fight(Enemy e, float timeout) => Fight(e, timeout, 0.9f, null);

        /// <summary>Heal up and wait out any i-frames between fights.</summary>
        IEnumerator Recover()
        {
            if (!G.Health.Alive) yield return Seconds(5f);
            G.Health.Heal(999);
            float wait = Time.time + 4f;
            while (G.Health.State.invulnerable > 0f && Time.time < wait) yield return null;
        }

        /// <summary>Keep about <paramref name="range"/> tiles from the enemy, aim at it and attack until it dies.</summary>
        IEnumerator Fight(Enemy e, float timeout, float range, System.Action<Enemy> observe)
        {
            float end = Time.time + timeout, nextNote = Time.time;
            while (e != null && Time.time < end)
            {
                if (Time.time >= nextNote)
                {
                    nextNote = Time.time + 1f;
                    Note($"fight t={timeout - (end - Time.time):0.0}s enemy {e.Center} hp {e.Health.current} {e.State} | player {G.Player.Center} " +
                         $"life {G.Health.Life} swinging {G.Weapons.Swinging} hits {G.Weapons.HitsLanded} held {G.Tools.HeldItem?.id}");
                }
                var c = e.Center;
                In.aimWorld = c;
                float dx = c.x - G.Player.Center.x;
                observe?.Invoke(e);
                In.moveX = Mathf.Abs(dx) > range + 0.6f ? Mathf.Sign(dx) : Mathf.Abs(dx) < range - 1.5f ? -Mathf.Sign(dx) : 0f;
                if (!G.Health.Alive) G.Health.Respawn(G.Player.Feet);   // keep the fight going if the scripted player loses
                In.useHeld = true;
                yield return null;
            }
            In.useHeld = false;
            In.moveX = 0f;
        }
    }
}
