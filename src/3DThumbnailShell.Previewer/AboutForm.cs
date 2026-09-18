using System;
using System.Drawing;
using System.Windows.Forms;

namespace _3DThumbnailShell.Previewer
{
    /// <summary>
    /// 关于对话框：品牌信息、作者（匿名IK）、个人主页链接（可点击）、软件简介。
    /// 作者信息与品牌标题同区显示（Dock=Top），确保始终可见。
    /// </summary>
    public sealed class AboutForm : Form
    {
        private const string HomePage = "https://space.bilibili.com/41807397";

        public AboutForm() : this(RenderSettings.Load().UiTheme)
        {
        }

        public AboutForm(UiThemeMode theme)
        {
            Text = I18n.T("aboutTitle");
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(500, 620);

            // 顶部信息区：品牌 + 制作者 + 个人主页（LinkLabel，URL 部分可点击）
            var header = new LinkLabel
            {
                Dock = DockStyle.Top,
                Height = 128,
                TextAlign = ContentAlignment.MiddleCenter,
                Font = new Font("Microsoft YaHei UI", 11f, FontStyle.Bold),
                ForeColor = Color.FromArgb(40, 40, 40),
                LinkColor = UiTheme.Copper,
                ActiveLinkColor = Color.FromArgb(226, 176, 128),
                Padding = new Padding(0, 12, 0, 0),
                TabStop = false
            };
            header.Text =
                "Thumb3D  ·  3D Thumbnail Assistant\r\n" +
                I18n.T("subtitle") + "  " + AppInfo.Version + "\r\n\r\n" +
                I18n.T("maker") + "\r\n" +
                I18n.T("homepage") + HomePage;
            // WinForms LinkLabel 已知 bug：链接区"首字符"会用 ForeColor 绘制（URL 开头的 h 掉色）。
            // 规避：链接区起点左移一位覆盖 URL 前的空格，让空格占住首字符位 → h 起全部正确上色；
            // 用 LinkArea 定义唯一链接区（点击跳转正常，零宽链接占位法会破坏点击命中）。
            var linkStart = header.Text.LastIndexOf(HomePage) - 1; // 空格
            header.LinkArea = new LinkArea(linkStart, HomePage.Length + 1);
            header.LinkClicked += (s, e) =>
            {
                try
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(HomePage) { UseShellExecute = true });
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, "打开主页失败: " + ex.Message, "关于", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            };

            // 软件简介 / 作者留言（GDI 富文本：首/末段加粗放大、正文普通，可滚动；颜色随主题）
            var fg = theme == UiThemeMode.Dark ? Color.FromArgb(235, 235, 235) : Color.FromArgb(60, 60, 60);
            var bg = theme == UiThemeMode.Dark ? Color.FromArgb(42, 42, 48) : Color.FromArgb(255, 253, 249);
            var usage = CommunityNote.Build(fg, bg);

            var okBtn = new Button
            {
                Text = I18n.T("ok"),
                Width = 90,
                Height = 30,
                Anchor = AnchorStyles.Bottom | AnchorStyles.Right,
                Location = new Point(395, 575)
            };
            okBtn.Click += (s, e) => { DialogResult = DialogResult.OK; Close(); };

            Controls.Add(usage);
            Controls.Add(header);
            Controls.Add(okBtn);
            okBtn.BringToFront();

            // 应用界面主题（与主窗口一致）
            UiTheme.ApplyTo(this, theme);
        }
    }

    /// <summary>作者留言正文（"关于"与"启动页"共用）：首段与末段加粗放大突出，正文普通字号。</summary>
    internal static class CommunityNote
    {
        public static readonly string[] Sections =
        {
            "感谢你使用我制作的大💩软件😋，希望我的软件可以给你带来帮助。",
            "不过注意！⚠️软件由 [匿名IK] 独立开发，相关代码、界面、图标、等等都用AI生成的，现在软件并不完美，你可能将遇到BUG，崩溃，各种模型显示错误等等，请根据个人需求使用，同时警惕盗版，二次篡改的风险，严禁将软件进行未经允许进行二次更改，挂闲鱼淘宝进行售卖,不准诈骗！不准骗人！不准把改赞赏码改成你的！不准倒卖！！！！！！！！我希望大家友好相处的一种环境。对了欢迎大家来我的个人QQ群玩：765184210，一起来交流各种事情。",
            "关于启动页的二维码？那个是赞赏码😋我曾想过上架steam把价格设置为1块钱然后可以用steam来实现在线更新的功能来维持更新软件，但是我发现上架steam居然要缴纳100美元🥲好像最低价格要0.99美元（大约6元）。我是一名刚刚步入社会的毕业大学生，而且还不是计算机专业的，我对这些一窍不知，我并不指望我制作的东西能赚钱，我很抱歉无力指望交100美元来实现steam更新，然后GitHub的在线更新不知道会怎么样，如果出现更新不及时还是我表示抱歉，请以我b站的更新视频为准吧。而赞赏码说实话我多多少少带着私心，不知道大家小时候有没有想过每个人给我一块钱的想法，我也想体验一下要饭的感觉🥺当然我希望大家自愿赞赏就好免费使用没关系别骂我什么变相收钱什么捞钱什么的，别骂我就行了。",
            "如果启动弹窗烦到你了其实可以在启动页和设置中选择关闭就没有啦。"
        };

        /// <summary>以 GDI 富文本构建作者留言：首/末段加粗放大突出，正文普通字号，带垂直滚动条。</summary>
        public static RichTextBox Build(Color fg, Color bg)
        {
            var rt = new RichTextBox
            {
                Dock = DockStyle.Fill,
                ReadOnly = true,
                BorderStyle = BorderStyle.None,
                BackColor = bg,
                ForeColor = fg,
                DetectUrls = false,
                WordWrap = true,
                ScrollBars = RichTextBoxScrollBars.Vertical,
                Margin = new Padding(4),
                TabStop = false
            };
            rt.Enter += (s, e) => rt.Parent?.Focus(); // 进入时不聚焦，避免出现焦点框

            void Append(string text, float size, FontStyle style)
            {
                rt.SelectionStart = rt.TextLength;
                rt.SelectionLength = 0;
                rt.SelectionFont = new Font("Microsoft YaHei UI", size, style);
                rt.SelectionColor = fg;
                rt.AppendText(text);
            }
            Append(Sections[0] + "\r\n\r\n", 12f, FontStyle.Bold);
            Append(Sections[1] + "\r\n\r\n" + Sections[2] + "\r\n\r\n", 10.5f, FontStyle.Regular);
            Append(Sections[3] + "\r\n", 12f, FontStyle.Bold);
            rt.SelectionStart = 0;
            rt.SelectionLength = 0;
            return rt;
        }
    }
}