using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Numerics;
using _3DThumbnailShell.Core.Renderer;

namespace _3DThumbnailShell.Core.Formats
{
    /// <summary>Object File Format (OFF)。头 → Nv Nf Ne → 顶点 → 面。无面时按点云。</summary>
    public sealed class OffReader : IMeshParser
    {
        public bool CanParse(string ext) => ext == ".off";

        private sealed class Scanner
        {
            private readonly StreamReader _sr;
            public Scanner(StreamReader sr) { _sr = sr; }

            /// <summary>读取一整行数值数组（自动跳过注释与空行，不做跨行）。</summary>
            public double[] NextLineNumbers()
            {
                var parts = ReadNextDataLine();
                if (parts == null) return new double[0];
                var res = new List<double>(parts.Length);
                foreach (var p in parts)
                    if (double.TryParse(p, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
                        res.Add(d);
                return res.ToArray();
            }

            private string[] ReadNextDataLine()
            {
                string l;
                do
                {
                    l = _sr.ReadLine();
                    if (l == null) return null;
                } while (string.IsNullOrWhiteSpace(l) || l[0] == '#');
                var parts = l.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
                for (var i = 0; i < parts.Length; i++)
                {
                    if (parts[i][0] == '#') { var sub = new string[i]; Array.Copy(parts, sub, i); parts = sub; break; }
                }
                return parts;
            }
        }

        public bool TryParse(Stream stream, out MeshData mesh)
        {
            mesh = new MeshData { OriginalFormat = "off" };
            mesh.IsPointCloud = true;
            stream.Position = 0;
            var sr = new StreamReader(stream, System.Text.Encoding.ASCII, false, 1 << 20, true);
            var sc = new Scanner(sr);

            // magic 行
            var magicLine = FirstNonComment(sr);
            if (magicLine == null || !magicLine.ToUpperInvariant().EndsWith("OFF")) return false;

            // 计数行
            var counts = CollectNumbers(sc, 2);
            if (counts.Count < 2) return false;
            long nVerts = (long)counts[0], nFaces = (long)counts[1];
            if (nVerts <= 0 || nVerts > 20_000_000) return false;

            mesh.Vertices = new List<Vector3>((int)nVerts);
            for (var i = 0; i < nVerts; i++)
            {
                var c = CollectNumbers(sc, 3);
                if (c.Count < 3) break;
                mesh.Vertices.Add(new Vector3((float)c[0], (float)c[1], (float)c[2]));
            }
            if (mesh.Vertices.Count == 0) return false;

            var ids = new List<int>();
            for (var f = 0; f < nFaces && f < 5_000_000; f++)
            {
                var faceHead = CollectNumbers(sc, 1);
                if (faceHead.Count == 0) break;
                int k = (int)Math.Round(faceHead[0]);
                if (k < 3 || k > 512) { DrainTokens(sc, k); continue; }

                var idxs = new List<int>(k);
                // 同一行剩余数字 + 必要时跨行
                var pending = k;
                for (var j = 1; j < faceHead.Count && pending > 0; j++) { idxs.Add((int)Math.Round(faceHead[j])); pending--; }
                while (pending > 0)
                {
                    var more = CollectNumbers(sc, pending);
                    if (more.Count == 0) break;
                    foreach (var v in more) { idxs.Add((int)Math.Round(v)); pending--; if (pending <= 0) break; }
                }
                if (idxs.Count < 3) continue;
                for (var t = 1; t + 1 < idxs.Count; t++)
                {
                    ids.Add(idxs[0]); ids.Add(idxs[t]); ids.Add(idxs[t + 1]);
                }
            }
            if (ids.Count >= 3) { mesh.IsPointCloud = false; mesh.Indices = ids; }
            mesh.Normals = new List<Vector3>();
            return true;
        }

        private static string FirstNonComment(StreamReader sr)
        {
            string l;
            do
            {
                l = sr.ReadLine();
                if (l == null) return null;
            } while (string.IsNullOrWhiteSpace(l) || l.TrimStart()[0] == '#');
            return l;
        }

        private static List<double> CollectNumbers(Scanner sc, int want)
        {
            var res = new List<double>(want + 8);
            res.AddRange(sc.NextLineNumbers());
            while (res.Count < want)
            {
                var more = sc.NextLineNumbers();
                if (more.Length == 0) break;
                res.AddRange(more);
            }
            return res;
        }

        private static void DrainTokens(Scanner sc, long n)
        {
            // 简单丢弃若干行（近似）
            for (var i = 0; i < Math.Min(n, 16); i++) sc.NextLineNumbers();
        }
    }
}