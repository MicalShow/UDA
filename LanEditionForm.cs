// LanEditionForm.cs - LAN edition UI built on the ResponsiveForm visual baseline.
//
// Replaces the old LanClientForm (deleted). Rather than a second visual implementation, this subclasses
// ResponsiveForm and reuses its entire shell verbatim: dark left navigation, top title/status/action
// buttons, overview cards, hardware configuration details, the all-hardware grid, the drivers grid, the
// log panel and the footer resource status (with all responsive/DPI behaviour). It only swaps the scan
// and install engines for the LAN client workflow and adds a single "服务器" settings page with a locked
// server address. Compiled by Build.ps1 and the Phase 3/4 harnesses (which include LanDriverService.cs);
// the layout harnesses compile only the ResponsiveForm base, so no LAN types leak into them.
//
// The LAN workflow is driven step by step (not as a black box): server status + test connection, scan
// server-matched candidates into the shared drivers grid, confirm + cancel, real-time progress/log. The
// one-shot LanDriverClient.Run() is untouched, so the CLI (--deployment/--whatif) and the protocol tests
// keep their exact semantics (all-platform ingestion, OS/arch filtering on request, strict-duplicate dedup,
// no-token read mode, admin-only repo writes).
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace UDA
{
    public sealed class LanClientUiSettings
    {
        public string ServerUrl = "http://192.168.0.253:8731";
        public string Token = "";
        public string BootstrapRepo = "";
        public static string SettingsFileName(string serverUrl)
        {
            string endpoint = (serverUrl ?? "").Trim().TrimEnd('/').ToLowerInvariant();
            using (var sha = System.Security.Cryptography.SHA256.Create())
                return "LanClient-" + BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(endpoint))).Replace("-", "") + ".json";
        }
    }

    public sealed class LanEditionForm : ResponsiveForm
    {
        private readonly LanClientConfig _lanCfg;
        private LanDriverClient _lanClient;
        private CancellationTokenSource _lanCts;
        private bool _lanScanning, _lanInstalling;
        private bool _firstAutomaticScan = true;
        private Dictionary<X, LanCandidateDto> _candByMatch;
        // Incremented on every busy enter/leave; late Progress<T> posts carry the generation they
        // were created under and are dropped when it no longer matches, so a stale callback can
        // never dirty a UI that has already been restored (the "spinner forever" bug class).
        private int _lanRunGeneration;
        private readonly bool _previewMode;

        private Panel _serverPage;
        private TextBox _serverUrlBox;
        private Button _serverLockBtn;
        private TextBox _bootstrapBox;
        private Button _testBtn;
        private Label _serverStatus;
        private Label _serverMeta;
        private Button _serverNavBtn;

        public LanEditionForm(C c, O o, L l) : this(c, o, l, false) { }

        internal LanEditionForm(C c, O o, L l, bool previewMode) : base(c, o, l, previewMode)
        {
            _previewMode = previewMode;
            AddTransferColumns();
            Text = "万能驱动助手-局域网络版";
            scanButton.Text = "扫描驱动";
            fullPackageCheck.Visible = false;   // LAN has no model-package concept; hide the legacy checkbox.
            _lanCfg = new LanClientConfig
            {
                ServerUrl = "",
                Token = o.LanToken ?? "",
                BootstrapRepoDir = "",
                Deploy = true, WhatIf = false, Force = false,
                InstallTimeoutSeconds = c.General.InstallTimeoutSeconds,
                DedupeByContent = c.General.DedupeInfByContent,
                VerifySignature = c.General.VerifyDriverSignature
            };
            _lanClient = new LanDriverClient(_lanCfg, null, null, ResolveOsTarget(c), l,
                new WinTrustSignatureVerifier(), new WindowsAdminChecker(), null, new RealSystemOps());
            BuildServerPage();
            AddServerNav();
            LoadSettings();
            if (!previewMode) FormClosed += delegate { SaveSettings(); if (_lanCts != null) try { _lanCts.Cancel(); } catch { } };
        }

        // ----- startup: read hardware only, do not auto-scan (the user must connect first) -----

        protected override async Task InitializeAsync()
        {
            ApplyScreenSafeBounds();
            logger.W("[INFO] UDA 客户端版本 / Client version: " + LanProtocol.ServerVersion + "；硬件上报超时 / report timeout: 120 seconds");
            var actualTarget = ResolveOsTarget(config);
            logger.W("[INFO] 匹配系统参数 / Match OS target: arch=" + actualTarget.Architecture + " major=" + actualTarget.MajorVersion + " build=" + actualTarget.BuildNumber);
            headerStatus.Text = "正在读取本机硬件信息…";
            try
            {
                machine = await Task.Run(delegate { var value = WM.Get(logger); HardwareProbe.Enrich(value, logger); return value; });
                FillHardware(machine);
                sourceStatus.Text = "服务器：" + CurrentServerUrl() + " · 尚未连接";
                headerStatus.Text = "正在自动扫描驱动…";
                footerStatus.Text = "就绪";
                ShowPage(homePage);
                // Portable client behaviour: once local hardware is ready, automatically perform
                // the same cancellable scan as the button. BeginInvoke lets the initial layout paint
                // first and avoids blocking startup on network/repository work.
                if (!_previewMode && IsValidServerUrl(CurrentServerUrl()))
                    BeginInvoke(new Action(async delegate { await ScanDriversAsync(); }));
            }
            catch (Exception ex)
            {
                logger.W("[ERROR] 初始化失败：" + ex);
                headerStatus.Text = "硬件信息读取失败";
                footerStatus.Text = ex.Message;
                ShowPage(homePage);
            }
        }

        // ----- scan: bootstrap NIC if needed -> probe -> report -> fill candidates into the drivers grid -----

        protected override async Task ScanDriversAsync()
        {
            if (_lanScanning) { if (_lanCts != null) _lanCts.Cancel(); return; }
            string url = CurrentServerUrl();
            if (!IsValidServerUrl(url))
            {
                LanInfo("请先在「服务器」页设置有效的服务器地址，例如 http://192.168.0.253:8731", "地址无效", MessageBoxIcon.Warning);
                ShowPage(_serverPage);
                return;
            }
            _lanCfg.ServerUrl = url;
            _lanCts = new CancellationTokenSource();
            CancellationToken ct = _lanCts.Token;
            EnterLanBusyUi(false);
            headerStatus.Text = "正在连接服务器并扫描匹配候选…";
            footerStatus.Text = "扫描驱动";
            try
            {
                bool automaticRetryAllowed = _firstAutomaticScan;
                _firstAutomaticScan = false;
                // Every requested scan must refresh the inventory, not reuse pre-install WMI data.
                {
                    machine = await LoadMachineAsync();
                    ct.ThrowIfCancellationRequested();
                    FillHardware(machine);
                }
                IProgress<string> step = new Progress<string>(delegate(string m) { Ui(delegate { footerStatus.Text = m; }); });
                var boot = await Task.Run(delegate { return _lanClient.Bootstrap(machine.Devices, ct, step); });
                if (boot.BootstrapAttempted) logger.W("[INFO] 网卡引导 / NIC bootstrap: ok=" + boot.BootstrapOk + " fail=" + boot.BootstrapFail);
                LanHealth health = await Task.Run(delegate { return _lanClient.Probe(ct); });
                if (health == null) throw new Exception("无法连接服务器 " + url + "，请检查地址与网络");
                for (int wait = 0; wait < 30 && (!health.RepoLoaded || health.ImportRunning); wait++)
                {
                    ct.ThrowIfCancellationRequested();
                    footerStatus.Text = health.ImportRunning ? "服务器正在更新驱动仓库，等待完成…" : "服务器正在加载驱动仓库…";
                    await Task.Delay(1000, ct);
                    health = await Task.Run(delegate { return _lanClient.Probe(ct); });
                    if (health == null) throw new Exception("等待仓库就绪时与服务器断开连接");
                }
                if (!health.RepoLoaded) throw new Exception("服务器驱动仓库尚未加载完成，请稍后重试");
                logger.W("[INFO] 已连接服务器 / Connected server version: " + health.ServerVersion + "；准备上报 " + machine.Devices.Count + " 个设备");
                UpdateServerStatus(health);
                LanReportResponse resp = await Task.Run(delegate { return _lanClient.Report(machine.Devices, machine, ct, step); });
                int abnormal = machine.Devices.Count(d => d != null && (d.Bad || string.IsNullOrEmpty(d.Inf) || string.IsNullOrEmpty(d.Version)));
                if (automaticRetryAllowed && abnormal > 0 && resp.Candidates.Count == 0)
                {
                    logger.W("[INFO] 首次自动扫描无候选但存在 " + abnormal + " 个异常设备，刷新硬件后自动复扫");
                    footerStatus.Text = "异常设备仍在初始化，正在刷新硬件并自动复扫…";
                    await Task.Delay(1500, ct);
                    machine = await LoadMachineAsync();
                    ct.ThrowIfCancellationRequested();
                    FillHardware(machine);
                    resp = await Task.Run(delegate { return _lanClient.Report(machine.Devices, machine, ct, step); });
                }
                scanResult = BuildLanScanResult(resp, machine);
                FillDrivers();
                int picked = scanResult.Matches.Count(x => x.Pick);
                int noMatch = scanResult.Matches.Count(x => x.State == "没有匹配驱动");
                headerStatus.Text = "扫描完成 · 可安装/更新 " + picked + " 项" + (noMatch > 0 ? " · " + noMatch + " 个设备没有匹配驱动" : "");
                sourceStatus.Text = "服务器：" + url + " · " + (health.RepoLoaded ? (health.InfCount + " 个驱动") : "仓库未加载");
                matchCount.Text = picked.ToString();
                deploymentState.Text = "LAN 匹配";
                footerStatus.Text = "可安装/更新 " + picked + " 项" + (noMatch > 0 ? "，" + noMatch + " 个设备没有匹配驱动" : "");
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
                headerStatus.Text = "扫描失败 · 硬件信息仍可查看";
                footerStatus.Text = ex.Message;
                logger.W("[ERROR] " + ex);
                _serverStatus.Text = "扫描失败：" + ex.Message;
                ShowPage(_serverPage);
            }
            finally
            {
                // Success, failure, exception or cancel: one restore path, always on the UI thread.
                EndLanBusyUi(false);
            }
        }

        // ----- install: download + verify (SHA-256 + signature) + install the user-confirmed picks -----

        protected override async Task InstallAsync()
        {
            if (_lanInstalling) { if (_lanCts != null) _lanCts.Cancel(); return; }
            if (scanResult == null || _candByMatch == null || scanResult.Matches.Count == 0)
            {
                LanInfo("请先点击「扫描驱动」获取匹配候选。", "提示", MessageBoxIcon.Information);
                return;
            }
            var picked = new List<LanCandidateDto>();
            foreach (DataGridViewRow row in driversGrid.Rows)
            {
                var x = row.Tag as X;
                if (x == null) continue;
                x.Pick = Convert.ToBoolean(row.Cells[0].Value ?? false);
                LanCandidateDto c;
                if (x.Pick && _candByMatch.TryGetValue(x, out c)) picked.Add(c);
            }
            if (picked.Count == 0)
            {
                LanInfo("请先在驱动列表中勾选需要安装的候选。", "提示", MessageBoxIcon.Information);
                return;
            }
            if (!LanConfirm("即将从服务器下载并安装 " + picked.Count + " 个驱动包（校验哈希与签名），是否继续？", "安装确认")) return;
            _lanCfg.ServerUrl = CurrentServerUrl();
            _lanCfg.Deploy = true;
            _lanCfg.WhatIf = false;
            _lanCts = new CancellationTokenSource();
            CancellationToken ct = _lanCts.Token;
            ResetTransferColumns();
            EnterLanBusyUi(true);
            StartResourceMonitor();
            bool refreshList = false;
            try
            {
                int run = _lanRunGeneration;
                var prog = new Progress<LanInstallProgress>(delegate(LanInstallProgress p)
                {
                    Ui(delegate
                    {
                        if (IsDisposed || run != _lanRunGeneration) return;   // a late post must never dirty a restored UI
                        progress.Maximum = Math.Max(1, p.Total);
                        progress.Value = Math.Min(p.Current, p.Total);
                        progress.Style = ProgressBarStyle.Blocks;
                        footerStatus.Text = "正在" + p.Stage + " " + p.Current + "/" + p.Total + "  " + p.Name;
                        if (!string.IsNullOrEmpty(p.InfRelativePath)) UpdateTransferRow(p);
                    });
                });
                var roundProg = new Progress<string>(delegate(string s)
                {
                    Ui(delegate
                    {
                        if (IsDisposed || run != _lanRunGeneration) return;
                        headerStatus.Text = s;   // every round shows its stage
                        footerStatus.Text = s;
                    });
                });
                // Limited multi-round coordinator wired into「安装所选」: round 0 installs exactly the
                // user-confirmed picks; continuation rounds (max 5) re-collect → re-report → re-scan and
                // install only NEW, recommended-safe (install-type, trusted) candidates, deduped by
                // device+content, cancellable via the same token, with cumulative totals.
                var loop = await Task.Run(delegate { return _lanClient.RunControlledLoop(picked, ct, prog, roundProg, null); });
                string msg = "完成：成功 " + loop.InstalledOk + "，跳过 " + loop.InstalledSkip + "，失败 " + loop.InstalledFail
                    + (loop.InstalledUnverified > 0 ? "，绑定待确认 " + loop.InstalledUnverified : "")
                    + (loop.Downloaded > 0 ? "，下载 " + loop.Downloaded : "")
                    + (loop.Rounds > 0 ? "（含闭环 " + loop.Rounds + " 轮）" : "")
                    + (loop.StoppedAtMaxRounds ? " · 已达最大轮数" : "")
                    + (loop.Cancelled ? " · 已取消" : "") + (loop.Reboot ? " · 需重启" : "");
                if (!string.IsNullOrEmpty(loop.Error) && loop.Error != "cancelled") msg += " · " + loop.Error;
                footerStatus.Text = msg;
                // A complete download/install failure does not change device state, so keep the
                // current candidate list and avoid an unnecessary second server scan while offline.
                // Refresh after installs, already-current skips, or unverified bindings only.
                refreshList = !loop.Cancelled && (loop.InstalledOk > 0 || loop.InstalledSkip > 0 || loop.InstalledUnverified > 0);
                LanInfo(msg, "安装结果", (loop.InstalledFail > 0 || loop.InstalledUnverified > 0 || loop.Cancelled) ? MessageBoxIcon.Warning : MessageBoxIcon.Information);
            }
            catch (OperationCanceledException)
            {
                footerStatus.Text = "安装已取消";
                logger.W("[INFO] 安装已取消 / Install cancelled by user");
            }
            catch (Exception ex)
            {
                logger.W("[ERROR] 安装失败：" + ex);
                footerStatus.Text = "安装失败：" + ex.Message;
                LanInfo(ex.Message, "安装失败", MessageBoxIcon.Error);
            }
            finally
            {
                // Success, failure, exception or cancel: one restore path, always on the UI thread —
                // flags, buttons, progress (no marquee left running), wait cursor and the CTS.
                EndLanBusyUi(true);
            }
            if (refreshList)
            {
                // Refresh AFTER the busy UI has been restored: a refresh failure can no longer leave
                // the drivers page spinning forever.
                headerStatus.Text = "正在重新读取硬件并核对安装结果…";
                try
                {
                    machine = await LoadMachineAsync();
                    ct.ThrowIfCancellationRequested();
                    FillHardware(machine);
                    await ScanDriversAsync();
                }
                catch (Exception ex)
                {
                    logger.W("[ERROR] 安装后刷新失败：" + ex);
                    footerStatus.Text = "安装后刷新失败：" + ex.Message;
                }
            }
        }

        private void AddTransferColumns()
        {
            foreach (var spec in new[] { Tuple.Create("TransferTotal", "包大小", 92), Tuple.Create("TransferReceived", "已下载", 106), Tuple.Create("TransferRate", "速度", 100) })
            {
                if (driversGrid.Columns.Contains(spec.Item1)) continue;
                driversGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = spec.Item1, HeaderText = spec.Item2, Width = spec.Item3,
                    ReadOnly = true, SortMode = DataGridViewColumnSortMode.NotSortable, AutoSizeMode = DataGridViewAutoSizeColumnMode.None });
            }
        }

        private void ResetTransferColumns()
        {
            if (driversGrid == null) return;
            foreach (DataGridViewRow row in driversGrid.Rows)
            {
                row.Cells["TransferTotal"].Value = "等待";
                row.Cells["TransferReceived"].Value = "等待";
                row.Cells["TransferRate"].Value = "等待";
            }
        }

        private void UpdateTransferRow(LanInstallProgress p)
        {
            if (driversGrid == null || _candByMatch == null) return;
            string total = p.TotalBytes < 0 ? "未知" : (p.TotalBytes == 0 ? "读取中" : FormatTransferBytes(p.TotalBytes));
            string received = FormatTransferBytes(Math.Max(0, p.DownloadedBytes)) + (p.TotalBytes > 0 ? " / " + total : "");
            string rate = p.BytesPerSecond <= 0 ? "计算中" : FormatTransferBytes((long)p.BytesPerSecond) + "/秒";
            foreach (DataGridViewRow row in driversGrid.Rows)
            {
                var match = row.Tag as X; LanCandidateDto candidate;
                if (match == null || !_candByMatch.TryGetValue(match, out candidate) ||
                    !string.Equals(candidate.InfRelativePath, p.InfRelativePath, StringComparison.OrdinalIgnoreCase)) continue;
                row.Cells["TransferTotal"].Value = total;
                row.Cells["TransferReceived"].Value = received;
                row.Cells["TransferRate"].Value = rate;
            }
        }

        private static string FormatTransferBytes(long bytes)
        {
            string[] units = { "B", "KB", "MB", "GB", "TB" }; double value = Math.Max(0, bytes); int unit = 0;
            while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
            return value.ToString(unit == 0 ? "0" : "0.0", System.Globalization.CultureInfo.InvariantCulture) + " " + units[unit];
        }

        // ----- busy-state enter/restore (one path for success / failure / exception / cancel) -----

        private void EnterLanBusyUi(bool installing)
        {
            _lanRunGeneration++;
            _lanInstalling = installing;
            _lanScanning = !installing;
            scanButton.Text = "取消";
            scanButton.Enabled = !installing;
            installButton.Text = "取消";
            installButton.Enabled = installing;
            if (exportSelectedButton != null) exportSelectedButton.Enabled = false;
            if (exportAllButton != null) exportAllButton.Enabled = false;
            ResetPointer();
            if (!installing) progress.Style = ProgressBarStyle.Marquee;   // install progress is block-driven by callbacks
        }

        /// <summary>Single busy-state restore, run on the UI thread for every terminal path of a scan
        /// or install: flags, buttons, progress (never leaves a marquee running), wait cursor, CTS.</summary>
        internal void EndLanBusyUi(bool installing)
        {
            Ui(delegate
            {
                _lanRunGeneration++;
                _lanInstalling = false;
                _lanScanning = false;
                if (_lanCts != null) { try { _lanCts.Cancel(); } catch { } try { _lanCts.Dispose(); } catch { } _lanCts = null; }
                scanButton.Text = "扫描驱动";
                scanButton.Enabled = true;
                installButton.Text = "安装所选";
                installButton.Enabled = true;
                if (exportSelectedButton != null) exportSelectedButton.Enabled = true;
                if (exportAllButton != null) exportAllButton.Enabled = true;
                ResetPointer();
                progress.Style = ProgressBarStyle.Blocks;
                progress.Value = 0;
            });
        }

        // ----- dialogs (test seam: when the harness hooks these, no modal dialog blocks the pump) -----

        internal Action<string> TestMessageSink;
        internal Func<string, bool> TestConfirm;

        private void LanInfo(string text, string caption, MessageBoxIcon icon)
        {
            if (TestMessageSink != null) { TestMessageSink(caption + "|" + text); return; }
            MessageBox.Show(this, text, caption, MessageBoxButtons.OK, icon);
        }

        private bool LanConfirm(string text, string caption)
        {
            if (TestMessageSink != null) TestMessageSink(caption + "|" + text);
            if (TestConfirm != null) return TestConfirm(text);
            return MessageBox.Show(this, text, caption, MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes;
        }

        // ----- post-install hardware re-collection (test seam replaces WMI) -----

        internal Func<Machine> TestMachineProvider;

        private Task<Machine> LoadMachineAsync()
        {
            Func<Machine> provider = TestMachineProvider;
            if (provider != null) return Task.Run(provider);
            Machine previous = machine;
            return Task.Run(delegate {
                var v = WM.Get(logger, previous != null);
                CurrentDriverState.Refresh(v.Devices, logger);
                if (previous != null) { v.Maker = previous.Maker; v.Model = previous.Model; v.Sku = previous.Sku; v.Serial = previous.Serial; v.OS = previous.OS; v.Info = new List<string[]>(previous.Info); }
                else HardwareProbe.Enrich(v, logger);
                return v;
            });
        }

        // ----- LAN GUI state harness seams (never used in production) -----

        internal LanDriverClient TestLanClient { set { _lanClient = value; } }
        internal void TestSetServerUrl(string url) { if (_serverUrlBox != null) _serverUrlBox.Text = url ?? ""; _lanCfg.ServerUrl = url ?? ""; }

        internal void TestSetMachine(Machine m) { machine = m; FillHardware(m); if (TestMachineProvider == null) TestMachineProvider = () => m; }
        internal Task TestScanAsync() { return ScanDriversAsync(); }
        internal Task TestInstallAsync() { return InstallAsync(); }
        internal void TestEnterBusyUi(bool installing) { EnterLanBusyUi(installing); }
        internal bool TestTransferColumnsPresent()
        {
            return driversGrid.Columns.Contains("TransferTotal") && driversGrid.Columns.Contains("TransferReceived") && driversGrid.Columns.Contains("TransferRate") &&
                driversGrid.Columns["TransferTotal"].HeaderText == "包大小" && driversGrid.Columns["TransferReceived"].HeaderText == "已下载" && driversGrid.Columns["TransferRate"].HeaderText == "速度";
        }

        internal bool TestBusyUiRestored()
        {
            return !_lanScanning && !_lanInstalling && _lanCts == null && !UseWaitCursor
                && scanButton.Enabled && installButton.Enabled
                && string.Equals(scanButton.Text, "扫描驱动", StringComparison.Ordinal)
                && string.Equals(installButton.Text, "安装所选", StringComparison.Ordinal)
                && progress.Style == ProgressBarStyle.Blocks && progress.Value == 0;
        }

        // ----- DPI: keep the LAN server page's own fixed paddings/controls scaled with the live DPI -----

        protected override void ApplyResponsiveRules()
        {
            base.ApplyResponsiveRules();
            if (_testBtn != null) _testBtn.Height = ScalePx(34);
            if (_serverLockBtn != null) { _serverLockBtn.Height = ScalePx(30); _serverLockBtn.Width = ScalePx(40); }
            if (_serverUrlBox != null) _serverUrlBox.Height = ScalePx(30);
            if (_bootstrapBox != null) _bootstrapBox.Height = ScalePx(30);
            if (_serverPage != null && _serverPage.Controls.Count > 0)
            {
                var root = _serverPage.Controls[0] as TableLayoutPanel;
                if (root != null) root.Padding = new Padding(ScalePx(18));
            }
        }

        // ----- map server candidates onto the engine's R/X model so FillDrivers() works unchanged -----

        private R BuildLanScanResult(LanReportResponse resp, Machine m)
        {
            _candByMatch = new Dictionary<X, LanCandidateDto>();
            var r = new R { PC = m, Mode = "LAN 服务器匹配", Package = false, Mapping = _lanCfg.ServerUrl };
            var deviceById = m.Devices.Where(d => !string.IsNullOrEmpty(d.Id)).ToDictionary(d => d.Id, StringComparer.OrdinalIgnoreCase);
            var matchedDeviceIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var cand in resp.Candidates)
            {
                if (string.IsNullOrWhiteSpace(cand.ContentHash)) continue;
                if (_lanCfg.VerifySignature && string.Equals(cand.SignatureStatus, "untrusted", StringComparison.OrdinalIgnoreCase)) continue;
                if (!string.IsNullOrEmpty(cand.DeviceInstanceId)) matchedDeviceIds.Add(cand.DeviceInstanceId);
                D d = null;
                if (!string.IsNullOrEmpty(cand.DeviceInstanceId)) deviceById.TryGetValue(cand.DeviceInstanceId, out d);
                // A historical receipt may only suppress a candidate when the device currently ACTUALLY
                // runs that exact package. A missing/broken/unknown-version device must always surface.
                if (LanDriverClient.IsReceiptSuppressed(cand, d)) continue;
                if (!string.IsNullOrEmpty(cand.LocalRejectionReason)) continue;
                if (d == null)
                    d = new D { Name = string.IsNullOrWhiteSpace(cand.FriendlyName) ? (string.IsNullOrEmpty(cand.Class) ? "未知硬件" : cand.Class) : cand.FriendlyName,
                        Id = cand.DeviceInstanceId, Class = cand.Class, ClassGuid = cand.ClassGuid, Version = "", Inf = "" };
                var i = new I { Path = cand.InfRelativePath, Version = cand.DriverVerVersion, Class = cand.Class };
                bool newer = LanVersion.IsNewer(cand.DriverVerVersion, d.Version);
                // Install-type: Bad / no CurrentInf / empty current version (no valid installed driver).
                // Update-type: a genuinely installed older driver.
                bool installType = d.Bad || string.IsNullOrEmpty(d.Inf) || string.IsNullOrEmpty(d.Version);
                string state = installType ? "安装驱动" : (newer ? "更新驱动" : "已是最新");
                if (state == "已是最新") continue;
                bool pick = true;
                // Exact hardware-ID match (level 4/3) takes precedence over compatible-ID matches;
                // the server's MatchLevel is authoritative when present, Tier is the legacy fallback.
                int matchLevel = cand.MatchLevel > 0 ? cand.MatchLevel : cand.Tier;
                var x = new X { D = d, I = i, Cat = DriverCategoryMapper.Friendly(cand.Class),
                    Type = (matchLevel >= 3 ? "精确硬件 ID" : "兼容 ID"), MatchTier = matchLevel, Reason = cand.MatchedId, State = state, Pick = pick };
                r.Matches.Add(x);
                _candByMatch[x] = cand;
            }
            AddUnmatchedDevices(resp, m, deviceById, matchedDeviceIds, r);
            return r;
        }

        /// <summary>Devices that got no applicable candidate are listed explicitly with the
        /// "没有匹配驱动" state instead of being silently absent. The server's UnmatchedDevices
        /// list (with its precise rejection reason) is authoritative; for old servers that do not
        /// send it, devices that still need a driver but received no candidate get the same state.</summary>
        private void AddUnmatchedDevices(LanReportResponse resp, Machine m,
            Dictionary<string, D> deviceById, HashSet<string> matchedDeviceIds, R r)
        {
            if (resp.UnmatchedDevices != null && resp.UnmatchedDevices.Count > 0)
            {
                foreach (var u in resp.UnmatchedDevices)
                {
                    if (u == null || string.IsNullOrEmpty(u.DeviceInstanceId)) continue;
                    D d;
                    deviceById.TryGetValue(u.DeviceInstanceId, out d);
                    var x = new X
                    {
                        D = d != null ? d : new D { Name = string.IsNullOrWhiteSpace(u.FriendlyName) ? "未知硬件" : u.FriendlyName, Id = u.DeviceInstanceId },
                        I = new I { Path = "", Version = "" },
                        Cat = d != null ? DriverCategoryMapper.Friendly(d.Class) : "",
                        Type = "没有匹配驱动",
                        Reason = string.IsNullOrWhiteSpace(u.Reason) ? u.ReasonCode : u.ReasonCode + " " + u.Reason,
                        State = "没有匹配驱动",
                        Pick = false
                    };
                    r.Matches.Add(x);
                }
                return;
            }
            // Old-server fallback: any device that needs a driver but got no candidate row.
            foreach (var d in m.Devices)
            {
                if (d == null || string.IsNullOrEmpty(d.Id) || matchedDeviceIds.Contains(d.Id)) continue;
                if (!(d.Bad || string.IsNullOrEmpty(d.Inf) || string.IsNullOrEmpty(d.Version))) continue;
                r.Matches.Add(new X
                {
                    D = d,
                    I = new I { Path = "", Version = "" },
                    Cat = DriverCategoryMapper.Friendly(d.Class),
                    Type = "没有匹配驱动",
                    Reason = "服务器没有返回适用于该设备的驱动候选 / the server returned no applicable driver candidate",
                    State = "没有匹配驱动",
                    Pick = false
                });
            }
        }

        // ----- test connection (single health probe) -----

        private async Task TestConnectionAsync()
        {
            string url = CurrentServerUrl();
            if (!IsValidServerUrl(url)) { MessageBox.Show(this, "请输入有效的服务器地址，例如 http://192.168.0.253:8731", "地址无效", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
            _lanCfg.ServerUrl = url;
            _testBtn.Enabled = false;
            progress.Style = ProgressBarStyle.Marquee;
            footerStatus.Text = "正在连接服务器…";
            headerStatus.Text = "正在测试连接…";
            try
            {
                LanHealth h = await Task.Run(delegate { return _lanClient.Probe(CancellationToken.None); });
                if (h == null)
                {
                    _serverStatus.Text = "连接失败：无法访问 " + url;
                    _serverMeta.Text = "请确认服务器已启动且地址正确";
                }
                else
                {
                    UpdateServerStatus(h);
                    LanReportResponse reportProbe = await Task.Run(delegate { return _lanClient.ProbeReportAccess(CancellationToken.None); });
                    if (reportProbe == null || !reportProbe.Ok)
                    {
                        string reason = reportProbe != null && !string.IsNullOrWhiteSpace(reportProbe.Error) ? reportProbe.Error : "上报接口无响应";
                        _serverStatus.Text = "连接成功，但硬件上报不可用";
                        _serverMeta.Text = reason;
                        sourceStatus.Text = "服务器：" + url + " · 上报预检失败";
                    }
                    else sourceStatus.Text = "服务器：" + url + " · 连接及上报预检正常";
                }
                logger.W("[INFO] " + _serverStatus.Text);
            }
            catch (Exception ex) { _serverStatus.Text = "连接失败：" + ex.Message; _serverMeta.Text = "-"; }
            finally
            {
                _testBtn.Enabled = true;
                progress.Style = ProgressBarStyle.Blocks;
                progress.Value = 0;
                footerStatus.Text = _serverStatus.Text;
                headerStatus.Text = "就绪";
            }
        }

        private void UpdateServerStatus(LanHealth h)
        {
            if (h == null) { _serverStatus.Text = "尚未连接服务器"; _serverMeta.Text = "-"; return; }
            _serverStatus.Text = h.RepoLoaded
                ? ("已连接 · 仓库已加载 · " + h.InfCount + " 个驱动 · 服务器版本 " + h.ServerVersion)
                : "已连接，但服务器仓库尚未加载";
            _serverMeta.Text = "OS 过滤：" + (string.IsNullOrEmpty(h.OsArchitecture) ? "amd64" : h.OsArchitecture) + " v" + h.OsMajorVersion
                + (h.TokenRequired ? " · 需要 Token" : " · 无需 Token（内网只读）");
        }

        // ----- the "服务器" settings page + nav entry -----

        private void BuildServerPage()
        {
            _serverPage = NewPage();
            _serverPage.AutoScroll = true;
            var root = new TableLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                ColumnCount = 1,
                RowCount = 3,
                Margin = Padding.Empty,
                BackColor = background,
                Padding = new Padding(18)
            };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 40F));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            var title = NewLabel("服务器连接", 12F, Color.FromArgb(30, 42, 60), true);
            title.Dock = DockStyle.Fill;
            title.TextAlign = ContentAlignment.MiddleLeft;
            title.Margin = new Padding(2, 0, 0, 10);
            root.Controls.Add(title, 0, 0);
            root.Controls.Add(BuildConnectionCard(), 0, 1);
            root.Controls.Add(BuildGuideCard(), 0, 2);
            _serverPage.Controls.Add(root);
            contentHost.Controls.Add(_serverPage);
        }

        private Control BuildConnectionCard()
        {
            var card = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
                ColumnCount = 3, RowCount = 5, BackColor = Color.White, Padding = new Padding(18), Margin = new Padding(0, 0, 0, 12) };
            card.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120F));
            card.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            card.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            for (int i = 0; i < 5; i++) card.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            card.Controls.Add(FieldLabel("驱动服务器"), 0, 0);
            _serverUrlBox = new TextBox { Dock = DockStyle.Fill, ReadOnly = true, BackColor = Color.FromArgb(248, 250, 253),
                Font = new Font("Microsoft YaHei UI", 9F), Margin = new Padding(0, 6, 8, 6), Height = 30 };
            card.Controls.Add(_serverUrlBox, 1, 0);
            _serverLockBtn = MakeLockButton();
            card.Controls.Add(_serverLockBtn, 2, 0);

            card.Controls.Add(FieldLabel("连接测试"), 0, 1);
            var testRow = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, AutoSize = false,
                FlowDirection = FlowDirection.LeftToRight, Margin = new Padding(0, 6, 0, 6) };
            _testBtn = NewActionButton("测试连接", false, 110);
            _testBtn.Click += async delegate { await TestConnectionAsync(); };
            testRow.Controls.Add(_testBtn);
            card.Controls.Add(testRow, 1, 1);
            card.Controls.Add(new Label(), 2, 1);

            card.Controls.Add(FieldLabel("服务器状态"), 0, 2);
            _serverStatus = new Label { Dock = DockStyle.Fill, AutoEllipsis = true, ForeColor = Color.FromArgb(35, 48, 67),
                Font = new Font("Microsoft YaHei UI", 9F), Text = "尚未连接服务器", TextAlign = ContentAlignment.MiddleLeft, Margin = new Padding(0, 6, 0, 2) };
            card.Controls.Add(_serverStatus, 1, 2);
            card.Controls.Add(new Label(), 2, 2);

            card.Controls.Add(FieldLabel("仓库信息"), 0, 3);
            _serverMeta = new Label { Dock = DockStyle.Fill, AutoEllipsis = true, ForeColor = muted,
                Font = new Font("Microsoft YaHei UI", 9F), Text = "-", TextAlign = ContentAlignment.MiddleLeft, Margin = new Padding(0, 2, 0, 6) };
            card.Controls.Add(_serverMeta, 1, 3);
            card.Controls.Add(new Label(), 2, 3);

            card.Controls.Add(FieldLabel("网卡引导仓库"), 0, 4);
            _bootstrapBox = new TextBox { Dock = DockStyle.Fill, ReadOnly = true, BackColor = Color.FromArgb(248, 250, 253),
                Font = new Font("Microsoft YaHei UI", 9F), Margin = new Padding(0, 6, 8, 6), Height = 30 };
            card.Controls.Add(_bootstrapBox, 1, 4);
            var browse = NewActionButton("浏览…", false, 84);
            browse.Click += delegate
            {
                using (var f = new FolderBrowserDialog { Description = "选择本地网卡引导仓库（可选）" })
                    if (f.ShowDialog(this) == DialogResult.OK) { _bootstrapBox.Text = f.SelectedPath; _lanCfg.BootstrapRepoDir = f.SelectedPath; SaveSettings(); }
            };
            card.Controls.Add(browse, 2, 4);
            return card;
        }

        private Control BuildGuideCard()
        {
            var card = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
                ColumnCount = 1, RowCount = 2, BackColor = Color.White, Padding = new Padding(18), Margin = new Padding(0, 0, 0, 12) };
            card.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            card.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            card.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            var h = NewLabel("操作指引", 11F, Color.FromArgb(30, 42, 60), true);
            h.Margin = new Padding(0, 0, 0, 8);
            card.Controls.Add(h, 0, 0);
            var body = new Label { Dock = DockStyle.Top, AutoSize = true, ForeColor = Color.FromArgb(60, 72, 90),
                Font = new Font("Microsoft YaHei UI", 9F), TextAlign = ContentAlignment.TopLeft };
            body.Text = "1. 点击「测试连接」确认服务器可达，并查看仓库驱动数量。\n"
                + "2. 客户端启动后会自动扫描；也可点击顶部「扫描驱动」重新扫描。\n"
                + "3. 在「驱动管理」勾选需要的候选，点击「安装所选」下载并安装（校验哈希与签名）。\n"
                + "4. 安装全程可点击「取消」中止；实时进度见底部状态栏与「运行日志」。\n\n"
                + "地址默认锁定只读：点击锁头按钮解锁编辑，再次点击上锁并保存。内网无 Token 读取模式下客户端仅读取服务端匹配结果，无仓库写权限。";
            card.Controls.Add(body, 0, 1);
            return card;
        }

        private void AddServerNav()
        {
            _serverNavBtn = NewNavButton("服务器", delegate { ShowPage(_serverPage); });
            navigation.Controls.Add(_serverNavBtn);
            // Insert right after "概览" (logo, subtitle, 概览) so the LAN settings entry is prominent.
            navigation.Controls.SetChildIndex(_serverNavBtn, 3);
        }

        private Label FieldLabel(string text)
        {
            return new Label { Text = text, Dock = DockStyle.Fill, AutoEllipsis = true, ForeColor = muted,
                Font = new Font("Microsoft YaHei UI", 9F), TextAlign = ContentAlignment.MiddleLeft, Margin = new Padding(0, 6, 10, 6) };
        }

        private Button MakeLockButton()
        {
            var b = new Button { Text = "🔒", Font = new Font("Segoe UI Emoji", 10F), Width = 40, Height = 30,
                FlatStyle = FlatStyle.Flat, BackColor = Color.White, ForeColor = Color.FromArgb(48, 62, 82), Margin = new Padding(0, 5, 0, 5) };
            b.FlatAppearance.BorderColor = Color.FromArgb(215, 222, 232);
            b.Click += delegate { ToggleServerLock(); };
            return b;
        }

        private void ToggleServerLock()
        {
            if (_serverUrlBox.ReadOnly)
            {
                _serverUrlBox.ReadOnly = false;
                _serverUrlBox.BackColor = Color.White;
                _serverLockBtn.Text = "🔓";
                _serverUrlBox.Focus();
                _serverUrlBox.SelectAll();
            }
            else
            {
                string url = (_serverUrlBox.Text ?? "").Trim();
                _serverUrlBox.Text = url;
                _serverUrlBox.ReadOnly = true;
                _serverUrlBox.BackColor = Color.FromArgb(248, 250, 253);
                _serverLockBtn.Text = "🔒";
                _lanCfg.ServerUrl = url;
                SaveSettings();
                _serverStatus.Text = "地址已保存：" + (url.Length == 0 ? "（空）" : url);
                _serverMeta.Text = "点击「测试连接」验证新地址";
            }
        }

        // ----- url helpers + OS target -----

        private string CurrentServerUrl() { return (_serverUrlBox != null ? (_serverUrlBox.Text ?? "") : "").Trim().TrimEnd('/'); }

        private static bool IsValidServerUrl(string url)
        {
            Uri parsed;
            return Uri.TryCreate(url, UriKind.Absolute, out parsed) && (parsed.Scheme == "http" || parsed.Scheme == "https");
        }

        private static OsTarget ResolveOsTarget(C c)
        {
            var env = OsTarget.FromEnvironment();
            return new OsTarget
            {
                Architecture = !string.IsNullOrWhiteSpace(c.General.TargetArchitecture) ? c.General.TargetArchitecture.Trim().ToLowerInvariant() : env.Architecture,
                MajorVersion = c.General.TargetOsMajorVersion > 0 ? c.General.TargetOsMajorVersion : env.MajorVersion,
                BuildNumber = env.BuildNumber
            };
        }

        // ----- persisted settings (server url + bootstrap repo) -----

        private string SettingsPath
        {
            get
            {
                string d = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UniversalDriverAssistant");
                Directory.CreateDirectory(d);
                // Scope overrides to the distributed server endpoint, not the extraction path.
                // A package from another server must never inherit the old server's override.
                string defaultsPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "LanClient.defaults.json");
                if (File.Exists(defaultsPath))
                {
                    var defaults = new JavaScriptSerializer().Deserialize<LanClientUiSettings>(File.ReadAllText(defaultsPath, Encoding.UTF8));
                    if (defaults != null) return Path.Combine(d, LanClientUiSettings.SettingsFileName(defaults.ServerUrl));
                }
                return Path.Combine(d, "LanClient.json");
            }
        }

        private void LoadSettings()
        {
            var defaults = new LanClientUiSettings();
            string defaultsPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "LanClient.defaults.json");
            try { if (File.Exists(defaultsPath)) { var loaded = new JavaScriptSerializer().Deserialize<LanClientUiSettings>(File.ReadAllText(defaultsPath, Encoding.UTF8)); if (loaded != null) defaults = loaded; } } catch { }
            var local = new LanClientUiSettings { ServerUrl = "", Token = "", BootstrapRepo = "" };
            try { if (File.Exists(SettingsPath)) { var loaded = new JavaScriptSerializer().Deserialize<LanClientUiSettings>(File.ReadAllText(SettingsPath, Encoding.UTF8)); if (loaded != null) local = loaded; } } catch { }
            string url = !string.IsNullOrWhiteSpace(options.Lan) ? options.Lan : (!string.IsNullOrWhiteSpace(local.ServerUrl) ? local.ServerUrl : defaults.ServerUrl);
            if (string.IsNullOrWhiteSpace(url)) url = new LanClientUiSettings().ServerUrl;
            _serverUrlBox.Text = url;
            string boot = !string.IsNullOrWhiteSpace(options.BootstrapRepo) ? options.BootstrapRepo : (!string.IsNullOrWhiteSpace(local.BootstrapRepo) ? local.BootstrapRepo : defaults.BootstrapRepo);
            if (string.IsNullOrWhiteSpace(boot)) boot = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "NicBootstrap");
            _bootstrapBox.Text = boot;
            _lanCfg.ServerUrl = (_serverUrlBox.Text ?? "").Trim();
            _lanCfg.Token = !string.IsNullOrWhiteSpace(options.LanToken) ? options.LanToken : (defaults.Token ?? "");
            _lanCfg.BootstrapRepoDir = (_bootstrapBox.Text ?? "").Trim();
        }

        private void SaveSettings()
        {
            try
            {
                var s = new LanClientUiSettings
                {
                    ServerUrl = (_serverUrlBox != null ? (_serverUrlBox.Text ?? "") : "").Trim(),
                    Token = "",
                    BootstrapRepo = (_bootstrapBox != null ? (_bootstrapBox.Text ?? "") : "").Trim()
                };
                File.WriteAllText(SettingsPath, new JavaScriptSerializer().Serialize(s), new UTF8Encoding(false));
            }
            catch { }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && _lanCts != null) { try { _lanCts.Cancel(); } catch { } }
            base.Dispose(disposing);
        }

        // ----- preview support for the LAN layout harness -----

        protected internal override void LoadPreviewData()
        {
            base.LoadPreviewData();
            Text = "万能驱动助手-局域网络版";
            scanButton.Text = "扫描驱动";
            fullPackageCheck.Visible = false;
            fullPackageCheck.Enabled = false;
            fullPackageCheck.Checked = false;
            _serverUrlBox.Text = "http://192.168.0.253:8731";
            _bootstrapBox.Text = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "NicBootstrap");
            _serverStatus.Text = "已连接 · 仓库已加载 · 1280 个驱动 · 服务器版本 3.0";
            _serverMeta.Text = "OS 过滤：amd64 v10 · 无需 Token（内网只读）";
            sourceStatus.Text = "服务器：http://192.168.0.253:8731 · 已连接";
            deploymentState.Text = "LAN 匹配";
            driversGrid.Rows.Clear();
            for (int i = 0; i < 6; i++)
            {
                driversGrid.Rows.Add(i < 3, i == 0 ? "缺少驱动" : (i < 3 ? "可更新" : "已是最新"),
                    "硬件设备 " + (i + 1), i % 2 == 0 ? "Net" : "Display", "1.0." + i, "2.0." + i,
                    i % 2 == 0 ? "精确硬件 ID" : "兼容 ID", "lan-driver-" + i + ".inf");
            }
        }

        protected internal override void SelectPreviewPage(string page)
        {
            if (string.Equals(page, "server", StringComparison.OrdinalIgnoreCase)) ShowPage(_serverPage);
            else base.SelectPreviewPage(page);
        }
    }
}
