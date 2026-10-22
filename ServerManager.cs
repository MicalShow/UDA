using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.ServiceProcess;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace UDA
{
    public static class ServerManagerProgram
    {
        [STAThread] public static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            string settings = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "manager-url.txt");
            string address = File.Exists(settings) ? File.ReadAllText(settings).Trim() : "http://localhost:8731";
            Uri target;
            if (!Uri.TryCreate(address, UriKind.Absolute, out target) || (target.Scheme != "http" && target.Scheme != "https")) address = "http://localhost:8731";
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(address.TrimEnd('/') + "/admin") { UseShellExecute = true });
        }
    }

    public sealed class ServerManagerForm : Form
    {
        readonly CookieContainer cookies = new CookieContainer();
        readonly JavaScriptSerializer json = new JavaScriptSerializer { MaxJsonLength = 16777216 };
        readonly TextBox address = new TextBox { Text = "http://localhost:8731", Width = 220 };
        readonly TextBox user = new TextBox { Text = "admin", Width = 110 };
        readonly TextBox password = new TextBox { UseSystemPasswordChar = true, Width = 130 };
        readonly TextBox search = new TextBox { Width = 230 };
        readonly TextBox source = new TextBox { Width = 450 };
        readonly TextBox publicUrl = new TextBox { Width = 330 };
        readonly Label status = new Label { AutoSize = true, MaximumSize = new Size(1000, 0) };
        readonly Label pages = new Label { AutoSize = true };
        readonly DataGridView grid = new DataGridView { Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, MultiSelect = true, SelectionMode = DataGridViewSelectionMode.FullRowSelect, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill };
        readonly Panel console = new Panel { Dock = DockStyle.Fill, Visible = false };
        int page = 1, totalPages = 1;
        bool busy;
        string BaseUrl { get { return address.Text.Trim().TrimEnd('/'); } }
        public ServerManagerForm()
        {
            Text = "万能驱动助手 · 服务端管理";
            Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            Size = new Size(1180, 780); MinimumSize = new Size(850, 600);
            AutoScaleMode = AutoScaleMode.Dpi; Font = new Font("Microsoft YaHei UI", 9);
            publicUrl.Text = "http://" + Dns.GetHostName() + ":8731";
            string settings = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "manager-url.txt");
            if (File.Exists(settings)) address.Text = File.ReadAllText(settings).Trim();
            string published = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ServerAddress.txt");
            if (File.Exists(published)) publicUrl.Text = File.ReadAllText(published).Trim();
            Uri savedUri; IPAddress savedIp;
            var localIps = Dns.GetHostAddresses(Dns.GetHostName()).Where(ip => ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && !IPAddress.IsLoopback(ip)).ToArray();
            if (localIps.Length > 0 && Uri.TryCreate(publicUrl.Text, UriKind.Absolute, out savedUri) &&
                IPAddress.TryParse(savedUri.Host, out savedIp) && !localIps.Contains(savedIp))
                status.Text = "发布地址不属于本机网卡；若已迁移服务器，请确认客户端访问地址。本机 IPv4：" + string.Join("、", localIps.Select(ip => ip.ToString()).ToArray());
            var root = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 4, ColumnCount = 1, Padding = new Padding(24) };
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize)); root.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); root.RowStyles.Add(new RowStyle(SizeType.AutoSize)); Controls.Add(root);
            var login = Flow(); login.Controls.Add(new Label { Text = "本机服务地址", AutoSize = true }); login.Controls.Add(address); login.Controls.Add(user); login.Controls.Add(password);
            Button(login, "登录", async () => { await Request("/api/admin/login", new { user = user.Text, password = password.Text }); password.Clear(); await RefreshDrivers(1); console.Visible = true; });
            Button(login, "退出", async () => { await Request("/api/admin/logout", new { }); console.Visible = false; grid.Rows.Clear(); }); root.Controls.Add(login, 0, 1); root.Controls.Add(console, 0, 2); root.Controls.Add(status, 0, 3);
            root.Controls.Add(new Label { Text = "UDA  /  DRIVER OPERATIONS\n驱动基础设施 · 服务端控制中心", AutoSize = true, Font = new Font("Microsoft YaHei UI", 18, FontStyle.Bold), ForeColor = Color.FromArgb(64, 216, 242), Margin = new Padding(0, 0, 0, 22) }, 0, 0);
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 4, ColumnCount = 1 };
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize)); layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); layout.RowStyles.Add(new RowStyle(SizeType.AutoSize)); layout.RowStyles.Add(new RowStyle(SizeType.AutoSize)); console.Controls.Add(layout);
            var toolbar = Flow(); toolbar.Controls.Add(search); Button(toolbar, "搜索", () => RefreshDrivers(1));
            Button(toolbar, "删除选中", async () => { var ids = grid.SelectedRows.Cast<DataGridViewRow>().Select(r => (string)r.Tag).ToArray(); if (ids.Length == 0) return; if (MessageBox.Show("删除选中的 " + ids.Length + " 个驱动？", Text, MessageBoxButtons.YesNo) != DialogResult.Yes) return; await Request("/api/admin/drivers/delete", new { ids = ids }); await RefreshDrivers(page); });
            foreach (string action in new[] { "启动", "停止", "重启" }) { string a = action; Button(login, a + "服务", () => Task.Run(() => { using (var sc = new ServiceController("UdaLanDriverService")) { if (a != "启动" && sc.Status != ServiceControllerStatus.Stopped) { sc.Stop(); sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(60)); } sc.Refresh(); if (a != "停止" && sc.Status != ServiceControllerStatus.Running) { sc.Start(); sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(60)); } } })); }
            layout.Controls.Add(toolbar, 0, 0);
            foreach (string col in new[] { "分类", "驱动文件", "厂商", "版本", "日期", "签名", "硬件ID数" }) grid.Columns.Add(col, col); layout.Controls.Add(grid, 0, 1);
            var pager = Flow(); Button(pager, "上一页", () => RefreshDrivers(Math.Max(1, page - 1))); pager.Controls.Add(pages); Button(pager, "下一页", () => RefreshDrivers(Math.Min(totalPages, page + 1))); layout.Controls.Add(pager, 0, 2);
            var maintenance = Flow(); maintenance.Controls.Add(source); Button(maintenance, "选择驱动目录", () => { using (var d = new FolderBrowserDialog()) if (d.ShowDialog() == DialogResult.OK) source.Text = d.SelectedPath; return Task.FromResult(0); });
            Button(maintenance, "追加驱动", async () => { if (!Directory.Exists(source.Text)) throw new Exception("请选择服务器本机存在的驱动目录"); await Request("/api/admin/import?src=" + Uri.EscapeDataString(source.Text), new { }); await PollJob(); await RefreshDrivers(1); });
            Button(maintenance, "检查重复", async () => { var r = await Request("/api/admin/duplicates"); MessageBox.Show(json.Serialize(r), "重复驱动检查结果"); });
            Button(maintenance, "清理重复", async () => { if (MessageBox.Show("清理重复驱动并更新索引？", Text, MessageBoxButtons.YesNo) != DialogResult.Yes) return; await Request("/api/admin/dedupe", new { }); await PollJob(); await RefreshDrivers(1); });
            maintenance.Controls.Add(new Label { Text = "客户端访问地址", AutoSize = true }); maintenance.Controls.Add(publicUrl);
            Button(maintenance, "生成客户端EXE", async () => { using (var d = new SaveFileDialog { Filter = "客户端程序|*.exe", FileName = "UDA-Client.exe" }) { if (d.ShowDialog() != DialogResult.OK) return; byte[] bytes = await Fetch("/api/admin/client-package?serverUrl=" + Uri.EscapeDataString(publicUrl.Text), new { }); File.WriteAllBytes(d.FileName, bytes); status.Text = "客户端已保存：" + d.FileName; } }); layout.Controls.Add(maintenance, 0, 3);
            ApplyTheme(this);
            root.GetControlFromPosition(0, 0).ForeColor = Color.FromArgb(64, 216, 242);
            grid.EnableHeadersVisualStyles = false;
            grid.BackgroundColor = Color.FromArgb(13, 24, 40);
            grid.BorderStyle = BorderStyle.None; grid.RowHeadersVisible = false;
            grid.GridColor = Color.FromArgb(35, 58, 78);
            grid.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(23, 45, 65);
            grid.ColumnHeadersDefaultCellStyle.ForeColor = Color.FromArgb(92, 225, 243);
            grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize;
            grid.DefaultCellStyle.BackColor = Color.FromArgb(15, 29, 46);
            grid.DefaultCellStyle.ForeColor = Color.FromArgb(220, 232, 243);
            grid.DefaultCellStyle.SelectionBackColor = Color.FromArgb(25, 91, 121);
            grid.DefaultCellStyle.SelectionForeColor = Color.White;
            grid.AlternatingRowsDefaultCellStyle.BackColor = Color.FromArgb(20, 36, 54);
            grid.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.AllCells;
            status.ForeColor = Color.FromArgb(92, 225, 243);
        }
        static void ApplyTheme(Control control)
        {
            control.BackColor = Color.FromArgb(11, 20, 34);
            control.ForeColor = Color.FromArgb(220, 232, 243);
            var button = control as Button;
            if (button != null) { button.FlatStyle = FlatStyle.Flat; button.FlatAppearance.BorderColor = Color.FromArgb(42, 135, 166); button.BackColor = Color.FromArgb(20, 51, 72); button.Padding = new Padding(12, 5, 12, 5); button.Margin = new Padding(4, 5, 4, 5); }
            if (control is TextBox) { control.BackColor = Color.FromArgb(23, 39, 56); ((TextBox)control).BorderStyle = BorderStyle.FixedSingle; }
            foreach (Control child in control.Controls) ApplyTheme(child);
        }
        FlowLayoutPanel Flow() { return new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true, Padding = new Padding(0, 6, 0, 6) }; }
        void Button(Control parent, string text, Func<Task> action)
        {
            var b = new Button { Text = text, AutoSize = true, MinimumSize = new Size(85, 34) }; parent.Controls.Add(b);
            b.Click += async delegate { if (busy) return; busy = true; b.Enabled = false; try { status.Text = "正在处理…"; await action(); if (status.Text == "正在处理…") status.Text = "完成"; } catch (Exception ex) { status.Text = ex.Message; MessageBox.Show(this, ex.Message, "操作失败"); } finally { busy = false; b.Enabled = true; } };
        }
        async Task<byte[]> Fetch(string path, object body = null)
        {
            string endpoint = BaseUrl;
            return await Task.Run(() => {
                var request = (HttpWebRequest)WebRequest.Create(endpoint + path); request.CookieContainer = cookies; request.Timeout = 120000; request.ReadWriteTimeout = 120000; request.Headers["X-UDA-Protocol"] = "1"; request.Proxy = null;
                if (body != null) { request.Method = "POST"; request.ContentType = "application/json"; byte[] data = Encoding.UTF8.GetBytes(json.Serialize(body)); using (var s = request.GetRequestStream()) s.Write(data, 0, data.Length); }
                try { using (var response = request.GetResponse()) using (var s = response.GetResponseStream()) using (var output = new MemoryStream()) { s.CopyTo(output); return output.ToArray(); } }
                catch (WebException ex) { if (ex.Response != null) using (var s = ex.Response.GetResponseStream()) using (var reader = new StreamReader(s)) throw new Exception(reader.ReadToEnd()); throw; }
            });
        }
        async Task<Dictionary<string, object>> Request(string path, object body = null) { return json.Deserialize<Dictionary<string, object>>(Encoding.UTF8.GetString(await Fetch(path, body))); }
        async Task RefreshDrivers(int p)
        {
            var result = await Request("/api/admin/drivers?page=" + p + "&pageSize=100&q=" + Uri.EscapeDataString(search.Text));
            page = p; totalPages = Math.Max(1, (Convert.ToInt32(result["total"]) + 99) / 100); if (page > totalPages) { await RefreshDrivers(totalPages); return; } grid.Rows.Clear();
            foreach (var item in (System.Collections.IEnumerable)result["items"]) { var row = (Dictionary<string, object>)item; int n = grid.Rows.Add(row["category"], row["inf"], row["provider"], row["version"], row["date"], row["signature"], row["hardwareIds"]); grid.Rows[n].Tag = row["id"]; }
            pages.Text = "第 " + page + " / " + totalPages + " 页，共 " + result["total"] + " 个驱动";
        }
        async Task PollJob()
        {
            while (!IsDisposed) { var r = await Request("/api/admin/import-status"); status.Text = Convert.ToString(r["message"]); if (!Convert.ToBoolean(r["running"])) { if (Convert.ToString(r["stage"]) == "failed") throw new Exception(Convert.ToString(r["error"])); return; } await Task.Delay(800); }
        }
    }
}
