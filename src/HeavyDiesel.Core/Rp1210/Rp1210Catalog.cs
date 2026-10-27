// ///////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
//  Rp1210Catalog — enumerate the RP1210 adapters installed on this machine.
//
//  RP1210 adapters self-register in %WINDIR%\RP121032.INI, which lists the installed
//  vendor APIs.  Each API named there has its own %WINDIR%\<API>.INI describing the
//  devices and protocols it supports.  Enumerating adapters is therefore a matter of
//  parsing INI files rather than probing hardware — no device needs to be plugged in,
//  and nothing here touches the network.
//
//  This is the single highest-value feature for adoption: a heavy workshop already
//  owns a Nexiq USB-Link, DG DPA5, DrewLinQ or Jaltest Data-Link because their OEM
//  software requires one.  If a tool built on this lists the adapter they already have, the
//  evaluation cost is a download.
//
//  Ported from the TU-RP1210 reference implementation (RP1210Select.py):
//      Copyright (c) 2017-2018 Jeremy Daily, The University of Tulsa
//      Used under the MIT License terms of that project's dual licence.
//
//  Deliberately dependency-free and fully offline — see the Heavy offline mandate.
// ///////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace HeavyDiesel.Rp1210
{
    /// <summary>One protocol a device can speak, as declared in the vendor INI.</summary>
    public sealed class Rp1210ProtocolInfo
    {
        /// <summary>The string handed to RP1210_ClientConnect, e.g. "J1939" or "J1708".</summary>
        public string ProtocolString { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        /// <summary>Declared speeds, e.g. 250 / 500 for J1939. May be empty.</summary>
        public List<string> Speeds { get; } = new List<string>();

        /// <summary>
        /// Device IDs that speak this protocol, from the section's <c>Devices=</c> key.
        /// Empty means the INI did not restrict it.
        /// </summary>
        public List<int> DeviceIds { get; } = new List<int>();

        /// <summary>
        /// True when <paramref name="deviceId"/> is declared as speaking this protocol.
        /// </summary>
        /// <remarks>
        /// An EMPTY DeviceIds list means the vendor INI stated no restriction, which is
        /// read as "all devices" — the permissive reading, because the alternative would
        /// hide working adapters whose INI simply omits the optional key. A NON-empty list
        /// is honoured strictly: Caterpillar's CCARP32 declares "CAT" on devices
        /// 1,3,4,5,6,7,8 but not 2, and offering device 2 on CAT would produce a
        /// ClientConnect failure with nothing to explain it.
        /// </remarks>
        public bool SupportsDevice(int deviceId)
        {
            if (DeviceIds.Count == 0) return true;

            for (int i = 0; i < DeviceIds.Count; i++)
                if (DeviceIds[i] == deviceId) return true;

            return false;
        }

        public override string ToString() =>
            string.IsNullOrEmpty(Description) ? ProtocolString : ProtocolString + " — " + Description;
    }

    /// <summary>A physical adapter exposed by a vendor API.</summary>
    public sealed class Rp1210DeviceInfo
    {
        public int DeviceId { get; set; }
        public string Description { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;

        public override string ToString()
        {
            string label = !string.IsNullOrEmpty(Description) ? Description
                         : !string.IsNullOrEmpty(Name) ? Name
                         : "Device " + DeviceId.ToString(CultureInfo.InvariantCulture);
            return label + " (ID " + DeviceId.ToString(CultureInfo.InvariantCulture) + ")";
        }
    }

    /// <summary>A vendor RP1210 API — one DLL, with its devices and protocols.</summary>
    public sealed class Rp1210VendorInfo
    {
        /// <summary>API name from RP121032.INI — this is the DLL base name, so the DLL
        /// to load is &lt;ApiName&gt;.dll.</summary>
        public string ApiName { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        /// <summary>"A", "B" or "C". RP1210C adds multi-client, better J1939 address
        /// claim handling and automatic CAN baud detection.</summary>
        public string Version { get; set; } = string.Empty;
        public bool AutoDetectCapable { get; set; }
        public List<Rp1210DeviceInfo> Devices { get; } = new List<Rp1210DeviceInfo>();
        public List<Rp1210ProtocolInfo> Protocols { get; } = new List<Rp1210ProtocolInfo>();

        public override string ToString() =>
            (string.IsNullOrEmpty(Name) ? ApiName : Name) +
            (string.IsNullOrEmpty(Version) ? "" : " [RP1210" + Version + "]");
    }

    /// <summary>
    /// Reads the machine's RP1210 registration files.  Pure file I/O — safe to call on
    /// a machine with no adapters installed, in which case the vendor list is empty.
    /// </summary>
    public static class Rp1210Catalog
    {
        /// <summary>Enumerate every registered RP1210 vendor API on this machine.
        /// Returns an empty list (never null) when RP121032.INI is absent, which simply
        /// means no RP1210-compliant adapter driver is installed.</summary>
        public static List<Rp1210VendorInfo> EnumerateVendors()
        {
            var result = new List<Rp1210VendorInfo>();

            string windir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            if (string.IsNullOrEmpty(windir)) return result;

            string rootIni = Path.Combine(windir, "RP121032.INI");
            if (!File.Exists(rootIni)) return result;

            // [RP1210Support] APIImplementations=NULN2R32,DGDPA5MA,DrewLinQ,...
            var root = IniFile.Parse(rootIni);
            string apis = root.Get("RP1210Support", "APIImplementations");
            if (string.IsNullOrWhiteSpace(apis)) return result;

            foreach (string rawApi in apis.Split(','))
            {
                string api = rawApi.Trim();
                if (api.Length == 0) continue;

                var vendor = ReadVendor(windir, api);
                if (vendor != null) result.Add(vendor);
            }
            return result;
        }

        /// <summary>Read one vendor INI (%WINDIR%\&lt;api&gt;.INI). Returns null if absent
        /// — a stale entry in RP121032.INI for an uninstalled driver is common.</summary>
        private static Rp1210VendorInfo ReadVendor(string windir, string apiName)
        {
            string path = Path.Combine(windir, apiName + ".INI");
            if (!File.Exists(path)) return null;

            IniFile ini;
            try { ini = IniFile.Parse(path); }
            catch { return null; }   // malformed vendor INI must not break enumeration

            var vendor = new Rp1210VendorInfo
            {
                ApiName = apiName,
                Name = ini.Get("VendorInformation", "Name"),
                Version = ini.Get("VendorInformation", "RP1210"),
                AutoDetectCapable = ParseBoolish(ini.Get("VendorInformation", "AutoDetectCapable")),
            };

            // Devices=1,2,3 → each has a [DeviceInformationN] section.
            foreach (string idText in SplitList(ini.Get("VendorInformation", "Devices")))
            {
                int id;
                if (!int.TryParse(idText, NumberStyles.Integer, CultureInfo.InvariantCulture, out id))
                    continue;

                string section = "DeviceInformation" + idText;
                vendor.Devices.Add(new Rp1210DeviceInfo
                {
                    DeviceId = id,
                    Description = ini.Get(section, "DeviceDescription"),
                    Name = ini.Get(section, "DeviceName"),
                });
            }

            // Protocols=1,2,3 → each has a [ProtocolInformationN] section.
            foreach (string idText in SplitList(ini.Get("VendorInformation", "Protocols")))
            {
                string section = "ProtocolInformation" + idText;
                string protoString = ini.Get(section, "ProtocolString");
                if (string.IsNullOrWhiteSpace(protoString)) continue;

                var proto = new Rp1210ProtocolInfo
                {
                    ProtocolString = protoString.Trim(),
                    Description = ini.Get(section, "ProtocolDescription"),
                };
                foreach (string s in SplitList(ini.Get(section, "ProtocolSpeed")))
                    proto.Speeds.Add(s);

                // [ProtocolInformationN] Devices=1,3,4 — WHICH devices speak this protocol.
                // Not every device of a vendor speaks every protocol the vendor declares:
                // Caterpillar's CCARP32 puts "CAT" on devices 1,3,4,5,6,7,8 but NOT 2, and
                // "J1939" on 3-8 but NOT 1 or 2. Without this key a (device x protocol)
                // listing offers combinations the vendor says do not exist, and the only
                // symptom is a ClientConnect failure the technician cannot account for.
                foreach (string s in SplitList(ini.Get(section, "Devices")))
                {
                    int devId;
                    if (int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out devId))
                        proto.DeviceIds.Add(devId);
                }

                vendor.Protocols.Add(proto);
            }

            return vendor;
        }

        private static IEnumerable<string> SplitList(string csv)
        {
            if (string.IsNullOrWhiteSpace(csv)) yield break;
            foreach (string part in csv.Split(','))
            {
                string t = part.Trim();
                if (t.Length > 0) yield return t;
            }
        }

        private static bool ParseBoolish(string v)
        {
            if (string.IsNullOrWhiteSpace(v)) return false;
            v = v.Trim();
            return v.Equals("1", StringComparison.Ordinal)
                || v.Equals("true", StringComparison.OrdinalIgnoreCase)
                || v.Equals("yes", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Minimal INI reader.  Deliberately hand-rolled rather than P/Invoking
        /// GetPrivateProfileString: the Win32 API silently truncates long values
        /// (vendor Devices= lists can be long) and applies %WINDIR% path quirks.
        /// Case-insensitive on both section and key, as INI convention expects.
        /// </summary>
        private sealed class IniFile
        {
            private readonly Dictionary<string, Dictionary<string, string>> _sections =
                new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);

            public static IniFile Parse(string path)
            {
                var ini = new IniFile();
                Dictionary<string, string> current = null;

                foreach (string rawLine in File.ReadAllLines(path))
                {
                    string line = rawLine.Trim();
                    if (line.Length == 0) continue;
                    if (line[0] == ';' || line[0] == '#') continue;

                    if (line[0] == '[')
                    {
                        int close = line.IndexOf(']');
                        if (close <= 1) continue;
                        string name = line.Substring(1, close - 1).Trim();
                        if (!ini._sections.TryGetValue(name, out current))
                        {
                            current = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                            ini._sections[name] = current;
                        }
                        continue;
                    }

                    if (current == null) continue;      // key before any section header

                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;

                    string key = line.Substring(0, eq).Trim();
                    string value = line.Substring(eq + 1).Trim();

                    // Strip inline comments only when clearly separated, so values
                    // containing ';' survive.
                    int sc = value.IndexOf(" ;", StringComparison.Ordinal);
                    if (sc >= 0) value = value.Substring(0, sc).Trim();

                    if (key.Length > 0) current[key] = value;
                }
                return ini;
            }

            public string Get(string section, string key)
            {
                Dictionary<string, string> s;
                if (!_sections.TryGetValue(section, out s)) return string.Empty;
                string v;
                return s.TryGetValue(key, out v) ? v : string.Empty;
            }
        }
    }
}
