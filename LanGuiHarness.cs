// LanGuiHarness.cs - LAN GUI state tests for LanEditionForm.
//
// Covers the three UI behaviours that protocol/layout tests cannot see:
//   1. Busy restore: after a scan or install ends — success, failure, exception or cancel — the
//      flags, buttons, progress bar (never a leftover marquee), wait cursor and CTS must all be
//      restored on the UI thread.
//   2. Multi-round entry: 「安装所选」 wires the user-confirmed picks into the limited multi-round
//      coordinator as round 0 (strictly what is checked), continuation rounds install only new,
//      recommended-safe candidates, with cumulative totals and a refreshed list at the end.
//      Runs against a real LanDriverServer on a loopback port with synthetic INFs and a fake
//      pnputil runner — nothing real is installed.
//   3. DPI bottom boundary: at 125% / 150% / 200% the footer/status bar must stay fully inside
//      the window and no control may overflow its parent.
// Modal dialogs are routed through test seams so the message pump never blocks.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace UDA
{
    internal static class LanGuiHarness
    {
        private static int _asserts;
        private static int _failed;
        private static string _current;
        private static L _log;

        [STAThread]
        private static int Main(string[] args)
        {
            string root = args.Length > 0 ? Path.GetFullPath(args[0]) : Path.Combine(Path.GetTempPath(), "uda-lan-gui-tests");
            Directory.CreateDirectory(root);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
            if (args.Length > 0 && Array.IndexOf(args, "/?") >= 0) { }
            int perTestSeconds = 180, overallSeconds = 900;
            // Watchdog: if any test (or the whole run) overruns, print FAIL and exit — never hang.
            // Environment.Exit also terminates blocked UI threads / modal pumps that no C# abort can reach.
            int startedTicks = Environment.TickCount;
            var watchdog = new Thread(delegate()
            {
                while (true)
                {
                    Thread.Sleep(1000);
                    int elapsed = Environment.TickCount - startedTicks;
                    if (elapsed > overallSeconds * 1000)
                    {
                        Console.WriteLine("[FAIL] overall: whole run exceeded " + overallSeconds + "s (last test: " + _current + ")");
                        Console.WriteLine("RESULT: FAIL");
                        Environment.Exit(2);
                    }
                }
            });
            watchdog.IsBackground = true; watchdog.Start();
            Action<string, Action> timedRun = delegate(string name, Action body)
            {
                _current = name;
                int before = _failed;
                int testStart = Environment.TickCount;
                bool timedOut = false;
                var timer = new System.Threading.Timer(delegate
                {
                    if (Environment.TickCount - testStart > perTestSeconds * 1000 && !timedOut)
                    {
                        timedOut = true;
                        Console.WriteLine("[FAIL] " + name + ": test exceeded " + perTestSeconds + "s — killed by watchdog");
                        Console.WriteLine("RESULT: FAIL");
                        Environment.Exit(2);
                    }
                });
                // Isolate the persistent install-receipt store per test run: point it at a scratch
                // file (deleted first) so receipts from a previous process run — or from a sibling
                // test — can never suppress installs or skip rounds inside this test.
                string receiptFile = Path.Combine(root, "receipts-" + name + ".txt");
                LanInstallReceiptStore.UseTestStore(receiptFile);
                try { body(); }
                catch (Exception ex) { Fail("threw: " + ex.GetType().Name + ": " + ex.Message + " @ " + ex.StackTrace); }
                finally { timer.Dispose(); LanInstallReceiptStore.ResetToProductStore(); }
                if (_failed == before) Console.WriteLine("[PASS] " + name);
                Console.WriteLine("[INFO] " + name + " took " + (Environment.TickCount - testStart) + " ms");
            };
            using (_log = new L(Path.Combine(root, "logs")))
            {
                timedRun("LanGui.BusyRestoreInstallPath", () => TestBusyRestore());
                timedRun("LanGui.MultiRoundInstallEntry", () => TestMultiRound());
                timedRun("LanGui.SkippedCandidateRefresh", () => TestSkippedCandidateRefresh());
                timedRun("LanGui.FirstScanRejectsRankedUpdate", () => TestFirstScanRejectsRankedUpdate(root));
                timedRun("LanGui.ConfirmDeclinedNoBusy", () => TestDecline());
                timedRun("LanGui.InstallFailureRestoresUi", () => TestFailure());
                timedRun("LanGui.DpiBottomBounds", () => TestDpiBottom());
            }
            Console.WriteLine("-------------------------------------------------");
            Console.WriteLine("Asserts: " + _asserts + "  Failed: " + _failed);
            if (_failed > 0) { Console.WriteLine("RESULT: FAIL"); return 1; }
            Console.WriteLine("RESULT: PASS");
            return 0;
        }

        private static void Run(string name, Action body)
        {
            _current = name;
            int before = _failed;
            try { body(); }
            catch (Exception ex) { Fail("threw: " + ex.GetType().Name + ": " + ex.Message + " @ " + ex.StackTrace); }
            if (_failed == before) Console.WriteLine("[PASS] " + name);
        }

        private static void T(bool condition, string message) { _asserts++; if (!condition) Fail(message); }
        private static void Fail(string message) { _failed++; Console.WriteLine("[FAIL] " + _current + ": " + message); }

        // ----- helpers: pump the WinForms message loop until an async UI task completes -----

        private static void PumpUntil(Task task, int timeoutMs)
        {
            int start = Environment.TickCount;
            while (!task.IsCompleted)
            {
                if (Environment.TickCount - start > timeoutMs)
                    throw new TimeoutException("UI task did not complete within " + timeoutMs + " ms (state=" + task.Status + ")");
                Application.DoEvents();
                Thread.Sleep(15);
            }
            Application.DoEvents();
            task.GetAwaiter().GetResult();   // propagate exceptions
        }

        private static void PumpUntil(Task task) { PumpUntil(task, 120000); }

        private static LanEditionForm NewForm()
        {
            var form = new LanEditionForm(new C(), new O(), _log, true);
            form.CreateControl();
            return form;
        }

        // ----- helpers: local LAN server + fake client (same seams as the protocol harness) -----

        private static string PrepareDir(string dir)
        {
            if (Directory.Exists(dir)) { try { Directory.Delete(dir, true); } catch { } }
            Directory.CreateDirectory(dir);
            return dir;
        }

        private static string WriteInf(string dir, string name, string hwid, string version)
        {
            string path = Path.Combine(dir, name);
            File.WriteAllText(path, new StringBuilder()
                .AppendLine("[Version]")
                .AppendLine("Signature=\"$Windows NT$\"")
                .AppendLine("Class=Net")
                .AppendLine("ClassGuid={4d36e972-e325-11ce-bfc1-08002be10318}")
                .AppendLine("Provider=%Vendor%")
                .AppendLine("DriverVer=03/14/2026," + version)
                .AppendLine("")
                .AppendLine("[Manufacturer]")
                .AppendLine("%Vendor%=Models")
                .AppendLine("")
                .AppendLine("[Models]")
                .AppendLine("%Dev%=Install," + hwid)
                .AppendLine("")
                .AppendLine("[Strings]")
                .AppendLine("Vendor=\"Test Vendor\"")
                .AppendLine("Dev=\"Test Device " + hwid + "\"")
                .ToString(), new UTF8Encoding(false));
            return path;
        }

        private static string MakeRepo(string repoDir, string[] srcDirs)
        {
            new OfflineRepoGenerator(new TrustingSignatureVerifier(), _log).Generate(repoDir, srcDirs,
                new OsTarget { Architecture = "amd64", MajorVersion = 10 }, null, "2026-09-24T00:00:00Z", CancellationToken.None, null);
            return repoDir;
        }

        private static int GetFreePort()
        {
            var l = new TcpListener(IPAddress.Loopback, 0);
            l.Start();
            int p = ((IPEndPoint)l.LocalEndpoint).Port;
            l.Stop();
            return p;
        }

        private class ServerEnv : IDisposable
        {
            public LanDriverServer Server;
            public string Url;
            public void Dispose()
            {
                if (Server != null) { try { Server.Stop(); } catch { } try { Server.Dispose(); } catch { } }
            }
        }

        private static ServerEnv StartServer(string repoDir)
        {
            int port = GetFreePort();
            var server = new LanDriverServer(new LanServerConfig
            {
                Port = port, RepoRoot = repoDir, Token = "", OsArchitecture = "amd64", OsMajorVersion = 10, Host = "localhost"
            }, _log);
            server.ImportSignatureVerifier = new TrustingSignatureVerifier();
            server.Start();
            var env = new ServerEnv { Server = server, Url = "http://localhost:" + port.ToString(System.Globalization.CultureInfo.InvariantCulture) };
            for (int i = 0; i < 60; i++)
            {
                try
                {
                    var req = (HttpWebRequest)WebRequest.Create(env.Url + "/api/health");
                    req.Timeout = 1000;
                    using (var resp = (HttpWebResponse)req.GetResponse()) if ((int)resp.StatusCode == 200) return env;
                }
                catch { }
                Thread.Sleep(25);
            }
            env.Dispose();
            throw new IOException("test server did not come up");
        }

        private static D Dev(string hwid, string id, string inf, string version)
        {
            return new D { Name = "Device " + id, Id = id, Class = "Net", ClassGuid = "{4d36e972-e325-11ce-bfc1-08002be10318}",
                Inf = inf ?? "", Version = version ?? "", H = new List<string> { hwid }, K = new List<string>() };
        }

        private static List<D> Clone(List<D> devices)
        {
            return devices.Select(d => new D { Name = d.Name, Id = d.Id, Class = d.Class, ClassGuid = d.ClassGuid,
                Inf = d.Inf, Version = d.Version, H = new List<string>(d.H), K = new List<string>(d.K) }).ToList();
        }

        private sealed class MutatingCollector : IHardwareCollector
        {
            private readonly List<List<D>> _snapshots;
            private int _index = -1;
            public MutatingCollector(List<List<D>> snapshots) { _snapshots = snapshots; }
            public List<D> Collect(L logger)
            {
                _index++;
                return _index < _snapshots.Count ? _snapshots[_index] : _snapshots[_snapshots.Count - 1];
            }
        }

        private static LanDriverClient NewClient(string url, string stagingDir, IHardwareCollector collector, FakePnpInstallRunner runner)
        {
            var cfg = new LanClientConfig
            {
                ServerUrl = url, Token = "", BootstrapRepoDir = "", TimeoutMs = 30000, DownloadTimeoutMs = 10000,
                MaxRetries = 4, BackoffInitialMs = 50, StagingDir = stagingDir, Deploy = false, WhatIf = false, Force = false,
                InstallTimeoutSeconds = 5, DedupeByContent = true, VerifySignature = true
            };
            return new LanDriverClient(cfg, new Machine { Maker = "T", Model = "M", Sku = "S" }, collector,
                new OsTarget { Architecture = "amd64", MajorVersion = 10 }, _log,
                new TrustingSignatureVerifier(), new AlwaysAdminChecker(), runner, new NoOpSystemOps());
        }

        private static Machine MachineOf(List<D> devices)
        {
            return new Machine { Maker = "T", Model = "M", Sku = "S", OS = "Test OS", Devices = devices };
        }

        // ---------------------------------------------------------------------------
        //  1. Busy restore: force every busy flavour, restore, and verify nothing spins.
        // ---------------------------------------------------------------------------
        private static void TestBusyRestore()
        {
            using (var form = NewForm())
            {
                T(form.TestTransferColumnsPresent(), "driver grid shows per-driver total size, downloaded bytes and transfer speed columns");
                // Install busy: dirty the UI exactly like a run that fails/cancels mid-flight.
                form.TestEnterBusyUi(true);
                T(form.TestProgress.Style == ProgressBarStyle.Blocks, "install busy keeps block progress");
                form.TestProgress.Style = ProgressBarStyle.Marquee;
                form.TestProgress.Value = Math.Min(40, form.TestProgress.Maximum);
                form.UseWaitCursor = true;
                form.EndLanBusyUi(true);
                T(form.TestBusyUiRestored(), "restore after install busy (flags/buttons/text/cts/waitcursor)");
                T(form.TestProgress.Style == ProgressBarStyle.Blocks && form.TestProgress.Value == 0,
                    "no marquee and zeroed progress after install restore, style=" + form.TestProgress.Style + " value=" + form.TestProgress.Value);

                // Scan busy then restore.
                form.TestEnterBusyUi(false);
                T(form.TestProgress.Style == ProgressBarStyle.Marquee, "scan busy shows marquee");
                form.TestProgress.Value = 30;
                form.EndLanBusyUi(false);
                T(form.TestBusyUiRestored(), "restore after scan busy");
                T(form.TestProgress.Value == 0, "progress zeroed after scan restore");

                // Restore must be idempotent and safe when nothing is busy.
                form.EndLanBusyUi(true);
                form.EndLanBusyUi(false);
                T(form.TestBusyUiRestored(), "restore idempotent when idle");
            }
        }

        // ---------------------------------------------------------------------------
        //  2. Multi-round entry through「安装所选」: round 0 strictly the checked rows; a new
        //     missing device is auto-installed in a later round; the list refreshes at the end.
        // ---------------------------------------------------------------------------
        private static void TestMultiRound()
        {
            string dir = PrepareDir(Path.Combine(Path.GetTempPath(), "uda-lan-gui-multiround"));
            string srcA = Path.Combine(dir, "A"); Directory.CreateDirectory(srcA);
            string srcB = Path.Combine(dir, "B"); Directory.CreateDirectory(srcB);
            WriteInf(srcA, "loopa1.inf", "PCI\\VEN_LGA&DEV_001", "2.0.0.0");
            WriteInf(srcB, "loopb1.inf", "PCI\\VEN_LGB&DEV_001", "2.0.0.0");
            string repo = MakeRepo(Path.Combine(dir, "repo"), new[] { srcA, srcB });
            using (var env = StartServer(repo))
            {
                var runner = new FakePnpInstallRunner { DefaultExitCode = 0 };
                var snapshots = new List<List<D>>
                {
                    new List<D> { Dev("PCI\\VEN_LGA&DEV_001", "GA", "", ""), Dev("PCI\\VEN_LGB&DEV_001", "GB", "", "") },                       // initial: both missing
                    new List<D> { Dev("PCI\\VEN_LGA&DEV_001", "GA", "oemA.inf", "2.0.0.0"), Dev("PCI\\VEN_LGB&DEV_001", "GB", "", "") },        // after round 0: A in effect, B still missing
                    new List<D> { Dev("PCI\\VEN_LGA&DEV_001", "GA", "oemA.inf", "2.0.0.0"), Dev("PCI\\VEN_LGB&DEV_001", "GB", "oemB.inf", "2.0.0.0") } // after round 1: all in effect
                };
                var client = NewClient(env.Url, Path.Combine(dir, "staging"), new MutatingCollector(snapshots), runner);
                var messages = new List<string>();
                bool confirmed = false;
                using (var form = NewForm())
                {
                    form.TestSetServerUrl(env.Url);
                    form.TestLanClient = client;
                    form.TestMessageSink = messages.Add;
                    form.TestConfirm = delegate { confirmed = true; return true; };
                    form.TestSetMachine(MachineOf(Clone(snapshots[0])));
                    form.TestMachineProvider = () => MachineOf(Clone(confirmed ? snapshots[2] : snapshots[0]));

                    PumpUntil(form.TestScanAsync());
                    T(form.TestDriversGrid.Rows.Count == 2, "scan filled 2 candidates, rows=" + form.TestDriversGrid.Rows.Count);
                    T(form.TestBusyUiRestored(), "busy restored after scan");
                    // First round strictly follows the checked rows: uncheck B.
                    foreach (DataGridViewRow row in form.TestDriversGrid.Rows)
                        if (string.Equals(Convert.ToString(row.Cells[2].Value), "Device GB", StringComparison.Ordinal))
                            row.Cells[0].Value = false;

                    PumpUntil(form.TestInstallAsync());
                    T(confirmed, "install confirmation was asked");
                    T(runner.Calls.Count == 2, "exactly two install calls (A round 0 + B later round), calls=" + runner.Calls.Count);
                    T(runner.Calls.Count(c => c.EndsWith("loopa1.inf", StringComparison.OrdinalIgnoreCase)) == 1, "checked A installed exactly once (dedup across rounds)");
                    T(runner.Calls.Count(c => c.EndsWith("loopb1.inf", StringComparison.OrdinalIgnoreCase)) == 1, "new missing B auto-installed in a continuation round");
                    string completion = messages.LastOrDefault(m => m.StartsWith("安装结果|", StringComparison.Ordinal)) ?? "";
                    T(completion.Contains("成功 2") && completion.Contains("失败 0"), "cumulative summary totals: " + completion);
                    T(completion.Contains("闭环 2 轮"), "rounds reported in summary: " + completion);
                    T(form.TestDriversGrid.Rows.Count == 0, "list refreshed at end (all up to date), rows=" + form.TestDriversGrid.Rows.Count);
                    T(form.TestBusyUiRestored(), "busy UI fully restored after multi-round install");
                    T(form.TestProgress.Style == ProgressBarStyle.Blocks && form.TestProgress.Value == 0, "no marquee left after install");
                }
            }
        }

        // ---------------------------------------------------------------------------
        //  3. Declined confirmation: no install, no busy state, grid untouched.
        // ---------------------------------------------------------------------------
        private static void TestDecline()
        {
            string dir = PrepareDir(Path.Combine(Path.GetTempPath(), "uda-lan-gui-decline"));
            string srcA = Path.Combine(dir, "A"); Directory.CreateDirectory(srcA);
            WriteInf(srcA, "decla1.inf", "PCI\\VEN_DCA&DEV_001", "2.0.0.0");
            string repo = MakeRepo(Path.Combine(dir, "repo"), new[] { srcA });
            using (var env = StartServer(repo))
            {
                var runner = new FakePnpInstallRunner { DefaultExitCode = 0 };
                var devices = new List<D> { Dev("PCI\\VEN_DCA&DEV_001", "DA", "", "") };
                var client = NewClient(env.Url, Path.Combine(dir, "staging"), new StaticHardwareCollector(Clone(devices)), runner);
                using (var form = NewForm())
                {
                    form.TestSetServerUrl(env.Url);
                    form.TestLanClient = client;
                    form.TestConfirm = delegate { return false; };
                    form.TestSetMachine(MachineOf(Clone(devices)));
                    PumpUntil(form.TestScanAsync());
                    T(form.TestDriversGrid.Rows.Count == 1, "scan filled 1 candidate");
                    PumpUntil(form.TestInstallAsync());
                    T(runner.Calls.Count == 0, "declined: nothing installed, calls=" + runner.Calls.Count);
                    T(form.TestBusyUiRestored(), "declined: busy UI untouched/restored");
                    T(form.TestDriversGrid.Rows.Count == 1, "declined: grid untouched");
                }
            }
        }

        // A scan candidate can be rejected by Windows as already current/best (pnputil 259).
        // Refresh hardware after skips so the stale update row is removed when the live version agrees.
        private static void TestSkippedCandidateRefresh()
        {
            string dir = PrepareDir(Path.Combine(Path.GetTempPath(), "uda-lan-gui-skipped-refresh"));
            string src = Path.Combine(dir, "A"); Directory.CreateDirectory(src);
            WriteInf(src, "skiprefresh.inf", "PCI\\VEN_SRA&DEV_001", "2.0.0.0");
            string repo = MakeRepo(Path.Combine(dir, "repo"), new[] { src });
            using (var env = StartServer(repo))
            {
                var initial = new List<D> { Dev("PCI\\VEN_SRA&DEV_001", "SRA", "oem-old.inf", "1.0.0.0") };
                var updated = new List<D> { Dev("PCI\\VEN_SRA&DEV_001", "SRA", "oem-current.inf", "2.0.0.0") };
                var runner = new FakePnpInstallRunner { DefaultExitCode = 259 };
                int machineReads = 0;
                var messages = new List<string>();
                using (var form = NewForm())
                {
                    form.TestSetServerUrl(env.Url);
                    form.TestLanClient = NewClient(env.Url, Path.Combine(dir, "staging"), new StaticHardwareCollector(Clone(initial)), runner);
                    form.TestSetMachine(MachineOf(Clone(initial)));
                    form.TestMachineProvider = () => MachineOf(Clone(Interlocked.Increment(ref machineReads) == 1 ? initial : updated));
                    form.TestMessageSink = messages.Add;
                    form.TestConfirm = delegate { return true; };
                    PumpUntil(form.TestScanAsync());
                    T(form.TestDriversGrid.Rows.Count == 1, "older current driver is shown as update before installation");
                    PumpUntil(form.TestInstallAsync());
                    string completion = messages.LastOrDefault(m => m.StartsWith("安装结果|", StringComparison.Ordinal)) ?? "";
                    T(completion.Contains("跳过 1"), "pnputil 259 is reported as skipped");
                    T(machineReads >= 3, "post-skip hardware and candidate list were refreshed; reads=" + machineReads);
                    T(form.TestDriversGrid.Rows.Count == 0, "stale update row removed after live version reaches candidate version");
                }
            }
        }

        private static void TestFirstScanRejectsRankedUpdate(string root)
        {
            string dir = PrepareDir(Path.Combine(root, "scan-rank"));
            string src = Path.Combine(dir, "A"); Directory.CreateDirectory(src);
            WriteInf(src, "scanrank.inf", "PCI\\VEN_RNK&DEV_001", "2.0.0.0");
            string repo = MakeRepo(Path.Combine(dir, "repo"), new[] { src });
            LanPnpRejectionStore.UseTestStore(Path.Combine(dir, "rejections.txt"));
            WindowsDriverPreference.TestPreferenceProbe = (device, inf) => false;
            try
            {
                using (var env = StartServer(repo))
                using (var form = NewForm())
                {
                    var devices = new List<D> { Dev("PCI\\VEN_RNK&DEV_001", "GUI-RANK", "oem-old.inf", "1.0.0.0") };
                    var runner = new FakePnpInstallRunner { DefaultExitCode = 0 };
                    form.TestSetServerUrl(env.Url);
                    form.TestLanClient = NewClient(env.Url, Path.Combine(dir, "staging"), new StaticHardwareCollector(devices), runner);
                    form.TestSetMachine(MachineOf(devices));
                    form.TestMachineProvider = () => MachineOf(Clone(devices));
                    form.TestMessageSink = message => { };
                    PumpUntil(form.TestScanAsync());
                    T(form.TestDriversGrid.Rows.Count == 0, "first scan never displays an unstaged package rejected by Windows ranking");
                    T(runner.Calls.Count == 0, "first-scan filtering needs no install attempt");
                    T(form.TestBusyUiRestored(), "ranking scan restores UI state");
                }
            }
            finally { WindowsDriverPreference.TestPreferenceProbe = null; LanPnpRejectionStore.ResetToProductStore(); }
        }

        // ---------------------------------------------------------------------------
        //  4. Install failure (dead server after a good scan): failure totals, busy restored,
        //     no marquee, buttons back.
        // ---------------------------------------------------------------------------
        private static void TestFailure()
        {
            string dir = PrepareDir(Path.Combine(Path.GetTempPath(), "uda-lan-gui-failure"));
            string srcA = Path.Combine(dir, "A"); Directory.CreateDirectory(srcA);
            WriteInf(srcA, "faila1.inf", "PCI\\VEN_FCA&DEV_001", "2.0.0.0");
            string repo = MakeRepo(Path.Combine(dir, "repo"), new[] { srcA });
            using (var env = StartServer(repo))
            {
                var devices = new List<D> { Dev("PCI\\VEN_FCA&DEV_001", "FA", "", "") };
                var runner = new FakePnpInstallRunner { DefaultExitCode = 0 };
                var messages = new List<string>();
                using (var form = NewForm())
                {
                    form.TestSetServerUrl(env.Url);
                    form.TestLanClient = NewClient(env.Url, Path.Combine(dir, "s1"), new StaticHardwareCollector(Clone(devices)), runner);
                    // The post-install auto-refresh re-collects hardware; pin it to the static snapshot so
                    // the refresh never falls through to a real (potentially very slow) WMI probe.
                    form.TestMachineProvider = () => MachineOf(Clone(devices));
                    form.TestMessageSink = messages.Add;
                    form.TestConfirm = delegate { return true; };
                    form.TestSetMachine(MachineOf(Clone(devices)));
                    PumpUntil(form.TestScanAsync());
                    T(form.TestDriversGrid.Rows.Count == 1, "scan filled 1 candidate");
                    // Swap to a client pointed at a dead port: every install path fails.
                    form.TestLanClient = NewClient("http://localhost:" + GetFreePort().ToString(System.Globalization.CultureInfo.InvariantCulture),
                        Path.Combine(dir, "s2"), new StaticHardwareCollector(Clone(devices)), runner);
                    PumpUntil(form.TestInstallAsync());
                    string completion = messages.LastOrDefault(m => m.StartsWith("安装结果|", StringComparison.Ordinal)) ?? "";
                    T(completion.Contains("失败 1"), "failure counted in summary: " + completion);
                    T(form.TestBusyUiRestored(), "busy restored after install failure");
                    T(form.TestProgress.Style == ProgressBarStyle.Blocks && form.TestProgress.Value == 0, "no marquee left after failure");
                }
            }
        }

        // ---------------------------------------------------------------------------
        //  5. DPI bottom boundary: at 125% / 150% / 200% the footer/status bar stays inside
        //     the window, fixed rows scale with DPI, and nothing overflows its parent.
        // ---------------------------------------------------------------------------
        private static void TestDpiBottom()
        {
            foreach (float scale in new[] { 1.25F, 1.5F, 1.75F, 2.0F, 2.5F })
            {
                foreach (string page in new[] { "home", "drivers", "server" })
                {
                    using (var form = NewForm())
                    {
                        form.LoadPreviewData();
                        form.ClientSize = new Size(1024, 700);
                        form.SetTestDpiScale(scale);
                        form.SelectPreviewPage(page);
                        form.RefreshResponsiveLayout();
                        Application.DoEvents();
                        var shell = form.TestShell;
                        var footer = form.TestFooterLayout;
                        T(footer != null && shell != null, scale + "x " + page + ": shell/footer built");
                        if (footer == null || shell == null) continue;
                        T(footer.Bottom <= form.ClientSize.Height, scale + "x " + page + ": footer bottom " + footer.Bottom + " inside window " + form.ClientSize.Height);
                        T(footer.Height >= 46 * scale - 2, scale + "x " + page + ": footer row scaled with DPI, height=" + footer.Height);
                        T(form.TestFooterStatus.Bottom <= footer.Bottom, scale + "x " + page + ": status text inside footer");
                        T(form.TestProgress.Bottom <= footer.Bottom, scale + "x " + page + ": progress bar inside footer");
                        var heights = shell.GetRowHeights();
                        T(heights.Length == 3 && heights.Sum() <= form.ClientSize.Height + 1,
                            scale + "x " + page + ": shell rows sum " + (heights.Length == 3 ? heights.Sum().ToString() : "?") + " <= client " + form.ClientSize.Height);
                        var overflows = new List<string>();
                        FindOverflows(form, overflows);
                        T(overflows.Count == 0, scale + "x " + page + ": no overflow — " + string.Join("; ", overflows));
                        var clippedButtons = new List<string>();
                        FindClippedButtonText(form, clippedButtons);
                        T(clippedButtons.Count == 0, scale + "x " + page + ": button text fully visible — " + string.Join("; ", clippedButtons));
                    }
                }
            }
        }

        private static void FindOverflows(Control parent, List<string> results)
        {
            foreach (Control child in parent.Controls)
            {
                if (!child.Visible) continue;
                if (child.Left < -2 || child.Top < -2 || child.Right > parent.ClientSize.Width + 2 || child.Bottom > parent.ClientSize.Height + 2)
                {
                    var scrollable = parent as ScrollableControl;
                    if (scrollable == null || !scrollable.AutoScroll)
                        results.Add(child.GetType().Name + " bounds=" + child.Bounds + " parent=" + parent.ClientSize);
                }
                FindOverflows(child, results);
            }
        }

        private static void FindClippedButtonText(Control parent, List<string> results)
        {
            foreach (Control child in parent.Controls)
            {
                if (!child.Visible) continue;
                var button = child as Button;
                if (button != null && !string.IsNullOrEmpty(button.Text))
                {
                    Size measured = TextRenderer.MeasureText(button.Text, button.Font,
                        new Size(Int32.MaxValue, Int32.MaxValue), TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
                    if (button.ClientSize.Width < measured.Width + 12 || button.ClientSize.Height < measured.Height + 8)
                        results.Add("'" + button.Text.Trim() + "' text=" + measured + " button=" + button.ClientSize);
                }
                FindClippedButtonText(child, results);
            }
        }
    }
}
