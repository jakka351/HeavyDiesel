// ///////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
//  J1939EngineState — engine speed and coolant temperature from standard broadcasts.
//
//  WHY THIS EXISTS: a forced DPF regeneration will refuse unless the engine is at idle and
//  warm. The competitor's own code checks exactly that before it will start one
//  ("DPF Regen Init Check - Engine is NOT IDLE", "DPF Init Check - Engine Coolant
//  Temperature is IN RANGE", threshold 80.0). Without that readout in front of the
//  operator, a refused regen looks like a broken tool rather than a cold engine.
//
//  This is ADVISORY ONLY. It reports; it does not gate. The decision to send belongs to
//  the technician, who can see the machine.
//
//  WHY NOT READ THESE THROUGH THE OEM CHANNEL: Cummins exposes both as proprietary
//  parameters, but their SCALING was never recovered — it lives in the competitor's
//  encrypted definition data. Engine speed and coolant temperature are also plain SAE
//  broadcasts with published scaling, so they are taken from there instead: real numbers
//  with a citable derivation, rather than a plausible number from an assumed scale.
//
//  SPN 190  Engine Speed              PGN 61444 (EEC1), start bit 24 -> bytes 3..4 LE,
//                                     16-bit, 0.125 rpm/bit, offset 0.
//  SPN 110  Engine Coolant Temperature PGN 65262 (ET1), start bit 0 -> byte 0,
//                                     8-bit, 1 degC/bit, offset -40 degC.
//                                     (The SAE Digital Annex states this as 1.8 degF/bit
//                                     with a -40 degF offset — the same physical quantity;
//                                     -40 is where the two scales cross. Rendered in degC.)
//
//  Both definitions were taken from the licence-cleared J1939 database, not from memory.
// ///////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
using System;

namespace HeavyDiesel.J1939
{
    /// <summary>
    /// Tracks the two engine values a regeneration depends on, decoded from standard J1939
    /// broadcasts. Passive: feed it received messages, read the properties.
    /// </summary>
    public sealed class J1939EngineState
    {
        /// <summary>EEC1 — carries SPN 190 engine speed.</summary>
        public const uint Eec1Pgn = 61444;

        /// <summary>ET1 — carries SPN 110 engine coolant temperature.</summary>
        public const uint Et1Pgn = 65262;

        /// <summary>
        /// The coolant temperature the regeneration procedure expects, in degrees Celsius.
        /// Recovered from the competitor's own pre-check, not invented.
        /// </summary>
        public const double RegenCoolantMinimumC = 80.0;

        /// <summary>A value older than this is reported as stale rather than as current.</summary>
        public const int StaleAfterMs = 5000;

        private readonly object _gate = new object();
        private double? _rpm;
        private double? _coolantC;
        private DateTime _rpmAtUtc;
        private DateTime _coolantAtUtc;

        /// <summary>Engine speed in rpm, or null when never seen / not available / stale.</summary>
        public double? EngineSpeedRpm
        {
            get { lock (_gate) return Fresh(_rpmAtUtc) ? _rpm : null; }
        }

        /// <summary>Coolant temperature in degrees Celsius, or null when never seen / not available / stale.</summary>
        public double? CoolantTemperatureC
        {
            get { lock (_gate) return Fresh(_coolantAtUtc) ? _coolantC : null; }
        }

        /// <summary>True when the engine is turning. Null when speed is unknown.</summary>
        public bool? EngineRunning
        {
            get
            {
                double? r = EngineSpeedRpm;
                return r.HasValue ? (bool?)(r.Value > 0.0) : null;
            }
        }

        /// <summary>True when coolant is at or above the temperature a regen expects.</summary>
        public bool? CoolantAtRegenTemperature
        {
            get
            {
                double? c = CoolantTemperatureC;
                return c.HasValue ? (bool?)(c.Value >= RegenCoolantMinimumC) : null;
            }
        }

        private static bool Fresh(DateTime atUtc)
        {
            return atUtc != default(DateTime)
                && (DateTime.UtcNow - atUtc).TotalMilliseconds <= StaleAfterMs;
        }

        /// <summary>Feed a received application message. Ignores everything but EEC1 and ET1.</summary>
        public void Observe(uint pgn, byte[] payload)
        {
            if (payload == null) return;

            if (pgn == Eec1Pgn && payload.Length >= 5)
            {
                // SPN 190: bytes 3..4, little-endian, 0.125 rpm/bit.
                int raw = payload[3] | (payload[4] << 8);
                double? rpm = DecodeTwoByte(raw, 0.125, 0.0);
                lock (_gate) { if (rpm.HasValue) { _rpm = rpm; _rpmAtUtc = DateTime.UtcNow; } }
            }
            else if (pgn == Et1Pgn && payload.Length >= 1)
            {
                // SPN 110: byte 0, 1 degC/bit, offset -40 degC.
                double? c = DecodeOneByte(payload[0], 1.0, -40.0);
                lock (_gate) { if (c.HasValue) { _coolantC = c; _coolantAtUtc = DateTime.UtcNow; } }
            }
        }

        // J1939 reserves the top of every parameter's range for "error" and "not
        // available". Decoding those as data is how a tool reports 215 degC on an engine
        // that simply is not broadcasting — a confident wrong answer, which is worse here
        // than a blank.
        private static double? DecodeOneByte(byte raw, double resolution, double offset)
        {
            if (raw >= 0xFE) return null;               // 0xFE error, 0xFF not available
            return raw * resolution + offset;
        }

        private static double? DecodeTwoByte(int raw, double resolution, double offset)
        {
            if (raw >= 0xFE00) return null;             // 0xFExx error, 0xFFxx not available
            return raw * resolution + offset;
        }

        /// <summary>
        /// One line describing regeneration readiness, for display above the routine list.
        /// Says what is known, what is not, and never converts silence into a number.
        /// </summary>
        public string DescribeRegenReadiness()
        {
            double? rpm = EngineSpeedRpm;
            double? c = CoolantTemperatureC;

            if (!rpm.HasValue && !c.HasValue)
                return "Engine state unknown - no EEC1 or ET1 broadcast seen in the last "
                     + (StaleAfterMs / 1000) + " s.";

            var sb = new System.Text.StringBuilder();

            sb.Append("Engine ");
            if (rpm.HasValue)
                sb.Append(rpm.Value <= 0.0 ? "STOPPED" : rpm.Value.ToString("0") + " rpm");
            else
                sb.Append("speed unknown");

            sb.Append("   |   Coolant ");
            if (c.HasValue) sb.Append(c.Value.ToString("0") + " " + (char)0xB0 + "C");
            else sb.Append("unknown");

            // The 80 degC figure is recovered from the competitor's own pre-check. The idle
            // requirement is recorded there too, but WITHOUT a numeric threshold — so the
            // rpm is reported and the judgement left to whoever is standing at the machine,
            // rather than inventing a limit.
            if (c.HasValue && c.Value < RegenCoolantMinimumC)
            {
                sb.Append("   |   BELOW the ").Append(RegenCoolantMinimumC.ToString("0"))
                  .Append((char)0xB0).Append("C a regeneration expects - it will probably refuse.");
            }
            else if (c.HasValue)
            {
                sb.Append("   |   at or above the ").Append(RegenCoolantMinimumC.ToString("0"))
                  .Append((char)0xB0).Append("C a regeneration expects.");
            }

            if (rpm.HasValue && rpm.Value > 0.0)
                sb.Append(" Regeneration also expects the engine at IDLE.");

            return sb.ToString();
        }
    }
}
