# Contributing

Thanks. Heavy-vehicle software has too few hands on it. A few rules keep this codebase the
kind of thing a workshop can trust.

## What we want most

1. **Bench evidence.** Confirm or refute a `BENCH-CONFIRM` comment on real hardware. Say which
   adapter, which driver version, which vehicle, and paste the relevant INI. That beats any
   amount of reasoning.
2. **Links.** A new `IJ1939Link` (J2534, SocketCAN, slcan, ESP32). Read
   `docs/architecture.md` section 6 first; the simulator is the template.
3. **Vendor notes.** `docs/vendors/<api>.md` with what the driver actually does.
4. **Bugs with captures.** A raw frame log (tick "Log every frame" in the bench) and the
   session log turn a report into a fix.

## What we will not merge

- SAE J1939 SPN / PGN / SLOT definition tables, or code that embeds them. The catalogue is a
  seam for a reason. Names you are entitled to share can go in a CSV in your own repo.
- OEM proprietary routines derived from someone else's software.
- Anything that silently drops, silently guesses, or turns "unknown" into "fine". If you
  cannot report a degradation to the operator, do not take the action.
- `DateTime.Now` for interval measurement. Anywhere.
- A theme for the bench.

## House style

The code is C# 7.3 on .NET Framework 4.8.1 and is meant to be readable by people who do not
write C# for a living (mechanics, firmware people, Python people):

- ASCII only in source files.
- Explicit property bodies (`get { return _x; }`), no expression-bodied members in new code.
- `string.Format(CultureInfo.InvariantCulture, ...)`, no string interpolation, no `ToString()`
  without a culture on anything numeric that reaches the wire or a log.
- No LINQ on the pump or receive threads.
- Public collections are returned as private copies; events are raised outside locks;
  handler exceptions are caught and reported, never allowed to kill a pump.
- Every constant that came from a standard cites the section. Every constant that came from
  observation says `BENCH-CONFIRM` and names the adapter it was observed on.
- When you correct a value, leave the correction note with the date and the evidence. Being
  wrong in public, with the paper trail, is how the next person trusts the number.
- Comments explain *why*, and especially why the obvious alternative is wrong. The header
  comment of each file is its design document; keep it true.

## Mechanics

- `dotnet build HeavyDiesel.sln -c Release` must be warning-free.
- `dotnet run -c Release --project tests/HeavyDiesel.SmokeTest` must pass. Add a check for
  what you changed; the smoke test is plain C#, no framework to learn.
- One concern per pull request. Describe what you tested on and how.
- Commit messages: present tense, say what and why, reference the issue.

## Conduct

Be the person you would want on the other end of a 2 a.m. phone call about a stopped haul
truck: direct, specific, not cruel.
