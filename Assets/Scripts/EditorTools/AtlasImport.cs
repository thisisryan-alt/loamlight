using System;
using System.IO;
using System.Linq;
using Loamlight.Core;
using Loamlight.World.Rendering;
using UnityEditor;
using UnityEngine;

namespace Loamlight.EditorTools
{
    /// <summary>
    /// Imports the generated tile atlas (Tools/art/gen_atlas.py): texture settings for albedo / normal / emission,
    /// the "Loamlight/Tile Lit" material, and the <see cref="WorldAtlas"/> asset (layout + UV rects) in Resources.
    /// </summary>
    public static class AtlasImport
    {
        public const string AtlasFolder = "Assets/Art/Generated/Atlas";
        public const string MaterialPath = "Assets/Generated/Rendering/TileAtlas.mat";
        public const string AssetPath = "Assets/Resources/World/WorldAtlas.asset";
        public const string ShaderName = "Loamlight/Tile Lit";


        [MenuItem("Loamlight/Setup/Import Atlas")]
        public static void ImportAll()
        {
            var jsonPath = $"{AtlasFolder}/tiles.atlas.json";
            if (!File.Exists(jsonPath)) throw new FileNotFoundException("run Tools/build_art.ps1 first", jsonPath);
            var json = File.ReadAllText(jsonPath);
            var root = MiniJson.Parse(json) as System.Collections.Generic.Dictionary<string, object>
                       ?? throw new InvalidDataException("atlas json root");

            var albedo = ConfigureTexture($"{AtlasFolder}/tiles.png", TextureImporterType.Default, true);
            var normal = ConfigureTexture($"{AtlasFolder}/tiles_n.png", TextureImporterType.NormalMap, false);
            var emission = ConfigureTexture($"{AtlasFolder}/tiles_e.png", TextureImporterType.Default, true);

            AssetFolders.Ensure(Path.GetDirectoryName(MaterialPath).Replace('\\', '/'));
            var shader = Shader.Find(ShaderName) ?? throw new InvalidOperationException($"shader {ShaderName} not found");
            var mat = AssetDatabase.LoadAssetAtPath<Material>(MaterialPath);
            if (mat == null)
            {
                mat = new Material(shader) { name = "TileAtlas" };
                AssetDatabase.CreateAsset(mat, MaterialPath);
            }
            if (mat.shader != shader) mat.shader = shader;
            SetTex(mat, "_MainTex", albedo);
            SetTex(mat, "_NormalMap", normal);
            SetTex(mat, "_EmissionMap", emission);
            if (!Mathf.Approximately(mat.GetFloat("_EmissionStrength"), 1.6f)) mat.SetFloat("_EmissionStrength", 1.6f);

            int width = Convert.ToInt32(root["width"]), height = Convert.ToInt32(root["height"]), cell = Convert.ToInt32(root["cell"]);
            var frames = ((System.Collections.Generic.List<object>)root["frames"]).Cast<System.Collections.Generic.List<object>>().ToArray();
            var uvs = new Vector4[frames.Length];
            for (int i = 0; i < frames.Length; i++)
            {
                float x = Convert.ToSingle(frames[i][0]), yTop = Convert.ToSingle(frames[i][1]);
                // image rows grow downward; UV v grows upward
                float vMin = (height - yTop - cell) / height, vMax = (height - yTop) / height;
                uvs[i] = new Vector4(x / width, vMin, (x + cell) / width, vMax);
            }

            AssetFolders.Ensure(Path.GetDirectoryName(AssetPath).Replace('\\', '/'));
            var atlas = AssetDatabase.LoadAssetAtPath<WorldAtlas>(AssetPath);
            bool created = atlas == null;
            if (created) atlas = ScriptableObject.CreateInstance<WorldAtlas>();
            var before = created ? null : EditorJsonUtility.ToJson(atlas);
            atlas.material = mat;
            atlas.albedo = albedo; atlas.normal = normal; atlas.emission = emission;
            atlas.width = width; atlas.height = height;
            atlas.tiles = Entries(root["tiles"]);
            atlas.walls = Entries(root["walls"]);
            atlas.uvRects = uvs;
            if (created) AssetDatabase.CreateAsset(atlas, AssetPath);
            else if (EditorJsonUtility.ToJson(atlas) != before) EditorUtility.SetDirty(atlas);
            AssetDatabase.SaveAssets();
            Debug.Log($"[AtlasImport] {width}x{height}, {uvs.Length} frames, {atlas.tiles.Length} tiles, {atlas.walls.Length} walls");
        }

        static AtlasEntry[] Entries(object node)
        {
            var dict = (System.Collections.Generic.Dictionary<string, object>)node;
            return dict.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv =>
            {
                var e = (System.Collections.Generic.Dictionary<string, object>)kv.Value;
                int Get(string k, int d) => e.TryGetValue(k, out var v) ? Convert.ToInt32(v) : d;
                var layout = (string)e["layout"] switch
                {
                    "terrain" => AtlasLayout.Terrain, "single" => AtlasLayout.Single, "object" => AtlasLayout.Object,
                    "variants" => AtlasLayout.Variants, _ => AtlasLayout.None,
                };
                return new AtlasEntry
                {
                    id = kv.Key, layout = layout, start = Get("start", 0), count = Get("count", 1), w = Get("w", 1), h = Get("h", 1),
                    variants = Get("variants", 1),
                };
            }).ToArray();
        }

        static void SetTex(Material m, string prop, Texture2D t)
        {
            if (m.GetTexture(prop) != t) m.SetTexture(prop, t);
        }

        static Texture2D ConfigureTexture(string path, TextureImporterType type, bool srgb)
        {
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
            var imp = (TextureImporter)AssetImporter.GetAtPath(path);
            imp.textureType = type;
            imp.convertToNormalmap = false;
            imp.sRGBTexture = srgb && type != TextureImporterType.NormalMap;
            imp.filterMode = FilterMode.Point;
            imp.mipmapEnabled = false;
            imp.textureCompression = TextureImporterCompression.Uncompressed;
            imp.wrapMode = TextureWrapMode.Clamp;
            imp.npotScale = TextureImporterNPOTScale.None;
            imp.alphaIsTransparency = type == TextureImporterType.Default && srgb;
            imp.maxTextureSize = 4096;
            imp.anisoLevel = 0;
            imp.SaveAndReimport();
            return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        }
    }
}
