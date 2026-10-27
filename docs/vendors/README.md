# Vendor notes

One file per RP1210 API, named after the DLL base name as it appears in `RP121032.INI`
(`DGDPA5MA.md`, `NULN2R32.md`, `DrewLinQ.md`...). This is where bench evidence lives.

Please include:

1. Adapter model, firmware, driver version (`RP1210_ReadDetailedVersion` output from the
   bench's Adapter tab).
2. The vendor INI, verbatim.
3. Vehicle or bench rig it was tested on, and the protocol string used.
4. Which of the `BENCH-CONFIRM` items in `docs/roadmap.md` you observed, and the answer:
   - CAN identifier byte order in raw-CAN mode
   - whether `nIsAppPacketizingIncomingMsgs=1` is honoured (did a DM1 ever arrive with more than 8 bytes?)
   - `TimestampWeight` present? Timestamps plausible against the host clock?
   - does transmit from an unclaimed source address work, or is command 19 needed?
   - multi-client: can the bench and an OEM tool hold the adapter at once?
5. Anything the Log tab said that surprised you.

A short raw capture (Log tab, "Log every frame", 10 seconds at idle) is worth more than a
paragraph of description.

Template:

```
# <API name> - <vendor> <adapter>

Driver: <version>   Firmware: <version>   RP1210 revision in INI: <A/B/C>
Tested: <date>, <vehicle/rig>, "<protocol string>", by <you>

## INI
<paste>

## Observations
- CAN id byte order: 
- Driver reassembly: 
- TimestampWeight: 
- Unclaimed-SA transmit: 
- Multi-client: 
- Other: 
```
