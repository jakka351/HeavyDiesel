// =============================================================================
//  J1939DashboardSet.cs - the heavy-vehicle instrument set, defined by SPN.
//
//  WHY THE LIGHT-VEHICLE CLUSTER CANNOT SIMPLY BE RELABELLED
//  ---------------------------------------------------------
//  The existing dashboard shows MAF, Lambda, throttle position and ignition
//  timing. Those are petrol / OBD-II concepts. A J1939 diesel has no lambda
//  sensor to speak of, no ignition timing at all, and reports load rather than
//  throttle. Pointing those gauges at J1939 data would either leave them blank
//  forever or, worse, find some numerically similar SPN and present it under the
//  wrong name. So the heavy skin REPLACES the set rather than remapping it.
//
//  DEFINED BY SPN, NOT BY HARD-CODED SCALING
//  ------------------------------------------
//  Each entry names only an SPN, a display label and a sensible gauge range. The
//  PGN it lives in, the bit position, the scale, the offset and the unit all come
//  from the loaded J1939 catalogue via IJ1939SpnSlotResolver.TryScale. Nothing
//  here restates a transfer function, so nothing here can disagree with the
//  catalogue - and an SPN the catalogue cannot place simply reads "--" rather
//  than being decoded by a second, divergent rule.
//
//  RANGES ARE DISPLAY HINTS ONLY. Min/Max/Warn/Danger size the dial and colour
//  the readout; they never clamp or validate a value. The catalogue's SLOT
//  already refuses J1939-71 error and not-available codes, which is the check
//  that matters.
//
//  ⚠️ WARN AND DANGER THRESHOLDS ARE CONVENTIONAL, NOT SPECIFIED. Nothing in
//  J1939 says at what coolant temperature a truck is in trouble - that is an
//  engine-specific figure from the OEM's service data. The values here are
//  ordinary heavy-diesel expectations chosen to colour a dial, and they must not
//  be read as a diagnostic threshold or used to raise a fault.
// =============================================================================

using System.Collections.Generic;

namespace HeavyDiesel.J1939
{
    /// <summary>One gauge on the heavy dashboard.</summary>
    public sealed class J1939GaugeSpec
    {
        internal J1939GaugeSpec(uint spn, string label, string fallbackUnit,
                                double min, double max, double warn, double danger)
        {
            Spn = spn; Label = label; FallbackUnit = fallbackUnit;
            Min = min; Max = max; Warn = warn; Danger = danger;
        }

        /// <summary>Suspect parameter number. Everything else about decoding comes from the catalogue.</summary>
        public uint Spn { get; private set; }

        /// <summary>Short label for the dial face.</summary>
        public string Label { get; private set; }

        /// <summary>
        /// Unit shown before the catalogue has supplied one. The catalogue's unit wins the
        /// moment a reading arrives - it is the authority, and it is taken verbatim.
        /// </summary>
        public string FallbackUnit { get; private set; }

        public double Min { get; private set; }
        public double Max { get; private set; }
        public double Warn { get; private set; }
        public double Danger { get; private set; }
    }

    /// <summary>
    /// The heavy instrument set. Static data only - no I/O, no decoding.
    /// </summary>
    public static class J1939DashboardSet
    {
        private static readonly List<J1939GaugeSpec> _gauges = new List<J1939GaugeSpec>
        {
            // ── engine core ──────────────────────────────────────────────────
            new J1939GaugeSpec(190,  "ENGINE SPEED",   "rpm",   0, 3000, 2200, 2600),
            new J1939GaugeSpec(110,  "COOLANT",        "°C", -40, 130,   98,  105),
            new J1939GaugeSpec(100,  "OIL PRESSURE",   "kPa",   0,  800,  150,   80),
            new J1939GaugeSpec(175,  "OIL TEMP",       "°C", -40, 150,  115,  125),
            new J1939GaugeSpec(102,  "BOOST",          "kPa",   0,  400,  350,  380),
            new J1939GaugeSpec(105,  "INTAKE TEMP",    "°C", -40, 120,   70,   90),
            new J1939GaugeSpec(92,   "ENGINE LOAD",    "%",     0,  100,   90,   98),

            // ── fuel ─────────────────────────────────────────────────────────
            new J1939GaugeSpec(96,   "FUEL LEVEL",     "%",     0,  100,   15,    8),
            new J1939GaugeSpec(94,   "FUEL PRESSURE",  "kPa",   0, 1000,  200,  100),

            // ── road ─────────────────────────────────────────────────────────
            new J1939GaugeSpec(84,   "ROAD SPEED",     "km/h",  0,  120,  100,  110),

            // ── aftertreatment - the heavy-specific half ─────────────────────
            new J1939GaugeSpec(1761, "DEF LEVEL",      "%",     0,  100,   15,    8),
            new J1939GaugeSpec(3719, "DPF SOOT LOAD",  "%",     0,  100,   70,   85),
            new J1939GaugeSpec(3251, "DPF DIFF PRESS", "kPa",   0,   20,   12,   16),
            new J1939GaugeSpec(3242, "DPF INLET TEMP", "°C",  0,  800,  600,  700),
        };

        /// <summary>The heavy gauge set, in display order.</summary>
        public static IList<J1939GaugeSpec> Gauges { get { return _gauges.AsReadOnly(); } }

        /// <summary>
        /// Every PGN the set needs, so a caller can decide whether a received group is worth
        /// decoding without walking the whole list.
        /// </summary>
        /// <remarks>
        /// Resolved from the CATALOGUE, not hard-coded, so it stays correct if the catalogue
        /// changes. Empty when no catalogue is loaded or none of the gauge SPNs carries a PGN
        /// placement - which is the honest answer, because without one nothing can be
        /// decoded anyway.
        /// </remarks>
        /// <param name="catalogue">The loaded SPN catalogue. May be null, giving an empty set.</param>
        /// <returns>The PGNs the gauges draw from. Never null.</returns>
        public static HashSet<uint> RequiredPgns(IJ1939SpnSlotResolver catalogue)
        {
            var pgns = new HashSet<uint>();
            if (catalogue == null) return pgns;

            foreach (J1939GaugeSpec spec in _gauges)
            {
                J1939SpnDefinition definition;
                if (catalogue.TryGetSpn(spec.Spn, out definition) && definition != null && definition.HasPgn)
                {
                    pgns.Add(definition.Pgn);
                }
            }

            return pgns;
        }
    }
}
