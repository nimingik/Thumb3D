using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace _3DThumbnailShell.Previewer
{
    /// <summary>
    /// 启动开始弹窗：显示个人介绍图片（二维码名片）+ "下次不再打开"选项。
    /// 勾选"下次不再打开"后点击进入，将 ShowWelcome 写回设置并持久化。
    /// </summary>
    public sealed class WelcomeForm : Form
    {
        private readonly RenderSettings _settings;
        private System.IO.Stream _embeddedImg; // 内嵌资源流：随位移入窗体生命期，用于位图延迟解码

        public WelcomeForm(RenderSettings settings)
        {
            _settings = settings ?? RenderSettings.Load();

            Text = I18n.T("welcomeTitle");
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(520, 900);

            // 顶部标题
            var title = new Label
            {
                Dock = DockStyle.Top,
                Height = 36,
                Text = I18n.T("welcomeHeadline") + "  " + AppInfo.Version,
                TextAlign = ContentAlignment.MiddleCenter,
                Font = new Font("Microsoft YaHei UI", 12f, FontStyle.Bold),
                ForeColor = Color.FromArgb(40, 40, 40)
            };

            // 个人介绍图片
            var img = new PictureBox
            {
                Dock = DockStyle.Top,
                Height = 200,
                SizeMode = PictureBoxSizeMode.Zoom,
                BackColor = Color.White
            };
            var baseDir = AppContext.BaseDirectory;
            // 优先从程序集内嵌资源加载（welcome.png 已封装进 DLL），回退外部文件兼容旧版布局
            var imgPath = Path.Combine(baseDir, "Resources", "welcome.png");
            if (!File.Exists(imgPath)) imgPath = Path.Combine(baseDir, "welcome.png");
            var imgStream = System.Reflection.Assembly.GetExecutingAssembly()
                .GetManifestResourceStream("_3DThumbnailShell.Previewer.Resources.welcome.png");
            if (imgStream != null)
            {
                try
                {
                    img.Image = new Bitmap(imgStream);
                    _embeddedImg = imgStream; // 保持流存活，避免 Bitmap 延迟解码时报错
                }
                catch { imgStream.Dispose(); img.Image = null; }
            }
            if (img.Image == null && File.Exists(imgPath))
            {
                try { img.Image = new Bitmap(imgPath); }
                catch { img.Image = null; }
            }
            if (img.Image == null)
            {
                img.Height = 60;
                var hint = new Label
                {
                    Dock = DockStyle.Fill,
                    Text = "（未找到欢迎图片 welcome.png）",
                    TextAlign = ContentAlignment.MiddleCenter,
                    ForeColor = Color.Gray
                };
                Controls.Add(hint);
            }

            // 图片下方文字说明：作者留言（富文本，首/末段加粗放大突出）+ 底部主页链接（可点击）
            const string url = "https://space.bilibili.com/41807397";
            var infoPanel = new Panel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(20, 4, 10, 4)
            };
            var homeLink = new LinkLabel
            {
                Dock = DockStyle.Bottom,
                Text = I18n.T("homepage") + url,
                TextAlign = ContentAlignment.MiddleLeft,
                Font = new Font("Microsoft YaHei UI", 9f),
                LinkColor = UiTheme.Copper,
                ActiveLinkColor = Color.FromArgb(226, 176, 128),
                Padding = new Padding(0, 4, 0, 0),
                TabStop = false
            };
            // LinkLabel 链接区"首字符"用 ForeColor 绘制的 bug 规避：起点左移指向 URL 前的字符
            var homeLinkStart = homeLink.Text.IndexOf(url) - 1;
            homeLink.LinkArea = new LinkArea(homeLinkStart, url.Length + 1);
            homeLink.LinkClicked += (s, e) =>
            {
                try
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, "打开主页失败: " + ex.Message, "欢迎", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            };

            // 作者留言（GDI 富文本：首/末段加粗放大、正文普通，可滚动；颜色随主题）
            var nfg = _settings.UiTheme == UiThemeMode.Dark ? Color.FromArgb(235, 235, 235) : Color.FromArgb(60, 60, 60);
            var nbg = _settings.UiTheme == UiThemeMode.Dark ? Color.FromArgb(42, 42, 48) : Color.FromArgb(255, 253, 249);
            var note = CommunityNote.Build(nfg, nbg);
            infoPanel.Controls.Add(homeLink);
            infoPanel.Controls.Add(note);

            // 底部：不再打开 + 进入按钮
            var bottom = new FlowLayoutPanel
            {
                Dock = DockStyle.Bottom,
                Height = 52,
                Padding = new Padding(14, 10, 14, 0),
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false
            };
            var skipCheck = new CheckBox
            {
                Text = I18n.T("skipNextTime"),
                AutoSize = true,
                Margin = new Padding(0, 8, 16, 0)
            };
            var enterBtn = new Button { Text = I18n.T("enterApp"), Width = 110, Height = 30, Margin = new Padding(0, 3, 0, 0) };
            enterBtn.Click += (s, e) =>
            {
                _settings.ShowWelcome = !skipCheck.Checked;
                _settings.Save();
                DialogResult = DialogResult.OK;
                Close();
            };
            bottom.Controls.Add(skipCheck);
            bottom.Controls.Add(enterBtn);

            Controls.Add(infoPanel);
            Controls.Add(img);
            Controls.Add(title);
            Controls.Add(bottom);

            // 应用界面主题（与主窗口一致）
            UiTheme.ApplyTo(this, _settings.UiTheme);
            // 留言为 WPF 宿主，颜色已在 Build 时按主题设定，无需额外处理。
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _embeddedImg?.Dispose();
                _embeddedImg = null;
            }
            base.Dispose(disposing);
        }
    }
}
