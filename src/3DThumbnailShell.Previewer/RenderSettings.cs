using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace _3DThumbnailShell.Previewer
{
    /// <summary>画质预设：从低到高（对 CPU/GPU 后端都生效）。</summary>
    public enum QualityPreset
    {
        Low = 0,
        Medium = 1,
        High = 2,
        Ultra = 3
    }

    /// <summary>渲染后端选择：Auto=优先 GPU（不可用自动回退 CPU），Gpu=强制 GPU，Cpu=强制 CPU。</summary>
    public enum RendererMode
    {
        Auto = 0,
        Gpu = 1,
        Cpu = 2
    }

    /// <summary>
    /// 预览器渲染设置（类游戏画质项）。
    /// 分辨率缩放 / 超采样 / 交互缩放 / 三角形上限，均对 CPU、GPU 后端生效。
    /// 持久化到 %LOCALAPPDATA%\3DThumbnailShell\previewer-settings.json。
    /// </summary>
    public sealed class RenderSettings
    {
        /// <summary>渲染分辨率相对控件尺寸的比例：1 / 0.75 / 0.5 / 0.25。</summary>
        public float ResolutionScale { get; set; } = 1f;

        /// <summary>静止/松手后的超采样倍数：4 / 2 / 1（4x 为高画质、最吃性能）。</summary>
        public int Ssaa { get; set; } = 2;

        /// <summary>交互拖动过程中的分辨率缩放：1 / 0.5 / 0.33 / 0.25（越小越流畅）。</summary>
        public float InteractScale { get; set; } = 1f;

        /// <summary>渲染三角形数量上限：0=不限制；其余按网格总面数等距抽稀到该上限（LOD）。</summary>
        public int MaxTriangles { get; set; } = 0;

        /// <summary>渲染后端：Auto / Gpu / Cpu。</summary>
        public RendererMode Mode { get; set; } = RendererMode.Auto;

        /// <summary>
        /// 指定使用的 GPU 适配器（DXGI 枚举名，如 "NVIDIA GeForce RTX 3060"）。
        /// null/空 = 自动选择默认适配器（通常是第一块，可能为核显）。
        /// </summary>
        public string GpuAdapterName { get; set; }

        /// <summary>
        /// 自定义模型颜色（"#RRGGBB"，null/空 = 使用模型自身颜色）。
        /// 与阴影强度一起作用于预览渲染与批量缩略图。
        /// </summary>
        public string ModelColorHex { get; set; }

        /// <summary>阴影强度 0..1：0=无阴影（纯平光），1=标准阴影。</summary>
        public float ShadowLevel { get; set; } = 1f;

        /// <summary>
        /// 照明（补光） 0..1：0=关闭（默认，旧观感）。&gt;0 时抬升材质基色并提高环境光，
        /// 用于"黑色/深色模型看不清"的场合（纯黑耗材的模型基色为 0，只加光也乘不出亮度）。
        /// 由主界面"照明"按钮切换，作用于预览渲染与"保存缩略图"。
        /// </summary>
        public float Illumination { get; set; } = 0f;

        /// <summary>
        /// 平滑着色程度 0..1：0=平涂锐利（每面统一颜色与光强，多色区域边界干脆），
        /// 1=全平滑 Gouraud（当前旧观感，棱角被抹圆）。默认 0.3（接近拓竹切片软件平涂观感）。
        /// </summary>
        public float SmoothShading { get; set; } = 0.3f;

        /// <summary>当前选择的预设（仅用于界面显示；实际生效值以各字段为准）。</summary>
        [JsonIgnore]
        public QualityPreset Preset { get; set; } = QualityPreset.High;

        /// <summary>最近一次打开/扫描的文件夹（启动时用于自动填充左侧文件浏览）。</summary>
        public string LastFolder { get; set; }

        /// <summary>诊断日志开关：开启后预览器关键操作写入 %LOCALAPPDATA%\3DThumbnailShell\previewer.log（排查 bug 用）。</summary>
        public bool DiagnosticsLogEnabled { get; set; } = true;

        /// <summary>是否显示负零件（3MF 镂空/支撑/布尔裁剪体等）。false = 完全隐藏。</summary>
        public bool ShowNegativeParts { get; set; } = true;

        /// <summary>
        /// 负零件自定义颜色（"#RRGGBB"，null/空 = 使用模型自身颜色）。
        /// 对所有含负零件的模型统一生效（与模型颜色一样在批量缩略图中也生效）。
        /// </summary>
        public string NegativeColorHex { get; set; }

        /// <summary>负零件不透明度 0..1：0=全透明，1=不透明实体。默认 0.25（近似原 2x2 点阵 25% 观感）。</summary>
        public float NegativeOpacity { get; set; } = 0.25f;

        /// <summary>是否显示修改器（Bambu/Orca 工程的 modifier_part：只改打印参数、不是实体）。false = 完全隐藏。</summary>
        public bool ShowModifierParts { get; set; } = true;

        /// <summary>
        /// 修改器自定义颜色（"#RRGGBB"，null/空 = 使用模型自身颜色）。
        /// 默认暖橙，便于与负零件（默认淡蓝）区分；对所有含修改器的模型统一生效。
        /// </summary>
        public string ModifierColorHex { get; set; } = "#FFC46B";

        /// <summary>修改器不透明度 0..1：0=全透明，1=不透明实体。默认 0.25（与负零件一致）。</summary>
        public float ModifierOpacity { get; set; } = 0.25f;

        /// <summary>
        /// 3D 显示背景颜色（"#RRGGBB"，null = 跟随界面主题默认画布：亮色=白，暗色=深）。
        /// 背景图片设置后优先于背景颜色。作用于预览显示（GPU/CPU 后端一致）。
        /// </summary>
        public string BackgroundColorHex { get; set; }

        /// <summary>缩略图保存比例："1:1"（默认）/ "4:3" / "16:9"。透明底、按该比例输出，消除白边。</summary>
        public string ThumbnailAspect { get; set; } = "1:1";

        /// <summary>3D 显示背景图片路径（null = 无）。设置后优先于背景颜色，按控件区域拉伸显示。</summary>
        public string BackgroundImagePath { get; set; }

        /// <summary>启动软件时是否自动弹出开始介绍弹窗（默认 true）。</summary>
        public bool ShowWelcome { get; set; } = true;

        /// <summary>首次启动是否自动弹出"更新内容"弹窗（默认 true，弹窗内勾选"下次不再显示"后置 false）。</summary>
        public bool ShowUpdateNotes { get; set; } = true;

        /// <summary>界面主题：亮色（档案暖白）/ 暗色（档案深色）。默认亮色，对所有窗口生效。</summary>
        public UiThemeMode UiTheme { get; set; } = UiThemeMode.Light;

        /// <summary>
        /// 自动取景：载入模型/复位视图时按包围盒自动调整相机距离，使模型填满画面约 85%
        /// （保持默认 45° 俯视视角，仅改变远近）。关闭时维持原固定相机距离（缩略图观感）。
        /// </summary>
        public bool AutoFitView { get; set; } = true;

        /// <summary>
        /// 显示地板网格：在模型底面绘制 Blender 式参考网格（亮底深灰线/暗底浅灰线），
        /// 帮助感知 3D 朝向与地面位置。CPU/GPU 后端一致，默认开启。</summary>
        public bool ShowFloorGrid { get; set; } = true;

        /// <summary>Gizmo 尺寸缩放系数：1 = 默认中等。范围 0.6..1.6，随设置调整。</summary>
        public float GizmoScale { get; set; } = 1f;

        /// <summary>界面语言："zh" = 简体中文（默认），"en" = English。</summary>
        public string Language { get; set; } = "zh";

        // ---------- 预设表 ----------

        public static RenderSettings FromPreset(QualityPreset preset)
        {
            var s = new RenderSettings { Preset = preset };
            switch (preset)
            {
                case QualityPreset.Low:
                    s.ResolutionScale = 0.25f; s.Ssaa = 1; s.InteractScale = 0.25f; s.MaxTriangles = 10000;
                    break;
                case QualityPreset.Medium:
                    s.ResolutionScale = 0.5f;  s.Ssaa = 2; s.InteractScale = 0.5f;  s.MaxTriangles = 20000;
                    break;
                case QualityPreset.High:
                    s.ResolutionScale = 0.75f; s.Ssaa = 2; s.InteractScale = 0.75f; s.MaxTriangles = 50000;
                    break;
                case QualityPreset.Ultra:
                default:
                    s.ResolutionScale = 1f;    s.Ssaa = 4; s.InteractScale = 1f;    s.MaxTriangles = 0;
                    break;
            }
            return s;
        }

        /// <summary>按当前字段值反推最接近的预设（用于设置窗口下拉框回显）。</summary>
        public QualityPreset MatchPreset()
        {
            foreach (var p in new[] { QualityPreset.Ultra, QualityPreset.High, QualityPreset.Medium, QualityPreset.Low })
            {
                var s = FromPreset(p);
                if (Equals(s)) return p;
            }
            return Preset;
        }

        public bool Equals(RenderSettings other)
        {
            return other != null
                && ResolutionScale == other.ResolutionScale
                && Ssaa == other.Ssaa
                && InteractScale == other.InteractScale
                && MaxTriangles == other.MaxTriangles;
        }

        // ---------- 持久化 ----------

        private static string SettingsPath
        {
            get
            {
                var dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "3DThumbnailShell");
                return Path.Combine(dir, "previewer-settings.json");
            }
        }

        private static readonly JsonSerializerOptions JsonOpts = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Converters = { new JsonStringEnumConverter() }
        };

        public static RenderSettings Load()
        {
            try
            {
                var path = SettingsPath;
                if (File.Exists(path))
                {
                    var s = JsonSerializer.Deserialize<RenderSettings>(File.ReadAllText(path), JsonOpts);
                    if (s != null)
                    {
                        s.Clamp();
                        s.Preset = s.MatchPreset();
                        return s;
                    }
                }
            }
            catch
            {
                // 配置损坏时回落到默认
            }
            return FromPreset(QualityPreset.High);
        }

        public void Save()
        {
            try
            {
                var path = SettingsPath;
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOpts));
            }
            catch
            {
                // 保存失败不打断预览
            }
        }

        /// <summary>加载 → 就地修改 → 保存（无需手动 Load/Save，用于只改某几个字段）。</summary>
        public static void Patch(Action<RenderSettings> mutate)
        {
            var s = Load();
            mutate?.Invoke(s);
            s.Save();
        }

        /// <summary>防御性限制非法值（如手改 json）。</summary>
        public void Clamp()
        {
            ResolutionScale = Clamp(ResolutionScale, 0.25f, 1f, 1f);
            InteractScale = Clamp(InteractScale, 0.25f, 1f, 1f);
            Ssaa = Ssaa switch { 4 => 4, 2 => 2, _ => 1 };
            MaxTriangles = Math.Max(0, MaxTriangles);
            ShadowLevel = Clamp(ShadowLevel, 0f, 1f, 1f);
            SmoothShading = Clamp(SmoothShading, 0f, 1f, 0.3f);
            NegativeOpacity = Clamp(NegativeOpacity, 0f, 1f, 0.25f);
            ModifierOpacity = Clamp(ModifierOpacity, 0f, 1f, 0.25f);
            if (GizmoScale != -1f) GizmoScale = Clamp(GizmoScale, 0.6f, 1.6f, 1f); // -1 = 关闭 Gizmo
            if (Language != "zh" && Language != "en") Language = "zh";
            if (!string.IsNullOrEmpty(ModelColorHex) && !ModelColorHex.StartsWith("#"))
                ModelColorHex = null;
            if (!string.IsNullOrEmpty(NegativeColorHex) && !NegativeColorHex.StartsWith("#"))
                NegativeColorHex = null;
            if (!string.IsNullOrEmpty(ModifierColorHex) && !ModifierColorHex.StartsWith("#"))
                ModifierColorHex = null;
            if (!string.IsNullOrEmpty(BackgroundColorHex) && !BackgroundColorHex.StartsWith("#"))
                BackgroundColorHex = null;
        }

        /// <summary>解析背景颜色为 Vector4（0..1 RGBA）；未设置/非法返回 null。</summary>
        public System.Numerics.Vector4? ParseBackgroundColor()
        {
            return ParseHexColor(BackgroundColorHex);
        }

        /// <summary>按缩略图保存比例计算 (宽,高)：1:1→512×512，4:3→512×384，16:9→512×288。</summary>
        public (int Width, int Height) ThumbSize(int baseSize = 512)
        {
            switch (ThumbnailAspect)
            {
                case "16:9": return (baseSize, baseSize * 9 / 16);
                case "4:3": return (baseSize, baseSize * 3 / 4);
                default: return (baseSize, baseSize);
            }
        }

        /// <summary>解析自定义模型颜色为 Vector4（0..1 RGBA）；未设置/非法返回 null。</summary>
        public System.Numerics.Vector4? ParseModelColor()
        {
            return ParseHexColor(ModelColorHex);
        }

        /// <summary>解析负零件自定义颜色为 Vector4（0..1 RGBA）；未设置/非法返回 null。</summary>
        public System.Numerics.Vector4? ParseNegativeColor()
        {
            return ParseHexColor(NegativeColorHex);
        }

        /// <summary>解析修改器自定义颜色为 Vector4（0..1 RGBA）；未设置/非法返回 null。</summary>
        public System.Numerics.Vector4? ParseModifierColor()
        {
            return ParseHexColor(ModifierColorHex);
        }

        private static System.Numerics.Vector4? ParseHexColor(string hex)
        {
            if (string.IsNullOrEmpty(hex)) return null;
            try
            {
                var s = hex.StartsWith("#") ? hex.Substring(1) : hex;
                // 支持 #RRGGBB（alpha=1 不透明）与 #RRGGBBAA（末尾两位为 alpha，透明通道）
                if (s.Length != 6 && s.Length != 8) return null;
                var r = Convert.ToInt32(s.Substring(0, 2), 16) / 255f;
                var g = Convert.ToInt32(s.Substring(2, 2), 16) / 255f;
                var b = Convert.ToInt32(s.Substring(4, 2), 16) / 255f;
                var a = s.Length == 8 ? Convert.ToInt32(s.Substring(6, 2), 16) / 255f : 1f;
                return new System.Numerics.Vector4(r, g, b, a);
            }
            catch
            {
                return null;
            }
        }

        private static float Clamp(float v, float min, float max, float fallback)
        {
            if (float.IsNaN(v) || float.IsInfinity(v)) return fallback;
            return Math.Min(max, Math.Max(min, v));
        }
    }
}
