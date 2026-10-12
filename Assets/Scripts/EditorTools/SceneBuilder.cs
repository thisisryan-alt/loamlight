using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Loamlight.Core;
using Loamlight.Game.Demo;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace Loamlight.EditorTools
{
    /// <summary>
    /// Builds every scene from code. Scenes are outputs: change this file, never the .unity YAML.
    /// Unity gives new objects random local file IDs, so a rebuilt scene always differs byte-wise. To keep
    /// setup idempotent a scene is only rebuilt when its inputs (all scripts + sheet sidecars) changed, tracked
    /// by a fingerprint file Unity ignores (leading dot). Pass -llForceScenes to rebuild regardless.
    /// </summary>
    public static class SceneBuilder
    {
        public const string SceneFolder = "Assets/Scenes";
        public const string PipelineDemoPath = SceneFolder + "/PipelineDemo.unity";

        public static readonly Color TorchColor = new Color(1f, 0.70f, 0.40f);
        public static readonly Color AmbientColor = new Color(0.55f, 0.62f, 0.85f);

        [MenuItem("Loamlight/Setup/Build Scenes")]
        public static void BuildAll()
        {
            AssetFolders.Ensure(SceneFolder);
            BuildIfChanged(PipelineDemoPath, BuildPipelineDemo);
            var scenes = new[] { new EditorBuildSettingsScene(PipelineDemoPath, true) };
            if (!EditorBuildSettings.scenes.Select(s => s.path).SequenceEqual(scenes.Select(s => s.path)))
                EditorBuildSettings.scenes = scenes;
            Debug.Log("[SceneBuilder] scenes built: " + string.Join(", ", scenes.Select(s => s.path)));
        }

        static void BuildIfChanged(string scenePath, System.Action build)
        {
            var fingerprintPath = Path.Combine(Path.GetDirectoryName(scenePath), "." + Path.GetFileNameWithoutExtension(scenePath) + ".fingerprint");
            var fingerprint = InputsFingerprint();
            bool force = LaunchArgs.Has("-llForceScenes");
            if (!force && File.Exists(scenePath) && File.Exists(fingerprintPath) && File.ReadAllText(fingerprintPath).Trim() == fingerprint)
            {
                Debug.Log($"[SceneBuilder] {scenePath} up to date");
                return;
            }
            build();
            File.WriteAllText(fingerprintPath, fingerprint + "\n");
        }

        /// <summary>Hash of everything a generated scene depends on (line endings normalised).</summary>
        public static string InputsFingerprint()
        {
            var files = Directory.GetFiles("Assets/Scripts", "*.cs", SearchOption.AllDirectories)
                .Concat(Directory.GetFiles(ArtImport.SpriteRoot, "*.sheet.json"))
                .Select(f => f.Replace('\\', '/')).OrderBy(f => f, System.StringComparer.Ordinal);
            using var sha = SHA256.Create();
            var sb = new StringBuilder(Application.unityVersion);
            foreach (var f in files)
                sb.Append('\n').Append(f).Append('\n').Append(File.ReadAllText(f).Replace("\r\n", "\n"));
            return System.BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(sb.ToString()))).Replace("-", "").Substring(0, 32);
        }

        // ------------------------------------------------------------------------------------------ demo
        static void BuildPipelineDemo()
        {
            // Create the scene first: NewScene(Single) unloads assets loaded before it.
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            var wall = LoadSprite("stone_wall", "stone_wall_0");
            var calib = LoadSprite("calib", "calib_still_0");
            var calibFlat = LoadSprite("calib_flat", "calib_flat_still_0");
            var minerSet = AssetDatabase.LoadAssetAtPath<SpriteAnimationSet>(ArtImport.SpriteRoot + "/miner.anim.asset");
            if (minerSet == null) throw new System.IO.FileNotFoundException("miner.anim.asset missing; run the art import first");

            var cam = BuildCamera(new Vector3(0f, -1.5f, -10f), 480, 270);
            NewLight("Global Light (ambient)", Light2D.LightType.Global, AmbientColor, 0.16f, Vector3.zero);

            // back wall and floor, both normal-mapped tiles
            Tiled("Back Wall", wall, new Vector2(48, 26), new Vector3(0, -1.5f, 0), "Walls", new Color(0.62f, 0.60f, 0.68f));
            Tiled("Floor", wall, new Vector2(48, 2), new Vector3(0, -7.5f, 0), "World", Color.white);

            // subjects: the same sphere with / without the normal map, and flipped by scale (must light identically)
            var subjects = new (string name, Sprite sprite, Vector3 pos, bool flip)[]
            {
                ("Sphere (normal map)", calib, new Vector3(-8f, 2.5f, 0f), false),
                ("Sphere (no normal map)", calibFlat, new Vector3(0f, 2.5f, 0f), false),
                ("Sphere (normal map, scale.x = -1)", calib, new Vector3(8f, 2.5f, 0f), true),
            };
            var orbitCenters = subjects.Select(s => (Vector2)s.pos).ToList();
            foreach (var s in subjects)
            {
                var sr = NewSprite(s.name, s.sprite, s.pos, "Entities");
                if (s.flip) sr.transform.localScale = new Vector3(-1f, 1f, 1f);
            }

            var walker = NewSprite("Miner (walk)", minerSet.Find("walk").frames[0], new Vector3(-4f, -6.5f, 0f), "Entities");
            walker.gameObject.AddComponent<SpriteAnimator>().Configure(minerSet, "walk");
            var swinger = NewSprite("Miner (use, scale.x = -1)", minerSet.Find("use").frames[0], new Vector3(4f, -6.5f, 0f), "Entities");
            swinger.transform.localScale = new Vector3(-1f, 1f, 1f);
            swinger.gameObject.AddComponent<SpriteAnimator>().Configure(minerSet, "use", 0.6f, loopAlways: true);
            orbitCenters.Add(new Vector2(-4f, -5.1f));
            orbitCenters.Add(new Vector2(4f, -5.1f));

            // one torch per subject, orbiting in sync (see PipelineDemoDirector)
            var lights = orbitCenters.Select((c, i) =>
                NewLight($"Torch {i}", Light2D.LightType.Point, TorchColor, 1.5f, new Vector3(c.x, c.y, 0f),
                         outerRadius: 3.4f, innerRadius: 0.15f, falloff: 0.6f, normalMaps: true).transform).ToArray();

            // a cool accent light far left (stand-in for a glowing ore vein) to show colored mixing on the wall
            NewLight("Accent (teal)", Light2D.LightType.Point, new Color(0.25f, 0.95f, 0.85f), 1.2f, new Vector3(-15f, -4f, 0f),
                     outerRadius: 6f, innerRadius: 0.3f, falloff: 0.7f, normalMaps: true);

            var labels = BuildWorldCanvas(cam);
            Label(labels, "normal map", new Vector2(-8f, 0.6f));
            Label(labels, "no normal map (control)", new Vector2(0f, 0.6f));
            Label(labels, "normal map, flipped by scale", new Vector2(8f, 0.6f));
            Label(labels, "walk (normal map)", new Vector2(-4f, -9f));
            Label(labels, "use, flipped by scale", new Vector2(4f, -9f));
            var caption = Label(labels, "Loamlight M0 pipeline demo", new Vector2(0f, 6.4f), 22);

            var director = new GameObject("Pipeline Demo Director").AddComponent<PipelineDemoDirector>();
            director.Configure(lights, orbitCenters.ToArray(), 1.9f, caption);

            EditorSceneManager.SaveScene(scene, PipelineDemoPath);
        }

        // ------------------------------------------------------------------------------------------ helpers
        static Sprite LoadSprite(string sheet, string spriteName)
        {
            var path = $"{ArtImport.SpriteRoot}/{sheet}.png";
            var sprite = AssetDatabase.LoadAllAssetsAtPath(path).OfType<Sprite>().FirstOrDefault(s => s.name == spriteName);
            return sprite != null ? sprite : throw new System.IO.FileNotFoundException($"sprite {spriteName} not found in {path}");
        }

        static Camera BuildCamera(Vector3 position, int refX, int refY)
        {
            var go = new GameObject("Main Camera") { tag = "MainCamera" };
            go.transform.position = position;
            var cam = go.AddComponent<Camera>();
            cam.orthographic = true;
            cam.orthographicSize = refY / 16f / 2f;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.015f, 0.015f, 0.025f, 1f);
            cam.nearClipPlane = 0.1f;
            cam.farClipPlane = 50f;
            go.AddComponent<UniversalAdditionalCameraData>();
            var ppc = go.AddComponent<PixelPerfectCamera>();
            ppc.assetsPPU = 16;
            ppc.refResolutionX = refX;
            ppc.refResolutionY = refY;
            ppc.gridSnapping = PixelPerfectCamera.GridSnapping.None;
            ppc.cropFrame = PixelPerfectCamera.CropFrame.None;
            return cam;
        }

        static SpriteRenderer NewSprite(string name, Sprite sprite, Vector3 pos, string sortingLayer)
        {
            var go = new GameObject(name);
            go.transform.position = pos;
            var sr = go.AddComponent<SpriteRenderer>();
            sr.sprite = sprite;
            sr.sortingLayerName = sortingLayer;
            return sr;
        }

        static void Tiled(string name, Sprite sprite, Vector2 size, Vector3 pos, string sortingLayer, Color tint)
        {
            var sr = NewSprite(name, sprite, pos, sortingLayer);
            sr.drawMode = SpriteDrawMode.Tiled;
            sr.tileMode = SpriteTileMode.Continuous;
            sr.size = size;
            sr.color = tint;
        }

        public static Light2D NewLight(string name, Light2D.LightType type, Color color, float intensity, Vector3 pos,
            float outerRadius = 4f, float innerRadius = 0.2f, float falloff = 0.5f, bool normalMaps = false)
        {
            var go = new GameObject(name);
            go.transform.position = pos;
            var light = go.AddComponent<Light2D>();
            light.lightType = type;
            light.color = color;
            light.intensity = intensity;
            light.targetSortingLayers = SortingLayer.layers.Select(l => l.id).ToArray();
            if (type == Light2D.LightType.Point)
            {
                light.pointLightOuterRadius = outerRadius;
                light.pointLightInnerRadius = innerRadius;
                light.falloffIntensity = falloff;
            }
            // Normal-map settings have no public setters; they default to Disabled.
            var so = new SerializedObject(light);
            so.FindProperty("m_NormalMapQuality").intValue =
                (int)(normalMaps ? Light2D.NormalMapQuality.Accurate : Light2D.NormalMapQuality.Disabled);
            so.FindProperty("m_NormalMapDistance").floatValue = 1.2f;
            so.ApplyModifiedPropertiesWithoutUndo();
            return light;
        }

        static RectTransform BuildWorldCanvas(Camera cam)
        {
            var go = new GameObject("Labels (world canvas)");
            var canvas = go.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            canvas.worldCamera = cam;
            canvas.sortingLayerName = "Overlay";
            var rt = (RectTransform)go.transform;
            rt.position = Vector3.zero;
            rt.sizeDelta = new Vector2(10, 10);
            return rt;
        }

        static Text Label(RectTransform canvas, string text, Vector2 worldPos, int fontSize = 14)
        {
            var go = new GameObject("Label: " + text);
            var rt = go.AddComponent<RectTransform>();
            rt.SetParent(canvas, false);
            rt.position = new Vector3(worldPos.x, worldPos.y, 0f);
            rt.localScale = Vector3.one / 32f;                 // 32 canvas units per world unit
            rt.sizeDelta = new Vector2(480, 40);
            var t = go.AddComponent<Text>();
            t.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            t.fontSize = fontSize;
            t.alignment = TextAnchor.MiddleCenter;
            t.color = new Color(0.85f, 0.85f, 0.9f);
            t.text = text;
            t.raycastTarget = false;
            return t;
        }
    }
}
