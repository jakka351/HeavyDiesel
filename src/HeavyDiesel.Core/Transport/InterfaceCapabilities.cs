// =============================================================================
//  InterfaceCapabilities.cs - what a link can honestly claim to do.
//
//  Filled in by each IJ1939Link from what the adapter and its driver actually
//  declare (for RP1210: the vendor INI), and UNDER-claimed everywhere they say
//  nothing. The DegradationNotice is the important field: it is the list of
//  things the operator must be told before trusting a reading. J1939Session logs
//  it at start, and the bench app shows it in the adapter panel.
// =============================================================================

using System;

namespace HeavyDiesel.Transport
{
    /// <summary>Protocol families a link can carry. Combine as flags.</summary>
    [Flags]
    public enum ProtocolFlags
    {
        /// <summary>Nothing declared.</summary>
        None = 0,

        /// <summary>SAE J1939 (29-bit CAN at 250 or 500 kbit/s).</summary>
        J1939 = 1 << 0,

        /// <summary>SAE J1587 over the J1708 RS-485 datalink.</summary>
        J1587 = 1 << 1,

        /// <summary>ISO 15765 (ISO-TP) over CAN, where a heavy adapter offers it.</summary>
        Iso15765 = 1 << 2,

        /// <summary>Raw classic CAN with 11-bit identifiers.</summary>
        RawCan11 = 1 << 3,

        /// <summary>Raw classic CAN with 29-bit identifiers.</summary>
        RawCan29 = 1 << 4
    }

    /// <summary>Electrical system the adapter is rated for.</summary>
    public enum VoltageRange
    {
        /// <summary>Not stated by the adapter.</summary>
        Unknown = 0,

        /// <summary>12 V only (light-vehicle hardware).</summary>
        Twelve = 1,

        /// <summary>12 V or 24 V. Every heavy-duty adapter should report this.</summary>
        TwelveOrTwentyFour = 2
    }

    /// <summary>
    /// A link's self-description. Plain data; built once per connection by the link
    /// and rebuilt when something is learned mid-session (for example a driver caught
    /// reassembling transport frames behind the stack's back).
    /// </summary>
    public sealed class InterfaceCapabilities
    {
        /// <summary>Protocol families the adapter declares. What the VENDOR says, not what the API permits.</summary>
        public ProtocolFlags Protocols { get; set; }

        /// <summary>
        /// Hardware receive filters available to the caller, or 0 when the API does not
        /// expose a count (RP1210 does not) and filtering is done in managed code.
        /// </summary>
        public int MaxFilters { get; set; }

        /// <summary>
        /// True when received messages carry an adapter timestamp whose unit is known.
        /// False means every interval - including the J1939-21 transport timers - is
        /// measured on the host clock and includes driver and scheduling latency.
        /// </summary>
        public bool HardwareTimestamps { get; set; }

        /// <summary>True when the driver supports exclusive (block) filters (RP1210B or later).</summary>
        public bool ExclusiveFilters { get; set; }

        /// <summary>True when more than one client may hold the adapter at once (RP1210B or later).</summary>
        public bool MultiClient { get; set; }

        /// <summary>
        /// True when the DRIVER claims it performs J1939-81 address claiming on the
        /// client's behalf. HeavyDiesel runs its own claim regardless; a link that
        /// also claims will show two addresses for one tool, and the session says so.
        /// </summary>
        public bool AssistsAddressClaim { get; set; }

        /// <summary>True when the adapter can switch connector pins under software control (a J2534-2 concept).</summary>
        public bool SupportsPinSelect { get; set; }

        /// <summary>Highest bit rate declared for the connected protocol, in bit/s.</summary>
        public int MaxBaud { get; set; }

        /// <summary>Electrical system the adapter is rated for.</summary>
        public VoltageRange SupportedVoltage { get; set; }

        /// <summary>
        /// Everything the operator must be told before trusting this link, joined into
        /// one technician-readable paragraph. Empty when the link has nothing to warn
        /// about - which is rare, and should be treated with suspicion.
        /// </summary>
        public string DegradationNotice { get; set; }

        /// <summary>Creates an empty, fully under-claimed capability set.</summary>
        public InterfaceCapabilities()
        {
            Protocols = ProtocolFlags.None;
            SupportedVoltage = VoltageRange.Unknown;
            DegradationNotice = string.Empty;
        }

        /// <summary>One-line summary for a status bar or log.</summary>
        /// <returns>The formatted capabilities.</returns>
        public override string ToString()
        {
            return string.Format(System.Globalization.CultureInfo.InvariantCulture,
                "protocols={0} maxBaud={1} hwTimestamps={2} multiClient={3} exclusiveFilters={4} voltage={5}{6}",
                Protocols, MaxBaud, HardwareTimestamps, MultiClient, ExclusiveFilters, SupportedVoltage,
                string.IsNullOrEmpty(DegradationNotice) ? string.Empty : " [degraded: " + DegradationNotice + "]");
        }
    }
}
