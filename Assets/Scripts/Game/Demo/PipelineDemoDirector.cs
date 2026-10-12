using System.Collections;
using Loamlight.Core;
using UnityEngine;
using UnityEngine.UI;

namespace Loamlight.Game.Demo
{
    /// <summary>
    /// M0 asset-pipeline proof. Every subject (normal-mapped sphere, the same sphere without a normal map,
    /// a scale-flipped sphere and the miner) has its own small light, and all lights orbit their subject at the
    /// same angle, so each subject is lit from the same direction and they can be compared side by side.
    /// </summary>
    public sealed class PipelineDemoDirector : MonoBehaviour, ICaptureDirector
    {
        [SerializeField] Transform[] orbitLights = new Transform[0];
        [SerializeField] Vector2[] orbitCenters = new Vector2[0];
        [SerializeField] float orbitRadius = 1.9f;
        [SerializeField] float degreesPerSecond = 60f;
        [SerializeField] bool autoOrbit = true;
        [SerializeField] Text caption;

        float _angle = 135f;

        public float LightAngle => _angle;

        public void Configure(Transform[] lights, Vector2[] centers, float radius, Text captionText)
        {
            orbitLights = lights;
            orbitCenters = centers;
            orbitRadius = radius;
            caption = captionText;
        }

        void Start() => SetLightAngle(_angle);

        void Update()
        {
            if (autoOrbit) SetLightAngle(_angle + degreesPerSecond * Time.deltaTime);
        }

        /// <summary>0 = right of the subject, 90 = above, counter-clockwise.</summary>
        public void SetLightAngle(float degrees)
        {
            _angle = Mathf.Repeat(degrees, 360f);
            var dir = new Vector2(Mathf.Cos(_angle * Mathf.Deg2Rad), Mathf.Sin(_angle * Mathf.Deg2Rad));
            for (int i = 0; i < orbitLights.Length && i < orbitCenters.Length; i++)
            {
                var p = orbitCenters[i] + dir * orbitRadius;
                orbitLights[i].position = new Vector3(p.x, p.y, orbitLights[i].position.z);
            }
            if (caption != null)
                caption.text = $"Loamlight M0 pipeline demo  -  light angle {_angle:0} deg";
        }

        public IEnumerator CaptureScript()
        {
            autoOrbit = false;
            var shots = new (float angle, string name)[]
            {
                (135f, "light_upper_left"),
                (45f, "light_upper_right"),
                (270f, "light_below"),
                (180f, "light_left"),
            };
            foreach (var (angle, shotName) in shots)
            {
                SetLightAngle(angle);
                yield return null;
                yield return null;
                yield return new CaptureShot(shotName);
            }
            autoOrbit = true;
        }
    }
}
