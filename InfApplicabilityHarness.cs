// InfApplicabilityHarness.cs - automated tests for the reusable INF applicability engine.
// Compiled by Run-InfApplicabilityTests.ps1 alongside InfApplicability.cs + DriverIndexCore.cs.
// Offline; no pnputil calls, no UI, no install.
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace UDA
{
    internal static class InfApplicabilityHarness
    {
        private static int _asserts;
        private static int _failed;
        private static string _current;

        private static int Main(string[] args)
        {
            string root = args.Length > 0 ? Path.GetFullPath(args[0]) : Path.Combine(Path.GetTempPath(), "uda-inf-applicability-tests");
            Directory.CreateDirectory(root);

            Run("Architecture.x64", () => TestArchAmd64(root));
            Run("Architecture.x86", () => TestArchX86(root));
            Run("Architecture.Arm64", () => TestArchArm64(root));
            Run("Architecture.UniversalNT", () => TestUniversalNt(root));
            Run("OsVersion.MajorGate", () => TestMajorGate(root));
            Run("OsVersion.Nt6MinorAndFixedBuildSlots", () => TestMinorAndFixedBuildSlots(root));
            Run("OsVersion.BuildBoundaries", () => TestBuildBoundaries(root));
            Run("Ids.ExactHardwareIdPriority", () => TestExactHwidPriority(root));
            Run("Ids.CompatibleTiers", () => TestCompatibleTiers(root));
            Run("Ids.NoMatch", () => TestNoIdMatch(root));
            Run("Ids.AcpiStringTokens", () => {
                var rec = ParseInf(root, "lenovo-acpi.inf", "[Version]\nSignature=\"$Windows NT$\"\nClass=System\n[Manufacturer]\nLenovo=Models,NTamd64\n[Models.NTamd64]\n%Name%=Install,%PowerId%\n[Strings]\nName=Lenovo Power Management\nPowerId=\"ACPI\\LEN0068\"\n");
                var target = Target("amd64", 10, 19045);
                T(InfApplicability.Evaluate(rec, target, new[] { "ACPI\\VEN_LEN&DEV_0068" }, null).Applicable, "Expanded Lenovo ACPI ID matches resolved INF token");
                T(!InfApplicability.Evaluate(rec, target, new[] { "ACPI\\LEN9999" }, null).Applicable, "Unrelated ACPI device must not match");
                T(!InfApplicability.Evaluate(rec, Target("x86", 10, 19045), new[] { "ACPI\\LEN0068" }, null).Applicable, "ACPI match must retain architecture gate");
            });
            Run("DecoratedEntries.ActiveSectionOnly", () => {
                string hw = @"INTELAUDIO\FUNC_01&VEN_8086&DEV_2812&SUBSYS_80860101";
                string changed = @"INTELAUDIO\SGPC_FUNC_01&VEN_8086&DEV_2812&SUBSYS_80860101";
                string header = "[Version]\nSignature=\"$Windows NT$\"\nClass=MEDIA\n[Manufacturer]\nIntel=IntelGfx,NTamd64.10.0...18362,NTamd64.10.0...19041\n";
                string body = "[IntelGfx.NTamd64.10.0...18362]\nAudio=Install," + hw + "\n[IntelGfx.NTamd64.10.0...19041]\nAudio=Install," + changed + "\n";
                var rec = ParseInf(root, "active-audio.inf", header + body);
                T(InfApplicability.Evaluate(rec, Target("amd64", 10, 18363), new[] { hw }, null).Applicable, "older OS uses FUNC section");
                T(!InfApplicability.Evaluate(rec, Target("amd64", 10, 26300), new[] { hw }, null).Applicable, "new OS cannot borrow FUNC ID from inactive older section");
                T(InfApplicability.Evaluate(rec, Target("amd64", 10, 26300), new[] { changed }, null).Applicable, "active SGPC ID remains applicable");
                rec.ModelSections.Clear();
                T(!InfApplicability.Evaluate(rec, Target("amd64", 10, 26300), new[] { hw }, null).Applicable, "old database metadata also excludes inactive IDs without reindex");
                rec = ParseInf(root, "empty-audio.inf", header + "[IntelGfx.NTamd64.10.0...18362]\nAudio=Install," + hw + "\n[IntelGfx.NTamd64.10.0...19041]\n");
                T(!InfApplicability.Evaluate(rec, Target("amd64", 10, 26300), new[] { hw }, null).Applicable, "empty newer Models section blocks fallback");
                rec = ParseInf(root, "two-manufacturers.inf", header.Replace("Intel=", "Other=Other,NTamd64.10.0...22000\nIntel=") + body + "[Other.NTamd64.10.0...22000]\nOther=Install,PCI\\VEN_1234&DEV_5678\n");
                T(InfApplicability.Evaluate(rec, Target("amd64", 10, 26300), new[] { changed }, null).Applicable, "separate manufacturer section does not suppress Intel group");
            });
            Run("DecoratedEntries.PerEntryGate", () => TestPerEntryDecoration(root));
            Run("DecoratedEntries.DottedManufacturerBase", () => {
                var rec = ParseInf(root, "dotted-base.inf", "[Version]\nSignature=\"$Windows NT$\"\nClass=System\n[Manufacturer]\nLenovo=ibm.Mfg,NTx86,NTamd64\n[ibm.Mfg.NTx86]\nPower=Install,ACPI\\LEN0268\n[ibm.Mfg.NTamd64]\nPower=Install,ACPI\\LEN0268\n[ibm.Mfg.NTamd64.Services]\nFake=Install,ACPI\\LEN9999\n[ibm.Mfg.NTarm64]\nFake=Install,ACPI\\LEN9999\n");
                T(rec.Decorations.Contains("NTamd64"), "Dotted base preserves amd64 decoration");
                T(rec.Decorations.Contains("NTx86"), "Dotted base preserves x86 decoration");
                T(rec.Models.Count == 2, "Only declared models are parsed, not services or undeclared targets");
                T(InfApplicability.Evaluate(rec, Target("amd64", 10, 19045), new[] { "ACPI\\LEN0268" }, null).Applicable, "Lenovo amd64 matches");
                T(!InfApplicability.Evaluate(rec, Target("arm64", 10, 22621), new[] { "ACPI\\LEN0268" }, null).Applicable, "Wrong architecture rejected");
                T(!InfApplicability.Evaluate(rec, Target("amd64", 10, 19045), new[] { "ACPI\\LEN9999" }, null).Applicable, "Unrelated IDs in non-model sections rejected");
                var bare = ParseInf(root, "dotted-bare.inf", "[Version]\nSignature=\"$Windows NT$\"\n[Manufacturer]\nLenovo=ibm.Mfg\n[ibm.Mfg]\nPower=Install,ACPI\\LEN0268\n");
                T(InfApplicability.Evaluate(bare, Target("amd64", 10, 19045), new[] { "ACPI\\LEN0268" }, null).Applicable, "Undecorated dotted base remains universal");
            });
            Run("OsOnly.Overload", () => TestOsOnlyOverload(root));
            Run("RecordLevel.AgreesWithOsArchFilter", () => TestAgreementWithOsArchFilter(root));

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

        private static OsTarget Target(string arch, int major, int build)
        {
            return new OsTarget { Architecture = arch, MajorVersion = major, BuildNumber = build };
        }

        private static InfRecord ParseInf(string dir, string fileName, string content)
        {
            string path = Path.Combine(dir, fileName);
            File.WriteAllText(path, content, new UTF8Encoding(false));
            return InfParser.Parse(path, "Test");
        }

        private const string Amd64Inf =
            "[Version]\r\n" +
            "Signature=\"$Windows NT$\"\r\n" +
            "Class=Net\r\n" +
            "Provider=Acme\r\n" +
            "DriverVer=03/14/2026,12.34.56.78\r\n" +
            "[Manufacturer]\r\n" +
            "%Mfg%=Models,NTamd64.10\r\n" +
            "[Models.NTamd64.10]\r\n" +
            "%Dev%=Inst,PCI\\VEN_10EC&DEV_8168&REV_10,PCI\\CC_0200\r\n" +
            "[Strings]\r\n" +
            "Mfg=\"Acme\"\r\n" +
            "Dev=\"Acme Adapter\"\r\n";

        // --- architecture coverage ------------------------------------------------

        private static void TestArchAmd64(string root)
        {
            var rec = ParseInf(PrepareDir(Path.Combine(root, "amd64")), "amd64.inf", Amd64Inf);
            var ok = InfApplicability.Evaluate(rec, Target("amd64", 10, 19045), new[] { "PCI\\VEN_10EC&DEV_8168&REV_10" }, null);
            T(ok.Applicable, "amd64 INF must apply on amd64: " + ok.Reason);
            T(ok.MatchLevel == InfMatchLevel.HardwareId, "level=" + ok.MatchLevel);
            T(ok.MatchedDecoration == "NTamd64.10", "dec=" + ok.MatchedDecoration);

            foreach (string wrong in new[] { "x86", "arm64", "arm" })
            {
                var r = InfApplicability.Evaluate(rec, Target(wrong, 10, 19045), new[] { "PCI\\VEN_10EC&DEV_8168&REV_10" }, null);
                T(!r.Applicable, "amd64 INF must reject on " + wrong);
                T(r.ReasonCode == "ArchitectureMismatch", wrong + " code=" + r.ReasonCode);
                T(r.Reason.Length > 0, wrong + " needs a reason");
            }
        }

        private static void TestArchX86(string root)
        {
            string inf = Amd64Inf.Replace("NTamd64.10", "NTx86");
            var rec = ParseInf(PrepareDir(Path.Combine(root, "x86")), "x86.inf", inf);
            var ok = InfApplicability.Evaluate(rec, Target("x86", 10, 19045), new[] { "PCI\\VEN_10EC&DEV_8168&REV_10" }, null);
            T(ok.Applicable, "x86 INF must apply on x86: " + ok.Reason);
            var bad = InfApplicability.Evaluate(rec, Target("amd64", 10, 19045), new[] { "PCI\\VEN_10EC&DEV_8168&REV_10" }, null);
            T(!bad.Applicable && bad.ReasonCode == "ArchitectureMismatch", "x86 INF must reject on amd64");
        }

        private static void TestArchArm64(string root)
        {
            string inf = Amd64Inf.Replace("NTamd64.10", "NTarm64");
            var rec = ParseInf(PrepareDir(Path.Combine(root, "arm64")), "arm64.inf", inf);
            var ok = InfApplicability.Evaluate(rec, Target("arm64", 10, 22000), new[] { "PCI\\VEN_10EC&DEV_8168&REV_10" }, null);
            T(ok.Applicable, "arm64 INF must apply on arm64: " + ok.Reason);
            var bad = InfApplicability.Evaluate(rec, Target("amd64", 10, 22000), new[] { "PCI\\VEN_10EC&DEV_8168&REV_10" }, null);
            T(!bad.Applicable && bad.ReasonCode == "ArchitectureMismatch", "arm64 INF must reject on amd64");
            // NTarm does NOT cover arm64.
            string arm = Amd64Inf.Replace("NTamd64.10", "NTarm");
            var recArm = ParseInf(PrepareDir(Path.Combine(root, "arm64")), "arm.inf", arm);
            var bad2 = InfApplicability.Evaluate(recArm, Target("arm64", 10, 22000), null, null);
            T(!bad2.Applicable && bad2.ReasonCode == "ArchitectureMismatch", "NTarm must not cover arm64");
        }

        private static void TestUniversalNt(string root)
        {
            string dir = PrepareDir(Path.Combine(root, "universal"));
            // Bare (undecorated) Models section: applies everywhere.
            var bare = ParseInf(dir, "bare.inf",
                "[Version]\r\nSignature=\"$Windows NT$\"\r\nClass=Net\r\n[Manufacturer]\r\n%Mfg%=Models\r\n[Models]\r\n%Dev%=Inst,PCI\\VEN_10EC&DEV_8168\r\n[Strings]\r\nMfg=\"Acme\"\r\nDev=\"A\"\r\n");
            foreach (string arch in new[] { "amd64", "x86", "arm64" })
            {
                var r = InfApplicability.Evaluate(bare, Target(arch, 10, 19045), new[] { "PCI\\VEN_10EC&DEV_8168" }, null);
                T(r.Applicable, "bare Models applies on " + arch + ": " + r.ReasonCode);
                T(r.MatchLevel == InfMatchLevel.HardwareId, "bare level " + arch + "=" + r.MatchLevel);
            }
            // Decorated "Models.NT" (any architecture, NT platform).
            var nt = ParseInf(dir, "nt.inf",
                "[Version]\r\nSignature=\"$Windows NT$\"\r\nClass=Net\r\n[Manufacturer]\r\n%Mfg%=Models,NT\r\n[Models.NT]\r\n%Dev%=Inst,PCI\\VEN_10EC&DEV_8168\r\n[Strings]\r\nMfg=\"Acme\"\r\nDev=\"A\"\r\n");
            foreach (string arch in new[] { "amd64", "x86", "arm64" })
            {
                var r = InfApplicability.Evaluate(nt, Target(arch, 10, 19045), new[] { "PCI\\VEN_10EC&DEV_8168" }, null);
                T(r.Applicable, "Models.NT applies on " + arch + ": " + r.ReasonCode);
            }
        }

        // --- OS major / build gates ------------------------------------------------

        private static void TestMajorGate(string root)
        {
            var rec = ParseInf(PrepareDir(Path.Combine(root, "major")), "major.inf", Amd64Inf);
            // NTamd64.10 is a minimum: applies on major >= 10 (Win10/Win11 both report major 10).
            T(InfApplicability.Evaluate(rec, Target("amd64", 10, 19045), null, null).Applicable, "major 10 ok");
            T(InfApplicability.Evaluate(rec, Target("amd64", 10, 22631), null, null).Applicable, "Win11 build ok");
            // Wrong Windows major: decoration requires 10, target reports 6.1 (Win7) -> reject.
            var r = InfApplicability.Evaluate(rec, Target("amd64", 6, 7601), null, null);
            T(!r.Applicable, "major 6 must reject NTamd64.10");
            T(r.ReasonCode == "OsVersionTooLow", "code=" + r.ReasonCode);
            // Decoration naming a newer major than the target -> reject.
            string newer = Amd64Inf.Replace("NTamd64.10", "NTamd64.11");
            var rec11 = ParseInf(PrepareDir(Path.Combine(root, "major")), "m11.inf", newer);
            var r2 = InfApplicability.Evaluate(rec11, Target("amd64", 10, 19045), null, null);
            T(!r2.Applicable && r2.ReasonCode == "OsVersionTooLow", "NTamd64.11 rejects major 10: " + r2.ReasonCode);
            // NTamd64.6.1 (Win7-era driver) still applies on 10.
            string legacy = Amd64Inf.Replace("NTamd64.10", "NTamd64.6.1");
            var recLegacy = ParseInf(PrepareDir(Path.Combine(root, "major")), "m61.inf", legacy);
            var r3 = InfApplicability.Evaluate(recLegacy, Target("amd64", 10, 19045), null, null);
            T(r3.Applicable, "NTamd64.6.1 applies on major 10: " + r3.ReasonCode);
            // The minor version of 6.1 must not be read as a build number.
            var r4 = InfApplicability.Evaluate(recLegacy, Target("amd64", 10, 7601), null, null);
            T(r4.Applicable, "NTamd64.6.1 minor '1' is not a build gate: " + r4.ReasonCode);
        }

        private static void TestBuildBoundaries(string root)
        {
            string dir = PrepareDir(Path.Combine(root, "build"));
            // Win11-only driver: TargetOSVersion build 22000 (first Win11 build).
            string inf = Amd64Inf.Replace("NTamd64.10", "NTamd64.10.0...22000");
            var rec = ParseInf(dir, "w11.inf", inf);
            T(rec.Decorations.Contains("NTamd64.10.0...22000"), "decoration parsed");
            // Windows 10 build 19045 (below lower bound) -> reject.
            var low = InfApplicability.Evaluate(rec, Target("amd64", 10, 19045), new[] { "PCI\\VEN_10EC&DEV_8168&REV_10" }, null);
            T(!low.Applicable, "Win10 19045 below build 22000 must reject");
            T(low.ReasonCode == "BuildTooLow", "code=" + low.ReasonCode);
            // Exactly at the lower bound -> accept.
            var at = InfApplicability.Evaluate(rec, Target("amd64", 10, 22000), new[] { "PCI\\VEN_10EC&DEV_8168&REV_10" }, null);
            T(at.Applicable, "build 22000 exactly at minimum must apply: " + at.Reason);
            // Win10-era driver (no build in decoration) applies on any build above.
            string w10 = Amd64Inf.Replace("NTamd64.10", "NTamd64.10.0");
            var rec10 = ParseInf(dir, "w10.inf", w10);
            T(InfApplicability.Evaluate(rec10, Target("amd64", 10, 17763), null, null).Applicable, "no-build decoration applies on 17763");
            T(InfApplicability.Evaluate(rec10, Target("amd64", 10, 22631), null, null).Applicable, "no-build decoration applies on 22631");
            // Unknown target build (0): build gate skipped (pnputil remains the authority).
            var unknown = InfApplicability.Evaluate(rec, Target("amd64", 10, 0), null, null);
            T(!unknown.Applicable && unknown.ReasonCode == "BuildUnknown", "unknown build rejects build-restricted driver with precise reason");
        }

        private static void TestMinorAndFixedBuildSlots(string root)
        {
            string[] decorations = { "NTamd64.6.1", "NTamd64.6.2", "NTamd64.6.3", "NTamd64.10.0" };
            int[] builds = { 7601, 9200, 9600, 19045 };
            for (int required = 0; required < decorations.Length; required++)
            {
                var rec = ParseInf(root, "minor-" + required + ".inf", Amd64Inf.Replace("NTamd64.10", decorations[required]));
                for (int actual = 0; actual < builds.Length; actual++)
                {
                    var target = Target("amd64", actual == 3 ? 10 : 6, builds[actual]);
                    bool expected = actual >= required;
                    T(InfApplicability.Evaluate(rec, target, new[] { "PCI\\VEN_10EC&DEV_8168&REV_10" }, null).Applicable == expected, decorations[required] + " on build " + builds[actual]);
                    T(OsArchFilter.Applicable(rec, target) == expected, "coarse minor gate " + decorations[required] + " on " + builds[actual]);
                }
            }
            var win8 = ParseInf(root, "win8-unknown.inf", Amd64Inf.Replace("NTamd64.10", "NTamd64.6.2"));
            T(!InfApplicability.Evaluate(win8, Target("amd64", 6, 0)).Applicable, "unknown NT6 minor must not prove Win8 support");
            T(!InfApplicability.Evaluate(win8, new OsTarget { Architecture = "amd64", MajorVersion = 6, MinorVersion = 1, BuildNumber = 0 }).Applicable, "explicit Windows7 minor rejects Win8 without build");
            T(InfApplicability.Evaluate(win8, new OsTarget { Architecture = "amd64", MajorVersion = 6, MinorVersion = 2, BuildNumber = 0 }).Applicable, "explicit Windows8 minor accepted");
            foreach (string decoration in new[] { "NTamd64.10.0.1.0.22000", "NTamd64.10.0...22000", "NTamd64.10.0.1..22000" })
            {
                var rec = ParseInf(root, "build-slots-" + decoration + ".inf", Amd64Inf.Replace("NTamd64.10", decoration));
                T(OsArchFilter.MinBuildFromDecoration(decoration) == 22000, "fixed build slot " + decoration);
                T(!InfApplicability.Evaluate(rec, Target("amd64", 10, 19045)).Applicable, "Win11 build gate with populated fields " + decoration);
                T(InfApplicability.Evaluate(rec, Target("amd64", 10, 22000)).Applicable, "Win11 minimum included " + decoration);
            }
            T(OsArchFilter.MinBuildFromDecoration("NTamd64.6.1..1") == 0, "SuiteMask is not BuildNumber");
            T(OsArchFilter.MinBuildFromDecoration("NTamd64.10.0...22000.1") == 22000, "trailing token does not replace build");
            // A mixed INF must not borrow Windows7 applicability from an unrelated device's model.
            var mixed = ParseInf(root, "mixed-os-devices.inf", "[Version]\nSignature=\"$Windows NT$\"\n[Manufacturer]\nM=Models,NTamd64.6.1,NTamd64.6.3\n[Models.NTamd64.6.1]\nOld=Install,PCI\\VEN_1111&DEV_1111\n[Models.NTamd64.6.3]\nNew=Install,PCI\\VEN_2222&DEV_2222\n");
            T(!InfApplicability.Evaluate(mixed, Target("amd64", 6, 7601), new[] { "PCI\\VEN_2222&DEV_2222" }, null).Applicable, "modern device does not inherit other device Win7 support");
        }

        // --- ID matching -----------------------------------------------------------

        private static void TestExactHwidPriority(string root)
        {
            string dir = PrepareDir(Path.Combine(root, "ids"));
            var rec = ParseInf(dir, "ids.inf",
                "[Version]\r\nSignature=\"$Windows NT$\"\r\nClass=Net\r\n[Manufacturer]\r\n%Mfg%=Models,NTamd64\r\n" +
                "[Models.NTamd64]\r\n" +
                "%Dev%=InstA,PCI\\VEN_10EC&DEV_8168&SUBSYS_1111AAAA&REV_10,PCI\\CC_0200\r\n" +
                "[Strings]\r\nMfg=\"Acme\"\r\nDev=\"A\"\r\n");
            // Device reports both the exact SUBSYS hardware ID and the generic class compatible ID.
            // Exact hardware ID must win over the compatible-ID match.
            var devHw = new[] { "PCI\\VEN_10EC&DEV_8168&SUBSYS_1111AAAA&REV_10", "PCI\\VEN_10EC&DEV_8168" };
            var devCmp = new[] { "PCI\\CC_0200" };
            var r = InfApplicability.Evaluate(rec, Target("amd64", 10, 19045), devHw, devCmp);
            T(r.Applicable, "must apply: " + r.Reason);
            T(r.MatchLevel == InfMatchLevel.HardwareId, "exact hwid wins, level=" + r.MatchLevel);
            T(r.MatchedInfId == "PCI\\VEN_10EC&DEV_8168&SUBSYS_1111AAAA&REV_10", "matched=" + r.MatchedInfId);
            T(r.MatchedInstallSection == "InstA", "section=" + r.MatchedInstallSection);
        }

        private static void TestCompatibleTiers(string root)
        {
            string dir = PrepareDir(Path.Combine(root, "tiers"));
            var rec = ParseInf(dir, "tiers.inf",
                "[Version]\r\nSignature=\"$Windows NT$\"\r\nClass=Net\r\n[Manufacturer]\r\n%Mfg%=Models,NTamd64\r\n" +
                "[Models.NTamd64]\r\n" +
                "%Dev%=InstA,PCI\\VEN_10EC&DEV_8168&REV_03,PCI\\CC_0200\r\n" +
                "[Strings]\r\nMfg=\"Acme\"\r\nDev=\"A\"\r\n");
            // Device hardware ID not listed, but matches the INF compatible ID -> tier 3.
            var r3 = InfApplicability.Evaluate(rec, Target("amd64", 10, 19045),
                new[] { "PCI\\VEN_10EC&DEV_9999&REV_01", "PCI\\CC_0200" }, null);
            T(r3.Applicable && r3.MatchLevel == InfMatchLevel.HardwareIdVsCompatibleId, "devHw vs infCmp level=" + r3.MatchLevel + " " + r3.Reason);
            T(r3.MatchedInfId == "PCI\\CC_0200", "matched3=" + r3.MatchedInfId);
            // Device compatible ID matching INF hardware ID -> tier 2.
            var r2 = InfApplicability.Evaluate(rec, Target("amd64", 10, 19045),
                null, new[] { "PCI\\VEN_10EC&DEV_8168&REV_03" });
            T(r2.Applicable && r2.MatchLevel == InfMatchLevel.CompatibleIdVsHardwareId, "devCmp vs infHw level=" + r2.MatchLevel);
            // Device compatible ID matching INF compatible ID -> tier 1.
            var r1 = InfApplicability.Evaluate(rec, Target("amd64", 10, 19045),
                new[] { "PCI\\VEN_10EC&DEV_9999" }, new[] { "PCI\\CC_0200" });
            T(r1.Applicable && r1.MatchLevel == InfMatchLevel.CompatibleId, "devCmp vs infCmp level=" + r1.MatchLevel);
            // Exact hardware ID beats a compatible-ID match on the same INF.
            var best = InfApplicability.Evaluate(rec, Target("amd64", 10, 19045),
                new[] { "PCI\\VEN_10EC&DEV_8168&REV_03" }, new[] { "PCI\\CC_0200" });
            T(best.MatchLevel == InfMatchLevel.HardwareId, "exact beats compatible, level=" + best.MatchLevel);
        }

        private static void TestNoIdMatch(string root)
        {
            var rec = ParseInf(PrepareDir(Path.Combine(root, "nomatch")), "nomatch.inf", Amd64Inf);
            var r = InfApplicability.Evaluate(rec, Target("amd64", 10, 19045),
                new[] { "PCI\\VEN_1111&DEV_2222" }, new[] { "PCI\\CC_0700" });
            T(!r.Applicable, "unmatched IDs must reject");
            T(r.ReasonCode == "NoMatchingModelEntry", "code=" + r.ReasonCode);
            T(r.Reason.Length > 0, "needs reason");
            // No Models entries at all (synthetic record) -> NoModels.
            var empty = new InfRecord { Path = "x", SourceName = "T" };
            var r2 = InfApplicability.Evaluate(empty, Target("amd64", 10, 19045), null, null);
            T(!r2.Applicable && r2.ReasonCode == "NoModels", "no models code=" + r2.ReasonCode);
            // Null record.
            var r3 = InfApplicability.Evaluate(null, Target("amd64", 10, 19045));
            T(!r3.Applicable && r3.ReasonCode == "NullRecord", "null record code=" + r3.ReasonCode);
        }

        // --- per-entry decoration gating ------------------------------------------

        private static void TestPerEntryDecoration(string root)
        {
            string dir = PrepareDir(Path.Combine(root, "perentry"));
            // Dual-arch INF: only the entry whose decoration matches may serve the target.
            var rec = ParseInf(dir, "dual.inf",
                "[Version]\r\nSignature=\"$Windows NT$\"\r\nClass=Net\r\n[Manufacturer]\r\n%Mfg%=Models,NTamd64,NTx86\r\n" +
                "[Models.NTamd64]\r\n%Dev64%=InstA,PCI\\VEN_10EC&DEV_8168\r\n" +
                "[Models.NTx86]\r\n%Dev86%=InstB,PCI\\VEN_10EC&DEV_8168\r\n" +
                "[Strings]\r\nMfg=\"Acme\"\r\nDev64=\"A64\"\r\nDev86=\"A86\"\r\n");
            T(rec.Models.Count == 2, "two entries, got " + rec.Models.Count);
            var on64 = InfApplicability.Evaluate(rec, Target("amd64", 10, 19045), new[] { "PCI\\VEN_10EC&DEV_8168" }, null);
            T(on64.Applicable && on64.MatchedInstallSection == "InstA", "amd64 target uses InstA: " + on64.MatchedInstallSection);
            T(on64.MatchedDecoration == "NTamd64", "dec=" + on64.MatchedDecoration);
            var on86 = InfApplicability.Evaluate(rec, Target("x86", 10, 19045), new[] { "PCI\\VEN_10EC&DEV_8168" }, null);
            T(on86.Applicable && on86.MatchedInstallSection == "InstB", "x86 target uses InstB: " + on86.MatchedInstallSection);
            // The x86 entry's decoration must not let an arm64 target through.
            var onArm = InfApplicability.Evaluate(rec, Target("arm64", 10, 22000), new[] { "PCI\\VEN_10EC&DEV_8168" }, null);
            T(!onArm.Applicable && onArm.ReasonCode == "ArchitectureMismatch", "arm64 rejected: " + onArm.ReasonCode);
        }

        // --- OS-only overload + agreement with OsArchFilter -------------------------

        private static void TestOsOnlyOverload(string root)
        {
            var rec = ParseInf(PrepareDir(Path.Combine(root, "osonly")), "os.inf", Amd64Inf);
            var ok = InfApplicability.Evaluate(rec, Target("amd64", 10, 19045));
            T(ok.Applicable, "OS-only applicable: " + ok.Reason);
            T(ok.MatchLevel == InfMatchLevel.None, "no IDs -> no level, got " + ok.MatchLevel);
            var bad = InfApplicability.Evaluate(rec, Target("x86", 10, 19045));
            T(!bad.Applicable && bad.ReasonCode == "ArchitectureMismatch", "OS-only reject");
            // Empty device ID lists behave like the OS-only overload.
            var empty = InfApplicability.Evaluate(rec, Target("amd64", 10, 19045), new string[0], new string[0]);
            T(empty.Applicable, "empty ID lists = OS-only: " + empty.Reason);
        }

        private static void TestAgreementWithOsArchFilter(string root)
        {
            // The engine's OS/arch gates must agree with the existing OsArchFilter so scan
            // (candidate stage) and install (revalidation stage) can never diverge.
            var rnd = new Random(20260928);
            string[] arches = { "amd64", "x86", "arm64", "arm" };
            string[] decs = { "NT", "NTamd64", "NTx86", "NTarm64", "NTarm", "NTamd64.10", "NTx86.6.1", "NTamd64.10.0...22000", "NTarm64.10.0...22000", "" };
            for (int i = 0; i < 400; i++)
            {
                var rec = new InfRecord { Path = "r" + i, SourceName = "Fuzz" };
                int n = rnd.Next(1, 4);
                for (int d = 0; d < n; d++) rec.Decorations.Add(decs[rnd.Next(decs.Length)]);
                var target = Target(arches[rnd.Next(arches.Length)], rnd.Next(6, 12), rnd.Next(0, 26000));
                bool filter = OsArchFilter.Applicable(rec, target);
                var r = InfApplicability.Evaluate(rec, target);
                T(filter == r.Applicable,
                    "divergence: filter=" + filter + " engine=" + r.Applicable + " (" + r.ReasonCode + ") target=" +
                    target.Architecture + "/" + target.MajorVersion + "/" + target.BuildNumber +
                    " decs=[" + string.Join(",", rec.Decorations.ToArray()) + "]");
            }
        }

        private static string PrepareDir(string dir)
        {
            Directory.CreateDirectory(dir);
            return dir;
        }
    }
}
