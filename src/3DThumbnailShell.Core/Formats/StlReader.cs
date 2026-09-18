using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Numerics;
using System.Text;
using System.Text.RegularExpressions;
using _3DThumbnailShell.Core.Renderer;

namespace _3DThumbnailShell.Core.Formats
{
    /// <summary>STL：二进制或 ASCII。先按长度精确判二进制，否则 ASCII。</summary>
    public sealed class StlReader : IMeshParser
    {
        public bool CanParse(string ext) => ext == ".stl";

        private static readonly Regex VertexRe =
            new Regex(@"vertex\s+([-+0-9.eE]+)\s+([-+0-9.eE]+)\s+([-+0-9.eE]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        public bool TryParse(Stream stream, out MeshData mesh)
        {
            mesh = new MeshData { OriginalFormat = "stl" };
            stream.Position = 0;
            if (stream.Length < 84) return false;

            // 尝试二进制：长度 == 80 + 4 + n*50
            var binCount = BitConverter.ToUInt32(ReadBlock(stream, 84, 80), 0);
            if (stream.Length == 84 + (long)binCount * 50)
            {
                stream.Position = 84;
                var br = new BinaryReader(stream, Encoding.ASCII, true);
                var verts = mesh.Vertices;
                var norms = mesh.Normals;
                var nFaces = (int)binCount;
                var baseIdx = 0;
                for (var i = 0; i < nFaces; i++)
                {
                    var nrm = new Vector3(br.ReadSingle(), br.ReadSingle(), br.ReadSingle());
                    var v0 = new Vector3(br.ReadSingle(), br.ReadSingle(), br.ReadSingle());
                    var v1 = new Vector3(br.ReadSingle(), br.ReadSingle(), br.ReadSingle());
                    var v2 = new Vector3(br.ReadSingle(), br.ReadSingle(), br.ReadSingle());
                    br.ReadUInt16(); // attribute byte count（忽略）
                    verts.Add(v0); verts.Add(v1); verts.Add(v2);
                    var isNan = float.IsNaN(nrm.X) || float.IsNaN(nrm.Y) || float.IsNaN(nrm.Z);
                    var nrm2 = isNan || nrm.LengthSquared() < 1e-12f
                        ? ComputeNormal(v0, v1, v2) : Vector3.Normalize(nrm);
                    for (var k = 0; k < 3; k++) norms.Add(nrm2);
                    mesh.Indices.Add(baseIdx); mesh.Indices.Add(baseIdx + 1); mesh.Indices.Add(baseIdx + 2);
                    baseIdx += 3;
                }
                return verts.Count > 0;
            }

            // ASCII 回退
            stream.Position = 0;
            var text = new StreamReader(stream, Encoding.ASCII, false, 1 << 20, true).ReadToEnd();
            if (string.IsNullOrWhiteSpace(text)) return false;
            var ms = VertexRe.Matches(text);
            if (ms.Count < 3) return false;

            var vi = CultureInfo.InvariantCulture;
            var curv = 0;
            foreach (Match m in ms)
            {
                var x = float.Parse(m.Groups[1].Value, NumberStyles.Float, vi);
                var y = float.Parse(m.Groups[2].Value, NumberStyles.Float, vi);
                var z = float.Parse(m.Groups[3].Value, NumberStyles.Float, vi);
                mesh.Vertices.Add(new Vector3(x, y, z));
                curv++;
                if (curv % 3 == 0 && curv >= 3)
                {
                    mesh.Indices.Add(curv - 3); mesh.Indices.Add(curv - 2); mesh.Indices.Add(curv - 1);
                }
            }
            if (mesh.Vertices.Count < 3) return false;

            // ASCII STL 无法线 → 逐面几何法线（flat），写入 per-vertex 供主路径使用
            BuildFlatNormals(mesh);
            return true;
        }

        internal static void BuildFlatNormals(MeshData mesh)
        {
            mesh.Normals.Clear();
            var nFaces = mesh.Indices.Count / 3;
            if (nFaces == 0)
            {
                // 退化网格：逐点法线置零占位
                for (var i = 0; i < mesh.Vertices.Count; i++) mesh.Normals.Add(Vector3.UnitY);
                return;
            }
            // 将顶点展开为每面 3 个，便于统一 flat 渲染
            var expanded = new List<Vector3>(nFaces * 3);
            var newNormals = new List<Vector3>(nFaces * 3);
            var oldVerts = mesh.Vertices;
            var newIndices = new List<int>(nFaces * 3);
            for (var f = 0; f < nFaces; f++)
            {
                var a = oldVerts[mesh.Indices[f * 3]];
                var b = oldVerts[mesh.Indices[f * 3 + 1]];
                var c = oldVerts[mesh.Indices[f * 3 + 2]];
                var nrm = ComputeNormal(a, b, c);
                var b0 = expanded.Count;
                expanded.Add(a); expanded.Add(b); expanded.Add(c);
                newNormals.Add(nrm); newNormals.Add(nrm); newNormals.Add(nrm);
                newIndices.Add(b0); newIndices.Add(b0 + 1); newIndices.Add(b0 + 2);
            }
            mesh.Vertices = expanded;
            mesh.Indices = newIndices;
            mesh.Normals = newNormals;
        }

        internal static Vector3 ComputeNormal(Vector3 a, Vector3 b, Vector3 c)
        {
            var nrm = Vector3.Cross(b - a, c - a);
            var len = nrm.Length();
            if (len < 1e-12f) return Vector3.UnitY;
            return nrm / len;
        }

        private static byte[] ReadBlock(Stream s, int count, int start)
        {
            s.Position = start;
            var b = new byte[count];
            var read = 0;
            while (read < count)
            {
                var n = s.Read(b, read, count - read);
                if (n <= 0) break;
                read += n;
            }
            return b;
        }
    }
}