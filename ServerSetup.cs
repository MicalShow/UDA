// ServerSetup.cs - elevated, dependency-free Windows installer for the LAN server.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.IO.Compression;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;
using System.Management;
using System.Text.RegularExpressions;
using System.Threading;

namespace UDA
{
    /// <summary>
    /// Testable install wait/diagnostics core used by the installer GUI. The installer must never
    /// report success on faith: after sc start it polls the SCM until the service is RUNNING, then
    /// polls the local /api/health and /admin endpoints (the repo loads in the background, so health
    /// answers "loading" first). Any failure surfaces the sc query state and the service event log
    /// tail so an operator sees WHY instead of a false "success".
    /// </summary>
    public static class ServerSetupWait
    {
        public const string ServiceName = "UdaLanDriverService";

        /// <summary>Poll <paramref name="probe"/> until it returns true or the timeout elapses.
        /// Cancellation via <paramref name="cancelled"/>. Returns false on timeout/cancel.</summary>
        public static bool WaitUntil(Func<bool> probe, TimeSpan timeout, Func<bool> cancelled, Action progress)
        {
            DateTime deadline = DateTime.UtcNow + timeout;
            while (!IsCancelled(cancelled))
            {
                bool ok = false;
                try { ok = probe != null && probe(); } catch { }
                if (ok) return true;
                if (DateTime.UtcNow >= deadline) return false;
                try { if (progress != null) progress(); } catch { }
                Thread.Sleep(1000);
            }
            return false;
        }

        private static bool IsCancelled(Func<bool> cancelled) { try { return cancelled != null && cancelled(); } catch { return false; } }

        /// <summary>Wait for the service to reach RUNNING. A STOPPED service fails fast (no point
        /// waiting out the timeout); the returned diagnostics carry the raw sc query state.</summary>
        public static bool WaitForServiceRunning(Func<string> queryService, TimeSpan timeout, out string diagnostics)
        {
            diagnostics = "";
            DateTime deadline = DateTime.UtcNow + timeout;
            while (true)
            {
                string state = queryService != null ? (queryService() ?? "") : "";
                if (state.IndexOf("RUNNING", StringComparison.OrdinalIgnoreCase) >= 0) return true;
                diagnostics = state;
                if (state.IndexOf("STOPPED", StringComparison.OrdinalIgnoreCase) >= 0) return false;
                if (DateTime.UtcNow >= deadline) return false;
                Thread.Sleep(1000);
            }
        }

        /// <summary>One GET probe. Returns (statusCode, body); "0" means unreachable.</summary>
        public static Tuple<int, string> HttpProbe(string url, int timeoutMs)
        {
            try
            {
                var req = (HttpWebRequest)WebRequest.Create(url);
                req.Method = "GET";
                req.Timeout = timeoutMs;
                req.ReadWriteTimeout = timeoutMs;
                req.AllowAutoRedirect = false;
                using (var resp = (HttpWebResponse)req.GetResponse())
                using (var sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                    return Tuple.Create((int)resp.StatusCode, sr.ReadToEnd());
            }
            catch (WebException wex)
            {
                var resp = wex.Response as HttpWebResponse;
                if (resp == null) return Tuple.Create(0, wex.Message ?? "");
                try
                {
                    using (var sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8)) return Tuple.Create((int)resp.StatusCode, sr.ReadToEnd());
                }
                catch { return Tuple.Create((int)resp.StatusCode, ""); }
            }
            catch (Exception ex) { return Tuple.Create(0, ex.Message ?? ""); }
        }

        /// <summary>Poll a health endpoint until it answers 200. When the body reports
        /// RepoLoaded=false the caller-supplied progress shows "正在加载仓库" instead of failing.</summary>
        public static Tuple<bool, string> WaitForHttpReady(Func<Tuple<int, string>> probe, TimeSpan timeout, Action<string> progress)
        {
            DateTime deadline = DateTime.UtcNow + timeout;
            string lastBody = "";
            while (true)
            {
                Tuple<int, string> r = null;
                try { r = probe(); } catch { }
                if (r != null && r.Item1 == 200)
                {
                    lastBody = r.Item2 ?? "";
                    return Tuple.Create(true, lastBody);
                }
                if (r != null && r.Item1 > 0) lastBody = r.Item2 ?? "";
                if (DateTime.UtcNow >= deadline) return Tuple.Create(false, lastBody);
                try { if (progress != null) progress("正在加载驱动仓库，后台就绪后继续… / repo loading in background"); } catch { }
                Thread.Sleep(1000);
            }
        }

        /// <summary>Human-readable failure diagnostics: the sc query state plus the tail of
        /// service-events.log, so a Stopped/timeout failure is always explainable.</summary>
        public static string BuildDiagnostics(string scQueryOutput, string eventLogPath, int tailLines)
        {
            var sb = new StringBuilder();
            sb.AppendLine("服务状态 / service state:");
            sb.AppendLine(string.IsNullOrWhiteSpace(scQueryOutput) ? "  (sc query 无输出 / no sc query output)" : scQueryOutput.TrimEnd());
            sb.AppendLine("服务事件日志末尾 / service-events.log tail:");
            try
            {
                if (!string.IsNullOrWhiteSpace(eventLogPath) && File.Exists(eventLogPath))
                {
                    List<string> lines = ReadLastLines(eventLogPath, tailLines);
                    foreach (string line in lines) sb.AppendLine("  " + line);
                }
                else sb.AppendLine("  (日志不存在 / event log not found)");
            }
            catch (Exception ex) { sb.AppendLine("  (读取日志失败 / failed to read log: " + ex.Message + ")"); }
            return sb.ToString();
        }

        private static List<string> ReadLastLines(string path, int count)
        {
            var lines = new List<string>();
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var sr = new StreamReader(fs, Encoding.UTF8))
            {
                string line;
                while ((line = sr.ReadLine()) != null) { lines.Add(line); if (lines.Count > count) lines.RemoveAt(0); }
            }
            return lines;
        }
    }

    public static class ServerSetupProgram
    {
        [STAThread]
        public static int Main(string[] args)
        {
            AppContext.SetSwitch("Switch.System.IO.UseLegacyPathHandling", false);
            AppContext.SetSwitch("Switch.System.IO.BlockLongPaths", false);
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += delegate(object sender, ThreadExceptionEventArgs e) { ReportStartupError(e.Exception); };
            try
            {
            if (!IsAdministrator())
            {
                try
                {
                    using (var elevated = Process.Start(new ProcessStartInfo(Application.ExecutablePath)
                    { UseShellExecute = true, Verb = "runas", WorkingDirectory = AppDomain.CurrentDomain.BaseDirectory }))
                    { if (elevated != null) elevated.WaitForExit(); }
                    return 0;
                }
                catch (System.ComponentModel.Win32Exception ex)
                {
                    if (ex.NativeErrorCode != 1223) MessageBox.Show("无法请求管理员权限：" + ex.Message, "服务端安装程序", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return 2;
                }
            }
            if (!File.Exists(Application.ExecutablePath + ".config") && System.Reflection.Assembly.GetExecutingAssembly().GetManifestResourceNames().Any(n => n.EndsWith("ServerPayload.zip", StringComparison.OrdinalIgnoreCase)))
            {
                string runtimeDirectory = Path.Combine(Path.GetTempPath(), "UDA-SetupRuntime-" + Guid.NewGuid().ToString("N"));
                string configured = null;
                try
                {
                    configured = ServerUpgrade.PrepareInstallerRuntime(Application.ExecutablePath, runtimeDirectory);
                    using (var child = Process.Start(new ProcessStartInfo(configured) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = runtimeDirectory, Arguments = string.Join(" ", args.Select(a => "\"" + a.Replace("\"", "\\\"") + "\"")) }))
                    { if (child == null) throw new IOException("安装程序运行时启动失败"); child.WaitForExit(); return child.ExitCode; }
                }
                finally
                {
                    // Remove only the two files created here; never recursively delete TEMP.
                    try { if (configured != null) { File.Delete(configured); File.Delete(configured + ".config"); } if (Directory.Exists(runtimeDirectory)) Directory.Delete(runtimeDirectory, false); } catch { }
                }
            }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new ServerSetupForm());
            return 0;
            }
            catch (Exception ex) { ReportStartupError(ex); return 1; }
        }

        private static void ReportStartupError(Exception ex)
        {
            string log = Path.Combine(Path.GetTempPath(), "UDA-Server-Setup-error.log");
            try { File.AppendAllText(log, DateTime.Now.ToString("o") + Environment.NewLine + ex + Environment.NewLine, Encoding.UTF8); } catch { }
            MessageBox.Show("安装程序运行失败：" + ex.Message + "\n\n详细日志：" + log, "服务端安装程序", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }

        private static bool IsAdministrator()
        {
            using (WindowsIdentity identity = WindowsIdentity.GetCurrent())
                return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
    }

    public static class ServerSetupDiscovery
    {
        public static string ReadArgument(string command, string name)
        {
            var match = Regex.Match(command ?? "", "(?:^|\\s)" + Regex.Escape(name) + "\\s+(?:\"([^\"]*)\"|(\\S+))", RegexOptions.IgnoreCase);
            return match.Success ? (match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value) : "";
        }
        public static string ExistingServiceCommand()
        {
            try
            {
                using (var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\UdaLanDriverService"))
                    return key == null ? "" : Convert.ToString(key.GetValue("ImagePath", ""));
            }
            catch { return ""; }
        }
        public static List<string> NetworkAddresses()
        {
            var addresses = new List<string>();
            try
            {
                foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces().Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback))
                {
                    try
                    {
                        foreach (var address in adapter.GetIPProperties().UnicastAddresses.Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a.Address)))
                            addresses.Add(address.Address + " · " + adapter.Name);
                    }
                    catch { }
                }
            }
            catch { }
            return addresses.Distinct().OrderBy(a => a.StartsWith("169.254.", StringComparison.Ordinal) ? 1 : 0).ToList();
        }
    }

    internal sealed class ServerSetupForm : Form
    {
        private readonly TextBox _repo = Box();
        private readonly NumericUpDown _port = new NumericUpDown { Minimum = 1, Maximum = 65535, Value = 8731, Dock = DockStyle.Fill };
        private readonly TextBox _publicUrl = Box();
        private readonly ComboBox _networkAddress = new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList };
        private readonly TextBox _user = Box();
        private readonly TextBox _password = Box();
        private readonly TextBox _confirm = Box();
        private readonly Label _status = new Label { Dock = DockStyle.Fill, AutoSize = true, ForeColor = Color.FromArgb(50, 70, 95) };
        private readonly ProgressBar _progress = new ProgressBar { Dock = DockStyle.Fill, Style = ProgressBarStyle.Marquee, MarqueeAnimationSpeed = 25, Visible = false };
        private readonly Button _install = new Button { Text = "安装并启动", Height = 38, Dock = DockStyle.Fill, BackColor = Color.FromArgb(40, 110, 220), ForeColor = Color.White, FlatStyle = FlatStyle.Flat };
        private readonly Button _uninstall = new Button { Text = "卸载服务端", Height = 34, Dock = DockStyle.Fill };
        private readonly CheckBox _resetCredentials = new CheckBox { Text = "重置管理员凭据", AutoSize = true, Dock = DockStyle.Left };
        private readonly object _outputLock = new object();
        private readonly StringBuilder _processOutput = new StringBuilder();

        public ServerSetupForm()
        {
            Text = "万能驱动助手 · 服务端安装程序 v5.3.9";
            Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            StartPosition = FormStartPosition.CenterScreen;
            MinimumSize = new Size(620, 600);
            ClientSize = new Size(700, 650);
            AutoScroll = true;
            Font = new Font("Microsoft YaHei UI", 9F);
            _password.UseSystemPasswordChar = _confirm.UseSystemPasswordChar = true;
            _user.Text = "admin";
            _publicUrl.Text = "http://" + Dns.GetHostName() + ":8731";
            string previous = ServerSetupDiscovery.ExistingServiceCommand();
            _repo.Text = ServerSetupDiscovery.ReadArgument(previous, "--repo");
            int previousPort;
            if (int.TryParse(ServerSetupDiscovery.ReadArgument(previous, "--port"), out previousPort) && previousPort > 0 && previousPort <= 65535) _port.Value = previousPort;
            _networkAddress.Items.Add("自定义地址 / 域名");
            foreach (string address in ServerSetupDiscovery.NetworkAddresses()) _networkAddress.Items.Add(address);
            _networkAddress.SelectedIndex = _networkAddress.Items.Count > 1 ? 1 : 0;
            UpdateSelectedAddress();
            try
            {
                string savedAddress = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "UniversalDriverAssistantServer", "ServerAddress.txt");
                if (!string.IsNullOrEmpty(previous) && File.Exists(savedAddress))
                {
                    Uri saved;
                    if (Uri.TryCreate(File.ReadAllText(savedAddress).Trim(), UriKind.Absolute, out saved) && (saved.Scheme == "http" || saved.Scheme == "https"))
                    { _networkAddress.SelectedIndex = 0; _publicUrl.Text = saved.ToString().TrimEnd('/'); }
                }
            }
            catch { }

            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(24), ColumnCount = 2, RowCount = 12 };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 145));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.Controls.Add(new Label { Text = "驱动目录", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
            var repoRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2 };
            repoRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); repoRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 85));
            repoRow.Controls.Add(_repo, 0, 0);
            var browse = new Button { Text = "浏览…", Dock = DockStyle.Fill }; browse.Click += Browse;
            repoRow.Controls.Add(browse, 1, 0); layout.Controls.Add(repoRow, 1, 0);
            Add(layout, "服务端口", _port, 1);
            var addressRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2 };
            addressRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 45)); addressRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 55));
            addressRow.Controls.Add(_networkAddress, 0, 0); addressRow.Controls.Add(_publicUrl, 1, 0);
            Add(layout, "网卡 / 客户端地址", addressRow, 2);
            Add(layout, "管理员账号", _user, 3);
            Add(layout, "管理员密码", _password, 4);
            Add(layout, "确认密码", _confirm, 5);
            layout.Controls.Add(_resetCredentials, 1, 6);
            var note = new Label { Text = "v5.3.9：机型专用整包优先；升级须已迁移 MySQL，校验后清理散装驱动副本。", AutoSize = true, MaximumSize = new Size(500, 0), ForeColor = Color.DimGray };
            layout.Controls.Add(note, 1, 7);
            layout.Controls.Add(_status, 1, 8);
            layout.Controls.Add(_progress, 1, 9);
            layout.Controls.Add(_install, 1, 10);
            layout.Controls.Add(_uninstall, 1, 11);
            for (int i = 0; i < 11; i++) layout.RowStyles.Add(new RowStyle(SizeType.Absolute, i == 7 ? 62 : 44));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
            Controls.Add(layout);
            _install.Click += Install;
            _uninstall.Click += Uninstall;
            _resetCredentials.CheckedChanged += delegate { UpdateCredentialFields(); };
            _port.ValueChanged += delegate { UpdateDefaultUrl(); };
            _networkAddress.SelectedIndexChanged += delegate { UpdateSelectedAddress(); };
            _publicUrl.TextChanged += delegate { Uri current; if (_networkAddress.SelectedIndex > 0 && Uri.TryCreate(_publicUrl.Text, UriKind.Absolute, out current) && current.Host != SelectedIp()) _networkAddress.SelectedIndex = 0; };
            string existingCredential = CredentialPath();
            _resetCredentials.Checked = !File.Exists(existingCredential);
            UpdateCredentialFields();
        }

        private static TextBox Box() { return new TextBox { Dock = DockStyle.Fill, Margin = new Padding(3, 8, 3, 3) }; }
        private static void Add(TableLayoutPanel p, string label, Control value, int row)
        { p.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left }, 0, row); p.Controls.Add(value, 1, row); }

        private void Browse(object sender, EventArgs e)
        {
            using (var d = new FolderBrowserDialog { Description = "选择普通驱动源目录（包含 INF）或已生成的驱动仓库" })
                if (d.ShowDialog(this) == DialogResult.OK) _repo.Text = d.SelectedPath;
        }

        private void UpdateDefaultUrl()
        {
            if (_networkAddress.SelectedIndex > 0) { UpdateSelectedAddress(); return; }
            Uri u;
            if (Uri.TryCreate(_publicUrl.Text, UriKind.Absolute, out u) && string.Equals(u.Host, Dns.GetHostName(), StringComparison.OrdinalIgnoreCase))
                _publicUrl.Text = "http://" + Dns.GetHostName() + ":" + ((int)_port.Value);
        }
        private string SelectedIp() { return Convert.ToString(_networkAddress.SelectedItem).Split(new[] { " · " }, StringSplitOptions.None)[0]; }
        private void UpdateSelectedAddress()
        { if (_networkAddress.SelectedIndex > 0) _publicUrl.Text = "http://" + SelectedIp() + ":" + ((int)_port.Value); }

        private async void Install(object sender, EventArgs e)
        {
            string source = PrepareMedia();
            string serviceSource = Path.Combine(source, "LanDriverService.exe");
            try { ServerStorage.UseProviderDirectory(Path.Combine(source, "DatabaseProvider")); }
            catch (Exception ex) { Fail("数据库运行库加载失败：" + ex.Message); return; }
            string templateSource = Path.Combine(source, "ClientTemplate");
            if (!File.Exists(serviceSource) || !Directory.Exists(templateSource)) { Fail("安装介质不完整：缺少服务程序或 ClientTemplate。"); return; }
                if (!File.Exists(Path.Combine(source, "UDA-Server-Manager.exe"))) { Fail("安装介质不完整：缺少 UDA-Server-Manager.exe。"); return; }
                if (!File.Exists(Path.Combine(source, "UDA-Database-Migration.exe"))) { Fail("安装介质不完整：缺少 UDA-Database-Migration.exe。"); return; }
            if (!Directory.Exists(_repo.Text)) { Fail("请选择存在的驱动目录。"); return; }
            bool indexed = File.Exists(Path.Combine(_repo.Text, "repo.manifest.json")) || ServerStorage.Config(_repo.Text).Enabled;
            bool hasInf = false;
            if (!indexed) try { hasInf = Directory.EnumerateFiles(_repo.Text, "*.inf", SearchOption.AllDirectories).Any(); } catch { }
            if (!indexed && !hasInf) { Fail("所选目录既不是已生成的仓库，也没有找到任何 INF 驱动文件。"); return; }
            Uri publicUri;
            if (!Uri.TryCreate(_publicUrl.Text.Trim(), UriKind.Absolute, out publicUri) || (publicUri.Scheme != "http" && publicUri.Scheme != "https")) { Fail("请输入有效的客户端访问地址。"); return; }
            if (_resetCredentials.Checked && (string.IsNullOrWhiteSpace(_user.Text) || _password.Text.Length < 12)) { Fail("重置凭据时管理员账号不能为空，密码至少 12 个字符。"); return; }
            if (_resetCredentials.Checked && _password.Text != _confirm.Text) { Fail("两次输入的密码不一致。"); return; }

            string detectedService = ServerSetupDiscovery.ExistingServiceCommand();
            if (detectedService.Length > 0 && MessageBox.Show(this,
                "本版本要求原数据已迁移到 MySQL。升级会核验数据库和驱动压缩包，再清理仓库中的散装驱动与过时数据副本；外部导入源目录保留。\n\n更新期间服务会短暂停止，核验时间取决于仓库大小。兼容现有协议客户端；新客户端可避免重复下载机型整包。确定继续？",
                "确认服务端更新", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;

            _install.Enabled = false; _progress.Visible = true; _status.Text = "正在准备安装…"; Refresh();
            string previousCommand = detectedService;
            try
            {
                string existingRepository = previousCommand.Length > 0 ? ServerSetupDiscovery.ReadArgument(previousCommand, "--repo") : _repo.Text;
                if (previousCommand.Length > 0 && !string.Equals(Path.GetFullPath(existingRepository).TrimEnd('\\'), Path.GetFullPath(_repo.Text).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("升级必须使用已迁移的原仓库目录，不能替换为新的驱动源目录");
                _status.Text = "正在验证 MySQL 数据迁移与连接（尚未停止旧服务）…";
                await Task.Run(() => ServerStorage.RequireMigratedDatabase(existingRepository));
            }
            catch (Exception ex) { _install.Enabled = true; _progress.Visible = false; Fail(ex.Message); return; }
            string previousExe = ServerUpgrade.ServiceExecutable(previousCommand);
            var previousVersion = File.Exists(previousExe) ? FileVersionInfo.GetVersionInfo(previousExe) : null;
            bool previousRequiresDatabase = previousVersion != null && (previousVersion.FileMajorPart > 5 || (previousVersion.FileMajorPart == 5 && previousVersion.FileMinorPart >= 3));
            string target = !string.IsNullOrWhiteSpace(previousExe) ? Path.GetDirectoryName(previousExe) : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "UniversalDriverAssistantServer");
            string upgradeBackup = null;
            Exception installFailure = null;
            bool wasRunning = QueryServiceState().IndexOf("RUNNING", StringComparison.OrdinalIgnoreCase) >= 0;
            try
            {
                string machineToken = previousCommand.Length > 0 ? ServerSetupDiscovery.ReadArgument(previousCommand, "--token") : LoadOrCreateMachineToken();
                if (previousCommand.Length > 0)
                {
                    _status.Text = "正在停止旧服务并等待进程退出、文件解除占用…";
                    IProgress<string> stopProgress = new Progress<string>(message => _status.Text = message);
                    await Task.Run(() => ServerUpgrade.StopServiceForUpdate(ServerSetupWait.ServiceName, previousExe, target, TimeSpan.FromSeconds(120), message => stopProgress.Report(message)));
                    _status.Text = "旧服务已完全停止，正在备份现有程序…";
                    string data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "UniversalDriverAssistant");
                    upgradeBackup = ServerUpgrade.Backup(target, data, ServerSetupDiscovery.ReadArgument(previousCommand, "--repo"), previousCommand);
                    string oldCredentials = CredentialPath();
                    if (File.Exists(oldCredentials)) File.Copy(oldCredentials, Path.Combine(upgradeBackup, "ExistingAdminCredentials.json"));
                }
                Directory.CreateDirectory(target);
                File.Copy(serviceSource, Path.Combine(target, "LanDriverService.exe"), true);
                CopyTree(templateSource, Path.Combine(target, "ClientTemplate"));
                CopyTree(Path.Combine(source, "DatabaseProvider"), Path.Combine(target, "DatabaseProvider"));
                CopyTree(Path.Combine(source, "database"), Path.Combine(target, "database"));
                File.Copy(Path.Combine(source, "UDA-Server-Manager.exe"), Path.Combine(target, "UDA-Server-Manager.exe"), true);
                string migrationManager = Path.Combine(source, "UDA-Database-Migration.exe");
                File.Copy(migrationManager, Path.Combine(target, "UDA-Database-Migration.exe"), true);
                foreach (string runtimeConfig in Directory.GetFiles(source, "*.exe.config")) File.Copy(runtimeConfig, Path.Combine(target, Path.GetFileName(runtimeConfig)), true);
                foreach (string name in new[] { "OfflineRepoGenerator.exe", "README.md", "DEPLOY-LAN.md", "DATABASE-MIGRATION.md", "Verify-PhysicalNetworkClient.ps1" })
                { string f = Path.Combine(source, name); if (File.Exists(f)) File.Copy(f, Path.Combine(target, name), true); }
                string selectedDir = Path.GetFullPath(_repo.Text);
                string repoPath = selectedDir;
                if (!indexed)
                {
                    string parent = Directory.GetParent(selectedDir) != null ? Directory.GetParent(selectedDir).FullName : Path.GetPathRoot(selectedDir);
                    string sourceName = new DirectoryInfo(selectedDir).Name;
                    if (string.IsNullOrWhiteSpace(sourceName)) sourceName = "Drivers";
                    repoPath = Path.Combine(parent, sourceName + "-UDA-Repository");
                    string driveRoot = Path.GetPathRoot(repoPath);
                    if (!string.IsNullOrWhiteSpace(driveRoot))
                    {
                        var drive = new DriveInfo(driveRoot);
                        if (drive.IsReady && drive.AvailableFreeSpace < 1024L * 1024L * 1024L)
                            throw new IOException("目标磁盘可用空间不足 1 GB：" + driveRoot);
                    }
                    CleanupStaleRepoWorkdirs(repoPath);
                    _status.Text = "仓库目标：" + repoPath + " · 正在扫描驱动文件…";
                    int exit = await RunStreaming(Path.Combine(target, "OfflineRepoGenerator.exe"), "--repo " + Quote(repoPath) + " --source " + Quote(selectedDir) + " --create");
                    if (exit != 0)
                    {
                        string details;
                        lock (_outputLock) details = _processOutput.ToString().Trim();
                        string errorLog = Path.Combine(parent, sourceName + "-UDA-Repository-Error.log");
                        try { File.WriteAllText(errorLog, details, new UTF8Encoding(false)); } catch { }
                        throw new InvalidOperationException("驱动仓库生成失败，退出代码 " + exit + "。\n目标目录：" + repoPath + "\n错误日志：" + errorLog + "\n\n" + details);
                    }
                }
                _status.Text = "正在为仓库中的每个驱动预生成快速下载包…"; Refresh();
                int packageExit = await RunStreaming(Path.Combine(target, "OfflineRepoGenerator.exe"), "--repo " + Quote(repoPath) + " --prepare-packages");
                if (packageExit != 0)
                {
                    string packageDetails; lock (_outputLock) packageDetails = _processOutput.ToString().Trim();
                    throw new InvalidOperationException("独立驱动包准备失败，服务尚未启动。\n" + packageDetails);
                }
                string credentials = CredentialPath();
                _status.Text = "正在检查归档核验记录、补验变化项并清理散装副本…"; Refresh();
                var compactProgress = new Progress<string>(message => { _status.Text = message; Refresh(); });
                long reclaimedBytes = await Task.Run(() => DatabaseArchiveRepository.Compact(repoPath, compactProgress));
                _status.Text = "清理完成，已释放 " + (reclaimedBytes / 1024.0 / 1024.0).ToString("F1") + " MiB"; Refresh();
                if (_resetCredentials.Checked || !File.Exists(credentials)) AdminCredentialStore.Create(credentials, _user.Text.Trim(), _password.Text);
                string token = machineToken;
                string exe = Path.Combine(target, "LanDriverService.exe");
                string binPath = Quote(exe) + " --service --port " + ((int)_port.Value) + " --bind \"+\" --repo " + Quote(repoPath) + " --import-allow " + Quote(selectedDir)
                    + (!indexed ? " --source " + Quote(selectedDir) : "")
                    + " --token " + Quote(token) + " --admin-credentials " + Quote(credentials) + " --client-template " + Quote(Path.Combine(target, "ClientTemplate")) + " --advertise " + Quote(publicUri.ToString().TrimEnd('/'));
                if (previousCommand.Length > 0) binPath = ServerUpgrade.UpdatedCommand(previousCommand, exe, repoPath, (int)_port.Value, Path.Combine(target, "ClientTemplate"), publicUri.ToString().TrimEnd('/'));
                Run("sc.exe", (previousCommand.Length > 0 ? "config" : "create") + " UdaLanDriverService binPath= " + Quote(binPath) + " start= auto", false);
                Run("sc.exe", "config UdaLanDriverService DisplayName= \"万能驱动助手服务端\" start= auto", false);
                Run("sc.exe", "description UdaLanDriverService \"Universal Driver Assistant LAN Server\"", false);
                Run("sc.exe", "failure UdaLanDriverService reset= 86400 actions= restart/5000/restart/15000/restart/60000", false);
                Run("sc.exe", "failureflag UdaLanDriverService 1", false);
                Run("netsh.exe", "advfirewall firewall delete rule name=\"UdaLanDriverService\"", true);
                Run("netsh.exe", "advfirewall firewall add rule name=\"UdaLanDriverService\" dir=in action=allow protocol=TCP localport=" + ((int)_port.Value), false);
                Run("sc.exe", "start UdaLanDriverService", false);
                // Never fake success: poll the SCM until the service is RUNNING, then poll the local
                // health/admin endpoints. The repo loads in the background, so health may answer
                // "loading" first — that is progress, not failure. Any failure shows the sc query
                // state and the service event log tail instead of a success dialog.
                string eventLog = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "UniversalDriverAssistant", "Logs", "service-events.log");
                _status.Text = "正在等待服务进入 Running…"; Refresh();
                string diag;
                bool running = ServerSetupWait.WaitForServiceRunning(QueryServiceState, TimeSpan.FromSeconds(180), out diag);
                if (!running)
                {
                    throw new InvalidOperationException("服务启动失败或超时（未进入 Running）。\n\n" +
                        ServerSetupWait.BuildDiagnostics(string.IsNullOrEmpty(diag) ? QueryServiceState() : diag, eventLog, 40));
                }
                int port = (int)_port.Value;
                string localBase = "http://localhost:" + port.ToString(CultureInfo.InvariantCulture);
                _status.Text = "服务已 Running，正在等待 HTTP 接口…"; Refresh();
                var health = ServerSetupWait.WaitForHttpReady(
                    () => ServerSetupWait.HttpProbe(localBase + "/api/health", 5000),
                    TimeSpan.FromSeconds(120), m => { _status.Text = m; Refresh(); });
                if (!health.Item1)
                    throw new InvalidOperationException("服务已 Running 但 /api/health 不可达。\n\n" +
                        ServerSetupWait.BuildDiagnostics(QueryServiceState(), eventLog, 40));
                string managerExe = Path.Combine(target, "UDA-Server-Manager.exe");
                if (!File.Exists(managerExe)) throw new IOException("安装包缺少服务端管理软件");
                File.WriteAllText(Path.Combine(target, "manager-url.txt"), localBase, new UTF8Encoding(false));
                // Repo loading is background progress, not a start failure; keep polling a bounded
                // time so the operator sees the repo become ready, then proceed either way.
                if (health.Item2.IndexOf("\"RepoLoaded\":false", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    health.Item2.IndexOf("\"RepoLoaded\": false", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    ServerSetupWait.WaitUntil(delegate
                    {
                        Tuple<int, string> h = ServerSetupWait.HttpProbe(localBase + "/api/health", 5000);
                        if (h.Item1 != 200) return false;
                        bool loaded = h.Item2.IndexOf("\"RepoLoaded\":false", StringComparison.OrdinalIgnoreCase) < 0 &&
                            h.Item2.IndexOf("\"RepoLoaded\": false", StringComparison.OrdinalIgnoreCase) < 0;
                        _status.Text = loaded ? "驱动仓库已就绪。" : "正在加载驱动仓库（首次可能需要较长时间，可在后台完成后使用）…"; Refresh();
                        return loaded;
                    }, TimeSpan.FromSeconds(600), () => false, null);
                }
                string installedSetup = Path.Combine(target, "ServerSetup.exe");
                File.Copy(Application.ExecutablePath, installedSetup, true);
                RegisterUninstaller(installedSetup);
                File.WriteAllText(Path.Combine(target, "ServerAddress.txt"), publicUri.ToString().TrimEnd('/'), new UTF8Encoding(false));
                string menu = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), "万能驱动助手");
                Directory.CreateDirectory(menu);
                Type shellType = Type.GetTypeFromProgID("WScript.Shell");
                if (shellType != null)
                {
                    object shell = Activator.CreateInstance(shellType);
                    object shortcut = shellType.InvokeMember("CreateShortcut", System.Reflection.BindingFlags.InvokeMethod, null, shell, new object[] { Path.Combine(menu, "服务端管理.lnk") });
                    shortcut.GetType().InvokeMember("TargetPath", System.Reflection.BindingFlags.SetProperty, null, shortcut, new object[] { managerExe });
                    shortcut.GetType().InvokeMember("Save", System.Reflection.BindingFlags.InvokeMethod, null, shortcut, null);
                }
                _status.Text = "安装完成：服务已启动，Web 控制台已就绪。";
                MessageBox.Show(this, "服务端安装完成，即将打开 Web 控制台。以后可从开始菜单的服务端管理入口打开。", "安装成功", MessageBoxButtons.OK, MessageBoxIcon.Information);
                try { Process.Start(managerExe); } catch { }
            }
            catch (Exception ex) { installFailure = ex; }
            if (installFailure != null)
            {
                Exception ex = installFailure;
                string recovery = "";
                if (upgradeBackup != null)
                {
                    try
                    {
                        _status.Text = "正在停止服务并等待文件解除占用后回滚…";
                        await Task.Run(() => ServerUpgrade.StopServiceForUpdate(ServerSetupWait.ServiceName, previousExe, target, TimeSpan.FromSeconds(120)));
                        ServerUpgrade.RestoreApplication(upgradeBackup, target);
                        if (!previousRequiresDatabase)
                        {
                            _status.Text = "正在从已验证归档恢复旧版文件布局…"; Refresh();
                            await Task.Run(() => ServerStorage.RestoreFileCompatibility(ServerSetupDiscovery.ReadArgument(previousCommand, "--repo")));
                        }
                        if (_resetCredentials.Checked && File.Exists(Path.Combine(upgradeBackup, "ExistingAdminCredentials.json"))) File.Copy(Path.Combine(upgradeBackup, "ExistingAdminCredentials.json"), CredentialPath(), true);
                        Run("sc.exe", "config UdaLanDriverService binPath= " + Quote(previousCommand), false);
                        if (wasRunning) Run("sc.exe", "start UdaLanDriverService", false);
                        recovery = "\n已恢复旧程序与服务参数。新增日志和仓库数据保留。";
                    }
                    catch (Exception rollback) { recovery = "\n自动恢复未完成：" + rollback.Message; }
                    recovery += "\n升级备份：" + upgradeBackup;
                }
                else if (previousCommand.Length > 0 && wasRunning)
                {
                    try { Run("sc.exe", "start UdaLanDriverService", true); recovery = "\n尚未覆盖旧程序，已尝试恢复原服务运行。"; } catch { }
                }
                string errorLog = Path.Combine(Path.GetTempPath(), "UDA-Server-Setup-install-error.log");
                try { File.WriteAllText(errorLog, DateTime.Now.ToString("o") + Environment.NewLine + ex + Environment.NewLine + recovery, new UTF8Encoding(false)); } catch { errorLog = "（无法写入临时诊断日志）"; }
                Fail("安装失败：" + ex.Message + recovery + "\n详细诊断：" + errorLog);
            }
            _progress.Visible = false; _install.Enabled = true;
        }

        private void UpdateCredentialFields()
        {
            bool enabled = _resetCredentials.Checked;
            _user.Enabled = _password.Enabled = _confirm.Enabled = enabled;
            if (!enabled) { _password.Text = _confirm.Text = ""; _status.Text = "检测到已有管理员凭据，升级时将保持原账号和密码。"; }
        }

        private void Uninstall(object sender, EventArgs e)
        {
            if (MessageBox.Show(this, "将停止并删除 Windows 服务、移除防火墙规则及程序文件。\n驱动源、已生成仓库和日志将保留。是否继续？", "卸载服务端", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
            try
            {
                Run("sc.exe", "stop UdaLanDriverService", true);
                Run("sc.exe", "delete UdaLanDriverService", true);
                Run("netsh.exe", "advfirewall firewall delete rule name=\"UdaLanDriverService\"", true);
                try { Registry.LocalMachine.DeleteSubKeyTree(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\UniversalDriverAssistantServer", false); } catch { }
                string target = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "UniversalDriverAssistantServer");
                string cleanup = Path.Combine(Path.GetTempPath(), "UDA-Remove-" + Guid.NewGuid().ToString("N") + ".cmd");
                File.WriteAllText(cleanup, "@echo off\r\ntimeout /t 2 /nobreak >nul\r\nrmdir /s /q \"" + target + "\"\r\ndel /q \"%~f0\"\r\n", Encoding.Default);
                Process.Start(new ProcessStartInfo("cmd.exe", "/c start \"\" /min \"" + cleanup + "\"") { UseShellExecute = false, CreateNoWindow = true });
                MessageBox.Show(this, "服务端已卸载。驱动仓库和日志已保留。", "卸载完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
                Close();
            }
            catch (Exception ex) { Fail("卸载失败：" + ex.Message); }
        }

        private static string CredentialPath() { string existing = ServerSetupDiscovery.ReadArgument(ServerSetupDiscovery.ExistingServiceCommand(), "--admin-credentials"); return existing.Length > 0 ? existing : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "UniversalDriverAssistant", "admin-credentials.json"); }

        private static void RegisterUninstaller(string installedSetup)
        {
            using (RegistryKey key = Registry.LocalMachine.CreateSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\UniversalDriverAssistantServer"))
            {
                key.SetValue("DisplayName", "万能驱动助手服务端");
                key.SetValue("DisplayVersion", "5.3.9");
                key.SetValue("Publisher", "Universal Driver Assistant");
                key.SetValue("InstallLocation", Path.GetDirectoryName(installedSetup));
                key.SetValue("UninstallString", "\"" + installedSetup + "\"");
                key.SetValue("NoModify", 1, RegistryValueKind.DWord);
                key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            }
        }

        private static void CopyTree(string source, string target)
        {
            Directory.CreateDirectory(target);
            foreach (string file in Directory.GetFiles(source)) File.Copy(file, Path.Combine(target, Path.GetFileName(file)), true);
            foreach (string dir in Directory.GetDirectories(source)) CopyTree(dir, Path.Combine(target, Path.GetFileName(dir)));
        }
        private static void CleanupStaleRepoWorkdirs(string repoPath)
        {
            string full = Path.GetFullPath(repoPath).TrimEnd(Path.DirectorySeparatorChar);
            string parent = Path.GetDirectoryName(full);
            string name = Path.GetFileName(full);
            if (string.IsNullOrWhiteSpace(parent) || string.IsNullOrWhiteSpace(name)) return;
            foreach (string pattern in new[] { name + ".staging-*", name + ".delta-*", name + ".old-*" })
            {
                foreach (string dir in Directory.GetDirectories(parent, pattern, SearchOption.TopDirectoryOnly))
                {
                    string candidate = Path.GetFullPath(dir);
                    if (!string.Equals(Path.GetDirectoryName(candidate), parent, StringComparison.OrdinalIgnoreCase)) continue;
                    try { Directory.Delete(candidate, true); }
                    catch (Exception ex) { throw new IOException("无法清理上次失败留下的临时仓库：" + candidate + "。" + ex.Message, ex); }
                }
            }
        }
        private static string PrepareMedia()
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            var assembly = System.Reflection.Assembly.GetExecutingAssembly();
            string resource = assembly.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith("ServerPayload.zip", StringComparison.OrdinalIgnoreCase));
            if (resource != null)
            {
                string embeddedTarget = Path.Combine(Path.GetTempPath(), "UDA-Server-Media-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(embeddedTarget);
                using (var stream = assembly.GetManifestResourceStream(resource))
                using (var archive = new ZipArchive(stream, ZipArchiveMode.Read)) archive.ExtractToDirectory(embeddedTarget);
                return embeddedTarget;
            }
            string payload = Path.Combine(baseDir, "ServerPayload.zip");
            if (!File.Exists(payload)) return baseDir;
            string target = Path.Combine(Path.GetTempPath(), "UDA-Server-Setup-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(target);
            ZipFile.ExtractToDirectory(payload, target);
            return target;
        }
        private static string Quote(string value) { return "\"" + value.Replace("\"", "\\\"") + "\""; }
        /// <summary>Raw `sc query` output for the LAN service, used for the Running wait and for
        /// failure diagnostics. Never throws.</summary>
        private string QueryServiceState()
        {
            try
            {
                using (var p = Process.Start(new ProcessStartInfo("sc.exe", "query " + ServerSetupWait.ServiceName)
                { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true }))
                {
                    string output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
                    p.WaitForExit();
                    return output;
                }
            }
            catch (Exception ex) { return "sc query failed: " + ex.Message; }
        }
        private static void Run(string file, string args, bool ignoreFailure)
        {
            using (var p = Process.Start(new ProcessStartInfo(file, args) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden, RedirectStandardOutput = true, RedirectStandardError = true }))
            { string output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd(); p.WaitForExit(); if (p.ExitCode != 0 && !ignoreFailure) throw new InvalidOperationException(file + " 失败 (" + p.ExitCode + "): " + output.Trim()); }
        }
        private Task<int> RunStreaming(string file, string args)
        {
            lock (_outputLock) _processOutput.Length = 0;
            var done = new TaskCompletionSource<int>();
            var p = new Process { StartInfo = new ProcessStartInfo(file, args) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden, RedirectStandardOutput = true, RedirectStandardError = true }, EnableRaisingEvents = true };
            DataReceivedEventHandler update = delegate(object sender, DataReceivedEventArgs e)
            {
                if (string.IsNullOrWhiteSpace(e.Data)) return;
                lock (_outputLock)
                {
                    _processOutput.AppendLine(e.Data);
                    if (_processOutput.Length > 32000) _processOutput.Remove(0, _processOutput.Length - 32000);
                }
                try { BeginInvoke((Action)delegate { _status.Text = e.Data.StartsWith("PROGRESS: ", StringComparison.Ordinal) ? e.Data.Substring(10) : e.Data; }); } catch { }
            };
            p.OutputDataReceived += update;
            p.ErrorDataReceived += update;
            p.Exited += delegate { int code = p.ExitCode; p.Dispose(); done.TrySetResult(code); };
            try { p.Start(); p.BeginOutputReadLine(); p.BeginErrorReadLine(); }
            catch (Exception ex) { p.Dispose(); done.TrySetException(ex); }
            return done.Task;
        }
        private static string RandomToken()
        {
            byte[] b = new byte[32]; using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(b);
            return Convert.ToBase64String(b).Replace("+", "-").Replace("/", "_").TrimEnd('=');
        }
        private static string LoadOrCreateMachineToken()
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "UniversalDriverAssistant");
            string path = Path.Combine(dir, "machine-token.txt");
            try { if (File.Exists(path)) { string saved = File.ReadAllText(path).Trim(); if (saved.Length >= 20) return saved; } } catch { }
            string token = "";
            try
            {
                using (var searcher = new ManagementObjectSearcher("SELECT PathName FROM Win32_Service WHERE Name='UdaLanDriverService'"))
                using (var rows = searcher.Get())
                    foreach (ManagementObject row in rows)
                    {
                        string command = Convert.ToString(row["PathName"] ?? "");
                        Match match = Regex.Match(command, "(?:^|\\s)--token\\s+(?:\\\"([^\\\"]+)\\\"|(\\S+))", RegexOptions.IgnoreCase);
                        if (match.Success) token = match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value;
                    }
            }
            catch { }
            if (token.Length < 20) token = RandomToken();
            Directory.CreateDirectory(dir);
            File.WriteAllText(path, token, new UTF8Encoding(false));
            return token;
        }
        private void Fail(string text) { _status.Text = text; MessageBox.Show(this, text, "安装程序", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }
}


