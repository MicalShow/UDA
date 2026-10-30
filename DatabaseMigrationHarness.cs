using System;
using System.Data.Common;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Security.Authentication;
using System.Windows.Forms;

namespace UDA
{
    internal static class DatabaseMigrationHarness
    {
        private static int _asserts;
        private static void Check(bool condition, string message)
        {
            _asserts++;
            if (!condition) throw new Exception(message);
        }
        private sealed class DatabaseError : DbException
        {
            public int Number { get; private set; }
            public string SqlState { get; private set; }
            public DatabaseError(int number, string state, string message, Exception inner = null) : base(message, inner) { Number = number; SqlState = state; }
        }
        [STAThread]
        private static int Main(string[] args)
        {
            if (args.Length == 1 && args[0] == "--upgrade-test-child") { System.Threading.Thread.Sleep(1000); return 0; }
            try
            {
                if (args.Length == 1 && args[0] == "--setup-output-child") {
                    for (int i = 0; i < 1000; i++) Console.WriteLine("PROGRESS: " + i);
                    Console.Error.WriteLine("FINAL-ERROR-MARKER"); return 7;
                }
                string root = Path.GetFullPath(args[0]); Directory.CreateDirectory(root);
                var setupOutput = new System.Collections.Generic.List<string>(); var setupOutputGate = new object();
                int setupExit = ServerUpgrade.RunSetupProcess(typeof(DatabaseMigrationHarness).Assembly.Location, "--setup-output-child", line => { lock (setupOutputGate) setupOutput.Add(line); });
                Check(setupExit == 7 && setupOutput.Count == 1001 && setupOutput.Contains("FINAL-ERROR-MARKER"), "setup drains both output streams before process disposal, preserving final failure");
                Check(ServerUpgrade.SetupFailureSummary(string.Join("\n", Enumerable.Repeat("PROGRESS: pending", 1000)) + "\nOfflineRepoGenerator ERROR: missing archive\n stack").Contains("missing archive"), "setup error summary prioritizes actual error above progress output");
                string configuredInstaller = ServerUpgrade.PrepareInstallerRuntime(typeof(DatabaseMigrationHarness).Assembly.Location, Path.Combine(root, "installer-runtime-" + Guid.NewGuid().ToString("N")));
                Check(File.Exists(configuredInstaller) && OfflineRepoGenerator.Sha256File(configuredInstaller) == OfflineRepoGenerator.Sha256File(typeof(DatabaseMigrationHarness).Assembly.Location), "single-file installer bootstrap preserves executable payload bytes");
                var runtime = System.Xml.Linq.XDocument.Load(configuredInstaller + ".config");
                Check((string)runtime.Descendants("Thread_UseAllCpuGroups").Single().Attribute("enabled") == "true" && (string)runtime.Descendants("GCCpuGroup").Single().Attribute("enabled") == "true" && ((string)runtime.Descendants("AppContextSwitchOverrides").Single().Attribute("value")).Contains("BlockLongPaths=false"), "standalone installer receives CPU-group and long-path CLR settings without a manually downloaded config");
                // Exercise the exact cookie/session lifecycle across a service restart over HTTP.
                var portProbe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0); portProbe.Start();
                int sessionPort = ((IPEndPoint)portProbe.LocalEndpoint).Port; portProbe.Stop();
                string sessionUrl = "http://localhost:" + sessionPort;
                using (var listener = new HttpListener())
                {
                    listener.Prefixes.Add(sessionUrl + "/"); listener.Start();
                    bool firstCookie = false, replacementCookie = false;
                    var responder = System.Threading.Tasks.Task.Run(delegate {
                        for (int i = 0; i < 6; i++)
                        {
                            var context = listener.GetContext();
                            if (i == 0 || i == 3) context.Response.Headers.Add("Set-Cookie", "UDAADMIN=" + (i == 0 ? "before-restart" : "after-restart") + "; Path=/; HttpOnly; SameSite=Strict");
                            if (i == 1) firstCookie = context.Request.Cookies["UDAADMIN"] != null && context.Request.Cookies["UDAADMIN"].Value == "before-restart";
                            if (i == 4) replacementCookie = context.Request.Cookies["UDAADMIN"] != null && context.Request.Cookies["UDAADMIN"].Value == "after-restart";
                            context.Response.StatusCode = i == 2 ? 401 : i == 5 ? 403 : 200;
                            byte[] response = System.Text.Encoding.UTF8.GetBytes(i == 2 ? "{\"error\":\"admin login required\"}" : "{}");
                            context.Response.ContentLength64 = response.Length; context.Response.OutputStream.Write(response, 0, response.Length); context.Response.Close();
                        }
                    });
                    var sessionCookies = new CookieContainer();
                    MigrationHttp.Send(sessionUrl, "/api/admin/login", "POST", "{}", sessionCookies, false);
                    MigrationHttp.Send(sessionUrl, "/api/admin/database", "GET", null, sessionCookies, false);
                    bool expired = false; try { MigrationHttp.Send(sessionUrl, "/api/admin/database", "GET", null, sessionCookies, false); } catch (MigrationAuthenticationException) { expired = true; }
                    Check(expired, "service restart 401 is classified as an expired admin session");
                    sessionCookies = new CookieContainer();
                    MigrationHttp.Send(sessionUrl, "/api/admin/login", "POST", "{}", sessionCookies, false);
                    MigrationHttp.Send(sessionUrl, "/api/admin/database", "GET", null, sessionCookies, false);
                    bool forbidden = false; try { MigrationHttp.Send(sessionUrl, "/api/admin/database", "GET", null, sessionCookies, false); } catch (MigrationAuthenticationException) { throw; } catch (InvalidOperationException) { forbidden = true; }
                    responder.GetAwaiter().GetResult();
                    Check(firstCookie && replacementCookie, "fresh login replaces the invalidated cookie and authenticates the next operation");
                    Check(forbidden, "non-401 failures do not trigger an administrator login retry");
                }
                string stampFile = Path.Combine(root, "archive-stamp-" + Guid.NewGuid().ToString("N") + ".zip");
                File.WriteAllBytes(stampFile, new byte[] { 1, 2, 3, 4 });
                string stamp;
                using (var stream = File.OpenRead(stampFile)) stamp = DatabaseArchiveRepository.ArchiveFileState(stream);
                Check(!string.IsNullOrEmpty(stamp), "Windows archive precheck reads native file identity and change time");
                using (var stream = File.OpenRead(stampFile)) Check(stamp == DatabaseArchiveRepository.ArchiveFileState(stream), "reading an unchanged archive preserves its verification identity");
                var writeTime = File.GetLastWriteTimeUtc(stampFile);
                File.WriteAllBytes(stampFile, new byte[] { 4, 3, 2, 1 }); File.SetLastWriteTimeUtc(stampFile, writeTime);
                using (var stream = File.OpenRead(stampFile)) Check(stamp != DatabaseArchiveRepository.ArchiveFileState(stream), "same-size overwrite with restored timestamp invalidates archive verification");
                int stopPolls = 0, exitPolls = 0, filePolls = 0; bool ordered = true;
                bool released = ServerUpgrade.WaitForUpdateRelease(() => ++stopPolls >= 2,
                    () => { if (stopPolls < 2) ordered = false; return ++exitPolls >= 2; },
                    () => { if (exitPolls < 2) ordered = false; return ++filePolls >= 2; }, TimeSpan.FromSeconds(3), null);
                Check(released && ordered && filePolls >= 2, "upgrade waits for stopped status, then process exit, then file release in order");
                Check(!ServerUpgrade.WaitForUpdateRelease(() => true, () => false, () => { throw new Exception("must not probe files before exit"); }, TimeSpan.FromMilliseconds(100), null), "SCM STOPPED alone never authorizes overwriting a still-running process");
                string lockedFile = Path.Combine(root, "upgrade-locked-" + Guid.NewGuid().ToString("N") + ".exe"); File.WriteAllText(lockedFile, "old application");
                using (var locked = new FileStream(lockedFile, FileMode.Open, FileAccess.Read, FileShare.None))
                {
                    Check(!ServerUpgrade.CanReplaceFile(lockedFile), "upgrade detects locked application files before copying");
                    Check(!ServerUpgrade.WaitForUpdateRelease(() => true, () => true, () => ServerUpgrade.CanReplaceFile(lockedFile), TimeSpan.FromMilliseconds(100), null), "locked files abort the upgrade instead of authorizing a copy");
                }
                Check(ServerUpgrade.WaitForUpdateRelease(() => true, () => true, () => ServerUpgrade.CanReplaceFile(lockedFile), TimeSpan.FromSeconds(1), null), "retry succeeds once the previous process releases the file");
                Check(!ServerUpgrade.CanReplaceFile(typeof(DatabaseMigrationHarness).Assembly.Location), "running executable images are detected as unavailable for replacement");
                string childExe = Path.Combine(root, "delayed-service-exit-" + Guid.NewGuid().ToString("N") + ".exe");
                File.Copy(typeof(DatabaseMigrationHarness).Assembly.Location, childExe);
                using (var child = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(childExe, "--upgrade-test-child") { UseShellExecute = false, CreateNoWindow = true }))
                {
                    Check(!ServerUpgrade.CanReplaceFile(childExe), "an actual running service-like child locks its executable before exit");
                    Check(ServerUpgrade.WaitForUpdateRelease(() => true, () => child.HasExited, () => ServerUpgrade.CanReplaceFile(childExe), TimeSpan.FromSeconds(10), null), "upgrade waits for a real delayed process exit and executable release before copying");
                    File.Copy(lockedFile, childExe, true);
                    Check(File.ReadAllText(childExe) == "old application", "first copy after process release succeeds without a second install attempt");
                }
                var longJob = new MachineImportJob { Id = Guid.NewGuid().ToString("N"), State = "completed", Mapping = new MachineDriverPackage() };
                for (int line = 0; line < 305; line++) MachineImportJobs.Report(root, longJob, "record-" + line.ToString("D4"), true);
                string fullLog = MachineImportJobs.LogText(root, longJob.Id);
                Check(longJob.Logs.Count == 300 && fullLog.Contains("record-0000") && fullLog.Contains("record-0304"), "full import logs preserve early records beyond the 300-line UI tail");
                Check(MachineImportJobs.Diagnostic(new AggregateException(new IOException("missing actual-driver.inf"))).Contains("actual-driver.inf"), "background task errors retain actual nested file diagnostics");
                const string secret = "DO-NOT-EXPOSE-TEST-PASSWORD";
                string diagnostic = ServerStorage.DatabaseFailureMessage(new DatabaseError(0, "HY000", "SSL Authentication Error " + secret,
                    new AuthenticationException("The remote certificate is invalid " + secret)));
                Check(diagnostic.Contains("CA") && diagnostic.Contains("地址") && !diagnostic.Contains(secret), "TLS failures expose actionable CA/hostname guidance without provider credentials");
                diagnostic = ServerStorage.DatabaseFailureMessage(new DatabaseError(1045, "28000", "Access denied " + secret));
                Check(diagnostic.Contains("1045") && diagnostic.Contains("28000") && diagnostic.Contains("认证") && !diagnostic.Contains(secret), "access-denied errors preserve Number/SQLSTATE without raw credentials");
                Check(ServerStorage.DatabaseFailureMessage(new DatabaseError(1142, "42000", secret)).Contains("权限"), "privilege errors classified");
                Check(ServerStorage.DatabaseFailureMessage(new DatabaseError(1049, "42000", secret)).Contains("不存在"), "unknown database classified");
                Check(ServerStorage.DatabaseFailureMessage(new DatabaseError(0, "HY000", "AllowPublicKeyRetrieval=True " + secret)).Contains("RSA"), "SHA-2 RSA authentication failure classified");
                Check(ServerStorage.DatabaseFailureMessage(new DatabaseError(0, "HY000", "Server does not support SSL")).Contains("启用 TLS"), "server with no TLS classified");
                Check(ServerStorage.DatabaseFailureMessage(new DatabaseError(0, "HY000", "Unable to connect to hosts")).Contains("端口"), "network error classified");
                Check(!ServerStorage.DatabaseFailureMessage(new DatabaseError(0, secret, secret)).Contains(secret), "unknown provider details and invalid SQLSTATE do not leak secrets");
                string ca = Path.Combine(root, "ca certificate.pem");
                File.WriteAllText(ca, "-----BEGIN CERTIFICATE-----\nTEST-CA\n-----END CERTIFICATE-----\n");
                string password = "sample;SslMode=None;quote=\"value\"";
                var options = new DbConnectionStringBuilder { ConnectionString = MigrationConnectionOptions.Build("db.example", 3306, "uda", "uda_service", password, "VerifyFull", ca) };
                Check(Convert.ToString(options["SslMode"]) == "VerifyFull" && Convert.ToString(options["Password"]) == password, "connection fields safely preserve special password characters and cannot override TLS");
                Check(Convert.ToString(options["SslCa"]) == ca, "CA file is passed to MySqlConnector");
                Check(!options.ContainsKey("AllowPublicKeyRetrieval"), "public-key retrieval is not silently enabled");
                options.ConnectionString = MigrationConnectionOptions.Build("db.example", 3306, "uda", "uda_service", password, "Required", "");
                Check(Convert.ToString(options["SslMode"]) == "Required" && !options.ContainsKey("SslCa"), "Required is explicit, no unused CA value");
                options.ConnectionString = MigrationConnectionOptions.Build("db.example", 3306, "uda", "uda_service", password, "VerifyCA", ca);
                Check(Convert.ToString(options["SslMode"]) == "VerifyCA", "explicit VerifyCA is supported");
                bool rejected = false;
                try { MigrationConnectionOptions.Build("db", 3306, "uda", "user", "", "VerifyFull", Path.Combine(root, "missing.pem")); } catch (ArgumentException) { rejected = true; }
                Check(rejected, "missing CA rejected before request");
                File.WriteAllText(ca, "-----BEGIN PRIVATE KEY-----\nsecret\n-----END PRIVATE KEY-----");
                rejected = false;
                try { MigrationConnectionOptions.Build("db", 3306, "uda", "user", "", "VerifyFull", ca); } catch (ArgumentException) { rejected = true; }
                Check(rejected, "private keys are not accepted as public CA certificates");
                Application.EnableVisualStyles();
                using (var form = new DatabaseMigrationForm("http://localhost:8731", new CookieContainer(), "test-admin", false))
                {
                    form.ShowInTaskbar = false; form.StartPosition = FormStartPosition.Manual; form.Location = new Point(-12000, -12000);
                    form.Show(); Application.DoEvents();
                    var ssl = (ComboBox)typeof(DatabaseMigrationForm).GetField("_ssl", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(form);
                    var caBox = (TextBox)typeof(DatabaseMigrationForm).GetField("_caFile", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(form);
                    Check(ssl.SelectedIndex == 0 && caBox.Enabled, "VerifyFull remains default with optional CA input");
                    ssl.SelectedIndex = 1; Check(!caBox.Enabled, "CA input disabled for Required");
                    ssl.SelectedIndex = 4; Check(caBox.Enabled, "CA input enabled for VerifyCA");
                    ssl.SelectedIndex = 0;
                    foreach (Control control in form.Controls) Check(control.Right <= form.ClientSize.Width && control.Bottom <= form.ClientSize.Height, "migration controls fit within form: " + control.Text);
                    using (var bitmap = new Bitmap(form.Width, form.Height)) { form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size)); bitmap.Save(Path.Combine(root, "migration-form.png")); }
                }
                Console.WriteLine("Asserts: " + _asserts + " Failed: 0\nRESULT: PASS");
                return 0;
            }
            catch (Exception ex) { Console.WriteLine("RESULT: FAIL\n" + ex); return 1; }
        }
    }
}
