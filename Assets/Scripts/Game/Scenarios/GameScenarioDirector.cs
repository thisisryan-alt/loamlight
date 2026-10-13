using System.Collections;
using Loamlight.Core;
using UnityEngine;

namespace Loamlight.Game.Scenarios
{
    /// <summary>Lives in the Game scene; when launched with -llCapture it runs the scenario named by -llScenario.</summary>
    public sealed class GameScenarioDirector : MonoBehaviour, ICaptureDirector, ICaptureVerifier
    {
        Scenario _scenario;

        public IEnumerator CaptureScript()
        {
            var g = GameBootstrap.Current;
            while (g == null || !g.Ready) { yield return null; g = GameBootstrap.Current; }
            string name = LaunchArgs.Get("-llScenario", "m1");
            _scenario = name switch
            {
                "perf" => new PerfScenario(),
                "m2" => new M2Scenario(),
                "m3" => new M3Scenario(),
                _ => new M1Scenario(),
            };
            _scenario.Attach(g);
            Debug.Log($"[Scenario] running '{name}'");
            var run = _scenario.Run();
            while (run.MoveNext()) yield return run.Current;
            // checks made after the last shot must still be verified
            yield return new CaptureShot("end");
        }

        public bool VerifyShot(string shotName, Texture2D frame, out string detail)
        {
            if (_scenario == null) { detail = "no scenario"; return true; }
            return _scenario.Drain(out detail);
        }
    }
}
