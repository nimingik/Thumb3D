using System;
using System.Numerics;

namespace _3DThumbnailShell.Core.Renderer
{
    /// <summary>
    /// 纯 CPU 软件渲染器：归一化 → 相机/投影 → 屏幕变换 → 顶下 BGRA 输出。
    /// 网格走三角形扫描线光栅化 + z-buffer + flat 光照；点云走逐点投影。
    /// 全程不创建任何 GPU/OpenGL/DirectX 上下文，可在 explorer 内稳定运行。
    /// </summary>
    public sealed class SoftwareRenderer
    {
        // 默认摄像机：119° 方位角 + 28.5° 俯视（椭球轨道相机，az=2.077、el=0.4974）
        // 缩略图与预览器默认视角一致（三四视图，模型船头朝右下）
        private const float DefaultAz = 2.077f;
        private const float DefaultEl = 0.4974f;

        // 调试钩子：诊断 net48 下渲染崩溃位置（ThumbnailProvider 可挂接后写入 provider.log）
        public static Action<string> Trace;

        // Bayer 有序抖动幅度（LSB）。抖动原用于打散 8bit 色带，但幅度过大（±8）会在"直接输出"的
        // 缩略图上形成可见噪点（预览器有超采样+缩放会把它平均掉，缩略图没有），故降至 ±2：
        // 仍能抑制色带，又不产生可见颗粒。
        private const int DitherAmp = 2;

        // 光照参数见 Lighting（CPU/GPU 共用，GPU 后端引用同一常量保证观感一致）

        public RenderResult Render(MeshData mesh, int size, int maxTriangles = 0)
        {
            // 兼容旧签名：正方形输出 + 默认相机视角（缩略图管线，带超采样抗锯齿）。
            // maxTriangles > 0 时按等距抽稀（LOD），超大模型缩略图也能快速渲染（见 RenderTrianglesPass）。
            return Render(mesh, size, size, DefaultAz, DefaultEl, 3.1f, 0f, 0f, 1f,
                maxTriangles, null, 1f, 1f, true, null, 0.25f, null, false,
                ThumbSuperSample(size, size), 0f, true, null, 0.25f);
        }

        /// <summary>
        /// 参数化渲染：任意输出尺寸（宽/高）、相机方位角/仰角/距离（球形轨道相机）、
        /// 屏幕像素平移（panX/panY）、视图缩放（zoom，围绕画面中心）。
        /// maxTriangles &gt; 0 时按等距抽稀（LOD）渲染到不超过该面数（预览器低画质用）。
        /// overrideColor 非 null 时用该颜色覆盖模型基色（自定义模型颜色）；
        /// shadowLevel 0..1 控制阴影强度（0=无阴影纯平光，1=标准，见 Lighting）。
        /// smoothShading 0..1 控制平滑着色程度（0=每面平涂：统一面光强+面颜色，多色边界锐利；
        /// 1=当前全平滑 Gouraud）。默认 1 保持旧行为（Shell/explorer 缩略图不受影响）。
        /// showNegative=false 隐藏负零件；negativeColor 非 null 时用该颜色覆盖负零件基色；
        /// negativeOpacity 0..1 控制负零件不透明度（0=全透明，1=不透明实体）。
        /// backgroundColor 非 null 时先填充整帧为该不透明颜色（BGRA）；null = 保持透明黑
        /// （默认，预览器背景图片模式下由显示层把图片合成在透明背景后）。
        /// 预览器用它在 3D 交互（旋转/缩放/平移）时实时重绘。
        /// superSample &gt; 1 时按该倍数放大渲染再做箱式降采样（缩略图抗锯齿，见下方注释）。
        /// </summary>
        public RenderResult Render(MeshData mesh, int width, int height,
            float az, float el, float dist, float panX, float panY, float zoom,
            int maxTriangles = 0, Vector4? overrideColor = null, float shadowLevel = 1f,
            float smoothShading = 1f, bool showNegative = true, Vector4? negativeColor = null,
            float negativeOpacity = 0.25f, Vector4? backgroundColor = null,
            bool showFloorGrid = false, int superSample = 1, float illumination = 0f,
            bool showModifier = true, Vector4? modifierColor = null, float modifierOpacity = 0.25f)
        {
            if (mesh == null || mesh.Vertices == null || mesh.Vertices.Count == 0
                || width <= 0 || height <= 0)
                return null;

            // 超采样抗锯齿：本光栅化器每像素只取 1 个采样点（无 MSAA）。模型里大量"亚像素级细薄
            // 面片"（切片软件导出的细长三角，或薄壁/细筋）在缩略图尺寸下会随机地覆盖或不覆盖
            // 像素中心，于是同一表面相邻像素在"亮面/暗面"间二值跳变，看起来就是密集白噪点。
            // 预览器一直有"高分辨率渲染 + 缩放平均"，所以看不到；缩略图直出就暴露了。
            // 这里同样放大渲染再按预乘 alpha 箱式降采样：亚像素面片按覆盖率混合，噪点变成平滑灰阶，
            // 边缘顺带得到抗锯齿。
            if (superSample > 1)
            {
                var big = Render(mesh, width * superSample, height * superSample,
                    az, el, dist, panX * superSample, panY * superSample, zoom,
                    maxTriangles, overrideColor, shadowLevel, smoothShading,
                    showNegative, negativeColor, negativeOpacity, backgroundColor,
                    showFloorGrid, 1, illumination, showModifier, modifierColor, modifierOpacity);
                if (big == null || big.Bgra == null) return null;
                return new RenderResult
                {
                    Width = width,
                    Height = height,
                    Bgra = Downsample(big.Bgra, big.Width, big.Height, width, height)
                };
            }

            var w = width;
            var h = height;
            Trace?.Invoke("r0");
            var res = new RenderResult { Width = w, Height = h };
            var buf = new byte[(long)w * h * 4]; // BGRA
            res.Bgra = buf;
            if (backgroundColor.HasValue) FillBackground(buf, backgroundColor.Value);
            Trace?.Invoke("r1 buf=" + buf.Length);

            // 1. 计算世界空间（缩放居中到单位尺度）的顶点
            var scale = ComputeNormalizationScale(mesh.Vertices, out var center, out var vmin);
            Trace?.Invoke("r2 scale=" + scale);
            var vrts = mesh.Vertices;
            int nv = vrts.Count;

            // 2. 相机
            var cameraPos = Spherical(dist, az, el);
            Trace?.Invoke("r3 cam=" + cameraPos.X + "," + cameraPos.Y + "," + cameraPos.Z);
            var view = Matrix4x4.CreateLookAt(cameraPos, Vector3.Zero, Vector3.UnitY);
            Trace?.Invoke("r4 view");
            var proj = Matrix4x4.CreatePerspectiveFieldOfView((float)(Math.PI / 3.6),
                (float)w / h, 0.1f, 20f);
            Trace?.Invoke("r5 proj");

            // 3. 深度缓冲（float，越近 depth 越小）
            var dbuf = new float[w * h];
            for (var i = 0; i < dbuf.Length; i++) dbuf[i] = float.MaxValue;
            Trace?.Invoke("r6 dbuf");

            if (mesh.IsPointCloud)
            {
                RenderPoints(mesh, scale, center, view, proj, w, h, panX, panY, zoom, buf, dbuf, overrideColor, illumination);
            }
            else if (mesh.IsLineCloud)
            {
                RenderLines(mesh, scale, center, view, proj, w, h, panX, panY, zoom, buf, dbuf, overrideColor, illumination);
            }
            else
            {
                Trace?.Invoke("r7 triangles nf=" + (mesh.Indices == null ? -1 : mesh.Indices.Count / 3));
                RenderTriangles(mesh, scale, center, view, proj, w, h, panX, panY, zoom, buf, dbuf,
                    maxTriangles, overrideColor, shadowLevel, smoothShading,
                    showNegative, negativeColor, negativeOpacity, illumination,
                    showModifier, modifierColor, modifierOpacity);
            }

            // 地面网格（画在模型之后，深度测试保证模型遮挡网格；点云/线云路径也绘制）
            if (showFloorGrid && !mesh.IsLineCloud)
            {
                var y0 = (vmin.Y - center.Y) * scale - 0.005f; // 模型底面，微降防共面闪烁
                RenderFloorGrid(y0, view, proj, w, h, panX, panY, zoom, buf, dbuf, backgroundColor);
            }
            Trace?.Invoke("r8 done");

            return res;
        }

        private float ComputeNormalizationScale(System.Collections.Generic.List<Vector3> vrts, out Vector3 center, out Vector3 min)
        {
            min = vrts[0]; var max = vrts[0];
            for (var i = 1; i < vrts.Count; i++)
            {
                var v = vrts[i];
                min = Vector3.Min(min, v); max = Vector3.Max(max, v);
            }
            center = (min + max) * 0.5f;
            var dim = max - min;
            var md = Math.Max(dim.X, Math.Max(dim.Y, dim.Z));
            if (md <= 1e-9f) md = 1f;
            return 1.6f / md; // 半宽约 0.8，留边距
        }

        private static Vector3 Spherical(float r, float az, float el)
        {
            var caz = (float)Math.Cos(az); var saz = (float)Math.Sin(az);
            var cel = (float)Math.Cos(el); var sel = (float)Math.Sin(el);
            return new Vector3(r * cel * saz, r * sel, r * cel * caz);
        }

        // ================= 超采样（缩略图抗锯齿） =================

        /// <summary>
        /// 缩略图超采样倍数：把长边渲染到约 1024px 再降采样（上限 4 倍）。
        /// 256px 缩略图 → 4×（1024px），512px → 2×，682px(4:3 的 512 高) → 2×，已 ≥1024px 则不超采样。
        /// </summary>
        public static int ThumbSuperSample(int width, int height)
        {
            var m = Math.Max(width, height);
            if (m <= 0) return 1;
            var ss = (int)Math.Round(1024.0 / m);
            if (ss < 1) ss = 1;
            if (ss > 4) ss = 4;
            return ss;
        }

        /// <summary>
        /// 整数倍箱式降采样：alpha 取平均（等效覆盖率，保证透明底缩略图边缘不过曝/发黑），
        /// 颜色按 alpha 加权平均（预乘意义下平均，避免半透明边缘混出黑边）。
        /// </summary>
        private static byte[] Downsample(byte[] src, int sw, int sh, int dw, int dh)
        {
            var dst = new byte[(long)dw * dh * 4];
            var fx = sw / dw; if (fx < 1) fx = 1;
            var fy = sh / dh; if (fy < 1) fy = 1;
            var tot = (long)fx * fy;
            for (var y = 0; y < dh; y++)
            {
                for (var x = 0; x < dw; x++)
                {
                    long a = 0, r = 0, g = 0, b = 0;
                    for (var yy = 0; yy < fy; yy++)
                    {
                        var row = ((long)(y * fy + yy) * sw + x * fx) * 4;
                        for (var xx = 0; xx < fx; xx++)
                        {
                            var p = row + xx * 4;
                            if (p < 0 || p + 3 >= src.Length) continue;
                            var pa = src[p + 3];       // BGRA：+0=B +1=G +2=R +3=A
                            a += pa;
                            b += src[p] * pa;
                            g += src[p + 1] * pa;
                            r += src[p + 2] * pa;
                        }
                    }
                    var q = ((long)y * dw + x) * 4;
                    dst[q + 3] = (byte)(a / tot);
                    if (a > 0)
                    {
                        dst[q] = (byte)(b / a);
                        dst[q + 1] = (byte)(g / a);
                        dst[q + 2] = (byte)(r / a);
                    }
                }
            }
            return dst;
        }

        // ================= 三角形网格路径 =================
        private void RenderTriangles(MeshData mesh, float scale, Vector3 center,
            Matrix4x4 view, Matrix4x4 proj, int w, int h,
            float panX, float panY, float zoom, byte[] buf, float[] dbuf, int maxTriangles,
            Vector4? overrideColor, float shadowLevel, float smoothShading,
            bool showNegative, Vector4? negativeColor, float negativeOpacity, float illumination,
            bool showModifier, Vector4? modifierColor, float modifierOpacity)
        {
            var idxs = mesh.Indices;
            if (idxs.Count == 0)
            {
                // 无索引但有顶点（退化）：退化为点云
                RenderPoints(mesh, scale, center, view, proj, w, h, panX, panY, zoom, buf, dbuf, overrideColor, illumination);
                return;
            }

            // Gouraud 平滑法线 + 逐顶点平滑色：对整个网格算一次，两遍绘制共用（避免重复计算）
            var smoothNormals = Lighting.SmoothVertexNormals(mesh, scale, center);
            var vcol = Lighting.SmoothVertexColors(mesh);

            // 三遍绘制保证遮挡正确：先画实体（不透明、写深度），再画负零件、修改器（alpha 混合、只测深度不写深度）。
            // 这样无论面索引顺序如何，实体在前时总能盖住叠加层，叠加层永远叠在实体/背景之上（与 GPU 后端一致）。
            RenderTrianglesPass(mesh, scale, center, view, proj, w, h, panX, panY, zoom, buf, dbuf,
                maxTriangles, overrideColor, shadowLevel, smoothShading, smoothNormals, vcol,
                MeshData.PartKindNormal, null, 1f, illumination);
            if (showNegative && negativeOpacity > 0f)
                RenderTrianglesPass(mesh, scale, center, view, proj, w, h, panX, panY, zoom, buf, dbuf,
                    maxTriangles, overrideColor, shadowLevel, smoothShading, smoothNormals, vcol,
                    MeshData.PartKindNegative, negativeColor, negativeOpacity, illumination);
            if (showModifier && modifierOpacity > 0f && HasModifierFaces(mesh))
                RenderTrianglesPass(mesh, scale, center, view, proj, w, h, panX, panY, zoom, buf, dbuf,
                    maxTriangles, overrideColor, shadowLevel, smoothShading, smoothNormals, vcol,
                    MeshData.PartKindModifier, modifierColor, modifierOpacity, illumination);
        }

        /// <summary>网格是否含修改器面（决定是否需要多跑一遍叠加绘制）。</summary>
        private static bool HasModifierFaces(MeshData mesh)
        {
            var fm = mesh.FaceModifier;
            if (fm == null || fm.Count == 0) return false;
            for (var i = 0; i < fm.Count; i++)
                if (fm[i] != 0) return true;
            return false;
        }

        /// <summary>单遍三角形绘制：partFilter=普通实体（不透明+写深度）/负零件/修改器（alpha 混合+不写深度）。</summary>
        private void RenderTrianglesPass(MeshData mesh, float scale, Vector3 center,
            Matrix4x4 view, Matrix4x4 proj, int w, int h,
            float panX, float panY, float zoom, byte[] buf, float[] dbuf, int maxTriangles,
            Vector4? overrideColor, float shadowLevel, float smoothShading,
            Vector3[] smoothNormals, Vector4[] vcol,
            byte partFilter, Vector4? partColor, float partOpacity, float illumination)
        {
            var vrts = mesh.Vertices;
            var idxs = mesh.Indices;
            var nFaces = idxs.Count / 3;

            // LOD 抽稀：超过 maxTriangles 时按等距 stride 取面，保持整体轮廓
            var stride = 1;
            if (maxTriangles > 0 && nFaces > maxTriangles)
                stride = (int)Math.Ceiling((double)nFaces / maxTriangles);
            var nDraw = (int)Math.Ceiling((double)nFaces / stride);

            // 屏幕坐标（含深度）
            var sx = new float[nDraw * 3];
            var sy = new float[nDraw * 3];
            var sd = new float[nDraw * 3];
            var outside = new byte[nDraw * 3];

            var k = 0;
            for (var f = 0; f < nFaces; f += stride, k++)
            {
                int i0 = idxs[f * 3], i1 = idxs[f * 3 + 1], i2 = idxs[f * 3 + 2];
                var vm0 = (vrts[i0] - center) * scale;
                var vm1 = (vrts[i1] - center) * scale;
                var vm2 = (vrts[i2] - center) * scale;

                if (!ProjectToScreen(vm0, view, proj, w, h, panX, panY, zoom, out sx[k * 3], out sy[k * 3], out sd[k * 3])) outside[k * 3] = 1;
                if (!ProjectToScreen(vm1, view, proj, w, h, panX, panY, zoom, out sx[k * 3 + 1], out sy[k * 3 + 1], out sd[k * 3 + 1])) outside[k * 3 + 1] = 1;
                if (!ProjectToScreen(vm2, view, proj, w, h, panX, panY, zoom, out sx[k * 3 + 2], out sy[k * 3 + 2], out sd[k * 3 + 2])) outside[k * 3 + 2] = 1;

                if (outside[k * 3] != 0 || outside[k * 3 + 1] != 0 || outside[k * 3 + 2] != 0)
                    continue; // 落在近平面/背后，跳过（归一化模型极少触发）

                // 部件类型判定：与当前遍要求不符则跳过（实体 / 负零件 / 修改器各一遍）
                var kind = MeshData.PartKindNormal;
                if (mesh.FaceNegative != null && f < mesh.FaceNegative.Count && mesh.FaceNegative[f] != 0)
                    kind = MeshData.PartKindNegative;
                if (mesh.FaceModifier != null && f < mesh.FaceModifier.Count && mesh.FaceModifier[f] != 0)
                    kind = MeshData.PartKindModifier;
                if (kind != partFilter) continue;

                // 面片亮度：Gouraud —— 用逐顶点平滑法线算三顶点强度，光栅化时按重心插值。
                // 平滑着色程度 t：顶点强度 = lerp(面平光强度, Gouraud 顶点强度, t)；
                // t=0 时三顶点同强度 → 整个面平涂（锐利棱边），t=1 恢复当前 Gouraud。
                var nrmRaw = Vector3.Cross(vm1 - vm0, vm2 - vm0);
                // NaN/零向量防护：退化三角形直接跳过（NaN > x 为 false，一并排除）。
                // 阈值必须极小（1e-20，与 GPU 后端一致）：叉积平方 ∝ (边长·scale)^4，
                // 多盘同屏时归一化尺度小，用 1e-12 会把细密网格整片误剔除 → 表面孔洞/碎片感。
                if (!(nrmRaw.LengthSquared() > 1e-20f)) continue;
                var nrm = Vector3.Normalize(nrmRaw);
                float intFlat = Lighting.ComputeIntensity(nrm, shadowLevel, illumination);
                float int0, int1, int2;
                if (smoothNormals != null && i0 < smoothNormals.Length && i1 < smoothNormals.Length && i2 < smoothNormals.Length)
                {
                    int0 = Lerp(intFlat, Lighting.VertexIntensity(smoothNormals[i0], nrm, shadowLevel, illumination), smoothShading);
                    int1 = Lerp(intFlat, Lighting.VertexIntensity(smoothNormals[i1], nrm, shadowLevel, illumination), smoothShading);
                    int2 = Lerp(intFlat, Lighting.VertexIntensity(smoothNormals[i2], nrm, shadowLevel, illumination), smoothShading);
                }
                else
                {
                    int0 = int1 = int2 = intFlat;
                }
                // 取色优先级：拓竹多色逐面颜色(平滑为逐顶点色) > 自定义颜色(仅未上色顶点) > 顶点色/默认灰
                // 自定义颜色不应掩盖模型自带的多彩：只作用于"未上色"顶点，绝不动上色顶点。
                // 平滑着色程度 t：每顶点显示色 = lerp(面上色色 Cf, 逐顶点平滑色, t)；
                // t=0 时整面上色顶点同色 → 颜色区域锐利不糊边；t=1 恢复当前全平滑。未上色面回退基色。
                Vector4 fallback;
                if (overrideColor.HasValue) fallback = overrideColor.Value;
                else fallback = Lighting.BaseColor(mesh, i0, i1, i2);
                var vc0 = Lighting.VertexColor(vcol, i0, fallback);
                var vc1 = Lighting.VertexColor(vcol, i1, fallback);
                var vc2 = Lighting.VertexColor(vcol, i2, fallback);
                Vector4 cf = fallback;
                if (mesh.FaceColors != null && f < mesh.FaceColors.Count && mesh.FaceColors[f].W > 0f)
                    cf = new Vector4(mesh.FaceColors[f].X, mesh.FaceColors[f].Y, mesh.FaceColors[f].Z, 1f);
                // 颜色平滑与光照平滑解耦：paint_color 多色模型颜色强制全平滑（消除逐面斑点/点云感），
                // 滑块只控制光照（明暗）平滑。
                var colorSmooth = Lighting.ColorSmooth(mesh, smoothShading);
                var c0 = Lerp(cf, vc0, colorSmooth);
                var c1 = Lerp(cf, vc1, colorSmooth);
                var c2 = Lerp(cf, vc2, colorSmooth);

                // 叠加层（负零件/修改器）颜色 override：统一用设置的颜色（忽略模型自带面/顶点色），光强照常作用
                if (partFilter != MeshData.PartKindNormal && partColor.HasValue)
                {
                    c0 = partColor.Value;
                    c1 = partColor.Value;
                    c2 = partColor.Value;
                }

                // 照明（补光）：抬升材质基色，保证纯黑/深色模型（黑色耗材、深色多色件）也能看清轮廓
                if (illumination > 0f)
                {
                    c0 = Lighting.Illuminate(c0, illumination);
                    c1 = Lighting.Illuminate(c1, illumination);
                    c2 = Lighting.Illuminate(c2, illumination);
                }

                // 叠加层（负零件/修改器）：alpha=不透明度混合、不写深度 → 半透明叠在实体上可透见内部
                RasterizeTriangle(
                    sx[k * 3], sy[k * 3], sd[k * 3],
                    sx[k * 3 + 1], sy[k * 3 + 1], sd[k * 3 + 1],
                    sx[k * 3 + 2], sy[k * 3 + 2], sd[k * 3 + 2],
                    c0.X, c0.Y, c0.Z,
                    c1.X, c1.Y, c1.Z,
                    c2.X, c2.Y, c2.Z,
                    int0, int1, int2, w, h, buf, dbuf,
                    partFilter == MeshData.PartKindNormal ? 1f : partOpacity,
                    partFilter == MeshData.PartKindNormal);
            }
        }

        // ================= 点云路径 =================
        private void RenderPoints(MeshData mesh, float scale, Vector3 center,
            Matrix4x4 view, Matrix4x4 proj, int w, int h,
            float panX, float panY, float zoom, byte[] buf, float[] dbuf, Vector4? overrideColor,
            float illumination)
        {
            var vrts = mesh.Vertices;
            for (var i = 0; i < vrts.Count; i++)
            {
                var vm = (vrts[i] - center) * scale;
                if (!ProjectToScreen(vm, view, proj, w, h, panX, panY, zoom, out var x, out var y, out var d))
                    continue;

                var col = overrideColor.HasValue
                    ? overrideColor.Value
                    : (mesh.Colors != null && i < mesh.Colors.Count ? mesh.Colors[i] : Lighting.DefaultColor);
                col = Lighting.Illuminate(col, illumination); // 照明：抬升深色点云，避免黑模型看不见
                var b = (byte)Clamp(col.Z * 255f, 0f, 255f);
                var g = (byte)Clamp(col.Y * 255f, 0f, 255f);
                var r = (byte)Clamp(col.X * 255f, 0f, 255f);

                int px = (int)Math.Round(x), py = (int)Math.Round(y);
                // 2x2 方块点
                for (var dy = 0; dy <= 1; dy++)
                    for (var dx = 0; dx <= 1; dx++)
                        PutPixel(px + dx, py + dy, d, r, g, b, w, h, buf, dbuf);
            }
        }

        private static float Clamp(float v, float lo, float hi) => v < lo ? lo : (v > hi ? hi : v);

        private static float Lerp(float a, float b, float t) => a + (b - a) * t;

        private static Vector4 Lerp(Vector4 a, Vector4 b, float t)
        {
            var r = a + (b - a) * t;
            return new Vector4(r.X, r.Y, r.Z, 1f);
        }

        // ================= 线段路径（G-code 工具路径） =================
        private void RenderLines(MeshData mesh, float scale, Vector3 center,
            Matrix4x4 view, Matrix4x4 proj, int w, int h,
            float panX, float panY, float zoom, byte[] buf, float[] dbuf, Vector4? overrideColor,
            float illumination)
        {
            var vrts = mesh.Vertices;
            var lines = mesh.Lines;
            if (lines == null) return;
            var nSeg = lines.Count / 2;
            for (var s = 0; s < nSeg; s++)
            {
                int i0 = lines[s * 2], i1 = lines[s * 2 + 1];
                if (i0 < 0 || i0 >= vrts.Count || i1 < 0 || i1 >= vrts.Count) continue;
                var v0 = (vrts[i0] - center) * scale;
                var v1 = (vrts[i1] - center) * scale;
                if (!ProjectToScreen(v0, view, proj, w, h, panX, panY, zoom, out var x0, out var y0, out var d0))
                    continue;
                if (!ProjectToScreen(v1, view, proj, w, h, panX, panY, zoom, out var x1, out var y1, out var d1))
                    continue;

                // 颜色：优先逐顶点色，否则默认色（无光照，线段不做明暗）
                var col = Lighting.Illuminate(overrideColor ?? Lighting.DefaultColor, illumination);
                byte r = (byte)Clamp(col.X * 255f, 0f, 255f);
                byte g = (byte)Clamp(col.Y * 255f, 0f, 255f);
                byte b = (byte)Clamp(col.Z * 255f, 0f, 255f);

                DrawBresenhamLine((int)Math.Round(x0), (int)Math.Round(y0), d0,
                    (int)Math.Round(x1), (int)Math.Round(y1), d1, r, g, b, w, h, buf, dbuf);
            }
        }

        private static void DrawBresenhamLine(int x0, int y0, float d0, int x1, int y1, float d1,
            byte r, byte g, byte b, int w, int h, byte[] buf, float[] dbuf)
        {
            int dx = Math.Abs(x1 - x0), dy = Math.Abs(y1 - y0);
            int sx = x0 < x1 ? 1 : -1, sy = y0 < y1 ? 1 : -1;
            int err = dx - dy;
            int steps = Math.Max(dx, dy) + 1;
            if (steps <= 1) { PutPixel(x0, y0, d0, r, g, b, w, h, buf, dbuf); return; }
            float dStep = (d1 - d0) / steps;
            float d = d0;
            while (true)
            {
                PutPixel(x0, y0, d, r, g, b, w, h, buf, dbuf);
                if (x0 == x1 && y0 == y1) break;
                int e2 = 2 * err;
                if (e2 > -dy) { err -= dy; x0 += sx; }
                if (e2 < dx) { err += dx; y0 += sy; }
                d += dStep;
            }
        }

        // ================= 地面网格（Blender 式参考平面，模型底面处） =================
        private void RenderFloorGrid(float y0, Matrix4x4 view, Matrix4x4 proj, int w, int h,
            float panX, float panY, float zoom, byte[] buf, float[] dbuf, Vector4? backgroundColor)
        {
            // 线色按背景明暗自适应：亮底深灰线、暗底浅灰线（两种主题下都清晰）
            float lum = 0.75f;
            if (backgroundColor.HasValue)
                lum = backgroundColor.Value.X * 0.299f + backgroundColor.Value.Y * 0.587f + backgroundColor.Value.Z * 0.114f;
            var col = lum > 0.5f ? new Vector4(0.32f, 0.32f, 0.32f, 1f) : new Vector4(0.72f, 0.72f, 0.72f, 1f);
            byte r = (byte)(col.X * 255f), g = (byte)(col.Y * 255f), b = (byte)(col.Z * 255f);

            // 网格范围 ±2（模型半宽约 0.8），间距 0.4：每方向 11 条线（含中轴）
            const float half = 2f, step = 0.4f;
            var n = (int)(half * 2f / step);
            for (var i = 0; i <= n; i++)
            {
                var t = -half + i * step;
                DrawFloorLine(new Vector3(t, y0, -half), new Vector3(t, y0, half),
                    view, proj, w, h, panX, panY, zoom, buf, dbuf, r, g, b);
                DrawFloorLine(new Vector3(-half, y0, t), new Vector3(half, y0, t),
                    view, proj, w, h, panX, panY, zoom, buf, dbuf, r, g, b);
            }
        }

        private void DrawFloorLine(Vector3 a, Vector3 b, Matrix4x4 view, Matrix4x4 proj, int w, int h,
            float panX, float panY, float zoom, byte[] buf, float[] dbuf, byte r, byte g, byte bl)
        {
            if (!ProjectToScreen(a, view, proj, w, h, panX, panY, zoom, out var x0, out var y0, out var d0)) return;
            if (!ProjectToScreen(b, view, proj, w, h, panX, panY, zoom, out var x1, out var y1, out var d1)) return;
            DrawBresenhamLine((int)Math.Round(x0), (int)Math.Round(y0), d0,
                (int)Math.Round(x1), (int)Math.Round(y1), d1, r, g, bl, w, h, buf, dbuf);
        }

        // ================= 工具 =================
        private static bool ProjectToScreen(Vector3 world, Matrix4x4 view, Matrix4x4 proj, int w, int h,
            float panX, float panY, float zoom,
            out float sx, out float sy, out float depth)
        {
            sx = 0; sy = 0; depth = 1f;
            var pv = Vector4.Transform(new Vector4(world, 1f), view);
            if (pv.Z > -0.1f || pv.W <= 0f) return false; // 在近平面之后
            var pc = Vector4.Transform(pv, proj);
            if (pc.W <= 1e-12f) return false;
            var inv = 1f / pc.W;
            float nx = pc.X * inv, ny = pc.Y * inv, nz = pc.Z * inv;
            // NaN/Inf 防护：坏顶点直接剔除，避免光栅化时产生黑/白噪点
            if (float.IsNaN(nx) || float.IsNaN(ny) || float.IsNaN(nz) ||
                float.IsInfinity(nx) || float.IsInfinity(ny) || float.IsInfinity(nz))
                return false;
            var cx = w * 0.5f;
            var cy = h * 0.5f;
            sx = cx + ((nx * 0.5f + 0.5f) * w - cx) * zoom + panX;
            sy = cy + ((0.5f - ny * 0.5f) * h - cy) * zoom + panY;
            depth = nz * 0.5f + 0.5f;
            return true;
        }

        private static void RasterizeTriangle(
            float x0, float y0, float z0,
            float x1, float y1, float z1,
            float x2, float y2, float z2,
            float r0, float g0, float b0,
            float r1, float g1, float b1,
            float r2, float g2, float b2,
            float int0, float int1, float int2,
            int w, int h, byte[] buf, float[] dbuf,
            float alpha = 1f, bool writeDepth = true)
        {
            // NaN/Inf 防护：异常三角形直接丢弃，杜绝噪点与撕裂
            if (float.IsNaN(x0) || float.IsNaN(y0) || float.IsNaN(z0) ||
                float.IsNaN(x1) || float.IsNaN(y1) || float.IsNaN(z1) ||
                float.IsNaN(x2) || float.IsNaN(y2) || float.IsNaN(z2) ||
                float.IsInfinity(x0) || float.IsInfinity(y0) || float.IsInfinity(z0) ||
                float.IsInfinity(x1) || float.IsInfinity(y1) || float.IsInfinity(z1) ||
                float.IsInfinity(x2) || float.IsInfinity(y2) || float.IsInfinity(z2))
                return;

            // 边界框
            var minX = (int)Math.Floor(Math.Min(x0, Math.Min(x1, x2))) - 1;
            var maxX = (int)Math.Ceiling(Math.Max(x0, Math.Max(x1, x2))) + 1;
            var minY = (int)Math.Floor(Math.Min(y0, Math.Min(y1, y2))) - 1;
            var maxY = (int)Math.Ceiling(Math.Max(y0, Math.Max(y1, y2))) + 1;
            // 极端坐标兜底（防御性，正常不触发）
            if (minX < int.MinValue + 2) minX = 0; if (minY < int.MinValue + 2) minY = 0;
            if (maxX > int.MaxValue - 2) maxX = w - 1; if (maxY > int.MaxValue - 2) maxY = h - 1;
            if (minX < 0) minX = 0; if (minY < 0) minY = 0;
            if (maxX > w - 1) maxX = w - 1; if (maxY > h - 1) maxY = h - 1;

            var e0a = y1 - y2; var e0b = x2 - x1;
            var e1a = y2 - y0; var e1b = x0 - x2;
            var e2a = y0 - y1; var e2b = x1 - x0;
            var area2 = (x0 * (y1 - y2) + x1 * (y2 - y0) + x2 * (y0 - y1));
            // 屏幕空间退化阈值同样取极小值：过大（1e-6 → 边长约 1e-3px）会把多盘同屏时
            // 被缩得很小的细密网格剔除掉，表面出现孔洞/碎片感
            if (Math.Abs(area2) < 1e-9f) return;
            var inv = 1f / area2;

            for (var y = minY; y <= maxY; y++)
            {
                var yy = y + 0.5f;
                var ptr = y * w;
                for (var x = minX; x <= maxX; x++)
                {
                    var xx = x + 0.5f;
                    var e0 = e0a * xx + e0b * yy + (x1 * y2 - x2 * y1);
                    var e1 = e1a * xx + e1b * yy + (x2 * y0 - x0 * y2);
                    var e2 = e2a * xx + e2b * yy + (x0 * y1 - x1 * y0);
                    // 同时支持两种绕序：三边同非负 或 同非正。
                    bool inside = (e0 >= 0f && e1 >= 0f && e2 >= 0f)
                               || (e0 <= 0f && e1 <= 0f && e2 <= 0f);
                    if (!inside) continue;

                    var z = (e0 * z0 + e1 * z1 + e2 * z2) * inv;
                    // Gouraud：按重心坐标插值三顶点颜色与强度，得到本像素颜色与光强，再相乘
                    var cr = (e0 * r0 + e1 * r1 + e2 * r2) * inv;
                    var cg = (e0 * g0 + e1 * g1 + e2 * g2) * inv;
                    var cb = (e0 * b0 + e1 * b1 + e2 * b2) * inv;
                    var it = (e0 * int0 + e1 * int1 + e2 * int2) * inv;
                    PutPixelBlend(x, y, z, (byte)Clamp(cr * it * 255f, 0f, 255f),
                        (byte)Clamp(cg * it * 255f, 0f, 255f),
                        (byte)Clamp(cb * it * 255f, 0f, 255f),
                        alpha, writeDepth, w, h, buf, dbuf);
                }
            }
        }

        /// <summary>把整帧填充为指定颜色（BGRA 布局），保留 alpha（W 为透明通道：0=全透明，1=不透明）。
        /// 背景色带 alpha 时，该帧可实现透明背景（如保存透明底缩略图）。</summary>
        private static void FillBackground(byte[] buf, Vector4 c)
        {
            var fr = (byte)Clamp(c.X * 255f, 0f, 255f);
            var fg = (byte)Clamp(c.Y * 255f, 0f, 255f);
            var fb = (byte)Clamp(c.Z * 255f, 0f, 255f);
            var fa = (byte)Clamp(c.W * 255f, 0f, 255f);
            for (var i = 0; i < buf.Length; i += 4)
            {
                buf[i] = fb;
                buf[i + 1] = fg;
                buf[i + 2] = fr;
                buf[i + 3] = fa;
            }
        }

        private static void PutPixel(int x, int y, float z, byte r, byte g, byte b,
            int w, int h, byte[] buf, float[] dbuf)
        {
            if (x < 0 || y < 0 || x >= w || y >= h) return;
            if (float.IsNaN(z) || float.IsInfinity(z)) return; // 深度异常直接丢弃
            var idxBuf = y * w + x;
            if (z >= dbuf[idxBuf]) return;
            dbuf[idxBuf] = z;
            var p = idxBuf * 4;
            // 有序抖动（Bayer 4x4）：把 8-bit 量化色带打散成亚阈值图案，视觉更平滑
            int d = Bayer4[(y & 3) * 4 + (x & 3)] - 8; // -8..+7
            buf[p] = (byte)Clamp(b + d, 0, 255);
            buf[p + 1] = (byte)Clamp(g + d, 0, 255);
            buf[p + 2] = (byte)Clamp(r + d, 0, 255);
            buf[p + 3] = 255; // alpha 不透明
        }

        /// <summary>
        /// 带 alpha 的像素写入（三角形路径用）：
        /// 实体（alpha=1、writeDepth=true）= 标准深度测试+写入；
        /// 负面（alpha&lt;1、writeDepth=false）= 只测深度不写深度，与已写入像素（实体/背景）按 alpha 混合，
        /// 半透明叠在实体之上（与 GPU 后端一致）。</summary>
        private static void PutPixelBlend(int x, int y, float z, byte r, byte g, byte b,
            float alpha, bool writeDepth, int w, int h, byte[] buf, float[] dbuf)
        {
            if (x < 0 || y < 0 || x >= w || y >= h) return;
            if (float.IsNaN(z) || float.IsInfinity(z)) return; // 深度异常直接丢弃
            var idxBuf = y * w + x;
            if (z >= dbuf[idxBuf]) return; // 深度测试：负面也测试（实体先画的在后），但不写深度
            if (writeDepth) dbuf[idxBuf] = z;
            var p = idxBuf * 4;
            // 有序抖动（与 PutPixel 一致）：把 8-bit 量化色带打散成亚阈值图案，视觉更平滑
            int d = (Bayer4[(y & 3) * 4 + (x & 3)] - 8) * DitherAmp / 8;
            var fr = (byte)Clamp(r + d, 0, 255);
            var fg = (byte)Clamp(g + d, 0, 255);
            var fb = (byte)Clamp(b + d, 0, 255);
            if (alpha >= 0.999f)
            {
                buf[p] = fb;
                buf[p + 1] = fg;
                buf[p + 2] = fr;
                buf[p + 3] = 255;
            }
            else
            {
                var a = alpha;
                var ia = 1f - a;
                buf[p] = (byte)Clamp(fb * a + buf[p] * ia, 0, 255);
                buf[p + 1] = (byte)Clamp(fg * a + buf[p + 1] * ia, 0, 255);
                buf[p + 2] = (byte)Clamp(fr * a + buf[p + 2] * ia, 0, 255);
                buf[p + 3] = 255; // 最终帧不透明，透明体现在与背景/实体的混合上
            }
        }

        // Bayer 4x4 有序抖动矩阵
        private static readonly byte[] Bayer4 =
        {
            0,  8,  2, 10,
            12, 4, 14,  6,
            3, 11,  1,  9,
            15, 7, 13,  5
        };
    }
}