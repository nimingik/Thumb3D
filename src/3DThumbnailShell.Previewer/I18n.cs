using System.Collections.Generic;

namespace _3DThumbnailShell.Previewer
{
    /// <summary>
    /// 简易多语言支持：键 → (简体中文, English)。
    /// 当前语言由 RenderSettings.Language 持久化；调用 SetLanguage 后全局生效。
    /// </summary>
    internal static class I18n
    {
        /// <summary>当前语言："zh" / "en"。</summary>
        public static string Lang = "zh";

        public static bool IsEn => Lang == "en";

        private static readonly Dictionary<string, (string zh, string en)> Tbl = new()
        {
            // ---- 通用 ----
            ["ok"] = ("确定", "OK"),
            ["apply"] = ("应用", "Apply"),
            ["cancel"] = ("取消", "Cancel"),
            ["about"] = ("关于...", "About..."),
            ["settings"] = ("设置", "Settings"),

            // ---- 设置窗口 ----
            ["qualityPreset"] = ("画质预设:", "Quality Preset:"),
            ["renderBackend"] = ("渲染后端:", "Render Backend:"),
            ["gpuAdapter"] = ("GPU 适配器:", "GPU Adapter:"),
            ["renderRes"] = ("渲染分辨率:", "Render Resolution:"),
            ["aa"] = ("抗锯齿:", "Anti-aliasing:"),
            ["interactScale"] = ("交互缩放:", "Interaction Scale:"),
            ["maxTri"] = ("三角形上限:", "Triangle Limit:"),
            ["modelColor"] = ("模型颜色:", "Model Color:"),
            ["shadowLevel"] = ("阴影强度:", "Shadow Level:"),
            ["smoothShading"] = ("平滑着色:", "Smooth Shading:"),
            ["diagLog"] = ("诊断日志:", "Diagnostic Log:"),
            ["negativeParts"] = ("负零件:", "Negative Parts:"),
            ["modifierParts"] = ("修改器:", "Modifiers:"),
            ["bgColor"] = ("背景颜色:", "Background Color:"),
            ["bgImage"] = ("背景图片:", "Background Image:"),
            ["welcomePopup"] = ("启动弹窗:", "Welcome Popup:"),
            ["autoFit"] = ("自动取景:", "Auto-Frame:"),
            ["floorGrid"] = ("地板网格:", "Floor Grid:"),
            ["theme"] = ("界面主题:", "Interface Theme:"),
            ["gizmoSize"] = ("Gizmo 尺寸:", "Gizmo Size:"),
            ["language"] = ("语言 / Language:", "语言 / Language:"),
            ["thumbAspect"] = ("缩略图比例:", "Thumbnail Ratio:"),

            ["presetCustom"] = ("自定义", "Custom"),
            ["presetLow"] = ("流畅(低)", "Smooth (Low)"),
            ["presetMedium"] = ("均衡(中)", "Balanced (Medium)"),
            ["presetHigh"] = ("高画质", "High Quality"),
            ["presetUltra"] = ("极致画质", "Ultra Quality"),
            ["backendAuto"] = ("自动(优先GPU)", "Auto (GPU preferred)"),
            ["backendGpu"] = ("GPU (DX11)", "GPU (DX11)"),
            ["backendCpu"] = ("CPU 软件渲染", "CPU Software"),
            ["gpuAuto"] = ("自动 (默认 GPU)", "Auto (default GPU)"),
            ["themeLight"] = ("亮色 (档案暖白)", "Light (Warm White)"),
            ["themeDark"] = ("暗色 (档案深色)", "Dark (Deep Dark)"),
            ["gizmoOff"] = ("关", "Off"),
            ["gizmoSmall"] = ("小", "Small"),
            ["gizmoMedium"] = ("中", "Medium"),
            ["gizmoLarge"] = ("大", "Large"),
            ["langZh"] = ("简体中文", "简体中文"),
            ["langEn"] = ("English", "English"),

            ["enable"] = ("启用", "Enable"),
            ["pickColor"] = ("选择颜色...", "Pick Color..."),
            ["pickImage"] = ("选择图片...", "Pick Image..."),
            ["resetDefault"] = ("恢复默认", "Reset"),
            ["notSelected"] = ("(未选择)", "(none)"),
            ["show"] = ("显示", "Show"),
            ["color"] = ("颜色...", "Color..."),
            ["openLog"] = ("打开日志", "Open Log"),
            ["logRecord"] = ("记录 previewer.log (排查问题用)", "Write previewer.log (for troubleshooting)"),
            ["checkShowAtStartup"] = ("启动软件时显示", "Show on startup"),
            ["checkAutoFit"] = ("载入/复位时自动拉近填满画面", "Auto zoom to fill on load/reset"),
            ["checkFloorGrid"] = ("显示参考网格地面", "Show reference grid floor"),
            ["ssaa4"] = ("4x 超采样", "4x SSAA"),
            ["ssaa2"] = ("2x 超采样", "2x SSAA"),
            ["ssaaOff"] = ("关闭抗锯齿", "AA Off"),
            ["maxUnlimited"] = ("不限制", "Unlimited"),
            ["max50k"] = ("50000", "50000"),
            ["max20k"] = ("20000", "20000"),
            ["max10k"] = ("10000", "10000"),
            ["shadowNone"] = ("无阴影", "None"),
            ["shadowLow"] = ("低", "Low"),
            ["shadowMed"] = ("中", "Medium"),
            ["shadowHigh"] = ("高", "High"),

            // ---- 主窗口 ----
            ["mainTitle"] = ("Thumb3D 三维缩略图助手", "Thumb3D 3D Thumbnail Assistant"),
            ["openFile"] = ("打开 3D 文件...", "Open 3D File..."),
            ["openFolder"] = ("打开文件夹...", "Open Folder..."),
            ["resetView"] = ("复位视图", "Reset View"),
            ["illumination"] = ("照明", "Lighting"),
            ["savePng"] = ("另存 PNG", "Save PNG"),
            ["saveThumb"] = ("保存缩略图", "Save Thumbnail"),
            ["batchThumb"] = ("批量缩略图", "Batch Thumbnails"),
            ["customThumb"] = ("自定义缩略图", "Custom Thumbnail"),
            ["openWithOther"] = ("用其他软件打开", "Open with Other App"),
            ["extManage"] = ("扩展管理:", "Extension Manager:"),
            ["installExt"] = ("安装缩略图扩展", "Install Thumbnail Extension"),
            ["installAllUac"] = ("为所有用户安装(UAC)", "Install for All Users (UAC)"),
            ["uninstallExt"] = ("卸载扩展", "Uninstall Extension"),
            ["restartExplorer"] = ("重启资源管理器", "Restart Explorer"),

            // ---- 关于/欢迎 ----
            ["aboutTitle"] = ("关于 Thumb3D", "About Thumb3D"),
            ["welcomeTitle"] = ("欢迎使用 Thumb3D", "Welcome to Thumb3D"),
            ["welcomeHeadline"] = ("欢迎使用 Thumb3D 三维缩略图助手", "Welcome to Thumb3D 3D Thumbnail Assistant"),
            ["subtitle"] = ("三维缩略图助手  ·  3D文件缩略图生成器", "3D Thumbnail Assistant  ·  3D File Preview Generator"),
            ["maker"] = ("制作者：匿名IK", "Maker: anonymousIK"),
            ["homepage"] = ("个人主页： ", "Homepage: "),
            ["intro"] = ("Thumb3D 三维缩略图助手，为 Windows 资源管理器提供\r\n3D 模型与切片文件的缩略图预览，并内置三维预览器。",
                        "Thumb3D adds 3D model & slicer file thumbnail previews\r\nto Windows Explorer, with a built-in 3D previewer."),
            ["softwareIntro"] = ("◆ 软件简介：\r\n    Thumb3D 三维缩略图助手，为 Windows 资源管理器提供\r\n    3D 模型与切片文件的缩略图预览，并内置三维预览器。",
                                "◆ About:\r\n    Thumb3D 3D Thumbnail Assistant adds 3D model &\r\n    slicer file previews to Windows Explorer."),
            ["skipNextTime"] = ("下次不再打开此弹窗", "Don't show this again"),
            ["enterApp"] = ("进入软件", "Enter"),

            // ---- 更新内容弹窗 ----
            ["changelogTitle"] = ("更新内容", "What's New"),
            ["changelogBody"] = ("没有想好。", "Not decided yet."),
            ["changelogSkipNextTime"] = ("下次不再显示", "Don't show this again"),
            ["viewChangelog"] = ("查看更新内容", "View What's New"),

            // ---- Gizmo 面文字（右/左、顶/底、前/后按用户要求调换展示） ----
            ["faceLeft"] = ("左面", "Left"),
            ["faceRight"] = ("右面", "Right"),
            ["faceBottom"] = ("底部", "Bottom"),
            ["faceTop"] = ("顶部", "Top"),
            ["faceFront"] = ("前面", "Front"),
            ["faceBack"] = ("后面", "Back"),
        };

        /// <summary>取当前语言下的文本；无匹配键返回键本身。</summary>
        public static string T(string key)
        {
            if (Tbl.TryGetValue(key, out var v))
                return IsEn ? v.en : v.zh;
            return key;
        }

        /// <summary>切换语言（不持久化，持久化由调用方写回 RenderSettings）。</summary>
        public static void SetLanguage(string lang)
        {
            Lang = lang == "en" ? "en" : "zh";
        }
    }
}
