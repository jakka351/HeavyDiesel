// =============================================================================
//  J1939NetworkMap.cs - who is on the bus, what they are saying, and how often.
//
//  WHAT THIS IS
//  ------------
//  The per-node / per-PGN bookkeeping behind a "J1939 network" screen: every
//  source address heard, the NAME it claimed (if a claim was seen), when it was
//  first and last heard, its frame rate, and the parameter groups it transmits
//  with their last payload and observed period.
//
//  It is a passive aggregator. Feed it application messages (single frames from
//  Rp1210Provider.FrameReceived in a listen-only setup, or reassembled messages
//  from J1939Session.ApplicationMessageReceived in an active one) and ask it for
//  a snapshot on a UI timer. It never transmits and never touches hardware.
//
//  CLOCK OWNERSHIP
//  ---------------
//  Same rule as J1939FaultTracker: no clock of its own. The caller supplies the
//  arrival time of every message and the "now" of every snapshot from ONE
//  monotonic clock (J1939Message.TimestampMs is the natural choice, with the
//  session or host Stopwatch for Evaluate). Rates and ages are only as good as
//  that discipline, and the snapshot flags a clock that runs backwards.
//
//  THREADING
//  ---------
//  Observe is called from the pump / notifier thread; Evaluate and GetSnapshot
//  from the UI thread. One lock; every snapshot is a fresh copy, so the UI can
//  never enumerate a collection the pump is mutating. Caller-supplied resolvers
//  (NAME lookup, PGN name lookup) run AFTER the lock is released.
//
//  Nothing here opens a file, touches the registry or reaches the network.
// =============================================================================

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;

namespace HeavyDiesel.J1939
{
    /// <summary>One parameter group as transmitted by one source address, snapshotted.</summary>
    public sealed class J1939PgnObservation
    {
        private readonly uint _pgn;
        private readonly string _pgnName;
        private readonly long _count;
        private readonly double _lastSeenMs;
        private readonly double _ageMs;
        private readonly double _lastIntervalMs;
        private readonly double _averageIntervalMs;
        private readonly byte _lastDestination;
        private readonly byte[] _lastPayload;
        private readonly bool _lastWasMultiFrame;

        internal J1939PgnObservation(uint pgn, string pgnName, long count, double lastSeenMs, double ageMs,
            double lastIntervalMs, double averageIntervalMs, byte lastDestination, byte[] lastPayload,
            bool lastWasMultiFrame)
        {
            _pgn = pgn;
            _pgnName = pgnName;
            _count = count;
            _lastSeenMs = lastSeenMs;
            _ageMs = ageMs;
            _lastIntervalMs = lastIntervalMs;
            _averageIntervalMs = averageIntervalMs;
            _lastDestination = lastDestination;
            _lastPayload = lastPayload;
            _lastWasMultiFrame = lastWasMultiFrame;
        }

        /// <summary>Parameter group number.</summary>
        public uint Pgn { get { return _pgn; } }

        /// <summary>Resolved group name, or null when no definition is loaded. Never guessed.</summary>
        public string PgnName { get { return _pgnName; } }

        /// <summary>True when <see cref="PgnName"/> is available.</summary>
        public bool PgnNameKnown { get { return !string.IsNullOrEmpty(_pgnName); } }

        /// <summary>Messages of this group seen from this source since the last clear.</summary>
        public long Count { get { return _count; } }

        /// <summary>Arrival time of the newest message, on the caller's clock.</summary>
        public double LastSeenMs { get { return _lastSeenMs; } }

        /// <summary>Milliseconds between the newest message and the snapshot reference time.</summary>
        public double AgeMs { get { return _ageMs; } }

        /// <summary>Interval between the two newest messages, or NaN when fewer than two have been seen.</summary>
        public double LastIntervalMs { get { return _lastIntervalMs; } }

        /// <summary>Mean interval over every message seen, or NaN when fewer than two have been seen.</summary>
        public double AverageIntervalMs { get { return _averageIntervalMs; } }

        /// <summary>Destination address of the newest message; 255 for broadcast groups.</summary>
        public byte LastDestination { get { return _lastDestination; } }

        /// <summary>Payload of the newest message. A private copy; safe to keep.</summary>
        public byte[] LastPayload { get { return _lastPayload; } }

        /// <summary>True when the newest message was reassembled from a multi-packet transfer.</summary>
        public bool LastWasMultiFrame { get { return _lastWasMultiFrame; } }

        /// <summary>Hex rendering of <see cref="LastPayload"/>, space separated.</summary>
        public string LastPayloadHex
        {
            get { return J1939Diagnostics.ToHex(_lastPayload); }
        }

        /// <summary>One line: "PGN 65262 (0xFEEE) ET1 x 41, 1000 ms, 8 bytes".</summary>
        /// <returns>The formatted observation.</returns>
        public override string ToString()
        {
            return string.Format(CultureInfo.InvariantCulture,
                "PGN {0} (0x{1:X4}){2} x {3}, period {4}, {5} bytes",
                _pgn, _pgn,
                string.IsNullOrEmpty(_pgnName) ? string.Empty : " " + _pgnName,
                _count,
                double.IsNaN(_averageIntervalMs) ? "n/a" : _averageIntervalMs.ToString("F0", CultureInfo.InvariantCulture) + " ms",
                _lastPayload == null ? 0 : _lastPayload.Length);
        }
    }

    /// <summary>One node (source address) on the bus, snapshotted.</summary>
    public sealed class J1939NodeObservation
    {
        private readonly byte _sourceAddress;
        private readonly J1939Name _name;
        private readonly double _firstSeenMs;
        private readonly double _lastSeenMs;
        private readonly double _ageMs;
        private readonly long _messageCount;
        private readonly double _messagesPerSecond;
        private readonly bool _isSilent;
        private readonly ReadOnlyCollection<J1939PgnObservation> _pgns;

        internal J1939NodeObservation(byte sourceAddress, J1939Name name, double firstSeenMs, double lastSeenMs,
            double ageMs, long messageCount, double messagesPerSecond, bool isSilent, IList<J1939PgnObservation> pgns)
        {
            _sourceAddress = sourceAddress;
            _name = name;
            _firstSeenMs = firstSeenMs;
            _lastSeenMs = lastSeenMs;
            _ageMs = ageMs;
            _messageCount = messageCount;
            _messagesPerSecond = messagesPerSecond;
            _isSilent = isSilent;
            _pgns = new ReadOnlyCollection<J1939PgnObservation>(pgns);
        }

        /// <summary>Source address, 0..253.</summary>
        public byte SourceAddress { get { return _sourceAddress; } }

        /// <summary>NAME this address announced in an Address Claimed frame, or null when no claim was observed. A clone.</summary>
        public J1939Name Name { get { return _name; } }

        /// <summary>True when an Address Claimed frame supplied a NAME for this address.</summary>
        public bool NameKnown { get { return _name != null; } }

        /// <summary>True when this is one of the J1939-81 off-board service tool addresses (249, 250).</summary>
        public bool IsServiceToolAddress { get { return J1939Pgn.IsServiceToolAddress(_sourceAddress); } }

        /// <summary>Arrival time of the first message heard, on the caller's clock.</summary>
        public double FirstSeenMs { get { return _firstSeenMs; } }

        /// <summary>Arrival time of the newest message heard, on the caller's clock.</summary>
        public double LastSeenMs { get { return _lastSeenMs; } }

        /// <summary>Milliseconds between the newest message and the snapshot reference time.</summary>
        public double AgeMs { get { return _ageMs; } }

        /// <summary>Messages heard from this address since the last clear.</summary>
        public long MessageCount { get { return _messageCount; } }

        /// <summary>
        /// Messages per second over the most recently completed one-second window. 0 before
        /// the first window completes and after the node goes quiet.
        /// </summary>
        public double MessagesPerSecond { get { return _messagesPerSecond; } }

        /// <summary>
        /// True when nothing has been heard from this address for longer than
        /// <see cref="J1939NetworkMap.SilentAfterMs"/>. A silent node is UNKNOWN, not gone:
        /// it may have nothing to say, may have been silenced by DM13, or may have lost power.
        /// </summary>
        public bool IsSilent { get { return _isSilent; } }

        /// <summary>Parameter groups heard from this address, ordered by PGN. A private copy.</summary>
        public ReadOnlyCollection<J1939PgnObservation> Pgns { get { return _pgns; } }

        /// <summary>Short label for a node list: the NAME's function/manufacturer when known, else the bare address.</summary>
        public string Label
        {
            get
            {
                if (_name == null)
                {
                    return string.Format(CultureInfo.InvariantCulture, "SA 0x{0:X2} ({0})", _sourceAddress);
                }

                return string.Format(CultureInfo.InvariantCulture,
                    "SA 0x{0:X2} ({0}) Func={1} Mfg={2} Inst={3}",
                    _sourceAddress, _name.Function, _name.ManufacturerCode, _name.EcuInstance);
            }
        }

        /// <summary>One line per node for a bus overview.</summary>
        /// <returns>The formatted node.</returns>
        public override string ToString()
        {
            StringBuilder sb = new StringBuilder(160);
            sb.Append(Label);
            sb.AppendFormat(CultureInfo.InvariantCulture, ": {0} msg, {1:F1}/s, {2} PGN(s)",
                _messageCount, _messagesPerSecond, _pgns.Count);

            if (_isSilent)
            {
                sb.AppendFormat(CultureInfo.InvariantCulture, " - SILENT for {0}", J1939EcuFaultEntry.FormatAge(_ageMs));
            }

            if (_name != null)
            {
                sb.Append(" ").Append(_name.ToString());
            }

            return sb.ToString();
        }
    }

    /// <summary>Immutable snapshot of the whole bus as seen by a <see cref="J1939NetworkMap"/>.</summary>
    public sealed class J1939NetworkSnapshot
    {
        private readonly ReadOnlyCollection<J1939NodeObservation> _nodes;
        private readonly double _referenceMs;
        private readonly long _totalMessages;
        private readonly int _uniquePgnCount;
        private readonly int _silentNodeCount;
        private readonly string _timebaseNote;

        internal J1939NetworkSnapshot(IList<J1939NodeObservation> nodes, double referenceMs, long totalMessages,
            int uniquePgnCount, string timebaseNote)
        {
            _nodes = new ReadOnlyCollection<J1939NodeObservation>(nodes);
            _referenceMs = referenceMs;
            _totalMessages = totalMessages;
            _uniquePgnCount = uniquePgnCount;
            _timebaseNote = timebaseNote;

            for (int i = 0; i < nodes.Count; i++)
            {
                if (nodes[i].IsSilent)
                {
                    _silentNodeCount++;
                }
            }
        }

        /// <summary>Every node heard, ordered by source address. A private copy.</summary>
        public ReadOnlyCollection<J1939NodeObservation> Nodes { get { return _nodes; } }

        /// <summary>Number of source addresses heard.</summary>
        public int NodeCount { get { return _nodes.Count; } }

        /// <summary>Number of nodes currently silent (see <see cref="J1939NodeObservation.IsSilent"/>).</summary>
        public int SilentNodeCount { get { return _silentNodeCount; } }

        /// <summary>Messages observed in total since the last clear.</summary>
        public long TotalMessages { get { return _totalMessages; } }

        /// <summary>Distinct parameter groups observed across all nodes.</summary>
        public int UniquePgnCount { get { return _uniquePgnCount; } }

        /// <summary>The clock value every age in this snapshot was measured against.</summary>
        public double ReferenceMs { get { return _referenceMs; } }

        /// <summary>Description of a detected clock problem, or null when the timebase looked consistent.</summary>
        public string TimebaseNote { get { return _timebaseNote; } }

        /// <summary>Finds a node by address.</summary>
        /// <param name="sourceAddress">Address to look up.</param>
        /// <param name="node">The node, or null.</param>
        /// <returns>True when the address has been heard.</returns>
        public bool TryGetNode(byte sourceAddress, out J1939NodeObservation node)
        {
            for (int i = 0; i < _nodes.Count; i++)
            {
                if (_nodes[i].SourceAddress == sourceAddress)
                {
                    node = _nodes[i];
                    return true;
                }
            }

            node = null;
            return false;
        }

        /// <summary>Headline for the top of a network screen.</summary>
        public string Headline
        {
            get
            {
                if (_nodes.Count == 0)
                {
                    return "No J1939 traffic observed";
                }

                return string.Format(CultureInfo.InvariantCulture,
                    "{0} node(s) heard, {1} distinct PGN(s), {2} message(s){3}",
                    _nodes.Count, _uniquePgnCount, _totalMessages,
                    _silentNodeCount > 0
                        ? string.Format(CultureInfo.InvariantCulture, "; {0} node(s) silent", _silentNodeCount)
                        : string.Empty);
            }
        }

        /// <summary>Headline followed by one line per node.</summary>
        /// <returns>The formatted snapshot.</returns>
        public override string ToString()
        {
            StringBuilder sb = new StringBuilder(256);
            sb.Append(Headline);

            if (!string.IsNullOrEmpty(_timebaseNote))
            {
                sb.Append(Environment.NewLine).Append("DEGRADED: ").Append(_timebaseNote);
            }

            for (int i = 0; i < _nodes.Count; i++)
            {
                sb.Append(Environment.NewLine).Append(_nodes[i].ToString());
            }

            return sb.ToString();
        }
    }

    /// <summary>
    /// Aggregates application-layer J1939 traffic into a per-node, per-PGN picture of
    /// the bus for display. Passive; thread-safe; snapshots are copies.
    /// </summary>
    public sealed class J1939NetworkMap
    {
        /// <summary>
        /// Default age after which a node is reported silent, in milliseconds. Most
        /// J1939 broadcasts repeat at 10 ms to 1 s; a node that has said nothing for
        /// five seconds is either idle by design or gone, and either way its last
        /// values must not be presented as current.
        /// </summary>
        public const double DEFAULT_SILENT_AFTER_MS = 5000.0;

        /// <summary>Rate window length, in milliseconds. One second: rates read as messages per second.</summary>
        private const double RATE_WINDOW_MS = 1000.0;

        /// <summary>Tolerated backwards step before the timebase is called into question.</summary>
        private const double CLOCK_REGRESSION_TOLERANCE_MS = 1000.0;

        private readonly object _sync = new object();
        private readonly Dictionary<byte, NodeRecord> _nodes = new Dictionary<byte, NodeRecord>();

        private double _silentAfterMs = DEFAULT_SILENT_AFTER_MS;
        private long _totalMessages;
        private bool _hasObservation;
        private double _newestObservationMs;
        private bool _hasEvaluated;
        private double _evaluatedNowMs;
        private string _timebaseNote;

        private Func<byte, J1939Name> _nameResolver;
        private Func<uint, string> _pgnNameResolver;

        /// <summary>Creates an empty map.</summary>
        public J1939NetworkMap()
        {
        }

        /// <summary>
        /// Optional NAME lookup, normally wired to <see cref="J1939AddressClaim.TryGetName"/>
        /// through <see cref="J1939Session.TryGetNodeName"/>. Null means NAMEs are not shown;
        /// a NAME is never guessed from an address. Invoked outside the internal lock.
        /// </summary>
        public Func<byte, J1939Name> NameResolver
        {
            get { lock (_sync) { return _nameResolver; } }
            set { lock (_sync) { _nameResolver = value; } }
        }

        /// <summary>
        /// Optional PGN name lookup (a loaded <see cref="J1939SpnCatalog"/>, or
        /// <see cref="J1939Pgn.TryDescribe"/> for protocol groups only). Null means PGNs
        /// are shown by number. Invoked outside the internal lock.
        /// </summary>
        public Func<uint, string> PgnNameResolver
        {
            get { lock (_sync) { return _pgnNameResolver; } }
            set { lock (_sync) { _pgnNameResolver = value; } }
        }

        /// <summary>Age at which a node is reported silent, in milliseconds. Must be positive.</summary>
        /// <exception cref="ArgumentOutOfRangeException">Value is not a finite positive number.</exception>
        public double SilentAfterMs
        {
            get { lock (_sync) { return _silentAfterMs; } }
            set
            {
                if (double.IsNaN(value) || double.IsInfinity(value) || value <= 0.0)
                {
                    throw new ArgumentOutOfRangeException("value", "SilentAfterMs must be a finite value greater than zero.");
                }

                lock (_sync) { _silentAfterMs = value; }
            }
        }

        /// <summary>Number of source addresses heard since the last clear.</summary>
        public int NodeCount
        {
            get { lock (_sync) { return _nodes.Count; } }
        }

        /// <summary>Messages observed since the last clear.</summary>
        public long TotalMessages
        {
            get { lock (_sync) { return _totalMessages; } }
        }

        /// <summary>
        /// Records one application-layer message. Call for single frames and for
        /// reassembled transport messages alike; the transport's own TP.CM / TP.DT
        /// frames should NOT be fed here, because they are plumbing rather than
        /// something a node is "saying".
        /// </summary>
        /// <param name="pgn">Parameter group number.</param>
        /// <param name="sourceAddress">Transmitting address.</param>
        /// <param name="destinationAddress">Destination, or 255 for broadcast.</param>
        /// <param name="payload">Message data. Copied; may be empty but not null.</param>
        /// <param name="receivedAtMs">Arrival time on the caller's clock.</param>
        /// <param name="wasMultiFrame">True when the message was reassembled from a transport session.</param>
        /// <exception cref="ArgumentNullException"><paramref name="payload"/> is null.</exception>
        public void Observe(uint pgn, byte sourceAddress, byte destinationAddress, byte[] payload,
            double receivedAtMs, bool wasMultiFrame)
        {
            if (payload == null)
            {
                throw new ArgumentNullException("payload");
            }

            if (double.IsNaN(receivedAtMs) || double.IsInfinity(receivedAtMs))
            {
                // Cannot be aged, cannot be rated. Counted on the node so the message is
                // not lost from the totals, but no timing is derived from it.
                lock (_sync)
                {
                    NoteTimebaseProblem("a message arrived with a non-finite timestamp; its timing was ignored");
                }

                return;
            }

            byte[] copy = new byte[payload.Length];
            Buffer.BlockCopy(payload, 0, copy, 0, payload.Length);

            lock (_sync)
            {
                if (_hasObservation && receivedAtMs < _newestObservationMs - CLOCK_REGRESSION_TOLERANCE_MS)
                {
                    NoteTimebaseProblem(string.Format(CultureInfo.InvariantCulture,
                        "message timestamps went backwards by {0:F0} ms (adapter counter wrap, or two clocks mixed) - rates and ages may be wrong",
                        _newestObservationMs - receivedAtMs));
                }

                if (!_hasObservation || receivedAtMs > _newestObservationMs)
                {
                    _newestObservationMs = receivedAtMs;
                    _hasObservation = true;
                }

                _totalMessages++;

                NodeRecord node;
                if (!_nodes.TryGetValue(sourceAddress, out node))
                {
                    node = new NodeRecord(sourceAddress, receivedAtMs);
                    _nodes.Add(sourceAddress, node);
                }

                node.Observe(receivedAtMs);

                PgnRecord group;
                if (!node.Pgns.TryGetValue(pgn, out group))
                {
                    group = new PgnRecord(pgn);
                    node.Pgns.Add(pgn, group);
                }

                group.Observe(receivedAtMs, destinationAddress, copy, wasMultiFrame);
            }
        }

        /// <summary>Convenience overload for a single received frame.</summary>
        /// <param name="message">The message. Ignored when null.</param>
        public void Observe(J1939Message message)
        {
            if (message == null)
            {
                return;
            }

            Observe(message.Pgn, message.SourceAddress, message.DestinationAddress, message.Data,
                message.TimestampMs, message.Length > 8);
        }

        /// <summary>
        /// Advances "now" and returns the aged snapshot. Call on a UI timer even when
        /// nothing has arrived - silence is a result, and nothing can go silent until
        /// the clock moves.
        /// </summary>
        /// <param name="nowMs">Current time on the same clock the observations used.</param>
        /// <returns>The snapshot.</returns>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="nowMs"/> is not finite.</exception>
        public J1939NetworkSnapshot Evaluate(double nowMs)
        {
            if (double.IsNaN(nowMs) || double.IsInfinity(nowMs))
            {
                throw new ArgumentOutOfRangeException("nowMs", "nowMs must be a finite millisecond value from the observation clock.");
            }

            lock (_sync)
            {
                if (_hasEvaluated && nowMs < _evaluatedNowMs - CLOCK_REGRESSION_TOLERANCE_MS)
                {
                    NoteTimebaseProblem(string.Format(CultureInfo.InvariantCulture,
                        "the evaluation clock went backwards by {0:F0} ms - ages may be wrong", _evaluatedNowMs - nowMs));
                }

                if (_hasObservation && nowMs < _newestObservationMs - CLOCK_REGRESSION_TOLERANCE_MS)
                {
                    NoteTimebaseProblem(string.Format(CultureInfo.InvariantCulture,
                        "the evaluation clock is {0:F0} ms behind the newest message - pass the same clock to Observe and Evaluate",
                        _newestObservationMs - nowMs));
                }

                if (!_hasEvaluated || nowMs > _evaluatedNowMs)
                {
                    _evaluatedNowMs = nowMs;
                    _hasEvaluated = true;
                }

                // Close out rate windows that have run past their length even though no
                // new message arrived to close them - otherwise a node that stops
                // transmitting keeps reporting its last busy second forever.
                foreach (KeyValuePair<byte, NodeRecord> pair in _nodes)
                {
                    pair.Value.RollWindowIfDue(nowMs);
                }
            }

            return BuildSnapshot();
        }

        /// <summary>Returns the snapshot without advancing the clock.</summary>
        /// <returns>The snapshot.</returns>
        public J1939NetworkSnapshot GetSnapshot()
        {
            return BuildSnapshot();
        }

        /// <summary>Drops every node, every count and the clock. Call on disconnect.</summary>
        public void Clear()
        {
            lock (_sync)
            {
                _nodes.Clear();
                _totalMessages = 0;
                _hasObservation = false;
                _newestObservationMs = 0.0;
                _hasEvaluated = false;
                _evaluatedNowMs = 0.0;
                _timebaseNote = null;
            }
        }

        // -----------------------------------------------------------------
        // Internals
        // -----------------------------------------------------------------

        private J1939NetworkSnapshot BuildSnapshot()
        {
            List<NodeRecord> captures;
            double referenceMs;
            double silentAfterMs;
            long total;
            string timebaseNote;
            Func<byte, J1939Name> nameResolver;
            Func<uint, string> pgnResolver;

            lock (_sync)
            {
                captures = new List<NodeRecord>(_nodes.Count);
                foreach (KeyValuePair<byte, NodeRecord> pair in _nodes)
                {
                    captures.Add(pair.Value.Capture());
                }

                referenceMs = _hasEvaluated && (!_hasObservation || _evaluatedNowMs >= _newestObservationMs)
                    ? _evaluatedNowMs
                    : (_hasObservation ? _newestObservationMs : 0.0);
                silentAfterMs = _silentAfterMs;
                total = _totalMessages;
                timebaseNote = _timebaseNote;
                nameResolver = _nameResolver;
                pgnResolver = _pgnNameResolver;
            }

            captures.Sort(CompareByAddress);

            HashSet<uint> uniquePgns = new HashSet<uint>();
            List<J1939NodeObservation> nodes = new List<J1939NodeObservation>(captures.Count);

            for (int i = 0; i < captures.Count; i++)
            {
                NodeRecord record = captures[i];

                J1939Name name = null;
                if (nameResolver != null)
                {
                    try
                    {
                        J1939Name resolved = nameResolver(record.SourceAddress);
                        if (resolved != null)
                        {
                            name = resolved.Clone();
                        }
                    }
                    catch (Exception)
                    {
                        name = null;   // a throwing resolver degrades to "no NAME known"
                    }
                }

                List<PgnRecord> groups = new List<PgnRecord>(record.Pgns.Values);
                groups.Sort(CompareByPgn);

                List<J1939PgnObservation> pgns = new List<J1939PgnObservation>(groups.Count);
                for (int g = 0; g < groups.Count; g++)
                {
                    PgnRecord group = groups[g];
                    uniquePgns.Add(group.Pgn);

                    string pgnName = null;
                    if (pgnResolver != null)
                    {
                        try { pgnName = pgnResolver(group.Pgn); }
                        catch (Exception) { pgnName = null; }
                    }

                    pgns.Add(new J1939PgnObservation(group.Pgn, pgnName, group.Count, group.LastSeenMs,
                        AgeOf(referenceMs, group.LastSeenMs), group.LastIntervalMs, group.AverageIntervalMs(),
                        group.LastDestination, group.LastPayload, group.LastWasMultiFrame));
                }

                double age = AgeOf(referenceMs, record.LastSeenMs);
                nodes.Add(new J1939NodeObservation(record.SourceAddress, name, record.FirstSeenMs, record.LastSeenMs,
                    age, record.Count, record.LastRate, age > silentAfterMs, pgns));
            }

            return new J1939NetworkSnapshot(nodes, referenceMs, total, uniquePgns.Count, timebaseNote);
        }

        private static double AgeOf(double referenceMs, double atMs)
        {
            double age = referenceMs - atMs;
            return age < 0.0 ? 0.0 : age;
        }

        private static int CompareByAddress(NodeRecord left, NodeRecord right)
        {
            return left.SourceAddress.CompareTo(right.SourceAddress);
        }

        private static int CompareByPgn(PgnRecord left, PgnRecord right)
        {
            return left.Pgn.CompareTo(right.Pgn);
        }

        /// <summary>Caller must hold <see cref="_sync"/>. Keeps the first problem seen.</summary>
        private void NoteTimebaseProblem(string note)
        {
            if (string.IsNullOrEmpty(_timebaseNote))
            {
                _timebaseNote = note;
            }
        }

        /// <summary>Mutable per-node state. Touched only under <see cref="_sync"/>.</summary>
        private sealed class NodeRecord
        {
            internal readonly byte SourceAddress;
            internal readonly Dictionary<uint, PgnRecord> Pgns = new Dictionary<uint, PgnRecord>();
            internal double FirstSeenMs;
            internal double LastSeenMs;
            internal long Count;

            // One-second rate window.
            internal double WindowStartMs;
            internal long WindowCount;
            internal double LastRate;

            internal NodeRecord(byte sourceAddress, double firstSeenMs)
            {
                SourceAddress = sourceAddress;
                FirstSeenMs = firstSeenMs;
                LastSeenMs = firstSeenMs;
                WindowStartMs = firstSeenMs;
            }

            internal void Observe(double atMs)
            {
                Count++;
                if (atMs > LastSeenMs)
                {
                    LastSeenMs = atMs;
                }

                RollWindowIfDue(atMs);
                WindowCount++;
            }

            internal void RollWindowIfDue(double nowMs)
            {
                double elapsed = nowMs - WindowStartMs;
                if (elapsed >= RATE_WINDOW_MS)
                {
                    LastRate = WindowCount * 1000.0 / elapsed;
                    WindowStartMs = nowMs;
                    WindowCount = 0;
                }
            }

            internal NodeRecord Capture()
            {
                NodeRecord copy = new NodeRecord(SourceAddress, FirstSeenMs);
                copy.LastSeenMs = LastSeenMs;
                copy.Count = Count;
                copy.WindowStartMs = WindowStartMs;
                copy.WindowCount = WindowCount;
                copy.LastRate = LastRate;
                foreach (KeyValuePair<uint, PgnRecord> pair in Pgns)
                {
                    copy.Pgns.Add(pair.Key, pair.Value.Capture());
                }

                return copy;
            }
        }

        /// <summary>Mutable per-PGN state. Touched only under <see cref="_sync"/>.</summary>
        private sealed class PgnRecord
        {
            internal readonly uint Pgn;
            internal long Count;
            internal double FirstSeenMs;
            internal double LastSeenMs;
            internal double LastIntervalMs = double.NaN;
            internal byte LastDestination;
            internal byte[] LastPayload = new byte[0];
            internal bool LastWasMultiFrame;

            internal PgnRecord(uint pgn)
            {
                Pgn = pgn;
            }

            internal void Observe(double atMs, byte destination, byte[] payloadCopy, bool wasMultiFrame)
            {
                if (Count == 0)
                {
                    FirstSeenMs = atMs;
                }
                else
                {
                    LastIntervalMs = atMs - LastSeenMs;
                }

                Count++;
                LastSeenMs = atMs;
                LastDestination = destination;
                LastPayload = payloadCopy;
                LastWasMultiFrame = wasMultiFrame;
            }

            internal double AverageIntervalMs()
            {
                if (Count < 2)
                {
                    return double.NaN;
                }

                return (LastSeenMs - FirstSeenMs) / (Count - 1);
            }

            internal PgnRecord Capture()
            {
                PgnRecord copy = new PgnRecord(Pgn);
                copy.Count = Count;
                copy.FirstSeenMs = FirstSeenMs;
                copy.LastSeenMs = LastSeenMs;
                copy.LastIntervalMs = LastIntervalMs;
                copy.LastDestination = LastDestination;
                copy.LastPayload = LastPayload;   // payloads are replaced, never mutated, so sharing is safe
                copy.LastWasMultiFrame = LastWasMultiFrame;
                return copy;
            }
        }
    }
}
