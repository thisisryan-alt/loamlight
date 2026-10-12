using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Loamlight.Core;
using Unity.Profiling;
using UnityEngine;

namespace Loamlight.Game.Scenarios
{
    /// <summary>
    /// Full-size world performance run: generation time, then frame times while touring the surface, the caverns,
    /// and a heavy-edit "dig storm" that dirties many chunks per frame. Fails if p95 frame time exceeds 16.7 ms.
    /// Reports ProfilerRecorder numbers for our own markers alongside frame time.
    /// </summary>
    public sealed class PerfScenario : Scenario
    {
        public const float BudgetMs = 1000f / 60f;

        public override IEnumerator Run()
        {
            QualitySettings.vSyncCount = 0;
            Application.targetFrameRate = -1;
            Note($"world {G.World.Width}x{G.World.Height}, generation+setup {G.LoadMs:0} ms ({G.GenReport}), " +
                 $"burst {(Loamlight.World.Generation.BurstProbe.IsBurstCompiled() ? "on" : "OFF")}");
            Check(G.LoadMs < 15000, $"world generated in {G.LoadMs / 1000.0:0.00} s (< 15 s)");

            // a Unity Profiler capture of the whole run (open it in Window > Analysis > Profiler); dev builds only
            string rawPath = null;
            if (Debug.isDebugBuild && LaunchArgs.TryGet("-llCapture", out var dir))
            {
                rawPath = System.IO.Path.Combine(dir, "perf.raw");
                UnityEngine.Profiling.Profiler.logFile = rawPath;
                UnityEngine.Profiling.Profiler.enableBinaryLog = true;
                UnityEngine.Profiling.Profiler.enabled = true;
            }
            var mainThread = ProfilerRecorder.StartNew(ProfilerCategory.Internal, "Main Thread", 600);
            var rebuild = ProfilerRecorder.StartNew(ProfilerCategory.Scripts, "Loamlight.ChunkRebuild", 600);
            var light = ProfilerRecorder.StartNew(ProfilerCategory.Scripts, "Loamlight.LightTick", 600);

            var frames = new List<float>();
            var labels = new List<string>();

            // surface tour: teleport across the world, letting chunks stream in
            int w = G.World.Width;
            foreach (var fx in new[] { 0.15f, 0.35f, 0.5f, 0.65f, 0.85f })
            {
                int x = (int)(w * fx);
                G.Player.Teleport(SurfaceAt(x));
                G.CameraFollow.SnapToTarget();
                In.moveX = 1f;
                yield return Measure(1.5f, frames);
                labels.Add($"surface@{x}");
            }
            In.moveX = 0f;
            yield return new CaptureShot("perf_surface");

            // underground: drop into the caverns
            foreach (var fx in new[] { 0.3f, 0.6f })
            {
                int x = (int)(w * fx);
                G.Player.Teleport(OpenSpot(x, (int)(G.World.Height * 0.4f)));
                G.CameraFollow.SnapToTarget();
                yield return Measure(1.5f, frames);
                labels.Add($"cavern@{x}");
            }
            yield return new CaptureShot("perf_cavern");

            // dig storm: 48 tile edits per frame around the camera for 2 seconds
            var storm = new List<float>();
            var rng = new System.Random(5);
            var c = (Vector2)G.Camera.transform.position;
            float end = Time.realtimeSinceStartup + 2f;
            while (Time.realtimeSinceStartup < end)
            {
                for (int i = 0; i < 48; i++)
                {
                    int x = (int)c.x + rng.Next(-30, 30), y = (int)c.y + rng.Next(-17, 17);
                    if (G.World.GetBlock(x, y) != 0) G.Edits.RemoveBlock(x, y);
                    else G.World.SetBlock(x, y, G.Db.TileId("stone"));
                }
                yield return null;
                storm.Add(Time.unscaledDeltaTime * 1000f);
            }
            frames.AddRange(storm);
            yield return new CaptureShot("perf_dig_storm");

            var sorted = frames.OrderBy(f => f).ToList();
            float p50 = sorted[sorted.Count / 2], p95 = sorted[(int)(sorted.Count * 0.95f)], max = sorted[sorted.Count - 1];
            var stormSorted = storm.OrderBy(f => f).ToList();
            float stormP95 = stormSorted[(int)(stormSorted.Count * 0.95f)];
            Note($"frames {frames.Count}: p50 {p50:0.00} ms, p95 {p95:0.00} ms, max {max:0.00} ms; dig storm p95 {stormP95:0.00} ms");
            Note($"main thread avg {Avg(mainThread):0.00} ms, chunk rebuild avg {Avg(rebuild):0.000} ms/frame, light tick avg {Avg(light):0.000} ms/sample, " +
                 $"last rebuild {G.Chunks.LastRebuildMs:0.00} ms for {G.Chunks.LastRebuildCount} chunks, active chunks {G.Chunks.ActiveChunks}, " +
                 $"light tick {G.Light.LastTickMs:0.00} ms");
            Check(p95 <= BudgetMs, $"p95 frame time {p95:0.00} ms within the 60 fps budget ({BudgetMs:0.0} ms)");
            mainThread.Dispose();
            rebuild.Dispose();
            light.Dispose();
            if (rawPath != null)
            {
                UnityEngine.Profiling.Profiler.enabled = false;
                UnityEngine.Profiling.Profiler.enableBinaryLog = false;
                Note($"profiler capture: {rawPath}");
            }
            yield return new CaptureShot("perf_summary");
        }

        static double Avg(ProfilerRecorder r)
        {
            if (!r.Valid || r.Count == 0) return double.NaN;
            var samples = new List<ProfilerRecorderSample>(r.Count);
            r.CopyTo(samples);
            double sum = 0;
            foreach (var s in samples) sum += s.Value;
            return sum / samples.Count * 1e-6;   // ns -> ms
        }

        static IEnumerator Measure(float seconds, List<float> into)
        {
            yield return null;   // skip the teleport frame itself (bulk chunk build)
            yield return null;
            float end = Time.realtimeSinceStartup + seconds;
            while (Time.realtimeSinceStartup < end)
            {
                yield return null;
                into.Add(Time.unscaledDeltaTime * 1000f);
            }
        }

        Vector2 SurfaceAt(int x)
        {
            for (int y = G.World.Height - 1; y > 0; y--)
                if (G.World.GetBlock(x, y) != 0 && G.Render.TileProps[G.World.GetBlock(x, y)].solid != 0) return new Vector2(x + 0.5f, y + 1);
            return new Vector2(x + 0.5f, G.World.Height * 0.75f);
        }

        Vector2 OpenSpot(int x, int y)
        {
            for (int dy = 0; dy < 400; dy++)
            {
                int yy = y - dy;
                if (yy < 2) break;
                if (G.World.GetBlock(x, yy) == 0 && G.World.GetBlock(x, yy + 1) == 0 && G.World.GetBlock(x, yy + 2) == 0) return new Vector2(x + 0.5f, yy);
            }
            return new Vector2(x + 0.5f, y);
        }
    }
}
