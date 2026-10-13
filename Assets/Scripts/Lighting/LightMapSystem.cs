using System;
using System.Collections.Generic;
using Loamlight.Content;
using Loamlight.World;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace Loamlight.Lighting
{
    /// <summary>
    /// Pillar 1. Tile light map computed on a fixed tick (Burst flood fill of sky light + emitters, attenuated hard
    /// by solid tiles) and applied through a URP Sprite Light2D whose cookie texture is the light map (1 texel per
    /// tile, bilinear). Underground is black unless something emits. A pool of point Light2Ds with normal maps sits
    /// on the brightest nearby emitters so tiles and sprites get per-pixel highlights.
    /// </summary>
    public sealed class LightMapSystem : MonoBehaviour
    {
        public const float AirDecay = 0.84f, WaterDecay = 0.76f, SolidDecay = 0.5f;
        static readonly ProfilerMarker TickMarker = new ProfilerMarker(ProfilerCategory.Scripts, "Loamlight.LightTick");

        [Header("Region")]
        public int marginTiles = 24;          // >= largest emitter reach (~15 tiles at AirDecay), so light never pops
        public float tickRate = 20f;

        [Header("Sky")]
        public Color skyColor = new Color(1f, 0.97f, 0.9f);
        [Range(0f, 1.5f)] public float skyIntensity = 1f;

        [Header("Highlights")]
        public int highlightLights = 24;
        public float highlightIntensity = 0.55f;
        public float highlightRadius = 6.5f;

        public float lavaGlow = 1f;
        public Color lavaColor = new Color(1f, 0.45f, 0.12f);
        [Header("Emberdeep ambient")]
        public Color deepAmbient = new Color(0.34f, 0.12f, 0.06f);
        [Range(0f, 0.3f)] public float deepFraction = 0.11f;

        /// <summary>Optional override that fills the light map instead of propagation (calibration scenes).</summary>
        public Func<int, int, Color> debugPattern;

        WorldData _world;
        NativeArray<TileProps> _props;
        Camera _camera;
        Light2D _mapLight;
        Texture2D _texture;
        Sprite _sprite;
        NativeArray<int> _skyFloor;
        NativeArray<float3> _light;
        NativeArray<float> _decay;
        NativeList<int4> _emitters;
        int _texW, _texH;
        RectInt _region;
        float _accum;
        readonly List<Light2D> _highlights = new List<Light2D>();
        readonly List<int4> _sortedEmitters = new List<int4>();

        public RectInt Region => _region;
        public double LastTickMs { get; private set; }
        public Light2D MapLight => _mapLight;
        /// <summary>Brightest channel of the light map at a tile from the last tick (0 outside the region).</summary>
        public float Sample(int x, int y)
        {
            if (!_light.IsCreated || !_region.Contains(new Vector2Int(x, y))) return 0f;
            var v = _light[(y - _region.y) * _texW + (x - _region.x)];
            return Mathf.Max(v.x, Mathf.Max(v.y, v.z));
        }

        public int SkyFloor(int x) => _skyFloor.IsCreated && x >= 0 && x < _skyFloor.Length ? _skyFloor[x] : -1;

        public void Init(WorldData world, NativeArray<TileProps> tileProps, Camera cam)
        {
            Shutdown();
            _world = world;
            _props = tileProps;
            _camera = cam;
            _skyFloor = new NativeArray<int>(world.Width, Allocator.Persistent);
            _emitters = new NativeList<int4>(256, Allocator.Persistent);
            new SkyFloorJob { layout = world.Layout, blocks = world.Blocks, walls = world.Walls, props = _props, skyFloor = _skyFloor }
                .Schedule(world.Width, 64).Complete();
            world.TileChanged += OnTileChanged;

            if (_mapLight == null)
            {
                var go = new GameObject("Light Map (sprite light)");
                go.transform.SetParent(transform, false);
                _mapLight = go.AddComponent<Light2D>();
                _mapLight.lightType = Light2D.LightType.Sprite;
                _mapLight.intensity = 1f;
                _mapLight.color = Color.white;
                _mapLight.lightOrder = 0;
            }
            _mapLight.targetSortingLayers = AllLayers();
            EnsureTexture();
            Tick();
        }

        static int[] AllLayers()
        {
            var layers = SortingLayer.layers;
            var ids = new List<int>();
            foreach (var l in layers) if (l.name != "Overlay") ids.Add(l.id);
            return ids.ToArray();
        }

        void EnsureTexture()
        {
            float halfH = _camera.orthographicSize, halfW = halfH * Mathf.Max(_camera.aspect, 16f / 9f);
            int w = Mathf.CeilToInt(halfW * 2f) + marginTiles * 2 + 2;
            int h = Mathf.CeilToInt(halfH * 2f) + marginTiles * 2 + 2;
            if (_texture != null && w <= _texW && h <= _texH) return;
            _texW = w;
            _texH = h;
            if (_light.IsCreated) _light.Dispose();
            if (_decay.IsCreated) _decay.Dispose();
            _light = new NativeArray<float3>(w * h, Allocator.Persistent);
            _decay = new NativeArray<float>(w * h, Allocator.Persistent);
            if (_texture != null) Destroy(_texture);
            if (_sprite != null) Destroy(_sprite);
            _texture = new Texture2D(w, h, TextureFormat.RGBA32, false, true)
            {
                name = "LightMap", filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp,
            };
            // pivot bottom-left, 1 texel = 1 tile = 1 world unit
            _sprite = Sprite.Create(_texture, new Rect(0, 0, w, h), Vector2.zero, 1f, 0, SpriteMeshType.FullRect);
            _sprite.name = "LightMap";
            _mapLight.lightCookieSprite = _sprite;
        }

        void Update()
        {
            if (_world == null) return;
            _accum += Time.unscaledDeltaTime;
            float step = 1f / tickRate;
            if (_accum < step) return;
            _accum %= step;
            Tick();
        }

        public void Tick()
        {
            if (_world == null) return;
            using var _ = TickMarker.Auto();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            EnsureTexture();
            var cam = _camera.transform.position;
            int ox = Mathf.FloorToInt(cam.x) - _texW / 2, oy = Mathf.FloorToInt(cam.y) - _texH / 2;
            _region = new RectInt(ox, oy, _texW, _texH);
            var pixels = _texture.GetRawTextureData<Color32>();

            if (debugPattern != null)
            {
                for (int ry = 0; ry < _texH; ry++)
                for (int rx = 0; rx < _texW; rx++)
                    pixels[ry * _texW + rx] = (Color32)debugPattern(ox + rx, oy + ry);
                _emitters.Clear();
            }
            else
            {
                _emitters.Clear();
                new PropagateJob
                {
                    layout = _world.Layout, ox = ox, oy = oy, w = _texW, h = _texH,
                    blocks = _world.Blocks, walls = _world.Walls, liquid = _world.Liquid, liquidKind = _world.LiquidKind,
                    props = _props, skyFloor = _skyFloor, sky = new float3(skyColor.r, skyColor.g, skyColor.b) * skyIntensity,
                    deep = new float3(deepAmbient.r, deepAmbient.g, deepAmbient.b), deepLine = (int)(_world.Height * deepFraction),
                    lava = new float3(lavaColor.r, lavaColor.g, lavaColor.b) * lavaGlow,
                    light = _light, decay = _decay, pixels = pixels, emitters = _emitters,
                }.Run();
            }
            _texture.Apply(false, false);
            _mapLight.transform.position = new Vector3(ox, oy, 0f);
            UpdateHighlights();
            LastTickMs = sw.Elapsed.TotalMilliseconds;
        }

        void UpdateHighlights()
        {
            _sortedEmitters.Clear();
            var c = (Vector2)_camera.transform.position;
            float halfH = _camera.orthographicSize + 4f, halfW = halfH * _camera.aspect + 4f;
            for (int i = 0; i < _emitters.Length; i++)
            {
                var e = _emitters[i];
                if (Mathf.Abs(e.x - c.x) < halfW && Mathf.Abs(e.y - c.y) < halfH) _sortedEmitters.Add(e);
            }
            _sortedEmitters.Sort((a, b) =>
                ((Vector2)new Vector2(a.x, a.y) - c).sqrMagnitude.CompareTo(((Vector2)new Vector2(b.x, b.y) - c).sqrMagnitude));
            int n = Mathf.Min(highlightLights, _sortedEmitters.Count);
            while (_highlights.Count < n) _highlights.Add(CreateHighlight(_highlights.Count));
            for (int i = 0; i < _highlights.Count; i++)
            {
                var l = _highlights[i];
                if (i >= n) { if (l.enabled) l.enabled = false; continue; }
                var e = _sortedEmitters[i];
                l.enabled = true;
                l.transform.position = new Vector3(e.x + 0.5f, e.y + 0.5f, 0f);
                // packed colour: rgb in 0..255 in e.z (24 bits), radius scale in e.w (tenths)
                l.color = new Color(((e.z >> 16) & 255) / 255f, ((e.z >> 8) & 255) / 255f, (e.z & 255) / 255f);
                l.pointLightOuterRadius = Mathf.Max(2f, e.w / 10f) * highlightRadius / 10f;
                l.intensity = highlightIntensity;
            }
        }

        /// <summary>A normal-mapped point light that affects every sorting layer (projectiles, casters, the boss).</summary>
        public static Light2D CreatePointLight(string name, Transform parent, Color color, float radius, float intensity)
        {
            var go = new GameObject(name);
            if (parent != null) go.transform.SetParent(parent, false);
            var l = go.AddComponent<Light2D>();
            l.lightType = Light2D.LightType.Point;
            l.pointLightInnerRadius = 0.2f;
            l.pointLightOuterRadius = radius;
            l.falloffIntensity = 0.6f;
            l.intensity = intensity;
            l.color = color;
            l.targetSortingLayers = AllLayers();
            Light2DSettings.EnableNormalMaps(l, 1.2f);
            return l;
        }

        Light2D CreateHighlight(int i)
        {
            var go = new GameObject($"Highlight {i}");
            go.transform.SetParent(transform, false);
            var l = go.AddComponent<Light2D>();
            l.lightType = Light2D.LightType.Point;
            l.pointLightInnerRadius = 0.2f;
            l.pointLightOuterRadius = highlightRadius;
            l.falloffIntensity = 0.65f;
            l.intensity = highlightIntensity;
            l.targetSortingLayers = AllLayers();
            Light2DSettings.EnableNormalMaps(l, 1.4f);
            return l;
        }

        void OnTileChanged(int x, int y)
        {
            if (!_skyFloor.IsCreated || x < 0 || x >= _skyFloor.Length) return;
            // recompute the column's sky floor (cheap: scan down from the top)
            var layout = _world.Layout;
            int floor = -1;
            for (int yy = layout.Height - 1; yy >= 0; yy--)
            {
                int i = layout.Index(x, yy);
                if (BlocksSky(_world.Blocks[i], _world.Walls[i], _props)) { floor = yy; break; }
            }
            _skyFloor[x] = floor;
        }

        static bool BlocksSky(ushort block, ushort wall, NativeArray<TileProps> props) =>
            wall != 0 || (block != 0 && props[block].solid != 0);

        void OnDestroy() => Shutdown();

        void Shutdown()
        {
            if (_world != null) _world.TileChanged -= OnTileChanged;
            if (_skyFloor.IsCreated) _skyFloor.Dispose();
            if (_light.IsCreated) _light.Dispose();
            if (_decay.IsCreated) _decay.Dispose();
            if (_emitters.IsCreated) _emitters.Dispose();
        }

        [BurstCompile(CompileSynchronously = true)]
        struct SkyFloorJob : IJobParallelFor
        {
            public WorldLayout layout;
            [ReadOnly] public NativeArray<ushort> blocks, walls;
            [ReadOnly] public NativeArray<TileProps> props;
            [WriteOnly] public NativeArray<int> skyFloor;

            public void Execute(int x)
            {
                int floor = -1;
                for (int y = layout.Height - 1; y >= 0; y--)
                {
                    int i = layout.Index(x, y);
                    if (walls[i] != 0 || (blocks[i] != 0 && props[blocks[i]].solid != 0)) { floor = y; break; }
                }
                skyFloor[x] = floor;
            }
        }

        [BurstCompile(CompileSynchronously = true)]
        struct PropagateJob : IJob
        {
            public WorldLayout layout;
            public int ox, oy, w, h;
            [ReadOnly] public NativeArray<ushort> blocks, walls;
            [ReadOnly] public NativeArray<byte> liquid, liquidKind;
            [ReadOnly] public NativeArray<TileProps> props;
            [ReadOnly] public NativeArray<int> skyFloor;
            public float3 sky, lava, deep;
            public int deepLine;
            public NativeArray<float3> light;
            public NativeArray<float> decay;
            public NativeArray<Color32> pixels;
            public NativeList<int4> emitters;

            public void Execute()
            {
                for (int ry = 0; ry < h; ry++)
                for (int rx = 0; rx < w; rx++)
                {
                    int x = ox + rx, y = oy + ry, r = ry * w + rx;
                    if (!layout.InBounds(x, y))
                    {
                        bool above = y >= layout.Height;
                        light[r] = above ? sky : float3.zero;
                        decay[r] = above ? AirDecay : SolidDecay;
                        continue;
                    }
                    int i = layout.Index(x, y);
                    ushort b = blocks[i];
                    float3 l = float3.zero;
                    float d = AirDecay;
                    if (b != 0)
                    {
                        var p = props[b];
                        if (p.solid != 0) d = SolidDecay;
                        if (math.cmax(p.light) > 0f)
                        {
                            l = p.light;
                            if (p.lightRadius >= 5f)
                            {
                                int3 c = (int3)math.round(math.saturate(p.light) * 255f);
                                emitters.Add(new int4(x, y, (c.x << 16) | (c.y << 8) | c.z, (int)(p.lightRadius * 10f)));
                            }
                        }
                    }
                    if (liquid[i] > 0)
                    {
                        if (liquidKind[i] == (byte)LiquidType.Lava)
                        {
                            l = math.max(l, lava * (0.5f + 0.5f * liquid[i] / 255f));
                            if (((x ^ y) & 3) == 0)
                            {
                                int3 c = (int3)math.round(math.saturate(lava) * 255f);
                                emitters.Add(new int4(x, y, (c.x << 16) | (c.y << 8) | c.z, 80));
                            }
                        }
                        else d = math.min(d, WaterDecay);
                    }
                    if (y > skyFloor[x]) l = math.max(l, sky);
                    // the Emberdeep smoulders: open cells near the world floor carry a faint red ambient
                    if (y < deepLine && b == 0) l = math.max(l, deep * math.saturate((deepLine - y) / 24f + 0.35f));
                    light[r] = l;
                    decay[r] = d;
                }

                for (int iter = 0; iter < 2; iter++)
                {
                    for (int ry = 0; ry < h; ry++)
                    {
                        int row = ry * w;
                        for (int rx = 1; rx < w; rx++) Relax(row + rx, row + rx - 1);
                        for (int rx = w - 2; rx >= 0; rx--) Relax(row + rx, row + rx + 1);
                    }
                    for (int rx = 0; rx < w; rx++)
                    {
                        for (int ry = 1; ry < h; ry++) Relax(ry * w + rx, (ry - 1) * w + rx);
                        for (int ry = h - 2; ry >= 0; ry--) Relax(ry * w + rx, (ry + 1) * w + rx);
                    }
                }

                for (int r = 0; r < light.Length; r++)
                {
                    // perceptual curve: dim light reads a little brighter, darkness stays black
                    float3 v = math.saturate(light[r]);
                    v = math.sqrt(v) * 0.55f + v * 0.45f;
                    int3 c = (int3)math.round(v * 255f);
                    pixels[r] = new Color32((byte)c.x, (byte)c.y, (byte)c.z, 255);
                }
            }

            void Relax(int r, int from)
            {
                light[r] = math.max(light[r], light[from] * decay[r]);
            }
        }
    }

    /// <summary>Normal-map settings on Light2D have no public setters; they are serialized fields.</summary>
    public static class Light2DSettings
    {
        static readonly System.Reflection.FieldInfo Quality =
            typeof(Light2D).GetField("m_NormalMapQuality", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        static readonly System.Reflection.FieldInfo Distance =
            typeof(Light2D).GetField("m_NormalMapDistance", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

        public static void EnableNormalMaps(Light2D light, float distance)
        {
            Quality?.SetValue(light, Light2D.NormalMapQuality.Accurate);
            Distance?.SetValue(light, distance);
        }

        public static bool Available => Quality != null && Distance != null;
    }
}
