# RP1210 field guide

What you need to know to talk to a heavy-duty adapter on Windows, written from the code in
`src/HeavyDiesel.Core/Rp1210/` and the scars behind it. This is **not** the specification.
The recommended practice itself (currently revision C) is sold by the Technology & Maintenance
Council of the American Trucking Associations; buy it, it is cheap, and it is the authority
when this guide and it disagree.

## 1. What RP1210 is

A Windows API, in the form of a vendor-supplied 32-bit DLL with a fixed set of `__stdcall`
exports, that lets any application drive any vendor's vehicle diagnostic adapter (VDA) on the
heavy-duty datalinks: SAE J1939 (29-bit CAN at 250/500 kbit/s), SAE J1708/J1587 (RS-485),
raw CAN, and on some adapters ISO 15765, J1850 and Caterpillar Data Link. It is the heavy
world's equivalent of J2534 PassThru. Every OEM tool (Cummins INSITE, Detroit DiagnosticLink,
Allison DOC, Cat ET, Bendix ACom) uses it, which is why every workshop already owns an adapter
that speaks it.

Revisions: **A** (single client, inclusive filters), **B** (multi-client, exclusive filters,
`ReadDetailedVersion`), **C** (automatic CAN baud detection, `GetHardwareStatusEx`, `Ioctl`).
Most drivers in the field are B or C. The vendor INI states which.

## 2. The registry: two INI files

Adapters register themselves in plain INI files under `%WINDIR%` (`C:\Windows`). No hardware
needs to be plugged in to enumerate them, and nothing touches the bus.

**`%WINDIR%\RP121032.INI`** - the root list:

```ini
[RP1210Support]
APIImplementations=NULN2R32,DGDPA5MA,DrewLinQ,CA3rp32
```

Each name is the **base name of a DLL** that lives on the normal `LoadLibrary` search path
(usually `System32`, or `SysWOW64` as seen from a 32-bit process) **and** of a vendor INI.
Stale entries for uninstalled drivers are common; `Rp1210Catalog` skips any whose INI is missing.

**`%WINDIR%\<API>.INI`** - one per vendor API:

```ini
[VendorInformation]
Name=Dearborn Group DPA 5 Multi Application
RP1210=C
AutoDetectCapable=Yes
TimestampWeight=1000
Devices=1,2
Protocols=1,2,3,4

[DeviceInformation1]
DeviceDescription=DPA 5 USB
DeviceName=DPA5USB

[ProtocolInformation1]
ProtocolString=J1939
ProtocolDescription=SAE J1939
ProtocolSpeed=250,500
Devices=1,2
```

Things `Rp1210Catalog` reads and why they matter:

| Key | Meaning | Trap |
|---|---|---|
| `RP1210=` | Revision A/B/C | Decides whether multi-client and exclusive filters exist. Absent = assume A. |
| `AutoDetectCapable=` | Driver can detect CAN baud (`:Baud=Auto`) | Only on C-era drivers. |
| `TimestampWeight=` | Microseconds per tick of the 4-byte receive timestamp | The spec default is 1000 (1 ms). **If absent, HeavyDiesel does not use the adapter timestamps at all** - a guessed scale would run the J1939-21 timers off a fabricated clock. |
| `Devices=` / `[DeviceInformationN]` | Physical adapters the DLL can open | The device ID is the `nDeviceID` you pass to `ClientConnect`. |
| `Protocols=` / `[ProtocolInformationN]` | Protocol strings the DLL accepts | `ProtocolSpeed` is kbit/s for CAN families and bit/s for J1708. |
| `[ProtocolInformationN] Devices=` | **Which** devices speak this protocol | Not every device speaks every protocol. Caterpillar's CCARP32 puts `CAT` on devices 1,3-8 but not 2. Offer the wrong pair and the only symptom is a `ClientConnect` failure. |

Do not read these with `GetPrivateProfileString`: it truncates long `Devices=` lists silently.
`Rp1210Catalog` has its own parser.

## 3. The entry points

Bound at runtime with `LoadLibrary` + `GetProcAddress` because the DLL name is not known until
the user picks an adapter. All `__stdcall`. All integers are 16-bit `short` unless stated.

| Export | Signature (C) | Notes |
|---|---|---|
| `RP1210_ClientConnect` | `(HWND hwnd, short deviceId, const char* protocol, long txBufSize, long rxBufSize, short isAppPacketizing)` | Returns a **client ID below 128** or an **error code at or above 128**. Some drivers return negatives on error. Pass `hwnd = NULL` (window-message notification is unused), buffer sizes 0 for driver defaults. |
| `RP1210_ClientDisconnect` | `(short clientId)` | |
| `RP1210_SendMessage` | `(short clientId, char* msg, short size, short notifyStatusOnTx, short blockOnSend)` | 0 on success. |
| `RP1210_ReadMessage` | `(short clientId, char* buf, short bufSize, short blockOnRead)` | Bytes placed in the buffer; **0 = nothing waiting**; negative = error. There is **no callback model**: you poll. |
| `RP1210_SendCommand` | `(short commandNumber, short clientId, char* data, short size)` | **Command number first, client ID second.** Reversing them is the classic bug and usually presents as a driver that accepts every command and does nothing. |
| `RP1210_ReadVersion` | `(char* dllMajor, char* dllMinor, char* apiMajor, char* apiMinor)` | Four 17-byte buffers. |
| `RP1210_ReadDetailedVersion` | `(short clientId, char* api, char* dll, char* fw)` | Three 17-byte buffers. RP1210B+. |
| `RP1210_GetHardwareStatus` | `(short clientId, char* info, short infoSize, short blocking)` | 64-byte status block, vendor-interpreted. |
| `RP1210_GetHardwareStatusEx` | same arguments, RP1210C | HeavyDiesel binds it only when the plain one is absent, and flags that as unconfirmed. |
| `RP1210_GetErrorMsg` | `(short code, char* text)` | 80-byte buffer. The DLL is the authority on what its own codes mean; render through this, do not keep your own table. |
| `RP1210_GetLastErrorMsg` | `(short code, int* subCode, char* text, short clientId)` | Vendor-specific detail. |
| `RP1210_Ioctl` | RP1210C | Not modelled here. |

The five from `ClientConnect` to `SendCommand` are mandatory; `Rp1210Provider` refuses to bind a
DLL missing any of them. The rest are optional and a missing one is reported, not fatal.

Buffers handed to `ReadMessage` are 2000 bytes. The receive queue depth inside the driver is
the vendor's business; the point is to drain it fast, which is why the provider polls from a
dedicated above-normal-priority thread.

### Protocol strings

Passed verbatim to `ClientConnect`. The name selects the wire format; options follow a colon.

```
J1939              J1939:Baud=250     J1939:Baud=500     J1939:Baud=Auto
CAN                CAN:Baud=250
J1708              ISO15765           J1850              CAT
```

`CAT` is Caterpillar Data Link, and only Caterpillar-family adapters (CA3, Comm Adapter II/III,
the embedded/wireless variants, and Noregon's CA2RP32 driver for the CAII) declare it. A Nexiq
or a DPA5 cannot open CDL no matter what you do in software; `Rp1210CdlSupport` explains that
to the operator rather than showing an empty list. The CDL payload is opaque to this project.

### SendCommand numbers

From `Rp1210Native.Rp1210Command`:

| # | Command | # | Command |
|---|---|---|---|
| 0 | Reset device | 23 | Set broadcast for J1850 |
| 3 | Set all filters states to PASS | 24 | Set J1708 filter type |
| 4 | Set message filtering for J1939 | 25 | Set J1939 filter type |
| 5 | Set message filtering for CAN | 26 | Set CAN filter type |
| 7 | Set message filtering for J1708 | 27 | Set J1939 interpacket time |
| 8 | Set message filtering for J1850 | 28 | Set max error message size |
| 9 | Set message filtering for ISO15765 | 29 | Disallow further connections |
| 14 | Generic driver command | 30 | Set J1850 filter type |
| 15 | Set J1708 mode | 31 | Release J1939 address |
| 16 | Echo transmitted messages | 32 | Set ISO15765 filter type |
| 17 | Set all filters states to DISCARD | 33 | Set broadcast for ISO15765 |
| 18 | Set message receive | 34 | Set ISO15765 flow control |
| 19 | Protect J1939 address | 35 | Clear ISO15765 flow control |
| 20 | Set broadcast for J1708 | 36 | Set ISO15765 link type |
| 21 | Set broadcast for CAN | 37 | Set J1939 baud |
| 22 | Set broadcast for J1939 | 38 | Set ISO15765 baud |
| | | 215 | Set block timeout |
| | | 305 | Set J1708 baud |

Two of these are issued on every connect: **3** (pass all - many drivers default to discarding
everything, so a client that forgets this reads nothing from a healthy bus) and **16** (echo our
own transmits back on the read queue, which is the only reliable way to separate tool traffic
from vehicle traffic in a capture and to confirm a frame reached the wire).

**19 (Protect J1939 address)** matters if a driver refuses to transmit from a source address it
did not see claimed. HeavyDiesel runs its own J1939-81 claim in managed code; the capability
notice tells you to issue 19 through `Rp1210Provider.SendCommand` if your vendor needs it.

## 4. Buffer layouts

Everything the driver hands back starts with a **4-byte big-endian timestamp** in ticks of
`TimestampWeight` microseconds. When echo mode (command 16) is **on**, and only then, one echo
indicator byte follows it (non-zero = this is your own transmit coming back). The provider
tracks the negotiated echo state and parses accordingly; hard-coding the echo byte as present
while echo is off shifts every field by one and gives you a confidently dead bus where the SA
reads as the DA.

**J1939 read**, after timestamp (+ echo byte):

```
[PGN low][PGN mid][PGN high][priority][source address][destination address][data 0..n]
```

PGN is three bytes LSB first. The priority byte's low three bits are the priority; the rest
are reserved. The destination byte is present for every message, including PDU2 broadcasts
where it carries nothing. For a PDU1 group the driver is supposed to report the PGN with its PS
byte cleared and the destination separately; the provider masks defensively and counts
drivers that do not.

**J1939 write**: the same six-byte header followed by data, **no** timestamp and **no** echo byte.
Data is 8 bytes or fewer - HeavyDiesel does its own J1939-21 segmentation.

**CAN read**, after timestamp (+ echo byte):

```
[extended flag 0/1][identifier: 2 bytes if standard, 4 bytes if extended, MSB first][data]
```

`BENCH-CONFIRM`: the identifier is decoded big-endian, which is what the practice specifies and
what TU-RP1210 does, but vendors have been seen to disagree. A wrong endianness is obvious on
a capture (a J1939 broadcast reads as an absurd 11-bit id); check against a known DM1 before
trusting raw-CAN mode on a new adapter.

**J1708, J1850, CAT**: the provider strips the timestamp (and echo byte) and surfaces the rest
as opaque payload. It does not frame these; `SendRaw` is there for callers that know the layout.

### `nIsAppPacketizingIncomingMsgs`

The last argument to `ClientConnect`. **1** means the application performs J1939-21 transport
(segmentation and reassembly); **0** asks the driver to do it. HeavyDiesel passes 1, always,
because driver-side reassembly bypasses the managed transport along with its sequence checks,
timeouts, abort handling and timing-degradation reporting, and hands you a 1785-byte "frame"
dressed as a CAN message. Not every driver honours the flag, so the condition is also detected
on the wire (a J1939 frame with more than 8 data bytes) and reported once per session.
`Rp1210Provider.DriverReassembly` is the escape hatch for a driver that refuses 1 at connect.

## 5. The traps, in the order you will meet them

1. **Your process is 64-bit.** `LoadLibrary` fails with error 193. Build x86.
2. **`ClientConnect` returned 129 and you used it as a client ID.** Below 128 is a client ID;
   128 and above is an error. Negative is an error too on some drivers.
3. **`SendCommand` arguments reversed.** Command number first.
4. **Nothing is received.** Filters default to discard. Send command 3.
5. **Everything is received but every field is shifted.** The echo byte. See section 4.
6. **The timestamps make no sense.** `TimestampWeight`, and counter wrap at 2^32.
7. **A DM1 arrives with 30 bytes.** The driver is reassembling. See `nIsAppPacketizing...`.
8. **The app hangs on disconnect after unplugging the adapter.** The driver is blocked in
   `ReadMessage` on a dead USB endpoint. Do not `FreeLibrary` under it; leak the handle.
9. **The app dies with an access violation inside the DLL.** Corrupted-state exception. See
   `docs/architecture.md` section 2 for the attributes that let managed code even see it, and
   why the session is then declared dead rather than retried.
10. **Two tools on one adapter.** Needs RP1210B+ multi-client and the vendor's cooperation.
    `Capabilities.MultiClient` tells you what the INI promises; the driver tells you the truth.

## 6. What HeavyDiesel does for you

`Rp1210Catalog.EnumerateVendors()` reads the INIs. `new Rp1210Provider(vendor, device)` then
`Connect("J1939:Baud=250", echoTransmits: true, out reason)` loads the DLL, binds, connects,
sets filters to pass, negotiates echo, starts the receive thread and parses every buffer into
`Rp1210Frame` (raw bytes always retained) and, for J1939 families, `J1939Message`. Hand the
provider to `new J1939Session(provider, name)` and the rest of the stack takes over. Every
degradation ends up in `provider.Capabilities.DegradationNotice`, in the `Log` / `Error`
events, or in a counter.

## 7. Vendors seen in the wild

Names as they appear in `RP121032.INI` on real workshop PCs: `NULN2R32` (Nexiq USB-Link 2),
`DGDPA5MA` (Dearborn DPA 5 multi-application), `DrewLinQ`, `CA3rp32` / `CCARP32` / `CAIPRP32`
/ `RFSRP32` (Caterpillar family), `CA2RP32` (Noregon/Vansco driver for the Cat Comm Adapter
II). PEAK-System provide a free RP1210 DLL for their PCAN hardware. If you have an adapter not
in this list, the INI parser will still pick it up - please report how it behaves.
