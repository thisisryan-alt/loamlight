using System.Collections;
using System.Collections.Generic;
using System.IO;
using Loamlight.Core;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Loamlight.Game.DebugTools
{
    /// <summary>
    /// Debug launch flag: <c>-llCapture &lt;dir&gt; [-llScene &lt;scene&gt;]</c>.
    /// Loads the scene, measures frame times, lets the scene's <see cref="ICaptureDirector"/> stage each shot,
    /// writes PNGs plus report.txt into the directory and quits (exit code 1 if anything looked wrong).
    /// Must run in a real rendering player (not -nographics, which draws nothing).
    /// </summary>
    public sealed class CaptureRunner : MonoBehaviour
    {
        const float WatchdogSeconds = 180f;
        const int TimingFrames = 240;

        string _dir;
        string _scene;
        int _shotIndex;
        bool _failed;
        readonly List<string> _report = new List<string>();

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void Bootstrap()
        {
            if (!LaunchArgs.TryGet("-llCapture", out var dir)) return;
            var go = new GameObject("[CaptureRunner]");
            DontDestroyOnLoad(go);
            var runner = go.AddComponent<CaptureRunner>();
            runner._dir = Path.GetFullPath(dir);
            runner._scene = LaunchArgs.Get("-llScene", null);
        }

        IEnumerator Start()
        {
            Directory.CreateDirectory(_dir);
            QualitySettings.vSyncCount = 0;            // honest frame times
            Application.targetFrameRate = -1;
            Debug.Log($"[Capture] writing to {_dir}");

            if (!string.IsNullOrEmpty(_scene) && SceneManager.GetActiveScene().name != _scene)
            {
                if (!Application.CanStreamedLevelBeLoaded(_scene))
                {
                    Fail($"scene '{_scene}' is not in the build");
                    Finish();
                    yield break;
                }
                yield return SceneManager.LoadSceneAsync(_scene);
            }
            for (int i = 0; i < 10; i++) yield return null;   // let the first frames settle (shader warm-up, layout)

            _report.Add($"scene: {SceneManager.GetActiveScene().name}");
            _report.Add($"resolution: {Screen.width}x{Screen.height}, device: {SystemInfo.graphicsDeviceName} ({SystemInfo.graphicsDeviceType})");
            _report.Add($"cpu: {SystemInfo.processorType}, {SystemInfo.processorCount} threads, build: {(Debug.isDebugBuild ? "development" : "release")}");
            yield return MeasureFrames();

            ICaptureDirector director = null;
            foreach (var mb in FindObjectsByType<MonoBehaviour>(FindObjectsInactive.Exclude))
                if (mb is ICaptureDirector d) { director = d; break; }

            var script = director != null ? director.CaptureScript() : DefaultScript();
            yield return Drive(script);
            Finish();
        }

        void Update()
        {
            if (Time.realtimeSinceStartup > WatchdogSeconds)
            {
                Fail($"watchdog: capture still running after {WatchdogSeconds}s");
                Finish();
            }
        }

        static IEnumerator DefaultScript()
        {
            yield return new WaitForSeconds(1f);
            yield return new CaptureShot("default");
        }

        IEnumerator Drive(IEnumerator script)
        {
            while (script.MoveNext())
            {
                if (script.Current is CaptureShot shot)
                {
                    yield return new WaitForEndOfFrame();
                    Capture(shot.Name);
                }
                else
                {
                    yield return script.Current;
                }
            }
        }

        IEnumerator MeasureFrames()
        {
            var cpu = new List<double>();
            var gpu = new List<double>();
            var frame = new List<float>();
            var timings = new FrameTiming[1];
            for (int i = 0; i < TimingFrames; i++)
            {
                yield return null;
                frame.Add(Time.unscaledDeltaTime * 1000f);
                FrameTimingManager.CaptureFrameTimings();
                if (FrameTimingManager.GetLatestTimings(1, timings) > 0)
                {
                    cpu.Add(timings[0].cpuFrameTime);
                    gpu.Add(timings[0].gpuFrameTime);
                }
            }
            frame.Sort();
            float avg = 0f;
            foreach (var f in frame) avg += f;
            avg /= frame.Count;
            _report.Add($"frame ms over {frame.Count} frames: avg {avg:0.00} ({1000f / avg:0} fps), " +
                        $"p50 {frame[frame.Count / 2]:0.00}, p95 {frame[(int)(frame.Count * 0.95f)]:0.00}, max {frame[frame.Count - 1]:0.00}");
            if (cpu.Count > 0)
                _report.Add($"FrameTimingManager avg: cpu {Avg(cpu):0.00} ms, gpu {Avg(gpu):0.00} ms ({cpu.Count} samples)");
            else
                _report.Add("FrameTimingManager: no data (frame timing stats disabled or unsupported)");
        }

        static double Avg(List<double> v)
        {
            double s = 0;
            foreach (var x in v) s += x;
            return s / v.Count;
        }

        void Capture(string shotName)
        {
            var tex = ScreenCapture.CaptureScreenshotAsTexture();
            var file = Path.Combine(_dir, $"{_shotIndex++:00}_{shotName}.png");
            File.WriteAllBytes(file, tex.EncodeToPNG());

            // A black or uniform frame means nothing rendered; fail loudly instead of "passing" with a blank PNG.
            var px = tex.GetPixels32();
            double sum = 0, sumSq = 0;
            for (int i = 0; i < px.Length; i += 7)
            {
                double l = 0.2126 * px[i].r + 0.7152 * px[i].g + 0.0722 * px[i].b;
                sum += l;
                sumSq += l * l;
            }
            int n = (px.Length + 6) / 7;
            double mean = sum / n, variance = sumSq / n - mean * mean;
            _report.Add($"shot {Path.GetFileName(file)}: {tex.width}x{tex.height}, luminance mean {mean:0.0}, variance {variance:0.0}");
            if (variance < 4.0) Fail($"shot '{shotName}' looks blank (luminance variance {variance:0.00})");
            Destroy(tex);
            Debug.Log($"[Capture] {file}");
        }

        void Fail(string message)
        {
            _failed = true;
            _report.Add("ERROR " + message);
            Debug.LogError("[LL-ERROR] " + message);
        }

        void Finish()
        {
            enabled = false;
            _report.Add(_failed ? "result: FAILED" : "result: ok");
            File.WriteAllLines(Path.Combine(_dir, "report.txt"), _report);
            Debug.Log("[Capture] done: " + (_failed ? "FAILED" : "ok"));
            Application.Quit(_failed ? 1 : 0);
        }
    }
}
