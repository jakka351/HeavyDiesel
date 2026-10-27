// ///////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
//  Rp1210Provider - live TMC RP1210 transport for HeavyDiesel.
//
//  Implements IJ1939Link over a vendor's RP1210 DLL.  The DLL name is not
//  known until the technician picks an adapter, so every entry point is bound at
//  runtime (LoadLibrary + GetProcAddress + GetDelegateForFunctionPointer) rather
//  than declared with a static [DllImport].
//
//  DIVISION OF LABOUR (docs/architecture.md): transport is a plugin, protocol is not.
//  This class moves frames and nothing else.  It does NOT run J1939-21 transport
//  protocol, address claim, or any UDS state machine - those live in managed code
//  above it (J1939/J1939TransportProtocol.cs, J1939/J1939AddressClaim.cs) and are
//  wired to the SendJ1939 / J1939MessageReceived members below.
//
//  DEGRADE LOUDLY.  Every place this class cannot do something - a missing export,
//  an unparseable wire format, a full receive queue - it reports the fact through
//  Capabilities.DegradationNotice, the Log event, or a counter.  There is no silent
//  drop path anywhere in this file.  In heavy diagnostics a quietly incomplete
//  parameter list sends someone to pull a gearbox.
//
//  BITNESS: RP1210 is a 32-bit API by definition; see the note in Rp1210Native.cs.
//  This class will load a 32-bit vendor DLL only from a 32-bit process.
//
//  THREADING: RP1210 client calls are serialised on _apiLock, the same posture
//  J2534Manager takes with its _deviceLock.  A dedicated background thread polls
//  RP1210_ReadMessage (the API has no callback / event model) and pumps parsed
//  frames into a BOUNDED queue.
//
//  CORRUPTED-STATE EXCEPTIONS: an AccessViolationException or SEHException raised
//  inside a vendor DLL is a corrupted-state exception, and since .NET 4.0 the CLR
//  does NOT deliver one to a managed catch unless the catching method carries both
//  [HandleProcessCorruptedStateExceptions] and [SecurityCritical].  Every method in
//  this file that calls the vendor DLL and catches one carries both; without them
//  those catch blocks are decoration and the process dies anyway.  The app.config
//  legacyCorruptedStateExceptionsPolicy switch is deliberately NOT used: it is
//  process-wide and would change exception behaviour for the anti-tamper and
//  licensing subsystems too.  The honest caveat: catching a CSE means process state
//  may ALREADY be corrupt, so a handler here exists to report the fault and stop
//  using that transport - not to pretend the driver is still usable.
// ///////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using HeavyDiesel.J1939;
using HeavyDiesel.Transport;

namespace HeavyDiesel.Rp1210
{
    /// <summary>
    /// Which wire format the connected RP1210 client uses.  RP1210 does not report
    /// this; it is inferred from the protocol string handed to ClientConnect, because
    /// the layout of the read buffer differs per family and parsing it as the wrong
    /// family produces confident nonsense.
    /// </summary>
    public enum Rp1210ProtocolFamily
    {
        /// <summary>Protocol string not recognised. Frames are surfaced as raw bytes only.</summary>
        Unknown = 0,

        /// <summary>"J1939", "J1939:Baud=250", "J1939:Baud=500" ...</summary>
        J1939 = 1,

        /// <summary>"CAN", "CAN:Baud=250" ... - raw CAN, no J1939 interpretation by the driver.</summary>
        Can = 2,

        /// <summary>"J1708" - rides RS-485; used by J1587.</summary>
        J1708 = 3,

        /// <summary>"ISO15765" - light-vehicle ISO-TP, where a heavy adapter offers it.</summary>
        Iso15765 = 4,

        /// <summary>"CAT" - Caterpillar Data Link. Proprietary payload; carried opaquely.</summary>
        CaterpillarDataLink = 5,

        /// <summary>"J1850" (either variant).</summary>
        J1850 = 6
    }

    /// <summary>One raw CAN frame as delivered by an RP1210 CAN-family client.</summary>
    public sealed class Rp1210CanFrame
    {
        /// <summary>Raw adapter timestamp exactly as reported (see <see cref="Rp1210Provider.TimestampUnit"/> for its unit).</summary>
        public uint AdapterTimestamp { get; set; }

        /// <summary>True when this frame is the adapter echoing back one of our own transmits.</summary>
        public bool IsTxEcho { get; set; }

        /// <summary>True for a 29-bit identifier, false for 11-bit.</summary>
        public bool IsExtended { get; set; }

        /// <summary>CAN identifier, already masked to 11 or 29 bits.</summary>
        public uint CanId { get; set; }

        /// <summary>Frame payload (0..8 bytes for classic CAN).</summary>
        public byte[] Data { get; set; }

        /// <summary>Single-line log form.</summary>
        /// <returns>The formatted frame.</returns>
        public override string ToString()
        {
            var sb = new StringBuilder(64);
            sb.AppendFormat(CultureInfo.InvariantCulture, "[{0,10}] ", AdapterTimestamp);
            sb.Append(IsTxEcho ? "TX " : "RX ");
            sb.AppendFormat(CultureInfo.InvariantCulture, IsExtended ? "{0:X8}" : "{0:X3}", CanId);
            byte[] d = Data ?? new byte[0];
            sb.AppendFormat(CultureInfo.InvariantCulture, " [{0}]", d.Length);
            for (int i = 0; i < d.Length; i++) sb.AppendFormat(CultureInfo.InvariantCulture, " {0:X2}", d[i]);
            return sb.ToString();
        }
    }

    /// <summary>
    /// One frame off the adapter, already parsed into whatever forms its protocol
    /// family supports.  The raw bytes are always retained so a technician can see
    /// exactly what the driver handed us even when parsing failed.
    /// </summary>
    public sealed class Rp1210Frame
    {
        /// <summary>Protocol family this frame was parsed as.</summary>
        public Rp1210ProtocolFamily Family { get; set; }

        /// <summary>Raw adapter timestamp, big-endian-decoded from the first four buffer bytes.</summary>
        public uint AdapterTimestamp { get; set; }

        /// <summary>True when the adapter is echoing one of our own transmits back to us.</summary>
        public bool IsTxEcho { get; set; }

        /// <summary>The whole read buffer as returned by RP1210_ReadMessage, timestamp included.</summary>
        public byte[] Raw { get; set; }

        /// <summary>Parsed J1939 message, or null when this is not a J1939-family frame or parsing failed.</summary>
        public J1939Message J1939 { get; set; }

        /// <summary>Parsed CAN frame, or null when this is not a CAN-family frame or parsing failed.</summary>
        public Rp1210CanFrame Can { get; set; }

        /// <summary>Protocol payload with the RP1210 header stripped, for families that have no
        /// structured parse here (J1708, CAT, J1850). Null when a structured parse succeeded.</summary>
        public byte[] Payload { get; set; }

        /// <summary>Non-empty when the frame arrived but could not be parsed. Never silently discarded.</summary>
        public string ParseError { get; set; }
    }

    /// <summary>
    /// Live RP1210 transport.  Construct with a vendor + device from
    /// <see cref="Rp1210Catalog"/>, call <see cref="Connect(string)"/> with a protocol string,
    /// then use either the single-frame request/response members or
    /// the J1939 members below.
    /// </summary>
    /// <remarks>
    /// The interface's SendReceive/ReadResponse shape suits UDS, which is a
    /// request/response protocol.  J1939 is neither: it is broadcast and
    /// connection-oriented.  The J1939 members (<see cref="SendJ1939"/>,
    /// <see cref="J1939MessageReceived"/>, <see cref="DrainJ1939(int)"/>) are the correct
    /// surface for heavy work and are what J1939TransportProtocol and J1939AddressClaim
    /// are wired to.
    /// </remarks>
    // IJ1939Link was derived from what J1939Session already called on this class, so a
    // J2534-, SocketCAN- or simulator-backed link can stand in the same place. See
    // Transport/IJ1939Link.cs.
    public sealed class Rp1210Provider : IJ1939Link
    {
        // ==================================================================
        //  Wire-format constants.
        //  Anything here that is not established beyond doubt is named, given a
        //  reason, and listed for bench confirmation rather than buried in an
        //  expression.  Guessing at a wire format silently is how a tool ends up
        //  confidently reporting the wrong parameter.
        // ==================================================================

        /// <summary>Bytes of big-endian adapter timestamp at the head of every read buffer.
        /// RP1210 defines this for all protocols; it is not vendor-specific.</summary>
        private const int TimestampBytes = 4;

        /// <summary>
        /// The J1939 message header common to read and write: [PGN 3, LSB first][priority 1]
        /// [source 1][destination 1]. On READ it is preceded by the 4-byte timestamp and, ONLY
        /// when echo mode is ON, a 1-byte echo indicator (see <see cref="_echoBytePresent"/>);
        /// the read parser adds those two offsets. On WRITE there is neither timestamp nor echo
        /// byte. The old fixed "read header = 7" hard-coded the echo byte as always present,
        /// which is the RP1210 echo-OFF dead-bus bug this const + _echoBytePresent replace.
        /// </summary>
        private const int J1939MessageHeaderBytes = 6;

        /// <summary>J1939 write layout (no timestamp, no echo byte):
        /// [PGN 3, LSB first][priority 1][source 1][destination 1][data ...].</summary>
        private const int J1939WriteHeaderBytes = 6;

        /// <summary>
        /// CAN read layout after the timestamp:
        /// [echo 1][extended flag 1][CAN id 2 bytes if standard / 4 bytes if extended][data ...].
        /// BENCH-CONFIRM: the identifier is decoded BIG-ENDIAN (MSB first), which is what
        /// the RP1210 recommended practice specifies and what the TU-RP1210 reference does.
        /// Vendors have been observed to disagree.  A wrong endianness is obvious on a
        /// bench capture - a J1939 broadcast reads as an absurd 11-bit id - so confirm
        /// against a known DM1 before trusting raw-CAN mode on a new adapter.
        /// </summary>
        private static readonly bool CanIdentifierIsBigEndian = true;

        /// <summary>Standard (11-bit) CAN identifiers occupy two bytes in the RP1210 CAN buffer.</summary>
        private const int CanStandardIdBytes = 2;

        /// <summary>Extended (29-bit) CAN identifiers occupy four bytes in the RP1210 CAN buffer.</summary>
        private const int CanExtendedIdBytes = 4;

        /// <summary>
        /// RP1210_GetHardwareStatusEx is tried only when plain RP1210_GetHardwareStatus is
        /// absent, and is called through the SAME delegate shape.  BENCH-CONFIRM: RP1210C
        /// documents Ex with the same argument list, but vendors differ.  If a driver only
        /// exports Ex and hardware status comes back as garbage, unbind it here rather than
        /// reinterpreting the buffer.
        /// </summary>
        private const bool UseHardwareStatusExFallback = true;

        /// <summary>
        /// Microseconds per timestamp tick assumed when the vendor INI omits TimestampWeight.
        /// The RP1210 recommended practice states 1000 (1 ms) as the default; when this
        /// assumption is in force it is stated in <see cref="InterfaceCapabilities.DegradationNotice"/>
        /// rather than being applied quietly.
        /// </summary>
        private const int DefaultTimestampWeightMicroseconds = 1000;

        /// <summary>
        /// RP1210_ClientConnect's nIsAppPacketizingIncomingMsgs argument.  1 means THIS
        /// APPLICATION performs J1939-21 packetizing and reassembly; 0 hands that job to the
        /// vendor driver.  The HeavyDiesel layering rule (docs/architecture.md) keeps the transport state machine in managed code,
        /// so this is 1 and is never configurable.
        /// </summary>
        private const short AppPacketizesIncomingMessages = 1;

        /// <summary>
        /// Escape hatch for the rare driver that refuses nIsAppPacketizingIncomingMsgs=1 at
        /// ClientConnect. When true the driver is asked to run J1939-21 reassembly itself
        /// (ClientConnect is called with 0). Off by default, and deliberately not the default
        /// for anyone: a driver-reassembled payload bypasses J1939TransportProtocol along
        /// with its sequence checking, timeouts and TimingDegraded reporting, and
        /// ParseJ1939Into reports that condition the moment it sees it on the wire. Set it
        /// BEFORE Connect; it is read at ClientConnect time only.
        /// </summary>
        public bool DriverReassembly { get; set; }

        /// <summary>Longest payload a classic CAN frame can carry.  A J1939 "frame" arriving
        /// with more data than this was reassembled by somebody, and it was not us.</summary>
        private const int MaxSingleFrameDataBytes = 8;

        /// <summary>Receive queue depth in frames.  A fully loaded 250 kbps J1939 bus runs
        /// roughly 2000 frames/second, so this is about four seconds of slack for a consumer
        /// that stalls (UI repaint, disk write).  Beyond that we drop and say so.</summary>
        public const int DefaultReceiveQueueCapacity = 8192;

        /// <summary>Vendor DLL buffer handed to ReadMessage. RP1210 fixes this at 2000 bytes.</summary>
        private const int ReadBufferBytes = Rp1210Const.BufferSize;

        /// <summary>Returned by <see cref="SendCommand"/> when there is no client to send to.
        /// Outside the RP1210 error range, so it can never be mistaken for a driver code.</summary>
        public const short NotConnectedErrorCode = 0x7FFF;

        // ==================================================================
        //  Bound entry points
        // ==================================================================

        private IntPtr _dll = IntPtr.Zero;
        private Rp1210Native.ClientConnectDelegate _clientConnect;
        private Rp1210Native.ClientDisconnectDelegate _clientDisconnect;
        private Rp1210Native.SendMessageDelegate _sendMessage;
        private Rp1210Native.ReadMessageDelegate _readMessage;
        private Rp1210Native.SendCommandDelegate _sendCommand;
        private Rp1210Native.ReadVersionDelegate _readVersion;                 // optional
        private Rp1210Native.ReadDetailedVersionDelegate _readDetailedVersion; // optional
        private Rp1210Native.GetHardwareStatusDelegate _getHardwareStatus;     // optional
        private Rp1210Native.GetErrorMsgDelegate _getErrorMsg;                 // optional but expected
        private Rp1210Native.GetLastErrorMsgDelegate _getLastErrorMsg;         // optional

        // ==================================================================
        //  State
        // ==================================================================

        private readonly Rp1210VendorInfo _vendor;
        private readonly Rp1210DeviceInfo _device;
        private readonly int _queueCapacity;

        /// <summary>Serialises every call into the vendor DLL.  Vendor DLLs are not
        /// documented as thread-safe and several are demonstrably not: the receive thread
        /// and a UI-thread send must not be inside the driver at the same time.</summary>
        private readonly object _apiLock = new object();

        private readonly object _queueLock = new object();
        private readonly Queue<Rp1210Frame> _queue = new Queue<Rp1210Frame>();

        private short _clientId = -1;
        private bool _connected;

        /// <summary>
        /// Set when a call into the vendor DLL raised a corrupted-state exception. Once
        /// true the session is dead and must not be reused: process state may already be
        /// damaged, so retrying into the same driver risks compounding it. Cleared only by
        /// a fresh Connect, which reloads the module. Volatile because the receive thread
        /// and the caller's thread both observe it.
        /// </summary>
        private volatile bool _driverFaulted;

        /// <summary>Volatile: Dispose runs on whatever thread tears the session down, while
        /// the receive thread and any blocked SendReceive/ReadResponse must observe it.</summary>
        private volatile bool _disposed;
        private string _protocolString = string.Empty;
        private Rp1210ProtocolFamily _family = Rp1210ProtocolFamily.Unknown;

        private Thread _rxThread;
        private volatile bool _rxRun;

        /// <summary>False once a receive thread has been abandoned inside the vendor
        /// driver. Gates FreeLibrary — see UnloadDriver.</summary>
        private volatile bool _rxStoppedCleanly = true;

        /// <summary>How long to wait for the receive thread to leave the driver before
        /// declaring it wedged and leaking the module handle.</summary>
        private const int ReceiveThreadJoinTimeoutMs = 1500;

        private long _framesReceived;
        private long _framesDropped;
        private long _parseFailures;
        private long _pgnPsCollisions;

        /// <summary>0 until the driver is caught delivering a message it reassembled itself,
        /// 1 once that has been reported. Set with Interlocked from the receive thread, so it
        /// is a plain int rather than volatile (CS0420).</summary>
        private int _driverReassemblyDetected;

        /// <summary>Bucket index of the last overflow report. Starts at -1, not 0, so the
        /// FIRST dropped frame reports immediately - starting at 0 would swallow the first
        /// thousand drops, which is the silent loss this design exists to prevent.</summary>
        private int _lastDropReportAt = -1;

        private int _timestampWeightUs = DefaultTimestampWeightMicroseconds;
        private bool _timestampWeightAssumed = true;
        private J1939TimestampUnit _timestampUnit = J1939TimestampUnit.Unknown;

        /// <summary>
        /// True when the driver is prepending the 1-byte RP1210 echo indicator to every read
        /// buffer, which it does ONLY while "Echo Transmitted Messages" (command 16) is ON.
        /// Set at connect from the ACTUAL negotiated echo state (see ConfigurePostConnect), NOT
        /// assumed. The read parsers (ParseJ1939Into / ParseCanInto) key their byte offsets off
        /// this: hard-coding the echo byte as present while the shipped default is echo-OFF
        /// shifts PGN/SA/DA one byte early and mangles every received frame (SA reads as the DA,
        /// PGN takes the priority byte in its high bits) - a silent, fully-dead J1939 bus.
        /// </summary>
        private volatile bool _echoBytePresent;

        private readonly List<string> _bindingWarnings = new List<string>();
        private InterfaceCapabilities _caps;

        // Request/response path (SendReceive / ReadResponse).  Non-echo frames are
        // MIRRORED into a short ring carrying a monotonic sequence number, and the
        // request path consumes by cursor.  Two consequences that matter:
        //   - a frame is never removed from the main queue to satisfy a request, so a
        //     J1939 consumer draining the queue cannot lose a message to a UDS-shaped
        //     call happening on another thread;
        //   - ReadResponse can pick up a frame that arrived before it was called, which
        //     is exactly the NRC 0x78 race a register-a-matcher design loses.
        private readonly object _respLock = new object();
        private readonly Queue<SequencedFrame> _respRing = new Queue<SequencedFrame>();
        private readonly ManualResetEvent _respSignal = new ManualResetEvent(false);
        private long _respSequence;
        private long _respCursor;
        private long _respRingDiscards;

        /// <summary>Depth of the request/response mirror ring. Small on purpose: it only has
        /// to span one request round trip, and older entries are of no use to a caller.</summary>
        private const int ResponseRingCapacity = 64;

        private struct SequencedFrame
        {
            public long Seq;
            public Rp1210Frame Frame;
        }

        // ==================================================================
        //  Events
        // ==================================================================

        /// <summary>Human-readable progress and warnings. Wire this to the flash/diagnostic log.</summary>
        public event Action<string> Log;

        /// <summary>Errors the technician must see. Never used for routine no-data-waiting cases.</summary>
        public event Action<string> Error;

        /// <summary>
        /// Raised for every parsed J1939 message, including our own echoes (check
        /// <see cref="J1939Message"/> against the frame's echo flag via
        /// <see cref="FrameReceived"/> if that matters).
        /// RAISED ON THE RECEIVE THREAD: a slow handler stalls the pump and causes drops.
        /// Marshal to the UI thread inside the handler, do not block here.
        /// </summary>
        public event Action<J1939Message> J1939MessageReceived;

        /// <summary>Raised for every frame of any family, parsed or not.
        /// Same receive-thread caveat as <see cref="J1939MessageReceived"/>.</summary>
        public event Action<Rp1210Frame> FrameReceived;

        // ==================================================================
        //  Construction
        // ==================================================================

        /// <summary>Create a provider for a catalog-enumerated adapter.</summary>
        /// <param name="vendor">Vendor API from <see cref="Rp1210Catalog.EnumerateVendors"/>. Its ApiName is the DLL base name.</param>
        /// <param name="device">Device from that vendor's device list.</param>
        /// <exception cref="ArgumentNullException">Either argument is null.</exception>
        public Rp1210Provider(Rp1210VendorInfo vendor, Rp1210DeviceInfo device)
            : this(vendor, device, DefaultReceiveQueueCapacity)
        {
        }

        /// <summary>Create a provider with an explicit receive queue depth.</summary>
        /// <param name="vendor">Vendor API from <see cref="Rp1210Catalog.EnumerateVendors"/>.</param>
        /// <param name="device">Device from that vendor's device list.</param>
        /// <param name="receiveQueueCapacity">Maximum queued frames before the oldest are dropped and counted.</param>
        /// <exception cref="ArgumentNullException">Either argument is null.</exception>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="receiveQueueCapacity"/> is below 64.</exception>
        public Rp1210Provider(Rp1210VendorInfo vendor, Rp1210DeviceInfo device, int receiveQueueCapacity)
        {
            if (vendor == null) throw new ArgumentNullException("vendor");
            if (device == null) throw new ArgumentNullException("device");
            if (receiveQueueCapacity < 64)
                throw new ArgumentOutOfRangeException("receiveQueueCapacity", "A receive queue below 64 frames will drop traffic on any live heavy bus.");

            _vendor = vendor;
            _device = device;
            _queueCapacity = receiveQueueCapacity;

            ReadTimestampWeight();
        }

        // ==================================================================
        //  Observable state
        // ==================================================================

        /// <summary>True when a client session is open on the adapter.</summary>
        public bool IsConnected
        {
            get { return _connected && _clientId >= 0 && !_driverFaulted; }
        }

        /// <summary>RP1210 client ID, or -1 when not connected.</summary>
        public short ClientId
        {
            get { return _clientId; }
        }

        /// <summary>Protocol string this client was opened with, e.g. "J1939:Baud=250" or "CAT".</summary>
        public string ProtocolString
        {
            get { return _protocolString; }
        }

        /// <summary>Wire format the read buffer is being parsed as.</summary>
        public Rp1210ProtocolFamily Family
        {
            get { return _family; }
        }

        /// <summary>Total frames read off the adapter since connect.</summary>
        public long FramesReceived
        {
            get { return Interlocked.Read(ref _framesReceived); }
        }

        /// <summary>
        /// Frames discarded because the receive queue was full.  A non-zero value means the
        /// capture is incomplete and any transport reassembly running over it may be wrong.
        /// This is surfaced, never swallowed.
        /// </summary>
        public long DroppedFrameCount
        {
            get { return Interlocked.Read(ref _framesDropped); }
        }

        /// <summary>Frames that arrived but could not be parsed into their protocol family.</summary>
        public long ParseFailureCount
        {
            get { return Interlocked.Read(ref _parseFailures); }
        }

        /// <summary>Frames queued and not yet drained.</summary>
        public int QueueDepth
        {
            get { lock (_queueLock) { return _queue.Count; } }
        }

        /// <summary>
        /// Unit of the adapter timestamps carried on received messages, derived from the
        /// vendor INI TimestampWeight.  <see cref="J1939TimestampUnit.Unknown"/> means
        /// timing falls back to the host clock and says so on every message.
        /// </summary>
        public J1939TimestampUnit TimestampUnit
        {
            get { return _timestampUnit; }
        }

        /// <summary>
        /// Microseconds per adapter timestamp tick, from the vendor INI.  When the INI states
        /// no TimestampWeight this reports the RP1210 default FOR DISPLAY ONLY:
        /// <see cref="TimestampUnit"/> is then <see cref="J1939TimestampUnit.Unknown"/> and no
        /// timestamp is converted with this value.
        /// </summary>
        public int TimestampWeightMicroseconds
        {
            get { return _timestampWeightUs; }
        }

        // ==================================================================
        //  Timestamp weight
        //
        //  RP1210 timestamps are a free-running tick count; the vendor INI states
        //  microseconds-per-tick in [VendorInformation] TimestampWeight.  Without it
        //  a raw uint cannot be converted to milliseconds without inventing a scale,
        //  which is why J1939Message carries the unit rather than assuming one.
        //  Rp1210Catalog does not expose this key, so it is read here.
        // ==================================================================

        private void ReadTimestampWeight()
        {
            int weight = 0;
            try
            {
                string windir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
                if (!string.IsNullOrEmpty(windir) && !string.IsNullOrEmpty(_vendor.ApiName))
                {
                    string path = Path.Combine(windir, _vendor.ApiName + ".INI");
                    if (File.Exists(path))
                    {
                        foreach (string rawLine in File.ReadAllLines(path))
                        {
                            string line = rawLine.Trim();
                            if (line.Length == 0 || line[0] == ';' || line[0] == '#' || line[0] == '[') continue;
                            int eq = line.IndexOf('=');
                            if (eq <= 0) continue;
                            if (!line.Substring(0, eq).Trim().Equals("TimestampWeight", StringComparison.OrdinalIgnoreCase)) continue;

                            string value = line.Substring(eq + 1).Trim();
                            int sc = value.IndexOf(" ;", StringComparison.Ordinal);
                            if (sc >= 0) value = value.Substring(0, sc).Trim();
                            int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out weight);
                            break;
                        }
                    }
                }
            }
            catch (IOException) { weight = 0; }
            catch (UnauthorizedAccessException) { weight = 0; }

            if (weight > 0)
            {
                _timestampWeightUs = weight;
                _timestampWeightAssumed = false;
            }
            else
            {
                _timestampWeightUs = DefaultTimestampWeightMicroseconds;
                _timestampWeightAssumed = true;
            }

            if (_timestampWeightAssumed)
            {
                // The INI omitted TimestampWeight, or could not be read at all. There is no
                // tick scale here - only a guess at one - and a guessed scale must never be
                // dressed up as an adapter capability. Reporting Milliseconds would stamp
                // every message J1939TimestampSource.Adapter and run the J1939-21 connection
                // mode timers (50 ms inter-packet, 200 ms Tr, 750 ms Th, 1250 ms T1-T4) off a
                // FABRICATED tick rate while the transport layer believed them wire-grade.
                // Unknown makes J1939Message fall back to the host monotonic clock, report
                // TimestampSource = HostClock, propagate TimingDegraded through
                // J1939TransportProtocol, and makes Capabilities.HardwareTimestamps false -
                // the honest answer. _timestampWeightUs keeps the RP1210 default purely so
                // TimestampWeightMicroseconds has a number to display; nothing converts with it.
                _timestampUnit = J1939TimestampUnit.Unknown;
            }
            else if (_timestampWeightUs == 1)
            {
                _timestampUnit = J1939TimestampUnit.Microseconds;
            }
            else
            {
                // J1939Message models microseconds and milliseconds. A declared weight that is
                // neither is scaled to milliseconds on the way in (see ScaleTimestamp) and
                // reported as such.
                _timestampUnit = J1939TimestampUnit.Milliseconds;
            }
        }

        /// <summary>
        /// Convert a raw adapter tick count to the unit reported by <see cref="TimestampUnit"/>.
        /// Weights of 1 (us) and 1000 (ms) pass through untouched, so the 2^32 wrap that
        /// J1939Message.ElapsedMsBetween handles stays intact.  Any other weight is rescaled,
        /// which moves the wrap point - an interval measured across a rollover on such an
        /// adapter will be wrong, and that is noted in the degradation notice.
        /// </summary>
        private uint ScaleTimestamp(uint rawTicks)
        {
            if (_timestampWeightUs == 1 || _timestampWeightUs == 1000) return rawTicks;
            ulong ms = ((ulong)rawTicks * (ulong)_timestampWeightUs) / 1000UL;
            return unchecked((uint)ms);
        }

        // ==================================================================
        //  DLL binding
        // ==================================================================

        /// <summary>
        /// Load the vendor DLL and bind its entry points.  The five core exports
        /// (ClientConnect, ClientDisconnect, SendMessage, ReadMessage, SendCommand) are
        /// mandatory - without any one of them the adapter cannot be driven and this
        /// returns false.  ReadVersion, ReadDetailedVersion, GetHardwareStatus[Ex] and
        /// GetLastErrorMsg are optional; a missing one is recorded and surfaced, not fatal.
        /// </summary>
        /// <param name="failureReason">Why binding failed, when it did. Empty on success.</param>
        /// <returns>True when the DLL loaded and all five core exports resolved.</returns>
        public bool LoadDriver(out string failureReason)
        {
            failureReason = string.Empty;
            if (_disposed) throw new ObjectDisposedException("Rp1210Provider");
            if (_dll != IntPtr.Zero) return true;

            string dllName = (_vendor.ApiName ?? string.Empty).Trim();
            if (dllName.Length == 0)
            {
                failureReason = "Vendor API name is empty - the RP1210 catalog entry is malformed.";
                RaiseError(failureReason);
                return false;
            }

            _bindingWarnings.Clear();

            // LoadLibrary follows the normal search order, which finds the vendor DLL in
            // %WINDIR%\System32 (or SysWOW64 for this 32-bit process) where RP1210 drivers
            // install themselves. No path is prepended: hard-coding one breaks vendors that
            // install elsewhere and register via RP121032.INI.
            _dll = Rp1210Native.LoadLibrary(dllName + ".dll");
            if (_dll == IntPtr.Zero)
            {
                int win32 = Marshal.GetLastWin32Error();
                failureReason = string.Format(CultureInfo.InvariantCulture,
                    "Cannot load {0}.dll (Win32 error {1}). {2}",
                    dllName, win32,
                    win32 == 193
                        ? "Error 193 is a bitness mismatch: RP1210 drivers are 32-bit and this process is 64-bit. The host process must be built x86."
                        : "The driver is registered in RP121032.INI but is not installed or is missing a dependency.");
                RaiseError(failureReason);
                return false;
            }

            _clientConnect    = Bind<Rp1210Native.ClientConnectDelegate>("RP1210_ClientConnect", true);
            _clientDisconnect = Bind<Rp1210Native.ClientDisconnectDelegate>("RP1210_ClientDisconnect", true);
            _sendMessage      = Bind<Rp1210Native.SendMessageDelegate>("RP1210_SendMessage", true);
            _readMessage      = Bind<Rp1210Native.ReadMessageDelegate>("RP1210_ReadMessage", true);
            _sendCommand      = Bind<Rp1210Native.SendCommandDelegate>("RP1210_SendCommand", true);

            _getErrorMsg         = Bind<Rp1210Native.GetErrorMsgDelegate>("RP1210_GetErrorMsg", false);
            _readVersion         = Bind<Rp1210Native.ReadVersionDelegate>("RP1210_ReadVersion", false);
            _readDetailedVersion = Bind<Rp1210Native.ReadDetailedVersionDelegate>("RP1210_ReadDetailedVersion", false);
            _getLastErrorMsg     = Bind<Rp1210Native.GetLastErrorMsgDelegate>("RP1210_GetLastErrorMsg", false);

            _getHardwareStatus = Bind<Rp1210Native.GetHardwareStatusDelegate>("RP1210_GetHardwareStatus", false);
            if (_getHardwareStatus == null && UseHardwareStatusExFallback)
            {
                _getHardwareStatus = Bind<Rp1210Native.GetHardwareStatusDelegate>("RP1210_GetHardwareStatusEx", false);
                if (_getHardwareStatus != null)
                    _bindingWarnings.Add("hardware status is served by RP1210_GetHardwareStatusEx (argument list assumed identical - unconfirmed on this vendor)");
            }

            if (_clientConnect == null || _clientDisconnect == null || _sendMessage == null ||
                _readMessage == null || _sendCommand == null)
            {
                failureReason = string.Format(CultureInfo.InvariantCulture,
                    "{0}.dll is missing a mandatory RP1210 export ({1}). It is not a usable RP1210 driver.",
                    dllName, string.Join(", ", MissingCoreExports().ToArray()));
                RaiseError(failureReason);
                UnloadDriver(true);   // receive thread never started at bind time
                return false;
            }

            if (_bindingWarnings.Count > 0)
                RaiseLog("RP1210 driver " + dllName + ".dll bound with reduced capability: " + string.Join("; ", _bindingWarnings.ToArray()));
            else
                RaiseLog("RP1210 driver " + dllName + ".dll bound (all entry points present).");

            return true;
        }

        private List<string> MissingCoreExports()
        {
            var missing = new List<string>(5);
            if (_clientConnect == null) missing.Add("RP1210_ClientConnect");
            if (_clientDisconnect == null) missing.Add("RP1210_ClientDisconnect");
            if (_sendMessage == null) missing.Add("RP1210_SendMessage");
            if (_readMessage == null) missing.Add("RP1210_ReadMessage");
            if (_sendCommand == null) missing.Add("RP1210_SendCommand");
            return missing;
        }

        private T Bind<T>(string exportName, bool mandatory) where T : class
        {
            IntPtr p = Rp1210Native.GetProcAddress(_dll, exportName);
            if (p == IntPtr.Zero)
            {
                if (!mandatory)
                    _bindingWarnings.Add(exportName + " not exported");
                return null;
            }
            try
            {
                return Marshal.GetDelegateForFunctionPointer(p, typeof(T)) as T;
            }
            catch (ArgumentException ex)
            {
                _bindingWarnings.Add(exportName + " could not be bound (" + ex.Message + ")");
                return null;
            }
        }

        /// <summary>
        /// Release the delegates and unmap the vendor DLL.
        /// </summary>
        /// <param name="receiveThreadStopped">
        /// Must be true only when the receive thread is provably no longer running.
        /// When false the module handle is deliberately leaked: calling FreeLibrary while
        /// a thread is still inside the vendor's code unmaps executing instructions and
        /// takes the process down with an access violation that no managed handler can
        /// catch. A leaked handle is strictly the lesser failure.
        /// </param>
        [System.Runtime.ExceptionServices.HandleProcessCorruptedStateExceptions]
        [System.Security.SecurityCritical]
        private void UnloadDriver(bool receiveThreadStopped)
        {
            _clientConnect = null;
            _clientDisconnect = null;
            _sendMessage = null;
            _readMessage = null;
            _sendCommand = null;
            _readVersion = null;
            _readDetailedVersion = null;
            _getHardwareStatus = null;
            _getErrorMsg = null;
            _getLastErrorMsg = null;

            if (_dll != IntPtr.Zero)
            {
                if (receiveThreadStopped)
                {
                    try { Rp1210Native.FreeLibrary(_dll); }
                    catch (SEHException) { /* a driver that faults on unload must not take the app with it */ }
                }
                // else: intentionally leaked, see the parameter doc.
                _dll = IntPtr.Zero;
            }
        }

        // ==================================================================
        //  Connect / disconnect
        // ==================================================================

        /// <summary>
        /// Open a client session.  The protocol string is passed straight through to
        /// RP1210_ClientConnect, so anything the vendor declares in its INI is reachable
        /// without a code change - including "CAT" (Caterpillar Data Link), which several
        /// installed vendors expose.
        /// </summary>
        /// <param name="protocolString">
        /// ASCII protocol string, for example "J1939:Baud=250", "J1939:Baud=500", "J1708",
        /// "CAN", "CAT".  Take it from <see cref="Rp1210VendorInfo.Protocols"/> so it matches
        /// what this vendor actually declares.
        /// </param>
        /// <param name="echoTransmits">
        /// True to have the adapter echo our own transmits back on the receive queue.  Echo
        /// on is the default because it is the only reliable way to tell tool traffic from
        /// vehicle traffic in a capture, and it confirms a frame actually reached the wire.
        /// </param>
        /// <param name="failureReason">Why the connect failed, in technician-readable form. Empty on success.</param>
        /// <returns>True when a client session is open and the receive thread is running.</returns>
        /// <exception cref="ArgumentException"><paramref name="protocolString"/> is null or blank.</exception>
        public bool Connect(string protocolString, bool echoTransmits, out string failureReason)
        {
            if (_disposed) throw new ObjectDisposedException("Rp1210Provider");
            if (string.IsNullOrWhiteSpace(protocolString))
                throw new ArgumentException("A protocol string is required, e.g. \"J1939:Baud=250\".", "protocolString");

            failureReason = string.Empty;
            if (IsConnected) return true;

            if (!LoadDriver(out failureReason)) return false;

            string proto = protocolString.Trim();
            short ret;
            lock (_apiLock)
            {
                // hwnd is IntPtr.Zero: RP1210's window-message notification path is unused.
                // Buffer sizes 0 request the driver defaults, which every vendor sizes for
                // its own hardware better than we can guess.
                //
                // The last argument is nIsAppPacketizingIncomingMsgs and it must be 1. Passing
                // 0 asks the DRIVER to run J1939-21 reassembly, which contradicts the layering rule
                // and would hand us a 1785-byte BAM payload dressed as a single CAN frame -
                // bypassing J1939TransportProtocol along with its sequence checking, timeouts,
                // abort handling and TimingDegraded reporting. Not every vendor honours the
                // flag, so the condition is ALSO detected on the wire; see ParseJ1939Into.
                //
                // Default stays 1 (this const). DriverReassembly is the documented escape hatch
                // for drivers that refuse 1 at ClientConnect: ParseJ1939Into still detects driver
                // reassembly on the wire, so a 0 session degrades honestly rather than silently.
                short pktz = DriverReassembly ? (short)0 : AppPacketizesIncomingMessages;
                ret = _clientConnect(IntPtr.Zero, (short)_device.DeviceId, proto, 0, 0, pktz);
            }

            // THE CLASSIC RP1210 BUG: the return value is overloaded. Below 128 it is the
            // client ID; at or above 128 it is an error code. Treating a 129 as a client ID
            // gets you a session that silently never works. Negative values are not in the
            // recommended practice but some drivers return them, so they are errors too.
            if (ret < 0 || ret >= Rp1210Const.MaxClientId)
            {
                short code = ret < 0 ? (short)(-ret) : ret;
                failureReason = string.Format(CultureInfo.InvariantCulture,
                    "RP1210_ClientConnect(device {0}, \"{1}\") failed: {2}",
                    _device.DeviceId, proto, DescribeError(code));
                RaiseError(failureReason);
                return false;
            }

            _clientId = ret;
            _protocolString = proto;
            _family = ClassifyProtocol(proto);
            _connected = true;
            _driverFaulted = false;   // fresh session on a freshly bound driver
            _caps = null;   // capabilities are protocol-dependent; rebuild on next read

            Interlocked.Exchange(ref _framesReceived, 0);
            Interlocked.Exchange(ref _framesDropped, 0);
            Interlocked.Exchange(ref _parseFailures, 0);
            Interlocked.Exchange(ref _pgnPsCollisions, 0);
            Interlocked.Exchange(ref _driverReassemblyDetected, 0);
            _lastDropReportAt = -1;
            lock (_queueLock) _queue.Clear();

            RaiseLog(string.Format(CultureInfo.InvariantCulture,
                "RP1210 connected: {0} device {1} protocol \"{2}\" client {3} (parsing as {4}).",
                _vendor.ApiName, _device.DeviceId, proto, _clientId, _family));

            ConfigurePostConnect(echoTransmits);
            StartReceiveThread();
            return true;
        }

        /// <summary>Open a client session with transmit echo enabled.</summary>
        /// <param name="protocolString">ASCII RP1210 protocol string.</param>
        /// <returns>True when connected.</returns>
        public bool Connect(string protocolString)
        {
            string reason;
            return Connect(protocolString, true, out reason);
        }

        /// <summary>
        /// Post-connect setup.  Filters default to a discard-everything state on many
        /// drivers, so a client that never calls SetAllFiltersStatesToPass sits there
        /// reading nothing on a perfectly healthy bus.
        /// </summary>
        private void ConfigurePostConnect(bool echoTransmits)
        {
            // Pass all traffic. Filtering happens in managed code, where it can be changed
            // per investigation step without renegotiating with the driver.
            short r = SendCommand(Rp1210Command.SetAllFiltersStatesToPass, new byte[1], 0);
            if (r != 0)
                RaiseError("RP1210 SetAllFiltersStatesToPass failed: " + DescribeError(r) +
                           " - the adapter may deliver no traffic at all.");

            // NOTE the argument order inside SendCommand: command number FIRST, then client
            // ID. Reversing them is the other classic RP1210 bug and usually presents as a
            // driver that accepts every command and does nothing.
            byte[] echo = new byte[1];
            echo[0] = echoTransmits ? Rp1210Const.EchoOn : Rp1210Const.EchoOff;
            r = SendCommand(Rp1210Command.EchoTransmittedMessages, echo, 1);
            if (r != 0)
                RaiseLog("RP1210 EchoTransmittedMessages(" + (echoTransmits ? "on" : "off") + ") rejected: " +
                         DescribeError(r) + " - transmit confirmation is unavailable on this adapter.");

            // The driver prepends the 1-byte echo indicator to every read buffer ONLY while echo
            // mode is actually ON. That is true only when we asked for it AND the command was
            // accepted: a ClientConnect resets echo to OFF, so a request we did not send, or one
            // the driver rejected, leaves echo OFF and no echo byte. The read parsers key their
            // byte offsets off this - get it wrong and every received frame is shifted one byte.
            _echoBytePresent = echoTransmits && r == 0;
        }

        /// <summary>
        /// Send an RP1210 command to this client.  Exposed so callers can reach vendor
        /// commands (address protection, filter type, interpacket time) without this class
        /// having to model each one.
        /// </summary>
        /// <param name="commandNumber">A value from <c>Rp1210Command</c>.</param>
        /// <param name="buffer">Command payload. Pass a non-null array even when the size is 0; some drivers dereference it.</param>
        /// <param name="length">Payload length in bytes.</param>
        /// <returns>0 on success, otherwise the RP1210 error code.</returns>
        public short SendCommand(short commandNumber, byte[] buffer, short length)
        {
            if (!IsConnected || _sendCommand == null) return NotConnectedErrorCode;
            byte[] buf = buffer ?? new byte[1];
            lock (_apiLock)
            {
                if (!IsConnected) return NotConnectedErrorCode;
                return _sendCommand(commandNumber, _clientId, buf, length);
            }
        }

        /// <summary>
        /// Declares that some thread outside this class may still be executing inside the vendor
        /// driver, so the module must NOT be unmapped when this provider is disposed.
        /// </summary>
        /// <remarks>
        /// The provider can only join its OWN receive thread. Callers that run their own threads
        /// through this provider — the J1939 session pump, the CDL session pump — know something it
        /// cannot: whether their thread actually stopped. When one of them fails to join it calls
        /// this, and Dispose then deliberately LEAKS the module handle rather than calling
        /// FreeLibrary underneath running code. A leaked handle costs one module reference until the
        /// process exits; unmapping executing code is an access violation that kills the process and
        /// cannot be caught by any managed handler.
        /// One-way on purpose: nothing can later prove the foreign thread finished, so the flag is
        /// never cleared for the life of this provider.
        /// </remarks>
        /// <param name="reason">Why unloading is unsafe. Surfaced to the operator.</param>
        public void MarkUnloadUnsafe(string reason)
        {
            _rxStoppedCleanly = false;

            RaiseError("RP1210 driver marked UNSAFE TO UNLOAD: "
                       + (string.IsNullOrEmpty(reason) ? "reason not given" : reason)
                       + ". The module will be left loaded rather than unmapped while code may still "
                       + "be running inside it. Restart the application before reconnecting this adapter.");
        }

        /// <summary>Close the client session and stop the receive thread. Safe to call when not connected.</summary>
        [System.Runtime.ExceptionServices.HandleProcessCorruptedStateExceptions]
        [System.Security.SecurityCritical]
        public void Disconnect()
        {
            // Recorded for Dispose: the DLL may only be unmapped if this came back true.
            _rxStoppedCleanly = StopReceiveThread();

            short id;
            lock (_apiLock)
            {
                id = _clientId;
                _connected = false;
                _clientId = -1;
                if (id >= 0 && _clientDisconnect != null)
                {
                    try
                    {
                        short r = _clientDisconnect(id);
                        if (r != 0) RaiseLog("RP1210_ClientDisconnect returned " + DescribeError(r));
                    }
                    catch (SEHException ex)
                    {
                        RaiseError("RP1210_ClientDisconnect faulted: " + ex.Message);
                    }
                }
            }

            long dropped = DroppedFrameCount;
            if (dropped > 0)
                RaiseError(string.Format(CultureInfo.InvariantCulture,
                    "RP1210 session ended having DROPPED {0} of {1} frames - that capture is incomplete.",
                    dropped, FramesReceived));
        }

        // ==================================================================
        //  Error text
        // ==================================================================

        /// <summary>
        /// Render an RP1210 error code as text via RP1210_GetErrorMsg, falling back to the
        /// bare number when the driver does not export it.  Never returns an empty string:
        /// an unexplained numeric code in a log is still better than nothing.
        /// </summary>
        /// <param name="errorCode">Code returned by an RP1210 entry point.</param>
        /// <returns>Text of the form "code 146 (ERR_INVALID_DEVICE)".</returns>
        [System.Runtime.ExceptionServices.HandleProcessCorruptedStateExceptions]
        [System.Security.SecurityCritical]
        public string DescribeError(short errorCode)
        {
            string text = null;
            var fn = _getErrorMsg;
            if (fn != null)
            {
                try
                {
                    byte[] buf = new byte[80];
                    short r;
                    lock (_apiLock) { r = fn(errorCode, buf); }
                    if (r == 0) text = AsciiTrim(buf);
                }
                catch (SEHException) { text = null; }
            }

            if (string.IsNullOrEmpty(text))
                return "code " + errorCode.ToString(CultureInfo.InvariantCulture) +
                       (fn == null ? " (driver does not export RP1210_GetErrorMsg)" : string.Empty);

            return "code " + errorCode.ToString(CultureInfo.InvariantCulture) + " (" + text + ")";
        }

        private static string AsciiTrim(byte[] buffer)
        {
            if (buffer == null) return string.Empty;
            int n = 0;
            while (n < buffer.Length && buffer[n] != 0) n++;
            return Encoding.ASCII.GetString(buffer, 0, n).Trim();
        }

        private static Rp1210ProtocolFamily ClassifyProtocol(string protocolString)
        {
            // The protocol string is "NAME" or "NAME:option=value,...". Only the name
            // determines the wire format.
            int colon = protocolString.IndexOf(':');
            string name = (colon >= 0 ? protocolString.Substring(0, colon) : protocolString).Trim();

            if (name.Equals("J1939", StringComparison.OrdinalIgnoreCase)) return Rp1210ProtocolFamily.J1939;
            if (name.Equals("CAN", StringComparison.OrdinalIgnoreCase)) return Rp1210ProtocolFamily.Can;
            if (name.Equals("J1708", StringComparison.OrdinalIgnoreCase)) return Rp1210ProtocolFamily.J1708;
            if (name.Equals("ISO15765", StringComparison.OrdinalIgnoreCase)) return Rp1210ProtocolFamily.Iso15765;
            if (name.Equals("CAT", StringComparison.OrdinalIgnoreCase)) return Rp1210ProtocolFamily.CaterpillarDataLink;
            if (name.StartsWith("J1850", StringComparison.OrdinalIgnoreCase)) return Rp1210ProtocolFamily.J1850;
            return Rp1210ProtocolFamily.Unknown;
        }

        private void RaiseLog(string text)
        {
            var h = Log;
            if (h != null) { try { h(text); } catch (Exception) { /* a broken log sink must not kill the bus */ } }
        }

        private void RaiseError(string text)
        {
            var h = Error;
            if (h != null) { try { h(text); } catch (Exception) { } }
            else RaiseLog(text);
        }

        // ==================================================================
        //  Receive thread
        //
        //  RP1210 has no callback or event model - RP1210_ReadMessage is polled.  One
        //  dedicated background thread owns the poll so that no caller ever has to,
        //  and so that a UI thread stalling on a repaint cannot cost us bus traffic.
        // ==================================================================

        private void StartReceiveThread()
        {
            if (_rxThread != null) return;
            _rxRun = true;
            _rxThread = new Thread(ReceiveLoop);
            _rxThread.IsBackground = true;
            _rxThread.Name = "RP1210-RX-" + _vendor.ApiName;
            _rxThread.Priority = ThreadPriority.AboveNormal;   // the bus does not wait for us
            _rxThread.Start();
        }

        /// <summary>
        /// Stop the receive thread.  Returns true only when the thread is provably no
        /// longer running, which is the precondition for unmapping the vendor DLL.
        /// </summary>
        private bool StopReceiveThread()
        {
            Thread t = _rxThread;
            _rxThread = null;
            _rxRun = false;
            if (t == null) return true;

            // The loop never blocks in the driver for more than one non-blocking read, so
            // it normally exits promptly. But "normally" is not good enough here: if the
            // technician unplugs the adapter mid-capture the vendor DLL can block forever
            // inside RP1210_ReadMessage on a dead USB endpoint. Returning false makes the
            // caller LEAK the module handle rather than FreeLibrary out from under native
            // code that is still executing on this thread - an unmapped-code access
            // violation is unrecoverable and kills the process, whereas a leaked handle
            // costs one module reference until the app closes.
            try
            {
                if (t.Join(ReceiveThreadJoinTimeoutMs)) return true;
            }
            catch (ThreadStateException) { return true; }

            RaiseError("RP1210 receive thread is wedged inside the vendor driver (likely an " +
                       "adapter unplugged mid-session). The driver DLL will be left loaded " +
                       "rather than unmapped while it is still executing - reconnect may " +
                       "require restarting the application.");
            return false;
        }

        [System.Runtime.ExceptionServices.HandleProcessCorruptedStateExceptions]
        [System.Security.SecurityCritical]
        private void ReceiveLoop()
        {
            byte[] buffer = new byte[ReadBufferBytes];
            int idleSpins = 0;

            while (_rxRun)
            {
                short n;
                try
                {
                    lock (_apiLock)
                    {
                        if (!_rxRun || !_connected || _readMessage == null) break;
                        n = _readMessage(_clientId, buffer, (short)ReadBufferBytes, Rp1210Const.NonBlockingIo);
                    }
                }
                catch (SEHException ex)
                {
                    RaiseError("RP1210_ReadMessage faulted: " + ex.Message + " - receive stopped.");
                    break;
                }
                catch (AccessViolationException ex)
                {
                    RaiseError("RP1210_ReadMessage access violation: " + ex.Message + " - receive stopped.");
                    break;
                }

                if (n > 0)
                {
                    idleSpins = 0;
                    int len = n;
                    if (len > buffer.Length) len = buffer.Length;   // defensive: a driver over-reporting must not read past the array
                    byte[] raw = new byte[len];
                    Buffer.BlockCopy(buffer, 0, raw, 0, len);
                    Interlocked.Increment(ref _framesReceived);
                    Dispatch(ParseFrame(raw));
                    continue;
                }

                if (n < 0)
                {
                    // Negative is an error per the RP1210 read contract. Report and keep
                    // polling: a transient bus fault should not silently end the capture,
                    // but it must be visible.
                    RaiseError("RP1210_ReadMessage returned " + DescribeError((short)(-n)));
                    Thread.Sleep(5);
                    continue;
                }

                // Nothing waiting. Spin briefly before sleeping: Thread.Sleep(1) is up to
                // ~15 ms at the default Windows timer resolution, which on a busy bus means
                // waking to a driver queue that is already deep.
                if (idleSpins < 40) { idleSpins++; Thread.SpinWait(150); }
                else Thread.Sleep(1);
            }
        }

        private void Dispatch(Rp1210Frame frame)
        {
            if (frame == null) return;

            Enqueue(frame);

            // Mirror (do not move) into the request/response ring.
            if (!frame.IsTxEcho) MirrorForRequestPath(frame);

            var fh = FrameReceived;
            if (fh != null) { try { fh(frame); } catch (Exception) { } }

            if (frame.J1939 != null)
            {
                var jh = J1939MessageReceived;
                if (jh != null) { try { jh(frame.J1939); } catch (Exception) { } }
            }
        }

        private void Enqueue(Rp1210Frame frame)
        {
            long dropped = 0;
            int total;
            lock (_queueLock)
            {
                // Bounded, and the bound is enforced by dropping the OLDEST frame. A consumer
                // that has fallen behind is better served with current data than with a
                // backlog, and either choice corrupts transport reassembly - which is exactly
                // why the drop is counted and reported instead of being absorbed.
                while (_queue.Count >= _queueCapacity)
                {
                    _queue.Dequeue();
                    dropped++;
                }
                _queue.Enqueue(frame);
                total = _queue.Count;
            }

            if (dropped == 0) return;

            long now = Interlocked.Add(ref _framesDropped, dropped);

            // Throttle the report to once per 1000 dropped frames so an overrun does not
            // itself become the thing that stalls the pump. The counter is always exact.
            int bucket = (int)(now / 1000);
            if (bucket != _lastDropReportAt)
            {
                _lastDropReportAt = bucket;
                RaiseError(string.Format(CultureInfo.InvariantCulture,
                    "RP1210 receive queue FULL ({0} frames): {1} frames dropped so far. " +
                    "The capture is incomplete and transport reassembly over it may be wrong. " +
                    "Drain faster or raise the queue capacity.", total, now));
            }
        }

        // ==================================================================
        //  Wire parsing
        // ==================================================================

        private Rp1210Frame ParseFrame(byte[] raw)
        {
            var frame = new Rp1210Frame { Family = _family, Raw = raw };

            if (raw.Length < TimestampBytes)
            {
                frame.ParseError = "read buffer shorter than the 4-byte RP1210 timestamp (" + raw.Length + " bytes)";
                CountAndMaybeLogParseFailure(frame);
                return frame;
            }

            // Adapter timestamp: 4 bytes, BIG-ENDIAN, at the head of every read buffer.
            uint rawTicks = ((uint)raw[0] << 24) | ((uint)raw[1] << 16) | ((uint)raw[2] << 8) | raw[3];
            frame.AdapterTimestamp = ScaleTimestamp(rawTicks);

            switch (_family)
            {
                case Rp1210ProtocolFamily.J1939:
                    ParseJ1939Into(frame, raw);
                    break;
                case Rp1210ProtocolFamily.Can:
                    ParseCanInto(frame, raw);
                    break;
                default:
                    // J1708, J1850, CAT and anything unrecognised are carried opaquely. The
                    // echo indicator is stripped only when echo mode is actually on (otherwise
                    // claiming one and being wrong would shift every payload byte by one).
                    int opaqueBase = TimestampBytes + (_echoBytePresent ? 1 : 0);
                    frame.Payload = Slice(raw, opaqueBase, raw.Length - opaqueBase);
                    break;
            }

            if (!string.IsNullOrEmpty(frame.ParseError))
                CountAndMaybeLogParseFailure(frame);
            return frame;
        }

        /// <summary>
        /// Count a parse failure exactly once and log the FIRST one of the session loudly, then fall
        /// silent (counted only). Routed through one place so the runt-frame early-return and the
        /// family-parse failures cannot double-count, and so the first failure is never missed -
        /// which would re-open the silent-dead-bus hole this loud first-failure log exists to close.
        /// _parseFailures is zeroed per Connect, so "first" means first this session.
        /// </summary>
        private void CountAndMaybeLogParseFailure(Rp1210Frame frame)
        {
            long fails = Interlocked.Increment(ref _parseFailures);
            if (fails != 1) return;
            RaiseError("RP1210 " + _family + " parse failed: " + frame.ParseError +
                       " (echo byte assumed " + (_echoBytePresent ? "PRESENT" : "ABSENT") +
                       "). Every frame that fails this way is dropped. Further parse " +
                       "failures are counted, not logged.");
        }

        /// <summary>
        /// J1939 read buffer, after the 4-byte timestamp and an OPTIONAL 1-byte echo indicator
        /// (present only while echo mode is on - see <see cref="_echoBytePresent"/>):
        /// [PGN 3, LSB FIRST][priority 1][source address 1][destination address 1][data ...].
        /// The destination byte is present for every J1939 message, including PDU2 broadcasts
        /// where it carries no meaning.
        /// </summary>
        private void ParseJ1939Into(Rp1210Frame frame, byte[] raw)
        {
            // The echo indicator is present ONLY when echo mode is on; it is NOT a fixed part of
            // the layout. b is the first byte of the J1939 message proper.
            int echoLen = _echoBytePresent ? 1 : 0;
            int b = TimestampBytes + echoLen;
            int need = b + J1939MessageHeaderBytes;
            if (raw.Length < need)
            {
                frame.ParseError = "J1939 frame too short: " + raw.Length + " bytes, need at least " + need;
                return;
            }

            frame.IsTxEcho = echoLen != 0 && raw[TimestampBytes] != 0;

            uint pgn = (uint)raw[b]
                     | ((uint)raw[b + 1] << 8)
                     | ((uint)raw[b + 2] << 16);

            byte priority = (byte)(raw[b + 3] & 0x07);   // low 3 bits; upper bits are reserved
            byte source = raw[b + 4];
            byte destination = raw[b + 5];
            byte[] data = Slice(raw, need, raw.Length - need);

            // A single CAN frame carries at most 8 data bytes, so anything longer was
            // reassembled by the driver despite ClientConnect asking for the opposite (see
            // AppPacketizesIncomingMessages). That is not a parse failure - the data is real -
            // but it means the payload came from the vendor's transport implementation and not
            // from ours, so it is reported rather than accepted quietly.
            if (data.Length > MaxSingleFrameDataBytes) ReportDriverReassembly(data.Length);

            if (pgn > J1939Id.MAX_PGN)
            {
                frame.ParseError = string.Format(CultureInfo.InvariantCulture,
                    "PGN 0x{0:X} exceeds the 18-bit maximum - driver framing does not match the assumed layout", pgn);
                frame.Payload = data;
                return;
            }

            byte pf = (byte)((pgn >> 8) & 0xFF);
            bool pdu1 = pf <= J1939Id.PDU1_PF_MAXIMUM;

            try
            {
                J1939Id id;
                if (pdu1)
                {
                    // PDU1 carries the destination in the PS byte of the identifier, but
                    // RP1210 delivers it separately and reports the PGN with PS cleared.
                    // Mask defensively: J1939Id.Create rejects a PDU1 PGN with a non-zero
                    // low byte precisely so a stuck PS cannot become a wrong parameter group.
                    uint psBits = pgn & 0xFFu;
                    if (psBits != 0 && psBits != destination)
                    {
                        long n = Interlocked.Increment(ref _pgnPsCollisions);
                        if (n == 1)
                            RaiseLog(string.Format(CultureInfo.InvariantCulture,
                                "RP1210 J1939: PDU1 PGN 0x{0:X5} carries PS 0x{1:X2} while the destination byte says 0x{2:X2}. " +
                                "Using the destination byte. Further occurrences are counted, not logged.",
                                pgn, psBits, destination));
                    }
                    id = J1939Id.Create(priority, pgn & 0x3FF00u, source, destination);
                }
                else
                {
                    // PDU2: the low byte is the group extension and belongs to the PGN.
                    // The destination byte is meaningless here and is deliberately ignored.
                    id = J1939Id.Create(priority, pgn, source);
                }

                frame.J1939 = _timestampUnit == J1939TimestampUnit.Unknown
                    ? J1939Message.FromHostClock(id, data)
                    : J1939Message.FromAdapter(id, data, frame.AdapterTimestamp, _timestampUnit);
            }
            catch (ArgumentException ex)
            {
                frame.ParseError = "J1939 identifier rejected: " + ex.Message;
                frame.Payload = data;
            }
        }

        /// <summary>
        /// Report - ONCE per session, not once per frame - that the vendor driver is running
        /// J1939-21 reassembly itself.  Once per frame would turn the warning into the thing
        /// that stalls the receive pump on a BAM-heavy bus, and the condition is a property of
        /// the driver, not of the message that revealed it.
        /// </summary>
        private void ReportDriverReassembly(int dataLength)
        {
            if (Interlocked.CompareExchange(ref _driverReassemblyDetected, 1, 0) != 0) return;

            // Capabilities are cached and were built before this was known, so drop the cache
            // to let the notice reach DegradationNotice on the next read.
            _caps = null;

            RaiseError(string.Format(CultureInfo.InvariantCulture,
                "RP1210 {0}: the vendor driver is performing J1939 transport reassembly - a {1}-byte message arrived where " +
                "a CAN frame carries at most {2}. The managed transport state machine is being BYPASSED: its sequence " +
                "checking, timeouts, aborts and TimingDegraded reporting never see these payloads. ClientConnect was called " +
                "with nIsAppPacketizingIncomingMsgs={3}, so this driver is ignoring that flag. Treat multipacket data from " +
                "this adapter as driver-supplied, not tool-verified.",
                _vendor.ApiName, dataLength, MaxSingleFrameDataBytes, AppPacketizesIncomingMessages));
        }

        /// <summary>
        /// CAN read buffer, after the 4-byte timestamp and an OPTIONAL 1-byte echo indicator
        /// (present only while echo mode is on - see <see cref="_echoBytePresent"/>):
        /// [extended flag 1][identifier 2 bytes when standard / 4 bytes when extended, MSB first][data ...].
        /// See <see cref="CanIdentifierIsBigEndian"/> for the endianness caveat.
        /// </summary>
        private void ParseCanInto(Rp1210Frame frame, byte[] raw)
        {
            // The echo indicator is present only while echo mode is on; the extended flag and
            // identifier follow it. Hard-coding it as present shifts the id when echo is off.
            int echoLen = _echoBytePresent ? 1 : 0;
            int p = TimestampBytes + echoLen;
            if (raw.Length < p + 1)
            {
                frame.ParseError = "CAN frame too short: " + raw.Length + " bytes";
                return;
            }

            bool echo = echoLen != 0 && raw[TimestampBytes] != 0;
            bool extended = raw[p] == Rp1210Const.ExtendedCan;
            p += 1;

            int idBytes = extended ? CanExtendedIdBytes : CanStandardIdBytes;
            if (raw.Length < p + idBytes)
            {
                frame.ParseError = "CAN frame truncated before its " + idBytes + "-byte identifier";
                return;
            }

            uint id = 0;
            if (CanIdentifierIsBigEndian)
            {
                for (int i = 0; i < idBytes; i++) id = (id << 8) | raw[p + i];
            }
            else
            {
                for (int i = idBytes - 1; i >= 0; i--) id = (id << 8) | raw[p + i];
            }
            p += idBytes;

            id &= extended ? J1939Id.IDENTIFIER_MASK : 0x7FFu;

            frame.IsTxEcho = echo;
            frame.Can = new Rp1210CanFrame
            {
                AdapterTimestamp = frame.AdapterTimestamp,
                IsTxEcho = echo,
                IsExtended = extended,
                CanId = id,
                Data = Slice(raw, p, raw.Length - p),
            };

            // A 29-bit frame on a raw-CAN client is a J1939 frame; surface it as one so the
            // same consumers work whether the driver was opened as "J1939" or "CAN". An
            // 11-bit frame is not J1939 and is deliberately left unparsed - J1939Id cannot
            // detect the IDE flag, so handing it one would fabricate PGN 0.
            if (!extended) return;

            J1939Id j;
            if (J1939Id.TryFromRaw(id, out j))
            {
                frame.J1939 = _timestampUnit == J1939TimestampUnit.Unknown
                    ? J1939Message.FromHostClock(j, frame.Can.Data)
                    : J1939Message.FromAdapter(j, frame.Can.Data, frame.AdapterTimestamp, _timestampUnit);
            }
        }

        private static byte[] Slice(byte[] source, int offset, int count)
        {
            if (count <= 0) return new byte[0];
            byte[] result = new byte[count];
            Buffer.BlockCopy(source, offset, result, 0, count);
            return result;
        }

        // ==================================================================
        //  Request/response mirror ring
        // ==================================================================

        private void MirrorForRequestPath(Rp1210Frame frame)
        {
            lock (_respLock)
            {
                _respSequence++;
                _respRing.Enqueue(new SequencedFrame { Seq = _respSequence, Frame = frame });
                while (_respRing.Count > ResponseRingCapacity)
                {
                    _respRing.Dequeue();
                    _respRingDiscards++;
                }
                SetResponseSignal();
            }
        }

        // Dispose closes _respSignal while another thread may be inside WaitForFrame.
        // Both sides tolerate that race: a disposed provider has no responses to deliver,
        // so the correct outcome is "return nothing", not an exception thrown out of a
        // diagnostic session at shutdown.
        private void SetResponseSignal()
        {
            if (_disposed) return;
            try { _respSignal.Set(); }
            catch (ObjectDisposedException) { }
        }

        private void ResetResponseSignal()
        {
            if (_disposed) return;
            try { _respSignal.Reset(); }
            catch (ObjectDisposedException) { }
        }

        /// <summary>
        /// Frames that aged out of the request/response mirror ring before a caller read
        /// them.  Unlike <see cref="DroppedFrameCount"/> this is NOT data loss - the frames
        /// are still in the main queue - it only means no request was waiting for them.
        /// </summary>
        public long ResponseRingDiscards
        {
            get { lock (_respLock) { return _respRingDiscards; } }
        }

        /// <summary>
        /// Wait for the next mirrored non-echo frame after <paramref name="cursor"/> that
        /// satisfies <paramref name="match"/>, advancing the cursor past it.
        /// </summary>
        private Rp1210Frame WaitForFrame(ref long cursor, Func<Rp1210Frame, bool> match, uint timeoutMs)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (true)
            {
                if (_disposed) return null;

                lock (_respLock)
                {
                    foreach (SequencedFrame sf in _respRing)
                    {
                        if (sf.Seq <= cursor) continue;
                        bool hit;
                        try { hit = match == null || match(sf.Frame); }
                        catch (Exception) { hit = false; }
                        if (hit) { cursor = sf.Seq; return sf.Frame; }
                        cursor = sf.Seq;   // examined and rejected; do not re-test it
                    }
                    ResetResponseSignal();
                }

                long remaining = (long)timeoutMs - sw.ElapsedMilliseconds;
                if (remaining <= 0) return null;
                if (!IsConnected) return null;

                // Cap the wait so a disconnect during a long timeout is noticed promptly.
                int slice = (int)Math.Min(remaining, 25L);
                try { _respSignal.WaitOne(slice); }
                catch (ObjectDisposedException) { return null; }
            }
        }

        // ==================================================================
        //  Transmit
        // ==================================================================

        /// <summary>
        /// Put one J1939 message on the wire.  Signature matches
        /// <c>HeavyDiesel.J1939.J1939FrameSink</c>, so this can be handed straight to
        /// <c>new J1939TransportProtocol(sourceAddress, provider.SendJ1939)</c>.
        /// </summary>
        /// <param name="message">The message. Data must be 8 bytes or fewer - segmentation belongs to J1939TransportProtocol, not to the driver.</param>
        /// <returns>True when the adapter accepted the frame.</returns>
        public bool SendJ1939(J1939Message message)
        {
            if (message == null) return false;
            return SendJ1939Raw(message.Id.Priority, message.Pgn, message.SourceAddress,
                                message.DestinationAddress, message.Data);
        }

        /// <summary>
        /// Void-returning form of <see cref="SendJ1939"/>, matching the
        /// <c>Action&lt;J1939Message&gt;</c> transmit callback that J1939AddressClaim takes.
        /// A rejected transmit is reported through <see cref="Error"/>, since the callback
        /// shape has nowhere to return a result.
        /// </summary>
        /// <param name="message">The message to transmit.</param>
        public void TransmitJ1939(J1939Message message)
        {
            if (!SendJ1939(message))
                RaiseError("RP1210 transmit failed for " + (message == null ? "(null message)" : message.ToString()));
        }

        /// <summary>
        /// Put one J1939 message on the wire from its parts.
        /// Write buffer layout: [PGN 3 bytes, LSB first][priority][source address][destination address][data ...].
        /// There is no timestamp and no echo byte on the transmit side.
        /// </summary>
        /// <param name="priority">0..7. Masked to 3 bits.</param>
        /// <param name="pgn">Parameter group number. For PDU1 groups the PS byte must be zero; the destination travels separately.</param>
        /// <param name="sourceAddress">Transmitting address. Some drivers refuse to transmit from an address that has not been claimed - see the degradation notice.</param>
        /// <param name="destinationAddress">Destination, or 255 for a broadcast group.</param>
        /// <param name="data">0..8 bytes.</param>
        /// <returns>True when the adapter accepted the frame.</returns>
        public bool SendJ1939Raw(byte priority, uint pgn, byte sourceAddress, byte destinationAddress, byte[] data)
        {
            if (!IsConnected) { RaiseError("RP1210 transmit refused: not connected."); return false; }
            if (_family != Rp1210ProtocolFamily.J1939)
            {
                RaiseError("RP1210 transmit refused: client is open on \"" + _protocolString +
                           "\", not J1939. Reconnect with a J1939 protocol string.");
                return false;
            }

            byte[] payload = data ?? new byte[0];
            if (payload.Length > 8)
            {
                RaiseError(string.Format(CultureInfo.InvariantCulture,
                    "RP1210 transmit refused: {0} bytes exceeds one CAN frame. Segmentation is J1939TransportProtocol's job, " +
                    "not the driver's - HeavyDiesel owns the transport state machine.", payload.Length));
                return false;
            }
            if (pgn > J1939Id.MAX_PGN)
            {
                RaiseError(string.Format(CultureInfo.InvariantCulture, "RP1210 transmit refused: PGN 0x{0:X} exceeds 18 bits.", pgn));
                return false;
            }

            byte[] buf = new byte[J1939WriteHeaderBytes + payload.Length];
            buf[0] = (byte)(pgn & 0xFF);
            buf[1] = (byte)((pgn >> 8) & 0xFF);
            buf[2] = (byte)((pgn >> 16) & 0xFF);
            buf[3] = (byte)(priority & 0x07);
            buf[4] = sourceAddress;
            buf[5] = destinationAddress;
            if (payload.Length > 0) Buffer.BlockCopy(payload, 0, buf, J1939WriteHeaderBytes, payload.Length);

            return SendRaw(buf, "J1939");
        }

        /// <summary>
        /// Put one raw CAN frame on the wire.  Only valid on a CAN-family client.
        /// Write buffer layout: [extended flag][identifier 2 bytes when standard / 4 bytes when extended, MSB first][data ...].
        /// </summary>
        /// <param name="canId">Identifier. Masked to 11 or 29 bits according to <paramref name="extended"/>.</param>
        /// <param name="extended">True for a 29-bit identifier.</param>
        /// <param name="data">0..8 bytes.</param>
        /// <returns>True when the adapter accepted the frame.</returns>
        public bool SendCan(uint canId, bool extended, byte[] data)
        {
            if (!IsConnected) { RaiseError("RP1210 transmit refused: not connected."); return false; }
            if (_family != Rp1210ProtocolFamily.Can)
            {
                RaiseError("RP1210 raw-CAN transmit refused: client is open on \"" + _protocolString + "\", not CAN.");
                return false;
            }

            byte[] payload = data ?? new byte[0];
            if (payload.Length > 8)
            {
                RaiseError("RP1210 raw-CAN transmit refused: " + payload.Length + " bytes exceeds one classic CAN frame.");
                return false;
            }

            int idBytes = extended ? CanExtendedIdBytes : CanStandardIdBytes;
            uint id = canId & (extended ? J1939Id.IDENTIFIER_MASK : 0x7FFu);

            byte[] buf = new byte[1 + idBytes + payload.Length];
            buf[0] = extended ? Rp1210Const.ExtendedCan : Rp1210Const.StandardCan;
            if (CanIdentifierIsBigEndian)
                for (int i = 0; i < idBytes; i++) buf[1 + i] = (byte)((id >> (8 * (idBytes - 1 - i))) & 0xFF);
            else
                for (int i = 0; i < idBytes; i++) buf[1 + i] = (byte)((id >> (8 * i)) & 0xFF);

            if (payload.Length > 0) Buffer.BlockCopy(payload, 0, buf, 1 + idBytes, payload.Length);
            return SendRaw(buf, "CAN");
        }

        /// <summary>
        /// Hand an already-formatted RP1210 message buffer to the driver.  Use this for
        /// protocol families this class does not frame (J1708, CAT, J1850), where the
        /// caller knows the layout and this class deliberately does not guess at it.
        /// </summary>
        /// <param name="buffer">Complete RP1210 transmit buffer for the connected protocol.</param>
        /// <param name="what">Short label used in log lines.</param>
        /// <returns>True when the adapter accepted the buffer.</returns>
        [System.Runtime.ExceptionServices.HandleProcessCorruptedStateExceptions]
        [System.Security.SecurityCritical]
        public bool SendRaw(byte[] buffer, string what)
        {
            if (buffer == null) throw new ArgumentNullException("buffer");
            if (!IsConnected || _sendMessage == null) return false;
            if (buffer.Length > short.MaxValue)
            {
                RaiseError("RP1210 transmit refused: buffer of " + buffer.Length + " bytes exceeds the API's 16-bit size field.");
                return false;
            }

            short r;
            try
            {
                lock (_apiLock)
                {
                    if (!IsConnected) return false;
                    r = _sendMessage(_clientId, buffer, (short)buffer.Length, 0, Rp1210Const.NonBlockingIo);
                }
            }
            catch (SEHException ex)
            {
                // Now that the CSE attributes above make this handler actually fire, a
                // fault here means the vendor DLL corrupted process state. Returning a
                // plain false invites the caller to retry straight back into a driver we
                // have just declared unsafe. Kill the session instead: report, mark
                // disconnected, and let every subsequent call fail fast at the IsConnected
                // guard. Recovering requires a fresh Connect, which reloads the driver.
                _driverFaulted = true;
                _connected = false;
                RaiseError("RP1210_SendMessage faulted: " + ex.Message
                           + " - the vendor driver corrupted process state, so this session has been marked dead. "
                           + "Reconnect to continue; do not keep transmitting on a faulted driver.");
                return false;
            }

            if (r != 0)
            {
                RaiseError("RP1210 " + (what ?? "message") + " transmit rejected: " + DescribeError(r));
                return false;
            }
            return true;
        }

        // ==================================================================
        //  Receive - drain
        // ==================================================================

        /// <summary>Take up to <paramref name="max"/> frames of any family off the receive queue.</summary>
        /// <param name="max">Maximum frames to return. Values below 1 are treated as 1.</param>
        /// <returns>Frames in arrival order. Empty when nothing is waiting; never null.</returns>
        public List<Rp1210Frame> DrainFrames(int max)
        {
            if (max < 1) max = 1;
            var result = new List<Rp1210Frame>(Math.Min(max, 256));
            lock (_queueLock)
            {
                while (result.Count < max && _queue.Count > 0) result.Add(_queue.Dequeue());
            }
            return result;
        }

        /// <summary>
        /// Take up to <paramref name="max"/> J1939 messages off the receive queue.  Frames
        /// that are not J1939 (and unparseable frames) are removed and counted, not returned
        /// - they remain visible through <see cref="DrainFrames"/> only if drained first.
        /// </summary>
        /// <param name="max">Maximum messages to return.</param>
        /// <param name="includeTxEchoes">True to include the adapter's echo of our own transmits.</param>
        /// <returns>Messages in arrival order; never null.</returns>
        public List<J1939Message> DrainJ1939(int max, bool includeTxEchoes)
        {
            if (max < 1) max = 1;
            var result = new List<J1939Message>(Math.Min(max, 256));
            lock (_queueLock)
            {
                while (result.Count < max && _queue.Count > 0)
                {
                    Rp1210Frame f = _queue.Dequeue();
                    if (f.J1939 == null) continue;
                    if (f.IsTxEcho && !includeTxEchoes) continue;
                    result.Add(f.J1939);
                }
            }
            return result;
        }

        /// <summary>Take up to <paramref name="max"/> received J1939 messages, excluding our own echoes.</summary>
        /// <param name="max">Maximum messages to return.</param>
        /// <returns>Messages in arrival order; never null.</returns>
        public List<J1939Message> DrainJ1939(int max)
        {
            return DrainJ1939(max, false);
        }

        /// <summary>Discard everything currently queued. Discards are counted as drops, because they are.</summary>
        public void FlushReceiveQueue()
        {
            int n;
            lock (_queueLock) { n = _queue.Count; _queue.Clear(); }
            if (n > 0) RaiseLog("RP1210 receive queue flushed, " + n + " frames discarded by request.");
        }

        // ==================================================================
        //  Single-frame request / response
        //
        //  This is a request/response shape and J1939 is not a request/response
        //  protocol.  It is implemented for the single-frame case because UDS-on-CAN
        //  and simple J1939 request/reply exchanges genuinely need it, and it refuses
        //  loudly rather than approximating anywhere else.
        // ==================================================================

        private readonly object _requestLock = new object();
        private byte _lastPeerAddress = J1939Pgn.GlobalAddress;
        private byte _lastLocalAddress = J1939Pgn.NullAddress;

        /// <summary>
        /// Send one frame and wait for the first plausible reply.
        /// </summary>
        /// <param name="txId">
        /// J1939 family: the full 29-bit J1939 identifier (priority, PGN, source, destination).
        /// CAN family: the raw CAN identifier; 29-bit is used automatically above 0x7FF.
        /// </param>
        /// <param name="payload">0..8 bytes. Longer payloads are refused - use J1939TransportProtocol.</param>
        /// <param name="timeout">Milliseconds to wait for a reply.</param>
        /// <returns>The reply's data bytes, or null on timeout, on refusal, or when not connected.</returns>
        public byte[] SendReceive(uint txId, byte[] payload, uint timeout = 2000)
        {
            if (!IsConnected || payload == null) return null;

            lock (_requestLock)
            {
                switch (_family)
                {
                    case Rp1210ProtocolFamily.J1939:
                        return SendReceiveJ1939(txId, payload, timeout);

                    case Rp1210ProtocolFamily.Can:
                        return SendReceiveCan(txId, payload, timeout);

                    default:
                        RaiseError("RP1210 SendReceive is not implemented for protocol \"" + _protocolString +
                                   "\". Its transmit and receive buffer layouts are not framed by this provider, and " +
                                   "guessing at them would produce confident nonsense. Use SendRaw plus DrainFrames.");
                        return null;
                }
            }
        }

        private byte[] SendReceiveJ1939(uint txId, byte[] payload, uint timeout)
        {
            J1939Id id;
            if (!J1939Id.TryFromRaw(txId & J1939Id.IDENTIFIER_MASK, out id))
            {
                RaiseError(string.Format(CultureInfo.InvariantCulture, "RP1210 SendReceive: 0x{0:X8} is not a valid 29-bit J1939 identifier.", txId));
                return null;
            }

            byte localSa = id.SourceAddress;
            byte peer = id.DestinationAddress;
            _lastLocalAddress = localSa;
            _lastPeerAddress = peer;

            bool directed = id.IsPeerToPeer;

            // Take the cursor BEFORE transmitting so nothing that arrives during the send
            // can be missed, and nothing that arrived before it can be mistaken for a reply.
            lock (_respLock) { _respCursor = _respSequence; }

            if (!SendJ1939Raw(id.Priority, id.Pgn, localSa, peer, payload)) return null;

            Rp1210Frame hit = WaitForFrame(ref _respCursor, delegate(Rp1210Frame f)
            {
                if (f.J1939 == null) return false;
                if (directed)
                {
                    // A reply to a directed request comes from the node we addressed and is
                    // aimed back at us, or is a broadcast that node emitted in response.
                    return f.J1939.SourceAddress == peer &&
                           (f.J1939.DestinationAddress == localSa || f.J1939.IsBroadcast);
                }
                // Broadcast request (PGN 59904 to the global address is the common case):
                // any node may answer, so accept the first reply that is not our own.
                return f.J1939.SourceAddress != localSa;
            }, timeout);

            return hit == null ? null : hit.J1939.Data;
        }

        private byte[] SendReceiveCan(uint txId, byte[] payload, uint timeout)
        {
            bool extended = txId > 0x7FFu;

            lock (_respLock) { _respCursor = _respSequence; }
            if (!SendCan(txId, extended, payload)) return null;

            // Raw CAN has no request/response concept and no correlation field, so the only
            // honest match is "the next frame that is not our own echo". Callers needing a
            // specific responder must use DrainFrames and correlate themselves.
            Rp1210Frame hit = WaitForFrame(ref _respCursor, delegate(Rp1210Frame f) { return f.Can != null; }, timeout);
            return hit == null ? null : hit.Can.Data;
        }

        /// <summary>
        /// Read a further frame without sending - the ISO 14229 NRC 0x78 wait loop, and the
        /// follow-on frames of a segmented exchange.  Continues from where the last
        /// <see cref="SendReceive"/> left off, so a reply that arrived while the caller was
        /// deciding what to do is still delivered.
        /// </summary>
        /// <param name="timeout">Milliseconds to wait.</param>
        /// <returns>The frame's data bytes, or null on timeout.</returns>
        public byte[] ReadResponse(uint timeout = 2000)
        {
            if (!IsConnected) return null;

            lock (_requestLock)
            {
                byte peer = _lastPeerAddress;
                byte local = _lastLocalAddress;

                Rp1210Frame hit = WaitForFrame(ref _respCursor, delegate(Rp1210Frame f)
                {
                    if (f.J1939 != null)
                    {
                        if (peer != J1939Pgn.GlobalAddress && peer != J1939Pgn.NullAddress)
                            return f.J1939.SourceAddress == peer &&
                                   (f.J1939.DestinationAddress == local || f.J1939.IsBroadcast);
                        return f.J1939.SourceAddress != local;
                    }
                    return f.Can != null;
                }, timeout);

                if (hit == null) return null;
                return hit.J1939 != null ? hit.J1939.Data : hit.Can.Data;
            }
        }

        // ==================================================================
        //  Capabilities
        //
        //  Adapter-derived wherever the vendor INI actually says something, and
        //  UNDER-claimed everywhere it does not.  Over-claiming here is what makes a
        //  tool produce confident wrong answers; under-claiming only makes it cautious.
        // ==================================================================

        /// <summary>What this adapter, on this protocol, can actually do. Never null.</summary>
        public InterfaceCapabilities Capabilities
        {
            get
            {
                if (_caps == null) _caps = BuildCapabilities();
                return _caps;
            }
        }

        private InterfaceCapabilities BuildCapabilities()
        {
            var notes = new List<string>();

            // ---- Protocols: what the VENDOR declares, not what RP1210 permits ----
            ProtocolFlags protocols = ProtocolFlags.None;
            bool sawCat = false, sawJ1850 = false;
            foreach (Rp1210ProtocolInfo p in _vendor.Protocols)
            {
                switch (ClassifyProtocol(p.ProtocolString))
                {
                    case Rp1210ProtocolFamily.J1939:
                        protocols |= ProtocolFlags.J1939 | ProtocolFlags.RawCan29;
                        break;
                    case Rp1210ProtocolFamily.Can:
                        protocols |= ProtocolFlags.RawCan11 | ProtocolFlags.RawCan29;
                        break;
                    case Rp1210ProtocolFamily.J1708:
                        // J1708 is the physical layer; J1587 is the application layer that
                        // rides it. A J1708 client is what makes J1587 reachable at all.
                        protocols |= ProtocolFlags.J1587;
                        break;
                    case Rp1210ProtocolFamily.Iso15765:
                        protocols |= ProtocolFlags.Iso15765 | ProtocolFlags.RawCan11;
                        break;
                    case Rp1210ProtocolFamily.CaterpillarDataLink:
                        sawCat = true;
                        break;
                    case Rp1210ProtocolFamily.J1850:
                        sawJ1850 = true;
                        break;
                }
            }
            if (sawCat)
                notes.Add("this adapter declares CAT (Caterpillar Data Link); it is reachable by connecting with the \"CAT\" " +
                          "protocol string but has no ProtocolFlags value and no framing in this provider - use SendRaw/DrainFrames");
            if (sawJ1850)
                notes.Add("this adapter declares J1850, which has no ProtocolFlags value and no framing here");

            // ---- RP1210 revision drives the multi-client / exclusive-filter answer ----
            string ver = (_vendor.Version ?? string.Empty).Trim().ToUpperInvariant();
            bool bOrLater = ver == "B" || ver == "C";
            if (ver.Length == 0)
                notes.Add("the vendor INI does not state an RP1210 revision, so multi-client and exclusive filters are reported as unavailable");
            else if (ver == "A")
                notes.Add("RP1210A driver: no multi-client and no exclusive filters, so this tool cannot share this adapter with a running OEM application");

            // ---- Baud ----
            int maxBaud = ResolveMaxBaud();

            // ---- Timestamps ----
            bool hwTimestamps = _timestampUnit != J1939TimestampUnit.Unknown;
            if (_timestampWeightAssumed)
                notes.Add(string.Format(CultureInfo.InvariantCulture,
                    "the vendor INI states no TimestampWeight, so this adapter's tick scale is unknown and its timestamps " +
                    "are NOT used: every message is stamped from the host clock and carries TimingDegraded, and that " +
                    "includes the J1939-21 transport timers - Windows scheduling and USB latency are inside every interval. " +
                    "({0} us per tick is the RP1210 default and is shown for reference only.) Add TimestampWeight to the " +
                    "vendor INI, or use an adapter that declares it, to get hardware timing back",
                    DefaultTimestampWeightMicroseconds));
            if (!_timestampWeightAssumed && _timestampWeightUs != 1 && _timestampWeightUs != 1000)
                notes.Add(string.Format(CultureInfo.InvariantCulture,
                    "TimestampWeight is {0} us per tick, so timestamps are rescaled to milliseconds; an interval measured " +
                    "across the counter rollover will be wrong", _timestampWeightUs));

            // ---- Filters ----
            // RP1210 exposes no filter-count query. This provider sets all filters to pass
            // and filters in managed code, so there is no per-adapter budget to report.
            notes.Add("RP1210 does not report a filter limit; this provider passes all traffic and filters in managed code, " +
                      "so budget the receive queue (" + _queueCapacity + " frames) rather than filters");

            // ---- Driver-side transport reassembly ----
            if (Volatile.Read(ref _driverReassemblyDetected) != 0)
                notes.Add("this driver performs J1939 transport reassembly itself despite being connected with " +
                          "nIsAppPacketizingIncomingMsgs=" + AppPacketizesIncomingMessages.ToString(CultureInfo.InvariantCulture) +
                          ", so multipacket payloads bypass the managed transport state machine and are driver-supplied " +
                          "rather than tool-verified");

            // ---- Address claim ----
            notes.Add("J1939 address claim runs in managed code (J1939AddressClaim), not in the driver; if this vendor " +
                      "refuses to transmit from an unclaimed source address, issue RP1210 command " +
                      Rp1210Command.ProtectJ1939Address.ToString(CultureInfo.InvariantCulture) +
                      " (ProtectJ1939Address) through SendCommand");

            if (_bindingWarnings.Count > 0)
                notes.Add("driver exports missing or degraded: " + string.Join("; ", _bindingWarnings.ToArray()));

            if (_family == Rp1210ProtocolFamily.Unknown && _connected)
                notes.Add("protocol string \"" + _protocolString + "\" is not one this provider frames; received buffers are " +
                          "surfaced as raw payload with the 4-byte timestamp stripped and nothing else assumed");

            return new InterfaceCapabilities
            {
                Protocols = protocols,
                MaxFilters = 0,
                HardwareTimestamps = hwTimestamps,
                ExclusiveFilters = bOrLater,
                MultiClient = bOrLater,
                AssistsAddressClaim = false,
                SupportsPinSelect = false,     // a J2534-2 concept; RP1210 adapters reach J1939 on the 9-pin Deutsch natively
                MaxBaud = maxBaud,
                SupportedVoltage = VoltageRange.TwelveOrTwentyFour,   // RP1210 adapters are heavy-duty hardware, 12/24 V by design
                DegradationNotice = notes.Count == 0
                    ? string.Empty
                    : _vendor.ToString() + ": " + string.Join(". ", notes.ToArray()) + ".",
            };
        }

        /// <summary>
        /// Highest bit rate this adapter declares for the connected protocol.  Prefers the
        /// explicit ":Baud=" in the connect string, then the vendor INI ProtocolSpeed list.
        /// </summary>
        private int ResolveMaxBaud()
        {
            int best = 0;

            int colon = _protocolString.IndexOf(':');
            if (colon >= 0)
            {
                foreach (string opt in _protocolString.Substring(colon + 1).Split(','))
                {
                    int eq = opt.IndexOf('=');
                    if (eq <= 0) continue;
                    if (!opt.Substring(0, eq).Trim().Equals("Baud", StringComparison.OrdinalIgnoreCase)) continue;
                    best = Math.Max(best, NormaliseBaud(opt.Substring(eq + 1).Trim()));
                }
            }

            if (best == 0)
            {
                foreach (Rp1210ProtocolInfo p in _vendor.Protocols)
                {
                    if (_connected && ClassifyProtocol(p.ProtocolString) != _family) continue;
                    foreach (string s in p.Speeds) best = Math.Max(best, NormaliseBaud(s));
                }
            }

            // Nothing declared: report the J1939 baseline rather than 0, which a caller
            // would read as "no bit rate at all".
            return best > 0 ? best : 250000;
        }

        private static int NormaliseBaud(string text)
        {
            int v;
            if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out v) || v <= 0) return 0;
            // RP1210 states CAN/J1939 speeds in kbps ("250", "500") and J1708 in bps ("9600").
            return v <= 1000 ? v * 1000 : v;
        }

        // ==================================================================
        //  Optional driver queries
        // ==================================================================

        /// <summary>Read the driver's API and DLL version. False when the driver does not export RP1210_ReadVersion.</summary>
        /// <param name="dllVersion">DLL major.minor, or empty.</param>
        /// <param name="apiVersion">API major.minor, or empty.</param>
        /// <returns>True when the values were read.</returns>
        [System.Runtime.ExceptionServices.HandleProcessCorruptedStateExceptions]
        [System.Security.SecurityCritical]
        public bool TryReadVersion(out string dllVersion, out string apiVersion)
        {
            dllVersion = string.Empty;
            apiVersion = string.Empty;
            var fn = _readVersion;
            if (fn == null) return false;
            try
            {
                byte[] dMaj = new byte[17], dMin = new byte[17], aMaj = new byte[17], aMin = new byte[17];
                lock (_apiLock) { fn(dMaj, dMin, aMaj, aMin); }
                dllVersion = AsciiTrim(dMaj) + "." + AsciiTrim(dMin);
                apiVersion = AsciiTrim(aMaj) + "." + AsciiTrim(aMin);
                return true;
            }
            catch (SEHException) { return false; }
        }

        /// <summary>Read the RP1210B detailed version strings. False when the export is absent or the call fails.</summary>
        /// <param name="apiVersion">API version text.</param>
        /// <param name="dllVersion">DLL version text.</param>
        /// <param name="firmwareVersion">Adapter firmware version text.</param>
        /// <returns>True when the values were read.</returns>
        [System.Runtime.ExceptionServices.HandleProcessCorruptedStateExceptions]
        [System.Security.SecurityCritical]
        public bool TryReadDetailedVersion(out string apiVersion, out string dllVersion, out string firmwareVersion)
        {
            apiVersion = string.Empty;
            dllVersion = string.Empty;
            firmwareVersion = string.Empty;
            var fn = _readDetailedVersion;
            if (fn == null || !IsConnected) return false;
            try
            {
                byte[] a = new byte[17], d = new byte[17], f = new byte[17];
                short r;
                lock (_apiLock) { r = fn(_clientId, a, d, f); }
                if (r != 0) { RaiseLog("RP1210_ReadDetailedVersion returned " + DescribeError(r)); return false; }
                apiVersion = AsciiTrim(a);
                dllVersion = AsciiTrim(d);
                firmwareVersion = AsciiTrim(f);
                return true;
            }
            catch (SEHException) { return false; }
        }

        /// <summary>
        /// Read the 64-byte hardware status block.  Returned verbatim: its per-vendor
        /// interpretation is not modelled here.
        /// </summary>
        /// <param name="status">The status bytes, or null.</param>
        /// <returns>True when the block was read.</returns>
        [System.Runtime.ExceptionServices.HandleProcessCorruptedStateExceptions]
        [System.Security.SecurityCritical]
        public bool TryGetHardwareStatus(out byte[] status)
        {
            status = null;
            var fn = _getHardwareStatus;
            if (fn == null || !IsConnected) return false;
            try
            {
                byte[] buf = new byte[64];
                short r;
                lock (_apiLock) { r = fn(_clientId, buf, (short)buf.Length, Rp1210Const.NonBlockingIo); }
                if (r != 0) { RaiseLog("RP1210_GetHardwareStatus returned " + DescribeError(r)); return false; }
                status = buf;
                return true;
            }
            catch (SEHException) { return false; }
        }

        /// <summary>
        /// Vendor-specific detail for the last error, where the driver exports
        /// RP1210_GetLastErrorMsg.  Returns an empty string when unavailable.
        /// </summary>
        /// <param name="errorCode">The error code being explained.</param>
        /// <returns>Detail text, or empty.</returns>
        [System.Runtime.ExceptionServices.HandleProcessCorruptedStateExceptions]
        [System.Security.SecurityCritical]
        public string DescribeLastError(short errorCode)
        {
            var fn = _getLastErrorMsg;
            if (fn == null) return string.Empty;
            try
            {
                byte[] buf = new byte[80];
                int sub = 0;
                short r;
                lock (_apiLock) { r = fn(errorCode, ref sub, buf, _clientId); }
                if (r != 0) return string.Empty;
                string text = AsciiTrim(buf);
                return sub != 0
                    ? text + " (sub-error " + sub.ToString(CultureInfo.InvariantCulture) + ")"
                    : text;
            }
            catch (SEHException) { return string.Empty; }
        }

        // ==================================================================
        //  Teardown
        // ==================================================================

        /// <summary>
        /// Stop the receive thread, close the client session and free the vendor DLL.
        /// Safe to call more than once and safe to call when never connected.
        /// </summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            try { Disconnect(); }
            catch (Exception ex) { RaiseError("RP1210 disconnect during dispose: " + ex.Message); }

            // Wake anything blocked in WaitForFrame before closing the handle, so a caller
            // sitting on a 2 s timeout returns at once instead of waiting it out.
            try { _respSignal.Set(); }
            catch (ObjectDisposedException) { }
            try { _respSignal.Close(); }
            catch (Exception) { }

            UnloadDriver(_rxStoppedCleanly);

            lock (_queueLock) _queue.Clear();
            lock (_respLock) _respRing.Clear();
        }
    }
}
