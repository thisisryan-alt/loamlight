using System.Linq;
using System.Runtime.InteropServices;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace Loamlight.EditorTools
{
    /// <summary>Windows player builds (native ARM64 on Arm machines, x64 elsewhere) into Builds/Windows.</summary>
    public static class PlayerBuild
    {
        public const string OutputPath = "Builds/Windows/Loamlight.exe";

        [MenuItem("Loamlight/Build/Development Player")]
        public static void BuildDevelopment() => Build(BuildOptions.Development);

        [MenuItem("Loamlight/Build/Release Player")]
        public static void BuildRelease() => Build(BuildOptions.None);

        static void Build(BuildOptions options)
        {
            bool arm = RuntimeInformation.OSArchitecture == Architecture.Arm64;
            UnityEditor.WindowsStandalone.UserBuildSettings.architecture = arm ? OSArchitecture.ARM64 : OSArchitecture.x64;
            PlayerSettings.SetScriptingBackend(NamedBuildTarget.Standalone, ScriptingImplementation.Mono2x);

            var scenes = EditorBuildSettings.scenes.Where(s => s.enabled).Select(s => s.path).ToArray();
            if (scenes.Length == 0) throw new BuildFailedException("no scenes in build settings; run setup first");

            var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
            {
                scenes = scenes,
                locationPathName = OutputPath,
                target = BuildTarget.StandaloneWindows64,
                options = options,
            });
            var s = report.summary;
            Debug.Log($"[PlayerBuild] {s.result}: {s.totalSize / (1024 * 1024)} MB, {s.totalErrors} errors, " +
                      $"{s.totalWarnings} warnings, {s.totalTime.TotalSeconds:0}s ({(arm ? "ARM64" : "x64")}, {options})");
            if ((s.result != BuildResult.Succeeded || s.totalErrors > 0) && Application.isBatchMode)
            {
                Debug.LogError("[LL-ERROR] player build failed");
                EditorApplication.Exit(1);
            }
        }
    }
}
