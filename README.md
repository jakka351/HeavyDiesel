

<a href="https://tester.engineering"><img width="24%" height="24%" align="right" alt="RightToRepair" src="https://github.com/user-attachments/assets/b41c7c2a-f293-45aa-97d7-bebcd65e9433" /></a>

<img width="20%" height="20%" align="right" alt="image" src="https://github.com/user-attachments/assets/63a38050-adf2-447d-b1b8-dfcbdee27819" />

# Heavy Diesel
### RP1210/J1939 Heavy Duty Project Bootstrap

***

An open RP1210 transport and SAE J1939 protocol foundation for heavy-vehicle software, in C#.
Trucks, buses, mining and construction plant, gensets, ag equipment - the heavy world runs on SAE J1939, and on Windows every diagnostic adapter the workshop already owns (Nexiq USB-Link, Dearborn DPA5, DrewLinQ, Noregon DLA+, Caterpillar CA3, PEAK) speaks one API: TMC RP1210. HeavyDiesel is the part of a heavy-duty diagnostic tool that is the same for everybody, released so that nobody has to write it again:

    HeavyDiesel.Core - a .NET Framework 4.8.1 class library (also builds for .NET 8):
    Rp1210 - adapter enumeration from the RP1210 INI registry, a runtime-bound driver for any vendor DLL, the wire formats, the classic bugs already handled.
    J1939 - 29-bit identifiers, NAME and J1939-81 address claiming, the full J1939-21 transport protocol (BAM, RTS/CTS, ETP), J1939-73 diagnostics (DM1/DM2/DM3/DM11/DM19, lamps, DTC decode), a fault tracker that ages what it hears, a network map, an SPN/PGN catalogue seam.
    Transport.Simulation - a virtual truck behind the same seam, so all of the above runs with no adapter and no vehicle.
    HeavyDiesel.Bench - a plain, default-themed WinForms app (x86, as RP1210 requires) that enumerates your adapters, connects, and shows the bus as a network: nodes, NAMEs, PGNs, rates, payloads, and the live DM1/DM2 fault picture.
    tests/HeavyDiesel.SmokeTest - a framework-free console program that drives the whole stack over the simulated bus and checks 44 things. Runs on Linux, macOS or Windows.

    
The code was lifted out of a shipping commercial product (Tester Engineering Heavy) and is written the way heavy diagnostics has to be written: it degrades loudly. Every place the stack cannot do something - a missing driver export, a wedged receive thread, an unparseable buffer, a stale DM1, an SPN with no definition - it says so to the operator instead of guessing. *A confidently wrong parameter sends a fitter to pull a gearbox.*


## Status: 
0.1.0 - foundation. Compiles clean, passes its smoke test, and is wired end to end against the simulator. It has been run against real adapters inside the product it came from, but this repository has not yet been bench-verified against iron. Treat every "BENCH-CONFIRM" comment in the source as a live request.

## Quick start - no hardware
    git clone https://github.com/jakka351/HeavyDiesel
      
    cd HeavyDiesel
      
    dotnet build HeavyDiesel.sln -c Release
    
Open HeavyDiesel.sln in Visual Studio 2019/2022 and run HeavyDiesel.Bench, or run src\HeavyDiesel.Bench\bin\x86\Release\net481\HeavyDiesel.Bench.exe.
In Vendor API pick Simulated bus - no hardware (virtual truck).
Leave Claim a source address ticked and press Connect.  

### Watch the Log tab: 
the tool listens for 1250 ms, claims SA 249, and starts reassembling.
Network tab: five nodes. Four announced a NAME; the body controller at 0x21 did not and is shown that way. Click a node to see the PGNs it transmits, their period and last payload.  

### Faults tab: 
the engine's two-code DM1 (which arrives by BAM, in three CAN frames) and the ABS single-frame DM1. Wait ten seconds after disconnecting and watch them go Stale - that is the point.
Requests menu: ask for the VIN, component ID, DM2 or DM19 and read the reply in the Log.
To prove the stack without the GUI:. 

    dotnet run -c Release --project tests/HeavyDiesel.SmokeTest
    
## Quick start - real adapter
Install the vendor's RP1210 driver (it registers itself in %WINDIR%\RP121032.INI).
Run the x86 bench EXE. A 64-bit process cannot load any RP1210 DLL - you get Win32 error 193 and nothing else. The bench warns on start if you got this wrong.  

Refresh shows every registered API, device and protocol string, read from the INI files only. Nothing is probed, so a vendor that is listed but unplugged still appears.  

Pick the device and J1939:Baud=250 (or 500, or Auto on an RP1210C driver), and Connect.
Claim a source address ticked = the tool is a real J1939-81 node: it listens 1250 ms, claims 249 (then 250, then only addresses it has not heard in use), defends the address, reassembles multi-packet messages and can send Requests. Unticked = listen only: nothing is ever transmitted, single-frame messages are decoded, multi-packet ones are not.
Before you plug into somebody's machine, read "Is it safe?" below.  
  
## Why this exists
Every open-source RP1210 project to date is a client: Python bindings, a Rust logger, a GUI. Useful, but the thing that actually stops a developer with a laptop is everything around the client - the paywalled spec, the $900 adapter, the truck you do not have, the 32-bit DLL in a 64-bit world, the J1939 network management you must get right before you transmit a single byte, and the diagnostic message decoding that is subtly wrong in most hobby code (PDU1 masking, the SPN conversion-method trap, DM1 staleness).  
  
HeavyDiesel ships that layer, with a simulator so you can see it work on day one. Build the tool on top; the OEM-specific routines, the flash, the seed-key - that is where the value is, and that is what stays yours.  

### Repository layout
```
HeavyDiesel.sln
src/
  HeavyDiesel.Core/
    Rp1210/
      Rp1210Native.cs         P/Invoke surface: entry-point delegates, SendCommand numbers, constants
      Rp1210Catalog.cs        Enumerate installed adapters from RP121032.INI + vendor INIs
      Rp1210CdlSupport.cs     Which adapters can open Caterpillar Data Link ("CAT")
      Rp1210Provider.cs       The live driver: load, bind, connect, receive thread, wire parsing
    Transport/
      IJ1939Link.cs           The seam between the J1939 stack and whatever moves frames
      InterfaceCapabilities.cs
      Simulation/
        SimulatedJ1939Link.cs The virtual truck
    J1939/
      J1939Id.cs              29-bit identifier, PDU1/PDU2, PGN masking
      J1939Message.cs         Frame + timing provenance (adapter clock vs host clock)
      J1939Pgn.cs             Protocol PGNs, control bytes, addresses, J1939-21 timers
      J1939AddressClaim.cs    NAME, address-claim state machine, observed source-address map
      J1939TransportProtocol.cs  BAM / RTS-CTS / ETP, both directions, with timers and faults
      J1939Diagnostics.cs     DM1/DM2/DM19 parsing, DTC + lamp decode, DM3/DM11 builders
      J1939FaultTracker.cs    Per-ECU fault state, aged - Unheard / NoActiveFaults / Active / Stale
      J1939NetworkMap.cs      Per-node, per-PGN bus picture for a network view
      J1939Session.cs         The orchestrator: one pump thread, both state machines, events
      J1939SpnCatalog.cs      SPN/PGN/SLOT resolution seam - ships EMPTY (see licensing)
      J1939EngineState.cs     Engine speed + coolant from EEC1 / ET1, with error-code handling
      J1939DashboardSet.cs    A heavy gauge set defined by SPN only
      Oem/IOemServiceDriver.cs  Plug-in point for OEM service routines (Cummins, MaxxForce...)
  HeavyDiesel.Bench/          The WinForms reference host (x86)
tests/
  HeavyDiesel.SmokeTest/      End-to-end check over the simulator
docs/
  architecture.md             Layering, threading, clocks, the degrade-loudly rule, extension points
  rp1210-field-guide.md       The practical RP1210: INI layout, entry points, buffers, the traps
  j1939-primer.md             Identifiers, NAMEs, address claim, transport, diagnostics, staleness
  bench-app.md                Using the bench, settings file, catalogue CSV format
  roadmap.md                  What is missing and what we would like help with
```
## Design in five rules. 
Transport is a plugin, protocol is not. IJ1939Link moves single CAN frames. Address claiming, segmentation, reassembly and diagnostics live above it in managed code and are the same over RP1210, over the simulator, and over whatever link you add next (J2534, SocketCAN, slcan). A driver that reassembles behind the stack's back is detected on the wire and reported.  
  
One pump thread, one clock. All bus I/O happens on a dedicated thread, never a UI timer. Timing comes from the adapter timestamp when its unit is known and from a monotonic Stopwatch otherwise - and every message says which. DateTime.Now is banned.  
  
Degrade loudly. Counters, notices and log lines for every drop, parse failure, missing export, stale report and unknown definition. No silent path.  
  
Under-claim. Capabilities are what the vendor INI declares; readings are shown only when a catalogue can justify them; "no DM1 received" is never shown as "no faults".  
  
Avoid contention, do not win it. The tool listens before claiming, never claims an address it has heard in use, and yields on contention - because the node it would displace might be a hydraulic controller.

## Is it safe?  
Connecting a tool to a live J1939 bus is a transmit, not a read. HeavyDiesel is built to make that transmit as boring as possible, but ***you are responsible for the machine:*** 
Listen-only mode transmits nothing. Use it first on anything you do not own.
Active mode transmits an Address Claimed frame (after 1250 ms of listening) and a Request for Address Claimed at start, then only what you ask it to send. It picks 249/250 first, which are the J1939-81 service-tool addresses, and never claims an address it has heard transmitting.  
  
The bench's Requests menu sends read-only Requests. It has no clear-codes, no routines and no proprietary writes. J1939Diagnostics can build DM3/DM11 clear requests, and they refuse to send without explicit operator confirmation - that gate is deliberate.  
  
Enter your SAE manufacturer code in the settings file. The default of 0 is reported as "not assigned" in the log on every start until you do. Inventing someone else's code puts a false identity on a customer's bus.
  
## Licensing of definition data  
HeavyDiesel contains no SAE J1939 SPN, PGN or SLOT definition tables. Those are licensed material (the SAE J1939 Digital Annex). The catalogue ships empty and the stack is written so an empty catalogue is fully serviceable: failure mode identifiers are standardised (J1939-73 Appendix A) and are always decoded, so a code with no SPN definition still reads as SPN 4364 (no definition loaded) - FMI 5: current below normal or open circuit. If you hold a licence, export names to the CSV format in docs/bench-app.md and load them from the Catalogue menu. Please do not open a pull request that adds SAE tables.  
  
The handful of protocol PGNs the stack names itself (TP.CM, TP.DT, Address Claimed, Request, DM1-DM19) are protocol structure, not definition data.  

## Building  
Visual Studio 2019 or 2022 with the .NET Framework 4.8.1 targeting pack, or the .NET 8 SDK on any OS (dotnet build). The SDK-style projects pull Microsoft.NETFramework.ReferenceAssemblies so the net481 targets compile without the pack.
HeavyDiesel.Core multi-targets net481 and net8.0. The RP1210 code is Windows-only by nature; it compiles everywhere and is simply not useful off Windows.  
  
    HeavyDiesel.Bench is net481, PlatformTarget=x86, no NuGet dependencies at runtime.
  
## Contributing   
Read docs/architecture.md first, then CONTRIBUTING.md. The short version: C# 7.3, ASCII only, explicit property bodies, no string interpolation, no DateTime.Now, no silent drops, no SAE data, and bench evidence beats reasoning - if you confirm or refute a BENCH-CONFIRM comment on real hardware, that is the most valuable PR this project can receive.

## Credits and licence. 
MIT - see LICENSE. Portions of the RP1210 enumeration and binding were ported from TU-RP1210 (Jeremy Daily, The University of Tulsa, MIT) - see NOTICE.md. RP1210 is a recommended practice of the Technology & Maintenance Council of the American Trucking Associations; the specification is available from TMC and is not reproduced here. SAE J1939 is a standard of SAE International.  
  
***Built by Tester Present Specialist Automotive Solutions - a heavy diesel mechanic who got sick of waiting for someone else to write it.***
