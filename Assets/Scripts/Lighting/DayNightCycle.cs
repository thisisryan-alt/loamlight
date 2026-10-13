using UnityEngine;

namespace Loamlight.Lighting
{
    /// <summary>Everything the sky looks like at one moment.</summary>
    public struct SkyState
    {
        public Color top, horizon;   // backdrop gradient
        public Color light;          // sky light colour fed to the light map
        public float intensity;      // sky light strength
        public float night;          // 0 = full day, 1 = full night (stars, moon)
    }

    /// <summary>
    /// Time of day on a loop. t = 0 is sunrise; the sun is up for the first 60% of the cycle, the moon for the rest.
    /// Drives the light map's sky colour and intensity (darkness at night is real: the sky light that floods every
    /// open column dims to a blue moonlight) and exposes the state the sky backdrop and spawners read.
    /// </summary>
    public sealed class DayNightCycle : MonoBehaviour
    {
        public const float DayFraction = 0.6f;

        [Tooltip("Real seconds per full day/night loop.")]
        public float dayLength = 900f;
        [Range(0f, 1f)] public float time = 0.08f;
        public int day = 1;
        public float TimeScale { get; set; } = 1f;
        public bool Paused { get; set; }
        public LightMapSystem Light { get; set; }

        public SkyState State { get; private set; }
        public bool IsNight => time >= 0.62f && time < 0.97f;
        /// <summary>0..1 across the sun's arc while it is up, else -1.</summary>
        public float SunArc => time < DayFraction ? time / DayFraction : -1f;
        /// <summary>0..1 across the moon's arc while it is up, else -1.</summary>
        public float MoonArc => time >= DayFraction ? (time - DayFraction) / (1f - DayFraction) : -1f;

        public event System.Action<int> NewDay;
        public event System.Action<bool> NightChanged;
        bool _wasNight;

        struct Key
        {
            public float t;
            public Color top, horizon, light;
            public float intensity, night;
            public Key(float t, string top, string horizon, Color light, float intensity, float night)
            {
                this.t = t;
                ColorUtility.TryParseHtmlString(top, out this.top);
                ColorUtility.TryParseHtmlString(horizon, out this.horizon);
                this.light = light;
                this.intensity = intensity;
                this.night = night;
            }
        }

        static readonly Key[] Keys =
        {
            new Key(0.00f, "#56689e", "#f2a27a", new Color(1f, 0.82f, 0.68f), 0.55f, 0.35f),
            new Key(0.07f, "#4a86d0", "#a9d2f0", new Color(1f, 0.95f, 0.88f), 0.95f, 0f),
            new Key(0.30f, "#3f7fd6", "#9fd0f5", new Color(1f, 0.98f, 0.93f), 1.0f, 0f),
            new Key(0.52f, "#4a7cc8", "#b6d2ec", new Color(1f, 0.94f, 0.84f), 0.95f, 0f),
            new Key(0.60f, "#3b3f7c", "#f0895a", new Color(1f, 0.68f, 0.48f), 0.5f, 0.3f),
            new Key(0.67f, "#0b1026", "#1b2547", new Color(0.5f, 0.6f, 0.95f), 0.22f, 1f),
            new Key(0.93f, "#0b1026", "#1d284d", new Color(0.5f, 0.6f, 0.95f), 0.22f, 1f),
            new Key(1.00f, "#56689e", "#f2a27a", new Color(1f, 0.82f, 0.68f), 0.55f, 0.35f),
        };

        /// <summary>Pure lookup (EditMode-tested): the sky at time t in [0, 1).</summary>
        public static SkyState Sample(float t)
        {
            t = Mathf.Repeat(t, 1f);
            for (int i = 0; i < Keys.Length - 1; i++)
            {
                var a = Keys[i];
                var b = Keys[i + 1];
                if (t > b.t) continue;
                float u = Mathf.SmoothStep(0f, 1f, (t - a.t) / Mathf.Max(1e-5f, b.t - a.t));
                return new SkyState
                {
                    top = Color.Lerp(a.top, b.top, u), horizon = Color.Lerp(a.horizon, b.horizon, u),
                    light = Color.Lerp(a.light, b.light, u), intensity = Mathf.Lerp(a.intensity, b.intensity, u),
                    night = Mathf.Lerp(a.night, b.night, u),
                };
            }
            var k = Keys[Keys.Length - 1];
            return new SkyState { top = k.top, horizon = k.horizon, light = k.light, intensity = k.intensity, night = k.night };
        }

        void Awake()
        {
            State = Sample(time);
            _wasNight = IsNight;
        }

        void Update()
        {
            if (!Paused && dayLength > 0f)
            {
                time += Time.deltaTime * TimeScale / dayLength;
                if (time >= 1f)
                {
                    time -= 1f;
                    day++;
                    NewDay?.Invoke(day);
                }
            }
            Apply();
        }

        /// <summary>Jump to a time of day (scenarios, sleeping in a bed, debug keys).</summary>
        public void SetTime(float t)
        {
            time = Mathf.Repeat(t, 1f);
            Apply();
        }

        void Apply()
        {
            State = Sample(time);
            if (Light != null)
            {
                Light.skyColor = State.light;
                Light.skyIntensity = State.intensity;
            }
            bool night = IsNight;
            if (night != _wasNight)
            {
                _wasNight = night;
                NightChanged?.Invoke(night);
            }
        }
    }
}
