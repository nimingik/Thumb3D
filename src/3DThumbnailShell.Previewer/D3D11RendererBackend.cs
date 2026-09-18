using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using _3DThumbnailShell.Core.Renderer;

namespace _3DThumbnailShell.Previewer
{
    /// <summary>
    /// DX11 GPU 渲染后端（Vortice 3.8.3 原生 API）。
    /// - 离屏 B8G8R8A8 渲染目标 + 深度缓冲，直接读回 BGRA 帧（与 CPU 渲染器输出一致）
    /// - 相机数学与 SoftwareRenderer 完全一致（Spherical + CreateLookAt + Perspective FOV π/3.6）
    /// - 光照在 CPU 预计算成顶点色（面法线 + Lighting 共享常量），shader 仅做 MVP 变换与透传
    /// - 顶点/索引缓冲按 (mesh, maxTriangles) 缓存，LOD 用等距 stride 抽稀（与 CPU 一致）
    /// - 设备创建失败（无 GPU/驱动异常）时 IsAvailable=false，由上层回退 CPU
    /// </summary>
    public sealed class D3D11RendererBackend : IRenderBackend
    {
        private const float FovY = (float)(Math.PI / 3.6);

        // 顶点布局：POSITION 12B + COLOR(RGBA float) 16B(未乘光的顶点点色，W=alpha) + INTENSITY 4B
        // Pack=1 保证紧凑无填充（stride=32）
        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        private struct GpuVertex
        {
            public Vector3 Position;
            public Vector4 Color;   // RGB=顶点色(未乘光)，W=alpha（负零件 0.25，实体 1）
            public float Intensity; // 该顶点的光照强度（CPU 预计算，与 CPU 渲染器一致）
        }

        private ID3D11Device _device;
        private ID3D11DeviceContext _context;
        private ID3D11VertexShader _vs;
        private ID3D11PixelShader _ps;
        private ID3D11InputLayout _layout;
        private ID3D11Buffer _cb;
        private ID3D11RasterizerState _rasterizer;
        private ID3D11BlendState _blend;   // 负零件 alpha 混合（SrcAlpha/InvSrcAlpha）
        private ID3D11DepthStencilState _dsLine; // 地面网格线：深度测试开、深度写入关
        private ID3D11Buffer _vbGrid;            // 地面网格线动态顶点缓冲（LineList，44 顶点）
        private float _gridBaseY = float.NaN;    // 归一化后模型底面 y（网格重建时更新）
        private readonly bool _available;
        private string _adapterName; // 实际创建设备所用适配器名（供状态栏显示/核验 GPU 选择）

        // 网格缓冲缓存（mesh + maxTriangles + 颜色/阴影变化时重建）
        // 拆三批：不透明实体（_Pos）、负零件（_Neg，alpha 混合）、修改器（_Mod，alpha 混合，颜色独立）
        private MeshData _cachedMesh;
        private int _cachedMaxTriangles = -1;
        private Vector4? _cachedOverride;
        private float _cachedShadow = -1f;
        private float _cachedSmooth = -1f;
        private bool _cachedShowNegative = true;
        private Vector4? _cachedNegOverride;
        private float _cachedNegOpacity = -1f;
        private float _cachedIllumination = -1f;   // 照明（补光）强度缓存键
        private bool _cachedShowModifier = true;
        private Vector4? _cachedModOverride;
        private float _cachedModOpacity = -1f;
        private ID3D11Buffer _vbPos;
        private ID3D11Buffer _ibPos;
        private int _vbPosCount;          // 不透明批顶点数
        private int _ibPosCount;          // 不透明批索引数
        private ID3D11Buffer _vbNeg;
        private ID3D11Buffer _ibNeg;
        private int _vbNegCount;          // 负零件批顶点数
        private int _ibNegCount;          // 负零件批索引数
        private ID3D11Buffer _vbMod;
        private ID3D11Buffer _ibMod;
        private int _vbModCount;          // 修改器批顶点数
        private int _ibModCount;          // 修改器批索引数
        private bool _vbIsPointCloud;

        // 帧缓冲缓存（尺寸变化时重建）
        private int _rtW, _rtH;
        private ID3D11Texture2D _rtTex;
        private ID3D11RenderTargetView _rtv;
        private ID3D11Texture2D _dsTex;
        private ID3D11DepthStencilView _dsv;
        private ID3D11Texture2D _stageTex;

        private const string ShaderSource = @"
cbuffer Constants : register(b0)
{
    float4x4 mvp;
};

struct VSIn
{
    float3 pos : POSITION;
    float4 col : COLOR;      // rgb 顶点色(未乘光)，a 透明
    float inten : COLOR1;    // 该顶点的光照强度
};

struct VSOut
{
    float4 pos : SV_Position;
    float4 col : COLOR;
    float inten : COLOR1;
};

VSOut VSMain(VSIn i)
{
    VSOut o;
    o.pos = mul(float4(i.pos, 1.0), mvp);
    o.col = i.col;
    o.inten = i.inten;
    return o;
}

float4 PSMain(VSOut i) : SV_Target
{
    // CPU 渲染器：像素色 = 插值顶点色 * 插值光强（两后端观感一致）
    return float4(i.col.rgb * i.inten, i.col.a);
}
";

        public D3D11RendererBackend(string preferredAdapterName = null)
        {
            var ok = false;
            try
            {
                var flags = DeviceCreationFlags.BgraSupport;
                // 指定 GPU → 硬件；失败 → WARP 回退
                try
                {
                    CreateDevice(preferredAdapterName, flags);
                }
                catch
                {
                    CreateDevice(null, flags, warp: true);
                }

                var vsBytes = Compile(ShaderSource, "VSMain", "vs_5_0");
                var psBytes = Compile(ShaderSource, "PSMain", "ps_5_0");
                _vs = _device.CreateVertexShader(vsBytes, null);
                _ps = _device.CreatePixelShader(psBytes, null);

                _layout = _device.CreateInputLayout(new[]
                {
                    new InputElementDescription("POSITION", 0, Format.R32G32B32_Float, 0, 0),
                    new InputElementDescription("COLOR", 0, Format.R32G32B32A32_Float, 12, 0),
                    // HLSL 里 inten 声明为 : COLOR1（=语义 COLOR，索引 1）。InputLayout 必须用相同的
                    // (语义, 索引) 组合，否则 CreateInputLayout 返回 E_INVALIDARG（或驱动错配导致顶点散裂）。
                    // 旧代码写成 ("COLOR1", 0) => 语义 COLOR 索引 0，与 shader 的索引 1 不匹配 ← 回归根因。
                    new InputElementDescription("COLOR", 1, Format.R32_Float, 28, 0)
                }, vsBytes);

                _cb = _device.CreateBuffer(
                    new BufferDescription(64, BindFlags.ConstantBuffer,
                        ResourceUsage.Dynamic, CpuAccessFlags.Write,
                        ResourceOptionFlags.None, 0));
                _rasterizer = _device.CreateRasterizerState(RasterizerDescription.CullNone);
                // 负零件 alpha 混合：SrcAlpha / InvSrcAlpha（预览器输出已按 B8G8R8A8）
                var bd = new BlendDescription(
                    Blend.SourceAlpha, Blend.InverseSourceAlpha,
                    Blend.SourceAlpha, Blend.InverseSourceAlpha);
                bd.RenderTarget[0].BlendEnable = true;
                bd.RenderTarget[0].RenderTargetWriteMask = ColorWriteEnable.All;
                _blend = _device.CreateBlendState(bd);
                // 地面网格线：深度测试开（被模型遮挡）、深度写入关（不影响后续绘制）
                _dsLine = _device.CreateDepthStencilState(new DepthStencilDescription
                {
                    DepthEnable = true,
                    DepthWriteMask = DepthWriteMask.Zero,
                    DepthFunc = ComparisonFunction.Less
                });
                _vbGrid = _device.CreateBuffer(
                    new BufferDescription(32 * 44, BindFlags.VertexBuffer,
                        ResourceUsage.Dynamic, CpuAccessFlags.Write,
                        ResourceOptionFlags.None, 0));
                ok = true;
            }
            catch
            {
                // 设备/着色器创建失败：不可用，上层回退 CPU
                Dispose();
            }
            _available = ok;
        }

        /// <summary>
        /// 创建设备。adapterName 为空 → 默认适配器（第一块）；非空 → 按名称枚举 DXGI 找匹配的硬件适配器；
        /// warp=true → WARP 软件渲染（忽略 adapterName）。
        /// </summary>
        private void CreateDevice(string adapterName, DeviceCreationFlags flags, bool warp = false)
        {
            IDXGIAdapter1 selected = null;
            var driverType = DriverType.Hardware;
            var usedName = "";
            try
            {
                if (warp)
                {
                    driverType = DriverType.Warp;
                    usedName = "WARP (CPU)";
                }
                else if (!string.IsNullOrEmpty(adapterName))
                {
                    using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
                    for (uint idx = 0; ; idx++)
                    {
                        var res = factory.EnumAdapters1(idx, out var adapter);
                        if (res.Failure || adapter == null) break;
                        var desc = adapter.Description1;
                        if ((desc.Flags & AdapterFlags.Software) == 0
                            && string.Equals(desc.Description.Trim(), adapterName, StringComparison.OrdinalIgnoreCase))
                        {
                            selected = adapter; // 接管引用，创建设备后释放（设备会持有适配器引用）
                            driverType = DriverType.Unknown;
                            usedName = desc.Description.Trim();
                            break;
                        }
                        adapter.Dispose();
                    }
                    if (selected == null) usedName = "默认适配器"; // 未匹配到指定名 → 回退默认硬件
                }
                else
                {
                    usedName = FirstHardwareAdapterName(); // 默认/自动 → 读第一块硬件适配器名
                    usedName = string.IsNullOrEmpty(usedName) ? "默认适配器" : usedName;
                }

                var adapterPtr = selected != null ? selected.NativePointer : IntPtr.Zero;
                var res2 = D3D11.D3D11CreateDevice(adapterPtr, driverType, flags, null,
                    out var device, out _, out var context);
                if (res2.Failure || device == null)
                    throw new InvalidOperationException("D3D11CreateDevice(" + driverType + ") 失败: " + res2);
                _device = device;
                _context = context;
                _adapterName = usedName;
            }
            finally
            {
                selected?.Dispose();
            }
        }

        /// <summary>枚举第一块非软件硬件适配器名（默认/自动路径用于显示实际生效的 GPU）。</summary>
        private static string FirstHardwareAdapterName()
        {
            try
            {
                using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
                for (uint idx = 0; ; idx++)
                {
                    var res = factory.EnumAdapters1(idx, out var adapter);
                    if (res.Failure || adapter == null) break;
                    using (adapter)
                    {
                        var desc = adapter.Description1;
                        if ((desc.Flags & AdapterFlags.Software) == 0)
                            return desc.Description.Trim();
                    }
                }
            }
            catch { }
            return null;
        }

        public bool IsAvailable => _available;

        /// <summary>实际创建设备所用适配器名（如 "NVIDIA GeForce RTX 3060" / "WARP (CPU)"）。</summary>
        public string AdapterName => _adapterName;

        public RenderResult Render(MeshData mesh, int width, int height, in CameraParams cam,
            int maxTriangles, Vector4? overrideColor, float shadowLevel, float smoothShading,
            bool showNegative, Vector4? negativeColor, float negativeOpacity,
            Vector4? backgroundColor, bool showFloorGrid, float illumination,
            bool showModifier, Vector4? modifierColor, float modifierOpacity)
        {
            if (!_available || mesh == null || mesh.Vertices == null || mesh.Vertices.Count == 0
                || width <= 0 || height <= 0)
                return null;

            try
            {
                // 1. 网格 → GPU 缓冲（含归一化 + 顶点色光照 + LOD 抽稀 + 实体/负零件/修改器拆分）
                EnsureMeshBuffers(mesh, maxTriangles, overrideColor, shadowLevel, smoothShading,
                    showNegative, negativeColor, negativeOpacity, illumination,
                    showModifier, modifierColor, modifierOpacity);
                EnsureFrameBuffers(width, height);

                // 2. 相机（与 CPU 渲染器数学一致）
                var eye = Spherical(cam.Dist, cam.Az, cam.El);
                var view = Matrix4x4.CreateLookAt(eye, Vector3.Zero, Vector3.UnitY);
                var proj = Matrix4x4.CreatePerspectiveFieldOfView(FovY, (float)width / height, 0.1f, 20f);
                // 行向量约定：CPU 渲染器算 v*view*proj；转置上传 + HLSL mul(v,m) 恰好抵消，
                // 因此 mvp 必须是 view*proj（写成 proj*view 会导致全白/裁切）
                var mvp = view * proj;
                // 屏幕空间缩放/平移（滚轮 zoom + 右键/中键 pan），与 CPU 渲染器
                // ProjectToScreen 一致：围绕画面中心缩放，再加像素平移。
                // CPU: sy = cy + ((0.5 - ny*0.5)*h - cy)*zoom + panY → NDC y' = ny*zoom - 2*panY/h
                // （panY 增加 → sy 增加 → 模型下移；GPU NDC y 向上为正，故平移项取负号）
                var post = new Matrix4x4(
                    cam.Zoom, 0f, 0f, 0f,
                    0f, cam.Zoom, 0f, 0f,
                    0f, 0f, 1f, 0f,
                    2f * cam.PanX / width, -2f * cam.PanY / height, 0f, 1f);
                mvp = mvp * post;
                // Matrix4x4 行主序 → 转置为列主序上传（HLSL mul(v, m)）
                var upload = Matrix4x4.Transpose(mvp);

                // 清屏：背景色（backgroundColor 非 null 填充不透明色；null=透明黑，供显示层合成背景图片）
                // 与 CPU 渲染器（buf 填充/全零）一致，保证两后端观感统一
                var bg = backgroundColor ?? new Vector4(0f, 0f, 0f, 0f);
                _context.ClearRenderTargetView(_rtv, new Color4(bg.X, bg.Y, bg.Z, bg.W));
                _context.ClearDepthStencilView(_dsv, DepthStencilClearFlags.Depth, 1f, 0);
                _context.OMSetRenderTargets(1, new[] { _rtv }, _dsv);
                var vp = new Viewport(0, 0, width, height, 0f, 1f);
                _context.RSSetViewports(new[] { vp });
                _context.RSSetState(_rasterizer);
                _context.IASetInputLayout(_layout);
                _context.VSSetShader(_vs, null, 0);
                _context.PSSetShader(_ps, null, 0);
                _context.VSSetConstantBuffers(0, 1, new[] { _cb });

                var mapped = _context.Map(_cb, MapMode.WriteDiscard, Vortice.Direct3D11.MapFlags.None);
                Marshal.StructureToPtr(upload, mapped.DataPointer, false);
                _context.Unmap(_cb, 0);

                if (_vbIsPointCloud)
                {
                    _context.IASetPrimitiveTopology(PrimitiveTopology.PointList);
                    _context.IASetVertexBuffers(0, 1, new[] { _vbPos }, new uint[] { 32 }, new uint[] { 0 });
                    _context.Draw((uint)_vbPosCount, 0);
                }
                else
                {
                    // 不透明实体批（null = 默认无混合）
                    _context.OMSetBlendState(null, new Color4(0f, 0f, 0f, 0f), 0xFFFFFFFF);
                    if (_ibPosCount > 0)
                    {
                        _context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
                        _context.IASetVertexBuffers(0, 1, new[] { _vbPos }, new uint[] { 32 }, new uint[] { 0 });
                        _context.IASetIndexBuffer(_ibPos, Format.R32_UInt, 0);
                        _context.DrawIndexed((uint)_ibPosCount, 0, 0);
                    }
                    // 负零件批：alpha 混合（后绘制以叠加到实体上，可透见内部）
                    if (_ibNegCount > 0)
                    {
                        _context.OMSetBlendState(_blend, new Color4(0f, 0f, 0f, 0f), 0xFFFFFFFF);
                        _context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
                        _context.IASetVertexBuffers(0, 1, new[] { _vbNeg }, new uint[] { 32 }, new uint[] { 0 });
                        _context.IASetIndexBuffer(_ibNeg, Format.R32_UInt, 0);
                        _context.DrawIndexed((uint)_ibNegCount, 0, 0);
                        _context.OMSetBlendState(null, new Color4(0f, 0f, 0f, 0f), 0xFFFFFFFF);
                    }
                    // 修改器批：同负零件叠加层（alpha 混合、颜色/不透明度独立可调）
                    if (_ibModCount > 0)
                    {
                        _context.OMSetBlendState(_blend, new Color4(0f, 0f, 0f, 0f), 0xFFFFFFFF);
                        _context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
                        _context.IASetVertexBuffers(0, 1, new[] { _vbMod }, new uint[] { 32 }, new uint[] { 0 });
                        _context.IASetIndexBuffer(_ibMod, Format.R32_UInt, 0);
                        _context.DrawIndexed((uint)_ibModCount, 0, 0);
                        _context.OMSetBlendState(null, new Color4(0f, 0f, 0f, 0f), 0xFFFFFFFF);
                    }
                }

                // 地面网格：模型之后绘制，深度测试保证被模型遮挡、不写深度（与 CPU 观感一致）
                if (showFloorGrid && !mesh.IsLineCloud && !float.IsNaN(_gridBaseY))
                    DrawFloorGrid(backgroundColor);

                // 3. 读回 BGRA
                _context.CopySubresourceRegion(_stageTex, 0, 0, 0, 0, _rtTex, 0, null);
                return ReadBack(width, height);
            }
            catch
            {
                return null;
            }
        }

        // ================= 着色器编译 =================
        // Vortice.D3DCompiler 3.8.3 的 net8.0 DLL 实际不含 Compiler 类（XML 文档过期），
        // 直接 P/Invoke Windows 自带的 d3dcompiler_47.dll，稳定且无额外依赖。

        [DllImport("d3dcompiler_47.dll", CharSet = CharSet.Ansi, CallingConvention = CallingConvention.Winapi)]
        private static extern int D3DCompile(
            [In] byte[] pSrcData, IntPtr srcDataSize, string pSourceName,
            IntPtr pDefines, IntPtr pInclude, string pEntrypoint, string pTarget,
            uint flags1, uint flags2, out IntPtr ppCode, out IntPtr ppErrorMsgs);

        /// <summary>ID3D10Blob：IUnknown 后紧跟 GetBufferPointer() / GetBufferSize()。</summary>
        [ComImport, Guid("8BA5FB08-5195-40e2-AC58-0D989C3A0102"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface ID3D10Blob
        {
            [PreserveSig] IntPtr GetBufferPointer();
            [PreserveSig] IntPtr GetBufferSize();
        }

        private static byte[] Compile(string source, string entry, string profile)
        {
            var srcBytes = Encoding.UTF8.GetBytes(source);
            IntPtr codePtr, errPtr;
            var hr = D3DCompile(srcBytes, (IntPtr)srcBytes.Length, "preview.hlsl",
                IntPtr.Zero, IntPtr.Zero, entry, profile, 0, 0, out codePtr, out errPtr);
            if (hr < 0 || codePtr == IntPtr.Zero)
            {
                var msg = errPtr != IntPtr.Zero ? ReadBlobText(errPtr) : "HRESULT 0x" + hr.ToString("X8");
                throw new InvalidOperationException("HLSL 编译失败: " + msg);
            }
            return ReadBlobBytes(codePtr);
        }

        private static byte[] ReadBlobBytes(IntPtr blob)
        {
            var b = (ID3D10Blob)Marshal.GetObjectForIUnknown(blob);
            try
            {
                var size = (int)b.GetBufferSize();
                var buf = new byte[size];
                Marshal.Copy(b.GetBufferPointer(), buf, 0, size);
                return buf;
            }
            finally
            {
                Marshal.Release(blob); // 释放 D3DCompile 的 out 引用
                Marshal.FinalReleaseComObject(b); // 释放 RCW 的 AddRef
            }
        }

        private static string ReadBlobText(IntPtr blob)
        {
            var bytes = ReadBlobBytes(blob);
            var end = Array.IndexOf(bytes, (byte)0);
            if (end >= 0) Array.Resize(ref bytes, end);
            return Encoding.UTF8.GetString(bytes);
        }

        // ================= 网格缓冲 =================

        private void EnsureMeshBuffers(MeshData mesh, int maxTriangles, Vector4? overrideColor, float shadowLevel,
            float smoothShading, bool showNegative, Vector4? negativeColor, float negativeOpacity,
            float illumination, bool showModifier, Vector4? modifierColor, float modifierOpacity)
        {
            // 缓存键：网格 + LOD + 自定义颜色 + 阴影强度 + 平滑着色 + 负零件/修改器设置 + 照明（任一变化都重建）
            if (_cachedMesh == mesh && _cachedMaxTriangles == maxTriangles
                && _cachedOverride == overrideColor
                && Math.Abs(_cachedShadow - shadowLevel) < 1e-4f
                && Math.Abs(_cachedSmooth - smoothShading) < 1e-4f
                && _cachedShowNegative == showNegative
                && _cachedNegOverride == negativeColor
                && Math.Abs(_cachedNegOpacity - negativeOpacity) < 1e-4f
                && Math.Abs(_cachedIllumination - illumination) < 1e-4f
                && _cachedShowModifier == showModifier
                && _cachedModOverride == modifierColor
                && Math.Abs(_cachedModOpacity - modifierOpacity) < 1e-4f) return;
            ReleaseMeshBuffers();
            _cachedMesh = mesh;
            _cachedMaxTriangles = maxTriangles;
            _cachedOverride = overrideColor;
            _cachedShadow = shadowLevel;
            _cachedSmooth = smoothShading;
            _cachedShowNegative = showNegative;
            _cachedNegOverride = negativeColor;
            _cachedNegOpacity = negativeOpacity;
            _cachedIllumination = illumination;
            _cachedShowModifier = showModifier;
            _cachedModOverride = modifierColor;
            _cachedModOpacity = modifierOpacity;

            if (mesh.IsPointCloud)
            {
                BuildPointCloud(mesh, overrideColor, illumination);
                return;
            }

            // 归一化（与 SoftwareRenderer.ComputeNormalizationScale 一致）
            var scale = ComputeNormalizationScale(mesh.Vertices, out var center, out var vmin);
            _gridBaseY = (vmin.Y - center.Y) * scale - 0.005f; // 模型底面（网格地面），微降防共面闪烁
            var vrts = mesh.Vertices;
            var idxs = mesh.Indices;
            var nFaces = idxs.Count / 3;
            if (nFaces == 0)
            {
                BuildPointCloud(mesh, overrideColor, illumination);
                return;
            }

            // LOD 抽稀（与 CPU 一致：等距 stride）
            var stride = 1;
            if (maxTriangles > 0 && nFaces > maxTriangles)
                stride = (int)Math.Ceiling((double)nFaces / maxTriangles);
            var nDraw = (int)Math.Ceiling((double)nFaces / stride);

            // Gouraud 平滑法线：给三顶点各自动量强度，GPU 像素插值产生连续明暗（与 CPU 一致淡化"破面"）
            var smoothNormals = Lighting.SmoothVertexNormals(mesh, scale, center);

            // 逐顶点平滑色：邻接上色面 RGB 平均到顶点（与 CPU 渲染器完全一致），平滑多色模型的马赛克
            var vcol = Lighting.SmoothVertexColors(mesh);

            var vertsPos = new List<GpuVertex>(nDraw * 3);
            var indicesPos = new List<int>(nDraw * 3);
            var vertsNeg = new List<GpuVertex>();
            var indicesNeg = new List<int>();
            var vertsMod = new List<GpuVertex>();
            var indicesMod = new List<int>();
            var k = 0;
            for (var f = 0; f < nFaces; f += stride, k++)
            {
                int i0 = idxs[f * 3], i1 = idxs[f * 3 + 1], i2 = idxs[f * 3 + 2];
                var vm0 = (vrts[i0] - center) * scale;
                var vm1 = (vrts[i1] - center) * scale;
                var vm2 = (vrts[i2] - center) * scale;
                var nrmRaw = Vector3.Cross(vm1 - vm0, vm2 - vm0);
                // 退化/NaN 面剔除：阈值取归一化空间下的极限小值（1e-20）。
                // 注意不能用 1e-12：叉积平方 ∝ (边长·scale)^4，合并盘（多盘同屏）归一化尺度最小，
                // 细密网格（多色模型 paint_color 展开的小三角形）会被误判为退化面整片剔除 →
                // 表面出现密集孔洞（"碎片/沙粒"观感）。1e-20 仅滤掉真正的数值噪声。
                if (!(nrmRaw.LengthSquared() > 1e-20f)) { k--; continue; }

                var nrm = Vector3.Normalize(nrmRaw);
                // Gouraud：逐顶点平滑法线强度；snorm 不可用时回退 flat（VertexIntensity 内部处理）。
                // 平滑着色程度 t：顶点强度 = lerp(面平光强度, Gouraud 顶点强度, t)，与 CPU 渲染器一致。
                float intFlat = Lighting.ComputeIntensity(nrm, shadowLevel, illumination);
                float i0v, i1v, i2v;
                if (smoothNormals != null && i0 < smoothNormals.Length && i1 < smoothNormals.Length && i2 < smoothNormals.Length)
                {
                    i0v = Lerp(intFlat, Lighting.VertexIntensity(smoothNormals[i0], nrm, shadowLevel, illumination), smoothShading);
                    i1v = Lerp(intFlat, Lighting.VertexIntensity(smoothNormals[i1], nrm, shadowLevel, illumination), smoothShading);
                    i2v = Lerp(intFlat, Lighting.VertexIntensity(smoothNormals[i2], nrm, shadowLevel, illumination), smoothShading);
                }
                else
                {
                    i0v = i1v = i2v = intFlat;
                }
                // 取色优先级：拓竹多色逐面颜色(平滑为逐顶点色) > 自定义颜色(仅未上色顶点) > 顶点色/默认灰
                // 与 CPU 渲染器一致；overrideColor 只作用于未上色顶点，绝不覆盖上色面的颜色。
                // 平滑着色程度 t：顶点显示色 = lerp(面上色色 Cf, 逐顶点平滑色, t)，与 CPU 渲染器一致。
                Vector4 fallback;
                if (overrideColor.HasValue) fallback = overrideColor.Value;
                else fallback = Lighting.BaseColor(mesh, i0, i1, i2);
                var vc0 = Lighting.VertexColor(vcol, i0, fallback);
                var vc1 = Lighting.VertexColor(vcol, i1, fallback);
                var vc2 = Lighting.VertexColor(vcol, i2, fallback);
                Vector4 cf = fallback;
                if (mesh.FaceColors != null && f < mesh.FaceColors.Count && mesh.FaceColors[f].W > 0f)
                    cf = new Vector4(mesh.FaceColors[f].X, mesh.FaceColors[f].Y, mesh.FaceColors[f].Z, 1f);
                // 颜色平滑与光照平滑解耦：paint_color 多色模型颜色强制全平滑（与 CPU 一致，消除逐面斑点）
                var colorSmooth = Lighting.ColorSmooth(mesh, smoothShading);
                var cv0 = Lerp(cf, vc0, colorSmooth);
                var cv1 = Lerp(cf, vc1, colorSmooth);
                var cv2 = Lerp(cf, vc2, colorSmooth);
                // 部件类型：实体 / 负零件 / 修改器（隐藏的批跳过，不建缓冲也就不绘制）
                var kind = MeshData.PartKindNormal;
                if (mesh.FaceNegative != null && f < mesh.FaceNegative.Count && mesh.FaceNegative[f] != 0)
                    kind = MeshData.PartKindNegative;
                if (mesh.FaceModifier != null && f < mesh.FaceModifier.Count && mesh.FaceModifier[f] != 0)
                    kind = MeshData.PartKindModifier;
                if (kind == MeshData.PartKindNegative && !showNegative) { k--; continue; }
                if (kind == MeshData.PartKindModifier && !showModifier) { k--; continue; }
                // 叠加层（负零件/修改器）颜色 override：统一用设置的颜色（忽略模型自带面/顶点色），光强照常作用
                if (kind == MeshData.PartKindNegative && negativeColor.HasValue)
                {
                    cv0 = negativeColor.Value;
                    cv1 = negativeColor.Value;
                    cv2 = negativeColor.Value;
                }
                else if (kind == MeshData.PartKindModifier && modifierColor.HasValue)
                {
                    cv0 = modifierColor.Value;
                    cv1 = modifierColor.Value;
                    cv2 = modifierColor.Value;
                }
                var alpha = kind == MeshData.PartKindNormal ? 1f
                    : (kind == MeshData.PartKindNegative ? negativeOpacity : modifierOpacity);
                // 照明（补光）：抬升材质基色，保证纯黑/深色模型也能看清轮廓（与 CPU 一致）
                if (illumination > 0f)
                {
                    cv0 = Lighting.Illuminate(cv0, illumination);
                    cv1 = Lighting.Illuminate(cv1, illumination);
                    cv2 = Lighting.Illuminate(cv2, illumination);
                }
                // 顶点色未乘光（光照强度单独放 Intensity，shader 里在像素插值后再乘，与 CPU 逐像素插值一致）
                var col0 = new Vector4(cv0.X, cv0.Y, cv0.Z, alpha);
                var col1 = new Vector4(cv1.X, cv1.Y, cv1.Z, alpha);
                var col2 = new Vector4(cv2.X, cv2.Y, cv2.Z, alpha);

                if (kind == MeshData.PartKindModifier)
                {
                    var b = vertsMod.Count;
                    vertsMod.Add(new GpuVertex { Position = vm0, Color = col0, Intensity = i0v });
                    vertsMod.Add(new GpuVertex { Position = vm1, Color = col1, Intensity = i1v });
                    vertsMod.Add(new GpuVertex { Position = vm2, Color = col2, Intensity = i2v });
                    indicesMod.Add(b); indicesMod.Add(b + 1); indicesMod.Add(b + 2);
                }
                else if (kind == MeshData.PartKindNegative)
                {
                    var b = vertsNeg.Count;
                    vertsNeg.Add(new GpuVertex { Position = vm0, Color = col0, Intensity = i0v });
                    vertsNeg.Add(new GpuVertex { Position = vm1, Color = col1, Intensity = i1v });
                    vertsNeg.Add(new GpuVertex { Position = vm2, Color = col2, Intensity = i2v });
                    indicesNeg.Add(b); indicesNeg.Add(b + 1); indicesNeg.Add(b + 2);
                }
                else
                {
                    var b = vertsPos.Count;
                    vertsPos.Add(new GpuVertex { Position = vm0, Color = col0, Intensity = i0v });
                    vertsPos.Add(new GpuVertex { Position = vm1, Color = col1, Intensity = i1v });
                    vertsPos.Add(new GpuVertex { Position = vm2, Color = col2, Intensity = i2v });
                    indicesPos.Add(b); indicesPos.Add(b + 1); indicesPos.Add(b + 2);
                }
            }

            _vbPosCount = vertsPos.Count;
            _ibPosCount = indicesPos.Count;
            _vbNegCount = vertsNeg.Count;
            _ibNegCount = indicesNeg.Count;
            _vbModCount = vertsMod.Count;
            _ibModCount = indicesMod.Count;
            _vbPos = vertsPos.Count > 0 ? CreateVertexBuffer(vertsPos.ToArray()) : null;
            _ibPos = indicesPos.Count > 0 ? CreateIndexBuffer(indicesPos.ToArray()) : null;
            _vbNeg = vertsNeg.Count > 0 ? CreateVertexBuffer(vertsNeg.ToArray()) : null;
            _ibNeg = indicesNeg.Count > 0 ? CreateIndexBuffer(indicesNeg.ToArray()) : null;
            _vbMod = vertsMod.Count > 0 ? CreateVertexBuffer(vertsMod.ToArray()) : null;
            _ibMod = indicesMod.Count > 0 ? CreateIndexBuffer(indicesMod.ToArray()) : null;
            _vbIsPointCloud = false;
        }

        private void BuildPointCloud(MeshData mesh, Vector4? overrideColor, float illumination)
        {
            var scale = ComputeNormalizationScale(mesh.Vertices, out var center, out var vmin);
            _gridBaseY = (vmin.Y - center.Y) * scale - 0.005f; // 点云底面（网格地面）
            var vrts = mesh.Vertices;
            var verts = new GpuVertex[vrts.Count];
            for (var i = 0; i < vrts.Count; i++)
            {
                var vm = (vrts[i] - center) * scale;
                var col = overrideColor.HasValue
                    ? overrideColor.Value
                    : (mesh.Colors != null && i < mesh.Colors.Count ? mesh.Colors[i] : Lighting.DefaultColor);
                col = Lighting.Illuminate(col, illumination); // 照明：抬升深色点云
                verts[i] = new GpuVertex { Position = vm, Color = new Vector4(col.X, col.Y, col.Z, 1f), Intensity = 1f };
            }
            _vbPosCount = verts.Length;
            _ibPosCount = 0;
            _vbNegCount = 0;
            _ibNegCount = 0;
            _vbPos = CreateVertexBuffer(verts);
            _vbNeg = null;
            _ibPos = null;
            _ibNeg = null;
            _vbIsPointCloud = true;
        }

        private ID3D11Buffer CreateVertexBuffer(GpuVertex[] data)
        {
            // 显式传 size/stride：T[] 重载的 size=0 不保证自动按长度计算
            return _device.CreateBuffer(data, BindFlags.VertexBuffer,
                ResourceUsage.Default, CpuAccessFlags.None, ResourceOptionFlags.None,
                (uint)(data.Length * 32), 32);
        }

        private ID3D11Buffer CreateIndexBuffer(int[] data)
        {
            return _device.CreateBuffer(data, BindFlags.IndexBuffer,
                ResourceUsage.Default, CpuAccessFlags.None, ResourceOptionFlags.None,
                (uint)(data.Length * 4), 4);
        }

        // ================= 帧缓冲 =================

        private void EnsureFrameBuffers(int w, int h)
        {
            if (_rtW == w && _rtH == h && _rtTex != null) return;
            ReleaseFrameBuffers();
            _rtW = w; _rtH = h;

            _rtTex = _device.CreateTexture2D(new Texture2DDescription(
                Format.B8G8R8A8_UNorm, (uint)w, (uint)h, 1, 1,
                BindFlags.RenderTarget, ResourceUsage.Default, CpuAccessFlags.None,
                1, 0, ResourceOptionFlags.None));
            _rtv = _device.CreateRenderTargetView(_rtTex);

            _dsTex = _device.CreateTexture2D(new Texture2DDescription(
                Format.D32_Float, (uint)w, (uint)h, 1, 1,
                BindFlags.DepthStencil, ResourceUsage.Default, CpuAccessFlags.None,
                1, 0, ResourceOptionFlags.None));
            _dsv = _device.CreateDepthStencilView(_dsTex);

            _stageTex = _device.CreateTexture2D(new Texture2DDescription(
                Format.B8G8R8A8_UNorm, (uint)w, (uint)h, 1, 1,
                BindFlags.None, ResourceUsage.Staging, CpuAccessFlags.Read,
                1, 0, ResourceOptionFlags.None));
        }

        private RenderResult ReadBack(int w, int h)
        {
            _context.Map(_stageTex, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None, out var box);
            var buf = new byte[w * h * 4];
            var rowBytes = w * 4;
            for (var y = 0; y < h; y++)
                Marshal.Copy(IntPtr.Add(box.DataPointer, y * (int)box.RowPitch), buf, y * rowBytes, rowBytes);
            _context.Unmap(_stageTex, 0);
            return new RenderResult { Width = w, Height = h, Bgra = buf };
        }

        // ================= 工具 =================

        private static float ComputeNormalizationScale(List<Vector3> vrts, out Vector3 center, out Vector3 min)
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
            return 1.6f / md;
        }

        private static Vector3 Spherical(float r, float az, float el)
        {
            var caz = (float)Math.Cos(az); var saz = (float)Math.Sin(az);
            var cel = (float)Math.Cos(el); var sel = (float)Math.Sin(el);
            return new Vector3(r * cel * saz, r * sel, r * cel * caz);
        }

        /// <summary>绘制地面网格（LineList）：范围 ±2 间距 0.4，位于模型底面；深度测试遮挡、不写深度。
        /// 线色按背景明暗自适应（亮底深灰、暗底浅灰），与 CPU 渲染器一致。</summary>
        private void DrawFloorGrid(Vector4? backgroundColor)
        {
            if (_vbGrid == null || _dsLine == null) return;
            float lum = 0.75f;
            if (backgroundColor.HasValue)
                lum = backgroundColor.Value.X * 0.299f + backgroundColor.Value.Y * 0.587f + backgroundColor.Value.Z * 0.114f;
            var c = lum > 0.5f ? new Vector4(0.32f, 0.32f, 0.32f, 1f) : new Vector4(0.72f, 0.72f, 0.72f, 1f);

            // 范围 ±2（模型半宽约 0.8），间距 0.4：每方向 11 条线，共 44 顶点
            var verts = new GpuVertex[44];
            var k = 0;
            const float half = 2f, step = 0.4f;
            var n = (int)(half * 2f / step);
            for (var i = 0; i <= n; i++)
            {
                var t = -half + i * step;
                verts[k++] = new GpuVertex { Position = new Vector3(t, _gridBaseY, -half), Color = c, Intensity = 1f };
                verts[k++] = new GpuVertex { Position = new Vector3(t, _gridBaseY, half), Color = c, Intensity = 1f };
                verts[k++] = new GpuVertex { Position = new Vector3(-half, _gridBaseY, t), Color = c, Intensity = 1f };
                verts[k++] = new GpuVertex { Position = new Vector3(half, _gridBaseY, t), Color = c, Intensity = 1f };
            }

            var mapped = _context.Map(_vbGrid, MapMode.WriteDiscard, Vortice.Direct3D11.MapFlags.None);
            if (mapped.DataPointer != IntPtr.Zero)
            {
                var p = mapped.DataPointer;
                for (var i = 0; i < verts.Length; i++)
                    Marshal.StructureToPtr(verts[i], IntPtr.Add(p, i * 32), false);
                _context.Unmap(_vbGrid, 0);
            }
            _context.OMSetDepthStencilState(_dsLine, 0);
            _context.IASetPrimitiveTopology(PrimitiveTopology.LineList);
            _context.IASetVertexBuffers(0, 1, new[] { _vbGrid }, new uint[] { 32 }, new uint[] { 0 });
            _context.Draw(44, 0);
            _context.OMSetDepthStencilState(null, 0); // 恢复默认（深度测试+写入）
        }

        // 与 CPU 渲染器 (SoftwareRenderer) 完全一致的 lerp：平滑着色程度 t 混合用
        private static float Lerp(float a, float b, float t) => a + (b - a) * t;

        private static Vector4 Lerp(Vector4 a, Vector4 b, float t)
        {
            var r = a + (b - a) * t;
            return new Vector4(r.X, r.Y, r.Z, 1f);
        }

        private void ReleaseMeshBuffers()
        {
            _vbPos?.Dispose(); _vbPos = null;
            _ibPos?.Dispose(); _ibPos = null;
            _vbNeg?.Dispose(); _vbNeg = null;
            _ibNeg?.Dispose(); _ibNeg = null;
            _vbMod?.Dispose(); _vbMod = null;
            _ibMod?.Dispose(); _ibMod = null;
            _cachedMesh = null;
            _cachedOverride = null;
            _cachedShadow = -1f;
            _cachedSmooth = -1f;
            _cachedNegOverride = null;
            _cachedNegOpacity = -1f;
            _cachedIllumination = -1f;
            _cachedModOverride = null;
            _cachedModOpacity = -1f;
        }

        private void ReleaseFrameBuffers()
        {
            _rtTex?.Dispose(); _rtTex = null;
            _rtv?.Dispose(); _rtv = null;
            _dsTex?.Dispose(); _dsTex = null;
            _dsv?.Dispose(); _dsv = null;
            _stageTex?.Dispose(); _stageTex = null;
        }

        public void Dispose()
        {
            ReleaseMeshBuffers();
            ReleaseFrameBuffers();
            _layout?.Dispose(); _layout = null;
            _vs?.Dispose(); _vs = null;
            _ps?.Dispose(); _ps = null;
            _cb?.Dispose(); _cb = null;
            _rasterizer?.Dispose(); _rasterizer = null;
            _blend?.Dispose(); _blend = null;
            _dsLine?.Dispose(); _dsLine = null;
            _vbGrid?.Dispose(); _vbGrid = null;
            _device?.Dispose(); _device = null;
            _context = null;
        }
    }

    /// <summary>枚举系统可用的 GPU 适配器名（DXGI 1.1，过滤掉软件渲染适配器），供设置界面下拉选择。</summary>
    public static class GpuAdapterInfo
    {
        public static List<string> GetAdapterNames()
        {
            var names = new List<string>();
            try
            {
                using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
                for (uint idx = 0; ; idx++)
                {
                    var res = factory.EnumAdapters1(idx, out var adapter);
                    if (res.Failure || adapter == null) break;
                    using (adapter)
                    {
                        var desc = adapter.Description1;
                        if ((desc.Flags & AdapterFlags.Software) != 0) continue; // 跳过软件适配器
                        var name = desc.Description?.Trim();
                        if (!string.IsNullOrEmpty(name) && !names.Contains(name))
                            names.Add(name);
                    }
                }
            }
            catch
            {
                // 枚举失败返回空列表（设置界面仅显示"自动"）
            }
            return names;
        }
    }
}
