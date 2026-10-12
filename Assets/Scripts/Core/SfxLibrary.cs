using System;
using UnityEngine;

namespace Loamlight.Core
{
    /// <summary>Generated sound effects grouped by name (dig_soft_0.wav, dig_soft_1.wav → "dig_soft").</summary>
    public sealed class SfxLibrary : ScriptableObject
    {
        public const string ResourcePath = "Audio/SfxLibrary";

        [Serializable]
        public sealed class Group
        {
            public string name;
            public AudioClip[] clips = Array.Empty<AudioClip>();
            public float volume = 1f;
        }

        public Group[] groups = Array.Empty<Group>();
        public AudioClip[] music = Array.Empty<AudioClip>();
    }
}
