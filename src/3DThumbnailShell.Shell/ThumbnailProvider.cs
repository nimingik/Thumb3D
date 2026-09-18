using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using _3DThumbnailShell.Core.Formats;
using _3DThumbnailShell.Core.Renderer;
using _3DThumbnailShell.Shell.Interop;

namespace _3DThumbnailShell.Shell
{
    /// <summary>
    /// COM 缩略图提供者：资源管理器注入本进程后，对 .stl/.obj/.3mf 等文件
    /// 调用 InitializeWithFile 传入路径，再 GetThumbnail 返回带 alpha 的 HBITMAP。
    /// 全程 try/catch，任何失败返回 E_NOTIMPL，绝不让 explorer 崩溃。
    /// </summary>
    [ComVisible(true)]
    [Guid("6E7A4B2C-8D5F-4A3E-9B1C-2D4F6A8E0C1D")]
    [ClassInterface(ClassInterfaceType.None)]
    public sealed class ThumbnailProvider : IThumbnailProvider, IInitializeWithFile, IInitializeWithStream
    {
        // HRESULT
        private const int S_OK = 0;
        private const int E_FAIL = unchecked((int)0x80004005);
        private const int E_NOTIMPL = unchecked((int)0x80004001);
        private const int E_OUTOFMEMORY = unchecked((int)0x8007000E);

        private string _path;          // IInitializeWithFile 传入的路径
        private byte[] _streamBytes;   // IInitializeWithStream 读入的文件内容

        // 自定义缩略图目录：%LOCALAPPDATA%\3DThumbnailShell\custom-thumbs\<源文件SHA256>.png
        // 由预览器"自定义缩略图"功能写入；流初始化（无路径）时按内容哈希命中。
        private static readonly string CustomThumbDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "3DThumbnailShell", "custom-thumbs");

        // 诊断日志：记录 explorer 是否真的调用了本组件、每步结果与异常。
        // 路径: %LOCALAPPDATA%\3DThumbnailShell\provider.log（explorer 以当前用户运行，可写）
        private static readonly string LogPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "3DThumbnailShell", "provider.log");

        private static void Log(string msg)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogPath));
                File.AppendAllText(LogPath,
                    DateTime.Now.ToString("HH:mm:ss.fff") + " [" + System.Diagnostics.Process.GetCurrentProcess().Id + "/" +
                    (Environment.Is64BitProcess ? "x64" : "x86") + "] " + msg + "\r\n");
            }
            catch { }
        }

        static ThumbnailProvider()
        {
            Log("=== 组件已加载（静态构造） ===");
        }

        public int Initialize(string pszFilePath, uint grfMode)
        {
            _path = pszFilePath;
            _streamBytes = null;
            Log("Initialize: " + pszFilePath + " (grfMode=0x" + grfMode.ToString("X") + ")");
            return S_OK;
        }

        /// <summary>流式初始化：把 IStream 内容完整读入内存，供 GetThumbnail 解析。</summary>
        public int Initialize(System.Runtime.InteropServices.ComTypes.IStream pstream, uint grfMode)
        {
            try
            {
                var ms = new MemoryStream();
                var buf = new byte[65536];
                // IStream.Read 的 pcbRead 是 ULONG* 输出参数，必须指向可写内存，不能传 NULL
                IntPtr pRead = Marshal.AllocHGlobal(4);
                try
                {
                    while (true)
                    {
                        Marshal.WriteInt32(pRead, 0);
                        pstream.Read(buf, buf.Length, pRead);
                        int n = Marshal.ReadInt32(pRead);
                        if (n <= 0) break;
                        ms.Write(buf, 0, n);
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(pRead);
                }
                _streamBytes = ms.ToArray();
                _path = null;
                Log("Initialize(Stream): " + _streamBytes.Length + " 字节");
                return S_OK;
            }
            catch (Exception ex)
            {
                Log("Initialize(Stream) 异常: " + ex.GetType().Name + ": " + ex.Message);
                return E_FAIL;
            }
        }

        /// <summary>按内容嗅探 3D 文件扩展名（流初始化时没有原始文件名）。</summary>
        private static string SniffExtension(byte[] d)
        {
            if (d == null || d.Length < 4) return ".bin";
            // ZIP 容器 -> 3MF
            if (d[0] == 0x50 && d[1] == 0x4B) return ".3mf";
            // CTB 魔数: 0x12FD0019 (LE)
            if (d.Length >= 4 && d[0] == 0x19 && d[1] == 0x00 && d[2] == 0xFD && d[3] == 0x12) return ".ctb";
            // glTF 二进制 magic: 'g' 'l' 'T' 'F'
            if (d[0] == 0x67 && d[1] == 0x6C && d[2] == 0x54 && d[3] == 0x46) return ".glb";
            // ASCII 魔数（去掉空白/BOM 后看头）
            var head = System.Text.Encoding.ASCII.GetString(d, 0, Math.Min(d.Length, 32))
                .TrimStart(' ', '\t', '\r', '\n', '\uFEFF', '\0');
            if (head.StartsWith("solid", StringComparison.OrdinalIgnoreCase)) return ".stl";
            if (head.StartsWith("ply", StringComparison.OrdinalIgnoreCase)) return ".ply";
            if (head.StartsWith("OFF", StringComparison.OrdinalIgnoreCase)) return ".off";
            if (head.StartsWith("ISO-10303-21", StringComparison.OrdinalIgnoreCase)) return ".step";
            if (head.StartsWith("<?xml") || head.StartsWith("<amf", StringComparison.OrdinalIgnoreCase)) return ".amf";
            if (head.StartsWith("glTF")) return ".gltf";
            if (head.StartsWith("{")) return ".gltf";
            // G-code：以 ; 注释 或 G0/G1/G90/M 等命令开头
            if (head[0] == ';' || head.StartsWith("G0") || head.StartsWith("G1") ||
                head.StartsWith("G9", StringComparison.OrdinalIgnoreCase) ||
                head.StartsWith("M1", StringComparison.OrdinalIgnoreCase) ||
                head.StartsWith("M8", StringComparison.OrdinalIgnoreCase) ||
                head.StartsWith("M10", StringComparison.OrdinalIgnoreCase)) return ".gcode";
            // OBJ 文本通常以顶点/面关键字开头
            if (head[0] == 'v' || head[0] == 'f' || head[0] == 'o' || head[0] == 'g') return ".obj";
            return ".bin";
        }

        /// <summary>
        /// 尝试加载自定义缩略图：
        ///  File 初始化 → 侧车 <源路径>.png；Stream 初始化 → 按内容 SHA256 命中 custom-thumbs 目录。
        ///  找到则等比缩放并居中绘制到 size×size 透明画布，转带 alpha 的 HBITMAP。
        /// </summary>
        private bool TryLoadCustomThumbnail(int size, out IntPtr phbmp, out WTS_ALPHATYPE palpha)
        {
            phbmp = IntPtr.Zero;
            palpha = WTS_ALPHATYPE.WTSAT_UNKNOWN;
            string imgPath = null;
            try
            {
                if (!string.IsNullOrEmpty(_path))
                {
                    imgPath = _path + ".png";
                    if (!File.Exists(imgPath)) return false;
                }
                else if (_streamBytes != null && _streamBytes.Length > 0)
                {
                    string hash;
                    using (var sha = SHA256.Create())
                        hash = BitConverter.ToString(sha.ComputeHash(_streamBytes)).Replace("-", "").ToLowerInvariant();
                    imgPath = Path.Combine(CustomThumbDir, hash + ".png");
                    if (!File.Exists(imgPath)) return false;
                }
                else return false;

                Log("  自定义缩略图命中: " + imgPath);
                using (var src = new Bitmap(imgPath))
                {
                    if (src.Width <= 0 || src.Height <= 0) return false;
                    var ratio = Math.Min((float)size / src.Width, (float)size / src.Height);
                    var tw = Math.Max(1, (int)(src.Width * ratio));
                    var th = Math.Max(1, (int)(src.Height * ratio));
                    using (var canvas = new Bitmap(size, size, PixelFormat.Format32bppArgb))
                    {
                        using (var g = Graphics.FromImage(canvas))
                        {
                            g.Clear(Color.Transparent);
                            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                            g.SmoothingMode = SmoothingMode.HighQuality;
                            g.DrawImage(src, (size - tw) / 2, (size - th) / 2, tw, th);
                        }
                        var rect = new Rectangle(0, 0, size, size);
                        var data = canvas.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                        try
                        {
                            var bgra = new byte[size * size * 4];
                            Marshal.Copy(data.Scan0, bgra, 0, bgra.Length);
                            phbmp = HbitmapFromBgra.Create(size, size, bgra);
                        }
                        finally
                        {
                            canvas.UnlockBits(data);
                        }
                        if (phbmp == IntPtr.Zero) return false;
                        palpha = WTS_ALPHATYPE.WTSAT_ARGB;
                        return true;
                    }
                }
            }
            catch (Exception ex)
            {
                Log("  自定义缩略图加载失败(回退渲染): " + ex.GetType().Name + ": " + ex.Message);
                phbmp = IntPtr.Zero;
                palpha = WTS_ALPHATYPE.WTSAT_UNKNOWN;
                return false;
            }
        }

        /// <summary>双线性缩放 BGRA 图像到目标尺寸（纯代码，无 GDI+ 依赖）。</summary>
        private static byte[] ScaleImage(byte[] src, int sw, int sh, int dw, int dh)
        {
            if (sw == dw && sh == dh) return src;
            var dst = new byte[dw * dh * 4];
            float xRatio = (float)sw / dw;
            float yRatio = (float)sh / dh;
            for (int dy = 0; dy < dh; dy++)
            {
                float sy = (dy + 0.5f) * yRatio - 0.5f;
                int y0 = (int)Math.Floor(sy);
                float yw = sy - y0;
                int y1 = y0 + 1;
                if (y0 < 0) { y0 = 0; yw = 0; }
                if (y1 >= sh) y1 = sh - 1;
                for (int dx = 0; dx < dw; dx++)
                {
                    float sx = (dx + 0.5f) * xRatio - 0.5f;
                    int x0 = (int)Math.Floor(sx);
                    float xw = sx - x0;
                    int x1 = x0 + 1;
                    if (x0 < 0) { x0 = 0; xw = 0; }
                    if (x1 >= sw) x1 = sw - 1;
                    int i00 = (y0 * sw + x0) * 4;
                    int i01 = (y0 * sw + x1) * 4;
                    int i10 = (y1 * sw + x0) * 4;
                    int i11 = (y1 * sw + x1) * 4;
                    int o = (dy * dw + dx) * 4;
                    for (int c = 0; c < 4; c++)
                    {
                        float v00 = src[i00 + c], v01 = src[i01 + c], v10 = src[i10 + c], v11 = src[i11 + c];
                        float top = v00 * (1 - xw) + v01 * xw;
                        float bot = v10 * (1 - xw) + v11 * xw;
                        dst[o + c] = (byte)(top * (1 - yw) + bot * yw);
                    }
                }
            }
            return dst;
        }

        /// <summary>已知但暂无解析器的 CAD/切片格式列表（生成占位缩略图）。</summary>
        private static readonly HashSet<string> CadOrSliceExts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".step", ".stp", ".c4d", ".sldprt", ".sldasm", ".ipt", ".iam", ".f3d", ".blend",
            ".photon", ".pwmx", ".pwmo", ".pws", ".form"
        };

        private static bool IsKnownCadOrSliceFormat(string ext)
        {
            return !string.IsNullOrEmpty(ext) && CadOrSliceExts.Contains(ext);
        }

        /// <summary>
        /// 为无法解析的 CAD/切片格式生成占位缩略图：渐变背景 + 扩展名文本。
        /// 用 System.Drawing 绘制后转 BGRA 字节。
        /// </summary>
        private static byte[] GeneratePlaceholder(string ext, int size)
        {
            var bgra = new byte[size * size * 4];
            try
            {
                using (var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb))
                using (var g = Graphics.FromImage(bmp))
                {
                    // 按格式类别选背景色
                    Color bg1, bg2;
                    if (ext == ".step" || ext == ".stp") { bg1 = Color.FromArgb(40, 60, 120); bg2 = Color.FromArgb(20, 30, 70); }
                    else if (ext == ".sldprt" || ext == ".sldasm") { bg1 = Color.FromArgb(120, 40, 40); bg2 = Color.FromArgb(70, 20, 20); }
                    else if (ext == ".ipt" || ext == ".iam") { bg1 = Color.FromArgb(40, 100, 60); bg2 = Color.FromArgb(20, 60, 35); }
                    else if (ext == ".f3d") { bg1 = Color.FromArgb(200, 100, 30); bg2 = Color.FromArgb(120, 60, 15); }
                    else if (ext == ".c4d") { bg1 = Color.FromArgb(30, 80, 160); bg2 = Color.FromArgb(15, 40, 90); }
                    else if (ext == ".blend") { bg1 = Color.FromArgb(60, 90, 160); bg2 = Color.FromArgb(30, 50, 100); }
                    else { bg1 = Color.FromArgb(60, 60, 70); bg2 = Color.FromArgb(30, 30, 40); } // 切片等

                    using (var brush = new LinearGradientBrush(new Rectangle(0, 0, size, size), bg1, bg2, LinearGradientMode.Vertical))
                        g.FillRectangle(brush, 0, 0, size, size);

                    // 居中显示扩展名（去掉点，大写）
                    var label = ext.TrimStart('.').ToUpperInvariant();
                    var fontSize = Math.Max(10f, size * 0.22f);
                    using (var font = new Font("Segoe UI", fontSize, FontStyle.Bold))
                    {
                        var textSize = g.MeasureString(label, font);
                        var tx = (size - textSize.Width) / 2f;
                        var ty = (size - textSize.Height) / 2f;
                        using (var shadow = new SolidBrush(Color.FromArgb(80, 0, 0, 0)))
                            g.DrawString(label, font, shadow, tx + 2, ty + 2);
                        using (var fg = new SolidBrush(Color.White))
                            g.DrawString(label, font, fg, tx, ty);
                    }
                    // Bitmap -> BGRA（在 using 块内，bmp 仍存活）
                    var rect = new Rectangle(0, 0, size, size);
                    var data = bmp.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                    try { Marshal.Copy(data.Scan0, bgra, 0, bgra.Length); }
                    finally { bmp.UnlockBits(data); }
                }
            }
            catch
            {
                // 失败：返回全透明
            }
            return bgra;
        }

        public int GetThumbnail(uint cx, out IntPtr phbmp, out WTS_ALPHATYPE palpha)
        {
            phbmp = IntPtr.Zero;
            palpha = WTS_ALPHATYPE.WTSAT_UNKNOWN;
            Log("GetThumbnail: cx=" + cx + " path=" + (_path ?? "(null)"));

            try
            {
                if (string.IsNullOrEmpty(_path) && (_streamBytes == null || _streamBytes.Length == 0))
                {
                    Log("  失败: 路径为空且无流数据");
                    return E_FAIL;
                }

                // 请求尺寸通常 32~256；夹取范围避免异常请求导致卡顿
                var size = (int)Math.Min(Math.Max(cx, 32u), 512u);

                // 步骤0: 自定义缩略图优先（侧车 <path>.png 或 custom-thumbs\<hash>.png）
                if (TryLoadCustomThumbnail(size, out phbmp, out palpha))
                {
                    Log("  成功(自定义缩略图): " + size + "x" + size + " bmp=0x" + phbmp.ToString("X"));
                    return S_OK;
                }

                Log("  步骤1: 解析开始");
                string knownExt = Path.GetExtension(_path ?? "").ToLowerInvariant();
                MeshData mesh;
                if (!string.IsNullOrEmpty(_path))
                {
                    mesh = MeshParserFactory.ParseFile(_path);
                }
                else if (_streamBytes != null && _streamBytes.Length > 0)
                {
                    // 流初始化没有文件名：按内容嗅探扩展名，落到临时文件后走统一解析
                    knownExt = SniffExtension(_streamBytes);
                    var tmp = Path.Combine(Path.GetTempPath(),
                        "3dthumb_" + Guid.NewGuid().ToString("N") + knownExt);
                    try
                    {
                        File.WriteAllBytes(tmp, _streamBytes);
                        mesh = MeshParserFactory.ParseFile(tmp);
                    }
                    finally
                    {
                        try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
                    }
                }
                else
                {
                    Log("  失败: 无路径也无流数据");
                    return E_FAIL;
                }
                Log("  步骤2: 解析完成 顶点=" + (mesh == null || mesh.Vertices == null ? -1 : mesh.Vertices.Count));
                if (mesh == null)
                {
                    // CAD 专有格式（.step/.sldprt/.ipt/.f3d/.c4d/.blend 等）暂无解析器，
                    // 生成带扩展名的占位缩略图，避免只显示默认文件图标
                    if (IsKnownCadOrSliceFormat(knownExt))
                    {
                        Log("  步骤2c: 生成占位缩略图 ext=" + knownExt);
                        var placeholder = GeneratePlaceholder(knownExt, size);
                        phbmp = HbitmapFromBgra.Create(size, size, placeholder);
                        if (phbmp != IntPtr.Zero)
                        {
                            palpha = WTS_ALPHATYPE.WTSAT_ARGB;
                            Log("  成功(占位图): " + size + "x" + size + " bmp=0x" + phbmp.ToString("X"));
                            return S_OK;
                        }
                    }
                    Log("  失败: 解析无结果");
                    return E_NOTIMPL;
                }

                // 切片文件（CTB/PHOTON 等）直接返回嵌入预览图，跳过 3D 渲染
                if (mesh.PreviewBgra != null && mesh.PreviewWidth > 0 && mesh.PreviewHeight > 0)
                {
                    Log("  步骤2b: 使用嵌入预览图 " + mesh.PreviewWidth + "x" + mesh.PreviewHeight);
                    var thumbBgra = ScaleImage(mesh.PreviewBgra, mesh.PreviewWidth, mesh.PreviewHeight, size, size);
                    Log("  步骤5: 创建HBITMAP " + size + "x" + size);
                    phbmp = HbitmapFromBgra.Create(size, size, thumbBgra);
                    if (phbmp == IntPtr.Zero)
                    {
                        Log("  失败: HBITMAP 创建失败");
                        return E_OUTOFMEMORY;
                    }
                    palpha = WTS_ALPHATYPE.WTSAT_ARGB;
                    Log("  成功(切片预览图): " + size + "x" + size + " bmp=0x" + phbmp.ToString("X"));
                    return S_OK;
                }

                if (mesh.Vertices == null || mesh.Vertices.Count == 0)
                {
                    Log("  失败: 解析无顶点");
                    return E_NOTIMPL;
                }

                // 3MF 规范 Z-up → 渲染 Y-up（与预览器一致），保证缩略图朝向与预览器/资源管理器一致
                for (var i = 0; i < mesh.Vertices.Count; i++)
                {
                    var v = mesh.Vertices[i];
                    mesh.Vertices[i] = new System.Numerics.Vector3(v.X, v.Z, -v.Y);
                }

                Log("  步骤3: 渲染开始 size=" + size);
                // 固定保守 LOD 上限（Shell 无预览器设置上下文）：超大模型（百万+面）等距抽稀到 30 万
                // 面再逐三角形光栅化，几秒内出图，避免 explorer 缩略图长时间不显示。
                const int thumbMaxTriangles = 300000;
                var res = new SoftwareRenderer().Render(mesh, size, thumbMaxTriangles);
                Log("  步骤4: 渲染完成 " + (res == null || res.Bgra == null ? -1 : res.Bgra.Length));
                if (res == null || res.Bgra == null || res.Bgra.Length == 0)
                {
                    Log("  失败: 渲染无输出");
                    return E_NOTIMPL;
                }

                Log("  步骤5: 创建HBITMAP " + res.Width + "x" + res.Height);
                phbmp = HbitmapFromBgra.Create(res.Width, res.Height, res.Bgra);
                Log("  步骤6: HBITMAP完成");
                if (phbmp == IntPtr.Zero)
                {
                    Log("  失败: HBITMAP 创建失败");
                    return E_OUTOFMEMORY;
                }

                palpha = WTS_ALPHATYPE.WTSAT_ARGB;
                Log("  成功: " + res.Width + "x" + res.Height + " bmp=0x" + phbmp.ToString("X"));
                return S_OK;
            }
            catch (Exception ex)
            {
                Log("  异常: " + ex.GetType().Name + ": " + ex.Message);
                phbmp = IntPtr.Zero;
                palpha = WTS_ALPHATYPE.WTSAT_UNKNOWN;
                return E_NOTIMPL;
            }
        }
    }
}
