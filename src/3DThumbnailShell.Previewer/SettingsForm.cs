using System;
using System.Collections.Generic;
using System.Windows.Forms;

namespace _3DThumbnailShell.Previewer
{
    /// <summary>
    /// 画质设置窗口（类游戏的渲染选项）：
    /// 预设（流畅/均衡/高/极致）联动各手动项；支持 确定 / 应用 / 取消。
    /// 修改立即写回 RenderSettings 并持久化到 %LOCALAPPDATA%\3DThumbnailShell\previewer-settings.json。
    /// </summary>
    public sealed class SettingsForm : Form
    {
        private readonly RenderSettings _settings;      // 外部共享实例（应用时写回）
        private readonly Action<RenderSettings> _onApplied;

        private readonly ComboBox _presetCombo;
        private readonly ComboBox _backendCombo;
        private readonly ComboBox _gpuCombo;      // GPU 适配器选择（自动 + 枚举名称）
        private readonly string[] _gpuNames;      // GPU 下拉项（[0]=自动）
        private readonly ComboBox _resScaleCombo;
        private readonly ComboBox _ssaaCombo;
        private readonly ComboBox _interactCombo;
        private readonly ComboBox _maxTriCombo;
        private readonly ComboBox _themeCombo;      // 界面主题（亮色/暗色）
        private readonly CheckBox _colorCheck;     // 启用自定义模型颜色
        private readonly Button _colorBtn;         // 颜色选择色块
        private readonly TrackBar _shadowTrack;    // 阴影强度滑条
        private readonly Label _shadowLabel;       // 阴影档位文字
        private readonly TrackBar _smoothTrack;    // 平滑着色程度滑条
        private readonly Label _smoothLabel;       // 平滑程度百分数文字
        private readonly CheckBox _logCheck;       // 诊断日志开关
        private readonly Button _openLogBtn;       // 打开日志文件按钮
        private readonly CheckBox _negCheck;       // 显示负零件
        private readonly Button _negBtn;           // 负零件颜色选择
        private readonly TrackBar _negTrack;       // 负零件不透明度滑条
        private readonly Label _negLbl;            // 不透明度百分数文字
        private bool _negColorPicked;              // 用户是否点过负零件颜色（区分"未设置"与白色）
        private readonly CheckBox _modCheck;       // 显示修改器
        private readonly Button _modBtn;           // 修改器颜色选择
        private readonly TrackBar _modTrack;       // 修改器不透明度滑条
        private readonly Label _modLbl;            // 不透明度百分数文字
        private bool _modColorPicked;              // 用户是否点过修改器颜色
        private readonly CheckBox _bgColorCheck;   // 自定义背景颜色
        private readonly Button _bgColorBtn;       // 背景颜色选择
        private readonly TrackBar _bgAlphaTrack;   // 背景颜色不透明度（0..100，透明通道）
        private readonly Label _bgAlphaLbl;        // 不透明度百分比文字
        private readonly CheckBox _bgImgCheck;     // 自定义背景图片
        private readonly Button _bgImgBtn;         // 背景图片选择
        private readonly Label _bgImgLbl;          // 背景图片文件名
        private readonly Button _bgImgReset;       // 恢复默认背景（颜色+图片）
        private string _bgImgPath;                 // 已选择的背景图片路径
        private readonly CheckBox _welcomeCheck;   // 启动时显示开始介绍弹窗
        private readonly CheckBox _autoFitCheck;   // 自动取景（载入/复位时拉近填满画面）
        private readonly CheckBox _floorCheck;     // 地板网格（模型底面显示参考网格地面）
        private readonly ComboBox _gizmoCombo;     // Gizmo 尺寸（小/中/大）
        private readonly ComboBox _langCombo;      // 语言（简体中文/English）
        private readonly ComboBox _thumbAspectCombo; // 缩略图保存比例（1:1/4:3/16:9）
        private readonly Label _brand;             // 顶部品牌信息栏（可随语言重译）
        private readonly Button _changelogBtn;     // "查看更新内容"入口按钮
        private readonly Button _aboutBtn;         // 底部按钮（可随语言重译）
        private readonly Button _okBtn;
        private readonly Button _applyBtn;
        private readonly Button _cancelBtn;
        private readonly List<(Control ctrl, string key)> _texts = new();  // 可翻译控件（标签/复选框/行内按钮 → 键）
        private readonly List<(ComboBox cb, string[] keys)> _combos = new(); // 下拉框（键列表，null=动态项不重译）
        private bool _loading; // 预设联动时避免递归回写

        private static readonly string[] ResScales = { "100%", "75%", "50%", "25%" };
        private static readonly float[] ResScaleVals = { 1f, 0.75f, 0.5f, 0.25f };
        private static readonly string[] SsaaItems = { "4x 超采样", "2x 超采样", "关闭抗锯齿" };
        private static readonly int[] SsaaVals = { 4, 2, 1 };
        private static readonly string[] Interacts = { "100%", "50%", "33%", "25%" };
        private static readonly float[] InteractVals = { 1f, 0.5f, 0.333f, 0.25f };
        private static readonly string[] MaxTris = { "不限制", "50000", "20000", "10000" };
        private static readonly int[] MaxTriVals = { 0, 50000, 20000, 10000 };
        private static readonly string[] ShadowItems = { "无阴影", "低", "中", "高" };
        private static readonly float[] ShadowVals = { 0f, 0.33f, 0.66f, 1f };
        private static readonly string[] GizmoSizes = { "关", "小", "中 (默认)", "大" };
        private static readonly float[] GizmoScaleVals = { -1f, 0.75f, 1f, 1.35f }; // -1 = 关闭 Gizmo
        private static readonly string[] Languages = { "简体中文", "English" };
        // 可翻译项：键 → 显示文本（下拉项/滑条档位）
        private static readonly string[] PresetKeys = { "presetCustom", "presetLow", "presetMedium", "presetHigh", "presetUltra" };
        private static readonly string[] BackendKeys = { "backendAuto", "backendGpu", "backendCpu" };
        private static readonly string[] SsaaKeys = { "ssaa4", "ssaa2", "ssaaOff" };
        private static readonly string[] MaxTriKeys = { "maxUnlimited", "max50k", "max20k", "max10k" };
        private static readonly string[] ShadowKeys = { "shadowNone", "shadowLow", "shadowMed", "shadowHigh" };
        private static readonly string[] ThemeKeys = { "themeLight", "themeDark" };
        private static readonly string[] GizmoKeys = { "gizmoOff", "gizmoSmall", "gizmoMedium", "gizmoLarge" };
        private static readonly string[] LangKeys = { "langZh", "langEn" };

        public SettingsForm(RenderSettings settings, Action<RenderSettings> onApplied)
        {
            _settings = settings ?? RenderSettings.Load();
            _onApplied = onApplied;

            Text = "设置";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            // 高度自适应屏幕工作区：设置区内部滚动，保证底部"关于/取消/应用/确定"按钮始终完整可见。
            // 内容总高约 908（品牌 52 + 22×36 行 + 按钮 52），常规 1080p 屏幕可直接放下，小屏自动滚动。
            var work = System.Windows.Forms.Screen.PrimaryScreen?.WorkingArea;
            var maxH = work.HasValue ? work.Value.Height - 60 : 940;
            ClientSize = new System.Drawing.Size(480, Math.Min(976, maxH));

            // 顶部品牌信息栏（英文主名 + 中英副标题）
            _brand = new Label
            {
                Dock = DockStyle.Top,
                Text = "Thumb3D  ·  3D Thumbnail Assistant\r\n三维缩略图助手  ·  3D文件缩略图生成器",
                TextAlign = System.Drawing.ContentAlignment.MiddleCenter,
                Height = 52,
                Font = new System.Drawing.Font(System.Drawing.FontFamily.GenericSansSerif, 12f, System.Drawing.FontStyle.Bold),
                ForeColor = System.Drawing.Color.DimGray,
                Padding = new Padding(0, 4, 0, 4)
            };
            Controls.Add(_brand);
            Controls.SetChildIndex(_brand, 0);

            var grid = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,   // 占满品牌栏与底部按钮之间的剩余空间；内容超高时内部滚动
                Padding = new Padding(16, 12, 16, 0),
                ColumnCount = 2,
                RowCount = 24,   // 实占 23 行 + 末行留空
                AutoSize = false,
                AutoScroll = true
            };
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            // 行样式集中预置：必须与行数一一对应，否则末尾行拿不到样式会抢占剩余空间（标签掉到控件下方）。
            // 行高 36：给滑条（TrackBar 高 30）和复选框留足纵向空间，内容不再拥挤/上下裁切。
            // 末行用 Percent 占位：面板高于内容时由它吸收多余高度，避免最后一行（缩略图比例）被拉伸。
            for (var r = 0; r < 23; r++)
                grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
            grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            _presetCombo = MakeCombo(grid, 0, "qualityPreset", PresetKeys);
            _backendCombo = MakeCombo(grid, 1, "renderBackend", BackendKeys);
            // GPU 适配器：自动 + 系统枚举到的所有硬件 GPU（用于多显卡/核显+独显切换）
            {
                var gpuList = new List<string> { I18n.T("gpuAuto") };
                gpuList.AddRange(GpuAdapterInfo.GetAdapterNames());
                _gpuNames = gpuList.ToArray();
            }
            _gpuCombo = MakeCombo(grid, 2, "gpuAdapter", null, _gpuNames);
            _resScaleCombo = MakeCombo(grid, 3, "renderRes", null, ResScales);
            _ssaaCombo = MakeCombo(grid, 4, "aa", SsaaKeys);
            _interactCombo = MakeCombo(grid, 5, "interactScale", null, Interacts);
            _maxTriCombo = MakeCombo(grid, 6, "maxTri", MaxTriKeys);
            (var _colorCheckTmp, var _colorBtnTmp, _, _) = MakeColorRow(grid, 7, "modelColor");
            _colorCheck = _colorCheckTmp; _colorBtn = _colorBtnTmp;
            (_shadowTrack, _shadowLabel) = MakeShadowRow(grid, 8, "shadowLevel");
            (_smoothTrack, _smoothLabel) = MakeSmoothRow(grid, 9, "smoothShading");
            (_logCheck, _openLogBtn) = MakeLogRow(grid, 10);
            (_negCheck, _negBtn, _negTrack, _negLbl) = MakeNegativeRow(grid, 11, "negativeParts");
            // 修改器（Bambu/Orca modifier_part）：与负零件同款行（开关 + 颜色 + 不透明度），颜色/透明度独立
            (_modCheck, _modBtn, _modTrack, _modLbl) = MakeNegativeRow(grid, 12, "modifierParts");
            (_bgColorCheck, _bgColorBtn, _bgAlphaTrack, _bgAlphaLbl) = MakeColorRow(grid, 13, "bgColor", withAlpha: true);
            (_bgImgCheck, _bgImgBtn, _bgImgLbl, _bgImgReset) = MakeImageRow(grid, 14, "bgImage");
            _welcomeCheck = MakeCheckRow(grid, 15, "welcomePopup", "checkShowAtStartup");
            _autoFitCheck = MakeCheckRow(grid, 16, "autoFit", "checkAutoFit");
            _floorCheck = MakeCheckRow(grid, 17, "floorGrid", "checkFloorGrid");
            _themeCombo = MakeCombo(grid, 18, "theme", ThemeKeys);
            _themeCombo.SelectedIndexChanged += OnThemeChanged;
            _gizmoCombo = MakeCombo(grid, 19, "gizmoSize", GizmoKeys);
            // Gizmo 尺寸：切换即写回共享设置并实时重绘（叠加层，仅刷新视图即可）
            _gizmoCombo.SelectedIndexChanged += (s, e) =>
            {
                if (_loading || _gizmoCombo.SelectedIndex < 0) return;
                _settings.GizmoScale = GizmoScaleVals[_gizmoCombo.SelectedIndex];
                _settings.Save();
                _onApplied?.Invoke(_settings);
            };
            _langCombo = MakeCombo(grid, 20, "language", LangKeys);

            // 更新内容入口：点击复用"更新内容"弹窗（可随时查看，也用于重置"下次不再显示"）
            var chartLbl = new Label { Text = I18n.T("changelogTitle"), Dock = DockStyle.Fill, TextAlign = System.Drawing.ContentAlignment.MiddleLeft, Margin = new Padding(0) };
            _texts.Add((chartLbl, "changelogTitle"));
            grid.Controls.Add(chartLbl, 0, 21);
            _changelogBtn = new Button { Text = I18n.T("viewChangelog"), AutoSize = false, Width = 150, Height = 26 };
            _texts.Add((_changelogBtn, "viewChangelog"));
            _changelogBtn.Click += (s, e) => { using var cf = new ChangelogForm(_settings); cf.ShowDialog(this); };
            grid.Controls.Add(_changelogBtn, 1, 21);
            // 缩略图保存比例：1:1 / 4:3 / 16:9（透明底、按该比例输出，消除白边）
            _thumbAspectCombo = MakeCombo(grid, 22, "thumbAspect", null, new[] { "1:1", "4:3", "16:9" });
            // 语言：切换即全局生效并实时重译本窗口 + 主窗口（持久化到设置）
            _langCombo.SelectedIndexChanged += (s, e) =>
            {
                if (_loading || _langCombo.SelectedIndex < 0) return;
                var lang = _langCombo.SelectedIndex == 1 ? "en" : "zh";
                I18n.SetLanguage(lang);
                _settings.Language = lang;
                _settings.Save();
                RefreshTexts();
                _onApplied?.Invoke(_settings);
            };

            _presetCombo.SelectedIndexChanged += OnPresetChanged;
            foreach (var c in new[] { _backendCombo, _resScaleCombo, _ssaaCombo, _interactCombo, _maxTriCombo })
                c.SelectedIndexChanged += (s, e) => { if (!_loading) _presetCombo.SelectedIndex = 0; };

            // GPU 适配器：切换即写回共享设置并实时重建渲染后端（重渲染）
            _gpuCombo.SelectedIndexChanged += (s, e) =>
            {
                if (_loading || _gpuCombo.SelectedIndex < 0) return;
                _settings.GpuAdapterName = _gpuCombo.SelectedIndex == 0 ? null : _gpuNames[_gpuCombo.SelectedIndex];
                _settings.Save();
                _onApplied?.Invoke(_settings);
            };

            _colorCheck.CheckedChanged += (s, e) => _colorBtn.Enabled = _colorCheck.Checked;
            _colorBtn.Click += (s, e) =>
            {
                using var cd = new ColorDialog { FullOpen = true, Color = _colorBtn.BackColor };
                if (cd.ShowDialog(this) == DialogResult.OK)
                    _colorBtn.BackColor = cd.Color;
            };
            _shadowTrack.ValueChanged += (s, e) =>
                _shadowLabel.Text = ShadowItems[_shadowTrack.Value];
            // 平滑着色：拖动即写共享设置实例并回调重渲染（实时预览）；_loading 期间（初始化）不回写
            _smoothTrack.ValueChanged += (s, e) =>
            {
                _smoothLabel.Text = _smoothTrack.Value + "%";
                if (_loading) return;
                _settings.SmoothShading = _smoothTrack.Value / 100f;
                _onApplied?.Invoke(_settings);
            };
            _openLogBtn.Click += (s, e) => OpenLogFile();

            // 负零件：显示开关控制颜色/不透明度控件可用性；不透明度拖动即实时回写（同平滑着色）
            _negCheck.CheckedChanged += (s, e) =>
            {
                _negBtn.Enabled = _negCheck.Checked;
                _negTrack.Enabled = _negCheck.Checked;
            };
            _negBtn.Click += (s, e) =>
            {
                using var cd = new ColorDialog { FullOpen = true, Color = _negBtn.BackColor };
                if (cd.ShowDialog(this) == DialogResult.OK)
                {
                    _negBtn.BackColor = cd.Color;
                    _negColorPicked = true;
                }
            };
            _negTrack.ValueChanged += (s, e) =>
            {
                _negLbl.Text = _negTrack.Value + "%";
                if (_loading) return;
                _settings.NegativeOpacity = _negTrack.Value / 100f;
                _onApplied?.Invoke(_settings);
            };

            // 修改器：与负零件同款交互（显示开关控制可用性；颜色/不透明度实时回写）
            _modCheck.CheckedChanged += (s, e) =>
            {
                _modBtn.Enabled = _modCheck.Checked;
                _modTrack.Enabled = _modCheck.Checked;
            };
            _modBtn.Click += (s, e) =>
            {
                using var cd = new ColorDialog { FullOpen = true, Color = _modBtn.BackColor };
                if (cd.ShowDialog(this) == DialogResult.OK)
                {
                    _modBtn.BackColor = cd.Color;
                    _modColorPicked = true;
                }
            };
            _modTrack.ValueChanged += (s, e) =>
            {
                _modLbl.Text = _modTrack.Value + "%";
                if (_loading) return;
                _settings.ModifierOpacity = _modTrack.Value / 100f;
                _onApplied?.Invoke(_settings);
            };

            // 背景颜色：勾选启用色块；点色块弹颜色选择器（实时回写预览）
            _bgColorCheck.CheckedChanged += (s, e) =>
            {
                _bgColorBtn.Enabled = _bgColorCheck.Checked;
                _bgAlphaTrack.Enabled = _bgColorCheck.Checked;
            };
            // 背景颜色透明通道：拖动即实时写回（含 alpha）并重渲染（_loading 期间不写）
            _bgAlphaTrack.ValueChanged += (s, e) =>
            {
                _bgAlphaLbl.Text = _bgAlphaTrack.Value + "%";
                if (_loading || !_bgColorCheck.Checked) return;
                ApplyBgColorLive();
            };
            void ApplyBgColorLive()
            {
                _settings.BackgroundColorHex = BgColorHex();
                _settings.BackgroundImagePath = null; // 切换纯色时清除图片，颜色优先呈现
                _bgImgCheck.Checked = false;
                _bgImgReset.Enabled = true;
                _onApplied?.Invoke(_settings);
            }
            _bgColorBtn.Click += (s, e) =>
            {
                using var cd = new ColorDialog { FullOpen = true, Color = _bgColorBtn.BackColor };
                if (cd.ShowDialog(this) == DialogResult.OK)
                {
                    _bgColorBtn.BackColor = cd.Color;
                    ApplyBgColorLive();
                }
            };

            // 背景图片：勾选启用选图按钮；选图后立即写回并实时重渲染
            _bgImgCheck.CheckedChanged += (s, e) => _bgImgBtn.Enabled = _bgImgCheck.Checked;
            _bgImgBtn.Click += (s, e) =>
            {
                using var ofd = new OpenFileDialog
                {
                    Title = "选择背景图片",
                    Filter = "图片文件|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp|所有文件|*.*"
                };
                if (ofd.ShowDialog(this) == DialogResult.OK)
                {
                    _bgImgPath = ofd.FileName;
                    _bgImgLbl.Text = System.IO.Path.GetFileName(_bgImgPath); // 文字色由主题控制
                    _settings.BackgroundImagePath = _bgImgPath;
                    _bgImgReset.Enabled = true;
                    _onApplied?.Invoke(_settings);
                }
            };
            // 恢复默认：清除背景颜色与图片，回到默认黑底，并实时重渲染
            _bgImgReset.Click += (s, e) => ResetBackground();

            // 底部按钮：加高到 52 并留出上下内边距（10 上 / 8 下），按钮不贴窗口底边
            var btns = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 52, Padding = new Padding(0, 10, 12, 8) };
            _aboutBtn = new Button { Text = "关于...", AutoSize = true };
            _aboutBtn.Click += (s, e) => { using var af = new AboutForm(_settings.UiTheme); af.ShowDialog(this); };
            _okBtn = new Button { Text = "确定", AutoSize = true };
            _okBtn.Click += (s, e) => { Apply(); DialogResult = DialogResult.OK; Close(); };
            _applyBtn = new Button { Text = "应用", AutoSize = true };
            _applyBtn.Click += (s, e) => Apply();
            _cancelBtn = new Button { Text = "取消", AutoSize = true };
            _cancelBtn.Click += (s, e) => { DialogResult = DialogResult.Cancel; Close(); };
            btns.Controls.Add(_cancelBtn);
            btns.Controls.Add(_applyBtn);
            btns.Controls.Add(_okBtn);
            btns.Controls.Add(_aboutBtn);
            btns.Controls.SetChildIndex(_okBtn, 0);
            btns.Controls.SetChildIndex(_applyBtn, 0);
            btns.Controls.SetChildIndex(_cancelBtn, 0);
            btns.Controls.SetChildIndex(_aboutBtn, 0);

            Controls.Add(btns);
            Controls.Add(grid);
            // 布局顺序（dock 按 z 序逆序处理）：底部按钮 → 顶部品牌栏 → 设置区 Fill 最后布局占剩余空间，
            // 从而设置区再高也不会遮住按钮（小屏时设置区内部滚动）。
            Controls.SetChildIndex(grid, 0);

            LoadValues();
            // 应用界面主题（亮色/暗色）；颜色选择色块等 Tag=="keep" 控件保留用户配色
            UiTheme.ApplyTo(this, _settings.UiTheme);
            RefreshTexts(); // 按当前语言（设置里的 Language）翻译界面
        }

        /// <summary>按当前语言重译本窗口：标题/品牌/行标签/下拉项/底部按钮/滑条档位。</summary>
        private void RefreshTexts()
        {
            // 回设 SelectedIndex 会触发各下拉的 SelectedIndexChanged（语言/主题/Gizmo 等处理器），
            // 必须置 _loading 守卫，否则语言处理器回调 RefreshTexts → 无限递归 → 栈溢出崩溃。
            var prevLoading = _loading;
            _loading = true;
            Text = I18n.T("settings");
            _brand.Text = "Thumb3D  ·  3D Thumbnail Assistant\r\n" + I18n.T("subtitle");
            foreach (var (ctrl, key) in _texts)
                ctrl.Text = I18n.T(key);
            foreach (var (cb, keys) in _combos)
            {
                if (keys == null) continue; // 动态项（GPU 列表）不重译
                var idx = cb.SelectedIndex;
                cb.Items.Clear();
                foreach (var k in keys) cb.Items.Add(I18n.T(k));
                if (idx >= 0 && idx < cb.Items.Count) cb.SelectedIndex = idx;
            }
            _aboutBtn.Text = I18n.T("about");
            _okBtn.Text = I18n.T("ok");
            _applyBtn.Text = I18n.T("apply");
            _cancelBtn.Text = I18n.T("cancel");
            if (_shadowLabel != null)
                _shadowLabel.Text = I18n.T(ShadowKeys[Math.Max(0, Math.Min(ShadowKeys.Length - 1, _shadowTrack.Value))]);
            _smoothLabel.Text = _smoothTrack.Value + "%";
            _negLbl.Text = _negTrack.Value + "%";
            if (!string.IsNullOrEmpty(_bgImgPath))
                _bgImgLbl.Text = System.IO.Path.GetFileName(_bgImgPath);
            else if (_bgImgCheck.Checked)
                _bgImgLbl.Text = I18n.T("notSelected");
            _loading = prevLoading;
        }

        /// <summary>界面主题切换：立即重写设置、重着色本窗口并回调主窗口实时重应用。</summary>
        private void OnThemeChanged(object sender, EventArgs e)
        {
            if (_loading || _themeCombo.SelectedIndex < 0) return;
            _settings.UiTheme = _themeCombo.SelectedIndex == 1 ? UiThemeMode.Dark : UiThemeMode.Light;
            UiTheme.ApplyTo(this, _settings.UiTheme);
            _onApplied?.Invoke(_settings);
        }

        /// <summary>清除背景自定义（颜色 + 图片），恢复跟随界面主题的默认画布，实时写回并重渲染。</summary>
        private void ResetBackground()
        {
            _settings.BackgroundColorHex = null;
            _settings.BackgroundImagePath = null;
            _bgColorCheck.Checked = false;
            _bgColorBtn.Enabled = false;
            _bgColorBtn.BackColor = System.Drawing.Color.White;
            _bgImgCheck.Checked = false;
            _bgImgBtn.Enabled = false;
            _bgImgPath = null;
            _bgImgLbl.Text = "(未选择)";
            _bgImgLbl.ForeColor = System.Drawing.Color.DimGray;
            _bgImgReset.Enabled = false;
            _onApplied?.Invoke(_settings);
        }

        /// <summary>
        /// 下拉行：标签键 + 可翻译项键（itemKeys）；动态项（dynamicItems 非空，如 GPU 名称）不翻译。
        /// 创建时按当前语言填文本，并登记到 _texts/_combos 供切换语言时重译。
        /// </summary>
        private ComboBox MakeCombo(TableLayoutPanel grid, int row, string labelKey, string[] itemKeys, string[] dynamicItems = null)
        {
            var lbl = new Label { Text = I18n.T(labelKey), Dock = DockStyle.Fill, TextAlign = System.Drawing.ContentAlignment.MiddleLeft, Margin = new Padding(0) };
            _texts.Add((lbl, labelKey));
            grid.Controls.Add(lbl, 0, row);
            var cb = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill, Margin = new Padding(0, 6, 0, 6) };
            if (dynamicItems != null)
            {
                cb.Items.AddRange(dynamicItems);
                _combos.Add((cb, null));
            }
            else
            {
                foreach (var k in itemKeys) cb.Items.Add(I18n.T(k));
                _combos.Add((cb, itemKeys));
            }
            grid.Controls.Add(cb, 1, row);
            return cb;
        }

        /// <summary>模型颜色行：勾选"启用"后可点色块弹颜色选择器。</summary>
        private (CheckBox, Button, TrackBar, Label) MakeColorRow(TableLayoutPanel grid, int row, string labelKey, bool withAlpha = false)
        {
            var lbl = new Label { Text = I18n.T(labelKey), Dock = DockStyle.Fill, TextAlign = System.Drawing.ContentAlignment.MiddleLeft, Margin = new Padding(0) };
            _texts.Add((lbl, labelKey));
            grid.Controls.Add(lbl, 0, row);
            var panel = new FlowLayoutPanel { Dock = DockStyle.Fill, Margin = new Padding(0, 2, 0, 2), FlowDirection = FlowDirection.LeftToRight, WrapContents = false };
            var check = new CheckBox { Text = I18n.T("enable"), AutoSize = true, Margin = new Padding(0, 4, 6, 0) };
            _texts.Add((check, "enable"));
            var btn = new Button
            {
                Text = I18n.T("pickColor"),
                Width = 110,
                Height = 26,
                BackColor = System.Drawing.Color.FromArgb(255, 255, 255),
                FlatStyle = FlatStyle.Flat,
                Enabled = false,
                Tag = "keep" // 颜色选择色块：保留用户所选颜色，不随主题改变
            };
            _texts.Add((btn, "pickColor"));
            btn.FlatAppearance.BorderColor = System.Drawing.Color.Gray;
            panel.Controls.Add(check);
            panel.Controls.Add(btn);
            TrackBar alphaTrack = null;
            Label alphaLbl = null;
            if (withAlpha)
            {
                // 背景颜色透明通道：0..100% 不透明度滑条（实时回写预览）
                alphaTrack = new TrackBar
                {
                    Minimum = 0, Maximum = 100, Value = 100, TickStyle = TickStyle.None,
                    Width = 90, Height = 24, Margin = new Padding(8, 0, 0, 0)
                };
                alphaLbl = new Label { Text = "100%", AutoSize = true, Margin = new Padding(4, 6, 0, 0) };
                panel.Controls.Add(alphaTrack);
                panel.Controls.Add(alphaLbl);
            }
            grid.Controls.Add(panel, 1, row);
            return (check, btn, alphaTrack, alphaLbl);
        }

        /// <summary>阴影强度行：0..3 档滑条（无/低/中/高）+ 档位文字。</summary>
        private (TrackBar, Label) MakeShadowRow(TableLayoutPanel grid, int row, string labelKey)
        {
            var lbl = new Label { Text = I18n.T(labelKey), Dock = DockStyle.Fill, TextAlign = System.Drawing.ContentAlignment.MiddleLeft, Margin = new Padding(0) };
            _texts.Add((lbl, labelKey));
            grid.Controls.Add(lbl, 0, row);
            var panel = new FlowLayoutPanel { Dock = DockStyle.Fill, Margin = new Padding(0, 2, 0, 2), FlowDirection = FlowDirection.LeftToRight, WrapContents = false };
            var track = new TrackBar
            {
                Minimum = 0,
                Maximum = ShadowItems.Length - 1,
                Width = 160,
                Height = 30,
                TickStyle = TickStyle.BottomRight
            };
            var sLbl = new Label { Text = "", AutoSize = true, Padding = new Padding(4, 6, 0, 0), ForeColor = System.Drawing.Color.DimGray };
            panel.Controls.Add(track);
            panel.Controls.Add(sLbl);
            grid.Controls.Add(panel, 1, row);
            return (track, sLbl);
        }

        /// <summary>平滑着色行：0..100 滑条（0=平涂锐利，100=全平滑）+ 百分数文字。</summary>
        private (TrackBar, Label) MakeSmoothRow(TableLayoutPanel grid, int row, string labelKey)
        {
            var lbl = new Label { Text = I18n.T(labelKey), Dock = DockStyle.Fill, TextAlign = System.Drawing.ContentAlignment.MiddleLeft, Margin = new Padding(0) };
            _texts.Add((lbl, labelKey));
            grid.Controls.Add(lbl, 0, row);
            var panel = new FlowLayoutPanel { Dock = DockStyle.Fill, Margin = new Padding(0, 2, 0, 2), FlowDirection = FlowDirection.LeftToRight, WrapContents = false };
            var track = new TrackBar
            {
                Minimum = 0,
                Maximum = 100,
                SmallChange = 5,
                LargeChange = 10,
                Width = 160,
                Height = 30,
                TickStyle = TickStyle.BottomRight
            };
            var smLbl = new Label { Text = "", AutoSize = true, Padding = new Padding(4, 6, 0, 0), ForeColor = System.Drawing.Color.DimGray };
            panel.Controls.Add(track);
            panel.Controls.Add(smLbl);
            grid.Controls.Add(panel, 1, row);
            return (track, smLbl);
        }

        /// <summary>诊断日志行：勾选开关 + 打开日志文件按钮。</summary>
        private (CheckBox, Button) MakeLogRow(TableLayoutPanel grid, int row)
        {
            var lbl = new Label { Text = I18n.T("diagLog"), Dock = DockStyle.Fill, TextAlign = System.Drawing.ContentAlignment.MiddleLeft, Margin = new Padding(0) };
            _texts.Add((lbl, "diagLog"));
            grid.Controls.Add(lbl, 0, row);
            var panel = new FlowLayoutPanel { Dock = DockStyle.Fill, Margin = new Padding(0, 2, 0, 2), FlowDirection = FlowDirection.LeftToRight, WrapContents = false };
            var check = new CheckBox
            {
                Text = I18n.T("logRecord"),
                AutoSize = true,
                Margin = new Padding(0, 4, 6, 0)
            };
            _texts.Add((check, "logRecord"));
            var btn = new Button { Text = I18n.T("openLog"), Width = 90, Height = 26 };
            _texts.Add((btn, "openLog"));
            panel.Controls.Add(check);
            panel.Controls.Add(btn);
            grid.Controls.Add(panel, 1, row);
            return (check, btn);
        }

        /// <summary>负零件行：显示开关 + 颜色按钮 + 不透明度滑条（0..100，拖动实时回写）。</summary>
        private (CheckBox, Button, TrackBar, Label) MakeNegativeRow(TableLayoutPanel grid, int row, string labelKey)
        {
            var lbl = new Label { Text = I18n.T(labelKey), Dock = DockStyle.Fill, TextAlign = System.Drawing.ContentAlignment.MiddleLeft, Margin = new Padding(0) };
            _texts.Add((lbl, labelKey));
            grid.Controls.Add(lbl, 0, row);
            var panel = new FlowLayoutPanel { Dock = DockStyle.Fill, Margin = new Padding(0, 2, 0, 2), FlowDirection = FlowDirection.LeftToRight, WrapContents = false };
            var check = new CheckBox { Text = I18n.T("show"), AutoSize = true, Margin = new Padding(0, 4, 4, 0) };
            _texts.Add((check, "show"));
            var btn = new Button
            {
                Text = I18n.T("color"),
                Width = 78,
                Height = 26,
                BackColor = System.Drawing.Color.White,
                FlatStyle = FlatStyle.Flat,
                Tag = "keep" // 颜色选择色块：保留用户所选颜色，不随主题改变
            };
            _texts.Add((btn, "color"));
            btn.FlatAppearance.BorderColor = System.Drawing.Color.Gray;
            var track = new TrackBar
            {
                Minimum = 0,
                Maximum = 100,
                SmallChange = 5,
                LargeChange = 10,
                Width = 130,
                Height = 30,
                TickStyle = TickStyle.BottomRight
            };
            var opLbl = new Label { Text = "", AutoSize = true, Padding = new Padding(4, 6, 0, 0), ForeColor = System.Drawing.Color.DimGray };
            panel.Controls.Add(check);
            panel.Controls.Add(btn);
            panel.Controls.Add(track);
            panel.Controls.Add(opLbl);
            grid.Controls.Add(panel, 1, row);
            return (check, btn, track, opLbl);
        }

        /// <summary>背景图片行：勾选"启用"后可选图片文件，右侧显示文件名 + 恢复默认按钮（同时清除颜色与图片）。</summary>
        private (CheckBox, Button, Label, Button) MakeImageRow(TableLayoutPanel grid, int row, string labelKey)
        {
            var lbl = new Label { Text = I18n.T(labelKey), Dock = DockStyle.Fill, TextAlign = System.Drawing.ContentAlignment.MiddleLeft, Margin = new Padding(0) };
            _texts.Add((lbl, labelKey));
            grid.Controls.Add(lbl, 0, row);
            var panel = new FlowLayoutPanel { Dock = DockStyle.Fill, Margin = new Padding(0, 2, 0, 2), FlowDirection = FlowDirection.LeftToRight, WrapContents = false };
            var check = new CheckBox { Text = I18n.T("enable"), AutoSize = true, Margin = new Padding(0, 4, 6, 0) };
            _texts.Add((check, "enable"));
            var btn = new Button
            {
                Text = I18n.T("pickImage"),
                Width = 100,
                Height = 26,
                Enabled = false
            };
            _texts.Add((btn, "pickImage"));
            var reset = new Button
            {
                Text = I18n.T("resetDefault"),
                Width = 80,
                Height = 26,
                Enabled = false
            };
            _texts.Add((reset, "resetDefault"));
            var fileLbl = new Label
            {
                Text = I18n.T("notSelected"),
                AutoSize = true,
                AutoEllipsis = true,
                MaximumSize = new System.Drawing.Size(88, 20),
                Padding = new Padding(4, 6, 0, 0),
                ForeColor = System.Drawing.Color.DimGray
            };
            panel.Controls.Add(check);
            panel.Controls.Add(btn);
            panel.Controls.Add(reset);
            panel.Controls.Add(fileLbl);
            grid.Controls.Add(panel, 1, row);
            return (check, btn, fileLbl, reset);
        }

        /// <summary>用系统记事本打开诊断日志文件（不存在则仅提示）。</summary>
        private void OpenLogFile()
        {
            try
            {
                var path = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "3DThumbnailShell", "previewer.log");
                if (!System.IO.File.Exists(path))
                {
                    MessageBox.Show(this, "日志文件尚不存在。\n先勾选 记录日志 并复现问题后再打开。\n路径: " + path,
                            "诊断日志", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                System.Diagnostics.Process.Start("notepad.exe", path);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "打开日志失败: " + ex.Message, "诊断日志",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void LoadValues()
        {
            _loading = true;
            _backendCombo.SelectedIndex = (int)_settings.Mode;
            // GPU 适配器：匹配当前设置；未命中（如 GPU 驱动/型号变更）回落"自动"
            var gpuIdx = Array.IndexOf(_gpuNames, _settings.GpuAdapterName, 1);
            _gpuCombo.SelectedIndex = gpuIdx < 0 ? 0 : gpuIdx;
            _resScaleCombo.SelectedIndex = IndexOf(ResScaleVals, _settings.ResolutionScale, 1);
            _ssaaCombo.SelectedIndex = IndexOf(SsaaVals, _settings.Ssaa, 1);
            _interactCombo.SelectedIndex = IndexOf(InteractVals, _settings.InteractScale, 1);
            _maxTriCombo.SelectedIndex = IndexOf(MaxTriVals, _settings.MaxTriangles, 0);
            _presetCombo.SelectedIndex = (int)_settings.Preset + 1;

            // 模型颜色
            var col = _settings.ParseModelColor();
            _colorCheck.Checked = col.HasValue;
            _colorBtn.Enabled = col.HasValue;
            if (col.HasValue)
                _colorBtn.BackColor = System.Drawing.Color.FromArgb(
                    (int)(col.Value.X * 255f), (int)(col.Value.Y * 255f), (int)(col.Value.Z * 255f));

            // 阴影强度
            var sIdx = IndexOf(ShadowVals, _settings.ShadowLevel, ShadowVals.Length - 1);
            _shadowTrack.Value = sIdx;
            _shadowLabel.Text = I18n.T(ShadowKeys[sIdx]);

            // 平滑着色（0..100 → 0..1）
            var smVal = (int)Math.Round(_settings.SmoothShading * 100f);
            _smoothTrack.Value = smVal;
            _smoothLabel.Text = smVal + "%";

            // 诊断日志开关
            _logCheck.Checked = _settings.DiagnosticsLogEnabled;

            // 负零件：显示开关 + 颜色 + 不透明度（0..100 → 0..1）
            _negCheck.Checked = _settings.ShowNegativeParts;
            _negBtn.Enabled = _settings.ShowNegativeParts;
            _negTrack.Enabled = _settings.ShowNegativeParts;
            var ncol = _settings.ParseNegativeColor();
            _negColorPicked = ncol.HasValue;
            _negBtn.BackColor = ncol.HasValue
                ? System.Drawing.Color.FromArgb(
                    (int)(ncol.Value.X * 255f), (int)(ncol.Value.Y * 255f), (int)(ncol.Value.Z * 255f))
                : System.Drawing.Color.White;
            var opVal = (int)Math.Round(_settings.NegativeOpacity * 100f);
            _negTrack.Value = opVal;
            _negLbl.Text = opVal + "%";

            // 修改器：显示开关 + 颜色 + 不透明度
            _modCheck.Checked = _settings.ShowModifierParts;
            _modBtn.Enabled = _settings.ShowModifierParts;
            _modTrack.Enabled = _settings.ShowModifierParts;
            var mcol = _settings.ParseModifierColor();
            _modColorPicked = mcol.HasValue;
            _modBtn.BackColor = mcol.HasValue
                ? System.Drawing.Color.FromArgb(
                    (int)(mcol.Value.X * 255f), (int)(mcol.Value.Y * 255f), (int)(mcol.Value.Z * 255f))
                : System.Drawing.Color.White;
            var modOpVal = (int)Math.Round(_settings.ModifierOpacity * 100f);
            _modTrack.Value = modOpVal;
            _modLbl.Text = modOpVal + "%";

            // 背景颜色
            var bgcol = _settings.ParseBackgroundColor();
            _bgColorCheck.Checked = bgcol.HasValue && string.IsNullOrEmpty(_settings.BackgroundImagePath);
            _bgColorBtn.Enabled = _bgColorCheck.Checked;
            _bgColorBtn.BackColor = bgcol.HasValue
                ? System.Drawing.Color.FromArgb(
                    (int)(bgcol.Value.X * 255f), (int)(bgcol.Value.Y * 255f), (int)(bgcol.Value.Z * 255f))
                : System.Drawing.Color.White;
            // 背景透明通道（alpha）：回填滑条；_loading 置真期间 ValueChanged 不会误写
            var bgA = bgcol.HasValue ? (int)Math.Round(bgcol.Value.W * 100f) : 100;
            _bgAlphaTrack.Value = Math.Min(100, Math.Max(0, bgA));
            _bgAlphaLbl.Text = _bgAlphaTrack.Value + "%";
            _bgAlphaTrack.Enabled = _bgColorCheck.Checked;

            // 背景图片（图片优先于颜色）
            var bgImg = _settings.BackgroundImagePath;
            _bgImgCheck.Checked = !string.IsNullOrEmpty(bgImg) && System.IO.File.Exists(bgImg);
            _bgImgBtn.Enabled = _bgImgCheck.Checked;
            _bgImgPath = _bgImgCheck.Checked ? bgImg : null;
            _bgImgLbl.Text = _bgImgCheck.Checked ? System.IO.Path.GetFileName(bgImg) : I18n.T("notSelected");
            _bgImgLbl.ForeColor = System.Drawing.Color.DimGray;
            // 有自定义背景（颜色或图片）时才允许恢复默认
            _bgImgReset.Enabled = _bgColorCheck.Checked || _bgImgCheck.Checked;

            // 启动弹窗开关
            _welcomeCheck.Checked = _settings.ShowWelcome;

            // 自动取景
            _autoFitCheck.Checked = _settings.AutoFitView;

            // 地板网格
            _floorCheck.Checked = _settings.ShowFloorGrid;

            // 界面主题
            _themeCombo.SelectedIndex = _settings.UiTheme == UiThemeMode.Dark ? 1 : 0;

            // Gizmo 尺寸
            _gizmoCombo.SelectedIndex = IndexOf(GizmoScaleVals, _settings.GizmoScale, 2);

            // 语言
            _langCombo.SelectedIndex = _settings.Language == "en" ? 1 : 0;

            // 缩略图保存比例
            _thumbAspectCombo.SelectedIndex = Math.Max(0, _thumbAspectCombo.Items.IndexOf(_settings.ThumbnailAspect));

            _loading = false;
        }

        private void OnPresetChanged(object sender, EventArgs e)
        {
            if (_loading || _presetCombo.SelectedIndex <= 0) return;
            var preset = (QualityPreset)(_presetCombo.SelectedIndex - 1);
            var s = RenderSettings.FromPreset(preset);
            _loading = true;
            _resScaleCombo.SelectedIndex = IndexOf(ResScaleVals, s.ResolutionScale, 1);
            _ssaaCombo.SelectedIndex = IndexOf(SsaaVals, s.Ssaa, 1);
            _interactCombo.SelectedIndex = IndexOf(InteractVals, s.InteractScale, 1);
            _maxTriCombo.SelectedIndex = IndexOf(MaxTriVals, s.MaxTriangles, 0);
            _loading = false;
        }

        /// <summary>当前背景颜色写回 hex：#RRGGBBAA（8 位，含透明通道 alpha，取自滑条；100%=FF 不透明）。</summary>
        private string BgColorHex()
        {
            var c = _bgColorBtn.BackColor;
            var a = (int)Math.Round(_bgAlphaTrack.Value / 100f * 255f);
            return "#" + c.R.ToString("X2") + c.G.ToString("X2") + c.B.ToString("X2") + a.ToString("X2");
        }

        private void Apply()
        {
            _settings.ResolutionScale = ResScaleVals[ClampIdx(_resScaleCombo.SelectedIndex, ResScaleVals.Length)];
            _settings.Ssaa = SsaaVals[ClampIdx(_ssaaCombo.SelectedIndex, SsaaVals.Length)];
            _settings.InteractScale = InteractVals[ClampIdx(_interactCombo.SelectedIndex, InteractVals.Length)];
            _settings.MaxTriangles = MaxTriVals[ClampIdx(_maxTriCombo.SelectedIndex, MaxTriVals.Length)];
            _settings.Mode = (RendererMode)ClampIdx(_backendCombo.SelectedIndex, 3);
            _settings.GpuAdapterName = _gpuCombo.SelectedIndex > 0 ? _gpuNames[_gpuCombo.SelectedIndex] : null;
            _settings.Preset = _presetCombo.SelectedIndex > 0
                ? (QualityPreset)(_presetCombo.SelectedIndex - 1)
                : _settings.MatchPreset();
            // 模型颜色 + 阴影
            _settings.ModelColorHex = _colorCheck.Checked
                ? ("#" + _colorBtn.BackColor.R.ToString("X2") + _colorBtn.BackColor.G.ToString("X2") + _colorBtn.BackColor.B.ToString("X2"))
                : null;
            _settings.ShadowLevel = ShadowVals[ClampIdx(_shadowTrack.Value, ShadowVals.Length)];
            _settings.SmoothShading = _smoothTrack.Value / 100f;
            _settings.DiagnosticsLogEnabled = _logCheck.Checked;
            _settings.ShowNegativeParts = _negCheck.Checked;
            _settings.NegativeColorHex = _negColorPicked
                ? ("#" + _negBtn.BackColor.R.ToString("X2") + _negBtn.BackColor.G.ToString("X2") + _negBtn.BackColor.B.ToString("X2"))
                : null;
            _settings.NegativeOpacity = _negTrack.Value / 100f;
            _settings.ShowModifierParts = _modCheck.Checked;
            _settings.ModifierColorHex = _modColorPicked
                ? ("#" + _modBtn.BackColor.R.ToString("X2") + _modBtn.BackColor.G.ToString("X2") + _modBtn.BackColor.B.ToString("X2"))
                : null;
            _settings.ModifierOpacity = _modTrack.Value / 100f;
            // 背景：图片优先于颜色。图片勾选且路径有效 → 用图片；否则看颜色勾选。
            if (_bgImgCheck.Checked && !string.IsNullOrEmpty(_bgImgPath) && System.IO.File.Exists(_bgImgPath))
            {
                _settings.BackgroundImagePath = _bgImgPath;
            }
            else
            {
                _settings.BackgroundImagePath = null;
                _settings.BackgroundColorHex = _bgColorCheck.Checked
                    ? BgColorHex()
                    : null;
            }
            _settings.ShowWelcome = _welcomeCheck.Checked;
            _settings.AutoFitView = _autoFitCheck.Checked;
            _settings.ShowFloorGrid = _floorCheck.Checked;
            _settings.GizmoScale = GizmoScaleVals[ClampIdx(_gizmoCombo.SelectedIndex, GizmoScaleVals.Length)];
            _settings.ThumbnailAspect = _thumbAspectCombo.SelectedItem as string ?? "1:1";
            _settings.UiTheme = _themeCombo.SelectedIndex == 1 ? UiThemeMode.Dark : UiThemeMode.Light;
            var lang = _langCombo.SelectedIndex == 1 ? "en" : "zh";
            I18n.SetLanguage(lang);
            _settings.Language = lang;
            _settings.Save();
            DiagnosticLog.RefreshEnabled();
            _onApplied?.Invoke(_settings);
        }

        /// <summary>单行开关：左侧文字 + 右侧复选框（启动弹窗、自动取景等）。</summary>
        private CheckBox MakeCheckRow(TableLayoutPanel grid, int row, string labelKey, string checkKey)
        {
            var lbl = new Label
            {
                Text = I18n.T(labelKey),
                Dock = DockStyle.Fill,
                TextAlign = System.Drawing.ContentAlignment.MiddleLeft,
                Margin = new Padding(0)
            };
            _texts.Add((lbl, labelKey));
            grid.Controls.Add(lbl, 0, row);
            var check = new CheckBox
            {
                Text = I18n.T(checkKey),
                AutoSize = true,
                Margin = new Padding(4, 12, 0, 0)
            };
            _texts.Add((check, checkKey));
            grid.Controls.Add(check, 1, row);
            return check;
        }

        private static int ClampIdx(int idx, int count) => idx < 0 ? 0 : (idx >= count ? count - 1 : idx);

        private static int IndexOf(float[] vals, float v, int fallback)
        {
            for (var i = 0; i < vals.Length; i++)
                if (Math.Abs(vals[i] - v) < 1e-4f) return i;
            return fallback;
        }

        private static int IndexOf(int[] vals, int v, int fallback)
        {
            for (var i = 0; i < vals.Length; i++)
                if (vals[i] == v) return i;
            return fallback;
        }
    }
}
