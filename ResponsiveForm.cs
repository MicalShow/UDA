using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace UDA
{
    /// <summary>
    /// Responsive maintenance UI. It deliberately uses one root TableLayoutPanel
    /// so Dock order can never make the navigation cover the content area.
    /// 响应式运维界面：根布局统一管理标题栏、导航、内容和状态栏，避免 Dock 层级覆盖。
    /// The LAN edition (LanEditionForm) subclasses this and reuses the whole shell; only the scan/install
    /// engines and a "服务器" settings page differ. Keeping this base free of LAN types lets the layout
    /// harnesses compile it without LanDriverService.cs.
    /// </summary>
    public class ResponsiveForm : Form
    {
        protected readonly C config;
        protected readonly O options;
        protected readonly L logger;
        private E engine;
        protected R scanResult;
        protected Machine machine;
        private CancellationTokenSource _scanCts;
        private bool _scanning;

        protected readonly Color navy = Color.FromArgb(234, 246, 253);
        protected readonly Color blue = Color.FromArgb(43, 151, 220);
        protected readonly Color background = Color.FromArgb(247, 249, 252);
        protected readonly Color muted = Color.FromArgb(96, 108, 126);

        protected TableLayoutPanel shell;
        protected TableLayoutPanel body;
        protected TableLayoutPanel homeRoot;
        protected TableLayoutPanel footerLayout;
        protected FlowLayoutPanel navigation;
        protected Panel contentHost;
        protected Panel homePage;
        protected Panel driversPage;
        protected Panel hardwarePage;
        protected Panel logPage;
        protected DataGridView driversGrid;
        protected DataGridView hardwareGrid;
        protected DataGridView detailsGrid;
        protected RichTextBox logBox;
        private readonly int _uiThreadId;
        private readonly SynchronizationContext _uiSyncContext;
        protected Label machineName;
        protected Label machineMeta;
        protected Label sourceStatus;
        protected Label headerStatus;
        protected Label footerStatus;
        protected Label allCount;
        protected Label problemCount;
        protected Label matchCount;
        protected Label deploymentState;
        protected Label resourceStatus;
        protected Label hardwareHealth;
        protected Label driverSummaryTitle;
        protected Label driverSummaryMeta;
        protected FlowLayoutPanel driverTools;
        protected TableLayoutPanel driverSummaryPanel;
        protected Button scanButton;
        protected Button installButton;
        protected Button exportSelectedButton;
        protected Button exportAllButton;
        protected CheckBox fullPackageCheck;
        protected ProgressBar progress;
        private System.Windows.Forms.Timer resourceTimer;
        private SystemResourceSampler resourceSampler;
        private string installProgressText = "正在安装驱动…";
        protected float testDpiScale;

        public ResponsiveForm(C c, O o, L l) : this(c, o, l, false)
        {
        }

        internal ResponsiveForm(C c, O o, L l, bool previewMode)
        {
            config = c;
            options = o;
            logger = l;
            // Capture the constructing thread and its sync context: Ui() must know which thread owns
            // the form so a background-thread log callback can never mutate controls inline.
            _uiThreadId = Thread.CurrentThread.ManagedThreadId;
            _uiSyncContext = SynchronizationContext.Current;

            Text = "万能驱动助手 5.2.6";
            Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            StartPosition = FormStartPosition.CenterScreen;
            AutoScaleMode = AutoScaleMode.Dpi;
            AutoScaleDimensions = new SizeF(96F, 96F);
            Font = new Font("Microsoft YaHei UI", 9F);
            BackColor = background;
            Size = new Size(1200, 760);
            MinimumSize = Size.Empty;

            BuildShell();
            logger.E += OnLog;
            if (!previewMode) Shown += async delegate { StartResourceMonitor(); await InitializeAsync(); };
            Resize += delegate { ApplyResponsiveRules(); };
        }

        protected internal virtual void LoadPreviewData()
        {
            var preview = new Machine
            {
                Maker = "LENOVO",
                Model = "ThinkPad T14 Gen 6 AMD",
                Sku = "21QJCTO1WW",
                Serial = "PF-TEST-2026",
                OS = "Microsoft Windows 11 企业版 LTSC"
            };
            preview.Info.Add(new[] { "计算机", "制造商", preview.Maker });
            preview.Info.Add(new[] { "计算机", "机型", preview.Model });
            preview.Info.Add(new[] { "处理器", "CPU", "AMD Ryzen 7 PRO 8840U with Radeon 780M Graphics" });
            preview.Info.Add(new[] { "内存", "物理内存", "32 GB（2 条）" });
            preview.Info.Add(new[] { "内存", "内存条 1", "DIMM 1 | 16 GB DDR5 5600 MHz | Samsung M425R2GA3BB0" });
            preview.Info.Add(new[] { "内存", "内存条 2", "DIMM 2 | 16 GB DDR5 5600 MHz | Samsung M425R2GA3BB0" });
            preview.Info.Add(new[] { "存储", "磁盘 1", "Samsung NVMe SSD | 1 TB | 健康度: 正常 | NVMe" });
            preview.Info.Add(new[] { "显卡", "GPU", "AMD Radeon 780M Graphics" });
            preview.Info.Add(new[] { "BIOS", "版本", "N3HET52W 1.24" });
            preview.Info.Add(new[] { "网络", "网卡", "Realtek PCIe GbE Family Controller" });
            for (int i = 0; i < 18; i++)
            {
                preview.Devices.Add(new D
                {
                    Name = i == 0 ? "未知 PCI 设备" : "测试硬件设备 " + (i + 1),
                    Class = i % 4 == 0 ? "System" : (i % 4 == 1 ? "Net" : (i % 4 == 2 ? "Display" : "MEDIA")),
                    Maker = i % 2 == 0 ? "Intel Corporation" : "Microsoft",
                    Error = i == 0 ? 28 : 0,
                    Inf = i == 0 ? "" : "oem" + i + ".inf",
                    Version = i == 0 ? "" : "10.0.26100." + i,
                    H = new List<string> { "PCI\\VEN_8086&DEV_" + (1000 + i).ToString("X4") + "&SUBSYS_00000000" }
                });
            }
            FillHardware(preview);
            headerStatus.Text = "预览模式 · 响应式布局验证";
            sourceStatus.Text = "已匹配机型整包：ThinkPad T14 Gen 6 AMD";
            matchCount.Text = "12";
            deploymentState.Text = "机型整包";
            footerStatus.Text = "布局测试数据已加载";
            resourceStatus.Text = "CPU 18%   内存 42%";
            driversGrid.Rows.Clear();
            for (int i = 0; i < 12; i++)
            {
                driversGrid.Rows.Add(i < 6, i < 2 ? "安装驱动" : (i < 6 ? "更新驱动" : "已是最新"),
                    "硬件设备 " + (i + 1), i % 2 == 0 ? "Chipset" : "Network", "1.0." + i,
                    "2.0." + i, "精确硬件 ID", "preview-driver-" + i + ".inf");
            }
            fullPackageCheck.Enabled = true;
            fullPackageCheck.Checked = true;
            driverSummaryTitle.Text = "发现 6 个驱动可以安装或升级，已选择 6 个";
            driverSummaryMeta.Text = "安装驱动 2 个  ·  更新驱动 4 个  ·  共匹配 12 个候选";
            logBox.Text = "[INFO] 万能驱动助手布局测试\r\n[INFO] 已加载模拟硬件信息\r\n[INFO] 1024×768 / 150% / 200% DPI 验证\r\n";
        }

        protected internal virtual void SelectPreviewPage(string page)
        {
            if (string.Equals(page, "drivers", StringComparison.OrdinalIgnoreCase)) ShowPage(driversPage);
            else if (string.Equals(page, "hardware", StringComparison.OrdinalIgnoreCase)) ShowPage(hardwarePage);
            else if (string.Equals(page, "log", StringComparison.OrdinalIgnoreCase)) ShowPage(logPage);
            else ShowPage(homePage);
        }

        internal void RefreshResponsiveLayout()
        {
            ApplyResponsiveRules();
            PerformRecursiveLayout(this);
            PerformRecursiveLayout(this);
        }

        private static void PerformRecursiveLayout(Control parent)
        {
            parent.PerformLayout();
            foreach (Control child in parent.Controls) PerformRecursiveLayout(child);
        }

        private void BuildShell()
        {
            shell = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                Margin = Padding.Empty,
                Padding = Padding.Empty,
                ColumnCount = 1,
                RowCount = 3,
                BackColor = background
            };
            shell.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            shell.RowStyles.Add(new RowStyle(SizeType.Absolute, 78F));
            shell.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            shell.RowStyles.Add(new RowStyle(SizeType.Absolute, 46F));
            Controls.Add(shell);

            shell.Controls.Add(BuildHeader(), 0, 0);
            shell.Controls.Add(BuildBody(), 0, 1);
            shell.Controls.Add(BuildFooter(), 0, 2);
        }

        private Control BuildHeader()
        {
            var header = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                Margin = Padding.Empty,
                ColumnCount = 2,
                RowCount = 1,
                BackColor = Color.White,
                Padding = new Padding(20, 8, 16, 7)
            };
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            header.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));

            var text = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Margin = Padding.Empty };
            text.RowStyles.Add(new RowStyle(SizeType.Percent, 58F));
            text.RowStyles.Add(new RowStyle(SizeType.Percent, 42F));
            text.Controls.Add(EllipsisLabel("设备驱动中心", 16F, Color.FromArgb(30, 42, 60), true), 0, 0);
            headerStatus = EllipsisLabel("正在初始化硬件信息…", 8.5F, muted, false);
            text.Controls.Add(headerStatus, 0, 1);
            header.Controls.Add(text, 0, 0);

            var actions = new FlowLayoutPanel
            {
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                WrapContents = false,
                FlowDirection = FlowDirection.LeftToRight,
                Anchor = AnchorStyles.Right | AnchorStyles.Top,
                Margin = new Padding(10, 7, 0, 0)
            };
            scanButton = NewActionButton("重新扫描", false, 104);
            installButton = NewActionButton("安装所选", true, 126);
            scanButton.Click += async delegate { await ScanDriversAsync(); };
            installButton.Click += async delegate { await InstallAsync(); };
            actions.Controls.Add(scanButton);
            actions.Controls.Add(installButton);
            header.Controls.Add(actions, 1, 0);
            return header;
        }

        private Control BuildBody()
        {
            body = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                Margin = Padding.Empty,
                ColumnCount = 2,
                RowCount = 1,
                BackColor = background
            };
            body.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 188F));
            body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            // Pin the single row to the body height: without it the row defaults to AutoSize and the
            // Dock=Fill nav/content stretch to their preferred size instead of the viewport, which
            // overflows the clipped bottom at any window shorter than the nav's stacked content.
            body.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));

            navigation = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                Margin = Padding.Empty,
                Padding = new Padding(12, 18, 12, 12),
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                AutoScroll = true,
                BackColor = navy
            };
            var logo = NewLabel("驱动中心", 18F, blue, true);
            logo.Margin = new Padding(10, 0, 0, 0);
            navigation.Controls.Add(logo);
            var subtitle = NewLabel("Universal Driver Assistant", 8F, muted, false);
            subtitle.Margin = new Padding(11, 0, 0, 18);
            navigation.Controls.Add(subtitle);
            navigation.Controls.Add(NewNavButton("概览", delegate { ShowPage(homePage); }));
            navigation.Controls.Add(NewNavButton("驱动管理", delegate { ShowPage(driversPage); }));
            navigation.Controls.Add(NewNavButton("全部硬件", delegate { ShowPage(hardwarePage); }));
            navigation.Controls.Add(NewNavButton("运行日志", delegate { ShowPage(logPage); }));
            body.Controls.Add(navigation, 0, 0);

            contentHost = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty, Padding = new Padding(18), BackColor = background };
            homePage = BuildHomePage();
            driversPage = BuildDriversPage();
            hardwarePage = BuildHardwarePage();
            logPage = BuildLogPage();
            contentHost.Controls.Add(homePage);
            contentHost.Controls.Add(driversPage);
            contentHost.Controls.Add(hardwarePage);
            contentHost.Controls.Add(logPage);
            body.Controls.Add(contentHost, 1, 0);
            ShowPage(homePage);
            return body;
        }

        private Control BuildFooter()
        {
            footerLayout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                Margin = Padding.Empty,
                Padding = new Padding(18, 9, 18, 7),
                ColumnCount = 3,
                RowCount = 1,
                BackColor = Color.White
            };
            footerLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 260F));
            footerLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            footerLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 210F));
            progress = new ProgressBar { Dock = DockStyle.Fill, Margin = new Padding(0, 1, 14, 1) };
            footerStatus = NewLabel("就绪", 8F, muted, false);
            footerStatus.AutoEllipsis = true;
            footerStatus.Dock = DockStyle.Fill;
            footerLayout.Controls.Add(progress, 0, 0);
            footerLayout.Controls.Add(footerStatus, 1, 0);
            resourceStatus = EllipsisLabel("CPU --%   内存 --%", 8F, Color.FromArgb(48, 98, 160), true);
            resourceStatus.TextAlign = ContentAlignment.MiddleRight;
            footerLayout.Controls.Add(resourceStatus, 2, 0);
            return footerLayout;
        }

        private Panel BuildHomePage()
        {
            var page = NewPage();
            page.AutoScroll = true;
            homeRoot = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Margin = Padding.Empty, BackColor = background };
            homeRoot.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            homeRoot.RowStyles.Add(new RowStyle(SizeType.Absolute, 142F));
            homeRoot.RowStyles.Add(new RowStyle(SizeType.Absolute, 96F));
            homeRoot.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            page.Controls.Add(homeRoot);

            var hero = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                Margin = new Padding(0, 0, 0, 10),
                Padding = new Padding(18, 16, 18, 14),
                ColumnCount = 2,
                RowCount = 1,
                BackColor = Color.White
            };
            hero.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 92F));
            hero.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            var pc = new Label
            {
                Text = "PC",
                Dock = DockStyle.Fill,
                Margin = new Padding(0, 0, 14, 0),
                TextAlign = ContentAlignment.MiddleCenter,
                BackColor = Color.FromArgb(232, 241, 255),
                ForeColor = blue,
                Font = new Font("Segoe UI", 21F, FontStyle.Bold)
            };
            hero.Controls.Add(pc, 0, 0);
            var identity = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Margin = Padding.Empty };
            identity.RowCount = 4;
            identity.RowStyles.Add(new RowStyle(SizeType.Percent, 34F));
            identity.RowStyles.Add(new RowStyle(SizeType.Percent, 22F));
            identity.RowStyles.Add(new RowStyle(SizeType.Percent, 22F));
            identity.RowStyles.Add(new RowStyle(SizeType.Percent, 22F));
            machineName = EllipsisLabel("正在读取计算机信息…", 16F, Color.FromArgb(30, 42, 60), true);
            machineMeta = EllipsisLabel("请稍候", 8.5F, muted, false);
            sourceStatus = EllipsisLabel("尚未扫描驱动库", 8.5F, blue, false);
            hardwareHealth = EllipsisLabel("● 正在检查硬件状态", 8.5F, Color.FromArgb(42, 166, 103), true);
            identity.Controls.Add(machineName, 0, 0);
            identity.Controls.Add(machineMeta, 0, 1);
            identity.Controls.Add(sourceStatus, 0, 2);
            identity.Controls.Add(hardwareHealth, 0, 3);
            hero.Controls.Add(identity, 1, 0);
            homeRoot.Controls.Add(hero, 0, 0);

            var cards = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, RowCount = 1, Margin = Padding.Empty, BackColor = background };
            for (int cardColumn = 0; cardColumn < 4; cardColumn++) cards.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25F));
            cards.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            allCount = AddCard(cards, "硬件设备", "--", Color.FromArgb(34, 111, 219), 0, 0);
            problemCount = AddCard(cards, "异常设备", "--", Color.FromArgb(235, 86, 86), 1, 0);
            matchCount = AddCard(cards, "推荐驱动", "--", Color.FromArgb(32, 170, 113), 2, 0);
            deploymentState = AddCard(cards, "部署状态", "待扫描", Color.FromArgb(145, 92, 210), 3, 0);
            homeRoot.Controls.Add(cards, 0, 1);

            var detailsHost = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Margin = new Padding(0, 10, 0, 0), Padding = new Padding(14, 10, 14, 12), BackColor = Color.White };
            detailsHost.RowStyles.Add(new RowStyle(SizeType.Absolute, 34F));
            detailsHost.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            detailsHost.Controls.Add(NewLabel("硬件配置详情", 12F, Color.FromArgb(30, 42, 60), true), 0, 0);
            detailsGrid = NewGrid();
            detailsGrid.ColumnHeadersVisible = false;
            detailsGrid.Columns.Add("Item", "项目");
            detailsGrid.Columns.Add("Value", "值");
            detailsGrid.Columns[0].FillWeight = 35F;
            detailsGrid.Columns[1].FillWeight = 65F;
            detailsHost.Controls.Add(detailsGrid, 0, 1);
            homeRoot.Controls.Add(detailsHost, 0, 2);
            return page;
        }

        private Panel BuildDriversPage()
        {
            var page = NewPage();
            var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Margin = Padding.Empty };
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 92F));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            var tools = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, FlowDirection = FlowDirection.LeftToRight, BackColor = Color.White, Padding = new Padding(16, 12, 10, 8) };
            driverTools = tools;
            var info = new Label { Text = "i", Width = 52, Height = 52, Margin = new Padding(0, 3, 14, 0), TextAlign = ContentAlignment.MiddleCenter, BackColor = blue, ForeColor = Color.White, Font = new Font("Segoe UI", 25F, FontStyle.Bold) };
            var summary = new TableLayoutPanel { Width = 520, Height = 60, ColumnCount = 1, RowCount = 2, Margin = Padding.Empty };
            driverSummaryPanel = summary;
            summary.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            summary.RowStyles.Add(new RowStyle(SizeType.Percent, 55F)); summary.RowStyles.Add(new RowStyle(SizeType.Percent, 45F));
            driverSummaryTitle = EllipsisLabel("点击“扫描服务端”检查可安装或可更新的驱动", 12F, Color.FromArgb(255, 102, 83), true);
            driverSummaryMeta = EllipsisLabel("驱动将按硬件分类显示，并在安装前缓存和校验。", 8.5F, muted, false);
            summary.Controls.Add(driverSummaryTitle, 0, 0); summary.Controls.Add(driverSummaryMeta, 0, 1);
            fullPackageCheck = new CheckBox { Text = "导入该机型全部 INF", AutoSize = false, AutoEllipsis = true, Width = 190, Height = 28, ForeColor = muted, Margin = new Padding(0, 3, 0, 0), TextAlign = ContentAlignment.MiddleLeft };
            tools.Controls.Add(info);
            tools.Controls.Add(summary);
            tools.Controls.Add(fullPackageCheck);
            root.Controls.Add(tools, 0, 0);
            driversGrid = NewGrid();
            driversGrid.RowTemplate.Height = 54;
            driversGrid.Columns.Add(new DataGridViewCheckBoxColumn { Name = "Pick", HeaderText = "", Width = 38, AutoSizeMode = DataGridViewAutoSizeColumnMode.None });
            foreach (string name in new[] { "状态", "设备名称", "类别", "当前版本", "候选版本", "匹配质量", "驱动文件" }) driversGrid.Columns.Add(name, name);
            root.Controls.Add(driversGrid, 0, 1);
            page.Controls.Add(root);
            return page;
        }

        private Panel BuildHardwarePage()
        {
            var page = NewPage();
            var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Margin = Padding.Empty };
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 52F));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            var tools = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, AutoScroll = true, FlowDirection = FlowDirection.LeftToRight, BackColor = Color.White, Padding = new Padding(12, 8, 8, 6) };
            var title = NewLabel("本机全部硬件设备", 12F, Color.FromArgb(30, 42, 60), true); title.Margin = new Padding(0, 4, 20, 0);
            exportSelectedButton = NewActionButton("导出选中驱动", false, 118);
            exportAllButton = NewActionButton("导出全部驱动", false, 118);
            exportSelectedButton.Click += async delegate { await ExportDriversAsync(false); };
            exportAllButton.Click += async delegate { await ExportDriversAsync(true); };
            tools.Controls.Add(title); tools.Controls.Add(exportSelectedButton); tools.Controls.Add(exportAllButton);
            root.Controls.Add(tools, 0, 0);
            hardwareGrid = NewGrid();
            hardwareGrid.DefaultCellStyle.WrapMode = DataGridViewTriState.True;
            hardwareGrid.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.DisplayedCellsExceptHeaders;
            foreach (string name in new[] { "健康", "设备名称", "类别", "制造商", "错误码", "硬件 ID", "当前驱动", "版本" }) hardwareGrid.Columns.Add(name, name);
            root.Controls.Add(hardwareGrid, 0, 1);
            page.Controls.Add(root);
            return page;
        }

        private Panel BuildLogPage()
        {
            var page = NewPage();
            logBox = new RichTextBox
            {
                Dock = DockStyle.Fill,
                BorderStyle = BorderStyle.None,
                ReadOnly = true,
                WordWrap = false,
                BackColor = Color.FromArgb(17, 24, 35),
                ForeColor = Color.FromArgb(187, 220, 196),
                Font = new Font("Consolas", 10F)
            };
            page.Controls.Add(logBox);
            return page;
        }

        protected virtual async Task InitializeAsync()
        {
            ApplyScreenSafeBounds();
            SetBusy(true, "正在读取 CPU、内存、磁盘、显卡和 PnP 设备…");
            try
            {
                machine = await Task.Run(delegate { var value = WM.Get(logger); HardwareProbe.Enrich(value, logger); return value; });
                FillHardware(machine);
                ShowPage(homePage);
                await ScanDriversAsync();
            }
            catch (Exception ex)
            {
                logger.W("[ERROR] 初始化失败：" + ex);
                headerStatus.Text = "硬件信息读取失败";
                footerStatus.Text = ex.Message;
            }
            finally { SetBusy(false, headerStatus.Text); }
        }

        protected virtual async Task ScanDriversAsync()
        {
            if (_scanning) { if (_scanCts != null) _scanCts.Cancel(); return; }
            _scanCts = new CancellationTokenSource();
            CancellationToken ct = _scanCts.Token;
            _scanning = true;
            scanButton.Text = "取消";
            scanButton.Enabled = true;
            installButton.Enabled = false;
            if (exportSelectedButton != null) exportSelectedButton.Enabled = false;
            if (exportAllButton != null) exportAllButton.Enabled = false;
            ResetPointer();
            headerStatus.Text = "正在连接驱动库并分析硬件 ID…";
            footerStatus.Text = "扫描驱动源";
            progress.Style = ProgressBarStyle.Marquee;
            try
            {
                if (engine != null) engine.Dispose();
                engine = new E(config, options, logger, machine);
                IProgress<string> scanProgress = new Progress<string>(delegate(string message)
                {
                    Ui(delegate { footerStatus.Text = message; });
                });
                scanResult = await Task.Run(delegate { return engine.Scan(ct, scanProgress); });
                FillDrivers();
                headerStatus.Text = "扫描完成 · " + scanResult.Mode;
                sourceStatus.Text = scanResult.Package ? "已匹配机型整包：" + scanResult.Mapping : "未找到机型整包，已使用全库硬件 ID 匹配";
                matchCount.Text = scanResult.Matches.Count(x => x.Pick).ToString();
                deploymentState.Text = scanResult.Package ? "机型整包" : "硬件匹配";
                footerStatus.Text = "发现 " + scanResult.Matches.Count + " 个候选，推荐 " + scanResult.Matches.Count(x => x.Pick) + " 个";
                ShowPage(driversPage);
            }
            catch (OperationCanceledException)
            {
                headerStatus.Text = "扫描已取消";
                footerStatus.Text = "扫描已取消";
                logger.W("[INFO] 扫描已取消 / Scan cancelled by user");
                ShowPage(homePage);
            }
            catch (Exception ex)
            {
                headerStatus.Text = "驱动库扫描失败 · 硬件信息仍可查看";
                footerStatus.Text = ex.Message;
                logger.W("[ERROR] " + ex);
                ShowPage(homePage);
            }
            finally
            {
                _scanning = false;
                if (_scanCts != null) { _scanCts.Dispose(); _scanCts = null; }
                scanButton.Text = "重新扫描";
                progress.Style = ProgressBarStyle.Blocks;
                progress.Value = 0;
                scanButton.Enabled = true;
                installButton.Enabled = true;
                if (exportSelectedButton != null) exportSelectedButton.Enabled = true;
                if (exportAllButton != null) exportAllButton.Enabled = true;
                ResetPointer();
            }
        }

        protected virtual async Task InstallAsync()
        {
            if (scanResult == null) return;
            foreach (DataGridViewRow row in driversGrid.Rows)
            {
                var match = row.Tag as X;
                if (match != null) match.Pick = Convert.ToBoolean(row.Cells[0].Value ?? false);
            }
            int count = scanResult.Matches.Count(x => x.Pick);
            if (count == 0 && !fullPackageCheck.Checked)
            {
                MessageBox.Show("请先选择需要安装的驱动。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            int packageCount = fullPackageCheck.Checked ? scanResult.PackageInfs.Count : count;
            if (MessageBox.Show("即将安装 " + packageCount + " 个驱动包，是否继续？", "安装确认", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
            SetBusy(true, "正在安装驱动…");
            StartResourceMonitor();
            try
            {
                var summary = await Task.Run(delegate
                {
                    return engine.Install(scanResult, fullPackageCheck.Checked, delegate(int current, int total, string name)
                    {
                        Ui(delegate
                        {
                            progress.Maximum = Math.Max(1, total);
                            progress.Value = Math.Min(current, total);
                            installProgressText = "正在安装 " + current + "/" + total + "  " + name;
                            footerStatus.Text = installProgressText;
                        });
                    });
                });
                Rep.Save(config, scanResult, summary);
                footerStatus.Text = "完成：成功 " + summary.Ok + "，跳过 " + summary.Skip + "，失败 " + summary.Fail;
                MessageBox.Show(footerStatus.Text, "安装完成", MessageBoxButtons.OK, summary.Fail > 0 ? MessageBoxIcon.Warning : MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                logger.W("[ERROR] 安装失败：" + ex);
                footerStatus.Text = "安装失败：" + ex.Message;
                MessageBox.Show(ex.Message, "安装失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally { SetBusy(false, headerStatus.Text); }
        }

        private async Task ExportDriversAsync(bool all)
        {
            D selected = null;
            if (!all)
            {
                selected = hardwareGrid.CurrentRow == null ? null : hardwareGrid.CurrentRow.Tag as D;
                if (selected == null) { MessageBox.Show("请先在硬件列表选择一个设备。", "导出驱动", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
                if (String.IsNullOrWhiteSpace(selected.Inf)) { MessageBox.Show("所选设备没有可导出的当前驱动 INF。", "导出驱动", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
            }
            using (var dialog = new FolderBrowserDialog { Description = all ? "选择全部第三方驱动的导出目录" : "选择该设备驱动的导出目录", ShowNewFolderButton = true })
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                string destination = dialog.SelectedPath;
                SetBusy(true, all ? "正在导出全部第三方驱动…" : "正在导出 " + selected.Name + "…");
                try
                {
                    await Task.Run(delegate
                    {
                        if (all) DriverExporter.ExportAll(destination, Math.Max(600, config.General.InstallTimeoutSeconds * 5), logger);
                        else DriverExporter.ExportOne(selected.Inf, destination, Math.Max(120, config.General.InstallTimeoutSeconds), logger);
                    });
                    footerStatus.Text = "驱动导出完成：" + destination;
                    MessageBox.Show(footerStatus.Text, "导出完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                catch (Exception ex)
                {
                    logger.W("[ERROR] 驱动导出失败 / Export failed: " + ex);
                    footerStatus.Text = "驱动导出失败：" + ex.Message;
                    MessageBox.Show(ex.Message, "导出失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
                finally { SetBusy(false, headerStatus.Text); }
            }
        }

        protected void StartResourceMonitor()
        {
            if (resourceTimer != null) return;
            resourceSampler = new SystemResourceSampler(); resourceSampler.Sample();
            resourceTimer = new System.Windows.Forms.Timer { Interval = 1000 };
            resourceTimer.Tick += delegate { UpdateResourceUsage(); };
            resourceTimer.Start(); UpdateResourceUsage();
        }

        private void UpdateResourceUsage()
        {
            if (resourceSampler == null) return;
            Tuple<double, double> value = resourceSampler.Sample();
            string text = "CPU " + value.Item1.ToString("0") + "%   内存 " + value.Item2.ToString("0") + "%";
            resourceStatus.Text = text;
            if (!resourceStatus.Visible) footerStatus.Text = installProgressText + "   " + text;
        }

        protected void StopResourceMonitor()
        {
            if (resourceTimer != null) { resourceTimer.Stop(); resourceTimer.Dispose(); resourceTimer = null; }
            resourceSampler = null; if (resourceStatus != null) resourceStatus.Text = "CPU --%   内存 --%";
        }

        protected void FillHardware(Machine value)
        {
            machineName.Text = (value.Maker + "  " + value.Model).Trim();
            machineMeta.Text = "System SKU: " + value.Sku + "    序列号: " + value.Serial + "    " + value.OS;
            allCount.Text = value.Devices.Count.ToString();
            problemCount.Text = value.Devices.Count(x => x.NeedsAttention).ToString();
            int bad = value.Devices.Count(x => x.NeedsAttention);
            hardwareHealth.Text = bad == 0 ? "● 硬件状态良好" : ("● 发现 " + bad + " 个异常设备");
            hardwareHealth.ForeColor = bad == 0 ? Color.FromArgb(42, 166, 103) : Color.FromArgb(230, 84, 73);

            detailsGrid.Rows.Clear();
            foreach (string[] item in value.Info) detailsGrid.Rows.Add(item[0] + " · " + item[1], item[2]);

            hardwareGrid.Rows.Clear();
            foreach (D device in value.Devices.OrderByDescending(x => x.NeedsAttention).ThenBy(x => x.Class).ThenBy(x => x.Name))
            {
                int index = hardwareGrid.Rows.Add(device.Error == 22 ? "已禁用" : device.Error == 45 ? "未连接" : device.NeedsAttention ? "异常" : "正常", device.Name, device.Class, device.Maker, device.Error,
                    string.Join(Environment.NewLine, device.H), device.Inf, device.Version);
                hardwareGrid.Rows[index].Tag = device;
                if (device.NeedsAttention) hardwareGrid.Rows[index].DefaultCellStyle.ForeColor = Color.FromArgb(200, 60, 60);
            }
        }

        protected virtual void FillDrivers()
        {
            driversGrid.Rows.Clear();
            foreach (X match in scanResult.Matches.OrderByDescending(x => x.Pick).ThenBy(x => x.Cat).ThenBy(x => x.D.Name))
            {
                int index = driversGrid.Rows.Add(match.Pick, match.State, match.D.Name, match.Cat, match.D.Version,
                    match.I.Version, match.Type, System.IO.Path.GetFileName(match.I.Path));
                driversGrid.Rows[index].Tag = match;
                if (match.Pick) driversGrid.Rows[index].DefaultCellStyle.BackColor = Color.FromArgb(239, 249, 244);
            }
            int selected = scanResult.Matches.Count(x => x.Pick);
            int installs = scanResult.Matches.Count(x => x.State == "安装驱动" || x.State == "缺少驱动");
            int updates = scanResult.Matches.Count(x => x.State == "更新驱动" || x.State == "可更新");
            int noMatch = scanResult.Matches.Count(x => x.State == "没有匹配驱动");
            if (driverSummaryTitle != null) driverSummaryTitle.Text = selected == 0 ? "当前设备驱动已是最新" : ("发现 " + selected + " 个驱动可以安装或升级，已选择 " + selected + " 个");
            string meta = "安装驱动 " + installs + " 个  ·  更新驱动 " + updates + " 个  ·  共匹配 " + scanResult.Matches.Count + " 个候选";
            if (noMatch > 0) meta += "  ·  没有匹配驱动 " + noMatch + " 个";
            if (driverSummaryMeta != null) driverSummaryMeta.Text = meta;
            fullPackageCheck.Enabled = scanResult.Package;
            fullPackageCheck.Checked = scanResult.Package;
        }

        protected void ApplyScreenSafeBounds()
        {
            Rectangle work = Screen.FromControl(this).WorkingArea;
            MinimumSize = new Size(Math.Min(ScalePx(720), work.Width), Math.Min(ScalePx(500), work.Height));
            int width = Math.Min(ScalePx(1200), Math.Max(ScalePx(720), work.Width - ScalePx(32)));
            int height = Math.Min(ScalePx(760), Math.Max(ScalePx(500), work.Height - ScalePx(32)));
            Size = new Size(width, height);
            if (work.Width / LayoutScale < 1000 || work.Height / LayoutScale < 680) WindowState = FormWindowState.Maximized;
            ApplyResponsiveRules();
        }

        protected virtual void ApplyResponsiveRules()
        {
            if (body == null || navigation == null) return;
            ScaleActionButtons(this);
            int width = (int)Math.Round(ClientSize.Width / LayoutScale);
            float navWidth = width < 760 ? 0F : ScalePx(width < 1050 ? 146 : 188);
            body.ColumnStyles[0].Width = navWidth;
            navigation.Visible = navWidth > 0F;
            bool compact = width < 680;
            scanButton.Visible = !compact;
            installButton.Text = compact ? "安装" : "安装所选";
            installButton.Width = ScalePx(compact ? 82 : 126);
            contentHost.Padding = new Padding(ScalePx(compact ? 8 : 18));
            // DPI: fixed 96-DPI absolute row heights are the classic source of a clipped bottom /
            // status bar at 125% / 150% / 200% and large fonts — keep every fixed row scaled to the
            // live DPI instead of the design-time 96-DPI pixel value.
            if (shell != null && shell.RowStyles.Count >= 3)
            {
                shell.RowStyles[0].Height = ScalePx(78);
                shell.RowStyles[2].Height = ScalePx(46);
            }
            if (driverTools != null && driverTools.Parent is TableLayoutPanel) ((TableLayoutPanel)driverTools.Parent).RowStyles[0].Height = ScalePx(92);
            if (exportSelectedButton != null && exportSelectedButton.Parent != null && exportSelectedButton.Parent.Parent is TableLayoutPanel) ((TableLayoutPanel)exportSelectedButton.Parent.Parent).RowStyles[0].Height = ScalePx(52);
            if (homeRoot != null && homeRoot.RowStyles.Count >= 3)
            {
                homeRoot.RowStyles[0].Height = ScalePx(142);
                homeRoot.RowStyles[1].Height = ScalePx(96);
            }
            scanButton.Height = ScalePx(34);
            installButton.Height = ScalePx(34);
            if (driverSummaryPanel != null)
            {
                int navLogical = navWidth <= 0 ? 0 : (int)Math.Round(navWidth / LayoutScale);
                int contentPadding = compact ? 16 : 36;
                int checkWidth = fullPackageCheck != null && fullPackageCheck.Visible ? 190 : 0;
                int summaryLogical = Math.Max(150, width - navLogical - contentPadding - 52 - 14 - checkWidth - 32);
                driverSummaryPanel.Width = ScalePx(Math.Min(520, summaryLogical));
            }
            if (footerLayout != null) footerLayout.ColumnStyles[0].Width = ScalePx(width < 600 ? 150 : 260);
            if (footerLayout != null && resourceStatus != null)
            {
                bool showResourceColumn = width >= 760;
                footerLayout.ColumnStyles[2].Width = showResourceColumn ? ScalePx(210) : 0F;
                resourceStatus.Visible = showResourceColumn;
            }
            ConfigureGridForWidth(driversGrid, compact, new[] { 38, 90, 190, 90, 105, 105, 115, 170 });
            ConfigureGridForWidth(hardwareGrid, compact, new[] { 70, 220, 100, 155, 72, 280, 115, 125 });
            if (homeRoot != null && homePage != null)
            {
                int availableHeight = (int)Math.Round((shell == null ? ClientSize.Height : shell.GetRowHeights()[1]) / LayoutScale);
                if (availableHeight < 520)
                {
                    homeRoot.Dock = DockStyle.Top;
                    homeRoot.Height = ScalePx(500);
                    homePage.AutoScrollMinSize = new Size(0, ScalePx(500));
                }
                else
                {
                    homePage.AutoScrollMinSize = Size.Empty;
                    homeRoot.Dock = DockStyle.Fill;
                }
            }
            foreach (Control control in navigation.Controls)
            {
                var button = control as Button;
                if (button != null) button.Width = Math.Max(ScalePx(110), (int)navWidth - ScalePx(24));
            }
        }

        protected float LayoutScale { get { if (testDpiScale > 0.01F) return testDpiScale; try { using (Graphics graphics = CreateGraphics()) return Math.Max(1F, graphics.DpiX / 96F); } catch { return 1F; } } }
        protected int ScalePx(int logicalPixels) { return Math.Max(0, (int)Math.Round(logicalPixels * LayoutScale)); }
        public void SetTestDpiScale(float scale) { testDpiScale = Math.Max(1F, scale); ApplyResponsiveRules(); }

        private void ScaleActionButtons(Control parent)
        {
            foreach (Control control in parent.Controls)
            {
                var button = control as Button;
                if (button != null && button.Tag is Size)
                {
                    Size logical = (Size)button.Tag;
                    Size text = TextRenderer.MeasureText(button.Text ?? "", button.Font,
                        new Size(Int32.MaxValue, Int32.MaxValue), TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
                    button.Width = Math.Max(ScalePx(logical.Width), text.Width + ScalePx(28));
                    button.Height = Math.Max(ScalePx(logical.Height), text.Height + ScalePx(14));
                }
                if (control.HasChildren) ScaleActionButtons(control);
            }
        }

        // LAN GUI state harness accessors (never used in production).
        internal TableLayoutPanel TestShell { get { return shell; } }
        internal TableLayoutPanel TestFooterLayout { get { return footerLayout; } }
        internal Label TestFooterStatus { get { return footerStatus; } }
        internal ProgressBar TestProgress { get { return progress; } }
        internal DataGridView TestDriversGrid { get { return driversGrid; } }

        private void ConfigureGridForWidth(DataGridView grid, bool compact, int[] widths)
        {
            if (grid == null || grid.ColumnCount == 0) return;
            grid.ColumnHeadersHeight = ScalePx(42);
            if (compact)
            {
                grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None;
                for (int i = 0; i < grid.ColumnCount && i < widths.Length; i++)
                {
                    grid.Columns[i].AutoSizeMode = DataGridViewAutoSizeColumnMode.None;
                    grid.Columns[i].Width = ScalePx(widths[i]);
                }
                grid.ColumnHeadersDefaultCellStyle.WrapMode = DataGridViewTriState.False;
                grid.ScrollBars = ScrollBars.Both;
            }
            else
            {
                grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
                for (int i = 0; i < grid.ColumnCount; i++) grid.Columns[i].AutoSizeMode = i == 0 && grid == driversGrid ? DataGridViewAutoSizeColumnMode.None : DataGridViewAutoSizeColumnMode.Fill;
                if (grid == driversGrid) grid.Columns[0].Width = ScalePx(38);
                grid.ColumnHeadersDefaultCellStyle.WrapMode = DataGridViewTriState.False;
            }
        }

        protected void SetBusy(bool busy, string text)
        {
            scanButton.Enabled = !busy;
            installButton.Enabled = !busy;
            if (exportSelectedButton != null) exportSelectedButton.Enabled = !busy;
            if (exportAllButton != null) exportAllButton.Enabled = !busy;
            // Work runs asynchronously; buttons/progress communicate state without a global busy pointer.
            ResetPointer();
            headerStatus.Text = text;
        }

        protected void ResetPointer()
        {
            ClearWaitPointer(this);
            Cursor.Current = Cursors.Default;
        }

        private static void ClearWaitPointer(Control control)
        {
            control.UseWaitCursor = false;
            foreach (Control child in control.Controls) ClearWaitPointer(child);
        }

        protected void ShowPage(Panel page)
        {
            foreach (Control control in contentHost.Controls) control.Visible = false;
            page.Visible = true;
            page.BringToFront();
        }

        protected Panel NewPage()
        {
            return new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty, BackColor = background, Visible = false };
        }

        protected DataGridView NewGrid()
        {
            var grid = new DataGridView
            {
                Dock = DockStyle.Fill,
                BackgroundColor = Color.White,
                BorderStyle = BorderStyle.None,
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                RowHeadersVisible = false,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                MultiSelect = false,
                GridColor = Color.FromArgb(235, 239, 245),
                CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal,
                ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None,
                EnableHeadersVisualStyles = false,
                ColumnHeadersHeight = 42,
                RowTemplate = { Height = 38 }
            };
            grid.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(248, 250, 253);
            grid.ColumnHeadersDefaultCellStyle.ForeColor = Color.FromArgb(75, 88, 105);
            grid.ColumnHeadersDefaultCellStyle.Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold);
            grid.DefaultCellStyle.SelectionBackColor = Color.FromArgb(229, 240, 255);
            grid.DefaultCellStyle.SelectionForeColor = Color.FromArgb(30, 42, 60);
            grid.DefaultCellStyle.Padding = new Padding(4, 2, 4, 2);
            grid.AlternatingRowsDefaultCellStyle.BackColor = Color.FromArgb(252, 253, 255);
            return grid;
        }

        protected Button NewNavButton(string text, Action action)
        {
            var button = new Button
            {
                Text = "  " + text,
                Width = 164,
                Height = 42,
                Margin = new Padding(0, 0, 0, 5),
                FlatStyle = FlatStyle.Flat,
                TextAlign = ContentAlignment.MiddleLeft,
                BackColor = navy,
                ForeColor = Color.FromArgb(51, 126, 167),
                Font = new Font("Microsoft YaHei UI", 9.5F)
            };
            button.FlatAppearance.BorderSize = 0;
            button.FlatAppearance.MouseOverBackColor = Color.FromArgb(210, 235, 249);
            button.Click += delegate { action(); };
            return button;
        }

        protected Button NewActionButton(string text, bool primary, int width)
        {
            var button = new Button
            {
                Text = text,
                Width = width,
                Height = 34,
                Margin = new Padding(0, 0, 8, 0),
                FlatStyle = FlatStyle.Flat,
                BackColor = primary ? blue : Color.White,
                ForeColor = primary ? Color.White : Color.FromArgb(48, 62, 82),
                Font = new Font("Microsoft YaHei UI", 8.5F, FontStyle.Bold)
            };
            button.Tag = new Size(width, 34);
            button.FlatAppearance.BorderColor = primary ? blue : Color.FromArgb(215, 222, 232);
            return button;
        }

        private Label AddCard(TableLayoutPanel parent, string title, string value, Color accent, int column, int row)
        {
            var card = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                Margin = new Padding(column == 0 ? 0 : 5, 0, column == 3 ? 0 : 5, 0),
                Padding = new Padding(14, 8, 12, 7),
                ColumnCount = 2,
                RowCount = 2,
                BackColor = Color.White
            };
            card.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 5F));
            card.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            card.RowStyles.Add(new RowStyle(SizeType.Percent, 42F));
            card.RowStyles.Add(new RowStyle(SizeType.Percent, 58F));
            var bar = new Panel { Dock = DockStyle.Fill, Margin = new Padding(0), BackColor = accent };
            card.Controls.Add(bar, 0, 0);
            card.SetRowSpan(bar, 2);
            var name = NewLabel(title, 8.5F, muted, false);
            name.Margin = new Padding(12, 2, 0, 0);
            var result = NewLabel(value, 16F, Color.FromArgb(35, 48, 67), true);
            result.Margin = new Padding(12, 0, 0, 0);
            result.AutoEllipsis = true;
            result.Dock = DockStyle.Fill;
            card.Controls.Add(name, 1, 0);
            card.Controls.Add(result, 1, 1);
            parent.Controls.Add(card, column, row);
            return result;
        }

        protected Label NewLabel(string text, float size, Color color, bool bold)
        {
            return new Label
            {
                Text = text,
                AutoSize = true,
                ForeColor = color,
                Font = new Font("Microsoft YaHei UI", size, bold ? FontStyle.Bold : FontStyle.Regular)
            };
        }

        protected Label EllipsisLabel(string text, float size, Color color, bool bold)
        {
            var label = NewLabel(text, size, color, bold);
            label.AutoSize = false;
            label.Dock = DockStyle.Fill;
            label.AutoEllipsis = true;
            label.TextAlign = ContentAlignment.MiddleLeft;
            return label;
        }

        private void OnLog(string message)
        {
            Ui(delegate
            {
                if (logBox == null || logBox.IsDisposed) return;
                logBox.AppendText(message + Environment.NewLine);
                logBox.SelectionStart = logBox.TextLength;
                logBox.ScrollToCaret();
            });
        }

        protected void Ui(Action action)
        {
            if (IsDisposed || Disposing) return;
            if (InvokeRequired) { SafeMarshal(action); return; }
            if (IsHandleCreated) { action(); return; }   // handle exists and we are on its thread
            // No handle anywhere yet. Only the form-owning (constructor) thread may run UI mutation
            // inline; a background thread must never do so — running inline here would create the
            // control handle on a foreign thread whose queue is never pumped, and the UI thread's
            // next SendMessage to that control (e.g. AppendText) would deadlock forever.
            if (Thread.CurrentThread.ManagedThreadId == _uiThreadId) { action(); return; }
            SafeMarshal(action);
        }

        /// <summary>Marshal a UI action to the form's thread without ever blocking the caller.
        /// Uses the WinForms synchronization context captured at construction; failures to marshal
        /// (handle gone, no context) drop the update instead of throwing on a background thread.</summary>
        private void SafeMarshal(Action action)
        {
            var ctx = _uiSyncContext;
            if (ctx != null) { try { ctx.Post(delegate { try { if (!IsDisposed && !Disposing) action(); } catch { } }, null); return; } catch { } }
            try { BeginInvoke(action); } catch { }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (_scanCts != null) _scanCts.Cancel();
                logger.E -= OnLog;
                if (engine != null) engine.Dispose();
                StopResourceMonitor();
            }
            base.Dispose(disposing);
        }
    }
}

