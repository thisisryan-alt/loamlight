using System;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Loamlight.EditorTools
{
    /// <summary>
    /// One idempotent entry point that recreates every setting and generated asset from code:
    /// <c>Unity -batchmode -projectPath . -executeMethod Loamlight.EditorTools.ProjectSetup.RunAll -quit</c>
    /// (wrapped by <c>Tools/unity.ps1 setup</c>). Running it twice must leave git clean.
    /// </summary>
    public static class ProjectSetup
    {
        public const string CompanyName = "Loamlight Dev";
        public const string ProductName = "Loamlight";

        /// <summary>Sorting layers, back to front. IDs are derived from the names so they never change.</summary>
        public static readonly string[] SortingLayers = { "Background", "Walls", "World", "Entities", "Foreground", "Overlay" };

        [MenuItem("Loamlight/Setup/Run All")]
        public static void RunAll()
        {
            try
            {
                ConfigureEditorAndPlayer();
                ConfigureSortingLayers();
                RenderPipelineSetup.Run();
                ArtImport.ImportAll();
                AudioImport.ImportAll();
                AtlasImport.ImportAll();
                FontImport.ImportAll();
                ContentImport.ImportAll();
                SceneBuilder.BuildAll();
                AssetDatabase.SaveAssets();
                Debug.Log("[Setup] complete");
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                Debug.LogError("[LL-ERROR] setup failed: " + e.Message);
                if (Application.isBatchMode) EditorApplication.Exit(1);
                throw;
            }
        }

        public static void ConfigureEditorAndPlayer()
        {
            EditorSettings.serializationMode = SerializationMode.ForceText;
            VersionControlSettings.mode = "Visible Meta Files";

            PlayerSettings.companyName = CompanyName;
            PlayerSettings.productName = ProductName;
            PlayerSettings.colorSpace = ColorSpace.Linear;          // 2D lights blend correctly in linear
            PlayerSettings.runInBackground = true;                   // captures keep rendering when unfocused
            PlayerSettings.fullScreenMode = FullScreenMode.Windowed;
            PlayerSettings.defaultScreenWidth = 1280;
            PlayerSettings.defaultScreenHeight = 720;
            PlayerSettings.resizableWindow = true;
            PlayerSettings.visibleInBackground = true;
            PlayerSettings.enableFrameTimingStats = true;            // CPU/GPU split in capture reports
            PlayerSettings.SplashScreen.show = false;

            // Input System only. Takes effect the next time the editor/player starts.
            var player = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/ProjectSettings.asset").FirstOrDefault();
            if (player != null)
            {
                var so = new SerializedObject(player);
                var handler = so.FindProperty("activeInputHandler");
                if (handler != null && handler.intValue != 1)
                {
                    handler.intValue = 1;
                    so.ApplyModifiedPropertiesWithoutUndo();
                }
            }
            Debug.Log("[Setup] editor + player settings applied");
        }

        public static void ConfigureSortingLayers()
        {
            var tagManager = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/TagManager.asset").FirstOrDefault();
            if (tagManager == null) throw new InvalidOperationException("TagManager.asset not found");
            var so = new SerializedObject(tagManager);
            var layers = so.FindProperty("m_SortingLayers");

            // Keep "Default" (id 0) first, then ours in order; anything else is dropped.
            var wanted = new[] { "Default" }.Concat(SortingLayers).ToArray();
            bool changed = layers.arraySize != wanted.Length;
            for (int i = 0; !changed && i < wanted.Length; i++)
            {
                var e = layers.GetArrayElementAtIndex(i);
                changed = e.FindPropertyRelative("name").stringValue != wanted[i] ||
                          e.FindPropertyRelative("uniqueID").longValue != (i == 0 ? 0 : StableIds.PositiveInt("sorting/" + wanted[i]));
            }
            if (!changed) return;

            layers.arraySize = wanted.Length;
            for (int i = 0; i < wanted.Length; i++)
            {
                var e = layers.GetArrayElementAtIndex(i);
                e.FindPropertyRelative("name").stringValue = wanted[i];
                e.FindPropertyRelative("uniqueID").longValue = i == 0 ? 0 : StableIds.PositiveInt("sorting/" + wanted[i]);
                var locked = e.FindPropertyRelative("locked");
                if (locked != null) locked.intValue = 0;
            }
            so.ApplyModifiedPropertiesWithoutUndo();
            Debug.Log("[Setup] sorting layers: " + string.Join(", ", wanted));
        }
    }
}
