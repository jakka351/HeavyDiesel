# Architecture

This document is the one to read before changing anything. It explains how the pieces fit,
which thread does what, where time comes from, and the rules that every file in `src/` obeys.

## 1. Layering

```
 +------------------------------------------------------------------+
 |  Host application          HeavyDiesel.Bench, your tool          |
 |  UI thread, timers, dialogs, operator confirmation               |
 +------------------------------------------------------------------+
 |  Views / aggregation       J1939NetworkMap   J1939FaultTracker   |
 |  passive, locked, snapshot-returning, caller-supplied clock      |
 +------------------------------------------------------------------+
 |  Session                   J1939Session                          |
 |  ONE pump thread, ONE notifier thread, ONE Stopwatch             |
 |     +-- J1939AddressClaim        (J1939-81 network management)   |
 |     +-- J1939TransportProtocol   (J1939-21 BAM / RTS-CTS / ETP)  |
 |     +-- J1939Diagnostics         (J1939-73, pure functions)      |
 +------------------------------------------------------------------+
 |  Link                      IJ1939Link                            |
 |     Rp1210Provider     SimulatedJ1939Link     (J2534, SocketCAN, |
 |     vendor DLL         virtual truck           slcan: roadmap)   |
 +------------------------------------------------------------------+
 |  Hardware                  Nexiq / DPA5 / DrewLinQ / PEAK / ...  |
 +------------------------------------------------------------------+
```

**Transport is a plugin, protocol is not.** The link moves single CAN frames (8 data bytes or
fewer) and nothing else. It does not claim addresses, it does not reassemble, it does not know
what a DM1 is. Everything above `IJ1939Link` is identical whether the frames come from an RP1210
adapter, the simulator, or a link you write.

`IJ1939Link` is six members, because that is exactly what `J1939Session` needs:

| Member | Contract |
|---|---|
| `IsConnected` | True while a client session is open and usable. |
| `ProtocolString` | Informational, in the adapter's vocabulary. |
| `Capabilities` | What the link can honestly claim. Under-claim where unsure. |
| `SendJ1939(J1939Message)` | Put one frame on the wire. Return true **only** if the adapter accepted it. |
| `DrainJ1939(int max)` | Hand back queued received frames, excluding echoes of our own transmits. Never blocks. |
| `MarkUnloadUnsafe(reason)` | A foreign thread may still be inside the driver; do not unmap on dispose. |

The return value of `SendJ1939` is load-bearing: a refused Address Claimed frame must come back
as `false`, otherwise the claim machine reports an address it never announced.

## 2. Threads

| Thread | Owner | Does | Must never |
|---|---|---|---|
| **RP1210 receive** (`RP1210-RX-<api>`) | `Rp1210Provider` | Polls `RP1210_ReadMessage` non-blocking (RP1210 has no callback model), parses the buffer, enqueues into a bounded queue (8192 frames), raises `FrameReceived` / `J1939MessageReceived`. | Block in a handler. A slow handler stalls the pump and causes counted drops. |
| **Simulator** (`J1939 simulator`) | `SimulatedJ1939Link` | Runs the virtual ECUs, delivers frames into its own bounded queue. | - |
| **Pump** (`J1939 pump`) | `J1939Session` | Every 5 ms: reads the session Stopwatch once, drains up to 256 frames, feeds the address-claim machine and the transport, ticks both with the same `nowMs`, queues notifications. | Raise a public event directly. |
| **Notifier** (`J1939 notifier`) | `J1939Session` | Raises `ApplicationMessageReceived`, `Log`, `AddressClaimStateChanged`, `TransportFault` from a bounded queue (4096). | - |
| **UI** | host | A 250 ms `System.Windows.Forms.Timer` pulls `Evaluate(now)` snapshots from the map and tracker and paints. | Be used for bus I/O. A modal dialog on the UI thread stalls anything running there past T1 and a live transfer is torn down. |

Why two threads in the session: the transport's inter-packet pacing (50 ms) and its timeouts
(200 ms to 1250 ms) are measured on the pump. A UI event handler that takes 300 ms to repaint a
grid would blow those timers if it ran on the pump. So the pump enqueues and the notifier
raises; a slow subscriber only backs up the notification queue, which is counted and reported
rather than silently dropped.

**Every event in this stack is raised on a worker thread.** Host code marshals to the UI itself.
The bench does not even do that - its handlers write only to the thread-safe model objects and
the timer does the painting.

### Inside the driver

`Rp1210Provider` serialises every call into the vendor DLL on one lock (`_apiLock`). Vendor
DLLs are not documented as thread-safe and several demonstrably are not. An
`AccessViolationException` or `SEHException` thrown inside a vendor DLL is a corrupted-state
exception; since .NET 4.0 a managed `catch` only sees one if the method carries both
`[HandleProcessCorruptedStateExceptions]` and `[SecurityCritical]`. Every method that calls
into the driver and catches has both. When one fires the session is marked dead - process state
may already be damaged, so retrying into the same driver is refused until a fresh `Connect`
reloads the module.

`FreeLibrary` is called only when the receive thread is **provably** stopped. If an adapter is
unplugged mid-capture the vendor DLL can block forever inside `ReadMessage`; unmapping code
that a thread is executing is an unrecoverable access violation. The provider leaks the module
handle instead (one reference until process exit) and says so. `MarkUnloadUnsafe` lets the
session, which runs its own thread through the provider, report the same condition.

## 3. The clock

There is no `DateTime.Now` anywhere in `src/`. It is neither monotonic (NTP steps, DST) nor
fine-grained, and an interval measured with it can abort a healthy J1939-21 transfer.

- **Adapter timestamps.** Every RP1210 read buffer starts with a 4-byte big-endian tick counter.
  Its unit is the vendor INI's `TimestampWeight` (microseconds per tick, default 1000). If the
  INI omits it the provider does **not** guess: messages are stamped from the host clock, the
  capability `HardwareTimestamps` is false, and the session logs that transport timing is
  degraded. The simulator stamps milliseconds from its own Stopwatch, so it reports hardware
  timestamps.
- **`J1939Message`** carries both the adapter value (with its unit) and a host-Stopwatch reading,
  and `TimestampSource` says which one `TimestampMs` is. `ElapsedMsBetween` handles the 2^32
  wrap of free-running adapter counters.
- **`J1939Session`** reads one Stopwatch once per pump iteration and feeds that value to both
  `Tick` calls. The address claim's 1250 ms listen window is a safety interlock and the claim
  machine checks it against *real* elapsed time as well, so a mixed timebase cannot satisfy it
  early.
- **Aggregators** (`J1939FaultTracker`, `J1939NetworkMap`) have no clock. The caller supplies the
  arrival time of every report and the "now" of every snapshot, from one clock. Both detect a
  clock that runs backwards and say so in the snapshot instead of inventing a conversion. In the
  bench: an active session uses `SessionClockMs` in and `Session.ElapsedMs` out; listen-only
  mode uses the form's Stopwatch at frame arrival.

## 4. Degrade loudly

The rule that shapes every file: **there is no silent drop path.** Concretely:

- Full receive queue: the oldest frame is dropped, the drop is counted, the first drop and every
  thousandth after it is reported. `DroppedFrameCount` is on the provider and in the status bar.
- Unparseable buffer: counted, the first one per session logged with the echo-byte assumption
  that was in force, the raw bytes retained on the frame.
- Driver missing an optional export: bound with a warning carried into `DegradationNotice`.
  Missing a mandatory one: refused to bind at all.
- Driver reassembling transport frames itself despite being asked not to: detected on the wire
  (a "frame" longer than 8 bytes), reported once, carried into capabilities.
- Vendor INI with no `TimestampWeight`: no hardware timing claimed.
- DM1 not seen for three periods: `Stale`, which is **unknown**, never `NoActiveFaults`.
- Parse failure on a DM1 that yielded nothing: `Undecodable`, never `NoActiveFaults`.
- SPN with no definition: rendered as `SPN n (no definition loaded)` with its standardised FMI
  text, never a guessed name.
- Address claim failed: tool sits on the null address, says so, and does **not** contest.

Capabilities are **under-claimed**: they reflect what the vendor INI declares, not what the API
permits. Over-claiming produces confident wrong answers; under-claiming only produces a
cautious tool.

## 5. Bitness

RP1210 is a 32-bit API by definition - the registry file is literally `RP121032.INI` - and
essentially every vendor DLL ships x86 only. The host process **must** be 32-bit.
`HeavyDiesel.Bench` sets `PlatformTarget=x86`; `HeavyDiesel.Core` is AnyCPU because the library
is bitness-neutral. A 64-bit host gets Win32 error 193 from `LoadLibrary`; the provider names
that error specifically. An out-of-process 32-bit broker for 64-bit hosts is on the roadmap.

## 6. Extension points

### Adding a link

Implement `IJ1939Link`. Look at `SimulatedJ1939Link` first (300 lines, no native code), then
`Rp1210Provider` for the full treatment. Requirements:

- Deliver single frames only. If your hardware or driver reassembles, either turn that off or
  mark the link degraded in `Capabilities.DegradationNotice`.
- Stamp messages with `J1939Message.FromAdapter(id, data, ticks, unit)` when you know the unit,
  `FromHostClock` when you do not. Set `HardwareTimestamps` accordingly.
- Exclude your own echoes from `DrainJ1939`.
- Bound your receive queue and count drops.
- Return `false` from `SendJ1939` when the frame did not reach the adapter.

Candidates we would take a PR for: J2534 PassThru (29-bit CAN), SocketCAN over TCP
(`socketcand`), serial slcan, and an ESP32 bridge over TCP.

### Adding an OEM service layer

`J1939/Oem/IOemServiceDriver.cs` is the operator-workflow contract a service-functions panel
needs: list functions, start/stop a session, run one with confirmation, abort, tick, describe
state. It deliberately carries no wire framing, counters or passwords - those are per-OEM and
live in the driver. The product this came from has Cummins, MaxxForce and John Deere drivers
behind this interface; they are not in this repository because they are derived from
reverse-engineered OEM protocols. The interface is here so that a clean-room implementation
has a shape to fit.

### Adding definition data

`J1939SpnCatalog` is a seam: it ships empty, is populated at runtime by `Load(...)`, and is safe
to read from the pump thread while the UI loads it (one immutable snapshot swapped atomically).
Name-only loading is enough for the network and fault views; SLOT loading enables `TryScale`,
which refuses rather than guesses when a SLOT is unusable. See `docs/bench-app.md` for the CSV
format the bench accepts.

## 7. House style

The code reads a little old-fashioned on purpose. It targets .NET Framework 4.8.1 / C# 7.3 and
is meant to be auditable by people who do not live in C#:

- ASCII only in source. Explicit property bodies. No string interpolation (`string.Format` with
  `CultureInfo.InvariantCulture`). No LINQ in hot paths.
- Every "it is known that..." in a comment is either cited to the standard section or marked
  `BENCH-CONFIRM`.
- Corrections are kept in the comments with their date and evidence (see `J1939Pgn.cs` for the
  DM13/DM19/ETP corrections). The history of being wrong is part of the documentation.
- Public types that return collections return private copies.

See `CONTRIBUTING.md`.
