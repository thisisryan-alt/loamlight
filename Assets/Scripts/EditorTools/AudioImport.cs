using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Loamlight.Core;
using UnityEditor;
using UnityEngine;

namespace Loamlight.EditorTools
{
    /// <summary>
    /// Imports the synthesized SFX (Tools/audio/synth_sfx.py) and music, and builds the <see cref="SfxLibrary"/>
    /// that groups variants by name: dig_soft_0.wav + dig_soft_1.wav → group "dig_soft".
    /// </summary>
    public static class AudioImport
    {
        public const string SfxFolder = "Assets/Audio/Generated";
        public const string MusicFolder = "Assets/Audio/Music";
        public const string LibraryPath = "Assets/Resources/Audio/SfxLibrary.asset";

        static readonly Regex Variant = new Regex(@"^(.*)_(\d+)$");

        [MenuItem("Loamlight/Setup/Import Audio")]
        public static void ImportAll()
        {
            var sfx = Directory.Exists(SfxFolder) ? Directory.GetFiles(SfxFolder, "*.wav").Select(p => p.Replace('\\', '/')).OrderBy(p => p, StringComparer.Ordinal).ToArray() : Array.Empty<string>();
            var music = Directory.Exists(MusicFolder) ? Directory.GetFiles(MusicFolder, "*.wav").Select(p => p.Replace('\\', '/')).OrderBy(p => p, StringComparer.Ordinal).ToArray() : Array.Empty<string>();
            foreach (var p in sfx) Configure(p, false);
            foreach (var p in music) Configure(p, true);

            var groups = sfx
                .Select(p => (path: p, name: Path.GetFileNameWithoutExtension(p)))
                .GroupBy(x => Variant.Match(x.name) is var m && m.Success ? m.Groups[1].Value : x.name)
                .OrderBy(g => g.Key, StringComparer.Ordinal)
                .Select(g => new SfxLibrary.Group
                {
                    name = g.Key,
                    clips = g.Select(x => AssetDatabase.LoadAssetAtPath<AudioClip>(x.path)).Where(c => c != null).ToArray(),
                    volume = 1f,
                }).ToArray();

            AssetFolders.Ensure(Path.GetDirectoryName(LibraryPath).Replace('\\', '/'));
            var lib = AssetDatabase.LoadAssetAtPath<SfxLibrary>(LibraryPath);
            bool created = lib == null;
            if (created) lib = ScriptableObject.CreateInstance<SfxLibrary>();
            var before = created ? null : EditorJsonUtility.ToJson(lib);
            lib.groups = groups;
            lib.music = music.Select(AssetDatabase.LoadAssetAtPath<AudioClip>).Where(c => c != null).ToArray();
            if (created) AssetDatabase.CreateAsset(lib, LibraryPath);
            else if (EditorJsonUtility.ToJson(lib) != before) EditorUtility.SetDirty(lib);
            AssetDatabase.SaveAssets();
            Debug.Log($"[AudioImport] {groups.Length} sfx groups ({sfx.Length} clips), {lib.music.Length} music tracks");
        }

        static void Configure(string path, bool isMusic)
        {
            var imp = AssetImporter.GetAtPath(path) as AudioImporter;
            if (imp == null) return;
            var s = imp.defaultSampleSettings;
            var want = s;
            want.loadType = isMusic ? AudioClipLoadType.Streaming : AudioClipLoadType.DecompressOnLoad;
            want.compressionFormat = isMusic ? AudioCompressionFormat.Vorbis : AudioCompressionFormat.PCM;
            want.quality = isMusic ? 0.6f : 1f;
            bool changed = !want.Equals(s) || imp.forceToMono != !isMusic;
            if (!changed) return;
            imp.defaultSampleSettings = want;
            imp.forceToMono = !isMusic;
            imp.SaveAndReimport();
        }
    }
}
