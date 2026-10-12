using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Loamlight.Core;
using UnityEditor;
using UnityEditor.U2D.Sprites;
using UnityEngine;

namespace Loamlight.EditorTools
{
    /// <summary>
    /// Imports generated sprite sheets (Tools/build_art.ps1 output) from their JSON sidecars:
    /// point filtering, no compression, no mips, the sheet's PPU, slicing with stable sprite IDs, the normal
    /// map assigned as the sprite's _NormalMap secondary texture, and a SpriteAnimationSet asset per sheet.
    /// </summary>
    public static class ArtImport
    {
        public const string SpriteRoot = "Assets/Art/Generated/Sprites";
        public const string NormalMapProperty = "_NormalMap";

#pragma warning disable 0649 // assigned by JsonUtility
        [Serializable] public sealed class SheetJson
        {
            public int version;
            public string name;
            public int frameWidth, frameHeight, columns, rows, pixelsPerUnit;
            public float[] pivot;
            public string meshType;
            public string normalMap;
            public AnimJson[] animations;
            public FrameJson[] frames;
        }
        [Serializable] public sealed class AnimJson { public string name; public float fps; public bool loop; public int[] frames; }
        [Serializable] public sealed class FrameJson { public string name; public int x, y, w, h; }
#pragma warning restore 0649

        public static IEnumerable<string> SidecarPaths() =>
            Directory.Exists(SpriteRoot)
                ? Directory.GetFiles(SpriteRoot, "*.sheet.json").Select(p => p.Replace('\\', '/')).OrderBy(p => p, StringComparer.Ordinal)
                : Enumerable.Empty<string>();

        public static SheetJson ReadSidecar(string path) => JsonUtility.FromJson<SheetJson>(File.ReadAllText(path));

        public static string AnimationSetPath(SheetJson sheet) => $"{SpriteRoot}/{sheet.name}.anim.asset";

        [MenuItem("Loamlight/Setup/Import Generated Art")]
        public static void ImportAll()
        {
            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
            int count = 0;
            foreach (var path in SidecarPaths())
            {
                ImportSheet(path);
                count++;
            }
            BuildSheetIndex();
            AssetDatabase.SaveAssets();
            Debug.Log($"[ArtImport] {count} sheets imported from {SpriteRoot}");
        }

        public const string SheetIndexPath = "Assets/Resources/Sprites/SpriteSheetIndex.asset";

        /// <summary>Resources index of every animation set, so the player build includes them all.</summary>
        static void BuildSheetIndex()
        {
            var sets = SidecarPaths().Select(p => AssetDatabase.LoadAssetAtPath<SpriteAnimationSet>(AnimationSetPath(ReadSidecar(p))))
                .Where(s => s != null).ToArray();
            AssetFolders.Ensure("Assets/Resources/Sprites");
            var idx = AssetDatabase.LoadAssetAtPath<SpriteSheetIndex>(SheetIndexPath);
            bool created = idx == null;
            if (created) idx = ScriptableObject.CreateInstance<SpriteSheetIndex>();
            var before = created ? null : EditorJsonUtility.ToJson(idx);
            idx.sheets = sets;
            if (created) AssetDatabase.CreateAsset(idx, SheetIndexPath);
            else if (EditorJsonUtility.ToJson(idx) != before) EditorUtility.SetDirty(idx);
        }

        public static void ImportSheet(string sidecarPath)
        {
            var sheet = ReadSidecar(sidecarPath);
            if (sheet == null || sheet.version != 1 || string.IsNullOrEmpty(sheet.name))
                throw new InvalidDataException($"{sidecarPath}: unsupported or empty sheet sidecar");
            var dir = Path.GetDirectoryName(sidecarPath).Replace('\\', '/');

            Texture2D normal = null;
            if (!string.IsNullOrEmpty(sheet.normalMap))
            {
                var normalPath = $"{dir}/{sheet.normalMap}";
                ConfigureNormalMap(normalPath);
                normal = AssetDatabase.LoadAssetAtPath<Texture2D>(normalPath)
                         ?? throw new FileNotFoundException("normal map did not import", normalPath);
            }

            var albedoPath = $"{dir}/{sheet.name}.png";
            ConfigureSheet(albedoPath, sheet, normal);
            BuildAnimationSet(albedoPath, sheet);
        }

        static void ApplyCommonPixelArtSettings(TextureImporter imp, int maxSize)
        {
            imp.filterMode = FilterMode.Point;
            imp.mipmapEnabled = false;
            imp.textureCompression = TextureImporterCompression.Uncompressed;
            imp.wrapMode = TextureWrapMode.Clamp;
            imp.npotScale = TextureImporterNPOTScale.None;
            imp.maxTextureSize = Mathf.Max(32, Mathf.NextPowerOfTwo(maxSize));
            imp.isReadable = false;
            imp.anisoLevel = 0;
        }

        static void ConfigureNormalMap(string path)
        {
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
            var imp = AssetImporter.GetAtPath(path) as TextureImporter
                      ?? throw new FileNotFoundException("missing normal map", path);
            imp.textureType = TextureImporterType.NormalMap;
            imp.convertToNormalmap = false;   // already a tangent-space normal map, not a height map
            imp.sRGBTexture = false;
            ApplyCommonPixelArtSettings(imp, SizeOf(path));
            imp.SaveAndReimport();
        }

        static void ConfigureSheet(string path, SheetJson sheet, Texture2D normal)
        {
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
            var imp = AssetImporter.GetAtPath(path) as TextureImporter
                      ?? throw new FileNotFoundException("missing sheet", path);
            imp.textureType = TextureImporterType.Sprite;
            imp.spriteImportMode = SpriteImportMode.Multiple;
            imp.spritePixelsPerUnit = sheet.pixelsPerUnit;
            imp.sRGBTexture = true;
            imp.alphaIsTransparency = true;
            ApplyCommonPixelArtSettings(imp, SizeOf(path));

            var settings = new TextureImporterSettings();
            imp.ReadTextureSettings(settings);
            settings.spriteMeshType = sheet.meshType == "FullRect" ? SpriteMeshType.FullRect : SpriteMeshType.Tight;
            settings.spriteExtrude = 0;
            settings.spriteGenerateFallbackPhysicsShape = false;
            imp.SetTextureSettings(settings);

            var factories = new SpriteDataProviderFactories();
            factories.Init();
            var provider = factories.GetSpriteEditorDataProviderFromObject(imp);
            provider.InitSpriteEditorDataProvider();

            var pivot = new Vector2(sheet.pivot[0], sheet.pivot[1]);
            var rects = sheet.frames.Select(f => new SpriteRect
            {
                name = f.name,
                rect = new Rect(f.x, f.y, f.w, f.h),
                alignment = SpriteAlignment.Custom,
                pivot = pivot,
                border = Vector4.zero,
                spriteID = StableIds.Guid($"sprite/{sheet.name}/{f.name}"),
            }).ToArray();
            provider.SetSpriteRects(rects);

            var nameIds = provider.GetDataProvider<ISpriteNameFileIdDataProvider>();
            nameIds?.SetNameFileIdPairs(rects.Select(r => new SpriteNameFileIdPair(r.name, r.spriteID)).ToList());

            var secondary = provider.GetDataProvider<ISecondaryTextureDataProvider>();
            secondary.textures = normal != null
                ? new[] { new SecondarySpriteTexture { name = NormalMapProperty, texture = normal } }
                : Array.Empty<SecondarySpriteTexture>();

            provider.Apply();
            imp.SaveAndReimport();
        }

        static void BuildAnimationSet(string albedoPath, SheetJson sheet)
        {
            var sprites = AssetDatabase.LoadAllAssetsAtPath(albedoPath).OfType<Sprite>().ToDictionary(s => s.name);
            var setPath = AnimationSetPath(sheet);
            var set = AssetDatabase.LoadAssetAtPath<SpriteAnimationSet>(setPath);
            bool created = set == null;
            if (created) set = ScriptableObject.CreateInstance<SpriteAnimationSet>();

            set.sheetName = sheet.name;
            set.clips = sheet.animations.Select(a => new SpriteAnimationSet.Clip
            {
                name = a.name,
                fps = a.fps,
                loop = a.loop,
                frames = a.frames.Select(i =>
                {
                    var frameName = sheet.frames[i].name;
                    return sprites.TryGetValue(frameName, out var s)
                        ? s
                        : throw new InvalidDataException($"{sheet.name}: sprite '{frameName}' missing after import");
                }).ToArray(),
            }).ToArray();

            if (created) AssetDatabase.CreateAsset(set, setPath);
            else EditorUtility.SetDirty(set);
        }

        static int SizeOf(string path)
        {
            var bytes = File.ReadAllBytes(path);
            // PNG IHDR: width and height are big-endian at offsets 16 and 20
            int w = (bytes[16] << 24) | (bytes[17] << 16) | (bytes[18] << 8) | bytes[19];
            int h = (bytes[20] << 24) | (bytes[21] << 16) | (bytes[22] << 8) | bytes[23];
            return Mathf.Max(w, h);
        }
    }
}
