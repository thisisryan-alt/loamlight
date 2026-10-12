using System;
using System.Collections.Generic;
using UnityEngine;

namespace Loamlight.Core
{
    /// <summary>
    /// Fire-and-forget SFX with a pooled set of AudioSources, simple stereo panning and distance attenuation
    /// relative to the camera, random variant + pitch per play, and per-name rate limiting so a busy frame
    /// (twenty blocks breaking) does not stack twenty copies of one sound.
    /// </summary>
    public sealed class Sfx : MonoBehaviour
    {
        public static float MasterVolume = 1f, SfxVolume = 0.8f, MusicVolume = 0.5f;

        static Sfx _instance;
        readonly Dictionary<string, SfxLibrary.Group> _groups = new Dictionary<string, SfxLibrary.Group>(StringComparer.Ordinal);
        readonly Dictionary<string, float> _lastPlayed = new Dictionary<string, float>(StringComparer.Ordinal);
        readonly List<AudioSource> _sources = new List<AudioSource>();
        AudioSource _music;
        SfxLibrary _library;
        int _next;
        System.Random _rng = new System.Random(7);

        /// <summary>Names of every sound played (scenarios assert on this to prove a feature made noise).</summary>
        public static readonly List<string> PlayLog = new List<string>();

        public static Sfx Instance
        {
            get
            {
                if (_instance != null) return _instance;
                var go = new GameObject("[Sfx]");
                DontDestroyOnLoad(go);
                _instance = go.AddComponent<Sfx>();
                return _instance;
            }
        }

        void Awake()
        {
            _library = Resources.Load<SfxLibrary>(SfxLibrary.ResourcePath);
            if (_library != null)
                foreach (var g in _library.groups) _groups[g.name] = g;
            for (int i = 0; i < 24; i++)
            {
                var s = gameObject.AddComponent<AudioSource>();
                s.playOnAwake = false;
                s.spatialBlend = 0f;
                _sources.Add(s);
            }
            _music = gameObject.AddComponent<AudioSource>();
            _music.loop = true;
            _music.playOnAwake = false;
        }

        public static bool Has(string name) => Instance._groups.ContainsKey(name);

        public static void Play(string name, Vector2 worldPos, float volume = 1f, float pitchJitter = 0.08f) =>
            Instance.PlayInternal(name, worldPos, volume, pitchJitter, true);

        public static void PlayUi(string name, float volume = 1f) => Instance.PlayInternal(name, Vector2.zero, volume, 0.02f, false);

        void PlayInternal(string name, Vector2 pos, float volume, float pitchJitter, bool positional)
        {
            if (string.IsNullOrEmpty(name)) return;
            if (PlayLog.Count < 4096) PlayLog.Add(name);
            if (!_groups.TryGetValue(name, out var g) || g.clips.Length == 0) return;
            float now = Time.unscaledTime;
            if (_lastPlayed.TryGetValue(name, out var last) && now - last < 0.035f) return;
            _lastPlayed[name] = now;

            float pan = 0f, att = 1f;
            if (positional && Camera.main != null)
            {
                var cam = (Vector2)Camera.main.transform.position;
                float halfW = Camera.main.orthographicSize * Camera.main.aspect;
                float dx = (pos.x - cam.x) / Mathf.Max(1f, halfW);
                pan = Mathf.Clamp(dx * 0.6f, -0.8f, 0.8f);
                float dist = Vector2.Distance(pos, cam);
                att = Mathf.Clamp01(1f - (dist - halfW) / (halfW * 1.5f));
                if (att <= 0.01f) return;
            }
            var src = _sources[_next];
            _next = (_next + 1) % _sources.Count;
            src.clip = g.clips[_rng.Next(g.clips.Length)];
            src.volume = volume * g.volume * att * SfxVolume * MasterVolume;
            src.pitch = 1f + (float)(_rng.NextDouble() * 2 - 1) * pitchJitter;
            src.panStereo = pan;
            src.Play();
        }

        public static void PlayMusic(int index)
        {
            var s = Instance;
            if (s._library == null || s._library.music.Length == 0) return;
            var clip = s._library.music[Mathf.Abs(index) % s._library.music.Length];
            if (s._music.clip == clip && s._music.isPlaying) return;
            s._music.clip = clip;
            s._music.volume = MusicVolume * MasterVolume;
            s._music.Play();
        }

        public static void StopMusic()
        {
            if (_instance != null) _instance._music.Stop();
        }

        void Update()
        {
            if (_music != null) _music.volume = MusicVolume * MasterVolume;
        }
    }
}
