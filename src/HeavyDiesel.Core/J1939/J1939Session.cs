// =============================================================================
//  J1939Session.cs - the orchestrator that makes the J1939 stack live.
//
//  WHAT THIS IS
//  ------------
//  J1939AddressClaim, J1939TransportProtocol and J1939Diagnostics are all passive:
//  they create no threads and touch no hardware. Rp1210Provider owns the adapter but
//  knows nothing about network management or segmentation. Nothing joins them up.
//  This class is that join: it owns ONE pump thread, wires both state machines to the
//  provider, and turns received frames into application messages the UI can render.
//
//  THREADING
//  ---------
//  All bus I/O happens on one dedicated background thread. Not a WinForms timer and
//  not the UI dispatcher: J1939-21 runs a 50 ms inter-packet gap, a 200 ms Tr, a
//  500 ms Th and 750/1250 ms T1-T4. A message box, a paint storm or a modal drag loop
//  on the UI thread stalls the pump past T1 and a live multipacket transfer is torn
//  down mid-message. On a truck that shows up as a fault list that is short by three
//  codes, which is worse than no fault list at all.
//
//  Public events are raised on a SECOND worker thread, not on the pump. The transport
//  documents that a slow event handler delays it and breaks connection-mode timing,
//  and a UI handler is exactly the kind of handler that blocks. The pump enqueues; the
//  notifier raises. Consumers marshal to the UI themselves - see the repo convention,
//  BeginInvoke((Action)(() => ...)). The one exception is a notification produced while
//  the session is not running, which is raised inline: queueing it would hand it to a
//  thread that does not exist, and a refusal reason that is never delivered is the
//  silent drop this class exists to prevent.
//
//  THE CLOCK
//  ---------
//  One Stopwatch, read once per pump iteration, feeding BOTH Tick calls. The address
//  claim's 1250 ms listen-before-claim window is a safety interlock (claiming an
//  address a real ECU already holds can make a non-arbitrary-address-capable module go
//  silent until power cycle), and a mixed timebase - Stopwatch into one machine, an
//  adapter counter into the other - satisfies the comparison instantly and claims with
//  zero real listen time. Never DateTime.Now: an NTP step or a DST change would abort
//  every live transport session.
//
//  HONESTY
//  -------
//  Nothing is reported that was not received. Every refusal, every dropped frame and
//  every degraded condition carries a reason through the Log event. Silent drops are
//  the failure mode this whole layer exists to prevent.
// =============================================================================
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Threading;
using HeavyDiesel.Transport;
using HeavyDiesel.Rp1210;

namespace HeavyDiesel.J1939
{
    /// <summary>
    /// One complete application-layer message received from the bus, whether it
    /// arrived in a single CAN frame or was reassembled from a multipacket transfer.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Both arrival paths produce this same event, deliberately. A DM1 reporting one
    /// active fault fits in eight bytes and arrives as a plain frame; a DM1 reporting
    /// two or more faults does not fit and arrives only as a BAM through the transport.
    /// A handler that subscribes to one path and not the other sees single-fault
    /// machines and goes blind on exactly the machines that are in trouble.
    /// </para>
    /// <para>
    /// Raised on a worker thread. Marshal to the UI before touching a control.
    /// </para>
    /// </remarks>
    public sealed class J1939ApplicationMessageEventArgs : EventArgs
    {
        private readonly J1939Message _message;
        private readonly bool _wasMultiFrame;
        private readonly int _packetCount;
        private readonly long _sessionClockMs;
        private readonly string _notes;

        /// <summary>Creates the event payload.</summary>
        /// <param name="message">The complete message: identifier plus whole payload.</param>
        /// <param name="wasMultiFrame">True when the payload was reassembled by <see cref="J1939TransportProtocol"/>.</param>
        /// <param name="packetCount">Data-transfer frames the message occupied; 1 for a single-frame message.</param>
        /// <param name="sessionClockMs">Session monotonic clock reading when the message was ingested, for ageing.</param>
        /// <param name="notes">Tolerated anomalies observed while receiving. Empty when clean.</param>
        /// <exception cref="ArgumentNullException"><paramref name="message"/> is null.</exception>
        public J1939ApplicationMessageEventArgs(J1939Message message, bool wasMultiFrame,
            int packetCount, long sessionClockMs, string notes)
        {
            if (message == null)
            {
                throw new ArgumentNullException("message");
            }

            _message = message;
            _wasMultiFrame = wasMultiFrame;
            _packetCount = packetCount;
            _sessionClockMs = sessionClockMs;
            _notes = notes ?? string.Empty;
        }

        /// <summary>Parameter group number, with PDU1 normalisation applied.</summary>
        public uint Pgn
        {
            get { return _message.Pgn; }
        }

        /// <summary>Address of the node that sent the message.</summary>
        public byte SourceAddress
        {
            get { return _message.SourceAddress; }
        }

        /// <summary>Address the message was directed at, or 255 for a broadcast group.</summary>
        public byte DestinationAddress
        {
            get { return _message.DestinationAddress; }
        }

        /// <summary>
        /// The complete payload. Same array as <c>Message.Data</c>; treat as read-only,
        /// it is not copied per subscriber.
        /// </summary>
        public byte[] Payload
        {
            get { return _message.Data; }
        }

        /// <summary>Payload length in bytes.</summary>
        public int Length
        {
            get { return _message.Length; }
        }

        /// <summary>
        /// True when the payload was reassembled from a BAM, RTS/CTS or ETP transfer.
        /// A DM1 or DM2 with this set carries more than one trouble code.
        /// </summary>
        public bool WasMultiFrame
        {
            get { return _wasMultiFrame; }
        }

        /// <summary>Number of data-transfer frames the message occupied. 1 for a single-frame message.</summary>
        public int PacketCount
        {
            get { return _packetCount; }
        }

        /// <summary>The whole message, including its identifier and timestamp provenance.</summary>
        public J1939Message Message
        {
            get { return _message; }
        }

        /// <summary>
        /// Message timestamp in milliseconds. Check <see cref="TimestampSource"/> before
        /// treating this as wire-accurate.
        /// </summary>
        public double TimestampMs
        {
            get { return _message.TimestampMs; }
        }

        /// <summary>Which clock <see cref="TimestampMs"/> came from.</summary>
        public J1939TimestampSource TimestampSource
        {
            get { return _message.TimestampSource; }
        }

        /// <summary>
        /// Reading of the session's monotonic clock when this message was taken off the
        /// adapter. Age a displayed value as <c>J1939Session.ElapsedMs - SessionClockMs</c>.
        /// DM1 is broadcast at 1 Hz while faults are active, so anything older than a
        /// couple of seconds is stale and must not be shown as current.
        /// </summary>
        public long SessionClockMs
        {
            get { return _sessionClockMs; }
        }

        /// <summary>Wall-clock UTC for display and log correlation only, never for interval measurement.</summary>
        public DateTime ReceivedUtc
        {
            get { return _message.CapturedUtc; }
        }

        /// <summary>
        /// Anomalies tolerated while receiving a multipacket message: duplicate frames
        /// absorbed, a PDU1 PGN low byte masked, a peer slower than Tr. Empty for a
        /// single-frame message and for a clean transfer. Surface it - it is the
        /// difference between "the data is good" and "the data is good but the ECU is
        /// limping".
        /// </summary>
        public string Notes
        {
            get { return _notes; }
        }

        /// <summary>Single-line log form.</summary>
        /// <returns>The formatted summary.</returns>
        public override string ToString()
        {
            StringBuilder sb = new StringBuilder(140);
            sb.AppendFormat(CultureInfo.InvariantCulture,
                "{0} SA 0x{1:X2} DA 0x{2:X2} {3} bytes{4}",
                J1939Pgn.Describe(Pgn), SourceAddress, DestinationAddress, Length,
                _wasMultiFrame
                    ? string.Format(CultureInfo.InvariantCulture, " (multipacket, {0} frames)", _packetCount)
                    : " (single frame)");

            if (_message.TimestampSource != J1939TimestampSource.Adapter)
            {
                sb.Append(" [host clock - timing soft]");
            }

            if (_notes.Length != 0)
            {
                sb.Append(" [").Append(_notes).Append("]");
            }

            return sb.ToString();
        }
    }

    /// <summary>
    /// Drives a live SAE J1939 session over a connected <see cref="Rp1210Provider"/>:
    /// owns the pump thread, the J1939-81 address claim and the J1939-21 transport, and
    /// turns received frames into application messages.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The provider must already be connected. This class does not connect it, does not
    /// reconnect it and does not dispose it - the caller owns its lifetime and may share
    /// it with other consumers.
    /// </para>
    /// <para>
    /// A session is single-use: <see cref="Start"/> once, <see cref="Stop"/> once. The
    /// address claim state machine has no restart path that would honestly represent
    /// "we stopped defending our address and then came back", so a second attachment to
    /// the bus needs a new session object.
    /// </para>
    /// <para>
    /// Typical use:
    /// </para>
    /// <code>
    /// var name    = J1939Name.CreateServiceTool(0, 1, IndustryGroup.OnHighway, 0);
    /// var session = new J1939Session(provider, name);
    /// session.Log += (s, text) =&gt; BeginInvoke((Action)(() =&gt; AppendLog(text)));
    /// session.ApplicationMessageReceived += (s, e) =&gt; BeginInvoke((Action)(() =&gt; Render(e)));
    /// session.Start();
    /// </code>
    /// </remarks>
    public sealed class J1939Session : IDisposable
    {
        /// <summary>Frames taken off the adapter per drain. Bounded so one pass cannot be starved by a flooded bus.</summary>
        private const int DRAIN_BATCH_SIZE = 256;

        /// <summary>
        /// Pump idle interval. The transport requires Tick at least every 10 ms: it
        /// drives both the timeout watchdogs and the 50 ms inter-packet transmit pacing.
        /// </summary>
        private const int PUMP_INTERVAL_MS = 5;

        /// <summary>Events raised per notifier pass before the queue is re-checked.</summary>
        private const int NOTIFY_BATCH_SIZE = 128;

        /// <summary>Notifier wakeup interval when the queue is empty.</summary>
        private const int NOTIFY_WAIT_MS = 100;

        /// <summary>
        /// Notification backlog ceiling. Reached only when a subscriber is far slower
        /// than the bus; overflow is reported, never silently absorbed.
        /// </summary>
        private const int MAX_QUEUED_NOTIFICATIONS = 4096;

        /// <summary>Bounded join for the pump thread on stop.</summary>
        private const int PUMP_JOIN_TIMEOUT_MS = 3000;

        /// <summary>Bounded join for the notifier thread on stop.</summary>
        private const int NOTIFIER_JOIN_TIMEOUT_MS = 2000;

        // Typed as the interface, not Rp1210Provider, since 2026-07-28: the session uses
        // exactly six members of its transport and binding those to one vendor API kept
        // J1939 off J2534 hardware for no protocol reason. See Transport/IJ1939Link.cs.
        private readonly IJ1939Link _provider;
        private readonly J1939Name _name;
        private readonly J1939AddressClaim _claim;

        // One monotonic clock for the whole session. Read once per pump iteration and
        // fed to BOTH Tick calls - see the file header for why a mixed timebase defeats
        // the listen-window interlock.
        private readonly Stopwatch _clock = Stopwatch.StartNew();

        private readonly object _lifecycleGate = new object();
        private readonly object _transportGate = new object();
        private readonly object _pumpSleepGate = new object();
        private readonly object _notifyGate = new object();
        private readonly Queue<Action> _notifications = new Queue<Action>();

        // The transport binds its source address at construction and exposes no setter.
        //
        // DESIGN DECISION - build on claim, rebuild on change.
        //   The claimed address is unknown until the claim has listened out its 1250 ms
        //   window, so there is nothing to construct the transport with at Start time.
        //   It could have been built against the null address and rebound later, but
        //   there is no rebind, and a transport sitting on 254 would answer RTS frames
        //   addressed to the null address - which is not us, and replying to them puts
        //   CTS traffic on the bus from an address we do not hold.
        //   So: no transport until an address is claimed, and a FRESH transport whenever
        //   the claimed address changes. The claim relocates on its own after losing
        //   arbitration or detecting a collision, so the address genuinely does change
        //   at runtime; a transport left on the old address would keep reassembling
        //   sessions the peer believes it is running with a node that no longer exists.
        //   Rebuilding abandons in-flight sessions, which is correct - they were bound to
        //   an address we surrendered - and each one is reported through TransportFault.
        private J1939TransportProtocol _transport;

        private Thread _pumpThread;
        private Thread _notifierThread;

        private volatile bool _stopPump;
        private volatile bool _stopNotifier;
        private volatile bool _notifierAlive;
        private volatile bool _running;
        private volatile bool _stopped;

        /// <summary>
        /// Set when the pump thread could not be joined during Stop. The thread may still be
        /// executing inside the vendor driver, so the RP1210 module must not be unmapped.
        /// </summary>
        private volatile bool _pumpAbandoned;
        private volatile bool _disposed;

        // Set by the claim's StateChanged handler (pump thread) and consumed by the pump
        // between frames, so a relocation that happens part-way through a drained batch
        // does not leave the rest of the batch going to a stale transport.
        private volatile bool _addressDirty = true;

        private volatile bool _sawHostClockFrame;
        private volatile bool _preClaimTransportFrameNoted;

        private long _framesReceived;
        private long _applicationMessagesDispatched;
        private long _multiFrameMessagesDispatched;
        private long _transportFramesDroppedPreClaim;
        private long _transmitFailures;
        private long _networkManagementTransmitFailures;
        private int _droppedNotifications;

        /// <summary>
        /// Creates a session over an already-connected provider.
        /// </summary>
        /// <param name="provider">
        /// A connected <see cref="Rp1210Provider"/> open on a J1939 protocol string. Its
        /// lifetime belongs to the caller: this class never connects, reconnects or
        /// disposes it.
        /// </param>
        /// <param name="name">
        /// This tool's J1939-81 NAME. The claim takes a private copy, so later edits to
        /// the caller's instance do not change what has been announced on the bus.
        /// </param>
        /// <exception cref="ArgumentNullException">Either argument is null.</exception>
        public J1939Session(IJ1939Link provider, J1939Name name)
        {
            if (provider == null)
            {
                throw new ArgumentNullException("provider",
                    "J1939Session does not own an adapter; supply a connected IJ1939Link "
                  + "(Rp1210Provider, or J1939J2534Link for J1939 over a J2534 device).");
            }

            if (name == null)
            {
                throw new ArgumentNullException("name",
                    "A J1939 node must have a NAME before it can claim an address. Use J1939Name.CreateServiceTool.");
            }

            _provider = provider;
            _name = name;

            _claim = new J1939AddressClaim(name, TransmitClaimFrame);
            _claim.Log = OnClaimLog;
            _claim.StateChanged += OnClaimStateChanged;
        }

        /// <summary>
        /// Raised for every complete application-layer message received, single frame or
        /// reassembled. Raised on a worker thread - marshal before touching a control.
        /// </summary>
        public event EventHandler<J1939ApplicationMessageEventArgs> ApplicationMessageReceived;

        /// <summary>
        /// Forwards every J1939-81 address claim state transition. Raised on a worker
        /// thread - marshal before touching a control.
        /// </summary>
        public event EventHandler<J1939AddressClaimEventArgs> AddressClaimStateChanged;

        /// <summary>
        /// Forwards every transport session that ended without delivering a complete
        /// message, in either direction, with the reason. Subscribe to this: an
        /// unsubscribed failure is an invisible short fault list. Raised on a worker
        /// thread - marshal before touching a control.
        /// </summary>
        public event EventHandler<J1939TransportFaultEventArgs> TransportFault;

        /// <summary>
        /// Raised when an outbound application message has finished transmitting — the
        /// completion half of <see cref="SendApplicationMessage"/>.
        /// </summary>
        /// <remarks>
        /// WHY THIS MATTERS. J1939 permits only ONE connection-mode transport session per
        /// (source, destination) pair at a time, and OEM service protocols exceed eight
        /// bytes routinely — a Cummins parameter read is twelve — so essentially every
        /// service request is a transport session. Without a completion signal a caller
        /// polling on a timer just queues behind itself: the transport's outbound queue
        /// (32 deep by default) fills, and then sends start being rejected for a reason
        /// that looks nothing like "you are asking too fast".
        ///
        /// Pair it with <see cref="TransportFault"/>: between them, every outbound message
        /// ends in exactly one of the two. A caller that waits on this alone will wedge on
        /// the first timeout.
        /// </remarks>
        public event EventHandler<J1939TransportMessageEventArgs> ApplicationMessageSent;

        /// <summary>
        /// Technician-facing log line. Every refusal, drop and degraded condition in this
        /// class comes out here with a reason.
        /// </summary>
        /// <remarks>
        /// Raised on a worker thread while the session is running. A refusal reported by
        /// a call made while the session is NOT running - Start declined, a request with
        /// no address claimed, a clear that was not transmitted - is raised inline on the
        /// calling thread, because there is no worker to hand it to and dropping it would
        /// leave the caller with a failure and no reason. Marshal before touching a
        /// control either way.
        /// </remarks>
        public event EventHandler<string> Log;

        /// <summary>True between a successful <see cref="Start"/> and <see cref="Stop"/>.</summary>
        public bool IsRunning
        {
            get { return _running; }
        }

        /// <summary>Current state of the J1939-81 address claim sequence.</summary>
        public J1939AddressClaimState State
        {
            get { return _claim.State; }
        }

        /// <summary>
        /// Source address currently held, or <see cref="J1939Pgn.NullAddress"/> (254)
        /// when none is held. Show it: the technician needs to see what the tool is doing
        /// on the bus.
        /// </summary>
        public byte ClaimedAddress
        {
            get { return _claim.ClaimedAddress; }
        }

        /// <summary>True only once an address has been claimed and the contention window has passed.</summary>
        public bool HasClaimedAddress
        {
            get { return _claim.HasClaimedAddress; }
        }

        /// <summary>This tool's NAME, as announced on the bus.</summary>
        public J1939Name Name
        {
            get { return _name; }
        }

        /// <summary>
        /// Reading of the session's monotonic clock, in milliseconds. Age every displayed
        /// reading against this: subtract <see cref="J1939ApplicationMessageEventArgs.SessionClockMs"/>
        /// to get the true age of the data on screen.
        /// </summary>
        public long ElapsedMs
        {
            get { return _clock.ElapsedMilliseconds; }
        }

        /// <summary>
        /// True when protocol timing cannot be trusted to the millisecond: the adapter
        /// does not timestamp in hardware, at least one frame arrived without a usable
        /// adapter timestamp, or the transport has measured a session off the host clock.
        /// Never present timing measured under this condition as if it were wire-accurate.
        /// </summary>
        /// <summary>
        /// True when Stop could not join the pump thread. The thread is a background thread so it
        /// cannot block shutdown, but it may still be inside a vendor RP1210 call — the provider has
        /// been told not to unmap its driver, and the adapter should not be reconnected until the
        /// application restarts.
        /// </summary>
        public bool PumpAbandoned
        {
            get { return _pumpAbandoned; }
        }

        public bool TimingDegraded
        {
            get
            {
                if (_sawHostClockFrame)
                {
                    return true;
                }

                InterfaceCapabilities caps = _provider.Capabilities;
                if (caps == null || !caps.HardwareTimestamps)
                {
                    return true;
                }

                J1939TransportProtocol transport = CurrentTransport();
                return transport != null && transport.TimingDegraded;
            }
        }

        /// <summary>
        /// Every source address observed transmitting, with the NAME of each node that
        /// announced one. Snapshot; safe to call from any thread.
        /// </summary>
        /// <returns>The observed nodes. Empty before anything has been heard; never null.</returns>
        public J1939SourceAddressEntry[] GetSourceAddressMap()
        {
            return _claim.GetSourceAddressMap();
        }

        /// <summary>
        /// Looks up the NAME announced by a source address.
        /// </summary>
        /// <param name="sourceAddress">Address to look up.</param>
        /// <param name="name">The NAME, or null when that address has been seen transmitting but has never claimed.</param>
        /// <returns>True when a NAME is known for the address.</returns>
        public bool TryGetNodeName(byte sourceAddress, out J1939Name name)
        {
            return _claim.TryGetName(sourceAddress, out name);
        }

        /// <summary>
        /// Starts the pump thread and begins the J1939-81 claim sequence. The claim
        /// listens for at least <see cref="J1939AddressClaim.MINIMUM_LISTEN_MS"/> before
        /// it transmits any claim, so no address is available for roughly the first
        /// 1.3 seconds.
        /// </summary>
        /// <exception cref="ObjectDisposedException">The session has been disposed.</exception>
        /// <exception cref="InvalidOperationException">
        /// The provider is not connected, or this session has already been stopped.
        /// </exception>
        public void Start()
        {
            lock (_lifecycleGate)
            {
                ThrowIfDisposed();

                if (_stopped)
                {
                    const string reason = "J1939 session start refused: this session has already been stopped. "
                        + "The address claim cannot honestly be restarted after the tool stopped defending its "
                        + "address - another node may have taken it while the pump was down. Create a new J1939Session.";
                    QueueLog(reason);
                    throw new InvalidOperationException(reason);
                }

                if (_running)
                {
                    QueueLog("J1939 session start ignored: already running on " + DescribeStatus() + ".");
                    return;
                }

                if (!_provider.IsConnected)
                {
                    const string reason = "J1939 session start refused: the RP1210 provider is not connected. "
                        + "Connect the adapter on a J1939 protocol string first - this class does not own the "
                        + "adapter and will not connect it.";
                    QueueLog(reason);
                    throw new InvalidOperationException(reason);
                }

                _stopPump = false;
                _stopNotifier = false;
                _addressDirty = true;

                // The notifier comes up FIRST, before anything is reported, so the
                // start-up notices below reach the subscriber on the worker thread rather
                // than re-entering it on the caller's - which for a WinForms caller is
                // the UI thread, in the middle of its own click handler.
                _notifierThread = new Thread(NotifierLoop) { IsBackground = true, Name = "J1939 notifier" };
                _notifierAlive = true;
                _notifierThread.Start();

                InterfaceCapabilities caps = _provider.Capabilities;
                if (caps != null)
                {
                    if (!caps.HardwareTimestamps)
                    {
                        QueueLog("J1939 timing DEGRADED: this adapter does not timestamp in hardware, so every "
                            + "interval is measured off the host clock and includes Windows scheduling and USB "
                            + "latency. J1939-21 connection-mode timers are 50 ms to 1250 ms; treat transport "
                            + "timing figures as indicative, not measured.");
                    }

                    if (!string.IsNullOrEmpty(caps.DegradationNotice))
                    {
                        QueueLog("J1939 adapter limitation: " + caps.DegradationNotice);
                    }

                    if (caps.AssistsAddressClaim)
                    {
                        QueueLog("J1939 note: this adapter reports that it assists address claiming. This session "
                            + "runs its own J1939-81 claim regardless, so the tool's NAME and address are the ones "
                            + "shown here. If the driver also claims an address, two addresses will appear for one "
                            + "physical tool.");
                    }
                }

                _pumpThread = new Thread(PumpLoop) { IsBackground = true, Name = "J1939 pump" };
                _running = true;
                _pumpThread.Start();

                QueueLog(string.Format(CultureInfo.InvariantCulture,
                    "J1939 session started on \"{0}\" with NAME 0x{1:X16}. Listening {2} ms before claiming an "
                    + "address; no frames can be transmitted and no multipacket message can be reassembled until "
                    + "the claim completes.",
                    _provider.ProtocolString, _name.Value, J1939AddressClaim.MINIMUM_LISTEN_MS));
            }
        }

        /// <summary>
        /// Stops the pump, abandons any in-flight transport sessions (each reported
        /// through <see cref="TransportFault"/>) and detaches from both state machines.
        /// Safe to call more than once and safe to call when never started. Does not
        /// disconnect or dispose the provider.
        /// </summary>
        public void Stop()
        {
            lock (_lifecycleGate)
            {
                if (_stopped && !_running)
                {
                    return;
                }

                bool wasRunning = _running;
                _running = false;
                _stopped = true;

                _stopPump = true;
                lock (_pumpSleepGate)
                {
                    Monitor.PulseAll(_pumpSleepGate);
                }

                Thread pump = _pumpThread;
                if (pump != null && wasRunning)
                {
                    if (!pump.Join(PUMP_JOIN_TIMEOUT_MS))
                    {
                        _pumpAbandoned = true;

                        QueueLog(string.Format(CultureInfo.InvariantCulture,
                            "J1939 pump thread did not exit within {0} ms and has been abandoned. It is a background "
                            + "thread so it cannot block shutdown, but it may still be inside a vendor RP1210 call. "
                            + "Do not reconnect this adapter until the application restarts.",
                            PUMP_JOIN_TIMEOUT_MS));

                        // Telling the provider directly, rather than reporting this and hoping the
                        // caller acts on it. An abandoned pump can be *inside* a provider call right
                        // now; if the provider is then disposed it calls FreeLibrary and unmaps code
                        // that is still executing, which is an unrecoverable access violation no
                        // managed handler can catch. The provider already knows how to leak the
                        // module handle instead — it just has to be told this thread exists.
                        // Knowledge and action in the same place beats caller discipline.
                        try
                        {
                            _provider.MarkUnloadUnsafe(
                                "the J1939 pump thread could not be joined and may still be executing inside the driver");
                        }
                        catch (Exception ex)
                        {
                            QueueLog("Could not mark the RP1210 driver unsafe to unload: " + ex.Message
                                     + " - disposing the provider may unmap code that is still running.");
                        }
                    }
                }

                _pumpThread = null;

                // Reset BEFORE detaching, so every abandoned session is reported rather
                // than vanishing. Leaving sessions alive across a detach would merge data
                // from two different bus states into one message on any later reattach.
                TeardownTransport("the J1939 session is stopping");

                _claim.StateChanged -= OnClaimStateChanged;
                _claim.Log = null;

                if (wasRunning)
                {
                    QueueLog("J1939 session stopped. The tool is no longer defending its source address; another "
                        + "node may take it. Any address shown from here on is historical, not current.");
                }

                _stopNotifier = true;
                lock (_notifyGate)
                {
                    Monitor.PulseAll(_notifyGate);
                }

                Thread notifier = _notifierThread;

                // A subscriber is allowed to call Stop() from inside its own handler -
                // "shut down when the transport faults" is a reasonable thing to write.
                // That handler runs ON the notifier, so joining it here would be the
                // thread waiting on itself.
                if (notifier != null && wasRunning && !ReferenceEquals(notifier, Thread.CurrentThread))
                {
                    if (!notifier.Join(NOTIFIER_JOIN_TIMEOUT_MS))
                    {
                        // Nothing else can be logged usefully here - the thread that would
                        // raise the log event is the one that is stuck.
                        Debug.WriteLine("J1939 notifier thread did not exit within "
                            + NOTIFIER_JOIN_TIMEOUT_MS + " ms; a subscriber is blocking.");
                    }
                }

                _notifierThread = null;
                _notifierAlive = false;

                DrainResidualNotifications();
            }
        }

        /// <summary>
        /// Stops the session and releases subscribers. Does NOT dispose the provider -
        /// the caller owns it. Safe to call more than once.
        /// </summary>
        public void Dispose()
        {
            lock (_lifecycleGate)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
            }

            Stop();

            ApplicationMessageReceived = null;
            AddressClaimStateChanged = null;
            TransportFault = null;
            Log = null;
        }

        // =====================================================================
        //  Requests
        // =====================================================================

        /// <summary>
        /// Sends a J1939 Request (PGN 59904) to one specific ECU.
        /// </summary>
        /// <param name="pgn">The parameter group being asked for, 0..0x3FFFF.</param>
        /// <param name="destinationAddress">Address of the ECU being asked.</param>
        /// <returns>True when the adapter accepted the frame. Any refusal is logged with its reason.</returns>
        /// <exception cref="ObjectDisposedException">The session has been disposed.</exception>
        public bool RequestPgn(uint pgn, byte destinationAddress)
        {
            string failureReason;
            return RequestPgn(pgn, destinationAddress, out failureReason);
        }

        /// <summary>
        /// Sends a J1939 Request (PGN 59904) to the global address, so every module that
        /// supports the parameter group answers.
        /// </summary>
        /// <param name="pgn">The parameter group being asked for, 0..0x3FFFF.</param>
        /// <returns>True when the adapter accepted the frame. Any refusal is logged with its reason.</returns>
        /// <exception cref="ObjectDisposedException">The session has been disposed.</exception>
        public bool RequestPgn(uint pgn)
        {
            string failureReason;
            return RequestPgn(pgn, J1939Pgn.GlobalAddress, out failureReason);
        }

        /// <summary>
        /// Sends a J1939 Request (PGN 59904) to the global address, reporting why if it
        /// could not be sent.
        /// </summary>
        /// <param name="pgn">The parameter group being asked for, 0..0x3FFFF.</param>
        /// <param name="failureReason">Empty on success; otherwise the reason the request was not sent.</param>
        /// <returns>True when the adapter accepted the frame.</returns>
        /// <exception cref="ObjectDisposedException">The session has been disposed.</exception>
        public bool RequestPgn(uint pgn, out string failureReason)
        {
            return RequestPgn(pgn, J1939Pgn.GlobalAddress, out failureReason);
        }

        /// <summary>
        /// Sends a J1939 Request (PGN 59904), reporting why if it could not be sent.
        /// </summary>
        /// <param name="pgn">The parameter group being asked for, 0..0x3FFFF.</param>
        /// <param name="destinationAddress">Address of the ECU being asked, or 255 to ask every module.</param>
        /// <param name="failureReason">Empty on success; otherwise the reason the request was not sent.</param>
        /// <returns>True when the adapter accepted the frame.</returns>
        /// <exception cref="ObjectDisposedException">The session has been disposed.</exception>
        public bool RequestPgn(uint pgn, byte destinationAddress, out string failureReason)
        {
            ThrowIfDisposed();

            if (pgn > J1939Id.MAX_PGN)
            {
                failureReason = string.Format(CultureInfo.InvariantCulture,
                    "J1939 request refused: PGN {0} does not fit the 18-bit PGN field (0..{1}).",
                    pgn, J1939Id.MAX_PGN);
                QueueLog(failureReason);
                return false;
            }

            if (destinationAddress == J1939Pgn.NullAddress)
            {
                failureReason = "J1939 request refused: the null address (254) means \"no node\" and cannot be a "
                    + "destination. Address the request to a specific ECU or to 255 for every module.";
                QueueLog(failureReason);
                return false;
            }

            if (!_running)
            {
                failureReason = "J1939 request refused: the session is not running, so nothing would be pumping the "
                    + "bus to receive the answer. Call Start() first.";
                QueueLog(failureReason);
                return false;
            }

            byte sourceAddress;
            if (!TryGetTransmitAddress("request for " + J1939Pgn.Describe(pgn), out sourceAddress, out failureReason))
            {
                return false;
            }

            J1939Message message = J1939Diagnostics.BuildRequestMessage(pgn, sourceAddress, destinationAddress);

            if (!TransmitFrame(message))
            {
                failureReason = string.Format(CultureInfo.InvariantCulture,
                    "J1939 request for {0} to 0x{1:X2} was not accepted by the adapter. The request did NOT reach "
                    + "the bus; do not treat the absence of a reply as \"the module does not support it\".",
                    J1939Pgn.Describe(pgn), destinationAddress);
                QueueLog(failureReason);
                return false;
            }

            failureReason = string.Empty;

            QueueLog(string.Format(CultureInfo.InvariantCulture,
                "J1939 request sent: {0} from SA 0x{1:X2} to {2}.",
                J1939Pgn.Describe(pgn), sourceAddress,
                destinationAddress == J1939Pgn.GlobalAddress
                    ? "every module (global)"
                    : string.Format(CultureInfo.InvariantCulture, "0x{0:X2}", destinationAddress)));

            return true;
        }

        // =====================================================================
        //  Destructive operations
        // =====================================================================

        /// <summary>
        /// Transmits a prepared DM3 or DM11 clear request.
        /// </summary>
        /// <remarks>
        /// <para>
        /// DESTRUCTIVE AND IRREVERSIBLE. Clearing on a live machine erases the fault
        /// history the fitter may still be working from, and on an emissions-relevant
        /// engine it resets the readiness monitors - the machine will not pass an
        /// emissions check again until a full drive cycle has re-run them.
        /// </para>
        /// <para>
        /// <paramref name="operatorConfirmed"/> is passed straight through to
        /// <see cref="J1939ClearRequest.ToMessage"/> and the resulting exception is NOT
        /// caught. That interlock is the point: it must be impossible to erase a
        /// machine's fault history because a default argument was left in place. Pass
        /// the operator's actual answer, never a literal true from a code path that did
        /// not ask them.
        /// </para>
        /// </remarks>
        /// <param name="request">A request built by <see cref="J1939Diagnostics.BuildDm3ClearPreviouslyActiveDtcs"/> or <see cref="J1939Diagnostics.BuildDm11ClearActiveDtcs"/>.</param>
        /// <param name="operatorConfirmed">The operator's real answer to the confirmation prompt.</param>
        /// <returns>True when the adapter accepted the frame. Any refusal is logged with its reason.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="request"/> is null.</exception>
        /// <exception cref="ObjectDisposedException">The session has been disposed.</exception>
        /// <exception cref="InvalidOperationException"><paramref name="operatorConfirmed"/> is false. Deliberate and not swallowed.</exception>
        public bool SendClearRequest(J1939ClearRequest request, bool operatorConfirmed)
        {
            string failureReason;
            return SendClearRequest(request, operatorConfirmed, out failureReason);
        }

        /// <summary>
        /// Transmits a prepared DM3 or DM11 clear request, reporting why if it could not
        /// be sent. See <see cref="SendClearRequest(J1939ClearRequest,bool)"/> for the
        /// safety contract - it applies identically here.
        /// </summary>
        /// <param name="request">A prepared clear request.</param>
        /// <param name="operatorConfirmed">The operator's real answer to the confirmation prompt.</param>
        /// <param name="failureReason">Empty on success; otherwise the reason the clear was not sent.</param>
        /// <returns>True when the adapter accepted the frame.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="request"/> is null.</exception>
        /// <exception cref="ObjectDisposedException">The session has been disposed.</exception>
        /// <exception cref="InvalidOperationException"><paramref name="operatorConfirmed"/> is false.</exception>
        public bool SendClearRequest(J1939ClearRequest request, bool operatorConfirmed, out string failureReason)
        {
            if (request == null)
            {
                throw new ArgumentNullException("request");
            }

            ThrowIfDisposed();

            // Built FIRST, before any eligibility check, and deliberately not wrapped:
            // an unconfirmed call must throw whatever else is wrong with the session, so
            // the interlock can never be bypassed by happening to be offline at the time.
            J1939Message message = request.ToMessage(operatorConfirmed);

            if (!_running)
            {
                failureReason = string.Format(CultureInfo.InvariantCulture,
                    "{0} refused: the J1939 session is not running, so no acknowledgement could be received even if "
                    + "the frame went out. Nothing was transmitted.", request.OperationName);
                QueueLog(failureReason);
                return false;
            }

            byte sourceAddress;
            if (!TryGetTransmitAddress(request.OperationName, out sourceAddress, out failureReason))
            {
                return false;
            }

            if (request.SourceAddress != sourceAddress)
            {
                failureReason = string.Format(CultureInfo.InvariantCulture,
                    "{0} refused: the request was built for source address 0x{1:X2} but this tool currently holds "
                    + "0x{2:X2}. Transmitting from an address we do not hold corrupts another node's traffic. "
                    + "Rebuild the request using J1939Session.ClaimedAddress and confirm with the operator again.",
                    request.OperationName, request.SourceAddress, sourceAddress);
                QueueLog(failureReason);
                return false;
            }

            QueueLog(string.Format(CultureInfo.InvariantCulture,
                "J1939 DESTRUCTIVE OPERATION confirmed by operator - transmitting {0} to {1}. {2}",
                request.OperationName,
                request.IsGlobalBroadcast
                    ? "EVERY module on the segment (global address)"
                    : string.Format(CultureInfo.InvariantCulture, "0x{0:X2}", request.DestinationAddress),
                request.Warning));

            if (!request.AcknowledgementExpected)
            {
                QueueLog(string.Format(CultureInfo.InvariantCulture,
                    "{0} went to the global address. J1939-73 specifies no acknowledgement for a global clear, so "
                    + "there will be NO confirmation that any module heard it or acted on it. Re-read the fault "
                    + "list to establish what actually happened.", request.OperationName));
            }

            if (!TransmitFrame(message))
            {
                failureReason = string.Format(CultureInfo.InvariantCulture,
                    "{0} was NOT accepted by the adapter and did not reach the bus. Nothing has been cleared.",
                    request.OperationName);
                QueueLog(failureReason);
                return false;
            }

            failureReason = string.Empty;
            return true;
        }

        // =====================================================================
        //  Status
        // =====================================================================

        /// <summary>
        /// One honest multi-clause status line for a status bar or a log header: what the
        /// claim is doing, whether multipacket reassembly is running, and every degraded
        /// condition currently in force.
        /// </summary>
        /// <returns>The formatted status.</returns>
        public string DescribeStatus()
        {
            StringBuilder sb = new StringBuilder(320);

            if (_disposed)
            {
                sb.Append("J1939: disposed");
            }
            else if (_running)
            {
                sb.Append("J1939: running");
            }
            else if (_stopped)
            {
                sb.Append("J1939: stopped (address shown below is historical)");
            }
            else
            {
                sb.Append("J1939: not started");
            }

            sb.Append(" | ").Append(_claim.DescribeStatus());

            long nmFailures = Interlocked.Read(ref _networkManagementTransmitFailures);
            if (nmFailures > 0)
            {
                sb.AppendFormat(CultureInfo.InvariantCulture,
                    " | WARNING: {0} address-claim frames were rejected by the adapter and never reached the bus, so "
                    + "any address shown above was never announced. Other nodes do not know this tool is here.",
                    nmFailures);
            }

            if (!_provider.IsConnected)
            {
                sb.Append(" | ADAPTER NOT CONNECTED - nothing is being received");
            }

            J1939TransportProtocol transport = CurrentTransport();
            if (transport == null)
            {
                sb.Append(" | multipacket reassembly NOT running (no source address claimed) - a DM1 with more than "
                    + "one active fault cannot be decoded");

                long preClaim = Interlocked.Read(ref _transportFramesDroppedPreClaim);
                if (preClaim > 0)
                {
                    sb.AppendFormat(CultureInfo.InvariantCulture, ", {0} transport frames missed so far", preClaim);
                }
            }
            else
            {
                sb.AppendFormat(CultureInfo.InvariantCulture,
                    " | transport SA 0x{0:X2}, {1} inbound / {2} outbound sessions",
                    transport.SourceAddress, transport.ActiveInboundSessionCount, transport.ActiveOutboundSessionCount);
            }

            if (TimingDegraded)
            {
                sb.Append(" | TIMING DEGRADED (host clock - protocol intervals include scheduling latency)");
            }

            sb.AppendFormat(CultureInfo.InvariantCulture,
                " | rx {0} frames, {1} messages ({2} multipacket)",
                Interlocked.Read(ref _framesReceived),
                Interlocked.Read(ref _applicationMessagesDispatched),
                Interlocked.Read(ref _multiFrameMessagesDispatched));

            long txFailures = Interlocked.Read(ref _transmitFailures);
            if (txFailures > 0)
            {
                sb.AppendFormat(CultureInfo.InvariantCulture, " | {0} transmit failures", txFailures);
            }

            int queued;
            lock (_notifyGate)
            {
                queued = _notifications.Count;
            }

            if (queued > MAX_QUEUED_NOTIFICATIONS / 4)
            {
                sb.AppendFormat(CultureInfo.InvariantCulture,
                    " | {0} events queued - the display is behind the bus", queued);
            }

            return sb.ToString();
        }

        /// <summary>Status line.</summary>
        /// <returns>The result of <see cref="DescribeStatus"/>.</returns>
        public override string ToString()
        {
            return DescribeStatus();
        }

        // =====================================================================
        //  Pump thread
        // =====================================================================

        private void PumpLoop()
        {
            try
            {
                _claim.Start(_clock.ElapsedMilliseconds);
            }
            catch (Exception ex)
            {
                _running = false;
                QueueLog("J1939 address claim could not be started, so the session is not on the bus: " + ex.Message);
                return;
            }

            while (!_stopPump)
            {
                int drained = 0;

                try
                {
                    List<J1939Message> batch = _provider.DrainJ1939(DRAIN_BATCH_SIZE);

                    if (batch != null)
                    {
                        drained = batch.Count;

                        for (int i = 0; i < batch.Count; i++)
                        {
                            ProcessInboundFrame(batch[i]);
                        }
                    }
                }
                catch (Exception ex)
                {
                    QueueLog("J1939 pump could not drain the adapter receive queue: " + ex.Message
                        + " - frames are being lost while this persists.");
                }

                // One clock reading, both state machines. Claim first: it may relocate,
                // and the transport must not tick against an address we have surrendered.
                long nowMs = _clock.ElapsedMilliseconds;

                try
                {
                    _claim.Tick(nowMs);
                }
                catch (Exception ex)
                {
                    QueueLog("J1939 address claim tick failed: " + ex.Message
                        + " - the claim is no longer being maintained and the tool may be arbitrated off the bus.");
                }

                if (_addressDirty)
                {
                    SyncTransportToClaimedAddress();
                }

                J1939TransportProtocol transport = CurrentTransport();
                if (transport != null)
                {
                    try
                    {
                        transport.Tick(nowMs);
                    }
                    catch (Exception ex)
                    {
                        QueueLog("J1939 transport tick failed: " + ex.Message
                            + " - multipacket timeouts and transmit pacing are not being serviced.");
                    }
                }

                // A full batch means the adapter still has a backlog: go straight round
                // again rather than sleeping on a loaded bus. Otherwise wait out the tick
                // interval on a monitor so Stop() wakes us immediately instead of burning
                // a core on a spin.
                if (drained < DRAIN_BATCH_SIZE)
                {
                    lock (_pumpSleepGate)
                    {
                        if (!_stopPump)
                        {
                            Monitor.Wait(_pumpSleepGate, PUMP_INTERVAL_MS);
                        }
                    }
                }
            }
        }

        private void ProcessInboundFrame(J1939Message frame)
        {
            if (frame == null)
            {
                QueueLog("J1939 pump discarded a null frame handed back by the RP1210 provider.");
                return;
            }

            Interlocked.Increment(ref _framesReceived);

            if (frame.TimestampSource != J1939TimestampSource.Adapter)
            {
                _sawHostClockFrame = true;
            }

            try
            {
                _claim.OnMessageReceived(frame);
            }
            catch (Exception ex)
            {
                QueueLog(string.Format(CultureInfo.InvariantCulture,
                    "J1939 address claim rejected a received frame ({0} from SA 0x{1:X2}): {2} - network management "
                    + "state may now be stale.", J1939Pgn.Describe(frame.Pgn), frame.SourceAddress, ex.Message));
            }

            // The claim can relocate inside OnMessageReceived (collision detected on the
            // address we hold), so re-bind before the frame reaches the transport.
            if (_addressDirty)
            {
                SyncTransportToClaimedAddress();
            }

            J1939TransportProtocol transport = CurrentTransport();

            if (transport == null)
            {
                if (IsTransportFrame(frame.Pgn))
                {
                    NoteTransportFrameWithoutTransport(frame);
                    return;
                }
            }
            else
            {
                bool consumed;

                try
                {
                    consumed = transport.OnMessageReceived(frame);
                }
                catch (Exception ex)
                {
                    QueueLog(string.Format(CultureInfo.InvariantCulture,
                        "J1939 transport failed on a received {0} frame from SA 0x{1:X2}: {2} - the frame was "
                        + "discarded and any message it belonged to is incomplete.",
                        J1939Pgn.Describe(frame.Pgn), frame.SourceAddress, ex.Message));
                    return;
                }

                if (consumed)
                {
                    // A transport frame. The reassembled message arrives later through
                    // MessageReassembled; handing the raw TP.DT bytes to the application
                    // layer as well would present seven bytes of a partial message as if
                    // it were a whole one.
                    return;
                }
            }

            DispatchApplicationMessage(frame, false, 1, string.Empty);
        }

        private void NoteTransportFrameWithoutTransport(J1939Message frame)
        {
            Interlocked.Increment(ref _transportFramesDroppedPreClaim);

            if (_preClaimTransportFrameNoted)
            {
                return;
            }

            _preClaimTransportFrameNoted = true;

            QueueLog(string.Format(CultureInfo.InvariantCulture,
                "J1939 multipacket traffic is on the bus ({0} from SA 0x{1:X2}) but no source address has been "
                + "claimed yet, so it cannot be reassembled. Any DM1 carrying more than one active fault is being "
                + "MISSED until the claim completes - the fault list is incomplete, not empty. Further occurrences "
                + "are counted, not logged.",
                J1939Pgn.Describe(frame.Pgn), frame.SourceAddress));
        }

        private void DispatchApplicationMessage(J1939Message message, bool wasMultiFrame, int packetCount, string notes)
        {
            Interlocked.Increment(ref _applicationMessagesDispatched);

            if (wasMultiFrame)
            {
                Interlocked.Increment(ref _multiFrameMessagesDispatched);
            }

            J1939ApplicationMessageEventArgs args = new J1939ApplicationMessageEventArgs(
                message, wasMultiFrame, packetCount, _clock.ElapsedMilliseconds, notes);

            Enqueue(delegate
            {
                EventHandler<J1939ApplicationMessageEventArgs> handler = ApplicationMessageReceived;
                if (handler != null)
                {
                    handler(this, args);
                }
            });
        }

        // =====================================================================
        //  Transport lifecycle
        // =====================================================================

        /// <summary>
        /// Send an application message to one node, segmenting over the J1939-21 transport
        /// protocol when it will not fit in a single frame.
        /// </summary>
        /// <param name="priority">J1939 priority, 0..7.</param>
        /// <param name="pgn">Parameter group number, 0..0x3FFFF.</param>
        /// <param name="destinationAddress">Target node, or 255 to broadcast.</param>
        /// <param name="payload">Message bytes. Copied; the caller keeps ownership.</param>
        /// <param name="failureReason">Empty on success; otherwise why nothing was sent.</param>
        /// <returns>True when the transport accepted the message for transmission.</returns>
        /// <remarks>
        /// ADDED for the OEM service layers (Cummins first). Those protocols ride
        /// proprietary PGNs with payloads that are routinely larger than eight bytes — a
        /// Cummins parameter read is twelve — so they need segmentation, and segmentation
        /// is the transport's job, not theirs. Without this the only outbound paths were
        /// RequestPgn and SendClearRequest, neither of which can carry a vendor payload.
        ///
        /// ACCEPTED IS NOT DELIVERED. A true return means the transport took ownership and
        /// will attempt it; completion or failure arrives later on the session's transport
        /// events. A caller that treats this as "the ECM has it" will misreport a machine
        /// that never answered.
        ///
        /// Requires a claimed address: J1939 has no way to send from nowhere, and a frame
        /// sourced from the null address is not a request, it is noise on someone's bus.
        /// </remarks>
        public bool SendApplicationMessage(byte priority, uint pgn, byte destinationAddress,
                                           byte[] payload, out string failureReason)
        {
            failureReason = string.Empty;

            if (payload == null)
            {
                failureReason = "no payload was supplied.";
                return false;
            }

            if (priority > J1939Id.MAX_PRIORITY)
            {
                failureReason = "priority " + priority + " is outside the 3-bit J1939 range (0..7).";
                return false;
            }

            if (pgn > J1939Id.MAX_PGN)
            {
                failureReason = "PGN 0x" + pgn.ToString("X") + " is outside the 18-bit J1939 range.";
                return false;
            }

            if (_disposed)
            {
                failureReason = "the session has been disposed.";
                return false;
            }

            if (!_running)
            {
                failureReason = "the J1939 session is not running.";
                return false;
            }

            byte sourceAddress;
            if (!TryGetTransmitAddress("application message on " + J1939Pgn.Describe(pgn),
                                       out sourceAddress, out failureReason))
            {
                return false;
            }

            J1939TransportProtocol transport = CurrentTransport();
            if (transport == null)
            {
                failureReason = "the transport layer is not available.";
                return false;
            }

            try
            {
                J1939SendTicket ticket = transport.BeginSend(priority, pgn, destinationAddress, payload);
                if (ticket == null)
                {
                    failureReason = "the transport returned no ticket.";
                    return false;
                }

                if (!ticket.Accepted)
                {
                    failureReason = string.IsNullOrEmpty(ticket.RejectReason)
                        ? "the transport rejected the message."
                        : ticket.RejectReason;
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                failureReason = "the transport threw: " + ex.Message;
                return false;
            }
        }

        private J1939TransportProtocol CurrentTransport()
        {
            lock (_transportGate)
            {
                return _transport;
            }
        }

        /// <summary>
        /// Builds, rebuilds or tears down the transport so its bound source address
        /// always matches the address the claim currently holds. Pump thread only.
        /// </summary>
        private void SyncTransportToClaimedAddress()
        {
            // Cleared first: a relocation that happens while we are here re-arms the flag
            // rather than being swallowed by it.
            _addressDirty = false;

            bool hasAddress = _claim.HasClaimedAddress;
            byte address = _claim.ClaimedAddress;

            if (!hasAddress)
            {
                // NOT a teardown. BAM reception is address-independent: a broadcast
                // announcement is addressed to the global address and is never acknowledged,
                // so it reassembles perfectly well while we hold no address of our own.
                //
                // Tearing the transport down here silently dropped every DM1 carrying two or
                // more active DTCs (those exceed 8 bytes and therefore ALWAYS arrive via BAM)
                // for the whole 1250 ms claim window, and permanently if the claim ended in
                // Failed or LostArbitration - while single-DTC DM1s kept rendering normally.
                // A multi-fault machine would quietly under-report, which is the precise
                // failure this layer exists to prevent.
                //
                // The original concern against binding to the null address was real but
                // narrower than a teardown: a transport on 254 could answer an RTS and put
                // CTS traffic on the bus from an address we do not hold. RefuseTransmit
                // removes that, so the listening transport is physically incapable of
                // transmitting; connection-mode transfers simply cannot complete, which is
                // the correct outcome when we are not a bus participant.
                EnsureListenOnlyTransport();
                return;
            }

            J1939TransportProtocol previous;
            byte previousAddress;

            lock (_transportGate)
            {
                if (_transport != null && _transport.SourceAddress == address)
                {
                    return;
                }

                previous = _transport;
                previousAddress = previous == null ? J1939Pgn.NullAddress : previous.SourceAddress;
                _transport = null;
            }

            if (previous != null)
            {
                int inbound = previous.ActiveInboundSessionCount;
                int outbound = previous.ActiveOutboundSessionCount;

                QueueLog(string.Format(CultureInfo.InvariantCulture,
                    "J1939 source address changed from 0x{0:X2} to 0x{1:X2}; rebuilding the transport. {2} inbound "
                    + "and {3} outbound multipacket sessions were bound to the old address and are being abandoned - "
                    + "any message they were carrying is lost, not delayed.",
                    previousAddress, address, inbound, outbound));

                // Reset before unsubscribing so the abandoned sessions still surface as
                // faults rather than disappearing.
                try
                {
                    previous.Reset();
                }
                catch (Exception ex)
                {
                    QueueLog("J1939 transport reset failed while rebinding to a new source address: " + ex.Message);
                }

                previous.MessageReassembled -= OnTransportMessageReassembled;
                previous.SessionFailed -= OnTransportSessionFailed;
                previous.MessageSent -= OnTransportMessageSent;
            }

            J1939TransportProtocol built;

            try
            {
                built = new J1939TransportProtocol(address, TransmitFrame);
            }
            catch (Exception ex)
            {
                QueueLog(string.Format(CultureInfo.InvariantCulture,
                    "J1939 transport could not be created for source address 0x{0:X2}: {1} - multipacket messages "
                    + "cannot be reassembled, so any DM1 with more than one active fault will be missed.",
                    address, ex.Message));
                return;
            }

            built.MessageReassembled += OnTransportMessageReassembled;
            built.SessionFailed += OnTransportSessionFailed;
            built.MessageSent += OnTransportMessageSent;

            lock (_transportGate)
            {
                _transport = built;
            }

            _preClaimTransportFrameNoted = false;

            QueueLog(string.Format(CultureInfo.InvariantCulture,
                "J1939 transport active on source address 0x{0:X2} ({0}). Multipacket messages - including any DM1 "
                + "reporting more than one active fault - will now be reassembled.", address));
        }

        /// <summary>
        /// Frame sink for the listen-only transport held while no source address is claimed.
        /// Always refuses, so that transport can never put a frame on the bus.
        /// </summary>
        /// <remarks>
        /// This is what makes BAM reassembly safe before a claim completes. The transport is bound
        /// to the null address purely so it can follow broadcast sequences; a refused sink means it
        /// cannot answer an RTS with a CTS, so no traffic ever leaves under an address we do not
        /// hold. Connection-mode transfers therefore cannot complete while unclaimed - correct,
        /// because we are not a bus participant yet.
        /// </remarks>
        private bool RefuseTransmit(J1939Message frame)
        {
            return false;
        }

        /// <summary>
        /// Ensures a listen-only transport exists so broadcast (BAM) multipacket messages still
        /// reassemble while the tool holds no source address. Pump thread only.
        /// </summary>
        private void EnsureListenOnlyTransport()
        {
            J1939TransportProtocol previous;

            lock (_transportGate)
            {
                if (_transport != null && _transport.SourceAddress == J1939Pgn.NullAddress)
                {
                    return;   // already listening
                }

                previous = _transport;
                _transport = null;
            }

            if (previous != null)
            {
                try { previous.Reset(); }
                catch (Exception ex)
                {
                    QueueLog("J1939 transport reset failed while dropping to listen-only: " + ex.Message);
                }

                previous.MessageReassembled -= OnTransportMessageReassembled;
                previous.SessionFailed -= OnTransportSessionFailed;
                previous.MessageSent -= OnTransportMessageSent;
            }

            J1939TransportProtocol built;

            try
            {
                built = new J1939TransportProtocol(J1939Pgn.NullAddress, RefuseTransmit);
            }
            catch (Exception ex)
            {
                QueueLog("J1939 listen-only transport could not be created: " + ex.Message
                         + " - broadcast multipacket messages will be missed until an address is claimed.");
                return;
            }

            // Broadcasts are addressed to the global address, not to us, so the transport must be
            // willing to follow sessions it is not the destination of.
            try { built.ReassembleForeignSessions = true; }
            catch (Exception ex)
            {
                QueueLog("J1939 listen-only transport could not enable foreign-session reassembly: " + ex.Message);
            }

            built.MessageReassembled += OnTransportMessageReassembled;
            built.SessionFailed += OnTransportSessionFailed;
            built.MessageSent += OnTransportMessageSent;

            lock (_transportGate)
            {
                _transport = built;
            }

            QueueLog("J1939 transport is LISTEN-ONLY: no source address is held, so broadcast (BAM) multipacket "
                     + "messages - including any DM1 reporting more than one active fault - still reassemble, but "
                     + "nothing can be transmitted and peer-to-peer transfers cannot complete.");
        }

        private void TeardownTransport(string reason)
        {
            J1939TransportProtocol previous;

            lock (_transportGate)
            {
                previous = _transport;
                _transport = null;
            }

            if (previous == null)
            {
                return;
            }

            int inbound = previous.ActiveInboundSessionCount;
            int outbound = previous.ActiveOutboundSessionCount;

            try
            {
                previous.Reset();
            }
            catch (Exception ex)
            {
                QueueLog("J1939 transport reset failed while shutting down: " + ex.Message);
            }

            previous.MessageReassembled -= OnTransportMessageReassembled;
            previous.SessionFailed -= OnTransportSessionFailed;
                previous.MessageSent -= OnTransportMessageSent;

            QueueLog(string.Format(CultureInfo.InvariantCulture,
                "J1939 transport on source address 0x{0:X2} shut down because {1}. {2} inbound and {3} outbound "
                + "multipacket sessions were abandoned.",
                previous.SourceAddress, reason, inbound, outbound));
        }

        private static bool IsTransportFrame(uint pgn)
        {
            return pgn == J1939Pgn.TransportProtocolConnectionManagement
                || pgn == J1939Pgn.TransportProtocolDataTransfer
                || pgn == J1939Pgn.ExtendedTransportProtocolConnectionManagement
                || pgn == J1939Pgn.ExtendedTransportProtocolDataTransfer;
        }

        // =====================================================================
        //  Transmit
        // =====================================================================

        /// <summary>
        /// The <see cref="J1939FrameSink"/> handed to the transport. Returning the
        /// provider's own result matters: the transport reacts to a refused write by
        /// failing the session instead of waiting out a timeout for a frame that never
        /// left the adapter.
        /// </summary>
        private bool TransmitFrame(J1939Message frame)
        {
            if (frame == null)
            {
                QueueLog("J1939 transmit refused: null frame.");
                return false;
            }

            if (_disposed)
            {
                QueueLog("J1939 transmit refused: the session has been disposed.");
                return false;
            }

            try
            {
                if (!_provider.IsConnected)
                {
                    Interlocked.Increment(ref _transmitFailures);
                    QueueLog(string.Format(CultureInfo.InvariantCulture,
                        "J1939 transmit refused: the RP1210 adapter is not connected. {0} did not reach the bus.",
                        frame));
                    return false;
                }

                if (_provider.SendJ1939(frame))
                {
                    return true;
                }

                Interlocked.Increment(ref _transmitFailures);

                QueueLog(string.Format(CultureInfo.InvariantCulture,
                    "J1939 transmit rejected by the adapter: {0}. The frame did NOT reach the bus - the driver's own "
                    + "error text is on the provider's Error event.", frame));

                return false;
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref _transmitFailures);
                QueueLog(string.Format(CultureInfo.InvariantCulture,
                    "J1939 transmit threw for {0}: {1}. The frame did NOT reach the bus.", frame, ex.Message));
                return false;
            }
        }

        /// <summary>
        /// Bus write for the address claim. Reports honestly whether the frame got out.
        /// </summary>
        /// <remarks>
        /// The claim's callback is <c>Func&lt;…,bool&gt;</c>, so returning false here makes the
        /// claim ROLL BACK rather than report an address it never announced. That is the fix for
        /// the case this harness reproduced: State == Claimed, ClaimedAddress == 0xF9, and not one
        /// frame on the bus. With the rollback in place, State and ClaimedAddress can be trusted
        /// again and no longer need DescribeStatus() to contradict them.
        ///
        /// Still does not rethrow: an exception would escape Tick / OnMessageReceived and kill the
        /// pump thread over a single refused write. A false return is ordinary control flow.
        /// </remarks>
        private bool TransmitClaimFrame(J1939Message frame)
        {
            if (TransmitFrame(frame))
            {
                return true;
            }

            Interlocked.Increment(ref _networkManagementTransmitFailures);

            QueueLog("The frame above was J1939-81 network management. The claim has been rolled back to the null "
                + "address rather than report an address that was never announced - the tool is NOT a participant "
                + "on this bus until a claim frame actually reaches it.");

            return false;
        }

        private bool TryGetTransmitAddress(string operationName, out byte sourceAddress, out string failureReason)
        {
            if (_claim.HasClaimedAddress)
            {
                sourceAddress = _claim.ClaimedAddress;
                failureReason = string.Empty;
                return true;
            }

            sourceAddress = J1939Pgn.NullAddress;

            J1939AddressClaimState state = _claim.State;

            switch (state)
            {
                case J1939AddressClaimState.NotStarted:
                    failureReason = operationName + " refused: the address claim has not started, so this tool has no "
                        + "source address. Nothing was transmitted.";
                    break;

                case J1939AddressClaimState.Listening:
                    failureReason = string.Format(CultureInfo.InvariantCulture,
                        "{0} refused: the tool is still listening out the mandatory {1} ms window before it may claim "
                        + "an address (SAE J1939-81). Transmitting now from the null address would be invalid and "
                        + "could collide with a real ECU. Nothing was transmitted; try again in about a second.",
                        operationName, J1939AddressClaim.MINIMUM_LISTEN_MS);
                    break;

                case J1939AddressClaimState.Claiming:
                    failureReason = operationName + " refused: an address claim has been transmitted but the "
                        + "contention window has not expired, so the address is not yet ours to use. Nothing was "
                        + "transmitted.";
                    break;

                case J1939AddressClaimState.LostArbitration:
                    failureReason = operationName + " refused: this tool LOST arbitration for its source address and "
                        + "currently holds none - it is not a participant on the bus. Nothing was transmitted. "
                        + "Nominate a source address you know is free and start a new session.";
                    break;

                default:
                    failureReason = operationName + " refused: the tool FAILED to obtain a source address and is not "
                        + "on the bus. Nothing was transmitted. Every candidate address was already in use; nominate "
                        + "a free address and start a new session.";
                    break;
            }

            QueueLog(failureReason);
            return false;
        }

        // =====================================================================
        //  Sub-machine event forwarding (all invoked on the pump thread)
        // =====================================================================

        private void OnClaimStateChanged(object sender, J1939AddressClaimEventArgs e)
        {
            _addressDirty = true;

            if (e == null)
            {
                return;
            }

            Enqueue(delegate
            {
                EventHandler<J1939AddressClaimEventArgs> handler = AddressClaimStateChanged;
                if (handler != null)
                {
                    handler(this, e);
                }
            });
        }

        private void OnClaimLog(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return;
            }

            QueueLog(text);
        }

        /// <summary>
        /// An outbound application message finished transmitting. Forwarded so a caller can
        /// pace itself — see the remarks on <see cref="ApplicationMessageSent"/>.
        /// </summary>
        private void OnTransportMessageSent(object sender, J1939TransportMessageEventArgs e)
        {
            if (e == null) return;

            var handler = ApplicationMessageSent;
            if (handler == null) return;

            try
            {
                handler(this, e);
            }
            catch (Exception ex)
            {
                // A subscriber that throws must not take down the transport pump: the
                // session would stop reassembling inbound traffic and the machine would
                // look dead for a reason that has nothing to do with the machine.
                QueueLog("J1939 ApplicationMessageSent subscriber threw: " + ex.Message);
            }
        }

        private void OnTransportMessageReassembled(object sender, J1939TransportMessageEventArgs e)
        {
            if (e == null)
            {
                QueueLog("J1939 transport reported a reassembled message with no payload description; it was discarded.");
                return;
            }

            // THE point of this class. A DM1 with one active fault fits in a single frame
            // and arrives through the pump; a DM1 with two or more does not fit and
            // arrives ONLY here, as a BAM. Both go to the same handler so the fault list
            // cannot silently truncate to the single-fault case.
            DispatchApplicationMessage(e.Message, true, e.PacketCount, e.Notes);
        }

        private void OnTransportSessionFailed(object sender, J1939TransportFaultEventArgs e)
        {
            if (e == null)
            {
                return;
            }

            Enqueue(delegate
            {
                EventHandler<J1939TransportFaultEventArgs> handler = TransportFault;
                if (handler != null)
                {
                    handler(this, e);
                }

                EventHandler<string> log = Log;
                if (log != null)
                {
                    log(this, "J1939 transport session failed: " + e.Description);
                }
            });
        }

        // =====================================================================
        //  Notifier thread
        //
        //  Subscribers run here, never on the pump. A handler that blocks for 40 ms
        //  painting a grid is normal for WinForms and fatal for a 50 ms inter-packet
        //  gap, so the two are separated by a queue.
        // =====================================================================

        private void QueueLog(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return;
            }

            Enqueue(delegate
            {
                EventHandler<string> handler = Log;
                if (handler != null)
                {
                    handler(this, text);
                }
            });
        }

        private void Enqueue(Action notification)
        {
            if (notification == null)
            {
                return;
            }

            if (!_notifierAlive)
            {
                // No notifier thread to hand this to: either the session has not been
                // started yet or it has already stopped. Raise on the calling thread
                // rather than queueing into a queue nothing drains - a refusal reason
                // that is never delivered is exactly the silent drop this class forbids,
                // and the refusals that matter most (Start refused, no address claimed,
                // clear not sent) all happen while the session is not running.
                Invoke(notification);
                return;
            }

            lock (_notifyGate)
            {
                if (_notifications.Count >= MAX_QUEUED_NOTIFICATIONS)
                {
                    // Drop the OLDEST. A backlog this deep means the display is seconds
                    // behind the bus, and on a machine under diagnosis the current state
                    // is what the fitter acts on. The count is reported, so the gap is
                    // visible rather than silent.
                    _notifications.Dequeue();
                    _droppedNotifications++;
                }

                _notifications.Enqueue(notification);
                Monitor.Pulse(_notifyGate);
            }
        }

        private void NotifierLoop()
        {
            List<Action> batch = new List<Action>(NOTIFY_BATCH_SIZE);

            while (true)
            {
                int dropped;

                lock (_notifyGate)
                {
                    while (_notifications.Count == 0 && _droppedNotifications == 0 && !_stopNotifier)
                    {
                        Monitor.Wait(_notifyGate, NOTIFY_WAIT_MS);
                    }

                    while (_notifications.Count > 0 && batch.Count < NOTIFY_BATCH_SIZE)
                    {
                        batch.Add(_notifications.Dequeue());
                    }

                    dropped = _droppedNotifications;
                    _droppedNotifications = 0;

                    if (batch.Count == 0 && dropped == 0 && _stopNotifier)
                    {
                        return;
                    }
                }

                if (dropped > 0)
                {
                    // Raised directly rather than queued: reporting a queue overflow by
                    // adding to the queue is how an overflow becomes a loop.
                    RaiseLogDirect(string.Format(CultureInfo.InvariantCulture,
                        "J1939 event backlog overflowed: {0} notifications were discarded because a subscriber could "
                        + "not keep up with the bus. Displayed data has gaps in it - re-read anything you are about "
                        + "to act on.", dropped));
                }

                for (int i = 0; i < batch.Count; i++)
                {
                    Invoke(batch[i]);
                }

                batch.Clear();
            }
        }

        /// <summary>
        /// Raises everything still queued when the notifier thread has gone, on the
        /// calling thread. Closes the window between the notifier's last empty-queue
        /// check and its return, in which a late notification would otherwise be
        /// enqueued and never delivered.
        /// </summary>
        private void DrainResidualNotifications()
        {
            while (true)
            {
                Action pending;

                lock (_notifyGate)
                {
                    pending = _notifications.Count == 0 ? null : _notifications.Dequeue();
                }

                if (pending == null)
                {
                    break;
                }

                Invoke(pending);
            }

            int dropped;

            lock (_notifyGate)
            {
                dropped = _droppedNotifications;
                _droppedNotifications = 0;
            }

            if (dropped > 0)
            {
                RaiseLogDirect(string.Format(CultureInfo.InvariantCulture,
                    "J1939 event backlog overflowed before shutdown: {0} notifications were discarded because a "
                    + "subscriber could not keep up with the bus. The displayed history has gaps in it.", dropped));
            }
        }

        private void Invoke(Action notification)
        {
            try
            {
                notification();
            }
            catch (Exception ex)
            {
                RaiseLogDirect("A J1939 event subscriber threw and its notification was lost: " + ex.Message);
            }
        }

        private void RaiseLogDirect(string text)
        {
            try
            {
                EventHandler<string> handler = Log;
                if (handler != null)
                {
                    handler(this, text);
                }
            }
            catch (Exception ex)
            {
                // The log sink itself is broken; there is nowhere left to report to that
                // is not also broken. Do not let it take down the notifier.
                Debug.WriteLine("J1939Session log handler threw: " + ex.Message);
            }
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException("J1939Session");
            }
        }
    }
}
