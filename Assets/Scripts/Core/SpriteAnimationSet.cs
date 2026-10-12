using System;
using UnityEngine;

namespace Loamlight.Core
{
    /// <summary>
    /// Named sprite animations cut from one generated sheet. Built by the editor importer from the sheet's
    /// JSON sidecar (Assets/Art/Generated/Sprites/*.sheet.json); never authored by hand.
    /// </summary>
    public sealed class SpriteAnimationSet : ScriptableObject
    {
        [Serializable]
        public sealed class Clip
        {
            public string name;
            public float fps = 10f;
            public bool loop = true;
            public Sprite[] frames = Array.Empty<Sprite>();

            public float Duration => frames.Length / Mathf.Max(0.01f, fps);
        }

        public string sheetName;
        public Clip[] clips = Array.Empty<Clip>();

        public Clip Find(string clipName)
        {
            foreach (var clip in clips)
                if (clip.name == clipName) return clip;
            return null;
        }
    }
}
