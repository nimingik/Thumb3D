using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Numerics;
using _3DThumbnailShell.Core.Formats;
using _3DThumbnailShell.Core.Renderer;

namespace _3DThumbnailShell.Previewer
{
    /// <summary>
    /// 3D 预览器主界面：
    /// - 打开单个文件 / 打开文件夹（自动列出文件夹内所有 3D 文件并加载第一个，可点选切换）
    /// - 3D 交互视图（旋转/缩放/平移，见 Viewport3D）
    /// - 高分辨率 2x 超采样渲染 + 双三次插值显示，解决图片糊
    /// - 另存 PNG / 缩略图扩展管理
    /// </summary>
    public sealed class MainForm : Form
    {
        private static readonly string[] Exts =
            { ".stl", ".obj", ".3mf", ".ply", ".off", ".amf", ".glb", ".gltf",
              ".gcode", ".gco", ".ctb", ".photon", ".pwmx", ".pwmo", ".pws", ".form",
              ".step", ".stp", ".c4d", ".sldprt", ".sldasm", ".ipt", ".iam", ".f3d", ".blend" };

        private readonly Button _openBtn;
        private readonly Button _folderBtn;
        private readonly Button _saveBtn;
        private readonly Button _thumbBtn;
        private readonly Button _batchThumbBtn;
        private readonly Button _customThumbBtn;
        private readonly Button _resetBtn;
        private readonly Button _illumBtn;       // 照明（补光）：抬亮纯黑/深色模型
        private readonly Button _settingsBtn;
        private readonly Button _extOpenBtn;     // 用其他软件打开当前文件
        private readonly Label _renderStatus;
        private readonly Label _extLabel;        // 扩展管理标签（可随语言重译）
        private readonly Button _installBtn;     // 安装缩略图扩展
        private readonly Button _installAllBtn;  // 为所有用户安装(UAC)
        private readonly Button _uninstallBtn;   // 卸载扩展
        private readonly Button _restartBtn;     // 重启资源管理器
        private readonly Viewport3D _view;
        private readonly RichTextBox _info;
        private readonly ListBox _fileList;
        private readonly ComboBox _filterCombo;   // 左侧文件类型筛选
        private readonly ComboBox _plateCombo;    // 顶部盘切换（3MF 多盘工程）
        private readonly TextBox _searchBox;      // 左侧搜索框（Everything 式 MFT 快速搜索）
        private readonly System.Windows.Forms.Timer _searchTimer;
        private readonly string _initial;
        private readonly string _dumpRenderPath; // 诊断模式：渲染完成后存 PNG 并退出
        private bool _dumpArmed;      // 主网格已 SetMesh 后再开始转储
        private bool _dumpDone;
        private int _dumpPlateIdx = -1; // -1=合并视图，0..n-1=各单盘
        private readonly List<string> _folderFiles = new List<string>();   // 文件夹全量 3D 文件
        private readonly List<string> _filteredFiles = new List<string>(); // 筛选/搜索后显示列表
        private string _currentName;   // 当前文件名（缩略图默认名）
        private string _currentPath;   // 当前文件完整路径（侧车缩略图目标）
        private string _currentFolder; // 当前打开的文件夹（搜索范围）
        private bool _batchRunning;    // 批量缩略图任务进行中
        private int _searchVersion;    // 搜索防抖版本号（丢弃过期结果）
        private List<PlateInfo> _currentPlates; // 当前 3MF 的多盘列表（Count>1 时可切换）
        private MeshData _currentMesh;   // 当前解析的主 MeshData（全部盘合并视图，盘切换回退用）
        private bool _plateSwitching;  // 盘下拉框填充/切换防重入
        private int _loadVersion;      // 加载版本号：连续点选时丢弃过期解析结果

        /// <summary>从程序集内嵌资源加载软件图标（Resources/app.ico），用作主窗口/标题栏图标。</summary>
        private static Icon LoadAppIcon()
        {
            try
            {
                var asm = typeof(MainForm).Assembly;
                var name = asm.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith("app.ico", StringComparison.OrdinalIgnoreCase));
                if (name != null)
                    using (var s = asm.GetManifestResourceStream(name))
                        if (s != null) return new Icon(s);
            }
            catch { }
            return null;
        }

        public MainForm(string initial, string dumpRenderPath = null)
        {
            _initial = initial;
            _dumpRenderPath = dumpRenderPath;
            // 界面语言：设置里持久化的 Language（zh/en），构建前应用
            I18n.SetLanguage(RenderSettings.Load().Language);
            Text = I18n.T("mainTitle") + "  " + AppInfo.Version;
            Icon = LoadAppIcon();
            Size = new Size(1366, 768);
            MinimumSize = new Size(900, 560);

            // 拖动文件到窗口打开
            AllowDrop = true;
            DragEnter += OnDragEnter;
            DragDrop += OnDragDrop;

            // 顶部工具栏：打开文件 / 打开文件夹 / 复位视图 / 保存 PNG
            var top = new FlowLayoutPanel
            {
                Dock = DockStyle.Top,
                Height = 40,
                Padding = new Padding(6),
                WrapContents = false
            };

            _openBtn = new Button { Text = I18n.T("openFile"), AutoSize = true };
            _openBtn.Click += (s, e) => OpenFile();

            _folderBtn = new Button { Text = I18n.T("openFolder"), AutoSize = true };
            _folderBtn.Click += (s, e) => OpenFolder();

            _resetBtn = new Button { Text = I18n.T("resetView"), AutoSize = true, Enabled = false };
            _resetBtn.Click += (s, e) => _view.ResetView();

            // 照明（补光）：黑色/深色模型（黑色耗材、深色多色件）基色接近 0，只加光也乘不出亮度，
            // 故开灯时同时抬升材质基色并提高环境光；开启态用主题高光色标示（与"安装扩展"同款强调）。
            _illumBtn = new Button { Text = I18n.T("illumination"), AutoSize = true };
            _illumBtn.Click += (s, e) =>
            {
                var st = _view.Settings;
                st.Illumination = st.Illumination > 0f ? 0f : 1f; // 开/关
                st.Save();
                _view.Settings = st; // 触发重渲染（GPU 缓冲按照明强度重建）
                UpdateIllumButton();
                _renderStatus.Text = st.Illumination > 0f ? "  照明: 开" : "  照明: 关";
            };

            _saveBtn = new Button { Text = I18n.T("savePng"), AutoSize = true, Enabled = false };
            _saveBtn.Click += (s, e) => SavePng();

            _thumbBtn = new Button { Text = I18n.T("saveThumb"), AutoSize = true, Enabled = false };
            _thumbBtn.Click += (s, e) => SaveThumbnail();

            _batchThumbBtn = new Button { Text = I18n.T("batchThumb"), AutoSize = true, Enabled = false };
            _batchThumbBtn.Click += (s, e) => BatchSaveThumbnails();

            _customThumbBtn = new Button { Text = I18n.T("customThumb"), AutoSize = true, Enabled = false };
            _customThumbBtn.Click += (s, e) => CustomThumbnail();

            _settingsBtn = new Button { Text = I18n.T("settings"), AutoSize = true };
            _settingsBtn.Click += (s, e) =>
            {
                var themeBefore = _view.Settings.UiTheme;
                using var f = new SettingsForm(_view.Settings, st =>
                {
                    _view.Settings = st;
                    if (st.UiTheme != themeBefore) // 主题切换：实时重应用主窗口配色
                    {
                        themeBefore = st.UiTheme;
                        UiTheme.ApplyTo(this, st.UiTheme);
                    }
                    RefreshTexts(); // 语言切换（如发生在设置里）后实时重译主窗口
                });
                f.ShowDialog(this);
            };

            _extOpenBtn = new Button { Text = I18n.T("openWithOther"), AutoSize = true, Enabled = false };
            _extOpenBtn.Click += (s, e) => OpenWithExternal();

            _renderStatus = new Label
            {
                Text = "",
                AutoSize = true,
                ForeColor = Color.Gray,
                Padding = new Padding(0, 6, 0, 0)
            };

            // 盘切换（3MF 多盘工程）：仅当当前模型有多盘时显示，可切换查看每一盘
            _plateCombo = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Width = 150,
                Visible = false
            };
            _plateCombo.SelectedIndexChanged += (s, e) => OnPlateChanged();

            top.Controls.Add(_openBtn);
            top.Controls.Add(_folderBtn);
            top.Controls.Add(_resetBtn);
            top.Controls.Add(_illumBtn);
            top.Controls.Add(new Label { Text = "  " });
            top.Controls.Add(_saveBtn);
            top.Controls.Add(_thumbBtn);
            top.Controls.Add(_batchThumbBtn);
            top.Controls.Add(_customThumbBtn);
            top.Controls.Add(_plateCombo);
            top.Controls.Add(_settingsBtn);
            top.Controls.Add(_extOpenBtn);

            // 第二行：缩略图扩展管理（免管理员，注册当前用户）
            var extBar = new FlowLayoutPanel
            {
                Dock = DockStyle.Top,
                Height = 38,
                Padding = new Padding(6),
                BackColor = Color.FromArgb(244, 244, 244),
                WrapContents = false, // 状态信息（筛选/GPU）不换行，超出用滚动
                AutoScroll = true
            };
            _extLabel = new Label { Text = I18n.T("extManage"), AutoSize = true, ForeColor = Color.Gray, Padding = new Padding(0, 6, 0, 0) };
            _installBtn = new Button { Text = I18n.T("installExt"), AutoSize = true, Tag = "accent" };
            _installBtn.Click += (s, e) => DoExt("install");
            _installAllBtn = new Button { Text = I18n.T("installAllUac"), AutoSize = true };
            _installAllBtn.Click += (s, e) => DoExtElevated("install");
            _uninstallBtn = new Button { Text = I18n.T("uninstallExt"), AutoSize = true };
            _uninstallBtn.Click += (s, e) => DoExt("uninstall");
            _restartBtn = new Button { Text = I18n.T("restartExplorer"), AutoSize = true };
            _restartBtn.Click += (s, e) => RestartExplorer();
            extBar.Controls.Add(_extLabel);
            extBar.Controls.Add(_installBtn);
            extBar.Controls.Add(_installAllBtn);
            extBar.Controls.Add(_uninstallBtn);
            extBar.Controls.Add(_restartBtn);
            // 状态信息（筛选计数 / GPU 型号·渲染耗时）：放在扩展管理行最右侧空白处
            extBar.Controls.Add(_renderStatus);

            // 左侧：文件类型筛选 + 搜索框 + 3D 文件列表。
            // 布局用 Anchor 绝对定位而非 Dock 混排：实测 Dock=Fill 会先布局占满整个面板，
            // 把后续 Top 控件（搜索框）盖住，导致搜索框/列表不可见。Anchor 方案完全可控。
            var left = new Panel { Dock = DockStyle.Left, Width = 240 };

            _searchBox = new TextBox
            {
                Location = new Point(0, 27),
                Size = new Size(240, 23),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
                Margin = new Padding(0),
                Font = new Font("Microsoft YaHei UI", 9f)
            };
            _searchBox.TextChanged += (s, e) => OnSearchTextChanged();

            _filterCombo = new ComboBox
            {
                Location = new Point(0, 0),
                Size = new Size(240, 26),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
                DropDownStyle = ComboBoxStyle.DropDownList
            };
            _filterCombo.Items.Add("全部类型 (模型/切片/CAD)");
            foreach (var ext in Exts)
                _filterCombo.Items.Add(ext.ToUpperInvariant() + " 文件");
            _filterCombo.SelectedIndex = 0;
            _filterCombo.SelectedIndexChanged += (s, e) => ApplyFilter(false);

            _fileList = new ListBox
            {
                Location = new Point(0, 55),
                Size = new Size(240, 200),
                Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
                IntegralHeight = false,
                ScrollAlwaysVisible = true // 始终显示滚动条（文件少时也可预知可滚动）
            };
            _fileList.SelectedIndexChanged += (s, e) => OnFileSelected();
            _fileList.DoubleClick += (s, e) => OpenFile();

            left.Controls.Add(_filterCombo);
            left.Controls.Add(_searchBox);
            left.Controls.Add(_fileList);

            // 搜索防抖：停止输入 300ms 后触发（避免每敲一个字符就搜一次）
            _searchTimer = new System.Windows.Forms.Timer { Interval = 300 };
            _searchTimer.Tick += (s, e) => { _searchTimer.Stop(); ApplyFilter(true); };

            _view = new Viewport3D { Dock = DockStyle.Fill };
            _view.RenderCompleted += (ms, backend) =>
            {
                if (IsDisposed) return;
                // 渲染后端/耗时 → 第二行右侧状态区（扩展管理行）
                _renderStatus.Text = $"  {backend} · {ms} ms";
                // 诊断模式：--dump-render <前缀> —— 合并视图 + 各单盘渲染完成后各存一张 PNG 再退出
                if (_dumpRenderPath != null && _dumpArmed && !_dumpDone)
                    DumpNextFrame();
            };
            _info = new RichTextBox
            {
                Dock = DockStyle.Bottom,
                Height = 120,
                ReadOnly = true,
                BackColor = Color.FromArgb(250, 250, 250)
            };

            Controls.Add(_view);
            Controls.Add(left);
            Controls.Add(_info);
            Controls.Add(extBar);
            Controls.Add(top);
            left.BringToFront();
            _info.BringToFront();
            top.BringToFront();
            extBar.BringToFront();
            UpdateIllumButton(); // 按已保存的设置显示"照明"按钮的开/关状态

            // 启动即填充左侧文件浏览：有初始文件 → 扫其所在目录；否则扫"最近使用的文件夹"或下载目录。
            // 后台扫描避免大目录卡启动 UI，回来后刷新列表。
            var startDir = (_initial != null && File.Exists(_initial)) ? Path.GetDirectoryName(_initial) : null;
            if (string.IsNullOrEmpty(startDir) || !Directory.Exists(startDir))
            {
                var lastDir = RenderSettings.Load().LastFolder;
                startDir = string.IsNullOrEmpty(lastDir) || !Directory.Exists(lastDir)
                    ? null : lastDir;
            }
            if (startDir != null)
            {
                _currentFolder = startDir;
                Task.Run(() =>
                {
                    var found = SafeScanFolder(startDir);
                    if (found == null) return;
                    if (IsDisposed) return;
                    try
                    {
                        BeginInvoke(new Action(() =>
                        {
                            try
                            {
                                if (IsDisposed) return;
                                _folderFiles.Clear();
                                _folderFiles.AddRange(found);
                                _currentFolder = startDir;
                                RenderSettings.Patch(s => s.LastFolder = startDir);
                                ApplyFilterResult(found, _searchBox.Text.Trim());
                                _info.Text = $"文件夹: {startDir}\r\n找到 {found.Count} 个 3D 文件，已自动加载第一个。点左侧列表切换。\r\n搜索框支持 Everything 式 MFT 快速搜索（需管理员权限，失败自动回退普通扫描）。\r\n";
                            }
                            catch (Exception ex)
                            {
                                _info.Text = "初始化文件夹失败: " + ex.Message;
                            }
                        }));
                    }
                    catch
                    {
                    }
                });
            }

            if (!string.IsNullOrEmpty(_initial) && File.Exists(_initial))
                LoadAndRender(_initial);

            DiagnosticLog.Log("启动完成 日志开关=" + DiagnosticLog.IsEnabled +
                " initial=" + (_initial ?? "(null)"));

            // 启动开始弹窗：首次显示主窗口后先弹"更新内容"（可勾选下次不再显示），再弹欢迎/介绍
            Shown += (s, e) =>
            {
                try
                {
                    if (_dumpRenderPath != null) return; // 诊断模式跳过开始弹窗
                    if (_view.Settings.ShowUpdateNotes)
                    {
                        using var c = new ChangelogForm(_view.Settings);
                        c.ShowDialog(this);
                    }
                    if (_view.Settings.ShowWelcome)
                    {
                        using var w = new WelcomeForm(_view.Settings);
                        w.ShowDialog(this);
                    }
                }
                catch (Exception ex)
                {
                    DiagnosticLog.Log("开始弹窗显示失败: " + ex.Message);
                }
            };

            // 启动时应用界面主题（亮色/暗色），3D 画布底色/占位文字/标题栏同步跟随
            UiTheme.ApplyTo(this, _view.Settings.UiTheme);
            _view.ApplyTheme(_view.Settings.UiTheme);
        }

        /// <summary>确保左侧列表显示当前文件所在目录的文件（无论通过 _initial、单文件对话框还是双击打开）。
        /// 同步阶段不重建已有列表（避免点选文件时列表闪断）；后台扫描完成才整体刷新，并校验目录未变，丢弃过期结果。</summary>
        private void EnsureSideList(string path)
        {
            var dir = Path.GetDirectoryName(path);
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;

            // 列表已有内容（打开文件夹树 / 多选 / 单文件模式）→ 不因点击列表项而收缩列表。
            // 旧逻辑：文件在子文件夹时（dir != _currentFolder）会用"该子文件夹"的扫描结果替换整个
            // 列表，导致"打开父文件夹后点 stl 文件，所有文件消失一大半"。仅当列表为空
            // （如命令行 _initial 首启、列表尚无任何文件）才扫描该目录填充。
            if (_folderFiles.Count > 0) return;

            _currentFolder = dir;
            RenderSettings.Patch(s => s.LastFolder = dir);

            // 列表为空时先放入当前文件（保证立即非空），后台再补扫全目录树
            _folderFiles.Add(path);
            ApplyFilterResult(new List<string> { path }, _searchBox.Text.Trim());

            // 后台补扫全目录树刷新
            Task.Run(() =>
            {
                var found = SafeScanFolder(dir);
                if (found == null || IsDisposed) return;
                try
                {
                    BeginInvoke(new Action(() =>
                    {
                        if (IsDisposed) return;
                        // 过期结果：当前目录已切换则丢弃
                        if (_currentFolder == null ||
                            !_currentFolder.Equals(dir, StringComparison.OrdinalIgnoreCase)) return;
                        var wasSel = _currentPath;
                        _folderFiles.Clear();
                        _folderFiles.AddRange(found);
                        RefreshFilteredList(); // 应用当前筛选 + 搜索词（而非直接全量显示）
                        // 尝试恢复选中当前文件
                        if (wasSel != null)
                        {
                            var i = found.FindIndex(x => x.Equals(wasSel, StringComparison.OrdinalIgnoreCase));
                            if (i >= 0) _fileList.SelectedIndex = i;
                        }
                    }));
                }
                catch
                {
                }
            });
        }

        /// <summary>扫描文件夹树中的 3D 文件（不弹错误，失败/为空返回 null）。逐目录容错递归：无权限子目录跳过，不让整个扫描失败。</summary>
        private List<string> SafeScanFolder(string folder)
        {
            var result = new List<string>();
            try
            {
                CollectFiles(folder, result);
            }
            catch
            {
                return result.Count == 0 ? null : result; // 顶层不可访问 → 视为失败返回 null；否则返回已收集结果
            }
            return result.OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
        }

        private void CollectFiles(string folder, List<string> result)
        {
            IEnumerable<string> here;
            try
            {
                here = Directory.EnumerateFiles(folder);
            }
            catch
            {
                return; // 无权限子目录：跳过，不中断递归
            }
            foreach (var f in here)
                if (Array.IndexOf(Exts, Path.GetExtension(f).ToLowerInvariant()) >= 0)
                    result.Add(f);

            IEnumerable<string> dirs;
            try
            {
                dirs = Directory.EnumerateDirectories(folder);
            }
            catch
            {
                return;
            }
            foreach (var d in dirs)
                CollectFiles(d, result);
        }

        /// <summary>照明按钮外观：开启时用主题高光色标示（与"安装缩略图扩展"同款强调），关闭时回到普通按钮。</summary>
        private void UpdateIllumButton()
        {
            var on = _view.Settings.Illumination > 0f;
            _illumBtn.Tag = on ? "accent" : null; // 开启态用主题高光色（ApplyTo 据此着色）
            _illumBtn.Text = I18n.T("illumination") + (on ? " ✓" : "");
            UiTheme.ApplyTo(_illumBtn, _view.Settings.UiTheme);
        }

        /// <summary>按当前语言重译主窗口：标题、工具栏按钮、扩展管理行。</summary>
        private void RefreshTexts()
        {
            Text = I18n.T("mainTitle") + "  " + AppInfo.Version;
            _openBtn.Text = I18n.T("openFile");
            _folderBtn.Text = I18n.T("openFolder");
            _resetBtn.Text = I18n.T("resetView");
            UpdateIllumButton(); // 照明按钮文字 + 开/关状态一起刷新
            _saveBtn.Text = I18n.T("savePng");
            _thumbBtn.Text = I18n.T("saveThumb");
            _batchThumbBtn.Text = I18n.T("batchThumb");
            _customThumbBtn.Text = I18n.T("customThumb");
            _settingsBtn.Text = I18n.T("settings");
            _extOpenBtn.Text = I18n.T("openWithOther");
            _extLabel.Text = I18n.T("extManage");
            _installBtn.Text = I18n.T("installExt");
            _installAllBtn.Text = I18n.T("installAllUac");
            _uninstallBtn.Text = I18n.T("uninstallExt");
            _restartBtn.Text = I18n.T("restartExplorer");
            // Gizmo 面文字随语言变化：触发视口重绘，预渲染位图将按新语言重建
            _view?.Invalidate();
        }

        private void OpenFile()
        {
            using var ofd = new OpenFileDialog
            {
                Filter = "3D/切片/CAD 文件|*.stl;*.obj;*.ply;*.off;*.3mf;*.amf;*.glb;*.gltf;*.gcode;*.gco;*.ctb;*.photon;*.pwmx;*.pwmo;*.pws;*.form;*.step;*.stp;*.c4d;*.sldprt;*.sldasm;*.ipt;*.iam;*.f3d;*.blend|所有文件|*.*",
                Multiselect = true
            };
            if (ofd.ShowDialog(this) == DialogResult.OK)
            {
                _folderFiles.Clear();
                _fileList.Items.Clear();
                foreach (var f in ofd.FileNames)
                    _fileList.Items.Add(Path.GetFileName(f));
                _folderFiles.AddRange(ofd.FileNames);
                _currentFolder = null; // 单文件模式：搜索退化为内存过滤
                RefreshFilteredList();
                LoadAndRender(ofd.FileNames[0]);
            }
        }

        /// <summary>拖放进入：有文件才允许放下。</summary>
        private void OnDragEnter(object sender, DragEventArgs e)
        {
            e.Effect = e.Data?.GetDataPresent(DataFormats.FileDrop) == true
                ? DragDropEffects.Copy
                : DragDropEffects.None;
        }

        /// <summary>拖放释放：把拖入的文件加入列表并加载第一个。</summary>
        private void OnDragDrop(object sender, DragEventArgs e)
        {
            if (e.Data?.GetData(DataFormats.FileDrop) is not string[] files || files.Length == 0) return;

            // 过滤出支持的 3D 文件
            var supported = files.Where(f => IsSupportedExt(f)).ToArray();
            if (supported.Length == 0) return;

            _folderFiles.Clear();
            _fileList.Items.Clear();
            foreach (var f in supported)
                _fileList.Items.Add(Path.GetFileName(f));
            _folderFiles.AddRange(supported);
            _currentFolder = null; // 拖放模式：搜索退化为内存过滤
            RefreshFilteredList();
            LoadAndRender(supported[0]);
        }

        /// <summary>判断扩展名是否属于支持的文件格式。</summary>
        private static bool IsSupportedExt(string f)
        {
            var ext = Path.GetExtension(f);
            if (string.IsNullOrEmpty(ext)) return false;
            return Exts.Contains(ext, StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>弹出系统"打开方式"对话框，让用户选择程序（如拓竹切片软件）打开当前文件。</summary>
        private void OpenWithExternal()
        {
            if (string.IsNullOrEmpty(_currentPath) || !File.Exists(_currentPath)) return;
            try
            {
                var launcher = (IOpenWithLauncher)Activator.CreateInstance(
                    Type.GetTypeFromCLSID(new Guid("E44E9428-BDBC-4987-A099-40DC8FD255E7")));
                if (launcher == null)
                {
                    MessageBox.Show(this, "无法启动系统打开方式对话框。", "用其他软件打开",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                launcher.Launch(IntPtr.Zero, _currentPath, 0); // 0 = 直接弹出打开方式并执行所选程序
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "打开失败: " + ex.Message, "用其他软件打开",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        /// <summary>Win10/11 系统"打开方式"接口（未公开）。</summary>
        [ComImport]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        [Guid("6A283FE2-ECFA-4599-91C4-E80957137B26")]
        private interface IOpenWithLauncher
        {
            [PreserveSig]
            int Launch(IntPtr hwnd,
                [MarshalAs(UnmanagedType.LPWStr)] string fileName,
                uint flags);
        }

        /// <summary>打开文件夹：列出所有支持的 3D 文件并自动加载第一个。</summary>
        private void OpenFolder()
        {
            using var fbd = new FolderBrowserDialog { Description = "选择包含 3D 文件的文件夹" };
            if (fbd.ShowDialog(this) != DialogResult.OK) return;

            List<string> found;
            try
            {
                // 容错递归：无权限子目录跳过，不让整个文件夹读取失败
                found = SafeScanFolder(fbd.SelectedPath);
            }
            catch (Exception ex)
            {
                _info.Text = "读取文件夹失败: " + ex.Message;
                return;
            }
            if (found == null)
            {
                _info.Text = "读取文件夹失败: " + fbd.SelectedPath;
                return;
            }

            if (found.Count == 0)
            {
                _info.Text = "该文件夹内未找到 3D 文件（支持: " + string.Join(" ", Exts) + "）";
                return;
            }

            _folderFiles.Clear();
            _folderFiles.AddRange(found);
            _currentFolder = fbd.SelectedPath; // 搜索范围 = 该文件夹树
            RenderSettings.Patch(s => s.LastFolder = fbd.SelectedPath); // 记住，便于下次启动自动浏览
            RefreshFilteredList();

            _info.Text = $"文件夹: {fbd.SelectedPath}\r\n找到 {found.Count} 个 3D 文件，已自动加载第一个。点左侧列表切换。\r\n搜索框支持 Everything 式 MFT 快速搜索（需管理员权限，失败自动回退普通扫描）。";
            _batchThumbBtn.Enabled = true;
            LoadAndRender(found[0]);
        }

        private void OnFileSelected()
        {
            if (_fileList.SelectedIndex < 0 || _fileList.SelectedIndex >= _filteredFiles.Count) return;
            var path = _filteredFiles[_fileList.SelectedIndex];
            // 已加载同一文件（列表刷新恢复选中触发）→ 跳过，避免重复解析
            if (_currentMesh != null &&
                path.Equals(_currentPath, StringComparison.OrdinalIgnoreCase)) return;
            if (File.Exists(path))
                LoadAndRender(path);
        }

        private async void LoadAndRender(string path)
        {
            var ver = ++_loadVersion; // 加载版本号：连续点选时丢弃过期解析结果
            DiagnosticLog.Log("LoadAndRender 开始: " + path + " ver=" + ver);
            try
            {
                _openBtn.Enabled = false;
                _folderBtn.Enabled = false;
                _saveBtn.Enabled = false;
                _thumbBtn.Enabled = false;
                _customThumbBtn.Enabled = false;
                _resetBtn.Enabled = false;
                // 注意：不禁用 _fileList，列表保持可点选；过期请求由 _loadVersion 丢弃
                _info.Text = "解析渲染中: " + Path.GetFileName(path) + " ...";

                MeshData mesh = null;
                await Task.Run(() => { mesh = MeshParserFactory.ParseFile(path); });
                DiagnosticLog.Log("  解析完成: " + (mesh == null ? "null" :
                    $"顶点={mesh.Vertices?.Count} 三角={mesh.Indices?.Count / 3} 盘={mesh.Plates?.Count} 颜色={mesh.Colors?.Count}"));

                if (ver != _loadVersion || IsDisposed) return; // 已被更新的选择取代
                if (mesh == null || mesh.Vertices == null || mesh.Vertices.Count == 0)
                {
                    DiagnosticLog.Log("  解析失败或空: " + path);
                    _info.Text = "无法解析该文件。";
                    return;
                }

                // 3MF 规范使用 Z-up（高度在 Z），而渲染/相机按 Y-up（高度在 Y）。
                // 不转换会让模型"躺倒"（高度方向被当作水平）。此处统一把 3MF 顶点
                // 从 Z-up 转为 Y-up：(x, y, z) → (x, z, -y)，保持右手系不镜像。
                if (Path.GetExtension(path).Equals(".3mf", StringComparison.OrdinalIgnoreCase))
                {
                    ToYUp(mesh);
                    if (mesh.Plates != null)
                        foreach (var pl in mesh.Plates)
                            if (pl?.Mesh != null) ToYUp(pl.Mesh);
                }

                // 设置到交互视图（内部 2x 超采样 + 后台渲染）
                _view.SetMesh(mesh);
                if (_dumpRenderPath != null) _dumpArmed = true; // 诊断模式：主网格渲染完成后开始转储
                _currentMesh = mesh;
                _saveBtn.Enabled = true;
                _thumbBtn.Enabled = true;
                _customThumbBtn.Enabled = true;
                _extOpenBtn.Enabled = true;
                _resetBtn.Enabled = true;
                _currentName = Path.GetFileName(path);
                _currentPath = path;
                EnsureSideList(path);

                // 3MF 多盘：填充盘切换下拉框（"全部盘" + 每盘名称），单盘隐藏
                PopulatePlates(mesh.Plates);

                var mode = mesh.IsPointCloud ? "点云" : "网格";
                var neg = mesh.FaceNegative == null ? 0 : mesh.FaceNegative.Count(x => x != 0);
                var mod = mesh.FaceModifier == null ? 0 : mesh.FaceModifier.Count(x => x != 0);
                var sb = new System.Text.StringBuilder();
                sb.Append($"文件: {Path.GetFileName(path)}\r\n");
                sb.Append($"格式: {mesh.OriginalFormat}  |  类型: {mode}\r\n");
                var colCount = (mesh.Colors != null && mesh.Colors.Count > 0 ? mesh.Colors.Count : 0)
                               + (mesh.FaceColors != null ? mesh.FaceColors.Count(c => c.W > 0f) : 0);
                sb.Append($"顶点: {mesh.Vertices.Count}  三角形: {mesh.Indices.Count / 3}  颜色: {colCount}");
                if (neg > 0) sb.Append($"  负零件: {neg} (25% 透明)");
                if (mod > 0) sb.Append($"  修改器: {mod} (叠加层)");
                if (mesh.Plates.Count > 1)
                {
                    sb.Append("\r\n盘: ");
                    for (var i = 0; i < mesh.Plates.Count; i++)
                    {
                        var pl = mesh.Plates[i];
                        sb.Append($"[{pl.Id}" + (string.IsNullOrEmpty(pl.Name) ? "" : " " + pl.Name) +
                                  $" {pl.Mesh.Indices.Count / 3}面]");
                        if (i < mesh.Plates.Count - 1) sb.Append(" ");
                    }
                }
                sb.Append("\r\n操作: 左键拖拽旋转 · 滚轮缩放 · 右键/中键平移 · 双击复位视图");
                _info.Text = sb.ToString();
            }
            catch (Exception ex)
            {
                DiagnosticLog.Log("  LoadAndRender 异常: " + ex.GetType().Name + ": " + ex.Message + "\n" + ex.StackTrace);
                _info.Text = "错误: " + ex.Message;
            }
            finally
            {
                _openBtn.Enabled = true;
                _folderBtn.Enabled = true;
                _fileList.Enabled = true;
                _customThumbBtn.Enabled = _currentPath != null;
            }
        }

        /// <summary>把 3MF(Z-up：高度在 Z) 网格转为渲染用 Y-up(高度在 Y)：(x,y,z)→(x,z,-y)，保持右手系不镜像。</summary>
        private static void ToYUp(MeshData m)
        {
            var vs = m.Vertices;
            for (var i = 0; i < vs.Count; i++)
            {
                var v = vs[i];
                vs[i] = new Vector3(v.X, v.Z, -v.Y);
            }
        }

        private void SavePng()
        {
            using var frame = _view.CaptureFrame(waitForPending: true);
            if (frame == null) return;
            using var sfd = new SaveFileDialog { Filter = "PNG|*.png", FileName = "preview.png" };
            if (sfd.ShowDialog(this) == DialogResult.OK)
            {
                frame.Save(sfd.FileName, ImageFormat.Png);
                _info.AppendText("\r\n已保存: " + sfd.FileName);
            }
        }

        /// <summary>
        /// 保存当前视角为方形缩略图（512×512）到侧车文件（源文件同目录 &lt;文件名&gt;.png）。
        /// 检测目标：已存在 → 弹窗确认是否覆盖（是/否/取消）；不存在 → 直接保存不提示。
        /// 侧车是资源管理器缩略图提供者的自定义缩略图来源（File 初始化直接命中）。
        /// </summary>
        private void SaveThumbnail()
        {
            if (_currentPath == null)
            {
                MessageBox.Show(this, "还没有可保存的 3D 文件，请先打开。", "保存缩略图",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            // 按设置比例(1:1/4:3/16:9)重渲当前模型 → 透明底缩略图，自动取景居中、无白边
            var (tw, th) = _view.Settings.ThumbSize(512);
            var thumb = _view.RenderThumbnail(tw, th);
            if (thumb == null)
            {
                MessageBox.Show(this, "还没有可保存的渲染结果，请先打开 3D 文件。", "保存缩略图",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            var target = CustomThumbPath(_currentPath); // 写入 explorer 真正读取的哈希目录
            if (File.Exists(target))
            {
                var r = MessageBox.Show(this,
                    $"文件已有缩略图：\n{Path.GetFileName(_currentPath)}\n\n是否覆盖？",
                    "保存缩略图",
                    MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
                if (r != DialogResult.Yes) return; // 否/取消：不覆盖
            }

            try
            {
                using (thumb)
                SaveCustomThumb(target, thumb);
                TouchModelFile(_currentPath); // 刷新 mtime，强制 explorer 缓存失效并重新查询
                _info.AppendText("\r\n缩略图已保存");
                var r2 = MessageBox.Show(this,
                    $"缩略图已保存到 explorer 读取的索引目录。\n\n资源管理器缩略图缓存可能需要重启才能刷新显示。\n是否立即重启资源管理器？",
                    "保存缩略图", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                if (r2 == DialogResult.Yes) RestartExplorer();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "保存失败: " + ex.Message, "保存缩略图",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        /// <summary>
        /// 批量生成缩略图（文件夹模式）：对当前文件夹树内全部 3D 文件逐一用默认相机渲染 512×512。
        /// 已存在缩略图的文件 → 弹窗询问"是否全部覆盖"（是=全部覆盖，否=跳过已有的只生成缺失）；
        /// 全部没有缩略图 → 直接生成不提示。后台线程执行 + 进度显示在右上角。
        /// 应用渲染设置的模型颜色 / 阴影强度。
        /// </summary>
        private void BatchSaveThumbnails()
        {
            if (_folderFiles.Count == 0 || _batchRunning)
            {
                if (_batchRunning)
                    MessageBox.Show(this, "批量生成正在进行中，请稍候。", "批量生成缩略图",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                else
                    MessageBox.Show(this, "请先打开文件夹。", "批量生成缩略图",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            // 统计已存在缩略图（explorer 读取的 custom-thumbs 哈希目录）的文件数
            var existing = _folderFiles.Count(f => File.Exists(CustomThumbPath(f)));
            var overwriteAll = true;
            if (existing > 0)
            {
                var r = MessageBox.Show(this,
                    $"当前文件夹树内 {_folderFiles.Count} 个 3D 文件中，{existing} 个已存在缩略图。\n\n" +
                    "是(Y) = 全部覆盖（重新生成所有缩略图）\n" +
                    "否(N) = 跳过已有的，只生成缺失的缩略图\n" +
                    "取消 = 不执行",
                    "批量生成缩略图",
                    MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
                if (r == DialogResult.Cancel) return;
                overwriteAll = r == DialogResult.Yes;
            }

            _batchRunning = true;
            _batchThumbBtn.Enabled = false;
            _renderStatus.Text = "  批量生成中...";
            var files = _folderFiles.ToArray(); // 快照
            var color = _view.Settings.ParseModelColor();
            var shadow = _view.Settings.ShadowLevel;
            var (tw, th) = _view.Settings.ThumbSize(512); // 缩略图保存比例(1:1/4:3/16:9)

            Task.Run(() =>
            {
                var renderer = new SoftwareRenderer();
                var ok = 0; var fail = 0; var skip = 0;
                for (var i = 0; i < files.Length; i++)
                {
                    var f = files[i];
                    if (!overwriteAll && File.Exists(CustomThumbPath(f)))
                    {
                        skip++;
                        ReportBatchProgress(i + 1, files.Length);
                        continue;
                    }
                    try
                    {
                        var mesh = MeshParserFactory.ParseFile(f);
                        if (mesh == null || mesh.Vertices == null || mesh.Vertices.Count == 0)
                        {
                            fail++;
                        }
                        else
                        {
                            // 3MF Z-up → Y-up（与预览器一致），并用与预览器相同的默认视角渲染，保证缩略图朝向一致
                            ToYUp(mesh);
                            // 超采样：低分辨率下细密网格会出"白噪点"，放大渲染后降采样（与预览器管线一致）
                            var res = renderer.Render(mesh, tw, th,
                                2.077f, 0.4974f, 3.1f, 0f, 0f, 1f, 0, color, shadow,
                                superSample: SoftwareRenderer.ThumbSuperSample(tw, th));
                            // backgroundColor=null → 透明底；按目标比例输出，保存透明 PNG（无白边）
                            if (res == null || res.Bgra == null || res.Bgra.Length == 0)
                            {
                                fail++;
                            }
                            else
                            {
                                using (var bmp = MakeBgraBitmap(res))
                                SaveCustomThumb(CustomThumbPath(f), bmp);
                            TouchModelFile(f); // 刷新 mtime，强制 explorer 缓存失效并重新查询
                            ok++;
                            }
                        }
                    }
                    catch
                    {
                        fail++;
                    }
                    ReportBatchProgress(i + 1, files.Length);
                }

                var msg = $"批量完成：成功 {ok}，失败 {fail}" + (skip > 0 ? $"，跳过已有 {skip}" : "") +
                          $"\n{files.Length} 个文件共 {TimeSpan.FromMilliseconds(0)}.";
                if (IsDisposed) return;
                try
                {
                    BeginInvoke(new Action(() =>
                    {
                        _batchRunning = false;
                        _batchThumbBtn.Enabled = true;
                        _renderStatus.Text = "  批量完成";
                        _info.AppendText("\r\n" + msg);
                        MessageBox.Show(this, msg, "批量生成缩略图",
                            MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }));
                }
                catch
                {
                    _batchRunning = false;
                }
            });
        }

        private void ReportBatchProgress(int done, int total)
        {
            if (IsDisposed) return;
            try
            {
                BeginInvoke(new Action(() =>
                {
                    if (IsDisposed) return;
                    _renderStatus.Text = $"  批量生成中... {done}/{total}";
                }));
            }
            catch
            {
            }
        }

        /// <summary>
        /// 自定义缩略图：用户选一张图片（png/jpg/bmp...）缩放到 512×512 白底，
        /// 保存到侧车 &lt;3d文件&gt;.png（资源管理器 File 初始化直接读取），
        /// 同时按源文件内容 SHA256 复制到 custom-thumbs 索引目录（流初始化按哈希查找）。
        /// 保存后询问是否重启资源管理器使缩略图立即生效。
        /// </summary>
        private void CustomThumbnail()
        {
            if (_currentPath == null)
            {
                MessageBox.Show(this, "请先打开 3D 文件，再为其设置自定义缩略图。", "自定义缩略图",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            using var ofd = new OpenFileDialog
            {
                Filter = "图片|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp|所有文件|*.*",
                Title = "选择自定义缩略图图片"
            };
            if (ofd.ShowDialog(this) != DialogResult.OK) return;

            try
            {
                // 缩放 512×512 白底，按内容 SHA256 存入 explorer 读取的 custom-thumbs 目录
                using (var img = Image.FromFile(ofd.FileName))
                using (var thumb = MakeThumbFromImage(img, 512))
                {
                    if (!SaveCustomThumb(CustomThumbPath(_currentPath), thumb))
                    {
                        MessageBox.Show(this, "无法读取模型文件内容，取哈希失败。", "自定义缩略图",
                            MessageBoxButtons.OK, MessageBoxIcon.Error);
                        return;
                    }
                }

                TouchModelFile(_currentPath); // 刷新 mtime，强制 explorer 缓存失效并重新查询
                _info.AppendText("\r\n自定义缩略图已保存: " + Path.GetFileName(_currentPath));
                var r2 = MessageBox.Show(this,
                    $"已为 {Path.GetFileName(_currentPath)} 设置自定义缩略图。\n\n资源管理器缩略图缓存可能需要重启才能刷新显示。\n是否立即重启资源管理器？",
                    "自定义缩略图",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                if (r2 == DialogResult.Yes) RestartExplorer();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "保存失败: " + ex.Message, "自定义缩略图",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        // ================= 缩略图合成工具 =================

        /// <summary>把渲染帧等比缩放到 size×size 白底位图（模型像素 alpha=255，背景透明→白）。</summary>
        private static Bitmap MakeThumbFromFrame(Bitmap frame, int size)
        {
            var thumb = new Bitmap(size, size, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(thumb))
            {
                g.Clear(Color.White);
                var scale = Math.Min((double)size / frame.Width, (double)size / frame.Height);
                var dw = (int)(frame.Width * scale);
                var dh = (int)(frame.Height * scale);
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                g.DrawImage(frame, new Rectangle((size - dw) / 2, (size - dh) / 2, dw, dh));
            }
            return thumb;
        }

        /// <summary>把渲染结果 BGRA 帧画到 size×size 白底位图（用于批量缩略图）。</summary>
        private static Bitmap MakeThumbFromBgra(RenderResult res, int size)
        {
            var frame = new Bitmap(res.Width, res.Height, PixelFormat.Format32bppArgb);
            var rect = new Rectangle(0, 0, res.Width, res.Height);
            var data = frame.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            System.Runtime.InteropServices.Marshal.Copy(res.Bgra, 0, data.Scan0, res.Bgra.Length);
            frame.UnlockBits(data);
            using (frame)
            {
                return MakeThumbFromFrame(frame, size);
            }
        }

        /// <summary>把渲染结果 BGRA 帧直接转为同尺寸位图（保留 alpha，透明底；不缩放/不铺白底）。</summary>
        private static Bitmap MakeBgraBitmap(RenderResult res)
        {
            var bmp = new Bitmap(res.Width, res.Height, PixelFormat.Format32bppArgb);
            var rect = new Rectangle(0, 0, res.Width, res.Height);
            var data = bmp.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            System.Runtime.InteropServices.Marshal.Copy(res.Bgra, 0, data.Scan0, res.Bgra.Length);
            bmp.UnlockBits(data);
            return bmp;
        }

        /// <summary>把用户选择的图片等比缩放到 size×size 白底位图（自定义缩略图）。</summary>
        private static Bitmap MakeThumbFromImage(Image img, int size)
        {
            var thumb = new Bitmap(size, size, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(thumb))
            {
                g.Clear(Color.White);
                var scale = Math.Min((double)size / img.Width, (double)size / img.Height);
                var dw = Math.Max(1, (int)(img.Width * scale));
                var dh = Math.Max(1, (int)(img.Height * scale));
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                g.DrawImage(img, new Rectangle((size - dw) / 2, (size - dh) / 2, dw, dh));
            }
            return thumb;
        }

        /// <summary>计算文件内容 SHA256（十六进制小写）；失败返回 null。</summary>
        private static string HashFile(string path)
        {
            try
            {
                using var fs = File.OpenRead(path);
                using var sha = System.Security.Cryptography.SHA256.Create();
                var h = sha.ComputeHash(fs);
                var sb = new System.Text.StringBuilder(64);
                foreach (var b in h) sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
            catch
            {
                return null;
            }
        }

        /// <summary>explorer 真正读取的自定义缩略图哈希路径（%LOCALAPPDATA%\3DThumbnailShell\custom-thumbs\&lt;sha256&gt;.png）。
        /// Win11 shell32 用 Stream 初始化 → 缩略图提供者按文件内容 SHA256 匹配此目录，侧车 &lt;文件&gt;.png 无效。</summary>
        private static string CustomThumbPath(string modelPath)
        {
            var hash = HashFile(modelPath);
            if (hash == null) return null;
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "3DThumbnailShell", "custom-thumbs", hash + ".png");
        }

        /// <summary>保存缩略到位图到 custom-thumbs 哈希目录（建目录；返回是否成功）。</summary>
        private static bool SaveCustomThumb(string target, Bitmap thumb)
        {
            if (target == null) return false;
            Directory.CreateDirectory(Path.GetDirectoryName(target));
            thumb.Save(target, ImageFormat.Png);
            return true;
        }

        /// <summary>刷新源文件修改时间，强制资源管理器失效并重新查询缩略图。
        /// explorer 的缩略图缓存以(路径,大小,修改时间)为键：保存的缩略图写入 custom-thumbs 哈希目录，
        /// 源 3MF 内容未变则 explorer 认为文件无变化，即便重启也不会重新调用我们的提供者 → 旧图不刷新。
        /// 仅触碰修改时间（不改内容，SHA256/哈希路径不变），缓存键即失效，下次查询读到新图。</summary>
        private static void TouchModelFile(string modelPath)
        {
            if (string.IsNullOrEmpty(modelPath) || !File.Exists(modelPath)) return;
            try { File.SetLastWriteTime(modelPath, DateTime.Now); }
            catch { }
        }

        // ================= 左侧筛选 / 搜索 =================

        /// <summary>填充 3MF 多盘切换下拉框。仅 Count&gt;1 显示；选中"全部盘"渲染合并视图。</summary>
        private void PopulatePlates(List<PlateInfo> plates)
        {
            _plateSwitching = true;
            try
            {
                _plateCombo.Items.Clear();
                _currentPlates = plates;
                if (plates == null || plates.Count <= 1)
                {
                    _plateCombo.Visible = false;
                    return;
                }
                _plateCombo.Items.Add("全部盘 (合并)");
                foreach (var p in plates)
                    _plateCombo.Items.Add("盘 " + p.Id + (string.IsNullOrEmpty(p.Name) ? "" : " · " + p.Name));
                _plateCombo.SelectedIndex = 0;
                _plateCombo.Visible = true;
            }
            finally
            {
                _plateSwitching = false;
            }
        }

        private void OnPlateChanged()
        {
            if (_plateSwitching || _plateCombo.SelectedIndex < 0) return;
            var plates = _currentPlates;
            if (plates == null || plates.Count <= 1) return;
            var idx = _plateCombo.SelectedIndex;
            if (idx == 0)
            {
                // 全部盘：切回主合并视图（缓存的 MeshData，无需重新解析）
                if (_currentMesh != null)
                    _view.SetMesh(_currentMesh);
            }
            else if (idx - 1 < plates.Count)
            {
                _view.SetMesh(plates[idx - 1].Mesh);
            }
        }

        /// <summary>诊断模式（--dump-render）：把当前帧存 PNG，随后依次切到各单盘再存，全部完成后退出。</summary>
        private void DumpNextFrame()
        {
            try
            {
                var plates = _currentPlates;
                if (_dumpPlateIdx < 0)
                {
                    // 合并视图已渲染完成 → 存档，然后依次切盘
                    SaveDumpFrame(_dumpRenderPath + "_merged.png");
                    _dumpPlateIdx = 0;
                    if (plates != null && plates.Count > 1 && _plateCombo.Items.Count > 1)
                    {
                        _plateCombo.SelectedIndex = 1; // 触发 OnPlateChanged → 渲染盘 1
                        return;
                    }
                }
                else if (plates != null && _dumpPlateIdx < plates.Count)
                {
                    SaveDumpFrame($"{_dumpRenderPath}_plate{_dumpPlateIdx + 1}.png");
                    _dumpPlateIdx++;
                    if (_plateCombo.Items.Count > _dumpPlateIdx + 1)
                    {
                        _plateCombo.SelectedIndex = _dumpPlateIdx + 1;
                        return;
                    }
                }
            }
            catch (Exception ex)
            {
                DiagnosticLog.Log("dump-render 失败: " + ex.Message);
            }
            _dumpDone = true;
            BeginInvoke(new Action(Close));
        }

        private void SaveDumpFrame(string path)
        {
            using var bmp = _view.CaptureFrame();
            if (bmp != null)
            {
                bmp.Save(path, ImageFormat.Png);
                _renderStatus.Text += "  已保存 " + Path.GetFileName(path);
            }
        }

        private void OnSearchTextChanged()
        {
            _searchTimer.Stop();
            _searchTimer.Start(); // 防抖：停止输入 300ms 后才搜索
        }

        /// <summary>
        /// 应用 类型筛选 + 关键字搜索 到左侧列表。
        /// search=true（搜索框）时：当前文件夹树存在 → 优先 Everything 式 MFT 快速搜索
        /// （需管理员权限，失败自动回退到内存列表过滤）；否则直接内存过滤。
        /// </summary>
        private void ApplyFilter(bool search)
        {
            var ext = SelectedFilterExt();
            var kw = _searchBox.Text.Trim();

            if (search && !string.IsNullOrEmpty(kw) && _currentFolder != null && _folderFiles.Count > 500)
            {
                // 大目录 + 关键字：尝试 MFT 快速搜索（后台线程，避免首次建索引卡 UI）
                var ver = ++_searchVersion;
                var root = _currentFolder;
                var exts = ext == null ? Exts : new[] { ext };
                _renderStatus.Text = "  搜索中(Everything MFT)...";
                Task.Run(() =>
                {
                    List<string> list = null;
                    try
                    {
                        var found = MftSearcher.Search(root, kw, exts);
                        if (found != null)
                            list = found.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
                    }
                    catch
                    {
                    }
                    if (IsDisposed || ver != _searchVersion) return;
                    try
                    {
                        BeginInvoke(new Action(() =>
                        {
                            if (IsDisposed || ver != _searchVersion) return;
                            if (list != null) ApplyFilterResult(list, kw);
                            else ApplyFilterMemory(ext, kw); // MFT 不可用/失败：回退到内存列表过滤
                        }));
                    }
                    catch
                    {
                    }
                });
                return;
            }

            // 内存过滤
            ApplyFilterMemory(ext, kw);
        }

        /// <summary>在已扫描的文件列表上做 类型筛选 + 关键字（文件名包含，大小写不敏感）。</summary>
        private void ApplyFilterMemory(string ext, string kw)
        {
            IEnumerable<string> q = _folderFiles;
            if (ext != null)
                q = q.Where(f => Path.GetExtension(f).Equals(ext, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrEmpty(kw))
                q = q.Where(f => Path.GetFileName(f).ToLowerInvariant().Contains(kw.ToLowerInvariant()));
            var list = q.OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
            ApplyFilterResult(list, kw);
        }

        private void ApplyFilterResult(List<string> list, string kw)
        {
            if (IsDisposed) return;
            _renderStatus.Text = "";
            _filteredFiles.Clear();
            _filteredFiles.AddRange(list);

            // 保留滚动位置：重建后恢复 TopIndex（项数变少时防越界），避免刷新跳回顶部
            var top = _fileList.TopIndex;
            _fileList.BeginUpdate();
            _fileList.Items.Clear();
            foreach (var f in list)
                _fileList.Items.Add(Path.GetFileName(f));
            _fileList.EndUpdate();
            if (top > 0 && _fileList.Items.Count > 0)
            {
                if (top >= _fileList.Items.Count) top = _fileList.Items.Count - 1;
                _fileList.TopIndex = top;
            }

            // 保持选中当前文件（若仍在结果中）。设置 SelectedIndex 会触发 OnFileSelected，
            // 后者对已加载的同一文件直接 return（防重复解析）。
            if (_currentPath != null)
            {
                var idx = list.FindIndex(x => x.Equals(_currentPath, StringComparison.OrdinalIgnoreCase));
                if (idx >= 0 && idx != _fileList.SelectedIndex)
                    _fileList.SelectedIndex = idx;
            }
            _renderStatus.Text = "筛选: " + list.Count + " 个文件" + (string.IsNullOrEmpty(kw) ? "" : " 匹配 \"" + kw + "\"");
        }

        /// <summary>当前筛选下拉框对应的扩展名；"全部"返回 null。</summary>
        private string SelectedFilterExt()
        {
            var idx = _filterCombo.SelectedIndex;
            if (idx <= 0) return null; // 0 = 全部类型
            return Exts[idx - 1];
        }

        /// <summary>刷新筛选结果（用于打开文件夹/文件后重算列表）。</summary>
        private void RefreshFilteredList()
        {
            _batchThumbBtn.Enabled = _folderFiles.Count > 0; // 列表有文件即可批量生成（不限于"打开文件夹"路径）
            var ext = SelectedFilterExt();
            IEnumerable<string> q = _folderFiles;
            if (ext != null)
                q = q.Where(f => Path.GetExtension(f).Equals(ext, StringComparison.OrdinalIgnoreCase));
            var kw = _searchBox.Text.Trim();
            if (!string.IsNullOrEmpty(kw))
                q = q.Where(f => Path.GetFileName(f).ToLowerInvariant().Contains(kw.ToLowerInvariant()));
            var list = q.OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
            ApplyFilterResult(list, kw);
        }

        private void DoExt(string action)
        {
            try
            {
                var msg = action == "install" ? ExtManager.Install(false) : ExtManager.Uninstall(false);
                MessageBox.Show(this, msg, "缩略图扩展", MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                _info.Text = msg;
            }
            catch (UnauthorizedAccessException)
            {
                MessageBox.Show(this, "写入注册表被拒绝。请以管理员身份运行本程序后再试（或使用 RegTool -hklm）。",
                    "缩略图扩展", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "操作失败: " + ex.Message, "缩略图扩展",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void DoExtElevated(string action)
        {
            // HKLM 需要管理员：以 UAC 提权重启自身，走命令行模式
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = Application.ExecutablePath,
                    Arguments = (action == "install" ? "--install" : "--uninstall") + " --hklm",
                    UseShellExecute = true,
                    Verb = "runas"
                };
                Process.Start(psi);
            }
            catch (System.ComponentModel.Win32Exception)
            {
                MessageBox.Show(this, "已取消提权（用户拒绝 UAC）。", "缩略图扩展",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "操作失败: " + ex.Message, "缩略图扩展",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void RestartExplorer()
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "taskkill",
                    Arguments = "/f /im explorer.exe",
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using (var p = Process.Start(psi)) p?.WaitForExit(5000);
                System.Threading.Thread.Sleep(1000);
                Process.Start("explorer.exe");
                _info.Text = "资源管理器已重启。若缩略图仍不显示，请注销重登。";
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "重启失败: " + ex.Message, "资源管理器",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
    }
}
