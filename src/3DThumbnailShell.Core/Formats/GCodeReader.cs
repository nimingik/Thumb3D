using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Numerics;
using _3DThumbnailShell.Core.Renderer;

namespace _3DThumbnailShell.Core.Formats
{
    /// <summary>
    /// G-code 切片文件解析器：提取挤出移动（G0/G1 且 E 递增）的工具路径，
    /// 生成线段（IsLineCloud），供渲染器以 3D 线框形式预览打印路径。
    /// 非挤出移动（空走）不绘制，避免缩略图杂乱。
    /// </summary>
    public sealed class GCodeReader : IMeshParser
    {
        private static readonly CultureInfo Ci = CultureInfo.InvariantCulture;
        private static readonly char[] Sep = { ' ', '\t' };

        public bool CanParse(string ext)
        {
            if (string.IsNullOrEmpty(ext)) return false;
            var e = ext.ToLowerInvariant();
            return e == ".gcode" || e == ".gco" || e == ".g" || e == ".gc" ||
                   e == ".nc" || e == ".ngc" || e == ".cnc";
        }

        public bool TryParse(Stream stream, out MeshData mesh)
        {
            mesh = new MeshData { OriginalFormat = "gcode", IsLineCloud = true };
            using (var sr = new StreamReader(stream, System.Text.Encoding.UTF8, true, 4096, leaveOpen: true))
            {
                var verts = mesh.Vertices;
                var lines = mesh.Lines;

                double x = 0, y = 0, z = 0;     // 当前位置（mm）
                double curE = 0;                        // 当前挤出量
                bool absolute = true;                    // G90/G91
                bool absE = true;                        // M82/M83（挤出绝对/相对）
                bool lastExtrude = false;                // 上一段是否挤出
                int lastIdx = -1;                        // 上一段终点顶点索引

                string line;
                while ((line = sr.ReadLine()) != null)
                {
                    // 去注释：; 和 (
                    int semi = line.IndexOf(';');
                    if (semi >= 0) line = line.Substring(0, semi);
                    int paren = line.IndexOf('(');
                    if (paren >= 0) line = line.Substring(0, paren);
                    line = line.Trim();
                    if (line.Length == 0) continue;

                    var parts = line.Split(Sep, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length == 0) continue;

                    string cmd = parts[0].ToUpperInvariant();
                    if (cmd == "G0" || cmd == "G1")
                    {
                        double nx = x, ny = y, nz = z;
                        double eDelta = 0;
                        bool hasMove = false;
                        for (int i = 1; i < parts.Length; i++)
                        {
                            var p = parts[i];
                            if (p.Length < 2) continue;
                            char k = p[0];
                            if (!double.TryParse(p.Substring(1), NumberStyles.Float, Ci, out var v)) continue;
                            if (k == 'X') { nx = absolute ? v : x + v; hasMove = true; }
                            else if (k == 'Y') { ny = absolute ? v : y + v; hasMove = true; }
                            else if (k == 'Z') { nz = absolute ? v : z + v; hasMove = true; }
                            else if (k == 'E')
                            {
                                if (absE) eDelta = v - curE;
                                else eDelta = v;
                            }
                        }

                        // 仅挤出移动绘制线段
                        if (eDelta > 1e-9 && hasMove)
                        {
                            int idx = verts.Count;
                            verts.Add(new Vector3((float)nx, (float)ny, (float)nz));
                            if (lastExtrude && lastIdx >= 0)
                            {
                                lines.Add(lastIdx);
                                lines.Add(idx);
                            }
                            lastIdx = idx;
                            lastExtrude = true;
                        }
                        else if (eDelta <= 1e-9)
                        {
                            // 空走：断开线段连续性
                            lastExtrude = false;
                            lastIdx = -1;
                        }

                        x = nx; y = ny; z = nz;
                        if (absE) curE += eDelta; else curE = eDelta;
                    }
                    else if (cmd == "G90") absolute = true;
                    else if (cmd == "G91") absolute = false;
                    else if (cmd == "M82") absE = true;
                    else if (cmd == "M83") absE = false;
                    else if (cmd == "G92")
                    {
                        // G92 重置当前坐标
                        for (int i = 1; i < parts.Length; i++)
                        {
                            var p = parts[i];
                            if (p.Length < 2) continue;
                            char k = p[0];
                            if (!double.TryParse(p.Substring(1), NumberStyles.Float, Ci, out var v)) continue;
                            if (k == 'X') x = v;
                            else if (k == 'Y') y = v;
                            else if (k == 'Z') z = v;
                            else if (k == 'E') { curE = v; }
                        }
                    }
                    else if (cmd == "G28")
                    {
                        // 回零：无参数则 X/Y/Z 全归 0
                        bool hx = false, hy = false, hz = false;
                        for (int i = 1; i < parts.Length; i++)
                        {
                            if (parts[i].StartsWith("X")) hx = true;
                            else if (parts[i].StartsWith("Y")) hy = true;
                            else if (parts[i].StartsWith("Z")) hz = true;
                        }
                        if (!hx && !hy && !hz) { hx = hy = hz = true; }
                        if (hx) x = 0;
                        if (hy) y = 0;
                        if (hz) z = 0;
                        lastExtrude = false; lastIdx = -1;
                    }
                }
            }

            if (mesh.Vertices.Count < 2 || mesh.Lines.Count == 0) return false;
            mesh.Normals = new List<Vector3>();
            return true;
        }
    }
}
