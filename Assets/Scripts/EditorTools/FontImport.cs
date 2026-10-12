using System;
using System.Collections.Generic;
using System.IO;
using Loamlight.Core;
using UnityEditor;
using UnityEngine;

namespace Loamlight.EditorTools
{
    /// <summary>
    /// Builds the bitmap UI font (Tools/art/gen_font.py output) as a Unity Font asset in Resources, so uGUI Text
    /// draws it pixel-exact at fontSize 8.
    /// </summary>
    public static class FontImport
    {
        public const string Folder = "Assets/Art/Generated/Font";
        public const string FontPath = "Assets/Resources/UI/PixelFont.fontsettings";
        public const string MaterialPath = "Assets/Resources/UI/PixelFont.mat";

        [MenuItem("Loamlight/Setup/Import Font")]
        public static void ImportAll()
        {
            var pngPath = $"{Folder}/pixelfont.png";
            var jsonPath = $"{Folder}/pixelfont.json";
            if (!File.Exists(pngPath)) throw new FileNotFoundException("run Tools/build_art.ps1 first", pngPath);
            AssetDatabase.ImportAsset(pngPath, ImportAssetOptions.ForceSynchronousImport);
            var imp = (TextureImporter)AssetImporter.GetAtPath(pngPath);
            imp.textureType = TextureImporterType.Default;
            imp.filterMode = FilterMode.Point;
            imp.mipmapEnabled = false;
            imp.textureCompression = TextureImporterCompression.Uncompressed;
            imp.alphaIsTransparency = true;
            imp.npotScale = TextureImporterNPOTScale.None;
            imp.wrapMode = TextureWrapMode.Clamp;
            imp.SaveAndReimport();
            var tex = AssetDatabase.LoadAssetAtPath<Texture2D>(pngPath);

            var meta = (Dictionary<string, object>)MiniJson.Parse(File.ReadAllText(jsonPath));
            float W = Convert.ToSingle(meta["width"]), H = Convert.ToSingle(meta["height"]);
            int ascent = Convert.ToInt32(meta["ascent"]), descent = Convert.ToInt32(meta["descent"]);
            var infos = new List<CharacterInfo>();
            foreach (Dictionary<string, object> g in (List<object>)meta["glyphs"])
            {
                float x = Convert.ToSingle(g["x"]), y = Convert.ToSingle(g["y"]), w = Convert.ToSingle(g["w"]), h = Convert.ToSingle(g["h"]);
                var ci = new CharacterInfo
                {
                    index = Convert.ToInt32(g["char"]),
                    advance = Convert.ToInt32(g["advance"]),
                    minX = 0, maxX = (int)w, minY = -descent, maxY = ascent,
                };
                // image rows grow downward; UV v grows upward
                float u0 = x / W, u1 = (x + w) / W, v1 = 1f - y / H, v0 = 1f - (y + h) / H;
                ci.uvBottomLeft = new Vector2(u0, v0);
                ci.uvBottomRight = new Vector2(u1, v0);
                ci.uvTopLeft = new Vector2(u0, v1);
                ci.uvTopRight = new Vector2(u1, v1);
                infos.Add(ci);
            }

            AssetFolders.Ensure("Assets/Resources/UI");
            var mat = AssetDatabase.LoadAssetAtPath<Material>(MaterialPath);
            if (mat == null)
            {
                mat = new Material(Shader.Find("GUI/Text Shader")) { name = "PixelFont" };
                AssetDatabase.CreateAsset(mat, MaterialPath);
            }
            if (mat.mainTexture != tex) mat.mainTexture = tex;

            var font = AssetDatabase.LoadAssetAtPath<Font>(FontPath);
            bool created = font == null;
            if (created) font = new Font("PixelFont");
            var before = created ? null : EditorJsonUtility.ToJson(font);
            font.material = mat;
            font.characterInfo = infos.ToArray();
            if (created) AssetDatabase.CreateAsset(font, FontPath);
            var so = new SerializedObject(font);
            Set(so, "m_LineSpacing", Convert.ToSingle(meta["lineHeight"]));
            Set(so, "m_FontSize", Convert.ToSingle(meta["fontSize"]));
            Set(so, "m_Ascent", ascent);
            so.ApplyModifiedPropertiesWithoutUndo();
            if (!created && EditorJsonUtility.ToJson(font) != before) EditorUtility.SetDirty(font);
            AssetDatabase.SaveAssets();
            Debug.Log($"[FontImport] {infos.Count} glyphs -> {FontPath}");
        }

        static void Set(SerializedObject so, string prop, float v)
        {
            var p = so.FindProperty(prop);
            if (p == null) { Debug.LogWarning($"[FontImport] Font.{prop} not found"); return; }
            if (p.propertyType == SerializedPropertyType.Integer) p.intValue = (int)v;
            else p.floatValue = v;
        }
    }
}
