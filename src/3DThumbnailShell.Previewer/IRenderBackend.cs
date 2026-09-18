using System;
using System.Numerics;
using _3DThumbnailShell.Core.Renderer;

namespace _3DThumbnailShell.Previewer
{
    /// <summary>
    /// 渲染后端抽象：CPU（软件渲染）或 DX11 GPU。返回 BGRA 帧缓冲（RenderResult）。
    /// 实现必须线程安全（Viewport3D 在后台线程调用），并在失败时返回 null 而非抛异常。
    /// overrideColor 非 null 时覆盖模型基色（自定义颜色）；shadowLevel 0..1 控制阴影强度。
    /// </summary>
    public interface IRenderBackend : IDisposable
    {
        /// <summary>后端是否可用（GPU 创建失败时为 false，供上层回退 CPU）。</summary>
        bool IsAvailable { get; }

        /// <summary>渲染一帧。smoothShading 0..1：0=平涂锐利，1=全平滑 Gouraud（CPU/GPU 数学一致）。
        /// showNegative=false 隐藏负零件；negativeColor 非 null 时覆盖负零件颜色；negativeOpacity 0..1 控制负零件不透明度。
        /// backgroundColor 非 null 时填充为不透明背景色；null = 透明背景（供显示层合成背景图片）。
        /// showFloorGrid=true 时在模型底面绘制参考网格地面（CPU/GPU 一致）。
        /// illumination 0..1 为"照明（补光）"强度：抬升材质基色 + 提高环境光，让纯黑/深色模型可辨（0=关闭）。
        /// showModifier/modifierColor/modifierOpacity：修改器（modifier_part）叠加层的显示开关、颜色与不透明度。</summary>
        RenderResult Render(MeshData mesh, int width, int height, in CameraParams cam,
            int maxTriangles, Vector4? overrideColor, float shadowLevel, float smoothShading,
            bool showNegative, Vector4? negativeColor, float negativeOpacity,
            Vector4? backgroundColor, bool showFloorGrid, float illumination,
            bool showModifier, Vector4? modifierColor, float modifierOpacity);
    }

    /// <summary>CPU 软件渲染后端：包装 SoftwareRenderer，行为与旧版完全一致（含 LOD 抽稀）。</summary>
    public sealed class CpuRenderBackend : IRenderBackend
    {
        private readonly SoftwareRenderer _renderer = new SoftwareRenderer();
        public bool IsAvailable => true;

        public RenderResult Render(MeshData mesh, int width, int height, in CameraParams cam,
            int maxTriangles, Vector4? overrideColor, float shadowLevel, float smoothShading,
            bool showNegative, Vector4? negativeColor, float negativeOpacity,
            Vector4? backgroundColor, bool showFloorGrid, float illumination,
            bool showModifier, Vector4? modifierColor, float modifierOpacity)
        {
            try
            {
                return _renderer.Render(mesh, width, height,
                    cam.Az, cam.El, cam.Dist, cam.PanX, cam.PanY, cam.Zoom,
                    maxTriangles, overrideColor, shadowLevel, smoothShading,
                    showNegative, negativeColor, negativeOpacity, backgroundColor,
                    showFloorGrid, 1, illumination, showModifier, modifierColor, modifierOpacity);
            }
            catch
            {
                return null;
            }
        }

        public void Dispose()
        {
        }
    }
}
