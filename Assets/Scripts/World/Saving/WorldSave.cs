using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Loamlight.Content;
using Loamlight.Core;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;

namespace Loamlight.World.Saving
{
    /// <summary>Everything about a save besides the tile arrays.</summary>
    public sealed class SaveMeta
    {
        public string name = "World";
        public uint seed;
        public int width, height;
        public double playSeconds;
        public float timeOfDay = 0.3f;
        public string createdUtc, savedUtc;
        /// <summary>Opaque JSON owned by the game layer (player, inventory, chests, bosses defeated...).</summary>
        public string gameJson = "{}";
    }

    /// <summary>
    /// Versioned world save:
    /// <code>
    /// "LMLW" | int version | int headerBytes | UTF-8 JSON header | RLE(blocks u16[]) RLE(walls u16[]) RLE(shapes u8[])
    ///   RLE(frames u8[]) RLE(liquid u8[]) RLE(liquidKind u8[])   (arrays in chunk-major storage order)
    /// RLE = int length, then (varint run, value) pairs. Not Deflate: System.IO.Compression needs MonoPosixHelper,
    /// which the Windows ARM64 player does not ship, and tile data is so run-heavy that RLE does nearly as well.
    /// </code>
    /// The header carries the tile and wall name palettes; on load every numeric id is remapped to the current
    /// content, so adding or reordering content never breaks old saves (unknown names load as air).
    /// Version history is handled by <see cref="Load"/>; every version ever written must stay loadable
    /// (Assets/Tests/Fixtures holds one save per version, checked by tests).
    /// </summary>
    public static class WorldSave
    {
        public const string Magic = "LMLW";
        public const int Version = 1;
        public const string Extension = ".lmw";

        public static void Save(string path, WorldData world, SaveMeta meta, ContentDatabase db)
        {
            meta.width = world.Width;
            meta.height = world.Height;
            meta.seed = world.Seed;
            meta.savedUtc = DateTime.UtcNow.ToString("o");
            if (string.IsNullOrEmpty(meta.createdUtc)) meta.createdUtc = meta.savedUtc;

            var header = new Dictionary<string, object>
            {
                ["name"] = meta.name, ["seed"] = (double)meta.seed, ["width"] = (double)meta.width, ["height"] = (double)meta.height,
                ["playSeconds"] = meta.playSeconds, ["timeOfDay"] = (double)meta.timeOfDay,
                ["createdUtc"] = meta.createdUtc, ["savedUtc"] = meta.savedUtc,
                ["tilePalette"] = Palette(db.tiles.Length, i => db.tiles[i].id),
                ["wallPalette"] = Palette(db.walls.Length, i => db.walls[i].id),
                ["game"] = meta.gameJson ?? "{}",
            };
            var headerBytes = Encoding.UTF8.GetBytes(MiniJson.Serialize(header));

            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var tmp = path + ".tmp";
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write))
            using (var bw = new BinaryWriter(fs))
            {
                bw.Write(Encoding.ASCII.GetBytes(Magic));
                bw.Write(Version);
                bw.Write(headerBytes.Length);
                bw.Write(headerBytes);
                Rle.Write(bw, world.Blocks);
                Rle.Write(bw, world.Walls);
                Rle.Write(bw, world.Shapes);
                Rle.Write(bw, world.Frames);
                Rle.Write(bw, world.Liquid);
                Rle.Write(bw, world.LiquidKind);
            }
            // write-then-swap so a crash mid-save never destroys the previous save
            // (File.Replace needs MonoPosixHelper, which the Windows player does not ship)
            File.Copy(tmp, path, true);
            File.Delete(tmp);
        }

        static List<object> Palette(int n, Func<int, string> name)
        {
            var list = new List<object>(n + 1) { "" };   // index 0 = air / no wall
            for (int i = 0; i < n; i++) list.Add(name(i));
            return list;
        }

        public static SaveMeta ReadMeta(string path) => ReadHeader(path, out _, out _);

        static SaveMeta ReadHeader(string path, out Dictionary<string, object> header, out int version)
        {
            using var fs = File.OpenRead(path);
            using var br = new BinaryReader(fs);
            return ReadHeader(br, out header, out version);
        }

        static SaveMeta ReadHeader(BinaryReader br, out Dictionary<string, object> header, out int version)
        {
            var magic = Encoding.ASCII.GetString(br.ReadBytes(4));
            if (magic != Magic) throw new InvalidDataException("not a Loamlight world save");
            version = br.ReadInt32();
            if (version < 1 || version > Version) throw new InvalidDataException($"unsupported save version {version} (this build reads 1..{Version})");
            int len = br.ReadInt32();
            header = (Dictionary<string, object>)MiniJson.Parse(Encoding.UTF8.GetString(br.ReadBytes(len)));
            return new SaveMeta
            {
                name = (string)header["name"],
                seed = (uint)Convert.ToDouble(header["seed"]),
                width = (int)Convert.ToDouble(header["width"]),
                height = (int)Convert.ToDouble(header["height"]),
                playSeconds = Convert.ToDouble(header["playSeconds"]),
                timeOfDay = (float)Convert.ToDouble(header["timeOfDay"]),
                createdUtc = header.TryGetValue("createdUtc", out var c) ? (string)c : "",
                savedUtc = header.TryGetValue("savedUtc", out var s) ? (string)s : "",
                gameJson = header.TryGetValue("game", out var g) ? (string)g : "{}",
            };
        }

        public static WorldData Load(string path, ContentDatabase db, out SaveMeta meta)
        {
            using var fs = File.OpenRead(path);
            using var br = new BinaryReader(fs);
            meta = ReadHeader(br, out var header, out int version);
            var world = new WorldData(meta.width, meta.height, meta.seed);
            try
            {
                Rle.Read(br, world.Blocks);
                Rle.Read(br, world.Walls);
                Rle.Read(br, world.Shapes);
                Rle.Read(br, world.Frames);
                Rle.Read(br, world.Liquid);
                Rle.Read(br, world.LiquidKind);
                Remap(world.Blocks, (List<object>)header["tilePalette"], name => db.TryTileId(name, out var id) ? id : (ushort)0);
                Remap(world.Walls, (List<object>)header["wallPalette"], name =>
                {
                    for (int i = 0; i < db.walls.Length; i++) if (db.walls[i].id == name) return (ushort)(i + 1);
                    return 0;
                });
            }
            catch
            {
                world.Dispose();
                throw;
            }
            world.MarkAllChunks(WorldData.FlagMeshDirty | WorldData.FlagLiquidActive | WorldData.FlagSimActive);
            return world;
        }

        static void Remap(NativeArray<ushort> ids, List<object> palette, Func<string, ushort> resolve)
        {
            var map = new ushort[palette.Count];
            bool identity = true;
            for (int i = 0; i < palette.Count; i++)
            {
                map[i] = i == 0 ? (ushort)0 : resolve((string)palette[i]);
                identity &= map[i] == i;
            }
            if (identity) return;
            for (int i = 0; i < ids.Length; i++)
            {
                int v = ids[i];
                ids[i] = v < map.Length ? map[v] : (ushort)0;
            }
        }
    }

    /// <summary>Run-length codec for the tile arrays: int length, then (LEB128 varint run, raw value) pairs.</summary>
    public static class Rle
    {
        public static unsafe void Write(BinaryWriter w, NativeArray<ushort> a)
        {
            w.Write(a.Length);
            var p = (ushort*)a.GetUnsafeReadOnlyPtr();
            int n = a.Length, i = 0;
            while (i < n)
            {
                ushort v = p[i];
                int j = i + 1;
                while (j < n && p[j] == v) j++;
                WriteVarint(w, (uint)(j - i));
                w.Write(v);
                i = j;
            }
        }

        public static unsafe void Write(BinaryWriter w, NativeArray<byte> a)
        {
            w.Write(a.Length);
            var p = (byte*)a.GetUnsafeReadOnlyPtr();
            int n = a.Length, i = 0;
            while (i < n)
            {
                byte v = p[i];
                int j = i + 1;
                while (j < n && p[j] == v) j++;
                WriteVarint(w, (uint)(j - i));
                w.Write(v);
                i = j;
            }
        }

        public static unsafe void Read(BinaryReader r, NativeArray<ushort> a)
        {
            int n = r.ReadInt32();
            if (n != a.Length) throw new InvalidDataException($"array length {n} != {a.Length}");
            var p = (ushort*)a.GetUnsafePtr();
            int i = 0;
            while (i < n)
            {
                int run = (int)ReadVarint(r);
                ushort v = r.ReadUInt16();
                if (run <= 0 || i + run > n) throw new InvalidDataException("corrupt run");
                for (int k = 0; k < run; k++) p[i + k] = v;
                i += run;
            }
        }

        public static unsafe void Read(BinaryReader r, NativeArray<byte> a)
        {
            int n = r.ReadInt32();
            if (n != a.Length) throw new InvalidDataException($"array length {n} != {a.Length}");
            var p = (byte*)a.GetUnsafePtr();
            int i = 0;
            while (i < n)
            {
                int run = (int)ReadVarint(r);
                byte v = r.ReadByte();
                if (run <= 0 || i + run > n) throw new InvalidDataException("corrupt run");
                UnsafeUtility.MemSet(p + i, v, run);
                i += run;
            }
        }

        static void WriteVarint(BinaryWriter w, uint v)
        {
            while (v >= 0x80)
            {
                w.Write((byte)(v | 0x80));
                v >>= 7;
            }
            w.Write((byte)v);
        }

        static uint ReadVarint(BinaryReader r)
        {
            uint v = 0;
            int shift = 0;
            while (true)
            {
                byte b = r.ReadByte();
                v |= (uint)(b & 0x7f) << shift;
                if ((b & 0x80) == 0) return v;
                shift += 7;
                if (shift > 28) throw new InvalidDataException("varint too long");
            }
        }
    }
}
