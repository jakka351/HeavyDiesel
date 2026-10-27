# HeavyDiesel.Bench

The reference host. Deliberately plain: default WinForms controls, no theme, no designer file
(the whole UI is built in `MainForm.cs`, about 1000 lines, so it reads top to bottom). Its job
is to show the stack working and to be the thing you copy from.

## Running it

- It must run as a **32-bit process**. The project is `PlatformTarget=x86`; the EXE lands in
  `src\HeavyDiesel.Bench\bin\x86\<Config>\net481\`. If you see a bitness warning on start you
  built or launched the wrong thing.
- No installer, no registry, no elevation. Settings live in `%APPDATA%\HeavyDiesel\bench.ini`.

## The adapter bar

| Control | What it does |
|---|---|
| **Vendor API** | Every API listed in `%WINDIR%\RP121032.INI` whose vendor INI exists, plus *Simulated bus - no hardware (virtual truck)*. |
| **Device** | The vendor's devices. The ID shown is the `nDeviceID` passed to `ClientConnect`. |
| **Protocol** | One entry per protocol string the chosen device supports, expanded per declared speed (`J1939:Baud=250`, `J1939:Baud=500`), plus `:Baud=Auto` on auto-detect-capable drivers. 250 kbit/s J1939 is preselected when present. Non-J1939 strings (`CAN`, `J1708`, `CAT`) connect too, but only in listen-only mode. |
| **Refresh** | Re-reads the INI files. Use after installing a driver. |
| **Claim a source address** | Ticked (default): run a `J1939Session` - J1939-81 address claim, J1939-21 transport, Requests enabled. Unticked: listen only. Nothing is transmitted, ever. |
| **Connect / Disconnect** | What it says. Disconnect tears down the session first, then the adapter. |

## Tabs

### Network

One row per source address heard. Columns:

| Column | Meaning |
|---|---|
| SA | Source address. |
| Node | `NAME <hex>` when an Address Claimed frame was seen from this address; `(no claim seen)` when the node transmits but has not announced. Never guessed. |
| Func / Mfg / Inst | NAME function code, SAE manufacturer code and ECU instance - the three fields that usually identify a module. |
| Msgs, Msg/s | Messages heard and the rate over the last completed second. |
| PGNs | Distinct parameter groups heard from this node. |
| Last seen | Age of the newest message. |
| State | `active`, `tool addr` (249/250), or `SILENT` after 5 s with nothing heard. Silent is grey and means *unknown*, not *gone*. |

Click a node and the right-hand list shows its parameter groups: PGN (decimal and hex), the
name if the catalogue knows it, count, mean period and last interval in ms, destination
(`all` for broadcasts), payload length (`*` = reassembled from a multi-packet transfer) and
the last payload in hex. The payload column is monospaced for a reason: watching bytes change
is how you find a parameter you do not have a definition for.

The headline above the list is the bus summary. `[...]` after it is a timebase complaint -
the map saw time run backwards, which means two clocks got mixed or an adapter counter
wrapped, and ages are suspect.

### Faults (DM1 / DM2)

One row per ECU the tracker has heard a DM1 or DM2 from, with its `ActiveState`
(`Unheard` / `NoActiveFaults` / `ActiveFaults` / `Stale` / `Undecodable`), the lamp headline,
active and previously-active code counts, DM1 age and count. Rows colour by *effective* lamp
severity (red stop, amber warning, protect/MIL) and go grey when stale. Click a row for the
full rendered entry: one line per code in the form
`SPN 100 (no definition loaded) - FMI 18: data valid but below normal operating range - moderately severe level [occurrences 3]`.

The headline is the sentence a technician reads first. "No DM1 or DM2 received - fault state
UNKNOWN (this is not the same as no faults)" is a deliberate sentence.

### Adapter

The full enumeration report: every API, its DLL name, RP1210 revision, auto-detect flag,
devices, protocols with speeds and per-protocol device restrictions; Cat Data Link
availability (and *why* your Nexiq cannot do it); the NAME this bench will announce and
whether its manufacturer code is a real assignment. After connecting: the driver's
`ReadVersion` / `ReadDetailedVersion` output and the link's capability line including its
degradation notice.

### Log

Everything the stack says, timestamped: RP1210 binding warnings, connect results, the address
claim state transitions, transport faults, every refused request with its reason, and decoded
text replies (VIN, component ID, software ID, ECU ID, DM19). Tick **Log every frame** for a
short raw capture; it floods on a live bus. The box keeps the last 4000 lines.

## Requests menu (active session only)

Read-only Requests (PGN 59904) to the global address: Address Claimed (re-maps who is on the
bus), VIN, Component ID, Software ID, ECU ID, DM2, DM5, DM19. Replies show in the Network
view and, for the text ones, decoded in the Log. There are no clears and no routines here.

## Catalogue menu

HeavyDiesel ships no SAE definition tables. If you have names - from a licensed Digital Annex
export, from an OEM document, from your own notes - put them in a CSV and load it:

```
kind,number,name
PGN,61444,Electronic Engine Controller 1
PGN,65262,Engine Temperature 1
SPN,190,Engine Speed
SPN,110,Engine Coolant Temperature
```

Three columns; `kind` is `SPN` or `PGN`; everything after the second comma is the name so
names may contain commas; `;` and `#` lines and a header row are ignored. Names only - no
scaling is read from this file, because a wrong scale is worse than no scale. The loaded
names appear in the Network PGN column and in fault descriptions immediately; the protocol
PGNs (TP.CM, DM1...) name themselves whether or not anything is loaded.

## Settings file

`%APPDATA%\HeavyDiesel\bench.ini`, plain `key=value`, rewritten on exit:

```ini
VendorApi=DGDPA5MA
DeviceId=1
ProtocolString=J1939:Baud=250
ClaimAddress=1
; J1939-81 NAME fields. ManufacturerCode 0 = not assigned (the claim log will say so).
ManufacturerCode=0
IdentityNumber=1385243
IndustryGroup=1
Function=255
```

- **ManufacturerCode** - your SAE-assigned code. 0 is "not assigned" and is reported in the
  log on every start. Do not type someone else's.
- **IdentityNumber** - drawn at random on first run and kept, so two bench installs on one
  bus have different NAMEs and can resolve a contention between them.
- **IndustryGroup** - 0 global, 1 on-highway, 2 agricultural, 3 construction, 4 marine,
  5 industrial/genset.
- **Function** - the NAME function code. 255 is "not available", the honest default; the
  off-board diagnostic service-tool code is in J1939-81 Appendix B if you have the standard.

## What it does not do, on purpose

No clears, no routines, no proprietary writes, no DBC import, no logging to disk, no theme. It
is a bench, not a product. The product goes on top.
