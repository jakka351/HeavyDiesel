// =============================================================================
//  J1939Message.cs - one received or outgoing J1939 frame / reassembled message
//
//  TIMESTAMPS ARE LOAD-BEARING. J1939-21 transport timing (Tr 200 ms, Th, T1..T4,
//  50 ms inter-packet spacing) must be measured against the ADAPTER's receive
//  timestamp wherever the device provides one. Windows thread scheduling plus USB
//  round-trip latency routinely adds tens of milliseconds between a frame arriving
//  at the interface and managed code seeing it, which is enough to abort a healthy
//  transfer or to accept a stale one. DateTime.Now is doubly wrong: it is neither
//  monotonic (NTP steps and DST shifts move it backwards) nor fine-grained enough.
//
//  The host fallback here is a process-wide Stopwatch, and it is never silent:
//  TimestampSource reports which clock produced TimestampMs, and ToString marks
//  host-clock frames so a technician reading a log knows the timing is soft.
// =============================================================================

using System;
using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace HeavyDiesel.J1939
{
    /// <summary>
    /// Unit of the raw timestamp value reported by a hardware adapter. Adapters
    /// differ (J2534 PassThru counts microseconds, most RP1210 drivers count
    /// milliseconds), so the unit travels with the value instead of being assumed.
    /// </summary>
    public enum J1939TimestampUnit
    {
        /// <summary>Unit not established. The raw value is retained but is not converted to milliseconds.</summary>
        Unknown = 0,

        /// <summary>Raw value counts microseconds.</summary>
        Microseconds = 1,

        /// <summary>Raw value counts milliseconds.</summary>
        Milliseconds = 2
    }

    /// <summary>Which clock produced <see cref="J1939Message.TimestampMs"/>.</summary>
    public enum J1939TimestampSource
    {
        /// <summary>Hardware adapter timestamp. Suitable for protocol timing.</summary>
        Adapter = 0,

        /// <summary>Host monotonic clock, sampled when the message object was created. Includes driver and scheduling latency.</summary>
        HostClock = 1
    }

    /// <summary>
    /// A single J1939 message: a 29-bit identifier, its data, and the timing
    /// information needed to run J1939-21 transport correctly.
    /// </summary>
    /// <remarks>
    /// <see cref="Data"/> may be a single CAN frame payload (0..8 bytes) or a
    /// reassembled transport payload of up to 1785 bytes (TP) or ~117 MB (ETP).
    /// </remarks>
    public class J1939Message
    {
        // Monotonic process clock. Stopwatch is immune to wall-clock adjustment;
        // the captured origin exists only so logs can show a wall-clock time
        // without ever using DateTime.Now for an interval measurement.
        private static readonly Stopwatch _hostClock = Stopwatch.StartNew();
        private static readonly DateTime _hostClockOriginUtc = DateTime.UtcNow;

        private readonly J1939Id _id;
        private readonly byte[] _data;
        private readonly uint _adapterTimestamp;
        private readonly bool _hasAdapterTimestamp;
        private readonly J1939TimestampUnit _adapterTimestampUnit;
        private readonly double _hostTimestampMs;

        /// <summary>
        /// Creates a message stamped with the host monotonic clock. Use only when the
        /// adapter provides no timestamp, or for outgoing messages.
        /// </summary>
        /// <param name="id">The 29-bit identifier.</param>
        /// <param name="data">Message data. Copied; the caller keeps ownership of the array it passed.</param>
        /// <exception cref="ArgumentNullException"><paramref name="data"/> is null. Pass an empty array for a zero-length frame.</exception>
        public J1939Message(J1939Id id, byte[] data)
            : this(id, data, 0, false, J1939TimestampUnit.Unknown)
        {
        }

        /// <summary>
        /// Creates a message stamped with a hardware adapter timestamp.
        /// </summary>
        /// <param name="id">The 29-bit identifier.</param>
        /// <param name="data">Message data. Copied; the caller keeps ownership of the array it passed.</param>
        /// <param name="adapterTimestamp">Raw timestamp value as reported by the device.</param>
        /// <param name="unit">
        /// Unit of <paramref name="adapterTimestamp"/>. Pass
        /// <see cref="J1939TimestampUnit.Unknown"/> if the device documentation does not
        /// state it - the value is then preserved but timing falls back to the host clock
        /// rather than being converted with an invented scale factor.
        /// </param>
        /// <exception cref="ArgumentNullException"><paramref name="data"/> is null.</exception>
        public J1939Message(J1939Id id, byte[] data, uint adapterTimestamp, J1939TimestampUnit unit)
            : this(id, data, adapterTimestamp, true, unit)
        {
        }

        private J1939Message(J1939Id id, byte[] data, uint adapterTimestamp, bool hasAdapterTimestamp, J1939TimestampUnit unit)
        {
            if (data == null)
            {
                throw new ArgumentNullException("data",
                    "J1939Message data must not be null; pass an empty array for a zero-length frame.");
            }

            _id = id;

            // Copy so a driver read loop that recycles one receive buffer cannot
            // retroactively rewrite the contents of messages already queued.
            _data = new byte[data.Length];
            Buffer.BlockCopy(data, 0, _data, 0, data.Length);

            _adapterTimestamp = adapterTimestamp;
            _hasAdapterTimestamp = hasAdapterTimestamp;
            _adapterTimestampUnit = unit;
            _hostTimestampMs = _hostClock.Elapsed.TotalMilliseconds;
        }

        /// <summary>Creates a message from adapter-supplied timing. Equivalent to the four-argument constructor.</summary>
        /// <param name="id">The 29-bit identifier.</param>
        /// <param name="data">Message data. Copied.</param>
        /// <param name="adapterTimestamp">Raw timestamp value as reported by the device.</param>
        /// <param name="unit">Unit of the raw timestamp value.</param>
        /// <returns>The new message.</returns>
        public static J1939Message FromAdapter(J1939Id id, byte[] data, uint adapterTimestamp, J1939TimestampUnit unit)
        {
            return new J1939Message(id, data, adapterTimestamp, unit);
        }

        /// <summary>Creates a message stamped with the host monotonic clock.</summary>
        /// <param name="id">The 29-bit identifier.</param>
        /// <param name="data">Message data. Copied.</param>
        /// <returns>The new message.</returns>
        public static J1939Message FromHostClock(J1939Id id, byte[] data)
        {
            return new J1939Message(id, data);
        }

        /// <summary>The 29-bit identifier.</summary>
        public J1939Id Id
        {
            get { return _id; }
        }

        /// <summary>
        /// Message data. The array is owned by this message; treat it as read-only.
        /// </summary>
        public byte[] Data
        {
            get { return _data; }
        }

        /// <summary>Number of data bytes.</summary>
        public int Length
        {
            get { return _data.Length; }
        }

        /// <summary>Parameter group number, with PDU1 PS masking already applied.</summary>
        public uint Pgn
        {
            get { return _id.Pgn; }
        }

        /// <summary>Transmitting node address.</summary>
        public byte SourceAddress
        {
            get { return _id.SourceAddress; }
        }

        /// <summary>Destination address; the global address (255) for PDU2 groups.</summary>
        public byte DestinationAddress
        {
            get { return _id.DestinationAddress; }
        }

        /// <summary>True when this message is not directed at one specific node.</summary>
        public bool IsBroadcast
        {
            get { return _id.IsBroadcast; }
        }

        /// <summary>
        /// Raw timestamp value exactly as the adapter reported it, or 0 when the
        /// adapter supplied none. Free-running device counters wrap at 2^32 ticks
        /// (about 71.6 minutes at microsecond resolution), so compare intervals with
        /// <see cref="ElapsedMsBetween"/> rather than subtracting absolute values.
        /// </summary>
        public uint AdapterTimestamp
        {
            get { return _adapterTimestamp; }
        }

        /// <summary>True when the adapter supplied a timestamp for this message.</summary>
        public bool HasAdapterTimestamp
        {
            get { return _hasAdapterTimestamp; }
        }

        /// <summary>Unit of <see cref="AdapterTimestamp"/>.</summary>
        public J1939TimestampUnit AdapterTimestampUnit
        {
            get { return _adapterTimestampUnit; }
        }

        /// <summary>
        /// Host monotonic clock reading, in milliseconds since the first use of this
        /// type in the process. Always populated, including on adapter-stamped
        /// messages, so the two clocks can be compared when diagnosing latency.
        /// </summary>
        public double HostTimestampMs
        {
            get { return _hostTimestampMs; }
        }

        /// <summary>
        /// Which clock <see cref="TimestampMs"/> came from. Anything other than
        /// <see cref="J1939TimestampSource.Adapter"/> means protocol timing measured
        /// from this message includes driver and scheduling latency.
        /// </summary>
        public J1939TimestampSource TimestampSource
        {
            get
            {
                return (_hasAdapterTimestamp && _adapterTimestampUnit != J1939TimestampUnit.Unknown)
                    ? J1939TimestampSource.Adapter
                    : J1939TimestampSource.HostClock;
            }
        }

        /// <summary>
        /// Timestamp in milliseconds, preferring the adapter value when one is present
        /// and its unit is known. Falls back to the host monotonic clock otherwise -
        /// check <see cref="TimestampSource"/> before treating a value as wire-accurate.
        /// </summary>
        public double TimestampMs
        {
            get
            {
                if (_hasAdapterTimestamp)
                {
                    switch (_adapterTimestampUnit)
                    {
                        case J1939TimestampUnit.Microseconds:
                            return _adapterTimestamp / 1000.0;
                        case J1939TimestampUnit.Milliseconds:
                            return _adapterTimestamp;
                    }
                }

                return _hostTimestampMs;
            }
        }

        /// <summary>
        /// Wall-clock UTC for display and log correlation only, derived from the
        /// monotonic host clock rather than sampled from the system clock. Never use
        /// this for interval measurement - use <see cref="ElapsedMsBetween"/>.
        /// </summary>
        public DateTime CapturedUtc
        {
            get { return _hostClockOriginUtc.AddMilliseconds(_hostTimestampMs); }
        }

        /// <summary>
        /// Milliseconds elapsed from <paramref name="earlier"/> to <paramref name="later"/>,
        /// using adapter timestamps when both messages carry them in the same known unit
        /// (with 32-bit counter wrap handled), and the host monotonic clock otherwise.
        /// This is the interval to test J1939-21 timers against.
        /// </summary>
        /// <param name="earlier">The earlier message.</param>
        /// <param name="later">The later message.</param>
        /// <returns>Elapsed milliseconds. Negative only if the arguments are in the wrong order on the host-clock path.</returns>
        /// <exception cref="ArgumentNullException">Either message is null.</exception>
        public static double ElapsedMsBetween(J1939Message earlier, J1939Message later)
        {
            if (earlier == null) throw new ArgumentNullException("earlier");
            if (later == null) throw new ArgumentNullException("later");

            bool bothFromAdapter = earlier._hasAdapterTimestamp
                                   && later._hasAdapterTimestamp
                                   && earlier._adapterTimestampUnit == later._adapterTimestampUnit
                                   && earlier._adapterTimestampUnit != J1939TimestampUnit.Unknown;

            if (bothFromAdapter)
            {
                // Unsigned subtraction wraps correctly across the 2^32 counter rollover
                // provided the true interval is shorter than one wrap period, which every
                // J1939-21 timer is by orders of magnitude.
                uint ticks = unchecked(later._adapterTimestamp - earlier._adapterTimestamp);

                return earlier._adapterTimestampUnit == J1939TimestampUnit.Microseconds
                    ? ticks / 1000.0
                    : ticks;
            }

            return later._hostTimestampMs - earlier._hostTimestampMs;
        }

        /// <summary>
        /// Single-line log form, for example
        /// "[    12.345] PGN 65226 (0xFECA) SA 0x00 prio 6 [8] 00 FF 00 00 00 00 00 00".
        /// Host-clock-timed messages are marked so soft timing is visible in a log.
        /// </summary>
        /// <returns>The formatted message.</returns>
        public override string ToString()
        {
            StringBuilder sb = new StringBuilder(96 + (_data.Length * 3));

            sb.AppendFormat(CultureInfo.InvariantCulture, "[{0,10:F3}] ", TimestampMs);
            sb.Append(_id.ToString());
            sb.AppendFormat(CultureInfo.InvariantCulture, " [{0}]", _data.Length);

            for (int i = 0; i < _data.Length; i++)
            {
                sb.AppendFormat(CultureInfo.InvariantCulture, " {0:X2}", _data[i]);
            }

            if (TimestampSource == J1939TimestampSource.HostClock)
            {
                sb.Append("  (host clock - no adapter timestamp)");
            }

            return sb.ToString();
        }
    }
}
