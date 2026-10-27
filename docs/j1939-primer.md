# J1939 primer

Enough SAE J1939 to read this codebase and to not hurt a machine. It follows the standard's
own parts: **-21** (data link / transport), **-81** (network management), **-73** (diagnostics),
**-71** (application layer, the bit that is licensed). Section references in the source point
into those documents; this page does not replace them.

## 1. The 29-bit identifier (J1939-21)

J1939 runs on CAN 2.0B extended frames, 250 kbit/s on nearly everything, 500 kbit/s on some
newer vehicles. The 29-bit identifier is not an arbitrary number; it is five fields:

```
 28..26   25   24   23..16   15..8    7..0
 priority EDP  DP   PF       PS       source address
 (3 bits) (1)  (1)  (8)      (8)      (8)
```

- **Priority** 0 (highest) to 7. 3 for control, 6 for most broadcasts, 7 for transport.
- **PF** - PDU Format - decides the addressing mode:
  - **PDU1**, PF 0x00-0xEF: the message is addressed to one node and **PS is the destination
    address** (255 = everyone). PS is *not* part of the PGN.
  - **PDU2**, PF 0xF0-0xFF: the message is a broadcast and **PS is a group extension** that *is*
    part of the PGN.
- **PGN** - Parameter Group Number - `EDP<<17 | DP<<16 | PF<<8 | (PDU2 ? PS : 0)`, 18 bits.
  It identifies *what* the message is (DM1, engine speed group, VIN...).
- **Source address** - *who* sent it. Every node on a segment has exactly one.

Forgetting to mask PS out of a PDU1 PGN is the single most common J1939 parser defect, and it
produces a plausible-looking wrong parameter group. `J1939Id` keeps the masking inside the type
and refuses to build an identifier whose addressing mode is wrong.

Examples: `0x18FECA00` = priority 6, PGN 65226 (DM1), SA 0x00 (engine). `0x18EA00F9` =
priority 6, PGN 59904 (Request), destination 0x00, SA 0xF9 (a service tool).

### Addresses worth knowing (J1939-81 Appendix B)

| SA | Node |
|---|---|
| 0 | Engine #1 |
| 3 | Transmission #1 |
| 11 | Brakes / ABS |
| 23 | Instrument cluster #1 |
| 33 | Body controller |
| 128-247 | Dynamically assignable (self-configurable nodes) |
| 249, 250 | Off-board diagnostic service tools #1 and #2 |
| 254 | Null address: "I could not claim one" |
| 255 | Global: destination for broadcasts |

### Data encoding

Parameters are packed little-endian at a start bit inside the 8 data bytes, scaled by a
**SLOT** (scale, limit, offset, transfer function). Engine speed (SPN 190) is bytes 4-5 of
PGN 61444 at 0.125 rpm/bit; coolant (SPN 110) is byte 1 of PGN 65262 at 1 degC/bit with a
-40 offset. (SAE numbers bytes from 1; the C# indexes them from 0, so those are `data[3..4]`
and `data[0]` in `J1939EngineState`.) Every parameter reserves the top of its range: `0xFE..` means *error*, `0xFF..`
means *not available*. Decode those as numbers and you report 215 degC on an engine that is
simply not broadcasting coolant. `J1939EngineState` shows the pattern; `J1939SlotDefinition`
generalises it.

## 2. NAMEs and address claiming (J1939-81)

A diagnostic tool is a first-class node. It must claim a source address before it transmits
anything else, and it must defend that address, or it gets arbitrated off and stops working.

The **NAME** is a 64-bit identity, sent least-significant-byte-first in the 8 data bytes of
**Address Claimed, PGN 60928** (PDU1, to the global address):

```
bit 63    arbitrary address capable (AAC)
62..60    industry group            59..56  vehicle system instance
55..49    vehicle system            48      reserved
47..40    function                  39..35  function instance
34..32    ECU instance              31..21  manufacturer code (SAE-assigned)
20..0     identity number (serial-like, unique per manufacturer)
```

**Contention is decided by numeric value: the lower NAME wins.** AAC is the most significant
bit, so a tool that sets it (and so can relocate) makes its NAME *larger* and loses to any
fixed-address ECU. That is correct: the tool yields, the vehicle keeps working. A losing node
that is not arbitrary-address-capable goes silent until power cycle - on a truck that is a
warranty conversation, on plant with a hydraulic controller in the loop it is a safety one.

How `J1939AddressClaim` behaves, in order:

1. **Listen 1250 ms** before transmitting any claim, and during that window send a **Request
   (PGN 59904) for 60928** from the null address so every node announces itself. Record every
   source address heard - claiming or merely transmitting - as occupied.
2. Claim **249**, then **250**, then an optional operator-nominated fallback, then (if AAC) walk
   the dynamic range downwards from 247 - **skipping every address observed in use**. It never
   picks a "preferred" address and fights for it.
3. Hold the **250 ms contention window**. If a competing claim with a lower NAME arrives,
   relocate (if AAC) or go to the null address and report loudly.
4. If nothing is free, send **Cannot Claim** (Address Claimed from SA 254, after a 0-153 ms
   pseudo-random delay) and tell the operator to nominate an address.

A frame carrying *our* source address is usually our own echo (most adapters loop transmits
back), but it might be another node squatting on our address - which is exactly the case this
machinery exists to catch. The claim code matches echoes against what it actually sent and
treats anything unmatched as a collision.

## 3. Transport protocol (J1939-21)

A CAN frame carries 8 bytes. Anything larger - a DM1 with more than one code, a VIN, a
calibration ID - goes through the transport protocol as a **TP.CM** connection-management
message (PGN 60416) followed by **TP.DT** data-transfer frames (PGN 60160), each carrying a
sequence number and 7 payload bytes. Up to 255 packets = **1785 bytes**.

| TP.CM control byte | Meaning |
|---|---|
| 0x20 **BAM** | Broadcast Announce Message: "everyone, here come N packets of PGN X". No handshake; packets follow at a 50 ms spacing. This is how a multi-code DM1 is sent. |
| 0x10 **RTS** | Request To Send: peer-to-peer session open. |
| 0x11 **CTS** | Clear To Send: the receiver says how many packets it will take and from which. |
| 0x13 **EndOfMsgACK** | Receiver confirms the whole message. |
| 0xFF **Conn_Abort** | Either side tears the session down, with a reason code. |

The **extended** transport protocol (ETP.CM 51200, ETP.DT 50944) does the same for messages up
to ~117 MB, with a 32-bit length and a 24-bit packet offset. (Two earlier values for these PGNs
in this codebase were wrong and named other real messages; the corrections and their evidence
are kept in `J1939Pgn.cs`.)

Timers, from the J1939-21 table and implemented in `J1939TransportProtocol`:

| Timer | ms | Guards |
|---|---|---|
| Tr | 200 | Response to a transport frame |
| Th | 500 | Receiver holding a connection open between CTS frames |
| T1 | 750 | Next TP.DT in a sequence |
| T2 | 1250 | First TP.DT after a CTS |
| T3 | 1250 | Sender awaiting CTS or EndOfMsgACK |
| T4 | 1050 | A "hold connection open" CTS in force |
| inter-packet | 50 | Spacing between transmitted TP.DT frames |

The direction of harm is asymmetric: aborting early loses the transfer, aborting late only
costs time. If a timer is ever in doubt, prefer the longer value.

**Why this matters for timing discipline.** These timers are measured against the adapter's
receive timestamp wherever it has one. Windows scheduling plus USB latency routinely adds tens
of milliseconds between a frame hitting the adapter and managed code seeing it - enough to
abort a healthy transfer or accept a stale one. See `docs/architecture.md` section 3.

### Request and acknowledgement

**Request, PGN 59904** (PDU1): three data bytes, the requested PGN LSB first, to a node or to
the global address. The answer is the requested group itself, broadcast or directed. A node
that cannot answer a directed request replies with **Acknowledgement, PGN 59392**: control
byte 0 ACK, 1 NACK, 2 access denied, 3 cannot respond.

## 4. Diagnostics (J1939-73)

| PGN | DM | What |
|---|---|---|
| 65226 | **DM1** | Active DTCs plus lamp status. Broadcast at 1 Hz while any fault is active, and once more (empty) when the last one clears. Then silence. |
| 65227 | **DM2** | Previously active DTCs. On request. |
| 65228 | **DM3** | Clear previously active DTCs. A *command*. |
| 65229 | DM4 | Freeze frame parameters. |
| 65230 | DM5 | Diagnostic readiness. |
| 65235 | **DM11** | Clear active DTCs. A *command*. |
| 57088 | DM13 | Stop / start broadcast - silences the bus for up to 5 s. |
| 54016 | DM19 | Calibration ID and calibration verification number (CVN). |

### Lamp bytes

Byte 1 of DM1/DM2: bits 7-6 malfunction indicator, 5-4 red stop, 3-2 amber warning, 1-0
protect (00 off, 01 on, 11 not available). Byte 2 is the matching flash field. Technicians
read lamps before codes, which is why `J1939LampStatus` is the first member of every report
and the fault tracker's headline leads with "RED STOP lamp on - stop the machine".

### The DTC (4 bytes, after the two lamp bytes)

```
byte 1   SPN bits 7..0
byte 2   SPN bits 15..8
byte 3   SPN bits 18..16 (bits 7..5) | FMI (bits 4..0)
byte 4   CM (bit 7) | occurrence count (bits 6..0, 127 = not available)
```

**SPN** (suspect parameter number) says *which* parameter; **FMI** (failure mode identifier,
0-31, standardised in J1939-73 Appendix A - "voltage above normal", "current below normal or
open circuit"...) says *how* it failed; the occurrence count says how many times.

**The conversion-method trap (5.7.1.11).** CM bit = 0 means the layout above. CM bit = 1 means
one of three *older* SPN encodings and the message does not say which, so the SPN is
ambiguous. `J1939Diagnostics` carries all candidates and flags the code `ConversionUncertain`;
the tracker and the bench surface that flag rather than picking one and hoping.

### Staleness - the whole problem

DM1 is a broadcast and its **absence carries no information**: the ECU may have gone healthy,
been silenced by DM13, lost power, or the adapter may have dropped the frames. A tool that
keeps showing the last DM1 it saw will show a red stop lamp thirty seconds after the lamp went
out and send a fitter to strip a machine that is fine.

`J1939FaultTracker` therefore keeps four states strictly apart and never collapses them:

| State | Meaning |
|---|---|
| `Unheard` | No DM1 ever received from this address. **Not** "no faults". |
| `NoActiveFaults` | A DM1 within the last 3 s reported nothing. The only good news. |
| `ActiveFaults` | A current DM1 reports codes or a lamp. |
| `Stale` | The last DM1 is older than 3 s. Codes and lamps shown are historical; state is **unknown**. |
| `Undecodable` | A current DM1 arrived but could not be parsed. Also not "no faults". |

Three seconds is three missed 1 Hz periods - the smallest gap ordinary jitter cannot explain
(a multi-code DM1 goes by BAM with 50 ms spacing per packet and routinely runs past 1.2 s on a
loaded bus) and comfortably inside the 5 s DM13 hold. DM2 is on-request, so it gets a 30 s
"ask again" threshold instead.

### Clearing codes

DM3 and DM11 are commands that change the vehicle. `J1939Diagnostics.BuildDm3...` and
`BuildDm11...` return a `J1939ClearRequest` whose `ToMessage(operatorConfirmed)` refuses to
produce a frame without `true`. The gate is in the library on purpose, and the bench does not
expose clears at all.

## 5. What is licensed and what is not

- **Licensed (SAE J1939 Digital Annex, J1939-71):** the SPN and PGN definition tables - which
  parameter lives where, its name, its SLOT. This repository contains none of it, and
  `J1939SpnCatalog` is the seam where you load your own copy at runtime.
- **Protocol structure (not definition data):** the PGNs that make the bus work - TP.CM, TP.DT,
  ETP, Address Claimed, Request, Acknowledgement, DM1-DM19 - are named in `J1939Pgn` and used
  as fallbacks by the catalogue.
- **Standardised and always available:** the 32 FMI descriptions, lamp semantics, the DTC
  layout. That is why a code with no SPN definition still renders usefully:
  `SPN 4364 (no definition loaded) - FMI 5: current below normal or open circuit`.

## 6. Reading the source in order

1. `J1939Id.cs` - 300 lines, the identifier.
2. `J1939Pgn.cs` - the constants and their corrections.
3. `J1939Message.cs` - what a frame carries, including its clock.
4. `J1939AddressClaim.cs` - the NAME and the state machine (read the header comment twice).
5. `J1939Diagnostics.cs` - DM1/DM2/DM19 parsing and the DTC trap.
6. `J1939TransportProtocol.cs` - large; read the enums and the public surface first.
7. `J1939Session.cs` - how it all gets a thread and a clock.
8. `J1939FaultTracker.cs` and `J1939NetworkMap.cs` - what a UI should be given.
