using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Numerics;
using System.Text;
using _3DThumbnailShell.Core.Renderer;

namespace _3DThumbnailShell.Core.Formats
{
    /// <summary>Stanford PLY：ascii / binary_little_endian / binary_big_endian。支持点云（无 face）与网格。</summary>
    public sealed class PlyReader : IMeshParser
    {
        public bool CanParse(string ext) => ext == ".ply";

        public bool TryParse(Stream stream, out MeshData mesh)
        {
            mesh = new MeshData { OriginalFormat = "ply" };
            mesh.IsPointCloud = true;
            stream.Position = 0;
            var br = new BinaryReader(stream, Encoding.ASCII, true);

            // 读 header（逐行直到 end_header）
            var headerLines = new List<string>();
            while (true)
            {
                var line = ReadAsciiLine(br);
                if (line == null) return false;
                headerLines.Add(line);
                if (line.Trim() == "end_header") break;
            }

            string fmt = "ascii";
            int vertexCount = 0;
            int faceCount = 0;
            var vertexProps = new List<string[]>();
            var faceListCountType = "uchar";
            var faceListIndexType = "int";

            foreach (var raw in headerLines)
            {
                var t = raw.Trim();
                if (t.Length == 0 || t[0] == '#') continue;
                var p = t.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
                switch (p[0])
                {
                    case "format": if (p.Length > 1) fmt = p[1]; break;
                    case "element":
                        if (p.Length >= 3)
                        {
                            if (p[1] == "vertex") vertexCount = int.Parse(p[2], CultureInfo.InvariantCulture);
                            else if (p[1] == "face") faceCount = int.Parse(p[2], CultureInfo.InvariantCulture);
                        }
                        break;
                    case "property":
                        if (p.Length >= 5 && p[1] == "list")
                        {
                            faceListCountType = p[2]; faceListIndexType = p[4];
                        }
                        else if (p.Length >= 3)
                        {
                            vertexProps.Add(new[] { p[1], p[2] });
                        }
                        break;
                }
            }

            var vx = IndexOf(vertexProps, "x"); var vy = IndexOf(vertexProps, "y"); var vz = IndexOf(vertexProps, "z");
            var vnx = IndexOf(vertexProps, "nx"); var vny = IndexOf(vertexProps, "ny"); var vnz = IndexOf(vertexProps, "nz");
            var vcr = IndexOf(vertexProps, "red"); var vcg = IndexOf(vertexProps, "green"); var vcb = IndexOf(vertexProps, "blue");
            if (vx < 0 || vy < 0 || vz < 0 || vertexCount == 0 || vertexCount > 20_000_000) return false;

            var verts = mesh.Vertices = new List<Vector3>(vertexCount);
            var normals = new List<Vector3>();
            var cols = new List<Vector4>();
            StreamReader srAscii = null;

            if (fmt == "ascii")
            {
                // 全程复用同一个 StreamReader，避免缓冲跨越丢面
                srAscii = new StreamReader(stream, Encoding.ASCII, false, 1 << 20, true);
                for (var i = 0; i < vertexCount; i++)
                {
                    var l = srAscii.ReadLine();
                    if (l == null) break;
                    var f = l.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
                    if (f.Length <= Math.Max(vz, Math.Max(vnx, vcr))) continue;
                    verts.Add(new Vector3(Pf(f, vx), Pf(f, vy), Pf(f, vz)));
                    if (vnx >= 0) normals.Add(Vector3.Normalize(new Vector3(Pf(f, vnx), Pf(f, vny), Pf(f, vnz))));
                    if (vcr >= 0) cols.Add(new Vector4(Pf(f, vcr) / 255f, Pf(f, vcg) / 255f, Pf(f, vcb) / 255f, 1f));
                }
            }
            else if (fmt == "binary_little_endian" || fmt == "binary_big_endian")
            {
                bool le = fmt == "binary_little_endian";
                for (var i = 0; i < vertexCount; i++)
                    ReadVertexRecord(br, vertexProps, vx, vy, vz, vnx, vny, vnz, vcr, vcg, vcb, le, verts, normals, cols);
            }
            else return false;

            if (verts.Count == 0) return false;
            mesh.Normals = normals;
            mesh.Colors = cols;
            if (mesh.Colors.Count > 0 && mesh.Colors.Count != mesh.Vertices.Count) mesh.Colors = new List<Vector4>();

            // 面
            List<int> ids = null;
            if (faceCount > 0)
            {
                if (fmt == "ascii") ids = ReadAsciiFaces(srAscii, faceCount, vertexCount);
                else ids = ReadBinaryFaces(br, fmt == "binary_big_endian", faceCount, faceListCountType, faceListIndexType, vertexCount);
                if (ids.Count >= 3) { mesh.IsPointCloud = false; mesh.Indices = ids; }
            }
            return true;
        }

        private static int IndexOf(List<string[]> props, string name)
        {
            for (var i = 0; i < props.Count; i++) if (props[i][1] == name) return i;
            return -1;
        }
        private static float Pf(string[] f, int i) => float.Parse(f[i], CultureInfo.InvariantCulture);

        private void ReadVertexRecord(BinaryReader br, List<string[]> props, int vx, int vy, int vz,
            int vnx, int vny, int vnz, int vcr, int vcg, int vcb, bool le,
            List<Vector3> verts, List<Vector3> normals, List<Vector4> cols)
        {
            verts.Add(new Vector3(ReadProp(br, props, vx, le), ReadProp(br, props, vy, le), ReadProp(br, props, vz, le)));
            if (vnx >= 0)
                normals.Add(Vector3.Normalize(new Vector3(ReadProp(br, props, vnx, le), ReadProp(br, props, vny, le), ReadProp(br, props, vnz, le))));
            if (vcr >= 0)
                cols.Add(new Vector4(ReadProp(br, props, vcr, le) / 255f, ReadProp(br, props, vcg, le) / 255f, ReadProp(br, props, vcb, le) / 255f, 1f));
        }

        private List<int> ReadAsciiFaces(StreamReader sr, int faceCount, int vertexCount)
        {
            var result = new List<int>();
            for (var i = 0; i < faceCount; i++)
            {
                var l = sr.ReadLine();
                if (l == null) break;
                var idxs = new List<int>();
                var toks = l.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
                for (var ti = 1; ti < toks.Length; ti++) // 跳过首 token（顶点数）
                {
                    if (int.TryParse(toks[ti], NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) && v >= 0 && v < vertexCount)
                        idxs.Add(v);
                }
                FanTriangulate(idxs, vertexCount, result);
            }
            return result;
        }

        private List<int> ReadBinaryFaces(BinaryReader br, bool be, int faceCount, string countType, string indexType, int vertexCount)
        {
            var result = new List<int>();
            for (var i = 0; i < faceCount; i++)
            {
                var n = ReadSz(br, countType, be);
                if (n < 3 || n > 256) continue;
                var idxs = new List<int>((int)n);
                for (var k = 0; k < n; k++)
                {
                    var idx = ReadSz(br, indexType, be);
                    if (idx < 0 || idx >= vertexCount) { idxs.Clear(); break; }
                    idxs.Add((int)idx);
                }
                FanTriangulate(idxs, vertexCount, result);
            }
            return result;
        }

        private static void FanTriangulate(List<int> face, int vertexCount, List<int> result)
        {
            for (var k = 1; k + 1 < face.Count; k++)
            {
                result.Add(face[0]); result.Add(face[k]); result.Add(face[k + 1]);
            }
        }

        // ================= 标量读取（支持 big-endian） =================
        private static float ReadProp(BinaryReader br, List<string[]> props, int index, bool be)
        {
            var type = props[index][0];
            switch (type)
            {
                case "float": case "float32": return ReadF4(br, be);
                case "double": return (float)ReadF8(br, be);
                case "uchar": case "uint8": return (float)br.ReadByte();
                case "char": case "int8": return (float)br.ReadSByte();
                case "short": case "int16": return (float)ReadI2(br, be);
                case "ushort": case "uint16": return (float)ReadU2(br, be);
                case "int": case "int32": return (float)ReadI4(br, be);
                case "uint": case "uint32": return (float)ReadU4(br, be);
                default: return ReadF4(br, be);
            }
        }
        private static long ReadSz(BinaryReader br, string type, bool be) => type switch
        {
            "uchar" or "uint8" => br.ReadByte(),
            "char" or "int8" => br.ReadSByte(),
            "ushort" or "uint16" => ReadU2(br, be),
            "short" or "int16" => ReadI2(br, be),
            "uint" or "uint32" => ReadU4(br, be),
            _ => ReadI4(br, be),
        };

        private static byte[] Rb(BinaryReader br, int n)
        {
            var b = br.ReadBytes(n);
            return b;
        }
        private static float ReadF4(BinaryReader br, bool be)
        {
            var b = Rb(br, 4);
            if (be) Array.Reverse(b);
            return BitConverter.ToSingle(b, 0);
        }
        private static double ReadF8(BinaryReader br, bool be)
        {
            var b = Rb(br, 8);
            if (be) Array.Reverse(b);
            return BitConverter.ToDouble(b, 0);
        }
        private static short ReadI2(BinaryReader br, bool be)
        {
            var b = Rb(br, 2);
            if (be) Array.Reverse(b);
            return BitConverter.ToInt16(b, 0);
        }
        private static ushort ReadU2(BinaryReader br, bool be)
        {
            var b = Rb(br, 2);
            if (be) Array.Reverse(b);
            return BitConverter.ToUInt16(b, 0);
        }
        private static int ReadI4(BinaryReader br, bool be)
        {
            var b = Rb(br, 4);
            if (be) Array.Reverse(b);
            return BitConverter.ToInt32(b, 0);
        }
        private static uint ReadU4(BinaryReader br, bool be)
        {
            var b = Rb(br, 4);
            if (be) Array.Reverse(b);
            return BitConverter.ToUInt32(b, 0);
        }

        private static string ReadAsciiLine(BinaryReader br)
        {
            var sb = new StringBuilder();
            while (true)
            {
                var b = br.Read();
                if (b < 0) return null;
                if (b == (byte)'\n') return sb.ToString();
                if (b != (byte)'\r') sb.Append((char)b);
            }
        }
    }
}