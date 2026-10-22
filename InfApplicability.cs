// InfApplicability.cs — reusable INF candidate applicability engine.
// Compiled by the in-box CSharpCodeProvider (C# 5.0). No third-party dependencies.
//
// Single source of truth for "does this INF candidate apply to this OS/device?".
// Designed to be called from two places without divergence:
//   1. the server-side candidate stage (rank/filter what to show), and
//   2. the pre-install revalidation stage (final gate before pnputil).
// Pure and offline: takes a parsed UDA.InfRecord + UDA.OsTarget + the device's ID
// lists, returns a structured verdict (match level + explicit rejection reason).
// It never installs anything and never touches pnputil.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace UDA
{
    /// <summary>
    /// Match specificity level, aligned with MatchScore.MatchTypeRank so results can be
    /// compared/ranked consistently with the existing MatchRanker tiers.
    /// 4 = device hardware ID matched an INF hardware ID (best, exact).
    /// 3 = device hardware ID matched an INF compatible ID.
    /// 2 = device compatible ID matched an INF hardware ID.
    /// 1 = device compatible ID matched an INF compatible ID.
    /// 0 = no ID match (only meaningful when the verdict is "not applicable").
    /// </summary>
    public enum InfMatchLevel
    {
        None = 0,
        CompatibleId = 1,
        CompatibleIdVsHardwareId = 2,
        HardwareIdVsCompatibleId = 3,
        HardwareId = 4
    }

    /// <summary>Structured verdict of an applicability evaluation.</summary>
    public sealed class InfApplicabilityResult
    {
        public bool Applicable;
        /// <summary>Machine-readable rejection code; "" when applicable. Codes:
        /// NullRecord, NoModels, NoApplicableDecoration, ArchitectureMismatch,
        /// OsVersionTooLow, BuildTooLow, NoDeviceIds, NoMatchingModelEntry.</summary>
        public string ReasonCode = "";
        /// <summary>Human-readable (bilingual) rejection reason; "" when applicable.</summary>
        public string Reason = "";
        public InfMatchLevel MatchLevel = InfMatchLevel.None;
        public string MatchedDeviceId = "";
        public string MatchedInfId = "";
        public string MatchedDecoration = "";
        public string MatchedModelDescription = "";
        public string MatchedInstallSection = "";

        public static InfApplicabilityResult Reject(string code, string reason)
        {
            return new InfApplicabilityResult { Applicable = false, ReasonCode = code ?? "", Reason = reason ?? "" };
        }
    }

    /// <summary>
    /// Reusable candidate applicability judgment for a single INF against a target OS and
    /// (optionally) a device's hardware/compatible ID lists. Enforces, in order:
    ///   1. the INF has at least one usable decorated [Models.*] entry;
    ///   2. at least one decoration matches the target architecture;
    ///   3. at least one decoration matches the target Windows major version (minimum semantics:
    ///      a decoration naming major 6.1 still applies on 10; one naming a higher major rejects);
    ///   4. at least one decoration's TargetOSVersion minimum build (NTamd64.10.0...22000 style)
    ///      is satisfied by the target build (unknown build cannot prove compatibility);
    ///   5. when device IDs are supplied, at least one model entry whose own decoration is
    ///      applicable matches a device hardware/compatible ID, exact hardware ID ranking first.
    /// OS/architecture gates use the same rule as OsArchFilter so scan and install agree.
    /// </summary>
    public static class InfApplicability
    {
        /// <summary>OS/architecture-only check (no device IDs): true when some decoration applies.</summary>
        public static InfApplicabilityResult Evaluate(InfRecord record, OsTarget target)
        {
            return Evaluate(record, target, null, null);
        }

        /// <summary>Full check including device ID matching. Device ID lists follow the PnP order
        /// (most specific first); null/empty lists mean "OS-only check" (ID gate skipped).</summary>
        public static InfApplicabilityResult Evaluate(InfRecord record, OsTarget target,
            IEnumerable<string> deviceHardwareIds, IEnumerable<string> deviceCompatibleIds)
        {
            if (record == null)
                return InfApplicabilityResult.Reject("NullRecord", "候选 INF 记录为空 / candidate INF record is null");
            if (target == null)
                target = new OsTarget();

            // ----- decoration gates (architecture, OS major/minor, minimum build) -----
            var decorations = record.Decorations ?? new List<string>();
            bool anyUsable = false;
            string bestApplied = null; // most specific applicable decoration, for reporting
            int bestRank = -1;
            foreach (var dec in decorations)
            {
                string arch; int major, minBuild;
                ParseDecoration(dec, out arch, out major, out minBuild);

                if (OsArchFilter.DecorationRejection(dec, target, false).Length > 0) continue;
                anyUsable = true;
                // Rank applicable decorations: explicit arch > universal NT, higher named major > none.
                int rank = (arch != "any" ? 2 : 0) + (major > 0 ? 1 : 0);
                if (rank > bestRank) { bestRank = rank; bestApplied = dec ?? ""; }
            }

            if (!anyUsable)
            {
                // Distinguish "INF has no model sections at all" from "decorations exist but none apply".
                if (record.Models == null || record.Models.Count == 0)
                {
                    // A record that carries decorations but no Models (synthetic decorations-only
                    // record, or a legacy index record whose Models were not backfilled) is still
                    // rejected — but with the precise per-decoration reason when one exists, so
                    // callers never see a bare "NoModels" for a decoration-shaped rejection.
                    var noModelCodes = new List<string>();
                    foreach (var dec in decorations)
                    {
                        string arch; int major, minBuild; string code;
                        ParseDecoration(dec, out arch, out major, out minBuild);
                        code = OsArchFilter.DecorationRejection(dec, target, false);
                        if (code.Length > 0 && noModelCodes.IndexOf(code) < 0) noModelCodes.Add(code);
                    }
                    if (noModelCodes.Count > 0)
                        return InfApplicabilityResult.Reject(string.Join(",", noModelCodes.ToArray()),
                            "INF 装饰节不适用当前系统（" + noModelCodes[0] + "）/ INF decoration does not apply to this OS (" + noModelCodes[0] + "): " +
                            "arch=" + (target.Architecture ?? "") +
                            " osMajor=" + target.MajorVersion.ToString(CultureInfo.InvariantCulture) +
                            " build=" + target.BuildNumber.ToString(CultureInfo.InvariantCulture) +
                            " infDecorations=[" + string.Join(",", decorations.ToArray()) + "]");
                    return InfApplicabilityResult.Reject("NoModels",
                        "INF 没有可用的 Models 条目 / INF has no usable Models entries");
                }
                var codes = new List<string>();
                foreach (var dec in decorations)
                {
                    string arch; int major, minBuild; string code;
                    ParseDecoration(dec, out arch, out major, out minBuild);
                    code = OsArchFilter.DecorationRejection(dec, target, false);
                    if (code.Length > 0 && codes.IndexOf(code) < 0) codes.Add(code);
                }
                string why = codes.Count > 0 ? string.Join(",", codes.ToArray()) : "NoApplicableDecoration";
                return InfApplicabilityResult.Reject(why,
                    "INF 装饰节不适用当前系统（" + why + "）/ INF decoration does not apply to this OS (" + why + "): " +
                    "arch=" + (target.Architecture ?? "") +
                    " osMajor=" + target.MajorVersion.ToString(CultureInfo.InvariantCulture) +
                    " build=" + target.BuildNumber.ToString(CultureInfo.InvariantCulture) +
                    " infDecorations=[" + string.Join(",", decorations.ToArray()) + "]");
            }

            // ----- device ID gate -----
            bool haveIds = !IsNullOrEmpty(deviceHardwareIds) || !IsNullOrEmpty(deviceCompatibleIds);
            if (!haveIds)
                return MakeApplicable(bestApplied, InfMatchLevel.None, "", "", "", "");

            // Only model entries whose own decoration is applicable may match.
            List<InfModelEntry> usableEntries = new List<InfModelEntry>();
            if (record.Models != null)
            {
                foreach (var m in record.Models)
                {
                    if (m == null) continue;
                    if (DecorationApplicable(m.Decoration, target)) usableEntries.Add(m);
                }
            }
            if (usableEntries.Count == 0)
                return InfApplicabilityResult.Reject("NoMatchingModelEntry",
                    "INF 没有适用于当前系统的 Models 条目 / INF has no model entry applicable to this OS");

            // Exact hardware ID first, then the remaining PnP-style tiers, so the returned
            // level always names the most specific match available.
            InfApplicabilityResult best = null;
            Match(deviceHardwareIds, true, usableEntries, delegate(string devId, InfModelEntry entry, string infId, InfMatchLevel level)
            {
                if (best == null || (int)level > (int)best.MatchLevel)
                    best = MakeApplicable(entry.Decoration, level, devId, infId, entry.Description, entry.InstallSection);
            });
            if (best == null || (int)InfMatchLevel.HardwareIdVsCompatibleId > (int)best.MatchLevel)
            {
                Match(deviceCompatibleIds, false, usableEntries, delegate(string devId, InfModelEntry entry, string infId, InfMatchLevel level)
                {
                    if (best == null || (int)level > (int)best.MatchLevel)
                        best = MakeApplicable(entry.Decoration, level, devId, infId, entry.Description, entry.InstallSection);
                });
            }
            if (best != null) return best;
            return InfApplicabilityResult.Reject("NoMatchingModelEntry",
                "INF 没有 Models 条目匹配设备硬件/兼容 ID / no INF model entry matches the device hardware/compatible IDs: " +
                "devHw=[" + JoinIds(deviceHardwareIds) + "] devCmp=[" + JoinIds(deviceCompatibleIds) + "]");
        }

        /// <summary>Per-decoration applicability for a parsed model entry (used to filter which
        /// [Models.*] lines may serve a given target). Mirrors the record-level gates above.</summary>
        public static bool DecorationApplicable(string decoration, OsTarget target)
        {
            return OsArchFilter.DecorationRejection(decoration, target, false).Length == 0;
        }

        // -------------------------------------------------------------------------

        private static InfApplicabilityResult MakeApplicable(string decoration, InfMatchLevel level,
            string devId, string infId, string modelDesc, string installSection)
        {
            return new InfApplicabilityResult
            {
                Applicable = true,
                MatchLevel = level,
                MatchedDeviceId = devId ?? "",
                MatchedInfId = infId ?? "",
                MatchedDecoration = decoration ?? "",
                MatchedModelDescription = modelDesc ?? "",
                MatchedInstallSection = installSection ?? ""
            };
        }

        /// <summary>
        /// Parses a Models decoration into (architecture, minimum OS version, minimum build).
        /// Grammar (TargetOSVersion): NT[arch][.major[.minor[.productType[.suiteMask[.build[.revision]]]]]]
        /// Architecture: NTamd64 / NTx86 / NTarm64 / NTarm / NT (= any arch). Empty fields preserve their fixed positions; the build is the sixth token, e.g. "NTamd64.10.0...22000" (major 10, minor 0, type/mask elided, build 22000).
        /// "NTamd64.10" / "NTamd64.6.1" name no build, so the "1" in 6.1 is never read as a build.
        /// </summary>
        private static void ParseDecoration(string decoration, out string arch, out int major, out int minBuild)
        {
            arch = OsArchFilter.ArchFromDecoration(decoration);
            if (string.IsNullOrWhiteSpace(decoration)) arch = "any";
            major = OsArchFilter.OsMajorFromDecoration(decoration);
            minBuild = OsArchFilter.MinBuildFromDecoration(decoration);
        }

        /// <summary>Walk device IDs in order; for each tier report every (device, entry, infId) hit.
        /// Tiers run from most specific to least, so the first hit of each tier wins overall.</summary>
        private static void Match(IEnumerable<string> deviceIds, bool deviceIsHardware, List<InfModelEntry> entries,
            Action<string, InfModelEntry, string, InfMatchLevel> onMatch)
        {
            if (deviceIds == null) return;
            foreach (var raw in deviceIds)
            {
                string dev = HardwareIdNormalizer.Normalize(raw);
                if (dev.Length == 0) continue;
                foreach (var entry in entries)
                {
                    if (deviceIsHardware)
                    {
                        foreach (var id in entry.HardwareIds)
                            if (dev.Equals(HardwareIdNormalizer.Normalize(id), StringComparison.OrdinalIgnoreCase))
                                onMatch(dev, entry, id, InfMatchLevel.HardwareId);
                        foreach (var id in entry.CompatibleIds)
                            if (dev.Equals(HardwareIdNormalizer.Normalize(id), StringComparison.OrdinalIgnoreCase))
                                onMatch(dev, entry, id, InfMatchLevel.HardwareIdVsCompatibleId);
                    }
                    else
                    {
                        foreach (var id in entry.HardwareIds)
                            if (dev.Equals(HardwareIdNormalizer.Normalize(id), StringComparison.OrdinalIgnoreCase))
                                onMatch(dev, entry, id, InfMatchLevel.CompatibleIdVsHardwareId);
                        foreach (var id in entry.CompatibleIds)
                            if (dev.Equals(HardwareIdNormalizer.Normalize(id), StringComparison.OrdinalIgnoreCase))
                                onMatch(dev, entry, id, InfMatchLevel.CompatibleId);
                    }
                }
            }
        }

        private static bool IsNullOrEmpty(IEnumerable<string> seq)
        {
            if (seq == null) return true;
            foreach (var s in seq) return false;
            return true;
        }

        private static string JoinIds(IEnumerable<string> ids)
        {
            var sb = new StringBuilder();
            if (ids != null)
                foreach (var id in ids)
                {
                    if (sb.Length > 0) sb.Append(',');
                    sb.Append(id ?? "");
                }
            return sb.ToString();
        }
    }
}
