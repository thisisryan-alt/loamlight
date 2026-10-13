using System.Runtime.InteropServices;
using Loamlight.Content;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;

namespace Loamlight.World.Rendering
{
    [StructLayout(LayoutKind.Sequential)]
    public struct TileVertex
    {
        public float3 position;
        public float3 normal;
        public float4 tangent;
        public Color32 color;
        public float2 uv;
    }

    /// <summary>
    /// Builds chunk meshes in Burst: mesh 3k is the wall layer of chunks[k], 3k+1 its block/object layer, 3k+2 liquids.
    /// Terrain autotiles from its air neighbours (reading across chunk borders), slopes and half blocks change the
    /// geometry, objects pick their cell from the tile's frame byte.
    /// </summary>
    [BurstCompile(CompileSynchronously = true)]
    public struct ChunkMeshJob : IJobParallelFor
    {
        public WorldLayout layout;
        [ReadOnly] public NativeArray<int> chunks;
        [ReadOnly] public NativeArray<ushort> blocks, walls;
        [ReadOnly] public NativeArray<byte> shapes, frames;
        [ReadOnly] public NativeArray<TileRenderInfo> tileInfo, wallInfo;
        [ReadOnly] public NativeArray<TileProps> tileProps;
        [ReadOnly] public NativeArray<float4> uvs;
        [ReadOnly] public NativeArray<VertexAttributeDescriptor> vertexLayout;
        public Mesh.MeshDataArray meshes;
        [ReadOnly] public NativeArray<byte> liquid, liquidKind;
        public int waterFrame, lavaFrame;
        public Color32 wallTint;
        public const int Layers = 3;

        static readonly float3 Normal = new float3(0, 0, -1);
        static readonly float4 Tangent = new float4(1, 0, 0, -1);

        public void Execute(int meshIndex)
        {
            int chunk = chunks[meshIndex / Layers];
            int layer = meshIndex % Layers;
            bool wallLayer = layer == 0;
            if (layer == 2) { BuildLiquids(meshIndex, chunk); return; }
            int cx = chunk % layout.ChunksX, cy = chunk / layout.ChunksX;
            int x0 = cx * WorldLayout.ChunkSize, y0 = cy * WorldLayout.ChunkSize;
            int baseIndex = chunk << 10;

            // pass 1: count quads/triangles
            int quads = 0, tris = 0;
            for (int i = 0; i < WorldLayout.ChunkArea; i++)
            {
                int x = x0 + (i & WorldLayout.ChunkMask), y = y0 + (i >> WorldLayout.ChunkShift);
                if (!layout.InBounds(x, y)) continue;
                int idx = baseIndex | i;
                if (wallLayer)
                {
                    if (walls[idx] != 0 && wallInfo[walls[idx]].layout != 0 && !HidesWall(x, y, idx)) quads++;
                }
                else if (blocks[idx] != 0 && tileInfo[blocks[idx]].layout != 0)
                {
                    var shape = (TileShape)shapes[idx];
                    if (shape == TileShape.SlopeUpLeft || shape == TileShape.SlopeUpRight) tris++;
                    else quads++;
                }
            }

            var md = meshes[meshIndex];
            int vertexCount = quads * 4 + tris * 3;
            int indexCount = quads * 6 + tris * 3;
            md.SetVertexBufferParams(vertexCount, vertexLayout);
            md.SetIndexBufferParams(indexCount, IndexFormat.UInt16);
            var verts = md.GetVertexData<TileVertex>();
            var inds = md.GetIndexData<ushort>();

            // pass 2: write geometry
            int v = 0, n = 0;
            for (int i = 0; i < WorldLayout.ChunkArea; i++)
            {
                int lx = i & WorldLayout.ChunkMask, ly = i >> WorldLayout.ChunkShift;
                int x = x0 + lx, y = y0 + ly;
                if (!layout.InBounds(x, y)) continue;
                int idx = baseIndex | i;
                if (wallLayer)
                {
                    ushort w = walls[idx];
                    if (w == 0 || wallInfo[w].layout == 0 || HidesWall(x, y, idx)) continue;
                    var info = wallInfo[w];
                    int frame = info.start + (int)(Hash(x, y) % (uint)math.max(1, info.count));
                    Quad(ref verts, ref inds, ref v, ref n, lx, ly, uvs[frame], wallTint, 0f, 1f);
                    continue;
                }

                ushort b = blocks[idx];
                if (b == 0) continue;
                var t = tileInfo[b];
                if (t.layout == 0) continue;
                int f;
                switch ((AtlasLayout)t.layout)
                {
                    case AtlasLayout.Terrain:
                        int mask = (Connects(x, y + 1) ? 0 : 1) | (Connects(x + 1, y) ? 0 : 2) | (Connects(x, y - 1) ? 0 : 4) | (Connects(x - 1, y) ? 0 : 8);
                        f = t.start + mask * t.variants + (int)(Hash(x, y) % t.variants);
                        break;
                    case AtlasLayout.Object:
                    {
                        // low 7 bits: cell index; bit 7: alternate state (open door) stored after the first w*h cells
                        int cell = frames[idx] & 0x7f;
                        if ((frames[idx] & 0x80) != 0) cell += t.w * t.h;
                        f = t.start + math.min(cell, t.count - 1);
                        break;
                    }
                    case AtlasLayout.Variants:
                        f = t.start + (int)(Hash(x, y) % (uint)math.max(1, t.count));
                        break;
                    default:
                        f = t.start + math.min(frames[idx], math.max(0, t.count - 1));
                        break;
                }
                var uv = uvs[f];
                var white = new Color32(255, 255, 255, 255);
                switch ((TileShape)shapes[idx])
                {
                    case TileShape.HalfBottom:
                        // show the top half of the texture so the block keeps its top edge
                        Quad(ref verts, ref inds, ref v, ref n, lx, ly, new float4(uv.x, (uv.y + uv.w) * 0.5f, uv.z, uv.w), white, 0f, 0.5f);
                        break;
                    case TileShape.SlopeUpRight:
                        Tri(ref verts, ref inds, ref v, ref n, lx, ly, uv, white, new float2(0, 0), new float2(1, 0), new float2(1, 1));
                        break;
                    case TileShape.SlopeUpLeft:
                        Tri(ref verts, ref inds, ref v, ref n, lx, ly, uv, white, new float2(0, 0), new float2(1, 0), new float2(0, 1));
                        break;
                    default:
                        Quad(ref verts, ref inds, ref v, ref n, lx, ly, uv, white, 0f, 1f);
                        break;
                }
            }

            md.subMeshCount = 1;
            var bounds = new Bounds(new Vector3(16f, 16f, 0f), new Vector3(32f, 32f, 1f));
            md.SetSubMesh(0, new SubMeshDescriptor(0, indexCount) { bounds = bounds, vertexCount = vertexCount },
                MeshUpdateFlags.DontRecalculateBounds | MeshUpdateFlags.DontValidateIndices | MeshUpdateFlags.DontNotifyMeshUsers);
        }

        void BuildLiquids(int meshIndex, int chunk)
        {
            int cx = chunk % layout.ChunksX, cy = chunk / layout.ChunksX;
            int x0 = cx * WorldLayout.ChunkSize, y0 = cy * WorldLayout.ChunkSize;
            int baseIndex = chunk << 10;
            int quads = 0;
            for (int i = 0; i < WorldLayout.ChunkArea; i++)
                if (liquid[baseIndex | i] > 0 && layout.InBounds(x0 + (i & WorldLayout.ChunkMask), y0 + (i >> WorldLayout.ChunkShift))) quads++;
            var md = meshes[meshIndex];
            md.SetVertexBufferParams(quads * 4, vertexLayout);
            md.SetIndexBufferParams(quads * 6, IndexFormat.UInt16);
            var verts = md.GetVertexData<TileVertex>();
            var inds = md.GetIndexData<ushort>();
            int v = 0, n = 0;
            for (int i = 0; i < WorldLayout.ChunkArea; i++)
            {
                int idx = baseIndex | i;
                int amount = liquid[idx];
                if (amount == 0) continue;
                int lx = i & WorldLayout.ChunkMask, ly = i >> WorldLayout.ChunkShift;
                int x = x0 + lx, y = y0 + ly;
                if (!layout.InBounds(x, y)) continue;
                bool lava = liquidKind[idx] == (byte)LiquidType.Lava;
                int frame = lava ? lavaFrame : waterFrame;
                if (frame < 0) frame = 0;
                bool covered = y + 1 < layout.Height && liquid[layout.Index(x, y + 1)] > 0;
                float h = covered ? 1f : math.max(1f / 16f, amount / 255f);
                var uv = uvs[covered ? frame : frame + 1];
                // surface frames keep their lighter top rows at the liquid's top edge
                var clipped = new float4(uv.x, uv.w - (uv.w - uv.y) * h, uv.z, uv.w);
                var color = lava ? new Color32(255, 255, 255, 245) : new Color32(255, 255, 255, 165);
                Quad(ref verts, ref inds, ref v, ref n, lx, ly, clipped, color, 0f, h);
            }
            md.subMeshCount = 1;
            var bounds = new Bounds(new Vector3(16f, 16f, 0f), new Vector3(32f, 32f, 1f));
            md.SetSubMesh(0, new SubMeshDescriptor(0, quads * 6) { bounds = bounds, vertexCount = quads * 4 },
                MeshUpdateFlags.DontRecalculateBounds | MeshUpdateFlags.DontValidateIndices | MeshUpdateFlags.DontNotifyMeshUsers);
        }

        bool Connects(int x, int y)
        {
            if (!layout.InBounds(x, y)) return true;
            ushort b = blocks[layout.Index(x, y)];
            if (b == 0) return false;
            var p = tileProps[b];
            return p.kind == (byte)TileKind.Terrain && p.solid != 0;
        }

        /// <summary>A wall fully covered by an opaque, fully surrounded terrain tile is never visible.</summary>
        bool HidesWall(int x, int y, int idx)
        {
            ushort b = blocks[idx];
            if (b == 0 || shapes[idx] != (byte)TileShape.Full) return false;
            var p = tileProps[b];
            if (p.kind != (byte)TileKind.Terrain || p.solid == 0) return false;
            return Connects(x, y + 1) && Connects(x + 1, y) && Connects(x, y - 1) && Connects(x - 1, y);
        }

        static uint Hash(int x, int y)
        {
            uint h = (uint)x * 374761393u + (uint)y * 668265263u;
            h = (h ^ (h >> 13)) * 1274126177u;
            return h ^ (h >> 16);
        }

        static void Quad(ref NativeArray<TileVertex> verts, ref NativeArray<ushort> inds, ref int v, ref int n,
            int lx, int ly, float4 uv, Color32 color, float yBottom, float yTop)
        {
            verts[v + 0] = new TileVertex { position = new float3(lx, ly + yBottom, 0), normal = Normal, tangent = Tangent, color = color, uv = new float2(uv.x, uv.y) };
            verts[v + 1] = new TileVertex { position = new float3(lx + 1, ly + yBottom, 0), normal = Normal, tangent = Tangent, color = color, uv = new float2(uv.z, uv.y) };
            verts[v + 2] = new TileVertex { position = new float3(lx + 1, ly + yTop, 0), normal = Normal, tangent = Tangent, color = color, uv = new float2(uv.z, uv.w) };
            verts[v + 3] = new TileVertex { position = new float3(lx, ly + yTop, 0), normal = Normal, tangent = Tangent, color = color, uv = new float2(uv.x, uv.w) };
            inds[n + 0] = (ushort)v; inds[n + 1] = (ushort)(v + 1); inds[n + 2] = (ushort)(v + 2);
            inds[n + 3] = (ushort)v; inds[n + 4] = (ushort)(v + 2); inds[n + 5] = (ushort)(v + 3);
            v += 4;
            n += 6;
        }

        static void Tri(ref NativeArray<TileVertex> verts, ref NativeArray<ushort> inds, ref int v, ref int n,
            int lx, int ly, float4 uv, Color32 color, float2 a, float2 b, float2 c)
        {
            verts[v + 0] = Vert(lx, ly, uv, color, a);
            verts[v + 1] = Vert(lx, ly, uv, color, b);
            verts[v + 2] = Vert(lx, ly, uv, color, c);
            inds[n + 0] = (ushort)v; inds[n + 1] = (ushort)(v + 1); inds[n + 2] = (ushort)(v + 2);
            v += 3;
            n += 3;
        }

        static TileVertex Vert(int lx, int ly, float4 uv, Color32 color, float2 p) => new TileVertex
        {
            position = new float3(lx + p.x, ly + p.y, 0), normal = Normal, tangent = Tangent, color = color,
            uv = new float2(math.lerp(uv.x, uv.z, p.x), math.lerp(uv.y, uv.w, p.y)),
        };
    }

    public static class ChunkMeshLayout
    {
        public static NativeArray<VertexAttributeDescriptor> Create(Allocator allocator)
        {
            var a = new NativeArray<VertexAttributeDescriptor>(5, allocator);
            a[0] = new VertexAttributeDescriptor(VertexAttribute.Position, VertexAttributeFormat.Float32, 3);
            a[1] = new VertexAttributeDescriptor(VertexAttribute.Normal, VertexAttributeFormat.Float32, 3);
            a[2] = new VertexAttributeDescriptor(VertexAttribute.Tangent, VertexAttributeFormat.Float32, 4);
            a[3] = new VertexAttributeDescriptor(VertexAttribute.Color, VertexAttributeFormat.UNorm8, 4);
            a[4] = new VertexAttributeDescriptor(VertexAttribute.TexCoord0, VertexAttributeFormat.Float32, 2);
            return a;
        }
    }
}
