using Loamlight.World.Collision;
using Unity.Mathematics;

namespace Loamlight.Player
{
    public struct MotorInput
    {
        public float moveX;
        public bool jumpHeld;
        public bool jumpPressed;
        public bool downHeld;
    }

    /// <summary>Movement feel, in tiles and seconds.</summary>
    public struct MotorParams
    {
        public float maxRun, groundAccel, groundDecel, airAccel, airDecel;
        public float gravity, apexGravityScale, apexThreshold, maxFall;
        public float jumpSpeed, jumpCut;
        public float coyoteTime, jumpBuffer, stepHeight;

        public static MotorParams Default => new MotorParams
        {
            maxRun = 9.5f, groundAccel = 75f, groundDecel = 90f, airAccel = 48f, airDecel = 30f,
            gravity = 82f, apexGravityScale = 0.55f, apexThreshold = 4f, maxFall = 42f,
            jumpSpeed = 29.5f, jumpCut = 0.42f,
            coyoteTime = 0.1f, jumpBuffer = 0.12f, stepHeight = 1.05f,
        };

        /// <summary>Peak height of a full (held) jump, ignoring apex hang: v^2 / 2g.</summary>
        public float FullJumpHeight => jumpSpeed * jumpSpeed / (2f * gravity);
    }

    public struct MotorState
    {
        public Body body;
        public float coyote;        // time left in which a jump still counts after walking off a ledge
        public float buffer;        // time left in which a jump press is still waiting for ground
        public bool rising;         // in a jump that can still be cut short by releasing
        public int facing;          // -1 left, +1 right
        public float fallPeakY;     // highest point since last grounded (fall damage)
        public float knockbackTime; // horizontal control is reduced while > 0

        public static MotorState At(float2 feet, float2 size) => new MotorState
        {
            body = new Body { position = feet, size = size }, facing = 1, fallPeakY = feet.y,
        };
    }

    public struct MotorEvents
    {
        public bool jumped, landed, steppedUp;
        public float landingSpeed;
        public float fallDistance;  // tiles fallen on landing
    }

    /// <summary>
    /// Pure platformer movement step: acceleration curves, coyote time, jump buffering, variable jump height
    /// (releasing jump cuts upward speed), softer gravity near the apex while jump is held, and the collision
    /// step-up shared with every other body. Deterministic for a given input sequence; EditMode-tested.
    /// </summary>
    public static class PlayerMotor
    {
        public static MotorEvents Step(ref MotorState s, in MotorInput input, in MotorParams p, ICollisionGrid grid, float dt)
        {
            var ev = new MotorEvents();
            ref var b = ref s.body;
            bool wasGrounded = b.grounded;

            // timers
            if (input.jumpPressed) s.buffer = p.jumpBuffer;
            else s.buffer = math.max(0f, s.buffer - dt);
            s.coyote = b.grounded ? p.coyoteTime : math.max(0f, s.coyote - dt);
            s.knockbackTime = math.max(0f, s.knockbackTime - dt);

            // horizontal: accelerate toward the target speed, decelerate faster than we accelerate
            float target = math.clamp(input.moveX, -1f, 1f) * p.maxRun;
            bool turning = target != 0f && math.sign(target) != math.sign(b.velocity.x) && b.velocity.x != 0f;
            float accel = b.grounded
                ? (target == 0f || turning ? p.groundDecel : p.groundAccel)
                : (target == 0f ? p.airDecel : p.airAccel);
            if (s.knockbackTime > 0f) accel *= 0.25f;
            b.velocity.x = MoveTowards(b.velocity.x, target, accel * dt);
            if (input.moveX > 0.1f) s.facing = 1;
            else if (input.moveX < -0.1f) s.facing = -1;

            // jump (buffered press + coyote window)
            if (s.buffer > 0f && (b.grounded || s.coyote > 0f))
            {
                b.velocity.y = p.jumpSpeed;
                b.grounded = false;
                s.buffer = 0f;
                s.coyote = 0f;
                s.rising = true;
                ev.jumped = true;
            }

            // variable height: releasing jump while rising cuts the ascent once
            if (s.rising && !input.jumpHeld && b.velocity.y > 0f)
            {
                b.velocity.y *= p.jumpCut;
                s.rising = false;
            }
            if (b.velocity.y <= 0f) s.rising = false;

            // gravity, softer around the apex while the button is held (more hang time, easier to aim)
            float g = p.gravity;
            if (input.jumpHeld && math.abs(b.velocity.y) < p.apexThreshold) g *= p.apexGravityScale;
            if (!b.grounded) b.velocity.y = math.max(b.velocity.y - g * dt, -p.maxFall);
            else if (b.velocity.y < 0f) b.velocity.y = 0f;
            if (b.grounded && !ev.jumped) b.velocity.y = -0.5f;   // keep contact so slopes/steps register

            b.dropThrough = input.downHeld;
            var r = TileCollision.Move(ref b, dt, grid, p.stepHeight);
            ev.steppedUp = r.steppedUp;
            if (r.hitCeiling) s.rising = false;

            if (!b.grounded) s.fallPeakY = math.max(s.fallPeakY, b.position.y);
            if (b.grounded && !wasGrounded)
            {
                ev.landed = true;
                ev.landingSpeed = r.landingSpeed;
                ev.fallDistance = math.max(0f, s.fallPeakY - b.position.y);
            }
            if (b.grounded) s.fallPeakY = b.position.y;
            return ev;
        }

        static float MoveTowards(float current, float target, float maxDelta) =>
            math.abs(target - current) <= maxDelta ? target : current + math.sign(target - current) * maxDelta;
    }
}
