// =============================================================================
//  HeavyDiesel smoke test - exercises the stack over the simulated link.
//
//  No test framework on purpose: this is runnable sample code. Each Check prints
//  PASS or FAIL; the process exit code is the number of failures.
//
//  What it proves when it passes:
//    * J1939Id and J1939Name encode/decode exactly (PDU1 masking included).
//    * J1939NetworkMap aggregates and ages correctly.
//    * J1939Session over SimulatedJ1939Link listens 1250 ms, claims SA 249, and
//      then reassembles a two-code DM1 sent by BAM, answers a VIN request (also
//      BAM), maps every claiming node's NAME and leaves the squatter unnamed.
//    * J1939FaultTracker reports the engine's two active codes and ages them.
// =============================================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Threading;
using HeavyDiesel.J1939;
using HeavyDiesel.Transport.Simulation;

namespace HeavyDiesel.SmokeTest
{
    internal static class Program
    {
        private static int _failures;
        private static int _checks;

        private static int Main(string[] args)
        {
            Console.WriteLine("HeavyDiesel smoke test");
            Console.WriteLine("======================");

            TestIdentifiers();
            TestNames();
            TestNetworkMap();
            TestEndToEnd();

            Console.WriteLine();
            Console.WriteLine(string.Format(CultureInfo.InvariantCulture, "{0} checks, {1} failed.", _checks, _failures));
            return _failures;
        }

        // ------------------------------------------------------------------

        private static void TestIdentifiers()
        {
            Console.WriteLine();
            Console.WriteLine("-- J1939Id --");

            // PDU2 broadcast: DM1 from the engine, priority 6.
            J1939Id dm1 = J1939Id.Create(6, J1939Pgn.Dm1, 0x00);
            Check("DM1 raw id is 0x18FECA00", dm1.Raw == 0x18FECA00u);
            Check("DM1 is PDU2 broadcast", dm1.IsPdu2 && dm1.IsBroadcast && dm1.DestinationAddress == 255);
            Check("DM1 PGN round-trips", dm1.Pgn == J1939Pgn.Dm1);

            // PDU1 peer-to-peer: Request from the tool (249) to the engine (0).
            J1939Id req = J1939Id.Create(6, J1939Pgn.Request, 249, 0x00);
            Check("Request raw id is 0x18EA00F9", req.Raw == 0x18EA00F9u);
            Check("Request PGN masks the destination out", req.Pgn == J1939Pgn.Request && req.DestinationAddress == 0 && req.IsPeerToPeer);

            // Decoding a PDU1 frame must not leak the destination into the PGN.
            J1939Id parsed;
            Check("TryFromRaw accepts 29-bit", J1939Id.TryFromRaw(0x18EA00F9u, out parsed) && parsed.Pgn == 59904);
            Check("TryFromRaw rejects >29-bit", !J1939Id.TryFromRaw(0x80000000u, out parsed));

            bool threw = false;
            try { J1939Id.Create(6, 59904 | 0x12, 249, 0); } catch (ArgumentException) { threw = true; }
            Check("PDU1 PGN with non-zero low byte is refused", threw);
        }

        private static void TestNames()
        {
            Console.WriteLine();
            Console.WriteLine("-- J1939Name --");

            J1939Name tool = J1939Name.CreateServiceTool(1234, 777777, IndustryGroup.OnHighway, 255);
            byte[] wire = tool.ToBytes();
            J1939Name back = J1939Name.FromBytes(wire, 0);
            Check("NAME survives wire round trip", back.Equals(tool) && back.Value == tool.Value);
            Check("AAC bit lands in byte 7 MSB", (wire[7] & 0x80) != 0);
            Check("manufacturer / identity decode", back.ManufacturerCode == 1234 && back.IdentityNumber == 777777);

            J1939Name engine = new J1939Name();
            engine.ArbitraryAddressCapable = false;
            engine.ManufacturerCode = 100;
            engine.IdentityNumber = 12345;
            Check("fixed-address ECU beats the tool in contention", engine.WinsContentionAgainst(tool) && !tool.WinsContentionAgainst(engine));
        }

        private static void TestNetworkMap()
        {
            Console.WriteLine();
            Console.WriteLine("-- J1939NetworkMap --");

            J1939NetworkMap map = new J1939NetworkMap();
            map.PgnNameResolver = delegate(uint pgn) { string n; return J1939Pgn.TryDescribe(pgn, out n) ? n : null; };

            for (int i = 0; i < 10; i++)
            {
                map.Observe(61444, 0x00, 255, new byte[8], 100.0 * i, false);   // every 100 ms
            }
            map.Observe(J1939Pgn.Dm1, 0x00, 255, new byte[8], 950.0, false);
            map.Observe(65280, 0x21, 255, new byte[8], 10.0, false);

            J1939NetworkSnapshot snap = map.Evaluate(1200.0);
            Check("two nodes heard", snap.NodeCount == 2);
            Check("three distinct PGNs", snap.UniquePgnCount == 3);

            J1939NodeObservation engine;
            Check("engine node present", snap.TryGetNode(0x00, out engine));
            if (engine != null)
            {
                J1939PgnObservation eec1 = engine.Pgns[0];
                Check("EEC1 counted 10 times", eec1.Pgn == 61444 && eec1.Count == 10);
                Check("EEC1 period is 100 ms", Math.Abs(eec1.AverageIntervalMs - 100.0) < 0.001);
                Check("DM1 named from protocol table", engine.Pgns[1].PgnNameKnown && engine.Pgns[1].PgnName.StartsWith("DM1", StringComparison.Ordinal));
                Check("rate window closed", engine.MessagesPerSecond > 9.0 && engine.MessagesPerSecond < 12.0);
            }

            J1939NetworkSnapshot later = map.Evaluate(7000.0);
            J1939NodeObservation body;
            Check("silent node flagged after 5 s", later.TryGetNode(0x21, out body) && body.IsSilent);
            Check("no timebase complaint on a monotonic clock", later.TimebaseNote == null);
        }

        private static void TestEndToEnd()
        {
            Console.WriteLine();
            Console.WriteLine("-- J1939Session over SimulatedJ1939Link --");

            SimulatedJ1939Link link = new SimulatedJ1939Link();
            List<string> log = new List<string>();
            object logGate = new object();
            link.Log += delegate(string s) { lock (logGate) log.Add("sim: " + s); };
            link.Connect();

            J1939NetworkMap map = new J1939NetworkMap();
            J1939FaultTracker faults = new J1939FaultTracker();
            J1939EngineState engine = new J1939EngineState();
            string vin = null;
            int multiFrameMessages = 0;

            J1939Session session = new J1939Session(link, J1939Name.CreateServiceTool(1234, 777777, IndustryGroup.OnHighway, 255));
            session.Log += delegate(object s, string text) { lock (logGate) log.Add(text); };
            session.AddressClaimStateChanged += delegate(object s, J1939AddressClaimEventArgs e) { lock (logGate) log.Add("claim: " + e); };
            session.TransportFault += delegate(object s, J1939TransportFaultEventArgs e) { lock (logGate) log.Add("FAULT: " + e); };
            session.ApplicationMessageReceived += delegate(object s, J1939ApplicationMessageEventArgs e)
            {
                map.Observe(e.Pgn, e.SourceAddress, e.DestinationAddress, e.Payload, e.SessionClockMs, e.WasMultiFrame);
                if (e.WasMultiFrame) Interlocked.Increment(ref multiFrameMessages);
                if (e.Pgn == J1939Pgn.Dm1) faults.OnDtcReport(J1939Diagnostics.ParseDm1(e.Payload, e.SourceAddress), e.SessionClockMs);
                if (e.Pgn == J1939Pgn.Dm2) faults.OnDtcReport(J1939Diagnostics.ParseDm2(e.Payload, e.SourceAddress), e.SessionClockMs);
                if (e.Pgn == 65260) vin = Encoding.ASCII.GetString(e.Payload).TrimEnd('*', '\0');
                engine.Observe(e.Pgn, e.Payload);
            };
            map.NameResolver = delegate(byte sa) { J1939Name n; return session.TryGetNodeName(sa, out n) ? n : null; };
            faults.NameResolver = map.NameResolver;

            Stopwatch sw = Stopwatch.StartNew();
            session.Start();

            // Listen window is 1250 ms, contention window 250 ms. Give it 2.5 s.
            WaitUntil(delegate { return session.HasClaimedAddress; }, 2500);
            Check("address claimed within 2.5 s", session.HasClaimedAddress);
            Check("claimed the first service-tool address (249)", session.ClaimedAddress == J1939Pgn.ServiceTool1Address);
            Console.WriteLine("   " + session.DescribeStatus());

            // Now the engine's two-code DM1 (10 bytes, BAM) should reassemble within a second or two.
            WaitUntil(delegate
            {
                J1939EcuFaultEntry entry;
                return faults.TryGetEntry(0x00, out entry) && entry.ActiveDtcCount == 2;
            }, 3000);

            J1939BusFaultSnapshot faultSnap = faults.Evaluate(session.ElapsedMs);
            J1939EcuFaultEntry engineFaults;
            Check("engine DM1 reassembled from BAM", faultSnap.TryGetEntry(0x00, out engineFaults) && engineFaults.ActiveDtcCount == 2);
            if (engineFaults != null)
            {
                Check("engine state is ActiveFaults", engineFaults.ActiveState == J1939EcuFaultState.ActiveFaults);
                Check("amber lamp decoded", engineFaults.EffectiveLampSeverity == J1939LampSeverity.Warning);
                Check("SPN 100 / FMI 18 decoded", engineFaults.ActiveDtcs.Count > 0 && engineFaults.ActiveDtcs[0].Spn == 100 && engineFaults.ActiveDtcs[0].Fmi == 18 && engineFaults.ActiveDtcs[0].OccurrenceCount == 3);
                Check("SPN 110 / FMI 0 decoded", engineFaults.ActiveDtcs.Count > 1 && engineFaults.ActiveDtcs[1].Spn == 110 && engineFaults.ActiveDtcs[1].Fmi == 0);
                Console.WriteLine("   " + engineFaults.Headline);
                foreach (string line in engineFaults.ActiveDtcDescriptions) Console.WriteLine("     " + line);
            }
            Check("multi-frame messages arrived", multiFrameMessages > 0);

            J1939EcuFaultEntry absFaults;
            Check("ABS single-frame DM1 decoded (SPN 629 FMI 12)", faultSnap.TryGetEntry(0x0B, out absFaults) && absFaults.ActiveDtcCount == 1 && absFaults.ActiveDtcs[0].Spn == 629 && absFaults.ActiveDtcs[0].Fmi == 12);
            J1939EcuFaultEntry transFaults;
            Check("transmission reports NoActiveFaults", faultSnap.TryGetEntry(0x03, out transFaults) && transFaults.ActiveState == J1939EcuFaultState.NoActiveFaults);
            Console.WriteLine("   " + faultSnap.Headline);

            // Request the VIN (BAM reply) and DM2 (single frame reply).
            string reason;
            Check("VIN request accepted", session.RequestPgn(65260, out reason));
            WaitUntil(delegate { return vin != null; }, 3000);
            Check("VIN received: " + (vin ?? "(none)"), vin == "SIMHEAVYDIESEL001");

            Check("DM2 request accepted", session.RequestPgn(J1939Pgn.Dm2, out reason));
            WaitUntil(delegate { J1939EcuFaultEntry e; return faults.TryGetEntry(0x00, out e) && e.HasDm2; }, 2000);
            faultSnap = faults.Evaluate(session.ElapsedMs);
            Check("DM2 previously-active code (SPN 102) received", faultSnap.TryGetEntry(0x00, out engineFaults) && engineFaults.PreviouslyActiveDtcCount == 1 && engineFaults.PreviouslyActiveDtcs[0].Spn == 102);

            // Network view: five nodes, four with NAMEs, the squatter without.
            J1939NetworkSnapshot net = map.Evaluate(session.ElapsedMs);
            Console.WriteLine("   " + net.Headline);
            Check("five nodes on the bus", net.NodeCount == 5);
            int named = 0;
            foreach (J1939NodeObservation n in net.Nodes) if (n.NameKnown) named++;
            Check("four nodes announced a NAME", named == 4);
            J1939NodeObservation squatter;
            Check("body controller (0x21) heard but unnamed", net.TryGetNode(0x21, out squatter) && !squatter.NameKnown);
            J1939NodeObservation eng;
            Check("engine EEC1 rate looks like 20 ms", net.TryGetNode(0x00, out eng) && eng.MessagesPerSecond > 40.0);
            Check("engine speed decoded around 780 rpm", engine.EngineSpeedRpm.HasValue && Math.Abs(engine.EngineSpeedRpm.Value - 780.0) < 30.0);
            Check("coolant decoded as 86 C", engine.CoolantTemperatureC.HasValue && Math.Abs(engine.CoolantTemperatureC.Value - 86.0) < 0.5);
            Check("no frames dropped by the link", link.DroppedFrameCount == 0);
            Check("no transport faults", !log.Exists(delegate(string s) { return s.StartsWith("FAULT:", StringComparison.Ordinal); }));

            session.Stop();
            session.Dispose();
            link.Dispose();

            // Stop the clock and prove ageing: the DM1 that was current must go stale.
            double frozenNow = faultSnap.ReferenceTimeMs + 10000.0;
            faultSnap = faults.Evaluate(frozenNow);
            Check("DM1 goes stale 10 s after the last broadcast", faultSnap.TryGetEntry(0x00, out engineFaults) && engineFaults.ActiveState == J1939EcuFaultState.Stale);

            Console.WriteLine(string.Format(CultureInfo.InvariantCulture, "   {0} ms wall time, {1} link frames, {2} tool frames",
                sw.ElapsedMilliseconds, link.FramesReceived, link.ToolFramesSent));

            if (_failures > 0 || Array.IndexOf(Environment.GetCommandLineArgs(), "--log") >= 0)
            {
                Console.WriteLine();
                Console.WriteLine("-- session log --");
                lock (logGate) foreach (string line in log) Console.WriteLine("   " + line);
            }
        }

        // ------------------------------------------------------------------

        private static void WaitUntil(Func<bool> condition, int timeoutMs)
        {
            Stopwatch sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                if (condition()) return;
                Thread.Sleep(20);
            }
        }

        private static void Check(string what, bool ok)
        {
            _checks++;
            if (!ok) _failures++;
            Console.WriteLine((ok ? "  PASS  " : "  FAIL  ") + what);
        }
    }
}
