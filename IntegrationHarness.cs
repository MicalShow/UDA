// IntegrationHarness.cs - Phase 2 integration tests.
//
// Compiles alongside the real sources (Run-IntegrationTests.ps1). Exercises the index-core
// integration end-to-end, fully offline: no real pnputil, no real device scan, no WMI dependency.
// Covers: the testable hardware-collector abstraction, package-scope filtering + model preference,
// the scan -> install-plan flow (whitelist, dedup, WhatIf, exit-code classification, per-item
// results), cancellation of index build / scan, config backward compatibility, and the
// admin / existence / signature install gates.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace UDA
{
    internal static class IntegrationHarness
    {
        private static int _asserts;
        private static int _failed;
        private static string _current;
        private static L _log;

        private static int Main(string[] args)
        {
            string root = args.Length > 0 ? Path.GetFullPath(args[0]) : Path.Combine(Path.GetTempPath(), "uda-integration-tests");
            Directory.CreateDirectory(root);
            using (_log = new L(Path.Combine(root, "logs")))
            {
                Run("HardwareCollector.Abstraction", () => TestHardwareCollectorAbstraction(root));
                Run("PackageScope.Filtering", () => TestPackageScopeFiltering(root));
                Run("EndToEnd.ScanToInstallPlan", () => TestEndToEndScanToInstallPlan(root));
                Run("Install.ExitCodes", () => TestInstallExitCodes(root));
                Run("Install.WhitelistAndDedup", () => TestInstallWhitelistAndDedup(root));
                Run("Install.SignatureGate", () => TestSignatureGate(root));
                Run("Install.MultiCatalogSelection", () => TestMultiCatalogSelection(root));
                Run("Install.AdminGate", () => TestAdminGate(root));
                Run("Cancellation.BuildAndScan", () => TestCancellation(root));
                Run("Config.BackwardCompat", () => TestConfigBackwardCompat(root));

                Console.WriteLine("-------------------------------------------------");
                Console.WriteLine("Asserts: " + _asserts + "  Failed: " + _failed);
                if (_failed > 0) { Console.WriteLine("RESULT: FAIL"); return 1; }
                Console.WriteLine("RESULT: PASS");
                return 0;
            }
        }

        private static void Run(string name, Action body)
        {
            _current = name;
            int before = _failed;
            try { body(); }
            catch (Exception ex) { Fail("threw: " + ex.GetType().Name + ": " + ex.Message); }
            if (_failed == before) Console.WriteLine("[PASS] " + name);
        }

        private static void T(bool condition, string message)
        {
            _asserts++;
            if (!condition) Fail(message);
        }

        private static void Fail(string message)
        {
            _failed++;
            Console.WriteLine("[FAIL] " + _current + ": " + message);
        }

        // ---------------------------------------------------------------------------
        //  Helpers
        // ---------------------------------------------------------------------------

        private sealed class SyncProgress : IProgress<string>
        {
            private readonly Action<string> _action;
            public SyncProgress(Action<string> a) { _action = a; }
            public void Report(string value) { if (_action != null) _action(value); }
        }

        private static string PrepareDir(string dir)
        {
            if (Directory.Exists(dir)) { try { Directory.Delete(dir, true); } catch { } }
            Directory.CreateDirectory(dir);
            return dir;
        }

        private static string WriteInf(string dir, string name, string hwid, string date, string version)
        {
            string path = Path.Combine(dir, name);
            File.WriteAllText(path, new StringBuilder()
                .AppendLine("[Version]")
                .AppendLine("Signature=\"$Windows NT$\"")
                .AppendLine("Class=Net")
                .AppendLine("ClassGuid={4d36e972-e325-11ce-bfc1-08002be10318}")
                .AppendLine("Provider=%Vendor%")
                .AppendLine("DriverVer=" + date + "," + version)
                .AppendLine("")
                .AppendLine("[Manufacturer]")
                .AppendLine("%Vendor%=Models,NTamd64.10")
                .AppendLine("")
                .AppendLine("[Models.NTamd64.10]")
                .AppendLine("%Dev%=Install," + hwid)
                .AppendLine("")
                .AppendLine("[Strings]")
                .AppendLine("Vendor=\"Test Vendor\"")
                .AppendLine("Dev=\"Test Device\"")
                .ToString(), new UTF8Encoding(false));
            return path;
        }

        private static D Device(string hwid, string currentInf, string currentVersion)
        {
            return new D
            {
                Name = "Device " + hwid,
                Class = "Net",
                Inf = currentInf ?? "",
                Version = currentVersion ?? "",
                H = new List<string> { hwid },
                K = new List<string>()
            };
        }

        private static C NewConfig(string root, string sourcePath)
        {
            var config = new C();
            config.General.TargetArchitecture = "amd64";
            config.General.TargetOsMajorVersion = 10;
            config.General.IndexDirectory = Path.Combine(root, "index");
            config.General.ReportDirectory = Path.Combine(root, "reports");
            config.General.InstallTimeoutSeconds = 5;
            config.General.ScanAllSourcesWhenModelPackageMissing = true;
            config.General.ScanAllSourcesAfterModelPackage = false;
            config.General.DedupeInfByContent = true;
            config.General.VerifyDriverSignature = true;
            config.Sources.Add(new S { Name = "Local", Path = sourcePath, Enabled = true, Priority = 1 });
            config.Categories["Network"] = new[] { "Net" };
            config.Categories["Other"] = new string[0];
            return config;
        }

        private static E NewEngine(C config, O options, Machine machine, IHardwareCollector hc,
            IPnpInstallRunner runner, ISignatureVerifier sig, IAdminChecker admin, ISystemOps sys)
        {
            return new E(config, options, _log, machine, hc, runner, sig, admin, sys);
        }

        // ---------------------------------------------------------------------------
        //  1. Hardware collector abstraction: an injected collector drives scan matches.
        // ---------------------------------------------------------------------------

        private static void TestHardwareCollectorAbstraction(string root)
        {
            string dir = PrepareDir(Path.Combine(root, "hc"));
            string inf = WriteInf(dir, "net.inf", "PCI\\VEN_10EC&DEV_8168", "03/14/2026", "12.34.56.78");
            var config = NewConfig(root, dir);

            var devices = new List<D>
            {
                Device("PCI\\VEN_10EC&DEV_8168", "", ""),     // missing driver -> should match
                Device("PCI\\VEN_FFFF&DEV_FFFF", "oem99.inf", "1.0.0.0")  // no INF in index -> no match
            };
            var machine = new Machine();
            using (var e = NewEngine(config, new O(), machine, new StaticHardwareCollector(devices), null, null, null, null))
            {
                R r = e.Scan();
                T(r.Infs.Count == 1, "infs=" + r.Infs.Count);
                T(r.Matches.Count == 1, "matches=" + r.Matches.Count);
                X m = r.Matches[0];
                T(m.MatchTier == 4, "tier=" + m.MatchTier);
                T(m.Reason == "PCI\\VEN_10EC&DEV_8168", "reason=" + m.Reason);
                T(m.I.Path == inf, "infPath=" + m.I.Path);
                T(m.I.Version == "12.34.56.78", "version=" + m.I.Version);
                T(m.I.Date == "03/14/2026", "date=" + m.I.Date);
                T(m.Type == "精确硬件 ID", "type=" + m.Type);
                T(m.Level == "L4", "level=" + m.Level);
                T(m.Pick, "pick (missing driver)");
                T(m.State == "缺少驱动", "state=" + m.State);
                T(m.D.Name == "Device PCI\\VEN_10EC&DEV_8168", "deviceName=" + m.D.Name);
                // The unmatched device must NOT appear in Matches.
                T(!r.Matches.Any(x => x.D.H.Contains("PCI\\VEN_FFFF&DEV_FFFF")), "unmatched not listed");
            }
        }

        // ---------------------------------------------------------------------------
        //  2. Package-scope filtering + model preference. Package INFs go into PackageInfs;
        //     an equal package candidate beats a fallback candidate (model preference + newer date).
        // ---------------------------------------------------------------------------

        private static void TestPackageScopeFiltering(string root)
        {
            string baseDir = PrepareDir(Path.Combine(root, "pkg"));
            string packageRoot = Path.Combine(baseDir, "GA", "ModelA");
            string fallbackRoot = Path.Combine(baseDir, "Common");
            Directory.CreateDirectory(packageRoot);
            Directory.CreateDirectory(fallbackRoot);

            // Package INF for device A: newer (2026). Fallback also has an INF for device A: older (2024),
            // plus an INF for device B that only lives in the fallback root.
            string pkgInfA = WriteInf(packageRoot, "pkg_a.inf", "PCI\\VEN_0001&DEV_0001", "06/01/2026", "2.0.0.0");
            string fbInfA = WriteInf(fallbackRoot, "fb_a.inf", "PCI\\VEN_0001&DEV_0001", "01/01/2024", "1.0.0.0");
            string fbInfB = WriteInf(fallbackRoot, "fb_b.inf", "PCI\\VEN_0002&DEV_0002", "01/01/2026", "1.0.0.0");

            var config = NewConfig(root, baseDir);
            config.General.ScanAllSourcesAfterModelPackage = true; // index both the package root and the fallback
            config.ModelMappings.Add(new M
            {
                Name = "ModelA",
                ManufacturerPattern = "TestVendor",
                ModelPattern = "ModelA",
                SystemSKUPattern = "*",
                DriverPath = "GA/ModelA",
                Priority = 1,
                Enabled = true
            });
            var machine = new Machine { Maker = "TestVendor", Model = "ModelA", Sku = "SKU1" };
            var devices = new List<D>
            {
                Device("PCI\\VEN_0001&DEV_0001", "", ""),
                Device("PCI\\VEN_0002&DEV_0002", "", "")
            };
            using (var e = NewEngine(config, new O(), machine, new StaticHardwareCollector(devices), null, null, null, null))
            {
                R r = e.Scan();
                T(r.Package, "package mode");
                T(r.Mapping == "ModelA", "mapping=" + r.Mapping);
                T(r.Path == packageRoot, "packagePath=" + r.Path);
                // PackageInfs only contains the single INF under the package root.
                T(r.PackageInfs.Count == 1, "packageInfs=" + r.PackageInfs.Count);
                T(r.PackageInfs[0].Path == pkgInfA, "packageInfPath");
                // Both roots indexed.
                T(r.Infs.Count == 3, "infs=" + r.Infs.Count);
                T(r.ApprovedRoots.Count == 2, "approvedRoots=" + r.ApprovedRoots.Count);
                T(r.ApprovedRoots.Contains(packageRoot, StringComparer.OrdinalIgnoreCase), "approved has package root");
                T(r.ApprovedRoots.Contains(baseDir, StringComparer.OrdinalIgnoreCase), "approved has source root");
                // Device A: the package INF wins over the older fallback INF (model preference + newer date).
                X matchA = r.Matches.First(x => x.D.H.Contains("PCI\\VEN_0001&DEV_0001"));
                T(matchA.I.Path == pkgInfA, "deviceA picks package inf: " + matchA.I.Path);
                T(matchA.MatchTier == 4, "deviceA tier");
                // Device B: only the fallback INF matches.
                X matchB = r.Matches.First(x => x.D.H.Contains("PCI\\VEN_0002&DEV_0002"));
                T(matchB.I.Path == fbInfB, "deviceB picks fallback inf: " + matchB.I.Path);
                // fbInfA must not be the chosen one for device A.
                T(matchA.I.Path != fbInfA, "fallback dup not chosen for A");
            }
        }

        // ---------------------------------------------------------------------------
        //  3. End-to-end scan -> install plan: WhatIf skips; a real (faked) install records
        //     per-item results with safe arguments and Success outcome.
        // ---------------------------------------------------------------------------

        private static void TestEndToEndScanToInstallPlan(string root)
        {
            string dir = PrepareDir(Path.Combine(root, "e2e"));
            string inf = WriteInf(dir, "dev.inf", "PCI\\VEN_0010&DEV_0010", "03/14/2026", "2.0.0.0");
            var config = NewConfig(root, dir);
            var devices = new List<D> { Device("PCI\\VEN_0010&DEV_0010", "", "") };

            using (var e = NewEngine(config, new O(), new Machine(), new StaticHardwareCollector(devices), null, null, null, null))
            {
                R r = e.Scan();
                T(r.Matches.Count == 1, "matches=" + r.Matches.Count);
                r.Matches[0].Pick = true;

                // WhatIf: nothing runs, the accepted plan item is recorded as a skip.
                var oWhatIf = new O { WhatIf = true };
                var eWhatIf = NewEngine(config, oWhatIf, new Machine(), new StaticHardwareCollector(devices), null, null, null, null);
                R rWhatIf = eWhatIf.Scan();
                rWhatIf.Matches[0].Pick = true;
                Sum sWhatIf = eWhatIf.Install(rWhatIf, false, delegate { });
                T(sWhatIf.Skip == 1 && sWhatIf.Fail == 0 && sWhatIf.Ok == 0, "whatIf skip=" + sWhatIf.Skip + " fail=" + sWhatIf.Fail);
                T(sWhatIf.Items.Count == 1, "whatIf items=" + sWhatIf.Items.Count);
                T(sWhatIf.Items[0].SkipReason == "WhatIf", "whatIf reason=" + sWhatIf.Items[0].SkipReason);
                T(!sWhatIf.Items[0].Ran, "whatIf not ran");

                // Real (faked) install: runner is invoked once with the safe pnputil arguments.
                var runner = new FakePnpInstallRunner { DefaultExitCode = 0 };
                var eRun = NewEngine(config, new O(), new Machine(), new StaticHardwareCollector(devices), runner,
                    new TrustingSignatureVerifier(), new AlwaysAdminChecker(), new NoOpSystemOps());
                R rRun = eRun.Scan();
                rRun.Matches[0].Pick = true;
                Sum s = eRun.Install(rRun, false, delegate { });
                T(s.Ok == 1 && s.Fail == 0, "ok=" + s.Ok + " fail=" + s.Fail);
                T(runner.Calls.Count == 1, "runner calls=" + runner.Calls.Count);
                T(runner.Calls[0] == inf, "runner call path");
                T(s.Items.Count == 1 && s.Items[0].Ran, "item ran");
                T(s.Items[0].Outcome == InstallOutcome.Success, "outcome=" + s.Items[0].Outcome);
                T(s.Items[0].ExitCode == 0, "exitCode=" + s.Items[0].ExitCode);
                T(s.Items[0].Order == 1, "order=" + s.Items[0].Order);
            }
        }

        // ---------------------------------------------------------------------------
        //  4. Exit-code classification through the install runner: 0/3010/259/1.
        // ---------------------------------------------------------------------------

        private static void TestInstallExitCodes(string root)
        {
            string dir = PrepareDir(Path.Combine(root, "exitcodes"));
            WriteInf(dir, "dev.inf", "PCI\\VEN_0020&DEV_0020", "03/14/2026", "2.0.0.0");
            var config = NewConfig(root, dir);
            var devices = new List<D> { Device("PCI\\VEN_0020&DEV_0020", "", "") };

            foreach (var code in new[] { 0, 3010, 259, 1 })
            {
                var runner = new FakePnpInstallRunner { DefaultExitCode = code };
                using (var e = NewEngine(config, new O(), new Machine(), new StaticHardwareCollector(devices), runner,
                    new TrustingSignatureVerifier(), new AlwaysAdminChecker(), new NoOpSystemOps()))
                {
                    R r = e.Scan();
                    r.Matches[0].Pick = true;
                    Sum s = e.Install(r, false, delegate { });
                    if (code == 0) { T(s.Ok == 1 && !s.Reboot, "ec0 ok=" + s.Ok); T(s.Items[0].Outcome == InstallOutcome.Success, "ec0 outcome"); }
                    else if (code == 3010) { T(s.Ok == 1 && s.Reboot, "ec3010 ok=" + s.Ok + " reboot=" + s.Reboot); T(s.Items[0].Outcome == InstallOutcome.SuccessReboot, "ec3010 outcome"); }
                    else if (code == 259) { T(s.Skip == 1 && s.Ok == 0, "ec259 skip=" + s.Skip); T(s.Items[0].Outcome == InstallOutcome.Skipped, "ec259 outcome"); }
                    else { T(s.Fail == 1 && s.Ok == 0, "ec1 fail=" + s.Fail); T(s.Items[0].Outcome == InstallOutcome.Failure, "ec1 outcome"); }
                }
            }
        }

        // ---------------------------------------------------------------------------
        //  5. Whitelist (outside approved root rejected) + dedup (two devices, one INF).
        // ---------------------------------------------------------------------------

        private static void TestInstallWhitelistAndDedup(string root)
        {
            string dir = PrepareDir(Path.Combine(root, "wl"));
            string inf = WriteInf(dir, "shared.inf", "PCI\\VEN_0030&DEV_0030", "03/14/2026", "2.0.0.0");
            var config = NewConfig(root, dir);
            // Two devices match the same INF -> the install plan must dedup to one pnputil call.
            var devices = new List<D>
            {
                Device("PCI\\VEN_0030&DEV_0030", "", ""),
                new D { Name = "Second device", Class = "Net", Inf = "", Version = "", H = new List<string> { "PCI\\VEN_0030&DEV_0030" }, K = new List<string>() }
            };
            using (var e = NewEngine(config, new O(), new Machine(), new StaticHardwareCollector(devices), null, null, null, null))
            {
                R r = e.Scan();
                T(r.Matches.Count == 2, "matches=" + r.Matches.Count);
                foreach (var m in r.Matches) m.Pick = true;

                var runner = new FakePnpInstallRunner { DefaultExitCode = 0 };
                var eRun = NewEngine(config, new O(), new Machine(), new StaticHardwareCollector(devices), runner,
                    new TrustingSignatureVerifier(), new AlwaysAdminChecker(), new NoOpSystemOps());
                R rRun = eRun.Scan();
                foreach (var m in rRun.Matches) m.Pick = true;
                Sum s = eRun.Install(rRun, false, delegate { });
                T(runner.Calls.Count == 1, "dedup calls=" + runner.Calls.Count);
                T(s.Ok == 1, "dedup ok=" + s.Ok);

                // Whitelist: clearing ApprovedRoots rejects the candidate through the live Install path.
                R rWl = eRun.Scan();
                foreach (var m in rWl.Matches) m.Pick = true;
                rWl.ApprovedRoots = new List<string> { Path.Combine(dir, "not-a-real-root") };
                var runner2 = new FakePnpInstallRunner { DefaultExitCode = 0 };
                // Rebuild engine so the new runner is used, but reuse the scanned result.
                var eWl = NewEngine(config, new O(), new Machine(), new StaticHardwareCollector(devices), runner2,
                    new TrustingSignatureVerifier(), new AlwaysAdminChecker(), new NoOpSystemOps());
                R rWlScan = eWl.Scan();
                foreach (var m in rWlScan.Matches) m.Pick = true;
                rWlScan.ApprovedRoots = new List<string> { Path.Combine(dir, "not-a-real-root") };
                Sum sWl = eWl.Install(rWlScan, false, delegate { });
                T(sWl.Ok == 0 && runner2.Calls.Count == 0, "whitelist ok=" + sWl.Ok + " calls=" + runner2.Calls.Count);
                T(sWl.Items.Count == 1 && sWl.Items[0].SkipReason == "outside approved source root", "whitelist reason=" + (sWl.Items.Count > 0 ? sWl.Items[0].SkipReason : "none"));
            }
        }

        // ---------------------------------------------------------------------------
        //  6. Signature gate: a rejecting verifier skips the item without invoking pnputil.
        // ---------------------------------------------------------------------------

        private static void TestSignatureGate(string root)
        {
            string dir = PrepareDir(Path.Combine(root, "sig"));
            WriteInf(dir, "dev.inf", "PCI\\VEN_0040&DEV_0040", "03/14/2026", "2.0.0.0");
            var config = NewConfig(root, dir);
            var devices = new List<D> { Device("PCI\\VEN_0040&DEV_0040", "", "") };
            var runner = new FakePnpInstallRunner { DefaultExitCode = 0 };
            using (var e = NewEngine(config, new O(), new Machine(), new StaticHardwareCollector(devices), runner,
                new RejectingSignatureVerifier(), new AlwaysAdminChecker(), new NoOpSystemOps()))
            {
                R r = e.Scan();
                r.Matches[0].Pick = true;
                Sum s = e.Install(r, false, delegate { });
                T(s.Ok == 0 && s.Skip == 1, "sig ok=" + s.Ok + " skip=" + s.Skip);
                T(runner.Calls.Count == 0, "sig runner not called=" + runner.Calls.Count);
                T(s.Items[0].SkipReason == "签名验证失败", "sig reason=" + s.Items[0].SkipReason);
            }
        }

        private static void TestMultiCatalogSelection(string root)
        {
            string dir = PrepareDir(Path.Combine(root, "multi-cat"));
            string gpioInf = Path.Combine(dir, "iaLPSS2_GPIO2_CNL.inf");
            string i2cInf = Path.Combine(dir, "iaLPSS2_I2C_CNL.inf");
            File.WriteAllText(gpioInf, "[Version]\r\nSignature=\"$WINDOWS NT$\"\r\nCatalogFile=iaLPSS2_GPIO2_CNL.cat\r\n[Manufacturer]\r\n%I%=Intel,NTamd64.10.0\r\n[Intel.NTamd64.10.0]\r\n%D%=Inst,ACPI\\INT34BB\r\n", Encoding.Default);
            File.WriteAllText(i2cInf, "[Version]\r\nSignature=\"$WINDOWS NT$\"\r\nCatalogFile.NTamd64=iaLPSS2_I2C_CNL.cat\r\n[Manufacturer]\r\n%I%=Intel,NTamd64.10.0\r\n[Intel.NTamd64.10.0]\r\n%D%=Inst,PCI\\VEN_8086&DEV_9DE8\r\n", Encoding.Default);
            string gpioCat = Path.Combine(dir, "iaLPSS2_GPIO2_CNL.cat");
            string i2cCat = Path.Combine(dir, "iaLPSS2_I2C_CNL.cat");
            File.WriteAllText(gpioCat, "gpio", Encoding.ASCII);
            File.WriteAllText(i2cCat, "i2c", Encoding.ASCII);
            T(string.Equals(WinTrustSignatureVerifier.ResolveCatalogPath(gpioInf), gpioCat, StringComparison.OrdinalIgnoreCase), "GPIO INF selects its own CAT");
            T(string.Equals(WinTrustSignatureVerifier.ResolveCatalogPath(i2cInf), i2cCat, StringComparison.OrdinalIgnoreCase), "decorated CatalogFile selects its own CAT");

            InfRecord acpi = InfParser.Parse(gpioInf, "SIO");
            T(acpi.HardwareIds.Contains("ACPI\\INT34BB", StringComparer.OrdinalIgnoreCase), "ACPI hardware ID imported");
            var matches = new MatchRanker(new[] { acpi }, null).Rank(new[] { "ACPI\\VEN_INT&DEV_34BB" }, new string[0], new OsTarget { Architecture = "amd64", MajorVersion = 10 });
            T(matches.Count == 1, "compact INF ACPI ID matches expanded Windows ACPI ID");
        }

        // ---------------------------------------------------------------------------
        //  7. Admin gate: deployment without admin skips every item with a clear reason.
        // ---------------------------------------------------------------------------

        private static void TestAdminGate(string root)
        {
            string dir = PrepareDir(Path.Combine(root, "admin"));
            WriteInf(dir, "dev.inf", "PCI\\VEN_0050&DEV_0050", "03/14/2026", "2.0.0.0");
            var config = NewConfig(root, dir);
            var devices = new List<D> { Device("PCI\\VEN_0050&DEV_0050", "", "") };
            var runner = new FakePnpInstallRunner { DefaultExitCode = 0 };
            using (var e = NewEngine(config, new O { Deploy = true }, new Machine(), new StaticHardwareCollector(devices),
                runner, new TrustingSignatureVerifier(), new NeverAdminChecker(), new NoOpSystemOps()))
            {
                R r = e.Scan();
                T(r.Matches.Count == 1, "admin scan matches=" + r.Matches.Count);
                r.Matches[0].Pick = true;
                Sum s = e.Install(r, false, delegate { });
                T(s.Ok == 0 && s.Fail == 1, "admin ok=" + s.Ok + " fail=" + s.Fail);
                T(runner.Calls.Count == 0, "admin runner not called=" + runner.Calls.Count);
                T(s.Items[0].SkipReason == "需要管理员权限", "admin reason=" + s.Items[0].SkipReason);
            }
        }

        // ---------------------------------------------------------------------------
        //  8. Cancellation: a pre-cancelled token and a progress-driven cancel both abort.
        // ---------------------------------------------------------------------------

        private static void TestCancellation(string root)
        {
            string dir = PrepareDir(Path.Combine(root, "cancel"));
            for (int i = 0; i < 30; i++)
                WriteInf(dir, "d" + i.ToString("00") + ".inf", "PCI\\VEN_006" + (i % 10) + "&DEV_" + i.ToString("D4"), "01/01/2026", "1.0.0.0");
            var config = NewConfig(root, dir);
            var devices = new List<D> { Device("PCI\\VEN_0060&DEV_0000", "", "") };

            // Pre-cancelled token: Build throws on the first checkpoint.
            using (var e = NewEngine(config, new O(), new Machine(), new StaticHardwareCollector(devices), null, null, null, null))
            {
                var cts = new CancellationTokenSource();
                cts.Cancel();
                bool cancelled = false;
                try { e.Scan(cts.Token, null); }
                catch (OperationCanceledException) { cancelled = true; }
                T(cancelled, "pre-cancelled scan threw OCE");
            }

            // Cancel from the first progress report mid-build.
            using (var e = NewEngine(config, new O(), new Machine(), new StaticHardwareCollector(devices), null, null, null, null))
            {
                var cts = new CancellationTokenSource();
                var progress = new SyncProgress(delegate(string msg) { cts.Cancel(); });
                bool cancelled = false;
                try { e.Scan(cts.Token, progress); }
                catch (OperationCanceledException) { cancelled = true; }
                T(cancelled, "progress-cancelled scan threw OCE");
            }
        }

        // ---------------------------------------------------------------------------
        //  9. Config backward compatibility: an old-style JSON (no Phase 2 fields) loads
        //     with the new defaults, and the engine scans with it.
        // ---------------------------------------------------------------------------

        private static void TestConfigBackwardCompat(string root)
        {
            string dir = PrepareDir(Path.Combine(root, "compat"));
            WriteInf(dir, "dev.inf", "PCI\\VEN_0070&DEV_0070", "03/14/2026", "2.0.0.0");

            // Minimal old-style config: only the fields that existed before Phase 2.
            string configPath = Path.Combine(dir, "old-config.json");
            File.WriteAllText(configPath, "{\"General\":{\"LogDirectory\":\"" + (root + "\\compat\\logs").Replace("\\", "\\\\") +
                "\",\"ReportDirectory\":\"" + (root + "\\compat\\reports").Replace("\\", "\\\\") +
                "\",\"InstallTimeoutSeconds\":5,\"DeploymentCountdownSeconds\":0,\"AutoReboot\":false,\"CreateRestorePoint\":false," +
                "\"ScanAllSourcesWhenModelPackageMissing\":true,\"ScanAllSourcesAfterModelPackage\":false,\"IncludeDisconnectedDevices\":false}," +
                "\"Sources\":[{\"Name\":\"Local\",\"Path\":\"" + dir.Replace("\\", "\\\\") + "\",\"Priority\":1,\"Enabled\":true,\"Username\":\"\",\"Password\":\"\"}]," +
                "\"ModelMappings\":[],\"Categories\":{\"Other\":[]},\"Filters\":{\"IncludeCategories\":[],\"ExcludeHardwareIds\":[],\"OnlyMissingOrProblemDevicesInMaintenance\":false}}",
                new UTF8Encoding(false));
            C c = C.Load(configPath);
            // New fields take their defaults even though the JSON omitted them.
            T(c.General.IndexDirectory == "%ProgramData%\\UniversalDriverAssistant\\Index", "indexDir default=" + c.General.IndexDirectory);
            T(c.General.DedupeInfByContent, "dedupe default");
            T(c.General.VerifyDriverSignature, "verifySig default");
            T(c.General.TargetArchitecture == "", "targetArch default");
            T(c.General.TargetOsMajorVersion == 0, "targetOs default");
            // Override the index dir to keep the test local, then scan.
            c.General.IndexDirectory = Path.Combine(root, "compat", "index");
            var devices = new List<D> { Device("PCI\\VEN_0070&DEV_0070", "", "") };
            using (var e = NewEngine(c, new O(), new Machine(), new StaticHardwareCollector(devices), null, null, null, null))
            {
                R r = e.Scan();
                T(r.Matches.Count == 1, "compat scan matches=" + r.Matches.Count);
                T(r.Matches[0].MatchTier == 4, "compat match tier");
            }
        }
    }
}
