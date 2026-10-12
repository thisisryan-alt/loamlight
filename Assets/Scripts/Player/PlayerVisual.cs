using Loamlight.Core;
using UnityEngine;

namespace Loamlight.Player
{
    /// <summary>Drives the player's sprite animation and facing from the motor state (flipX keeps normals correct).</summary>
    public sealed class PlayerVisual : MonoBehaviour
    {
        public PlayerController Controller { get; set; }
        public ToolUser Tools { get; set; }
        SpriteAnimator _anim;
        SpriteRenderer _renderer;
        float _stepTimer;

        void Awake()
        {
            _anim = GetComponent<SpriteAnimator>();
            _renderer = GetComponent<SpriteRenderer>();
        }

        void LateUpdate()
        {
            if (Controller == null || _anim == null) return;
            var v = Controller.Velocity;
            string clip;
            if (Tools != null && Tools.Swinging) clip = "use";
            else if (!Controller.Grounded) clip = v.y > 0.5f ? "jump" : "fall";
            else if (Mathf.Abs(v.x) > 0.6f) clip = "walk";
            else clip = "idle";
            _anim.Play(clip);
            _renderer.flipX = Controller.Facing < 0;

            if (clip == "walk")
            {
                _stepTimer -= Time.deltaTime * Mathf.Abs(v.x) / 9.5f;
                if (_stepTimer <= 0f)
                {
                    _stepTimer = 0.32f;
                    Sfx.Play("step", Controller.Feet, 0.35f, 0.15f);
                }
            }
        }
    }
}
