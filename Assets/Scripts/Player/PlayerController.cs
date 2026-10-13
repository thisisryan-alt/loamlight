using System;
using Loamlight.Core;
using Loamlight.World;
using Loamlight.World.Collision;
using Unity.Mathematics;
using UnityEngine;

namespace Loamlight.Player
{
    /// <summary>
    /// Runs <see cref="PlayerMotor"/> on a fixed 60 Hz step (independent of frame rate) and interpolates the
    /// rendered position between steps. Gameplay time can be scaled (hitstop) without touching Time.timeScale.
    /// </summary>
    public sealed class PlayerController : MonoBehaviour
    {
        public const float StepTime = 1f / 60f;
        public static readonly float2 Size = new float2(1.25f, 2.6f);

        public MotorParams motor = MotorParams.Default;
        public IPlayerInput Input { get; set; }
        public ICollisionGrid Grid { get; set; }
        /// <summary>Multiplier on simulated time (1 = normal, 0 = frozen for hitstop).</summary>
        public float TimeScale { get; set; } = 1f;
        public bool Frozen { get; set; }
        public ILiquidSampler Liquids { get; set; }
        public float Submerged { get; private set; }
        public LiquidType SubmergedIn { get; private set; }
        /// <summary>Head went under or came back out of a liquid (splash sounds, particles).</summary>
        public event Action<bool, LiquidType> Splashed;

        MotorState _state, _prev;
        float _accum;

        public event Action<MotorEvents> Stepped;

        public MotorState State => _state;
        public Vector2 Feet => new Vector2(_state.body.position.x, _state.body.position.y);
        public Vector2 Center => Feet + new Vector2(0f, Size.y * 0.5f);
        public Vector2 Velocity => _state.body.velocity;
        public int Facing => _state.facing;
        public bool Grounded => _state.body.grounded;
        public Body Body => _state.body;

        public void Teleport(Vector2 feet)
        {
            _state = MotorState.At(new float2(feet.x, feet.y), Size);
            _prev = _state;
            _accum = 0f;
            transform.position = new Vector3(feet.x, feet.y, 0f);
        }

        /// <summary>Turn to face a direction (attacks turn the player toward the aim).</summary>
        public void Face(int dir)
        {
            if (dir != 0) _state.facing = dir > 0 ? 1 : -1;
        }

        public void AddImpulse(Vector2 velocity, float controlLoss)
        {
            _state.body.velocity = new float2(velocity.x, math.max(_state.body.velocity.y, velocity.y));
            if (velocity.y > 0f) _state.body.grounded = false;
            _state.knockbackTime = math.max(_state.knockbackTime, controlLoss);
        }

        void Update()
        {
            if (Grid == null || Input == null || Frozen) return;
            _accum += Time.deltaTime * TimeScale * GameClock.Scale;
            if (_accum > 0.25f) _accum = 0.25f;   // never spiral after a hitch
            bool jumpPressed = false;
            while (_accum >= StepTime)
            {
                _prev = _state;
                jumpPressed |= Input.ConsumeJumpPressed();
                float wet = TileCollision.Submersion(_state.body, Liquids, out var kind);
                if ((wet > 0.35f) != (Submerged > 0.35f)) Splashed?.Invoke(wet > 0.35f, wet > 0.35f ? kind : SubmergedIn);
                Submerged = wet;
                SubmergedIn = wet > 0f ? kind : LiquidType.None;
                var input = new MotorInput
                {
                    moveX = Input.MoveX, jumpHeld = Input.JumpHeld, jumpPressed = jumpPressed, downHeld = Input.DownHeld,
                    submerged = wet, lava = kind == LiquidType.Lava,
                };
                jumpPressed = false;
                var ev = PlayerMotor.Step(ref _state, input, motor, Grid, StepTime);
                Stepped?.Invoke(ev);
                _accum -= StepTime;
            }
            float t = _accum / StepTime;
            var p = math.lerp(_prev.body.position, _state.body.position, t);
            transform.position = new Vector3(p.x, p.y, 0f);
        }
    }
}
