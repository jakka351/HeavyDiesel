// =============================================================================
//  SimulatedJ1939Link.cs - a virtual truck behind the IJ1939Link seam.
//
//  WHY THIS EXISTS
//  ---------------
//  The biggest barrier to writing heavy-vehicle software is not the spec, it is
//  the bench: a real RP1210 adapter costs real money and a real truck costs more.
//  This link is a small J1939 network in memory - an engine, a transmission, an
//  ABS module, a cluster and one badly-behaved node that never claims - so the
//  whole stack above the seam (address claim, transport reassembly, DM1 ageing,
//  the network view) runs and can be TESTED with no hardware at all.
//
//  It is a simulator, not an emulator. The ECUs broadcast a handful of real
//  parameter groups at realistic periods, answer Requests, defend their
//  addresses, and send multi-packet messages by BAM so the receive side of
//  J1939TransportProtocol gets exercised. They do not model an engine.
//
//  WHAT IT DELIBERATELY GETS RIGHT
//  -------------------------------
//  * Timestamps are adapter-style milliseconds from one Stopwatch, so the stack
//    sees HardwareTimestamps = true and runs its timers off them.
//  * The tool's own transmits are NOT echoed back (DrainJ1939 excludes echoes on
//    every real link too).
//  * A node that transmits but never answers an Address Claimed request exists
//    on purpose: real buses have them, and the map must show "no claim seen"
//    rather than invent a NAME.
//  * Claiming an address an ECU already holds gets that ECU's claim back at
//    you, and its NAME is lower, so you lose - exactly what happens on a truck.
//
//  NOTHING HERE IS SAE DEFINITION DATA. PGN numbers are protocol structure; the
//  byte layouts used for EEC1 engine speed and ET1 coolant are the two the
//  stack already documents in J1939EngineState. Manufacturer codes and NAMEs
//  are fictional.
// =============================================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Threading;
using HeavyDiesel.J1939;

namespace HeavyDiesel.Transport.Simulation
{
    /// <summary>
    /// An in-memory J1939 bus with a few virtual ECUs, exposed as an
    /// <see cref="IJ1939Link"/>. Construct, call <see cref="Connect"/>, hand it to a
    /// <see cref="J1939Session"/> exactly as you would an <c>Rp1210Provider</c>.
    /// </summary>
    public sealed class SimulatedJ1939Link : IJ1939Link, IDisposable
    {
        /// <summary>Protocol string reported by this link.</summary>
        public const string PROTOCOL_STRING = "J1939:Baud=250 (simulated)";

        /// <summary>Receive queue depth before the oldest frames are dropped and counted.</summary>
        public const int RECEIVE_QUEUE_CAPACITY = 8192;

        private const int TICK_MS = 2;
        private const int BAM_INTER_PACKET_MS = 50;
        private const int REQUEST_RESPONSE_DELAY_MS = 15;

        private readonly object _bus = new object();
        private readonly object _queueGate = new object();
        private readonly Queue<J1939Message> _rx = new Queue<J1939Message>();
        private readonly List<VirtualEcu> _ecus = new List<VirtualEcu>();
        private readonly List<PendingBam> _bams = new List<PendingBam>();
        private readonly List<PendingFrame> _pending = new List<PendingFrame>();
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly Random _random = new Random(1939);

        private Thread _thread;
        private volatile bool _run;
        private volatile bool _connected;
        private volatile bool _disposed;

        private long _framesReceived;
        private long _framesDropped;
        private long _toolFrames;
        private InterfaceCapabilities _caps;

        /// <summary>Raised for every frame the virtual ECUs put on the bus, on the simulator thread. Do not block in it.</summary>
        public event Action<J1939Message> MessageReceived;

        /// <summary>Raised for every frame the tool transmits into the bus, on the caller's thread.</summary>
        public event Action<J1939Message> ToolMessageSent;

        /// <summary>Human-readable progress, on whatever thread produced it.</summary>
        public event Action<string> Log;

        /// <summary>Creates the virtual truck with its default node set.</summary>
        public SimulatedJ1939Link()
        {
            BuildDefaultNetwork();
        }

        // ------------------------------------------------------------------
        //  IJ1939Link
        // ------------------------------------------------------------------

        /// <inheritdoc />
        public bool IsConnected { get { return _connected && !_disposed; } }

        /// <inheritdoc />
        public string ProtocolString { get { return PROTOCOL_STRING; } }

        /// <inheritdoc />
        public InterfaceCapabilities Capabilities
        {
            get
            {
                if (_caps == null)
                {
                    _caps = new InterfaceCapabilities
                    {
                        Protocols = ProtocolFlags.J1939 | ProtocolFlags.RawCan29,
                        MaxFilters = 0,
                        HardwareTimestamps = true,
                        ExclusiveFilters = true,
                        MultiClient = true,
                        AssistsAddressClaim = false,
                        SupportsPinSelect = false,
                        MaxBaud = 250000,
                        SupportedVoltage = VoltageRange.TwelveOrTwentyFour,
                        DegradationNotice = "SIMULATED BUS: nothing here came from a vehicle. Timestamps are host-process milliseconds dressed as adapter ticks; "
                                          + "every ECU, NAME, VIN and fault code is fictional.",
                    };
                }
                return _caps;
            }
        }

        /// <summary>Starts the virtual ECUs. Idempotent.</summary>
        public void Connect()
        {
            if (_disposed) throw new ObjectDisposedException("SimulatedJ1939Link");
            if (_connected) return;

            _run = true;
            _connected = true;
            _thread = new Thread(SimulatorLoop) { IsBackground = true, Name = "J1939 simulator" };
            _thread.Start();
            RaiseLog("Simulated J1939 bus up: " + _ecus.Count + " virtual node(s).");
        }

        /// <summary>Stops the virtual ECUs and empties the receive queue.</summary>
        public void Disconnect()
        {
            _run = false;
            _connected = false;
            Thread t = _thread;
            _thread = null;
            if (t != null && t != Thread.CurrentThread)
            {
                t.Join(1000);
            }
            lock (_queueGate) { _rx.Clear(); }
        }

        /// <inheritdoc />
        public bool SendJ1939(J1939Message message)
        {
            if (message == null) return false;
            if (!IsConnected) { RaiseLog("Simulator: transmit refused, not connected."); return false; }
            if (message.Length > 8) { RaiseLog("Simulator: transmit refused, " + message.Length + " bytes exceeds one frame."); return false; }

            Interlocked.Increment(ref _toolFrames);

            Action<J1939Message> sent = ToolMessageSent;
            if (sent != null) { try { sent(message); } catch (Exception) { } }

            long now = NowMs();
            lock (_bus)
            {
                HandleToolFrameLocked(message, now);
            }
            return true;
        }

        /// <inheritdoc />
        public List<J1939Message> DrainJ1939(int max)
        {
            if (max < 1) max = 1;
            List<J1939Message> result = new List<J1939Message>(Math.Min(max, 256));
            lock (_queueGate)
            {
                while (result.Count < max && _rx.Count > 0) result.Add(_rx.Dequeue());
            }
            return result;
        }

        /// <inheritdoc />
        public void MarkUnloadUnsafe(string reason)
        {
            RaiseLog("Simulator: MarkUnloadUnsafe(" + reason + ") - nothing to unmap on a simulated link.");
        }

        /// <summary>Frames the virtual ECUs have produced since connect.</summary>
        public long FramesReceived { get { return Interlocked.Read(ref _framesReceived); } }

        /// <summary>Frames discarded because nobody drained the queue in time.</summary>
        public long DroppedFrameCount { get { return Interlocked.Read(ref _framesDropped); } }

        /// <summary>Frames the tool has transmitted into the bus.</summary>
        public long ToolFramesSent { get { return Interlocked.Read(ref _toolFrames); } }

        /// <summary>Frames queued and not yet drained.</summary>
        public int QueueDepth { get { lock (_queueGate) { return _rx.Count; } } }

        /// <summary>Stops the bus.</summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Disconnect();
        }

        // ------------------------------------------------------------------
        //  The network
        // ------------------------------------------------------------------

        private void BuildDefaultNetwork()
        {
            // Engine, SA 0x00. The talkative one, with a two-code DM1 that goes out by BAM.
            VirtualEcu engine = new VirtualEcu(0x00, MakeName(1, 0, 0, 0, 100, 12345), "Engine");
            engine.AddBroadcast(61444, 20, BuildEec1);                       // EEC1 - engine speed
            engine.AddBroadcast(65262, 1000, BuildEt1);                      // ET1  - coolant
            engine.AddBroadcast(65265, 100, delegate { return Fill8(0xFF); });  // CCVS - placeholder
            engine.AddBroadcast(J1939Pgn.Dm1, 1000, delegate
            {
                // Amber warning lamp on, flash not available, two DTCs (CM=0):
                //   SPN 100 FMI 18 OC 3   SPN 110 FMI 0 OC 1
                return new byte[] { 0x04, 0xFF, 0x64, 0x00, 0x12, 0x03, 0x6E, 0x00, 0x00, 0x01 };
            });
            engine.AddResponse(65260, delegate { return Ascii("SIMHEAVYDIESEL001*"); });                       // VIN
            engine.AddResponse(65259, delegate { return Ascii("HeavyDiesel*SimEngine*SN0001*Unit1*"); });      // component ID
            engine.AddResponse(65242, delegate { return Prefix(1, Ascii("SIM-ENG-0.1.0*")); });                // software ID
            engine.AddResponse(64965, delegate { return Ascii("SimEngine*ENG001*HeavyDiesel*HW1*"); });        // ECU ID
            engine.AddResponse(J1939Pgn.Dm2, delegate
            {
                // Lamps off, one previously active code: SPN 102 FMI 4 OC 2
                return new byte[] { 0x00, 0xFF, 0x66, 0x00, 0x04, 0x02 };
            });
            engine.AddResponse(J1939Pgn.Dm19, delegate
            {
                byte[] cal = new byte[20];
                cal[0] = 0x78; cal[1] = 0x56; cal[2] = 0x34; cal[3] = 0x12;   // CVN 0x12345678, LSB first
                byte[] id = Ascii("SIMCAL01");
                Buffer.BlockCopy(id, 0, cal, 4, id.Length);
                for (int i = 4 + id.Length; i < 20; i++) cal[i] = 0xFF;
                return cal;
            });
            _ecus.Add(engine);

            // Transmission, SA 0x03. Chatty on ETC1, clean DM1.
            VirtualEcu trans = new VirtualEcu(0x03, MakeName(1, 0, 3, 0, 100, 22222), "Transmission");
            trans.AddBroadcast(61442, 10, delegate { return Fill8(0xFF); });   // ETC1
            trans.AddBroadcast(61445, 100, delegate { return Fill8(0xFF); });  // ETC2
            trans.AddBroadcast(J1939Pgn.Dm1, 1000, delegate
            {
                return new byte[] { 0x00, 0xFF, 0x00, 0x00, 0x00, 0x00, 0xFF, 0xFF };   // no codes, lamps off
            });
            trans.AddResponse(65259, delegate { return Ascii("HeavyDiesel*SimTrans*SN0002**"); });
            _ecus.Add(trans);

            // ABS, SA 0x0B. One single-frame DTC, amber lamp.
            VirtualEcu abs = new VirtualEcu(0x0B, MakeName(1, 0, 9, 0, 101, 33333), "Brakes");
            abs.AddBroadcast(61441, 100, delegate { return Fill8(0xFF); });   // EBC1
            abs.AddBroadcast(J1939Pgn.Dm1, 1000, delegate
            {
                return new byte[] { 0x04, 0xFF, 0x75, 0x02, 0x0C, 0x01, 0xFF, 0xFF };  // SPN 629 FMI 12 OC 1
            });
            _ecus.Add(abs);

            // Instrument cluster, SA 0x17. Proprietary B only.
            VirtualEcu cluster = new VirtualEcu(0x17, MakeName(1, 0, 23, 0, 102, 44444), "Cluster");
            cluster.AddBroadcast(65280, 500, delegate { return Fill8(0xAA); });
            _ecus.Add(cluster);

            // Body controller, SA 0x21. Transmits, never answers a claim request. Real
            // buses have these; the map must show it as occupied with no NAME.
            VirtualEcu body = new VirtualEcu(0x21, null, "Body (never claims)");
            body.AddBroadcast(65281, 250, delegate { return Fill8(0x55); });
            _ecus.Add(body);
        }

        private static J1939Name MakeName(byte industryGroup, byte vehicleSystem, byte function, byte ecuInstance,
            ushort manufacturer, uint identity)
        {
            J1939Name n = new J1939Name();
            n.ArbitraryAddressCapable = false;   // fixed-address ECUs, so they WIN contention against a tool
            n.IndustryGroupValue = industryGroup;
            n.VehicleSystem = vehicleSystem;
            n.Function = function;
            n.EcuInstance = ecuInstance;
            n.ManufacturerCode = manufacturer;
            n.IdentityNumber = identity;
            return n;
        }

        private byte[] BuildEec1()
        {
            // SPN 190 engine speed, bytes 3..4 little-endian, 0.125 rpm/bit. Idle with a wobble.
            double rpm = 780.0 + _random.Next(-15, 16);
            int raw = (int)Math.Round(rpm * 8.0);
            byte[] d = Fill8(0xFF);
            d[3] = (byte)(raw & 0xFF);
            d[4] = (byte)((raw >> 8) & 0xFF);
            return d;
        }

        private byte[] BuildEt1()
        {
            // SPN 110 coolant, byte 0, 1 degC/bit, -40 offset. 86 degC -> 126.
            byte[] d = Fill8(0xFF);
            d[0] = (byte)(86 + 40);
            return d;
        }

        private static byte[] Fill8(byte value)
        {
            byte[] d = new byte[8];
            for (int i = 0; i < 8; i++) d[i] = value;
            return d;
        }

        private static byte[] Ascii(string text)
        {
            return Encoding.ASCII.GetBytes(text);
        }

        private static byte[] Prefix(byte first, byte[] rest)
        {
            byte[] d = new byte[rest.Length + 1];
            d[0] = first;
            Buffer.BlockCopy(rest, 0, d, 1, rest.Length);
            return d;
        }

        // ------------------------------------------------------------------
        //  Simulator thread
        // ------------------------------------------------------------------

        private long NowMs()
        {
            return _clock.ElapsedMilliseconds;
        }

        private void SimulatorLoop()
        {
            while (_run)
            {
                long now = NowMs();
                List<J1939Message> outgoing = new List<J1939Message>();

                lock (_bus)
                {
                    // Periodic broadcasts.
                    for (int e = 0; e < _ecus.Count; e++)
                    {
                        VirtualEcu ecu = _ecus[e];
                        for (int b = 0; b < ecu.Broadcasts.Count; b++)
                        {
                            Broadcast bc = ecu.Broadcasts[b];
                            if (now < bc.NextDueMs) continue;
                            bc.NextDueMs = now + bc.PeriodMs;
                            QueueApplicationMessageLocked(ecu.SourceAddress, bc.Pgn, J1939Pgn.GlobalAddress, bc.Builder(), 6, now, outgoing);
                        }
                    }

                    // One-shot frames (request responses, address claims) that are due.
                    for (int i = _pending.Count - 1; i >= 0; i--)
                    {
                        PendingFrame pf = _pending[i];
                        if (now < pf.DueMs) continue;
                        _pending.RemoveAt(i);
                        outgoing.Add(Stamp(pf.Id, pf.Data, now));
                    }

                    // BAM transfers in flight: next TP.DT every 50 ms.
                    for (int i = _bams.Count - 1; i >= 0; i--)
                    {
                        PendingBam bam = _bams[i];
                        if (now < bam.NextDueMs) continue;

                        if (bam.NextPacket > bam.PacketCount)
                        {
                            _bams.RemoveAt(i);
                            continue;
                        }

                        byte[] dt = new byte[8];
                        dt[0] = (byte)bam.NextPacket;
                        int offset = (bam.NextPacket - 1) * 7;
                        for (int k = 0; k < 7; k++)
                        {
                            int src = offset + k;
                            dt[1 + k] = src < bam.Payload.Length ? bam.Payload[src] : (byte)0xFF;
                        }
                        bam.NextPacket++;
                        bam.NextDueMs = now + BAM_INTER_PACKET_MS;
                        outgoing.Add(Stamp(J1939Id.Create(7, J1939Pgn.TransportProtocolDataTransfer, bam.SourceAddress, J1939Pgn.GlobalAddress), dt, now));
                    }
                }

                for (int i = 0; i < outgoing.Count; i++)
                {
                    Deliver(outgoing[i]);
                }

                Thread.Sleep(TICK_MS);
            }
        }

        /// <summary>Caller must hold <see cref="_bus"/>. Single frame if it fits; BAM otherwise.</summary>
        private void QueueApplicationMessageLocked(byte sourceAddress, uint pgn, byte destination, byte[] payload,
            byte priority, long now, List<J1939Message> outgoing)
        {
            if (payload.Length <= 8)
            {
                outgoing.Add(Stamp(J1939Id.Create(priority, pgn, sourceAddress, destination), payload, now));
                return;
            }

            int packets = (payload.Length + 6) / 7;
            byte[] cm = new byte[8];
            cm[0] = J1939Pgn.TpCmBam;
            cm[1] = (byte)(payload.Length & 0xFF);
            cm[2] = (byte)((payload.Length >> 8) & 0xFF);
            cm[3] = (byte)packets;
            cm[4] = 0xFF;
            cm[5] = (byte)(pgn & 0xFF);
            cm[6] = (byte)((pgn >> 8) & 0xFF);
            cm[7] = (byte)((pgn >> 16) & 0xFF);
            outgoing.Add(Stamp(J1939Id.Create(7, J1939Pgn.TransportProtocolConnectionManagement, sourceAddress, J1939Pgn.GlobalAddress), cm, now));

            PendingBam bam = new PendingBam();
            bam.SourceAddress = sourceAddress;
            bam.Payload = payload;
            bam.PacketCount = packets;
            bam.NextPacket = 1;
            bam.NextDueMs = now + BAM_INTER_PACKET_MS;
            _bams.Add(bam);
        }

        private static J1939Message Stamp(J1939Id id, byte[] data, long now)
        {
            return J1939Message.FromAdapter(id, data, (uint)now, J1939TimestampUnit.Milliseconds);
        }

        private void Deliver(J1939Message message)
        {
            Interlocked.Increment(ref _framesReceived);
            long dropped = 0;
            lock (_queueGate)
            {
                while (_rx.Count >= RECEIVE_QUEUE_CAPACITY)
                {
                    _rx.Dequeue();
                    dropped++;
                }
                _rx.Enqueue(message);
            }
            if (dropped > 0)
            {
                long total = Interlocked.Add(ref _framesDropped, dropped);
                if (total == dropped) RaiseLog("Simulator: receive queue full, frames are being dropped. Drain faster.");
            }

            Action<J1939Message> h = MessageReceived;
            if (h != null) { try { h(message); } catch (Exception) { } }
        }

        // ------------------------------------------------------------------
        //  Tool frames arriving at the bus
        // ------------------------------------------------------------------

        /// <summary>Caller must hold <see cref="_bus"/>.</summary>
        private void HandleToolFrameLocked(J1939Message message, long now)
        {
            uint pgn = message.Pgn;

            if (pgn == J1939Pgn.Request && message.Length >= 3)
            {
                uint requested = (uint)message.Data[0] | ((uint)message.Data[1] << 8) | ((uint)message.Data[2] << 16);
                byte destination = message.DestinationAddress;
                int stagger = 0;

                for (int e = 0; e < _ecus.Count; e++)
                {
                    VirtualEcu ecu = _ecus[e];
                    if (destination != J1939Pgn.GlobalAddress && destination != ecu.SourceAddress) continue;

                    if (requested == J1939Pgn.AddressClaimed)
                    {
                        if (ecu.Name == null) continue;   // the squatter stays silent
                        ScheduleLocked(J1939Id.Create(6, J1939Pgn.AddressClaimed, ecu.SourceAddress, J1939Pgn.GlobalAddress),
                            ecu.Name.ToBytes(), now + REQUEST_RESPONSE_DELAY_MS + stagger);
                        stagger += 10;
                        continue;
                    }

                    ResponseBuilder builder;
                    if (ecu.Responses.TryGetValue(requested, out builder))
                    {
                        byte[] payload = builder();
                        List<J1939Message> outgoing = new List<J1939Message>();
                        // Responses to a directed request go back to the requester; to a
                        // global request they are broadcast, which is what real ECUs do.
                        byte replyTo = destination == J1939Pgn.GlobalAddress ? J1939Pgn.GlobalAddress : message.SourceAddress;
                        if (payload.Length <= 8)
                        {
                            J1939Id id = J1939Id.Create(6, requested, ecu.SourceAddress, IsPdu1(requested) ? replyTo : J1939Pgn.GlobalAddress);
                            ScheduleLocked(id, payload, now + REQUEST_RESPONSE_DELAY_MS + stagger);
                        }
                        else
                        {
                            // Multi-packet replies always go by BAM here. Peer-to-peer RTS/CTS
                            // is the tool's side to drive; see the roadmap.
                            QueueApplicationMessageLocked(ecu.SourceAddress, requested, J1939Pgn.GlobalAddress, payload, 6, now, outgoing);
                            for (int i = 0; i < outgoing.Count; i++) Deliver(outgoing[i]);
                        }
                        stagger += 10;
                    }
                    else if (destination != J1939Pgn.GlobalAddress)
                    {
                        // Directed request for something this ECU does not have: NACK (control byte 1).
                        byte[] nack = new byte[8];
                        nack[0] = 0x01;
                        nack[1] = 0xFF; nack[2] = 0xFF; nack[3] = 0xFF;
                        nack[4] = message.SourceAddress;
                        nack[5] = (byte)(requested & 0xFF);
                        nack[6] = (byte)((requested >> 8) & 0xFF);
                        nack[7] = (byte)((requested >> 16) & 0xFF);
                        ScheduleLocked(J1939Id.Create(6, J1939Pgn.Acknowledgement, ecu.SourceAddress, J1939Pgn.GlobalAddress), nack, now + REQUEST_RESPONSE_DELAY_MS);
                    }
                }
                return;
            }

            if (pgn == J1939Pgn.AddressClaimed)
            {
                // Somebody claimed an address. If it is one of ours, the owner answers with its
                // own claim; whoever has the lower NAME keeps it. Our ECUs are not arbitrary
                // address capable and carry small manufacturer codes, so they win.
                byte claimed = message.SourceAddress;
                for (int e = 0; e < _ecus.Count; e++)
                {
                    VirtualEcu ecu = _ecus[e];
                    if (ecu.SourceAddress != claimed || ecu.Name == null) continue;
                    ScheduleLocked(J1939Id.Create(6, J1939Pgn.AddressClaimed, ecu.SourceAddress, J1939Pgn.GlobalAddress),
                        ecu.Name.ToBytes(), now + 5);
                    RaiseLog(string.Format(CultureInfo.InvariantCulture,
                        "Simulator: tool claimed SA 0x{0:X2}, which {1} holds - contesting.", claimed, ecu.Label));
                }
                return;
            }

            // Everything else (DM3/DM11 clears, proprietary, TP from the tool) is accepted
            // and ignored. A simulator that silently acts on a clear would be worse than
            // one that ignores it.
        }

        /// <summary>Caller must hold <see cref="_bus"/>.</summary>
        private void ScheduleLocked(J1939Id id, byte[] data, long dueMs)
        {
            PendingFrame pf = new PendingFrame();
            pf.Id = id;
            pf.Data = data;
            pf.DueMs = dueMs;
            _pending.Add(pf);
        }

        private static bool IsPdu1(uint pgn)
        {
            return ((pgn >> 8) & 0xFF) <= J1939Id.PDU1_PF_MAXIMUM;
        }

        private void RaiseLog(string text)
        {
            Action<string> h = Log;
            if (h != null) { try { h(text); } catch (Exception) { } }
        }

        // ------------------------------------------------------------------
        //  Records
        // ------------------------------------------------------------------

        private delegate byte[] PayloadBuilder();
        private delegate byte[] ResponseBuilder();

        private sealed class Broadcast
        {
            internal uint Pgn;
            internal int PeriodMs;
            internal long NextDueMs;
            internal PayloadBuilder Builder;
        }

        private sealed class VirtualEcu
        {
            internal readonly byte SourceAddress;
            internal readonly J1939Name Name;
            internal readonly string Label;
            internal readonly List<Broadcast> Broadcasts = new List<Broadcast>();
            internal readonly Dictionary<uint, ResponseBuilder> Responses = new Dictionary<uint, ResponseBuilder>();

            internal VirtualEcu(byte sourceAddress, J1939Name name, string label)
            {
                SourceAddress = sourceAddress;
                Name = name;
                Label = label;
            }

            internal void AddBroadcast(uint pgn, int periodMs, PayloadBuilder builder)
            {
                Broadcast b = new Broadcast();
                b.Pgn = pgn;
                b.PeriodMs = periodMs;
                b.NextDueMs = periodMs;   // first transmit one period in, so nodes do not all fire at t=0
                b.Builder = builder;
                Broadcasts.Add(b);
            }

            internal void AddResponse(uint pgn, ResponseBuilder builder)
            {
                Responses[pgn] = builder;
            }
        }

        private sealed class PendingFrame
        {
            internal J1939Id Id;
            internal byte[] Data;
            internal long DueMs;
        }

        private sealed class PendingBam
        {
            internal byte SourceAddress;
            internal byte[] Payload;
            internal int PacketCount;
            internal int NextPacket;
            internal long NextDueMs;
        }
    }
}
