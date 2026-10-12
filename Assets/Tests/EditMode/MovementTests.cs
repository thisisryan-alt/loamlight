using Loamlight.Player;
using Loamlight.World;
using Loamlight.World.Collision;
using NUnit.Framework;
using Unity.Mathematics;

namespace Loamlight.Tests
{
    /// <summary>Tiny hand-built tile grid for movement tests: '#' solid, '-' platform, '/' slope up-right, '\' slope up-left, '_' half.</summary>
    public sealed class FakeGrid : ICollisionGrid
    {
        readonly string[] _rows;   // row 0 = top
        public FakeGrid(params string[] rows) => _rows = rows;
        public int Height => _rows.Length;

        public CellCollision Cell(int x, int y, out TileShape shape)
        {
            shape = TileShape.Full;
            int r = _rows.Length - 1 - y;
            if (y >= _rows.Length) return CellCollision.Empty;
            if (r >= _rows.Length || x < 0 || x >= _rows[r].Length) return CellCollision.Solid;
            switch (_rows[r][x])
            {
                case '#': return CellCollision.Solid;
                case '-': return CellCollision.Platform;
                case '/': shape = TileShape.SlopeUpRight; return CellCollision.Solid;
                case '\\': shape = TileShape.SlopeUpLeft; return CellCollision.Solid;
                case '_': shape = TileShape.HalfBottom; return CellCollision.Solid;
                default: return CellCollision.Empty;
            }
        }
    }

    public class CollisionTests
    {
        static Body BodyAt(float x, float y) => new Body { position = new float2(x, y), size = new float2(1.25f, 2.6f) };

        [Test]
        public void FallsAndLandsOnFloor()
        {
            var g = new FakeGrid("..........", "..........", "..........", "..........", "..........", "##########");
            var b = BodyAt(5f, 4f);
            for (int i = 0; i < 120; i++) { b.velocity.y -= 80f / 60f; TileCollision.Move(ref b, 1f / 60f, g, 1.05f); }
            Assert.That(b.position.y, Is.EqualTo(1f).Within(0.01f));
            Assert.That(b.grounded, Is.True);
        }

        [Test]
        public void FastFallNeverTunnels()
        {
            var g = new FakeGrid(".....", ".....", ".....", ".....", "#####");
            var b = BodyAt(2.5f, 4f);
            b.velocity.y = -200f;   // 3.3 tiles per step
            TileCollision.Move(ref b, 1f / 60f, g, 1.05f);
            Assert.That(b.position.y, Is.EqualTo(1f).Within(0.01f));
        }

        [Test]
        public void WalksUpOneBlockStepButNotTwo()
        {
            // a single block at x=4, then a two-high column at x=8
            var g = new FakeGrid("..........", "..........", "..........", "........#.", "....#...#.", "##########");
            var b = BodyAt(2.5f, 1f);
            b.grounded = true;
            float maxY = 0f;
            for (int i = 0; i < 90; i++)
            {
                b.velocity = new float2(6f, b.grounded ? -0.5f : b.velocity.y - 80f / 60f);
                TileCollision.Move(ref b, 1f / 60f, g, 1.05f);
                maxY = math.max(maxY, b.position.y);
            }
            Assert.That(maxY, Is.EqualTo(2f).Within(0.05f), "stepped up onto the single block");
            Assert.That(b.position.y, Is.EqualTo(1f).Within(0.05f), "back on the floor");
            Assert.That(b.Right, Is.LessThanOrEqualTo(8f + 0.01f), "the two-high column blocks");
        }

        [Test]
        public void WalksUpSlopeSmoothly()
        {
            // slope up-right at x=4 rising onto a ledge at height 2
            var g = new FakeGrid("..........", "..........", "..........", "..........", "..../#####", "##########");
            var b = BodyAt(2f, 1f);
            b.grounded = true;
            float lastY = b.position.y;
            bool monotonic = true;
            for (int i = 0; i < 60; i++)
            {
                b.velocity = new float2(4f, -0.5f);
                TileCollision.Move(ref b, 1f / 60f, g, 1.05f);
                if (b.position.y < lastY - 0.01f) monotonic = false;
                lastY = b.position.y;
            }
            Assert.That(monotonic, Is.True, "height never dips while climbing the slope");
            Assert.That(b.position.y, Is.EqualTo(2f).Within(0.05f));
            Assert.That(b.position.x, Is.GreaterThan(5.5f));
        }

        [Test]
        public void HalfBlockIsHalfHigh()
        {
            var g = new FakeGrid(".....", ".....", ".....", "..._.", "#####");
            var b = BodyAt(3.5f, 3f);
            for (int i = 0; i < 60; i++) { b.velocity.y -= 80f / 60f; TileCollision.Move(ref b, 1f / 60f, g, 1.05f); }
            Assert.That(b.position.y, Is.EqualTo(1.5f).Within(0.02f));
        }

        [Test]
        public void PlatformsAreOneWay()
        {
            var g = new FakeGrid("......", "......", "......", "......", "------", "......", "######");
            // from below: jump through
            var up = BodyAt(3f, 1f);
            up.velocity.y = 20f;
            TileCollision.Move(ref up, 0.2f, g, 1.05f);
            Assert.That(up.position.y, Is.GreaterThan(3f), "passes up through the platform");
            // from above: lands on top (platform row y=2, top at 3)
            var down = BodyAt(3f, 5f);
            for (int i = 0; i < 60; i++) { down.velocity.y -= 80f / 60f; TileCollision.Move(ref down, 1f / 60f, g, 1.05f); }
            Assert.That(down.position.y, Is.EqualTo(3f).Within(0.02f));
            // holding Down drops through
            down.dropThrough = true;
            for (int i = 0; i < 60; i++) { down.velocity.y -= 80f / 60f; TileCollision.Move(ref down, 1f / 60f, g, 1.05f); }
            Assert.That(down.position.y, Is.EqualTo(1f).Within(0.02f));
        }

        [Test]
        public void CeilingStopsAJump()
        {
            var g = new FakeGrid("######", "......", "......", "......", "......", "######");
            var b = BodyAt(3f, 1f);
            b.velocity.y = 30f;
            for (int i = 0; i < 10; i++) TileCollision.Move(ref b, 1f / 60f, g, 1.05f);
            Assert.That(b.Top, Is.LessThanOrEqualTo(5f + 0.01f));
        }
    }

    public class MotorTests
    {
        static readonly FakeGrid Flat = new FakeGrid(
            "....................",
            "....................",
            "....................",
            "....................",
            "....................",
            "....................",
            "....................",
            "....................",
            "....................",
            "####################");

        static MotorState Standing(float x = 5f) => MotorState.At(new float2(x, 1f), PlayerController.Size);

        static MotorEvents Run(ref MotorState s, MotorInput input, int steps, ICollisionGrid g = null)
        {
            var p = MotorParams.Default;
            MotorEvents last = default;
            for (int i = 0; i < steps; i++)
            {
                var ev = PlayerMotor.Step(ref s, input, p, g ?? Flat, 1f / 60f);
                if (ev.jumped) last.jumped = true;
                if (ev.landed) last.landed = true;
                input.jumpPressed = false;
            }
            return last;
        }

        [Test]
        public void ReachesMaxRunSpeed()
        {
            var s = Standing();
            Run(ref s, new MotorInput(), 5);
            Run(ref s, new MotorInput { moveX = 1f }, 30);
            Assert.That(s.body.velocity.x, Is.EqualTo(MotorParams.Default.maxRun).Within(0.01f));
        }

        [Test]
        public void FullJumpHeightAndShortHop()
        {
            var p = MotorParams.Default;
            var s = Standing();
            Run(ref s, new MotorInput(), 5);
            float peak = 0f;
            var input = new MotorInput { jumpPressed = true, jumpHeld = true };
            for (int i = 0; i < 90; i++) { PlayerMotor.Step(ref s, input, p, Flat, 1f / 60f); input.jumpPressed = false; peak = math.max(peak, s.body.position.y - 1f); }
            Assert.That(peak, Is.InRange(5f, 7f), "held jump");

            var h = Standing();
            Run(ref h, new MotorInput(), 5);
            float hop = 0f;
            input = new MotorInput { jumpPressed = true, jumpHeld = true };
            for (int i = 0; i < 90; i++)
            {
                if (i == 3) input.jumpHeld = false;
                PlayerMotor.Step(ref h, input, p, Flat, 1f / 60f);
                input.jumpPressed = false;
                hop = math.max(hop, h.body.position.y - 1f);
            }
            Assert.That(hop, Is.LessThan(peak * 0.5f), $"released early: {hop:0.00} vs {peak:0.00}");
            Assert.That(hop, Is.GreaterThan(0.5f));
        }

        [Test]
        public void CoyoteTimeAllowsJumpJustAfterLeavingLedge()
        {
            var ledge = new FakeGrid(
                "....................",
                "....................",
                "....................",
                "....................",
                "....................",
                "....................",
                "....................",
                "######..............",
                "######..............",
                "######..............");
            var p = MotorParams.Default;
            var s = MotorState.At(new float2(4f, 3f), PlayerController.Size);
            Run(ref s, new MotorInput(), 5, ledge);
            // run off the ledge until airborne
            int airborneAt = -1;
            for (int i = 0; i < 60 && airborneAt < 0; i++)
            {
                PlayerMotor.Step(ref s, new MotorInput { moveX = 1f }, p, ledge, 1f / 60f);
                if (!s.body.grounded) airborneAt = i;
            }
            Assert.That(airborneAt, Is.GreaterThan(0));
            // 3 steps (50 ms) later, inside the 100 ms coyote window: jump still works
            Run(ref s, new MotorInput { moveX = 1f }, 3, ledge);
            var ev = PlayerMotor.Step(ref s, new MotorInput { moveX = 1f, jumpPressed = true, jumpHeld = true }, p, ledge, 1f / 60f);
            Assert.That(ev.jumped, Is.True, "coyote jump");
        }

        [Test]
        public void NoJumpAfterCoyoteWindow()
        {
            var p = MotorParams.Default;
            var s = MotorState.At(new float2(5f, 8f), PlayerController.Size);   // mid-air, never grounded
            Run(ref s, new MotorInput(), 12);
            var ev = PlayerMotor.Step(ref s, new MotorInput { jumpPressed = true, jumpHeld = true }, p, Flat, 1f / 60f);
            Assert.That(ev.jumped, Is.False);
        }

        [Test]
        public void JumpBufferFiresOnLanding()
        {
            var p = MotorParams.Default;
            var s = MotorState.At(new float2(5f, 1.6f), PlayerController.Size);   // falling, just above the floor
            s.body.velocity.y = -10f;
            // press jump while still in the air, within 120 ms of landing
            PlayerMotor.Step(ref s, new MotorInput { jumpPressed = true, jumpHeld = true }, p, Flat, 1f / 60f);
            bool jumped = false;
            for (int i = 0; i < 6 && !jumped; i++)
                jumped = PlayerMotor.Step(ref s, new MotorInput { jumpHeld = true }, p, Flat, 1f / 60f).jumped;
            Assert.That(jumped, Is.True, "buffered jump");
        }

        [Test]
        public void LandingReportsFallDistance()
        {
            var p = MotorParams.Default;
            var s = MotorState.At(new float2(5f, 9f), PlayerController.Size);
            MotorEvents landed = default;
            for (int i = 0; i < 120 && !landed.landed; i++) landed = PlayerMotor.Step(ref s, new MotorInput(), p, Flat, 1f / 60f);
            Assert.That(landed.landed, Is.True);
            Assert.That(landed.fallDistance, Is.EqualTo(8f).Within(0.1f));
        }
    }
}
