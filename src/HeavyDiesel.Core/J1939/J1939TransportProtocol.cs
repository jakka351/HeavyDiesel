// =============================================================================
//  J1939TransportProtocol.cs - SAE J1939-21 segmented transport (TP and ETP)
//
//  This is the layer that turns 8-byte CAN frames into whole parameter groups.
//  Almost everything a technician sees on a heavy vehicle above eight bytes -
//  DM1 with more than one active DTC, DM2, DM19 calibration IDs, component and
//  software identification, most proprietary data - arrives through here. If
//  this layer drops a packet quietly, the tool shows a SHORT parameter list
//  that looks complete. That is worse than showing nothing, because it sends a
//  technician after the wrong system. Every abandoned session in this file
//  raises SessionFailed with the specific reason. There are no silent drops.
//
//  THREE STATE MACHINES, DELIBERATELY NOT ONE
//  ------------------------------------------
//  BAM (broadcast) and RTS/CTS (connection mode) are not two configurations of
//  one algorithm; they are different protocols that happen to share a frame
//  format. BAM is fire-and-forget with no back-channel: the receiver keeps up
//  or loses the message, and there is no abort frame to send because there is
//  no connection to abort. RTS/CTS is receiver-paced with an explicit window,
//  retry point, acknowledgement and abort. Folding them together produces code
//  that sends a Conn_Abort in response to a broadcast failure (illegal) or
//  that waits for a CTS that will never come. ETP is a third machine again: it
//  adds a data-packet-offset frame so 8-bit sequence numbers can address a
//  payload far beyond 1785 bytes. Each machine below owns its own state enum
//  and its own frame handlers. Only buffer bookkeeping is shared.
//
//  TIMING
//  ------
//  Every interval that CAN be measured from adapter hardware timestamps IS:
//  late-arrival detection runs at frame ingest through
//  J1939Message.ElapsedMsBetween, which is adapter-accurate and wrap-safe.
//  DateTime.Now appears nowhere in this file. The one interval that cannot be
//  measured from message timestamps is the absence of a message, so the owner
//  supplies a monotonic clock through Tick(nowMs) for that watchdog only. Every
//  failure event reports which clock detected it (J1939TimeoutDetection), and
//  any session that saw a frame without a usable adapter timestamp is flagged
//  TimingDegraded all the way out to the caller so the UI can tell the
//  technician the timing is soft.
//
//  On the J1939Pgn timing constants: these are now the published per-timer
//  values from the SAE J1939-21 table - Tr 200, Th 500, T1 750, T2 1250,
//  T3 1250, T4 1050. An earlier revision of the project brief flattened them to
//  "Th 750, T1..T4 1250", which is wrong (750 is T1, not Th); that was corrected
//  in J1939Pgn.cs, and this paragraph with it. Read the values from J1939Pgn -
//  never restate them here, or the two drift again.
//
//  If a future discrepancy is ever genuinely unresolvable, prefer the LONGER
//  value: aborting early loses a transfer that was about to succeed, aborting
//  late only costs time.
//
//  THREADING
//  ---------
//  The provider owns the thread. This class creates none. Session state is
//  guarded by a single lock; outgoing frames and outgoing events are queued
//  inside that lock and flushed outside it, in FIFO order, so a slow or
//  re-entrant handler can never deadlock the transport or reorder the wire.
// =============================================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace HeavyDiesel.J1939
{
    /// <summary>
    /// Sink the transport uses to put one CAN frame on the wire. Supplied by the
    /// provider, which owns the adapter and the thread.
    /// </summary>
    /// <param name="frame">The frame to transmit. Data is always 8 bytes for transport frames.</param>
    /// <returns>
    /// True when the frame was handed to the adapter. False marks the session
    /// failed with <see cref="J1939TransportFault.TransmitFailed"/> rather than
    /// letting it die by timeout, so the technician sees the real cause.
    /// </returns>
    public delegate bool J1939FrameSink(J1939Message frame);

    /// <summary>Which transport mechanism carried, or will carry, a message.</summary>
    public enum J1939TransportKind
    {
        /// <summary>No transport session.</summary>
        None = 0,

        /// <summary>Eight bytes or fewer: sent as one CAN frame, no transport session.</summary>
        SingleFrame = 1,

        /// <summary>Broadcast Announce Message: unacknowledged broadcast, 9..1785 bytes.</summary>
        Bam = 2,

        /// <summary>RTS/CTS connection mode: receiver-paced peer-to-peer, 9..1785 bytes.</summary>
        ConnectionMode = 3,

        /// <summary>Extended transport protocol: receiver-paced peer-to-peer beyond 1785 bytes.</summary>
        ExtendedConnectionMode = 4
    }

    /// <summary>Whether a session is receiving from the bus or transmitting to it.</summary>
    public enum J1939TransportDirection
    {
        /// <summary>Reassembly of a message arriving from another node.</summary>
        Inbound = 0,

        /// <summary>Segmentation of a message this tool is sending.</summary>
        Outbound = 1
    }

    /// <summary>
    /// Connection abort reason codes carried in byte 1 of a TP.Conn_Abort or
    /// ETP.Conn_Abort frame, per the SAE J1939-21 abort reason table.
    /// </summary>
    public enum J1939ConnectionAbortReason : byte
    {
        /// <summary>No abort code applies (used for failures that are not connection aborts, such as BAM loss).</summary>
        None = 0,

        /// <summary>1 - already in one or more connection-managed sessions and cannot support another.</summary>
        AlreadyInSession = 1,

        /// <summary>2 - system resources were needed for another task, so this session was terminated.</summary>
        SystemResourcesReclaimed = 2,

        /// <summary>3 - a timeout occurred and this is the abort that closes the session.</summary>
        Timeout = 3,

        /// <summary>4 - CTS received while data transfer was already in progress.</summary>
        UnexpectedCtsDuringTransfer = 4,

        /// <summary>5 - maximum retransmit request limit reached.</summary>
        MaximumRetransmitLimitReached = 5,

        /// <summary>6 - unexpected data transfer packet.</summary>
        UnexpectedDataTransferPacket = 6,

        /// <summary>7 - bad sequence number, and the receiver cannot recover.</summary>
        BadSequenceNumber = 7,

        /// <summary>8 - duplicate sequence number, and the receiver cannot recover.</summary>
        DuplicateSequenceNumber = 8,

        /// <summary>9 - total message size is greater than 1785 bytes (standard TP only).</summary>
        TotalMessageSizeTooLarge = 9,

        /// <summary>250 - reason not covered by the published table.</summary>
        Unspecified = 250
    }

    /// <summary>
    /// Why a transport session ended without delivering a complete message.
    /// Reported on every abandoned session; there is no silent-drop path.
    /// </summary>
    public enum J1939TransportFault
    {
        /// <summary>No fault.</summary>
        None = 0,

        /// <summary>Tr (200 ms) response time exceeded.</summary>
        TimeoutTr = 1,

        /// <summary>Th (500 ms) hold time exceeded while a hold-connection CTS was in force
        /// (see J1939Pgn.HoldTimeThMs).</summary>
        TimeoutTh = 2,

        /// <summary>T1 expired between consecutive data packets (see J1939Pgn.ConnectionTimeoutT1Ms).</summary>
        TimeoutT1 = 3,

        /// <summary>T2 expired at the receiver waiting for the first data packet after CTS (see J1939Pgn.ConnectionTimeoutT2Ms).</summary>
        TimeoutT2 = 4,

        /// <summary>T3 expired at the sender waiting for CTS or EndOfMsgACK (see J1939Pgn.ConnectionTimeoutT3Ms).</summary>
        TimeoutT3 = 5,

        /// <summary>T4 (1050 ms) expired while a hold-connection CTS was in force
        /// (see J1939Pgn.ConnectionTimeoutT4Ms).</summary>
        TimeoutT4 = 6,

        /// <summary>A sequence number arrived that was neither the expected one nor a duplicate: packets were lost.</summary>
        SequenceGap = 7,

        /// <summary>A sequence number was repeated carrying DIFFERENT data. The two copies cannot both be right, so nothing is delivered.</summary>
        DuplicateSequenceConflict = 8,

        /// <summary>A data transfer frame arrived with no announced session, or in a state that does not accept data.</summary>
        UnexpectedDataPacket = 9,

        /// <summary>The peer sent a connection abort. See the abort reason on the event.</summary>
        AbortReceived = 10,

        /// <summary>This tool sent a connection abort. See the abort reason on the event.</summary>
        AbortSent = 11,

        /// <summary>The peer opened a new session over the top of this one. Per J1939-21 the older session is abandoned.</summary>
        SupersededByNewSession = 12,

        /// <summary>A control frame was the wrong length, carried an unknown control byte, or an unencodable PGN.</summary>
        MalformedControlFrame = 13,

        /// <summary>The announced message size is outside the range the mechanism can carry.</summary>
        DeclaredSizeOutOfRange = 14,

        /// <summary>The announced packet count does not match the announced byte count.</summary>
        DeclaredPacketCountMismatch = 15,

        /// <summary>The announced size is legal but larger than this stack is configured to buffer.</summary>
        SizeExceedsConfiguredLimit = 16,

        /// <summary>The concurrent session limit was reached, so this session was refused rather than starved.</summary>
        TooManyConcurrentSessions = 17,

        /// <summary>The frame sink refused or failed to transmit a frame.</summary>
        TransmitFailed = 18,

        /// <summary>The owning application cancelled the session.</summary>
        LocallyCancelled = 19,

        /// <summary>The transport was reset (channel closed, adapter disconnected) while the session was live.</summary>
        StackReset = 20,

        /// <summary>The peer's flow control was self-contradictory: a CTS asking for packets outside the announced message.</summary>
        InvalidFlowControl = 21
    }

    /// <summary>Which clock observed a timeout.</summary>
    public enum J1939TimeoutDetection
    {
        /// <summary>The failure was not a timeout.</summary>
        NotApplicable = 0,

        /// <summary>A frame arrived late, measured between adapter hardware timestamps. Wire-accurate.</summary>
        AdapterTimestamp = 1,

        /// <summary>No frame arrived at all; the host tick watchdog expired. Includes driver and scheduling latency.</summary>
        HostTick = 2
    }

    /// <summary>A completed transport message, inbound or outbound.</summary>
    public sealed class J1939TransportMessageEventArgs : EventArgs
    {
        private readonly J1939TransportDirection _direction;
        private readonly J1939TransportKind _kind;
        private readonly J1939Message _message;
        private readonly byte _sourceAddress;
        private readonly byte _destinationAddress;
        private readonly uint _declaredPgn;
        private readonly int _packetCount;
        private readonly double _durationMs;
        private readonly bool _timingDegraded;
        private readonly string _notes;

        /// <summary>Creates the event payload.</summary>
        /// <param name="direction">Inbound reassembly or outbound segmentation.</param>
        /// <param name="kind">Mechanism that carried the message.</param>
        /// <param name="message">The whole message: identifier plus complete payload.</param>
        /// <param name="sourceAddress">Transmitting node address.</param>
        /// <param name="destinationAddress">Receiving node address, 255 for broadcast.</param>
        /// <param name="declaredPgn">PGN exactly as it appeared in the announcing control frame, before normalisation.</param>
        /// <param name="packetCount">Number of data transfer frames the message occupied.</param>
        /// <param name="durationMs">Milliseconds from announcement to completion.</param>
        /// <param name="timingDegraded">True when at least one frame lacked a usable adapter timestamp.</param>
        /// <param name="notes">Tolerated anomalies observed during the session. Empty when the session was clean.</param>
        public J1939TransportMessageEventArgs(
            J1939TransportDirection direction,
            J1939TransportKind kind,
            J1939Message message,
            byte sourceAddress,
            byte destinationAddress,
            uint declaredPgn,
            int packetCount,
            double durationMs,
            bool timingDegraded,
            string notes)
        {
            if (message == null) throw new ArgumentNullException("message");

            _direction = direction;
            _kind = kind;
            _message = message;
            _sourceAddress = sourceAddress;
            _destinationAddress = destinationAddress;
            _declaredPgn = declaredPgn;
            _packetCount = packetCount;
            _durationMs = durationMs;
            _timingDegraded = timingDegraded;
            _notes = notes ?? string.Empty;
        }

        /// <summary>Inbound reassembly or outbound segmentation.</summary>
        public J1939TransportDirection Direction { get { return _direction; } }

        /// <summary>Mechanism that carried the message.</summary>
        public J1939TransportKind Kind { get { return _kind; } }

        /// <summary>
        /// The complete message. Its identifier carries the transported PGN, not the
        /// TP.CM / TP.DT identifier the frames arrived under.
        /// </summary>
        public J1939Message Message { get { return _message; } }

        /// <summary>Complete reassembled payload. Same array as <c>Message.Data</c>; treat as read-only.</summary>
        public byte[] Payload { get { return _message.Data; } }

        /// <summary>Transported parameter group number, with PDU1 normalisation applied.</summary>
        public uint Pgn { get { return _message.Pgn; } }

        /// <summary>
        /// PGN exactly as the announcing control frame carried it. Differs from
        /// <see cref="Pgn"/> only when a PDU1 group arrived with a non-zero low byte,
        /// which some ECUs emit by putting the destination address there.
        /// </summary>
        public uint DeclaredPgn { get { return _declaredPgn; } }

        /// <summary>Transmitting node address.</summary>
        public byte SourceAddress { get { return _sourceAddress; } }

        /// <summary>Receiving node address; 255 for a broadcast session.</summary>
        public byte DestinationAddress { get { return _destinationAddress; } }

        /// <summary>Number of data transfer frames the message occupied.</summary>
        public int PacketCount { get { return _packetCount; } }

        /// <summary>Milliseconds from the announcing frame to completion.</summary>
        public double DurationMs { get { return _durationMs; } }

        /// <summary>
        /// True when at least one frame in the session lacked a usable adapter
        /// timestamp, so the timing figures include host scheduling latency. Surface
        /// this to the technician rather than presenting soft timing as measured.
        /// </summary>
        public bool TimingDegraded { get { return _timingDegraded; } }

        /// <summary>
        /// Anomalies that were tolerated rather than fatal: duplicate frames absorbed,
        /// a PDU1 PGN low byte masked, a peer that answered slower than Tr. Empty when
        /// the session was clean. Never discard this - it is the difference between
        /// "the data is good" and "the data is good but the ECU is limping".
        /// </summary>
        public string Notes { get { return _notes; } }

        /// <summary>Single-line log form.</summary>
        /// <returns>The formatted summary.</returns>
        public override string ToString()
        {
            StringBuilder sb = new StringBuilder(160);
            sb.AppendFormat(CultureInfo.InvariantCulture,
                "{0} {1} {2} SA 0x{3:X2} DA 0x{4:X2} {5} bytes in {6} packets, {7:F1} ms",
                _direction, _kind, J1939Pgn.Describe(_message.Pgn),
                _sourceAddress, _destinationAddress, _message.Length, _packetCount, _durationMs);

            if (_timingDegraded)
            {
                sb.Append(" (host clock - timing soft)");
            }

            if (_notes.Length != 0)
            {
                sb.Append(" [").Append(_notes).Append("]");
            }

            return sb.ToString();
        }
    }

    /// <summary>
    /// A transport session that ended without delivering a complete message, with
    /// everything needed to explain the failure to a technician.
    /// </summary>
    public sealed class J1939TransportFaultEventArgs : EventArgs
    {
        private readonly J1939TransportDirection _direction;
        private readonly J1939TransportKind _kind;
        private readonly J1939TransportFault _fault;
        private readonly J1939ConnectionAbortReason _abortReason;
        private readonly J1939TimeoutDetection _detection;
        private readonly uint _pgn;
        private readonly byte _sourceAddress;
        private readonly byte _destinationAddress;
        private readonly int _bytesTransferred;
        private readonly int _totalBytes;
        private readonly int _packetsTransferred;
        private readonly int _totalPackets;
        private readonly int _expectedSequence;
        private readonly int _observedSequence;
        private readonly double _elapsedMs;
        private readonly int _limitMs;
        private readonly bool _timingDegraded;
        private readonly byte[] _partialPayload;
        private readonly string _description;

        /// <summary>Creates the event payload.</summary>
        /// <param name="direction">Inbound reassembly or outbound segmentation.</param>
        /// <param name="kind">Mechanism the session was using.</param>
        /// <param name="fault">Why the session ended.</param>
        /// <param name="abortReason">Abort code sent or received, or <see cref="J1939ConnectionAbortReason.None"/>.</param>
        /// <param name="detection">Which clock observed the timeout, when the fault is a timeout.</param>
        /// <param name="pgn">Transported parameter group number, 0 when it was never established.</param>
        /// <param name="sourceAddress">Transmitting node address.</param>
        /// <param name="destinationAddress">Receiving node address, 255 for broadcast.</param>
        /// <param name="bytesTransferred">Payload bytes successfully transferred before the failure.</param>
        /// <param name="totalBytes">Announced total payload size, 0 when it was never established.</param>
        /// <param name="packetsTransferred">Data transfer frames successfully handled.</param>
        /// <param name="totalPackets">Announced total packet count.</param>
        /// <param name="expectedSequence">Sequence number the session was expecting, -1 when not applicable.</param>
        /// <param name="observedSequence">Sequence number actually seen, -1 when not applicable.</param>
        /// <param name="elapsedMs">Measured interval that broke a timer, 0 when not a timeout.</param>
        /// <param name="limitMs">Timer limit that was exceeded, 0 when not a timeout.</param>
        /// <param name="timingDegraded">True when at least one frame lacked a usable adapter timestamp.</param>
        /// <param name="partialPayload">Bytes received so far. Never present this as a result; it is forensic only.</param>
        /// <param name="description">Plain-language explanation suitable for a technician-facing log.</param>
        public J1939TransportFaultEventArgs(
            J1939TransportDirection direction,
            J1939TransportKind kind,
            J1939TransportFault fault,
            J1939ConnectionAbortReason abortReason,
            J1939TimeoutDetection detection,
            uint pgn,
            byte sourceAddress,
            byte destinationAddress,
            int bytesTransferred,
            int totalBytes,
            int packetsTransferred,
            int totalPackets,
            int expectedSequence,
            int observedSequence,
            double elapsedMs,
            int limitMs,
            bool timingDegraded,
            byte[] partialPayload,
            string description)
        {
            _direction = direction;
            _kind = kind;
            _fault = fault;
            _abortReason = abortReason;
            _detection = detection;
            _pgn = pgn;
            _sourceAddress = sourceAddress;
            _destinationAddress = destinationAddress;
            _bytesTransferred = bytesTransferred;
            _totalBytes = totalBytes;
            _packetsTransferred = packetsTransferred;
            _totalPackets = totalPackets;
            _expectedSequence = expectedSequence;
            _observedSequence = observedSequence;
            _elapsedMs = elapsedMs;
            _limitMs = limitMs;
            _timingDegraded = timingDegraded;
            _partialPayload = partialPayload;
            _description = description ?? string.Empty;
        }

        /// <summary>Inbound reassembly or outbound segmentation.</summary>
        public J1939TransportDirection Direction { get { return _direction; } }

        /// <summary>Mechanism the session was using.</summary>
        public J1939TransportKind Kind { get { return _kind; } }

        /// <summary>Why the session ended.</summary>
        public J1939TransportFault Fault { get { return _fault; } }

        /// <summary>Abort code sent or received. <see cref="J1939ConnectionAbortReason.None"/> for BAM, which has no abort frame.</summary>
        public J1939ConnectionAbortReason AbortReason { get { return _abortReason; } }

        /// <summary>Which clock observed the timeout.</summary>
        public J1939TimeoutDetection Detection { get { return _detection; } }

        /// <summary>Transported parameter group number; 0 when the session failed before one was established.</summary>
        public uint Pgn { get { return _pgn; } }

        /// <summary>Transmitting node address.</summary>
        public byte SourceAddress { get { return _sourceAddress; } }

        /// <summary>Receiving node address; 255 for broadcast.</summary>
        public byte DestinationAddress { get { return _destinationAddress; } }

        /// <summary>Payload bytes successfully transferred before the failure.</summary>
        public int BytesTransferred { get { return _bytesTransferred; } }

        /// <summary>Announced total payload size.</summary>
        public int TotalBytes { get { return _totalBytes; } }

        /// <summary>Data transfer frames successfully handled.</summary>
        public int PacketsTransferred { get { return _packetsTransferred; } }

        /// <summary>Announced total packet count.</summary>
        public int TotalPackets { get { return _totalPackets; } }

        /// <summary>Sequence number the session expected next, or -1.</summary>
        public int ExpectedSequence { get { return _expectedSequence; } }

        /// <summary>Sequence number actually observed, or -1.</summary>
        public int ObservedSequence { get { return _observedSequence; } }

        /// <summary>Measured interval that broke a timer, in milliseconds.</summary>
        public double ElapsedMs { get { return _elapsedMs; } }

        /// <summary>Timer limit that was exceeded, in milliseconds.</summary>
        public int LimitMs { get { return _limitMs; } }

        /// <summary>True when at least one frame lacked a usable adapter timestamp.</summary>
        public bool TimingDegraded { get { return _timingDegraded; } }

        /// <summary>
        /// Bytes received before the failure, or null. FORENSIC ONLY. This is an
        /// incomplete parameter group; decoding it produces plausible wrong answers.
        /// </summary>
        public byte[] PartialPayload { get { return _partialPayload; } }

        /// <summary>Plain-language explanation suitable for a technician-facing log.</summary>
        public string Description { get { return _description; } }

        /// <summary>Single-line log form.</summary>
        /// <returns>The formatted summary.</returns>
        public override string ToString()
        {
            return _description;
        }
    }

    /// <summary>Result of asking the transport to send a message.</summary>
    public sealed class J1939SendTicket
    {
        private readonly bool _accepted;
        private readonly long _ticketId;
        private readonly J1939TransportKind _kind;
        private readonly string _rejectReason;

        internal J1939SendTicket(bool accepted, long ticketId, J1939TransportKind kind, string rejectReason)
        {
            _accepted = accepted;
            _ticketId = ticketId;
            _kind = kind;
            _rejectReason = rejectReason ?? string.Empty;
        }

        /// <summary>True when the transport took ownership of the message.</summary>
        public bool Accepted { get { return _accepted; } }

        /// <summary>Identifier for <see cref="J1939TransportProtocol.CancelSend"/>. Zero when rejected.</summary>
        public long TicketId { get { return _ticketId; } }

        /// <summary>Mechanism chosen for the message.</summary>
        public J1939TransportKind Kind { get { return _kind; } }

        /// <summary>Why the message was refused. Empty when accepted.</summary>
        public string RejectReason { get { return _rejectReason; } }

        /// <summary>Single-line log form.</summary>
        /// <returns>The formatted result.</returns>
        public override string ToString()
        {
            return _accepted
                ? string.Format(CultureInfo.InvariantCulture, "accepted ticket {0} as {1}", _ticketId, _kind)
                : string.Format(CultureInfo.InvariantCulture, "rejected: {0}", _rejectReason);
        }
    }

    /// <summary>
    /// SAE J1939-21 segmented transport: BAM, RTS/CTS connection mode and ETP,
    /// receive and transmit.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This class creates no threads. The provider owns a dedicated transport
    /// thread and pumps this object from it:
    /// </para>
    /// <code>
    /// while (running)
    /// {
    ///     J1939Message frame;
    ///     while (rxQueue.TryDequeue(out frame)) transport.OnMessageReceived(frame);
    ///     transport.Tick(stopwatch.ElapsedMilliseconds);
    ///     Thread.Sleep(5);
    /// }
    /// </code>
    /// <para>
    /// Drain received frames BEFORE calling <see cref="Tick"/> so a frame already in
    /// the queue cannot lose a race with its own timeout. Call <see cref="Tick"/> at
    /// least every 10 ms: it drives both the timeout watchdogs and the 50 ms
    /// inter-packet transmit pacing.
    /// </para>
    /// <para>
    /// Events are raised on the pumping thread, outside the internal lock. Handlers
    /// must marshal to the UI themselves and must not block: a slow handler delays
    /// the transport and can break connection-mode timing.
    /// </para>
    /// </remarks>
    public sealed class J1939TransportProtocol
    {
        private const int DefaultReceiveWindowPackets = 16;
        private const int DefaultMaxInboundMessageBytes = 1024 * 1024;
        private const int DefaultMaxConcurrentInboundSessions = 16;
        private const int DefaultMaxQueuedOutboundMessages = 32;
        private const byte DefaultTransportPriority = 7;
        private const int MaxNotesPerSession = 16;

        // How many times a receiver re-grants a burst before it gives up on a lost
        // data packet. J1939-21 recovery for a gap is a fresh CTS pinned at the first
        // missing packet, not an abort, and on a loaded bus one packet lost to an
        // adapter receive-queue overrun is routine. Bounded so a sender that keeps
        // losing the same packet cannot hold the connection open indefinitely; past
        // this count the session ends with abort reason 5.
        private const int MAX_SEQUENCE_GAP_RECOVERIES = 3;

        // Session state. Everything below is guarded by _gate.
        private readonly object _gate = new object();

        // Frame emission and event dispatch are queued under _gate and drained under
        // _flushGate, never while holding _gate. The queues are FIFO, so wire order
        // and event order are preserved even if two threads pump concurrently.
        private readonly object _flushGate = new object();
        private readonly Queue<EventArgs> _notifications = new Queue<EventArgs>();

        private readonly byte _sourceAddress;
        private readonly J1939FrameSink _frameSink;

        // BAM reassembly is keyed by SOURCE ADDRESS alone: a BAM is broadcast, it has
        // no destination, and J1939-21 permits one in flight per source.
        private readonly Dictionary<byte, BamInboundSession> _bamInbound =
            new Dictionary<byte, BamInboundSession>();

        // Connection-mode reassembly (TP and ETP) is keyed by the SOURCE/DESTINATION
        // PAIR. A node may run one connection with us and another with a third node
        // at the same time; those are different sessions and must not collide.
        private readonly Dictionary<int, InboundSession> _pairInbound =
            new Dictionary<int, InboundSession>();

        // We transmit from exactly one source address, so at most one outgoing BAM.
        private BamOutboundSession _bamOutbound;

        // At most one outgoing connection per destination, TP or ETP.
        private readonly Dictionary<byte, OutboundSession> _pairOutbound =
            new Dictionary<byte, OutboundSession>();

        private readonly Queue<PendingSend> _pendingSends = new Queue<PendingSend>();

        private byte _transportPriority = DefaultTransportPriority;
        private int _receiveWindowPackets = DefaultReceiveWindowPackets;
        private int _maxInboundMessageBytes = DefaultMaxInboundMessageBytes;
        private int _maxConcurrentInboundSessions = DefaultMaxConcurrentInboundSessions;
        private int _maxQueuedOutboundMessages = DefaultMaxQueuedOutboundMessages;
        private int _interPacketDelayMs = J1939Pgn.InterPacketDelayMs;
        private bool _reassembleForeignSessions;

        private double _hostNowMs;
        private long _tickOrigin;
        private bool _tickStarted;

        // Owned monotonic clock. _hostNowMs used to advance ONLY inside Tick, which made
        // the watchdog measure pump-loop latency rather than message absence: if the pump
        // thread stalled longer than T1/T2/T3 (gen-2 GC, USB bulk-transfer stall, BLE
        // reconnect, laptop resume), every live session was torn down on the next Tick
        // even though the adapter had buffered every frame on time. That is precisely the
        // "intermittent, hard to reproduce" transport failure this layer exists to avoid.
        // Reading this clock at frame ingest stamps true arrival time, so a late drain no
        // longer looks like a dead bus. Genuine sender stalls are still caught, and caught
        // more precisely, by the adapter-timestamp gap check in MeasureGap.
        private readonly Stopwatch _clock = Stopwatch.StartNew();

        /// <summary>Fresh reading of the owned monotonic clock, on the same rebased
        /// timebase as <c>_hostNowMs</c>.</summary>
        private double HostNow()
        {
            return _clock.Elapsed.TotalMilliseconds;
        }
        private long _nextTicketId = 1;
        private bool _timingDegraded;

        /// <summary>Creates a transport bound to one source address and one frame sink.</summary>
        /// <param name="sourceAddress">
        /// The address this tool has claimed, normally
        /// <see cref="J1939Pgn.ServiceTool1Address"/> (249). Inbound peer-to-peer
        /// sessions addressed here are answered; sessions between other nodes are
        /// ignored unless <see cref="ReassembleForeignSessions"/> is set.
        /// </param>
        /// <param name="frameSink">Callback that puts one frame on the wire.</param>
        /// <exception cref="ArgumentNullException"><paramref name="frameSink"/> is null.</exception>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="sourceAddress"/> is the global address (255), which cannot transmit.</exception>
        public J1939TransportProtocol(byte sourceAddress, J1939FrameSink frameSink)
        {
            if (frameSink == null)
            {
                throw new ArgumentNullException("frameSink",
                    "The transport does not own the adapter; the provider must supply a frame sink.");
            }

            if (sourceAddress == J1939Pgn.GlobalAddress)
            {
                throw new ArgumentOutOfRangeException("sourceAddress", sourceAddress,
                    "The global address (255) is a destination only and cannot be used as a source address.");
            }

            _sourceAddress = sourceAddress;
            _frameSink = frameSink;
        }

        /// <summary>Raised when a complete message has been reassembled from the bus.</summary>
        public event EventHandler<J1939TransportMessageEventArgs> MessageReassembled;

        /// <summary>Raised when a message this tool sent has been fully transferred and, for connection mode, acknowledged.</summary>
        public event EventHandler<J1939TransportMessageEventArgs> MessageSent;

        /// <summary>
        /// Raised for every session that ends without delivering a complete message,
        /// in either direction, with the specific reason. Subscribe to this: an
        /// unsubscribed failure is an invisible short parameter list.
        /// </summary>
        public event EventHandler<J1939TransportFaultEventArgs> SessionFailed;

        /// <summary>The address this transport transmits from.</summary>
        public byte SourceAddress { get { return _sourceAddress; } }

        /// <summary>
        /// Priority placed on TP.CM / TP.DT / ETP frames. Default 7 (lowest), which is
        /// conventional for transport traffic so it yields to control messages.
        /// </summary>
        /// <exception cref="ArgumentOutOfRangeException">Value above 7.</exception>
        public byte TransportPriority
        {
            get { lock (_gate) { return _transportPriority; } }
            set
            {
                if (value > J1939Id.MAX_PRIORITY)
                {
                    throw new ArgumentOutOfRangeException("value", value, "J1939 priority is a 3-bit field (0..7).");
                }

                lock (_gate) { _transportPriority = value; }
            }
        }

        /// <summary>
        /// Packets granted per CTS when this tool is the receiver. Default 16. Larger
        /// values reduce round trips; smaller values suit a loaded bus or an adapter
        /// with a shallow receive queue.
        /// </summary>
        /// <exception cref="ArgumentOutOfRangeException">Value outside 1..255.</exception>
        public int ReceiveWindowPackets
        {
            get { lock (_gate) { return _receiveWindowPackets; } }
            set
            {
                if (value < 1 || value > J1939Pgn.MaxTransportProtocolPackets)
                {
                    throw new ArgumentOutOfRangeException("value", value, "CTS packet count must be 1..255.");
                }

                lock (_gate) { _receiveWindowPackets = value; }
            }
        }

        /// <summary>
        /// Largest inbound message this transport will buffer. Default 1 MB.
        /// </summary>
        /// <remarks>
        /// ETP announces a 32-bit size, so a garbled or hostile frame can ask for a
        /// 117 MB allocation. Nothing is allocated until the announced size passes
        /// this limit, so an attacker cannot drive allocation. A session refused here
        /// fails with <see cref="J1939TransportFault.SizeExceedsConfiguredLimit"/> and
        /// is aborted with reason 2, not dropped.
        /// </remarks>
        /// <exception cref="ArgumentOutOfRangeException">Value below 1785 or above the ETP maximum.</exception>
        public int MaxInboundMessageBytes
        {
            get { lock (_gate) { return _maxInboundMessageBytes; } }
            set
            {
                if (value < J1939Pgn.MaxTransportProtocolPayloadBytes ||
                    value > J1939Pgn.MaxExtendedTransportProtocolPayloadBytes)
                {
                    throw new ArgumentOutOfRangeException("value", value,
                        "Inbound message limit must be at least 1785 bytes (standard TP maximum) and no more than the ETP maximum.");
                }

                lock (_gate) { _maxInboundMessageBytes = value; }
            }
        }

        /// <summary>
        /// Concurrent inbound sessions accepted before further announcements are
        /// refused. Default 16. On a busy truck bus every ECU may BAM a DM1 at once.
        /// </summary>
        /// <exception cref="ArgumentOutOfRangeException">Value below 1.</exception>
        public int MaxConcurrentInboundSessions
        {
            get { lock (_gate) { return _maxConcurrentInboundSessions; } }
            set
            {
                if (value < 1)
                {
                    throw new ArgumentOutOfRangeException("value", value, "At least one inbound session must be permitted.");
                }

                lock (_gate) { _maxConcurrentInboundSessions = value; }
            }
        }

        /// <summary>Outgoing messages that may wait behind an in-flight session. Default 32.</summary>
        /// <exception cref="ArgumentOutOfRangeException">Value below 0.</exception>
        public int MaxQueuedOutboundMessages
        {
            get { lock (_gate) { return _maxQueuedOutboundMessages; } }
            set
            {
                if (value < 0)
                {
                    throw new ArgumentOutOfRangeException("value", value, "Queue depth cannot be negative.");
                }

                lock (_gate) { _maxQueuedOutboundMessages = value; }
            }
        }

        /// <summary>
        /// Minimum spacing between transmitted data frames, milliseconds. Default 50,
        /// the J1939-21 value.
        /// </summary>
        /// <remarks>
        /// Lowering this is NOT standard-conformant. Some ECUs tolerate it and some
        /// drop packets; on a mixed bus it also starves lower-priority traffic. Change
        /// it only against a specific measured requirement.
        /// </remarks>
        /// <exception cref="ArgumentOutOfRangeException">Value below 0 or above 1000.</exception>
        public int InterPacketDelayMs
        {
            get { lock (_gate) { return _interPacketDelayMs; } }
            set
            {
                if (value < 0 || value > 1000)
                {
                    throw new ArgumentOutOfRangeException("value", value, "Inter-packet delay must be 0..1000 ms.");
                }

                lock (_gate) { _interPacketDelayMs = value; }
            }
        }

        /// <summary>
        /// When true, peer-to-peer sessions between two OTHER nodes are reassembled and
        /// reported (bus monitoring). Default false.
        /// </summary>
        /// <remarks>
        /// A monitored session is passive: no CTS and no EndOfMsgACK is transmitted,
        /// because the real receiver is doing that. Reassembly therefore follows the
        /// real receiver's pacing and reports whatever it observes.
        /// </remarks>
        public bool ReassembleForeignSessions
        {
            get { lock (_gate) { return _reassembleForeignSessions; } }
            set { lock (_gate) { _reassembleForeignSessions = value; } }
        }

        /// <summary>
        /// True once any frame has been seen without a usable adapter timestamp.
        /// Surface this to the technician: transport timing is being measured against
        /// the host clock and includes Windows scheduling and USB latency.
        /// </summary>
        public bool TimingDegraded { get { lock (_gate) { return _timingDegraded; } } }

        /// <summary>Inbound sessions currently reassembling.</summary>
        public int ActiveInboundSessionCount
        {
            get { lock (_gate) { return _bamInbound.Count + _pairInbound.Count; } }
        }

        /// <summary>Outbound sessions currently transmitting, plus messages queued behind them.</summary>
        public int ActiveOutboundSessionCount
        {
            get
            {
                lock (_gate)
                {
                    return (_bamOutbound != null ? 1 : 0) + _pairOutbound.Count + _pendingSends.Count;
                }
            }
        }

        /// <summary>
        /// Feeds one received frame to the transport.
        /// </summary>
        /// <param name="message">The received frame. Must not be null.</param>
        /// <returns>
        /// True when the frame was a transport frame (TP.CM, TP.DT, ETP.CM or ETP.DT)
        /// and was consumed here. False for everything else, so the caller can route
        /// the frame on to the application layer.
        /// </returns>
        /// <exception cref="ArgumentNullException"><paramref name="message"/> is null.</exception>
        public bool OnMessageReceived(J1939Message message)
        {
            if (message == null) throw new ArgumentNullException("message");

            bool consumed;

            lock (_gate)
            {
                if (message.TimestampSource != J1939TimestampSource.Adapter)
                {
                    _timingDegraded = true;
                }

                switch (message.Pgn)
                {
                    case J1939Pgn.TransportProtocolConnectionManagement:
                        HandleTpCm(message);
                        consumed = true;
                        break;

                    case J1939Pgn.TransportProtocolDataTransfer:
                        HandleTpDt(message);
                        consumed = true;
                        break;

                    case J1939Pgn.ExtendedTransportProtocolConnectionManagement:
                        HandleEtpCm(message);
                        consumed = true;
                        break;

                    case J1939Pgn.ExtendedTransportProtocolDataTransfer:
                        HandleEtpDt(message);
                        consumed = true;
                        break;

                    default:
                        consumed = false;
                        break;
                }
            }

            Flush();
            return consumed;
        }

        /// <summary>
        /// Drives timeout detection and transmit pacing. Call at least every 10 ms from
        /// the transport thread, after draining received frames.
        /// </summary>
        /// <param name="nowMs">
        /// A monotonic millisecond counter, for example
        /// <c>Stopwatch.ElapsedMilliseconds</c>. Must never go backwards; a value
        /// lower than the previous one is clamped rather than rewinding the timers.
        /// Never pass a wall-clock reading: an NTP step would abort live sessions.
        /// </param>
        public void Tick(long nowMs)
        {
            lock (_gate)
            {
                // nowMs is retained for API compatibility and for the monotonicity
                // contract, but the timers run off the owned clock so that they keep
                // advancing between ticks and cannot be distorted by an irregular pump
                // cadence. A caller-supplied value that goes backwards is still ignored.
                if (!_tickStarted)
                {
                    _tickOrigin = nowMs;
                    _tickStarted = true;
                }

                double candidate = HostNow();
                if (candidate > _hostNowMs)
                {
                    _hostNowMs = candidate;
                }

                ScanInboundTimeouts();
                PumpOutbound();
                PromotePendingSends();
            }

            Flush();
        }

        /// <summary>
        /// Segments and sends a message, choosing single frame, BAM, RTS/CTS or ETP
        /// from the payload size and destination.
        /// </summary>
        /// <param name="priority">Priority for the single-frame case, 0..7. Transport frames use <see cref="TransportPriority"/>.</param>
        /// <param name="pgn">Parameter group number to transport, 0..0x3FFFF.</param>
        /// <param name="destinationAddress">Destination node, or <see cref="J1939Pgn.GlobalAddress"/> (255) to broadcast.</param>
        /// <param name="payload">Message payload. Copied; the caller keeps ownership.</param>
        /// <returns>
        /// A ticket. Check <see cref="J1939SendTicket.Accepted"/>: a rejected ticket
        /// carries the reason and raises no events. An accepted ticket completes with
        /// <see cref="MessageSent"/> or fails with <see cref="SessionFailed"/>.
        /// </returns>
        /// <exception cref="ArgumentNullException"><paramref name="payload"/> is null.</exception>
        /// <exception cref="ArgumentOutOfRangeException">Priority or PGN out of range.</exception>
        public J1939SendTicket BeginSend(byte priority, uint pgn, byte destinationAddress, byte[] payload)
        {
            if (payload == null) throw new ArgumentNullException("payload");

            if (priority > J1939Id.MAX_PRIORITY)
            {
                throw new ArgumentOutOfRangeException("priority", priority, "J1939 priority is a 3-bit field (0..7).");
            }

            if (pgn > J1939Id.MAX_PGN)
            {
                throw new ArgumentOutOfRangeException("pgn", pgn, "J1939 PGN is an 18-bit field (0..0x3FFFF).");
            }

            J1939SendTicket ticket;

            lock (_gate)
            {
                ticket = BeginSendLocked(priority, pgn, destinationAddress, payload);
            }

            Flush();
            return ticket;
        }

        /// <summary>
        /// Cancels a queued or in-flight outgoing message. A connection-mode session
        /// that has already opened is aborted on the wire so the peer is not left
        /// waiting; a BAM simply stops, because BAM has no abort frame.
        /// </summary>
        /// <param name="ticketId">Identifier from <see cref="J1939SendTicket.TicketId"/>.</param>
        /// <returns>True when a matching message was found and cancelled.</returns>
        public bool CancelSend(long ticketId)
        {
            bool cancelled = false;

            lock (_gate)
            {
                int queued = _pendingSends.Count;
                for (int i = 0; i < queued; i++)
                {
                    PendingSend pending = _pendingSends.Dequeue();
                    if (!cancelled && pending.TicketId == ticketId)
                    {
                        cancelled = true;
                        EnqueueNotification(new J1939TransportFaultEventArgs(
                            J1939TransportDirection.Outbound, pending.Kind,
                            J1939TransportFault.LocallyCancelled, J1939ConnectionAbortReason.None,
                            J1939TimeoutDetection.NotApplicable, pending.Pgn, _sourceAddress,
                            pending.DestinationAddress, 0, pending.Payload.Length, 0, 0, -1, -1, 0.0, 0,
                            _timingDegraded, null,
                            string.Format(CultureInfo.InvariantCulture,
                                "Queued outgoing {0} for {1} to 0x{2:X2} cancelled before transmission started.",
                                pending.Kind, J1939Pgn.Describe(pending.Pgn), pending.DestinationAddress)));
                        continue;
                    }

                    _pendingSends.Enqueue(pending);
                }

                if (!cancelled && _bamOutbound != null && _bamOutbound.TicketId == ticketId)
                {
                    _bamOutbound.CancelLocally();
                    _bamOutbound = null;
                    cancelled = true;
                }

                if (!cancelled)
                {
                    byte match = 0;
                    bool found = false;
                    foreach (KeyValuePair<byte, OutboundSession> entry in _pairOutbound)
                    {
                        if (entry.Value.TicketId == ticketId)
                        {
                            match = entry.Key;
                            found = true;
                            break;
                        }
                    }

                    if (found)
                    {
                        _pairOutbound[match].CancelLocally();
                        _pairOutbound.Remove(match);
                        cancelled = true;
                    }
                }
            }

            Flush();
            return cancelled;
        }

        /// <summary>
        /// Abandons every session in both directions, raising
        /// <see cref="J1939TransportFault.StackReset"/> for each. Call when the channel
        /// closes or the adapter disconnects: leaving sessions alive across a
        /// reconnect merges data from two different bus states into one message.
        /// </summary>
        public void Reset()
        {
            lock (_gate)
            {
                foreach (KeyValuePair<byte, BamInboundSession> entry in _bamInbound)
                {
                    entry.Value.FailOnReset();
                }

                _bamInbound.Clear();

                foreach (KeyValuePair<int, InboundSession> entry in _pairInbound)
                {
                    entry.Value.FailOnReset();
                }

                _pairInbound.Clear();

                if (_bamOutbound != null)
                {
                    _bamOutbound.FailOnReset();
                    _bamOutbound = null;
                }

                foreach (KeyValuePair<byte, OutboundSession> entry in _pairOutbound)
                {
                    entry.Value.FailOnReset();
                }

                _pairOutbound.Clear();

                while (_pendingSends.Count > 0)
                {
                    PendingSend pending = _pendingSends.Dequeue();
                    EnqueueNotification(new J1939TransportFaultEventArgs(
                        J1939TransportDirection.Outbound, pending.Kind,
                        J1939TransportFault.StackReset, J1939ConnectionAbortReason.None,
                        J1939TimeoutDetection.NotApplicable, pending.Pgn, _sourceAddress,
                        pending.DestinationAddress, 0, pending.Payload.Length, 0, 0, -1, -1, 0.0, 0,
                        _timingDegraded, null,
                        string.Format(CultureInfo.InvariantCulture,
                            "Transport reset discarded a queued outgoing {0} for {1} to 0x{2:X2}.",
                            pending.Kind, J1939Pgn.Describe(pending.Pgn), pending.DestinationAddress)));
                }

                // Frames for sessions that no longer exist must not reach the wire.
                _outgoing.Clear();
                _orphanStreams.Clear();
                _timingDegraded = false;
            }

            Flush();
        }

        /// <summary>
        /// Multi-line snapshot of every live session, for a technician-facing log or a
        /// support dump. Shows what the transport is waiting on and how far each
        /// transfer has progressed.
        /// </summary>
        /// <returns>One line per session, or a single line when idle.</returns>
        public string DescribeActiveSessions()
        {
            StringBuilder sb = new StringBuilder(256);

            lock (_gate)
            {
                foreach (KeyValuePair<byte, BamInboundSession> entry in _bamInbound)
                {
                    sb.AppendLine(entry.Value.Describe(_hostNowMs));
                }

                foreach (KeyValuePair<int, InboundSession> entry in _pairInbound)
                {
                    sb.AppendLine(entry.Value.Describe(_hostNowMs));
                }

                if (_bamOutbound != null)
                {
                    sb.AppendLine(_bamOutbound.Describe(_hostNowMs));
                }

                foreach (KeyValuePair<byte, OutboundSession> entry in _pairOutbound)
                {
                    sb.AppendLine(entry.Value.Describe(_hostNowMs));
                }

                if (_pendingSends.Count > 0)
                {
                    sb.AppendFormat(CultureInfo.InvariantCulture,
                        "{0} outgoing message(s) queued behind an in-flight session.", _pendingSends.Count);
                    sb.AppendLine();
                }
            }

            if (sb.Length == 0)
            {
                return "No transport sessions active.";
            }

            return sb.ToString().TrimEnd();
        }

        // =====================================================================
        //  Frame routing
        // =====================================================================

        // ETP control bytes. J1939Pgn carries the standard TP set; the extended set
        // is local to this file because the foundation types deliberately publish
        // only values the project spec listed.
        private const byte EtpCmRts = 0x14;
        private const byte EtpCmCts = 0x15;
        private const byte EtpCmDpo = 0x16;
        private const byte EtpCmEndOfMsgAck = 0x17;

        // Orphan bookkeeping, one entry per stream: BAM source, or connection-mode
        // pair. Reporting is rate-limited because connecting to a bus in the middle of
        // a 255-packet transfer would otherwise produce 255 identical failures and
        // drown the real ones. Rate-limited, NOT latched off: the failure mode that
        // produces orphans is a lost announcing frame, so a permanent latch silences
        // the tool exactly when the peer most needs telling to restart.
        private sealed class OrphanStream
        {
            public double LastReportMs;
            public double LastAbortMs;
            public int SuppressedSinceReport;
            public bool EverReported;
            public bool EverAborted;
        }

        // One recovery abort per Tr window per stream. Tr is the J1939-21 responder
        // obligation, so a peer that is listening has had its chance to act within it;
        // anything faster only floods a loaded bus with aborts.
        private const int ORPHAN_ABORT_REARM_MS = J1939Pgn.ResponseTimeTrMs;

        // One rolled-up report per T1 window per stream. Past T1 any session that
        // could have owned these packets is dead by definition, so the next report is
        // new information rather than a repeat of the last one.
        private const int ORPHAN_REPORT_REARM_MS = J1939Pgn.ConnectionTimeoutT1Ms;

        private readonly Dictionary<int, OrphanStream> _orphanStreams =
            new Dictionary<int, OrphanStream>();

        private static int PairKey(byte sourceAddress, byte destinationAddress)
        {
            return (sourceAddress << 8) | destinationAddress;
        }

        private static int BamOrphanKey(byte sourceAddress)
        {
            return 0x10000 | sourceAddress;
        }

        private bool IsAddressedToUs(J1939Message message)
        {
            return message.DestinationAddress == _sourceAddress;
        }

        private bool ShouldTrack(J1939Message message)
        {
            return IsAddressedToUs(message)
                || message.DestinationAddress == J1939Pgn.GlobalAddress
                || _reassembleForeignSessions;
        }

        private void HandleTpCm(J1939Message message)
        {
            if (message.Length < 8)
            {
                ReportMalformed(message, J1939TransportKind.ConnectionMode,
                    "TP.CM frame shorter than 8 bytes");
                return;
            }

            byte control = message.Data[0];

            switch (control)
            {
                case J1939Pgn.TpCmBam:
                    HandleBamAnnounce(message);
                    break;

                case J1939Pgn.TpCmRts:
                    HandleRtsAnnounce(message);
                    break;

                case J1939Pgn.TpCmCts:
                case J1939Pgn.TpCmEndOfMsgAck:
                    RouteToOutbound(message, control, false);
                    break;

                case J1939Pgn.TpCmAbort:
                    RouteAbort(message, J1939TransportKind.ConnectionMode);
                    break;

                default:
                    ReportMalformed(message, J1939TransportKind.ConnectionMode,
                        string.Format(CultureInfo.InvariantCulture,
                            "unknown TP.CM control byte 0x{0:X2}", control));
                    break;
            }
        }

        private void HandleEtpCm(J1939Message message)
        {
            if (message.Length < 8)
            {
                ReportMalformed(message, J1939TransportKind.ExtendedConnectionMode,
                    "ETP.CM frame shorter than 8 bytes");
                return;
            }

            byte control = message.Data[0];

            switch (control)
            {
                case EtpCmRts:
                    HandleEtpRtsAnnounce(message);
                    break;

                case EtpCmDpo:
                    {
                        InboundSession session;
                        if (_pairInbound.TryGetValue(PairKey(message.SourceAddress, message.DestinationAddress), out session))
                        {
                            EtpInboundSession etp = session as EtpInboundSession;
                            if (etp != null)
                            {
                                etp.HandleDataPacketOffset(message);
                                RemoveIfFinished(etp);
                                break;
                            }
                        }

                        ReportMalformed(message, J1939TransportKind.ExtendedConnectionMode,
                            "ETP.CM_DPO with no matching extended transport session");
                        break;
                    }

                case EtpCmCts:
                case EtpCmEndOfMsgAck:
                    RouteToOutbound(message, control, true);
                    break;

                case J1939Pgn.TpCmAbort:
                    RouteAbort(message, J1939TransportKind.ExtendedConnectionMode);
                    break;

                default:
                    ReportMalformed(message, J1939TransportKind.ExtendedConnectionMode,
                        string.Format(CultureInfo.InvariantCulture,
                            "unknown ETP.CM control byte 0x{0:X2}", control));
                    break;
            }
        }

        private void HandleTpDt(J1939Message message)
        {
            if (message.Length < 8)
            {
                ReportMalformed(message, J1939TransportKind.ConnectionMode,
                    "TP.DT frame shorter than 8 bytes");
                return;
            }

            if (message.DestinationAddress == J1939Pgn.GlobalAddress)
            {
                BamInboundSession bam;
                if (_bamInbound.TryGetValue(message.SourceAddress, out bam))
                {
                    bam.HandleDataTransfer(message);
                    if (bam.Finished)
                    {
                        _bamInbound.Remove(message.SourceAddress);
                    }

                    return;
                }

                ReportOrphanDataPacket(message, BamOrphanKey(message.SourceAddress),
                    J1939TransportKind.Bam, false);
                return;
            }

            int key = PairKey(message.SourceAddress, message.DestinationAddress);

            InboundSession pair;
            if (_pairInbound.TryGetValue(key, out pair))
            {
                ConnectionInboundSession connection = pair as ConnectionInboundSession;
                if (connection != null)
                {
                    connection.HandleDataTransfer(message);
                    RemoveIfFinished(connection);
                    return;
                }

                // An ETP session is open on this pair but a standard TP.DT arrived.
                pair.FailUnexpectedDataPacket(message);
                RemoveIfFinished(pair);
                return;
            }

            ReportOrphanDataPacket(message, key, J1939TransportKind.ConnectionMode, IsAddressedToUs(message));
        }

        private void HandleEtpDt(J1939Message message)
        {
            if (message.Length < 8)
            {
                ReportMalformed(message, J1939TransportKind.ExtendedConnectionMode,
                    "ETP.DT frame shorter than 8 bytes");
                return;
            }

            int key = PairKey(message.SourceAddress, message.DestinationAddress);

            InboundSession pair;
            if (_pairInbound.TryGetValue(key, out pair))
            {
                EtpInboundSession etp = pair as EtpInboundSession;
                if (etp != null)
                {
                    etp.HandleDataTransfer(message);
                    RemoveIfFinished(etp);
                    return;
                }

                pair.FailUnexpectedDataPacket(message);
                RemoveIfFinished(pair);
                return;
            }

            ReportOrphanDataPacket(message, key, J1939TransportKind.ExtendedConnectionMode, IsAddressedToUs(message));
        }

        private void HandleBamAnnounce(J1939Message message)
        {
            byte[] data = message.Data;
            int size = data[1] | (data[2] << 8);
            int packets = data[3];
            uint declaredPgn = (uint)(data[5] | (data[6] << 8) | (data[7] << 16));

            if (!ShouldTrack(message))
            {
                return;
            }

            uint pgn;
            bool masked;
            J1939TransportFault announcementFault;
            string rejection = ValidateAnnouncement(declaredPgn, size, packets,
                J1939Pgn.MaxTransportProtocolPayloadBytes, out pgn, out masked, out announcementFault);

            if (rejection != null)
            {
                // Validation runs BEFORE any existing session is touched. A garbled
                // frame that happens to look like a BAM must not be able to destroy a
                // healthy transfer that is already half reassembled.
                //
                // BAM has no back-channel either: there is nothing legal to transmit
                // here, so the only correct action is to tell the operator.
                EnqueueNotification(new J1939TransportFaultEventArgs(
                    J1939TransportDirection.Inbound, J1939TransportKind.Bam,
                    announcementFault, J1939ConnectionAbortReason.None,
                    J1939TimeoutDetection.NotApplicable, pgn, message.SourceAddress,
                    message.DestinationAddress, 0, size, 0, packets, -1, -1, 0.0, 0,
                    message.TimestampSource != J1939TimestampSource.Adapter, null,
                    string.Format(CultureInfo.InvariantCulture,
                        "Rejected BAM from 0x{0:X2} for {1}: {2}. Nothing was reassembled.",
                        message.SourceAddress, J1939Pgn.Describe(pgn), rejection)));
                return;
            }

            // The announcement is sound, so a BAM already in flight from this source is
            // abandoned, per J1939-21: a source may hold only one broadcast session open
            // at a time and the new announcement is the authoritative one.
            BamInboundSession existing;
            if (_bamInbound.TryGetValue(message.SourceAddress, out existing))
            {
                existing.FailSuperseded(declaredPgn);
                _bamInbound.Remove(message.SourceAddress);
            }

            if (_bamInbound.Count + _pairInbound.Count >= _maxConcurrentInboundSessions)
            {
                EnqueueNotification(new J1939TransportFaultEventArgs(
                    J1939TransportDirection.Inbound, J1939TransportKind.Bam,
                    J1939TransportFault.TooManyConcurrentSessions, J1939ConnectionAbortReason.None,
                    J1939TimeoutDetection.NotApplicable, pgn, message.SourceAddress,
                    message.DestinationAddress, 0, size, 0, packets, -1, -1, 0.0, 0,
                    _timingDegraded, null,
                    string.Format(CultureInfo.InvariantCulture,
                        "Refused BAM from 0x{0:X2} for {1}: {2} sessions already active (limit {3}). Raise MaxConcurrentInboundSessions or reduce bus load.",
                        message.SourceAddress, J1939Pgn.Describe(pgn),
                        _bamInbound.Count + _pairInbound.Count, _maxConcurrentInboundSessions)));
                return;
            }

            BamInboundSession session = new BamInboundSession(this, message, pgn, declaredPgn, size, packets);
            if (masked)
            {
                session.AddNote("PDU1 PGN arrived with a non-zero low byte; low byte masked to zero");
            }

            if (message.DestinationAddress != J1939Pgn.GlobalAddress)
            {
                session.AddNote(string.Format(CultureInfo.InvariantCulture,
                    "BAM was directed at 0x{0:X2} instead of the global address", message.DestinationAddress));
            }

            _bamInbound[message.SourceAddress] = session;
            _orphanStreams.Remove(BamOrphanKey(message.SourceAddress));
        }

        private void HandleRtsAnnounce(J1939Message message)
        {
            byte[] data = message.Data;
            int size = data[1] | (data[2] << 8);
            int packets = data[3];
            byte maxPacketsPerCts = data[4];
            uint declaredPgn = (uint)(data[5] | (data[6] << 8) | (data[7] << 16));

            if (!ShouldTrack(message) || message.DestinationAddress == J1939Pgn.GlobalAddress)
            {
                // RTS to the global address is not a legal connection request.
                if (message.DestinationAddress == J1939Pgn.GlobalAddress)
                {
                    ReportMalformed(message, J1939TransportKind.ConnectionMode,
                        "TP.CM_RTS addressed to the global address; connection mode is peer-to-peer only");
                }

                return;
            }

            int key = PairKey(message.SourceAddress, message.DestinationAddress);

            uint pgn;
            bool masked;
            J1939TransportFault announcementFault;
            string rejection = ValidateAnnouncement(declaredPgn, size, packets,
                J1939Pgn.MaxTransportProtocolPayloadBytes, out pgn, out masked, out announcementFault);

            if (rejection != null)
            {
                J1939ConnectionAbortReason reason = size > J1939Pgn.MaxTransportProtocolPayloadBytes
                    ? J1939ConnectionAbortReason.TotalMessageSizeTooLarge
                    : J1939ConnectionAbortReason.Unspecified;

                // Refused before any existing session is disturbed, for the same reason
                // as BAM: a malformed frame must not be able to kill a live transfer.
                RefuseConnection(message, J1939TransportKind.ConnectionMode, pgn, size, packets,
                    announcementFault, reason, rejection, false);
                return;
            }

            InboundSession existing;
            if (_pairInbound.TryGetValue(key, out existing))
            {
                // A peer whose CTS we lost will retry the RTS. Abandoning the stale
                // session and accepting the retry is what unwedges a real transfer;
                // refusing it leaves the pair dead until something power-cycles.
                existing.FailSuperseded(declaredPgn);
                _pairInbound.Remove(key);
            }

            if (_bamInbound.Count + _pairInbound.Count >= _maxConcurrentInboundSessions)
            {
                RefuseConnection(message, J1939TransportKind.ConnectionMode, pgn, size, packets,
                    J1939TransportFault.TooManyConcurrentSessions,
                    J1939ConnectionAbortReason.AlreadyInSession,
                    string.Format(CultureInfo.InvariantCulture,
                        "{0} sessions already active (limit {1})",
                        _bamInbound.Count + _pairInbound.Count, _maxConcurrentInboundSessions), false);
                return;
            }

            ConnectionInboundSession session = new ConnectionInboundSession(
                this, message, pgn, declaredPgn, size, packets, maxPacketsPerCts);

            if (masked)
            {
                session.AddNote("PDU1 PGN arrived with a non-zero low byte; low byte masked to zero");
            }

            _pairInbound[key] = session;
            _orphanStreams.Remove(key);
            session.Start();
            RemoveIfFinished(session);
        }

        private void HandleEtpRtsAnnounce(J1939Message message)
        {
            byte[] data = message.Data;
            long size = (long)data[1] | ((long)data[2] << 8) | ((long)data[3] << 16) | ((long)data[4] << 24);
            uint declaredPgn = (uint)(data[5] | (data[6] << 8) | (data[7] << 16));

            if (!ShouldTrack(message) || message.DestinationAddress == J1939Pgn.GlobalAddress)
            {
                if (message.DestinationAddress == J1939Pgn.GlobalAddress)
                {
                    ReportMalformed(message, J1939TransportKind.ExtendedConnectionMode,
                        "ETP.CM_RTS addressed to the global address; ETP has no broadcast form");
                }

                return;
            }

            int key = PairKey(message.SourceAddress, message.DestinationAddress);

            InboundSession existing;
            bool supersedes = _pairInbound.TryGetValue(key, out existing);

            // A retry on a pair that already has a session does not consume a new slot.
            int activeSessions = _bamInbound.Count + _pairInbound.Count - (supersedes ? 1 : 0);

            uint pgn;
            bool masked;
            bool pgnValid = TryNormalisePgn(declaredPgn, out pgn, out masked);

            // ORDER MATTERS: the size limit is checked BEFORE anything is allocated.
            // ETP announces 32 bits of size, so a garbled or hostile RTS can ask for
            // 117 MB. Nothing here allocates from an unvalidated field.
            string rejection = null;
            J1939ConnectionAbortReason abortReason = J1939ConnectionAbortReason.Unspecified;
            J1939TransportFault fault = J1939TransportFault.MalformedControlFrame;

            if (!pgnValid)
            {
                rejection = string.Format(CultureInfo.InvariantCulture,
                    "announced PGN 0x{0:X6} is not encodable in 18 bits", declaredPgn);
            }
            else if (size < 9 || size > J1939Pgn.MaxExtendedTransportProtocolPayloadBytes)
            {
                rejection = string.Format(CultureInfo.InvariantCulture,
                    "announced size {0} bytes is outside the ETP range 9..{1}",
                    size, J1939Pgn.MaxExtendedTransportProtocolPayloadBytes);
                fault = J1939TransportFault.DeclaredSizeOutOfRange;
            }
            else if (size > _maxInboundMessageBytes)
            {
                rejection = string.Format(CultureInfo.InvariantCulture,
                    "announced size {0} bytes exceeds the configured buffer limit of {1} bytes",
                    size, _maxInboundMessageBytes);
                fault = J1939TransportFault.SizeExceedsConfiguredLimit;
                abortReason = J1939ConnectionAbortReason.SystemResourcesReclaimed;
            }
            else if (activeSessions >= _maxConcurrentInboundSessions)
            {
                rejection = string.Format(CultureInfo.InvariantCulture,
                    "{0} sessions already active (limit {1})", activeSessions, _maxConcurrentInboundSessions);
                fault = J1939TransportFault.TooManyConcurrentSessions;
                abortReason = J1939ConnectionAbortReason.AlreadyInSession;
            }

            if (rejection != null)
            {
                // Refused before the existing session on this pair is touched.
                int reportedSize = size > int.MaxValue ? int.MaxValue : (int)size;
                RefuseConnection(message, J1939TransportKind.ExtendedConnectionMode, pgn, reportedSize, 0,
                    fault, abortReason, rejection, true);
                return;
            }

            if (supersedes)
            {
                existing.FailSuperseded(declaredPgn);
                _pairInbound.Remove(key);
            }

            int totalBytes = (int)size;
            int totalPackets = (totalBytes + J1939Pgn.TransportProtocolBytesPerPacket - 1)
                               / J1939Pgn.TransportProtocolBytesPerPacket;

            EtpInboundSession session = new EtpInboundSession(
                this, message, pgn, declaredPgn, totalBytes, totalPackets);

            if (masked)
            {
                session.AddNote("PDU1 PGN arrived with a non-zero low byte; low byte masked to zero");
            }

            if (totalBytes <= J1939Pgn.MaxTransportProtocolPayloadBytes)
            {
                session.AddNote(string.Format(CultureInfo.InvariantCulture,
                    "ETP used for {0} bytes, which standard TP could have carried", totalBytes));
            }

            _pairInbound[key] = session;
            _orphanStreams.Remove(key);
            session.Start();
            RemoveIfFinished(session);
        }

        private void RouteToOutbound(J1939Message message, byte control, bool extended)
        {
            if (!IsAddressedToUs(message))
            {
                // Flow control between two other nodes. Not ours to act on.
                return;
            }

            OutboundSession session;
            if (!_pairOutbound.TryGetValue(message.SourceAddress, out session))
            {
                ReportMalformed(message,
                    extended ? J1939TransportKind.ExtendedConnectionMode : J1939TransportKind.ConnectionMode,
                    string.Format(CultureInfo.InvariantCulture,
                        "flow control 0x{0:X2} from 0x{1:X2} with no outgoing session to that node",
                        control, message.SourceAddress));
                return;
            }

            session.HandleFlowControl(message, control, extended);

            if (session.Finished)
            {
                _pairOutbound.Remove(message.SourceAddress);
            }
        }

        private void RouteAbort(J1939Message message, J1939TransportKind kind)
        {
            byte[] data = message.Data;
            J1939ConnectionAbortReason reason = (J1939ConnectionAbortReason)data[1];
            uint declaredPgn = (uint)(data[5] | (data[6] << 8) | (data[7] << 16));

            // Usability travels separately instead of collapsing a failed decode to
            // PGN 0: zero is a legitimate PGN, so folding "unencodable" into it lets a
            // garbled abort field match a live session by accident.
            uint pgn;
            bool masked;
            bool pgnUsable = TryNormalisePgn(declaredPgn, out pgn, out masked);

            string pgnText = pgnUsable
                ? J1939Pgn.Describe(pgn)
                : string.Format(CultureInfo.InvariantCulture,
                    "an unencodable PGN field of 0x{0:X6}", declaredPgn);

            int inboundKey = PairKey(message.SourceAddress, message.DestinationAddress);

            InboundSession inbound;
            bool haveInbound = _pairInbound.TryGetValue(inboundKey, out inbound);

            OutboundSession outbound = null;
            if (IsAddressedToUs(message))
            {
                _pairOutbound.TryGetValue(message.SourceAddress, out outbound);
            }

            // An abort frame from A to B could belong to the A-to-B transfer or to the
            // B-to-A transfer. The abort carries the PGN of the message being aborted,
            // so an unambiguous match on that field settles it outright.
            bool inboundMatchesPgn = pgnUsable && haveInbound && inbound.Pgn == pgn;
            bool outboundMatchesPgn = pgnUsable && outbound != null && outbound.Pgn == pgn;

            if (inboundMatchesPgn && !outboundMatchesPgn)
            {
                inbound.FailAbortReceived(reason);
                _pairInbound.Remove(inboundKey);
                return;
            }

            if (outboundMatchesPgn && !inboundMatchesPgn)
            {
                outbound.FailAbortReceived(reason);
                _pairOutbound.Remove(message.SourceAddress);
                return;
            }

            if (!haveInbound && outbound == null)
            {
                EnqueueNotification(new J1939TransportFaultEventArgs(
                    J1939TransportDirection.Inbound, kind, J1939TransportFault.AbortReceived,
                    reason, J1939TimeoutDetection.NotApplicable, pgn,
                    message.SourceAddress, message.DestinationAddress, 0, 0, 0, 0, -1, -1, 0.0, 0,
                    _timingDegraded, null,
                    string.Format(CultureInfo.InvariantCulture,
                        "Connection abort from 0x{0:X2} to 0x{1:X2} for {2} (reason {3} = {4}) with no matching session. The announcing frame was probably missed.",
                        message.SourceAddress, message.DestinationAddress, pgnText,
                        (byte)reason, reason)));
                return;
            }

            // The PGN settled nothing: the field was unusable (padding it with 0xFF is
            // common), or it matched both directions, or it matched neither. Defaulting
            // to the inbound session here tears down a healthy reassembly while the
            // outbound transfer the abort was aimed at is left to die on T3. Charge it
            // instead to the direction that is waiting on the peer - a transfer stalled
            // on a CTS or an EndOfMsgACK is a far more likely abort target than a
            // reassembly we are actively clearing to send for - and name both
            // candidates in the report so the guess is visible.
            bool chargeOutbound = outbound != null && (!haveInbound || outbound.AwaitingPeerResponse);

            string ambiguity = string.Format(CultureInfo.InvariantCulture,
                "connection abort from 0x{0:X2} (reason {1} = {2}) carried {3}, which identified no single session; open candidates were {4}; charged to the {5} transfer",
                message.SourceAddress, (byte)reason, reason, pgnText,
                DescribeAbortCandidates(inbound, outbound),
                chargeOutbound ? "outbound" : "inbound");

            if (chargeOutbound)
            {
                outbound.AddNote(ambiguity);
                outbound.FailAbortReceived(reason);
                _pairOutbound.Remove(message.SourceAddress);
                return;
            }

            inbound.AddNote(ambiguity);
            inbound.FailAbortReceived(reason);
            _pairInbound.Remove(inboundKey);
        }

        // Names every session the abort could have been aimed at, so the fault report
        // says what was open at the time rather than only what was torn down.
        private static string DescribeAbortCandidates(InboundSession inbound, OutboundSession outbound)
        {
            if (inbound != null && outbound != null)
            {
                return string.Format(CultureInfo.InvariantCulture,
                    "inbound reassembly of {0} from 0x{1:X2} and outbound transfer of {2} to 0x{3:X2}",
                    J1939Pgn.Describe(inbound.Pgn), inbound.SourceAddress,
                    J1939Pgn.Describe(outbound.Pgn), outbound.DestinationAddress);
            }

            if (inbound != null)
            {
                return string.Format(CultureInfo.InvariantCulture,
                    "inbound reassembly of {0} from 0x{1:X2} only",
                    J1939Pgn.Describe(inbound.Pgn), inbound.SourceAddress);
            }

            return string.Format(CultureInfo.InvariantCulture,
                "outbound transfer of {0} to 0x{1:X2} only",
                J1939Pgn.Describe(outbound.Pgn), outbound.DestinationAddress);
        }

        // =====================================================================
        //  Announcement validation. Nothing is allocated until this passes.
        // =====================================================================

        private static bool TryNormalisePgn(uint declared, out uint pgn, out bool lowByteMasked)
        {
            lowByteMasked = false;
            pgn = 0;

            if (declared > J1939Id.MAX_PGN)
            {
                return false;
            }

            byte pduFormat = (byte)((declared >> 8) & 0xFFu);
            if (pduFormat <= J1939Id.PDU1_PF_MAXIMUM && (declared & 0xFFu) != 0)
            {
                // PDU1 groups carry no PS bits in the PGN. Some ECUs put the
                // destination address in this byte. Mask it, but say so.
                pgn = declared & 0xFFFFFF00u;
                lowByteMasked = true;
                return true;
            }

            pgn = declared;
            return true;
        }

        // Returns null when the announcement is usable, otherwise the reason text and
        // the matching fault code. The fault is returned directly rather than being
        // inferred from the text later: classifying a failure by string matching is
        // how a diagnostic tool ends up reporting the wrong cause.
        private static string ValidateAnnouncement(uint declaredPgn, int size, int packets,
            int maxBytes, out uint pgn, out bool lowByteMasked, out J1939TransportFault fault)
        {
            fault = J1939TransportFault.MalformedControlFrame;

            if (!TryNormalisePgn(declaredPgn, out pgn, out lowByteMasked))
            {
                return string.Format(CultureInfo.InvariantCulture,
                    "announced PGN 0x{0:X6} is not encodable in 18 bits", declaredPgn);
            }

            if (size < 9 || size > maxBytes)
            {
                fault = J1939TransportFault.DeclaredSizeOutOfRange;
                return string.Format(CultureInfo.InvariantCulture,
                    "announced size {0} bytes is outside the range 9..{1} that this mechanism can carry",
                    size, maxBytes);
            }

            if (packets < 1 || packets > J1939Pgn.MaxTransportProtocolPackets)
            {
                fault = J1939TransportFault.DeclaredPacketCountMismatch;
                return string.Format(CultureInfo.InvariantCulture,
                    "announced packet count {0} is outside 1..255", packets);
            }

            int expected = (size + J1939Pgn.TransportProtocolBytesPerPacket - 1)
                           / J1939Pgn.TransportProtocolBytesPerPacket;

            if (packets != expected)
            {
                fault = J1939TransportFault.DeclaredPacketCountMismatch;
                return string.Format(CultureInfo.InvariantCulture,
                    "announced {0} packets for {1} bytes, which needs {2}", packets, size, expected);
            }

            return null;
        }

        // =====================================================================
        //  Refusals and orphan reporting
        // =====================================================================

        private void RefuseConnection(J1939Message message, J1939TransportKind kind, uint pgn,
            int size, int packets, J1939TransportFault fault, J1939ConnectionAbortReason reason,
            string rejection, bool extended)
        {
            if (IsAddressedToUs(message) && reason != J1939ConnectionAbortReason.None)
            {
                EnqueueFrame(BuildAbort(message.SourceAddress, pgn, reason, extended), 0);
            }

            EnqueueNotification(new J1939TransportFaultEventArgs(
                J1939TransportDirection.Inbound, kind, fault, reason,
                J1939TimeoutDetection.NotApplicable, pgn, message.SourceAddress,
                message.DestinationAddress, 0, size, 0, packets, -1, -1, 0.0, 0,
                message.TimestampSource != J1939TimestampSource.Adapter, null,
                string.Format(CultureInfo.InvariantCulture,
                    "Refused connection request from 0x{0:X2} for {1}: {2}. Aborted with reason {3} = {4}.",
                    message.SourceAddress, J1939Pgn.Describe(pgn), rejection, (byte)reason, reason)));
        }

        private void ReportOrphanDataPacket(J1939Message message, int key, J1939TransportKind kind, bool abortPeer)
        {
            if (!ShouldTrack(message))
            {
                return;
            }

            double now = HostNow();

            OrphanStream stream;
            if (!_orphanStreams.TryGetValue(key, out stream))
            {
                stream = new OrphanStream();
                _orphanStreams[key] = stream;
            }

            // Peer recovery is deliberately NOT gated on the reporting latch. When the
            // frame that keeps getting lost is the announcing RTS, the peer will retry
            // its data transfer forever and only an abort tells it to start over; a
            // one-shot latch would make the tool go silent and leave the pair wedged.
            // The Tr window is the only limit, and it exists to bound bus load, not to
            // stop the recovery.
            bool sendAbort = abortPeer
                             && (!stream.EverAborted || now - stream.LastAbortMs >= ORPHAN_ABORT_REARM_MS);

            if (sendAbort)
            {
                stream.EverAborted = true;
                stream.LastAbortMs = now;

                EnqueueFrame(BuildAbort(message.SourceAddress, 0,
                    J1939ConnectionAbortReason.UnexpectedDataTransferPacket,
                    kind == J1939TransportKind.ExtendedConnectionMode), 0);
            }

            if (stream.EverReported && now - stream.LastReportMs < ORPHAN_REPORT_REARM_MS)
            {
                // Counted, not dropped: the next report carries the total.
                stream.SuppressedSinceReport++;
                return;
            }

            int rolledUp = stream.SuppressedSinceReport;
            bool firstReport = !stream.EverReported;

            stream.SuppressedSinceReport = 0;
            stream.EverReported = true;
            stream.LastReportMs = now;

            string abortText = sendAbort
                ? " A connection abort was sent so the node can restart."
                : string.Empty;

            string description = firstReport
                ? string.Format(CultureInfo.InvariantCulture,
                    "Data transfer packet (sequence {0}) from 0x{1:X2} to 0x{2:X2} with no open session.{3} Further packets from this stream are counted and rolled up rather than reported one by one. Usual cause: the tool connected part-way through a transfer, or the announcing frame was lost.",
                    message.Data[0], message.SourceAddress, message.DestinationAddress, abortText)
                : string.Format(CultureInfo.InvariantCulture,
                    "{0} further orphan packet(s) from 0x{1:X2} to 0x{2:X2} with no open session (latest sequence {3}).{4}",
                    rolledUp, message.SourceAddress, message.DestinationAddress,
                    message.Data[0], abortText);

            EnqueueNotification(new J1939TransportFaultEventArgs(
                J1939TransportDirection.Inbound, kind, J1939TransportFault.UnexpectedDataPacket,
                abortPeer ? J1939ConnectionAbortReason.UnexpectedDataTransferPacket : J1939ConnectionAbortReason.None,
                J1939TimeoutDetection.NotApplicable, 0, message.SourceAddress,
                message.DestinationAddress, 0, 0, 0, 0, -1, message.Data[0], 0.0, 0,
                _timingDegraded, null, description));
        }

        private void ReportMalformed(J1939Message message, J1939TransportKind kind, string detail)
        {
            EnqueueNotification(new J1939TransportFaultEventArgs(
                J1939TransportDirection.Inbound, kind, J1939TransportFault.MalformedControlFrame,
                J1939ConnectionAbortReason.None, J1939TimeoutDetection.NotApplicable, 0,
                message.SourceAddress, message.DestinationAddress, 0, 0, 0, 0, -1, -1, 0.0, 0,
                message.TimestampSource != J1939TimestampSource.Adapter, null,
                string.Format(CultureInfo.InvariantCulture,
                    "Discarded transport frame from 0x{0:X2}: {1}. Frame: {2}",
                    message.SourceAddress, detail, message)));
        }

        private void RemoveIfFinished(InboundSession session)
        {
            if (session.Finished)
            {
                _pairInbound.Remove(PairKey(session.SourceAddress, session.DestinationAddress));
            }
        }

        // =====================================================================
        //  Timeout scanning and transmit pumping
        // =====================================================================

        private readonly List<byte> _bamSweep = new List<byte>();
        private readonly List<int> _pairSweep = new List<int>();
        private readonly List<byte> _outboundSweep = new List<byte>();

        private void ScanInboundTimeouts()
        {
            _bamSweep.Clear();
            foreach (KeyValuePair<byte, BamInboundSession> entry in _bamInbound)
            {
                entry.Value.CheckTimeouts(_hostNowMs);
                if (entry.Value.Finished)
                {
                    _bamSweep.Add(entry.Key);
                }
            }

            for (int i = 0; i < _bamSweep.Count; i++)
            {
                _bamInbound.Remove(_bamSweep[i]);
            }

            _pairSweep.Clear();
            foreach (KeyValuePair<int, InboundSession> entry in _pairInbound)
            {
                entry.Value.CheckTimeouts(_hostNowMs);
                if (entry.Value.Finished)
                {
                    _pairSweep.Add(entry.Key);
                }
            }

            for (int i = 0; i < _pairSweep.Count; i++)
            {
                _pairInbound.Remove(_pairSweep[i]);
            }
        }

        private void PumpOutbound()
        {
            if (_bamOutbound != null)
            {
                _bamOutbound.OnTick(_hostNowMs);
                if (_bamOutbound.Finished)
                {
                    _bamOutbound = null;
                }
            }

            _outboundSweep.Clear();
            foreach (KeyValuePair<byte, OutboundSession> entry in _pairOutbound)
            {
                entry.Value.OnTick(_hostNowMs);
                if (entry.Value.Finished)
                {
                    _outboundSweep.Add(entry.Key);
                }
            }

            for (int i = 0; i < _outboundSweep.Count; i++)
            {
                _pairOutbound.Remove(_outboundSweep[i]);
            }
        }

        private void PromotePendingSends()
        {
            if (_pendingSends.Count == 0)
            {
                return;
            }

            // Scanned in FIFO order, so ordering to any one destination is preserved.
            // A message whose destination is free is allowed past one whose destination
            // is busy: head-of-line blocking on an unrelated ECU would stall the tool.
            int queued = _pendingSends.Count;
            for (int i = 0; i < queued; i++)
            {
                PendingSend pending = _pendingSends.Dequeue();

                if (CanStart(pending))
                {
                    StartOutbound(pending);
                }
                else
                {
                    _pendingSends.Enqueue(pending);
                }
            }
        }

        private bool CanStart(PendingSend pending)
        {
            if (pending.Kind == J1939TransportKind.Bam)
            {
                return _bamOutbound == null;
            }

            return !_pairOutbound.ContainsKey(pending.DestinationAddress);
        }

        // =====================================================================
        //  Send admission
        // =====================================================================

        private struct PendingSend
        {
            public long TicketId;
            public J1939TransportKind Kind;
            public byte Priority;
            public uint Pgn;
            public byte DestinationAddress;
            public byte[] Payload;
        }

        private J1939SendTicket BeginSendLocked(byte priority, uint pgn, byte destinationAddress, byte[] payload)
        {
            if (payload.Length == 0)
            {
                return new J1939SendTicket(false, 0, J1939TransportKind.None,
                    "payload is empty; there is nothing to transmit");
            }

            if (destinationAddress == J1939Pgn.NullAddress)
            {
                return new J1939SendTicket(false, 0, J1939TransportKind.None,
                    "the null address (254) marks a node that failed to claim and cannot receive");
            }

            byte pduFormat = (byte)((pgn >> 8) & 0xFFu);
            bool pdu1 = pduFormat <= J1939Id.PDU1_PF_MAXIMUM;

            if (pdu1 && (pgn & 0xFFu) != 0)
            {
                return new J1939SendTicket(false, 0, J1939TransportKind.None,
                    string.Format(CultureInfo.InvariantCulture,
                        "PGN {0} (0x{1:X4}) is PDU1, so its low byte must be zero; the destination goes in the destinationAddress argument, not in the PGN",
                        pgn, pgn));
            }

            long ticketId = _nextTicketId++;

            if (payload.Length <= 8)
            {
                if (!pdu1 && destinationAddress != J1939Pgn.GlobalAddress)
                {
                    return new J1939SendTicket(false, 0, J1939TransportKind.None,
                        string.Format(CultureInfo.InvariantCulture,
                            "PGN {0} (0x{1:X4}) is PDU2 and is broadcast only; a single frame cannot carry destination 0x{2:X2}",
                            pgn, pgn, destinationAddress));
                }

                byte encodedDestination = pdu1 ? destinationAddress : J1939Pgn.GlobalAddress;
                J1939Message frame = J1939Message.FromHostClock(
                    J1939Id.Create(priority, pgn, _sourceAddress, encodedDestination), payload);

                EnqueueFrame(frame, ticketId);
                EnqueueNotification(new J1939TransportMessageEventArgs(
                    J1939TransportDirection.Outbound, J1939TransportKind.SingleFrame, frame,
                    _sourceAddress, destinationAddress, pgn, 1, 0.0, false, string.Empty));

                return new J1939SendTicket(true, ticketId, J1939TransportKind.SingleFrame, null);
            }

            J1939TransportKind kind;

            if (destinationAddress == J1939Pgn.GlobalAddress)
            {
                if (payload.Length > J1939Pgn.MaxTransportProtocolPayloadBytes)
                {
                    return new J1939SendTicket(false, 0, J1939TransportKind.None,
                        string.Format(CultureInfo.InvariantCulture,
                            "{0} bytes cannot be broadcast: BAM carries at most {1} bytes and ETP has no broadcast form. Send it to a specific destination.",
                            payload.Length, J1939Pgn.MaxTransportProtocolPayloadBytes));
                }

                kind = J1939TransportKind.Bam;
            }
            else if (payload.Length <= J1939Pgn.MaxTransportProtocolPayloadBytes)
            {
                kind = J1939TransportKind.ConnectionMode;
            }
            else
            {
                if (payload.Length > J1939Pgn.MaxExtendedTransportProtocolPayloadBytes)
                {
                    return new J1939SendTicket(false, 0, J1939TransportKind.None,
                        string.Format(CultureInfo.InvariantCulture,
                            "{0} bytes exceeds the ETP maximum of {1} bytes",
                            payload.Length, J1939Pgn.MaxExtendedTransportProtocolPayloadBytes));
                }

                kind = J1939TransportKind.ExtendedConnectionMode;
            }

            byte[] copy = new byte[payload.Length];
            Buffer.BlockCopy(payload, 0, copy, 0, payload.Length);

            PendingSend request = new PendingSend();
            request.TicketId = ticketId;
            request.Kind = kind;
            request.Priority = priority;
            request.Pgn = pgn;
            request.DestinationAddress = destinationAddress;
            request.Payload = copy;

            if (CanStart(request))
            {
                StartOutbound(request);
                return new J1939SendTicket(true, ticketId, kind, null);
            }

            if (_pendingSends.Count >= _maxQueuedOutboundMessages)
            {
                return new J1939SendTicket(false, 0, J1939TransportKind.None,
                    string.Format(CultureInfo.InvariantCulture,
                        "{0} messages are already queued (limit {1}) and the {2} path to 0x{3:X2} is busy",
                        _pendingSends.Count, _maxQueuedOutboundMessages, kind, destinationAddress));
            }

            _pendingSends.Enqueue(request);
            return new J1939SendTicket(true, ticketId, kind, null);
        }

        private void StartOutbound(PendingSend request)
        {
            switch (request.Kind)
            {
                case J1939TransportKind.Bam:
                    {
                        BamOutboundSession session = new BamOutboundSession(this, request);
                        _bamOutbound = session;
                        session.Start(_hostNowMs);
                        if (session.Finished)
                        {
                            _bamOutbound = null;
                        }

                        break;
                    }

                case J1939TransportKind.ConnectionMode:
                    {
                        ConnectionOutboundSession session = new ConnectionOutboundSession(this, request);
                        _pairOutbound[request.DestinationAddress] = session;
                        session.Start(_hostNowMs);
                        if (session.Finished)
                        {
                            _pairOutbound.Remove(request.DestinationAddress);
                        }

                        break;
                    }

                default:
                    {
                        EtpOutboundSession session = new EtpOutboundSession(this, request);
                        _pairOutbound[request.DestinationAddress] = session;
                        session.Start(_hostNowMs);
                        if (session.Finished)
                        {
                            _pairOutbound.Remove(request.DestinationAddress);
                        }

                        break;
                    }
            }
        }

        // =====================================================================
        //  Frame construction
        // =====================================================================

        private J1939Message BuildTpCm(byte[] data, byte destination)
        {
            return J1939Message.FromHostClock(
                J1939Id.Create(_transportPriority, J1939Pgn.TransportProtocolConnectionManagement,
                    _sourceAddress, destination), data);
        }

        private J1939Message BuildTpDt(byte[] data, byte destination)
        {
            return J1939Message.FromHostClock(
                J1939Id.Create(_transportPriority, J1939Pgn.TransportProtocolDataTransfer,
                    _sourceAddress, destination), data);
        }

        private J1939Message BuildEtpCm(byte[] data, byte destination)
        {
            return J1939Message.FromHostClock(
                J1939Id.Create(_transportPriority, J1939Pgn.ExtendedTransportProtocolConnectionManagement,
                    _sourceAddress, destination), data);
        }

        private J1939Message BuildEtpDt(byte[] data, byte destination)
        {
            return J1939Message.FromHostClock(
                J1939Id.Create(_transportPriority, J1939Pgn.ExtendedTransportProtocolDataTransfer,
                    _sourceAddress, destination), data);
        }

        private J1939Message BuildAbort(byte destination, uint pgn, J1939ConnectionAbortReason reason, bool extended)
        {
            byte[] data = new byte[8];
            data[0] = J1939Pgn.TpCmAbort;
            data[1] = (byte)reason;
            data[2] = 0xFF;
            data[3] = 0xFF;
            data[4] = 0xFF;
            data[5] = (byte)(pgn & 0xFFu);
            data[6] = (byte)((pgn >> 8) & 0xFFu);
            data[7] = (byte)((pgn >> 16) & 0xFFu);

            return extended ? BuildEtpCm(data, destination) : BuildTpCm(data, destination);
        }

        private static byte[] BuildDataFrame(int sequenceNumber, byte[] payload, int offset)
        {
            byte[] data = new byte[8];
            data[0] = (byte)sequenceNumber;

            int remaining = payload.Length - offset;
            int count = remaining < J1939Pgn.TransportProtocolBytesPerPacket
                ? remaining
                : J1939Pgn.TransportProtocolBytesPerPacket;

            for (int i = 0; i < J1939Pgn.TransportProtocolBytesPerPacket; i++)
            {
                data[i + 1] = i < count ? payload[offset + i] : (byte)0xFF;
            }

            return data;
        }

        // =====================================================================
        //  Queued emission and dispatch
        // =====================================================================

        private struct OutgoingFrame
        {
            public J1939Message Frame;
            public long FaultTag;
        }

        private readonly Queue<OutgoingFrame> _outgoing = new Queue<OutgoingFrame>();

        private void EnqueueFrame(J1939Message frame, long faultTag)
        {
            OutgoingFrame item = new OutgoingFrame();
            item.Frame = frame;
            item.FaultTag = faultTag;
            _outgoing.Enqueue(item);
        }

        private void EnqueueNotification(EventArgs args)
        {
            _notifications.Enqueue(args);
        }

        // Drains frames and events OUTSIDE the session lock, in FIFO order. Holding
        // _gate across a driver write or an application event handler is how transport
        // stacks deadlock; _flushGate serialises drainers without blocking ingest.
        private void Flush()
        {
            Exception handlerFault = null;

            lock (_flushGate)
            {
                while (true)
                {
                    OutgoingFrame frame = new OutgoingFrame();
                    EventArgs notification = null;
                    bool haveFrame = false;

                    lock (_gate)
                    {
                        if (_outgoing.Count > 0)
                        {
                            frame = _outgoing.Dequeue();
                            haveFrame = true;
                        }
                        else if (_notifications.Count > 0)
                        {
                            notification = _notifications.Dequeue();
                        }
                    }

                    if (haveFrame)
                    {
                        TransmitOne(frame);
                        continue;
                    }

                    if (notification == null)
                    {
                        break;
                    }

                    try
                    {
                        RaiseNotification(notification);
                    }
                    catch (Exception ex)
                    {
                        // Keep draining so nothing queued is lost, then surface the
                        // handler's bug to the pump loop rather than swallowing it.
                        if (handlerFault == null)
                        {
                            handlerFault = ex;
                        }
                    }
                }
            }

            if (handlerFault != null)
            {
                throw new InvalidOperationException(
                    "A J1939 transport event handler threw. The transport state is intact and all queued work was drained; fix the handler. Inner exception has the detail.",
                    handlerFault);
            }
        }

        private void TransmitOne(OutgoingFrame item)
        {
            bool sent;
            string failureDetail;

            try
            {
                sent = _frameSink(item.Frame);
                failureDetail = sent ? null : "the frame sink refused the frame";
            }
            catch (Exception ex)
            {
                sent = false;
                failureDetail = ex.Message;
            }

            if (sent)
            {
                return;
            }

            NoteTransmitFailure(item, failureDetail);
        }

        private void NoteTransmitFailure(OutgoingFrame item, string detail)
        {
            bool reported = false;

            lock (_gate)
            {
                if (item.FaultTag > 0)
                {
                    if (_bamOutbound != null && _bamOutbound.TicketId == item.FaultTag)
                    {
                        _bamOutbound.FailTransmit(detail);
                        _bamOutbound = null;
                        reported = true;
                    }
                    else
                    {
                        byte match = 0;
                        bool found = false;
                        foreach (KeyValuePair<byte, OutboundSession> entry in _pairOutbound)
                        {
                            if (entry.Value.TicketId == item.FaultTag)
                            {
                                match = entry.Key;
                                found = true;
                                break;
                            }
                        }

                        if (found)
                        {
                            _pairOutbound[match].FailTransmit(detail);
                            _pairOutbound.Remove(match);
                            reported = true;
                        }
                    }
                }
                else if (item.FaultTag < 0)
                {
                    int key = (int)(-item.FaultTag - 1);
                    InboundSession session;
                    if (_pairInbound.TryGetValue(key, out session))
                    {
                        session.FailTransmit(detail);
                        _pairInbound.Remove(key);
                        reported = true;
                    }
                }

                if (!reported)
                {
                    EnqueueNotification(new J1939TransportFaultEventArgs(
                        J1939TransportDirection.Outbound, J1939TransportKind.None,
                        J1939TransportFault.TransmitFailed, J1939ConnectionAbortReason.None,
                        J1939TimeoutDetection.NotApplicable, item.Frame.Pgn, _sourceAddress,
                        item.Frame.DestinationAddress, 0, 0, 0, 0, -1, -1, 0.0, 0,
                        _timingDegraded, null,
                        string.Format(CultureInfo.InvariantCulture,
                            "Could not transmit {0}: {1}", item.Frame, detail)));
                }
            }
        }

        private void RaiseNotification(EventArgs args)
        {
            J1939TransportFaultEventArgs fault = args as J1939TransportFaultEventArgs;
            if (fault != null)
            {
                EventHandler<J1939TransportFaultEventArgs> failed = SessionFailed;
                if (failed != null)
                {
                    failed(this, fault);
                }

                return;
            }

            J1939TransportMessageEventArgs message = args as J1939TransportMessageEventArgs;
            if (message == null)
            {
                return;
            }

            if (message.Direction == J1939TransportDirection.Inbound)
            {
                EventHandler<J1939TransportMessageEventArgs> reassembled = MessageReassembled;
                if (reassembled != null)
                {
                    reassembled(this, message);
                }

                return;
            }

            EventHandler<J1939TransportMessageEventArgs> sent = MessageSent;
            if (sent != null)
            {
                sent(this, message);
            }
        }

        // =====================================================================
        //  Inbound session base.
        //
        //  This holds ONLY buffer bookkeeping and timing provenance - the parts
        //  that are genuinely identical whatever announced the message. It holds
        //  no state machine. Each mechanism below implements its own frame
        //  handling and its own timeout policy, because BAM, RTS/CTS and ETP do
        //  not behave alike and pretending they do is how transport layers ship
        //  bugs that only appear on a real vehicle.
        // =====================================================================

        private abstract class InboundSession
        {
            protected readonly J1939TransportProtocol Owner;
            protected readonly byte Priority;
            protected readonly byte[] Payload;
            protected readonly bool[] PacketReceived;
            protected readonly int TotalBytes;
            protected readonly int TotalPackets;
            protected readonly uint DeclaredPgn;
            protected readonly double StartedMs;

            protected int PacketsReceived;
            protected double LastActivityMs;
            protected J1939Message LastFrame;
            protected bool TimingDegraded;

            private readonly List<string> _notes = new List<string>();
            private int _suppressedNotes;

            protected InboundSession(J1939TransportProtocol owner, J1939Message announcement,
                uint pgn, uint declaredPgn, int totalBytes, int totalPackets)
            {
                Owner = owner;
                SourceAddress = announcement.SourceAddress;
                DestinationAddress = announcement.DestinationAddress;
                Priority = announcement.Id.Priority;
                Pgn = pgn;
                DeclaredPgn = declaredPgn;
                TotalBytes = totalBytes;
                TotalPackets = totalPackets;

                // Allocated only after the announced size has been validated against
                // MaxInboundMessageBytes by the caller. Never sized from a raw field.
                Payload = new byte[totalBytes];
                PacketReceived = new bool[totalPackets];

                StartedMs = owner._hostNowMs;
                LastActivityMs = owner._hostNowMs;
                LastFrame = announcement;
                TimingDegraded = announcement.TimestampSource != J1939TimestampSource.Adapter;
            }

            public byte SourceAddress { get; private set; }

            public byte DestinationAddress { get; private set; }

            public uint Pgn { get; private set; }

            public bool Finished { get; protected set; }

            public abstract J1939TransportKind Kind { get; }

            public abstract void CheckTimeouts(double nowMs);

            public abstract string Describe(double nowMs);

            public void AddNote(string note)
            {
                if (_notes.Count >= MaxNotesPerSession)
                {
                    _suppressedNotes++;
                    return;
                }

                _notes.Add(note);
            }

            protected string NotesText
            {
                get
                {
                    if (_notes.Count == 0)
                    {
                        return string.Empty;
                    }

                    string joined = string.Join("; ", _notes.ToArray());
                    if (_suppressedNotes > 0)
                    {
                        joined = string.Format(CultureInfo.InvariantCulture,
                            "{0}; and {1} further anomalies not listed", joined, _suppressedNotes);
                    }

                    return joined;
                }
            }

            protected int SessionKey
            {
                get { return PairKey(SourceAddress, DestinationAddress); }
            }

            // Negative fault tag identifies this inbound session, so a CTS or ACK that
            // the adapter refuses fails the session instead of stalling it silently.
            protected long FaultTag
            {
                get { return -(SessionKey + 1L); }
            }

            // Measures the gap since the previous frame of this session. Uses adapter
            // hardware timestamps whenever both frames carry them in the same unit;
            // ElapsedMsBetween handles the 32-bit counter wrap.
            protected double MeasureGap(J1939Message message, out bool adapterMeasured)
            {
                adapterMeasured = LastFrame != null
                                  && LastFrame.TimestampSource == J1939TimestampSource.Adapter
                                  && message.TimestampSource == J1939TimestampSource.Adapter
                                  && LastFrame.AdapterTimestampUnit == message.AdapterTimestampUnit;

                double gap = LastFrame != null ? J1939Message.ElapsedMsBetween(LastFrame, message) : 0.0;

                if (message.TimestampSource != J1939TimestampSource.Adapter)
                {
                    TimingDegraded = true;
                }

                return gap;
            }

            protected void MarkActivity(J1939Message message)
            {
                LastFrame = message;

                // Fresh reading, not the cached tick value: a frame ingested during a long
                // pump stall must record when it actually arrived, or the next Tick will
                // charge the stall against the sender and abort a perfectly healthy session.
                LastActivityMs = Owner.HostNow();
                if (LastActivityMs > Owner._hostNowMs) Owner._hostNowMs = LastActivityMs;
            }

            // Returns false when the same sequence number arrived twice carrying
            // DIFFERENT data. Two versions of one packet cannot both be right, and
            // guessing which is correct is exactly the kind of plausible-looking wrong
            // answer this layer exists to prevent.
            protected bool StoreSegment(int packetIndex, byte[] frameData, out bool duplicateIdentical)
            {
                duplicateIdentical = false;

                int offset = packetIndex * J1939Pgn.TransportProtocolBytesPerPacket;
                int remaining = TotalBytes - offset;
                int count = remaining < J1939Pgn.TransportProtocolBytesPerPacket
                    ? remaining
                    : J1939Pgn.TransportProtocolBytesPerPacket;

                if (count <= 0)
                {
                    return false;
                }

                if (PacketReceived[packetIndex])
                {
                    for (int i = 0; i < count; i++)
                    {
                        if (Payload[offset + i] != frameData[i + 1])
                        {
                            return false;
                        }
                    }

                    duplicateIdentical = true;
                    return true;
                }

                for (int i = 0; i < count; i++)
                {
                    Payload[offset + i] = frameData[i + 1];
                }

                PacketReceived[packetIndex] = true;
                PacketsReceived++;
                return true;
            }

            protected void Complete()
            {
                // The transported PGN, not the TP.CM identifier the frames arrived
                // under. A PDU2 group carries no destination field, so it is encoded
                // broadcast; the real peer address travels on the event args.
                byte pduFormat = (byte)((Pgn >> 8) & 0xFFu);
                byte encodedDestination = pduFormat >= J1939Id.PDU2_PF_MINIMUM
                    ? J1939Pgn.GlobalAddress
                    : DestinationAddress;

                J1939Id id = J1939Id.Create(Priority, Pgn, SourceAddress, encodedDestination);

                J1939Message assembled = LastFrame != null && LastFrame.HasAdapterTimestamp
                    ? J1939Message.FromAdapter(id, Payload, LastFrame.AdapterTimestamp, LastFrame.AdapterTimestampUnit)
                    : J1939Message.FromHostClock(id, Payload);

                if (TimingDegraded)
                {
                    AddNote("at least one frame had no adapter timestamp; session timing is host-clock soft");
                }

                Owner.EnqueueNotification(new J1939TransportMessageEventArgs(
                    J1939TransportDirection.Inbound, Kind, assembled, SourceAddress, DestinationAddress,
                    DeclaredPgn, TotalPackets, Owner._hostNowMs - StartedMs, TimingDegraded, NotesText));

                Finished = true;
            }

            protected void Fail(J1939TransportFault fault, J1939ConnectionAbortReason abortReason,
                J1939TimeoutDetection detection, int expectedSequence, int observedSequence,
                double elapsedMs, int limitMs, string description, bool sendAbort)
            {
                if (Finished)
                {
                    return;
                }

                Finished = true;

                if (sendAbort && abortReason != J1939ConnectionAbortReason.None)
                {
                    // Tagged 0: the session is already gone, so a transmit failure here
                    // is reported on its own rather than trying to fail it twice.
                    Owner.EnqueueFrame(Owner.BuildAbort(SourceAddress, Pgn, abortReason,
                        Kind == J1939TransportKind.ExtendedConnectionMode), 0);
                }

                byte[] partial = null;
                if (PacketsReceived > 0)
                {
                    partial = new byte[TotalBytes];
                    Buffer.BlockCopy(Payload, 0, partial, 0, TotalBytes);
                }

                string notes = NotesText;
                string full = notes.Length == 0
                    ? description
                    : string.Format(CultureInfo.InvariantCulture, "{0} Earlier anomalies: {1}.", description, notes);

                Owner.EnqueueNotification(new J1939TransportFaultEventArgs(
                    J1939TransportDirection.Inbound, Kind, fault, abortReason, detection, Pgn,
                    SourceAddress, DestinationAddress,
                    PacketsReceived * J1939Pgn.TransportProtocolBytesPerPacket > TotalBytes
                        ? TotalBytes
                        : PacketsReceived * J1939Pgn.TransportProtocolBytesPerPacket,
                    TotalBytes, PacketsReceived, TotalPackets, expectedSequence, observedSequence,
                    elapsedMs, limitMs, TimingDegraded, partial, full));
            }

            public void FailSuperseded(uint newPgn)
            {
                Fail(J1939TransportFault.SupersededByNewSession, J1939ConnectionAbortReason.None,
                    J1939TimeoutDetection.NotApplicable, -1, -1, 0.0, 0,
                    string.Format(CultureInfo.InvariantCulture,
                        "Node 0x{0:X2} announced a new {1} session for {2} while {3} was still reassembling ({4} of {5} packets received). Per J1939-21 the older session is abandoned; the incomplete data is discarded.",
                        SourceAddress, Kind, J1939Pgn.Describe(newPgn), J1939Pgn.Describe(Pgn),
                        PacketsReceived, TotalPackets), false);
            }

            public void FailAbortReceived(J1939ConnectionAbortReason reason)
            {
                Fail(J1939TransportFault.AbortReceived, reason, J1939TimeoutDetection.NotApplicable,
                    -1, -1, 0.0, 0,
                    string.Format(CultureInfo.InvariantCulture,
                        "Node 0x{0:X2} aborted its {1} transfer of {2} with reason {3} = {4} after {5} of {6} packets.",
                        SourceAddress, Kind, J1939Pgn.Describe(Pgn), (byte)reason, reason,
                        PacketsReceived, TotalPackets), false);
            }

            public void FailOnReset()
            {
                Fail(J1939TransportFault.StackReset, J1939ConnectionAbortReason.None,
                    J1939TimeoutDetection.NotApplicable, -1, -1, 0.0, 0,
                    string.Format(CultureInfo.InvariantCulture,
                        "Transport reset while reassembling {0} from 0x{1:X2} ({2} of {3} packets received).",
                        J1939Pgn.Describe(Pgn), SourceAddress, PacketsReceived, TotalPackets), false);
            }

            public void FailTransmit(string detail)
            {
                Fail(J1939TransportFault.TransmitFailed, J1939ConnectionAbortReason.None,
                    J1939TimeoutDetection.NotApplicable, -1, -1, 0.0, 0,
                    string.Format(CultureInfo.InvariantCulture,
                        "Could not send flow control to 0x{0:X2} for {1}: {2}. The transfer cannot continue.",
                        SourceAddress, J1939Pgn.Describe(Pgn), detail), false);
            }

            public void FailUnexpectedDataPacket(J1939Message message)
            {
                Fail(J1939TransportFault.UnexpectedDataPacket,
                    J1939ConnectionAbortReason.UnexpectedDataTransferPacket,
                    J1939TimeoutDetection.NotApplicable, -1, message.Data[0], 0.0, 0,
                    string.Format(CultureInfo.InvariantCulture,
                        "Node 0x{0:X2} sent a {1} data packet while a {2} session was open for {3}. The two mechanisms cannot share a connection, so the session is abandoned.",
                        SourceAddress,
                        message.Pgn == J1939Pgn.ExtendedTransportProtocolDataTransfer ? "ETP.DT" : "TP.DT",
                        Kind, J1939Pgn.Describe(Pgn)), true);
            }

            protected string DescribeCommon(string state, double nowMs)
            {
                return string.Format(CultureInfo.InvariantCulture,
                    "IN  {0,-14} SA 0x{1:X2} DA 0x{2:X2} {3} {4}/{5} packets {6}/{7} bytes, {8}, idle {9:F0} ms{10}",
                    Kind, SourceAddress, DestinationAddress, J1939Pgn.Describe(Pgn),
                    PacketsReceived, TotalPackets,
                    PacketsReceived * J1939Pgn.TransportProtocolBytesPerPacket > TotalBytes
                        ? TotalBytes
                        : PacketsReceived * J1939Pgn.TransportProtocolBytesPerPacket,
                    TotalBytes, state, nowMs - LastActivityMs,
                    TimingDegraded ? " (host clock)" : string.Empty);
            }
        }

        // =====================================================================
        //  STATE MACHINE 1 of 3 - BAM reassembly (broadcast, unacknowledged)
        //
        //  There is no back-channel. We cannot ask for a retransmission, we cannot
        //  pace the sender, and we must not transmit a Conn_Abort: there is no
        //  connection to abort and a BAM sender is not listening. The only two
        //  outcomes are a complete message or a reported failure. Sequence numbers
        //  must arrive 1..N in order; CAN preserves per-source frame order, so an
        //  unexpected sequence number means packets were lost on the bus or in the
        //  adapter queue, not that they arrived out of order.
        // =====================================================================

        private sealed class BamInboundSession : InboundSession
        {
            private enum BamRxState
            {
                AwaitingFirstPacket,
                AwaitingNextPacket
            }

            private BamRxState _state = BamRxState.AwaitingFirstPacket;
            private int _nextExpectedSequence = 1;
            private int _duplicatesAbsorbed;

            public BamInboundSession(J1939TransportProtocol owner, J1939Message announcement,
                uint pgn, uint declaredPgn, int totalBytes, int totalPackets)
                : base(owner, announcement, pgn, declaredPgn, totalBytes, totalPackets)
            {
            }

            public override J1939TransportKind Kind
            {
                get { return J1939TransportKind.Bam; }
            }

            public void HandleDataTransfer(J1939Message message)
            {
                bool adapterMeasured;
                double gap = MeasureGap(message, out adapterMeasured);

                // Adapter-accurate late detection. A BAM sender that stalls past T1 has
                // lost the thread; anything it sends afterwards belongs to no session
                // we can trust to be contiguous.
                if (gap > J1939Pgn.ConnectionTimeoutT1Ms)
                {
                    Fail(J1939TransportFault.TimeoutT1, J1939ConnectionAbortReason.None,
                        adapterMeasured ? J1939TimeoutDetection.AdapterTimestamp : J1939TimeoutDetection.HostTick,
                        _nextExpectedSequence, message.Data[0], gap, J1939Pgn.ConnectionTimeoutT1Ms,
                        string.Format(CultureInfo.InvariantCulture,
                            "BAM from 0x{0:X2} for {1}: {2:F0} ms between packets exceeds T1 ({3} ms). Broadcast transport cannot recover a stalled sender, so {4} of {5} packets are discarded.",
                            SourceAddress, J1939Pgn.Describe(Pgn), gap, J1939Pgn.ConnectionTimeoutT1Ms,
                            PacketsReceived, TotalPackets), false);
                    return;
                }

                MarkActivity(message);

                int sequence = message.Data[0];

                if (sequence < 1 || sequence > TotalPackets)
                {
                    Fail(J1939TransportFault.SequenceGap, J1939ConnectionAbortReason.None,
                        J1939TimeoutDetection.NotApplicable, _nextExpectedSequence, sequence, 0.0, 0,
                        string.Format(CultureInfo.InvariantCulture,
                            "BAM from 0x{0:X2} for {1}: sequence number {2} is outside the announced range 1..{3}. The stream is not what was announced, so nothing is delivered.",
                            SourceAddress, J1939Pgn.Describe(Pgn), sequence, TotalPackets), false);
                    return;
                }

                if (sequence != _nextExpectedSequence)
                {
                    if (PacketReceived[sequence - 1])
                    {
                        HandleDuplicate(sequence, message);
                        return;
                    }

                    Fail(J1939TransportFault.SequenceGap, J1939ConnectionAbortReason.None,
                        J1939TimeoutDetection.NotApplicable, _nextExpectedSequence, sequence, 0.0, 0,
                        string.Format(CultureInfo.InvariantCulture,
                            "BAM from 0x{0:X2} for {1}: expected packet {2}, received packet {3}. {4} packet(s) were lost and BAM has no retransmission, so the message is discarded rather than delivered short.",
                            SourceAddress, J1939Pgn.Describe(Pgn), _nextExpectedSequence, sequence,
                            sequence - _nextExpectedSequence), false);
                    return;
                }

                bool duplicateIdentical;
                if (!StoreSegment(sequence - 1, message.Data, out duplicateIdentical))
                {
                    FailDuplicateConflict(sequence);
                    return;
                }

                _nextExpectedSequence++;
                _state = BamRxState.AwaitingNextPacket;

                if (PacketsReceived == TotalPackets)
                {
                    if (_duplicatesAbsorbed > 0)
                    {
                        AddNote(string.Format(CultureInfo.InvariantCulture,
                            "{0} duplicate packet(s) absorbed (identical data)", _duplicatesAbsorbed));
                    }

                    Complete();
                }
            }

            private void HandleDuplicate(int sequence, J1939Message message)
            {
                bool duplicateIdentical;
                if (!StoreSegment(sequence - 1, message.Data, out duplicateIdentical))
                {
                    FailDuplicateConflict(sequence);
                    return;
                }

                // Identical repeat. Some adapters replay frames from their queue after a
                // buffer wrap; absorbing it is safe because the bytes match exactly.
                _duplicatesAbsorbed++;
            }

            private void FailDuplicateConflict(int sequence)
            {
                Fail(J1939TransportFault.DuplicateSequenceConflict, J1939ConnectionAbortReason.None,
                    J1939TimeoutDetection.NotApplicable, _nextExpectedSequence, sequence, 0.0, 0,
                    string.Format(CultureInfo.InvariantCulture,
                        "BAM from 0x{0:X2} for {1}: packet {2} arrived twice with different data. Both copies cannot be right, so the message is discarded rather than guessed at.",
                        SourceAddress, J1939Pgn.Describe(Pgn), sequence), false);
            }

            public override void CheckTimeouts(double nowMs)
            {
                double idle = nowMs - LastActivityMs;
                if (idle <= J1939Pgn.ConnectionTimeoutT1Ms)
                {
                    return;
                }

                string phase = _state == BamRxState.AwaitingFirstPacket
                    ? "no data packet followed the announcement"
                    : string.Format(CultureInfo.InvariantCulture,
                        "the sender stopped after packet {0}", _nextExpectedSequence - 1);

                Fail(J1939TransportFault.TimeoutT1, J1939ConnectionAbortReason.None,
                    J1939TimeoutDetection.HostTick, _nextExpectedSequence, -1, idle,
                    J1939Pgn.ConnectionTimeoutT1Ms,
                    string.Format(CultureInfo.InvariantCulture,
                        "BAM from 0x{0:X2} for {1} timed out: {2} within T1 ({3} ms). {4} of {5} packets were received and are discarded. Detected by the host tick watchdog, so the figure includes driver latency.",
                        SourceAddress, J1939Pgn.Describe(Pgn), phase, J1939Pgn.ConnectionTimeoutT1Ms,
                        PacketsReceived, TotalPackets), false);
            }

            public override string Describe(double nowMs)
            {
                return DescribeCommon(_state == BamRxState.AwaitingFirstPacket
                    ? "awaiting first TP.DT"
                    : string.Format(CultureInfo.InvariantCulture, "awaiting TP.DT {0}", _nextExpectedSequence), nowMs);
            }
        }

        // =====================================================================
        //  STATE MACHINE 2 of 3 - RTS/CTS connection mode reassembly
        //
        //  Nothing like BAM. WE pace the sender: every burst is explicitly granted
        //  by a CTS carrying a window size and a restart point, the transfer ends
        //  with an EndOfMsgACK, and either end may abort with a reason code. The
        //  sender is listening, so a failure here MUST put a Conn_Abort on the wire
        //  - otherwise the ECU sits on an open connection until its own timer runs
        //  out and refuses the retry we are about to make.
        //
        //  In monitor mode (a session between two other nodes) this machine is
        //  passive: it reassembles what it observes and transmits nothing, because
        //  the real receiver owns the flow control.
        // =====================================================================

        private sealed class ConnectionInboundSession : InboundSession
        {
            private enum ConnectionRxState
            {
                AwaitingFirstPacketOfBurst,
                AwaitingNextPacketOfBurst
            }

            private readonly bool _passive;
            private readonly int _senderPacketLimit;

            private ConnectionRxState _state = ConnectionRxState.AwaitingFirstPacketOfBurst;
            private int _nextExpectedSequence = 1;
            private int _burstRemaining;
            private int _duplicatesAbsorbed;
            private int _sequenceGapRecoveries;
            private int _stalePacketsDiscarded;
            private bool _awaitingRetransmission;

            public ConnectionInboundSession(J1939TransportProtocol owner, J1939Message announcement,
                uint pgn, uint declaredPgn, int totalBytes, int totalPackets, byte maxPacketsPerCts)
                : base(owner, announcement, pgn, declaredPgn, totalBytes, totalPackets)
            {
                _passive = announcement.DestinationAddress != owner._sourceAddress;

                // 0xFF means the sender imposes no limit. 0 is not a legal value; treat
                // it as "no limit" rather than deadlocking on a zero-size window.
                if (maxPacketsPerCts == 0)
                {
                    _senderPacketLimit = J1939Pgn.MaxTransportProtocolPackets;
                    AddNote("RTS declared a maximum of 0 packets per CTS, which is not a legal window; treated as unlimited");
                }
                else
                {
                    _senderPacketLimit = maxPacketsPerCts;
                }
            }

            public override J1939TransportKind Kind
            {
                get { return J1939TransportKind.ConnectionMode; }
            }

            public void Start()
            {
                if (_passive)
                {
                    AddNote("monitored session: this tool is not the destination and sent no flow control");
                    _burstRemaining = TotalPackets;
                    return;
                }

                SendClearToSend();
            }

            private void SendClearToSend()
            {
                int remaining = TotalPackets - PacketsReceived;
                int grant = Owner._receiveWindowPackets;

                if (grant > _senderPacketLimit)
                {
                    grant = _senderPacketLimit;
                }

                if (grant > remaining)
                {
                    grant = remaining;
                }

                if (grant < 1)
                {
                    grant = 1;
                }

                _burstRemaining = grant;
                _nextExpectedSequence = PacketsReceived + 1;
                _state = ConnectionRxState.AwaitingFirstPacketOfBurst;

                byte[] data = new byte[8];
                data[0] = J1939Pgn.TpCmCts;
                data[1] = (byte)grant;
                data[2] = (byte)_nextExpectedSequence;
                data[3] = 0xFF;
                data[4] = 0xFF;
                data[5] = (byte)(Pgn & 0xFFu);
                data[6] = (byte)((Pgn >> 8) & 0xFFu);
                data[7] = (byte)((Pgn >> 16) & 0xFFu);

                Owner.EnqueueFrame(Owner.BuildTpCm(data, SourceAddress), FaultTag);
            }

            private void SendEndOfMessageAck()
            {
                byte[] data = new byte[8];
                data[0] = J1939Pgn.TpCmEndOfMsgAck;
                data[1] = (byte)(TotalBytes & 0xFF);
                data[2] = (byte)((TotalBytes >> 8) & 0xFF);
                data[3] = (byte)TotalPackets;
                data[4] = 0xFF;
                data[5] = (byte)(Pgn & 0xFFu);
                data[6] = (byte)((Pgn >> 8) & 0xFFu);
                data[7] = (byte)((Pgn >> 16) & 0xFFu);

                Owner.EnqueueFrame(Owner.BuildTpCm(data, SourceAddress), FaultTag);
            }

            public void HandleDataTransfer(J1939Message message)
            {
                int limit = _state == ConnectionRxState.AwaitingFirstPacketOfBurst
                    ? J1939Pgn.ConnectionTimeoutT2Ms
                    : J1939Pgn.ConnectionTimeoutT1Ms;

                J1939TransportFault timeoutFault = _state == ConnectionRxState.AwaitingFirstPacketOfBurst
                    ? J1939TransportFault.TimeoutT2
                    : J1939TransportFault.TimeoutT1;

                bool adapterMeasured;
                double gap = MeasureGap(message, out adapterMeasured);

                if (gap > limit)
                {
                    Fail(timeoutFault, J1939ConnectionAbortReason.Timeout,
                        adapterMeasured ? J1939TimeoutDetection.AdapterTimestamp : J1939TimeoutDetection.HostTick,
                        _nextExpectedSequence, message.Data[0], gap, limit,
                        string.Format(CultureInfo.InvariantCulture,
                            "Connection-mode transfer of {0} from 0x{1:X2}: {2:F0} ms between packets exceeds {3} ({4} ms). {5} of {6} packets had arrived. Aborted so the node can restart.",
                            J1939Pgn.Describe(Pgn), SourceAddress, gap, timeoutFault, limit,
                            PacketsReceived, TotalPackets), !_passive);
                    return;
                }

                MarkActivity(message);

                int sequence = message.Data[0];

                if (sequence < 1 || sequence > TotalPackets)
                {
                    Fail(J1939TransportFault.SequenceGap, J1939ConnectionAbortReason.BadSequenceNumber,
                        J1939TimeoutDetection.NotApplicable, _nextExpectedSequence, sequence, 0.0, 0,
                        string.Format(CultureInfo.InvariantCulture,
                            "Connection-mode transfer of {0} from 0x{1:X2}: sequence number {2} is outside the announced range 1..{3}.",
                            J1939Pgn.Describe(Pgn), SourceAddress, sequence, TotalPackets), !_passive);
                    return;
                }

                if (sequence != _nextExpectedSequence)
                {
                    if (PacketReceived[sequence - 1])
                    {
                        bool identical;
                        if (!StoreSegment(sequence - 1, message.Data, out identical))
                        {
                            Fail(J1939TransportFault.DuplicateSequenceConflict,
                                J1939ConnectionAbortReason.DuplicateSequenceNumber,
                                J1939TimeoutDetection.NotApplicable, _nextExpectedSequence, sequence, 0.0, 0,
                                string.Format(CultureInfo.InvariantCulture,
                                    "Connection-mode transfer of {0} from 0x{1:X2}: packet {2} arrived twice with different data. Aborted rather than delivering a message that cannot be verified.",
                                    J1939Pgn.Describe(Pgn), SourceAddress, sequence), !_passive);
                            return;
                        }

                        _duplicatesAbsorbed++;
                        return;
                    }

                    // Every packet below PacketsReceived + 1 is already stored, so a
                    // sequence we have neither seen nor expected is always AHEAD of us:
                    // the packets in between were lost on the bus or in the adapter
                    // receive queue.
                    if (_awaitingRetransmission)
                    {
                        // Tail of the burst that the previous re-grant abandoned, still
                        // in flight when the fresh CTS went out. Discarding it costs
                        // nothing - the sender is already resending from the packet we
                        // asked for - and it must not spend a recovery attempt, or one
                        // lost packet in a 16-packet window would burn the whole budget.
                        _stalePacketsDiscarded++;
                        return;
                    }

                    if (!_passive && _sequenceGapRecoveries < MAX_SEQUENCE_GAP_RECOVERIES)
                    {
                        // J1939-21 recovery for a lost TP.DT is a new TP.CM_CTS naming
                        // the first packet still missing; the sender resumes there.
                        // Conn_Abort is NOT that mechanism - it TERMINATES the
                        // connection, which is exactly how the transmit side of this
                        // file treats an abort it receives - so aborting here would
                        // discard every packet already reassembled over one frame that
                        // the adapter dropped.
                        _sequenceGapRecoveries++;
                        _awaitingRetransmission = true;

                        AddNote(string.Format(CultureInfo.InvariantCulture,
                            "packet {0} was lost (packet {1} arrived instead); re-granted from packet {0} (attempt {2} of {3})",
                            _nextExpectedSequence, sequence, _sequenceGapRecoveries,
                            MAX_SEQUENCE_GAP_RECOVERIES));

                        // Recomputes the grant from PacketsReceived + 1, which is the
                        // first missing packet while reception is contiguous, and
                        // restarts the T2 wait for the retransmission.
                        SendClearToSend();
                        return;
                    }

                    string gapContext = _passive
                        ? "this session is monitored, so no re-grant could be sent"
                        : string.Format(CultureInfo.InvariantCulture,
                            "{0} re-grant(s) from packet {1} did not recover it",
                            _sequenceGapRecoveries, PacketsReceived + 1);

                    Fail(J1939TransportFault.SequenceGap,
                        _passive
                            ? J1939ConnectionAbortReason.BadSequenceNumber
                            : J1939ConnectionAbortReason.MaximumRetransmitLimitReached,
                        J1939TimeoutDetection.NotApplicable, _nextExpectedSequence, sequence, 0.0, 0,
                        string.Format(CultureInfo.InvariantCulture,
                            "Connection-mode transfer of {0} from 0x{1:X2}: expected packet {2}, received packet {3}. {4} packet(s) lost and {5}. The transfer is abandoned.",
                            J1939Pgn.Describe(Pgn), SourceAddress, _nextExpectedSequence, sequence,
                            sequence - _nextExpectedSequence, gapContext), !_passive);
                    return;
                }

                bool duplicateIdentical;
                if (!StoreSegment(sequence - 1, message.Data, out duplicateIdentical))
                {
                    Fail(J1939TransportFault.DuplicateSequenceConflict,
                        J1939ConnectionAbortReason.DuplicateSequenceNumber,
                        J1939TimeoutDetection.NotApplicable, _nextExpectedSequence, sequence, 0.0, 0,
                        string.Format(CultureInfo.InvariantCulture,
                            "Connection-mode transfer of {0} from 0x{1:X2}: packet {2} arrived twice with different data.",
                            J1939Pgn.Describe(Pgn), SourceAddress, sequence), !_passive);
                    return;
                }

                if (_awaitingRetransmission)
                {
                    // The re-granted packet arrived; the connection is back in step.
                    _awaitingRetransmission = false;

                    if (_stalePacketsDiscarded > 0)
                    {
                        AddNote(string.Format(CultureInfo.InvariantCulture,
                            "{0} packet(s) of the abandoned burst arrived after the re-grant and were discarded",
                            _stalePacketsDiscarded));
                        _stalePacketsDiscarded = 0;
                    }
                }

                _nextExpectedSequence++;
                _state = ConnectionRxState.AwaitingNextPacketOfBurst;

                if (_burstRemaining > 0)
                {
                    _burstRemaining--;
                }

                if (PacketsReceived == TotalPackets)
                {
                    if (_duplicatesAbsorbed > 0)
                    {
                        AddNote(string.Format(CultureInfo.InvariantCulture,
                            "{0} duplicate packet(s) absorbed (identical data)", _duplicatesAbsorbed));
                    }

                    if (!_passive)
                    {
                        SendEndOfMessageAck();
                    }

                    Complete();
                    return;
                }

                if (_burstRemaining == 0 && !_passive)
                {
                    SendClearToSend();
                }
            }

            public override void CheckTimeouts(double nowMs)
            {
                int limit = _state == ConnectionRxState.AwaitingFirstPacketOfBurst
                    ? J1939Pgn.ConnectionTimeoutT2Ms
                    : J1939Pgn.ConnectionTimeoutT1Ms;

                J1939TransportFault fault = _state == ConnectionRxState.AwaitingFirstPacketOfBurst
                    ? J1939TransportFault.TimeoutT2
                    : J1939TransportFault.TimeoutT1;

                double idle = nowMs - LastActivityMs;
                if (idle <= limit)
                {
                    return;
                }

                string phase = _state == ConnectionRxState.AwaitingFirstPacketOfBurst
                    ? "no data packet followed the clear-to-send"
                    : string.Format(CultureInfo.InvariantCulture,
                        "the sender stopped after packet {0} with {1} still granted",
                        _nextExpectedSequence - 1, _burstRemaining);

                Fail(fault, J1939ConnectionAbortReason.Timeout, J1939TimeoutDetection.HostTick,
                    _nextExpectedSequence, -1, idle, limit,
                    string.Format(CultureInfo.InvariantCulture,
                        "Connection-mode transfer of {0} from 0x{1:X2} timed out: {2} within {3} ({4} ms). {5} of {6} packets received. Detected by the host tick watchdog.",
                        J1939Pgn.Describe(Pgn), SourceAddress, phase, fault, limit,
                        PacketsReceived, TotalPackets), !_passive);
            }

            public override string Describe(double nowMs)
            {
                string state = _state == ConnectionRxState.AwaitingFirstPacketOfBurst
                    ? string.Format(CultureInfo.InvariantCulture,
                        "granted {0}, awaiting TP.DT {1}", _burstRemaining, _nextExpectedSequence)
                    : string.Format(CultureInfo.InvariantCulture,
                        "awaiting TP.DT {0}, {1} left in burst", _nextExpectedSequence, _burstRemaining);

                if (_passive)
                {
                    state = "monitoring, " + state;
                }

                return DescribeCommon(state, nowMs);
            }
        }

        // =====================================================================
        //  STATE MACHINE 3 of 3 - ETP reassembly
        //
        //  The extra machinery over RTS/CTS is the data packet offset. ETP.DT still
        //  numbers packets 1..255 in one byte, so an ETP.CM_DPO frame precedes each
        //  burst and says which absolute packet the next sequence number 1 refers
        //  to. Absolute packet number = offset + sequence number; the byte position
        //  is (offset + sequence - 1) * 7. Get that wrong and the payload assembles
        //  into a shifted, plausible-looking mess.
        //
        //  Total message size is a 32-bit field, which is exactly why the buffer is
        //  only allocated after the size is checked against MaxInboundMessageBytes.
        // =====================================================================

        private sealed class EtpInboundSession : InboundSession
        {
            private enum EtpRxState
            {
                AwaitingDataPacketOffset,
                AwaitingFirstPacketOfBurst,
                AwaitingNextPacketOfBurst
            }

            private readonly bool _passive;

            private EtpRxState _state = EtpRxState.AwaitingDataPacketOffset;
            private int _grantedPackets;
            private int _burstRemaining;
            private int _packetOffset;
            private int _expectedSequenceInBurst = 1;
            private int _duplicatesAbsorbed;
            private int _sequenceGapRecoveries;
            private int _stalePacketsDiscarded;
            private bool _awaitingRegrantOffset;

            public EtpInboundSession(J1939TransportProtocol owner, J1939Message announcement,
                uint pgn, uint declaredPgn, int totalBytes, int totalPackets)
                : base(owner, announcement, pgn, declaredPgn, totalBytes, totalPackets)
            {
                _passive = announcement.DestinationAddress != owner._sourceAddress;
            }

            public override J1939TransportKind Kind
            {
                get { return J1939TransportKind.ExtendedConnectionMode; }
            }

            public void Start()
            {
                if (_passive)
                {
                    AddNote("monitored session: this tool is not the destination and sent no flow control");
                    _grantedPackets = J1939Pgn.MaxTransportProtocolPackets;
                    return;
                }

                SendClearToSend();
            }

            private void SendClearToSend()
            {
                int remaining = TotalPackets - PacketsReceived;
                int grant = Owner._receiveWindowPackets;

                if (grant > J1939Pgn.MaxTransportProtocolPackets)
                {
                    grant = J1939Pgn.MaxTransportProtocolPackets;
                }

                if (grant > remaining)
                {
                    grant = remaining;
                }

                if (grant < 1)
                {
                    grant = 1;
                }

                _grantedPackets = grant;
                _state = EtpRxState.AwaitingDataPacketOffset;

                int nextPacket = PacketsReceived + 1;

                byte[] data = new byte[8];
                data[0] = EtpCmCts;
                data[1] = (byte)grant;
                data[2] = (byte)(nextPacket & 0xFF);
                data[3] = (byte)((nextPacket >> 8) & 0xFF);
                data[4] = (byte)((nextPacket >> 16) & 0xFF);
                data[5] = (byte)(Pgn & 0xFFu);
                data[6] = (byte)((Pgn >> 8) & 0xFFu);
                data[7] = (byte)((Pgn >> 16) & 0xFFu);

                Owner.EnqueueFrame(Owner.BuildEtpCm(data, SourceAddress), FaultTag);
            }

            private void SendEndOfMessageAck()
            {
                byte[] data = new byte[8];
                data[0] = EtpCmEndOfMsgAck;
                data[1] = (byte)(TotalBytes & 0xFF);
                data[2] = (byte)((TotalBytes >> 8) & 0xFF);
                data[3] = (byte)((TotalBytes >> 16) & 0xFF);
                data[4] = (byte)((TotalBytes >> 24) & 0xFF);
                data[5] = (byte)(Pgn & 0xFFu);
                data[6] = (byte)((Pgn >> 8) & 0xFFu);
                data[7] = (byte)((Pgn >> 16) & 0xFFu);

                Owner.EnqueueFrame(Owner.BuildEtpCm(data, SourceAddress), FaultTag);
            }

            public void HandleDataPacketOffset(J1939Message message)
            {
                bool adapterMeasured;
                double gap = MeasureGap(message, out adapterMeasured);

                if (_state != EtpRxState.AwaitingDataPacketOffset)
                {
                    Fail(J1939TransportFault.InvalidFlowControl,
                        J1939ConnectionAbortReason.UnexpectedCtsDuringTransfer,
                        J1939TimeoutDetection.NotApplicable, -1, -1, 0.0, 0,
                        string.Format(CultureInfo.InvariantCulture,
                            "ETP transfer of {0} from 0x{1:X2}: a data packet offset arrived mid-burst, with {2} packet(s) of the current burst still outstanding.",
                            J1939Pgn.Describe(Pgn), SourceAddress, _burstRemaining), !_passive);
                    return;
                }

                if (gap > J1939Pgn.ConnectionTimeoutT2Ms)
                {
                    Fail(J1939TransportFault.TimeoutT2, J1939ConnectionAbortReason.Timeout,
                        adapterMeasured ? J1939TimeoutDetection.AdapterTimestamp : J1939TimeoutDetection.HostTick,
                        -1, -1, gap, J1939Pgn.ConnectionTimeoutT2Ms,
                        string.Format(CultureInfo.InvariantCulture,
                            "ETP transfer of {0} from 0x{1:X2}: {2:F0} ms to the data packet offset exceeds T2 ({3} ms).",
                            J1939Pgn.Describe(Pgn), SourceAddress, gap, J1939Pgn.ConnectionTimeoutT2Ms), !_passive);
                    return;
                }

                MarkActivity(message);

                byte[] data = message.Data;
                int packets = data[1];
                int offset = data[2] | (data[3] << 8) | (data[4] << 16);

                if (packets < 1 || packets > _grantedPackets ||
                    offset < 0 || offset + packets > TotalPackets ||
                    offset != PacketsReceived)
                {
                    Fail(J1939TransportFault.InvalidFlowControl,
                        J1939ConnectionAbortReason.Unspecified,
                        J1939TimeoutDetection.NotApplicable, PacketsReceived + 1, offset + 1, 0.0, 0,
                        string.Format(CultureInfo.InvariantCulture,
                            "ETP transfer of {0} from 0x{1:X2}: data packet offset {2} for {3} packet(s) does not fit the session. Expected offset {4}, at most {5} packet(s), and a total of {6}.",
                            J1939Pgn.Describe(Pgn), SourceAddress, offset, packets,
                            PacketsReceived, _grantedPackets, TotalPackets), !_passive);
                    return;
                }

                _packetOffset = offset;
                _burstRemaining = packets;
                _expectedSequenceInBurst = 1;
                _state = EtpRxState.AwaitingFirstPacketOfBurst;

                if (_awaitingRegrantOffset)
                {
                    // The sender accepted the re-grant and restarted the burst at the
                    // packet we asked for.
                    _awaitingRegrantOffset = false;

                    if (_stalePacketsDiscarded > 0)
                    {
                        AddNote(string.Format(CultureInfo.InvariantCulture,
                            "{0} packet(s) of the abandoned burst arrived after the re-grant and were discarded",
                            _stalePacketsDiscarded));
                        _stalePacketsDiscarded = 0;
                    }
                }
            }

            public void HandleDataTransfer(J1939Message message)
            {
                if (_state == EtpRxState.AwaitingDataPacketOffset)
                {
                    if (_awaitingRegrantOffset)
                    {
                        // Tail of the burst the re-grant abandoned, still in flight when
                        // the fresh CTS went out. Without a matching DPO these packets
                        // cannot be placed, so they are counted and dropped rather than
                        // killing a transfer that is already recovering. LastActivityMs
                        // is deliberately not refreshed here: a sender that never sends
                        // the new DPO must still trip T2 on schedule.
                        _stalePacketsDiscarded++;
                        return;
                    }

                    Fail(J1939TransportFault.UnexpectedDataPacket,
                        J1939ConnectionAbortReason.UnexpectedDataTransferPacket,
                        J1939TimeoutDetection.NotApplicable, -1, message.Data[0], 0.0, 0,
                        string.Format(CultureInfo.InvariantCulture,
                            "ETP transfer of {0} from 0x{1:X2}: data packet {2} arrived before the data packet offset that says which absolute packet it is. Without the offset the payload cannot be placed.",
                            J1939Pgn.Describe(Pgn), SourceAddress, message.Data[0]), !_passive);
                    return;
                }

                int limit = _state == EtpRxState.AwaitingFirstPacketOfBurst
                    ? J1939Pgn.ConnectionTimeoutT2Ms
                    : J1939Pgn.ConnectionTimeoutT1Ms;

                J1939TransportFault timeoutFault = _state == EtpRxState.AwaitingFirstPacketOfBurst
                    ? J1939TransportFault.TimeoutT2
                    : J1939TransportFault.TimeoutT1;

                bool adapterMeasured;
                double gap = MeasureGap(message, out adapterMeasured);

                if (gap > limit)
                {
                    Fail(timeoutFault, J1939ConnectionAbortReason.Timeout,
                        adapterMeasured ? J1939TimeoutDetection.AdapterTimestamp : J1939TimeoutDetection.HostTick,
                        _packetOffset + _expectedSequenceInBurst, _packetOffset + message.Data[0], gap, limit,
                        string.Format(CultureInfo.InvariantCulture,
                            "ETP transfer of {0} from 0x{1:X2}: {2:F0} ms between packets exceeds {3} ({4} ms) at packet {5} of {6}.",
                            J1939Pgn.Describe(Pgn), SourceAddress, gap, timeoutFault, limit,
                            PacketsReceived, TotalPackets), !_passive);
                    return;
                }

                MarkActivity(message);

                int sequence = message.Data[0];

                if (sequence != _expectedSequenceInBurst)
                {
                    if (!_passive && _sequenceGapRecoveries < MAX_SEQUENCE_GAP_RECOVERIES)
                    {
                        // Same J1939-21 recovery as standard TP: re-grant the burst from
                        // the first packet still missing instead of aborting. For ETP
                        // that means a fresh ETP.CM_CTS, which the sender answers with a
                        // new ETP.CM_DPO at offset PacketsReceived.
                        _sequenceGapRecoveries++;
                        _awaitingRegrantOffset = true;

                        AddNote(string.Format(CultureInfo.InvariantCulture,
                            "burst sequence {0} (absolute packet {1}) was lost (sequence {2} arrived instead); re-granted from absolute packet {1} (attempt {3} of {4})",
                            _expectedSequenceInBurst, _packetOffset + _expectedSequenceInBurst,
                            sequence, _sequenceGapRecoveries, MAX_SEQUENCE_GAP_RECOVERIES));

                        // Re-grants from PacketsReceived + 1 and restarts the T2 wait for
                        // the new data packet offset.
                        SendClearToSend();
                        return;
                    }

                    string gapContext = _passive
                        ? "this session is monitored, so no re-grant could be sent"
                        : string.Format(CultureInfo.InvariantCulture,
                            "{0} re-grant(s) from absolute packet {1} did not recover it",
                            _sequenceGapRecoveries, PacketsReceived + 1);

                    Fail(J1939TransportFault.SequenceGap,
                        _passive
                            ? J1939ConnectionAbortReason.BadSequenceNumber
                            : J1939ConnectionAbortReason.MaximumRetransmitLimitReached,
                        J1939TimeoutDetection.NotApplicable,
                        _packetOffset + _expectedSequenceInBurst, _packetOffset + sequence, 0.0, 0,
                        string.Format(CultureInfo.InvariantCulture,
                            "ETP transfer of {0} from 0x{1:X2}: expected burst sequence {2} (absolute packet {3}), received {4}, and {5}. The transfer is abandoned.",
                            J1939Pgn.Describe(Pgn), SourceAddress, _expectedSequenceInBurst,
                            _packetOffset + _expectedSequenceInBurst, sequence, gapContext), !_passive);
                    return;
                }

                // Absolute packet number = offset + sequence. This is the whole point of
                // the DPO frame and the one place ETP differs materially from TP.
                int absolutePacket = _packetOffset + sequence;

                if (absolutePacket < 1 || absolutePacket > TotalPackets)
                {
                    Fail(J1939TransportFault.SequenceGap, J1939ConnectionAbortReason.BadSequenceNumber,
                        J1939TimeoutDetection.NotApplicable, PacketsReceived + 1, absolutePacket, 0.0, 0,
                        string.Format(CultureInfo.InvariantCulture,
                            "ETP transfer of {0} from 0x{1:X2}: offset {2} plus sequence {3} addresses packet {4}, outside 1..{5}.",
                            J1939Pgn.Describe(Pgn), SourceAddress, _packetOffset, sequence,
                            absolutePacket, TotalPackets), !_passive);
                    return;
                }

                bool duplicateIdentical;
                if (!StoreSegment(absolutePacket - 1, message.Data, out duplicateIdentical))
                {
                    Fail(J1939TransportFault.DuplicateSequenceConflict,
                        J1939ConnectionAbortReason.DuplicateSequenceNumber,
                        J1939TimeoutDetection.NotApplicable, PacketsReceived + 1, absolutePacket, 0.0, 0,
                        string.Format(CultureInfo.InvariantCulture,
                            "ETP transfer of {0} from 0x{1:X2}: absolute packet {2} arrived twice with different data.",
                            J1939Pgn.Describe(Pgn), SourceAddress, absolutePacket), !_passive);
                    return;
                }

                if (duplicateIdentical)
                {
                    _duplicatesAbsorbed++;
                }

                _expectedSequenceInBurst++;
                _state = EtpRxState.AwaitingNextPacketOfBurst;

                if (_burstRemaining > 0)
                {
                    _burstRemaining--;
                }

                if (PacketsReceived == TotalPackets)
                {
                    if (_duplicatesAbsorbed > 0)
                    {
                        AddNote(string.Format(CultureInfo.InvariantCulture,
                            "{0} duplicate packet(s) absorbed (identical data)", _duplicatesAbsorbed));
                    }

                    if (!_passive)
                    {
                        SendEndOfMessageAck();
                    }

                    Complete();
                    return;
                }

                if (_burstRemaining == 0)
                {
                    if (_passive)
                    {
                        _state = EtpRxState.AwaitingDataPacketOffset;
                    }
                    else
                    {
                        SendClearToSend();
                    }
                }
            }

            public override void CheckTimeouts(double nowMs)
            {
                int limit = _state == EtpRxState.AwaitingNextPacketOfBurst
                    ? J1939Pgn.ConnectionTimeoutT1Ms
                    : J1939Pgn.ConnectionTimeoutT2Ms;

                J1939TransportFault fault = _state == EtpRxState.AwaitingNextPacketOfBurst
                    ? J1939TransportFault.TimeoutT1
                    : J1939TransportFault.TimeoutT2;

                double idle = nowMs - LastActivityMs;
                if (idle <= limit)
                {
                    return;
                }

                string phase;
                switch (_state)
                {
                    case EtpRxState.AwaitingDataPacketOffset:
                        phase = "no data packet offset followed the clear-to-send";
                        break;
                    case EtpRxState.AwaitingFirstPacketOfBurst:
                        phase = "no data packet followed the data packet offset";
                        break;
                    default:
                        phase = string.Format(CultureInfo.InvariantCulture,
                            "the sender stopped with {0} packet(s) of the burst outstanding", _burstRemaining);
                        break;
                }

                Fail(fault, J1939ConnectionAbortReason.Timeout, J1939TimeoutDetection.HostTick,
                    PacketsReceived + 1, -1, idle, limit,
                    string.Format(CultureInfo.InvariantCulture,
                        "ETP transfer of {0} from 0x{1:X2} timed out: {2} within {3} ({4} ms). {5} of {6} packets received. Detected by the host tick watchdog.",
                        J1939Pgn.Describe(Pgn), SourceAddress, phase, fault, limit,
                        PacketsReceived, TotalPackets), !_passive);
            }

            public override string Describe(double nowMs)
            {
                string state;
                switch (_state)
                {
                    case EtpRxState.AwaitingDataPacketOffset:
                        state = string.Format(CultureInfo.InvariantCulture,
                            "granted {0}, awaiting ETP.CM_DPO", _grantedPackets);
                        break;
                    case EtpRxState.AwaitingFirstPacketOfBurst:
                        state = string.Format(CultureInfo.InvariantCulture,
                            "offset {0}, awaiting first ETP.DT of {1}", _packetOffset, _burstRemaining);
                        break;
                    default:
                        state = string.Format(CultureInfo.InvariantCulture,
                            "offset {0}, awaiting ETP.DT {1}, {2} left in burst",
                            _packetOffset, _expectedSequenceInBurst, _burstRemaining);
                        break;
                }

                if (_passive)
                {
                    state = "monitoring, " + state;
                }

                return DescribeCommon(state, nowMs);
            }
        }

        // =====================================================================
        //  Outbound session base. As on the receive side this holds only the
        //  payload, the pacing clock and the reporting plumbing; the three
        //  mechanisms below implement their own transmit state machines.
        // =====================================================================

        private abstract class OutboundSession
        {
            protected readonly J1939TransportProtocol Owner;
            protected readonly byte Priority;
            protected readonly byte[] Payload;
            protected readonly int TotalPackets;
            protected readonly double StartedMs;

            protected int PacketsSent;
            protected double LastActivityMs;
            protected double NextDataDueMs;

            private readonly List<string> _notes = new List<string>();
            private int _suppressedNotes;

            protected OutboundSession(J1939TransportProtocol owner, PendingSend request)
            {
                Owner = owner;
                TicketId = request.TicketId;
                Priority = request.Priority;
                Pgn = request.Pgn;
                DestinationAddress = request.DestinationAddress;
                Payload = request.Payload;

                TotalPackets = (Payload.Length + J1939Pgn.TransportProtocolBytesPerPacket - 1)
                               / J1939Pgn.TransportProtocolBytesPerPacket;

                StartedMs = owner._hostNowMs;
                LastActivityMs = owner._hostNowMs;
            }

            public long TicketId { get; private set; }

            public uint Pgn { get; private set; }

            public byte DestinationAddress { get; private set; }

            public bool Finished { get; protected set; }

            public abstract J1939TransportKind Kind { get; }

            // BAM has no connection and therefore no abort frame. Sending one would
            // be a protocol violation, and nothing is listening for it anyway.
            protected abstract bool UsesConnectionAbort { get; }

            // True when this transfer can do nothing until the peer speaks. Used to
            // decide which direction an abort with an unusable or non-matching PGN
            // belongs to. False here covers BAM, which has no back-channel and so is
            // never waiting on anybody.
            public virtual bool AwaitingPeerResponse
            {
                get { return false; }
            }

            public abstract void Start(double nowMs);

            public abstract void OnTick(double nowMs);

            public abstract string Describe(double nowMs);

            public virtual void HandleFlowControl(J1939Message message, byte control, bool extended)
            {
                Fail(J1939TransportFault.InvalidFlowControl, J1939ConnectionAbortReason.Unspecified,
                    J1939TimeoutDetection.NotApplicable, 0.0, 0,
                    string.Format(CultureInfo.InvariantCulture,
                        "Node 0x{0:X2} sent flow control 0x{1:X2} for {2}, which this transfer does not use.",
                        message.SourceAddress, control, J1939Pgn.Describe(Pgn)));
            }

            public void AddNote(string note)
            {
                if (_notes.Count >= MaxNotesPerSession)
                {
                    _suppressedNotes++;
                    return;
                }

                _notes.Add(note);
            }

            protected string NotesText
            {
                get
                {
                    if (_notes.Count == 0)
                    {
                        return string.Empty;
                    }

                    string joined = string.Join("; ", _notes.ToArray());
                    if (_suppressedNotes > 0)
                    {
                        joined = string.Format(CultureInfo.InvariantCulture,
                            "{0}; and {1} further anomalies not listed", joined, _suppressedNotes);
                    }

                    return joined;
                }
            }

            protected void EmitDataFrame(int sequenceNumber, int packetIndexZeroBased, bool extended)
            {
                byte[] data = BuildDataFrame(sequenceNumber, Payload,
                    packetIndexZeroBased * J1939Pgn.TransportProtocolBytesPerPacket);

                J1939Message frame = extended
                    ? Owner.BuildEtpDt(data, DestinationAddress)
                    : Owner.BuildTpDt(data, DestinationAddress);

                Owner.EnqueueFrame(frame, TicketId);
                PacketsSent++;
            }

            // Measures how long the peer took to answer, using adapter timestamps when
            // the frame carries one. Tr is an obligation on the responder, so a peer
            // that answers slower than Tr but faster than T3 is not a failure - it is
            // a sick ECU, and the technician should be told.
            protected void NotePeerResponseTime(J1939Message message)
            {
                double elapsed = Owner._hostNowMs - LastActivityMs;
                if (elapsed > J1939Pgn.ResponseTimeTrMs)
                {
                    AddNote(string.Format(CultureInfo.InvariantCulture,
                        "node 0x{0:X2} took about {1:F0} ms to answer (Tr is {2} ms)",
                        message.SourceAddress, elapsed, J1939Pgn.ResponseTimeTrMs));
                }

                if (message.TimestampSource != J1939TimestampSource.Adapter)
                {
                    Owner._timingDegraded = true;
                }
            }

            protected void Complete()
            {
                byte pduFormat = (byte)((Pgn >> 8) & 0xFFu);
                byte encodedDestination = pduFormat >= J1939Id.PDU2_PF_MINIMUM
                    ? J1939Pgn.GlobalAddress
                    : DestinationAddress;

                J1939Id id = J1939Id.Create(Priority, Pgn, Owner._sourceAddress, encodedDestination);
                J1939Message assembled = J1939Message.FromHostClock(id, Payload);

                Owner.EnqueueNotification(new J1939TransportMessageEventArgs(
                    J1939TransportDirection.Outbound, Kind, assembled, Owner._sourceAddress,
                    DestinationAddress, Pgn, TotalPackets, Owner._hostNowMs - StartedMs,
                    Owner._timingDegraded, NotesText));

                Finished = true;
            }

            protected void Fail(J1939TransportFault fault, J1939ConnectionAbortReason abortReason,
                J1939TimeoutDetection detection, double elapsedMs, int limitMs, string description)
            {
                if (Finished)
                {
                    return;
                }

                Finished = true;

                if (UsesConnectionAbort && abortReason != J1939ConnectionAbortReason.None)
                {
                    Owner.EnqueueFrame(Owner.BuildAbort(DestinationAddress, Pgn, abortReason,
                        Kind == J1939TransportKind.ExtendedConnectionMode), 0);
                }

                string notes = NotesText;
                string full = notes.Length == 0
                    ? description
                    : string.Format(CultureInfo.InvariantCulture, "{0} Earlier anomalies: {1}.", description, notes);

                int bytesSent = PacketsSent * J1939Pgn.TransportProtocolBytesPerPacket;
                if (bytesSent > Payload.Length)
                {
                    bytesSent = Payload.Length;
                }

                Owner.EnqueueNotification(new J1939TransportFaultEventArgs(
                    J1939TransportDirection.Outbound, Kind, fault, abortReason, detection, Pgn,
                    Owner._sourceAddress, DestinationAddress, bytesSent, Payload.Length,
                    PacketsSent, TotalPackets, -1, -1, elapsedMs, limitMs,
                    Owner._timingDegraded, null, full));
            }

            public void FailAbortReceived(J1939ConnectionAbortReason reason)
            {
                Fail(J1939TransportFault.AbortReceived, reason, J1939TimeoutDetection.NotApplicable, 0.0, 0,
                    string.Format(CultureInfo.InvariantCulture,
                        "Node 0x{0:X2} aborted reception of {1} with reason {2} = {3} after {4} of {5} packets were sent.",
                        DestinationAddress, J1939Pgn.Describe(Pgn), (byte)reason, reason,
                        PacketsSent, TotalPackets));
            }

            public void FailOnReset()
            {
                Fail(J1939TransportFault.StackReset, J1939ConnectionAbortReason.None,
                    J1939TimeoutDetection.NotApplicable, 0.0, 0,
                    string.Format(CultureInfo.InvariantCulture,
                        "Transport reset while sending {0} to 0x{1:X2} ({2} of {3} packets sent).",
                        J1939Pgn.Describe(Pgn), DestinationAddress, PacketsSent, TotalPackets));
            }

            public void FailTransmit(string detail)
            {
                // No abort frame: the sink that just failed is the same sink the abort
                // would have to travel through.
                Fail(J1939TransportFault.TransmitFailed, J1939ConnectionAbortReason.None,
                    J1939TimeoutDetection.NotApplicable, 0.0, 0,
                    string.Format(CultureInfo.InvariantCulture,
                        "Could not transmit {0} to 0x{1:X2}: {2}. {3} of {4} packets had been sent.",
                        J1939Pgn.Describe(Pgn), DestinationAddress, detail, PacketsSent, TotalPackets));
            }

            public void CancelLocally()
            {
                Fail(J1939TransportFault.LocallyCancelled,
                    UsesConnectionAbort ? J1939ConnectionAbortReason.SystemResourcesReclaimed
                                        : J1939ConnectionAbortReason.None,
                    J1939TimeoutDetection.NotApplicable, 0.0, 0,
                    string.Format(CultureInfo.InvariantCulture,
                        "Cancelled sending {0} to 0x{1:X2} after {2} of {3} packets.",
                        J1939Pgn.Describe(Pgn), DestinationAddress, PacketsSent, TotalPackets));
            }

            protected string DescribeCommon(string state, double nowMs)
            {
                return string.Format(CultureInfo.InvariantCulture,
                    "OUT {0,-14} DA 0x{1:X2} {2} {3}/{4} packets {5} bytes, {6}, idle {7:F0} ms",
                    Kind, DestinationAddress, J1939Pgn.Describe(Pgn), PacketsSent, TotalPackets,
                    Payload.Length, state, nowMs - LastActivityMs);
            }
        }

        // =====================================================================
        //  BAM transmit. Announce, then push. Nothing acknowledges anything, so
        //  the only obligation is the inter-packet spacing: a receiver that
        //  cannot keep up has no way to tell us, and 50 ms is what the standard
        //  gives it to cope.
        // =====================================================================

        private sealed class BamOutboundSession : OutboundSession
        {
            private enum BamTxState
            {
                Announcing,
                SendingData,
                Done
            }

            private BamTxState _state = BamTxState.Announcing;
            private int _nextSequence = 1;

            public BamOutboundSession(J1939TransportProtocol owner, PendingSend request)
                : base(owner, request)
            {
            }

            public override J1939TransportKind Kind
            {
                get { return J1939TransportKind.Bam; }
            }

            protected override bool UsesConnectionAbort
            {
                get { return false; }
            }

            public override void Start(double nowMs)
            {
                byte[] data = new byte[8];
                data[0] = J1939Pgn.TpCmBam;
                data[1] = (byte)(Payload.Length & 0xFF);
                data[2] = (byte)((Payload.Length >> 8) & 0xFF);
                data[3] = (byte)TotalPackets;
                data[4] = 0xFF;
                data[5] = (byte)(Pgn & 0xFFu);
                data[6] = (byte)((Pgn >> 8) & 0xFFu);
                data[7] = (byte)((Pgn >> 16) & 0xFFu);

                Owner.EnqueueFrame(Owner.BuildTpCm(data, J1939Pgn.GlobalAddress), TicketId);

                _state = BamTxState.SendingData;
                LastActivityMs = nowMs;
                NextDataDueMs = nowMs + Owner._interPacketDelayMs;
            }

            public override void OnTick(double nowMs)
            {
                if (_state != BamTxState.SendingData || nowMs < NextDataDueMs)
                {
                    return;
                }

                EmitDataFrame(_nextSequence, _nextSequence - 1, false);
                LastActivityMs = nowMs;
                NextDataDueMs = nowMs + Owner._interPacketDelayMs;
                _nextSequence++;

                if (_nextSequence > TotalPackets)
                {
                    _state = BamTxState.Done;
                    Complete();
                }
            }

            public override string Describe(double nowMs)
            {
                string state = _state == BamTxState.Announcing
                    ? "announcing"
                    : string.Format(CultureInfo.InvariantCulture, "sending TP.DT {0}", _nextSequence);

                return DescribeCommon(state, nowMs);
            }
        }

        // =====================================================================
        //  RTS/CTS transmit. The receiver decides the pace, the window and the
        //  restart point; we obey it, and we never send faster than the
        //  inter-packet delay even when the window would allow it.
        // =====================================================================

        private sealed class ConnectionOutboundSession : OutboundSession
        {
            private enum ConnectionTxState
            {
                AwaitingClearToSend,
                SendingBurst,
                AwaitingEndOfMsgAck,
                HoldingConnection
            }

            private ConnectionTxState _state = ConnectionTxState.AwaitingClearToSend;
            private int _nextSequence = 1;
            private int _burstRemaining;
            private int _highestSequenceSent;
            private double _holdStartedMs;
            private bool _holdWarned;

            public ConnectionOutboundSession(J1939TransportProtocol owner, PendingSend request)
                : base(owner, request)
            {
            }

            public override J1939TransportKind Kind
            {
                get { return J1939TransportKind.ConnectionMode; }
            }

            protected override bool UsesConnectionAbort
            {
                get { return true; }
            }

            public override bool AwaitingPeerResponse
            {
                get
                {
                    return _state == ConnectionTxState.AwaitingClearToSend
                           || _state == ConnectionTxState.AwaitingEndOfMsgAck
                           || _state == ConnectionTxState.HoldingConnection;
                }
            }

            public override void Start(double nowMs)
            {
                byte[] data = new byte[8];
                data[0] = J1939Pgn.TpCmRts;
                data[1] = (byte)(Payload.Length & 0xFF);
                data[2] = (byte)((Payload.Length >> 8) & 0xFF);
                data[3] = (byte)TotalPackets;
                data[4] = 0xFF;
                data[5] = (byte)(Pgn & 0xFFu);
                data[6] = (byte)((Pgn >> 8) & 0xFFu);
                data[7] = (byte)((Pgn >> 16) & 0xFFu);

                Owner.EnqueueFrame(Owner.BuildTpCm(data, DestinationAddress), TicketId);

                _state = ConnectionTxState.AwaitingClearToSend;
                LastActivityMs = nowMs;
            }

            public override void HandleFlowControl(J1939Message message, byte control, bool extended)
            {
                if (extended)
                {
                    Fail(J1939TransportFault.InvalidFlowControl, J1939ConnectionAbortReason.Unspecified,
                        J1939TimeoutDetection.NotApplicable, 0.0, 0,
                        string.Format(CultureInfo.InvariantCulture,
                            "Node 0x{0:X2} answered a standard RTS for {1} with extended transport flow control 0x{2:X2}. The two mechanisms cannot be mixed within one connection.",
                            message.SourceAddress, J1939Pgn.Describe(Pgn), control));
                    return;
                }

                NotePeerResponseTime(message);

                if (control == J1939Pgn.TpCmCts)
                {
                    HandleClearToSend(message);
                    return;
                }

                if (control == J1939Pgn.TpCmEndOfMsgAck)
                {
                    HandleEndOfMessageAck(message);
                    return;
                }

                base.HandleFlowControl(message, control, extended);
            }

            private void HandleClearToSend(J1939Message message)
            {
                if (_state == ConnectionTxState.SendingBurst)
                {
                    // J1939-21 reason 4 exists precisely for this: a CTS arriving while
                    // the previously granted burst is still going.
                    Fail(J1939TransportFault.InvalidFlowControl,
                        J1939ConnectionAbortReason.UnexpectedCtsDuringTransfer,
                        J1939TimeoutDetection.NotApplicable, 0.0, 0,
                        string.Format(CultureInfo.InvariantCulture,
                            "Node 0x{0:X2} sent a clear-to-send for {1} while {2} packet(s) of the previous grant were still going out.",
                            message.SourceAddress, J1939Pgn.Describe(Pgn), _burstRemaining));
                    return;
                }

                int granted = message.Data[1];
                int nextSequence = message.Data[2];

                if (granted == 0)
                {
                    // Hold connection open. The receiver is busy and will send a real
                    // grant shortly; T4 bounds how long we wait, Th is when it stops
                    // looking healthy.
                    _state = ConnectionTxState.HoldingConnection;
                    _holdStartedMs = Owner._hostNowMs;
                    _holdWarned = false;
                    LastActivityMs = Owner._hostNowMs;
                    return;
                }

                if (nextSequence < 1 || nextSequence > TotalPackets ||
                    nextSequence + granted - 1 > TotalPackets)
                {
                    Fail(J1939TransportFault.InvalidFlowControl, J1939ConnectionAbortReason.BadSequenceNumber,
                        J1939TimeoutDetection.NotApplicable, 0.0, 0,
                        string.Format(CultureInfo.InvariantCulture,
                            "Node 0x{0:X2} asked for {1} packet(s) starting at {2} for {3}, which runs past the announced total of {4}.",
                            message.SourceAddress, granted, nextSequence, J1939Pgn.Describe(Pgn), TotalPackets));
                    return;
                }

                if (nextSequence <= _highestSequenceSent)
                {
                    AddNote(string.Format(CultureInfo.InvariantCulture,
                        "node 0x{0:X2} requested retransmission from packet {1} (had already received up to {2})",
                        message.SourceAddress, nextSequence, _highestSequenceSent));
                }

                _nextSequence = nextSequence;
                _burstRemaining = granted;
                _state = ConnectionTxState.SendingBurst;
                LastActivityMs = Owner._hostNowMs;

                // First packet of a granted burst goes out on the next tick; spacing
                // applies between data frames, not between the CTS and the first one.
                NextDataDueMs = Owner._hostNowMs;
            }

            private void HandleEndOfMessageAck(J1939Message message)
            {
                if (PacketsSent < TotalPackets || _state == ConnectionTxState.SendingBurst)
                {
                    Fail(J1939TransportFault.InvalidFlowControl, J1939ConnectionAbortReason.Unspecified,
                        J1939TimeoutDetection.NotApplicable, 0.0, 0,
                        string.Format(CultureInfo.InvariantCulture,
                            "Node 0x{0:X2} acknowledged {1} as complete after only {2} of {3} packets were sent. The message it assembled is not the message that was queued, so this transfer is reported as failed.",
                            message.SourceAddress, J1939Pgn.Describe(Pgn), PacketsSent, TotalPackets));
                    return;
                }

                int acknowledgedBytes = message.Data[1] | (message.Data[2] << 8);
                if (acknowledgedBytes != Payload.Length)
                {
                    AddNote(string.Format(CultureInfo.InvariantCulture,
                        "acknowledgement reported {0} bytes, {1} were sent", acknowledgedBytes, Payload.Length));
                }

                Complete();
            }

            public override void OnTick(double nowMs)
            {
                switch (_state)
                {
                    case ConnectionTxState.SendingBurst:
                        if (nowMs < NextDataDueMs)
                        {
                            return;
                        }

                        EmitDataFrame(_nextSequence, _nextSequence - 1, false);

                        if (_nextSequence > _highestSequenceSent)
                        {
                            _highestSequenceSent = _nextSequence;
                        }

                        _nextSequence++;
                        _burstRemaining--;
                        LastActivityMs = nowMs;
                        NextDataDueMs = nowMs + Owner._interPacketDelayMs;

                        if (_burstRemaining <= 0)
                        {
                            _state = _highestSequenceSent >= TotalPackets
                                ? ConnectionTxState.AwaitingEndOfMsgAck
                                : ConnectionTxState.AwaitingClearToSend;
                        }

                        return;

                    case ConnectionTxState.HoldingConnection:
                        {
                            double held = nowMs - _holdStartedMs;

                            if (!_holdWarned && held > J1939Pgn.HoldTimeThMs)
                            {
                                _holdWarned = true;
                                AddNote(string.Format(CultureInfo.InvariantCulture,
                                    "node 0x{0:X2} held the connection open past Th ({1} ms)",
                                    DestinationAddress, J1939Pgn.HoldTimeThMs));
                            }

                            if (held > J1939Pgn.ConnectionTimeoutT4Ms)
                            {
                                Fail(J1939TransportFault.TimeoutT4, J1939ConnectionAbortReason.Timeout,
                                    J1939TimeoutDetection.HostTick, held, J1939Pgn.ConnectionTimeoutT4Ms,
                                    string.Format(CultureInfo.InvariantCulture,
                                        "Node 0x{0:X2} held the connection for {1} open for {2:F0} ms without granting a burst, exceeding T4 ({3} ms). {4} of {5} packets had been sent.",
                                        DestinationAddress, J1939Pgn.Describe(Pgn), held,
                                        J1939Pgn.ConnectionTimeoutT4Ms, PacketsSent, TotalPackets));
                            }

                            return;
                        }

                    default:
                        {
                            double idle = nowMs - LastActivityMs;
                            if (idle <= J1939Pgn.ConnectionTimeoutT3Ms)
                            {
                                return;
                            }

                            string awaiting = _state == ConnectionTxState.AwaitingEndOfMsgAck
                                ? "an end-of-message acknowledgement"
                                : "a clear-to-send";

                            Fail(J1939TransportFault.TimeoutT3, J1939ConnectionAbortReason.Timeout,
                                J1939TimeoutDetection.HostTick, idle, J1939Pgn.ConnectionTimeoutT3Ms,
                                string.Format(CultureInfo.InvariantCulture,
                                    "Node 0x{0:X2} did not send {1} for {2} within T3 ({3} ms; waited {4:F0} ms). {5} of {6} packets had been sent. Detected by the host tick watchdog.",
                                    DestinationAddress, awaiting, J1939Pgn.Describe(Pgn),
                                    J1939Pgn.ConnectionTimeoutT3Ms, idle, PacketsSent, TotalPackets));
                            return;
                        }
                }
            }

            public override string Describe(double nowMs)
            {
                string state;
                switch (_state)
                {
                    case ConnectionTxState.AwaitingClearToSend:
                        state = "awaiting TP.CM_CTS";
                        break;
                    case ConnectionTxState.SendingBurst:
                        state = string.Format(CultureInfo.InvariantCulture,
                            "sending TP.DT {0}, {1} left in burst", _nextSequence, _burstRemaining);
                        break;
                    case ConnectionTxState.HoldingConnection:
                        state = "connection held open by the receiver";
                        break;
                    default:
                        state = "awaiting TP.CM_EndOfMsgACK";
                        break;
                }

                return DescribeCommon(state, nowMs);
            }
        }

        // =====================================================================
        //  ETP transmit. Same conversation as RTS/CTS with one extra frame: every
        //  granted burst is preceded by an ETP.CM_DPO that tells the receiver which
        //  absolute packet the next sequence number 1 refers to.
        // =====================================================================

        private sealed class EtpOutboundSession : OutboundSession
        {
            private enum EtpTxState
            {
                AwaitingClearToSend,
                SendingBurst,
                AwaitingEndOfMsgAck,
                HoldingConnection
            }

            private EtpTxState _state = EtpTxState.AwaitingClearToSend;
            private int _packetOffset;
            private int _sequenceInBurst = 1;
            private int _burstRemaining;
            private int _highestPacketSent;
            private double _holdStartedMs;
            private bool _holdWarned;

            public EtpOutboundSession(J1939TransportProtocol owner, PendingSend request)
                : base(owner, request)
            {
            }

            public override J1939TransportKind Kind
            {
                get { return J1939TransportKind.ExtendedConnectionMode; }
            }

            protected override bool UsesConnectionAbort
            {
                get { return true; }
            }

            public override bool AwaitingPeerResponse
            {
                get
                {
                    return _state == EtpTxState.AwaitingClearToSend
                           || _state == EtpTxState.AwaitingEndOfMsgAck
                           || _state == EtpTxState.HoldingConnection;
                }
            }

            public override void Start(double nowMs)
            {
                byte[] data = new byte[8];
                data[0] = EtpCmRts;
                data[1] = (byte)(Payload.Length & 0xFF);
                data[2] = (byte)((Payload.Length >> 8) & 0xFF);
                data[3] = (byte)((Payload.Length >> 16) & 0xFF);
                data[4] = (byte)((Payload.Length >> 24) & 0xFF);
                data[5] = (byte)(Pgn & 0xFFu);
                data[6] = (byte)((Pgn >> 8) & 0xFFu);
                data[7] = (byte)((Pgn >> 16) & 0xFFu);

                Owner.EnqueueFrame(Owner.BuildEtpCm(data, DestinationAddress), TicketId);

                _state = EtpTxState.AwaitingClearToSend;
                LastActivityMs = nowMs;
            }

            public override void HandleFlowControl(J1939Message message, byte control, bool extended)
            {
                if (!extended)
                {
                    Fail(J1939TransportFault.InvalidFlowControl, J1939ConnectionAbortReason.Unspecified,
                        J1939TimeoutDetection.NotApplicable, 0.0, 0,
                        string.Format(CultureInfo.InvariantCulture,
                            "Node 0x{0:X2} answered an extended RTS for {1} with standard transport flow control 0x{2:X2}. The two mechanisms cannot be mixed within one connection.",
                            message.SourceAddress, J1939Pgn.Describe(Pgn), control));
                    return;
                }

                NotePeerResponseTime(message);

                if (control == EtpCmCts)
                {
                    HandleClearToSend(message);
                    return;
                }

                if (control == EtpCmEndOfMsgAck)
                {
                    HandleEndOfMessageAck(message);
                    return;
                }

                base.HandleFlowControl(message, control, extended);
            }

            private void HandleClearToSend(J1939Message message)
            {
                if (_state == EtpTxState.SendingBurst)
                {
                    Fail(J1939TransportFault.InvalidFlowControl,
                        J1939ConnectionAbortReason.UnexpectedCtsDuringTransfer,
                        J1939TimeoutDetection.NotApplicable, 0.0, 0,
                        string.Format(CultureInfo.InvariantCulture,
                            "Node 0x{0:X2} sent an extended clear-to-send for {1} while {2} packet(s) of the previous grant were still going out.",
                            message.SourceAddress, J1939Pgn.Describe(Pgn), _burstRemaining));
                    return;
                }

                byte[] data = message.Data;
                int granted = data[1];
                int nextPacket = data[2] | (data[3] << 8) | (data[4] << 16);

                if (granted == 0)
                {
                    _state = EtpTxState.HoldingConnection;
                    _holdStartedMs = Owner._hostNowMs;
                    _holdWarned = false;
                    LastActivityMs = Owner._hostNowMs;
                    return;
                }

                if (nextPacket < 1 || nextPacket > TotalPackets ||
                    nextPacket + granted - 1 > TotalPackets ||
                    granted > J1939Pgn.MaxTransportProtocolPackets)
                {
                    Fail(J1939TransportFault.InvalidFlowControl, J1939ConnectionAbortReason.BadSequenceNumber,
                        J1939TimeoutDetection.NotApplicable, 0.0, 0,
                        string.Format(CultureInfo.InvariantCulture,
                            "Node 0x{0:X2} asked for {1} packet(s) starting at absolute packet {2} for {3}, which does not fit the announced total of {4}.",
                            message.SourceAddress, granted, nextPacket, J1939Pgn.Describe(Pgn), TotalPackets));
                    return;
                }

                if (nextPacket <= _highestPacketSent)
                {
                    AddNote(string.Format(CultureInfo.InvariantCulture,
                        "node 0x{0:X2} requested retransmission from absolute packet {1} (had already received up to {2})",
                        message.SourceAddress, nextPacket, _highestPacketSent));
                }

                // Sequence numbers restart at 1 for every burst; the offset is what
                // makes them absolute again at the receiver.
                _packetOffset = nextPacket - 1;
                _sequenceInBurst = 1;
                _burstRemaining = granted;

                byte[] dpo = new byte[8];
                dpo[0] = EtpCmDpo;
                dpo[1] = (byte)granted;
                dpo[2] = (byte)(_packetOffset & 0xFF);
                dpo[3] = (byte)((_packetOffset >> 8) & 0xFF);
                dpo[4] = (byte)((_packetOffset >> 16) & 0xFF);
                dpo[5] = (byte)(Pgn & 0xFFu);
                dpo[6] = (byte)((Pgn >> 8) & 0xFFu);
                dpo[7] = (byte)((Pgn >> 16) & 0xFFu);

                Owner.EnqueueFrame(Owner.BuildEtpCm(dpo, DestinationAddress), TicketId);

                _state = EtpTxState.SendingBurst;
                LastActivityMs = Owner._hostNowMs;
                NextDataDueMs = Owner._hostNowMs + Owner._interPacketDelayMs;
            }

            private void HandleEndOfMessageAck(J1939Message message)
            {
                if (PacketsSent < TotalPackets || _state == EtpTxState.SendingBurst)
                {
                    Fail(J1939TransportFault.InvalidFlowControl, J1939ConnectionAbortReason.Unspecified,
                        J1939TimeoutDetection.NotApplicable, 0.0, 0,
                        string.Format(CultureInfo.InvariantCulture,
                            "Node 0x{0:X2} acknowledged {1} as complete after only {2} of {3} packets were sent.",
                            message.SourceAddress, J1939Pgn.Describe(Pgn), PacketsSent, TotalPackets));
                    return;
                }

                byte[] data = message.Data;
                long acknowledgedBytes = (long)data[1] | ((long)data[2] << 8)
                                         | ((long)data[3] << 16) | ((long)data[4] << 24);

                if (acknowledgedBytes != Payload.Length)
                {
                    AddNote(string.Format(CultureInfo.InvariantCulture,
                        "acknowledgement reported {0} bytes, {1} were sent", acknowledgedBytes, Payload.Length));
                }

                Complete();
            }

            public override void OnTick(double nowMs)
            {
                switch (_state)
                {
                    case EtpTxState.SendingBurst:
                        {
                            if (nowMs < NextDataDueMs)
                            {
                                return;
                            }

                            int absolutePacket = _packetOffset + _sequenceInBurst;
                            EmitDataFrame(_sequenceInBurst, absolutePacket - 1, true);

                            if (absolutePacket > _highestPacketSent)
                            {
                                _highestPacketSent = absolutePacket;
                            }

                            _sequenceInBurst++;
                            _burstRemaining--;
                            LastActivityMs = nowMs;
                            NextDataDueMs = nowMs + Owner._interPacketDelayMs;

                            if (_burstRemaining <= 0)
                            {
                                _state = _highestPacketSent >= TotalPackets
                                    ? EtpTxState.AwaitingEndOfMsgAck
                                    : EtpTxState.AwaitingClearToSend;
                            }

                            return;
                        }

                    case EtpTxState.HoldingConnection:
                        {
                            double held = nowMs - _holdStartedMs;

                            if (!_holdWarned && held > J1939Pgn.HoldTimeThMs)
                            {
                                _holdWarned = true;
                                AddNote(string.Format(CultureInfo.InvariantCulture,
                                    "node 0x{0:X2} held the connection open past Th ({1} ms)",
                                    DestinationAddress, J1939Pgn.HoldTimeThMs));
                            }

                            if (held > J1939Pgn.ConnectionTimeoutT4Ms)
                            {
                                Fail(J1939TransportFault.TimeoutT4, J1939ConnectionAbortReason.Timeout,
                                    J1939TimeoutDetection.HostTick, held, J1939Pgn.ConnectionTimeoutT4Ms,
                                    string.Format(CultureInfo.InvariantCulture,
                                        "Node 0x{0:X2} held the extended connection for {1} open for {2:F0} ms without granting a burst, exceeding T4 ({3} ms). {4} of {5} packets had been sent.",
                                        DestinationAddress, J1939Pgn.Describe(Pgn), held,
                                        J1939Pgn.ConnectionTimeoutT4Ms, PacketsSent, TotalPackets));
                            }

                            return;
                        }

                    default:
                        {
                            double idle = nowMs - LastActivityMs;
                            if (idle <= J1939Pgn.ConnectionTimeoutT3Ms)
                            {
                                return;
                            }

                            string awaiting = _state == EtpTxState.AwaitingEndOfMsgAck
                                ? "an end-of-message acknowledgement"
                                : "a clear-to-send";

                            Fail(J1939TransportFault.TimeoutT3, J1939ConnectionAbortReason.Timeout,
                                J1939TimeoutDetection.HostTick, idle, J1939Pgn.ConnectionTimeoutT3Ms,
                                string.Format(CultureInfo.InvariantCulture,
                                    "Node 0x{0:X2} did not send {1} for {2} within T3 ({3} ms; waited {4:F0} ms). {5} of {6} packets had been sent.",
                                    DestinationAddress, awaiting, J1939Pgn.Describe(Pgn),
                                    J1939Pgn.ConnectionTimeoutT3Ms, idle, PacketsSent, TotalPackets));
                            return;
                        }
                }
            }

            public override string Describe(double nowMs)
            {
                string state;
                switch (_state)
                {
                    case EtpTxState.AwaitingClearToSend:
                        state = "awaiting ETP.CM_CTS";
                        break;
                    case EtpTxState.SendingBurst:
                        state = string.Format(CultureInfo.InvariantCulture,
                            "offset {0}, sending ETP.DT {1}, {2} left in burst",
                            _packetOffset, _sequenceInBurst, _burstRemaining);
                        break;
                    case EtpTxState.HoldingConnection:
                        state = "connection held open by the receiver";
                        break;
                    default:
                        state = "awaiting ETP.CM_EndOfMsgACK";
                        break;
                }

                return DescribeCommon(state, nowMs);
            }
        }
    }
}
