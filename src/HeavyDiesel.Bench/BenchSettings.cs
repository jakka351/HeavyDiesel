// =============================================================================
//  BenchSettings.cs - the handful of things the bench app remembers between runs.
//
//  Stored as key=value lines in %APPDATA%\HeavyDiesel\bench.ini. No registry, no
//  XML settings machinery, nothing that needs elevation, and readable in Notepad
//  when a technician asks "what did it claim on my bus".
//
//  THE IDENTITY NUMBER IS GENERATED ONCE AND KEPT. Two copies of this app on one
//  bus with the same NAME cannot resolve an address contention between them, so
//  the identity is drawn at random on first run and persisted. The manufacturer
//  code is NOT invented: it defaults to 0, which is "no assignment", and the
//  address-claim machine says so in its log on every start until a real
//  SAE-assigned code is entered.
// =============================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using HeavyDiesel.J1939;

namespace HeavyDiesel.Bench
{
    internal sealed class BenchSettings
    {
        private static readonly Random _random = new Random();

        public string VendorApi = string.Empty;
        public int DeviceId = -1;
        public string ProtocolString = string.Empty;
        public bool ClaimAddress = true;

        /// <summary>SAE manufacturer code, 0..2047. 0 means "not assigned" and is reported as such.</summary>
        public ushort ManufacturerCode = 0;

        /// <summary>21-bit identity, drawn at random on first run so two bench installs never share a NAME.</summary>
        public uint IdentityNumber = 0;

        /// <summary>J1939-81 industry group. On-highway by default.</summary>
        public IndustryGroup IndustryGroup = IndustryGroup.OnHighway;

        /// <summary>
        /// NAME function code. 255 is "not available" per J1939 convention and is the honest
        /// default: this project does not carry the SAE function-code table, and announcing
        /// the tool as some other kind of device would be a lie on a customer's bus. Enter
        /// the off-board diagnostic service tool code from J1939-81 Appendix B if you have it.
        /// </summary>
        public byte Function = 255;

        public static string FilePath
        {
            get
            {
                string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "HeavyDiesel");
                return Path.Combine(dir, "bench.ini");
            }
        }

        public static BenchSettings Load()
        {
            BenchSettings s = new BenchSettings();
            try
            {
                if (File.Exists(FilePath))
                {
                    foreach (string raw in File.ReadAllLines(FilePath))
                    {
                        string line = raw.Trim();
                        if (line.Length == 0 || line[0] == ';' || line[0] == '#') continue;
                        int eq = line.IndexOf('=');
                        if (eq <= 0) continue;
                        string key = line.Substring(0, eq).Trim();
                        string value = line.Substring(eq + 1).Trim();
                        s.Apply(key, value);
                    }
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }

            if (s.IdentityNumber == 0)
            {
                // 1..MAX, never 0: an all-zero identity looks like an unconfigured node.
                s.IdentityNumber = (uint)_random.Next(1, (int)J1939Name.MAX_IDENTITY_NUMBER);
                s.Save();
            }

            return s;
        }

        private void Apply(string key, string value)
        {
            int i;
            uint u;
            switch (key.ToUpperInvariant())
            {
                case "VENDORAPI": VendorApi = value; break;
                case "DEVICEID": if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out i)) DeviceId = i; break;
                case "PROTOCOLSTRING": ProtocolString = value; break;
                case "CLAIMADDRESS": ClaimAddress = value == "1" || value.Equals("true", StringComparison.OrdinalIgnoreCase); break;
                case "MANUFACTURERCODE":
                    if (uint.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out u) && u <= J1939Name.MAX_MANUFACTURER_CODE) ManufacturerCode = (ushort)u;
                    break;
                case "IDENTITYNUMBER":
                    if (uint.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out u) && u <= J1939Name.MAX_IDENTITY_NUMBER) IdentityNumber = u;
                    break;
                case "INDUSTRYGROUP":
                    if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out i) && i >= 0 && i <= J1939Name.MAX_INDUSTRY_GROUP) IndustryGroup = (IndustryGroup)i;
                    break;
                case "FUNCTION":
                    if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out i) && i >= 0 && i <= 255) Function = (byte)i;
                    break;
            }
        }

        public void Save()
        {
            try
            {
                string dir = Path.GetDirectoryName(FilePath);
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

                StringBuilder sb = new StringBuilder();
                sb.AppendLine("; HeavyDiesel.Bench settings. Edit by hand if you like; the app rewrites this file on exit.");
                sb.AppendLine("VendorApi=" + VendorApi);
                sb.AppendLine("DeviceId=" + DeviceId.ToString(CultureInfo.InvariantCulture));
                sb.AppendLine("ProtocolString=" + ProtocolString);
                sb.AppendLine("ClaimAddress=" + (ClaimAddress ? "1" : "0"));
                sb.AppendLine("; J1939-81 NAME fields. ManufacturerCode 0 = not assigned (the claim log will say so).");
                sb.AppendLine("ManufacturerCode=" + ManufacturerCode.ToString(CultureInfo.InvariantCulture));
                sb.AppendLine("IdentityNumber=" + IdentityNumber.ToString(CultureInfo.InvariantCulture));
                sb.AppendLine("IndustryGroup=" + ((int)IndustryGroup).ToString(CultureInfo.InvariantCulture));
                sb.AppendLine("Function=" + Function.ToString(CultureInfo.InvariantCulture));
                File.WriteAllText(FilePath, sb.ToString());
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        /// <summary>The NAME this bench will announce, built from the stored fields.</summary>
        public J1939Name BuildName()
        {
            return J1939Name.CreateServiceTool(ManufacturerCode, IdentityNumber, IndustryGroup, Function);
        }

        public IList<string> DescribeIdentity()
        {
            List<string> lines = new List<string>();
            lines.Add("NAME this bench announces when it claims an address:");
            lines.Add("  " + BuildName().ToString());
            if (ManufacturerCode == 0)
            {
                lines.Add("  ManufacturerCode is 0 (no SAE assignment). Enter your real code in " + FilePath + ".");
            }
            if (Function == 255)
            {
                lines.Add("  Function is 255 (not available). Enter the J1939-81 service-tool function code if you have the table.");
            }
            return lines;
        }
    }
}
