// =============================================================================
//  J1939Diagnostics.cs - SAE J1939-73 diagnostic messages: DM1, DM2, DM3, DM11,
//  DM19, and the PGN 59904 Request that carries them.
//
//  SOURCES
//  -------
//  Every wire layout, encoding value and description string below is taken from
//  SAE J1939-73 Revised SEP2006. Nothing here is inferred except where a comment says so explicitly and
//  the result carries an uncertainty flag. Section references are given inline.
//
//  THE SPN CONVERSION TRAP (5.7.1.11)
//  ----------------------------------
//  A DTC's 19-bit SPN is packed FOUR different ways. Byte 4 bit 8 is the
//  Conversion Method flag:
//
//     CM = 0  ->  version 4 - the current layout, unambiguous.
//     CM = 1  ->  version 1, 2 OR 3 - and the message does not say which.
//
//  A parser that ignores CM reports plausible but entirely wrong SPNs on every
//  CM=1 module. That is confident nonsense: the technician is sent to the wrong
//  circuit with no visible failure. So DecodeDtc branches on CM, decodes ALL
//  THREE legacy candidates, picks the best-supported one, and sets
//  ConversionUncertain so the UI can mark it. It never presents a CM=1 SPN as
//  authoritative.
//
//  HONEST DEGRADATION
//  ------------------
//  FMI is standardised across every manufacturer, so a fault is describable even
//  when the SPN has no definition entry. Describe() says "(no definition loaded)"
//  rather than rendering a bare number as though it were a known parameter.
//
//  LAMPS FIRST
//  -----------
//  Technicians read lamp state before they read codes, so
//  J1939LampStatus is the first member of every report, not a footnote.
//
//  DESTRUCTIVE OPERATIONS
//  ----------------------
//  DM3 and DM11 erase fault history and reset readiness monitors. They are
//  IRREVERSIBLE. The builders here return J1939ClearRequest, which cannot yield
//  a transmittable frame without an explicit operator-confirmation argument.
//  Nothing in this file transmits, opens a file, or touches the network.
//
//  ASCII only, no interpolation, explicit property bodies - matches the rest of
//  the J1939 stack.
// =============================================================================

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;

namespace HeavyDiesel.J1939
{
    /// <summary>
    /// Two-bit lamp command, J1939-73 5.7.1.1 - 5.7.1.4 (SPN 1213 MIL, 623 red
    /// stop, 624 amber warning, 987 protect).
    /// </summary>
    public enum J1939LampCommand
    {
        /// <summary>00 - lamp off.</summary>
        Off = 0,

        /// <summary>01 - lamp on.</summary>
        On = 1,

        /// <summary>10 - short MI for WWH-OBD on the MIL; SAE reserved on the other three lamps.</summary>
        ShortMilOrReserved = 2,

        /// <summary>11 - not available / don't care. This is NOT "off": the ECU is declining to command the lamp.</summary>
        NotAvailable = 3
    }

    /// <summary>
    /// Two-bit lamp flash command, J1939-73 5.7.1.5 - 5.7.1.8 (SPN 3038 flash
    /// MIL, 3039 flash red stop, 3040 flash amber warning, 3041 flash protect).
    /// </summary>
    public enum J1939LampFlash
    {
        /// <summary>00 - slow flash, 1 Hz at 50 percent duty cycle.</summary>
        SlowFlash = 0,

        /// <summary>01 - fast flash, 2 Hz or faster at 50 percent duty cycle.</summary>
        FastFlash = 1,

        /// <summary>10 - class C DTC on the MIL; SAE reserved on the other three lamps.</summary>
        ClassCOrReserved = 2,

        /// <summary>11 - unavailable / do not flash. The normal value for a steady lamp.</summary>
        DoNotFlash = 3
    }

    /// <summary>
    /// Relative urgency of a lamp report, ordered so a UI can sort or colour by it.
    /// </summary>
    /// <remarks>
    /// The ordering is taken from the lamp definitions themselves in J1939-73
    /// 5.7.1.1 - 5.7.1.4, not invented: red stop means the condition "warrants
    /// stopping the vehicle", amber warning means the vehicle "need not be
    /// immediately stopped", protect reports a non-electronic system problem, and
    /// the MIL is emissions-only.
    /// </remarks>
    public enum J1939LampSeverity
    {
        /// <summary>No lamp is commanded on.</summary>
        None = 0,

        /// <summary>Malfunction indicator only - an emissions-related fault.</summary>
        Emissions = 1,

        /// <summary>Protect lamp - a system condition, usually not an electronic fault.</summary>
        Protect = 2,

        /// <summary>Amber warning - a fault that does not require stopping immediately.</summary>
        Warning = 3,

        /// <summary>Red stop - the transmitting ECU is asking for the vehicle to be stopped.</summary>
        StopNow = 4
    }

    /// <summary>Which DTC list a <see cref="J1939DtcReport"/> holds.</summary>
    public enum J1939DtcReportKind
    {
        /// <summary>DM1, PGN 65226 - currently active DTCs.</summary>
        ActiveDm1 = 0,

        /// <summary>DM2, PGN 65227 - previously active DTCs.</summary>
        PreviouslyActiveDm2 = 1
    }

    /// <summary>
    /// One of the four SPN packing layouts defined by J1939-73 5.7.1.11.
    /// </summary>
    public enum J1939SpnConversionVersion
    {
        /// <summary>Version 1: SPN sent most significant bit first; the three LEAST significant SPN bits sit in byte 5 bits 8-6.</summary>
        Version1 = 1,

        /// <summary>Version 2: as version 1 but with the two SPN bytes swapped; the three LEAST significant SPN bits sit in byte 5 bits 8-6.</summary>
        Version2 = 2,

        /// <summary>Version 3: Intel order for all 19 bits. Byte-identical to version 4 - only the CM bit differs.</summary>
        Version3 = 3,

        /// <summary>Version 4: Intel order for all 19 bits with CM cleared to 0. The current, unambiguous layout.</summary>
        Version4 = 4
    }

    /// <summary>
    /// One candidate reading of a CM=1 SPN. J1939-73 5.7.1.11 states that a CM=1
    /// DTC is in version 1, 2 or 3 format and that the message itself does not
    /// say which, so a receiver can only enumerate the possibilities.
    /// </summary>
    public struct J1939SpnCandidate : IEquatable<J1939SpnCandidate>
    {
        private readonly J1939SpnConversionVersion _version;
        private readonly uint _spn;

        /// <summary>Creates a candidate reading.</summary>
        /// <param name="version">The layout this reading assumes.</param>
        /// <param name="spn">The SPN that layout produces.</param>
        public J1939SpnCandidate(J1939SpnConversionVersion version, uint spn)
        {
            _version = version;
            _spn = spn;
        }

        /// <summary>The layout this reading assumes.</summary>
        public J1939SpnConversionVersion Version
        {
            get { return _version; }
        }

        /// <summary>The SPN this layout produces from the same four bytes.</summary>
        public uint Spn
        {
            get { return _spn; }
        }

        /// <summary>Value equality over version and SPN.</summary>
        /// <param name="other">Candidate to compare with.</param>
        /// <returns>True when both fields match.</returns>
        public bool Equals(J1939SpnCandidate other)
        {
            return _version == other._version && _spn == other._spn;
        }

        /// <inheritdoc />
        public override bool Equals(object obj)
        {
            return obj is J1939SpnCandidate && Equals((J1939SpnCandidate)obj);
        }

        /// <inheritdoc />
        public override int GetHashCode()
        {
            return ((int)_version * 397) ^ (int)_spn;
        }

        /// <summary>Equality operator.</summary>
        /// <param name="left">Left operand.</param>
        /// <param name="right">Right operand.</param>
        /// <returns>True when both fields match.</returns>
        public static bool operator ==(J1939SpnCandidate left, J1939SpnCandidate right)
        {
            return left.Equals(right);
        }

        /// <summary>Inequality operator.</summary>
        /// <param name="left">Left operand.</param>
        /// <param name="right">Right operand.</param>
        /// <returns>True when the fields differ.</returns>
        public static bool operator !=(J1939SpnCandidate left, J1939SpnCandidate right)
        {
            return !left.Equals(right);
        }

        /// <summary>Short form, for example "v1=1208".</summary>
        /// <returns>The formatted candidate.</returns>
        public override string ToString()
        {
            return string.Format(CultureInfo.InvariantCulture, "v{0}={1}", (int)_version, _spn);
        }
    }

    /// <summary>
    /// One of the four J1939-73 diagnostic lamps: its commanded state and its
    /// flash command, decoded together because Table 5 makes the pair meaningful
    /// only in combination.
    /// </summary>
    public sealed class J1939Lamp
    {
        private readonly string _name;
        private readonly bool _isMil;
        private readonly J1939LampCommand _command;
        private readonly J1939LampFlash _flash;

        /// <summary>Creates a decoded lamp.</summary>
        /// <param name="name">Display name, for example "Red Stop".</param>
        /// <param name="isMil">True for the malfunction indicator lamp, whose 10 encodings differ from the other three.</param>
        /// <param name="command">Two-bit lamp command.</param>
        /// <param name="flash">Two-bit flash command.</param>
        public J1939Lamp(string name, bool isMil, J1939LampCommand command, J1939LampFlash flash)
        {
            _name = name ?? string.Empty;
            _isMil = isMil;
            _command = command;
            _flash = flash;
        }

        /// <summary>Display name. Never null.</summary>
        public string Name
        {
            get { return _name; }
        }

        /// <summary>True when this is the malfunction indicator lamp.</summary>
        public bool IsMalfunctionIndicator
        {
            get { return _isMil; }
        }

        /// <summary>Two-bit lamp command as transmitted.</summary>
        public J1939LampCommand Command
        {
            get { return _command; }
        }

        /// <summary>Two-bit flash command as transmitted.</summary>
        public J1939LampFlash Flash
        {
            get { return _flash; }
        }

        /// <summary>
        /// True only when the ECU is actively commanding this lamp on
        /// (<see cref="J1939LampCommand.On"/>). A command of
        /// <see cref="J1939LampCommand.NotAvailable"/> is "don't care", not off,
        /// and is deliberately not reported as on.
        /// </summary>
        public bool IsOn
        {
            get { return _command == J1939LampCommand.On; }
        }

        /// <summary>
        /// True when the ECU is not commanding this lamp at all (encoding 11).
        /// Distinct from off: nothing can be concluded about the lamp.
        /// </summary>
        public bool IsNotAvailable
        {
            get { return _command == J1939LampCommand.NotAvailable; }
        }

        /// <summary>
        /// True when the lamp is on AND the flash command asks for a flash rather
        /// than a steady light. Per Table 5 a flash command only takes effect when
        /// the lamp command is 01.
        /// </summary>
        public bool IsFlashing
        {
            get
            {
                return _command == J1939LampCommand.On
                       && (_flash == J1939LampFlash.SlowFlash || _flash == J1939LampFlash.FastFlash);
            }
        }

        /// <summary>
        /// Required lamp output per J1939-73 Table 5, for example "on, fast flash"
        /// or "off". Reserved combinations are named as reserved rather than
        /// guessed at.
        /// </summary>
        public string Description
        {
            get
            {
                switch (_command)
                {
                    case J1939LampCommand.Off:
                        return "off";

                    case J1939LampCommand.On:
                        switch (_flash)
                        {
                            case J1939LampFlash.SlowFlash:
                                return "on, slow flash (1 Hz)";
                            case J1939LampFlash.FastFlash:
                                return "on, fast flash (2 Hz or faster)";
                            case J1939LampFlash.ClassCOrReserved:
                                return _isMil ? "off, class C DTC active" : "on (SAE reserved flash command)";
                            default:
                                return "on, steady";
                        }

                    case J1939LampCommand.ShortMilOrReserved:
                        if (!_isMil)
                        {
                            return "SAE reserved lamp command";
                        }

                        switch (_flash)
                        {
                            case J1939LampFlash.SlowFlash:
                                return "short MI not active";
                            case J1939LampFlash.FastFlash:
                                return "short MI active";
                            default:
                                return "SAE reserved lamp command";
                        }

                    default:
                        return "not available (ECU is not commanding this lamp)";
                }
            }
        }

        /// <summary>Single-line form, for example "Red Stop: on, fast flash (2 Hz or faster)".</summary>
        /// <returns>The formatted lamp.</returns>
        public override string ToString()
        {
            return string.Format(CultureInfo.InvariantCulture, "{0}: {1}", _name, Description);
        }
    }

    /// <summary>
    /// The four diagnostic lamps carried in bytes 1 and 2 of DM1 and DM2.
    /// </summary>
    /// <remarks>
    /// <para>Byte 1 (lamp commands), J1939 bit 8 is the most significant:</para>
    /// <list type="table">
    /// <item><term>bits 8-7</term><description>Malfunction indicator lamp status (SPN 1213)</description></item>
    /// <item><term>bits 6-5</term><description>Red stop lamp status (SPN 623)</description></item>
    /// <item><term>bits 4-3</term><description>Amber warning lamp status (SPN 624)</description></item>
    /// <item><term>bits 2-1</term><description>Protect lamp status (SPN 987)</description></item>
    /// </list>
    /// <para>Byte 2 (flash commands):</para>
    /// <list type="table">
    /// <item><term>bits 8-7</term><description>Flash malfunction indicator lamp (SPN 3038)</description></item>
    /// <item><term>bits 6-5</term><description>Flash red stop lamp (SPN 3039)</description></item>
    /// <item><term>bits 4-3</term><description>Flash amber warning lamp (SPN 3040)</description></item>
    /// <item><term>bits 2-1</term><description>Flash protect lamp (SPN 3041)</description></item>
    /// </list>
    /// <para>
    /// A technician reads this before reading the codes, so it is the first
    /// member of <see cref="J1939DtcReport"/> and has its own one-line
    /// <see cref="Headline"/>.
    /// </para>
    /// </remarks>
    public sealed class J1939LampStatus
    {
        private readonly J1939Lamp _mil;
        private readonly J1939Lamp _redStop;
        private readonly J1939Lamp _amberWarning;
        private readonly J1939Lamp _protect;
        private readonly byte _rawLampByte;
        private readonly byte _rawFlashByte;
        private readonly bool _present;

        private J1939LampStatus(byte lampByte, byte flashByte, bool present)
        {
            _rawLampByte = lampByte;
            _rawFlashByte = flashByte;
            _present = present;

            // J1939 numbers bits 8 (MSB) down to 1 (LSB); bits 8-7 is therefore
            // the top two bits of the byte.
            _mil = new J1939Lamp("Malfunction Indicator", true,
                (J1939LampCommand)((lampByte >> 6) & 0x03),
                (J1939LampFlash)((flashByte >> 6) & 0x03));

            _redStop = new J1939Lamp("Red Stop", false,
                (J1939LampCommand)((lampByte >> 4) & 0x03),
                (J1939LampFlash)((flashByte >> 4) & 0x03));

            _amberWarning = new J1939Lamp("Amber Warning", false,
                (J1939LampCommand)((lampByte >> 2) & 0x03),
                (J1939LampFlash)((flashByte >> 2) & 0x03));

            _protect = new J1939Lamp("Protect", false,
                (J1939LampCommand)(lampByte & 0x03),
                (J1939LampFlash)(flashByte & 0x03));
        }

        /// <summary>
        /// Decodes the two lamp bytes of a DM1 or DM2 payload.
        /// </summary>
        /// <param name="lampByte">Byte 1 of the payload - the four lamp commands.</param>
        /// <param name="flashByte">Byte 2 of the payload - the four flash commands.</param>
        /// <returns>The decoded lamp status.</returns>
        public static J1939LampStatus Decode(byte lampByte, byte flashByte)
        {
            return new J1939LampStatus(lampByte, flashByte, true);
        }

        /// <summary>
        /// The status used when a payload was too short to contain lamp bytes.
        /// Every lamp reads as not available and <see cref="IsPresent"/> is false,
        /// so a UI shows "unknown" rather than a fabricated "all off".
        /// </summary>
        /// <returns>An absent lamp status.</returns>
        public static J1939LampStatus Absent()
        {
            return new J1939LampStatus(0xFF, 0xFF, false);
        }

        /// <summary>False when the source payload was too short to carry lamp bytes.</summary>
        public bool IsPresent
        {
            get { return _present; }
        }

        /// <summary>Malfunction indicator lamp (SPN 1213 / 3038). Emissions faults only.</summary>
        public J1939Lamp MalfunctionIndicator
        {
            get { return _mil; }
        }

        /// <summary>Red stop lamp (SPN 623 / 3039). The ECU is asking for the vehicle to be stopped.</summary>
        public J1939Lamp RedStop
        {
            get { return _redStop; }
        }

        /// <summary>Amber warning lamp (SPN 624 / 3040). Fault present, immediate stop not required.</summary>
        public J1939Lamp AmberWarning
        {
            get { return _amberWarning; }
        }

        /// <summary>Protect lamp (SPN 987 / 3041). System condition, usually not an electronic fault.</summary>
        public J1939Lamp Protect
        {
            get { return _protect; }
        }

        /// <summary>Byte 1 of the payload, as received.</summary>
        public byte RawLampByte
        {
            get { return _rawLampByte; }
        }

        /// <summary>Byte 2 of the payload, as received.</summary>
        public byte RawFlashByte
        {
            get { return _rawFlashByte; }
        }

        /// <summary>True when at least one of the four lamps is commanded on.</summary>
        public bool AnyLampOn
        {
            get
            {
                return _mil.IsOn || _redStop.IsOn || _amberWarning.IsOn || _protect.IsOn;
            }
        }

        /// <summary>
        /// True when the red stop lamp is on. Surface this hard: the transmitting
        /// ECU has declared a condition severe enough to warrant stopping the
        /// vehicle (J1939-73 5.7.1.2).
        /// </summary>
        public bool IsStopNow
        {
            get { return _redStop.IsOn; }
        }

        /// <summary>Highest urgency among the four lamps.</summary>
        public J1939LampSeverity Severity
        {
            get
            {
                if (_redStop.IsOn) return J1939LampSeverity.StopNow;
                if (_amberWarning.IsOn) return J1939LampSeverity.Warning;
                if (_protect.IsOn) return J1939LampSeverity.Protect;
                if (_mil.IsOn) return J1939LampSeverity.Emissions;
                return J1939LampSeverity.None;
            }
        }

        /// <summary>
        /// One line for the top of a technician's screen, for example
        /// "RED STOP lamp on - ECU is requesting the vehicle be stopped".
        /// </summary>
        public string Headline
        {
            get
            {
                if (!_present)
                {
                    return "Lamp status unknown - message too short to carry lamp bytes";
                }

                switch (Severity)
                {
                    case J1939LampSeverity.StopNow:
                        return "RED STOP lamp on - ECU is requesting the vehicle be stopped";
                    case J1939LampSeverity.Warning:
                        return "AMBER WARNING lamp on - fault present, immediate stop not required";
                    case J1939LampSeverity.Protect:
                        return "PROTECT lamp on - system operating outside its protected range";
                    case J1939LampSeverity.Emissions:
                        return "MALFUNCTION INDICATOR lamp on - emissions-related fault";
                    default:
                        return _mil.IsNotAvailable && _redStop.IsNotAvailable
                               && _amberWarning.IsNotAvailable && _protect.IsNotAvailable
                            ? "No lamp commanded - this ECU reports all four lamps as not available"
                            : "All lamps off";
                }
            }
        }

        /// <summary>
        /// All four lamps on one line, for example
        /// "MIL off | Red Stop on, fast flash (2 Hz or faster) | Amber Warning off | Protect off".
        /// </summary>
        public string Summary
        {
            get
            {
                return string.Format(CultureInfo.InvariantCulture,
                    "MIL {0} | Red Stop {1} | Amber Warning {2} | Protect {3}",
                    _mil.Description, _redStop.Description, _amberWarning.Description, _protect.Description);
            }
        }

        /// <summary>Headline followed by the per-lamp summary.</summary>
        /// <returns>The formatted lamp status.</returns>
        public override string ToString()
        {
            return string.Format(CultureInfo.InvariantCulture, "{0} [{1}]", Headline, Summary);
        }
    }

    /// <summary>
    /// One J1939-73 diagnostic trouble code: a 19-bit suspect parameter number,
    /// a 5-bit failure mode identifier, the 1-bit SPN conversion method flag and
    /// a 7-bit occurrence count.
    /// </summary>
    /// <remarks>
    /// <para>Wire layout, DM1/DM2 bytes 3-6 (J1939-73 5.7.1):</para>
    /// <list type="table">
    /// <item><term>byte 1</term><description>SPN bits 7-0</description></item>
    /// <item><term>byte 2</term><description>SPN bits 15-8</description></item>
    /// <item><term>byte 3</term><description>bits 8-6 SPN bits 18-16, bits 5-1 FMI</description></item>
    /// <item><term>byte 4</term><description>bit 8 conversion method, bits 7-1 occurrence count</description></item>
    /// </list>
    /// <para>
    /// That layout is version 4 and applies only when the conversion method bit
    /// is 0. When it is 1 the SPN is in version 1, 2 or 3 format and the message
    /// does not say which, so <see cref="Spn"/> is a best-supported reading,
    /// <see cref="ConversionUncertain"/> is true, and <see cref="SpnCandidates"/>
    /// lists every possibility.
    /// </para>
    /// </remarks>
    public sealed class J1939Dtc
    {
        /// <summary>Largest encodable SPN (19 bits). Also the "not available" value.</summary>
        public const uint MAX_SPN = 0x7FFFFu;

        /// <summary>Largest encodable FMI (5 bits). FMI 31 means "condition exists", or "not available" when the SPN is also all ones.</summary>
        public const byte MAX_FMI = 31;

        /// <summary>Occurrence count value that means "not available" (J1939-73 5.7.1.12).</summary>
        public const byte OCCURRENCE_COUNT_NOT_AVAILABLE = 127;

        /// <summary>Lowest SPN reserved for proprietary manufacturer diagnostics.</summary>
        public const uint PROPRIETARY_SPN_MINIMUM = 520192u;

        private readonly uint _spn;
        private readonly byte _fmi;
        private readonly byte _conversionMethod;
        private readonly byte _occurrenceCount;
        private readonly J1939SpnConversionVersion _selectedVersion;
        private readonly J1939SpnCandidate[] _candidates;
        private readonly byte[] _rawBytes;

        internal J1939Dtc(uint spn, byte fmi, byte conversionMethod, byte occurrenceCount,
            J1939SpnConversionVersion selectedVersion, J1939SpnCandidate[] candidates, byte[] rawBytes)
        {
            _spn = spn;
            _fmi = fmi;
            _conversionMethod = conversionMethod;
            _occurrenceCount = occurrenceCount;
            _selectedVersion = selectedVersion;
            _candidates = candidates ?? new J1939SpnCandidate[0];
            _rawBytes = rawBytes;
        }

        /// <summary>
        /// Suspect parameter number, 0 to 524287. When
        /// <see cref="ConversionUncertain"/> is true this is the reading produced
        /// by <see cref="SelectedConversionVersion"/> and must not be presented as
        /// authoritative.
        /// </summary>
        public uint Spn
        {
            get { return _spn; }
        }

        /// <summary>Failure mode identifier, 0 to 31. Standardised across all manufacturers.</summary>
        public byte Fmi
        {
            get { return _fmi; }
        }

        /// <summary>SPN conversion method flag: 0 for version 4, 1 for legacy versions 1-3.</summary>
        public byte ConversionMethod
        {
            get { return _conversionMethod; }
        }

        /// <summary>
        /// Occurrence count, 0 to 126. A value of 127 means the count is not
        /// available - check <see cref="OccurrenceCountAvailable"/> before showing
        /// it, or a technician reads "127 occurrences" that never happened.
        /// </summary>
        public byte OccurrenceCount
        {
            get { return _occurrenceCount; }
        }

        /// <summary>False when the occurrence count field holds the "not available" value 127.</summary>
        public bool OccurrenceCountAvailable
        {
            get { return _occurrenceCount != OCCURRENCE_COUNT_NOT_AVAILABLE; }
        }

        /// <summary>
        /// True when the conversion method bit was 1, meaning the SPN is in one of
        /// three legacy layouts that the message does not distinguish. Mark these
        /// in the UI.
        /// </summary>
        public bool ConversionUncertain
        {
            get { return _conversionMethod != 0; }
        }

        /// <summary>Layout used to produce <see cref="Spn"/>.</summary>
        public J1939SpnConversionVersion SelectedConversionVersion
        {
            get { return _selectedVersion; }
        }

        /// <summary>
        /// Every SPN this DTC's bytes could represent. Empty when
        /// <see cref="ConversionUncertain"/> is false, because version 4 is
        /// unambiguous. When it is true this holds the version 1, 2 and 3
        /// readings; a caller holding an SPN definition database can pick the one
        /// that resolves.
        /// </summary>
        public J1939SpnCandidate[] SpnCandidates
        {
            get { return _candidates; }
        }

        /// <summary>The four source bytes, in wire order. Owned by this instance; treat as read-only.</summary>
        public byte[] RawBytes
        {
            get { return _rawBytes; }
        }

        /// <summary>True when the SPN falls in the 520192-524287 range reserved for manufacturer-specific diagnostics.</summary>
        public bool IsProprietarySpn
        {
            get { return _spn >= PROPRIETARY_SPN_MINIMUM && _spn <= MAX_SPN; }
        }

        /// <summary>
        /// True when these four bytes are a "no active faults" marker or trailing
        /// fill rather than a real code: all zeros (the setting J1939-73 recommends,
        /// Example 2) or all ones (the grandfathered setting, and the 0xFF padding
        /// that fills the tail of a packet).
        /// </summary>
        public bool IsNoFaultPlaceholder
        {
            get
            {
                if (_spn == 0u && _fmi == 0) return true;
                return _spn == MAX_SPN && _fmi == MAX_FMI;
            }
        }

        /// <summary>Standardised FMI text, for example "current below normal or open circuit".</summary>
        public string FmiDescription
        {
            get { return J1939Diagnostics.FmiDescription(_fmi); }
        }

        /// <summary>
        /// Technician-facing description with no SPN definition database available.
        /// Produces, for example,
        /// "SPN 4364 (no definition loaded) - FMI 5: current below normal or open circuit".
        /// </summary>
        /// <returns>The formatted description.</returns>
        public string Describe()
        {
            return Describe(null);
        }

        /// <summary>
        /// Technician-facing description, resolving the SPN name through
        /// <paramref name="spnNameLookup"/> when one is supplied.
        /// </summary>
        /// <param name="spnNameLookup">
        /// Returns the parameter name for an SPN, or null / empty when the SPN has
        /// no definition. May be null, in which case every SPN is reported as
        /// having no definition loaded. A raw SPN is never rendered as though it
        /// were a known parameter.
        /// </param>
        /// <returns>The formatted description.</returns>
        public string Describe(Func<uint, string> spnNameLookup)
        {
            StringBuilder sb = new StringBuilder(160);

            string spnName = null;
            if (spnNameLookup != null)
            {
                spnName = spnNameLookup(_spn);
            }

            sb.AppendFormat(CultureInfo.InvariantCulture, "SPN {0} (", _spn);

            if (!string.IsNullOrEmpty(spnName))
            {
                sb.Append(spnName);
            }
            else if (IsProprietarySpn)
            {
                sb.Append("manufacturer-specific SPN, no definition loaded");
            }
            else
            {
                sb.Append("no definition loaded");
            }

            sb.AppendFormat(CultureInfo.InvariantCulture, ") - FMI {0}: {1}", _fmi, FmiDescription);

            if (OccurrenceCountAvailable)
            {
                sb.AppendFormat(CultureInfo.InvariantCulture, " [occurrences {0}]", _occurrenceCount);
            }
            else
            {
                sb.Append(" [occurrence count not available]");
            }

            if (ConversionUncertain)
            {
                sb.Append(" [SPN UNCERTAIN - CM=1, legacy conversion version 1, 2 or 3; shown as v");
                sb.Append((int)_selectedVersion);
                sb.Append(", candidates ");
                for (int i = 0; i < _candidates.Length; i++)
                {
                    if (i > 0) sb.Append(", ");
                    sb.Append(_candidates[i].ToString());
                }
                sb.Append("]");
            }

            return sb.ToString();
        }

        /// <summary>Same as <see cref="Describe()"/>.</summary>
        /// <returns>The formatted description.</returns>
        public override string ToString()
        {
            return Describe(null);
        }
    }

    /// <summary>
    /// A decoded DM1 or DM2: lamp status first, then the trouble codes.
    /// </summary>
    public sealed class J1939DtcReport
    {
        private readonly J1939DtcReportKind _kind;
        private readonly uint _pgn;
        private readonly byte _sourceAddress;
        private readonly bool _sourceAddressKnown;
        private readonly J1939LampStatus _lamps;
        private readonly ReadOnlyCollection<J1939Dtc> _dtcs;
        private readonly int _placeholderSlots;
        private readonly int _trailingBytesIgnored;
        private readonly int _payloadLength;
        private readonly string _malformedReason;

        internal J1939DtcReport(J1939DtcReportKind kind, uint pgn, byte sourceAddress, bool sourceAddressKnown,
            J1939LampStatus lamps, IList<J1939Dtc> dtcs, int placeholderSlots, int trailingBytesIgnored,
            int payloadLength, string malformedReason)
        {
            _kind = kind;
            _pgn = pgn;
            _sourceAddress = sourceAddress;
            _sourceAddressKnown = sourceAddressKnown;
            _lamps = lamps;
            _dtcs = new ReadOnlyCollection<J1939Dtc>(dtcs ?? new List<J1939Dtc>());
            _placeholderSlots = placeholderSlots;
            _trailingBytesIgnored = trailingBytesIgnored;
            _payloadLength = payloadLength;
            _malformedReason = malformedReason;
        }

        /// <summary>
        /// Lamp status. Read this first: it is what a technician reads first, and
        /// it is meaningful even when the code list is empty.
        /// </summary>
        public J1939LampStatus Lamps
        {
            get { return _lamps; }
        }

        /// <summary>Whether this report holds active (DM1) or previously active (DM2) codes.</summary>
        public J1939DtcReportKind Kind
        {
            get { return _kind; }
        }

        /// <summary>Parameter group this report was decoded from.</summary>
        public uint Pgn
        {
            get { return _pgn; }
        }

        /// <summary>Transmitting ECU address. Only meaningful when <see cref="SourceAddressKnown"/> is true.</summary>
        public byte SourceAddress
        {
            get { return _sourceAddress; }
        }

        /// <summary>True when the caller supplied the transmitting ECU address.</summary>
        public bool SourceAddressKnown
        {
            get { return _sourceAddressKnown; }
        }

        /// <summary>The decoded trouble codes. Placeholders and padding are excluded.</summary>
        public ReadOnlyCollection<J1939Dtc> Dtcs
        {
            get { return _dtcs; }
        }

        /// <summary>True when at least one real trouble code was decoded.</summary>
        public bool HasFaults
        {
            get { return _dtcs.Count > 0; }
        }

        /// <summary>
        /// Four-byte slots that held a "no fault" marker or 0xFF fill rather than a
        /// code. J1939-73 Example 2 defines both the recommended all-zero form and
        /// the grandfathered all-ones form.
        /// </summary>
        public int PlaceholderSlots
        {
            get { return _placeholderSlots; }
        }

        /// <summary>Bytes left over after the last whole four-byte slot, discarded rather than decoded as a partial DTC.</summary>
        public int TrailingBytesIgnored
        {
            get { return _trailingBytesIgnored; }
        }

        /// <summary>Length of the payload this report was decoded from.</summary>
        public int PayloadLength
        {
            get { return _payloadLength; }
        }

        /// <summary>True when the payload was longer than a single CAN frame and therefore arrived over J1939-21 transport.</summary>
        public bool IsMultiFrame
        {
            get { return _payloadLength > 8; }
        }

        /// <summary>
        /// True when at least one DTC in this report carries CM=1 and its SPN is
        /// therefore a best-supported reading rather than a certainty.
        /// </summary>
        public bool AnyConversionUncertain
        {
            get
            {
                for (int i = 0; i < _dtcs.Count; i++)
                {
                    if (_dtcs[i].ConversionUncertain) return true;
                }
                return false;
            }
        }

        /// <summary>Why the payload could not be fully decoded, or null when it was well formed.</summary>
        public string MalformedReason
        {
            get { return _malformedReason; }
        }

        /// <summary>True when <see cref="MalformedReason"/> is set.</summary>
        public bool IsMalformed
        {
            get { return !string.IsNullOrEmpty(_malformedReason); }
        }

        /// <summary>Short name of the source message, "DM1" or "DM2".</summary>
        public string MessageName
        {
            get { return _kind == J1939DtcReportKind.ActiveDm1 ? "DM1" : "DM2"; }
        }

        /// <summary>
        /// Lamp headline plus the code count, for example
        /// "DM1 from SA 0x00: RED STOP lamp on - ECU is requesting the vehicle be stopped; 3 active codes".
        /// </summary>
        public string Headline
        {
            get
            {
                StringBuilder sb = new StringBuilder(160);
                sb.Append(MessageName);

                if (_sourceAddressKnown)
                {
                    sb.AppendFormat(CultureInfo.InvariantCulture, " from SA 0x{0:X2}", _sourceAddress);
                }

                sb.Append(": ");
                sb.Append(_lamps.Headline);
                sb.Append("; ");

                string codeWord = _kind == J1939DtcReportKind.ActiveDm1 ? "active" : "previously active";

                if (_dtcs.Count == 0)
                {
                    sb.AppendFormat(CultureInfo.InvariantCulture, "no {0} codes", codeWord);
                }
                else
                {
                    sb.AppendFormat(CultureInfo.InvariantCulture, "{0} {1} code{2}",
                        _dtcs.Count, codeWord, _dtcs.Count == 1 ? string.Empty : "s");
                }

                if (AnyConversionUncertain)
                {
                    sb.Append(" (some SPNs uncertain - legacy CM=1 conversion)");
                }

                if (IsMalformed)
                {
                    sb.Append(" [");
                    sb.Append(_malformedReason);
                    sb.Append("]");
                }

                return sb.ToString();
            }
        }

        /// <summary>Headline followed by one line per code.</summary>
        /// <returns>The formatted report.</returns>
        public override string ToString()
        {
            StringBuilder sb = new StringBuilder(256);
            sb.Append(Headline);

            for (int i = 0; i < _dtcs.Count; i++)
            {
                sb.Append(Environment.NewLine);
                sb.Append("  ");
                sb.Append(_dtcs[i].Describe(null));
            }

            return sb.ToString();
        }
    }

    /// <summary>
    /// One calibration reported by DM19: its verification number and its
    /// identification string.
    /// </summary>
    public sealed class J1939Calibration
    {
        private readonly byte[] _cvnBytes;
        private readonly uint _cvnValue;
        private readonly byte[] _calibrationIdBytes;
        private readonly string _calibrationId;
        private readonly bool _calibrationIdPrintable;

        internal J1939Calibration(byte[] cvnBytes, uint cvnValue, byte[] calibrationIdBytes,
            string calibrationId, bool calibrationIdPrintable)
        {
            _cvnBytes = cvnBytes;
            _cvnValue = cvnValue;
            _calibrationIdBytes = calibrationIdBytes;
            _calibrationId = calibrationId;
            _calibrationIdPrintable = calibrationIdPrintable;
        }

        /// <summary>The four CVN bytes in wire order. Byte 0 is the least significant (J1939-73 5.7.19).</summary>
        public byte[] CvnBytes
        {
            get { return _cvnBytes; }
        }

        /// <summary>
        /// The CVN as a number, assembled least-significant-byte-first per
        /// J1939-73 5.7.19. Its declared type is Hexadecimal.
        /// </summary>
        public uint CvnValue
        {
            get { return _cvnValue; }
        }

        /// <summary>
        /// The CVN rendered as hex, for example "0x00ABCDEF".
        /// </summary>
        /// <remarks>
        /// The CVN is a four-byte checksum over the calibration image. It is NOT
        /// text and must never be rendered as ASCII - doing so produces mojibake
        /// that looks like a part number. This has been a real defect in this
        /// codebase before (Mode 09 PID 06 CVN, bug 42), so the only string form
        /// offered here is hexadecimal.
        /// </remarks>
        public string CvnHex
        {
            get
            {
                return string.Format(CultureInfo.InvariantCulture, "0x{0:X8}", _cvnValue);
            }
        }

        /// <summary>The four CVN bytes as they appeared on the wire, for example "EF CD AB 00".</summary>
        public string CvnWireHex
        {
            get { return J1939Diagnostics.ToHex(_cvnBytes); }
        }

        /// <summary>The sixteen calibration identification bytes as received, including any padding.</summary>
        public byte[] CalibrationIdBytes
        {
            get { return _calibrationIdBytes; }
        }

        /// <summary>
        /// Calibration identification as text, with the trailing 0x00 padding
        /// removed. Non-printable bytes are shown as '.' and set
        /// <see cref="CalibrationIdPrintable"/> false; J1939-73 5.7.19.2 limits
        /// this field to printable characters, so anything else means the module
        /// is out of spec or the frame is corrupt.
        /// </summary>
        public string CalibrationId
        {
            get { return _calibrationId; }
        }

        /// <summary>False when the identification field contained bytes outside the printable ASCII range.</summary>
        public bool CalibrationIdPrintable
        {
            get { return _calibrationIdPrintable; }
        }

        /// <summary>The sixteen identification bytes as hex, for use when the text form is not printable.</summary>
        public string CalibrationIdHex
        {
            get { return J1939Diagnostics.ToHex(_calibrationIdBytes); }
        }

        /// <summary>Single-line form, for example "CAL ID 'CONTENDER1' CVN 0x00ABCDEF".</summary>
        /// <returns>The formatted calibration.</returns>
        public override string ToString()
        {
            if (_calibrationIdPrintable)
            {
                return string.Format(CultureInfo.InvariantCulture,
                    "CAL ID '{0}' CVN {1}", _calibrationId, CvnHex);
            }

            return string.Format(CultureInfo.InvariantCulture,
                "CAL ID (not printable: {0}) CVN {1}", CalibrationIdHex, CvnHex);
        }
    }

    /// <summary>A decoded DM19 response: every calibration the module reported.</summary>
    public sealed class J1939Dm19Report
    {
        private readonly ReadOnlyCollection<J1939Calibration> _calibrations;
        private readonly int _payloadLength;
        private readonly int _trailingBytesIgnored;
        private readonly string _malformedReason;

        internal J1939Dm19Report(IList<J1939Calibration> calibrations, int payloadLength,
            int trailingBytesIgnored, string malformedReason)
        {
            _calibrations = new ReadOnlyCollection<J1939Calibration>(calibrations ?? new List<J1939Calibration>());
            _payloadLength = payloadLength;
            _trailingBytesIgnored = trailingBytesIgnored;
            _malformedReason = malformedReason;
        }

        /// <summary>The reported calibrations, in wire order.</summary>
        public ReadOnlyCollection<J1939Calibration> Calibrations
        {
            get { return _calibrations; }
        }

        /// <summary>Length of the payload this report was decoded from.</summary>
        public int PayloadLength
        {
            get { return _payloadLength; }
        }

        /// <summary>Bytes left over after the last whole twenty-byte group, discarded rather than decoded as a partial calibration.</summary>
        public int TrailingBytesIgnored
        {
            get { return _trailingBytesIgnored; }
        }

        /// <summary>Why the payload could not be fully decoded, or null when it was well formed.</summary>
        public string MalformedReason
        {
            get { return _malformedReason; }
        }

        /// <summary>True when <see cref="MalformedReason"/> is set.</summary>
        public bool IsMalformed
        {
            get { return !string.IsNullOrEmpty(_malformedReason); }
        }

        /// <summary>One line per calibration.</summary>
        /// <returns>The formatted report.</returns>
        public override string ToString()
        {
            StringBuilder sb = new StringBuilder(128);
            sb.AppendFormat(CultureInfo.InvariantCulture,
                "DM19: {0} calibration{1}", _calibrations.Count, _calibrations.Count == 1 ? string.Empty : "s");

            if (IsMalformed)
            {
                sb.Append(" [");
                sb.Append(_malformedReason);
                sb.Append("]");
            }

            for (int i = 0; i < _calibrations.Count; i++)
            {
                sb.Append(Environment.NewLine);
                sb.Append("  ");
                sb.Append(_calibrations[i].ToString());
            }

            return sb.ToString();
        }
    }

    /// <summary>
    /// A prepared but UNSENT request to erase diagnostic data (DM3 or DM11).
    /// </summary>
    /// <remarks>
    /// <para>
    /// This type exists so a destructive operation cannot be built and dispatched
    /// in one step. <see cref="RequiresOperatorConfirmation"/> is always true, and
    /// <see cref="ToMessage"/> refuses to produce a transmittable frame until the
    /// caller passes the operator's answer. Use <see cref="PreviewHex"/> to show
    /// the frame in a confirmation dialog without arming it.
    /// </para>
    /// <para>
    /// This mirrors the gating used for every high-risk procedure in HeavyDiesel:
    /// the builder is safe to call, the send is not.
    /// </para>
    /// </remarks>
    public sealed class J1939ClearRequest
    {
        private readonly string _operationName;
        private readonly uint _clearPgn;
        private readonly byte _sourceAddress;
        private readonly byte _destinationAddress;
        private readonly string _warning;

        internal J1939ClearRequest(string operationName, uint clearPgn, byte sourceAddress,
            byte destinationAddress, string warning)
        {
            _operationName = operationName;
            _clearPgn = clearPgn;
            _sourceAddress = sourceAddress;
            _destinationAddress = destinationAddress;
            _warning = warning;
        }

        /// <summary>
        /// Always true. Diagnostic data clearing is irreversible; the UI layer must
        /// obtain an explicit operator decision before calling <see cref="ToMessage"/>.
        /// </summary>
        public bool RequiresOperatorConfirmation
        {
            get { return true; }
        }

        /// <summary>Human-readable operation name, for example "DM11 - clear active DTCs".</summary>
        public string OperationName
        {
            get { return _operationName; }
        }

        /// <summary>The PGN being requested: 65228 for DM3, 65235 for DM11.</summary>
        public uint ClearPgn
        {
            get { return _clearPgn; }
        }

        /// <summary>Address this tool transmits from.</summary>
        public byte SourceAddress
        {
            get { return _sourceAddress; }
        }

        /// <summary>Address the clear is directed at, or 255 for a whole-segment clear.</summary>
        public byte DestinationAddress
        {
            get { return _destinationAddress; }
        }

        /// <summary>
        /// True when this clear is addressed to the global address, so EVERY module
        /// on the segment erases its data.
        /// </summary>
        public bool IsGlobalBroadcast
        {
            get { return _destinationAddress == J1939Pgn.GlobalAddress; }
        }

        /// <summary>
        /// False for a global clear. J1939-73 5.7.3 and 5.7.11 state that no
        /// positive or negative acknowledgement is sent when the request went to
        /// the global address, so a broadcast clear gives no confirmation that it
        /// worked or that anything heard it.
        /// </summary>
        public bool AcknowledgementExpected
        {
            get { return !IsGlobalBroadcast; }
        }

        /// <summary>
        /// The full text to put in front of the operator. Never abbreviate this in
        /// a confirmation dialog.
        /// </summary>
        public string Warning
        {
            get { return _warning; }
        }

        /// <summary>
        /// The frame that would be transmitted, as hex, for display in a
        /// confirmation dialog. Reading this does not arm the request.
        /// </summary>
        public string PreviewHex
        {
            get
            {
                J1939Id id = J1939Id.Create(J1939Diagnostics.RequestPriority,
                    J1939Pgn.Request, _sourceAddress, _destinationAddress);

                return string.Format(CultureInfo.InvariantCulture,
                    "ID 0x{0:X8} data {1}", id.Raw, J1939Diagnostics.ToHex(J1939Diagnostics.BuildRequest(_clearPgn)));
            }
        }

        /// <summary>
        /// Produces the transmittable request frame.
        /// </summary>
        /// <param name="operatorConfirmed">
        /// The operator's actual answer. Pass false or a default and this throws:
        /// the interlock is deliberate, because the operation cannot be undone.
        /// </param>
        /// <returns>The Request frame that triggers the clear.</returns>
        /// <exception cref="InvalidOperationException">
        /// <paramref name="operatorConfirmed"/> is false. Erasing diagnostic data
        /// without an operator decision is never correct.
        /// </exception>
        public J1939Message ToMessage(bool operatorConfirmed)
        {
            if (!operatorConfirmed)
            {
                throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture,
                    "{0} was not confirmed by the operator. This operation erases stored fault history and cannot be undone; build the frame only after an explicit confirmation.",
                    _operationName));
            }

            J1939Id id = J1939Id.Create(J1939Diagnostics.RequestPriority,
                J1939Pgn.Request, _sourceAddress, _destinationAddress);

            return J1939Message.FromHostClock(id, J1939Diagnostics.BuildRequest(_clearPgn));
        }

        /// <summary>Operation name, target and warning on one line.</summary>
        /// <returns>The formatted request.</returns>
        public override string ToString()
        {
            return string.Format(CultureInfo.InvariantCulture,
                "{0} -> DA 0x{1:X2} (UNSENT, confirmation required): {2}",
                _operationName, _destinationAddress, _warning);
        }
    }

    /// <summary>
    /// SAE J1939-73 diagnostic message decoding and request building.
    /// </summary>
    /// <remarks>
    /// Every method here is pure: nothing transmits, reads a file, or touches the
    /// network. Building a clear request does not perform a clear.
    /// </remarks>
    public static class J1939Diagnostics
    {
        /// <summary>Bytes in one DM1 / DM2 diagnostic trouble code.</summary>
        public const int DTC_LENGTH = 4;

        /// <summary>Bytes of lamp status at the head of a DM1 / DM2 payload.</summary>
        public const int LAMP_STATUS_LENGTH = 2;

        /// <summary>Bytes in one DM19 calibration group: a four-byte CVN followed by a sixteen-byte calibration identifier.</summary>
        public const int DM19_GROUP_LENGTH = 20;

        /// <summary>Bytes in the PGN 59904 Request payload.</summary>
        public const int REQUEST_PAYLOAD_LENGTH = 3;

        /// <summary>Default priority for a Request (PGN 59904), per SAE J1939-21.</summary>
        public const byte RequestPriority = 6;

        /// <summary>
        /// DM19, Calibration Information: PGN 54016 (0x00D300), PDU format 211,
        /// destination specific.
        /// </summary>
        /// <remarks>
        /// DELIBERATELY LOCAL, AND A CONFLICT WORTH RESOLVING. The shared constant
        /// <see cref="J1939Pgn.Dm19"/> is 54272 (0x00D400), which SAE J1939-73
        /// Revised SEP2006 assigns to DM18 (Data Security) - DM19 is defined at
        /// 54016 (0x00D300) in section 5.7.19 and is listed as 54016 in both the
        /// standard's OBD service table and its cross-reference appendix. An earlier
        /// internal spec carried the same 54272 error. Requesting 54272
        /// would ask a module for Data Security and get a NACK or the wrong
        /// parameter group, so this file uses the verified value and does not edit
        /// the shared constant. BENCH CONFIRMATION REQUESTED; once settled, fix
        /// J1939Pgn.Dm19 and delete this constant.
        /// </remarks>
        public const uint Dm19CalibrationInformation = 54016;

        // -----------------------------------------------------------------
        // Failure mode identifier
        // -----------------------------------------------------------------

        /// <summary>
        /// The standardised failure mode identifier description, J1939-73
        /// Appendix A.
        /// </summary>
        /// <remarks>
        /// FMI is standardised across every manufacturer, which makes this the
        /// honest degradation path: a fault is still describable when its SPN has
        /// no definition entry. See <see cref="J1939Dtc.Describe(Func{uint,string})"/>,
        /// which combines the two into
        /// "SPN 4364 (no definition loaded) - FMI 5: current below normal or open circuit".
        /// </remarks>
        /// <param name="fmi">Failure mode identifier, 0 to 31.</param>
        /// <returns>The standardised description. Values above 31 are reported as invalid rather than guessed at.</returns>
        public static string FmiDescription(byte fmi)
        {
            switch (fmi)
            {
                case 0: return "data valid but above normal operational range - most severe level";
                case 1: return "data valid but below normal operational range - most severe level";
                case 2: return "data erratic, intermittent or incorrect";
                case 3: return "voltage above normal or shorted to high source";
                case 4: return "voltage below normal or shorted to low source";
                case 5: return "current below normal or open circuit";
                case 6: return "current above normal or grounded circuit";
                case 7: return "mechanical system not responding or out of adjustment";
                case 8: return "abnormal frequency or pulse width or period";
                case 9: return "abnormal update rate";
                case 10: return "abnormal rate of change";
                case 11: return "root cause not known";
                case 12: return "bad intelligent device or component";
                case 13: return "out of calibration";
                case 14: return "special instructions - refer to the manufacturer's troubleshooting manual";
                case 15: return "data valid but above normal operating range - least severe level";
                case 16: return "data valid but above normal operating range - moderately severe level";
                case 17: return "data valid but below normal operating range - least severe level";
                case 18: return "data valid but below normal operating range - moderately severe level";
                case 19: return "received network data in error";
                case 20: return "data drifted high";
                case 21: return "data drifted low";
                case 22:
                case 23:
                case 24:
                case 25:
                case 26:
                case 27:
                case 28:
                case 29:
                case 30:
                    return "reserved for SAE assignment - no standard meaning";
                case 31: return "condition exists";
                default:
                    return string.Format(CultureInfo.InvariantCulture,
                        "invalid FMI {0} - the FMI field is 5 bits, 0 to 31", fmi);
            }
        }

        // -----------------------------------------------------------------
        // Diagnostic trouble code
        // -----------------------------------------------------------------

        /// <summary>
        /// Decodes one four-byte diagnostic trouble code, branching on the SPN
        /// conversion method flag.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Byte 4 bit 8 is the conversion method. When it is 0 the SPN is version
        /// 4 and the decode is authoritative. When it is 1 the SPN is in version
        /// 1, 2 or 3 format and J1939-73 5.7.1.11 does not provide any way for a
        /// receiver to tell which - so all three readings are computed, the
        /// version 3 reading is selected, and
        /// <see cref="J1939Dtc.ConversionUncertain"/> is set.
        /// </para>
        /// <para>
        /// Version 3 is the selected reading because it is byte-identical to
        /// version 4 (the two differ only in the value of the CM bit), which makes
        /// it correct both for genuine version 3 modules and for the common
        /// real-world case of a module that packs the modern layout but sets the
        /// flag wrongly. It is a choice, not a certainty, which is why the flag and
        /// <see cref="J1939Dtc.SpnCandidates"/> are both exposed: a caller holding
        /// an SPN definition database can select whichever candidate resolves.
        /// </para>
        /// <para>Verified against the worked example in J1939-73 5.7.1.11 (SPN 1208, FMI 3, OC 10):</para>
        /// <list type="table">
        /// <item><term>version 1</term><description>00 97 03 8A</description></item>
        /// <item><term>version 2</term><description>97 00 03 8A</description></item>
        /// <item><term>version 3</term><description>B8 04 03 8A</description></item>
        /// <item><term>version 4</term><description>B8 04 03 0A</description></item>
        /// </list>
        /// </remarks>
        /// <param name="fourBytes">Exactly four bytes, in wire order.</param>
        /// <returns>The decoded code.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="fourBytes"/> is null.</exception>
        /// <exception cref="ArgumentException"><paramref name="fourBytes"/> is not exactly four bytes long.</exception>
        public static J1939Dtc DecodeDtc(byte[] fourBytes)
        {
            if (fourBytes == null)
            {
                throw new ArgumentNullException("fourBytes");
            }

            if (fourBytes.Length != DTC_LENGTH)
            {
                throw new ArgumentException(string.Format(CultureInfo.InvariantCulture,
                    "A J1939 DTC is exactly {0} bytes; got {1}. Use DecodeDtc(buffer, offset) to read one out of a longer payload.",
                    DTC_LENGTH, fourBytes.Length), "fourBytes");
            }

            return DecodeDtc(fourBytes, 0);
        }

        /// <summary>
        /// Decodes the four-byte diagnostic trouble code at
        /// <paramref name="offset"/> within a longer payload.
        /// </summary>
        /// <param name="buffer">Payload holding the code.</param>
        /// <param name="offset">Index of the code's first byte.</param>
        /// <returns>The decoded code.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="buffer"/> is null.</exception>
        /// <exception cref="ArgumentOutOfRangeException">Four bytes are not available at <paramref name="offset"/>.</exception>
        public static J1939Dtc DecodeDtc(byte[] buffer, int offset)
        {
            if (buffer == null)
            {
                throw new ArgumentNullException("buffer");
            }

            if (offset < 0 || offset > buffer.Length - DTC_LENGTH)
            {
                throw new ArgumentOutOfRangeException("offset", offset, string.Format(CultureInfo.InvariantCulture,
                    "Need {0} bytes at this offset; the payload is {1} bytes long.", DTC_LENGTH, buffer.Length));
            }

            byte b0 = buffer[offset];
            byte b1 = buffer[offset + 1];
            byte b2 = buffer[offset + 2];
            byte b3 = buffer[offset + 3];

            // FMI, CM and OC sit in the same places in every version; only the SPN
            // packing changes.
            byte fmi = (byte)(b2 & 0x1F);
            byte conversionMethod = (byte)((b3 >> 7) & 0x01);
            byte occurrenceCount = (byte)(b3 & 0x7F);

            byte[] raw = new byte[DTC_LENGTH];
            raw[0] = b0;
            raw[1] = b1;
            raw[2] = b2;
            raw[3] = b3;

            // Version 3 and version 4 share this packing: Intel order across all
            // 19 bits, with the three most significant SPN bits in byte 3 bits 8-6.
            uint spnIntel = (uint)b0 | ((uint)b1 << 8) | (((uint)b2 >> 5) & 0x07u) << 16;

            if (conversionMethod == 0)
            {
                return new J1939Dtc(spnIntel, fmi, conversionMethod, occurrenceCount,
                    J1939SpnConversionVersion.Version4, null, raw);
            }

            // Version 1: SPN sent most significant bit first. Bytes 3 and 4 carry
            // the 16 most significant bits of the 19, and byte 5 bits 8-6 carry the
            // three LEAST significant bits.
            uint spnV1 = ((uint)b0 << 11) | ((uint)b1 << 3) | (((uint)b2 >> 5) & 0x07u);

            // Version 2: as version 1 with the two SPN bytes exchanged.
            uint spnV2 = ((uint)b1 << 11) | ((uint)b0 << 3) | (((uint)b2 >> 5) & 0x07u);

            J1939SpnCandidate[] candidates = new J1939SpnCandidate[3];
            candidates[0] = new J1939SpnCandidate(J1939SpnConversionVersion.Version1, spnV1);
            candidates[1] = new J1939SpnCandidate(J1939SpnConversionVersion.Version2, spnV2);
            candidates[2] = new J1939SpnCandidate(J1939SpnConversionVersion.Version3, spnIntel);

            return new J1939Dtc(spnIntel, fmi, conversionMethod, occurrenceCount,
                J1939SpnConversionVersion.Version3, candidates, raw);
        }

        // -----------------------------------------------------------------
        // Lamp status
        // -----------------------------------------------------------------

        /// <summary>
        /// Decodes the two lamp bytes at the head of a DM1 or DM2 payload.
        /// </summary>
        /// <param name="lampByte">Byte 1 - the four lamp commands.</param>
        /// <param name="flashByte">Byte 2 - the four flash commands.</param>
        /// <returns>The decoded lamp status.</returns>
        public static J1939LampStatus DecodeLampStatus(byte lampByte, byte flashByte)
        {
            return J1939LampStatus.Decode(lampByte, flashByte);
        }

        /// <summary>Formats bytes as space-separated uppercase hex, for example "EF CD AB 00".</summary>
        /// <param name="bytes">Bytes to format. Null yields an empty string.</param>
        /// <returns>The formatted bytes.</returns>
        public static string ToHex(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0)
            {
                return string.Empty;
            }

            StringBuilder sb = new StringBuilder(bytes.Length * 3);
            for (int i = 0; i < bytes.Length; i++)
            {
                if (i > 0) sb.Append(' ');
                sb.AppendFormat(CultureInfo.InvariantCulture, "{0:X2}", bytes[i]);
            }

            return sb.ToString();
        }

        // -----------------------------------------------------------------
        // DM1 / DM2
        // -----------------------------------------------------------------

        /// <summary>
        /// Parses a DM1 payload (PGN 65226) - lamp status plus the currently
        /// active diagnostic trouble codes.
        /// </summary>
        /// <param name="payload">
        /// The message data: either a single CAN frame of up to 8 bytes, or a
        /// payload already reassembled from J1939-21 transport. Trailing 0xFF or
        /// 0x00 fill is recognised and not decoded as a code.
        /// </param>
        /// <returns>The decoded report. Lamp status is populated even when there are no codes.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="payload"/> is null.</exception>
        public static J1939DtcReport ParseDm1(byte[] payload)
        {
            return ParseDtcReport(payload, J1939DtcReportKind.ActiveDm1, 0, false);
        }

        /// <summary>
        /// Parses a DM1 payload and records which ECU it came from.
        /// </summary>
        /// <param name="payload">The message data.</param>
        /// <param name="sourceAddress">Address of the transmitting ECU.</param>
        /// <returns>The decoded report.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="payload"/> is null.</exception>
        public static J1939DtcReport ParseDm1(byte[] payload, byte sourceAddress)
        {
            return ParseDtcReport(payload, J1939DtcReportKind.ActiveDm1, sourceAddress, true);
        }

        /// <summary>
        /// Parses a DM2 payload (PGN 65227) - lamp status plus the previously
        /// active diagnostic trouble codes. Structurally identical to DM1
        /// (J1939-73 5.7.2).
        /// </summary>
        /// <param name="payload">The message data, single frame or reassembled.</param>
        /// <returns>The decoded report.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="payload"/> is null.</exception>
        public static J1939DtcReport ParseDm2(byte[] payload)
        {
            return ParseDtcReport(payload, J1939DtcReportKind.PreviouslyActiveDm2, 0, false);
        }

        /// <summary>
        /// Parses a DM2 payload and records which ECU it came from.
        /// </summary>
        /// <param name="payload">The message data.</param>
        /// <param name="sourceAddress">Address of the transmitting ECU.</param>
        /// <returns>The decoded report.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="payload"/> is null.</exception>
        public static J1939DtcReport ParseDm2(byte[] payload, byte sourceAddress)
        {
            return ParseDtcReport(payload, J1939DtcReportKind.PreviouslyActiveDm2, sourceAddress, true);
        }

        private static J1939DtcReport ParseDtcReport(byte[] payload, J1939DtcReportKind kind,
            byte sourceAddress, bool sourceAddressKnown)
        {
            if (payload == null)
            {
                throw new ArgumentNullException("payload");
            }

            uint pgn = kind == J1939DtcReportKind.ActiveDm1 ? J1939Pgn.Dm1 : J1939Pgn.Dm2;
            List<J1939Dtc> dtcs = new List<J1939Dtc>();

            if (payload.Length < LAMP_STATUS_LENGTH)
            {
                // Too short to hold lamp bytes. Report it rather than inventing
                // "all lamps off", which would read as good news.
                string tooShort = string.Format(CultureInfo.InvariantCulture,
                    "payload is {0} byte(s); at least {1} are needed for lamp status",
                    payload.Length, LAMP_STATUS_LENGTH);

                return new J1939DtcReport(kind, pgn, sourceAddress, sourceAddressKnown,
                    J1939LampStatus.Absent(), dtcs, 0, payload.Length, payload.Length, tooShort);
            }

            J1939LampStatus lamps = J1939LampStatus.Decode(payload[0], payload[1]);

            int placeholderSlots = 0;
            int index = LAMP_STATUS_LENGTH;

            while (index + DTC_LENGTH <= payload.Length)
            {
                J1939Dtc dtc = DecodeDtc(payload, index);

                // Both "no active faults" encodings from J1939-73 Example 2 (the
                // recommended all-zero form and the grandfathered all-ones form),
                // and the 0xFF fill that pads the tail of a frame, land here.
                if (dtc.IsNoFaultPlaceholder)
                {
                    placeholderSlots++;
                }
                else
                {
                    dtcs.Add(dtc);
                }

                index += DTC_LENGTH;
            }

            int trailing = payload.Length - index;
            string malformed = null;

            if (trailing > 0 && !IsFill(payload, index, trailing))
            {
                // A partial code. Do not decode it - a three-byte remainder cannot
                // produce a trustworthy SPN, and a fabricated one is worse than a
                // reported gap.
                malformed = string.Format(CultureInfo.InvariantCulture,
                    "{0} trailing byte(s) ({1}) are not a whole DTC and are not 0x00/0xFF fill - discarded",
                    trailing, ToHex(Slice(payload, index, trailing)));
            }

            return new J1939DtcReport(kind, pgn, sourceAddress, sourceAddressKnown,
                lamps, dtcs, placeholderSlots, trailing, payload.Length, malformed);
        }

        // -----------------------------------------------------------------
        // DM19
        // -----------------------------------------------------------------

        /// <summary>
        /// Parses a DM19 payload - the calibration verification number and
        /// calibration identifier for each calibration in the module (J1939-73
        /// 5.7.19). The payload repeats a twenty-byte group: four CVN bytes
        /// (least significant first) then sixteen calibration identifier bytes.
        /// </summary>
        /// <remarks>
        /// The CVN is a four-byte checksum over the calibration image, and
        /// <see cref="J1939Calibration"/> only ever renders it as hexadecimal.
        /// Rendering a CVN as ASCII produces a plausible-looking string that is
        /// not a part number and is not anything else either.
        /// </remarks>
        /// <param name="payload">The message data, usually reassembled from transport since one group needs 20 bytes.</param>
        /// <returns>The decoded report.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="payload"/> is null.</exception>
        public static J1939Dm19Report ParseDm19(byte[] payload)
        {
            if (payload == null)
            {
                throw new ArgumentNullException("payload");
            }

            List<J1939Calibration> calibrations = new List<J1939Calibration>();
            int index = 0;

            while (index + DM19_GROUP_LENGTH <= payload.Length)
            {
                byte[] cvn = Slice(payload, index, 4);

                // J1939-73 5.7.19: "Byte 1 is least significant byte", and a
                // checksum shorter than four bytes is padded with 0x00 in the most
                // significant bytes.
                uint cvnValue = (uint)cvn[0]
                                | ((uint)cvn[1] << 8)
                                | ((uint)cvn[2] << 16)
                                | ((uint)cvn[3] << 24);

                byte[] calIdBytes = Slice(payload, index + 4, 16);

                bool printable;
                string calId = DecodeCalibrationId(calIdBytes, out printable);

                calibrations.Add(new J1939Calibration(cvn, cvnValue, calIdBytes, calId, printable));
                index += DM19_GROUP_LENGTH;
            }

            int trailing = payload.Length - index;
            string malformed = null;

            if (trailing > 0 && !IsFill(payload, index, trailing))
            {
                malformed = string.Format(CultureInfo.InvariantCulture,
                    "{0} trailing byte(s) ({1}) are not a whole {2}-byte calibration group - discarded",
                    trailing, ToHex(Slice(payload, index, trailing)), DM19_GROUP_LENGTH);
            }
            else if (calibrations.Count == 0 && payload.Length > 0)
            {
                malformed = string.Format(CultureInfo.InvariantCulture,
                    "payload is {0} byte(s); a DM19 calibration group is {1} bytes",
                    payload.Length, DM19_GROUP_LENGTH);
            }

            return new J1939Dm19Report(calibrations, payload.Length, trailing, malformed);
        }

        private static string DecodeCalibrationId(byte[] bytes, out bool printable)
        {
            // 5.7.19.2: ASCII, printable characters only, padded at the end with
            // 0x00 when shorter than sixteen bytes. 0xFF padding is out of spec but
            // common, so it is trimmed too.
            int end = bytes.Length;
            while (end > 0 && (bytes[end - 1] == 0x00 || bytes[end - 1] == 0xFF || bytes[end - 1] == 0x20))
            {
                end--;
            }

            printable = true;
            StringBuilder sb = new StringBuilder(end);

            for (int i = 0; i < end; i++)
            {
                byte b = bytes[i];
                if (b >= 0x20 && b <= 0x7E)
                {
                    sb.Append((char)b);
                }
                else
                {
                    printable = false;
                    sb.Append('.');
                }
            }

            return sb.ToString();
        }

        // -----------------------------------------------------------------
        // Request (PGN 59904)
        // -----------------------------------------------------------------

        /// <summary>
        /// Builds the three-byte payload of a Request (PGN 59904): the requested
        /// PGN, LEAST SIGNIFICANT BYTE FIRST.
        /// </summary>
        /// <remarks>
        /// Byte order is the trap here. Requesting DM2 (65227 = 0x00FECB) puts
        /// CB FE 00 on the wire, not 00 FE CB. Sending it big-endian asks for PGN
        /// 0x00CBFE, which no module answers.
        /// </remarks>
        /// <param name="pgn">The parameter group being requested, 0 to 0x3FFFF.</param>
        /// <returns>A three-byte payload.</returns>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="pgn"/> does not fit in 18 bits.</exception>
        public static byte[] BuildRequest(uint pgn)
        {
            if (pgn > J1939Id.MAX_PGN)
            {
                throw new ArgumentOutOfRangeException("pgn", pgn,
                    "A J1939 PGN is an 18-bit field (0..0x3FFFF).");
            }

            byte[] payload = new byte[REQUEST_PAYLOAD_LENGTH];
            payload[0] = (byte)(pgn & 0xFFu);
            payload[1] = (byte)((pgn >> 8) & 0xFFu);
            payload[2] = (byte)((pgn >> 16) & 0xFFu);
            return payload;
        }

        /// <summary>
        /// Builds a Request addressed to one specific ECU.
        /// </summary>
        /// <param name="pgn">The parameter group being requested.</param>
        /// <param name="sourceAddress">Address this tool transmits from, normally <see cref="J1939Pgn.ServiceTool1Address"/>.</param>
        /// <param name="destinationAddress">Address of the ECU being asked.</param>
        /// <returns>The Request message.</returns>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="pgn"/> does not fit in 18 bits.</exception>
        public static J1939Message BuildRequestMessage(uint pgn, byte sourceAddress, byte destinationAddress)
        {
            byte[] payload = BuildRequest(pgn);
            J1939Id id = J1939Id.Create(RequestPriority, J1939Pgn.Request, sourceAddress, destinationAddress);
            return J1939Message.FromHostClock(id, payload);
        }

        /// <summary>
        /// Builds a Request addressed to the global address, so every module that
        /// supports the parameter group answers.
        /// </summary>
        /// <param name="pgn">The parameter group being requested.</param>
        /// <param name="sourceAddress">Address this tool transmits from.</param>
        /// <returns>The Request message.</returns>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="pgn"/> does not fit in 18 bits.</exception>
        public static J1939Message BuildRequestMessage(uint pgn, byte sourceAddress)
        {
            return BuildRequestMessage(pgn, sourceAddress, J1939Pgn.GlobalAddress);
        }

        // -----------------------------------------------------------------
        // DM3 / DM11 - destructive
        // -----------------------------------------------------------------

        /// <summary>
        /// Prepares a DM3 request: erase all diagnostic data for PREVIOUSLY ACTIVE
        /// trouble codes on the addressed module.
        /// </summary>
        /// <remarks>
        /// <para>
        /// WARNING - DESTRUCTIVE AND IRREVERSIBLE. There is no undo and no read-back
        /// of what was erased. J1939-73 5.7.3 lists what goes: previously active
        /// DTCs, freeze frame data, oxygen sensor test data, system monitoring test
        /// status, on-board monitoring results, distance and engine minutes while
        /// the MIL was on, warm-ups since clear, and any manufacturer-specific
        /// reset the module chooses to perform. On an emissions-relevant machine
        /// this also resets readiness monitors, and the vehicle will not pass an
        /// inspection until a full drive cycle has re-run them.
        /// </para>
        /// <para>
        /// Clear fault history only after the fault has been diagnosed and the
        /// evidence recorded. Nothing here transmits: the returned
        /// <see cref="J1939ClearRequest"/> will not yield a frame until
        /// <see cref="J1939ClearRequest.ToMessage"/> is called with the operator's
        /// confirmation.
        /// </para>
        /// <para>
        /// The wire form is a Request (PGN 59904) naming PGN 65228 - DM3 is never
        /// transmitted as a DM3 frame by a tool. The module answers with the
        /// Acknowledgement group (PGN 59392): control byte 0 positive, 1 negative,
        /// 2 access denied, 3 cannot respond now.
        /// </para>
        /// </remarks>
        /// <param name="sourceAddress">Address this tool transmits from.</param>
        /// <param name="destinationAddress">
        /// The module to clear. Prefer a specific address: a global clear erases
        /// every module on the segment and returns no acknowledgement at all.
        /// </param>
        /// <returns>An unsent, confirmation-gated request.</returns>
        public static J1939ClearRequest BuildDm3ClearPreviouslyActiveDtcs(byte sourceAddress, byte destinationAddress)
        {
            return new J1939ClearRequest(
                "DM3 - clear previously active DTCs",
                J1939Pgn.Dm3,
                sourceAddress,
                destinationAddress,
                BuildClearWarning("previously active trouble codes and their freeze frame, monitor and distance data",
                    destinationAddress));
        }

        /// <summary>
        /// Prepares a DM11 request: erase all diagnostic data for ACTIVE trouble
        /// codes on the addressed module.
        /// </summary>
        /// <remarks>
        /// <para>
        /// WARNING - DESTRUCTIVE AND IRREVERSIBLE, AND WIDER IN EFFECT THAN DM3.
        /// J1939-73 5.7.11 requires emissions-related components to clear and reset
        /// diagnostic data for ALL active AND previously active DTCs in response to
        /// this request, so a DM11 on an emissions module wipes the fault history a
        /// technician may still need and resets readiness monitors. The machine
        /// will not pass an inspection until a drive cycle has re-run them.
        /// </para>
        /// <para>
        /// A live fault that has not been repaired will simply set again, so
        /// clearing before diagnosis destroys evidence and gains nothing. Nothing
        /// here transmits: the returned <see cref="J1939ClearRequest"/> will not
        /// yield a frame until <see cref="J1939ClearRequest.ToMessage"/> is called
        /// with the operator's confirmation.
        /// </para>
        /// <para>
        /// The wire form is a Request (PGN 59904) naming PGN 65235. The module
        /// answers with the Acknowledgement group (PGN 59392). J1939-73 requires
        /// the clear to be performed with the ignition on and the engine not
        /// running.
        /// </para>
        /// </remarks>
        /// <param name="sourceAddress">Address this tool transmits from.</param>
        /// <param name="destinationAddress">
        /// The module to clear. Prefer a specific address: a global clear erases
        /// every module on the segment and returns no acknowledgement at all.
        /// </param>
        /// <returns>An unsent, confirmation-gated request.</returns>
        public static J1939ClearRequest BuildDm11ClearActiveDtcs(byte sourceAddress, byte destinationAddress)
        {
            return new J1939ClearRequest(
                "DM11 - clear active DTCs",
                J1939Pgn.Dm11,
                sourceAddress,
                destinationAddress,
                BuildClearWarning("active trouble codes - and, on emissions-related modules, previously active codes and readiness monitors as well",
                    destinationAddress));
        }

        private static string BuildClearWarning(string what, byte destinationAddress)
        {
            StringBuilder sb = new StringBuilder(320);

            sb.Append("This permanently erases ");
            sb.Append(what);
            sb.Append(". It cannot be undone, and the erased history cannot be read back afterwards. ");
            sb.Append("On an emissions-relevant system it also resets readiness monitors, and the machine will fail inspection until a drive cycle has re-run them. ");
            sb.Append("Record the codes and freeze frames before clearing.");

            if (destinationAddress == J1939Pgn.GlobalAddress)
            {
                sb.Append(" THIS IS A GLOBAL REQUEST: every module on the segment will clear, and J1939-73 specifies that no acknowledgement is returned for a global clear - there will be no confirmation that it worked, or that anything heard it.");
            }

            return sb.ToString();
        }

        // -----------------------------------------------------------------
        // Helpers
        // -----------------------------------------------------------------

        private static bool IsFill(byte[] buffer, int offset, int count)
        {
            for (int i = 0; i < count; i++)
            {
                byte b = buffer[offset + i];
                if (b != 0xFF && b != 0x00)
                {
                    return false;
                }
            }

            return true;
        }

        private static byte[] Slice(byte[] buffer, int offset, int count)
        {
            byte[] result = new byte[count];
            Buffer.BlockCopy(buffer, offset, result, 0, count);
            return result;
        }
    }
}
