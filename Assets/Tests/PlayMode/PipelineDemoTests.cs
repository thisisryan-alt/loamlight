using System.Collections;
using System.Linq;
using Loamlight.Core;
using Loamlight.Game.Demo;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace Loamlight.Tests
{
    public class PipelineDemoTests
    {
        [UnitySetUp]
        public IEnumerator LoadScene()
        {
            yield return SceneManager.LoadSceneAsync("PipelineDemo");
            yield return null;
        }

        [UnityTest]
        public IEnumerator LightsUseNormalMapsAndTargetEveryLayer()
        {
            var lights = Object.FindObjectsByType<Light2D>(FindObjectsInactive.Exclude);
            var points = lights.Where(l => l.lightType == Light2D.LightType.Point).ToArray();
            Assert.That(points.Length, Is.GreaterThanOrEqualTo(6));
            foreach (var l in points)
            {
                Assert.That(l.normalMapQuality, Is.Not.EqualTo(Light2D.NormalMapQuality.Disabled), l.name);
                Assert.That(l.targetSortingLayers.Length, Is.EqualTo(SortingLayer.layers.Length), l.name);
            }
            Assert.That(lights.Count(l => l.lightType == Light2D.LightType.Global), Is.EqualTo(1));
            yield break;
        }

        [UnityTest]
        public IEnumerator TorchesOrbitTheirSubjectsInSync()
        {
            var director = Object.FindAnyObjectByType<PipelineDemoDirector>();
            Assert.That(director, Is.Not.Null);
            var torches = Object.FindObjectsByType<Light2D>(FindObjectsInactive.Exclude)
                .Where(l => l.name.StartsWith("Torch")).OrderBy(l => l.name).ToArray();

            director.SetLightAngle(90f);
            var above = torches.Select(t => t.transform.position).ToArray();
            director.SetLightAngle(270f);
            var below = torches.Select(t => t.transform.position).ToArray();
            for (int i = 0; i < torches.Length; i++)
            {
                Assert.That(above[i].x, Is.EqualTo(below[i].x).Within(1e-3f), torches[i].name);
                Assert.That(above[i].y - below[i].y, Is.EqualTo(3.8f).Within(1e-3f), torches[i].name + ": 2 x orbit radius");
            }
            yield return null;
            Assert.That(director.LightAngle, Is.Not.EqualTo(270f), "auto-orbit should keep the light moving");
        }

        [UnityTest]
        public IEnumerator MinerAnimates()
        {
            var walker = Object.FindObjectsByType<SpriteAnimator>(FindObjectsInactive.Exclude).First(a => a.CurrentClip == "walk");
            var sr = walker.GetComponent<SpriteRenderer>();
            var seen = new System.Collections.Generic.HashSet<Sprite>();
            float end = Time.time + 0.8f;
            while (Time.time < end)
            {
                seen.Add(sr.sprite);
                yield return null;
            }
            Assert.That(seen.Count, Is.GreaterThanOrEqualTo(4), "walk cycle should show several frames in 0.8 s");
            Assert.That(sr.sprite.GetSecondaryTextureCount(), Is.EqualTo(1), "animated frames carry the normal map");
        }
    }
}
