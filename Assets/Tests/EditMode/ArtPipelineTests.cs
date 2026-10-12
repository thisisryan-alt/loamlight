using System.Linq;
using Loamlight.Core;
using Loamlight.EditorTools;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.U2D.Sprites;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace Loamlight.Tests
{
    /// <summary>Regression guard for the generated-art pipeline: every sheet must import the way the game expects.</summary>
    public class ArtPipelineTests
    {
        static string[] Sidecars => ArtImport.SidecarPaths().ToArray();

        [Test]
        public void GeneratedSheetsExist()
        {
            Assert.That(Sidecars, Is.Not.Empty, "no *.sheet.json under " + ArtImport.SpriteRoot + " (run Tools/build_art.ps1)");
            foreach (var expected in new[] { "miner", "calib", "calib_flat", "stone_wall" })
                Assert.That(Sidecars.Any(p => p.EndsWith("/" + expected + ".sheet.json")), expected + " sheet missing");
        }

        [TestCaseSource(nameof(Sidecars))]
        public void SheetImportSettings(string sidecar)
        {
            var sheet = ArtImport.ReadSidecar(sidecar);
            var dir = System.IO.Path.GetDirectoryName(sidecar).Replace('\\', '/');
            var path = $"{dir}/{sheet.name}.png";
            var imp = (TextureImporter)AssetImporter.GetAtPath(path);
            Assert.That(imp, Is.Not.Null, path);
            Assert.That(imp.textureType, Is.EqualTo(TextureImporterType.Sprite));
            Assert.That(imp.spriteImportMode, Is.EqualTo(SpriteImportMode.Multiple));
            Assert.That(imp.filterMode, Is.EqualTo(FilterMode.Point));
            Assert.That(imp.textureCompression, Is.EqualTo(TextureImporterCompression.Uncompressed));
            Assert.That(imp.mipmapEnabled, Is.False);
            Assert.That(imp.sRGBTexture, Is.True);
            Assert.That(imp.spritePixelsPerUnit, Is.EqualTo(16f), "the whole game is 16 px per unit");
            Assert.That(imp.spritePixelsPerUnit, Is.EqualTo((float)sheet.pixelsPerUnit));
        }

        [TestCaseSource(nameof(Sidecars))]
        public void SpritesSlicedWithStableIdsAndPivots(string sidecar)
        {
            var sheet = ArtImport.ReadSidecar(sidecar);
            var dir = System.IO.Path.GetDirectoryName(sidecar).Replace('\\', '/');
            var path = $"{dir}/{sheet.name}.png";
            var sprites = AssetDatabase.LoadAllAssetsAtPath(path).OfType<Sprite>().ToDictionary(s => s.name);
            Assert.That(sprites.Count, Is.EqualTo(sheet.frames.Length));

            var factories = new SpriteDataProviderFactories();
            factories.Init();
            var provider = factories.GetSpriteEditorDataProviderFromObject(AssetImporter.GetAtPath(path));
            provider.InitSpriteEditorDataProvider();
            var rects = provider.GetSpriteRects().ToDictionary(r => r.name);

            foreach (var f in sheet.frames)
            {
                Assert.That(sprites.ContainsKey(f.name), f.name);
                var s = sprites[f.name];
                Assert.That(s.rect, Is.EqualTo(new Rect(f.x, f.y, f.w, f.h)), f.name);
                Assert.That(s.pivot.x, Is.EqualTo(sheet.pivot[0] * f.w).Within(0.01f), f.name + " pivot x (px)");
                Assert.That(s.pivot.y, Is.EqualTo(sheet.pivot[1] * f.h).Within(0.01f), f.name + " pivot y (px)");
                Assert.That(rects[f.name].spriteID, Is.EqualTo(StableIds.Guid($"sprite/{sheet.name}/{f.name}")),
                    f.name + ": sprite IDs must be derived from names so re-imports never churn .meta files");
            }
        }

        [TestCaseSource(nameof(Sidecars))]
        public void NormalMapIsSecondaryTexture(string sidecar)
        {
            var sheet = ArtImport.ReadSidecar(sidecar);
            var dir = System.IO.Path.GetDirectoryName(sidecar).Replace('\\', '/');
            var sprites = AssetDatabase.LoadAllAssetsAtPath($"{dir}/{sheet.name}.png").OfType<Sprite>().ToArray();

            if (string.IsNullOrEmpty(sheet.normalMap))
            {
                foreach (var s in sprites) Assert.That(s.GetSecondaryTextureCount(), Is.EqualTo(0), s.name);
                return;
            }

            var normalPath = $"{dir}/{sheet.normalMap}";
            var nimp = (TextureImporter)AssetImporter.GetAtPath(normalPath);
            Assert.That(nimp.textureType, Is.EqualTo(TextureImporterType.NormalMap));
            Assert.That(nimp.sRGBTexture, Is.False, "normal maps are linear data");
            Assert.That(nimp.convertToNormalmap, Is.False);
            Assert.That(nimp.filterMode, Is.EqualTo(FilterMode.Point));
            Assert.That(nimp.textureCompression, Is.EqualTo(TextureImporterCompression.Uncompressed));
            Assert.That(nimp.mipmapEnabled, Is.False);

            var normalTex = AssetDatabase.LoadAssetAtPath<Texture2D>(normalPath);
            foreach (var s in sprites)
            {
                var tex = new SecondarySpriteTexture[s.GetSecondaryTextureCount()];
                s.GetSecondaryTextures(tex);
                var nm = tex.FirstOrDefault(t => t.name == ArtImport.NormalMapProperty);
                Assert.That(nm.texture, Is.Not.Null, s.name + " has no _NormalMap secondary texture");
                Assert.That(nm.texture, Is.SameAs(normalTex), s.name);
            }
        }

        [TestCaseSource(nameof(Sidecars))]
        public void AnimationSetMatchesSidecar(string sidecar)
        {
            var sheet = ArtImport.ReadSidecar(sidecar);
            var set = AssetDatabase.LoadAssetAtPath<SpriteAnimationSet>(ArtImport.AnimationSetPath(sheet));
            Assert.That(set, Is.Not.Null);
            Assert.That(set.clips.Select(c => c.name), Is.EqualTo(sheet.animations.Select(a => a.name)));
            foreach (var a in sheet.animations)
            {
                var clip = set.Find(a.name);
                Assert.That(clip.frames.Length, Is.EqualTo(a.frames.Length), a.name);
                Assert.That(clip.frames.All(f => f != null), a.name + " has null frames");
                Assert.That(clip.fps, Is.EqualTo(a.fps));
            }
        }

        [Test]
        public void MinerHasGameplayAnimations()
        {
            var set = AssetDatabase.LoadAssetAtPath<SpriteAnimationSet>(ArtImport.SpriteRoot + "/miner.anim.asset");
            foreach (var clip in new[] { "idle", "walk", "jump", "fall", "use" })
                Assert.That(set.Find(clip), Is.Not.Null, clip);
        }
    }

    public class ProjectSettingsTests
    {
        [Test]
        public void SerializationAndVersionControl()
        {
            Assert.That(EditorSettings.serializationMode, Is.EqualTo(SerializationMode.ForceText));
            Assert.That(VersionControlSettings.mode, Is.EqualTo("Visible Meta Files"));
        }

        [Test]
        public void LinearColorSpace() => Assert.That(PlayerSettings.colorSpace, Is.EqualTo(ColorSpace.Linear));

        [Test]
        public void Urp2DRendererIsActive()
        {
            var urp = GraphicsSettings.defaultRenderPipeline as UniversalRenderPipelineAsset;
            Assert.That(urp, Is.Not.Null, "default render pipeline is not URP");
            var renderer = AssetDatabase.LoadAssetAtPath<Renderer2DData>(RenderPipelineSetup.RendererPath);
            Assert.That(renderer, Is.Not.Null);
            var so = new SerializedObject(urp);
            var list = so.FindProperty("m_RendererDataList");
            Assert.That(list.arraySize, Is.GreaterThan(0));
            Assert.That(list.GetArrayElementAtIndex(0).objectReferenceValue, Is.SameAs(renderer));
            for (int i = 0; i < QualitySettings.names.Length; i++)
            {
                var q = QualitySettings.GetRenderPipelineAssetAt(i);
                Assert.That(q == null || q == urp, $"quality level {QualitySettings.names[i]} uses another pipeline");
            }
        }

        [Test]
        public void SortingLayersInOrder()
        {
            var names = SortingLayer.layers.Select(l => l.name).ToArray();
            Assert.That(names, Is.EqualTo(new[] { "Default" }.Concat(ProjectSetup.SortingLayers).ToArray()));
        }
    }

    public class LaunchArgsTests
    {
        [TearDown] public void Restore() => LaunchArgs.Override(null);

        [Test]
        public void ParsesFlagsAndValues()
        {
            LaunchArgs.Override(new[] { "Loamlight.exe", "-llCapture", "\"C:/out dir\"", "-llScene", "PipelineDemo", "-flag" });
            Assert.That(LaunchArgs.TryGet("-llCapture", out var dir), Is.True);
            Assert.That(dir, Is.EqualTo("C:/out dir"));
            Assert.That(LaunchArgs.Get("-llscene", null), Is.EqualTo("PipelineDemo"), "flags are case-insensitive");
            Assert.That(LaunchArgs.Has("-flag"), Is.True);
            Assert.That(LaunchArgs.TryGet("-flag", out _), Is.False, "a trailing flag has no value");
            Assert.That(LaunchArgs.Get("-missing", "x"), Is.EqualTo("x"));
        }
    }
}
