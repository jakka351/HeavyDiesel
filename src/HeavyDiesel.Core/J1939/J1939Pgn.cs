// =============================================================================
//  J1939Pgn.cs - SAE J1939 protocol constants (PGNs, control bytes, addresses,
//  J1939-21 timing) and honest PGN description lookup.
//
//  Single source of truth for every wire constant the heavy stack depends on.
//  Nothing here is inferred: values not published in the project spec are NOT
//  present, and Describe() refuses to name a PGN it has no definition for.
// =============================================================================

using System;
using System.Globalization;

namespace HeavyDiesel.J1939
{
    /// <summary>
    /// SAE J1939 parameter group numbers, transport control bytes, reserved
    /// addresses and J1939-21 timing values.
    /// </summary>
    public static class J1939Pgn
    {
        // ---------------------------------------------------------------
        // Transport / network management PGNs (J1939-21, J1939-81)
        // ---------------------------------------------------------------

        /// <summary>TP.CM - Transport Protocol Connection Management, PGN 60416 (0xEC00), PDU1.</summary>
        public const uint TransportProtocolConnectionManagement = 60416;

        /// <summary>TP.DT - Transport Protocol Data Transfer, PGN 60160 (0xEB00), PDU1.</summary>
        public const uint TransportProtocolDataTransfer = 60160;

        /// <summary>ETP.CM - Extended Transport Protocol Connection Management, PGN 51200 (0xC800), PDU1.</summary>
        /// <remarks>
        /// CORRECTED 2026-07-28. This was 51712, which is a DIFFERENT REAL PGN: 51712
        /// (0xCA00) is XFER / "Transfer". The previous value was reached by resolving a
        /// spec discrepancy in favour of the decimal ("51456 (0xC800)") and then deriving
        /// this one from it; the hex was the correct half and the whole pair was shifted
        /// two groups up. Confirmed against two independent sources that agree:
        /// TU-RP1210's J1939db.json (SAE Digital Annex derived) and Diesel Explorer's
        /// J1939_PGN_List. Both list ETP.CM 51200 / ETP.DT 50944, and separately list
        /// 51712 as XFER and 51456 as RQST2 - so the old values were not merely off, they
        /// named other messages. Still unverified against a real ETP capture.
        /// </remarks>
        public const uint ExtendedTransportProtocolConnectionManagement = 51200;

        /// <summary>ETP.DT - Extended Transport Protocol Data Transfer, PGN 50944 (0xC700), PDU1.</summary>
        /// <remarks>
        /// CORRECTED 2026-07-28 from 51456, which is RQST2 / "Request 2". See the note on
        /// ExtendedTransportProtocolConnectionManagement above for the evidence.
        /// </remarks>
        public const uint ExtendedTransportProtocolDataTransfer = 50944;

        /// <summary>Address Claimed / Cannot Claim, PGN 60928 (0xEE00), PDU1. Carries the 64-bit NAME.</summary>
        public const uint AddressClaimed = 60928;

        /// <summary>Request, PGN 59904 (0xEA00), PDU1. Three-byte payload is the requested PGN, LSB first.</summary>
        public const uint Request = 59904;

        /// <summary>Acknowledgement (ACK / NACK / Access Denied / Cannot Respond), PGN 59392 (0xE800).</summary>
        public const uint Acknowledgement = 59392;

        // ---------------------------------------------------------------
        // Diagnostic message PGNs (J1939-73)
        // ---------------------------------------------------------------

        /// <summary>DM1 - active DTCs plus lamp status, PGN 65226 (0xFECA). Broadcast at 1 Hz while codes are active.</summary>
        public const uint Dm1 = 65226;

        /// <summary>DM2 - previously active DTCs, PGN 65227 (0xFECB).</summary>
        public const uint Dm2 = 65227;

        /// <summary>DM3 - clear previously active DTCs, PGN 65228 (0xFECC).</summary>
        public const uint Dm3 = 65228;

        /// <summary>DM4 - freeze frame parameters, PGN 65229 (0xFECD).</summary>
        public const uint Dm4 = 65229;

        /// <summary>DM5 - diagnostic readiness, PGN 65230 (0xFECE).</summary>
        public const uint Dm5 = 65230;

        /// <summary>DM11 - clear active DTCs, PGN 65235 (0xFED3).</summary>
        public const uint Dm11 = 65235;

        /// <summary>DM13 - stop / start broadcast, PGN 57088 (0xDF00).</summary>
        /// <remarks>
        /// CORRECTED 2026-07-28 from 57344, which is CM1 / "Cab Message 1" - a different
        /// real message. Both reference sources agree DM13 is 57088.
        /// </remarks>
        public const uint Dm13 = 57088;

        /// <summary>DM19 - calibration identification and calibration verification number, PGN 54016 (0xD300).</summary>
        /// <remarks>
        /// CORRECTED 2026-07-28 from 54272. This resolves a long-standing disagreement
        /// INSIDE this codebase: J1939Diagnostics used 54016 while this constant said
        /// 54272, so the same message had two values depending on which one a caller
        /// reached for. 54272 (0xD400) is DM18 / "Data Security" - requesting it expecting
        /// calibration identification would ask a module for something else entirely.
        /// Both reference sources agree DM19 is 54016; J1939Diagnostics was right.
        /// </remarks>
        public const uint Dm19 = 54016;

        // ---------------------------------------------------------------
        // TP.CM / ETP.CM control bytes (byte 0 of the TP.CM payload)
        // ---------------------------------------------------------------

        /// <summary>TP.CM_RTS - Request To Send, control byte 0x10 (peer-to-peer session open).</summary>
        public const byte TpCmRts = 0x10;

        /// <summary>TP.CM_CTS - Clear To Send, control byte 0x11.</summary>
        public const byte TpCmCts = 0x11;

        /// <summary>TP.CM_EndOfMsgACK - End of Message Acknowledgement, control byte 0x13.</summary>
        public const byte TpCmEndOfMsgAck = 0x13;

        /// <summary>TP.CM_BAM - Broadcast Announce Message, control byte 0x20.</summary>
        public const byte TpCmBam = 0x20;

        /// <summary>TP.Conn_Abort - connection abort, control byte 0xFF.</summary>
        public const byte TpCmAbort = 0xFF;

        // ---------------------------------------------------------------
        // Reserved addresses (J1939-81)
        // ---------------------------------------------------------------

        /// <summary>Preferred source address for the first off-board diagnostic service tool (249).</summary>
        public const byte ServiceTool1Address = 249;

        /// <summary>Source address for a second off-board diagnostic service tool (250). Fall-back when 249 is taken.</summary>
        public const byte ServiceTool2Address = 250;

        /// <summary>Null address (254). Used as the source address by an ECU that failed to claim.</summary>
        public const byte NullAddress = 254;

        /// <summary>Global address (255). Destination for broadcast traffic.</summary>
        public const byte GlobalAddress = 255;

        // ---------------------------------------------------------------
        // Transport sizing (J1939-21)
        // ---------------------------------------------------------------

        /// <summary>Payload bytes carried by a single TP.DT frame: byte 0 is the sequence number, bytes 1..7 are data.</summary>
        public const int TransportProtocolBytesPerPacket = 7;

        /// <summary>Maximum TP.DT packets in one standard transport session (sequence numbers 1..255).</summary>
        public const int MaxTransportProtocolPackets = 255;

        /// <summary>Maximum standard transport protocol payload: 255 packets x 7 bytes = 1785 bytes.</summary>
        public const int MaxTransportProtocolPayloadBytes = 1785;

        /// <summary>
        /// Maximum extended transport protocol payload: 16777215 packets x 7 bytes = 117440505 bytes (~117 MB).
        /// ETP.CM carries a 32-bit total message size and a 24-bit packet offset, so the offset field is the binding limit.
        /// </summary>
        public const int MaxExtendedTransportProtocolPayloadBytes = 117440505;

        // ---------------------------------------------------------------
        // Timing (milliseconds)
        //
        // Per-timer values from the published SAE J1939-21 timing table.
        //
        // CORRECTED 2026-07-26. An earlier project brief flattened these to
        // "Th 750, T1..T4 all 1250", which is wrong: 750 ms is T1, not Th, and
        // T2/T3/T4 are not a single figure. These are now the standard values.
        //
        // Still worth a bench check against a real ECU, because a wrong timer
        // tears down a live transfer. Direction of harm is asymmetric: aborting
        // early loses the transfer outright, aborting late only costs time - so
        // if a future discrepancy is genuinely unresolvable, prefer the longer value.
        // ---------------------------------------------------------------

        /// <summary>Tr - response time: maximum delay before a device responds to a transport frame (200 ms).</summary>
        public const int ResponseTimeTrMs = 200;

        /// <summary>Th - hold time: maximum time a receiver may hold a connection open between CTS frames (500 ms).</summary>
        public const int HoldTimeThMs = 500;

        /// <summary>T1 - connection timeout while awaiting the next TP.DT of a sequence (750 ms).</summary>
        public const int ConnectionTimeoutT1Ms = 750;

        /// <summary>T2 - connection timeout at the receiver while awaiting the first TP.DT after CTS (1250 ms).</summary>
        public const int ConnectionTimeoutT2Ms = 1250;

        /// <summary>T3 - connection timeout at the sender while awaiting CTS or EndOfMsgACK (1250 ms).</summary>
        public const int ConnectionTimeoutT3Ms = 1250;

        /// <summary>T4 - connection timeout while a "hold connection open" CTS is in force (1050 ms).</summary>
        public const int ConnectionTimeoutT4Ms = 1050;

        /// <summary>Nominal spacing between consecutive TP.DT frames on transmit (50 ms).</summary>
        public const int InterPacketDelayMs = 50;

        // ---------------------------------------------------------------
        // Description lookup
        // ---------------------------------------------------------------

        /// <summary>
        /// Attempts to resolve a human-readable name for <paramref name="pgn"/>.
        /// Returns false and sets <paramref name="name"/> to null when no definition
        /// is held, so callers can branch on "unknown" instead of parsing text.
        /// </summary>
        /// <param name="pgn">Parameter group number.</param>
        /// <param name="name">Receives the definition name, or null when unknown.</param>
        /// <returns>True when a definition is held for this PGN.</returns>
        public static bool TryDescribe(uint pgn, out string name)
        {
            switch (pgn)
            {
                case TransportProtocolConnectionManagement:
                    name = "TP.CM - Transport Protocol Connection Management";
                    return true;
                case TransportProtocolDataTransfer:
                    name = "TP.DT - Transport Protocol Data Transfer";
                    return true;
                case ExtendedTransportProtocolConnectionManagement:
                    name = "ETP.CM - Extended Transport Protocol Connection Management";
                    return true;
                case ExtendedTransportProtocolDataTransfer:
                    name = "ETP.DT - Extended Transport Protocol Data Transfer";
                    return true;
                case AddressClaimed:
                    name = "Address Claimed";
                    return true;
                case Request:
                    name = "Request";
                    return true;
                case Acknowledgement:
                    name = "Acknowledgement";
                    return true;
                case Dm1:
                    name = "DM1 - active diagnostic trouble codes and lamp status";
                    return true;
                case Dm2:
                    name = "DM2 - previously active diagnostic trouble codes";
                    return true;
                case Dm3:
                    name = "DM3 - clear previously active diagnostic trouble codes";
                    return true;
                case Dm4:
                    name = "DM4 - freeze frame parameters";
                    return true;
                case Dm5:
                    name = "DM5 - diagnostic readiness";
                    return true;
                case Dm11:
                    name = "DM11 - clear active diagnostic trouble codes";
                    return true;
                case Dm13:
                    name = "DM13 - stop / start broadcast";
                    return true;
                case Dm19:
                    name = "DM19 - calibration identification and verification number";
                    return true;
                default:
                    name = null;
                    return false;
            }
        }

        /// <summary>
        /// Returns a display string for <paramref name="pgn"/>. Unknown PGNs are
        /// reported as unknown rather than guessed at - a wrong parameter group
        /// name sends a technician after the wrong system.
        /// </summary>
        /// <param name="pgn">Parameter group number.</param>
        /// <returns>"PGN 65226 (0xFECA) DM1 - ..." or "PGN 65262 (0xFEEE) (unknown - no definition loaded)".</returns>
        public static string Describe(uint pgn)
        {
            string name;
            if (TryDescribe(pgn, out name))
            {
                return string.Format(CultureInfo.InvariantCulture,
                    "PGN {0} (0x{1:X4}) {2}", pgn, pgn, name);
            }

            return string.Format(CultureInfo.InvariantCulture,
                "PGN {0} (0x{1:X4}) (unknown - no definition loaded)", pgn, pgn);
        }

        /// <summary>
        /// True when <paramref name="address"/> is one of the addresses reserved by
        /// J1939-81 for an off-board diagnostic service tool (249 or 250).
        /// </summary>
        /// <param name="address">Source or destination address.</param>
        /// <returns>True for 249 or 250.</returns>
        public static bool IsServiceToolAddress(byte address)
        {
            return address == ServiceTool1Address || address == ServiceTool2Address;
        }
    }
}
