using Loamlight.Combat;
using Loamlight.Core;
using UnityEngine;

namespace Loamlight.AI
{
    /// <summary>
    /// The Gloamwarden's three phases. Every attack is telegraphed: the lantern flares and a warning chimes before a
    /// volley; a glowing line shows the path before a dash; the warden spreads its arms before a ring of wisps.
    /// <list type="bullet">
    /// <item>Phase 1: drifting over the player; aimed volleys of three wisps, then a single dash.</item>
    /// <item>Phase 2 (below 60%): it swallows the light (the world dims), calls duskbats, adds a ring of wisps,
    /// dashes twice in a row.</item>
    /// <item>Phase 3 (below 25%): enraged; faster, shorter tells, chains of three dashes, denser rings.</item>
    /// </list>
    /// Phase changes are short invulnerable roars, so the turn of the fight always reads.
    /// </summary>
    public sealed class WardenBrain
    {
        public const float Phase2At = 0.6f, Phase3At = 0.25f;
        enum Act { Drift, VolleyTell, DashTell, Dash, RingTell, Transition }

        public int Phase { get; private set; } = 1;
        public int AttacksStarted { get; private set; }
        public int DashesDone { get; private set; }

        Act _act = Act.Drift;
        float _t, _next = 2.5f, _summon = 12f;
        int _cycle, _dashesLeft;
        Vector2 _dashDir;

        void Go(Act a)
        {
            _act = a;
            _t = 0f;
        }

        public void Tick(Enemy e, IDamageable target, float dt)
        {
            _t += dt;
            float frac = e.Health.Fraction;
            if (Phase == 1 && frac <= Phase2At) Transition(e, 2);
            else if (Phase == 2 && frac <= Phase3At) Transition(e, 3);

            var c = e.Center;
            var aim = target != null ? target.Center : c;
            float speed = Phase == 1 ? 6.5f : Phase == 2 ? 8.5f : 11f;
            float tellScale = Phase == 1 ? 1f : Phase == 2 ? 0.85f : 0.7f;
            switch (_act)
            {
                case Act.Transition:
                    e.Velocity = Vector2.MoveTowards(e.Velocity, Vector2.zero, 30f * dt);
                    e.ClipOverride = "flare";
                    e.TelegraphOverride = true;
                    if (_t >= 1.6f)
                    {
                        e.TelegraphOverride = false;
                        Go(Act.Drift);
                        _next = 0.8f;
                    }
                    break;

                case Act.Drift:
                {
                    e.ClipOverride = "float";
                    var goal = aim + new Vector2(Mathf.Sin(Time.time * 0.7f) * 9f, 5f + Mathf.Sin(Time.time * 1.3f) * 1.5f);
                    var to = goal - c;
                    e.Velocity = Vector2.MoveTowards(e.Velocity, to.normalized * Mathf.Min(speed, to.magnitude * 2f), 24f * dt);
                    e.Facing = aim.x >= c.x ? 1 : -1;
                    _next -= dt;
                    if (_next <= 0f && target != null) Choose(e);
                    if (Phase >= 2)
                    {
                        _summon -= dt;
                        if (_summon <= 0f)
                        {
                            _summon = 14f;
                            e.Summon("duskbat", c + new Vector2(-3f, 2f));
                            e.Summon("duskbat", c + new Vector2(3f, 2f));
                        }
                    }
                    break;
                }

                case Act.VolleyTell:
                    e.Velocity = Vector2.MoveTowards(e.Velocity, Vector2.zero, 25f * dt);
                    e.ClipOverride = "flare";
                    e.TelegraphOverride = true;
                    if (_t >= 0.65f * tellScale)
                    {
                        int n = Phase == 1 ? 3 : 5;
                        var from = c + new Vector2(0f, 1.6f);
                        var dir = (aim - from).normalized;
                        float spread = Phase == 1 ? 12f : 10f;
                        for (int k = 0; k < n; k++)
                        {
                            float a = (k - (n - 1) * 0.5f) * spread;
                            e.Shoot(Rotate(dir, a) * (12f + 2f * Phase), from);
                        }
                        Sfx.Play("cast", c, 0.7f);
                        e.TelegraphOverride = false;
                        Go(Act.Drift);
                        _next = 2.2f - 0.4f * (Phase - 1);
                    }
                    break;

                case Act.DashTell:
                {
                    e.Velocity = Vector2.MoveTowards(e.Velocity, Vector2.zero, 40f * dt);
                    e.ClipOverride = "flare";
                    e.TelegraphOverride = true;
                    float tell = (DashesDone > 0 && _dashesLeft < Phase ? 0.45f : 0.8f) * tellScale;
                    if (_t < tell * 0.7f) _dashDir = (aim - c).sqrMagnitude > 0.01f ? (aim - c).normalized : Vector2.right;   // tracks, then locks
                    e.ShowLine(c, c + _dashDir * 24f, Mathf.PingPong(_t * 8f, 1f));
                    if (_t >= tell)
                    {
                        e.HideLine();
                        e.TelegraphOverride = false;
                        e.Velocity = _dashDir * (26f + 3f * Phase);
                        e.Facing = _dashDir.x >= 0f ? 1 : -1;
                        Sfx.Play("swing", c, 0.9f);
                        Go(Act.Dash);
                    }
                    break;
                }

                case Act.Dash:
                    e.ClipOverride = "dash";
                    if (_t >= 0.5f)
                    {
                        DashesDone++;
                        _dashesLeft--;
                        if (_dashesLeft > 0) Go(Act.DashTell);
                        else { Go(Act.Drift); _next = 1.8f - 0.3f * (Phase - 1); }
                    }
                    break;

                case Act.RingTell:
                    e.Velocity = Vector2.MoveTowards(e.Velocity, Vector2.zero, 30f * dt);
                    e.ClipOverride = "cast";
                    e.TelegraphOverride = true;
                    if (_t >= 0.8f * tellScale)
                    {
                        int n = Phase == 2 ? 10 : 14;
                        float offset = _cycle * 13f;
                        for (int k = 0; k < n; k++) e.Shoot(Rotate(Vector2.right, offset + k * 360f / n) * 10f, c + new Vector2(0f, 1.6f));
                        Sfx.Play("boom", c, 0.6f);
                        e.TelegraphOverride = false;
                        Go(Act.Drift);
                        _next = 2f;
                    }
                    break;
            }
        }

        void Choose(Enemy e)
        {
            _cycle++;
            AttacksStarted++;
            int k = _cycle % (Phase == 1 ? 2 : 3);
            Sfx.Play("warn", e.Center, 0.7f);
            if (k == 0) Go(Act.VolleyTell);
            else if (k == 1)
            {
                _dashesLeft = Phase;
                Go(Act.DashTell);
            }
            else Go(Act.RingTell);
        }

        void Transition(Enemy e, int phase)
        {
            Phase = phase;
            e.HideLine();
            e.MakeInvulnerable(1.6f);
            e.Darken(phase == 2 ? 0.3f : 0.5f);
            e.RaisePhase(phase);
            Sfx.Play("roar", e.Center, 1f);
            if (phase == 2)
                for (int k = -1; k <= 1; k++) e.Summon("duskbat", e.Center + new Vector2(k * 4f, 3f));
            Go(Act.Transition);
        }

        static Vector2 Rotate(Vector2 v, float degrees)
        {
            float r = degrees * Mathf.Deg2Rad, c = Mathf.Cos(r), s = Mathf.Sin(r);
            return new Vector2(v.x * c - v.y * s, v.x * s + v.y * c);
        }
    }
}
