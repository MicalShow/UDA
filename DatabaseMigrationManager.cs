// Standalone, local-only database migration utility for the UDA server.
using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using System.ServiceProcess;
using Microsoft.Win32;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace UDA
{
    internal static class MigrationConnectionOptions
    {
        internal static readonly string[] SslModes = { "VerifyFull", "Required", "Preferred", "None", "VerifyCA" };
        internal static string Build(string host, uint port, string database, string user, string password, string sslMode, string caFile)
        {
            if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(database) || string.IsNullOrWhiteSpace(user)) throw new ArgumentException("数据库地址、库名和用户名不能为空。");
            if (Array.IndexOf(SslModes, sslMode) < 0 || port < 1 || port > 65535) throw new ArgumentException("数据库端口或 TLS 模式无效。");
            var b = new DbConnectionStringBuilder();
            b["Server"] = host.Trim(); b["Port"] = port; b["Database"] = database.Trim(); b["User ID"] = user.Trim(); b["Password"] = password ?? "";
            b["SslMode"] = sslMode; b["Connection Timeout"] = 5;
            if (!string.IsNullOrWhiteSpace(caFile))
            {
                if (sslMode != "VerifyFull" && sslMode != "VerifyCA") throw new ArgumentException("CA 证书仅用于 VerifyFull 或 VerifyCA 模式。");
                string path = Path.GetFullPath(caFile.Trim());
                if (!File.Exists(path)) throw new ArgumentException("CA 证书文件不存在，请重新选择。");
                if (new FileInfo(path).Length > 1024 * 1024) throw new ArgumentException("CA 证书文件不能超过 1 MiB。");
                string pem = File.ReadAllText(path);
                if (!pem.Contains("-----BEGIN CERTIFICATE-----") || pem.Contains("PRIVATE KEY")) throw new ArgumentException("请选择 PEM 格式的公开 CA 证书，不能选择私钥文件。");
                b["SslCa"] = path;
            }
            return b.ConnectionString;
        }
    }

    public static class DatabaseMigrationProgram
    {
        [STAThread]
        public static void Main()
        {
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            string baseUrl = ReadBaseUrl();
            using (var login = new MigrationLoginForm(baseUrl))
            {
                if (login.ShowDialog() != DialogResult.OK) return;
                Application.Run(new DatabaseMigrationForm(baseUrl, login.Cookies, login.User));
            }
        }
        private static string ReadBaseUrl()
        {
            string file = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "manager-url.txt");
            if (File.Exists(file))
            {
                string value = File.ReadAllText(file).Trim().TrimEnd('/'); Uri uri;
                if (Uri.TryCreate(value, UriKind.Absolute, out uri) && (uri.Scheme == "http" || uri.Scheme == "https"))
                    return new UriBuilder(uri.Scheme, "localhost", uri.Port).Uri.ToString().TrimEnd('/');
            }
            return "http://localhost:8080";
        }
    }

    internal sealed class MigrationAuthenticationException : InvalidOperationException
    { public MigrationAuthenticationException() : base("管理员会话已失效，请重新登录验证。") { } }

    internal static class MigrationHttp
    {
        public static string Send(string baseUrl, string path, string method, string body, CookieContainer cookies, bool protocol)
        {
            var request = (HttpWebRequest)WebRequest.Create(baseUrl.TrimEnd('/') + path); request.Proxy = null; request.AllowAutoRedirect = false;
            request.Method = method; request.CookieContainer = cookies; request.Timeout = 30000; request.ReadWriteTimeout = 30000;
            if (path == "/api/admin/database" && method == "POST") { request.Timeout = 1800000; request.ReadWriteTimeout = 1800000; }
            if (protocol) request.Headers["X-UDA-Protocol"] = "1";
            if (body != null) { byte[] bytes = Encoding.UTF8.GetBytes(body); request.ContentType = "application/json; charset=utf-8"; request.ContentLength = bytes.Length; using (var stream = request.GetRequestStream()) stream.Write(bytes, 0, bytes.Length); }
            try { using (var response = (HttpWebResponse)request.GetResponse()) using (var reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8)) return reader.ReadToEnd(); }
            catch (WebException ex)
            {
                var response = ex.Response as HttpWebResponse;
                if (response == null) throw new IOException("无法连接本机 UDA 服务，请确认服务已启动（" + ex.Status + "）。", ex);
                if (response.StatusCode == HttpStatusCode.Unauthorized) { response.Close(); throw new MigrationAuthenticationException(); }
                using (response) using (var reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8))
                { string text = reader.ReadToEnd(); throw new InvalidOperationException("服务端返回 " + (int)response.StatusCode + "：" + ErrorText(text)); }
            }
        }
        private static string ErrorText(string json)
        { try { var d = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(json); if (d.ContainsKey("error")) return Convert.ToString(d["error"]); } catch { } return json; }
    }

    internal sealed class MigrationLoginForm : Form
    {
        private readonly string _url; private readonly TextBox _user = new TextBox(), _password = new TextBox(); private readonly Label _status = new Label();
        public CookieContainer Cookies { get; private set; } public string User { get; private set; }
        public MigrationLoginForm(string url)
        {
            _url = url; Text = "万能驱动助手 · 数据库迁移登录"; StartPosition = FormStartPosition.CenterScreen; FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false; MinimizeBox = false; ClientSize = new Size(430, 255); Font = new Font("Microsoft YaHei UI", 9F);
            var title = new Label { Text = "管理员身份验证", Font = new Font("Microsoft YaHei UI", 16F, FontStyle.Bold), ForeColor = Color.FromArgb(20, 105, 132), AutoSize = true, Left = 28, Top = 24 };
            Controls.Add(title); AddLabel("管理员账号", 32, 83); _user.SetBounds(130, 78, 265, 28); Controls.Add(_user);
            AddLabel("管理员密码", 32, 124); _password.SetBounds(130, 119, 265, 28); _password.UseSystemPasswordChar = true; Controls.Add(_password);
            _status.SetBounds(30, 158, 370, 28); _status.ForeColor = Color.Firebrick; Controls.Add(_status);
            var login = new Button { Text = "验证并继续", Left = 274, Top = 197, Width = 120, Height = 34, BackColor = Color.FromArgb(8, 127, 171), ForeColor = Color.White, FlatStyle = FlatStyle.Flat };
            login.Click += delegate { Authenticate(); }; Controls.Add(login); AcceptButton = login;
            Shown += delegate { _user.Focus(); };
        }
        private void AddLabel(string text, int x, int y) { Controls.Add(new Label { Text = text, Left = x, Top = y + 5, AutoSize = true }); }
        private void Authenticate()
        {
            if (string.IsNullOrWhiteSpace(_user.Text) || _password.Text.Length == 0) { _status.Text = "请输入管理员账号和密码。"; return; }
            try
            {
                var cookies = new CookieContainer(); string payload = new JavaScriptSerializer().Serialize(new { user = _user.Text.Trim(), password = _password.Text });
                MigrationHttp.Send(_url, "/api/admin/login", "POST", payload, cookies, false);
                Cookies = cookies; User = _user.Text.Trim(); _password.Clear(); DialogResult = DialogResult.OK; Close();
            }
            catch (Exception ex) { _password.Clear(); _status.Text = ex.Message; }
        }
    }

    internal sealed class DatabaseMigrationForm : Form
    {
        private readonly string _url; private CookieContainer _cookies; private readonly string _admin;
        private readonly TextBox _host = new TextBox(), _database = new TextBox(), _username = new TextBox(), _password = new TextBox();
        private readonly TextBox _caFile = new TextBox(); private readonly Button _browseCa = new Button();
        private readonly NumericUpDown _port = new NumericUpDown(); private readonly ComboBox _ssl = new ComboBox(); private readonly TextBox _output = new TextBox();
        private readonly Label _status = new Label(); private readonly JavaScriptSerializer _json = new JavaScriptSerializer();
        public DatabaseMigrationForm(string url, CookieContainer cookies, string admin, bool loadInitialStatus = true)
        {
            _url = url; _cookies = cookies; _admin = admin; Text = "万能驱动助手 · 数据库迁移"; StartPosition = FormStartPosition.CenterScreen;
            MinimumSize = new Size(780, 800); Size = new Size(800, 830); Font = new Font("Microsoft YaHei UI", 9F); BackColor = Color.FromArgb(246, 250, 251);
            var title = new Label { Text = "MySQL / MariaDB 数据库迁移", Font = new Font("Microsoft YaHei UI", 16F, FontStyle.Bold), ForeColor = Color.FromArgb(20, 105, 132), AutoSize = true, Left = 26, Top = 20 }; Controls.Add(title);
            Controls.Add(new Label { Text = "已验证管理员：" + admin + "    服务地址：" + url, Left = 28, Top = 55, Width = 690, ForeColor = Color.DimGray });
            AddField("数据库地址", _host, 28, 95, "localhost 或 db.example.com"); AddField("端口", _port, 385, 95, "3306"); _port.Minimum = 1; _port.Maximum = 65535; _port.Value = 3306;
            AddField("数据库名", _database, 28, 145, "uda"); AddField("用户名", _username, 385, 145, "uda_service");
            AddField("数据库密码", _password, 28, 195, ""); _password.UseSystemPasswordChar = true;
            var sslLabel = new Label { Text = "TLS / SSL", Left = 385, Top = 202, AutoSize = true }; Controls.Add(sslLabel);
            _ssl.SetBounds(460, 196, 250, 29); _ssl.DropDownStyle = ComboBoxStyle.DropDownList;
            _ssl.Items.AddRange(new object[] { "VerifyFull（验证 CA 与地址，推荐）", "Required（加密，不验证证书身份）", "Preferred（优先加密）", "None（仅隔离测试）", "VerifyCA（验证 CA，不验证地址）" }); _ssl.SelectedIndex = 0; Controls.Add(_ssl);
            Controls.Add(new Label { Text = "CA 证书", Left = 28, Top = 250, AutoSize = true });
            _caFile.SetBounds(128, 244, 485, 28); Controls.Add(_caFile);
            _browseCa.Text = "选择文件…"; _browseCa.SetBounds(623, 243, 112, 30); Controls.Add(_browseCa);
            _browseCa.Click += delegate { using (var dialog = new OpenFileDialog { Title = "选择 MySQL CA 证书（PEM 格式）", Filter = "CA 证书 (*.pem;*.crt;*.cer)|*.pem;*.crt;*.cer|所有文件 (*.*)|*.*", CheckFileExists = true }) { if (dialog.ShowDialog(this) == DialogResult.OK) _caFile.Text = dialog.FileName; } };
            _ssl.SelectedIndexChanged += delegate { _caFile.Enabled = _browseCa.Enabled = _ssl.SelectedIndex == 0 || _ssl.SelectedIndex == 4; };
            Controls.Add(new Label { Text = "CA 证书可留空以使用系统信任；自签名/公司 CA 请选 PEM 文件并长期保留，确保 UDA 服务账户可读取。VerifyFull 还要求数据库地址匹配证书。", Left = 29, Top = 283, Width = 705, Height = 48, ForeColor = Color.DimGray });
            AddButton("测试连接", 28, "test"); AddButton("迁移并启用", 145, "migrate"); AddButton("更新连接", 262, "connect"); AddButton("同步待写入数据", 379, "sync"); AddButton("迁回本地", 540, "local");
            var refresh = new Button { Text = "刷新状态", Left = 650, Top = 340, Width = 90, Height = 34 }; refresh.Click += delegate { Run("status"); }; Controls.Add(refresh);
            var check = new Button { Text = "迁移前检查", Left = 28, Top = 382, Width = 150, Height = 32 };
            var prepare = new Button { Text = "完整补迁并复核", Left = 190, Top = 382, Width = 170, Height = 32 };
            check.Click += async delegate { await PrepareLocal(false); }; prepare.Click += async delegate { await PrepareLocal(true); };
            Controls.Add(check); Controls.Add(prepare);
            _status.SetBounds(30, 422, 690, 28); _status.ForeColor = Color.FromArgb(28, 103, 120); Controls.Add(_status);
            _output.SetBounds(28, 456, 712, 280); _output.Multiline = true; _output.ReadOnly = true; _output.ScrollBars = ScrollBars.Vertical; _output.Font = new Font("Consolas", 9F); Controls.Add(_output);
            Shown += delegate { if (loadInitialStatus) Run("status"); };
        }
        private void AddField(string label, Control input, int left, int top, string placeholder)
        {
            Controls.Add(new Label { Text = label, Left = left, Top = top + 5, AutoSize = true }); input.SetBounds(left + 100, top, left == 28 ? 240 : 250, 28);
            if (input is TextBox && !string.IsNullOrWhiteSpace(placeholder)) ((TextBox)input).AccessibleDescription = placeholder;
            Controls.Add(input);
        }
        private void AddButton(string text, int left, string action)
        {
            var button = new Button { Text = text, Left = left, Top = 340, Width = 108, Height = 34, BackColor = action == "migrate" ? Color.FromArgb(8, 127, 171) : Color.White, ForeColor = action == "migrate" ? Color.White : Color.FromArgb(37, 90, 109), FlatStyle = FlatStyle.Flat };
            button.Tag = action; button.Click += delegate { Run(action); }; Controls.Add(button);
        }
        private string Connection()
        {
            return MigrationConnectionOptions.Build(_host.Text, (uint)_port.Value, _database.Text, _username.Text, _password.Text,
                MigrationConnectionOptions.SslModes[_ssl.SelectedIndex], _caFile.Enabled ? _caFile.Text : "");
        }
        private void Run(string action)
        {
            try
            {
                if (!EnsureAdminSession()) return;
                string body = null;
                if (action == "test" || action == "migrate" || action == "connect")
                {
                    string connection = Connection(); if (action == "migrate" && MessageBox.Show("将仓库元数据、版本策略、分类和设备安装日志迁移到目标数据库。本地副本保留。继续？", "确认迁移", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
                    body = _json.Serialize(new { action = action, connection = connection });
                }
                else if (action == "local" && MessageBox.Show("将数据库中的最新数据同步回本地并切换存储。继续？", "确认迁回", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
                _status.Text = "正在执行…"; Refresh(); string response = MigrationHttp.Send(_url, action == "status" ? "/api/admin/database" : "/api/admin/database", action == "status" ? "GET" : "POST", body ?? (action == "status" ? null : _json.Serialize(new { action = action })), _cookies, action != "status");
                _output.Text = FormatResponse(response); _status.Text = "操作完成。";
            }
            catch (Exception ex) { _status.Text = "操作未完成。"; _output.Text = ex.Message; }
        }
        private bool EnsureAdminSession()
        {
            try { MigrationHttp.Send(_url, "/api/admin/database", "GET", null, _cookies, false); return true; }
            catch (MigrationAuthenticationException)
            {
                _status.Text = "服务重启或会话过期，请重新验证管理员身份。";
                using (var login = new MigrationLoginForm(_url))
                {
                    if (login.ShowDialog(this) != DialogResult.OK) return false;
                    _cookies = login.Cookies;
                }
                // Verify the replacement session before allowing local database access or stopping service.
                MigrationHttp.Send(_url, "/api/admin/database", "GET", null, _cookies, false);
                return true;
            }
        }
        private async Task PrepareLocal(bool repair)
        {
            try
            {
                if (!EnsureAdminSession()) { _status.Text = "已取消，未执行补迁。"; return; }
                string command;
                using (var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\UdaLanDriverService"))
                    command = key == null ? "" : Convert.ToString(key.GetValue("ImagePath", ""));
                var match = Regex.Match(command, "(?:^|\\s)--repo\\s+(?:\"([^\"]*)\"|(\\S+))", RegexOptions.IgnoreCase);
                if (!match.Success) throw new IOException("未识别已注册 UDA 服务的 --repo 参数；请在正式服务端本机运行工具。");
                string root = Path.GetFullPath(match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value);
                string connection = ServerStorage.Config(root).Enabled ? "" : Connection();
                if (MessageBox.Show("仓库：" + root + "\r\n此操作将暂停 UDA 服务，完成后自动恢复。" + (repair ? "将补迁全部业务数据，保留本地原件；不升级服务程序。" : "检查连接和数据大小，不启用迁移。"), "迁移准备", MessageBoxButtons.OKCancel) != DialogResult.OK) return;
                foreach (Control control in Controls) if (control is Button) control.Enabled = false;
                _status.Text = "正在暂停服务并检查，请等待…";
                _output.Text = await Task.Run(delegate {
                    using (var service = new ServiceController("UdaLanDriverService"))
                    {
                        service.Refresh(); bool restart = service.Status != ServiceControllerStatus.Stopped;
                        try
                        {
                            if (restart) { service.Stop(); service.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromMinutes(2)); }
                            ServerStorage.UseProviderDirectory(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "DatabaseProvider"));
                            return ServerStorage.PrepareCompleteMigration(root, connection, repair);
                        }
                        finally { if (restart) { service.Refresh(); if (service.Status == ServiceControllerStatus.Stopped) service.Start(); service.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromMinutes(2)); } }
                    }
                });
                _status.Text = "处理完成，服务已恢复。";
            }
            catch (Exception ex) { _status.Text = "未完成，请处理以下问题后重试。"; _output.Text = ex.Message; }
            finally { foreach (Control control in Controls) if (control is Button) control.Enabled = true; }
        }
        private string FormatResponse(string response)
        {
            try
            {
                var data = _json.Deserialize<Dictionary<string, object>>(response); object storageValue;
                if (!data.TryGetValue("storage", out storageValue)) return response;
                var storage = storageValue as Dictionary<string, object>; if (storage == null) return response;
                bool databaseOnly = storage.ContainsKey("databaseOnly") && Convert.ToBoolean(storage["databaseOnly"]);
                foreach (Button button in Controls.OfType<Button>()) if (Convert.ToString(button.Tag) == "local" || Convert.ToString(button.Tag) == "migrate") button.Enabled = !databaseOnly;
                var text = new StringBuilder(); text.AppendLine("当前存储：" + (Convert.ToString(storage["backend"]) == "mysql" ? "MySQL / MariaDB" : "本地文件"));
                if (databaseOnly) text.AppendLine("当前版本要求数据库主存储；保留连接配置和临时重试缓冲，不允许切回文件模式。");
                text.AppendLine("待同步记录：" + storage["pending"]);
                if (storage.ContainsKey("pendingLogFiles")) text.AppendLine("待检查/同步日志文件：" + storage["pendingLogFiles"]);
                if (storage.ContainsKey("repositoryId") && Convert.ToString(storage["repositoryId"]).Length > 0) text.AppendLine("数据集 ID：" + storage["repositoryId"]);
                if (data.ContainsKey("documents")) text.AppendLine("本次处理文档：" + data["documents"]); if (storage.ContainsKey("error")) text.AppendLine("状态：" + (Convert.ToString(storage["error"]).Length == 0 ? "正常" : Convert.ToString(storage["error"])));
                return text.ToString();
            }
            catch { return response; }
        }
    }
}
