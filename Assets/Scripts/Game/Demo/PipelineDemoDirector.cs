using System.Collections;
using Loamlight.Core;
using UnityEngine;
using UnityEngine.UI;

namespace Loamlight.Game.Demo
{
    /// <summary>
    /// M0 asset-pipeline proof. Every subject (normal-mapped sphere, the same sphere without a normal map,
    /// a flipX copy, and two miners) has its own small light, and all lights orbit their subject at the
    /// same angle, so each subject is lit from the same direction and they can be compared side by side.
    /// Launch flag -llDemoScaleFlip mirrors the flipped sphere with scale.x = -1 instead of flipX: a negative
    /// control that must make capture verification fail (URP inverts normal Y under negative scale).
    /// </summary>
    public sealed class PipelineDemoDirector : MonoBehaviour, ICaptureDirector, ICaptureVerifier
    {
        [SerializeField] Transform[] orbitLights = new Transform[0];
        [SerializeField] Vector2[] orbitCenters = new Vector2[0];
        [SerializeField] float orbitRadius = 1.9f;
        [SerializeField] float degreesPerSecond = 60f;
        [SerializeField] bool autoOrbit = true;
        [SerializeField] Text caption;
        [Header("Capture verification")]
        [SerializeField] Transform normalSphere;
        [SerializeField] Transform controlSphere;
        [SerializeField] Transform flippedSphere;
        [SerializeField] float sphereRadius = 19f / 16f;

        float _angle = 135f;

        public float LightAngle => _angle;

        public void Configure(Transform[] lights, Vector2[] centers, float radius, Text captionText,
                              Transform withNormals, Transform withoutNormals, Transform flipped)
        {
            orbitLights = lights;
            orbitCenters = centers;
            orbitRadius = radius;
            caption = captionText;
            normalSphere = withNormals;
            controlSphere = withoutNormals;
            flippedSphere = flipped;
        }

        void Start()
        {
            if (LaunchArgs.Has("-llDemoScaleFlip") && flippedSphere != null)
            {
                flippedSphere.GetComponent<SpriteRenderer>().flipX = false;
                flippedSphere.localScale = new Vector3(-1f, 1f, 1f);
            }
            SetLightAngle(_angle);
        }

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

        /// <summary>
        /// Checks, from pixels, the three things M0 must prove: (1) the normal map changes the shading compared
        /// with the identical sprite without one, (2) it brightens the side facing the light (channel convention
        /// is right end to end), (3) a flipX copy lights the same as the original (the sphere is symmetric).
        /// </summary>
        public bool VerifyShot(string shotName, Texture2D frame, out string detail)
        {
            var cam = Camera.main;
            var pixels = frame.GetPixels32();
            var dir = new Vector2(Mathf.Cos(_angle * Mathf.Deg2Rad), Mathf.Sin(_angle * Mathf.Deg2Rad));

            float flipDiff = 0f, effect = 0f, toward = 0f, away = 0f;
            int n = 0, nToward = 0, nAway = 0;
            const int grid = 15;
            for (int gy = 0; gy < grid; gy++)
            for (int gx = 0; gx < grid; gx++)
            {
                var uv = new Vector2(gx / (grid - 1f) * 2f - 1f, gy / (grid - 1f) * 2f - 1f) * 0.85f;
                if (uv.sqrMagnitude > 0.85f * 0.85f) continue;
                float lit = Luma(Sample(cam, frame, pixels, normalSphere, uv));
                float control = Luma(Sample(cam, frame, pixels, controlSphere, uv));
                float flip = Luma(Sample(cam, frame, pixels, flippedSphere, uv));
                flipDiff += Mathf.Abs(lit - flip);
                effect += Mathf.Abs(lit - control);
                n++;
                float facing = Vector2.Dot(uv.normalized, dir);
                if (uv.magnitude < 0.25f) continue;
                if (facing > 0.5f) { toward += lit - control; nToward++; }
                else if (facing < -0.5f) { away += lit - control; nAway++; }
            }
            flipDiff /= n;
            effect /= n;
            float contrast = toward / Mathf.Max(1, nToward) - away / Mathf.Max(1, nAway);

            bool ok = flipDiff < 3f && effect > 5f && contrast > 8f;
            detail = $"light {_angle:0} deg: flipX vs original {flipDiff:0.0} (<3), normal-map effect {effect:0.0} (>5), " +
                     $"lit-side minus far-side vs control {contrast:+0.0;-0.0} (>8)";
            return ok;
        }

        Color32 Sample(Camera cam, Texture2D frame, Color32[] pixels, Transform sphere, Vector2 uv)
        {
            var c = cam.WorldToScreenPoint(sphere.position);
            var e = cam.WorldToScreenPoint(sphere.position + Vector3.right * sphereRadius);
            float r = e.x - c.x;
            int x = Mathf.Clamp(Mathf.FloorToInt(c.x + uv.x * r), 0, frame.width - 1);
            int y = Mathf.Clamp(Mathf.FloorToInt(c.y + uv.y * r), 0, frame.height - 1);   // both bottom-left origin
            return pixels[y * frame.width + x];
        }

        static float Luma(Color32 c) => 0.2126f * c.r + 0.7152f * c.g + 0.0722f * c.b;
    }
}
