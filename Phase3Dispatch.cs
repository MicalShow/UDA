// Phase3Dispatch.cs - Phase 3 CLI dispatch (offline --repo and LAN --lan modes).
//
// Kept in a separate file so the Phase 1/2 harnesses (which compile UniversalDriverAssistant.cs but NOT
// OfflineRepo.cs / LanDriverService.cs) still build. Program.Main finds this class by reflection
// (Type.GetType("UDA.Phase3Dispatch")); if absent (older harness build) it simply skips Phase 3 and the
// existing maintenance/deployment flow runs unchanged. Compiled by Build.ps1 and the Phase 3 harnesses.
using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace UDA
{
    public static class Phase3Dispatch
    {
        /// <summary>Returns the process exit code, or -1 when no Phase 3 mode was requested (so Program.Main
        /// falls through to the legacy maintenance/deployment flow).</summary>
        public static int Run(O o, C c, L l)
        {
            if (o.Sccm) return RunSccm(o, c, l);
            if (o.Repo != null) return RunOffline(o, c, l);
            bool lanEdition = File.Exists(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "LanEdition.flag"));
            if (o.Lan != null)
            {
                if (o.Deploy || o.WhatIf || o.ScanOnly) return RunLan(o, c, l);
                return RunLanGui(o, c, l);
            }
            if (lanEdition) return RunLanGui(o, c, l);
            return -1;
        }

        public static int SccmExitCode(LanDriverClient.LanClosedLoopResult result)
        {
            if (result == null || result.Cancelled || !string.IsNullOrEmpty(result.Error)) return 2;
            if (result.InstalledFail > 0 || result.InstalledUnverified > 0 || result.InstalledSkip > 0 || result.StoppedAtMaxRounds) return 1;
            return result.Reboot ? 3010 : 0;
        }

        private static int RunSccm(O o, C c, L l)
        {
            if (!new WindowsAdminChecker().IsAdministrator()) throw new InvalidOperationException("SCCM mode requires LocalSystem or administrator; no interactive elevation is attempted.");
            var defaultsPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "LanClient.defaults.json");
            var defaults = File.Exists(defaultsPath) ? new JavaScriptSerializer().Deserialize<LanClientUiSettings>(File.ReadAllText(defaultsPath)) : new LanClientUiSettings();
            var cfg = new LanClientConfig { ServerUrl = o.Lan ?? defaults.ServerUrl, Token = o.LanToken ?? defaults.Token,
                BootstrapRepoDir = o.BootstrapRepo, Deploy = !o.ScanOnly, WhatIf = o.WhatIf, VerifySignature = true,
                InstallTimeoutSeconds = c.General.InstallTimeoutSeconds, DedupeByContent = true };
            Uri server;
            if (!Uri.TryCreate(cfg.ServerUrl, UriKind.Absolute, out server) || (server.Scheme != "http" && server.Scheme != "https")) throw new InvalidOperationException("Valid server URL required.");
            var target = ResolveOsTarget(c);
            var collector = new WmiHardwareCollector();
            var client = new LanDriverClient(cfg, null, collector, target, l, new WinTrustSignatureVerifier(), new WindowsAdminChecker(), null, new RealSystemOps());
            var devices = collector.Collect(l);
            client.Bootstrap(devices, CancellationToken.None, null);
            LanHealth health = null;
            for (int i = 0; i < 60; i++)
            {
                health = client.Probe(CancellationToken.None);
                if (health != null && health.RepoLoaded && !health.ImportRunning) break;
                Thread.Sleep(1000);
            }
            if (health == null || !health.RepoLoaded || health.ImportRunning) throw new IOException("Server unavailable or repository not ready after 60 seconds.");
            devices = collector.Collect(l);
            var response = client.Report(devices, null, CancellationToken.None, null);
            string directory = Environment.ExpandEnvironmentVariables(c.General.ReportDirectory); Directory.CreateDirectory(directory);
            string report = Path.Combine(directory, "UDA-SCCM-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N"));
            File.WriteAllText(report + "-candidates.json", new JavaScriptSerializer { MaxJsonLength = 16 * 1024 * 1024 }.Serialize(response), new UTF8Encoding(false));
            foreach (var candidate in response.Candidates) l.W("[INFO] SCCM candidate: " + candidate.DeviceInstanceId + " " + candidate.InfRelativePath);
            if (o.ScanOnly) { l.W("[INFO] SCCM scan-only: no driver installation requested"); return 0; }
            var result = client.RunControlledLoop(response.Candidates.Where(candidate => candidate.NeedInstall).ToList(), CancellationToken.None, null, null,
                new LanDriverClient.LanClosedLoopOptions { AllowUpdates = true });
            int exit = SccmExitCode(result);
            File.WriteAllText(report + "-result.json", new JavaScriptSerializer().Serialize(new { exitCode = exit, result = result }), new UTF8Encoding(false));
            l.W("[INFO] SCCM deployment finished: exit=" + exit);
            return exit;
        }

        private static int RunLanGui(O o, C c, L l)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            // LanEditionForm reuses the full ResponsiveForm visual baseline and drives the LAN workflow
            // (server status / test connection / scan candidates / confirm install / cancel / live progress).
            Application.Run(new LanEditionForm(c, o, l));
            return 0;
        }

        private static int RunOffline(O o, C c, L l)
        {
            var ot = ResolveOsTarget(c);
            // NIC bootstrap first: if a local NIC bootstrap repo is configured, install any missing NIC
            // driver offline so the machine can reach the network before the main repo scan/install. Mirrors
            // the LAN client's bootstrap step; a missing/empty bootstrap repo is a no-op (safe downgrade).
            if (!string.IsNullOrEmpty(o.BootstrapRepo))
            {
                var nb = NicBootstrap.TryInstall(o.BootstrapRepo, ot, new WmiHardwareCollector(), null,
                    o.Deploy, o.WhatIf, o.Force, c.General.InstallTimeoutSeconds,
                    c.General.DedupeInfByContent, c.General.VerifyDriverSignature, l,
                    new WinTrustSignatureVerifier(), new WindowsAdminChecker(), null, new RealSystemOps(), CancellationToken.None);
                l.W("[INFO] 离线 NIC 引导 / Offline NIC bootstrap: attempted=" + nb.Attempted + " ok=" + nb.Ok + " fail=" + nb.Fail + (nb.Error.Length > 0 ? " err=" + nb.Error : ""));
            }
            var repo = OfflineRepo.Open(o.Repo, l);
            var inst = new OfflineInstaller(repo, ot, new WmiHardwareCollector(), null, l,
                o.Deploy, o.WhatIf, o.Force, c.General.InstallTimeoutSeconds,
                c.General.DedupeInfByContent, c.General.VerifyDriverSignature,
                new WinTrustSignatureVerifier(), new WindowsAdminChecker(), null, new RealSystemOps());
            var r = inst.Scan(CancellationToken.None, null);
            Rep.Save(c, r, null);
            if (o.ScanOnly) return 0;
            foreach (var x in r.Matches) x.Pick = x.Pick || o.Force;
            var s = inst.Install(r, false, (x, y, n) => l.W("[INFO] " + x + "/" + y + " " + n));
            Rep.Save(c, r, s);
            return s.Fail > 0 ? (s.Ok > 0 ? 1 : 2) : (s.Reboot ? 3010 : 0);
        }

        private static int RunLan(O o, C c, L l)
        {
            var env = OsTarget.FromEnvironment();
            var ot = new OsTarget
            {
                Architecture = !string.IsNullOrWhiteSpace(c.General.TargetArchitecture) ? c.General.TargetArchitecture.Trim().ToLowerInvariant() : env.Architecture,
                MajorVersion = c.General.TargetOsMajorVersion > 0 ? c.General.TargetOsMajorVersion : env.MajorVersion,
                MinorVersion = c.General.TargetOsMajorVersion > 0 && c.General.TargetOsMajorVersion != env.MajorVersion ? -1 : env.MinorVersion,
                BuildNumber = env.BuildNumber
            };
            var cc = new LanClientConfig
            {
                ServerUrl = o.Lan, Token = o.LanToken, BootstrapRepoDir = o.BootstrapRepo,
                Deploy = o.Deploy, WhatIf = o.WhatIf, Force = o.Force,
                InstallTimeoutSeconds = c.General.InstallTimeoutSeconds,
                DedupeByContent = c.General.DedupeInfByContent, VerifySignature = c.General.VerifyDriverSignature
            };
            var client = new LanDriverClient(cc, null, new WmiHardwareCollector(), ot, l,
                new WinTrustSignatureVerifier(), new WindowsAdminChecker(), null, new RealSystemOps());
            var res = client.Run(CancellationToken.None);
            l.W("[INFO] LAN 客户端完成 / LAN client done: " + new JavaScriptSerializer().Serialize(res));
            SaveLanResult(c, res);
            return res.Cancelled ? 0 : (res.InstalledFail > 0 ? (res.InstalledOk > 0 ? 1 : 2) : 0);
        }

        private static OsTarget ResolveOsTarget(C c)
        {
            var env = OsTarget.FromEnvironment();
            if (!string.IsNullOrWhiteSpace(c.General.TargetArchitecture) && c.General.TargetOsMajorVersion > 0)
                return new OsTarget { Architecture = c.General.TargetArchitecture.Trim().ToLowerInvariant(), MajorVersion = c.General.TargetOsMajorVersion, BuildNumber = env.BuildNumber };
            return env;
        }

        private static void SaveLanResult(C c, LanClientResult res)
        {
            if (res == null) return;
            string d = Environment.ExpandEnvironmentVariables(c.General.ReportDirectory);
            try { Directory.CreateDirectory(d); } catch { }
            string p = Path.Combine(d, "UDA-Lan-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
            File.WriteAllText(p + ".json", new JavaScriptSerializer().Serialize(res), new UTF8Encoding(false));
        }
    }
}
