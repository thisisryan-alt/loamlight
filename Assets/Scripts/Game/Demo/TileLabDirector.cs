using System.Collections;
using Loamlight.Content;
using Loamlight.Core;
using Loamlight.Lighting;
using Loamlight.World;
using Loamlight.World.Rendering;
using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace Loamlight.Game.Demo
{
    /// <summary>
    /// Rendering lab for the world pipeline (M1 spikes), verified from pixels on every capture:
    ///  1. a chunk-mesh tile object (the calibration sphere drawn by the mesher) shades like the same sphere as a
    ///     SpriteRenderer under identical normal-mapped point lights;
    ///  2. the light-map cookie Sprite Light2D carries colour (red left / blue right) and leaves unlit tiles black;
    ///  3. the cookie is aligned to tiles (a single lit texel peaks on its own tile, symmetric neighbours);
    ///  4. real propagation: sky + torches, cave interior dark.
    /// </summary>
    public sealed class TileLabDirector : MonoBehaviour, ICaptureDirector, ICaptureVerifier
    {
        [SerializeField] Camera cam;
        [SerializeField] SpriteRenderer spriteSphere;
        [SerializeField] Light2D meshLight, spriteLight;

        public const int W = 96, H = 64;
        public static readonly Vector2Int MeshSphereOrigin = new Vector2Int(36, 30);   // bottom-left tile of the 4x4 object
        public static readonly Vector2 SpriteSphereCenter = new Vector2(58f, 32f);
        static readonly Vector2Int AlignTile = new Vector2Int(47, 25);

        WorldData _world;
        WorldRenderData _render;
        ChunkRenderer _chunks;
        LightMapSystem _lightMap;
        float _angle = 135f;
        string _mode = "normals";

        public void Configure(Camera camera, SpriteRenderer sphere, Light2D lightForMesh, Light2D lightForSprite)
        {
            cam = camera;
            spriteSphere = sphere;
            meshLight = lightForMesh;
            spriteLight = lightForSprite;
        }

        void Start()
        {
            var db = ContentDatabase.Instance;
            _render = new WorldRenderData(db, WorldAtlas.Load());
            _world = BuildWorld(db);
            _chunks = new GameObject("Chunks").AddComponent<ChunkRenderer>();
            _chunks.Init(_world, _render, cam);
            _lightMap = new GameObject("Light Map").AddComponent<LightMapSystem>();
            _lightMap.Init(_world, _render.TileProps, cam);
            SetMode("normals");
            _chunks.BuildAllVisibleNow();
        }

        void OnDestroy()
        {
            _world?.Dispose();
            _render?.Dispose();
        }

        static WorldData BuildWorld(ContentDatabase db)
        {
            var w = new WorldData(W, H, 1);
            ushort stone = db.TileId("stone"), dirt = db.TileId("dirt"), grass = db.TileId("grass"), torch = db.TileId("torch");
            ushort calib = db.TileId("debug_calib"), planks = db.TileId("wood_planks"), bricks = db.TileId("stone_bricks");
            ushort stoneWall = db.WallId("stone_wall"), dirtWall = db.WallId("dirt_wall");
            for (int x = 0; x < W; x++)
            for (int y = 0; y < H; y++)
            {
                if (y < 20) w.SetBlock(x, y, y > 16 ? dirt : stone);
                if (y == 20 && x > 4 && x < 30) w.SetBlock(x, y, grass);
                if (y < 18 && y > 2) w.SetWall(x, y, y > 14 ? dirtWall : stoneWall);
                // backdrop wall behind the comparison area so both spheres sit on the same background
                if (y >= 22 && y < 40 && x >= 32 && x < 64) w.SetWall(x, y, stoneWall);
            }
            // a cave under the grass with one torch
            for (int x = 8; x < 26; x++)
            for (int y = 6; y < 13; y++)
                if ((x - 17f) * (x - 17f) / 81f + (y - 9.5f) * (y - 9.5f) / 12f < 1f) w.SetBlock(x, y, 0);
            w.SetBlock(12, 7, torch);
            // shapes: half block and both slopes on the surface
            w.SetBlock(66, 20, stone, TileShape.HalfBottom);
            w.SetBlock(68, 20, stone, TileShape.SlopeUpRight);
            w.SetBlock(70, 20, stone, TileShape.SlopeUpLeft);
            w.SetBlock(72, 20, planks);
            w.SetBlock(73, 20, bricks);
            // the 4x4 calibration object, cell index = cx + cy * 4 (cy from the bottom)
            for (int cy = 0; cy < 4; cy++)
            for (int cx = 0; cx < 4; cx++)
                w.SetBlock(MeshSphereOrigin.x + cx, MeshSphereOrigin.y + cy, calib, TileShape.Full, (byte)(cx + cy * 4));
            return w;
        }

        void SetMode(string mode)
        {
            _mode = mode;
            bool normals = mode == "normals";
            meshLight.enabled = normals;
            spriteLight.enabled = normals;
            _lightMap.highlightLights = normals ? 0 : 24;
            _lightMap.debugPattern = mode switch
            {
                "normals" => (x, y) => Color.black,
                "cookie_color" => (x, y) => y < 30 ? (x < 48 ? new Color(1f, 0.1f, 0.1f) : new Color(0.1f, 0.2f, 1f)) : Color.black,
                "cookie_align" => (x, y) => x == AlignTile.x && y == AlignTile.y ? Color.white : Color.black,
                _ => null,
            };
            _lightMap.Tick();
        }

        void Update()
        {
            if (_mode != "normals") return;
            Orbit(_angle + 45f * Time.deltaTime);
        }

        void Orbit(float angle)
        {
            _angle = Mathf.Repeat(angle, 360f);
            var d = new Vector3(Mathf.Cos(_angle * Mathf.Deg2Rad), Mathf.Sin(_angle * Mathf.Deg2Rad), 0f) * 2.2f;
            meshLight.transform.position = (Vector3)MeshCenter + d;
            spriteLight.transform.position = (Vector3)SpriteSphereCenter + d;
        }

        static Vector2 MeshCenter => new Vector2(MeshSphereOrigin.x + 2f, MeshSphereOrigin.y + 2f);

        public IEnumerator CaptureScript()
        {
            yield return new CaptureShot("burst_" + (Loamlight.World.Generation.BurstProbe.IsBurstCompiled() ? "on" : "OFF"));
            foreach (var angle in new[] { 135f, 300f })
            {
                SetMode("normals");
                Orbit(angle);
                yield return null;
                yield return null;
                yield return new CaptureShot($"normals_{angle:0}");
            }
            SetMode("cookie_color");
            yield return null;
            yield return new CaptureShot("cookie_color");
            SetMode("cookie_align");
            yield return null;
            yield return new CaptureShot("cookie_align");
            SetMode("propagate");
            _lightMap.skyIntensity = 1f;
            yield return null;
            yield return new CaptureShot("propagate");
        }

        public bool VerifyShot(string shotName, Texture2D frame, out string detail)
        {
            var px = frame.GetPixels32();
            Color32 At(Vector2 world)
            {
                var s = cam.WorldToScreenPoint(world);
                int x = Mathf.Clamp(Mathf.FloorToInt(s.x), 0, frame.width - 1), y = Mathf.Clamp(Mathf.FloorToInt(s.y), 0, frame.height - 1);
                return px[y * frame.width + x];
            }
            float L(Color32 c) => 0.2126f * c.r + 0.7152f * c.g + 0.0722f * c.b;

            if (shotName.StartsWith("burst_"))
            {
                bool on = shotName == "burst_on";
                detail = on ? "jobs run Burst-compiled in this player" : "Burst is NOT compiling jobs in this player";
                return on;
            }
            if (shotName.StartsWith("normals_"))
            {
                // the mesh sphere and the sprite sphere must shade alike, and both must be lit on the light's side
                var dir = new Vector2(Mathf.Cos(_angle * Mathf.Deg2Rad), Mathf.Sin(_angle * Mathf.Deg2Rad));
                float diff = 0f, toward = 0f, away = 0f;
                int n = 0, nt = 0, na = 0;
                for (int gy = -6; gy <= 6; gy++)
                for (int gx = -6; gx <= 6; gx++)
                {
                    var uv = new Vector2(gx, gy) / 6f * 1.0f;
                    if (uv.sqrMagnitude > 0.85f * 0.85f) continue;
                    var off = uv * (19f / 16f);
                    float a = L(At(MeshCenter + off)), b = L(At(SpriteSphereCenter + off));
                    diff += Mathf.Abs(a - b);
                    n++;
                    if (uv.magnitude < 0.25f) continue;
                    float f = Vector2.Dot(uv.normalized, dir);
                    if (f > 0.5f) { toward += a; nt++; } else if (f < -0.5f) { away += a; na++; }
                }
                diff /= n;
                float contrast = toward / Mathf.Max(1, nt) - away / Mathf.Max(1, na);
                detail = $"mesh vs sprite sphere mean diff {diff:0.0} (<6), mesh lit-side contrast {contrast:0.0} (>12)";
                return diff < 6f && contrast > 12f;
            }
            if (shotName == "cookie_color")
            {
                // stone/dirt under the red half and the blue half; sky region (pattern black) must be black
                var red = At(new Vector2(20.5f, 18.5f));
                var blue = At(new Vector2(80.5f, 18.5f));
                var dark = At(new Vector2(50.5f, 35.5f));      // a wall tile where the pattern is black
                // the tiles are brown, so blue light reads as "more blue than red", not a saturated blue
                bool ok = red.r > red.b * 2 + 10 && blue.b > blue.r + 8 && L(dark) < 4f;
                detail = $"red-lit tile {red}, blue-lit tile {blue}, unlit {dark}";
                return ok;
            }
            if (shotName == "cookie_align")
            {
                float c = L(At(AlignTile + new Vector2(0.5f, 0.5f)));
                float l = L(At(AlignTile + new Vector2(-0.5f, 0.5f))), r = L(At(AlignTile + new Vector2(1.5f, 0.5f)));
                float d = L(At(AlignTile + new Vector2(0.5f, -0.5f))), u = L(At(AlignTile + new Vector2(0.5f, 1.5f)));
                bool ok = c > l + 8 && c > r + 8 && c > d + 8 && Mathf.Abs(l - r) < 6 && Mathf.Abs(d - u) < 20;
                detail = $"lit tile {c:0}, left {l:0}, right {r:0}, below {d:0}, above {u:0}";
                return ok;
            }
            if (shotName == "propagate")
            {
                float surface = L(At(new Vector2(85.5f, 19.5f)));      // top block under open sky
                float torchSide = L(At(new Vector2(14.5f, 8.5f)));     // next to the torch in the cave
                float deep = L(At(new Vector2(80.5f, 4.5f)));          // solid rock far from any light
                detail = $"sky-lit {surface:0}, torch-lit cave {torchSide:0}, deep rock {deep:0}";
                return surface > 40 && torchSide > 25 && deep < 6;
            }
            detail = "no check";
            return true;
        }
    }
}
