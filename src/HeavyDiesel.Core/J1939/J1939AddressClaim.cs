// =============================================================================
//  J1939AddressClaim.cs - SAE J1939-81 network management: the 64-bit NAME,
//  the address claim state machine, and the observed source-address map.
//
//  WHY THIS IS SAFETY-RELEVANT
//  ---------------------------
//  A diagnostic tool is a first-class node on a J1939 segment. It must claim a
//  source address and defend it, or it gets arbitrated off and stops working.
//  Getting it WRONG is worse than not participating at all: if this tool claims
//  an address a genuine ECU already holds, that ECU must respond, and whichever
//  node has the numerically HIGHER NAME loses. A losing ECU that is arbitrary
//  address capable relocates; one that is not GOES SILENT and stays silent until
//  power cycle. On a truck that is a warranty conversation. On plant with a
//  hydraulic controller in the loop it is a safety conversation.
//
//  So this implementation is built to AVOID contention rather than to win it:
//
//    1. It listens for the mandatory 1250 ms before transmitting anything that
//       claims an address.
//    2. During that window it asks the bus who is there (Request for PGN 60928)
//       and records every source address it sees, claimed or merely transmitting.
//    3. It only ever claims an address it has NOT observed in use. It does not
//       pick a "preferred" address and fight for it.
//    4. If it loses a contention anyway (a node that powered up late, or a
//       simultaneous claim), it relocates if it can and goes to the null address
//       and reports loudly if it cannot. It never sits on a contested address.
//
//  ArbitraryAddressCapable defaults to TRUE. That bit is the most significant bit
//  of the NAME, so setting it makes our NAME numerically larger and therefore
//  makes us LOSE contention against most fixed-address ECUs. That is deliberate
//  and correct: the tool yields, the vehicle keeps working.
//
//  THREADING
//  ---------
//  Same contract as the rest of the heavy stack: an external thread drives
//  OnMessageReceived(J1939Message) and Tick(long nowMs). All state is guarded by
//  one lock. Outgoing frames are handed to a callback the owner supplies and are
//  dispatched OUTSIDE the lock, so an owner whose transmit path loops back into
//  OnMessageReceived cannot deadlock this class.
//
//  TIMEBASE
//  --------
//  Every timer runs off nowMs as supplied to Tick. The owner must feed a
//  monotonic millisecond value from the adapter timestamp or a Stopwatch, never
//  DateTime.Now. Messages arriving between ticks are timed against the last tick
//  value, which is never later than true now, so every interval is measured
//  slightly short - erring towards waiting longer before declaring an address
//  claimed, which is the safe direction.
// =============================================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace HeavyDiesel.J1939
{
    /// <summary>
    /// Industry group field of the J1939 NAME (3 bits). Some OEM tooling filters
    /// the bus on this field, and HeavyDiesel covers on-highway trucks as well as
    /// mining and construction plant, so it is configurable rather than fixed.
    /// </summary>
    public enum IndustryGroup : byte
    {
        /// <summary>Global: applies to all industry groups. Encoding of 0, and the value of an unconfigured NAME.</summary>
        Global = 0,

        /// <summary>On-highway equipment (trucks and buses).</summary>
        OnHighway = 1,

        /// <summary>Agricultural and forestry equipment.</summary>
        Agricultural = 2,

        /// <summary>Construction equipment.</summary>
        Construction = 3,

        /// <summary>Marine equipment.</summary>
        Marine = 4,

        /// <summary>Industrial process control and generator sets.</summary>
        IndustrialGenerator = 5
    }

    /// <summary>
    /// Where the address claim state machine currently is. Surface this in the UI:
    /// a technician needs to see what the tool is doing on the bus.
    /// </summary>
    public enum J1939AddressClaimState
    {
        /// <summary>Not started. Nothing has been transmitted and no address is held.</summary>
        NotStarted = 0,

        /// <summary>Listening out the mandatory pre-claim window. No address is held.</summary>
        Listening = 1,

        /// <summary>A claim has been transmitted and the contention window has not yet expired.</summary>
        Claiming = 2,

        /// <summary>The address is held and defended. The tool is a full participant on the bus.</summary>
        Claimed = 3,

        /// <summary>A competing node with a lower NAME took the address and relocation was not possible. No address is held.</summary>
        LostArbitration = 4,

        /// <summary>No free address could be obtained. No address is held.</summary>
        Failed = 5
    }

    /// <summary>Raised when the address claim state machine changes state.</summary>
    public sealed class J1939AddressClaimEventArgs : EventArgs
    {
        private readonly J1939AddressClaimState _previousState;
        private readonly J1939AddressClaimState _state;
        private readonly byte _address;
        private readonly string _reason;

        /// <summary>Creates the event payload.</summary>
        /// <param name="previousState">State before the transition.</param>
        /// <param name="state">State after the transition.</param>
        /// <param name="address">Address held after the transition, or 254 when none is held.</param>
        /// <param name="reason">Human-readable reason for the transition.</param>
        public J1939AddressClaimEventArgs(J1939AddressClaimState previousState, J1939AddressClaimState state, byte address, string reason)
        {
            _previousState = previousState;
            _state = state;
            _address = address;
            _reason = reason ?? string.Empty;
        }

        /// <summary>State before the transition.</summary>
        public J1939AddressClaimState PreviousState
        {
            get { return _previousState; }
        }

        /// <summary>State after the transition.</summary>
        public J1939AddressClaimState State
        {
            get { return _state; }
        }

        /// <summary>Address held after the transition, or <see cref="J1939Pgn.NullAddress"/> (254) when none is held.</summary>
        public byte Address
        {
            get { return _address; }
        }

        /// <summary>Human-readable reason for the transition. Never null.</summary>
        public string Reason
        {
            get { return _reason; }
        }

        /// <summary>Single-line log form.</summary>
        /// <returns>The formatted transition.</returns>
        public override string ToString()
        {
            return string.Format(CultureInfo.InvariantCulture,
                "{0} -> {1} (SA 0x{2:X2}): {3}", _previousState, _state, _address, _reason);
        }
    }

    /// <summary>
    /// The 64-bit J1939-81 NAME. Identifies a node and, critically, decides who
    /// wins an address contention: the numerically LOWER NAME wins.
    /// </summary>
    /// <remarks>
    /// <para>Field layout, most significant bit first:</para>
    /// <list type="table">
    /// <item><term>bit 63</term><description>Arbitrary address capable (1 bit)</description></item>
    /// <item><term>bits 62..60</term><description>Industry group (3 bits)</description></item>
    /// <item><term>bits 59..56</term><description>Vehicle system instance (4 bits)</description></item>
    /// <item><term>bits 55..49</term><description>Vehicle system (7 bits)</description></item>
    /// <item><term>bit 48</term><description>Reserved (1 bit)</description></item>
    /// <item><term>bits 47..40</term><description>Function (8 bits)</description></item>
    /// <item><term>bits 39..35</term><description>Function instance (5 bits)</description></item>
    /// <item><term>bits 34..32</term><description>ECU instance (3 bits)</description></item>
    /// <item><term>bits 31..21</term><description>Manufacturer code (11 bits)</description></item>
    /// <item><term>bits 20..0</term><description>Identity number (21 bits)</description></item>
    /// </list>
    /// <para>
    /// On the wire the NAME occupies the eight data bytes of PGN 60928 LEAST
    /// SIGNIFICANT BYTE FIRST, so <see cref="ToBytes"/> is a little-endian
    /// serialisation of <see cref="Value"/> and byte 7 is the one carrying the
    /// arbitrary-address-capable bit.
    /// </para>
    /// <para>
    /// This type is MUTABLE so the industry group and the rest of the identity can
    /// be configured from the UI. <see cref="J1939AddressClaim"/> takes a private
    /// copy at construction, so mutating a NAME after handing it over does not
    /// change what is already on the bus.
    /// </para>
    /// </remarks>
    public sealed class J1939Name : IEquatable<J1939Name>, IComparable<J1939Name>
    {
        /// <summary>Largest encodable vehicle system instance (4 bits).</summary>
        public const byte MAX_VEHICLE_SYSTEM_INSTANCE = 15;

        /// <summary>Largest encodable vehicle system (7 bits). J1939 treats 127 as "not available".</summary>
        public const byte MAX_VEHICLE_SYSTEM = 127;

        /// <summary>Largest encodable function instance (5 bits).</summary>
        public const byte MAX_FUNCTION_INSTANCE = 31;

        /// <summary>Largest encodable ECU instance (3 bits).</summary>
        public const byte MAX_ECU_INSTANCE = 7;

        /// <summary>Largest encodable industry group (3 bits).</summary>
        public const byte MAX_INDUSTRY_GROUP = 7;

        /// <summary>Largest encodable manufacturer code (11 bits).</summary>
        public const ushort MAX_MANUFACTURER_CODE = 2047;

        /// <summary>Largest encodable identity number (21 bits).</summary>
        public const uint MAX_IDENTITY_NUMBER = 2097151;

        /// <summary>Serialised length of a NAME in bytes.</summary>
        public const int BYTE_LENGTH = 8;

        private const int ARBITRARY_ADDRESS_CAPABLE_SHIFT = 63;
        private const int INDUSTRY_GROUP_SHIFT = 60;
        private const int VEHICLE_SYSTEM_INSTANCE_SHIFT = 56;
        private const int VEHICLE_SYSTEM_SHIFT = 49;
        private const int RESERVED_SHIFT = 48;
        private const int FUNCTION_SHIFT = 40;
        private const int FUNCTION_INSTANCE_SHIFT = 35;
        private const int ECU_INSTANCE_SHIFT = 32;
        private const int MANUFACTURER_CODE_SHIFT = 21;
        private const int IDENTITY_NUMBER_SHIFT = 0;

        private bool _arbitraryAddressCapable;
        private byte _industryGroup;
        private byte _vehicleSystemInstance;
        private byte _vehicleSystem;
        private byte _reserved;
        private byte _function;
        private byte _functionInstance;
        private byte _ecuInstance;
        private ushort _manufacturerCode;
        private uint _identityNumber;

        /// <summary>
        /// Creates an otherwise-zero NAME with
        /// <see cref="ArbitraryAddressCapable"/> set to true, so a tool built on the
        /// default can self-relocate on losing arbitration instead of dropping off
        /// the bus.
        /// </summary>
        public J1939Name()
        {
            _arbitraryAddressCapable = true;
        }

        /// <summary>Creates a NAME from a packed 64-bit value.</summary>
        /// <param name="value">Packed NAME.</param>
        public J1939Name(ulong value)
        {
            Value = value;
        }

        /// <summary>
        /// Builds a NAME for an off-board diagnostic service tool.
        /// </summary>
        /// <param name="manufacturerCode">
        /// SAE-assigned manufacturer code, 0..2047. There is no correct value to
        /// default this to: 0 is not an assignment, and inventing somebody else's
        /// code puts a false identity on a customer's bus. Supply the real one.
        /// </param>
        /// <param name="identityNumber">
        /// Serial-number-like identity, 0..2097151. Must differ between two tools of
        /// the same make on the same bus, otherwise their NAMEs collide and neither
        /// can resolve a contention against the other.
        /// </param>
        /// <param name="industryGroup">Industry group this tool is being used in.</param>
        /// <param name="function">
        /// J1939-71 function code. Not defaulted, because this file does not carry
        /// the SAE function-code table and a guessed function code is a node
        /// announcing itself as the wrong kind of device.
        /// </param>
        /// <returns>An arbitrary-address-capable NAME with the supplied identity.</returns>
        /// <exception cref="ArgumentOutOfRangeException">A field is out of range.</exception>
        public static J1939Name CreateServiceTool(ushort manufacturerCode, uint identityNumber, IndustryGroup industryGroup, byte function)
        {
            J1939Name name = new J1939Name();
            name.ArbitraryAddressCapable = true;
            name.IndustryGroupValue = (byte)industryGroup;
            name.Function = function;
            name.ManufacturerCode = manufacturerCode;
            name.IdentityNumber = identityNumber;
            return name;
        }

        /// <summary>
        /// True when this node can pick a different source address after losing an
        /// address contention. Because this is the most significant bit of the NAME,
        /// setting it makes the NAME numerically larger and so makes this node LOSE
        /// contentions it would otherwise win - which is the intended behaviour for a
        /// diagnostic tool sharing a bus with vehicle ECUs.
        /// </summary>
        public bool ArbitraryAddressCapable
        {
            get { return _arbitraryAddressCapable; }
            set { _arbitraryAddressCapable = value; }
        }

        /// <summary>Industry group, as a named value. Unnamed encodings 6 and 7 read back as a cast integer; use <see cref="IndustryGroupValue"/> for those.</summary>
        public IndustryGroup IndustryGroup
        {
            get { return (IndustryGroup)_industryGroup; }
            set { IndustryGroupValue = (byte)value; }
        }

        /// <summary>Raw industry group field, 0..7. Single validation point for <see cref="IndustryGroup"/>.</summary>
        /// <exception cref="ArgumentOutOfRangeException">Value exceeds 7.</exception>
        public byte IndustryGroupValue
        {
            get { return _industryGroup; }
            set
            {
                if (value > MAX_INDUSTRY_GROUP)
                {
                    throw new ArgumentOutOfRangeException("value", value, "Industry group is a 3-bit field (0..7).");
                }

                _industryGroup = value;
            }
        }

        /// <summary>Vehicle system instance, 0..15.</summary>
        /// <exception cref="ArgumentOutOfRangeException">Value exceeds 15.</exception>
        public byte VehicleSystemInstance
        {
            get { return _vehicleSystemInstance; }
            set
            {
                if (value > MAX_VEHICLE_SYSTEM_INSTANCE)
                {
                    throw new ArgumentOutOfRangeException("value", value, "Vehicle system instance is a 4-bit field (0..15).");
                }

                _vehicleSystemInstance = value;
            }
        }

        /// <summary>Vehicle system, 0..127. J1939 treats 127 as "not available".</summary>
        /// <exception cref="ArgumentOutOfRangeException">Value exceeds 127.</exception>
        public byte VehicleSystem
        {
            get { return _vehicleSystem; }
            set
            {
                if (value > MAX_VEHICLE_SYSTEM)
                {
                    throw new ArgumentOutOfRangeException("value", value, "Vehicle system is a 7-bit field (0..127).");
                }

                _vehicleSystem = value;
            }
        }

        /// <summary>
        /// Reserved bit, 0 or 1. Exposed only so a NAME captured off a real bus
        /// round-trips byte-exact; leave it at 0 on a NAME this tool transmits.
        /// </summary>
        /// <exception cref="ArgumentOutOfRangeException">Value exceeds 1.</exception>
        public byte Reserved
        {
            get { return _reserved; }
            set
            {
                if (value > 1)
                {
                    throw new ArgumentOutOfRangeException("value", value, "Reserved is a 1-bit field (0..1).");
                }

                _reserved = value;
            }
        }

        /// <summary>Function code, 0..255. J1939 treats 255 as "not available".</summary>
        public byte Function
        {
            get { return _function; }
            set { _function = value; }
        }

        /// <summary>Function instance, 0..31.</summary>
        /// <exception cref="ArgumentOutOfRangeException">Value exceeds 31.</exception>
        public byte FunctionInstance
        {
            get { return _functionInstance; }
            set
            {
                if (value > MAX_FUNCTION_INSTANCE)
                {
                    throw new ArgumentOutOfRangeException("value", value, "Function instance is a 5-bit field (0..31).");
                }

                _functionInstance = value;
            }
        }

        /// <summary>ECU instance, 0..7.</summary>
        /// <exception cref="ArgumentOutOfRangeException">Value exceeds 7.</exception>
        public byte EcuInstance
        {
            get { return _ecuInstance; }
            set
            {
                if (value > MAX_ECU_INSTANCE)
                {
                    throw new ArgumentOutOfRangeException("value", value, "ECU instance is a 3-bit field (0..7).");
                }

                _ecuInstance = value;
            }
        }

        /// <summary>SAE-assigned manufacturer code, 0..2047.</summary>
        /// <exception cref="ArgumentOutOfRangeException">Value exceeds 2047.</exception>
        public ushort ManufacturerCode
        {
            get { return _manufacturerCode; }
            set
            {
                if (value > MAX_MANUFACTURER_CODE)
                {
                    throw new ArgumentOutOfRangeException("value", value, "Manufacturer code is an 11-bit field (0..2047).");
                }

                _manufacturerCode = value;
            }
        }

        /// <summary>Identity number, 0..2097151. Must be unique among nodes sharing a manufacturer code.</summary>
        /// <exception cref="ArgumentOutOfRangeException">Value exceeds 2097151.</exception>
        public uint IdentityNumber
        {
            get { return _identityNumber; }
            set
            {
                if (value > MAX_IDENTITY_NUMBER)
                {
                    throw new ArgumentOutOfRangeException("value", value, "Identity number is a 21-bit field (0..2097151).");
                }

                _identityNumber = value;
            }
        }

        /// <summary>
        /// The packed 64-bit NAME. This is the value contention is decided on:
        /// lower wins. Setting it decodes every field; reading it re-encodes them,
        /// so <c>new J1939Name(x).Value == x</c> for every 64-bit x.
        /// </summary>
        public ulong Value
        {
            get
            {
                return ((_arbitraryAddressCapable ? 1UL : 0UL) << ARBITRARY_ADDRESS_CAPABLE_SHIFT)
                     | ((ulong)_industryGroup << INDUSTRY_GROUP_SHIFT)
                     | ((ulong)_vehicleSystemInstance << VEHICLE_SYSTEM_INSTANCE_SHIFT)
                     | ((ulong)_vehicleSystem << VEHICLE_SYSTEM_SHIFT)
                     | ((ulong)_reserved << RESERVED_SHIFT)
                     | ((ulong)_function << FUNCTION_SHIFT)
                     | ((ulong)_functionInstance << FUNCTION_INSTANCE_SHIFT)
                     | ((ulong)_ecuInstance << ECU_INSTANCE_SHIFT)
                     | ((ulong)_manufacturerCode << MANUFACTURER_CODE_SHIFT)
                     | ((ulong)_identityNumber << IDENTITY_NUMBER_SHIFT);
            }

            set
            {
                _arbitraryAddressCapable = ((value >> ARBITRARY_ADDRESS_CAPABLE_SHIFT) & 0x1UL) != 0UL;
                _industryGroup = (byte)((value >> INDUSTRY_GROUP_SHIFT) & 0x7UL);
                _vehicleSystemInstance = (byte)((value >> VEHICLE_SYSTEM_INSTANCE_SHIFT) & 0xFUL);
                _vehicleSystem = (byte)((value >> VEHICLE_SYSTEM_SHIFT) & 0x7FUL);
                _reserved = (byte)((value >> RESERVED_SHIFT) & 0x1UL);
                _function = (byte)((value >> FUNCTION_SHIFT) & 0xFFUL);
                _functionInstance = (byte)((value >> FUNCTION_INSTANCE_SHIFT) & 0x1FUL);
                _ecuInstance = (byte)((value >> ECU_INSTANCE_SHIFT) & 0x7UL);
                _manufacturerCode = (ushort)((value >> MANUFACTURER_CODE_SHIFT) & 0x7FFUL);
                _identityNumber = (uint)((value >> IDENTITY_NUMBER_SHIFT) & 0x1FFFFFUL);
            }
        }

        /// <summary>Decodes a packed NAME.</summary>
        /// <param name="value">Packed 64-bit NAME.</param>
        /// <returns>The decoded NAME.</returns>
        public static J1939Name FromValue(ulong value)
        {
            return new J1939Name(value);
        }

        /// <summary>
        /// Serialises to the eight data bytes of PGN 60928, least significant byte
        /// first, exactly as the NAME appears on the wire.
        /// </summary>
        /// <returns>A new 8-byte array.</returns>
        public byte[] ToBytes()
        {
            byte[] bytes = new byte[BYTE_LENGTH];
            WriteTo(bytes, 0);
            return bytes;
        }

        /// <summary>Serialises into an existing buffer, least significant byte first.</summary>
        /// <param name="buffer">Destination buffer.</param>
        /// <param name="offset">Index of the first NAME byte.</param>
        /// <exception cref="ArgumentNullException"><paramref name="buffer"/> is null.</exception>
        /// <exception cref="ArgumentException">The buffer has fewer than 8 bytes available at <paramref name="offset"/>.</exception>
        public void WriteTo(byte[] buffer, int offset)
        {
            if (buffer == null)
            {
                throw new ArgumentNullException("buffer");
            }

            if (offset < 0 || buffer.Length - offset < BYTE_LENGTH)
            {
                throw new ArgumentException("A J1939 NAME needs 8 bytes at the supplied offset.", "offset");
            }

            ulong value = Value;
            for (int i = 0; i < BYTE_LENGTH; i++)
            {
                buffer[offset + i] = (byte)((value >> (i * 8)) & 0xFFUL);
            }
        }

        /// <summary>Decodes a NAME from wire bytes, least significant byte first.</summary>
        /// <param name="data">Buffer holding the NAME.</param>
        /// <param name="offset">Index of the first NAME byte.</param>
        /// <returns>The decoded NAME.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="data"/> is null.</exception>
        /// <exception cref="ArgumentException">Fewer than 8 bytes are available at <paramref name="offset"/>.</exception>
        public static J1939Name FromBytes(byte[] data, int offset)
        {
            J1939Name name;
            if (!TryFromBytes(data, offset, out name))
            {
                if (data == null)
                {
                    throw new ArgumentNullException("data");
                }

                throw new ArgumentException("A J1939 NAME needs 8 bytes at the supplied offset.", "offset");
            }

            return name;
        }

        /// <summary>Non-throwing decode, for receive loops that may see a short or malformed frame.</summary>
        /// <param name="data">Buffer holding the NAME.</param>
        /// <param name="offset">Index of the first NAME byte.</param>
        /// <param name="name">Receives the decoded NAME, or null on failure.</param>
        /// <returns>True when 8 bytes were available and decoded.</returns>
        public static bool TryFromBytes(byte[] data, int offset, out J1939Name name)
        {
            if (data == null || offset < 0 || data.Length - offset < BYTE_LENGTH)
            {
                name = null;
                return false;
            }

            ulong value = 0UL;
            for (int i = 0; i < BYTE_LENGTH; i++)
            {
                value |= (ulong)data[offset + i] << (i * 8);
            }

            name = new J1939Name(value);
            return true;
        }

        /// <summary>Independent copy, so the caller can mutate one without affecting the other.</summary>
        /// <returns>A new NAME with the same field values.</returns>
        public J1939Name Clone()
        {
            return new J1939Name(Value);
        }

        /// <summary>
        /// True when this NAME beats <paramref name="other"/> in an address
        /// contention: strictly lower numeric value wins.
        /// </summary>
        /// <param name="other">The competing NAME.</param>
        /// <returns>True when this NAME is numerically lower.</returns>
        /// <remarks>
        /// Equal NAMEs return false. Two identical NAMEs on one segment is an
        /// unresolvable configuration fault, and yielding is the safe response for a
        /// diagnostic tool: the other node may be a vehicle ECU.
        /// </remarks>
        /// <exception cref="ArgumentNullException"><paramref name="other"/> is null.</exception>
        public bool WinsContentionAgainst(J1939Name other)
        {
            if (other == null)
            {
                throw new ArgumentNullException("other");
            }

            return Value < other.Value;
        }

        /// <summary>Orders by packed NAME value. Lower sorts first and wins contention.</summary>
        /// <param name="other">The NAME to compare with; null sorts last.</param>
        /// <returns>Negative, zero or positive per <see cref="IComparable{T}"/>.</returns>
        public int CompareTo(J1939Name other)
        {
            if (other == null)
            {
                return 1;
            }

            return Value.CompareTo(other.Value);
        }

        /// <summary>Value equality over the packed NAME.</summary>
        /// <param name="other">The NAME to compare with.</param>
        /// <returns>True when both encode the same 64-bit value.</returns>
        public bool Equals(J1939Name other)
        {
            return other != null && Value == other.Value;
        }

        /// <inheritdoc />
        public override bool Equals(object obj)
        {
            return Equals(obj as J1939Name);
        }

        /// <inheritdoc />
        public override int GetHashCode()
        {
            ulong value = Value;
            return (int)(value ^ (value >> 32));
        }

        /// <summary>Null-safe equality operator.</summary>
        /// <param name="left">Left operand.</param>
        /// <param name="right">Right operand.</param>
        /// <returns>True when both are null or both encode the same value.</returns>
        public static bool operator ==(J1939Name left, J1939Name right)
        {
            if (ReferenceEquals(left, right))
            {
                return true;
            }

            if (ReferenceEquals(left, null) || ReferenceEquals(right, null))
            {
                return false;
            }

            return left.Value == right.Value;
        }

        /// <summary>Null-safe inequality operator.</summary>
        /// <param name="left">Left operand.</param>
        /// <param name="right">Right operand.</param>
        /// <returns>True when the operands are not equal.</returns>
        public static bool operator !=(J1939Name left, J1939Name right)
        {
            return !(left == right);
        }

        /// <summary>
        /// Technician-readable form, for example
        /// "NAME 0x800000FF00000001 AAC=1 IG=1(OnHighway) VS=0 VSI=0 Func=0 FuncInst=0 EcuInst=0 Mfg=2047 Id=1".
        /// </summary>
        /// <returns>The formatted NAME.</returns>
        public override string ToString()
        {
            StringBuilder sb = new StringBuilder(128);
            sb.AppendFormat(CultureInfo.InvariantCulture, "NAME 0x{0:X16}", Value);
            sb.AppendFormat(CultureInfo.InvariantCulture, " AAC={0}", _arbitraryAddressCapable ? 1 : 0);

            if (_industryGroup <= (byte)IndustryGroup.IndustrialGenerator)
            {
                sb.AppendFormat(CultureInfo.InvariantCulture, " IG={0}({1})", _industryGroup, (IndustryGroup)_industryGroup);
            }
            else
            {
                sb.AppendFormat(CultureInfo.InvariantCulture, " IG={0}(reserved)", _industryGroup);
            }

            sb.AppendFormat(CultureInfo.InvariantCulture,
                " VS={0} VSI={1} Func={2} FuncInst={3} EcuInst={4} Mfg={5} Id={6}",
                _vehicleSystem, _vehicleSystemInstance, _function, _functionInstance,
                _ecuInstance, _manufacturerCode, _identityNumber);

            return sb.ToString();
        }
    }

    /// <summary>
    /// One entry of the observed source-address map: a source address seen on the
    /// bus, and the NAME that claimed it if a claim was observed.
    /// </summary>
    /// <remarks>
    /// An entry with <see cref="HasName"/> false means the address was seen
    /// transmitting but never seen claiming - typically a node that claimed before
    /// the tool was plugged in and did not answer the discovery request. It still
    /// counts as occupied. Treating a silent-but-active address as free is how a
    /// tool ends up displacing a live ECU.
    /// </remarks>
    public sealed class J1939SourceAddressEntry
    {
        /// <summary>Creates an entry.</summary>
        /// <param name="address">Source address, 0..253.</param>
        /// <param name="name">NAME that claimed the address, or null when no claim was observed.</param>
        /// <param name="firstSeenMs">Tick-clock time this address was first seen.</param>
        /// <param name="lastSeenMs">Tick-clock time this address was last seen.</param>
        /// <param name="claimCount">Number of AddressClaimed frames observed from this address.</param>
        public J1939SourceAddressEntry(byte address, J1939Name name, long firstSeenMs, long lastSeenMs, int claimCount)
        {
            Address = address;
            Name = name;
            FirstSeenMs = firstSeenMs;
            LastSeenMs = lastSeenMs;
            ClaimCount = claimCount;
        }

        /// <summary>Source address this entry describes.</summary>
        public byte Address { get; internal set; }

        /// <summary>NAME that claimed this address, or null when the address was only seen transmitting.</summary>
        public J1939Name Name { get; internal set; }

        /// <summary>True when an AddressClaimed frame was observed from this address.</summary>
        public bool HasName
        {
            get { return Name != null; }
        }

        /// <summary>Tick-clock milliseconds at which this address was first seen.</summary>
        public long FirstSeenMs { get; internal set; }

        /// <summary>Tick-clock milliseconds at which this address was last seen. Compare against the current tick to judge staleness.</summary>
        public long LastSeenMs { get; internal set; }

        /// <summary>
        /// AddressClaimed frames observed from this address. Repeated claims mean
        /// contention is happening on the segment, which is worth showing a technician.
        /// </summary>
        public int ClaimCount { get; internal set; }

        /// <summary>Independent copy, so a UI snapshot cannot be mutated by the receive thread.</summary>
        /// <returns>A new entry with the same values and a cloned NAME.</returns>
        public J1939SourceAddressEntry Clone()
        {
            return new J1939SourceAddressEntry(
                Address,
                Name == null ? null : Name.Clone(),
                FirstSeenMs,
                LastSeenMs,
                ClaimCount);
        }

        /// <summary>Single-line form for a bus-participants table.</summary>
        /// <returns>The formatted entry.</returns>
        public override string ToString()
        {
            if (Name == null)
            {
                return string.Format(CultureInfo.InvariantCulture,
                    "SA 0x{0:X2} ({0,3}) active, no address claim observed (last seen {1} ms)", Address, LastSeenMs);
            }

            return string.Format(CultureInfo.InvariantCulture,
                "SA 0x{0:X2} ({0,3}) {1} (claims {2}, last seen {3} ms)", Address, Name, ClaimCount, LastSeenMs);
        }
    }

    /// <summary>
    /// SAE J1939-81 address claim state machine, network-management message
    /// handling, and the observed source-address map.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Drive it from one external thread: call <see cref="OnMessageReceived"/> for
    /// every received frame and <see cref="Tick"/> regularly (10 to 50 ms is
    /// appropriate; the shortest timer here is the 250 ms contention window). All
    /// state is internally locked, and outgoing frames and callbacks are dispatched
    /// after the lock is released.
    /// </para>
    /// <para>
    /// This class never touches the bus itself. It hands frames to the transmit
    /// callback supplied at construction.
    /// </para>
    /// </remarks>
    public sealed class J1939AddressClaim
    {
        /// <summary>
        /// Mandatory quiet period before this node may claim an address, in
        /// milliseconds. J1939-81 requires a node to listen before claiming so that
        /// it sees the addresses already in use. Skipping it is how a tool claims an
        /// address out from under a live ECU.
        /// </summary>
        public const int MINIMUM_LISTEN_MS = 1250;

        /// <summary>
        /// Time a claiming node waits for a competing claim before regarding the
        /// address as its own, in milliseconds. J1939-81 requires a node whose
        /// address is claimed by another to respond within this window.
        /// </summary>
        public const int CONTENTION_WINDOW_MS = 250;

        /// <summary>
        /// Upper bound of the pseudo-random delay before a Cannot Claim Address
        /// message, in milliseconds, so that several failing nodes do not answer in
        /// lockstep.
        /// </summary>
        public const int CANNOT_CLAIM_MAX_DELAY_MS = 153;

        /// <summary>Lowest source address available for self-configurable (arbitrary address capable) nodes.</summary>
        public const byte DYNAMIC_ADDRESS_LOW = 128;

        /// <summary>Highest source address available for self-configurable (arbitrary address capable) nodes.</summary>
        public const byte DYNAMIC_ADDRESS_HIGH = 247;

        /// <summary>Priority used for AddressClaimed and Request frames this class transmits.</summary>
        public const byte NETWORK_MANAGEMENT_PRIORITY = 6;

        /// <summary>Value of <see cref="FallbackAddress"/> that disables the third claim candidate.</summary>
        public const byte FALLBACK_DISABLED = J1939Pgn.NullAddress;

        private static readonly J1939Message[] NoFrames = new J1939Message[0];
        private static readonly string[] NoNotes = new string[0];
        private static readonly J1939AddressClaimEventArgs[] NoEvents = new J1939AddressClaimEventArgs[0];

        private readonly object _sync = new object();
        /// <summary>
        /// Bus write. Returns TRUE only when the frame actually reached the adapter.
        /// </summary>
        /// <remarks>
        /// Deliberately <c>Func&lt;…,bool&gt;</c> and not <c>Action</c>. With a void callback the
        /// owner has nowhere to report a refused write, so a rejected AddressClaimed leaves this
        /// machine reporting <c>Claimed</c> on an address that was never announced — and every
        /// subsequent transmit then carries a source address no other node associates with us.
        /// The result is honoured in <see cref="Flush"/>; do not widen this back to Action.
        /// </remarks>
        private readonly Func<J1939Message, bool> _transmit;
        private readonly J1939Name _name;
        private readonly Dictionary<byte, J1939SourceAddressEntry> _map = new Dictionary<byte, J1939SourceAddressEntry>();
        private readonly List<byte> _candidates = new List<byte>();
        private readonly List<J1939Message> _outbox = new List<J1939Message>();
        private readonly List<string> _notes = new List<string>();

        // Echo suppression. Adapters commonly loop our own transmissions back on the
        // receive path, so a frame carrying our source address has to be matched against
        // what we actually sent before it can be treated as another node colliding with
        // us. Aged off an internal monotonic clock rather than caller-supplied tick
        // values, because this is bookkeeping about real elapsed time, not protocol time.
        private readonly List<TransmittedFrame> _recentTransmits = new List<TransmittedFrame>();
        private readonly Stopwatch _echoClock = Stopwatch.StartNew();
        private const int ECHO_MEMORY_MS = 2000;
        private const int ECHO_MEMORY_MAX_FRAMES = 32;

        /// <summary>Address we have already raised a collision note for, so a
        /// continuously-transmitting squatter does not bury the log.</summary>
        private int _collisionReportedFor = -1;

        /// <summary>Set once a mixed-clock condition has been reported, so the warning
        /// does not repeat on every tick.</summary>
        private bool _mixedClockWarned;

        private struct TransmittedFrame
        {
            public uint Raw;
            public byte[] Data;
            public double AtMs;
        }
        private readonly List<J1939AddressClaimEventArgs> _events = new List<J1939AddressClaimEventArgs>();

        private Action<string> _log;
        private byte _fallbackAddress = FALLBACK_DISABLED;
        private bool _requestAddressClaimedOnStart = true;

        private J1939AddressClaimState _state = J1939AddressClaimState.NotStarted;
        private byte _claimAddress = J1939Pgn.NullAddress;
        private int _candidateIndex;
        private long _lastTickMs;
        private long _listenStartMs;

        /// <summary>
        /// Listen-window start on the INTERNAL monotonic clock, independent of the
        /// caller-supplied tick values. The 1250 ms listen-before-claim requirement is a
        /// safety interlock, not a formality: claiming an address already in use can make
        /// a genuine ECU relocate or go silent. Enforcing it purely as
        /// <c>nowMs - _listenStartMs</c> trusts two numbers from the caller, so a caller
        /// that seeds Start from one clock and drives Tick from another (for example
        /// Tick(msg.TimestampMs), a free-running adapter counter already in the millions)
        /// satisfies the comparison instantly and claims with ZERO real listen time and no
        /// warning. Gating on both clocks makes that impossible rather than merely
        /// documented.
        /// </summary>
        private double _listenStartRealMs;
        private long _claimSentMs;
        private long _cannotClaimDueMs;
        private bool _cannotClaimPending;

        /// <summary>Creates the state machine.</summary>
        /// <param name="name">
        /// This tool's NAME. A private copy is taken, so later edits to the caller's
        /// instance do not change what has already been announced on the bus.
        /// </param>
        /// <param name="transmit">
        /// Callback that puts a frame on the bus. Invoked outside the internal lock.
        /// MUST return true only if the frame actually reached the adapter: returning false
        /// invalidates any claim whose AddressClaimed did not get out, rather than leaving
        /// this machine reporting an address it never announced.
        /// Exceptions thrown by it propagate to the caller of <see cref="Tick"/> or
        /// <see cref="OnMessageReceived"/>, because a failed bus write is real and
        /// must not be swallowed.
        /// </param>
        /// <exception cref="ArgumentNullException">Either argument is null.</exception>
        public J1939AddressClaim(J1939Name name, Func<J1939Message, bool> transmit)
        {
            if (name == null)
            {
                throw new ArgumentNullException("name");
            }

            if (transmit == null)
            {
                throw new ArgumentNullException("transmit");
            }

            _name = name.Clone();
            _transmit = transmit;
        }

        /// <summary>
        /// Raised on every state transition, after the internal lock is released.
        /// Handler exceptions are caught and reported through <see cref="Log"/> so a
        /// faulty UI handler cannot stop the bus pump.
        /// </summary>
        public event EventHandler<J1939AddressClaimEventArgs> StateChanged;

        /// <summary>
        /// Optional technician-facing log sink. Invoked outside the internal lock,
        /// and its exceptions are swallowed - a broken logger must never take down
        /// the network-management stack.
        /// </summary>
        public Action<string> Log
        {
            get { lock (_sync) { return _log; } }
            set { lock (_sync) { _log = value; } }
        }

        /// <summary>
        /// Third claim candidate, tried after 249 and 250. Defaults to
        /// <see cref="FALLBACK_DISABLED"/> (254), meaning no third candidate: J1939
        /// does not reserve a third service-tool address, so rather than guess one
        /// this class fails loudly and lets the technician nominate an address.
        /// </summary>
        /// <exception cref="ArgumentOutOfRangeException">Set to the global address (255).</exception>
        public byte FallbackAddress
        {
            get { lock (_sync) { return _fallbackAddress; } }
            set
            {
                if (value == J1939Pgn.GlobalAddress)
                {
                    throw new ArgumentOutOfRangeException("value", value,
                        "The global address (255) cannot be claimed. Use 254 to disable the fallback candidate.");
                }

                lock (_sync) { _fallbackAddress = value; }
            }
        }

        /// <summary>
        /// When true (the default), a Request for AddressClaimed is broadcast at the
        /// start of the listen window so every node announces itself and the map is
        /// populated before any address is claimed. It is sent from the null address
        /// (254) because no address has been claimed yet.
        /// </summary>
        /// <remarks>
        /// Turning this off makes the tool passively silent until it claims, at the
        /// cost of claiming against an empty map: nodes that claimed before the tool
        /// was plugged in will not have been observed, so a contention becomes more
        /// likely rather than less.
        /// </remarks>
        public bool RequestAddressClaimedOnStart
        {
            get { lock (_sync) { return _requestAddressClaimedOnStart; } }
            set { lock (_sync) { _requestAddressClaimedOnStart = value; } }
        }

        /// <summary>Current state of the claim sequence.</summary>
        public J1939AddressClaimState State
        {
            get { lock (_sync) { return _state; } }
        }

        /// <summary>
        /// Source address currently held, or <see cref="J1939Pgn.NullAddress"/> (254)
        /// when none is held. Show this to the technician: they need to see what the
        /// tool is doing on the bus.
        /// </summary>
        public byte ClaimedAddress
        {
            get { lock (_sync) { return _claimAddress; } }
        }

        /// <summary>True only when the address has been claimed and the contention window has passed.</summary>
        public bool HasClaimedAddress
        {
            get { lock (_sync) { return _state == J1939AddressClaimState.Claimed; } }
        }

        /// <summary>A copy of this tool's NAME.</summary>
        public J1939Name Name
        {
            get { lock (_sync) { return _name.Clone(); } }
        }

        /// <summary>
        /// Begins the claim sequence: enter the mandatory listen window, then claim.
        /// May be called again after <see cref="J1939AddressClaimState.Failed"/> or
        /// <see cref="J1939AddressClaimState.LostArbitration"/> to retry, which
        /// restarts the full listen window and re-tries every candidate.
        /// </summary>
        /// <param name="nowMs">
        /// Monotonic millisecond timestamp from the adapter or a Stopwatch. Never
        /// DateTime.Now.
        /// </param>
        /// <remarks>
        /// The observed source-address map is deliberately NOT cleared on a retry: an
        /// address seen in use once is still assumed in use, because a module that was
        /// quiet during one listen window may simply have had nothing to say. If the
        /// technician has genuinely removed a node and wants its address back, call
        /// <see cref="ClearSourceAddressMap"/> first.
        /// </remarks>
        /// <exception cref="InvalidOperationException">A claim sequence is already in progress.</exception>
        public void Start(long nowMs)
        {
            lock (_sync)
            {
                if (_state == J1939AddressClaimState.Listening
                    || _state == J1939AddressClaimState.Claiming
                    || _state == J1939AddressClaimState.Claimed)
                {
                    throw new InvalidOperationException(
                        "Address claim already in progress or complete (state " + _state + ").");
                }

                _lastTickMs = nowMs;
                _listenStartMs = nowMs;
                _listenStartRealMs = _echoClock.Elapsed.TotalMilliseconds;
                _claimAddress = J1939Pgn.NullAddress;
                _candidateIndex = 0;
                _cannotClaimPending = false;
                BuildCandidatesLocked();
                WarnAboutIdentityLocked();

                SetStateLocked(J1939AddressClaimState.Listening, string.Format(CultureInfo.InvariantCulture,
                    "listening {0} ms before claiming an address", MINIMUM_LISTEN_MS));

                if (_requestAddressClaimedOnStart)
                {
                    QueueFrameLocked(J1939Id.Create(NETWORK_MANAGEMENT_PRIORITY, J1939Pgn.Request, J1939Pgn.NullAddress, J1939Pgn.GlobalAddress),
                        new byte[]
                        {
                            (byte)(J1939Pgn.AddressClaimed & 0xFFu),
                            (byte)((J1939Pgn.AddressClaimed >> 8) & 0xFFu),
                            (byte)((J1939Pgn.AddressClaimed >> 16) & 0xFFu)
                        });

                    NoteLocked("J1939 address claim: requesting AddressClaimed from all nodes to map the bus before claiming.");
                }
            }

            Flush();
        }

        /// <summary>
        /// Advances the timers. Drive this from the same thread that pumps received
        /// frames.
        /// </summary>
        /// <param name="nowMs">
        /// Monotonic millisecond timestamp from the adapter or a Stopwatch. Never
        /// DateTime.Now.
        /// </param>
        public void Tick(long nowMs)
        {
            lock (_sync)
            {
                if (nowMs < _lastTickMs)
                {
                    // A clock that went backwards would freeze every timer here.
                    // Rebase rather than hang, and say so.
                    NoteLocked(string.Format(CultureInfo.InvariantCulture,
                        "J1939 address claim: tick clock went backwards ({0} ms -> {1} ms); rebasing timers. The owner must supply a monotonic clock.",
                        _lastTickMs, nowMs));

                    long shift = _lastTickMs - nowMs;
                    _listenStartMs -= shift;
                    _claimSentMs -= shift;
                    _cannotClaimDueMs -= shift;
                }

                _lastTickMs = nowMs;

                if (_cannotClaimPending && nowMs >= _cannotClaimDueMs)
                {
                    _cannotClaimPending = false;
                    QueueAddressClaimedLocked(J1939Pgn.NullAddress);
                    NoteLocked("J1939 address claim: transmitted Cannot Claim Address (SA 254).");
                }

                switch (_state)
                {
                    case J1939AddressClaimState.Listening:
                        // BOTH clocks must agree the window has elapsed. The caller's tick
                        // values drive protocol timing, but the real-time gate is what
                        // makes the safety interlock unskippable - see _listenStartRealMs.
                        double realListenedMs = _echoClock.Elapsed.TotalMilliseconds - _listenStartRealMs;

                        if (nowMs - _listenStartMs >= MINIMUM_LISTEN_MS
                            && realListenedMs >= MINIMUM_LISTEN_MS)
                        {
                            BeginClaimLocked("listen window complete");
                        }
                        else if (nowMs - _listenStartMs >= MINIMUM_LISTEN_MS && !_mixedClockWarned)
                        {
                            _mixedClockWarned = true;
                            NoteLocked(string.Format(CultureInfo.InvariantCulture,
                                "J1939 address claim: the tick values say the {0} ms listen window has elapsed but only "
                                + "{1:F0} ms of real time has passed. Start() and Tick() appear to be driven from "
                                + "different clocks. Holding the claim until the window has genuinely elapsed - claiming "
                                + "early risks knocking a live ECU off the bus.",
                                MINIMUM_LISTEN_MS, realListenedMs));
                        }

                        break;

                    case J1939AddressClaimState.Claiming:
                        if (nowMs - _claimSentMs >= CONTENTION_WINDOW_MS)
                        {
                            SetStateLocked(J1939AddressClaimState.Claimed, string.Format(CultureInfo.InvariantCulture,
                                "no competing claim within {0} ms", CONTENTION_WINDOW_MS));

                            NoteLocked(string.Format(CultureInfo.InvariantCulture,
                                "J1939 address claim: SA 0x{0:X2} ({0}) claimed and held. The tool is now a participant on this bus.",
                                _claimAddress));
                        }

                        break;
                }
            }

            Flush();
        }

        /// <summary>
        /// Feeds one received frame in. Every frame is used to maintain the
        /// source-address map; AddressClaimed and Request frames additionally drive
        /// the state machine.
        /// </summary>
        /// <param name="message">The received frame. Ignored when null.</param>
        public void OnMessageReceived(J1939Message message)
        {
            if (message == null)
            {
                return;
            }

            lock (_sync)
            {
                byte sourceAddress = message.SourceAddress;

                // A frame bearing OUR source address is usually our own echo - most J2534
                // and RP1210 configurations loop transmissions back. But it might instead
                // be another node transmitting on the address we hold, and THAT is the
                // case this class exists to prevent: continuing to transmit on a contested
                // address is what makes a real ECU relocate or, on an older
                // non-arbitrary-address-capable node, go silent. On a truck that is a
                // warranty conversation; on plant with a hydraulic controller in the loop
                // it is a safety conversation.
                //
                // The previous code filtered our address out entirely, which made a
                // genuine collision completely invisible: no map entry, no log line, no
                // relocation. Distinguish the two by matching against what we actually
                // sent, and treat anything unmatched as a collision.
                if (sourceAddress != J1939Pgn.NullAddress
                    && sourceAddress != J1939Pgn.GlobalAddress)
                {
                    if (sourceAddress != _claimAddress)
                    {
                        TouchLocked(sourceAddress);
                    }
                    else if (!IsOwnEchoLocked(message))
                    {
                        HandleAddressCollisionLocked(message);
                    }
                }

                if (message.Pgn == J1939Pgn.AddressClaimed)
                {
                    HandleAddressClaimedLocked(message);
                }
                else if (message.Pgn == J1939Pgn.Request)
                {
                    HandleRequestLocked(message);
                }
            }

            Flush();
        }

        /// <summary>
        /// Snapshot of every source address observed on the bus, ordered by address.
        /// Entries are copies; the receive thread cannot mutate them afterwards.
        /// </summary>
        /// <remarks>
        /// This tool's own address is not in the map - the map records what was
        /// observed from other nodes. Add <see cref="ClaimedAddress"/> and
        /// <see cref="Name"/> to it when presenting a whole-bus participant list.
        /// </remarks>
        /// <returns>The observed source addresses.</returns>
        public J1939SourceAddressEntry[] GetSourceAddressMap()
        {
            lock (_sync)
            {
                J1939SourceAddressEntry[] snapshot = new J1939SourceAddressEntry[_map.Count];
                int index = 0;
                foreach (KeyValuePair<byte, J1939SourceAddressEntry> pair in _map)
                {
                    snapshot[index++] = pair.Value.Clone();
                }

                Array.Sort(snapshot, CompareEntriesByAddress);
                return snapshot;
            }
        }

        /// <summary>Looks up the NAME observed claiming a source address.</summary>
        /// <param name="sourceAddress">Source address to look up.</param>
        /// <param name="name">Receives a copy of the NAME, or null when none was observed.</param>
        /// <returns>True when an AddressClaimed frame has been seen from this address.</returns>
        public bool TryGetName(byte sourceAddress, out J1939Name name)
        {
            lock (_sync)
            {
                J1939SourceAddressEntry entry;
                if (_map.TryGetValue(sourceAddress, out entry) && entry.Name != null)
                {
                    name = entry.Name.Clone();
                    return true;
                }

                name = null;
                return false;
            }
        }

        /// <summary>
        /// Forgets every observed source address. Only useful when a node has
        /// genuinely been removed from the bus and its address should become
        /// claimable again on the next <see cref="Start"/>.
        /// </summary>
        /// <remarks>
        /// Clearing the map discards the evidence this class uses to avoid claiming
        /// an occupied address. Call it because a module was unplugged, not to make a
        /// failed claim succeed.
        /// </remarks>
        public void ClearSourceAddressMap()
        {
            lock (_sync)
            {
                _map.Clear();
                NoteLocked("J1939 address claim: observed source-address map cleared by request. Addresses previously seen in use are now treated as free.");
            }

            Flush();
        }

        /// <summary>One-line status for a UI status bar.</summary>
        /// <returns>For example "J1939: claimed SA 0x F9 (249), 7 other nodes on bus".</returns>
        public string DescribeStatus()
        {
            lock (_sync)
            {
                switch (_state)
                {
                    case J1939AddressClaimState.NotStarted:
                        return "J1939: not started, no address claimed";

                    case J1939AddressClaimState.Listening:
                        return string.Format(CultureInfo.InvariantCulture,
                            "J1939: listening ({0} of {1} ms), {2} nodes seen",
                            Math.Max(0L, _lastTickMs - _listenStartMs), MINIMUM_LISTEN_MS, _map.Count);

                    case J1939AddressClaimState.Claiming:
                        return string.Format(CultureInfo.InvariantCulture,
                            "J1939: claiming SA 0x{0:X2} ({0}), awaiting contention window", _claimAddress);

                    case J1939AddressClaimState.Claimed:
                        return string.Format(CultureInfo.InvariantCulture,
                            "J1939: claimed SA 0x{0:X2} ({0}), {1} other nodes on bus", _claimAddress, _map.Count);

                    case J1939AddressClaimState.LostArbitration:
                        return "J1939: LOST ARBITRATION, no address claimed - tool is not on the bus";

                    default:
                        return "J1939: FAILED to claim an address - tool is not on the bus";
                }
            }
        }

        private static int CompareEntriesByAddress(J1939SourceAddressEntry left, J1939SourceAddressEntry right)
        {
            return left.Address.CompareTo(right.Address);
        }

        // -----------------------------------------------------------------
        // Candidate selection
        // -----------------------------------------------------------------

        private void BuildCandidatesLocked()
        {
            _candidates.Clear();
            _candidates.Add(J1939Pgn.ServiceTool1Address);
            _candidates.Add(J1939Pgn.ServiceTool2Address);

            if (_fallbackAddress != FALLBACK_DISABLED
                && _fallbackAddress != J1939Pgn.GlobalAddress
                && !_candidates.Contains(_fallbackAddress))
            {
                _candidates.Add(_fallbackAddress);
            }

            // An arbitrary-address-capable node advertises that it can move. Walking
            // the self-configurable range downwards keeps us away from the low end,
            // where a handful of specific assignments live (CAN conditioners at 128,
            // 131, 139, 143), and it only ever picks addresses observed to be free.
            if (_name.ArbitraryAddressCapable)
            {
                for (int address = DYNAMIC_ADDRESS_HIGH; address >= DYNAMIC_ADDRESS_LOW; address--)
                {
                    byte candidate = (byte)address;
                    if (!_candidates.Contains(candidate))
                    {
                        _candidates.Add(candidate);
                    }
                }
            }
        }

        private bool TryNextFreeAddressLocked(out byte address)
        {
            while (_candidateIndex < _candidates.Count)
            {
                byte candidate = _candidates[_candidateIndex++];

                J1939SourceAddressEntry occupant;
                if (!_map.TryGetValue(candidate, out occupant))
                {
                    address = candidate;
                    return true;
                }

                NoteLocked(string.Format(CultureInfo.InvariantCulture,
                    "J1939 address claim: skipping SA 0x{0:X2} ({0}) - already in use by {1}. Not contesting it.",
                    candidate,
                    occupant.Name == null ? "a node that has not announced a NAME" : occupant.Name.ToString()));
            }

            address = J1939Pgn.NullAddress;
            return false;
        }

        private void BeginClaimLocked(string trigger)
        {
            byte candidate;
            if (!TryNextFreeAddressLocked(out candidate))
            {
                _claimAddress = J1939Pgn.NullAddress;
                SetStateLocked(J1939AddressClaimState.Failed, "no free source address available");
                ScheduleCannotClaimLocked();

                NoteLocked(
                    "J1939 ADDRESS CLAIM FAILED. Every candidate source address is already in use on this bus, "
                    + "so the tool has taken the null address (254) and is NOT a participant: it cannot send or "
                    + "receive addressed diagnostic traffic. It did not contest any address, because displacing a "
                    + "live ECU can take it off the bus until power cycle. "
                    + (_fallbackAddress == FALLBACK_DISABLED
                        ? "Set FallbackAddress to an address you know is free on this vehicle, then start again."
                        : "Nominate a different FallbackAddress, then start again."));

                return;
            }

            ClaimAddressLocked(candidate, trigger);
        }

        private void ClaimAddressLocked(byte address, string reason)
        {
            _claimAddress = address;
            _claimSentMs = _lastTickMs;
            QueueAddressClaimedLocked(address);
            SetStateLocked(J1939AddressClaimState.Claiming, reason);

            NoteLocked(string.Format(CultureInfo.InvariantCulture,
                "J1939 address claim: claiming SA 0x{0:X2} ({0}) - {1}. Waiting {2} ms for a competing claim.",
                address, reason, CONTENTION_WINDOW_MS));

            if (address < DYNAMIC_ADDRESS_LOW)
            {
                NoteLocked(string.Format(CultureInfo.InvariantCulture,
                    "J1939 address claim: WARNING - SA 0x{0:X2} ({0}) is in the preferred-address range reserved for "
                    + "vehicle ECUs. It was not observed in use, but a module that was asleep or unpowered during the "
                    + "listen window may claim it back and be displaced.",
                    address));
            }
        }

        // -----------------------------------------------------------------
        // Received network-management messages
        // -----------------------------------------------------------------

        private void HandleAddressClaimedLocked(J1939Message message)
        {
            if (message.Length < J1939Name.BYTE_LENGTH)
            {
                NoteLocked(string.Format(CultureInfo.InvariantCulture,
                    "J1939 address claim: ignoring malformed AddressClaimed from SA 0x{0:X2} - {1} data bytes, expected {2}.",
                    message.SourceAddress, message.Length, J1939Name.BYTE_LENGTH));
                return;
            }

            J1939Name other;
            if (!J1939Name.TryFromBytes(message.Data, 0, out other))
            {
                return;
            }

            byte sourceAddress = message.SourceAddress;

            if (sourceAddress == J1939Pgn.NullAddress)
            {
                // Cannot Claim Address: some other node failed to get an address.
                // Informational only, and deliberately not mapped - 254 is not owned.
                NoteLocked(string.Format(CultureInfo.InvariantCulture,
                    "J1939 address claim: another node reports Cannot Claim Address ({0}).", other));
                return;
            }

            if (other.Equals(_name))
            {
                // Either the adapter looped our own claim back, or a second node is
                // configured with our exact NAME. The first is common, the second is
                // an unresolvable fault - so say something, but do not yield to what
                // is most likely our own echo.
                bool plausibleEcho = sourceAddress == _claimAddress
                                     && (_state == J1939AddressClaimState.Claiming || _state == J1939AddressClaimState.Claimed)
                                     && (_lastTickMs - _claimSentMs) <= CONTENTION_WINDOW_MS;

                if (!plausibleEcho)
                {
                    NoteLocked(string.Format(CultureInfo.InvariantCulture,
                        "J1939 address claim: WARNING - AddressClaimed from SA 0x{0:X2} carries THIS TOOL'S NAME ({1}). "
                        + "Either the adapter is echoing transmitted frames, or a second node shares our NAME - which "
                        + "cannot be arbitrated. Give this tool a unique identity number.",
                        sourceAddress, other));
                }

                return;
            }

            RecordClaimLocked(sourceAddress, other);

            bool holdingThisAddress = (_state == J1939AddressClaimState.Claiming || _state == J1939AddressClaimState.Claimed)
                                      && sourceAddress == _claimAddress;

            if (holdingThisAddress)
            {
                HandleContentionLocked(other);
            }
        }

        /// <summary>
        /// True when <paramref name="message"/> is one of our own recently transmitted
        /// frames looped back by the adapter, rather than traffic from another node.
        /// Matched on identifier plus payload: an echo is byte-identical to what we sent,
        /// whereas a colliding node's frame will differ (different NAME, different PGN,
        /// or different data) except in the vanishingly unlikely case of an exact clone.
        /// </summary>
        private bool IsOwnEchoLocked(J1939Message message)
        {
            double nowMs = _echoClock.Elapsed.TotalMilliseconds;

            for (int i = _recentTransmits.Count - 1; i >= 0; i--)
            {
                TransmittedFrame sent = _recentTransmits[i];

                if (nowMs - sent.AtMs > ECHO_MEMORY_MS)
                {
                    _recentTransmits.RemoveAt(i);
                    continue;
                }

                if (sent.Raw != message.Id.Raw || sent.Data.Length != message.Length)
                {
                    continue;
                }

                bool same = true;
                for (int b = 0; b < sent.Data.Length; b++)
                {
                    if (sent.Data[b] != message.Data[b]) { same = false; break; }
                }

                if (same)
                {
                    // Consumed: a second identical frame is a real duplicate on the bus,
                    // not a second echo of one transmission.
                    _recentTransmits.RemoveAt(i);
                    return true;
                }
            }

            return false;
        }

        private void RecordTransmittedFrame(J1939Message frame)
        {
            lock (_sync)
            {
                if (_recentTransmits.Count >= ECHO_MEMORY_MAX_FRAMES)
                {
                    _recentTransmits.RemoveAt(0);
                }

                TransmittedFrame sent;
                sent.Raw = frame.Id.Raw;
                sent.Data = frame.Data;
                sent.AtMs = _echoClock.Elapsed.TotalMilliseconds;
                _recentTransmits.Add(sent);
            }
        }

        /// <summary>
        /// Another node is transmitting on the address this tool holds. Yield it rather
        /// than keep transmitting: two nodes on one address corrupt each other's traffic
        /// and can silence a genuine ECU.
        /// </summary>
        private void HandleAddressCollisionLocked(J1939Message message)
        {
            if (_state != J1939AddressClaimState.Claiming && _state != J1939AddressClaimState.Claimed)
            {
                return;
            }

            byte contested = _claimAddress;

            // Rate-limit: a colliding node is typically broadcasting continuously, and one
            // note per frame would bury the log at the moment the technician most needs it.
            bool alreadyReported = _collisionReportedFor == contested;
            _collisionReportedFor = contested;

            if (!alreadyReported)
            {
                NoteLocked(string.Format(CultureInfo.InvariantCulture,
                    "J1939 ADDRESS COLLISION: another node is transmitting on SA 0x{0:X2} ({0}), the address this tool "
                    + "holds (observed {1}). Two nodes on one address corrupt each other's traffic, and on an older "
                    + "non-arbitrary-address-capable ECU it can silence the module. Yielding the address now.",
                    contested, J1939Pgn.Describe(message.Pgn)));
            }

            TouchLocked(contested);

            _claimAddress = J1939Pgn.NullAddress;
            SetStateLocked(J1939AddressClaimState.LostArbitration, string.Format(CultureInfo.InvariantCulture,
                "SA 0x{0:X2} is in use by another node", contested));

            byte replacement;
            if (_name.ArbitraryAddressCapable && TryNextFreeAddressLocked(out replacement))
            {
                ClaimAddressLocked(replacement, string.Format(CultureInfo.InvariantCulture,
                    "relocating away from occupied SA 0x{0:X2}", contested));
                return;
            }

            ScheduleCannotClaimLocked();

            NoteLocked(
                "J1939 address collision with no free address to move to. The tool has taken the null address (254) "
                + "and is no longer a bus participant. Nominate a FallbackAddress you know is free, then start again.");
        }

        private void HandleContentionLocked(J1939Name other)
        {
            byte contestedAddress = _claimAddress;

            if (_name.WinsContentionAgainst(other))
            {
                // Winner re-asserts its claim. The loser is now obliged to move.
                QueueAddressClaimedLocked(contestedAddress);
                _claimSentMs = _lastTickMs;

                NoteLocked(string.Format(CultureInfo.InvariantCulture,
                    "J1939 address claim: contention on SA 0x{0:X2} ({0}). Our NAME 0x{1:X16} is lower than 0x{2:X16}, "
                    + "so we keep the address and have re-asserted the claim. The other node ({3}) must now relocate or "
                    + "go silent - if that was a vehicle ECU, disconnect and pick a different address.",
                    contestedAddress, _name.Value, other.Value, other));

                return;
            }

            NoteLocked(string.Format(CultureInfo.InvariantCulture,
                "J1939 address claim: LOST arbitration for SA 0x{0:X2} ({0}). Our NAME 0x{1:X16} is not lower than "
                + "0x{2:X16} ({3}). Surrendering the address.",
                contestedAddress, _name.Value, other.Value, other));

            _claimAddress = J1939Pgn.NullAddress;
            SetStateLocked(J1939AddressClaimState.LostArbitration, string.Format(CultureInfo.InvariantCulture,
                "SA 0x{0:X2} taken by a lower NAME", contestedAddress));

            byte replacement;
            if (_name.ArbitraryAddressCapable && TryNextFreeAddressLocked(out replacement))
            {
                ClaimAddressLocked(replacement, string.Format(CultureInfo.InvariantCulture,
                    "relocating after losing arbitration for SA 0x{0:X2}", contestedAddress));
                return;
            }

            ScheduleCannotClaimLocked();

            NoteLocked(
                "J1939 ADDRESS CLAIM LOST. The tool has surrendered its source address and taken the null address "
                + "(254). It is NOT a participant on this bus: addressed diagnostic traffic will not work until it "
                + "claims again. "
                + (_name.ArbitraryAddressCapable
                    ? "No free address remained to relocate to. Set FallbackAddress to an address you know is free, then start again."
                    : "This NAME is not arbitrary-address-capable, so it cannot self-relocate. Set ArbitraryAddressCapable on the tool NAME, or nominate a free FallbackAddress, then start again."));
        }

        /// <summary>
        /// Applies the PDU1 rule to a PGN that arrived as a payload field rather than
        /// inside a CAN identifier. For PDU1 (PF &lt;= 0xEF) the low byte is a destination
        /// address, not part of the parameter group, so it must be masked out before the
        /// value is compared against a known PGN.
        /// </summary>
        /// <param name="pgn">The raw 3-byte PGN as read from the payload.</param>
        /// <returns>The normalised PGN.</returns>
        private static uint NormalisePgn(uint pgn)
        {
            uint pf = (pgn >> 8) & 0xFF;
            return pf <= 0xEF ? (pgn & 0x3FF00u) : pgn;
        }

        private void HandleRequestLocked(J1939Message message)
        {
            if (message.Length < 3)
            {
                return;
            }

            uint requestedPgn = message.Data[0]
                                | ((uint)message.Data[1] << 8)
                                | ((uint)message.Data[2] << 16);

            // A PGN carried as a PAYLOAD field never passes through J1939Id, so nothing
            // has applied the PDU1 rule to it. AddressClaimed (60928, PF 0xEE) is PDU1,
            // and some ECUs and fleet gateways put the destination address in the PS byte
            // of the requested PGN rather than zeroing it. Without this mask such a
            // request compares unequal, we never answer, and the tool silently never
            // announces its address on that bus.
            requestedPgn = NormalisePgn(requestedPgn);

            if (requestedPgn != J1939Pgn.AddressClaimed)
            {
                return;
            }

            byte destination = message.Id.DestinationAddress;
            bool addressedToUs = destination == J1939Pgn.GlobalAddress
                                 || (_claimAddress != J1939Pgn.NullAddress && destination == _claimAddress);

            if (!addressedToUs)
            {
                return;
            }

            switch (_state)
            {
                case J1939AddressClaimState.Claiming:
                case J1939AddressClaimState.Claimed:
                    // Always broadcast: the response to a request for AddressClaimed
                    // goes to the global address so the whole segment learns of it.
                    QueueAddressClaimedLocked(_claimAddress);
                    break;

                case J1939AddressClaimState.LostArbitration:
                case J1939AddressClaimState.Failed:
                    ScheduleCannotClaimLocked();
                    break;

                default:
                    // NotStarted or Listening: we have made no claim and the listen
                    // window is mandatory, so answering would announce an address we
                    // have not taken. Stay quiet.
                    break;
            }
        }

        // -----------------------------------------------------------------
        // Source-address map
        // -----------------------------------------------------------------

        private void TouchLocked(byte sourceAddress)
        {
            J1939SourceAddressEntry entry;
            if (_map.TryGetValue(sourceAddress, out entry))
            {
                entry.LastSeenMs = _lastTickMs;
                return;
            }

            _map[sourceAddress] = new J1939SourceAddressEntry(sourceAddress, null, _lastTickMs, _lastTickMs, 0);
        }

        private void RecordClaimLocked(byte sourceAddress, J1939Name name)
        {
            J1939SourceAddressEntry entry;
            if (!_map.TryGetValue(sourceAddress, out entry))
            {
                entry = new J1939SourceAddressEntry(sourceAddress, null, _lastTickMs, _lastTickMs, 0);
                _map[sourceAddress] = entry;
            }

            entry.Name = name;
            entry.LastSeenMs = _lastTickMs;
            entry.ClaimCount = entry.ClaimCount + 1;
        }

        // -----------------------------------------------------------------
        // Outgoing frames
        // -----------------------------------------------------------------

        private void QueueAddressClaimedLocked(byte sourceAddress)
        {
            QueueFrameLocked(
                J1939Id.Create(NETWORK_MANAGEMENT_PRIORITY, J1939Pgn.AddressClaimed, sourceAddress, J1939Pgn.GlobalAddress),
                _name.ToBytes());
        }

        private void QueueFrameLocked(J1939Id id, byte[] data)
        {
            _outbox.Add(J1939Message.FromHostClock(id, data));
        }

        private void ScheduleCannotClaimLocked()
        {
            if (_cannotClaimPending)
            {
                return;
            }

            _cannotClaimPending = true;
            _cannotClaimDueMs = _lastTickMs + CannotClaimDelayMs();
        }

        /// <summary>
        /// Pseudo-random 0..153 ms delay before a Cannot Claim Address message, so
        /// several failing nodes do not answer in lockstep. Derived from the low byte
        /// of our NAME rather than a random number generator, so the delay is
        /// reproducible when a capture is read back.
        /// </summary>
        private int CannotClaimDelayMs()
        {
            return (int)((_name.Value & 0xFFUL) * (ulong)CANNOT_CLAIM_MAX_DELAY_MS / 255UL);
        }

        // -----------------------------------------------------------------
        // State, logging and deferred dispatch
        // -----------------------------------------------------------------

        private void SetStateLocked(J1939AddressClaimState next, string reason)
        {
            J1939AddressClaimState previous = _state;
            _state = next;
            _events.Add(new J1939AddressClaimEventArgs(previous, next, _claimAddress, reason));
        }

        private void NoteLocked(string note)
        {
            _notes.Add(note);
        }

        private void WarnAboutIdentityLocked()
        {
            if (_name.ManufacturerCode == 0)
            {
                NoteLocked(
                    "J1939 address claim: WARNING - manufacturer code 0 is not an SAE assignment. The NAME this tool "
                    + "puts on the bus is not a valid identity, and contention outcomes against real ECUs will be "
                    + "decided by an arbitrary value. Set the assigned manufacturer code before live use.");
            }

            if (_name.Function == 0)
            {
                NoteLocked(
                    "J1939 address claim: WARNING - function code 0 announces this node as an engine controller. "
                    + "Set the J1939-71 function code for an off-board diagnostic service tool.");
            }
        }

        /// <summary>
        /// Dispatches everything queued while the lock was held. Frames go out first
        /// after the notes that explain them, so a technician's log reads in order.
        /// </summary>
        /// <summary>
        /// Roll back a claim whose AddressClaimed frame could not be transmitted, and
        /// report the frames that were never sent. Called only from the Flush transmit
        /// loop when the owner's transmit callback throws.
        /// </summary>
        /// <param name="frames">The batch being flushed.</param>
        /// <param name="failedIndex">Index of the frame whose transmit threw.</param>
        private void InvalidateUnannouncedClaim(J1939Message[] frames, int failedIndex)
        {
            int undelivered = frames.Length - failedIndex;

            lock (_sync)
            {
                if (_state != J1939AddressClaimState.Claiming && _state != J1939AddressClaimState.Claimed)
                {
                    return;
                }

                byte lost = _claimAddress;
                _claimAddress = J1939Pgn.NullAddress;

                SetStateLocked(J1939AddressClaimState.Failed, string.Format(CultureInfo.InvariantCulture,
                    "could not transmit AddressClaimed for SA 0x{0:X2}", lost));

                NoteLocked(string.Format(CultureInfo.InvariantCulture,
                    "J1939 address claim ABANDONED: the adapter refused the write for SA 0x{0:X2} ({0}), so the claim "
                    + "never reached the bus ({1} queued frame(s) undelivered). The tool holds no address. Check the "
                    + "adapter connection, bus termination and that the ignition is on, then start again.",
                    lost, undelivered));
            }

            // Deliberately outside the lock, and best-effort: the throw is already
            // propagating, so a listener that also throws must not mask it.
            try { Flush(); } catch { }
        }

        private void Flush()
        {
            J1939Message[] frames;
            string[] notes;
            J1939AddressClaimEventArgs[] events;
            Action<string> log;

            lock (_sync)
            {
                log = _log;

                if (_notes.Count == 0 && _outbox.Count == 0 && _events.Count == 0)
                {
                    return;
                }

                notes = _notes.Count == 0 ? NoNotes : _notes.ToArray();
                frames = _outbox.Count == 0 ? NoFrames : _outbox.ToArray();
                events = _events.Count == 0 ? NoEvents : _events.ToArray();

                _notes.Clear();
                _outbox.Clear();
                _events.Clear();
            }

            for (int i = 0; i < notes.Length; i++)
            {
                SafeLog(log, notes[i]);
            }

            for (int i = 0; i < frames.Length; i++)
            {
                bool delivered;
                try
                {
                    delivered = _transmit(frames[i]);
                }
                catch
                {
                    // A failed bus write is a real fault and must still surface to the
                    // owner - but the state machine has ALREADY committed the transition
                    // that queued this frame. Leaving it committed would mean believing we
                    // hold an address whose AddressClaimed never reached the wire: the tool
                    // would then transmit on an address no other node knows it owns.
                    // Invalidate the claim first, then let the exception out.
                    InvalidateUnannouncedClaim(frames, i);
                    throw;
                }

                if (!delivered)
                {
                    // The adapter REFUSED the frame rather than throwing - the common case
                    // on a bus that is off, mis-wired, in error-passive, or simply not
                    // there. Identical consequence to the throw path above: the claim was
                    // already committed, so without this the machine reports Claimed on an
                    // address it never announced. That is the worst outcome available here,
                    // because every later transmit then uses a source address no other node
                    // associates with us.
                    InvalidateUnannouncedClaim(frames, i);
                    return;
                }

                RecordTransmittedFrame(frames[i]);
            }

            EventHandler<J1939AddressClaimEventArgs> handler = StateChanged;
            if (handler == null)
            {
                return;
            }

            for (int i = 0; i < events.Length; i++)
            {
                try
                {
                    handler(this, events[i]);
                }
                catch (Exception ex)
                {
                    SafeLog(log, "J1939 address claim: StateChanged handler threw - " + ex.Message);
                }
            }
        }

        private static void SafeLog(Action<string> log, string text)
        {
            if (log == null)
            {
                return;
            }

            try
            {
                log(text);
            }
            catch (Exception)
            {
                // A broken logger must never stop the network-management stack.
            }
        }
    }
}
