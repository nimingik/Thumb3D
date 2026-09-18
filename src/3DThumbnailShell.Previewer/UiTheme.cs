using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace _3DThumbnailShell.Previewer
{
    /// <summary>界面主题模式（设置中可选，全局持久化到 previewer-settings.json）。</summary>
    public enum UiThemeMode
    {
        Light = 0,
        Dark = 1
    }

    /// <summary>
    /// UI 主题（参考"莱茵生命档案终端"的档案实验室风）：
    /// 亮色 = 暖白米白底 + 铜色高光；暗色 = 深色底 + 铜色高光。
    /// ApplyTo 递归着色整个控件树；
    /// Tag=="keep"   的控件跳过（保留用户自定义颜色，如颜色选择色块）；
    /// Tag=="accent" 的按钮用铜色高光强调（如"安装缩略图扩展"主操作）；
    /// Viewport3D 的 3D 视口背景由 RenderSettings 单独控制，不参与 UI 主题。
    /// </summary>
    public static class UiTheme
    {
        /// <summary>铜色高光（两套主题共用）。</summary>
        public static readonly Color Copper = Color.FromArgb(196, 149, 106);

        private sealed class Palette
        {
            public Color Window;        // 窗口背景
            public Color Panel;         // 面板/工具栏表面
            public Color Text;          // 主文字
            public Color SecondaryText; // 次要文字
            public Color Input;         // 输入框/列表背景
            public Color Button;        // 按钮背景
            public Color ButtonHover;   // 按钮悬停
            public Color Border;        // 控件描边
        }

        private static readonly Palette Light = new Palette
        {
            Window = Color.FromArgb(245, 240, 235),       // #F5F0EB 暖白
            Panel = Color.FromArgb(255, 253, 249),        // #FFFDF9 面板
            Text = Color.FromArgb(26, 26, 26),            // #1A1A1A
            SecondaryText = Color.FromArgb(107, 107, 107),
            Input = Color.FromArgb(255, 255, 255),
            Button = Color.FromArgb(255, 253, 249),
            ButtonHover = Color.FromArgb(233, 219, 200),
            Border = Color.FromArgb(200, 188, 170)
        };

        private static readonly Palette Dark = new Palette
        {
            Window = Color.FromArgb(30, 30, 34),          // #1E1E22 深底
            Panel = Color.FromArgb(42, 42, 48),           // #2A2A30 面板
            Text = Color.FromArgb(232, 230, 225),         // #E8E6E1
            SecondaryText = Color.FromArgb(158, 156, 150),
            Input = Color.FromArgb(52, 52, 58),
            Button = Color.FromArgb(52, 52, 58),
            ButtonHover = Color.FromArgb(70, 70, 78),
            Border = Color.FromArgb(86, 86, 94)
        };

        /// <summary>递归应用主题到控件树。</summary>
        public static void ApplyTo(Control root, UiThemeMode mode)
        {
            var p = mode == UiThemeMode.Dark ? Dark : Light;
            ApplyRecursive(root, p, mode);
        }

        private static void ApplyRecursive(Control c, Palette p, UiThemeMode mode)
        {
            // 3D 视口：背景由渲染设置单独控制，不走 UI 主题
            if (c is Viewport3D) return;
            // Tag=="keep"：保留用户自定义配色（颜色选择色块等）
            if ((string)c.Tag == "keep") return;

            switch (c)
            {
                case Button btn:
                    if ((string)btn.Tag == "accent")
                    {
                        btn.FlatStyle = FlatStyle.Flat;
                        btn.BackColor = Copper;
                        btn.ForeColor = Color.White;
                        btn.FlatAppearance.BorderColor = Copper; // BorderColor 不支持 Transparent（ButtonBase）
                        btn.FlatAppearance.MouseOverBackColor = Color.FromArgb(178, 133, 90);
                        btn.FlatAppearance.MouseDownBackColor = Color.FromArgb(150, 110, 72);
                    }
                    else
                    {
                        btn.FlatStyle = FlatStyle.Flat;
                        btn.BackColor = p.Button;
                        btn.ForeColor = p.Text;
                        btn.FlatAppearance.BorderColor = p.Border;
                        btn.FlatAppearance.MouseOverBackColor = p.ButtonHover;
                        btn.FlatAppearance.MouseDownBackColor = p.Border;
                    }
                    break;

                // LinkLabel 继承自 Label，必须先于 Label 匹配
                case LinkLabel link:
                    link.ForeColor = p.Text;
                    link.LinkColor = Copper;                      // 未访问链接（主题铜色）
                    link.VisitedLinkColor = Color.FromArgb(160, 120, 80);  // 访问后略深铜
                    link.ActiveLinkColor = Color.FromArgb(226, 176, 128);  // 按下时亮铜
                    link.DisabledLinkColor = p.SecondaryText;
                    link.BackColor = Color.Transparent;
                    link.Invalidate(); // 链接区颜色变化后强制重绘，避免部分文字残留旧色
                    break;

                case Label lbl:
                    // 灰色系文字 → 次要文字；其余 → 主文字
                    lbl.ForeColor = IsGrayish(lbl.ForeColor) ? p.SecondaryText : p.Text;
                    lbl.BackColor = Color.Transparent;
                    break;

                case CheckBox cb:
                    cb.ForeColor = p.Text;
                    cb.BackColor = Color.Transparent;
                    break;

                case TextBox tb:
                    tb.BackColor = p.Input;
                    tb.ForeColor = p.Text;
                    tb.BorderStyle = BorderStyle.FixedSingle;
                    break;

                case ComboBox cb:
                    cb.BackColor = p.Input;
                    cb.ForeColor = p.Text;
                    cb.FlatStyle = FlatStyle.Flat;
                    break;

                case ListBox lb:
                    lb.BackColor = p.Input;
                    lb.ForeColor = p.Text;
                    lb.BorderStyle = BorderStyle.FixedSingle;
                    break;

                case RichTextBox rtb:
                    rtb.BackColor = p.Panel;
                    rtb.ForeColor = p.Text;
                    rtb.BorderStyle = BorderStyle.FixedSingle;
                    break;

                case TrackBar tr:
                    tr.BackColor = p.Panel;
                    break;

                case PictureBox pb:
                    pb.BackColor = p.Input;
                    break;

                // FlowLayoutPanel / TableLayoutPanel 都是 Panel，必须先于 Panel 匹配
                case FlowLayoutPanel flp:
                    flp.BackColor = p.Panel;
                    break;

                case TableLayoutPanel tlp:
                    tlp.BackColor = p.Window;
                    break;

                case Panel panel:
                    panel.BackColor = p.Window;
                    break;

                case GroupBox gb:
                    gb.ForeColor = p.Text;
                    gb.BackColor = Color.Transparent;
                    break;

                default:
                    if (c is Form f)
                    {
                        f.BackColor = p.Window;
                        ApplyTitleBar(f, mode); // 暗色主题下把系统标题栏也调暗，保持整体统一
                    }
                    break;
            }

            foreach (Control child in c.Controls)
                ApplyRecursive(child, p, mode);
        }

        /// <summary>设置窗体标题栏深浅（Win10 1809+ / Win11 沉浸式深色标题栏；失败静默忽略）。</summary>
        private static void ApplyTitleBar(Form f, UiThemeMode mode)
        {
            try
            {
                if (!f.IsHandleCreated)
                {
                    f.HandleCreated += (s, e) => SetDarkTitleBar(f.Handle, mode == UiThemeMode.Dark);
                    return;
                }
                SetDarkTitleBar(f.Handle, mode == UiThemeMode.Dark);
            }
            catch
            {
                // 不支持的系统版本：保持系统默认标题栏
            }
        }

        private static void SetDarkTitleBar(IntPtr hwnd, bool dark)
        {
            if (hwnd == IntPtr.Zero) return;
            var v = dark ? 1 : 0;
            if (DwmSetWindowAttribute(hwnd, 20, ref v, sizeof(int)) != 0)
                DwmSetWindowAttribute(hwnd, 19, ref v, sizeof(int)); // 旧版 Win10
        }

        [DllImport("dwmapi.dll", PreserveSig = true)]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int attrValue, int attrSize);

        /// <summary>近似灰判定（R≈G≈B 的低饱和色，如 Gray / DimGray → 次要文字）。</summary>
        private static bool IsGrayish(Color col)
        {
            var max = Math.Max(col.R, Math.Max(col.G, col.B));
            var min = Math.Min(col.R, Math.Min(col.G, col.B));
            return max - min <= 12;
        }
    }

    /// <summary>应用程序元信息：版本号（取自程序集，显示为 vX.Y）。</summary>
    internal static class AppInfo
    {
        /// <summary>版本号字符串，如 "v1.0"。</summary>
        public static string Version
        {
            get
            {
                var v = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
                return v == null ? "v1.0" : ("v" + v.Major + "." + v.Minor);
            }
        }
    }
}
