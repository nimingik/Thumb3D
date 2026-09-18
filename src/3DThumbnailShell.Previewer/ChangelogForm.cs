using System;
using System.Drawing;
using System.Windows.Forms;

namespace _3DThumbnailShell.Previewer
{
    /// <summary>
    /// 更新内容弹窗：标题"更新内容"居中加粗放大，正文为可滚动的富文本（为以后长文更新铺垫）。
    /// 首次启动自动弹出；底部"下次不再显示"默认勾选，确定时写入 RenderSettings.ShowUpdateNotes=false 不再自动弹出。
    /// 设置窗口的"查看更新内容"也复用本窗体。
    /// </summary>
    public sealed class ChangelogForm : Form
    {
        private readonly RenderSettings _settings;
        private readonly CheckBox _skipCheck;

        public ChangelogForm(RenderSettings settings)
        {
            _settings = settings ?? RenderSettings.Load();

            Text = I18n.T("changelogTitle");
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(460, 460);

            // 顶部标题："更新内容" 居中、加粗、放大
            var title = new Label
            {
                Dock = DockStyle.Top,
                Height = 52,
                Text = I18n.T("changelogTitle"),
                TextAlign = ContentAlignment.MiddleCenter,
                Font = new Font("Microsoft YaHei UI", 16f, FontStyle.Bold),
                ForeColor = Color.FromArgb(40, 40, 40)
            };

            // 正文：可滚动富文本（短内容无滚动条，长文可滚动）
            var body = new RichTextBox
            {
                Dock = DockStyle.Fill,
                WordWrap = true,
                ScrollBars = RichTextBoxScrollBars.Vertical,
                BorderStyle = BorderStyle.FixedSingle,
                ReadOnly = true,
                TabStop = false,
                Text = I18n.T("changelogBody")
            };

            // 底部：默认勾选"下次不再显示" + 确定按钮
            var bottom = new FlowLayoutPanel
            {
                Dock = DockStyle.Bottom,
                Height = 52,
                Padding = new Padding(14, 10, 14, 0),
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false
            };
            _skipCheck = new CheckBox
            {
                Text = I18n.T("changelogSkipNextTime"),
                Checked = true, // 默认勾选：下次不再自动显示
                AutoSize = true,
                Margin = new Padding(0, 8, 20, 0)
            };
            var okBtn = new Button { Text = I18n.T("ok"), Width = 110, Height = 30, Margin = new Padding(0, 3, 0, 0), Tag = "accent" };
            okBtn.Click += (s, e) =>
            {
                // 勾选则下次不再自动弹出，取消勾选则下次仍弹
                _settings.ShowUpdateNotes = !_skipCheck.Checked;
                _settings.Save();
                DialogResult = DialogResult.OK;
                Close();
            };
            bottom.Controls.Add(_skipCheck);
            bottom.Controls.Add(okBtn);

            Controls.Add(body);
            Controls.Add(title);
            Controls.Add(bottom);

            UiTheme.ApplyTo(this, _settings.UiTheme);

            // 富文本统一用主题前景色（主题已设 BackColor/ForeColor）
            body.SelectAll();
            body.SelectionColor = body.ForeColor;
            body.Select(0, 0);
            body.Invalidate();
        }
    }
}