using System.Collections.Generic;
using Loamlight.Core;
using Loamlight.Lighting;
using UnityEngine;

namespace Loamlight.Game
{
    /// <summary>
    /// Unlit sky behind the world (sorting layer "Sky"): a gradient, stars and moon at night, the sun by day, drifting
    /// clouds and three parallax silhouette layers anchored to the world's surface level. Everything is tinted from the
    /// <see cref="DayNightCycle"/> and fades to black as the camera goes underground.
    /// </summary>
    [DefaultExecutionOrder(1000)]   // after the camera has followed the player
    public sealed class SkyBackdrop : MonoBehaviour
    {
        sealed class Layer
        {
            public readonly List<SpriteRenderer> copies = new List<SpriteRenderer>();
            public SpriteRenderer fill;
            public float width, parallax, verticalParallax, lift;
            public Color body;
        }

        Camera _cam;
        DayNightCycle _cycle;
        float _surfaceY;
        Material _unlit;
        SpriteRenderer _gradient, _sun, _moon;
        Texture2D _gradientTex;
        readonly List<SpriteRenderer> _stars = new List<SpriteRenderer>();
        readonly List<SpriteRenderer> _clouds = new List<SpriteRenderer>();
        readonly List<Layer> _layers = new List<Layer>();
        float _starWidth, _starHeight;
        Sprite _white;

        public static SkyBackdrop Create(Camera cam, DayNightCycle cycle, float surfaceY)
        {
            var sky = new GameObject("Sky Backdrop").AddComponent<SkyBackdrop>();
            sky.Init(cam, cycle, surfaceY);
            return sky;
        }

        void Init(Camera cam, DayNightCycle cycle, float surfaceY)
        {
            _cam = cam;
            _cycle = cycle;
            _surfaceY = surfaceY;
            _unlit = Resources.Load<Material>("Materials/SpriteUnlit");

            _gradientTex = new Texture2D(1, 32, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
            _gradient = Make("Gradient", Sprite.Create(_gradientTex, new Rect(0, 0, 1, 32), new Vector2(0.5f, 0.5f), 1f), -100);

            var whiteTex = new Texture2D(1, 1, TextureFormat.RGBA32, false) { filterMode = FilterMode.Point };
            whiteTex.SetPixel(0, 0, Color.white);
            whiteTex.Apply();
            _white = Sprite.Create(whiteTex, new Rect(0, 0, 1, 1), new Vector2(0.5f, 1f), 1f);

            var star = SpriteSheetIndex.Frame("sky_stars", "all");
            if (star != null)
            {
                _starWidth = star.rect.width / 16f;
                _starHeight = star.rect.height / 16f;
                for (int i = 0; i < 12; i++) _stars.Add(Make("Stars", star, -95));
            }
            _moon = Make("Moon", SpriteSheetIndex.Frame("sky_moon", "all"), -94);
            _sun = Make("Sun", SpriteSheetIndex.Frame("sky_sun", "all"), -94);
            for (int i = 0; i < 4; i++) _clouds.Add(Make("Cloud", SpriteSheetIndex.Frame($"sky_cloud{i % 3}", "all"), -80 + i));

            AddLayer("sky_far", -70, 0.92f, 0.86f, 10f, "#7d93b8");
            AddLayer("sky_mid", -60, 0.8f, 0.72f, 3f, "#5f8f7a");
            AddLayer("sky_near", -50, 0.62f, 0.55f, -3f, "#2f5a3e");
        }

        SpriteRenderer Make(string name, Sprite sprite, int order)
        {
            var go = new GameObject(name);
            go.transform.SetParent(transform, false);
            var sr = go.AddComponent<SpriteRenderer>();
            sr.sprite = sprite;
            sr.sortingLayerName = "Sky";
            sr.sortingOrder = order;
            if (_unlit != null) sr.sharedMaterial = _unlit;
            return sr;
        }

        void AddLayer(string sheet, int order, float parallax, float verticalParallax, float lift, string body)
        {
            var sprite = SpriteSheetIndex.Frame(sheet, "all");
            if (sprite == null) return;
            var layer = new Layer { width = sprite.rect.width / 16f, parallax = parallax, verticalParallax = verticalParallax, lift = lift };
            ColorUtility.TryParseHtmlString(body, out layer.body);
            layer.body *= 0.84f;
            layer.body.a = 1f;
            for (int i = 0; i < 5; i++) layer.copies.Add(Make(sheet, sprite, order));
            layer.fill = Make(sheet + " fill", _white, order);
            _layers.Add(layer);
        }

        void LateUpdate()
        {
            if (_cam == null || _cycle == null) return;
            var s = _cycle.State;
            Vector3 c = _cam.transform.position;
            float halfH = _cam.orthographicSize, halfW = halfH * _cam.aspect;

            // underground the backdrop fades to black (caves show their walls, or darkness)
            float above = Mathf.Clamp01((c.y - (_surfaceY - 70f)) / 45f);
            Color dim = new Color(above, above, above, 1f);

            for (int i = 0; i < 32; i++)
            {
                float u = i / 31f;
                _gradientTex.SetPixel(0, i, Color.Lerp(s.horizon, s.top, Mathf.SmoothStep(0f, 1f, u)) * dim);
            }
            _gradientTex.Apply(false);
            _gradient.transform.position = new Vector3(c.x, c.y, 0f);
            _gradient.transform.localScale = new Vector3(halfW * 2f + 2f, (halfH * 2f + 2f) / 32f, 1f);
            _cam.backgroundColor = s.horizon * dim;

            // stars: tiled across the view with a slow drift, visible at night
            float starAlpha = Mathf.Clamp01(s.night * 1.2f - 0.15f) * above;
            if (_stars.Count > 0)
            {
                float ox = Mathf.Repeat(c.x * 0.03f + Time.time * 0.05f, _starWidth);
                float oy = Mathf.Repeat(c.y * 0.03f, _starHeight);
                int i = 0;
                for (int row = 0; row < 3; row++)
                for (int col = 0; col < 4; col++, i++)
                {
                    var sr = _stars[i];
                    sr.enabled = starAlpha > 0.01f;
                    sr.color = new Color(1f, 1f, 1f, starAlpha);
                    sr.transform.position = new Vector3(c.x - halfW - ox + (col + 0.5f) * _starWidth, c.y - halfH - oy + (row + 0.5f) * _starHeight, 0f);
                }
            }

            // sun and moon on arcs over the view
            PlaceOnArc(_sun, _cycle.SunArc, c, halfW, halfH, Color.white * Mathf.Lerp(1f, 0.85f, s.night) * above);
            PlaceOnArc(_moon, _cycle.MoonArc, c, halfW, halfH, new Color(1f, 1f, 1f, above));

            // clouds drift with the wind, lit by the sky
            Color cloudTint = Color.Lerp(s.light * Mathf.Lerp(0.35f, 1f, s.intensity), s.horizon, 0.25f) * dim;
            cloudTint.a = 0.92f;
            for (int i = 0; i < _clouds.Count; i++)
            {
                float span = halfW * 2f + 16f;
                float x = Mathf.Repeat(i * 23.7f + Time.time * (0.35f + 0.12f * i) - c.x * 0.1f, span) - span * 0.5f;
                float y = halfH * (0.35f + 0.13f * (i % 3)) + (_surfaceY - c.y) * 0.1f;
                _clouds[i].transform.position = new Vector3(c.x + x, c.y + y, 0f);
                _clouds[i].color = cloudTint;
                _clouds[i].enabled = above > 0.01f;
            }

            // parallax silhouettes: copies laid side by side, scrolled modulo the layer width
            Color tint = Color.Lerp(s.light * s.intensity, s.horizon, 0.3f);
            tint = Color.Lerp(tint, new Color(0.08f, 0.1f, 0.2f), s.night * 0.6f) * dim;
            tint.a = 1f;
            foreach (var layer in _layers)
            {
                float baseY = _surfaceY + layer.lift + (c.y - _surfaceY) * layer.verticalParallax;
                float scroll = Mathf.Repeat(c.x * (1f - layer.parallax), layer.width);
                float x0 = c.x - halfW - scroll + layer.width * 0.5f;
                for (int i = 0; i < layer.copies.Count; i++)
                {
                    var sr = layer.copies[i];
                    sr.transform.position = new Vector3(x0 + i * layer.width, baseY, 0f);
                    sr.color = tint;
                }
                // solid ground under the silhouette so the gradient never shows below it
                layer.fill.transform.position = new Vector3(c.x, baseY + 0.01f, 0f);
                layer.fill.transform.localScale = new Vector3(halfW * 2f + 2f, Mathf.Max(0f, baseY - (c.y - halfH - 1f)), 1f);
                layer.fill.color = layer.body * tint;
            }
        }

        static void PlaceOnArc(SpriteRenderer sr, float arc, Vector3 c, float halfW, float halfH, Color color)
        {
            if (sr == null) return;
            sr.enabled = arc >= 0f && color.a > 0.01f;
            if (!sr.enabled) return;
            float a = arc * Mathf.PI;
            sr.transform.position = new Vector3(c.x - Mathf.Cos(a) * halfW * 0.85f, c.y - halfH * 0.2f + Mathf.Sin(a) * halfH * 0.95f, 0f);
            sr.color = color;
        }

        void OnDestroy()
        {
            if (_gradientTex != null) Destroy(_gradientTex);
        }
    }
}
