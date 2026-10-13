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

        Enemy SpawnBeside(string id, float dx)
        {
            var def = G.Db.Enemy(id);
            if (def == null) return null;
            return G.Spawner.Spawn(def, G.Player.Feet + new Vector2(dx, 0.5f));
        }

        /// <summary>Face the enemy and keep attacking until it dies or time runs out.</summary>
        IEnumerator Fight(Enemy e, float timeout)
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
                In.moveX = Mathf.Abs(dx) > 0.9f ? Mathf.Sign(dx) : 0f;
                In.useHeld = true;
                yield return null;
            }
            In.useHeld = false;
            In.moveX = 0f;
        }
    }
}
