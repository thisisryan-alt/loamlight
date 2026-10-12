using Loamlight.Content;
using Unity.Collections;
using Unity.Mathematics;

namespace Loamlight.World.Collision
{
    public enum CellCollision : byte { Empty = 0, Solid = 1, Platform = 2 }

    /// <summary>What entity physics needs to know about the tile grid.</summary>
    public interface ICollisionGrid
    {
        /// <summary>Collision class and shape of the tile at (x, y). Outside the world: solid (except above the top).</summary>
        CellCollision Cell(int x, int y, out TileShape shape);
    }

    /// <summary>Collision view of a <see cref="WorldData"/>: terrain is solid, platforms are one-way, closed doors solid.</summary>
    public sealed class WorldCollisionGrid : ICollisionGrid
    {
        readonly WorldData _world;
        readonly NativeArray<TileProps> _props;

        public WorldCollisionGrid(WorldData world, NativeArray<TileProps> props)
        {
            _world = world;
            _props = props;
        }

        public CellCollision Cell(int x, int y, out TileShape shape)
        {
            shape = TileShape.Full;
            if (y >= _world.Height) return CellCollision.Empty;
            if (x < 0 || y < 0 || x >= _world.Width) return CellCollision.Solid;
            int i = _world.Layout.Index(x, y);
            ushort b = _world.Blocks[i];
            if (b == 0) return CellCollision.Empty;
            var p = _props[b];
            switch ((TileKind)p.kind)
            {
                case TileKind.Terrain:
                    if (p.solid == 0) return CellCollision.Empty;
                    shape = (TileShape)_world.Shapes[i];
                    return CellCollision.Solid;
                case TileKind.Platform:
                    return CellCollision.Platform;
                case TileKind.Door:
                    // frame bit 7 = open
                    return (_world.Frames[i] & 0x80) != 0 ? CellCollision.Empty : CellCollision.Solid;
                default:
                    return CellCollision.Empty;
            }
        }
    }

    /// <summary>An axis-aligned box standing on its bottom-centre point, in tile units.</summary>
    public struct Body
    {
        public float2 position;   // bottom centre
        public float2 velocity;
        public float2 size;
        public bool grounded;
        public bool dropThrough;  // ignore one-way platforms (holding Down)

        public float Left => position.x - size.x * 0.5f;
        public float Right => position.x + size.x * 0.5f;
        public float Bottom => position.y;
        public float Top => position.y + size.y;
        public float2 Center => new float2(position.x, position.y + size.y * 0.5f);
    }

    public struct MoveResult
    {
        public bool grounded, hitCeiling, hitLeft, hitRight, steppedUp;
        public float landingSpeed;  // downward speed at the moment of landing (fall damage, dust)
    }

    /// <summary>
    /// Kinematic AABB-versus-tile movement. Tiles are height fields standing on their bottom edge (full = 1, half =
    /// 0.5, slopes linear), which makes slopes, half blocks and the one-block auto step-up the same rule: if the
    /// surface ahead rises less than the step height and there is head room, walk up onto it.
    /// Movement is split into sub-steps of under half a tile, so fast bodies never tunnel.
    /// </summary>
    public static class TileCollision
    {
        const float Eps = 0.001f;
        const float MaxSubStep = 0.45f;

        public static float SurfaceTop(TileShape shape, float u)
        {
            switch (shape)
            {
                case TileShape.HalfBottom: return 0.5f;
                case TileShape.SlopeUpRight: return math.saturate(u);
                case TileShape.SlopeUpLeft: return 1f - math.saturate(u);
                default: return 1f;
            }
        }

        /// <summary>Highest surface of a tile over the horizontal interval [x0, x1] (world units).</summary>
        static float MaxTop(TileShape shape, int tx, float x0, float x1)
        {
            float u0 = math.saturate(x0 - tx), u1 = math.saturate(x1 - tx);
            return math.max(SurfaceTop(shape, u0), SurfaceTop(shape, u1));
        }

        public static MoveResult Move(ref Body body, float dt, ICollisionGrid grid, float stepHeight)
        {
            var result = new MoveResult();
            bool wasGrounded = body.grounded;
            float2 delta = body.velocity * dt;
            int steps = math.max(1, (int)math.ceil(math.cmax(math.abs(delta)) / MaxSubStep));
            float2 d = delta / steps;
            body.grounded = false;
            for (int s = 0; s < steps; s++)
            {
                if (d.x != 0f) MoveX(ref body, d.x, grid, wasGrounded || result.grounded ? stepHeight : 0.3f, ref result);
                if (d.y != 0f) MoveY(ref body, d.y, grid, ref result);
            }
            // stick to the ground when walking down slopes and steps instead of bouncing off them
            if (!result.grounded && wasGrounded && body.velocity.y <= 0f)
            {
                float drop = ProbeGround(body, grid, 0.55f);
                if (drop >= 0f)
                {
                    body.position.y -= drop;
                    result.grounded = true;
                }
            }
            if (!result.grounded && body.velocity.y <= 0f && ProbeGround(body, grid, Eps * 4f) >= 0f) result.grounded = true;
            body.grounded = result.grounded;
            if (body.grounded && body.velocity.y < 0f) body.velocity.y = 0f;
            return result;
        }

        static void MoveX(ref Body b, float dx, ICollisionGrid grid, float stepHeight, ref MoveResult r)
        {
            float newX = b.position.x + dx;
            float left = newX - b.size.x * 0.5f, right = newX + b.size.x * 0.5f;
            int tx0 = (int)math.floor(left + Eps), tx1 = (int)math.floor(right - Eps);
            int ty0 = (int)math.floor(b.Bottom + Eps), ty1 = (int)math.floor(b.Top - Eps);
            float needUp = 0f;
            bool blocked = false;
            float clampX = newX;
            for (int tx = tx0; tx <= tx1; tx++)
            for (int ty = ty0; ty <= ty1; ty++)
            {
                if (grid.Cell(tx, ty, out var shape) != CellCollision.Solid) continue;
                float top = ty + MaxTop(shape, tx, left, right);
                if (top <= b.Bottom + Eps) continue;          // we are above this tile's surface
                float rise = top - b.Bottom;
                if (rise <= stepHeight)
                {
                    needUp = math.max(needUp, rise);
                    continue;
                }
                blocked = true;
                if (dx > 0f) clampX = math.min(clampX, tx - b.size.x * 0.5f - Eps);
                else clampX = math.max(clampX, tx + 1 + b.size.x * 0.5f + Eps);
            }
            if (!blocked && needUp > 0f)
            {
                if (Free(grid, left, right, b.Bottom + needUp, b.Top + needUp))
                {
                    b.position.y += needUp + Eps;
                    r.steppedUp = true;
                }
                else
                {
                    blocked = true;
                    clampX = dx > 0f ? math.floor(right) - b.size.x * 0.5f - Eps : math.floor(left) + 1 + b.size.x * 0.5f + Eps;
                }
            }
            if (blocked)
            {
                b.position.x = dx > 0f ? math.min(newX, clampX) : math.max(newX, clampX);
                if (dx > 0f) r.hitRight = true; else r.hitLeft = true;
                b.velocity.x = 0f;
            }
            else b.position.x = newX;
        }

        static void MoveY(ref Body b, float dy, ICollisionGrid grid, ref MoveResult r)
        {
            float left = b.Left, right = b.Right;
            int tx0 = (int)math.floor(left + Eps), tx1 = (int)math.floor(right - Eps);
            if (dy < 0f)
            {
                float newY = b.position.y + dy;
                int ty0 = (int)math.floor(newY), ty1 = (int)math.floor(b.Bottom + Eps);
                float land = float.NegativeInfinity;
                for (int tx = tx0; tx <= tx1; tx++)
                for (int ty = ty0; ty <= ty1; ty++)
                {
                    var c = grid.Cell(tx, ty, out var shape);
                    if (c == CellCollision.Empty) continue;
                    float top = c == CellCollision.Platform ? ty + 1f : ty + MaxTop(shape, tx, left, right);
                    if (c == CellCollision.Platform && b.dropThrough) continue;
                    if (b.Bottom >= top - Eps - (c == CellCollision.Solid ? 0.02f : 0f) && newY < top) land = math.max(land, top);
                }
                if (land > float.NegativeInfinity)
                {
                    r.landingSpeed = math.max(r.landingSpeed, -b.velocity.y);
                    b.position.y = land;
                    b.velocity.y = 0f;
                    r.grounded = true;
                }
                else b.position.y = newY;
            }
            else
            {
                float newTop = b.Top + dy;
                int ty0 = (int)math.floor(b.Top - Eps), ty1 = (int)math.floor(newTop - Eps);
                float ceiling = float.PositiveInfinity;
                for (int tx = tx0; tx <= tx1; tx++)
                for (int ty = ty0; ty <= ty1; ty++)
                {
                    if (grid.Cell(tx, ty, out _) != CellCollision.Solid) continue;
                    if (ty >= b.Top - Eps) ceiling = math.min(ceiling, ty);
                }
                if (ceiling < float.PositiveInfinity && newTop > ceiling)
                {
                    b.position.y = ceiling - b.size.y - Eps;
                    b.velocity.y = 0f;
                    r.hitCeiling = true;
                }
                else b.position.y += dy;
            }
        }

        /// <summary>Distance down to the nearest supporting surface within maxDrop, or -1.</summary>
        public static float ProbeGround(Body b, ICollisionGrid grid, float maxDrop)
        {
            float left = b.Left, right = b.Right;
            int tx0 = (int)math.floor(left + Eps), tx1 = (int)math.floor(right - Eps);
            int ty0 = (int)math.floor(b.Bottom - maxDrop), ty1 = (int)math.floor(b.Bottom + Eps);
            float best = -1f;
            for (int tx = tx0; tx <= tx1; tx++)
            for (int ty = ty0; ty <= ty1; ty++)
            {
                var c = grid.Cell(tx, ty, out var shape);
                if (c == CellCollision.Empty || (c == CellCollision.Platform && b.dropThrough)) continue;
                float top = c == CellCollision.Platform ? ty + 1f : ty + MaxTop(shape, tx, left, right);
                float drop = b.Bottom - top;
                if (drop >= -Eps * 2f && drop <= maxDrop && (best < 0f || drop < best)) best = math.max(0f, drop);
            }
            return best;
        }

        /// <summary>True if the box overlaps no solid tile (platforms ignored).</summary>
        public static bool Free(ICollisionGrid grid, float left, float right, float bottom, float top)
        {
            int tx0 = (int)math.floor(left + Eps), tx1 = (int)math.floor(right - Eps);
            int ty0 = (int)math.floor(bottom + Eps), ty1 = (int)math.floor(top - Eps);
            for (int tx = tx0; tx <= tx1; tx++)
            for (int ty = ty0; ty <= ty1; ty++)
            {
                if (grid.Cell(tx, ty, out var shape) != CellCollision.Solid) continue;
                if (ty + MaxTop(shape, tx, left, right) > bottom + Eps) return false;
            }
            return true;
        }

        public static bool Overlaps(Body b, int tx, int ty) =>
            b.Right > tx + Eps && b.Left < tx + 1 - Eps && b.Top > ty + Eps && b.Bottom < ty + 1 - Eps;
    }
}
