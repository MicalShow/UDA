// DriverIndexHarness.cs - runnable tests for the Phase 1 driver index core.
// Compiled by Run-DriverIndexTests.ps1 alongside the real sources. Offline; no pnputil calls.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace UDA
{
    internal static class DriverIndexHarness
    {
        private static int _asserts;
        private static int _failed;
        private static string _current;

        private static int Main(string[] args)
        {
            string root = args.Length > 0 ? Path.GetFullPath(args[0]) : Path.Combine(Path.GetTempPath(), "uda-driver-index-tests");
            Directory.CreateDirectory(root);

            Run("InfParser", () => TestInfParser(root));
            Run("HardwareIdNormalizer", () => TestNormalizer());
            Run("OsArchFilter", () => TestOsArchFilter());
            Run("MatchRanker", () => TestRanker());
            Run("DriverIndex.Incremental", () => TestIncremental(root));
            Run("DriverIndex.Schema", () => TestSchemaVersion(root));
            Run("DriverIndex.Transaction", () => TestTransaction(root));
            Run("InstallPlanner", () => TestPlanner(root));

            Console.WriteLine("-------------------------------------------------");
            Console.WriteLine("Asserts: " + _asserts + "  Failed: " + _failed);
            if (_failed > 0)
            {
                Console.WriteLine("RESULT: FAIL");
                return 1;
            }
            Console.WriteLine("RESULT: PASS");
            return 0;
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

        private static void TestInfParser(string root)
        {
            string dir = PrepareDir(Path.Combine(root, "infs"));
            string inf = Path.Combine(dir, "net.inf");
            File.WriteAllText(inf, new StringBuilder()
                .AppendLine("[Version]")
                .AppendLine("Signature=\"$Windows NT$\"")
                .AppendLine("Class=Net")
                .AppendLine("ClassGuid={4d36e972-e325-11ce-bfc1-08002be10318}")
                .AppendLine("Provider=%Vendor%")
                .AppendLine("DriverVer=03/14/2026,12.34.56.78")
                .AppendLine("")
                .AppendLine("[Manufacturer]")
                .AppendLine("%Vendor%=Models,NTamd64.10")
                .AppendLine("")
                .AppendLine("[Models.NTamd64.10]")
                .AppendLine("%DevA%=InstA,PCI\\VEN_10EC&DEV_8168&SUBSYS_0001ABCD&REV_10,PCI\\VEN_10EC&DEV_8168,PCI\\CC_0200")
                .AppendLine("%DevB%=InstB,USB\\VID_1234&PID_5678&REV_0100")
                .AppendLine("")
                .AppendLine("[Strings]")
                .AppendLine("Vendor=\"Acme Net\"")
                .AppendLine("DevA=\"Acme Gigabit Adapter\"")
                .AppendLine("DevB=\"Acme USB Dongle\"")
                .ToString(), new UTF8Encoding(false));

            var rec = InfParser.Parse(inf, "Local");
            T(rec.Class == "Net", "Class=" + rec.Class);
            T(rec.ClassGuid == "{4d36e972-e325-11ce-bfc1-08002be10318}", "ClassGuid=" + rec.ClassGuid);
            T(rec.Provider == "Acme Net", "Provider=" + rec.Provider);
            T(rec.Manufacturer == "Acme Net", "Manufacturer=" + rec.Manufacturer);
            T(rec.DriverVerDate == "03/14/2026", "DriverVerDate=" + rec.DriverVerDate);
            T(rec.DriverVerVersion == "12.34.56.78", "DriverVerVersion=" + rec.DriverVerVersion);
            T(rec.Decorations.Contains("NTamd64.10"), "decorations=" + string.Join(",", rec.Decorations));
            T(rec.Models.Count == 2, "models=" + rec.Models.Count);
            T(rec.Models[0].Description == "Acme Gigabit Adapter", "desc0=" + rec.Models[0].Description);
            T(rec.Models[0].InstallSection == "InstA", "inst0=" + rec.Models[0].InstallSection);
            // DevA: two hardware IDs (specific) + one compatible ID (class code), via ';' separator.
            T(rec.Models[0].HardwareIds.Count == 2, "hw0=" + rec.Models[0].HardwareIds.Count);
            T(rec.Models[0].CompatibleIds.Count == 1, "cmp0=" + rec.Models[0].CompatibleIds.Count);
            T(rec.Models[0].CompatibleIds[0] == "PCI\\CC_0200", "cmp0id=" + rec.Models[0].CompatibleIds[0]);
            // DevB: classified by heuristic (VID_/PID_ => hardware id).
            T(rec.Models[1].HardwareIds.Count == 1, "hw1=" + rec.Models[1].HardwareIds.Count);
            T(rec.Models[1].HardwareIds[0] == "USB\\VID_1234&PID_5678&REV_0100", "hw1id=" + rec.Models[1].HardwareIds[0]);
            // Aggregated IDs.
            T(rec.HardwareIds.Count == 3, "aggHw=" + rec.HardwareIds.Count);
            T(rec.CompatibleIds.Count == 1, "aggCmp=" + rec.CompatibleIds.Count);
            T(rec.ContentHash.Length == 64, "hashLen=" + rec.ContentHash.Length);
            T(rec.Fingerprint.IndexOf('|') > 0, "fingerprint=" + rec.Fingerprint);

            // No-[Manufacturer] fallback path.
            string simple = Path.Combine(dir, "simple.inf");
            File.WriteAllText(simple, "[Version]\r\nClass=System\r\nDriverVer=01/01/2025,1.0.0.0\r\n[Models]\r\n%Dev%=Install,PCI\\VEN_0001&DEV_0002\r\n[Strings]\r\nDev=Thing\r\n", new UTF8Encoding(false));
            var s = InfParser.Parse(simple, "Local");
            T(s.HardwareIds.Count == 1, "simpleHw=" + s.HardwareIds.Count);
            T(s.HardwareIds[0] == "PCI\\VEN_0001&DEV_0002", "simpleId=" + s.HardwareIds[0]);
        }

        private static void TestNormalizer()
        {
            T(HardwareIdNormalizer.Normalize("  pci\\ven_10ec&dev_8168 ") == "PCI\\VEN_10EC&DEV_8168", "normalize case/space");
            T(HardwareIdNormalizer.BusOf("USB\\VID_1") == "USB", "bus");
            T(HardwareIdNormalizer.IsHardwareId("PCI\\VEN_10EC&DEV_8168"), "isHw ven/dev");
            T(HardwareIdNormalizer.IsHardwareId("USB\\VID_1234&PID_5678"), "isHw vid/pid");
            T(!HardwareIdNormalizer.IsHardwareId("PCI\\CC_030000"), "isCmp cc");
            T(!HardwareIdNormalizer.IsHardwareId("USB\\Class_03"), "isCmp class");
            T(HardwareIdNormalizer.Specificity("PCI\\VEN_10EC&DEV_8168&SUBSYS_0001&REV_10") > HardwareIdNormalizer.Specificity("PCI\\VEN_10EC&DEV_8168"), "specificity subsys");
            T(HardwareIdNormalizer.Specificity("PCI\\VEN_10EC&DEV_8168") > HardwareIdNormalizer.Specificity("PCI\\CC_0200"), "specificity vs cc");
            T(HardwareIdNormalizer.CanonicalVenDev("PCI\\VEN_10EC&DEV_8168&SUBSYS_1") == "VEN_10EC&DEV_8168", "canonical");
            T(HardwareIdNormalizer.FromDeviceInstanceId("ACPI\\VEN_INT&DEV_34BB\\0") == "ACPI\\INT34BB", "ACPI instance fallback");
            T(HardwareIdNormalizer.CanonicalVenDev("USB\\VID_1234&PID_5678") == "VID_1234&PID_5678", "canonical usb");
        }

        private static void TestOsArchFilter()
        {
            var amd64 = new OsTarget { Architecture = "amd64", MajorVersion = 10 };
            var arm64 = new OsTarget { Architecture = "arm64", MajorVersion = 10 };

            var amd64Only = new InfRecord { Decorations = new List<string> { "NTamd64.10" } };
            T(OsArchFilter.Applicable(amd64Only, amd64), "amd64 inf on amd64");
            T(!OsArchFilter.Applicable(amd64Only, arm64), "amd64 inf on arm64");

            var armOnly = new InfRecord { Decorations = new List<string> { "NTarm64.10" } };
            T(!OsArchFilter.Applicable(armOnly, amd64), "arm64 inf on amd64");
            T(OsArchFilter.Applicable(armOnly, arm64), "arm64 inf on arm64");

            var universal = new InfRecord { Decorations = new List<string> { "" } };
            T(OsArchFilter.Applicable(universal, amd64), "universal on amd64");
            T(OsArchFilter.Applicable(universal, arm64), "universal on arm64");

            var win10Plus = new InfRecord { Decorations = new List<string> { "NTamd64.10" } };
            T(OsArchFilter.Applicable(win10Plus, new OsTarget { Architecture = "amd64", MajorVersion = 11 }), "win10 inf on win11");
            T(!OsArchFilter.Applicable(win10Plus, new OsTarget { Architecture = "amd64", MajorVersion = 6 }), "win10 inf on win7");

            T(OsArchFilter.ArchFromDecoration("NTamd64.10") == "amd64", "archFromDec");
            T(OsArchFilter.OsMajorFromDecoration("NTamd64.10") == 10, "osMajorFromDec");
            T(OsArchFilter.OsMajorFromDecoration("NTamd64.10.0...17735") == 10, "osMajor ignores min-build");
            T(OsArchFilter.Applicable(
                new InfRecord { Decorations = new List<string> { "NTamd64.10.0...17735", "NTamd64.10.0...21262" } },
                amd64), "Windows build decorations apply to Windows 10");
        }

        private static void TestRanker()
        {
            // Two INFs for the same device: one exact HWID (more specific + newer), one compatible ID only.
            var exact = Rec("a.inf", "Src", "03/14/2026", "2.0.0.0", new[] { "PCI\\VEN_10EC&DEV_8168&SUBSYS_0001&REV_10" }, new string[0]);
            var compat = Rec("b.inf", "Src", "01/01/2024", "1.0.0.0", new string[0], new[] { "PCI\\CC_0200" });
            var ranker = new MatchRanker(new[] { exact, compat }, new string[0]);
            var res = ranker.Rank(new[] { "PCI\\VEN_10EC&DEV_8168&SUBSYS_0001&REV_10" }, new[] { "PCI\\CC_0200" }, new OsTarget { Architecture = "amd64", MajorVersion = 10 });
            T(res.Count == 2, "rankCount=" + res.Count);
            T(res[0].Record.Path == "a.inf", "bestIsExact=" + res[0].Record.Path);
            T(res[0].Tier == 4, "bestTier=" + res[0].Tier);
            T(res[1].Tier == 1, "worstTier=" + res[1].Tier);

            // Specificity tiebreak among same-tier exact matches.
            var specific = Rec("s.inf", "Src", "03/14/2026", "1.0.0.0", new[] { "PCI\\VEN_10EC&DEV_8168&SUBSYS_0001&REV_10" }, new string[0]);
            var generic = Rec("g.inf", "Src", "03/14/2026", "1.0.0.0", new[] { "PCI\\VEN_10EC&DEV_8168" }, new string[0]);
            var r2 = new MatchRanker(new[] { generic, specific }, new string[0]);
            var res2 = r2.Rank(new[] { "PCI\\VEN_10EC&DEV_8168&SUBSYS_0001&REV_10" }, new string[0], new OsTarget { Architecture = "amd64", MajorVersion = 10 });
            T(res2[0].Record.Path == "s.inf", "specificityOrder=" + res2[0].Record.Path);

            // Date tiebreak: same device, newer DriverVer wins.
            var older = Rec("o.inf", "Src", "01/01/2024", "1.0.0.0", new[] { "PCI\\VEN_0001&DEV_0002" }, new string[0]);
            var newer = Rec("n.inf", "Src", "06/01/2025", "1.0.0.0", new[] { "PCI\\VEN_0001&DEV_0002" }, new string[0]);
            var r3 = new MatchRanker(new[] { older, newer }, new string[0]);
            var res3 = r3.Rank(new[] { "PCI\\VEN_0001&DEV_0002" }, new string[0], new OsTarget { Architecture = "amd64", MajorVersion = 10 });
            T(res3[0].Record.Path == "n.inf", "dateOrder=" + res3[0].Record.Path);

            // Model preference boosts an otherwise-equal candidate.
            var plain = Rec("p.inf", "All", "03/14/2026", "1.0.0.0", new[] { "PCI\\VEN_0003&DEV_0004" }, new string[0]);
            var model = Rec("m.inf", "ModelPackage", "03/14/2026", "1.0.0.0", new[] { "PCI\\VEN_0003&DEV_0004" }, new string[0]);
            var r4 = new MatchRanker(new[] { plain, model }, new[] { "ModelPackage" });
            var res4 = r4.Rank(new[] { "PCI\\VEN_0003&DEV_0004" }, new string[0], new OsTarget { Architecture = "amd64", MajorVersion = 10 });
            T(res4[0].Record.Path == "m.inf", "modelPref=" + res4[0].Record.Path);

            // Determinism: repeated ranking yields identical ordering.
            var a = r4.Rank(new[] { "PCI\\VEN_0003&DEV_0004" }, new string[0], new OsTarget { Architecture = "amd64", MajorVersion = 10 });
            var b = r4.Rank(new[] { "PCI\\VEN_0003&DEV_0004" }, new string[0], new OsTarget { Architecture = "amd64", MajorVersion = 10 });
            bool same = a.Count == b.Count;
            for (int i = 0; same && i < a.Count; i++) same = a[i].Record.Path == b[i].Record.Path;
            T(same, "determinism");

            // OS/arch filter excludes a non-applicable INF from ranking.
            var armOnly = Rec("arm.inf", "Src", "03/14/2026", "1.0.0.0", new[] { "PCI\\VEN_0005&DEV_0006" }, new string[0]);
            armOnly.Decorations = new List<string> { "NTarm64.10" };
            var r5 = new MatchRanker(new[] { armOnly }, new string[0]);
            var res5 = r5.Rank(new[] { "PCI\\VEN_0005&DEV_0006" }, new string[0], new OsTarget { Architecture = "amd64", MajorVersion = 10 });
            T(res5.Count == 0, "archExcluded=" + res5.Count);
        }

        private static InfRecord Rec(string path, string source, string date, string version, string[] hw, string[] cmp)
        {
            var r = new InfRecord { Path = path, SourceName = source, DriverVerDate = date, DriverVerVersion = version, Decorations = new List<string> { "" } };
            r.HardwareIds.AddRange(hw);
            r.CompatibleIds.AddRange(cmp);
            return r;
        }

        // ---------------------------------------------------------------------------

        private static void TestIncremental(string root)
        {
            string dir = PrepareDir(Path.Combine(root, "incremental"));
            string dbPath = Path.Combine(dir, "index.udadb");
            CleanDb(dbPath);

            WriteInf(dir, "d1.inf", "PCI\\VEN_0001&DEV_0001");
            WriteInf(dir, "d2.inf", "PCI\\VEN_0002&DEV_0002");

            using (var idx = new DriverIndex(dbPath))
            {
                idx.Open();
                T(idx.NeedsRebuild, "fresh needsRebuild");
                var r1 = idx.UpdateFromDirectory(dir, "Local", new OsTarget { Architecture = "amd64", MajorVersion = 10 });
                T(r1.Added == 2, "add=" + r1.Added);
                T(idx.Count == 2, "count=" + idx.Count);
                idx.Commit();
            }

            using (var idx = new DriverIndex(dbPath))
            {
                idx.Open();
                T(!idx.NeedsRebuild, "reopened not needsRebuild");
                T(idx.Count == 2, "reopenCount=" + idx.Count);
                var q = idx.Query(new[] { "PCI\\VEN_0001&DEV_0001" }, new string[0], new OsTarget { Architecture = "amd64", MajorVersion = 10 }, new string[0]);
                T(q.Count == 1 && q[0].Tier == 4, "query1=" + (q.Count == 0 ? "none" : q[0].Tier.ToString()));

                // Add a new INF.
                WriteInf(dir, "d3.inf", "PCI\\VEN_0003&DEV_0003");
                var r2 = idx.UpdateFromDirectory(dir, "Local", new OsTarget { Architecture = "amd64", MajorVersion = 10 });
                T(r2.Added == 1 && r2.Unchanged == 2, "add2=" + r2.Added + " unchanged=" + r2.Unchanged);
                T(idx.Count == 3, "count3=" + idx.Count);
                idx.Commit();
            }

            using (var idx = new DriverIndex(dbPath))
            {
                idx.Open();
                // Modify d1 (new content -> new fingerprint).
                WriteInf(dir, "d1.inf", "PCI\\VEN_0001&DEV_0001&SUBSYS_0099");
                var r3 = idx.UpdateFromDirectory(dir, "Local", new OsTarget { Architecture = "amd64", MajorVersion = 10 });
                T(r3.Modified == 1, "modified=" + r3.Modified);
                T(idx.Count == 3, "count3b=" + idx.Count);
                var q = idx.Query(new[] { "PCI\\VEN_0001&DEV_0001&SUBSYS_0099" }, new string[0], new OsTarget { Architecture = "amd64", MajorVersion = 10 }, new string[0]);
                T(q.Count == 1, "modifiedVisible=" + q.Count);
                idx.Commit();
            }

            using (var idx = new DriverIndex(dbPath))
            {
                idx.Open();
                // Delete d2.
                File.Delete(Path.Combine(dir, "d2.inf"));
                var r4 = idx.UpdateFromDirectory(dir, "Local", new OsTarget { Architecture = "amd64", MajorVersion = 10 });
                T(r4.Deleted == 1, "deleted=" + r4.Deleted);
                T(idx.Count == 2, "count2=" + idx.Count);
                var q = idx.Query(new[] { "PCI\\VEN_0002&DEV_0002" }, new string[0], new OsTarget { Architecture = "amd64", MajorVersion = 10 }, new string[0]);
                T(q.Count == 0, "deletedGone=" + q.Count);
                idx.Commit();
            }
        }

        private static void TestSchemaVersion(string root)
        {
            string dir = PrepareDir(Path.Combine(root, "schema"));
            string dbPath = Path.Combine(dir, "index.udadb");
            CleanDb(dbPath);
            WriteInf(dir, "s.inf", "PCI\\VEN_0007&DEV_0007");

            using (var idx = new DriverIndex(dbPath))
            {
                idx.Open();
                idx.UpdateFromDirectory(dir, "Local", new OsTarget { Architecture = "amd64", MajorVersion = 10 });
                idx.Commit();
            }
            // Patch the on-disk schema version to something stale (offset 8 = first int after the 8-byte magic).
            using (var fs = new FileStream(dbPath, FileMode.Open, FileAccess.Write, FileShare.None))
            using (var bw = new BinaryWriter(fs))
            {
                fs.Position = 8;
                bw.Write(DriverIndex.CurrentSchema - 1);
            }
            using (var idx = new DriverIndex(dbPath))
            {
                idx.Open();
                T(idx.NeedsRebuild, "stale schema needsRebuild");
                T(idx.SchemaVersionObserved == DriverIndex.CurrentSchema - 1, "observedSchema=" + idx.SchemaVersionObserved);
                // After update, the store is rebuilt cleanly even though the old records were stale.
                var r = idx.UpdateFromDirectory(dir, "Local", new OsTarget { Architecture = "amd64", MajorVersion = 10 });
                T(r.Rebuilt, "rebuilt");
                T(idx.Count == 1, "countAfterRebuild=" + idx.Count);
                idx.Commit();
            }
            using (var idx = new DriverIndex(dbPath))
            {
                idx.Open();
                T(!idx.NeedsRebuild, "post-rebuild current schema");
                T(idx.SchemaVersionObserved == DriverIndex.CurrentSchema, "post-schema=" + idx.SchemaVersionObserved);
            }
        }

        private static void TestTransaction(string root)
        {
            string dir = PrepareDir(Path.Combine(root, "txn"));
            string dbPath = Path.Combine(dir, "index.udadb");
            CleanDb(dbPath);
            WriteInf(dir, "t1.inf", "PCI\\VEN_0008&DEV_0008");
            WriteInf(dir, "t2.inf", "PCI\\VEN_0009&DEV_0009");

            using (var idx = new DriverIndex(dbPath))
            {
                idx.Open();
                idx.UpdateFromDirectory(dir, "Local", new OsTarget { Architecture = "amd64", MajorVersion = 10 });
                idx.Commit(); // first commit: File.Move (no .bak yet)
            }
            // A second commit flips via File.Replace, producing the .bak backup of the first version.
            using (var idx = new DriverIndex(dbPath))
            {
                idx.Open();
                idx.Commit();
            }
            T(File.Exists(dbPath + ".bak"), "bak exists");

            // Corrupt the live file; recovery should fall back to .bak.
            File.WriteAllBytes(dbPath, Encoding.ASCII.GetBytes("GARBAGE-corrupt-not-a-real-index-file"));
            using (var idx = new DriverIndex(dbPath))
            {
                idx.Open();
                T(idx.Count == 2, "recoveredFromBak=" + idx.Count);
                T(!idx.NeedsRebuild, "recoveredNotNeedsRebuild");
            }
            T(File.Exists(dbPath), "db restored from bak");

            // Stale .tmp must be ignored without affecting the live file.
            File.WriteAllBytes(dbPath + ".tmp", Encoding.ASCII.GetBytes("partial-uncommitted"));
            using (var idx = new DriverIndex(dbPath))
            {
                idx.Open();
                T(idx.Count == 2, "tmpIgnored=" + idx.Count);
            }
            T(!File.Exists(dbPath + ".tmp"), "tmp cleaned");
        }

        private static void TestPlanner(string root)
        {
            string dir = PrepareDir(Path.Combine(root, "planner"));
            string approved = Path.Combine(dir, "approved");
            Directory.CreateDirectory(approved);
            string outside = Path.Combine(dir, "outside");
            Directory.CreateDirectory(outside);

            string good1 = WriteInf(approved, "good1.inf", "PCI\\VEN_0010&DEV_0010");
            string good2 = WriteInf(approved, "good2.inf", "PCI\\VEN_0011&DEV_0011");
            string foreign = WriteInf(outside, "foreign.inf", "PCI\\VEN_0012&DEV_0012");

            var rec1 = InfParser.Parse(good1, "Local");
            var rec2 = InfParser.Parse(good2, "Local");
            var recForeign = InfParser.Parse(foreign, "Local");

            // Whitelist: foreign path rejected, accepted ones kept.
            var plan = InstallPlanner.BuildPlan(new[] { rec1, recForeign, rec2 }, new[] { approved }, false);
            T(plan.Accepted == 2, "accepted=" + plan.Accepted);
            T(plan.Skipped == 1, "skipped=" + plan.Skipped);
            T(plan.Items.First(i => i.InfPath == foreign).SkipReason == "outside approved source root", "foreignReason=" + plan.Items.First(i => i.InfPath == foreign).SkipReason);
            T(plan.Items.First(i => i.InfPath == good1).Order == 1, "order1");

            // Dedup by path: same candidate twice -> one accepted, one skipped.
            var dupPath = InstallPlanner.BuildPlan(new[] { rec1, rec1 }, new[] { approved }, false);
            T(dupPath.Accepted == 1 && dupPath.Skipped == 1, "dupPath a=" + dupPath.Accepted + " s=" + dupPath.Skipped);

            // Dedup by content: two INFs with identical content -> one accepted.
            string dupA = WriteInf(approved, "dupA.inf", "PCI\\VEN_0013&DEV_0013");
            string dupB = WriteInf(approved, "dupB.inf", "PCI\\VEN_0013&DEV_0013");
            // Make the two files byte-identical so their content hashes match.
            byte[] bytes = File.ReadAllBytes(dupA); File.WriteAllBytes(dupB, bytes);
            var recDupA = InfParser.Parse(dupA, "Local");
            var recDupB = InfParser.Parse(dupB, "Local");
            T(recDupA.ContentHash == recDupB.ContentHash, "sameHash");
            var dupContent = InstallPlanner.BuildPlan(new[] { recDupA, recDupB }, new[] { approved }, true);
            T(dupContent.Accepted == 1 && dupContent.Skipped == 1, "dupContent a=" + dupContent.Accepted + " s=" + dupContent.Skipped);

            // Argument safety: argument string is quoted, no embedded quotes.
            string args = InstallPlanner.BuildArguments(rec1.Path);
            T(args == "/add-driver \"" + rec1.Path + "\" /install", "args=" + args);

            // Exit-code classification.
            T(InstallPlanner.ClassifyExitCode(0, false) == InstallOutcome.Success, "ec0");
            T(InstallPlanner.ClassifyExitCode(3010, false) == InstallOutcome.SuccessReboot, "ec3010");
            T(InstallPlanner.ClassifyExitCode(259, false) == InstallOutcome.Skipped, "ec259");
            T(InstallPlanner.ClassifyExitCode(87, false) == InstallOutcome.InvalidParameter, "ec87");
            T(InstallPlanner.ClassifyExitCode(1, false) == InstallOutcome.Failure, "ec1");
            T(InstallPlanner.ClassifyExitCode(0, true) == InstallOutcome.Timeout, "ecTimeout");

            // A path with a quote character must be rejected.
            string evilPath = approved + "\\bad\".inf";
            var evil = new InfRecord { Path = evilPath, ContentHash = "x" };
            var evilPlan = InstallPlanner.BuildPlan(new[] { evil }, new[] { approved }, false);
            T(evilPlan.Accepted == 0, "evilAccepted=" + evilPlan.Accepted);
            T(evilPlan.Items[0].SkipReason != "", "evilReason set");

            // Empty approved roots -> everything rejected.
            var noRoots = InstallPlanner.BuildPlan(new[] { rec1 }, new string[0], false);
            T(noRoots.Accepted == 0, "noRootsAccepted=" + noRoots.Accepted);
        }

        private static void CleanDb(string dbPath)
        {
            foreach (var f in new[] { dbPath, dbPath + ".tmp", dbPath + ".bak" })
                if (File.Exists(f)) { try { File.Delete(f); } catch { } }
        }

        private static string PrepareDir(string dir)
        {
            if (Directory.Exists(dir)) { try { Directory.Delete(dir, true); } catch { } }
            Directory.CreateDirectory(dir);
            return dir;
        }

        private static string WriteInf(string dir, string name, string hwid)
        {
            string path = Path.Combine(dir, name);
            File.WriteAllText(path, "[Version]\r\nClass=System\r\nDriverVer=01/01/2026,1.0.0.0\r\n[Manufacturer]\r\n%M%=Models,NTamd64.10\r\n[Models.NTamd64.10]\r\n%D%=Install," + hwid + "\r\n[Strings]\r\n%M%=M\r\n%D%=Device\r\n", new UTF8Encoding(false));
            return path;
        }
    }
}
