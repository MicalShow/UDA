// DriverIndexCore.cs — Phase 1: offline high-performance driver index core.
// Compiled by the in-box CSharpCodeProvider (C# 5.0). No third-party dependencies.
// See README "Phase 1 / 驱动索引核心" for the SQLite-vs-BCL-store decision and file format.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;

namespace UDA
{
    // =================================================================================
    //  Data records
    // =================================================================================

    public sealed class InfModelSection
    {
        public string Group = "";
        public string Decoration = "";
    }

    /// <summary>One model line parsed from a [Models*] section of an INF.</summary>
    public sealed class InfModelEntry
    {
        public string Description = "";
        public string InstallSection = "";
        public string ModelGroup = "";
        public string Decoration = ""; // e.g. "NTamd64.10" or "" for the bare/universal section
        public List<string> HardwareIds = new List<string>();
        public List<string> CompatibleIds = new List<string>();
    }

    /// <summary>A parsed INF plus its identity fingerprint, ready for indexing.</summary>
    public sealed class InfRecord
    {
        public string Path = "";
        public string SourceName = "";
        public string Provider = "";
        public string Manufacturer = "";
        public string Class = "";
        public string ClassGuid = "";
        public string ExtensionId = "";
        public string DriverVerDate = "";
        public string DriverVerVersion = "";
        public string Fingerprint = "";  // quick key: "len|mtimeUtcTicks"
        public string ContentHash = "";  // SHA-256 hex of file bytes
        public List<string> Decorations = new List<string>();
        public List<InfModelEntry> Models = new List<InfModelEntry>();
        public List<InfModelSection> ModelSections = new List<InfModelSection>();
        // Aggregated, normalized IDs across all model lines (deduplicated).
        public List<string> HardwareIds = new List<string>();
        public List<string> CompatibleIds = new List<string>();

        public DateTime Date()
        {
            DateTime dt;
            return InfParser.TryParseDriverDate(DriverVerDate, out dt) ? dt : DateTime.MinValue;
        }
    }

    /// <summary>The OS/architecture target used for decoration filtering and matching.</summary>
    public sealed class OsTarget
    {
        public string Architecture = "amd64"; // amd64 | x86 | arm64 | arm | unknown
        public int MajorVersion = 10;          // Windows major (10 covers Win10/Win11)
        public int MinorVersion = -1;          // -1 = infer legacy clients from build; never assume all NT 6.x are equal
        public int BuildNumber = 0;            // Windows build number (e.g. 19045); 0 = unknown

        public int EffectiveMinorVersion()
        {
            if (MinorVersion >= 0) return MinorVersion;
            if (MajorVersion != 6) return 0;
            if (BuildNumber <= 0) return -1;
            if (BuildNumber >= 9600) return 3;
            if (BuildNumber >= 9200) return 2;
            if (BuildNumber >= 7600) return 1;
            return 0;
        }

        public static OsTarget FromEnvironment()
        {
            var t = new OsTarget();
            try
            {
                using (var s = new System.Management.ManagementObjectSearcher("SELECT * FROM Win32_OperatingSystem"))
                using (var c = s.Get())
                    foreach (System.Management.ManagementObject o in c)
                    {
                        string arch = Convert.ToString(o["OSArchitecture"]).Trim();
                        if (arch.IndexOf("64", StringComparison.OrdinalIgnoreCase) >= 0 && arch.IndexOf("ARM", StringComparison.OrdinalIgnoreCase) >= 0) t.Architecture = "arm64";
                        else if (arch.IndexOf("ARM", StringComparison.OrdinalIgnoreCase) >= 0) t.Architecture = "arm";
                        else if (arch.IndexOf("64", StringComparison.OrdinalIgnoreCase) >= 0) t.Architecture = "amd64";
                        else if (arch.IndexOf("32", StringComparison.OrdinalIgnoreCase) >= 0) t.Architecture = "x86";
                        string ver = Convert.ToString(o["Version"]).Trim();
                        int major;
                        if (int.TryParse(ver.Split('.')[0], out major) && major > 0) t.MajorVersion = major;
                        int minor;
                        if (ver.Split('.').Length > 1 && int.TryParse(ver.Split('.')[1], out minor)) t.MinorVersion = minor;
                        int build;
                        if (int.TryParse(Convert.ToString(o["BuildNumber"]).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out build) && build > 0) t.BuildNumber = build;
                        break;
                    }
            }
            catch { }
            if (t.BuildNumber <= 0)
            {
                try
                {
                    using (var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion"))
                    {
                        int build;
                        if (key != null && int.TryParse(Convert.ToString(key.GetValue("CurrentBuildNumber")), out build) && build > 0)
                            t.BuildNumber = build;
                    }
                }
                catch { }
            }
            return t;
        }
    }

    // =================================================================================
    //  Numeric driver-version comparison (single source of truth)
    // =================================================================================

    /// <summary>
    /// Numeric, component-wise driver version comparison. "9.9.9.9" &lt; "10.0.0.1" numerically,
    /// unlike an ordinal string compare. Missing components count as 0; unparseable components too.
    /// </summary>
    public static class DriverVersion
    {
        public static bool IsNewer(string candidate, string current)
        {
            if (string.IsNullOrWhiteSpace(candidate)) return false;
            if (string.IsNullOrWhiteSpace(current)) return true;
            string[] a = candidate.Split('.', ',', '+'), b = current.Split('.', ',', '+');
            int count = Math.Max(a.Length, b.Length);
            for (int i = 0; i < count; i++)
            {
                long av = 0, bv = 0;
                if (i < a.Length) long.TryParse(a[i].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out av);
                if (i < b.Length) long.TryParse(b[i].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out bv);
                if (av != bv) return av > bv;
            }
            return false;
        }
    }

    // =================================================================================
    //  Shared applicability rule (scan + install must agree on this)
    // =================================================================================

    /// <summary>
    /// The single applicability rule shared by the scan/match stage and the pre-install revalidation
    /// stage, so a candidate shown as installable can never be silently skipped at install time for
    /// an OS/architecture reason (and vice versa). Returns null when the INF applies to the target,
    /// otherwise a human-readable reason why it does not.
    /// </summary>
    public static class DriverApplicability
    {
        /// <summary>null = applicable; otherwise a readable inapplicability reason.</summary>
        public static string Explain(InfRecord record, OsTarget target)
        {
            if (record == null) return "候选 INF 记录为空 / candidate INF record is null";
            if (target == null) return null;
            if (!OsArchFilter.Applicable(record, target))
            {
                return "INF 装饰节不适用当前系统 / INF decoration does not apply: arch=" + (target.Architecture ?? "") +
                       " osMajor=" + target.MajorVersion.ToString(CultureInfo.InvariantCulture) +
                       " build=" + target.BuildNumber.ToString(CultureInfo.InvariantCulture) +
                       " infDecorations=[" + string.Join(",", (record.Decorations ?? new List<string>()).ToArray()) + "]";
            }
            return null;
        }
    }

    // =================================================================================
    //  INF parser
    // =================================================================================

    /// <summary>
    /// Parses Windows INF files: [Manufacturer] + decorated [Models.*] sections,
    /// [Strings] %key% substitution, hardware/compatible IDs, and [Version] metadata
    /// (Provider/Class/ClassGuid/DriverVer). Pure, offline, no PnP calls.
    /// </summary>
    public static class InfParser
    {
        public static InfRecord Parse(string path, string sourceName)
        {
            return ParseBytes(File.ReadAllBytes(path), path, sourceName);
        }
        public static InfRecord ParseBytes(byte[] bytes, string path, string sourceName)
        {
            string text = DecodeInfText(bytes);
            var record = new InfRecord();
            record.Path = Path.GetFullPath(path);
            record.SourceName = sourceName ?? "";
            record.ContentHash = Sha256Hex(bytes);
            var fi = new FileInfo(path);
            long len = fi.Exists ? fi.Length : bytes.Length;
            long mtime = fi.Exists ? fi.LastWriteTimeUtc.Ticks : 0;
            record.Fingerprint = len.ToString(CultureInfo.InvariantCulture) + "|" + mtime.ToString(CultureInfo.InvariantCulture);

            var lines = SplitLines(text);
            var sections = ParseSections(lines);
            var strings = BuildStrings(sections);

            // [Version]
            Section version;
            if (sections.TryGetValue("version", out version))
            {
                record.Provider = Resolve(GetValue(version, "Provider"), strings);
                record.Class = GetValue(version, "Class").Trim();
                record.ClassGuid = NormalizeClassGuid(GetValue(version, "ClassGuid"));
                Guid extension;
                if (Guid.TryParse(GetValue(version, "ExtensionId").Trim().Trim('"'), out extension) && extension != Guid.Empty) record.ExtensionId = extension.ToString("B");
                string dv = GetValue(version, "DriverVer");
                ParseDriverVer(dv, record);
            }

            // [Manufacturer] -> base model-section names (+ optional decoration targets).
            var modelSectionsToRead = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            Section mfg;
            if (sections.TryGetValue("manufacturer", out mfg))
            {
                foreach (var kv in mfg.Entries)
                {
                    string[] rhs = kv.Value.Split(',');
                    if (rhs.Length == 0) continue;
                    string baseName = Resolve(rhs[0], strings).Trim().Trim('"');
                    if (baseName.Length == 0) continue;
                    if (string.IsNullOrEmpty(record.Manufacturer)) record.Manufacturer = Resolve(kv.Key, strings);
                    // The base name is arbitrary and can itself contain dots (ibm.Mfg).
                    // Only Manufacturer-declared targets are model sections; .Services/.HW
                    // and similarly named sections must never become device models.
                    if (rhs.Length == 1)
                    {
                        if (sections.ContainsKey(baseName)) modelSectionsToRead[baseName] = "";
                    }
                    else foreach (string rawTarget in rhs.Skip(1))
                    {
                        string decoration = Resolve(rawTarget, strings).Trim().Trim('"');
                        if (!Regex.IsMatch(decoration, @"^NT(?:amd64|x86|arm64|arm)?(?:\.(?:[0-9]+|0x[0-9a-f]+)?)*$", RegexOptions.IgnoreCase)) continue;
                        string name = baseName + "." + decoration;
                        if (sections.ContainsKey(name)) modelSectionsToRead[name] = decoration;
                    }
                }
            }
            else
            {
                // No [Manufacturer]: fall back to any section literally named "Models" or "Models.*".
                foreach (var name in sections.Keys)
                    if (name == "models" || name.StartsWith("models.", StringComparison.Ordinal))
                        modelSectionsToRead[name] = name == "models" ? "" : sections[name].OriginalName.Substring("Models.".Length);
            }

            var hwSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var cmpSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var decSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var modelSection in modelSectionsToRead)
            {
                Section sec;
                if (!sections.TryGetValue(modelSection.Key, out sec)) continue;
                string decoration = modelSection.Value;
                string group = decoration.Length == 0 ? modelSection.Key : modelSection.Key.Substring(0, modelSection.Key.Length - decoration.Length - 1);
                record.ModelSections.Add(new InfModelSection { Group = group, Decoration = decoration });
                if (decSeen.Add(decoration)) record.Decorations.Add(decoration);
                foreach (var kv in sec.Entries)
                {
                    var entry = ParseModelLine(kv.Key, kv.Value, decoration, strings);
                    if (entry == null) continue;
                    entry.ModelGroup = group;
                    record.Models.Add(entry);
                    foreach (var id in entry.HardwareIds) if (hwSeen.Add(id)) record.HardwareIds.Add(id);
                    foreach (var id in entry.CompatibleIds) if (cmpSeen.Add(id)) record.CompatibleIds.Add(id);
                }
            }
            if (record.Decorations.Count == 0) record.Decorations.Add("");
            return record;
        }

        // ----- text decoding -----

        private static string DecodeInfText(byte[] bytes)
        {
            if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
                return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
                return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
            // Default to UTF-8; structure (section names, IDs) is ASCII so legacy ANSI INFs parse fine.
            return Encoding.UTF8.GetString(bytes);
        }

        private static List<string> SplitLines(string text)
        {
            var raw = text.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
            var lines = new List<string>(raw.Length);
            var current = new StringBuilder();
            foreach (var r in raw)
            {
                string line = StripComment(r).Trim();
                if (line.EndsWith("\\", StringComparison.Ordinal) && line.Length > 1)
                {
                    current.Append(line.Substring(0, line.Length - 1).TrimEnd());
                    continue;
                }
                current.Append(line);
                lines.Add(current.ToString());
                current.Length = 0;
            }
            if (current.Length > 0) lines.Add(current.ToString());
            return lines;
        }

        private static string StripComment(string line)
        {
            bool inQuote = false;
            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];
                if (c == '"') inQuote = !inQuote;
                else if (c == ';' && !inQuote) return line.Substring(0, i);
            }
            return line;
        }

        private sealed class Section
        {
            public string OriginalName = "";
            public List<KeyValuePair<string, string>> Entries = new List<KeyValuePair<string, string>>();
        }

        private static Dictionary<string, Section> ParseSections(List<string> lines)
        {
            var map = new Dictionary<string, Section>(StringComparer.OrdinalIgnoreCase);
            Section current = null;
            foreach (var line in lines)
            {
                if (line.Length == 0) continue;
                if (line[0] == '[')
                {
                    int end = line.IndexOf(']');
                    if (end > 0)
                    {
                        string name = line.Substring(1, end - 1).Trim();
                        current = new Section { OriginalName = name };
                        map[name.ToLowerInvariant()] = current;
                    }
                    continue;
                }
                int eq = line.IndexOf('=');
                if (eq <= 0 || current == null) continue;
                string key = line.Substring(0, eq).Trim();
                string val = line.Substring(eq + 1).Trim();
                if (key.Length == 0) continue;
                current.Entries.Add(new KeyValuePair<string, string>(key, val));
            }
            return map;
        }

        private static Dictionary<string, string> BuildStrings(Dictionary<string, Section> sections)
        {
            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            Section strings;
            if (!sections.TryGetValue("strings", out strings)) return dict;
            foreach (var kv in strings.Entries)
                dict[kv.Key.Trim('%').Trim()] = kv.Value.Trim().Trim('"');
            return dict;
        }

        private static string GetValue(Section section, string key)
        {
            foreach (var kv in section.Entries)
                if (string.Equals(kv.Key, key, StringComparison.OrdinalIgnoreCase))
                    return kv.Value;
            return "";
        }

        // %Key% -> resolved value. Supports %Key% and %Key%; literal fallback.
        private static readonly Regex TokenRegex = new Regex(@"%([^%]+)%", RegexOptions.Compiled);
        private static string Resolve(string value, Dictionary<string, string> strings)
        {
            if (string.IsNullOrEmpty(value) || strings == null || strings.Count == 0) return value;
            return TokenRegex.Replace(value, delegate(Match m)
            {
                string v;
                return strings.TryGetValue(m.Groups[1].Value, out v) ? v : m.Value;
            });
        }

        private static InfModelEntry ParseModelLine(string descKey, string rhs, string decoration, Dictionary<string, string> strings)
        {
            string desc = Resolve(descKey, strings).Trim();
            // parts[0] = install section; parts[1..] = hardware/compatible IDs (all comma-separated;
            // ';' is a comment in INF, stripped earlier). Each ID is classified by specificity.
            string[] parts = rhs.Split(',');
            if (parts.Length < 2) return null;
            string install = Resolve(parts[0], strings).Trim().Trim('"');
            var entry = new InfModelEntry { Description = desc, InstallSection = install, Decoration = decoration };
            for (int i = 1; i < parts.Length; i++)
            {
                string n = HardwareIdNormalizer.Normalize(Resolve(parts[i], strings));
                if (n.Length == 0) continue;
                if (HardwareIdNormalizer.IsHardwareId(n)) entry.HardwareIds.Add(n);
                else entry.CompatibleIds.Add(n);
            }
            if (entry.HardwareIds.Count == 0 && entry.CompatibleIds.Count == 0) return null;
            return entry;
        }

        private static void ParseDriverVer(string dv, InfRecord record)
        {
            if (string.IsNullOrWhiteSpace(dv)) return;
            string[] parts = dv.Split(',');
            if (parts.Length >= 1) record.DriverVerDate = parts[0].Trim();
            if (parts.Length >= 2) record.DriverVerVersion = parts[1].Trim();
        }

        public static bool TryParseDriverDate(string text, out DateTime value)
        {
            value = DateTime.MinValue;
            if (string.IsNullOrWhiteSpace(text)) return false;
            string[] formats = { "M/d/yyyy", "M-d-yyyy", "yyyy/M/d", "M/d/yy", "M-d-yy" };
            return DateTime.TryParseExact(text.Trim(), formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out value)
                || DateTime.TryParse(text.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.None, out value);
        }

        private static string NormalizeClassGuid(string raw)
        {
            string g = (raw ?? "").Trim().Trim('"').Trim();
            if (g.Length == 0) return "";
            if (!g.StartsWith("{", StringComparison.Ordinal)) g = "{" + g;
            if (!g.EndsWith("}", StringComparison.Ordinal)) g = g + "}";
            return g.ToLowerInvariant();
        }

        private static string Sha256Hex(byte[] bytes)
        {
            using (var sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(bytes);
                var sb = new StringBuilder(hash.Length * 2);
                foreach (byte b in hash) sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
                return sb.ToString();
            }
        }
    }

    // =================================================================================
    //  Hardware ID normalization + specificity
    // =================================================================================

    /// <summary>
    /// Normalizes PnP hardware/compatible IDs and computes a specificity score so that
    /// more specific IDs (more bus-specific sub-components) rank above generic ones.
    /// </summary>
    public static class HardwareIdNormalizer
    {
        private static readonly HashSet<string> KnownBuses = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "PCI", "USB", "HDAUDIO", "ACPI", "ROOT", "SWD", "BTHENUM", "HDAUDIO", "ACPIHAL", "DISPLAY" };

        public static string Normalize(string id)
        {
            if (id == null) return "";
            string s = id.Trim().Trim('"').Trim();
            if (s.Length == 0) return "";
            s = s.ToUpperInvariant();
            // Windows exposes equivalent ACPI PNP IDs in two forms depending on the
            // enumerator/OS generation. INF files commonly use ACPI\INT34BB while a device can
            // report ACPI\VEN_INT&DEV_34BB (and similarly INTC1055 / VEN_INTC&DEV_1055).
            // Canonicalize the expanded form so repository and client IDs meet in one index key.
            Match acpi = Regex.Match(s, @"^ACPI\\VEN_([A-Z0-9]{3,4})&DEV_([A-Z0-9]{4})$");
            if (acpi.Success) return "ACPI\\" + acpi.Groups[1].Value + acpi.Groups[2].Value;
            return s;
        }

        public static string BusOf(string id)
        {
            string n = Normalize(id);
            int bs = n.IndexOf('\\');
            return bs > 0 ? n.Substring(0, bs) : "";
        }

        /// <summary>Derive the enumerator/device portion from a PnP instance path when WMI exposes
        /// no HardwareID array (common for missing ACPI/System drivers). Example:
        /// ACPI\VEN_INT&amp;DEV_34BB\0 -> ACPI\INT34BB after normalisation.</summary>
        public static string FromDeviceInstanceId(string instanceId)
        {
            if (string.IsNullOrWhiteSpace(instanceId)) return "";
            string raw = instanceId.Trim().Trim('"');
            int first = raw.IndexOf('\\');
            if (first <= 0) return "";
            int second = raw.IndexOf('\\', first + 1);
            string device = second > first ? raw.Substring(0, second) : raw;
            return Normalize(device);
        }

        /// <summary>Add conservative IDs that Windows may omit from WMI. The device-instance
        /// fallback is exact at the enumerator/device level. HID collection/USB interface suffixes
        /// are added as compatible IDs only, preserving exact-ID priority.</summary>
        public static void AddSafeMatchingAliases(List<string> hardwareIds, List<string> compatibleIds, string instanceId)
        {
            if (hardwareIds == null || compatibleIds == null) return;
            string fallback = FromDeviceInstanceId(instanceId);
            if (fallback.Length > 0 && !hardwareIds.Contains(fallback, StringComparer.OrdinalIgnoreCase)) hardwareIds.Add(fallback);
            foreach (string raw in hardwareIds.ToArray())
            {
                string n = Normalize(raw);
                string parent = Regex.Replace(n, @"&COL[0-9A-F]{2}$", "", RegexOptions.IgnoreCase);
                parent = Regex.Replace(parent, @"&MI_[0-9A-F]{2}$", "", RegexOptions.IgnoreCase);
                if (!string.Equals(parent, n, StringComparison.OrdinalIgnoreCase) &&
                    !compatibleIds.Contains(parent, StringComparer.OrdinalIgnoreCase)) compatibleIds.Add(parent);
            }
        }

        public static bool IsHardwareId(string id)
        {
            string n = Normalize(id);
            if (n.Length == 0) return false;
            if (n.IndexOf('\\') < 0)
                return n.StartsWith("*", StringComparison.Ordinal); // e.g. *PNP0B00
            string after = n.Substring(n.IndexOf('\\') + 1);
            // Generic class-code / device-class IDs are compatible IDs.
            if (after.IndexOf("CC_", StringComparison.Ordinal) >= 0) return false;
            if (after.IndexOf("CLASS_", StringComparison.Ordinal) >= 0) return false;
            if (after.IndexOf("SUBCLASS", StringComparison.Ordinal) >= 0) return false;
            // Specific vendor/device tuples mark a hardware ID.
            if (after.IndexOf("VEN_", StringComparison.Ordinal) >= 0) return true;
            if (after.IndexOf("VID_", StringComparison.Ordinal) >= 0) return true;
            if (after.IndexOf("DEV_", StringComparison.Ordinal) >= 0) return true;
            if (after.IndexOf("PID_", StringComparison.Ordinal) >= 0) return true;
            if (after.IndexOf("SUBSYS_", StringComparison.Ordinal) >= 0) return true;
            if (after.IndexOf("PROD_", StringComparison.Ordinal) >= 0) return true;
            // Bus-prefixed but otherwise opaque (e.g. USB\ROOT_HUB30) — treat as a hardware ID.
            return true;
        }

        /// <summary>Higher == more specific. Counts &-separated components and meaningful tokens.</summary>
        public static int Specificity(string id)
        {
            string n = Normalize(id);
            if (n.Length == 0) return 0;
            int score = 0;
            int bs = n.IndexOf('\\');
            string after = bs > 0 ? n.Substring(bs + 1) : n;
            string[] comps = after.Split('&');
            foreach (var c in comps) if (c.Trim().Length > 0) score++;
            if (after.IndexOf("SUBSYS_", StringComparison.Ordinal) >= 0) score += 2;
            if (after.IndexOf("REV_", StringComparison.Ordinal) >= 0) score += 1;
            if (after.IndexOf("VEN_", StringComparison.Ordinal) >= 0 || after.IndexOf("VID_", StringComparison.Ordinal) >= 0) score += 1;
            if (after.IndexOf("DEV_", StringComparison.Ordinal) >= 0 || after.IndexOf("PID_", StringComparison.Ordinal) >= 0) score += 1;
            return score;
        }

        /// <summary>Canonical VEN/DEV or VID/PID pair for cross-source dedup, or "" if none.</summary>
        public static string CanonicalVenDev(string id)
        {
            string n = Normalize(id);
            var m = Regex.Match(n, @"(VEN_[0-9A-F]{4}&DEV_[0-9A-F]{4})");
            if (m.Success) return m.Groups[1].Value;
            m = Regex.Match(n, @"(VID_[0-9A-F]{4}&PID_[0-9A-F]{4})");
            if (m.Success) return m.Groups[1].Value;
            return "";
        }
    }

    // =================================================================================
    //  OS / architecture decoration filter
    // =================================================================================

    /// <summary>
    /// Decides whether an INF's decorated [Models.*] sections apply to a target OS/arch.
    /// Decorations: NT, NTx86, NTamd64, NTarm, NTarm64, optionally followed by .NN (OS major).
    /// </summary>
    public static class OsArchFilter
    {
        public static bool Applicable(InfRecord record, OsTarget target)
        {
            if (record == null || target == null) return true;
            if (record.Decorations == null || record.Decorations.Count == 0) return true;
            bool anyUniversal = false;
            bool anyArchMatch = false;
            bool anyDecorated = false;
            foreach (var dec in record.Decorations)
            {
                string d = (dec ?? "").Trim();
                if (d.Length == 0) { anyUniversal = true; continue; }
                anyDecorated = true;
                if (DecorationRejection(d, target, true).Length == 0) anyArchMatch = true;
            }
            // A universal (bare) section means the INF covers all architectures.
            if (anyUniversal) return true;
            if (!anyDecorated) return true;
            return anyArchMatch;
        }

        public static string ArchFromDecoration(string decoration)
        {
            string d = (decoration ?? "").ToUpperInvariant();
            foreach (var token in d.Split('.'))
            {
                if (token == "NTAMD64") return "amd64";
                if (token == "NTX86") return "x86";
                if (token == "NTARM64") return "arm64";
                if (token == "NTARM") return "arm";
                if (token == "NT") return "any";
            }
            return "";
        }

        public static int OsMajorFromDecoration(string decoration)
        {
            return DecorationNumber(decoration, 1);
        }

        /// <summary>
        /// Minimum build is the fixed sixth token in NT[arch].major.minor.product.suite.build.
        /// Empty and populated product/suite fields do not change its position.
        /// </summary>
        public static int MinBuildFromDecoration(string decoration)
        {
            // TargetOSVersion has fixed slots: head.major.minor.product.suite.build.
            // ProductType/SuiteMask must never be mistaken for BuildNumber.
            return DecorationNumber(decoration, 5);
        }
        public static int OsMinorFromDecoration(string decoration) { return DecorationNumber(decoration, 2); }
        private static int DecorationNumber(string decoration, int index)
        {
            var parts = (decoration ?? "").Trim().Split('.');
            int value;
            if (parts.Length <= index || string.IsNullOrWhiteSpace(parts[index])) return 0;
            string token = parts[index].Trim();
            return token.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                ? (int.TryParse(token.Substring(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value) ? value : 0)
                : (int.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out value) ? value : 0);
        }
        public static string DecorationRejection(string decoration, OsTarget target, bool allowUnknownBuild)
        {
            if (target == null) return "";
            string d = (decoration ?? "").Trim();
            if (d.Length == 0) return "";
            if (!Regex.IsMatch(d, @"^NT(?:amd64|x86|arm64|arm)?(?:\.(?:[0-9]+|0x[0-9a-f]+)?)*$", RegexOptions.IgnoreCase)) return "InvalidDecoration";
            string arch = ArchFromDecoration(d);
            if (arch != "any" && !string.Equals(arch, target.Architecture, StringComparison.OrdinalIgnoreCase)) return "ArchitectureMismatch";
            int major = OsMajorFromDecoration(d), minor = OsMinorFromDecoration(d);
            var parts = d.Split('.');
            bool minorSpecified = parts.Length > 2 && parts[2].Length > 0;
            if (major > target.MajorVersion) return "OsVersionTooLow";
            if (major > 0 && major < target.MajorVersion) return "";
            if (major > 0 && minorSpecified)
            {
                int actualMinor = target.EffectiveMinorVersion();
                if (actualMinor < 0) return "OsVersionUnknown";
                if (actualMinor < minor) return "OsVersionTooLow";
                if (actualMinor > minor) return "";
            }
            int build = MinBuildFromDecoration(d);
            if (build > 0 && target.BuildNumber <= 0) return allowUnknownBuild ? "" : "BuildUnknown";
            if (build > 0 && target.BuildNumber < build) return "BuildTooLow";
            return "";
        }

    }

    // =================================================================================
    //  Deterministic match ranking
    // =================================================================================

    public struct MatchScore : IComparable<MatchScore>, IComparable
    {
        public int MatchTypeRank;   // 4=devHw∩infHw, 3=devHw∩infCmp, 2=devCmp∩infHw, 1=devCmp∩infCmp
        public int Specificity;
        public int ModelPreference;
        public long DateTicks;
        public long VersionScore;
        public string Path;

        public int CompareTo(MatchScore other)
        {
            int c = MatchTypeRank.CompareTo(other.MatchTypeRank); if (c != 0) return c;
            c = Specificity.CompareTo(other.Specificity); if (c != 0) return c;
            c = ModelPreference.CompareTo(other.ModelPreference); if (c != 0) return c;
            c = DateTicks.CompareTo(other.DateTicks); if (c != 0) return c;
            c = VersionScore.CompareTo(other.VersionScore); if (c != 0) return c;
            return -string.CompareOrdinal(Path ?? "", other.Path ?? ""); // lower path wins (stable tiebreak)
        }
        public int CompareTo(object obj) { return CompareTo((MatchScore)obj); }
    }

    public sealed class MatchCandidate
    {
        public InfRecord Record;
        public MatchScore Score;
        public string MatchedId = "";
        public int Tier;
    }

    /// <summary>
    /// Deterministic, PnP-style ranking. Tier precedence: device-HWID vs INF-HWID (best),
    /// then device-HWID vs INF-CompatID, then device-CompatID vs INF-HWID, then device-CompatID
    /// vs INF-CompatID. Within a tier: more specific ID, then model preference, then newer
    /// DriverVer date, then higher version, then path (stable). Fully offline.
    /// </summary>
    public sealed class MatchRanker
    {
        private struct IdRef { public int RecordOrdinal; public bool IsHardware; }

        private readonly List<InfRecord> _records;
        private readonly Dictionary<string, List<IdRef>> _index;
        private readonly HashSet<string> _preferredSources;

        public MatchRanker(IEnumerable<InfRecord> records, IEnumerable<string> preferredSources)
        {
            _records = (records ?? Enumerable.Empty<InfRecord>()).ToList();
            _preferredSources = new HashSet<string>(preferredSources ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);
            _index = new Dictionary<string, List<IdRef>>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < _records.Count; i++)
            {
                var r = _records[i];
                foreach (var id in r.HardwareIds) AddRef(id, i, true);
                foreach (var id in r.CompatibleIds) AddRef(id, i, false);
            }
        }

        private void AddRef(string id, int ordinal, bool isHardware)
        {
            string n = HardwareIdNormalizer.Normalize(id);
            if (n.Length == 0) return;
            List<IdRef> list;
            if (!_index.TryGetValue(n, out list)) { list = new List<IdRef>(); _index[n] = list; }
            list.Add(new IdRef { RecordOrdinal = ordinal, IsHardware = isHardware });
        }

        public List<MatchCandidate> Rank(IList<string> deviceHardwareIds, IList<string> deviceCompatibleIds, OsTarget target)
        {
            var bestPerRecord = new Dictionary<int, MatchCandidate>();
            Consider(deviceHardwareIds, true, target, bestPerRecord);
            Consider(deviceCompatibleIds, false, target, bestPerRecord);
            var result = bestPerRecord.Values.ToList();
            result.Sort(delegate(MatchCandidate a, MatchCandidate b) { return b.Score.CompareTo(a.Score); });
            return result;
        }

        private void Consider(IList<string> deviceIds, bool deviceIsHardware, OsTarget target, Dictionary<int, MatchCandidate> bestPerRecord)
        {
            if (deviceIds == null) return;
            foreach (var raw in deviceIds)
            {
                string n = HardwareIdNormalizer.Normalize(raw);
                if (n.Length == 0) continue;
                List<IdRef> list;
                if (!_index.TryGetValue(n, out list)) continue;
                foreach (var r in list)
                {
                    var record = _records[r.RecordOrdinal];
                    if (target != null && !OsArchFilter.Applicable(record, target)) continue;
                    int tier = TierFor(deviceIsHardware, r.IsHardware);
                    int spec = HardwareIdNormalizer.Specificity(n);
                    int pref = _preferredSources.Contains(record.SourceName) ? 1 : 0;
                    var score = new MatchScore
                    {
                        MatchTypeRank = tier,
                        Specificity = spec,
                        ModelPreference = pref,
                        DateTicks = record.Date().Ticks,
                        VersionScore = VersionScore(record.DriverVerVersion),
                        Path = record.Path
                    };
                    MatchCandidate existing;
                    if (!bestPerRecord.TryGetValue(r.RecordOrdinal, out existing) || score.CompareTo(existing.Score) > 0)
                    {
                        bestPerRecord[r.RecordOrdinal] = new MatchCandidate { Record = record, Score = score, MatchedId = n, Tier = tier };
                    }
                }
            }
        }

        private static int TierFor(bool deviceIsHardware, bool infIsHardware)
        {
            if (deviceIsHardware && infIsHardware) return 4;
            if (deviceIsHardware && !infIsHardware) return 3;
            if (!deviceIsHardware && infIsHardware) return 2;
            return 1;
        }

        public static long VersionScore(string version)
        {
            if (string.IsNullOrWhiteSpace(version)) return 0;
            string[] parts = version.Split('.', ',', '+');
            long[] nums = new long[4];
            for (int i = 0; i < 4 && i < parts.Length; i++)
            {
                long v;
                if (long.TryParse(parts[i].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out v)) nums[i] = v & 0xFFFF;
            }
            return (nums[0] << 48) | (nums[1] << 32) | (nums[2] << 16) | nums[3];
        }
    }

    // =================================================================================
    //  Persistent on-disk driver index (transactional, schema-versioned, dependency-free)
    // =================================================================================

    /// <summary>
    /// A truly database-like, offline, dependency-free persistent index. Single-file binary
    /// store with a fixed header (magic + schema version), content-addressed records, a
    /// persisted inverted index (hardware ID -> record ordinals), transactional commits
    /// (temp file + atomic File.Replace + .bak recovery), and incremental updates driven by
    /// cheap file fingerprints (size + last-write-time) with SHA-256 content identity.
    ///
    /// SQLite was the preferred option but requires a native sqlite3.dll + managed wrapper
    /// that is not part of the in-box .NET Framework and cannot be carried by this project's
    /// single-EXE, no-third-party build. This BCL store is the deployable alternative.
    /// </summary>
    public sealed class DriverIndex : IDisposable
    {
        public const int CurrentSchema = 3;
        private const string Magic = "UDAIDX01";
        private readonly string _path;
        private readonly string _tmp;
        private readonly string _bak;

        private List<InfRecord> _records = new List<InfRecord>();
        private Dictionary<string, List<int>> _index = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
        public bool NeedsRebuild { get; private set; }
        public int SchemaVersionObserved { get; private set; }

        public DriverIndex(string path)
        {
            _path = Path.GetFullPath(path);
            _tmp = _path + ".tmp";
            _bak = _path + ".bak";
        }

        public int Count { get { return _records.Count; } }
        public IReadOnlyList<InfRecord> Records { get { return _records; } }

        public void Open()
        {
            // Crash recovery: an unfinished commit leaves a stale .tmp; the live file is still good.
            if (File.Exists(_tmp)) { try { File.Delete(_tmp); } catch { } }
            if (TryRead(_path)) return;
            if (File.Exists(_bak) && TryRead(_bak))
            {
                try { File.Copy(_bak, _path, true); } catch { }
                return;
            }
            _records = new List<InfRecord>();
            _index = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
            NeedsRebuild = true;
            SchemaVersionObserved = 0;
        }

        private bool TryRead(string file)
        {
            try
            {
                if (!File.Exists(file)) return false;
                using (var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var br = new BinaryReader(fs, Encoding.UTF8, false))
                {
                    byte[] magic = br.ReadBytes(8);
                    if (magic.Length < 8 || Encoding.ASCII.GetString(magic) != Magic) return false;
                    int schema = br.ReadInt32();
                    SchemaVersionObserved = schema;
                    int recordCount = br.ReadInt32();
                    var records = new List<InfRecord>(recordCount);
                    var serializer = CreateRecordSerializer();
                    for (int i = 0; i < recordCount; i++)
                    {
                        string fp = br.ReadString();
                        string json = br.ReadString();
                        var rec = DeserializeRecord(serializer, json);
                        if (rec == null) return false;
                        rec.Fingerprint = fp;
                        records.Add(rec);
                    }
                    int indexCount = br.ReadInt32();
                    var index = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
                    for (int i = 0; i < indexCount; i++)
                    {
                        string key = br.ReadString();
                        int count = br.ReadInt32();
                        var ords = new List<int>(count);
                        for (int j = 0; j < count; j++) ords.Add(br.ReadInt32());
                        index[key] = ords;
                    }
                    _records = records;
                    _index = index;
                    NeedsRebuild = schema != CurrentSchema;
                    return true;
                }
            }
            catch { }
            return false;
        }

        private static InfRecord DeserializeRecord(JavaScriptSerializer serializer, string json)
        {
            try { return serializer.Deserialize<InfRecord>(json); }
            catch { return null; }
        }

        /// <summary>Atomically persist the current records + inverted index.</summary>
        public void Commit()
        {
            RebuildIndex();
            Directory.CreateDirectory(Path.GetDirectoryName(_path));
            using (var fs = new FileStream(_tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var bw = new BinaryWriter(fs, Encoding.UTF8, false))
            {
                bw.Write(Encoding.ASCII.GetBytes(Magic));
                bw.Write(CurrentSchema);
                bw.Write(_records.Count);
                var serializer = CreateRecordSerializer();
                foreach (var rec in _records)
                {
                    bw.Write(rec.Fingerprint ?? "");
                    bw.Write(serializer.Serialize(rec));
                }
                bw.Write(_index.Count);
                foreach (var kv in _index)
                {
                    bw.Write(kv.Key);
                    bw.Write(kv.Value.Count);
                    foreach (var ord in kv.Value) bw.Write(ord);
                }
                bw.Flush();
                fs.Flush(true);
            }
            // Atomic flip. File.Replace requires the destination to exist; otherwise File.Move.
            if (File.Exists(_path)) File.Replace(_tmp, _path, _bak, true);
            else File.Move(_tmp, _path);
        }

        // Expanded models/IDs in a single vendor INF can exceed the framework's 2 MB default.
        // Read and write must use the same limit; otherwise a committed index cannot be reopened.
        private static JavaScriptSerializer CreateRecordSerializer()
        {
            return new JavaScriptSerializer { MaxJsonLength = 64 * 1024 * 1024, RecursionLimit = 256 };
        }

        private void RebuildIndex()
        {
            _index = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < _records.Count; i++)
            {
                var r = _records[i];
                foreach (var id in r.HardwareIds) AddIndex(id, i);
                foreach (var id in r.CompatibleIds) AddIndex(id, i);
            }
        }

        private void AddIndex(string id, int ordinal)
        {
            string n = HardwareIdNormalizer.Normalize(id);
            if (n.Length == 0) return;
            List<int> list;
            if (!_index.TryGetValue(n, out list)) { list = new List<int>(); _index[n] = list; }
            if (!list.Contains(ordinal)) list.Add(ordinal);
        }

        public MatchRanker CreateRanker(IEnumerable<string> preferredSources)
        {
            return new MatchRanker(_records, preferredSources);
        }

        /// <summary>Replace the entire record set in memory (used by the Phase 3 offline repository
        /// builder to persist a portable index whose records carry relative paths). Does not parse
        /// files; callers supply fully-formed records. Commit() persists atomically.</summary>
        public void ReplaceAll(IEnumerable<InfRecord> records)
        {
            _records = (records ?? Enumerable.Empty<InfRecord>()).ToList();
            RebuildIndex();
            NeedsRebuild = false;
        }

        public List<MatchCandidate> Query(IList<string> deviceHardwareIds, IList<string> deviceCompatibleIds, OsTarget target, IEnumerable<string> preferredSources)
        {
            return CreateRanker(preferredSources).Rank(deviceHardwareIds, deviceCompatibleIds, target);
        }

        // ----- incremental update -----

        public IndexUpdateReport UpdateFromDirectory(string root, string sourceName, OsTarget target)
        {
            return UpdateFromDirectory(root, sourceName, target, CancellationToken.None);
        }

        /// <summary>
        /// Incremental update with cancellation. The token is checked between directories and
        /// before each parse so a long sweep over a large driver library can be cancelled promptly.
        /// A cancelled update leaves the on-disk index at the last committed state (see Commit).
        /// </summary>
        public IndexUpdateReport UpdateFromDirectory(string root, string sourceName, OsTarget target, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var report = new IndexUpdateReport();
            if (NeedsRebuild)
            {
                _records.Clear();
                _index.Clear();
                report.Rebuilt = true;
            }
            root = Path.GetFullPath(root);
            var existing = new Dictionary<string, InfRecord>(StringComparer.OrdinalIgnoreCase);
            foreach (var r in _records)
                if (string.Equals(r.SourceName, sourceName, StringComparison.OrdinalIgnoreCase))
                    existing[r.Path] = r;

            var onDisk = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (Directory.Exists(root))
            {
                foreach (var f in EnumerateInfs(root, ct))
                    onDisk[f] = Path.GetFullPath(f);
            }

            // Deletes: existing records whose file is gone.
            var survivors = new List<InfRecord>();
            foreach (var kv in existing)
            {
                if (onDisk.ContainsKey(kv.Key)) survivors.Add(kv.Value);
                else report.Deleted++;
            }
            // Unchanged: quick fingerprint match (no reparse needed).
            var unchangedFps = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var rec in survivors)
            {
                string quick;
                if (TryQuickKey(rec.Path, out quick) && quick == rec.Fingerprint)
                {
                    unchangedFps[rec.Path] = rec.Fingerprint;
                    report.Unchanged++;
                }
            }
            // Drop every record that belongs to this source, then re-add survivors + new files.
            // (Unchanged survivors are re-added as-is; modified survivors are re-parsed.)
            _records.RemoveAll(delegate(InfRecord r)
            {
                return string.Equals(r.SourceName, sourceName, StringComparison.OrdinalIgnoreCase);
            });
            foreach (var rec in survivors)
            {
                ct.ThrowIfCancellationRequested();
                if (unchangedFps.ContainsKey(rec.Path)) _records.Add(rec);
                else if (onDisk.ContainsKey(rec.Path))
                {
                    var reparsed = SafeParse(rec.Path, sourceName);
                    if (reparsed != null) { _records.Add(reparsed); report.Modified++; }
                }
            }
            // Adds: files on disk that were not previously known for this source.
            foreach (var kv in onDisk)
            {
                ct.ThrowIfCancellationRequested();
                if (existing.ContainsKey(kv.Key)) continue;
                var reparsed = SafeParse(kv.Key, sourceName);
                if (reparsed != null) { _records.Add(reparsed); report.Added++; }
            }
            RebuildIndex();
            NeedsRebuild = false;
            return report;
        }

        private static IEnumerable<string> EnumerateInfs(string root, CancellationToken ct)
        {
            var stack = new Stack<string>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                ct.ThrowIfCancellationRequested();
                string dir = stack.Pop();
                string[] files;
                string[] dirs;
                try
                {
                    files = Directory.GetFiles(dir, "*.inf");
                    dirs = Directory.GetDirectories(dir);
                }
                catch { continue; }
                foreach (var f in files) yield return f;
                foreach (var d in dirs) stack.Push(d);
            }
        }

        private static InfRecord SafeParse(string path, string sourceName)
        {
            try { return InfParser.Parse(path, sourceName); }
            catch { return null; }
        }

        private static bool TryQuickKey(string path, out string quick)
        {
            quick = "";
            try
            {
                var fi = new FileInfo(path);
                if (!fi.Exists) return false;
                quick = fi.Length.ToString(CultureInfo.InvariantCulture) + "|" + fi.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture);
                return true;
            }
            catch { return false; }
        }

        public void Dispose() { }
    }

    public sealed class IndexUpdateReport
    {
        public int Added, Modified, Deleted, Unchanged;
        public bool Rebuilt;
        public int Total { get { return Added + Modified + Deleted + Unchanged; } }
    }

    // =================================================================================
    //  pnputil install planner (path whitelist, dedup, argument safety, exit-code model)
    // =================================================================================

    public enum InstallOutcome { Success, SuccessReboot, Skipped, InvalidParameter, Timeout, Failure }

    public sealed class InstallResult
    {
        public int ExitCode = -1;
        public InstallOutcome Outcome = InstallOutcome.Failure;
        public bool TimedOut;
        public string Output = "";
    }

    public sealed class InstallPlanItem
    {
        public string InfPath = "";
        public int Order;
        public string SourceRoot = "";
        public string ContentHash = "";
        public string SkipReason = ""; // non-empty when the item was rejected during planning
    }

    public sealed class InstallPlan
    {
        public List<InstallPlanItem> Items = new List<InstallPlanItem>();
        public int Accepted;
        public int Skipped;
    }

    /// <summary>
    /// Builds a safe, deduplicated pnputil install plan from candidate INF records. The planner
    /// never executes pnputil; it produces an ordered plan plus a result model for the runner.
    /// Safety: every accepted path must resolve under an approved source root (whitelist), end in
    /// .inf, and contain no quote / control characters that could break pnputil argument parsing.
    /// </summary>
    public static class InstallPlanner
    {
        /// <summary>Map a pnputil /add-driver exit code to a result outcome.</summary>
        public static InstallOutcome ClassifyExitCode(int exitCode, bool timedOut)
        {
            if (timedOut) return InstallOutcome.Timeout;
            switch (exitCode)
            {
                case 0: return InstallOutcome.Success;
                case 259: return InstallOutcome.Skipped;       // STILL_ACTIVE / no applicable device
                case 3010: return InstallOutcome.SuccessReboot;
                case 87: return InstallOutcome.InvalidParameter;
                case -1: return InstallOutcome.Failure;        // never started / unknown
                default: return exitCode > 0 ? InstallOutcome.Failure : InstallOutcome.Failure;
            }
        }

        public static InstallResult ResultFrom(int exitCode, bool timedOut, string output)
        {
            return new InstallResult { ExitCode = exitCode, TimedOut = timedOut, Output = output ?? "", Outcome = ClassifyExitCode(exitCode, timedOut) };
        }

        /// <summary>The canonical, safe pnputil argument string for an accepted item.</summary>
        public static string BuildArguments(string infPath)
        {
            return "/add-driver \"" + infPath + "\" /install";
        }

        public static InstallPlan BuildPlan(IEnumerable<InfRecord> candidates, IEnumerable<string> approvedRoots, bool dedupeByContent)
        {
            var plan = new InstallPlan();
            var roots = new List<string>();
            foreach (var r in approvedRoots ?? Enumerable.Empty<string>())
            {
                if (string.IsNullOrWhiteSpace(r)) continue;
                try { roots.Add(Path.GetFullPath(r).TrimEnd('\\') + "\\"); } catch { }
            }
            var seenPath = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var seenHash = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int order = 0;
            foreach (var rec in candidates ?? Enumerable.Empty<InfRecord>())
            {
                var item = new InstallPlanItem { InfPath = rec.Path ?? "", ContentHash = rec.ContentHash ?? "" };
                string reason = Validate(rec.Path, roots);
                if (reason != null)
                {
                    item.SkipReason = reason;
                    plan.Items.Add(item); plan.Skipped++;
                    continue;
                }
                if (!seenPath.Add(rec.Path)) { item.SkipReason = "duplicate path"; plan.Items.Add(item); plan.Skipped++; continue; }
                if (dedupeByContent && rec.ContentHash.Length > 0 && !seenHash.Add(rec.ContentHash))
                {
                    item.SkipReason = "duplicate content";
                    plan.Items.Add(item); plan.Skipped++;
                    continue;
                }
                item.Order = ++order;
                item.SourceRoot = FindRoot(rec.Path, roots);
                plan.Items.Add(item); plan.Accepted++;
            }
            return plan;
        }

        private static string Validate(string path, List<string> roots)
        {
            if (string.IsNullOrWhiteSpace(path)) return "empty path";
            string full;
            try { full = Path.GetFullPath(path); }
            catch { return "invalid path"; }
            if (!full.EndsWith(".inf", StringComparison.OrdinalIgnoreCase)) return "not an .inf";
            foreach (char c in full)
                if (c == '"' || c < 0x20) return "unsafe characters";
            if (roots.Count == 0) return "no approved source root";
            if (FindRoot(full, roots) == null) return "outside approved source root";
            return null;
        }

        private static string FindRoot(string full, List<string> roots)
        {
            string f = full.TrimEnd('\\') + "\\";
            foreach (var r in roots)
                if (f.StartsWith(r, StringComparison.OrdinalIgnoreCase)) return r.TrimEnd('\\');
            return null;
        }
    }
}
