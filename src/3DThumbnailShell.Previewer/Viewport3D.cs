using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using _3DThumbnailShell.Core.Renderer;

namespace _3DThumbnailShell.Previewer
{
    /// <summary>
    /// 3D 交互视图控件：轨道相机（左键拖拽旋转）、滚轮缩放、右键/中键平移、双击复位。
    /// 渲染在后台线程完成（渲染任务串行化 + 版本号丢弃过期结果）。
    /// 渲染后端/画质由 RenderSettings 控制：CPU 软件渲染或 DX11 GPU；显示时双三次插值缩放。
    /// </summary>
    public sealed class Viewport3D : Control, IMessageFilter
    {
        private const int WM_MOUSEWHEEL = 0x020A;
        private const float DefaultAz = 2.077f;    // 默认视角：119° 方位角（三四视图，模型船头朝右下，与拓竹缩略图一致）
        private const float DefaultEl = 0.4974f;   // 28.5° 俯视（较平缓，便于看到地面网格与模型侧面）
        private const float DefaultDist = 3.1f;    // 默认相机距离（关闭自动取景时的固定远近，与缩略图一致）
        private MeshData _mesh;
        private float _az = DefaultAz, _el = DefaultEl; // 相机方位/仰角
        private float _dist = 3.1f;                 // 相机距离
        private float _panX, _panY;                 // 屏幕像素平移
        private float _zoom = 1f;                   // 视图缩放（围绕画面中心）

        private Bitmap _frame;          // 最近一次渲染帧（高分辨率）
        private int _version;           // 渲染版本号：丢弃过期结果
        private bool _disposed;

        // 背景图片缓存（显示层合成：帧背景为透明时图片从帧下透出）
        private Bitmap _bgImage;
        private string _bgImagePath;

        private Point _last;
        private bool _dragging;
        private int _dragMode;          // 0=无 1=旋转 2=平移

        private RenderSettings _settings;
        private bool _backendInvalid = true;

        private IRenderBackend _backend;
        private IRenderBackend _cpuBackend; // 专用于线段云（G-code），GPU 不支持线段
        private string _backendName = "CPU";

        // 渲染串行化：同一时刻只跑一个后台渲染任务，新请求合并为待渲染。
        // 状态全部在 _renderLock 下读改：原子化「检查渲染槽-置位」，
        // 杜绝 UI 线程与后台线程竞争导致并发渲染（共享 DX11 context 被两台任务同时
        // 使用会概率性 GPU 挂起）以及渲染槽被永久占用（死锁）导致的视图卡死。
        private readonly object _renderLock = new object();
        private volatile bool _rendering;   // 是否正有渲染任务在跑（供 CaptureFrame 快速读取）
        private bool _pending;              // 是否积累了待补渲的最新请求（仅 _renderLock 保护）
        private RenderRequest _lastRequest;

        /// <summary>渲染完成回调（UI 线程）：耗时毫秒 + 后端名称。</summary>
        public event Action<long, string> RenderCompleted;

        /// <summary>当前渲染后端名称（如 "DX11 GPU" / "CPU"），供界面显示。</summary>
        public string BackendName => _backendName;

        /// <summary>渲染设置：修改后自动以新设置重渲染。</summary>
        public RenderSettings Settings
        {
            get => _settings;
            set
            {
                var prevAutoFit = _settings?.AutoFitView ?? true;
                _settings = value ?? RenderSettings.Load();
                _backendInvalid = true; // 后端（Mode 变化）下次渲染时重选
                BackColor = ComputeCanvasColor(); // 画布底色跟随背景设置/主题
                Invalidate();
                if (_mesh != null)
                {
                    // 刚开启自动取景：立即按包围盒重新取景（仅此一种设置变更会改动视角，避免覆盖用户缩放）
                    if (_settings.AutoFitView && !prevAutoFit && !IsPreviewImageMesh(_mesh))
                        ResetView();
                    else
                        ScheduleRender(highQuality: true, force: true);
                }
            }
        }

        public Viewport3D()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            // ControlStyles.Selectable 默认 false（基础 Control 不设置）：
            // Focus() 内部 CanSelect 要求该样式，否则即使 TabStop=true 也拿不到焦点；
            // 而真实滚轮是 SendMessage 直达焦点窗口（不经 IMessageFilter 队列），视口无焦点则滚轮永不可达。
            // 必须显式设置 Selectable 才能在 OnMouseEnter 时成功夺焦，滚轮缩放才可用。
            TabStop = true;
            _settings = RenderSettings.Load();
            BackColor = ComputeCanvasColor(); // 画布底色跟随主题（亮色白/暗色深）
            // 兜底：全局消息过滤捕获滚轮，命中视口区域即缩放（即使焦点机制被其他控件抢走也可靠）
            Application.AddMessageFilter(this);
        }

        /// <summary>
        /// 界面主题变化时同步画布：底色/占位文字/渲染背景默认色跟随主题，
        /// 已加载模型则按新背景强制重渲染。设置中的自定义背景颜色/图片优先于主题。
        /// </summary>
        public void ApplyTheme(UiThemeMode mode)
        {
            _ = mode; // 主题从设置实例读取，此处仅作显式调用入口
            BackColor = ComputeCanvasColor();
            Invalidate();
            if (_mesh != null) ScheduleRender(highQuality: true, force: true);
        }

        /// <summary>画布底色：设置的自定义背景颜色优先；否则跟随主题（亮色=白，暗色=深中性色）。</summary>
        private Color ComputeCanvasColor()
        {
            var bg = _settings?.ParseBackgroundColor();
            if (bg.HasValue)
                return Color.FromArgb(255,
                    (int)(bg.Value.X * 255f), (int)(bg.Value.Y * 255f), (int)(bg.Value.Z * 255f));
            return _settings != null && _settings.UiTheme == UiThemeMode.Dark
                ? Color.FromArgb(24, 24, 28)
                : Color.White;
        }

        /// <summary>主题默认画布颜色（0..1 RGBA，供渲染后端填充背景）：亮色=白，暗色=深中性色。</summary>
        private static System.Numerics.Vector4 ThemeCanvasVector(UiThemeMode mode)
        {
            return mode == UiThemeMode.Dark
                ? new System.Numerics.Vector4(24f / 255f, 24f / 255f, 28f / 255f, 1f)
                : new System.Numerics.Vector4(1f, 1f, 1f, 1f);
        }

        /// <summary>全局滚轮兜底：WM_MOUSEWHEEL 命中本控件客户区时直接缩放，避免焦点丢失导致滚轮失效。</summary>
        bool IMessageFilter.PreFilterMessage(ref Message m)
        {
            if (m.Msg == WM_MOUSEWHEEL)
            {
                // 坐标取 lParam（消息携带的屏幕坐标），比 Cursor.Position 可靠（自动化测试可精确控制）
                var screen = new Point((short)(m.LParam.ToInt64() & 0xFFFF), (short)((m.LParam.ToInt64() >> 16) & 0xFFFF));
                var pt = PointToClient(screen);
                if (IsHandleCreated && Visible && _mesh != null && ClientRectangle.Contains(pt))
                {
                    var delta = (short)((m.WParam.ToInt64() >> 16) & 0xFFFF);
                    ApplyZoom(delta > 0);
                    return true; // 已处理，吞掉该消息
                }
            }
            return false;
        }

        /// <summary>设置当前模型，触发重新渲染（含归一化）。可在任意线程调用。</summary>
        public void SetMesh(MeshData mesh)
        {
            _mesh = mesh;
            ResetCamera(mesh);

            // 切片文件（CTB/PHOTON 等）直接显示嵌入预览图，跳过 3D 渲染
            if (mesh != null && mesh.PreviewBgra != null && mesh.PreviewWidth > 0 && mesh.PreviewHeight > 0)
            {
                _frame = BgraToBitmap(mesh.PreviewBgra, mesh.PreviewWidth, mesh.PreviewHeight);
                Invalidate();
                return;
            }
            ScheduleRender(highQuality: true, force: true);
        }

        private static Bitmap BgraToBitmap(byte[] bgra, int w, int h)
        {
            var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            var rect = new Rectangle(0, 0, w, h);
            var data = bmp.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            try { System.Runtime.InteropServices.Marshal.Copy(bgra, 0, data.Scan0, bgra.Length); }
            finally { bmp.UnlockBits(data); }
            return bmp;
        }

        public void ResetView()
        {
            ResetCamera(_mesh);
            ScheduleRender(highQuality: true, force: true);
        }

        /// <summary>复位相机：默认 45°/45°；自动取景开启时按包围盒拉近 + 平移居中，否则固定距离。</summary>
        private void ResetCamera(MeshData mesh)
        {
            _az = DefaultAz; _el = DefaultEl; _dist = AutoFitDist(mesh);
            _panX = 0f; _panY = 0f; _zoom = 1f;
            if (_settings != null && _settings.AutoFitView && !IsPreviewImageMesh(mesh))
            {
                var (px, py) = AutoFitPan(mesh, _dist);
                _panX = px; _panY = py;
            }
        }

        /// <summary>是否 3D 网格（而非直接显示嵌入预览图的切片文件）。</summary>
        private static bool IsPreviewImageMesh(MeshData mesh)
        {
            return mesh != null && mesh.PreviewBgra != null && mesh.PreviewWidth > 0 && mesh.PreviewHeight > 0;
        }

        /// <summary>
        /// 自动取景开关开启时返回按包围盒算出的适配相机距离；关闭（或切片预览图）返回固定 3.1f。
        /// 仅在载入模型 / 复位视图时调用，不覆盖用户后续的缩放/平移。
        /// </summary>
        private float AutoFitDist(MeshData mesh)
        {
            if (_settings == null || !_settings.AutoFitView) return DefaultDist;
            if (IsPreviewImageMesh(mesh)) return DefaultDist;
            return ComputeFitDistance(mesh, Width, Height);
        }

        /// <summary>
        /// 取景后把模型投影包围框中心平移到画面中心（像素）。
        /// 45° 俯视 + 模型有高度时，透视近大远小会使包围框中心偏离原点
        /// （顶部更靠近相机被放大，包围框中心上移/侧移），不修正则模型偏出画面/被裁剪。
        /// panX/panY 与交互平移语义一致：正 panY 模型下移、正 panX 右移。
        /// </summary>
        private (float panX, float panY) AutoFitPan(MeshData mesh, float dist)
        {
            if (mesh == null || mesh.Vertices == null || mesh.Vertices.Count == 0) return (0f, 0f);
            var vrts = mesh.Vertices;
            var min = vrts[0]; var max = vrts[0];
            for (var i = 1; i < vrts.Count; i++)
            {
                min = Vector3.Min(min, vrts[i]);
                max = Vector3.Max(max, vrts[i]);
            }
            var center = (min + max) * 0.5f;
            var dim = max - min;
            var md = Math.Max(dim.X, Math.Max(dim.Y, dim.Z));
            if (md <= 1e-9f) return (0f, 0f);
            var scale = 1.6f / md;

            var stride = Math.Max(1, (int)Math.Ceiling((double)vrts.Count / 65536));
            var samples = new Vector3[(vrts.Count + stride - 1) / stride];
            int k = 0;
            for (var i = 0; i < vrts.Count; i += stride)
                samples[k++] = (vrts[i] - center) * scale;

            int w = Width, h = Height;
            if (w < 8 || h < 8) { w = 1000; h = 1000; }
            var aspect = (float)w / h;
            var ndc = ProjectNdc(samples, dist, aspect);
            float cx = (ndc.minX + ndc.maxX) * 0.5f;
            float cy = (ndc.minY + ndc.maxY) * 0.5f;
            return (-cx * w * 0.5f, cy * h * 0.5f);
        }

        /// <summary>
        /// 自动取景：把网格顶点归一化到渲染器同款单位空间，抽样投影到屏幕求真实轮廓外接框，
        /// 二分求解相机距离，使模型投影外接框最大边占画面约 85%（contain：整模型可见且尽量填满）。
        /// 用顶点抽样而非包围盒 8 角点：盘位散布/镂空布局的外接框远大于实际轮廓，用角点会严重欠取景。
        /// 与 SoftwareRenderer 投影数学一致（Spherical / LookAt / Perspective FOV=π/3.6、归一化 1.6/md）。
        /// </summary>
        private float ComputeFitDistance(MeshData mesh, int w, int h)
        {
            if (mesh == null || mesh.Vertices == null || mesh.Vertices.Count == 0) return DefaultDist;
            var vrts = mesh.Vertices;
            var min = vrts[0]; var max = vrts[0];
            for (var i = 1; i < vrts.Count; i++)
            {
                min = Vector3.Min(min, vrts[i]);
                max = Vector3.Max(max, vrts[i]);
            }
            var center = (min + max) * 0.5f;
            var dim = max - min;
            var md = Math.Max(dim.X, Math.Max(dim.Y, dim.Z));
            if (md <= 1e-9f) return DefaultDist;
            var scale = 1.6f / md;

            // 顶点抽样（上限约 64k 个，投影成本可控）：大模型取等距抽样，小模型全取
            var stride = Math.Max(1, (int)Math.Ceiling((double)vrts.Count / 65536));
            var samples = new Vector3[(vrts.Count + stride - 1) / stride];
            int k = 0;
            for (var i = 0; i < vrts.Count; i += stride)
                samples[k++] = (vrts[i] - center) * scale;

            // 控件尚未布局（宽高为 0）时按方形画布兜底，避免除零
            if (w < 8 || h < 8) { w = 1000; h = 1000; }
            var aspect = (float)w / h;
            const float target = 0.85f;

            // 二分：dist 越小投影越大；覆盖 > 目标则调大 dist
            float lo = 0.3f, hi = 20f;
            for (var iter = 0; iter < 30; iter++)
            {
                var mid = (lo + hi) * 0.5f;
                var cov = ProjectCoverage(samples, mid, aspect);
                if (cov < 0f) lo = mid;          // 顶点被近平面裁剪：距离过小
                else if (cov > target) lo = mid;
                else hi = mid;
            }
            var fitDist = (lo + hi) * 0.5f;
            return fitDist;
        }

        /// <summary>
        /// 把归一化后的顶点抽样按给定相机距离投影，返回 max(投影宽/画宽, 投影高/画高)。
        /// 有效顶点不足 2 个（全部被近平面裁剪，距离过小）时返回 -1。
        /// </summary>
        private float ProjectCoverage(Span<Vector3> samples, float dist, float aspect)
        {
            var caz = (float)Math.Cos(_az); var saz = (float)Math.Sin(_az);
            var cel = (float)Math.Cos(_el); var sel = (float)Math.Sin(_el);
            var camPos = new Vector3(dist * cel * saz, dist * sel, dist * cel * caz);
            var view = Matrix4x4.CreateLookAt(camPos, Vector3.Zero, Vector3.UnitY);
            var proj = Matrix4x4.CreatePerspectiveFieldOfView((float)(Math.PI / 3.6), aspect, 0.1f, 20f);

            float minX = float.MaxValue, maxX = float.MinValue, minY = float.MaxValue, maxY = float.MinValue;
            int valid = 0;
            foreach (var c in samples)
            {
                var pv = Vector4.Transform(new Vector4(c, 1f), view);
                if (pv.Z > -0.1f || pv.W <= 0f) continue; // 与渲染器相同的近平面剔除
                var pc = Vector4.Transform(pv, proj);
                if (pc.W <= 1e-12f) continue;
                var inv = 1f / pc.W;
                float nx = pc.X * inv, ny = pc.Y * inv;
                if (float.IsNaN(nx) || float.IsNaN(ny) || float.IsInfinity(nx) || float.IsInfinity(ny)) continue;
                if (nx < minX) minX = nx; if (nx > maxX) maxX = nx;
                if (ny < minY) minY = ny; if (ny > maxY) maxY = ny;
                valid++;
            }
            if (valid < 2) return -1f;
            // NDC [-1,1] 横跨整幅画面：像素宽/高占画面比例 = (max-min)*0.5
            return Math.Max((maxX - minX) * 0.5f, (maxY - minY) * 0.5f);
        }

        /// <summary>与 ProjectCoverage 相同的投影，返回 NDC 包围框（供 AutoFitPan 计算投影中心）。</summary>
        private (float minX, float minY, float maxX, float maxY) ProjectNdc(Span<Vector3> samples, float dist, float aspect)
        {
            var caz = (float)Math.Cos(_az); var saz = (float)Math.Sin(_az);
            var cel = (float)Math.Cos(_el); var sel = (float)Math.Sin(_el);
            var camPos = new Vector3(dist * cel * saz, dist * sel, dist * cel * caz);
            var view = Matrix4x4.CreateLookAt(camPos, Vector3.Zero, Vector3.UnitY);
            var proj = Matrix4x4.CreatePerspectiveFieldOfView((float)(Math.PI / 3.6), aspect, 0.1f, 20f);

            float minX = float.MaxValue, maxX = float.MinValue, minY = float.MaxValue, maxY = float.MinValue;
            foreach (var c in samples)
            {
                var pv = Vector4.Transform(new Vector4(c, 1f), view);
                if (pv.Z > -0.1f || pv.W <= 0f) continue;
                var pc = Vector4.Transform(pv, proj);
                if (pc.W <= 1e-12f) continue;
                var inv = 1f / pc.W;
                float nx = pc.X * inv, ny = pc.Y * inv;
                if (float.IsNaN(nx) || float.IsNaN(ny) || float.IsInfinity(nx) || float.IsInfinity(ny)) continue;
                if (nx < minX) minX = nx; if (nx > maxX) maxX = nx;
                if (ny < minY) minY = ny; if (ny > maxY) maxY = ny;
            }
            return (minX, minY, maxX, maxY);
        }

        /// <summary>
        /// 返回当前渲染帧（供保存）。waitForPending=true 时（必须在 UI 线程调用）
        /// 等待进行中的渲染串行化完成，保证拿到最新视角的高清帧，而非旧帧或交互低分辨率帧。
        /// 无帧时返回 null。
        /// </summary>
        public Bitmap CaptureFrame(bool waitForPending = false)
        {
            if (_disposed) return null;
            if (waitForPending && _rendering)
            {
                // 渲染完成回调通过 BeginInvoke 在 UI 线程执行：DoEvents 让回调与后续渲染推进
                var sw = Stopwatch.StartNew();
                while (_rendering && sw.ElapsedMilliseconds < 3000)
                {
                    Application.DoEvents();
                    Thread.Sleep(5);
                }
                Application.DoEvents(); // 处理已排队的帧更新回调
            }
            return _frame == null ? null : new Bitmap(_frame);
        }

        /// <summary>
        /// 按指定尺寸重渲当前模型 → 透明底 BGRA 位图（用于保存透明缩略图）。
        /// 保留当前相机方位/仰角/距离，pan/zoom 重置为 0/1 让模型自动取景居中（无白边）。
        /// 网格已是 Y-up；背景不填充（backgroundColor=null → 透明黑），模型像素不透明。
        /// </summary>
        public Bitmap RenderThumbnail(int rw, int rh)
        {
            if (_mesh == null || _disposed || rw <= 0 || rh <= 0) return null;
            var s = _settings;
            var renderer = new SoftwareRenderer();
            var res = renderer.Render(_mesh, rw, rh,
                _az, _el, _dist, 0f, 0f, 1f,
                s.MaxTriangles, s.ParseModelColor(), s.ShadowLevel, s.SmoothShading,
                s.ShowNegativeParts, s.ParseNegativeColor(), s.NegativeOpacity,
                backgroundColor: null, showFloorGrid: false,
                superSample: SoftwareRenderer.ThumbSuperSample(rw, rh),
                illumination: s.Illumination, // 所见即所得：保存缩略图与当前预览（含照明开关）一致
                showModifier: s.ShowModifierParts, modifierColor: s.ParseModifierColor(),
                modifierOpacity: s.ModifierOpacity);
            if (res == null || res.Bgra == null || res.Bgra.Length == 0) return null;
            var bmp = new Bitmap(rw, rh, PixelFormat.Format32bppArgb);
            var rect = new Rectangle(0, 0, rw, rh);
            var d = bmp.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            System.Runtime.InteropServices.Marshal.Copy(res.Bgra, 0, d.Scan0, res.Bgra.Length);
            bmp.UnlockBits(d);
            return bmp;
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            // 左键点击 Gizmo（立方体某面或某轴）：快速对准对应视角，不启动拖拽
            if (e.Button == MouseButtons.Left && _mesh != null && ShowGizmo)
            {
                var hit = HitTestGizmo(e.Location);
                if (hit.HasValue)
                {
                    SnapView(hit.Value.az, hit.Value.el);
                    return;
                }
            }
            if (e.Button == MouseButtons.Left)
            {
                _dragMode = 1; // 旋转
            }
            else if (e.Button == MouseButtons.Right || e.Button == MouseButtons.Middle)
            {
                _dragMode = 2; // 平移
            }
            else
            {
                _dragMode = 0;
            }
            if (_dragMode != 0)
            {
                _dragging = true;
                _last = e.Location;
                // 捕获鼠标：拖拽移出控件区域仍持续收到 MouseMove（右键/中键平移不中断）
                Capture = true;
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (!_dragging || _mesh == null) return;
            var dx = e.X - _last.X;
            var dy = e.Y - _last.Y;
            _last = e.Location;

            if (_dragMode == 1)
            {
                _az += dx * 0.012f;
                _el += dy * 0.012f;
                if (_el > 1.55f) _el = 1.55f;
                if (_el < -1.55f) _el = -1.55f;
            }
            else if (_dragMode == 2)
            {
                _panX += dx;
                _panY += dy;
            }
            // 交互中用低分辨率快速重绘，松手后再高清
            ScheduleRender(highQuality: false, force: false);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (_dragging)
            {
                _dragging = false;
                _dragMode = 0;
                Capture = false;
                ScheduleRender(highQuality: true, force: false);
            }
        }

        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            base.OnMouseDoubleClick(e);
            // 仅左键双击复位视图。中键/右键连续两次按下（如快速分段拖动平移）
            // 会被系统判定为双击，若无条件复位会导致平移“自动归位”。
            if (e.Button == MouseButtons.Left)
                ResetView();
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            base.OnMouseEnter(e);
            // 滚轮事件只发给有焦点的控件：鼠标进入视口即夺焦，否则滚轮缩放不生效
            Focus();
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            if (_mesh == null) return;
            ApplyZoom(e.Delta > 0);
        }

        /// <summary>滚轮缩放：以画面中心为锚点。OnMouseWheel 与 IMessageFilter 兜底共用。</summary>
        private void ApplyZoom(bool up)
        {
            var factor = up ? 1.12f : 1f / 1.12f;
            _zoom *= factor;
            if (_zoom < 0.05f) _zoom = 0.05f;
            if (_zoom > 60f) _zoom = 60f;
            ScheduleRender(highQuality: true, force: false);
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (_mesh != null && Width > 8 && Height > 8)
                ScheduleRender(highQuality: true, force: false);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            e.Graphics.Clear(BackColor);
            // 背景图片（显示层合成）：设置后优先于背景颜色；帧背景透明，图片从帧下透出
            EnsureBackgroundImage();
            if (_bgImage != null)
            {
                e.Graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                e.Graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
                e.Graphics.DrawImage(_bgImage, ClientRectangle);
            }
            if (_frame != null)
            {
                e.Graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                e.Graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
                e.Graphics.DrawImage(_frame, ClientRectangle);
            }
            else if (_mesh == null)
            {
                using var f = new Font("Microsoft YaHei UI", 11f);
                // 占位文字颜色随画布明暗自适应（暗底用浅灰，亮底用中灰）
                var hintColor = IsDarkCanvas(BackColor) ? Color.FromArgb(150, 150, 150) : Color.Gray;
                TextRenderer.DrawText(e.Graphics, "拖入或打开 3D 文件开始预览\n左键拖拽旋转 · 滚轮缩放 · 右键平移 · 双击复位",
                    f, ClientRectangle, hintColor, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }

            // 右下角 C4D 式导航器（控件层叠加，可点击切换视角）
            DrawGizmo(e.Graphics);
        }

        /// <summary>画布是否偏暗（决定占位文字颜色）。</summary>
        private static bool IsDarkCanvas(Color c)
        {
            return (c.R * 0.299 + c.G * 0.587 + c.B * 0.114) < 128;
        }

        // ---------- C4D 式可交互立方体导航器（Gizmo） ----------
        // 几何随 GizmoScale（设置项）缩放：小/中/大对应 0.75/1/1.35，放大保证文字完整
        private float GizmoScale => _settings?.GizmoScale ?? 1f;
        /// <summary>Gizmo 是否开启：设置里选"关"（GizmoScale = -1）时不绘制也不响应点击。</summary>
        private bool ShowGizmo => GizmoScale >= 0f;
        private float GizmoBoxR      => 42f * GizmoScale;   // 立方体半径（中心→面，像素）
        private float GizmoAxisLen   => 86f * GizmoScale;   // 轴箭头长度（中心→锥尖，像素）
        private float GizmoHitPad    => 18f * GizmoScale;   // 轴线命中判定半径
        private const float GizmoPadRight  = 45f; // 距右缘（贴边靠右，留少量边距）
        private const float GizmoPadBottom = 130f; // 距下缘（往上抬高）
        private const float TopTilt        = 1.55f; // 顶/底视角倾角（避开极点退化基向量）

        private static readonly Vector3[] GizmoCorners =
        {
            new(-1f, -1f, -1f), // 0
            new( 1f, -1f, -1f), // 1
            new( 1f,  1f, -1f), // 2
            new(-1f,  1f, -1f), // 3
            new(-1f, -1f,  1f), // 4
            new( 1f, -1f,  1f), // 5
            new( 1f,  1f,  1f), // 6
            new(-1f,  1f,  1f), // 7
        };
        // 六个面：固定坐标 ±BoxR 位于 axis 轴，四个角点由其余两轴组合
        private static readonly (int sign, int axis, int i0, int i1, int i2, int i3)[] GizmoFaces =
        {
            ( 1, 2, 4, 5, 6, 7), // +Z 前
            (-1, 2, 0, 1, 2, 3), // -Z 后
            ( 1, 1, 3, 2, 6, 7), // +Y 上
            (-1, 1, 0, 1, 5, 4), // -Y 下
            ( 1, 0, 1, 2, 6, 5), // +X 右
            (-1, 0, 0, 3, 7, 4), // -X 左
        };
        private static readonly (int a, int b)[] GizmoEdges =
        {
            (0,1),(1,2),(2,3),(3,0),
            (4,5),(5,6),(6,7),(7,4),
            (0,4),(1,5),(2,6),(3,7),
        };
        // Z-up 渲染坐标：+X=模型右、+Y=上（模型 Z 顶）、+Z=模型后（模型 Y 前）。轴箭头为正方向，
        // axis2 指向模型前方 -Z（渲染器 -Z），保证"绿 Y 箭头朝前"而非朝后。
        private static readonly Vector3[] GizmoAxes = { new(1, 0, 0), new(0, 1, 0), new(0, 0, -1) };
        private static readonly Color[] GizmoColors =
        {
            // Z-up 拓竹习惯：轴1(上/下)=蓝Z，轴2(前/后)=绿Y，轴0(右/左)=红X；低饱和避免抢视觉
            Color.FromArgb(214, 128, 122),   // 轴0 红 X（右/左）
            Color.FromArgb(122, 148, 214),   // 轴1 蓝 Z（上/下）
            Color.FromArgb(124, 188, 122),   // 轴2 绿 Y（前/后）
        };
        private static readonly string[] GizmoNames = { "X", "Z", "Y" };
        // 立方体各面文字（键 → 随 I18n 语言切换）：axis→[右/左, 上/下, 前/后]。
        // 顶/底、前/后、右/左按用户要求全部调换展示：
        // axis0 的 +X(右)面显示"左面"、-X(左)面显示"右面"；axis1 的 +Y(上)面显示"底部"、-Y(下)面显示"顶部"；
        // axis2 的 +Z(后)面显示"前面"、-Z(前)面显示"后面"。
        private static readonly string[] GizmoFaceKeys = { "faceLeft", "faceRight", "faceBottom", "faceTop", "faceFront", "faceBack" };

        /// <summary>Gizmo 坐标轴标签字体。</summary>
        private static readonly Font GizmoFont = new Font("Segoe UI", 11f, FontStyle.Bold);
        /// <summary>Gizmo 立方体面文字字体（中文）。</summary>
        private static readonly Font GizmoFaceFont = new Font("Microsoft YaHei UI", 9.5f, FontStyle.Bold);

        /// <summary>
        /// 右下角 C4D 式立方体导航器：随相机旋转的透明六面立方体 + 三个彩色轴箭头。
        /// 点击某面（前/后/上/下/左/右）或某根轴+箭头，可快速对准对应视角。
        /// 叠加绘制在控件层（OnPaint），用控件像素定位，保证完整可见、清晰（不被缩放）且可点击。
        /// </summary>
        private Bitmap _gizmoBmp;                       // 预渲染 Gizmo 位图（透明底）
        private int _gizmoPxPrev;                       // 上次位图边长（尺寸设置变化时重建）
        private int GizmoPx => Math.Max(120, (int)Math.Round(200f * GizmoScale)); // 位图边长（随设置缩放）

        private void DrawGizmo(Graphics g)
        {
            if (!ShowGizmo) return; // 设置里"关"则不绘制
            if (Width < 180 || Height < 180) return;
            var px = GizmoPx;
            if (_gizmoBmp == null || _gizmoPxPrev != px)
            {
                _gizmoBmp?.Dispose();
                _gizmoBmp = new Bitmap(px, px, PixelFormat.Format32bppArgb);
                _gizmoPxPrev = px;
            }
            var (x, y, f) = CameraBasis();
            var dark = IsDarkCanvas(BackColor);
            using (var gb = Graphics.FromImage(_gizmoBmp))
            {
                gb.Clear(Color.Transparent);
                RenderGizmoGlyph(gb, new PointF(px * 0.5f, px * 0.5f), x, y, f, dark);
            }
            var r = GizmoScreenRect();
            g.DrawImage(_gizmoBmp, (int)r.X, (int)r.Y, px, px);
        }

        /// <summary>把 C4D 立方体+三轴雕刻进指定 Graphics，中心 c（像素），基向量见 CameraBasis。</summary>
        private void RenderGizmoGlyph(Graphics g, PointF c, Vector3 x, Vector3 y, Vector3 f, bool dark)
        {

            g.SmoothingMode = SmoothingMode.AntiAlias;
            // 透明底图无法用 ClearType 次像素渲染（会产生模糊），改用对齐像素网格的灰度抗锯齿 → 文字更清晰
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;

            // 立方体 8 角投影（坐标已按 BoxR 放大为像素）
            Span<Vector2> sc = stackalloc Vector2[8];
            Span<float> sd = stackalloc float[8];
            for (var i = 0; i < 8; i++)
            {
                var v = GizmoCorners[i] * GizmoBoxR;
                sc[i] = new Vector2(c.X + Vector3.Dot(v, x), c.Y - Vector3.Dot(v, y));
                sd[i] = Vector3.Dot(v, f);
            }

            // 面：按视深从远到近以画家算法绘制（面向观察者更亮/更实）
            Span<float> fdepth = stackalloc float[6];
            for (var i = 0; i < 6; i++)
            {
                var (_, _, a, b, d, e) = GizmoFaces[i];
                fdepth[i] = (sd[a] + sd[b] + sd[d] + sd[e]) * 0.25f;
            }
            Span<int> order = stackalloc int[6] { 0, 1, 2, 3, 4, 5 };
            for (var a = 1; a < 6; a++)
                for (var b = a; b > 0 && fdepth[order[b]] < fdepth[order[b - 1]]; b--)
                    (order[b], order[b - 1]) = (order[b - 1], order[b]);

            // 中性深灰立方体（拓竹风格：面不染色，靠明暗区分朝向）+ 面向观察者的面画中文方向文字
            var gray = dark ? 46 : 118; // 亮底用更深灰保证文字/线框对比
            var labelColor = dark ? Color.FromArgb(235, 235, 235) : Color.FromArgb(40, 40, 40);
            using (var lblBrush = new SolidBrush(labelColor))
            using (var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
            {
                Span<Vector2> p = stackalloc Vector2[4]; // 复用的面顶点缓冲（仅 6 面）
                for (var k = 0; k < 6; k++)
                {
                    var (sign, axis, i0, i1, i2, i3) = GizmoFaces[order[k]];
                    var nv = Vector3.Dot(GizmoAxes[axis] * sign, f); // 面外法线沿视线分量
                    var front = nv > 0f;
                    p[0] = sc[i0]; p[1] = sc[i1]; p[2] = sc[i2]; p[3] = sc[i3];
                    using (var fb = new SolidBrush(Color.FromArgb(front ? 175 : 70, gray, gray, gray)))
                        g.FillPolygon(fb, ToPointF(p));
                    if (front)
                    {
                        var cx = (sc[i0].X + sc[i1].X + sc[i2].X + sc[i3].X) * 0.25f;
                        var cy = (sc[i0].Y + sc[i1].Y + sc[i2].Y + sc[i3].Y) * 0.25f;
                        g.DrawString(I18n.T(GizmoFaceKeys[axis * 2 + (sign > 0 ? 0 : 1)]), GizmoFaceFont, lblBrush, cx, cy, fmt);
                    }
                }
            }

            // 棱（统一在最上层描，形成清晰线框）
            using (var ep = new Pen(dark ? Color.FromArgb(210, 235, 235, 235) : Color.FromArgb(160, 28, 28, 28), 1.4f))
                foreach (var (a, b) in GizmoEdges)
                    g.DrawLine(ep, sc[a].X, sc[a].Y, sc[b].X, sc[b].Y);

            // 三个 + 轴箭头与标签
            for (var i = 0; i < 3; i++)
            {
                var tipW = GizmoAxes[i] * GizmoAxisLen;
                var tx = c.X + Vector3.Dot(tipW, x);
                var ty = c.Y - Vector3.Dot(tipW, y);
                float len = (float)Math.Sqrt((tx - c.X) * (tx - c.X) + (ty - c.Y) * (ty - c.Y));
                if (len < 6f) continue; // 轴与视线重合，不可见则跳过
                var ux = (tx - c.X) / len; var uy = (ty - c.Y) / len;
                using (var pen = new Pen(GizmoColors[i], 2.4f))
                {
                    pen.StartCap = LineCap.Round; pen.EndCap = LineCap.Round;
                    g.DrawLine(pen, c.X, c.Y, tx, ty);
                }
                // 锥形箭头
                var bx = tx - ux * 11f; var by = ty - uy * 11f;
                using (var fb = new SolidBrush(GizmoColors[i]))
                    g.FillPolygon(fb, new[] {
                        new PointF(tx, ty),
                        new PointF(bx - uy * 6f, by + ux * 6f),
                        new PointF(bx + uy * 6f, by - ux * 6f) });
                // 标签：锥尖外侧
                using (var fb = new SolidBrush(GizmoColors[i]))
                    g.DrawString(GizmoNames[i], GizmoFont, fb, tx + ux * 10f - 3f, ty + uy * 10f + 4f);
            }
        }

        private static PointF[] ToPointF(Span<Vector2> pts)
        {
            var r = new PointF[pts.Length];
            for (var i = 0; i < pts.Length; i++) r[i] = new PointF(pts[i].X, pts[i].Y);
            return r;
        }

        /// <summary>Gizmo 屏幕矩形（视口右下角，靠角落）。</summary>
        private RectangleF GizmoScreenRect()
        {
            var cr = ClientRectangle;
            return new RectangleF(cr.Right - GizmoPadRight - GizmoPx,
                                  cr.Bottom - GizmoPadBottom - GizmoPx, GizmoPx, GizmoPx);
        }

        /// <summary>与渲染一致的相机正交基（右/上/前向）。</summary>
        private (Vector3 x, Vector3 y, Vector3 f) CameraBasis()
        {
            var caz = (float)Math.Cos(_az); var saz = (float)Math.Sin(_az);
            var cel = (float)Math.Cos(_el); var sel = (float)Math.Sin(_el);
            if (Math.Abs(cel) < 1e-4f) cel = 1e-4f;
            var camPos = new Vector3(cel * saz, sel, cel * caz);
            var f = -Vector3.Normalize(camPos);
            var x = Vector3.Normalize(Vector3.Cross(Vector3.UnitY, f));
            var y = Vector3.Cross(f, x);
            return (x, y, f);
        }

        /// <summary>
        /// Gizmo 命中测试：命中某轴箭头返回其对应 + 视角；命中某面返回该面对应视角；
        /// 均未命中返回 null。命中结果用于左键点击切换视角。
        /// </summary>
        private (float az, float el)? HitTestGizmo(Point p)
        {
            // 命中投影中心 = 预渲染位图中棱的中心，平移到位图在屏幕上的左上角
            var r = GizmoScreenRect();
            var c = new PointF(r.X + GizmoPx * 0.5f, r.Y + GizmoPx * 0.5f);
            var (x, y, f) = CameraBasis();
            var v = new Vector2(p.X, p.Y);

            // 轴（在面上层，优先判定）：点到 中心→锥尖 线段的距离
            for (var i = 0; i < 3; i++)
            {
                var tipW = GizmoAxes[i] * GizmoAxisLen;
                var tx = c.X + Vector3.Dot(tipW, x);
                var ty = c.Y - Vector3.Dot(tipW, y);
                if (DistToSeg(v, new Vector2(c.X, c.Y), new Vector2(tx, ty)) < GizmoHitPad)
                    return i == 2 ? AxisSnap(2, -1) : AxisSnap(i, 1); // 轴2 指向模型前(-Z)，箭头=前要按 sign-1 对齐
            }

            // 面：点在四边形内（取视深最近者）
            (float az, float el)? best = null; float bestDepth = float.MinValue;
            for (var k = 0; k < 6; k++)
            {
                var (sign, axis, i0, i1, i2, i3) = GizmoFaces[k];
                var pts = new[] {
                    project(GizmoCorners[i0]), project(GizmoCorners[i1]),
                    project(GizmoCorners[i2]), project(GizmoCorners[i3]) };
                if (!PtInQuad(v, pts)) continue;
                var depth = (projectDepth(GizmoCorners[i0]) + projectDepth(GizmoCorners[i1])
                           + projectDepth(GizmoCorners[i2]) + projectDepth(GizmoCorners[i3])) * 0.25f;
                if (depth > bestDepth) { bestDepth = depth; best = AxisSnap(axis, sign); }
            }
            return best;

            Vector2 project(Vector3 w) => new Vector2(
                c.X + Vector3.Dot(w * GizmoBoxR, x), c.Y - Vector3.Dot(w * GizmoBoxR, y));
            float projectDepth(Vector3 w) => Vector3.Dot(w * GizmoBoxR, f);
        }

        /// <summary>轴/面对应的正负视角（az/el）。axis=0→X,1→Y,2→Z；sign=±1 指方向。</summary>
        private (float az, float el) AxisSnap(int axis, int sign)
        {
            if (axis == 1) return (_az, sign > 0 ? TopTilt : -TopTilt); // 上/下（保留当前方位）
            if (axis == 2) return (sign > 0 ? 0f : (float)Math.PI, 0f); // 前(-Z面)→az=π(相机在-Z侧) / 后(+Z面)→az=0
            return (sign > 0 ? (float)(Math.PI / 2) : (float)(Math.PI * 3 / 2), 0f); // 右 +X/左 -X
        }

        /// <summary>切换到指定视角：设置方位/仰角，若开启自动取景按新视角重新居中。</summary>
        private void SnapView(float az, float el)
        {
            _az = az; _el = el;
            if (_settings != null && _settings.AutoFitView && !IsPreviewImageMesh(_mesh))
            {
                var (px, py) = AutoFitPan(_mesh, _dist);
                _panX = px; _panY = py;
            }
            ScheduleRender(highQuality: true, force: false);
        }

        private static float DistToSeg(Vector2 p, Vector2 a, Vector2 b)
        {
            var ab = b - a; var ap = p - a;
            var len2 = ab.LengthSquared();
            if (len2 < 1e-12f) return Vector2.Distance(p, a);
            var t = Math.Clamp(Vector2.Dot(ap, ab) / len2, 0f, 1f);
            return Vector2.Distance(p, a + ab * t);
        }

        /// <summary>点在凸四边形内（射线法，任意绕向均适用）。</summary>
        private static bool PtInQuad(Vector2 p, Vector2[] q)
        {
            bool inside = false;
            for (int i = 0, j = q.Length - 1; i < q.Length; j = i++)
            {
                var a = q[i]; var b = q[j];
                if (((a.Y > p.Y) != (b.Y > p.Y)) &&
                    (p.X < (b.X - a.X) * (p.Y - a.Y) / (b.Y - a.Y) + a.X))
                    inside = !inside;
            }
            return inside;
        }

        /// <summary>按设置路径加载/缓存背景图片（路径未变则复用，文件缺失/损坏返回 null）。</summary>
        private void EnsureBackgroundImage()
        {
            var path = _settings?.BackgroundImagePath;
            if (path == _bgImagePath) return;
            _bgImage?.Dispose();
            _bgImage = null;
            _bgImagePath = path;
            if (!string.IsNullOrEmpty(path) && System.IO.File.Exists(path))
            {
                try { _bgImage = new Bitmap(path); }
                catch { _bgImage = null; }
            }
        }

        // ================= 渲染调度 =================

        private sealed class RenderRequest
        {
            public int Version;
            public MeshData Mesh;
            public float Az, El, Dist, PanX, PanY, Zoom;
            public bool HighQuality;
        }

        private void ScheduleRender(bool highQuality, bool force)
        {
            var m = _mesh;
            if (m == null || _disposed || Width < 8 || Height < 8) return;

            // 平移换算：渲染器把 PanX/PanY 视为"渲染像素"，而交互累计的是控件像素；
            // 交互低清帧与静止高清帧渲染尺寸不同（InteractScale vs ResolutionScale*Ssaa），
            // 若直接透传，同一平移量在两种分辨率下的视觉偏移不同 → 旋转/拖动时零件跳位。
            // 统一换算为"占控件比例 × 本次渲染尺寸"，使视觉平移跨分辨率一致。
            var s = _settings;
            var scale = highQuality ? s.ResolutionScale * s.Ssaa : s.InteractScale;
            var rw = Math.Min(Math.Max((int)(Width * scale), 64), 4096);
            var rh = Math.Min(Math.Max((int)(Height * scale), 64), 4096);

            var req = new RenderRequest
            {
                Version = ++_version,
                Mesh = m,
                Az = _az, El = _el, Dist = _dist,
                PanX = _panX * ((float)rw / Math.Max(Width, 1)),
                PanY = _panY * ((float)rh / Math.Max(Height, 1)),
                Zoom = _zoom,
                HighQuality = highQuality
            };

            // 原子化占用渲染槽：仅当无渲染任务在跑时才真正启动；否则合并为待渲染，
            // 由当前任务完成后的收尾流程统一补渲（不丢最新请求，也不产生并发渲染）。
            RenderRequest start;
            lock (_renderLock)
            {
                _lastRequest = req;
                if (_rendering)
                {
                    _pending = true;
                    start = null;
                }
                else
                {
                    _rendering = true;
                    start = req;
                }
            }
            if (start != null)
                StartRenderTask(start);
        }

        private void StartRenderTask(RenderRequest req)
        {
            // 后台线程渲染（渲染后端共享同一 DX11 device/context，故必须串行，见 _renderLock 说明）
            Task.Run(() => RenderWorker(req));
        }

        private void RenderWorker(RenderRequest req)
        {
            try
            {
                var s = _settings;
                // 线段云（G-code）GPU 后端不支持，强制 CPU
                IRenderBackend backend;
                string backendName;
                if (req.Mesh.IsLineCloud)
                {
                    backend = _cpuBackend ??= new CpuRenderBackend();
                    backendName = "CPU (lines)";
                }
                else
                {
                    backend = ResolveBackend(s);
                    backendName = _backendName;
                }
                // 三角形上限：GPU 后端绘制能力强，LOD 抽稀过度会把大模型抽成不相接的散点（"沙粒/点云"感），
                // 因此 GPU 模式下大幅放宽上限（画全量或到 800 万面）；仅 CPU 低画质才严格抽稀。
                int maxTriangles = s.MaxTriangles;
                if (backendName.Contains("GPU") && maxTriangles > 0 && maxTriangles < 8_000_000)
                    maxTriangles = 8_000_000;
                var overrideColor = s.ParseModelColor();
                var shadowLevel = s.ShadowLevel;
                var smoothShading = s.SmoothShading;
                var showNegative = s.ShowNegativeParts;
                var negativeColor = s.ParseNegativeColor();
                var negativeOpacity = s.NegativeOpacity;
                // 修改器（modifier_part）：与负零件同机制、颜色/不透明度独立可调
                var showModifier = s.ShowModifierParts;
                var modifierColor = s.ParseModifierColor();
                var modifierOpacity = s.ModifierOpacity;
                // 背景：图片模式 → 渲染透明背景（由 OnPaint 把图片合成在帧下）；否则填充背景色
                // （自定义背景颜色优先；未设置则跟随界面主题：亮色=白底，暗色=深底，保证画布与界面统一）
                var useBgImage = !string.IsNullOrEmpty(s.BackgroundImagePath) && System.IO.File.Exists(s.BackgroundImagePath);
                var backgroundColor = useBgImage ? (System.Numerics.Vector4?)null
                    : (s.ParseBackgroundColor() ?? ThemeCanvasVector(s.UiTheme));

                // 渲染尺寸：静止 = 控件尺寸 × 分辨率缩放 × 超采样；交互 = 控件尺寸 × 交互缩放
                var scale = req.HighQuality ? s.ResolutionScale * s.Ssaa : s.InteractScale;
                var rw = Math.Min(Math.Max((int)(Width * scale), 64), 4096);
                var rh = Math.Min(Math.Max((int)(Height * scale), 64), 4096);

                RenderResult res = null;
                long ms = 0;
                try
                {
                    var sw = Stopwatch.StartNew();
                    res = backend.Render(req.Mesh, rw, rh,
                        new CameraParams(req.Az, req.El, req.Dist, req.PanX, req.PanY, req.Zoom),
                        maxTriangles, overrideColor, shadowLevel, smoothShading,
                        showNegative, negativeColor, negativeOpacity, backgroundColor,
                        s.ShowFloorGrid, s.Illumination, showModifier, modifierColor, modifierOpacity);
                    sw.Stop();
                    ms = sw.ElapsedMilliseconds;
                }
                catch
                {
                    res = null;
                }

                if (res != null && res.Bgra != null && res.Bgra.Length > 0)
                {
                    var bmp = new Bitmap(res.Width, res.Height, PixelFormat.Format32bppArgb);
                    var rect = new Rectangle(0, 0, res.Width, res.Height);
                    var data = bmp.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
                    Marshal.Copy(res.Bgra, 0, data.Scan0, res.Bgra.Length);
                    bmp.UnlockBits(data);

                    if (_disposed || !IsHandleCreated)
                    {
                        bmp.Dispose();
                    }
                    else
                    {
                        // BeginInvoke 在控件句柄销毁瞬间可能抛异常：单独捕获，绝不外泄导致渲染槽未释放
                        try
                        {
                            BeginInvoke(new Action(() =>
                            {
                                if (_disposed || req.Version != _version) { bmp.Dispose(); return; }
                                _frame?.Dispose();
                                _frame = bmp;
                                Invalidate();
                                RenderCompleted?.Invoke(ms, backendName);
                            }));
                        }
                        catch
                        {
                            bmp.Dispose();
                        }
                    }
                }
            }
            catch
            {
                // 渲染整体兜底：绝不让后台任务崩溃导致渲染槽泄漏
            }

            // 收尾：原子释放/抢占渲染槽（此段在 try/catch 之后，绝不会因 return 跳过）。
            // 若收尾期间积累了待补渲请求，抢占渲染槽并交给其继续，杜绝并发渲染与新请求漏醒。
            RenderRequest next;
            lock (_renderLock)
            {
                _rendering = false;
                if (_pending)
                {
                    _pending = false;
                    next = _lastRequest;
                    _rendering = true;
                }
                else
                {
                    next = null;
                }
            }
            if (next == null || _disposed) return;
            if (IsHandleCreated)
            {
                try { BeginInvoke(new Action(() => StartRenderTask(next))); return; }
                catch { }
            }
            StartRenderTask(next);
        }

        /// <summary>按设置选择后端：Auto=GPU优先（失败回退CPU），Gpu=强制GPU，Cpu=强制CPU。后台线程调用。</summary>
        private IRenderBackend ResolveBackend(RenderSettings s)
        {
            if (!_backendInvalid && _backend != null) return _backend;
            lock (this)
            {
                if (!_backendInvalid && _backend != null) return _backend;
                _backend?.Dispose();
                _backend = null;

                if (s.Mode == RendererMode.Gpu || s.Mode == RendererMode.Auto)
                {
                    try
                    {
                        var gpu = new D3D11RendererBackend(s.GpuAdapterName);
                        if (gpu.IsAvailable)
                        {
                            _backend = gpu;
                            _backendName = string.IsNullOrEmpty(gpu.AdapterName) ? "DX11 GPU" : ("DX11 GPU · " + gpu.AdapterName);
                        }
                        else
                        {
                            gpu.Dispose();
                        }
                    }
                    catch
                    {
                        // GPU 后端创建失败：回退 CPU
                    }
                }
                if (_backend == null)
                {
                    _backend = new CpuRenderBackend();
                    _backendName = "CPU";
                }
                _backendInvalid = false;
                return _backend;
            }
        }

        protected override void Dispose(bool disposing)
        {
            _disposed = true;
            if (disposing)
            {
                Application.RemoveMessageFilter(this);
                _frame?.Dispose();
                _frame = null;
                if (!_rendering)
                {
                    _backend?.Dispose();
                    _backend = null;
                    _cpuBackend?.Dispose();
                    _cpuBackend = null;
                }
            }
            base.Dispose(disposing);
        }
    }
}
