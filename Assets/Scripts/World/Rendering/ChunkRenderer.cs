using System.Collections.Generic;
using System.Diagnostics;
using Unity.Collections;
using Unity.Jobs;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.Rendering;
using Debug = UnityEngine.Debug;

namespace Loamlight.World.Rendering
{
    /// <summary>
    /// Streams chunk meshes around the camera: only chunks in view (plus a margin) own a pooled renderer, and only
    /// chunks that are newly visible or flagged dirty are rebuilt, in Burst batches capped per frame.
    /// No GameObject per tile: one GameObject per visible chunk with a wall mesh and a block mesh.
    /// </summary>
    public sealed class ChunkRenderer : MonoBehaviour
    {
        static readonly ProfilerMarker RebuildMarker = new ProfilerMarker(ProfilerCategory.Scripts, "Loamlight.ChunkRebuild");

        public int marginChunks = 1;
        public int rebuildBudget = 48;
        public Color32 wallTint = new Color32(150, 150, 160, 255);

        WorldData _world;
        WorldRenderData _render;
        Camera _camera;
        readonly Dictionary<int, ChunkView> _active = new Dictionary<int, ChunkView>();
        readonly Stack<ChunkView> _pool = new Stack<ChunkView>();
        readonly HashSet<int> _needsBuild = new HashSet<int>();
        readonly List<int> _buildList = new List<int>();
        readonly List<int> _scratch = new List<int>();
        NativeArray<VertexAttributeDescriptor> _vertexLayout;
        RectInt _visible;

        public int ActiveChunks => _active.Count;
        public int LastRebuildCount { get; private set; }
        public double LastRebuildMs { get; private set; }
        public long TotalRebuilds { get; private set; }
        public RectInt VisibleChunkRect => _visible;

        sealed class ChunkView
        {
            public GameObject root;
            public Mesh wallMesh, blockMesh;
            public int chunk = -1;
        }

        public void Init(WorldData world, WorldRenderData render, Camera cam)
        {
            _world = world;
            _render = render;
            _camera = cam;
            if (!_vertexLayout.IsCreated) _vertexLayout = ChunkMeshLayout.Create(Allocator.Persistent);
            foreach (var v in _active.Values) Release(v);
            _active.Clear();
            _needsBuild.Clear();
        }

        void OnDestroy()
        {
            if (_vertexLayout.IsCreated) _vertexLayout.Dispose();
            foreach (var v in _active.Values) DestroyView(v);
            while (_pool.Count > 0) DestroyView(_pool.Pop());
        }

        void LateUpdate()
        {
            if (_world == null || _camera == null) return;
            UpdateVisibility();
            Rebuild(rebuildBudget);
        }

        /// <summary>Builds every visible chunk right now (first frame after load, captures, tests).</summary>
        public void BuildAllVisibleNow()
        {
            if (_world == null) return;
            UpdateVisibility();
            Rebuild(int.MaxValue);
        }

        void UpdateVisibility()
        {
            var layout = _world.Layout;
            float halfH = _camera.orthographicSize, halfW = halfH * _camera.aspect;
            var p = _camera.transform.position;
            int minX = Mathf.FloorToInt((p.x - halfW) / WorldLayout.ChunkSize) - marginChunks;
            int maxX = Mathf.FloorToInt((p.x + halfW) / WorldLayout.ChunkSize) + marginChunks;
            int minY = Mathf.FloorToInt((p.y - halfH) / WorldLayout.ChunkSize) - marginChunks;
            int maxY = Mathf.FloorToInt((p.y + halfH) / WorldLayout.ChunkSize) + marginChunks;
            minX = Mathf.Clamp(minX, 0, layout.ChunksX - 1); maxX = Mathf.Clamp(maxX, 0, layout.ChunksX - 1);
            minY = Mathf.Clamp(minY, 0, layout.ChunksY - 1); maxY = Mathf.Clamp(maxY, 0, layout.ChunksY - 1);
            _visible = new RectInt(minX, minY, maxX - minX + 1, maxY - minY + 1);

            _scratch.Clear();
            foreach (var kv in _active)
            {
                int cx = kv.Key % layout.ChunksX, cy = kv.Key / layout.ChunksX;
                if (cx < minX || cx > maxX || cy < minY || cy > maxY) _scratch.Add(kv.Key);
            }
            foreach (var c in _scratch)
            {
                Release(_active[c]);
                _active.Remove(c);
                _needsBuild.Remove(c);
            }

            for (int cy = minY; cy <= maxY; cy++)
            for (int cx = minX; cx <= maxX; cx++)
            {
                int c = cy * layout.ChunksX + cx;
                if (!_active.ContainsKey(c))
                {
                    _active[c] = Acquire(c, cx, cy);
                    _needsBuild.Add(c);
                }
                else if ((_world.ChunkFlags[c] & WorldData.FlagMeshDirty) != 0)
                {
                    _needsBuild.Add(c);
                }
            }
        }

        void Rebuild(int budget)
        {
            LastRebuildCount = 0;
            if (_needsBuild.Count == 0) return;
            using var _ = RebuildMarker.Auto();
            var sw = Stopwatch.StartNew();

            // nearest chunks to the camera first
            _buildList.Clear();
            _buildList.AddRange(_needsBuild);
            var cam = _camera.transform.position;
            int chunksX = _world.Layout.ChunksX;
            _buildList.Sort((a, b) => Dist(a).CompareTo(Dist(b)));
            float Dist(int c) => (new Vector2((c % chunksX + 0.5f) * 32f, (c / chunksX + 0.5f) * 32f) - (Vector2)cam).sqrMagnitude;
            if (_buildList.Count > budget) _buildList.RemoveRange(budget, _buildList.Count - budget);

            int count = _buildList.Count;
            var chunks = new NativeArray<int>(count, Allocator.TempJob);
            for (int i = 0; i < count; i++) chunks[i] = _buildList[i];
            var meshData = Mesh.AllocateWritableMeshData(count * 2);
            var job = new ChunkMeshJob
            {
                layout = _world.Layout, chunks = chunks, blocks = _world.Blocks, walls = _world.Walls,
                shapes = _world.Shapes, frames = _world.Frames, tileInfo = _render.Tiles, wallInfo = _render.Walls,
                tileProps = _render.TileProps, uvs = _render.Uvs, vertexLayout = _vertexLayout, meshes = meshData,
                wallTint = wallTint,
            };
            job.Schedule(count * 2, 1).Complete();
            chunks.Dispose();

            var meshes = new Mesh[count * 2];
            for (int i = 0; i < count; i++)
            {
                var view = _active[_buildList[i]];
                meshes[i * 2] = view.wallMesh;
                meshes[i * 2 + 1] = view.blockMesh;
            }
            Mesh.ApplyAndDisposeWritableMeshData(meshData, meshes,
                MeshUpdateFlags.DontRecalculateBounds | MeshUpdateFlags.DontValidateIndices);
            var bounds = new Bounds(new Vector3(16f, 16f, 0f), new Vector3(32f, 32f, 1f));
            foreach (var m in meshes) m.bounds = bounds;

            foreach (var c in _buildList)
            {
                _needsBuild.Remove(c);
                _world.ChunkFlags[c] = (byte)(_world.ChunkFlags[c] & ~WorldData.FlagMeshDirty);
            }
            LastRebuildCount = count;
            TotalRebuilds += count;
            LastRebuildMs = sw.Elapsed.TotalMilliseconds;
        }

        ChunkView Acquire(int chunk, int cx, int cy)
        {
            var view = _pool.Count > 0 ? _pool.Pop() : CreateView();
            view.chunk = chunk;
            view.root.name = $"Chunk {cx},{cy}";
            view.root.transform.position = new Vector3(cx * WorldLayout.ChunkSize, cy * WorldLayout.ChunkSize, 0f);
            view.root.SetActive(true);
            return view;
        }

        void Release(ChunkView view)
        {
            view.chunk = -1;
            view.wallMesh.Clear();
            view.blockMesh.Clear();
            view.root.SetActive(false);
            _pool.Push(view);
        }

        ChunkView CreateView()
        {
            var root = new GameObject("Chunk");
            root.transform.SetParent(transform, false);
            var view = new ChunkView
            {
                root = root,
                wallMesh = new Mesh { name = "chunk walls" },
                blockMesh = new Mesh { name = "chunk blocks" },
            };
            view.wallMesh.MarkDynamic();
            view.blockMesh.MarkDynamic();
            AddLayer(root.transform, "Walls", view.wallMesh, "Walls");
            AddLayer(root.transform, "Blocks", view.blockMesh, "World");
            return view;
        }

        void AddLayer(Transform parent, string name, Mesh mesh, string sortingLayer)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var r = go.AddComponent<MeshRenderer>();
            r.sharedMaterial = _render.Material;
            r.sortingLayerName = sortingLayer;
            r.shadowCastingMode = ShadowCastingMode.Off;
            r.receiveShadows = false;
            r.lightProbeUsage = LightProbeUsage.Off;
            r.reflectionProbeUsage = ReflectionProbeUsage.Off;
        }

        static void DestroyView(ChunkView v)
        {
            if (v.wallMesh != null) Destroy(v.wallMesh);
            if (v.blockMesh != null) Destroy(v.blockMesh);
            if (v.root != null) Destroy(v.root);
        }
    }
}
