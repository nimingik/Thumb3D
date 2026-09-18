using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Numerics;
using System.Text;
using _3DThumbnailShell.Core.Renderer;

namespace _3DThumbnailShell.Core.Formats
{
    /// <summary>Wavefront OBJ。读取 v/vn/vt/f。多边形扇形三角化，MTL 非强制。</summary>
    public sealed class ObjReader : IMeshParser
    {
        public bool CanParse(string ext) => ext == ".obj";

        public bool TryParse(Stream stream, out MeshData mesh)
        {
            mesh = new MeshData { OriginalFormat = "obj" };
            stream.Position = 0;
            var text = new StreamReader(stream, Encoding.UTF8, false, 1 << 20, true).ReadToEnd();
            if (string.IsNullOrEmpty(text)) return false;

            var vi = CultureInfo.InvariantCulture;
            var poss = new List<Vector3>();
            var normals = new List<Vector3>();

            var verts = new List<Vector3>();
            var indices = new List<int>();
            var faceNormals = new List<Vector3>(); // 每面法线（复用几何法线）

            var hasVTex = false; // 忽略 vt，仅记录是否存在以正确处理索引

            using (var sr = new StringReader(text))
            {
                string line;
                while ((line = sr.ReadLine()) != null)
                {
                    var t = line.Trim();
                    if (t.Length == 0 || t[0] == '#') continue;
                    var parts = t.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
                    switch (parts[0])
                    {
                        case "v":
                            if (parts.Length >= 4)
                            {
                                poss.Add(new Vector3(
                                    float.Parse(parts[1], NumberStyles.Float, vi),
                                    float.Parse(parts[2], NumberStyles.Float, vi),
                                    float.Parse(parts[3], NumberStyles.Float, vi)));
                            }
                            break;
                        case "vn":
                            if (parts.Length >= 4)
                            {
                                normals.Add(Vector3.Normalize(new Vector3(
                                    float.Parse(parts[1], NumberStyles.Float, vi),
                                    float.Parse(parts[2], NumberStyles.Float, vi),
                                    float.Parse(parts[3], NumberStyles.Float, vi))));
                            }
                            break;
                        case "vt":
                            hasVTex = true; break;
                        case "f":
                            if (parts.Length < 4) break;
                            ParseFace(parts, poss, normals, hasVTex, verts, indices, faceNormals);
                            break;
                    }
                }
            }

            if (verts.Count < 3) return false;

            // normals：优先逐面几何法线（flat），保证稳定显示
            mesh.Vertices = verts;
            mesh.Indices = indices;
            mesh.Normals = new List<Vector3>(indices.Count / 3 * 3);
            foreach (var nrm in faceNormals)
            {
                mesh.Normals.Add(nrm); mesh.Normals.Add(nrm); mesh.Normals.Add(nrm);
            }
            if (mesh.Normals.Count != mesh.Vertices.Count)
            {
                // 兜底：几何法线
                StlReader.BuildFlatNormals(mesh);
            }
            return true;
        }

        private void ParseFace(string[] parts, List<Vector3> poss, List<Vector3> normals, bool hasVTex,
            List<Vector3> verts, List<int> indices, List<Vector3> faceNormals)
        {
            // 收集该面的 vn 是否存在（任一方括号内带 vn）
            var vertRefs = new List<int>(parts.Length - 1);
            var normRefs = new List<int>(parts.Length - 1);
            for (var i = 1; i < parts.Length; i++)
            {
                var segs = parts[i].Split('/');
                var vIdx = int.Parse(segs[0], CultureInfo.InvariantCulture);
                vertRefs.Add(vIdx < 0 ? poss.Count + vIdx : vIdx - 1);
                if (segs.Length >= 3 && segs[2].Length > 0)
                {
                    var nIdx = int.Parse(segs[2], CultureInfo.InvariantCulture);
                    normRefs.Add(nIdx < 0 ? normals.Count + nIdx : nIdx - 1);
                }
                else
                {
                    normRefs.Add(-1);
                }
            }

            // 扇形三角化
            for (var k = 1; k < vertRefs.Count - 1; k++)
            {
                var a = vertRefs[0]; var b = vertRefs[k]; var c = vertRefs[k + 1];
                var b0 = verts.Count;
                verts.Add(poss[a]); verts.Add(poss[b]); verts.Add(poss[c]);
                // 几何法线
                var nrm = StlReader.ComputeNormal(poss[a], poss[b], poss[c]);
                faceNormals.Add(nrm);
                indices.Add(b0); indices.Add(b0 + 1); indices.Add(b0 + 2);
            }
        }
    }
}