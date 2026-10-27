# Notices

## TU-RP1210

`src/HeavyDiesel.Core/Rp1210/Rp1210Native.cs` and `Rp1210Catalog.cs` were ported from the
TU-RP1210 reference implementation (Python/ctypes):

    Copyright (c) 2017-2018 Jeremy Daily, The University of Tulsa
    https://github.com/Heavy-Vehicle-Networking-At-U-Tulsa/TU-RP1210
    Used under the MIT License terms of that project's dual licence.

The two reference sources named in `J1939Pgn.cs` for the ETP / DM13 / DM19 corrections were
TU-RP1210's `J1939db.json` and Diesel Explorer's J1939 PGN list; only the four PGN numbers
were taken, no definition data.

## Standards

- **TMC RP1210** is a Recommended Practice of the Technology & Maintenance Council of the
  American Trucking Associations. The practice is not reproduced here; this repository
  documents observed behaviour of the public API in its own words.
- **SAE J1939** (-21, -71, -73, -81) are standards of SAE International. No SPN, PGN or SLOT
  definition tables from the J1939 Digital Annex are included. PGN numbers used for protocol
  structure (transport, network management, diagnostic messages) and the standardised FMI
  text are used as protocol facts.
- Caterpillar, Cummins, Nexiq, Dearborn, DG Technologies, Noregon, Drew Technologies and
  PEAK-System are trademarks of their respective owners and are named only to identify
  interoperating products.

## Safety

This software transmits on vehicle networks. It is provided without warranty of any kind;
see LICENSE. Read "Is it safe?" in the README before connecting to a machine you do not own.
