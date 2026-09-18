using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Numerics;
using System.Text;
using _3DThumbnailShell.Core.Renderer;

namespace _3DThumbnailShell.Core.Formats
{
    /// <summary>
    /// glTF (.gltf / .glb) 几何读取。支持 GLB 分块与 .gltf 嵌入 base64；外部 .bin 需文件路径。
    /// 仅取首个 primitive 的 POSITION/NORMAL/COLOR_0 与 indices（mode=4 三角形）。
    /// </summary>
    public sealed class GltfReader : IMeshParser
    {
        public bool CanParse(string ext) => ext == ".gltf" || ext == ".glb";

        public bool TryParse(Stream stream, out MeshData mesh)
        {
            mesh = null;
            stream.Position = 0;
            // 自包含：GLB 或 data-URI 嵌入的 .gltf
            try { mesh = ParseCore(stream, filePath: null); }
            catch { mesh = null; }
            return mesh != null && mesh.Vertices != null && mesh.Vertices.Count > 0;
        }

        /// <summary>带文件路径解析，可解析外部 .bin。供工厂在 .gltf/.glb 时调用。</summary>
        public MeshData Parse(string filePath)
        {
            using (var fs = File.OpenRead(filePath))
                return ParseCore(fs, filePath);
        }

        private MeshData ParseCore(Stream stream, string filePath)
        {
            stream.Position = 0;
            // 探测 GLB magic
            var head = new byte[20];
            var read = ReadFully(stream, head, 0, head.Length);
            var isGlb = read >= 12 && head[0] == 0x67 && head[1] == 0x6C && head[2] == 0x54 && head[3] == 0x46;

            string json;
            byte[] bin;
            if (isGlb)
            {
                ParseGlb(stream, out json, out bin);
            }
            else
            {
                stream.Position = 0;
                json = new StreamReader(stream, Encoding.UTF8, false, 1 << 24, true).ReadToEnd();
                bin = null;
            }

            var root = new JsonParser(json).Parse();
            if (root == null || !root.Has("buffers")) { System.Diagnostics.Debug.WriteLine("GLTF: no buffers"); return null; }

            // 抽取 buffers 字节
            var bufByteArrays = LoadBuffers(root, bin, filePath, isGlb);
            var bufferViews = ParseBufferViews(root);
            var accessors = ParseAccessors(root);

            // 首个 mesh 的 primitive
            var primitives = RootPrimitives(root);

            MeshData mesh = null;
            foreach (var prim in primitives)
            {
                mesh = BuildPrimitive(prim, bufferViews, accessors, bufByteArrays);
                if (mesh != null) break;
            }
            return mesh;
        }

        // ==================== GLB ====================
        private static void ParseGlb(Stream s, out string json, out byte[] bin)
        {
            json = ""; bin = null;
            var br = new BinaryReader(s, Encoding.UTF8, true);
            br.BaseStream.Position = 12; // 跳过 magic/version/length
            while (br.BaseStream.Position < br.BaseStream.Length)
            {
                var length = br.ReadUInt32();
                var type = br.ReadUInt32();
                var data = br.ReadBytes((int)length);
                if (type == 0x4E4F534A) json = Encoding.UTF8.GetString(data);
                else if (type == 0x004E4942) bin = data;
                // 每个 chunk 按 4 字节对齐填充
                var pad = (int)((length + 3) & ~3u) - (int)length;
                if (pad > 0) br.BaseStream.Position += pad;
            }
        }

        // ==================== Buffers ====================
        private byte[][] LoadBuffers(JVal root, byte[] glbBin, string filePath, bool isGlb)
        {
            var buffers = root["buffers"];
            var result = new byte[buffers.Count][];
            for (var i = 0; i < buffers.Count; i++)
            {
                var b = buffers[i];
                if (isGlb && i == 0 && glbBin != null)
                {
                    result[i] = glbBin; continue;
                }
                var uri = b != null && b.Has("uri") ? b["uri"].S : null;
                if (string.IsNullOrEmpty(uri)) { result[i] = null; continue; }
                var byteLength = (int)b["byteLength"].D;
                if (uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                {
                    var idx = uri.IndexOf(",", StringComparison.Ordinal);
                    var b64 = uri.Substring(idx + 1);
                    result[i] = Convert.FromBase64String(b64);
                }
                else if (filePath != null)
                {
                    var dir = Path.GetDirectoryName(filePath);
                    var p = Path.GetFullPath(Path.Combine(dir, uri));
                    result[i] = File.ReadAllBytes(p);
                }
            }
            return result;
        }

        private List<JVal> ParseBufferViews(JVal root)
        {
            var res = new List<JVal>();
            var views = root["bufferViews"];
            for (var i = 0; i < views.Count; i++) res.Add(views[i]);
            return res;
        }

        private List<JVal> ParseAccessors(JVal root)
        {
            var res = new List<JVal>();
            var acc = root["accessors"];
            for (var i = 0; i < acc.Count; i++) res.Add(acc[i]);
            return res;
        }

        private List<JVal> RootPrimitives(JVal root)
        {
            var res = new List<JVal>();
            var meshes = root["meshes"];
            for (var mi = 0; mi < meshes.Count; mi++)
            {
                var prims = meshes[mi]["primitives"];
                for (var pi = 0; pi < prims.Count; pi++) res.Add(prims[pi]);
            }
            return res;
        }

        // ==================== 几何 ====================
        private MeshData BuildPrimitive(JVal prim, List<JVal> views, List<JVal> accessors, byte[][] buffers)
        {
            var mode = prim != null && prim.Has("mode") ? (int)prim["mode"].D : 4;
            if (mode != 4) return null; // 只处理三角形
            var attrs = prim["attributes"];
            if (attrs == null) return null;

            int posAcc = attrs.Has("POSITION") ? (int)attrs["POSITION"].D : -1;
            if (posAcc < 0 || posAcc >= accessors.Count) return null;

            var positions = ReadVec3(accessors[posAcc], views, buffers);
            if (positions == null || positions.Count == 0) return null;

            var mesh = new MeshData { OriginalFormat = "gltf" };

            // indices
            var indices = new List<int>();
            if (prim.Has("indices"))
            {
                var idxAcc = (int)prim["indices"].D;
                indices = ReadIndices(accessors[idxAcc], views, buffers, positions.Count);
            }
            else
            {
                for (var i = 0; i < positions.Count; i++) indices.Add(i);
            }
            if (indices.Count < 3) return null;

            mesh.Vertices = positions;
            mesh.Indices = indices;

            // NORMAL
            if (attrs.Has("NORMAL"))
            {
                var n = ReadVec3(accessors[(int)attrs["NORMAL"].D], views, buffers);
                mesh.Normals = n;
            }
            else mesh.Normals = new List<Vector3>();

            // COLOR_0
            if (attrs.Has("COLOR_0"))
            {
                var c = ReadColor(accessors[(int)attrs["COLOR_0"].D], views, buffers);
                mesh.Colors = c;
            }
            return mesh;
        }

        // ==================== Accessor 读取 ====================
        private static byte[] BufferData(int bufferIdx, int byteOffset, int byteLength, List<JVal> views, byte[][] buffers)
        {
            if (bufferIdx < 0 || bufferIdx >= buffers.Length || buffers[bufferIdx] == null) return null;
            var src = buffers[bufferIdx];
            if (byteOffset + byteLength > src.Length) return null;
            var outb = new byte[byteLength];
            Buffer.BlockCopy(src, byteOffset, outb, 0, byteLength);
            return outb;
        }

        private List<Vector3> ReadVec3(JVal acc, List<JVal> views, byte[][] buffers)
        {
            var compType = (int)acc["componentType"].D;
            var count = (int)acc["count"].D;
            var typeStr = acc.Has("type") ? acc["type"].S : "VEC3";
            var comps = typeStr switch { "VEC2" => 2, "VEC3" => 3, _ => 3 };

            var bv = acc["bufferView"];
            JVal view = bv != null ? views[(int)bv.D] : null;
            int bufIdx = view != null ? (int)view["buffer"].D : -1;
            int byteOffset = acc.Has("byteOffset") ? (int)acc["byteOffset"].D : (view != null && view.Has("byteOffset") ? (int)view["byteOffset"].D : 0);
            int byteLength = count * comps * SizeOf(compType);
            if (view != null) byteLength = Math.Min(byteLength, view.Has("byteLength") ? (int)view["byteLength"].D : byteLength);

            var data = BufferData(bufIdx, byteOffset, byteLength, views, buffers);
            if (data == null) return null;
            var res = new List<Vector3>(count);
            var isFloat = compType == 5126;
            for (var i = 0; i < count; i++)
            {
                var off = i * comps * (isFloat ? 4 : SizeOf(compType));
                float x = ReadComp(data, off, compType);
                float y = comps >= 2 ? ReadComp(data, off + (isFloat ? 4 : SizeOf(compType)), compType) : 0f;
                float z = comps >= 3 ? ReadComp(data, off + 2 * (isFloat ? 4 : SizeOf(compType)), compType) : 0f;
                res.Add(new Vector3(x, y, z));
            }
            return res;
        }

        private List<Vector4> ReadColor(JVal acc, List<JVal> views, byte[][] buffers)
        {
            var compType = (int)acc["componentType"].D;
            var count = (int)acc["count"].D;
            var isVec4 = acc.Has("type") && acc["type"].S == "VEC4";
            var comps = isVec4 ? 4 : 3;

            var bv = acc["bufferView"];
            JVal view = bv != null ? views[(int)bv.D] : null;
            int bufIdx = view != null ? (int)view["buffer"].D : -1;
            int byteOffset = acc.Has("byteOffset") ? (int)acc["byteOffset"].D : (view != null && view.Has("byteOffset") ? (int)view["byteOffset"].D : 0);
            var byteLength = count * comps * SizeOf(compType);

            var data = BufferData(bufIdx, byteOffset, byteLength, views, buffers);
            if (data == null) return null;
            var sz = SizeOf(compType);
            var norm = compType != 5126; // 整数需归一化
            var res = new List<Vector4>(count);
            for (var i = 0; i < count; i++)
            {
                var off = i * comps * sz;
                var c = new Vector4(ReadComp(data, off, compType), ReadComp(data, off + sz, compType),
                    ReadComp(data, off + 2 * sz, compType), 1f);
                if (isVec4) c.W = ReadComp(data, off + 3 * sz, compType);
                if (norm)
                {
                    c *= 1f / MaxOfType(compType);
                }
                res.Add(c);
            }
            return res;
        }

        private List<int> ReadIndices(JVal acc, List<JVal> views, byte[][] buffers, int vertexCount)
        {
            var compType = (int)acc["componentType"].D;
            var count = (int)acc["count"].D;
            var bv = acc["bufferView"];
            JVal view = bv != null ? views[(int)bv.D] : null;
            int bufIdx = view != null ? (int)view["buffer"].D : -1;
            int byteOffset = acc.Has("byteOffset") ? (int)acc["byteOffset"].D : (view != null && view.Has("byteOffset") ? (int)view["byteOffset"].D : 0);
            var byteLength = count * SizeOf(compType);
            var data = BufferData(bufIdx, byteOffset, byteLength, views, buffers);
            if (data == null) return null;
            var res = new List<int>(count);
            var sz = SizeOf(compType);
            for (var i = 0; i < count; i++)
            {
                var v = (int)ReadComp(data, i * sz, compType);
                if (v < 0 || v >= vertexCount) continue;
                res.Add(v);
            }
            return res;
        }

        private static int SizeOf(int compType) => compType switch
        {
            5126 => 4,  // float
            5120 => 1,  // byte
            5121 => 1,  // ubyte
            5122 => 2,  // short
            5123 => 2,  // ushort
            5125 => 4,  // uint
            _ => 4,
        };
        private static float MaxOfType(int compType) => compType switch
        {
            5120 => 127f, 5121 => 255f, 5122 => 32767f, _ => 65535f,
        };

        private static float ReadComp(byte[] d, int off, int compType)
        {
            if (off + 4 > d.Length) return 0f;
            switch (compType)
            {
                case 5126: return BitConverter.ToSingle(d, off);
                case 5120: return (sbyte)d[off];
                case 5121: return d[off];
                case 5122: return BitConverter.ToInt16(d, off);
                case 5123: return BitConverter.ToUInt16(d, off);
                case 5125: return BitConverter.ToUInt32(d, off);
                default: return 0f;
            }
        }

        private static int ReadFully(Stream s, byte[] b, int off, int len)
        {
            int total = 0;
            while (total < len)
            {
                var n = s.Read(b, off + total, len - total);
                if (n <= 0) break;
                total += n;
            }
            return total;
        }
    }
}