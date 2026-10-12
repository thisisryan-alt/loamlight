using UnityEngine;

namespace Loamlight.Core
{
    /// <summary>
    /// Plays clips from a <see cref="SpriteAnimationSet"/> on the attached SpriteRenderer.
    /// The normal map travels with each sprite (secondary texture), so swapping sprites keeps lighting correct.
    /// </summary>
    [RequireComponent(typeof(SpriteRenderer))]
    public sealed class SpriteAnimator : MonoBehaviour
    {
        [SerializeField] SpriteAnimationSet animations;
        [SerializeField] string startClip = "idle";
        [SerializeField] float speed = 1f;
        [Tooltip("Loop the clip even if the sheet marks it as one-shot (demo/preview use).")]
        [SerializeField] bool forceLoop;

        SpriteRenderer _renderer;
        SpriteAnimationSet.Clip _clip;
        float _time;

        public SpriteAnimationSet Animations => animations;
        public string CurrentClip => _clip?.name;
        public int CurrentFrame { get; private set; }

        /// <summary>Editor/scene-builder entry point; also usable at runtime before Awake.</summary>
        public void Configure(SpriteAnimationSet set, string clip, float playbackSpeed = 1f, bool loopAlways = false)
        {
            animations = set;
            startClip = clip;
            speed = playbackSpeed;
            forceLoop = loopAlways;
        }

        void Awake()
        {
            _renderer = GetComponent<SpriteRenderer>();
            Play(startClip, true);
        }

        public void Play(string clipName, bool restart = false)
        {
            if (animations == null) return;
            var clip = animations.Find(clipName);
            if (clip == null || clip.frames.Length == 0)
            {
                Debug.LogWarning($"[SpriteAnimator] '{name}': no clip '{clipName}' in {animations.sheetName}", this);
                return;
            }
            if (clip == _clip && !restart) return;
            _clip = clip;
            SetTime(0f);
        }

        void Update() => SetTime(_time + Time.deltaTime * speed);

        /// <summary>Jump to an absolute time within the current clip (deterministic captures and tests).</summary>
        public void SetTime(float seconds)
        {
            if (_clip == null) return;
            _time = seconds;
            int count = _clip.frames.Length;
            int index = Mathf.FloorToInt(_time * _clip.fps);
            index = (_clip.loop || forceLoop) ? ((index % count) + count) % count : Mathf.Clamp(index, 0, count - 1);
            CurrentFrame = index;
            if (_renderer == null) _renderer = GetComponent<SpriteRenderer>();
            _renderer.sprite = _clip.frames[index];
        }
    }
}
