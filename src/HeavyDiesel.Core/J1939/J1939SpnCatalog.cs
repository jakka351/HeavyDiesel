// =============================================================================
//  J1939SpnCatalog.cs - the SPN / PGN / SLOT resolution seam, plus the fault-text
//  renderer that stays useful whether or not any definition data is loaded.
//
//  THIS FILE IS A SEAM, NOT A DATABASE
//  -----------------------------------
//  SAE J1939 SPN and PGN definition tables are licensed material. NOTHING in
//  this file contains, transcribes or derives from such a table, and nothing
//  here reads one from disk. The catalogue ships EMPTY and stays empty until a
//  caller hands it a definition source at runtime - a licensed SAE Digital Annex
//  the host application has loaded, a user's own CSV, whatever the host chooses.
//  An empty catalogue is the correct, honest default state, not a defect, and
//  the renderer below is written so that state is fully serviceable.
//
//  WHY AN EMPTY CATALOGUE IS STILL WORTH SHIPPING
//  ----------------------------------------------
//  The failure mode identifier is standardised across every manufacturer
//  (J1939-73 Appendix A), so J1939Diagnostics.FmiDescription is ALWAYS
//  available - no licence, no download, no connectivity. A code whose SPN
//  cannot be named is therefore still describable:
//
//      SPN 4364 (no definition loaded) - FMI 5: current below normal or open circuit
//
//  That tells a fitter it is chasing an open circuit and gives it the number to
//  look up in the machine's own manual. THAT is honest degradation. Printing a
//  bare number dressed up as a known parameter, or guessing a name from a
//  neighbouring SPN, sends a fitter to pull a driveline off a machine that is
//  fine. The renderer never does either.
//
//  PGN NAMES ARE A DIFFERENT CASE
//  ------------------------------
//  The handful of transport and diagnostic parameter groups that J1939Pgn
//  already names (TP.CM, TP.DT, ETP.*, Address Claimed, Request, DM1-DM19) are
//  protocol structure, not licensed definition data, so PGN lookup falls back
//  to J1939Pgn.TryDescribe when the loaded source has nothing. A loaded source
//  wins where it has an entry, because it is the fuller set.
//
//  SLOT - WHAT IT IS AND WHY THE SCALING RULES ARE WRITTEN THE WAY THEY ARE
//  ------------------------------------------------------------------------
//  A SLOT ("Scaling, Limit, Offset and Transfer function") is the SAE object
//  that turns a raw code off the wire into an engineering value. An SPN points
//  at a SLOT; the SLOT carries the quantity, the scale, the unit, the range and
//  the width. Without one, a raw code is just a number and this catalogue says
//  so rather than inventing a reading.
//
//  TRANSFER FUNCTION IMPLEMENTED HERE, EXACTLY:
//
//      engineering = raw * Scale + Offset,   where Offset == Minimum
//
//  WHERE THE OFFSET COMES FROM. SAE SLOT tables publish scale, range and offset,
//  but the offset is redundant with the range: a SLOT's declared Minimum IS, by
//  construction, the engineering value of raw code 0, so Offset == Minimum. That
//  is an identity, not a guess, and it was checked rather than assumed - across
//  the 275 numeric whole-byte SLOTs available for cross-check, 264 reproduce
//  their own declared Maximum from rawMax * Scale + Minimum. The 11 that do not
//  are defects in the Maximum column (locale decimal separators - a 2-byte count
//  whose maximum reads "64.255" where "64,255" was meant - and documented usable
//  ranges deliberately narrower than the encodable range, such as a percentage
//  SLOT limited to -100..100). NONE of them contradict Offset == Minimum.
//
//  Which is precisely why Maximum NEVER appears in the arithmetic. It is used in
//  one place only - MaximumRawCode - and there it can only WIDEN the accepted
//  raw range, never narrow it, so a corrupt Maximum can cost a refusal but can
//  never produce a wrong number.
//
//  A SLOT WE CANNOT COMPUTE WITH IS KEPT, NOT DROPPED
//  --------------------------------------------------
//  A SLOT whose width or scale will not parse, or that is a bit field, ASCII
//  text or opaque data, is retained with IsUsable false and a stated
//  UnusableReason. The name still displays; TryScale refuses and says why. This
//  matters more than it looks: dropping the entry would make the SPN look like
//  it has no SLOT at all, which is a different and less informative failure.
//
//  WHAT WE DO *NOT* CARRY, AND WHY
//  -------------------------------
//  A per-SPN source table available during development carries a sixth column
//  that is easy to mistake for the parameter's length. It is not. Its values run
//  0-7, it is constant across every SPN inside a parameter group for 1988 of the
//  2008 groups that use it, and it lines up with published J1939 message
//  priorities (engine controller 1 and 2 and transmission control 3, brake and
//  stability groups 2, the general run 6, low-rate groups 7, 0 where the source
//  did not state one). It is a message priority, not a length, and loading it
//  into J1939SpnDefinition.Length would hand a caller a byte count of 6 for a
//  two-byte pressure. Length here means BITS, it comes from the SLOT width, and
//  it is 0 when unknown.
//
//  THREADING
//  ---------
//  The RP1210 / J2534 pump thread resolves names and scales readings while it
//  decodes; the WinForms thread calls Load or Clear from a menu action. All
//  state lives in one immutable snapshot object that is swapped by a single
//  atomic reference write, so readers need no lock and can never observe a
//  half-built map. No dictionary is mutated after it is published, and every
//  definition object is immutable once constructed.
//
//  Nothing here opens a file, touches the registry or reaches the network.
//  ASCII only, no interpolation, explicit property bodies - matches the rest of
//  the J1939 stack.
// =============================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading;

namespace HeavyDiesel.J1939
{
    /// <summary>
    /// What a SLOT's raw code actually is, which decides whether arithmetic on
    /// it means anything.
    /// </summary>
    public enum J1939SlotKind
    {
        /// <summary>The source did not state a kind, or stated one we do not model. Never scaled.</summary>
        Unspecified = 0,

        /// <summary>A measurement: scale and offset apply and the result is an engineering value.</summary>
        Numeric = 1,

        /// <summary>
        /// A bit field or bit-mapped value. The raw code selects a state; there
        /// is no continuous quantity, so multiplying it by anything is
        /// meaningless. Never scaled.
        /// </summary>
        BitField = 2,

        /// <summary>Character data, fixed or delimited. Never scaled.</summary>
        Ascii = 3,

        /// <summary>
        /// Opaque or structurally defined data - proprietary blocks, transfer
        /// payloads, trouble codes, variant data. Carries no transfer function.
        /// Never scaled.
        /// </summary>
        Opaque = 4
    }

    /// <summary>
    /// One SAE SLOT - the scaling, limit, offset and transfer function that
    /// converts a raw J1939 code into an engineering value.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Immutable. Usability is decided once, at construction, and a SLOT that
    /// cannot be computed with is kept rather than dropped so its name still
    /// displays and <see cref="UnusableReason"/> can be shown instead of a
    /// number. See the file header for the transfer function and for where the
    /// offset comes from.
    /// </para>
    /// </remarks>
    public sealed class J1939SlotDefinition
    {
        private readonly int _slotId;
        private readonly string _slotName;
        private readonly string _quantity;
        private readonly double _scale;
        private readonly string _unit;
        private readonly double _minimum;
        private readonly double _maximum;
        private readonly int _widthBits;
        private readonly J1939SlotKind _kind;
        private readonly bool _isUsable;
        private readonly string _unusableReason;

        /// <summary>
        /// Creates a SLOT from already-typed values and decides on the spot
        /// whether it can be computed with.
        /// </summary>
        /// <param name="slotId">Identifier the SPN records refer to.</param>
        /// <param name="slotName">SAE SLOT name, for example "SAEtp01". May be null.</param>
        /// <param name="quantity">Physical quantity, for example "Temperature". May be null.</param>
        /// <param name="scale">Engineering units per raw count. Must be finite and above zero to be usable.</param>
        /// <param name="unit">Unit text, verbatim from the definition source. May be null or blank for a dimensionless quantity.</param>
        /// <param name="minimum">Lowest engineering value, which is also the offset.</param>
        /// <param name="maximum">Highest engineering value. Descriptive only - never used in the scaling arithmetic.</param>
        /// <param name="widthBits">Encoded width in bits. Must be above zero to be usable.</param>
        /// <param name="kind">What the raw code is. Only <see cref="J1939SlotKind.Numeric"/> is scalable.</param>
        public J1939SlotDefinition(int slotId, string slotName, string quantity, double scale,
            string unit, double minimum, double maximum, int widthBits, J1939SlotKind kind)
        {
            _slotId = slotId;
            _slotName = slotName == null ? string.Empty : slotName.Trim();
            _quantity = quantity == null ? string.Empty : quantity.Trim();
            _scale = scale;
            _unit = unit == null ? string.Empty : unit.Trim();
            _minimum = minimum;
            _maximum = maximum;
            _widthBits = widthBits;
            _kind = kind;
            _unusableReason = DetermineUnusableReason(kind, scale, minimum, widthBits);
            _isUsable = _unusableReason == null;
        }

        /// <summary>Identifier that SPN definitions refer to.</summary>
        public int SlotId
        {
            get { return _slotId; }
        }

        /// <summary>SAE SLOT name, for example "SAEtp01". Never null; may be empty.</summary>
        public string SlotName
        {
            get { return _slotName; }
        }

        /// <summary>Physical quantity, for example "Temperature". Never null; may be empty.</summary>
        public string Quantity
        {
            get { return _quantity; }
        }

        /// <summary>
        /// Engineering units per raw count. Meaningless unless
        /// <see cref="IsUsable"/> is true.
        /// </summary>
        public double Scale
        {
            get { return _scale; }
        }

        /// <summary>
        /// The additive term of the transfer function. This is
        /// <see cref="Minimum"/>: a SLOT's declared minimum is by construction
        /// the engineering value of raw code 0. Exposed separately because
        /// calling it "Minimum" at the call site hides what it does.
        /// </summary>
        public double Offset
        {
            get { return _minimum; }
        }

        /// <summary>Unit text exactly as the definition source gave it. Never null; may be empty for a dimensionless quantity.</summary>
        /// <remarks>
        /// Not normalised, not converted, not prettified. Rewriting a unit is
        /// inventing one, and a fitter reading "degC" where the definition said
        /// something else has been told something the tool does not know.
        /// </remarks>
        public string Unit
        {
            get { return _unit; }
        }

        /// <summary>Lowest engineering value, and the offset. See <see cref="Offset"/>.</summary>
        public double Minimum
        {
            get { return _minimum; }
        }

        /// <summary>
        /// Highest engineering value as the definition source declared it.
        /// Descriptive: it never enters the scaling arithmetic, because this
        /// column is the one that carries locale and rounding damage.
        /// </summary>
        public double Maximum
        {
            get { return _maximum; }
        }

        /// <summary>Encoded width in bits. 0 when the source width could not be parsed.</summary>
        public int WidthBits
        {
            get { return _widthBits; }
        }

        /// <summary>What the raw code is.</summary>
        public J1939SlotKind Kind
        {
            get { return _kind; }
        }

        /// <summary>True only when this SLOT can be used to compute an engineering value.</summary>
        public bool IsUsable
        {
            get { return _isUsable; }
        }

        /// <summary>
        /// Why this SLOT cannot be computed with, or null when it can. Written
        /// to be shown to a technician as-is.
        /// </summary>
        public string UnusableReason
        {
            get { return _unusableReason; }
        }

        /// <summary>
        /// Largest raw code that carries a measurement rather than an error or
        /// "not available" indicator.
        /// </summary>
        /// <remarks>
        /// <para>
        /// J1939-71 reserves the top of every unsigned parameter's range: a
        /// one-byte parameter carries data in 0x00-0xFA and signals error or
        /// unavailability above it, a two-byte parameter in 0x0000-0xFAFF, and so
        /// on. Scaling 0xFF on a one-byte temperature yields a confident-looking
        /// 215 degrees that the ECU never reported, which is exactly the class of
        /// answer this stack exists to not give.
        /// </para>
        /// <para>
        /// Some SLOTs do declare the full encodable range as valid data. Where
        /// the declared <see cref="Maximum"/> implies a raw code above the
        /// reserved boundary, this widens to accommodate it, capped at the width.
        /// The widening is one-directional on purpose: a damaged Maximum can cost
        /// a refusal, never buy a wrong number.
        /// </para>
        /// <para>
        /// Zero when the width is unknown, which is itself a reason the SLOT is
        /// unusable.
        /// </para>
        /// </remarks>
        public double MaximumRawCode
        {
            get
            {
                if (_widthBits <= 0 || _widthBits > MAX_MODELLED_WIDTH_BITS)
                {
                    return 0.0;
                }

                double encodableCeiling = Math.Pow(2.0, _widthBits) - 1.0;
                double ceiling = SaeValidCodeCeiling(_widthBits, encodableCeiling);

                // Widen, never narrow - see the remarks.
                if (_scale > 0.0 && IsFinite(_minimum) && IsFinite(_maximum))
                {
                    double declaredCeiling = (_maximum - _minimum) / _scale;
                    if (declaredCeiling > ceiling)
                    {
                        ceiling = Math.Min(declaredCeiling, encodableCeiling);
                    }
                }

                return ceiling;
            }
        }

        /// <summary>
        /// Builds a SLOT from the free text a definition source hands over,
        /// recording anything that will not parse instead of guessing at it.
        /// </summary>
        /// <remarks>
        /// Never returns null and never throws for bad input. A row whose width,
        /// scale or minimum will not parse still produces a SLOT - named, so it
        /// can be displayed - with <see cref="IsUsable"/> false and a reason.
        /// Numeric fields tolerate thousands separators and blanks, because real
        /// definition sources contain both.
        /// </remarks>
        /// <param name="slotId">Identifier the SPN records refer to.</param>
        /// <param name="slotName">SAE SLOT name. May be null.</param>
        /// <param name="quantity">Physical quantity. May be null.</param>
        /// <param name="scaleText">Scale as text. Unparseable text yields an unusable SLOT.</param>
        /// <param name="unit">Unit text, taken verbatim.</param>
        /// <param name="minimumText">Minimum as text. This is the offset, so unparseable text yields an unusable SLOT.</param>
        /// <param name="maximumText">Maximum as text. Unparseable text is tolerated - it is descriptive only.</param>
        /// <param name="widthText">Width as free text such as "2 bytes" or "6 bits".</param>
        /// <param name="kindText">Kind token: NUM, BIN, ASCII or IGN. Anything else maps to <see cref="J1939SlotKind.Unspecified"/>.</param>
        /// <returns>A SLOT, usable or not.</returns>
        public static J1939SlotDefinition CreateFromText(int slotId, string slotName, string quantity,
            string scaleText, string unit, string minimumText, string maximumText,
            string widthText, string kindText)
        {
            double scale;
            if (!TryParseNumber(scaleText, out scale))
            {
                scale = double.NaN;
            }

            double minimum;
            if (!TryParseNumber(minimumText, out minimum))
            {
                minimum = double.NaN;
            }

            double maximum;
            if (!TryParseNumber(maximumText, out maximum))
            {
                maximum = double.NaN;
            }

            int widthBits;
            if (!TryParseWidthBits(widthText, out widthBits))
            {
                widthBits = 0;
            }

            return new J1939SlotDefinition(slotId, slotName, quantity, scale, unit,
                minimum, maximum, widthBits, ParseKind(kindText));
        }

        /// <summary>
        /// Parses a free-text width such as "1 byte", "2 bytes", "6 bits" or
        /// "64 bits" into a bit count.
        /// </summary>
        /// <remarks>
        /// Returns false for anything else - "Variable", blank, a bare number
        /// with no unit. A width the tool guessed at would silently mis-slice
        /// every reading taken through it, so an unrecognised width is reported
        /// rather than defaulted.
        /// </remarks>
        /// <param name="text">The width text. May be null.</param>
        /// <param name="widthBits">Receives the width in bits, or 0 when unparseable.</param>
        /// <returns>True when the text named a definite width.</returns>
        public static bool TryParseWidthBits(string text, out int widthBits)
        {
            widthBits = 0;

            if (string.IsNullOrEmpty(text))
            {
                return false;
            }

            string trimmed = text.Trim();
            int split = 0;
            while (split < trimmed.Length && (char.IsDigit(trimmed[split]) || trimmed[split] == ','))
            {
                split++;
            }

            if (split == 0)
            {
                return false;
            }

            int count;
            if (!int.TryParse(trimmed.Substring(0, split).Replace(",", string.Empty),
                    NumberStyles.Integer, CultureInfo.InvariantCulture, out count) || count < 0)
            {
                return false;
            }

            string unitWord = trimmed.Substring(split).Trim().ToUpperInvariant();

            if (unitWord == "BYTE" || unitWord == "BYTES")
            {
                // Guard the multiply rather than let a 1728-byte ASCII SLOT
                // overflow into a negative bit count.
                if (count > int.MaxValue / 8)
                {
                    return false;
                }

                widthBits = count * 8;
                return true;
            }

            if (unitWord == "BIT" || unitWord == "BITS")
            {
                widthBits = count;
                return true;
            }

            return false;
        }

        /// <summary>One line for a log or a tooltip.</summary>
        /// <returns>The formatted description.</returns>
        public override string ToString()
        {
            if (!_isUsable)
            {
                return string.Format(CultureInfo.InvariantCulture,
                    "SLOT {0} {1} - unusable: {2}", _slotId, _slotName, _unusableReason);
            }

            return string.Format(CultureInfo.InvariantCulture,
                "SLOT {0} {1} - {2}, {3} per count, offset {4}, unit '{5}', {6} bit(s)",
                _slotId, _slotName, _quantity, _scale, _minimum, _unit, _widthBits);
        }

        /// <summary>Widths beyond this are transport payloads, not parameters, and would overflow the shift maths.</summary>
        private const int MAX_MODELLED_WIDTH_BITS = 52;

        /// <summary>
        /// The single place that decides usability, so the constructor and the
        /// text factory can never disagree about it.
        /// </summary>
        private static string DetermineUnusableReason(J1939SlotKind kind, double scale, double minimum, int widthBits)
        {
            if (kind != J1939SlotKind.Numeric)
            {
                return string.Format(CultureInfo.InvariantCulture,
                    "SLOT kind is {0}, not a scalable measurement", DescribeKind(kind));
            }

            if (widthBits <= 0)
            {
                return "encoded width is unknown or zero, so a raw code cannot be bounded";
            }

            if (widthBits > MAX_MODELLED_WIDTH_BITS)
            {
                return string.Format(CultureInfo.InvariantCulture,
                    "encoded width of {0} bits is wider than a single parameter", widthBits);
            }

            if (!IsFinite(scale))
            {
                return "scale factor could not be parsed";
            }

            if (scale <= 0.0)
            {
                // A zero scale collapses every raw code onto the offset, which
                // renders as one confident constant no matter what the ECU said.
                return "scale factor is zero or negative, so no transfer function is defined";
            }

            if (!IsFinite(minimum))
            {
                return "minimum could not be parsed, so the offset is unknown";
            }

            return null;
        }

        private static string DescribeKind(J1939SlotKind kind)
        {
            switch (kind)
            {
                case J1939SlotKind.Numeric: return "numeric";
                case J1939SlotKind.BitField: return "bit field";
                case J1939SlotKind.Ascii: return "ASCII text";
                case J1939SlotKind.Opaque: return "opaque data";
                default: return "unspecified";
            }
        }

        /// <summary>
        /// Highest raw code J1939-71 treats as data for the common whole-byte
        /// widths. Sub-byte fields have no reserved region, so they use the whole
        /// encodable range.
        /// </summary>
        private static double SaeValidCodeCeiling(int widthBits, double encodableCeiling)
        {
            switch (widthBits)
            {
                case 8: return 250.0;             // 0xFA
                case 16: return 64255.0;          // 0xFAFF
                case 24: return 16448250.0;       // 0xFAFFFF
                case 32: return 4211081215.0;     // 0xFAFFFFFF
                default: return encodableCeiling;
            }
        }

        private static J1939SlotKind ParseKind(string kindText)
        {
            if (string.IsNullOrEmpty(kindText))
            {
                return J1939SlotKind.Unspecified;
            }

            switch (kindText.Trim().ToUpperInvariant())
            {
                case "NUM": return J1939SlotKind.Numeric;
                case "BIN": return J1939SlotKind.BitField;
                case "ASCII": return J1939SlotKind.Ascii;
                case "IGN": return J1939SlotKind.Opaque;
                default: return J1939SlotKind.Unspecified;
            }
        }

        /// <summary>
        /// Parses a numeric field, tolerating thousands separators. A blank field
        /// is a failure, not a zero - zero is a meaningful offset.
        /// </summary>
        private static bool TryParseNumber(string text, out double value)
        {
            value = 0.0;

            if (string.IsNullOrEmpty(text))
            {
                return false;
            }

            string trimmed = text.Trim();
            if (trimmed.Length == 0)
            {
                return false;
            }

            return double.TryParse(trimmed.Replace(",", string.Empty),
                NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }

        /// <summary>double.IsFinite does not exist on this framework.</summary>
        internal static bool IsFinite(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }
    }

    /// <summary>
    /// One SPN: its name, the parameter group that carries it, where it sits in
    /// that group, and the SLOT that scales it.
    /// </summary>
    /// <remarks>
    /// Immutable. Every mapping is optional, because real definition sets name
    /// far more SPNs than they place: an SPN can arrive with a name and nothing
    /// else, and that is still worth having.
    /// </remarks>
    public sealed class J1939SpnDefinition
    {
        private readonly uint _spn;
        private readonly string _name;
        private readonly int _pgn;
        private readonly string _startPosition;
        private readonly int _slotId;
        private readonly int _length;

        /// <summary>
        /// Creates a name-only definition - no owning group, no SLOT, no length.
        /// </summary>
        /// <param name="spn">Suspect parameter number.</param>
        /// <param name="name">Parameter name.</param>
        public J1939SpnDefinition(uint spn, string name)
            : this(spn, name, -1, null, -1, 0)
        {
        }

        /// <summary>
        /// Creates a full definition.
        /// </summary>
        /// <param name="spn">Suspect parameter number.</param>
        /// <param name="name">Parameter name. May be null.</param>
        /// <param name="pgn">Owning parameter group number, or any negative value when the SPN has no owning group.</param>
        /// <param name="startPosition">
        /// Position expression within the group's data field, in SAE notation -
        /// "4", "1-2", "3.1", "1,2". Kept as the expression rather than reduced
        /// to a number, because reducing it would discard the bit offset and the
        /// discontiguous cases. May be null.
        /// </param>
        /// <param name="slotId">SLOT identifier, or any negative value when the SPN has no SLOT.</param>
        /// <param name="length">
        /// Encoded length in BITS, or 0 when unknown. The authoritative width is
        /// the SLOT's - see the file header for why a sixth column in one source
        /// table must not be loaded here.
        /// </param>
        public J1939SpnDefinition(uint spn, string name, int pgn, string startPosition, int slotId, int length)
        {
            _spn = spn;
            _name = name == null ? string.Empty : name.Trim();
            _pgn = pgn;
            _startPosition = startPosition == null ? string.Empty : startPosition.Trim();
            _slotId = slotId;
            _length = length > 0 ? length : 0;
        }

        /// <summary>Suspect parameter number, 0 to 524287.</summary>
        public uint Spn
        {
            get { return _spn; }
        }

        /// <summary>Parameter name. Never null; may be empty.</summary>
        public string Name
        {
            get { return _name; }
        }

        /// <summary>True when this SPN has an owning parameter group.</summary>
        public bool HasPgn
        {
            get { return _pgn >= 0; }
        }

        /// <summary>Owning parameter group number. Meaningless unless <see cref="HasPgn"/> is true.</summary>
        public uint Pgn
        {
            get { return _pgn >= 0 ? (uint)_pgn : 0u; }
        }

        /// <summary>
        /// Position expression within the group's data field, in SAE notation.
        /// Never null; empty when the SPN has no placement.
        /// </summary>
        public string StartPosition
        {
            get { return _startPosition; }
        }

        /// <summary>True when this SPN names a SLOT.</summary>
        public bool HasSlot
        {
            get { return _slotId >= 0; }
        }

        /// <summary>SLOT identifier. Meaningless unless <see cref="HasSlot"/> is true.</summary>
        public int SlotId
        {
            get { return _slotId >= 0 ? _slotId : 0; }
        }

        /// <summary>Encoded length in bits, or 0 when unknown.</summary>
        public int Length
        {
            get { return _length; }
        }

        /// <summary>One line for a log or a tooltip.</summary>
        /// <returns>The formatted description.</returns>
        public override string ToString()
        {
            StringBuilder sb = new StringBuilder(96);
            sb.AppendFormat(CultureInfo.InvariantCulture, "SPN {0} {1}", _spn, _name);

            if (HasPgn)
            {
                sb.AppendFormat(CultureInfo.InvariantCulture, ", PGN {0}", Pgn);
                if (_startPosition.Length > 0)
                {
                    sb.AppendFormat(CultureInfo.InvariantCulture, " at {0}", _startPosition);
                }
            }

            if (HasSlot)
            {
                sb.AppendFormat(CultureInfo.InvariantCulture, ", SLOT {0}", SlotId);
            }

            return sb.ToString();
        }
    }

    /// <summary>
    /// Resolves suspect parameter numbers and parameter group numbers to
    /// human-readable names.
    /// </summary>
    /// <remarks>
    /// Both methods return false rather than a placeholder string when no
    /// definition is held, so callers branch on "unknown" instead of parsing
    /// text. An implementation must never invent a name: an unnamed SPN is
    /// still fully serviceable through its standardised FMI, whereas a wrong
    /// name sends a fitter to the wrong system.
    /// </remarks>
    public interface IJ1939SpnResolver
    {
        /// <summary>
        /// Attempts to resolve the parameter name for <paramref name="spn"/>.
        /// </summary>
        /// <param name="spn">Suspect parameter number, 0 to 524287.</param>
        /// <param name="name">Receives the parameter name, or null when unknown.</param>
        /// <returns>True only when a definition is held for this SPN.</returns>
        bool TryGetSpnName(uint spn, out string name);

        /// <summary>
        /// Attempts to resolve the group name for <paramref name="pgn"/>.
        /// </summary>
        /// <param name="pgn">Parameter group number.</param>
        /// <param name="name">Receives the group name, or null when unknown.</param>
        /// <returns>True only when a definition is held for this PGN.</returns>
        bool TryGetPgnName(uint pgn, out string name);
    }

    /// <summary>
    /// A resolver that also carries SLOT data, so a raw code can be turned into
    /// an engineering value.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="IJ1939SpnResolver"/> rather than folded into it:
    /// a name source and a scaling source are different capabilities, and a
    /// caller that only names SPNs should not have to pretend it can scale them.
    /// Consumers test for this interface and fall back to names alone.
    /// </remarks>
    public interface IJ1939SpnSlotResolver : IJ1939SpnResolver
    {
        /// <summary>
        /// Attempts to resolve the full definition for <paramref name="spn"/>.
        /// </summary>
        /// <param name="spn">Suspect parameter number.</param>
        /// <param name="definition">Receives the definition, or null when unknown.</param>
        /// <returns>True only when a definition is held.</returns>
        bool TryGetSpn(uint spn, out J1939SpnDefinition definition);

        /// <summary>
        /// Attempts to resolve the SLOT that scales <paramref name="spn"/>.
        /// </summary>
        /// <param name="spn">Suspect parameter number.</param>
        /// <param name="slot">Receives the SLOT, or null when there is none.</param>
        /// <returns>
        /// True when a SLOT was found. The SLOT may still be unusable - check
        /// <see cref="J1939SlotDefinition.IsUsable"/>.
        /// </returns>
        bool TryGetSlotForSpn(uint spn, out J1939SlotDefinition slot);

        /// <summary>
        /// Converts a raw code into an engineering value, or refuses and says why.
        /// </summary>
        /// <param name="spn">Suspect parameter number.</param>
        /// <param name="rawValue">The assembled raw code off the wire.</param>
        /// <param name="engineeringValue">Receives the scaled value on success, 0 otherwise.</param>
        /// <param name="unit">Receives the unit on success, empty otherwise.</param>
        /// <param name="failureReason">Receives the reason on failure, null on success.</param>
        /// <returns>True only when the value is fully justified by a usable SLOT.</returns>
        bool TryScale(uint spn, double rawValue, out double engineeringValue,
            out string unit, out string failureReason);
    }

    /// <summary>
    /// An SPN / PGN / SLOT catalogue that ships empty and is populated at runtime
    /// from a definition source supplied by the caller.
    /// </summary>
    /// <remarks>
    /// <para>
    /// No definition data is compiled into this type. Construct it, and until
    /// <see cref="Load"/> is called every SPN reports as having no definition
    /// loaded and PGN lookup falls back to the protocol names in
    /// <see cref="J1939Pgn.TryDescribe"/>. That is a working state, not a
    /// broken one - see <see cref="J1939FaultText.DescribeFault"/>.
    /// </para>
    /// <para>
    /// Instances are safe to read from any thread while another thread loads or
    /// clears them.
    /// </para>
    /// </remarks>
    public sealed class J1939SpnCatalog : IJ1939SpnSlotResolver
    {
        private readonly Func<uint, string> _spnNameLookup;
        private CatalogSnapshot _snapshot;

        /// <summary>
        /// Creates an empty catalogue. Nothing is loaded and nothing is read
        /// from disk; call <see cref="Load"/> to populate it.
        /// </summary>
        public J1939SpnCatalog()
        {
            _snapshot = CatalogSnapshot.Empty;
            _spnNameLookup = new Func<uint, string>(LookupSpnName);
        }

        /// <summary>
        /// Number of SPN names currently held. Zero on a freshly constructed
        /// catalogue.
        /// </summary>
        /// <remarks>
        /// This counts entries actually accepted by <see cref="Load"/>. Entries
        /// whose name was null, empty or whitespace are dropped, because an
        /// entry with a blank name renders as a named parameter with no name -
        /// which reads to a fitter as though the tool knows something it does
        /// not. A source that yields fewer entries than it contains is
        /// visible here rather than silently absorbed.
        /// </remarks>
        public int SpnCount
        {
            get { return Volatile.Read(ref _snapshot).SpnNames.Count; }
        }

        /// <summary>
        /// Number of PGN names currently held, not counting the protocol groups
        /// that <see cref="J1939Pgn.TryDescribe"/> names on its own.
        /// </summary>
        public int PgnCount
        {
            get { return Volatile.Read(ref _snapshot).PgnNames.Count; }
        }

        /// <summary>
        /// Number of full SPN definitions held - the subset of
        /// <see cref="SpnCount"/> that carries a group, a placement or a SLOT.
        /// </summary>
        /// <remarks>
        /// Deliberately smaller than <see cref="SpnCount"/> in a real load. A
        /// definition set names far more parameters than it places, and the gap
        /// between these two numbers is exactly the population that will resolve
        /// by name but refuse to scale. Showing both keeps that visible.
        /// </remarks>
        public int SpnDefinitionCount
        {
            get { return Volatile.Read(ref _snapshot).SpnDefinitions.Count; }
        }

        /// <summary>Number of SLOTs held, usable or not.</summary>
        public int SlotCount
        {
            get { return Volatile.Read(ref _snapshot).Slots.Count; }
        }

        /// <summary>
        /// Number of held SLOTs that can actually be computed with.
        /// </summary>
        /// <remarks>
        /// The difference against <see cref="SlotCount"/> is the honest measure
        /// of how much of a loaded set will scale. Bit fields, text and opaque
        /// blocks legitimately land in the gap; so does anything the source
        /// stated in a form we would not guess at.
        /// </remarks>
        public int UsableSlotCount
        {
            get { return Volatile.Read(ref _snapshot).UsableSlotCount; }
        }

        /// <summary>
        /// True when at least one definition is held.
        /// </summary>
        /// <remarks>
        /// Loading a source that yielded no usable entries leaves this false on
        /// purpose. A catalogue that cannot resolve anything is not loaded,
        /// whatever the caller intended, and reporting otherwise would let a UI
        /// claim definitions are available when every lookup will miss.
        /// </remarks>
        public bool IsLoaded
        {
            get
            {
                CatalogSnapshot snapshot = Volatile.Read(ref _snapshot);
                return snapshot.SpnNames.Count > 0
                    || snapshot.PgnNames.Count > 0
                    || snapshot.SpnDefinitions.Count > 0
                    || snapshot.Slots.Count > 0;
            }
        }

        /// <summary>
        /// A delegate adapter over <see cref="TryGetSpnName"/> that returns null
        /// for an SPN with no definition.
        /// </summary>
        /// <remarks>
        /// Shaped to pass straight to
        /// <see cref="J1939Dtc.Describe(Func{uint,string})"/> and to
        /// <c>J1939FaultTracker.SpnNameResolver</c>. The same delegate instance
        /// is returned every time, so it can be compared or detached, and it
        /// stays correct across later <see cref="Load"/> and <see cref="Clear"/>
        /// calls because it reads the live snapshot each invocation.
        /// </remarks>
        public Func<uint, string> SpnNameLookup
        {
            get { return _spnNameLookup; }
        }

        /// <summary>
        /// Replaces the entire catalogue with the supplied definitions.
        /// </summary>
        /// <remarks>
        /// <para>
        /// This REPLACES, it does not merge: whatever was held before is
        /// discarded. A null argument is treated as an empty map, so
        /// <c>Load(spns, null)</c> loads SPN names and leaves PGN lookup on the
        /// protocol fallback. Both maps are swapped together in one atomic
        /// reference write, so a concurrent reader sees either the whole old
        /// catalogue or the whole new one.
        /// </para>
        /// <para>
        /// The dictionaries are copied. The caller may reuse or mutate its own
        /// instances afterwards without corrupting a lookup running on the pump
        /// thread.
        /// </para>
        /// </remarks>
        /// <param name="spnNames">SPN number to parameter name. May be null.</param>
        /// <param name="pgnNames">PGN number to group name. May be null.</param>
        public void Load(IDictionary<uint, string> spnNames, IDictionary<uint, string> pgnNames)
        {
            CatalogSnapshot replacement = new CatalogSnapshot(
                CopyNamedEntries(spnNames),
                CopyNamedEntries(pgnNames),
                CatalogSnapshot.EmptySpnDefinitions,
                CatalogSnapshot.EmptySlots);

            Volatile.Write(ref _snapshot, replacement);
        }

        /// <summary>
        /// Replaces the entire catalogue with a full definition set: SPN
        /// records, SLOT records, a names-only superset and PGN names.
        /// </summary>
        /// <remarks>
        /// <para>
        /// This REPLACES, it does not merge with what was held before. Every map
        /// is built first and published in one atomic reference write, so a
        /// concurrent reader on the pump thread sees either the whole old
        /// catalogue or the whole new one - never an SPN whose SLOT has not
        /// arrived yet.
        /// </para>
        /// <para>
        /// MERGE ORDER FOR NAMES. Definition sets routinely name more parameters
        /// than they place, so <paramref name="spnNames"/> exists to carry that
        /// superset. Where an SPN appears in both, the name on its
        /// <see cref="J1939SpnDefinition"/> wins, because that record is the one
        /// that also states the group and the SLOT and is therefore the more
        /// specific of the two. Names with no matching definition still resolve
        /// through <see cref="TryGetSpnName"/> and still refuse to scale.
        /// </para>
        /// <para>
        /// Entries are dropped only when they cannot be used at all: an SPN with
        /// no name and no mapping, or a duplicate identifier. A SLOT that will
        /// not scale is NOT dropped - it is held so its name displays and its
        /// <see cref="J1939SlotDefinition.UnusableReason"/> can be shown.
        /// Nothing is copied by reference from a caller's collection except the
        /// definition objects themselves, which are immutable.
        /// </para>
        /// </remarks>
        /// <param name="spnDefinitions">Full SPN records. May be null.</param>
        /// <param name="slotDefinitions">SLOT records. May be null.</param>
        /// <param name="spnNames">Names-only superset. May be null.</param>
        /// <param name="pgnNames">PGN number to group name. May be null.</param>
        public void Load(IEnumerable<J1939SpnDefinition> spnDefinitions,
            IEnumerable<J1939SlotDefinition> slotDefinitions,
            IDictionary<uint, string> spnNames,
            IDictionary<uint, string> pgnNames)
        {
            Dictionary<uint, string> names = CopyNamedEntries(spnNames);
            Dictionary<uint, J1939SpnDefinition> definitions = new Dictionary<uint, J1939SpnDefinition>();

            if (spnDefinitions != null)
            {
                foreach (J1939SpnDefinition definition in spnDefinitions)
                {
                    if (definition == null)
                    {
                        continue;
                    }

                    // A record with neither a name nor a mapping resolves nothing
                    // and would only inflate the loaded count.
                    if (definition.Name.Length == 0 && !definition.HasPgn && !definition.HasSlot)
                    {
                        continue;
                    }

                    definitions[definition.Spn] = definition;

                    if (definition.Name.Length > 0)
                    {
                        names[definition.Spn] = definition.Name;
                    }
                }
            }

            Dictionary<int, J1939SlotDefinition> slots = new Dictionary<int, J1939SlotDefinition>();
            int usableSlots = 0;

            if (slotDefinitions != null)
            {
                foreach (J1939SlotDefinition slot in slotDefinitions)
                {
                    if (slot == null)
                    {
                        continue;
                    }

                    J1939SlotDefinition existing;
                    if (slots.TryGetValue(slot.SlotId, out existing) && existing.IsUsable)
                    {
                        usableSlots--;
                    }

                    slots[slot.SlotId] = slot;

                    if (slot.IsUsable)
                    {
                        usableSlots++;
                    }
                }
            }

            CatalogSnapshot replacement = new CatalogSnapshot(
                names, CopyNamedEntries(pgnNames), definitions, slots, usableSlots);

            Volatile.Write(ref _snapshot, replacement);
        }

        /// <summary>
        /// Discards every loaded definition, returning the catalogue to its
        /// shipped state. PGN lookup continues to answer for the protocol groups
        /// named by <see cref="J1939Pgn.TryDescribe"/>.
        /// </summary>
        public void Clear()
        {
            Volatile.Write(ref _snapshot, CatalogSnapshot.Empty);
        }

        /// <inheritdoc />
        public bool TryGetSpnName(uint spn, out string name)
        {
            CatalogSnapshot snapshot = Volatile.Read(ref _snapshot);

            if (snapshot.SpnNames.TryGetValue(spn, out name))
            {
                return true;
            }

            name = null;
            return false;
        }

        /// <inheritdoc />
        /// <remarks>
        /// A loaded definition wins where one exists. Otherwise the protocol
        /// names in <see cref="J1939Pgn.TryDescribe"/> answer, so transport and
        /// diagnostic groups are named even with nothing loaded - those are
        /// protocol structure rather than licensed definition data.
        /// </remarks>
        public bool TryGetPgnName(uint pgn, out string name)
        {
            CatalogSnapshot snapshot = Volatile.Read(ref _snapshot);

            if (snapshot.PgnNames.TryGetValue(pgn, out name))
            {
                return true;
            }

            return J1939Pgn.TryDescribe(pgn, out name);
        }

        /// <inheritdoc />
        public bool TryGetSpn(uint spn, out J1939SpnDefinition definition)
        {
            CatalogSnapshot snapshot = Volatile.Read(ref _snapshot);

            if (snapshot.SpnDefinitions.TryGetValue(spn, out definition))
            {
                return true;
            }

            definition = null;
            return false;
        }

        /// <summary>
        /// Attempts to resolve a SLOT by identifier.
        /// </summary>
        /// <remarks>
        /// A returned SLOT may be unusable. That is not a failure of this call -
        /// it is the answer, and <see cref="J1939SlotDefinition.UnusableReason"/>
        /// carries the detail.
        /// </remarks>
        /// <param name="slotId">SLOT identifier.</param>
        /// <param name="slot">Receives the SLOT, or null when none is held.</param>
        /// <returns>True only when a SLOT with this identifier is held.</returns>
        public bool TryGetSlot(int slotId, out J1939SlotDefinition slot)
        {
            CatalogSnapshot snapshot = Volatile.Read(ref _snapshot);

            if (snapshot.Slots.TryGetValue(slotId, out slot))
            {
                return true;
            }

            slot = null;
            return false;
        }

        /// <inheritdoc />
        public bool TryGetSlotForSpn(uint spn, out J1939SlotDefinition slot)
        {
            // One snapshot read for both lookups: two reads could straddle a
            // Load and pair an SPN from the old set with a SLOT from the new one.
            CatalogSnapshot snapshot = Volatile.Read(ref _snapshot);

            J1939SpnDefinition definition;
            if (snapshot.SpnDefinitions.TryGetValue(spn, out definition)
                && definition.HasSlot
                && snapshot.Slots.TryGetValue(definition.SlotId, out slot))
            {
                return true;
            }

            slot = null;
            return false;
        }

        /// <summary>
        /// Converts a raw J1939 code into an engineering value using the SPN's
        /// SLOT, or refuses and states why.
        /// </summary>
        /// <remarks>
        /// <para>
        /// THE FORMULA, IN FULL:
        /// </para>
        /// <code>engineeringValue = rawValue * slot.Scale + slot.Offset</code>
        /// <para>
        /// where <c>slot.Offset</c> is <c>slot.Minimum</c>. A SLOT's declared
        /// minimum is by construction the engineering value of raw code 0, which
        /// makes it the offset; the file header records how that was verified
        /// against a real SLOT table rather than assumed. <c>slot.Maximum</c>
        /// never enters this calculation.
        /// </para>
        /// <para>
        /// This method returns FALSE - and no number - when the SPN is unknown,
        /// carries no SLOT, names a SLOT that is not held, or names a SLOT that
        /// cannot be computed with. It also refuses a raw code above
        /// <see cref="J1939SlotDefinition.MaximumRawCode"/>, because those codes
        /// are J1939-71 error and "not available" indicators and scaling one
        /// produces a plausible reading that no ECU ever sent. A caller that
        /// wants the unscaled number already has it - it passed it in.
        /// </para>
        /// <para>
        /// No result is clamped. If a genuine measurement sits outside the SLOT's
        /// declared range, that is a fact about the machine and squashing it into
        /// range would hide the fault the fitter is chasing.
        /// </para>
        /// </remarks>
        /// <param name="spn">Suspect parameter number.</param>
        /// <param name="rawValue">The assembled raw code off the wire.</param>
        /// <param name="engineeringValue">Receives the scaled value on success, 0 otherwise.</param>
        /// <param name="unit">
        /// Receives the SLOT's unit text on success - empty for a dimensionless
        /// quantity - and empty on failure. Never null.
        /// </param>
        /// <param name="failureReason">Receives the reason on failure, null on success.</param>
        /// <returns>True only when the value is fully justified by a usable SLOT.</returns>
        public bool TryScale(uint spn, double rawValue, out double engineeringValue,
            out string unit, out string failureReason)
        {
            engineeringValue = 0.0;
            unit = string.Empty;

            CatalogSnapshot snapshot = Volatile.Read(ref _snapshot);

            J1939SpnDefinition definition;
            if (!snapshot.SpnDefinitions.TryGetValue(spn, out definition))
            {
                failureReason = string.Format(CultureInfo.InvariantCulture,
                    "SPN {0} has no definition in the loaded catalogue; no scaling available", spn);
                return false;
            }

            if (!definition.HasSlot)
            {
                failureReason = string.Format(CultureInfo.InvariantCulture,
                    "SPN {0} is defined but names no SLOT; no scaling available", spn);
                return false;
            }

            J1939SlotDefinition slot;
            if (!snapshot.Slots.TryGetValue(definition.SlotId, out slot))
            {
                failureReason = string.Format(CultureInfo.InvariantCulture,
                    "SPN {0} names SLOT {1}, which is not in the loaded SLOT table; no scaling available",
                    spn, definition.SlotId);
                return false;
            }

            if (!slot.IsUsable)
            {
                failureReason = string.Format(CultureInfo.InvariantCulture,
                    "SPN {0} SLOT {1} {2} cannot be used for scaling: {3}",
                    spn, slot.SlotId, slot.SlotName, slot.UnusableReason);
                return false;
            }

            if (!J1939SlotDefinition.IsFinite(rawValue))
            {
                failureReason = string.Format(CultureInfo.InvariantCulture,
                    "SPN {0}: raw value is not a finite number", spn);
                return false;
            }

            if (rawValue < 0.0)
            {
                failureReason = string.Format(CultureInfo.InvariantCulture,
                    "SPN {0}: raw code {1} is negative; J1939 raw codes are unsigned",
                    spn, rawValue.ToString("R", CultureInfo.InvariantCulture));
                return false;
            }

            double ceiling = slot.MaximumRawCode;
            if (rawValue > ceiling)
            {
                failureReason = string.Format(CultureInfo.InvariantCulture,
                    "SPN {0}: raw code {1} is above the highest data code {2} for a {3}-bit parameter; "
                    + "this encodes an error or not-available indicator, not a measurement",
                    spn, rawValue.ToString("R", CultureInfo.InvariantCulture),
                    ceiling.ToString("R", CultureInfo.InvariantCulture), slot.WidthBits);
                return false;
            }

            engineeringValue = (rawValue * slot.Scale) + slot.Offset;
            unit = slot.Unit;
            failureReason = null;
            return true;
        }

        /// <summary>
        /// One-line status for a log or a status bar, for example
        /// "J1939 catalogue: empty - no definition source loaded".
        /// </summary>
        /// <returns>The formatted status.</returns>
        public override string ToString()
        {
            CatalogSnapshot snapshot = Volatile.Read(ref _snapshot);

            if (snapshot.SpnNames.Count == 0 && snapshot.PgnNames.Count == 0
                && snapshot.SpnDefinitions.Count == 0 && snapshot.Slots.Count == 0)
            {
                return "J1939 catalogue: empty - no definition source loaded";
            }

            return string.Format(CultureInfo.InvariantCulture,
                "J1939 catalogue: {0} SPN name(s), {1} SPN definition(s), {2} PGN name(s), "
                + "{3} SLOT(s) of which {4} usable",
                snapshot.SpnNames.Count, snapshot.SpnDefinitions.Count, snapshot.PgnNames.Count,
                snapshot.Slots.Count, snapshot.UsableSlotCount);
        }

        /// <summary>
        /// Backs <see cref="SpnNameLookup"/>. Returns null for an SPN with no
        /// definition, which is the contract
        /// <see cref="J1939Dtc.Describe(Func{uint,string})"/> expects.
        /// </summary>
        private string LookupSpnName(uint spn)
        {
            string name;
            return TryGetSpnName(spn, out name) ? name : null;
        }

        /// <summary>
        /// Copies a caller's map, dropping entries with no usable name and
        /// trimming the rest.
        /// </summary>
        /// <remarks>
        /// ALWAYS returns a fresh dictionary, including for a null or empty
        /// source. Returning the shared <c>CatalogSnapshot.EmptyMap</c> here
        /// would be free, and it would also hand a mutable reference to the
        /// object that backs every empty snapshot in the process - one caller
        /// merging names into it would silently populate every other catalogue
        /// and survive <see cref="Clear"/>. The allocation happens once per load
        /// and buys that entire class of bug away.
        /// </remarks>
        private static Dictionary<uint, string> CopyNamedEntries(IDictionary<uint, string> source)
        {
            if (source == null || source.Count == 0)
            {
                return new Dictionary<uint, string>();
            }

            Dictionary<uint, string> copy = new Dictionary<uint, string>(source.Count);

            foreach (KeyValuePair<uint, string> entry in source)
            {
                if (string.IsNullOrEmpty(entry.Value))
                {
                    continue;
                }

                string trimmed = entry.Value.Trim();
                if (trimmed.Length == 0)
                {
                    continue;
                }

                copy[entry.Key] = trimmed;
            }

            return copy;
        }

        /// <summary>
        /// The whole catalogue as one immutable object, so a load is a single
        /// atomic reference swap and readers never need a lock.
        /// </summary>
        private sealed class CatalogSnapshot
        {
            /// <summary>Shared empty map. Never mutated, so sharing it is safe.</summary>
            internal static readonly Dictionary<uint, string> EmptyMap = new Dictionary<uint, string>();

            /// <summary>Shared empty SPN definition map. Never mutated.</summary>
            internal static readonly Dictionary<uint, J1939SpnDefinition> EmptySpnDefinitions =
                new Dictionary<uint, J1939SpnDefinition>();

            /// <summary>Shared empty SLOT map. Never mutated.</summary>
            internal static readonly Dictionary<int, J1939SlotDefinition> EmptySlots =
                new Dictionary<int, J1939SlotDefinition>();

            /// <summary>The shipped state: nothing loaded.</summary>
            internal static readonly CatalogSnapshot Empty =
                new CatalogSnapshot(EmptyMap, EmptyMap, EmptySpnDefinitions, EmptySlots);

            private readonly Dictionary<uint, string> _spnNames;
            private readonly Dictionary<uint, string> _pgnNames;
            private readonly Dictionary<uint, J1939SpnDefinition> _spnDefinitions;
            private readonly Dictionary<int, J1939SlotDefinition> _slots;
            private readonly int _usableSlotCount;

            internal CatalogSnapshot(Dictionary<uint, string> spnNames, Dictionary<uint, string> pgnNames,
                Dictionary<uint, J1939SpnDefinition> spnDefinitions, Dictionary<int, J1939SlotDefinition> slots)
                : this(spnNames, pgnNames, spnDefinitions, slots, CountUsable(slots))
            {
            }

            internal CatalogSnapshot(Dictionary<uint, string> spnNames, Dictionary<uint, string> pgnNames,
                Dictionary<uint, J1939SpnDefinition> spnDefinitions, Dictionary<int, J1939SlotDefinition> slots,
                int usableSlotCount)
            {
                _spnNames = spnNames ?? EmptyMap;
                _pgnNames = pgnNames ?? EmptyMap;
                _spnDefinitions = spnDefinitions ?? EmptySpnDefinitions;
                _slots = slots ?? EmptySlots;
                _usableSlotCount = usableSlotCount;
            }

            /// <summary>SPN number to parameter name. Read-only by convention after construction.</summary>
            internal Dictionary<uint, string> SpnNames
            {
                get { return _spnNames; }
            }

            /// <summary>PGN number to group name. Read-only by convention after construction.</summary>
            internal Dictionary<uint, string> PgnNames
            {
                get { return _pgnNames; }
            }

            /// <summary>SPN number to full definition. Read-only by convention after construction.</summary>
            internal Dictionary<uint, J1939SpnDefinition> SpnDefinitions
            {
                get { return _spnDefinitions; }
            }

            /// <summary>SLOT identifier to SLOT. Read-only by convention after construction.</summary>
            internal Dictionary<int, J1939SlotDefinition> Slots
            {
                get { return _slots; }
            }

            /// <summary>How many held SLOTs are usable. Counted once, at build time.</summary>
            internal int UsableSlotCount
            {
                get { return _usableSlotCount; }
            }

            private static int CountUsable(Dictionary<int, J1939SlotDefinition> slots)
            {
                if (slots == null || slots.Count == 0)
                {
                    return 0;
                }

                int n = 0;
                foreach (KeyValuePair<int, J1939SlotDefinition> entry in slots)
                {
                    if (entry.Value != null && entry.Value.IsUsable)
                    {
                        n++;
                    }
                }

                return n;
            }
        }
    }

    /// <summary>
    /// Renders a J1939 diagnostic trouble code as technician-facing text that
    /// degrades honestly when the SPN has no definition.
    /// </summary>
    /// <remarks>
    /// This is the payoff for shipping an empty catalogue. The FMI half of every
    /// code is standardised by J1939-73 Appendix A and therefore always
    /// available, so a code is always describable in terms a fitter can act on
    /// even when its SPN cannot be named. Every uncertainty in the decode -
    /// unknown SPN, unavailable occurrence count, ambiguous legacy SPN encoding
    /// - is stated in the text rather than smoothed over.
    /// </remarks>
    public static class J1939FaultText
    {
        /// <summary>
        /// Describes one diagnostic trouble code, resolving the SPN name through
        /// <paramref name="resolver"/> when one is supplied.
        /// </summary>
        /// <remarks>
        /// <para>Resolved SPN whose SLOT will scale:</para>
        /// <code>SPN 110 Engine Coolant Temperature [Temperature, degC] - FMI 0: data valid but above normal operational range - most severe level [occurrences 3]</code>
        /// <para>
        /// Resolved SPN with no SLOT, or one that will not scale - named, and
        /// silent about units rather than guessing at them:
        /// </para>
        /// <code>SPN 110 Engine Coolant Temperature - FMI 0: data valid but above normal operational range - most severe level</code>
        /// <para>Unresolved SPN - the shipped state, and still actionable:</para>
        /// <code>SPN 4364 (no definition loaded) - FMI 5: current below normal or open circuit</code>
        /// <para>Legacy ambiguous SPN encoding:</para>
        /// <code>UNCERTAIN SPN 1208 (no definition loaded) - FMI 3: voltage above normal or shorted to high source [occurrences 10] [SPN conversion method bit set - legacy encoding, byte order could not be established; read as version 3; readings v1=151, v2=38659, v3=1208]</code>
        /// <para>
        /// A resolver that throws is treated as holding no definition. A
        /// definition source is not worth crashing a diagnostics screen over,
        /// and the fault still renders with its FMI text.
        /// </para>
        /// </remarks>
        /// <param name="dtc">The decoded trouble code. Required.</param>
        /// <param name="resolver">
        /// Name source. May be null, in which case every SPN reports as having
        /// no definition loaded.
        /// </param>
        /// <returns>The formatted description.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="dtc"/> is null.</exception>
        public static string DescribeFault(J1939Dtc dtc, IJ1939SpnResolver resolver)
        {
            if (dtc == null)
            {
                throw new ArgumentNullException("dtc");
            }

            StringBuilder sb = new StringBuilder(224);

            // Leading marker so an ambiguous decode is unmissable in a grid row
            // that gets truncated. The detail goes in the suffix; this is the
            // part that survives a narrow column.
            if (dtc.ConversionUncertain)
            {
                sb.Append("UNCERTAIN ");
            }

            sb.AppendFormat(CultureInfo.InvariantCulture, "SPN {0} ", dtc.Spn);
            sb.Append(SpnNameOrHonestGap(dtc.Spn, dtc.IsProprietarySpn, resolver));
            AppendSlotAnnotation(sb, dtc.Spn, resolver);

            sb.AppendFormat(CultureInfo.InvariantCulture, " - FMI {0}: {1}",
                dtc.Fmi, J1939Diagnostics.FmiDescription(dtc.Fmi));

            AppendOccurrenceCount(sb, dtc);
            AppendConversionUncertainty(sb, dtc, resolver);

            return sb.ToString();
        }

        /// <summary>
        /// Appends the parameter's quantity and unit, but only when the resolver
        /// holds a SLOT that could actually produce a reading in them.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Renders as <c>SPN 110 Engine Coolant Temperature [Temperature, degC]</c>.
        /// This is the one place a fitter learns what the number behind the fault
        /// even measures, which is what turns "SPN 110 FMI 0" into "the coolant
        /// is reading too hot".
        /// </para>
        /// <para>
        /// NOTHING IS APPENDED unless a SLOT is present AND usable. No SLOT means
        /// silence - not a guessed unit, not an inferred one from the parameter's
        /// name. An unusable SLOT means silence too: a bit field or an
        /// unparseable width has no unit to state, and printing its quantity
        /// alone would imply a measurement the tool cannot take. A resolver that
        /// throws is treated as holding nothing, same as the name path.
        /// </para>
        /// </remarks>
        private static void AppendSlotAnnotation(StringBuilder sb, uint spn, IJ1939SpnResolver resolver)
        {
            IJ1939SpnSlotResolver slotResolver = resolver as IJ1939SpnSlotResolver;
            if (slotResolver == null)
            {
                return;
            }

            J1939SlotDefinition slot;

            try
            {
                if (!slotResolver.TryGetSlotForSpn(spn, out slot) || slot == null || !slot.IsUsable)
                {
                    return;
                }
            }
            catch (Exception)
            {
                return;
            }

            bool hasQuantity = !string.IsNullOrEmpty(slot.Quantity);
            bool hasUnit = !string.IsNullOrEmpty(slot.Unit);

            if (!hasQuantity && !hasUnit)
            {
                return;
            }

            sb.Append(" [");

            if (hasQuantity)
            {
                sb.Append(slot.Quantity);
            }

            if (hasUnit)
            {
                if (hasQuantity)
                {
                    sb.Append(", ");
                }

                sb.Append(slot.Unit);
            }

            sb.Append("]");
        }

        /// <summary>
        /// Appends the occurrence count, distinguishing "not counted" from "the
        /// module declined to report a count".
        /// </summary>
        /// <remarks>
        /// J1939-73 5.7.1.12 gives 127 the meaning "not available". Rendering
        /// that as "127 occurrences" is a fabricated reading of an intermittent
        /// fault's history, so the unavailable case is named. A count of zero is
        /// omitted entirely: it adds nothing and pushes the useful text off the
        /// end of a narrow column.
        /// </remarks>
        private static void AppendOccurrenceCount(StringBuilder sb, J1939Dtc dtc)
        {
            if (!dtc.OccurrenceCountAvailable)
            {
                sb.Append(" [occurrence count not available]");
                return;
            }

            if (dtc.OccurrenceCount > 0)
            {
                sb.AppendFormat(CultureInfo.InvariantCulture,
                    " [occurrences {0}]", dtc.OccurrenceCount);
            }
        }

        /// <summary>
        /// Appends the legacy SPN encoding caveat and every alternative reading.
        /// </summary>
        /// <remarks>
        /// <para>
        /// J1939-73 5.7.1.11: a conversion method bit of 1 means the SPN is
        /// packed in version 1, 2 or 3 format and the message gives a receiver
        /// no way to tell which. The decoder picks the best-supported reading;
        /// this states plainly that it is a reading, and lists the others so a
        /// fitter with the machine's own documentation can settle it.
        /// </para>
        /// <para>
        /// Each alternative is name-resolved too. When exactly one candidate
        /// resolves against the loaded source that is a strong hint - but only a
        /// hint, and it is presented as one rather than used to silently rewrite
        /// the headline SPN.
        /// </para>
        /// </remarks>
        private static void AppendConversionUncertainty(StringBuilder sb, J1939Dtc dtc, IJ1939SpnResolver resolver)
        {
            if (!dtc.ConversionUncertain)
            {
                return;
            }

            sb.Append(" [SPN conversion method bit set - legacy encoding, byte order could not be established; read as version ");
            sb.Append(((int)dtc.SelectedConversionVersion).ToString(CultureInfo.InvariantCulture));

            J1939SpnCandidate[] candidates = dtc.SpnCandidates;
            if (candidates != null && candidates.Length > 0)
            {
                sb.Append("; readings ");

                for (int i = 0; i < candidates.Length; i++)
                {
                    if (i > 0)
                    {
                        sb.Append(", ");
                    }

                    sb.AppendFormat(CultureInfo.InvariantCulture, "v{0}={1}",
                        (int)candidates[i].Version, candidates[i].Spn);

                    string candidateName = TryResolveSpnName(candidates[i].Spn, resolver);
                    if (!string.IsNullOrEmpty(candidateName))
                    {
                        sb.Append(" (");
                        sb.Append(candidateName);
                        sb.Append(")");
                    }
                }
            }

            sb.Append("]");
        }

        /// <summary>
        /// The SPN's name, or an explicit statement that none is held. Never a
        /// bare number dressed up as a parameter name.
        /// </summary>
        private static string SpnNameOrHonestGap(uint spn, bool isProprietarySpn, IJ1939SpnResolver resolver)
        {
            string name = TryResolveSpnName(spn, resolver);
            if (!string.IsNullOrEmpty(name))
            {
                return name;
            }

            // A proprietary-range SPN (J1939-73: 520192 and above) will never
            // appear in an SAE definition set at all, so saying so saves the
            // fitter looking for it in the standard tables.
            return isProprietarySpn
                ? "(manufacturer-specific SPN, no definition loaded)"
                : "(no definition loaded)";
        }

        /// <summary>
        /// Resolves an SPN name, treating a null or throwing resolver as holding
        /// no definition.
        /// </summary>
        private static string TryResolveSpnName(uint spn, IJ1939SpnResolver resolver)
        {
            if (resolver == null)
            {
                return null;
            }

            try
            {
                string name;
                if (resolver.TryGetSpnName(spn, out name) && !string.IsNullOrEmpty(name))
                {
                    return name;
                }
            }
            catch (Exception)
            {
                // Matches J1939FaultTracker.WrapSpnResolver: a definition source
                // that faults must not take the diagnostics screen down with it.
                // The fault still renders honestly with its standardised FMI.
            }

            return null;
        }
    }

    /// <summary>
    /// Known-answer self-test for the SPN / PGN / SLOT catalogue and the fault
    /// text renderer.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This repo has no unit-test framework and deliberately does not want one;
    /// known-answer self-tests ship in the product the way
    /// <c>J1708SelfTest</c> and <c>tps_self_test</c> do. Call
    /// <see cref="Run(out string)"/> and read the report.
    /// </para>
    /// <para>
    /// WHAT IT PROVES. The arithmetic of the transfer function including a
    /// negative offset, that every refusal path refuses AND says why, that an
    /// unusable SLOT is retained rather than dropped, that the free-text width
    /// parser reports rather than guesses, the name merge order, and that an
    /// empty catalogue still renders a fault with its standardised FMI text.
    /// </para>
    /// <para>
    /// WHAT IT CANNOT PROVE. That any particular definition SET is correct. A
    /// wrong scale in a loaded table produces a confident wrong number and this
    /// harness will happily agree with it - the fixtures here are hand-written
    /// known answers, not the shipped data. Provenance of the loaded set is a
    /// separate problem with a separate answer.
    /// </para>
    /// </remarks>
    public static class J1939SpnCatalogSelfTest
    {
        /// <summary>
        /// Runs every case and builds a plain-text report.
        /// </summary>
        /// <param name="report">Receives the report, one line per case plus a total.</param>
        /// <returns>True only when every case passed.</returns>
        public static bool Run(out string report)
        {
            StringBuilder sb = new StringBuilder(4096);
            int passed = 0;
            int total = 0;

            sb.AppendLine("J1939 SPN / PGN / SLOT catalogue self-test");
            sb.AppendLine("------------------------------------------");

            RunEmptyCatalogueCases(sb, ref passed, ref total);
            RunSlotParsingCases(sb, ref passed, ref total);
            RunUsableSlotCases(sb, ref passed, ref total);
            RunRefusalCases(sb, ref passed, ref total);
            RunMergeAndLifecycleCases(sb, ref passed, ref total);

            sb.AppendLine("------------------------------------------");
            sb.AppendFormat(CultureInfo.InvariantCulture, "{0} of {1} case(s) passed", passed, total);
            sb.AppendLine();

            report = sb.ToString();
            return passed == total;
        }

        // -----------------------------------------------------------------
        // An empty catalogue is the shipped state and must stay serviceable.
        // -----------------------------------------------------------------

        private static void RunEmptyCatalogueCases(StringBuilder sb, ref int passed, ref int total)
        {
            J1939SpnCatalog catalog = new J1939SpnCatalog();

            Check(sb, ref passed, ref total, "empty: reports nothing loaded",
                !catalog.IsLoaded && catalog.SpnCount == 0 && catalog.SpnDefinitionCount == 0
                && catalog.SlotCount == 0 && catalog.UsableSlotCount == 0,
                catalog.ToString());

            // The whole point of shipping empty: an unresolved SPN still tells a
            // fitter what failed and how.
            string text = J1939FaultText.DescribeFault(MakeDtc(4364u, 5, 0), catalog);
            Check(sb, ref passed, ref total, "empty: unresolved SPN still renders with FMI text",
                text.Contains("SPN 4364")
                && text.Contains("(no definition loaded)")
                && text.Contains("FMI 5")
                && text.IndexOf('[') < 0,
                text);

            string nullResolverText = J1939FaultText.DescribeFault(MakeDtc(4364u, 5, 0), null);
            Check(sb, ref passed, ref total, "empty: a null resolver renders the same honest text",
                nullResolverText == text, nullResolverText);

            double value;
            string unit;
            string why;
            bool scaled = catalog.TryScale(110u, 100.0, out value, out unit, out why);
            Check(sb, ref passed, ref total, "empty: TryScale refuses and names the SPN",
                !scaled && value == 0.0 && unit == string.Empty
                && why != null && why.Contains("SPN 110") && why.Contains("no definition"),
                Describe(scaled, value, unit, why));

            // PGN protocol names are not licensed definition data, so they answer
            // even with nothing loaded.
            string pgnName;
            Check(sb, ref passed, ref total, "empty: PGN lookup still falls back to protocol names",
                catalog.TryGetPgnName(J1939_PGN_ADDRESS_CLAIMED, out pgnName) && !string.IsNullOrEmpty(pgnName),
                pgnName);
        }

        // -----------------------------------------------------------------
        // Free-text width and the retain-but-refuse rule.
        // -----------------------------------------------------------------

        private static void RunSlotParsingCases(StringBuilder sb, ref int passed, ref int total)
        {
            int bits;

            Check(sb, ref passed, ref total, "width: '1 byte' is 8 bits",
                J1939SlotDefinition.TryParseWidthBits("1 byte", out bits) && bits == 8,
                bits.ToString(CultureInfo.InvariantCulture));

            Check(sb, ref passed, ref total, "width: '2 bytes' is 16 bits",
                J1939SlotDefinition.TryParseWidthBits("2 bytes", out bits) && bits == 16,
                bits.ToString(CultureInfo.InvariantCulture));

            Check(sb, ref passed, ref total, "width: '6 bits' is 6 bits",
                J1939SlotDefinition.TryParseWidthBits("6 bits", out bits) && bits == 6,
                bits.ToString(CultureInfo.InvariantCulture));

            Check(sb, ref passed, ref total, "width: '1 bit' is 1 bit",
                J1939SlotDefinition.TryParseWidthBits("1 bit", out bits) && bits == 1,
                bits.ToString(CultureInfo.InvariantCulture));

            Check(sb, ref passed, ref total, "width: 'Variable' is refused, not guessed",
                !J1939SlotDefinition.TryParseWidthBits("Variable", out bits) && bits == 0,
                bits.ToString(CultureInfo.InvariantCulture));

            Check(sb, ref passed, ref total, "width: a bare number with no unit is refused",
                !J1939SlotDefinition.TryParseWidthBits("2", out bits) && bits == 0,
                bits.ToString(CultureInfo.InvariantCulture));

            Check(sb, ref passed, ref total, "width: blank is refused",
                !J1939SlotDefinition.TryParseWidthBits(string.Empty, out bits)
                && !J1939SlotDefinition.TryParseWidthBits(null, out bits),
                bits.ToString(CultureInfo.InvariantCulture));

            // Thousands separators are real in definition sources.
            J1939SlotDefinition counter = J1939SlotDefinition.CreateFromText(
                12, "SAEct04", "Count", "1", "count", "0", "64,255", "2 bytes", "NUM");
            Check(sb, ref passed, ref total, "text: a maximum of '64,255' parses as 64255",
                counter.IsUsable && counter.Maximum == 64255.0 && counter.WidthBits == 16,
                counter.ToString());

            // An unparseable width is RETAINED so the name still displays.
            J1939SlotDefinition variable = J1939SlotDefinition.CreateFromText(
                283, "SAEmd01", "Manufacturer defined", "0", " ", "0", "255", "Variable", "IGN");
            Check(sb, ref passed, ref total, "text: an unparseable width is retained and marked unusable",
                !variable.IsUsable && variable.SlotName == "SAEmd01"
                && !string.IsNullOrEmpty(variable.UnusableReason),
                variable.ToString());

            J1939SlotDefinition unparseableScale = J1939SlotDefinition.CreateFromText(
                900, "TESTbad", "Pressure", "not a number", "kPa", "0", "1000", "1 byte", "NUM");
            Check(sb, ref passed, ref total, "text: an unparseable scale is retained and marked unusable",
                !unparseableScale.IsUsable && unparseableScale.SlotName == "TESTbad"
                && unparseableScale.UnusableReason.Contains("scale"),
                unparseableScale.UnusableReason);

            // BIN rows carry a state COUNT in the scale column - using it as a
            // multiplier would produce confident nonsense.
            J1939SlotDefinition bitField = J1939SlotDefinition.CreateFromText(
                87, "SAEbs02", "Bit Field", "4", "states", "0", "3", "2 bits", "BIN");
            Check(sb, ref passed, ref total, "text: a BIN slot is retained and refuses to scale",
                !bitField.IsUsable && bitField.Kind == J1939SlotKind.BitField
                && bitField.UnusableReason.Contains("bit field"),
                bitField.UnusableReason);

            J1939SlotDefinition ascii = J1939SlotDefinition.CreateFromText(
                94, "SAEatf0001", "ASCII, text (fixed length)", "1", "ASCII", "0", "255", "1 byte", "ASCII");
            Check(sb, ref passed, ref total, "text: an ASCII slot refuses to scale",
                !ascii.IsUsable && ascii.Kind == J1939SlotKind.Ascii, ascii.UnusableReason);

            // The sentinel a real source uses for "this SPN has no real SLOT".
            J1939SlotDefinition sentinel = J1939SlotDefinition.CreateFromText(
                202, "Diagnostic", string.Empty, "0", string.Empty, "0", "0", "0 bytes", "IGN");
            Check(sb, ref passed, ref total, "text: the zero-width IGN sentinel refuses to scale",
                !sentinel.IsUsable && sentinel.SlotId == 202, sentinel.UnusableReason);

            // NUM with a zero scale would collapse every code onto the offset.
            J1939SlotDefinition zeroScale = J1939SlotDefinition.CreateFromText(
                215, "SAEfm00", "FMI", "0", "Binary", "0", "31", "5 bits", "NUM");
            Check(sb, ref passed, ref total, "text: a NUM slot with zero scale refuses to scale",
                !zeroScale.IsUsable && zeroScale.UnusableReason.Contains("zero"),
                zeroScale.UnusableReason);

            Check(sb, ref passed, ref total, "text: an unknown kind token maps to Unspecified and refuses",
                !J1939SlotDefinition.CreateFromText(901, "TESTkind", "Pressure", "1", "kPa", "0", "250",
                    "1 byte", "WAT").IsUsable,
                "unknown kind refused");
        }

        // -----------------------------------------------------------------
        // The happy path, and the arithmetic that has to be exactly right.
        // -----------------------------------------------------------------

        private static void RunUsableSlotCases(StringBuilder sb, ref int passed, ref int total)
        {
            J1939SpnCatalog catalog = BuildFixtureCatalog();

            Check(sb, ref passed, ref total, "load: counts distinguish names, definitions and usable slots",
                catalog.IsLoaded && catalog.SpnCount == 5 && catalog.SpnDefinitionCount == 4
                && catalog.SlotCount == 3 && catalog.UsableSlotCount == 1,
                catalog.ToString());

            J1939SpnDefinition definition;
            Check(sb, ref passed, ref total, "resolve: TryGetSpn returns the full record",
                catalog.TryGetSpn(SPN_COOLANT_TEMPERATURE, out definition) && definition != null
                && definition.HasPgn && definition.Pgn == 65262u
                && definition.StartPosition == "1"
                && definition.HasSlot && definition.SlotId == SLOT_TEMPERATURE
                && definition.Length == 8,
                definition == null ? "not found" : definition.ToString());

            J1939SlotDefinition slot;
            Check(sb, ref passed, ref total, "resolve: TryGetSlot returns the SLOT by id",
                catalog.TryGetSlot(SLOT_TEMPERATURE, out slot) && slot != null && slot.IsUsable
                && slot.Quantity == "Temperature" && slot.Unit == "degC" && slot.WidthBits == 8,
                slot == null ? "not found" : slot.ToString());

            Check(sb, ref passed, ref total, "resolve: TryGetSlotForSpn hops SPN to SLOT",
                catalog.TryGetSlotForSpn(SPN_COOLANT_TEMPERATURE, out slot)
                && slot != null && slot.SlotId == SLOT_TEMPERATURE,
                slot == null ? "not found" : slot.ToString());

            Check(sb, ref passed, ref total, "resolve: offset is the SLOT minimum",
                slot != null && slot.Offset == -40.0 && slot.Minimum == -40.0,
                slot == null ? "not found" : slot.Offset.ToString("R", CultureInfo.InvariantCulture));

            double value;
            string unit;
            string why;

            // raw 100 * scale 1 + offset -40 = 60 degC.
            bool ok = catalog.TryScale(SPN_COOLANT_TEMPERATURE, 100.0, out value, out unit, out why);
            Check(sb, ref passed, ref total, "scale: raw 100 on a -40 offset gives 60 degC",
                ok && value == 60.0 && unit == "degC" && why == null,
                Describe(ok, value, unit, why));

            // The offset has to apply at the bottom of the range too.
            ok = catalog.TryScale(SPN_COOLANT_TEMPERATURE, 0.0, out value, out unit, out why);
            Check(sb, ref passed, ref total, "scale: raw 0 gives the SLOT minimum, not zero",
                ok && value == -40.0, Describe(ok, value, unit, why));

            // Top of the J1939 data range for a one-byte parameter.
            ok = catalog.TryScale(SPN_COOLANT_TEMPERATURE, 250.0, out value, out unit, out why);
            Check(sb, ref passed, ref total, "scale: raw 250 is data and gives 210 degC",
                ok && value == 210.0, Describe(ok, value, unit, why));

            // A fractional scale must not be quietly rounded.
            J1939SpnCatalog fine = new J1939SpnCatalog();
            fine.Load(
                new J1939SpnDefinition[]
                {
                    new J1939SpnDefinition(SPN_FINE_TEMPERATURE, "Fine Temperature", 65262, "2-3", SLOT_FINE_TEMPERATURE, 16)
                },
                new J1939SlotDefinition[]
                {
                    new J1939SlotDefinition(SLOT_FINE_TEMPERATURE, "SAEtp02", "Temperature", 0.03125,
                        "degC", -273.0, 1734.96875, 16, J1939SlotKind.Numeric)
                },
                null, null);

            ok = fine.TryScale(SPN_FINE_TEMPERATURE, 64255.0, out value, out unit, out why);
            Check(sb, ref passed, ref total, "scale: a 1/32 scale over two bytes reaches the declared maximum",
                ok && value == 1734.96875, Describe(ok, value, unit, why));

            ok = fine.TryScale(SPN_FINE_TEMPERATURE, 8736.0, out value, out unit, out why);
            Check(sb, ref passed, ref total, "scale: a 1/32 scale gives 0 degC at raw 8736",
                ok && value == 0.0, Describe(ok, value, unit, why));

            // A blank unit is a legitimate dimensionless quantity, not a failure.
            J1939SpnCatalog ratio = new J1939SpnCatalog();
            ratio.Load(
                new J1939SpnDefinition[] { new J1939SpnDefinition(SPN_RATIO, "Test Ratio", 65262, "4", SLOT_RATIO, 8) },
                new J1939SlotDefinition[]
                {
                    new J1939SlotDefinition(SLOT_RATIO, "SAEr03", "Ratio", 1.0, string.Empty, 0.0, 250.0, 8, J1939SlotKind.Numeric)
                },
                null, null);

            ok = ratio.TryScale(SPN_RATIO, 7.0, out value, out unit, out why);
            Check(sb, ref passed, ref total, "scale: a dimensionless SLOT succeeds with an empty unit",
                ok && value == 7.0 && unit == string.Empty, Describe(ok, value, unit, why));

            // The renderer's payoff line.
            string text = J1939FaultText.DescribeFault(MakeDtc(SPN_COOLANT_TEMPERATURE, 0, 3), catalog);
            Check(sb, ref passed, ref total, "render: a usable SLOT contributes quantity and unit",
                text.Contains("SPN 110 Engine Coolant Temperature [Temperature, degC]")
                && text.Contains("FMI 0")
                && text.Contains("[occurrences 3]"),
                text);

            string dimensionless = J1939FaultText.DescribeFault(MakeDtc(SPN_RATIO, 1, 0), ratio);
            Check(sb, ref passed, ref total, "render: a dimensionless SLOT shows its quantity only",
                dimensionless.Contains("Test Ratio [Ratio]"), dimensionless);
        }

        // -----------------------------------------------------------------
        // Every way TryScale can refuse, and the text that goes with it.
        // -----------------------------------------------------------------

        private static void RunRefusalCases(StringBuilder sb, ref int passed, ref int total)
        {
            J1939SpnCatalog catalog = BuildFixtureCatalog();

            double value;
            string unit;
            string why;

            bool ok = catalog.TryScale(SPN_UNKNOWN, 42.0, out value, out unit, out why);
            Check(sb, ref passed, ref total, "refuse: an unknown SPN gives no number and names itself",
                !ok && value == 0.0 && why != null && why.Contains("SPN 4364")
                && why.Contains("no definition"),
                Describe(ok, value, unit, why));

            ok = catalog.TryScale(SPN_NAME_ONLY, 42.0, out value, out unit, out why);
            Check(sb, ref passed, ref total, "refuse: an SPN known only by name still has no definition to scale with",
                !ok && why != null && why.Contains("no definition"),
                Describe(ok, value, unit, why));

            ok = catalog.TryScale(SPN_NO_SLOT, 42.0, out value, out unit, out why);
            Check(sb, ref passed, ref total, "refuse: a defined SPN with no SLOT says so",
                !ok && why != null && why.Contains("names no SLOT"),
                Describe(ok, value, unit, why));

            ok = catalog.TryScale(SPN_DANGLING_SLOT, 42.0, out value, out unit, out why);
            Check(sb, ref passed, ref total, "refuse: a SLOT id absent from the table says which id is missing",
                !ok && why != null && why.Contains("SLOT 4242") && why.Contains("not in the loaded SLOT table"),
                Describe(ok, value, unit, why));

            ok = catalog.TryScale(SPN_BIT_FIELD, 3.0, out value, out unit, out why);
            Check(sb, ref passed, ref total, "refuse: an UNUSABLE SLOT refuses and repeats the reason",
                !ok && why != null && why.Contains("SLOT 87") && why.Contains("bit field"),
                Describe(ok, value, unit, why));

            // 0xFF on a one-byte parameter is "not available", not 215 degC.
            ok = catalog.TryScale(SPN_COOLANT_TEMPERATURE, 255.0, out value, out unit, out why);
            Check(sb, ref passed, ref total, "refuse: raw 255 on a one-byte SLOT is not-available, not a reading",
                !ok && value == 0.0 && why != null && why.Contains("not-available"),
                Describe(ok, value, unit, why));

            ok = catalog.TryScale(SPN_COOLANT_TEMPERATURE, 251.0, out value, out unit, out why);
            Check(sb, ref passed, ref total, "refuse: raw 251 is the first reserved code and is refused",
                !ok, Describe(ok, value, unit, why));

            ok = catalog.TryScale(SPN_COOLANT_TEMPERATURE, -1.0, out value, out unit, out why);
            Check(sb, ref passed, ref total, "refuse: a negative raw code is refused",
                !ok && why != null && why.Contains("unsigned"),
                Describe(ok, value, unit, why));

            ok = catalog.TryScale(SPN_COOLANT_TEMPERATURE, double.NaN, out value, out unit, out why);
            Check(sb, ref passed, ref total, "refuse: a non-finite raw code is refused",
                !ok && why != null && why.Contains("finite"),
                Describe(ok, value, unit, why));

            ok = catalog.TryScale(SPN_COOLANT_TEMPERATURE, double.PositiveInfinity, out value, out unit, out why);
            Check(sb, ref passed, ref total, "refuse: an infinite raw code is refused",
                !ok, Describe(ok, value, unit, why));

            // A SLOT that declares the full encodable range as data must accept
            // it - the widening rule.
            J1939SpnCatalog fullRange = new J1939SpnCatalog();
            fullRange.Load(
                new J1939SpnDefinition[] { new J1939SpnDefinition(SPN_FULL_RANGE, "Full Range Count", 65262, "1", SLOT_FULL_RANGE, 8) },
                new J1939SlotDefinition[]
                {
                    new J1939SlotDefinition(SLOT_FULL_RANGE, "SAEct03", "Count", 1.0, "count", 0.0, 255.0, 8, J1939SlotKind.Numeric)
                },
                null, null);

            ok = fullRange.TryScale(SPN_FULL_RANGE, 255.0, out value, out unit, out why);
            Check(sb, ref passed, ref total, "refuse: a SLOT declaring 255 as data accepts raw 255",
                ok && value == 255.0, Describe(ok, value, unit, why));

            // Every refusal must carry a reason - a silent false is unusable.
            Check(sb, ref passed, ref total, "refuse: every failure path supplied a reason",
                AllRefusalsExplained(catalog), "checked every refusal path");

            // An unusable SLOT must not leak units into the fault line.
            string text = J1939FaultText.DescribeFault(MakeDtc(SPN_BIT_FIELD, 2, 0), catalog);
            Check(sb, ref passed, ref total, "render: an unusable SLOT contributes no units",
                text.Contains("SPN 520 Test Bit Field")
                && text.IndexOf('[') < 0
                && text.Contains("FMI 2"),
                text);

            string noSlotText = J1939FaultText.DescribeFault(MakeDtc(SPN_NO_SLOT, 4, 0), catalog);
            Check(sb, ref passed, ref total, "render: an SPN with no SLOT is named and says nothing about units",
                noSlotText.Contains("SPN 190 Engine Speed")
                && noSlotText.IndexOf('[') < 0,
                noSlotText);

            string unknownText = J1939FaultText.DescribeFault(MakeDtc(SPN_UNKNOWN, 5, 0), catalog);
            Check(sb, ref passed, ref total, "render: an unknown SPN in a loaded catalogue still renders honestly",
                unknownText.Contains("SPN 4364")
                && unknownText.Contains("no definition loaded")
                && unknownText.Contains("FMI 5")
                && unknownText.IndexOf('[') < 0,
                unknownText);
        }

        // -----------------------------------------------------------------
        // Merge order, back-compatibility and lifecycle.
        // -----------------------------------------------------------------

        private static void RunMergeAndLifecycleCases(StringBuilder sb, ref int passed, ref int total)
        {
            // The pre-existing name-only overload must keep behaving exactly as
            // it did - J1939FaultTracker and the heavy UI both depend on it.
            J1939SpnCatalog legacy = new J1939SpnCatalog();
            Dictionary<uint, string> spnNames = new Dictionary<uint, string>();
            spnNames[110u] = "  Engine Coolant Temperature  ";
            spnNames[111u] = "   ";
            Dictionary<uint, string> pgnNames = new Dictionary<uint, string>();
            pgnNames[65262u] = "Engine Temperature 1";

            legacy.Load(spnNames, pgnNames);

            string name;
            Check(sb, ref passed, ref total, "compat: the name-only overload still loads, trims and drops blanks",
                legacy.IsLoaded && legacy.SpnCount == 1 && legacy.PgnCount == 1
                && legacy.TryGetSpnName(110u, out name) && name == "Engine Coolant Temperature"
                && !legacy.TryGetSpnName(111u, out name),
                legacy.ToString());

            Check(sb, ref passed, ref total, "compat: SpnNameLookup is the same delegate and reads the live snapshot",
                object.ReferenceEquals(legacy.SpnNameLookup, legacy.SpnNameLookup)
                && legacy.SpnNameLookup(110u) == "Engine Coolant Temperature"
                && legacy.SpnNameLookup(999u) == null,
                "delegate stable");

            Check(sb, ref passed, ref total, "compat: a name-only load leaves no definitions and no slots",
                legacy.SpnDefinitionCount == 0 && legacy.SlotCount == 0,
                legacy.ToString());

            // The definition record is the more specific of the two sources, so
            // its name wins where both name the same SPN.
            J1939SpnCatalog merged = new J1939SpnCatalog();
            Dictionary<uint, string> superset = new Dictionary<uint, string>();
            superset[110u] = "Coolant Temperature From The Names-Only Superset";
            superset[8249u] = "Hydraulic Pump 1 Case Drain Flow Rate";

            merged.Load(
                new J1939SpnDefinition[]
                {
                    new J1939SpnDefinition(110u, "Engine Coolant Temperature", 65262, "1", SLOT_TEMPERATURE, 8)
                },
                new J1939SlotDefinition[] { MakeTemperatureSlot() },
                superset,
                null);

            Check(sb, ref passed, ref total, "merge: the definition's name beats the names-only superset",
                merged.TryGetSpnName(110u, out name) && name == "Engine Coolant Temperature",
                name);

            J1939SpnDefinition supersetOnly;
            Check(sb, ref passed, ref total, "merge: a superset name with no definition resolves by name only",
                merged.TryGetSpnName(8249u, out name)
                && name == "Hydraulic Pump 1 Case Drain Flow Rate"
                && !merged.TryGetSpn(8249u, out supersetOnly)
                && supersetOnly == null,
                name);

            Check(sb, ref passed, ref total, "merge: name count exceeds definition count, as a real load does",
                merged.SpnCount == 2 && merged.SpnDefinitionCount == 1,
                merged.ToString());

            // A record with nothing in it resolves nothing and must not inflate
            // the loaded count.
            J1939SpnCatalog sparse = new J1939SpnCatalog();
            sparse.Load(
                new J1939SpnDefinition[]
                {
                    null,
                    new J1939SpnDefinition(7000u, string.Empty),
                    new J1939SpnDefinition(7001u, "Real Parameter")
                },
                new J1939SlotDefinition[] { null },
                null, null);

            Check(sb, ref passed, ref total, "load: null and empty records are dropped, real ones kept",
                sparse.SpnDefinitionCount == 1 && sparse.SlotCount == 0
                && sparse.TryGetSpnName(7001u, out name),
                sparse.ToString());

            // Load REPLACES. A stale SLOT surviving a reload would scale new SPNs
            // with old numbers.
            J1939SpnCatalog reloaded = BuildFixtureCatalog();
            reloaded.Load(new J1939SpnDefinition[] { new J1939SpnDefinition(1u, "Only Me") }, null, null, null);

            double value;
            string unit;
            string why;
            Check(sb, ref passed, ref total, "load: a reload replaces rather than merges",
                reloaded.SpnDefinitionCount == 1 && reloaded.SlotCount == 0
                && !reloaded.TryScale(SPN_COOLANT_TEMPERATURE, 100.0, out value, out unit, out why)
                && why != null && why.Contains("no definition"),
                reloaded.ToString());

            reloaded.Clear();
            Check(sb, ref passed, ref total, "clear: everything goes back to the shipped empty state",
                !reloaded.IsLoaded && reloaded.SpnCount == 0 && reloaded.SpnDefinitionCount == 0
                && reloaded.SlotCount == 0 && reloaded.UsableSlotCount == 0,
                reloaded.ToString());

            // Regression pin. An earlier revision handed the shared empty map to
            // the merge path, so loading one catalogue with a null names map
            // populated every other catalogue in the process and survived Clear.
            J1939SpnCatalog isolated = new J1939SpnCatalog();
            isolated.Load(new J1939SpnDefinition[] { new J1939SpnDefinition(2u, "Contaminant") }, null, null, null);

            J1939SpnCatalog untouched = new J1939SpnCatalog();
            Check(sb, ref passed, ref total, "isolation: loading one catalogue leaves a fresh one empty",
                !untouched.IsLoaded && untouched.SpnCount == 0 && untouched.PgnCount == 0,
                untouched.ToString());

            // A duplicate SLOT id must not double-count the usable tally.
            J1939SpnCatalog duplicates = new J1939SpnCatalog();
            duplicates.Load(null,
                new J1939SlotDefinition[]
                {
                    MakeTemperatureSlot(),
                    MakeTemperatureSlot(),
                    J1939SlotDefinition.CreateFromText(SLOT_TEMPERATURE, "SAEtp01", "Temperature",
                        "1", "degC", "-40", "210", "Variable", "NUM")
                },
                null, null);

            Check(sb, ref passed, ref total, "load: a duplicate SLOT id keeps the last and counts it once",
                duplicates.SlotCount == 1 && duplicates.UsableSlotCount == 0,
                duplicates.ToString());
        }

        // -----------------------------------------------------------------
        // Fixtures. Hand-written known answers, NOT the shipped data set.
        // -----------------------------------------------------------------

        private const uint SPN_COOLANT_TEMPERATURE = 110u;
        private const uint SPN_NO_SLOT = 190u;
        private const uint SPN_DANGLING_SLOT = 100u;
        private const uint SPN_BIT_FIELD = 520u;
        private const uint SPN_NAME_ONLY = 8249u;

        /// <summary>Not in any fixture, and inside the 19-bit encodable range so a DTC can carry it.</summary>
        private const uint SPN_UNKNOWN = 4364u;

        private const uint SPN_FINE_TEMPERATURE = 175u;
        private const uint SPN_RATIO = 4000u;
        private const uint SPN_FULL_RANGE = 4001u;

        private const int SLOT_TEMPERATURE = 67;
        private const int SLOT_BIT_FIELD = 87;
        private const int SLOT_FINE_TEMPERATURE = 68;
        private const int SLOT_RATIO = 56;
        private const int SLOT_FULL_RANGE = 133;
        private const int SLOT_MISSING = 4242;

        private const uint J1939_PGN_ADDRESS_CLAIMED = 60928u;

        private static J1939SlotDefinition MakeTemperatureSlot()
        {
            return new J1939SlotDefinition(SLOT_TEMPERATURE, "SAEtp01", "Temperature",
                1.0, "degC", -40.0, 210.0, 8, J1939SlotKind.Numeric);
        }

        private static J1939SpnCatalog BuildFixtureCatalog()
        {
            J1939SpnCatalog catalog = new J1939SpnCatalog();

            Dictionary<uint, string> supersetNames = new Dictionary<uint, string>();
            supersetNames[SPN_NAME_ONLY] = "Hydraulic Pump 1 Case Drain Flow Rate";

            catalog.Load(
                new J1939SpnDefinition[]
                {
                    new J1939SpnDefinition(SPN_COOLANT_TEMPERATURE, "Engine Coolant Temperature",
                        65262, "1", SLOT_TEMPERATURE, 8),
                    new J1939SpnDefinition(SPN_NO_SLOT, "Engine Speed", 61444, "4-5", -1, 0),
                    new J1939SpnDefinition(SPN_DANGLING_SLOT, "Engine Oil Pressure", 65263, "4", SLOT_MISSING, 8),
                    new J1939SpnDefinition(SPN_BIT_FIELD, "Test Bit Field", 65263, "1.1", SLOT_BIT_FIELD, 2)
                },
                new J1939SlotDefinition[]
                {
                    MakeTemperatureSlot(),
                    J1939SlotDefinition.CreateFromText(SLOT_BIT_FIELD, "SAEbs02", "Bit Field",
                        "4", "states", "0", "3", "2 bits", "BIN"),
                    J1939SlotDefinition.CreateFromText(202, "Diagnostic", string.Empty,
                        "0", string.Empty, "0", "0", "0 bytes", "IGN")
                },
                supersetNames,
                null);

            return catalog;
        }

        /// <summary>
        /// Builds a version-4 DTC through the real decoder, so the fixtures
        /// exercise the shipped byte layout rather than a hand-built object.
        /// </summary>
        private static J1939Dtc MakeDtc(uint spn, byte fmi, byte occurrenceCount)
        {
            byte[] bytes = new byte[4];
            bytes[0] = (byte)(spn & 0xFFu);
            bytes[1] = (byte)((spn >> 8) & 0xFFu);
            bytes[2] = (byte)((((spn >> 16) & 0x07u) << 5) | (fmi & 0x1Fu));
            bytes[3] = (byte)(occurrenceCount & 0x7F);

            return J1939Diagnostics.DecodeDtc(bytes);
        }

        /// <summary>
        /// Sweeps every refusal path and asserts none of them returns false with
        /// a null or blank reason.
        /// </summary>
        private static bool AllRefusalsExplained(J1939SpnCatalog catalog)
        {
            uint[] spns = new uint[]
            {
                SPN_UNKNOWN, SPN_NAME_ONLY, SPN_NO_SLOT, SPN_DANGLING_SLOT,
                SPN_BIT_FIELD, SPN_COOLANT_TEMPERATURE
            };

            double[] raws = new double[] { -1.0, 0.0, 255.0, double.NaN, double.PositiveInfinity };

            for (int i = 0; i < spns.Length; i++)
            {
                for (int j = 0; j < raws.Length; j++)
                {
                    double value;
                    string unit;
                    string why;

                    if (!catalog.TryScale(spns[i], raws[j], out value, out unit, out why))
                    {
                        if (string.IsNullOrEmpty(why) || value != 0.0 || unit != string.Empty)
                        {
                            return false;
                        }
                    }
                    else if (why != null)
                    {
                        return false;
                    }
                }
            }

            return true;
        }

        private static string Describe(bool ok, double value, string unit, string why)
        {
            if (ok)
            {
                return string.Format(CultureInfo.InvariantCulture, "true, {0} {1}",
                    value.ToString("R", CultureInfo.InvariantCulture), unit);
            }

            return "false, " + (why == null ? "NO REASON GIVEN" : why);
        }

        private static void Check(StringBuilder sb, ref int passed, ref int total,
            string name, bool condition, string detail)
        {
            total++;

            if (condition)
            {
                passed++;
                sb.AppendFormat(CultureInfo.InvariantCulture, "  PASS  {0}", name);
            }
            else
            {
                sb.AppendFormat(CultureInfo.InvariantCulture, "  FAIL  {0}", name);
            }

            sb.AppendLine();

            if (!string.IsNullOrEmpty(detail))
            {
                sb.AppendFormat(CultureInfo.InvariantCulture, "          {0}", detail);
                sb.AppendLine();
            }
        }
    }
}
