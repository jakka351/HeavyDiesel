// ///////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
//  Rp1210CdlSupport — which installed RP1210 adapters can reach Cat Data Link.
//
//  Cat Data Link rides standard RP1210: the protocol string handed to
//  RP1210_ClientConnect is "CAT" (CAT-ET-RE note 05; corroborated by
//  %WINDIR%\CA3rp32.ini, which declares ProtocolString=CAT with
//  ProtocolDescription="Cat Data Link").  So a tool built on this needs no CDL hardware build
//  and no proprietary physical layer.
//
//  That is a smaller win than it first appears, and this file exists mostly to
//  keep it honest.  Every RP1210 API observed to declare "CAT" is supplied by
//  Caterpillar; a Bosch VCM3, Detroit Diesel or Dearborn adapter declares only
//  J1939 / J1708 / CAN and cannot open CDL at all.  A fitter who owns a
//  perfectly good heavy-duty adapter and is told "CDL unavailable" needs to be
//  told WHY, not shown an empty list.
//
//  Reads only the machine's RP1210 INI registration through the shared
//  Rp1210Catalog.  No hardware is probed, no DLL is loaded, nothing touches the
//  network — safe to call at UI construction time and safe in a mine pit.
// ///////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace HeavyDiesel.Rp1210
{
    /// <summary>
    /// One (vendor API, device) pair that can be asked to open Cat Data Link,
    /// together with the protocol entry that says so.
    /// </summary>
    public sealed class Rp1210CdlEndpoint
    {
        /// <summary>The vendor API declaring CDL. Its <see cref="Rp1210VendorInfo.ApiName"/>
        /// is the DLL base name to load.</summary>
        public Rp1210VendorInfo Vendor { get; set; }

        /// <summary>The device to pass as RP1210_ClientConnect's DeviceID.</summary>
        public Rp1210DeviceInfo Device { get; set; }

        /// <summary>The vendor INI protocol entry whose ProtocolString is "CAT".</summary>
        public Rp1210ProtocolInfo Protocol { get; set; }

        /// <summary>DLL base name of the vendor API.</summary>
        public string ApiName
        {
            get { return Vendor == null ? string.Empty : Vendor.ApiName; }
        }

        /// <summary>Device ID for RP1210_ClientConnect.</summary>
        public int DeviceId
        {
            get { return Device == null ? -1 : Device.DeviceId; }
        }

        /// <summary>Protocol string for RP1210_ClientConnect — always "CAT".</summary>
        public string ProtocolString
        {
            get { return Protocol == null ? Rp1210CdlSupport.CDL_PROTOCOL_STRING : Protocol.ProtocolString; }
        }

        public override string ToString()
        {
            return string.Format(
                CultureInfo.InvariantCulture,
                "{0} — {1} [{2}, DeviceID {3}, protocol \"{4}\"]",
                Vendor == null ? "?" : Vendor.ToString(),
                Device == null ? "?" : Device.ToString(),
                ApiName,
                DeviceId,
                ProtocolString);
        }
    }

    /// <summary>
    /// Cat Data Link capability queries over the shared <see cref="Rp1210Catalog"/>.
    /// Pure INI inspection — see the file header for the offline guarantee.
    /// </summary>
    public static class Rp1210CdlSupport
    {
        /// <summary>The protocol string an RP1210 API must declare to open Cat Data Link,
        /// and the string to pass to RP1210_ClientConnect.</summary>
        public const string CDL_PROTOCOL_STRING = "CAT";

        /// <summary>Human-readable protocol name for the UI.</summary>
        public const string CDL_DISPLAY_NAME = "Cat Data Link";

        // ───────────────────────────────────────────────────────────────────
        // Capability queries
        // ───────────────────────────────────────────────────────────────────

        /// <summary>
        /// True when this vendor API declares the "CAT" protocol string, i.e. it
        /// claims it can open Cat Data Link.  Declaring is not the same as working
        /// — see <see cref="DescribeCdlAvailability()"/>.
        /// </summary>
        public static bool DeclaresCdl(Rp1210VendorInfo vendor)
        {
            return FindCdlProtocol(vendor) != null;
        }

        /// <summary>
        /// The vendor's "CAT" protocol entry, or null if it does not declare one.
        /// Comparison is case-insensitive and trimmed: the RP1210 spec does not
        /// pin the casing and vendor INIs are hand-maintained text files.
        /// </summary>
        public static Rp1210ProtocolInfo FindCdlProtocol(Rp1210VendorInfo vendor)
        {
            if (vendor == null) return null;

            for (int i = 0; i < vendor.Protocols.Count; i++)
            {
                Rp1210ProtocolInfo proto = vendor.Protocols[i];
                if (proto == null || string.IsNullOrEmpty(proto.ProtocolString)) continue;

                if (string.Equals(proto.ProtocolString.Trim(), CDL_PROTOCOL_STRING,
                                  StringComparison.OrdinalIgnoreCase))
                    return proto;
            }
            return null;
        }

        /// <summary>
        /// Every (vendor, device) pair on this machine that can be asked to open
        /// Cat Data Link.  Empty — never null — when no CAT-capable adapter driver
        /// is installed.
        /// </summary>
        /// <remarks>
        /// Granularity caveat, stated because it can produce a wrong entry rather
        /// than a missing one: vendor INIs scope each protocol to a device list
        /// (<c>[ProtocolInformationN] Devices=</c>), but <see cref="Rp1210Catalog"/>
        /// reads protocols at API level only.  So every device of a CAT-declaring
        /// API is listed here.  On the Caterpillar APIs observed so far each API
        /// exposes a single device, which makes the distinction moot, but a
        /// multi-device CAT API could list a device that cannot actually open CDL.
        /// Only RP1210_ClientConnect settles it.
        /// </remarks>
        public static List<Rp1210CdlEndpoint> EnumerateCdlCapable()
        {
            return EnumerateCdlCapable(Rp1210Catalog.EnumerateVendors());
        }

        /// <summary>
        /// As <see cref="EnumerateCdlCapable()"/>, over a vendor list the caller has
        /// already enumerated — avoids re-reading the INIs when the UI is also
        /// showing the full adapter list.
        /// </summary>
        public static List<Rp1210CdlEndpoint> EnumerateCdlCapable(IEnumerable<Rp1210VendorInfo> vendors)
        {
            var result = new List<Rp1210CdlEndpoint>();
            if (vendors == null) return result;

            foreach (Rp1210VendorInfo vendor in vendors)
            {
                Rp1210ProtocolInfo cdl = FindCdlProtocol(vendor);
                if (cdl == null) continue;

                foreach (Rp1210DeviceInfo device in vendor.Devices)
                {
                    if (device == null) continue;

                    // The vendor declares CDL per DEVICE, and the coverage is genuinely
                    // uneven — Caterpillar's CCARP32 puts "CAT" on devices 1,3,4,5,6,7,8
                    // but NOT on device 2. Listing a device the INI excludes would offer an
                    // endpoint that can only fail at ClientConnect.
                    if (!cdl.SupportsDevice(device.DeviceId)) continue;

                    result.Add(new Rp1210CdlEndpoint
                    {
                        Vendor = vendor,
                        Device = device,
                        Protocol = cdl,
                    });
                }
            }
            return result;
        }

        // ───────────────────────────────────────────────────────────────────
        // UI summary
        // ───────────────────────────────────────────────────────────────────

        /// <summary>
        /// Plain-text summary of Cat Data Link availability for the UI.  Always
        /// explains the outcome: when nothing CAT-capable is installed it says so
        /// and says why the adapters that ARE installed cannot substitute.
        /// </summary>
        public static string DescribeCdlAvailability()
        {
            return DescribeCdlAvailability(Rp1210Catalog.EnumerateVendors());
        }

        /// <summary>
        /// As <see cref="DescribeCdlAvailability()"/>, over an already-enumerated
        /// vendor list.
        /// </summary>
        public static string DescribeCdlAvailability(IList<Rp1210VendorInfo> vendors)
        {
            var sb = new StringBuilder();
            sb.AppendLine(CDL_DISPLAY_NAME + " (CDL) over RP1210");
            sb.AppendLine("--------------------------------------");

            if (vendors == null || vendors.Count == 0)
            {
                sb.AppendLine("No RP1210 adapter driver is registered on this machine.");
                sb.AppendLine("(%WINDIR%\\RP121032.INI is missing, empty, or lists no usable API.)");
                sb.AppendLine();
                AppendRequirement(sb);
                return sb.ToString().TrimEnd();
            }

            List<Rp1210CdlEndpoint> endpoints = EnumerateCdlCapable(vendors);

            if (endpoints.Count > 0)
            {
                sb.AppendLine(Inv("{0} CDL-capable adapter {1} found:",
                                  endpoints.Count, endpoints.Count == 1 ? "endpoint" : "endpoints"));
                foreach (Rp1210CdlEndpoint ep in endpoints)
                    sb.AppendLine("  * " + ep);

                sb.AppendLine();
                sb.AppendLine("Declared, not proven. An INI entry says the driver accepts the \"CAT\"");
                sb.AppendLine("protocol string; it does not prove this tool can exchange valid CDL frames");
                sb.AppendLine("with a real ECM. Unverified against Caterpillar iron.");
            }
            else
            {
                // Distinguish "declares CAT but lists no device" from "declares no CAT
                // at all" — they need different fixes and the user cannot tell them
                // apart from an empty list.
                int declaringButDeviceless = 0;
                for (int i = 0; i < vendors.Count; i++)
                    if (DeclaresCdl(vendors[i]) && vendors[i].Devices.Count == 0)
                        declaringButDeviceless++;

                if (declaringButDeviceless > 0)
                {
                    sb.AppendLine(Inv("{0} installed RP1210 API(s) declare the \"CAT\" protocol but list",
                                      declaringButDeviceless));
                    sb.AppendLine("no devices, so there is nothing to connect to. The vendor INI is");
                    sb.AppendLine("incomplete or the driver was partially uninstalled — reinstall the");
                    sb.AppendLine("Caterpillar adapter driver.");
                }
                else
                {
                    sb.AppendLine(Inv("No CDL-capable adapter is installed. None of the {0} registered",
                                      vendors.Count));
                    sb.AppendLine("RP1210 API(s) declares the \"CAT\" protocol string.");
                }

                sb.AppendLine();
                sb.AppendLine("Installed RP1210 APIs and the protocols they do declare:");
                for (int i = 0; i < vendors.Count; i++)
                    sb.AppendLine("  * " + DescribeVendorProtocols(vendors[i]));

                sb.AppendLine();
                AppendRequirement(sb);
            }

            return sb.ToString().TrimEnd();
        }

        /// <summary>One line: vendor label plus the protocol strings it declares.</summary>
        private static string DescribeVendorProtocols(Rp1210VendorInfo vendor)
        {
            if (vendor == null) return "(null vendor entry)";

            var protocols = new StringBuilder();
            for (int i = 0; i < vendor.Protocols.Count; i++)
            {
                Rp1210ProtocolInfo proto = vendor.Protocols[i];
                if (proto == null || string.IsNullOrEmpty(proto.ProtocolString)) continue;
                if (protocols.Length > 0) protocols.Append(", ");
                protocols.Append(proto.ProtocolString);
            }
            if (protocols.Length == 0) protocols.Append("none declared");

            return Inv("{0} [{1}]: {2}", vendor, vendor.ApiName, protocols);
        }

        /// <summary>
        /// The constraint the RE established (note 05 / CAT_IMPLEMENTATION_PLAN §1).
        /// Repeated wherever CDL is unavailable, because "use a different adapter"
        /// is the only remedy and the user needs to know which one.
        /// </summary>
        /// <remarks>
        /// The constraint is on the ADAPTER, not on who wrote the driver. Note 05
        /// records the CAT-declaring APIs as "all Caterpillar-supplied"; that is
        /// slightly loose. Checked against a bench machine carrying all five,
        /// CA3rp32 / RFSRP32 / CAIPRP32 / CCARP32 carry CompanyName "Caterpillar
        /// Inc.", but CA2RP32 carries "Noregon Systems\Vansco Electronics" and
        /// describes itself as the RP1210A API for the CAII — a third-party driver
        /// for Caterpillar hardware. The wording below reflects that.
        /// </remarks>
        private static void AppendRequirement(StringBuilder sb)
        {
            sb.AppendLine("A Caterpillar-family adapter is required.");
            sb.AppendLine();
            sb.AppendLine("Cat Data Link is carried only by adapters in the Caterpillar family - the");
            sb.AppendLine("CA3, the Communication Adapter II / 3, and the embedded and wireless");
            sb.AppendLine("variants. Theirs are the only RP1210 drivers observed to declare \"CAT\".");
            sb.AppendLine("Most are published by Caterpillar Inc.; the CAII driver (CA2RP32) is");
            sb.AppendLine("published by Noregon/Vansco but still drives Caterpillar hardware.");
            sb.AppendLine();
            sb.AppendLine("General-purpose heavy-duty adapters - Bosch VCM3, Detroit Diesel,");
            sb.AppendLine("Dearborn/Nexiq - declare only J1939, J1708 and CAN. They cannot reach Cat");
            sb.AppendLine("Data Link, and no setting in this tool changes that: the limitation is in the");
            sb.AppendLine("adapter, not in this tool.");
            sb.AppendLine();
            sb.AppendLine("J1939 and J1708/ATA on Caterpillar machines remain available through any");
            sb.AppendLine("adapter that declares those protocols.");
        }

        private static string Inv(string format, params object[] args)
        {
            return string.Format(CultureInfo.InvariantCulture, format, args);
        }
    }
}
