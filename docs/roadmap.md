# Roadmap

What is here is the protocol foundation and a bench. What follows is the rest of the kit, in
the order we think it unblocks the most people. Each item is scoped so one developer can land
it. Claim one in an issue before you start.

## 0.1 - foundation (this release)

- [x] RP1210 enumeration, runtime binding, provider with receive thread and wire parsing
- [x] J1939 identifiers, NAME, address claim, transport (BAM / RTS-CTS / ETP), diagnostics
- [x] Fault tracker with staleness, network map, catalogue seam
- [x] Simulated bus (virtual truck) behind the link seam
- [x] WinForms bench: enumerate, connect, network view, fault view, requests
- [x] Smoke test over the simulator, 44 checks, runs on any OS
- [x] Compiles on `dotnet build` (Linux/macOS/Windows) and in Visual Studio

## 0.2 - bench evidence

The code has run against real adapters inside the product it came from; this repository has
not. Every `BENCH-CONFIRM` in the source is an open item:

- [ ] CAN identifier endianness in raw-CAN mode, per vendor
- [ ] `RP1210_GetHardwareStatusEx` argument list on drivers that only export the Ex form
- [ ] Which drivers ignore `nIsAppPacketizingIncomingMsgs=1` and reassemble anyway
- [ ] `TimestampWeight` presence and accuracy per vendor INI
- [ ] J1939-21 timer values against a real ECU (prefer the longer value if in doubt)
- [ ] ETP against a real large transfer (the PGNs were corrected from two references; no capture yet)

A PR that adds a `docs/vendors/<api>.md` with INI contents and observed behaviour is the most
valuable thing this project can receive right now.

## 0.3 - more links behind `IJ1939Link`

- [ ] **J2534 PassThru** (29-bit CAN) - most light-vehicle adapters can do J1939 at 250/500k
- [ ] **SocketCAN over TCP** (`socketcand`) - Linux bench rigs, Raspberry Pi gateways
- [ ] **Serial slcan** - $20 USB dongles
- [ ] **ESP32 bridge** (TCP / BLE) - dual-CAN boards as a wireless VDA
- [ ] A **replay link** that plays a candump / pcap / RP1210 log back through the stack

## 0.4 - the other side of RP1210: an open VDA

Everything open-source so far is a *client* of RP1210. Nobody has shipped the adapter side:

- [ ] **Virtual VDA DLL** - a real RP1210C 32-bit DLL (plain C, `__stdcall` exports) plus vendor
      INI and a registrar that drops it into `RP121032.INI`, backed by the virtual truck. Any
      RP1210 application - including ones you did not write - then talks to a fake Cummins on
      the desk. The DLL has to implement J1939 transport itself (that is the one non-trivial
      part); the stack's transport can be ported or the DLL can host .NET.
- [ ] **Bridge VDA DLL** - the same DLL fronting slcan / SocketCAN / the ESP32 bridge, so cheap
      hardware becomes an RP1210 adapter to OEM-style software.
- [ ] **x64 broker** - a 32-bit host process over named pipes so 64-bit applications can use
      RP1210 drivers. The session already treats the link as an interface; the broker is a link.

## 0.5 - protocol breadth

- [ ] **J1708 / J1587** framing in the provider (MID / PID / SID decode, checksum handling)
      - still the legacy datalink on a great deal of working iron
- [ ] **ISO 15765 / UDS over J1939-class adapters** for 2013+ engines that moved to UDS
- [ ] Peer-to-peer (RTS/CTS) responses in the simulator, and an ETP transfer
- [ ] DM4 freeze frame, DM5 readiness and DM13 handling in the diagnostics layer
- [ ] A **conformance harness** that drives any vendor DLL through the entry points and
      reports which of the traps in `docs/rp1210-field-guide.md` it falls into

## 0.6 - tooling

- [ ] `hd` command-line: `list`, `sniff`, `vin`, `dm1`, `request <pgn>` against any link
- [ ] Log converters: RP1210 capture <-> candump <-> pcap (so Wireshark's J1939 dissector does the heavy lifting)
- [ ] NuGet package for `HeavyDiesel.Core`
- [ ] A loader for a licensed Digital Annex export (SPNs with SLOTs, not just names) - the
      loader ships, the data never does

## Not planned

- SAE definition tables. See the README.
- OEM proprietary routines (regens, injector cut-outs, parameter writes). `IOemServiceDriver`
  is the shape; the content is yours.
- A themed UI. The bench stays plain.
