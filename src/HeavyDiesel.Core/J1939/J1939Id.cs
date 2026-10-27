// =============================================================================
//  J1939Id.cs - SAE J1939-21 29-bit CAN identifier
//
//  Layout, MSB -> LSB:
//    bits 28..26 priority | bit 25 EDP | bit 24 DP | bits 23..16 PF
//    bits 15..8 PS | bits 7..0 source address
//
//  PDU1 (PF <= 0xEF): PS is the DESTINATION ADDRESS and is NOT part of the PGN.
//  PDU2 (PF >= 0xF0): PS is a group extension and IS part of the PGN.
//
//  The PDU1 / PDU2 discrimination lives inside this type on purpose. Callers
//  get a PGN only from the Pgn property, which always applies the PDU1 mask;
//  there is deliberately no public helper that assembles a PGN from loose
//  fields, because forgetting the mask is the single most common J1939 parser
//  defect and it produces a plausible-looking wrong parameter group.
// =============================================================================

using System;
using System.Globalization;

namespace HeavyDiesel.J1939
{
    /// <summary>
    /// An SAE J1939-21 29-bit extended CAN identifier, decoded into its
    /// priority / EDP / DP / PF / PS / source-address fields.
    /// </summary>
    /// <remarks>
    /// This is a value type: <c>default(J1939Id)</c> is a fully valid identifier
    /// (priority 0, PGN 0, source address 0), not an "unset" sentinel. Use a
    /// nullable <c>J1939Id?</c> where absence must be representable.
    /// </remarks>
    public struct J1939Id : IEquatable<J1939Id>
    {
        /// <summary>Mask of the 29 identifier bits. Adapter flag bits (for example the extended-frame flag) must be stripped before construction.</summary>
        public const uint IDENTIFIER_MASK = 0x1FFFFFFFu;

        /// <summary>Largest encodable PGN: 18 bits of EDP + DP + PF + PS.</summary>
        public const uint MAX_PGN = 0x3FFFFu;

        /// <summary>Largest encodable priority (3 bits). 0 is highest priority on the wire.</summary>
        public const byte MAX_PRIORITY = 7;

        /// <summary>Highest PF value that selects PDU1 (destination-specific) format.</summary>
        public const byte PDU1_PF_MAXIMUM = 0xEF;

        /// <summary>Lowest PF value that selects PDU2 (broadcast) format.</summary>
        public const byte PDU2_PF_MINIMUM = 0xF0;

        private const int PRIORITY_SHIFT = 26;
        private const int EDP_SHIFT = 25;
        private const int DP_SHIFT = 24;
        private const int PF_SHIFT = 16;
        private const int PS_SHIFT = 8;

        private readonly uint _raw;

        private J1939Id(uint raw)
        {
            _raw = raw;
        }

        /// <summary>The raw 29-bit identifier as it appears on the wire.</summary>
        public uint Raw
        {
            get { return _raw; }
        }

        /// <summary>Priority, bits 28..26. 0 is the highest priority, 7 the lowest.</summary>
        public byte Priority
        {
            get { return (byte)((_raw >> PRIORITY_SHIFT) & 0x07u); }
        }

        /// <summary>Extended data page, bit 25.</summary>
        public byte Edp
        {
            get { return (byte)((_raw >> EDP_SHIFT) & 0x01u); }
        }

        /// <summary>Data page, bit 24.</summary>
        public byte Dp
        {
            get { return (byte)((_raw >> DP_SHIFT) & 0x01u); }
        }

        /// <summary>PDU format, bits 23..16. Selects PDU1 or PDU2 addressing.</summary>
        public byte PduFormat
        {
            get { return (byte)((_raw >> PF_SHIFT) & 0xFFu); }
        }

        /// <summary>
        /// PDU specific, bits 15..8. Raw field only: it is the destination address
        /// for PDU1 and a group extension for PDU2. Prefer <see cref="DestinationAddress"/>
        /// and <see cref="Pgn"/>, which apply the format rules.
        /// </summary>
        public byte PduSpecific
        {
            get { return (byte)((_raw >> PS_SHIFT) & 0xFFu); }
        }

        /// <summary>Source address, bits 7..0. The address of the transmitting node.</summary>
        public byte SourceAddress
        {
            get { return (byte)(_raw & 0xFFu); }
        }

        /// <summary>True when PF &lt;= 0xEF: PDU1 / destination-specific format.</summary>
        public bool IsPdu1
        {
            get { return PduFormat <= PDU1_PF_MAXIMUM; }
        }

        /// <summary>True when PF &gt;= 0xF0: PDU2 / broadcast format.</summary>
        public bool IsPdu2
        {
            get { return PduFormat >= PDU2_PF_MINIMUM; }
        }

        /// <summary>
        /// The parameter group number. For PDU2 the PS field is included; for PDU1
        /// the PS field is masked out because it carries the destination address.
        /// </summary>
        public uint Pgn
        {
            get
            {
                uint pageBits = ((uint)Edp << 17) | ((uint)Dp << 16) | ((uint)PduFormat << 8);
                return IsPdu2 ? (pageBits | PduSpecific) : pageBits;
            }
        }

        /// <summary>
        /// Destination address. For PDU1 this is the PS field. For PDU2 there is no
        /// destination field on the wire and this returns the global address (255),
        /// which is the correct semantic: a PDU2 frame is addressed to everyone.
        /// Use <see cref="IsPeerToPeer"/> to tell a real peer-to-peer destination
        /// from an implied broadcast.
        /// </summary>
        public byte DestinationAddress
        {
            get { return IsPdu1 ? PduSpecific : J1939Pgn.GlobalAddress; }
        }

        /// <summary>True when this frame is directed at one specific node: PDU1 with a destination other than the global address.</summary>
        public bool IsPeerToPeer
        {
            get { return IsPdu1 && PduSpecific != J1939Pgn.GlobalAddress; }
        }

        /// <summary>True when every node on the segment is an intended recipient (PDU2, or PDU1 addressed to 255).</summary>
        public bool IsBroadcast
        {
            get { return !IsPeerToPeer; }
        }

        /// <summary>
        /// Builds an identifier from a raw 29-bit value received from an adapter.
        /// </summary>
        /// <param name="raw">
        /// The 29 identifier bits, with any adapter-specific flag bits (for example
        /// an extended-frame flag in bit 31) already stripped. The value is not
        /// truncated: an out-of-range value throws rather than silently decoding
        /// into a plausible but wrong parameter group.
        /// </param>
        /// <returns>The decoded identifier.</returns>
        /// <exception cref="ArgumentOutOfRangeException">The value does not fit in 29 bits.</exception>
        public static J1939Id FromRaw(uint raw)
        {
            if ((raw & ~IDENTIFIER_MASK) != 0)
            {
                throw new ArgumentOutOfRangeException("raw", raw,
                    "Value does not fit in a 29-bit J1939 identifier. Strip adapter flag bits (mask with J1939Id.IDENTIFIER_MASK) before calling.");
            }

            return new J1939Id(raw);
        }

        /// <summary>
        /// Non-throwing form of <see cref="FromRaw"/>, for receive loops that may see
        /// frames the adapter did not filter out.
        /// </summary>
        /// <param name="raw">Candidate 29-bit identifier.</param>
        /// <param name="id">Receives the decoded identifier when the value is in range.</param>
        /// <returns>True when the value fits in 29 bits.</returns>
        public static bool TryFromRaw(uint raw, out J1939Id id)
        {
            if ((raw & ~IDENTIFIER_MASK) != 0)
            {
                id = default(J1939Id);
                return false;
            }

            id = new J1939Id(raw);
            return true;
        }

        /// <summary>
        /// Encodes an identifier for transmission, placing the destination address in
        /// the PS field for PDU1 parameter groups.
        /// </summary>
        /// <param name="priority">Priority 0..7.</param>
        /// <param name="pgn">Parameter group number, 0..0x3FFFF.</param>
        /// <param name="sourceAddress">Address this tool or node is transmitting from.</param>
        /// <param name="destinationAddress">
        /// Destination for PDU1 groups. Must be <see cref="J1939Pgn.GlobalAddress"/> for
        /// PDU2 groups, which have no destination field.
        /// </param>
        /// <returns>The encoded identifier. <c>Create(p, pgn, sa, da).Pgn == pgn</c>.</returns>
        /// <exception cref="ArgumentOutOfRangeException">Priority or PGN out of range.</exception>
        /// <exception cref="ArgumentException">
        /// A PDU1 PGN was supplied with a non-zero low byte, or a specific destination
        /// was supplied for a PDU2 PGN. Both indicate the caller has the addressing
        /// mode wrong, and silently dropping either field would put a wrong identifier
        /// on the bus.
        /// </exception>
        public static J1939Id Create(byte priority, uint pgn, byte sourceAddress, byte destinationAddress)
        {
            if (priority > MAX_PRIORITY)
            {
                throw new ArgumentOutOfRangeException("priority", priority,
                    "J1939 priority is a 3-bit field (0..7).");
            }

            if (pgn > MAX_PGN)
            {
                throw new ArgumentOutOfRangeException("pgn", pgn,
                    "J1939 PGN is an 18-bit field (0..0x3FFFF).");
            }

            byte pduFormat = (byte)((pgn >> 8) & 0xFFu);
            byte edp = (byte)((pgn >> 17) & 0x01u);
            byte dp = (byte)((pgn >> 16) & 0x01u);

            byte pduSpecific;
            if (pduFormat <= PDU1_PF_MAXIMUM)
            {
                if ((pgn & 0xFFu) != 0)
                {
                    throw new ArgumentException(string.Format(CultureInfo.InvariantCulture,
                        "PGN {0} (0x{1:X4}) is PDU1 (PF 0x{2:X2}) so its low byte must be zero - in PDU1 the PS field carries the destination address, not part of the PGN.",
                        pgn, pgn, pduFormat), "pgn");
                }

                pduSpecific = destinationAddress;
            }
            else
            {
                if (destinationAddress != J1939Pgn.GlobalAddress)
                {
                    throw new ArgumentException(string.Format(CultureInfo.InvariantCulture,
                        "PGN {0} (0x{1:X4}) is PDU2 (PF 0x{2:X2}) and is broadcast only; destination address 0x{3:X2} cannot be encoded. Pass J1939Pgn.GlobalAddress.",
                        pgn, pgn, pduFormat, destinationAddress), "destinationAddress");
                }

                pduSpecific = (byte)(pgn & 0xFFu);
            }

            uint raw = ((uint)priority << PRIORITY_SHIFT)
                     | ((uint)edp << EDP_SHIFT)
                     | ((uint)dp << DP_SHIFT)
                     | ((uint)pduFormat << PF_SHIFT)
                     | ((uint)pduSpecific << PS_SHIFT)
                     | sourceAddress;

            return new J1939Id(raw);
        }

        /// <summary>
        /// Encodes a broadcast identifier (destination = global address 255). Valid for
        /// PDU2 groups and for PDU1 groups that are legitimately broadcast, such as DM13.
        /// </summary>
        /// <param name="priority">Priority 0..7.</param>
        /// <param name="pgn">Parameter group number, 0..0x3FFFF.</param>
        /// <param name="sourceAddress">Address this tool or node is transmitting from.</param>
        /// <returns>The encoded identifier.</returns>
        public static J1939Id Create(byte priority, uint pgn, byte sourceAddress)
        {
            return Create(priority, pgn, sourceAddress, J1939Pgn.GlobalAddress);
        }

        /// <summary>Value equality over the raw identifier.</summary>
        /// <param name="other">Identifier to compare with.</param>
        /// <returns>True when the raw identifiers match.</returns>
        public bool Equals(J1939Id other)
        {
            return _raw == other._raw;
        }

        /// <inheritdoc />
        public override bool Equals(object obj)
        {
            return obj is J1939Id && Equals((J1939Id)obj);
        }

        /// <inheritdoc />
        public override int GetHashCode()
        {
            return (int)_raw;
        }

        /// <summary>Equality operator.</summary>
        /// <param name="left">Left operand.</param>
        /// <param name="right">Right operand.</param>
        /// <returns>True when the raw identifiers match.</returns>
        public static bool operator ==(J1939Id left, J1939Id right)
        {
            return left._raw == right._raw;
        }

        /// <summary>Inequality operator.</summary>
        /// <param name="left">Left operand.</param>
        /// <param name="right">Right operand.</param>
        /// <returns>True when the raw identifiers differ.</returns>
        public static bool operator !=(J1939Id left, J1939Id right)
        {
            return left._raw != right._raw;
        }

        /// <summary>
        /// Human-readable form, for example
        /// "PGN 65226 (0xFECA) SA 0x00 prio 6" or
        /// "PGN 60416 (0xEC00) SA 0xF9 DA 0x00 prio 7".
        /// </summary>
        /// <returns>The formatted identifier.</returns>
        public override string ToString()
        {
            uint pgn = Pgn;

            if (IsPdu1)
            {
                return string.Format(CultureInfo.InvariantCulture,
                    "PGN {0} (0x{1:X4}) SA 0x{2:X2} DA 0x{3:X2} prio {4}",
                    pgn, pgn, SourceAddress, DestinationAddress, Priority);
            }

            return string.Format(CultureInfo.InvariantCulture,
                "PGN {0} (0x{1:X4}) SA 0x{2:X2} prio {3}",
                pgn, pgn, SourceAddress, Priority);
        }
    }
}
