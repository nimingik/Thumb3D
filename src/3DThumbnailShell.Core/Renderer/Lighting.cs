using System.Numerics;

namespace _3DThumbnailShell.Core.Renderer
{
    /// <summary>
    /// CPU / GPU 渲染共用的光照参数与辅助：环境光 + 主方向光 + 对侧补光。
    /// 常量与原 SoftwareRenderer 完全一致；GPU 后端（预览器）也直接引用，保证两种后端观感一致。
    /// </summary>
    public static class Lighting
    {
        public const float Ambient = 0.30f;
        public const float Diffuse = 0.62f;
        public const float FillStrength = 0.26f;

        // 照明（补光）模式参数：环境光抬到 IllumAmbient，主光/补光按 IllumDiffuseScale 等比减弱
        // （整体更均匀明亮，暗面也看得见），材质基色再向白抬升 IllumColorLift（深色/纯黑模型因此可辨）。
        public const float IllumAmbient = 0.72f;
        public const float IllumDiffuseScale = 0.55f;
        public const float IllumColorLift = 0.62f;

        public static readonly Vector3 LightDir = Vector3.Normalize(new Vector3(-0.35f, -0.70f, 0.62f));
        public static readonly Vector3 FillLightDir = Vector3.Normalize(new Vector3(0.45f, -0.35f, -0.55f));

        public static readonly Vector4 DefaultColor = new Vector4(0.72f, 0.74f, 0.78f, 1f);

        /// <summary>照明（补光）强度 0..1 的裁剪（0=关闭）。</summary>
        public static float ClampIllumination(float v)
        {
            return v < 0f ? 0f : (v > 1f ? 1f : v);
        }

        /// <summary>
        /// 照明模式的材质基色抬升：按强度向白混合（保留色相，只提亮）。
        /// 纯黑模型（如黑色耗材的 3mf）本身基色为 0，任何光照都乘不出亮度，
        /// 必须抬升基色才能在预览/缩略图里看清轮廓。illumination=0 时原样返回。
        /// </summary>
        public static Vector4 Illuminate(Vector4 c, float illumination)
        {
            var t = ClampIllumination(illumination) * IllumColorLift;
            if (t <= 0f) return c;
            return new Vector4(
                c.X + (1f - c.X) * t,
                c.Y + (1f - c.Y) * t,
                c.Z + (1f - c.Z) * t,
                c.W);
        }

        /// <summary>方向光强度（0..1）：环境 + 主光 + 对侧补光，与 SoftwareRenderer 完全一致。</summary>
        public static float ComputeIntensity(Vector3 nrm)
        {
            return ComputeIntensity(nrm, 1f, 0f);
        }

        /// <summary>
        /// 阴影强度 0..1 的方向光强度：shadowLevel=1 与旧观感完全一致；
        /// shadowLevel=0 时纯环境光（无明暗，全平光）；中间值线性过渡。
        /// 供"自定义模型阴影从无到高"使用。
        /// </summary>
        public static float ComputeIntensity(Vector3 nrm, float shadowLevel)
        {
            return ComputeIntensity(nrm, shadowLevel, 0f);
        }

        /// <summary>
        /// 方向光强度（0..1）。shadowLevel 控制明暗对比；illumination 为"照明（补光）"强度 0..1：
        /// 提高环境光成分、等比减弱主光/补光，让黑色/深色模型也看得清（0 时与旧观感完全一致）。
        /// </summary>
        public static float ComputeIntensity(Vector3 nrm, float shadowLevel, float illumination)
        {
            var lvl = shadowLevel < 0f ? 0f : (shadowLevel > 1f ? 1f : shadowLevel);
            var ill = ClampIllumination(illumination);
            var ambient = Ambient + (1f - lvl) * (1f - Ambient); // lvl=0 → 1.0（纯平光）
            if (ill > 0f) ambient += (IllumAmbient - ambient) * ill; // 补光：环境光抬升
            var dim = 1f - (1f - IllumDiffuseScale) * ill;           // 补光：方向光等比减弱
            var diffuse = Diffuse * lvl * dim;
            var fill = FillStrength * lvl * dim;
            var d = Vector3.Dot(nrm, LightDir);
            if (d < 0f) d = 0f;
            var d2 = Vector3.Dot(nrm, FillLightDir);
            if (d2 < 0f) d2 = 0f;
            var i = ambient + diffuse * d + fill * d2;
            if (i > 1f) i = 1f;
            return i;
        }

        /// <summary>面片基色：多顶点颜色取平均（与统一面渲染一致），否则默认灰。NaN 颜色回退默认灰。</summary>
        public static Vector4 BaseColor(MeshData mesh, int i0, int i1, int i2)
        {
            if (mesh.Colors != null && mesh.Colors.Count > 0)
            {
                var c = Vector4.Zero; var n = 0;
                if (i0 < mesh.Colors.Count) { c += mesh.Colors[i0]; n++; }
                if (i1 < mesh.Colors.Count) { c += mesh.Colors[i1]; n++; }
                if (i2 < mesh.Colors.Count) { c += mesh.Colors[i2]; n++; }
                if (n > 0)
                {
                    var avg = new Vector4(c.X / n, c.Y / n, c.Z / n, 1f);
                    if (!float.IsNaN(avg.X) && !float.IsNaN(avg.Y) && !float.IsNaN(avg.Z))
                        return avg;
                }
            }
            return DefaultColor;
        }

        /// <summary>
        /// 计算逐顶点平滑法线（把相邻三角形面积加权法线按顶点累加再归一化），用于 Gouraud 平滑着色。
        /// 让同一零件表面的明暗连续过渡，消除平面着色在三角形边缘/碎片接缝处的亮度跳变（"破面"感）。
        /// 退化/孤立顶点法线为空（返回 null，两后端回退到 flat 面法线）。
        /// </summary>
        public static Vector3[] SmoothVertexNormals(MeshData mesh, float scale, Vector3 center)
        {
            var vrts = mesh.Vertices;
            if (vrts == null || vrts.Count == 0 || mesh.Indices == null || mesh.Indices.Count < 3)
                return null;
            int nv = vrts.Count;
            var acc = new Vector3[nv];
            var cnt = new int[nv];
            var nFaces = mesh.Indices.Count / 3;
            for (var f = 0; f < nFaces; f++)
            {
                int a = mesh.Indices[f * 3], b = mesh.Indices[f * 3 + 1], c = mesh.Indices[f * 3 + 2];
                if (a < 0 || b < 0 || c < 0 || a >= nv || b >= nv || c >= nv) continue;
                var w0 = (vrts[a] - center) * scale;
                var w1 = (vrts[b] - center) * scale;
                var w2 = (vrts[c] - center) * scale;
                var nrm = Vector3.Cross(w1 - w0, w2 - w0);
                // 退化面跳过（阈值极小：叉积平方 ∝ (边长·scale)^4，多盘同屏尺度小时 1e-12 会误剔细密网格）
                if (!(nrm.LengthSquared() > 1e-20f)) continue;
                acc[a] += nrm; acc[b] += nrm; acc[c] += nrm;
                cnt[a]++; cnt[b]++; cnt[c]++;
            }
            var res = new Vector3[nv];
            for (var i = 0; i < nv; i++)
            {
                if (cnt[i] <= 0) continue;
                var n = Vector3.Normalize(acc[i]);
                if (!float.IsNaN(n.X) && n.LengthSquared() > 0.5f) res[i] = n;
            }
            return res;
        }

        /// <summary>顶点法线强度；法线无效（零/NaN）时回退到给出的面法线。illumination 为"照明（补光）"强度 0..1。</summary>
        public static float VertexIntensity(Vector3 smoothNrm, Vector3 fallbackFaceNrm, float shadowLevel,
            float illumination = 0f)
        {
            var n = smoothNrm;
            if (!(n.LengthSquared() > 0.5f) || float.IsNaN(n.X))
                n = fallbackFaceNrm;
            return ComputeIntensity(n, shadowLevel, illumination);
        }

        /// <summary>
        /// 颜色平滑程度：逐面上色(paint_color)的多色模型强制全平滑（与拓竹切片软件观感一致）。
        /// 拓竹多色分色在切片时把渐变区切成逐面颜色，短程交替（常为 1~3 面一跳）；若按滑块做
        /// "面上色色 × 部分顶点平滑"，亚像素面片会呈现白/蓝斑点，合并视图缩小后就是"点云/破面"感。
        /// 强制颜色全平滑可消除该斑点；滑块仍控制光照（明暗）平滑，二者解耦。
        /// 无逐面上色（纯色/顶点色模型）时颜色本就单一，跟随滑块无副作用。
        /// </summary>
        public static float ColorSmooth(MeshData mesh, float smoothShading)
        {
            if (mesh != null && mesh.FaceColors != null && mesh.FaceColors.Count > 0) return 1f;
            return smoothShading;
        }

        /// <summary>
        /// 逐顶点平滑色（CPU/GPU 共用，保证两后端一致）：把邻接上色面(FaceColors[f].W>0)的 RGB 平均到顶点，
        /// 让多色模型彩绘区域从"逐面平涂马赛克"变为平滑连贯过渡（与拓竹切片软件观感一致）。
        /// 无上色面邻接的顶点置 W=0（运行时回退基色）；没有任何上色面时返回 null（整体回退基色）。
        /// </summary>
        public static Vector4[] SmoothVertexColors(MeshData mesh)
        {
            if (mesh == null || mesh.FaceColors == null || mesh.FaceColors.Count == 0
                || mesh.Indices == null || mesh.Indices.Count < 3)
                return null;
            var vrts = mesh.Vertices;
            if (vrts == null || vrts.Count == 0) return null;

            int nv = vrts.Count;
            var acc = new Vector3[nv];
            var cnt = new int[nv];
            var fc = mesh.FaceColors;
            var nFaces = mesh.Indices.Count / 3;
            var any = false;
            for (var f = 0; f < nFaces && f < fc.Count; f++)
            {
                if (!(fc[f].W > 0f)) continue; // 仅累加上色面
                int a = mesh.Indices[f * 3], b = mesh.Indices[f * 3 + 1], c = mesh.Indices[f * 3 + 2];
                if (a < 0 || b < 0 || c < 0 || a >= nv || b >= nv || c >= nv) continue;
                var rgb = new Vector3(fc[f].X, fc[f].Y, fc[f].Z);
                acc[a] += rgb; cnt[a]++;
                acc[b] += rgb; cnt[b]++;
                acc[c] += rgb; cnt[c]++;
                any = true;
            }
            if (!any) return null;

            var res = new Vector4[nv];
            for (var i = 0; i < nv; i++)
            {
                if (cnt[i] > 0)
                    res[i] = new Vector4(acc[i].X / cnt[i], acc[i].Y / cnt[i], acc[i].Z / cnt[i], 1f);
                else
                    res[i] = new Vector4(0f, 0f, 0f, 0f); // 未上色顶点，W=0 表示回退
            }
            return res;
        }

        /// <summary>
        /// 顶点取色：该顶点有平滑上色色就用它，否则回退到 fallback（自定义颜色/基色）。
        /// 保证 overrideColor 只作用于"未上色"顶点，绝不覆盖上色面的颜色。
        /// </summary>
        public static Vector4 VertexColor(Vector4[] smooth, int i, Vector4 fallback)
        {
            if (smooth != null && i >= 0 && i < smooth.Length && smooth[i].W > 0f)
                return new Vector4(smooth[i].X, smooth[i].Y, smooth[i].Z, 1f);
            return fallback;
        }
    }
}
