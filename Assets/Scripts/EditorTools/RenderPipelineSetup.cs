using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace Loamlight.EditorTools
{
    /// <summary>Creates the URP asset with the 2D Renderer and makes it active for every quality level.</summary>
    public static class RenderPipelineSetup
    {
        public const string Folder = "Assets/Settings/Rendering";
        public const string RendererPath = Folder + "/Loamlight2DRenderer.asset";
        public const string PipelinePath = Folder + "/LoamlightURP.asset";
        const string UrpPackage = "Packages/com.unity.render-pipelines.universal";

        public static void Run()
        {
            AssetFolders.Ensure(Folder);

            var renderer = AssetDatabase.LoadAssetAtPath<Renderer2DData>(RendererPath);
            if (renderer == null)
            {
                // Mirrors URP's own "Create > Rendering > URP 2D Renderer" (whose helper is internal).
                renderer = ScriptableObject.CreateInstance<Renderer2DData>();
                AssetDatabase.CreateAsset(renderer, RendererPath);
                ResourceReloader.ReloadAllNullIn(renderer, UrpPackage);
            }
            var rso = new SerializedObject(renderer);
            Set(rso, "m_PostProcessData", AssetDatabase.LoadAssetAtPath<Object>(UrpPackage + "/Runtime/Data/PostProcessData.asset"));
            Set(rso, "m_HDREmulationScale", 1f);
            // Full-resolution light textures keep per-pixel normal-map detail crisp on pixel art (revisit in profiling).
            Set(rso, "m_LightRenderTextureScale", 1f);
            Set(rso, "m_UseDepthStencilBuffer", true);
            rso.ApplyModifiedPropertiesWithoutUndo();

            var pipeline = AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(PipelinePath);
            if (pipeline == null)
            {
                pipeline = UniversalRenderPipelineAsset.Create(renderer);
                AssetDatabase.CreateAsset(pipeline, PipelinePath);
            }
            pipeline.supportsHDR = true;
            pipeline.msaaSampleCount = 1;          // point-filtered pixel art: MSAA only costs
            pipeline.renderScale = 1f;
            pipeline.supportsCameraDepthTexture = false;
            pipeline.supportsCameraOpaqueTexture = false;
            EditorUtility.SetDirty(pipeline);

            if (GraphicsSettings.defaultRenderPipeline != pipeline)
                GraphicsSettings.defaultRenderPipeline = pipeline;
            int current = QualitySettings.GetQualityLevel();
            for (int i = 0; i < QualitySettings.names.Length; i++)
            {
                QualitySettings.SetQualityLevel(i, false);
                if (QualitySettings.renderPipeline != pipeline) QualitySettings.renderPipeline = pipeline;
            }
            QualitySettings.SetQualityLevel(current, false);
            AssetDatabase.SaveAssets();
            Debug.Log($"[Setup] URP 2D renderer active: {PipelinePath}");
        }

        static void Set(SerializedObject so, string prop, Object value)
        {
            var p = so.FindProperty(prop);
            if (p == null) { Debug.LogWarning($"[Setup] {so.targetObject.GetType().Name}.{prop} not found"); return; }
            p.objectReferenceValue = value;
        }

        static void Set(SerializedObject so, string prop, float value)
        {
            var p = so.FindProperty(prop);
            if (p == null) { Debug.LogWarning($"[Setup] {so.targetObject.GetType().Name}.{prop} not found"); return; }
            p.floatValue = value;
        }

        static void Set(SerializedObject so, string prop, bool value)
        {
            var p = so.FindProperty(prop);
            if (p == null) { Debug.LogWarning($"[Setup] {so.targetObject.GetType().Name}.{prop} not found"); return; }
            p.boolValue = value;
        }
    }

    public static class AssetFolders
    {
        /// <summary>Creates every missing folder along an "Assets/..." path.</summary>
        public static void Ensure(string path)
        {
            var parts = path.Split('/');
            var cur = parts[0];
            for (int i = 1; i < parts.Length; i++)
            {
                var next = cur + "/" + parts[i];
                if (!AssetDatabase.IsValidFolder(next)) AssetDatabase.CreateFolder(cur, parts[i]);
                cur = next;
            }
        }
    }
}
