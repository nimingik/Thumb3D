using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Numerics;
using _3DThumbnailShell.Core.Renderer;

namespace _3DThumbnailShell.Core.Formats
{
    /// <summary>
    /// STEP (ISO 10303-21) 线框解析器。
    /// 解析实体引用图，提取边曲线（直线/圆/椭圆/B样条）并采样为 3D 线段，
    /// 以 IsLineCloud 方式渲染（与 G-code 共用线段渲染管线）。
    /// 不依赖外部 CAD 内核。
    /// </summary>
    public sealed class StepReader : IMeshParser
    {
        // ---- STEP 实体字典 ----
        private Dictionary<long, StepEntity> _entities;

        public bool CanParse(string ext)
        {
            if (string.IsNullOrEmpty(ext)) return false;
            ext = ext.ToLowerInvariant();
            return ext == ".step" || ext == ".stp";
        }

        public bool TryParse(Stream stream, out MeshData mesh)
        {
            mesh = new MeshData { OriginalFormat = "step" };
            try
            {
                using var sr = new StreamReader(stream);
                var text = sr.ReadToEnd();
                _entities = new Dictionary<long, StepEntity>();

                ParseEntities(text);

                var verts = new List<Vector3>();
                var lines = new List<int>();

                ExtractEdges(verts, lines);

                if (lines.Count == 0)
                {
                    // 无线段，回退占位
                    mesh = null;
                    return false;
                }

                mesh.Vertices = verts;
                mesh.Indices = new List<int>();
                mesh.Lines = lines;
                mesh.IsLineCloud = true;
                return true;
            }
            catch
            {
                mesh = null;
                return false;
            }
        }

        // ---------- 实体解析 ----------

        private void ParseEntities(string text)
        {
            var dataIdx = text.IndexOf("DATA;", StringComparison.Ordinal);
            if (dataIdx < 0) return;
            var endIdx = text.IndexOf("ENDSEC;", dataIdx, StringComparison.Ordinal);
            if (endIdx < 0) endIdx = text.Length;
            var data = text.Substring(dataIdx + 5, endIdx - dataIdx - 5);

            int i = 0;
            int n = data.Length;
            while (i < n)
            {
                while (i < n && (data[i] == ' ' || data[i] == '\r' || data[i] == '\n' || data[i] == '\t')) i++;
                if (i >= n || data[i] != '#') break;
                i++; // skip #
                // parse id
                long id = 0;
                while (i < n && char.IsDigit(data[i])) { id = id * 10 + (data[i] - '0'); i++; }
                while (i < n && (data[i] == ' ' || data[i] == '\t')) i++;
                if (i < n && data[i] == '=') i++;
                while (i < n && (data[i] == ' ' || data[i] == '\t')) i++;
                // parse type name
                int typeStart = i;
                while (i < n && (char.IsLetterOrDigit(data[i]) || data[i] == '_')) i++;
                var type = data.Substring(typeStart, i - typeStart).ToUpperInvariant();
                while (i < n && data[i] != '(') i++;
                if (i >= n) break;
                // parse params (balanced parens)
                int depth = 0;
                int paramStart = i;
                for (; i < n; i++)
                {
                    if (data[i] == '(') depth++;
                    else if (data[i] == ')') { depth--; if (depth == 0) break; }
                }
                var paramsStr = data.Substring(paramStart + 1, i - paramStart - 1);
                i++; // skip closing )
                while (i < n && data[i] != ';') i++;
                if (i < n) i++; // skip ;

                var ent = new StepEntity { Id = id, Type = type, Params = paramsStr };
                _entities[id] = ent;
            }
        }

        // ---------- 边提取 ----------

        private void ExtractEdges(List<Vector3> verts, List<int> lines)
        {
            if (_entities == null) return;
            foreach (var ent in _entities.Values)
            {
                if (ent.Type != "EDGE_CURVE") continue;
                // EDGE_CURVE('name', edge_start, edge_end, edge_geometry, same_sense)
                var args = ParseArgs(ent.Params);
                if (args.Count < 5) continue;
                var startRef = AsRef(args[1]);
                var endRef = AsRef(args[2]);
                var curveRef = AsRef(args[3]);

                Vector3? startPt = ResolveVertexPoint(startRef);
                Vector3? endPt = ResolveVertexPoint(endRef);

                if (curveRef.HasValue && _entities.TryGetValue(curveRef.Value, out var curve))
                {
                    var sampled = SampleCurve(curve, startPt, endPt);
                    if (sampled != null && sampled.Count >= 2)
                    {
                        for (int k = 0; k < sampled.Count - 1; k++)
                        {
                            verts.Add(sampled[k]);
                            verts.Add(sampled[k + 1]);
                            lines.Add(verts.Count - 2);
                            lines.Add(verts.Count - 1);
                        }
                    }
                    else if (startPt.HasValue && endPt.HasValue)
                    {
                        verts.Add(startPt.Value);
                        verts.Add(endPt.Value);
                        lines.Add(verts.Count - 2);
                        lines.Add(verts.Count - 1);
                    }
                }
                else if (startPt.HasValue && endPt.HasValue)
                {
                    verts.Add(startPt.Value);
                    verts.Add(endPt.Value);
                    lines.Add(verts.Count - 2);
                    lines.Add(verts.Count - 1);
                }
            }
        }

        private List<Vector3> SampleCurve(StepEntity curve, Vector3? startPt, Vector3? endPt)
        {
            switch (curve.Type)
            {
                case "LINE":
                    return SampleLine(curve, startPt, endPt);
                case "CIRCLE":
                    return SampleCircle(curve);
                case "ELLIPSE":
                    return SampleEllipse(curve);
                case "B_SPLINE_CURVE":
                case "B_SPLINE_CURVE_WITH_KNOTS":
                    return SampleBSpline(curve);
                default:
                    return null;
            }
        }

        // LINE('name', point, vector) -- vector is direction * magnitude
        private List<Vector3> SampleLine(StepEntity line, Vector3? startPt, Vector3? endPt)
        {
            var args = ParseArgs(line.Params);
            if (args.Count < 3) return null;
            var pRef = AsRef(args[1]);
            var vRef = AsRef(args[2]);
            if (!pRef.HasValue || !vRef.HasValue) return null;
            var p = ResolveCartesianPoint(pRef.Value);
            var v = ResolveVector(vRef.Value);
            if (p == null || v == null) return null;

            // 用端点确定线段长度
            if (startPt.HasValue && endPt.HasValue)
            {
                return new List<Vector3> { startPt.Value, endPt.Value };
            }
            var a = p.Value;
            var b = a + v.Value;
            return new List<Vector3> { a, b };
        }

        // CIRCLE('name', axis2_placement_3d, radius)
        private List<Vector3> SampleCircle(StepEntity circle)
        {
            var args = ParseArgs(circle.Params);
            if (args.Count < 3) return null;
            var plcRef = AsRef(args[1]);
            double radius = AsDouble(args[2]);
            if (!plcRef.HasValue || radius <= 0) return null;
            var plc = ResolveAxis2Placement3D(plcRef.Value);
            var center = plc.Center; var zAxis = plc.ZAxis; var xAxis = plc.XAxis;
            if (zAxis == Vector3.Zero) return null;
            var yAxis = Vector3.Cross(zAxis, xAxis);
            yAxis = yAxis.LengthSquared() > 1e-12f ? Vector3.Normalize(yAxis) : Vector3.UnitY;

            var pts = new List<Vector3>();
            int segs = 48;
            for (int i = 0; i <= segs; i++)
            {
                double t = 2 * Math.PI * i / segs;
                var pt = center + (float)(radius * Math.Cos(t)) * xAxis + (float)(radius * Math.Sin(t)) * yAxis;
                pts.Add(pt);
            }
            return pts;
        }

        // ELLIPSE('name', axis2_placement_3d, semi_axis1, semi_axis2)
        private List<Vector3> SampleEllipse(StepEntity ellipse)
        {
            var args = ParseArgs(ellipse.Params);
            if (args.Count < 4) return null;
            var plcRef = AsRef(args[1]);
            double a = AsDouble(args[2]);
            double b = AsDouble(args[3]);
            if (!plcRef.HasValue) return null;
            var plc = ResolveAxis2Placement3D(plcRef.Value);
            var center = plc.Center; var zAxis = plc.ZAxis; var xAxis = plc.XAxis;
            if (zAxis == Vector3.Zero) return null;
            var yAxis = Vector3.Cross(zAxis, xAxis);
            yAxis = yAxis.LengthSquared() > 1e-12f ? Vector3.Normalize(yAxis) : Vector3.UnitY;

            var pts = new List<Vector3>();
            int segs = 48;
            for (int i = 0; i <= segs; i++)
            {
                double t = 2 * Math.PI * i / segs;
                var pt = center + (float)(a * Math.Cos(t)) * xAxis + (float)(b * Math.Sin(t)) * yAxis;
                pts.Add(pt);
            }
            return pts;
        }

        // 简化 B-spline：用控制点折线近似
        private List<Vector3> SampleBSpline(StepEntity bspline)
        {
            var args = ParseArgs(bspline.Params);
            if (args.Count < 4) return null;
            // degree, control_points_list, curve_form, closed_curve, self_intersect
            var cpList = args[1];
            var pts = new List<Vector3>();
            // 参数形如 (#10,#11,#12)
            var inner = StripParens(cpList);
            var refs = SplitTopLevel(inner);
            foreach (var r in refs)
            {
                var rf = AsRef(r);
                if (rf.HasValue)
                {
                    var p = ResolveCartesianPoint(rf.Value);
                    if (p.HasValue) pts.Add(p.Value);
                }
            }
            return pts.Count >= 2 ? pts : null;
        }

        // ---------- 引用解析 ----------

        private Vector3? ResolveCartesianPoint(long id)
        {
            if (!_entities.TryGetValue(id, out var e)) return null;
            // CARTESIAN_POINT('name', (x, y, z))
            if (e.Type == "CARTESIAN_POINT")
            {
                var args = ParseArgs(e.Params);
                if (args.Count < 2) return null;
                var coords = StripParens(args[1]);
                var nums = SplitTopLevel(coords);
                if (nums.Count < 3) return null;
                return new Vector3((float)AsDouble(nums[0]), (float)AsDouble(nums[1]), (float)AsDouble(nums[2]));
            }
            return null;
        }

        private Vector3? ResolveVertexPoint(long? id)
        {
            if (!id.HasValue) return null;
            if (!_entities.TryGetValue(id.Value, out var e)) return null;
            if (e.Type == "VERTEX_POINT")
            {
                // VERTEX_POINT('name', cartesian_point)
                var args = ParseArgs(e.Params);
                if (args.Count < 2) return null;
                var cpRef = AsRef(args[1]);
                if (cpRef.HasValue) return ResolveCartesianPoint(cpRef.Value);
            }
            else if (e.Type == "CARTESIAN_POINT")
            {
                return ResolveCartesianPoint(id.Value);
            }
            return null;
        }

        private Vector3? ResolveVector(long id)
        {
            if (!_entities.TryGetValue(id, out var e)) return null;
            // VECTOR('name', direction, magnitude)
            if (e.Type == "VECTOR")
            {
                var args = ParseArgs(e.Params);
                if (args.Count < 3) return null;
                var dirRef = AsRef(args[1]);
                double mag = AsDouble(args[2]);
                if (!dirRef.HasValue) return null;
                var dir = ResolveDirection(dirRef.Value);
                if (dir == Vector3.Zero) return Vector3.Zero;
                return dir * (float)mag;
            }
            // DIRECTION can be used directly
            if (e.Type == "DIRECTION")
            {
                return ResolveDirection(id);
            }
            return null;
        }

        private Vector3 ResolveDirection(long id)
        {
            if (!_entities.TryGetValue(id, out var e)) return Vector3.Zero;
            if (e.Type == "DIRECTION")
            {
                var args = ParseArgs(e.Params);
                if (args.Count < 2) return Vector3.Zero;
                var coords = StripParens(args[1]);
                var nums = SplitTopLevel(coords);
                if (nums.Count < 3) return Vector3.Zero;
                var d = new Vector3((float)AsDouble(nums[0]), (float)AsDouble(nums[1]), (float)AsDouble(nums[2]));
                return d.LengthSquared() > 1e-12f ? Vector3.Normalize(d) : Vector3.Zero;
            }
            return Vector3.Zero;
        }

        private AxisPlacement ResolveAxis2Placement3D(long id)
        {
            if (!_entities.TryGetValue(id, out var e)) return new AxisPlacement(Vector3.Zero, Vector3.Zero, Vector3.UnitX);
            // AXIS2_PLACEMENT_3D('name', location, axis, ref_direction)
            if (e.Type == "AXIS2_PLACEMENT_3D" || e.Type == "AXIS2_PLACEMENT_2D")
            {
                var args = ParseArgs(e.Params);
                Vector3 center = Vector3.Zero;
                Vector3 zAxis = Vector3.UnitZ;
                Vector3 xAxis = Vector3.UnitX;
                if (args.Count >= 2)
                {
                    var locRef = AsRef(args[1]);
                    if (locRef.HasValue) center = ResolveCartesianPoint(locRef.Value) ?? Vector3.Zero;
                }
                if (args.Count >= 3 && AsRef(args[2]).HasValue)
                {
                    var z = ResolveDirection(AsRef(args[2]).Value);
                    if (z != Vector3.Zero) zAxis = z;
                }
                if (args.Count >= 4 && AsRef(args[3]).HasValue)
                {
                    var x = ResolveDirection(AsRef(args[3]).Value);
                    if (x != Vector3.Zero) xAxis = x;
                }
                // 正交化
                zAxis = Vector3.Normalize(zAxis);
                var dot = Vector3.Dot(xAxis, zAxis);
                xAxis = xAxis - dot * zAxis;
                if (xAxis.LengthSquared() < 1e-12f)
                {
                    if (Math.Abs(zAxis.X) < 0.9f)
                        xAxis = Vector3.Normalize(Vector3.Cross(zAxis, Vector3.UnitX));
                    else
                        xAxis = Vector3.Normalize(Vector3.Cross(zAxis, Vector3.UnitY));
                }
                else
                {
                    xAxis = Vector3.Normalize(xAxis);
                }
                return new AxisPlacement(center, zAxis, xAxis);
            }
            return new AxisPlacement(Vector3.Zero, Vector3.Zero, Vector3.UnitX);
        }

        // ---------- 参数解析工具 ----------

        /// <summary>按顶层逗号分割参数字符串，忽略括号和引号内的逗号</summary>
        private List<string> ParseArgs(string s)
        {
            var result = new List<string>();
            int depth = 0;
            bool inStr = false;
            int start = 0;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '\'') inStr = !inStr;
                else if (!inStr)
                {
                    if (c == '(') depth++;
                    else if (c == ')') depth--;
                    else if (c == ',' && depth == 0)
                    {
                        result.Add(s.Substring(start, i - start).Trim());
                        start = i + 1;
                    }
                }
            }
            if (start < s.Length) result.Add(s.Substring(start).Trim());
            return result;
        }

        private string StripParens(string s)
        {
            s = s.Trim();
            if (s.Length >= 2 && s[0] == '(' && s[s.Length - 1] == ')')
                return s.Substring(1, s.Length - 2);
            return s;
        }

        private List<string> SplitTopLevel(string s)
        {
            var result = new List<string>();
            int depth = 0;
            bool inStr = false;
            int start = 0;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '\'') inStr = !inStr;
                else if (!inStr)
                {
                    if (c == '(') depth++;
                    else if (c == ')') depth--;
                    else if (c == ',' && depth == 0)
                    {
                        result.Add(s.Substring(start, i - start).Trim());
                        start = i + 1;
                    }
                }
            }
            if (start < s.Length) result.Add(s.Substring(start).Trim());
            return result;
        }

        private long? AsRef(string s)
        {
            s = s.Trim();
            if (s.StartsWith("#"))
            {
                if (long.TryParse(s.Substring(1), out long id)) return id;
            }
            return null;
        }

        private double AsDouble(string s)
        {
            s = s.Trim();
            if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double v))
                return v;
            return 0;
        }
    }

    internal class StepEntity
    {
        public long Id;
        public string Type;
        public string Params;
    }

    internal struct AxisPlacement
    {
        public Vector3 Center;
        public Vector3 ZAxis;
        public Vector3 XAxis;
        public AxisPlacement(Vector3 center, Vector3 zAxis, Vector3 xAxis)
        {
            Center = center; ZAxis = zAxis; XAxis = xAxis;
        }
    }
}
