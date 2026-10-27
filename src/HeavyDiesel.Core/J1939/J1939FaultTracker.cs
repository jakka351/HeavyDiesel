// =============================================================================
//  J1939FaultTracker.cs - the live fault and lamp state of every ECU on a
//  J1939 bus, aged and snapshotted for a UI thread.
//
//  WHY THIS EXISTS
//  ---------------
//  DM1 (PGN 65226) is a broadcast. Every ECU sends its own, roughly every
//  second, with no coordination between them. There is no "read all faults"
//  request that returns one answer, so the coherent picture a technician needs
//  has to be assembled by a receiver that watches the bus over time. That is
//  this class.
//
//  STALENESS IS THE WHOLE PROBLEM (J1939-73 5.7.1)
//  -----------------------------------------------
//  DM1 is transmitted at 1 Hz while a fault is active. When the fault clears
//  the ECU sends one more DM1 with no codes and then stops. So the ABSENCE of
//  DM1 carries no information on its own - the ECU may have gone healthy, may
//  have been silenced by a DM13 broadcast-stop (PGN 57344 holds broadcasts for
//  up to 5 s), may have lost power, or the adapter may have dropped frames.
//  A cache that just keeps the last DM1 it saw will happily show a red stop
//  lamp thirty seconds after the lamp went out. That sends a fitter to strip a
//  machine that is fine.
//
//  So every stored report is aged against a caller-supplied clock, and three
//  states are kept strictly apart and never collapsed into "no faults":
//
//     Unheard        - no DM1 has EVER arrived from this address.
//     NoActiveFaults - a CURRENT DM1 arrived and it reports nothing active.
//     Stale          - a DM1 arrived once but is now too old to believe.
//
//  A fourth state, Undecodable, covers a DM1 that arrived but could not be
//  parsed - also not the same thing as "no faults".
//
//  CLOCK OWNERSHIP
//  ---------------
//  This class has no clock of its own, deliberately. J1939Message.TimestampMs
//  may come from adapter hardware on an epoch that has nothing to do with
//  Environment.TickCount, and silently mixing the two produces ages that are
//  wrong by minutes. The caller supplies every time value, and the tracker
//  flags it when the two clocks it is handed disagree rather than guessing a
//  conversion.
//
//  THREADING
//  ---------
//  The RP1210/J2534 pump thread calls OnDtcReport. The WinForms thread calls
//  Evaluate and GetSnapshot on a timer. All state is behind one lock, and
//  every method that returns data returns freshly built objects - the UI can
//  never enumerate a collection that the pump is mutating. Caller-supplied
//  resolvers are invoked AFTER the lock is released, so foreign code can never
//  be run while the pump is blocked.
//
//  Nothing here opens a file, touches the registry or reaches the network.
//  ASCII only, explicit property bodies - matches the rest of the J1939 stack.
// =============================================================================

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;

namespace HeavyDiesel.J1939
{
    /// <summary>
    /// The active-fault state of one ECU, as far as received DM1 traffic can
    /// establish it. These values are deliberately not collapsible: "we have
    /// not heard", "it says it is clear" and "what it said has expired" are
    /// three different answers and only one of them is good news.
    /// </summary>
    public enum J1939EcuFaultState
    {
        /// <summary>
        /// No DM1 has ever been received from this source address. Nothing at
        /// all is known about its active faults. This is NOT "no faults".
        /// An entry can be in this state and still hold DM2 data, because DM2
        /// is an on-request message and can arrive without any DM1.
        /// </summary>
        Unheard = 0,

        /// <summary>
        /// A DM1 arrived within <see cref="J1939FaultTracker.StaleAfterMs"/>
        /// and it reported no active codes and no lamp commanded on. This is
        /// the only value that means the ECU is currently reporting itself
        /// healthy.
        /// </summary>
        NoActiveFaults = 1,

        /// <summary>
        /// A current DM1 reports at least one active code, or commands at
        /// least one lamp on.
        /// </summary>
        ActiveFaults = 2,

        /// <summary>
        /// A DM1 was received but is now older than
        /// <see cref="J1939FaultTracker.StaleAfterMs"/>. The codes and lamps
        /// held for this ECU are historical. The current state is unknown -
        /// the ECU may have gone healthy, gone quiet, or been silenced by DM13.
        /// </summary>
        Stale = 3,

        /// <summary>
        /// A current DM1 arrived but could not be decoded (too short to carry
        /// lamp bytes, or a trailing fragment that is not a whole DTC) and
        /// yielded no codes and no lamp. Reporting this as "no active faults"
        /// would be a fabricated all-clear.
        /// </summary>
        Undecodable = 4
    }

    /// <summary>
    /// Immutable snapshot of one ECU's fault and lamp state at a point in time.
    /// </summary>
    /// <remarks>
    /// Every collection on this type is a private copy taken while the tracker
    /// held its lock, so it is safe to enumerate from the UI thread for as long
    /// as the caller keeps it. The <see cref="J1939Dtc"/> and
    /// <see cref="J1939LampStatus"/> instances inside are shared, which is safe
    /// because both are immutable once parsed. <see cref="Name"/> is a clone,
    /// because <see cref="J1939Name"/> is not.
    /// </remarks>
    public sealed class J1939EcuFaultEntry
    {
        private static readonly ReadOnlyCollection<J1939Dtc> EmptyDtcs =
            new ReadOnlyCollection<J1939Dtc>(new J1939Dtc[0]);

        private static readonly ReadOnlyCollection<string> EmptyLines =
            new ReadOnlyCollection<string>(new string[0]);

        private readonly byte _sourceAddress;
        private readonly J1939Name _name;

        private readonly J1939DtcReport _dm1;
        private readonly double _dm1AgeMs;
        private readonly bool _dm1Stale;
        private readonly int _dm1ReportCount;

        private readonly J1939DtcReport _dm2;
        private readonly double _dm2AgeMs;
        private readonly bool _dm2Stale;
        private readonly int _dm2ReportCount;

        private readonly ReadOnlyCollection<J1939Dtc> _activeDtcs;
        private readonly ReadOnlyCollection<string> _activeDescriptions;
        private readonly ReadOnlyCollection<J1939Dtc> _previouslyActiveDtcs;
        private readonly ReadOnlyCollection<string> _previouslyActiveDescriptions;

        private readonly J1939EcuFaultState _state;

        internal J1939EcuFaultEntry(byte sourceAddress, J1939Name name,
            J1939DtcReport dm1, double dm1AgeMs, bool dm1Stale, int dm1ReportCount,
            J1939DtcReport dm2, double dm2AgeMs, bool dm2Stale, int dm2ReportCount,
            Func<uint, string> spnNameResolver)
        {
            _sourceAddress = sourceAddress;
            _name = name;

            _dm1 = dm1;
            _dm1AgeMs = dm1AgeMs;
            _dm1Stale = dm1Stale;
            _dm1ReportCount = dm1ReportCount;

            _dm2 = dm2;
            _dm2AgeMs = dm2AgeMs;
            _dm2Stale = dm2Stale;
            _dm2ReportCount = dm2ReportCount;

            _activeDtcs = CopyDtcs(dm1);
            _activeDescriptions = DescribeAll(_activeDtcs, spnNameResolver);
            _previouslyActiveDtcs = CopyDtcs(dm2);
            _previouslyActiveDescriptions = DescribeAll(_previouslyActiveDtcs, spnNameResolver);

            _state = ClassifyActiveState(dm1, dm1Stale);
        }

        /// <summary>
        /// Builds a placeholder entry for an address nothing has been received
        /// from, so a caller asking about an unknown ECU gets an explicit
        /// <see cref="J1939EcuFaultState.Unheard"/> instead of a zeroed record
        /// that reads as healthy.
        /// </summary>
        /// <param name="sourceAddress">The address that was asked about.</param>
        /// <returns>An entry whose state is <see cref="J1939EcuFaultState.Unheard"/>.</returns>
        public static J1939EcuFaultEntry CreateUnheard(byte sourceAddress)
        {
            return new J1939EcuFaultEntry(sourceAddress, null,
                null, double.NaN, false, 0,
                null, double.NaN, false, 0,
                null);
        }

        /// <summary>Source address of the transmitting ECU, 0..253.</summary>
        public byte SourceAddress
        {
            get { return _sourceAddress; }
        }

        /// <summary>
        /// NAME claimed by this address, or null when no address claim has been
        /// observed. A cloned copy - mutating it does not affect the claim map.
        /// </summary>
        public J1939Name Name
        {
            get { return _name; }
        }

        /// <summary>True when an address claim supplied a NAME for this address.</summary>
        public bool NameKnown
        {
            get { return _name != null; }
        }

        /// <summary>Active-fault state. Read this before anything else on this type.</summary>
        public J1939EcuFaultState ActiveState
        {
            get { return _state; }
        }

        /// <summary>True when any DM1 or DM2 has ever been received from this address.</summary>
        public bool HasBeenHeard
        {
            get { return _dm1 != null || _dm2 != null; }
        }

        // -----------------------------------------------------------------
        // DM1 - active faults
        // -----------------------------------------------------------------

        /// <summary>The most recent DM1, or null when none has been received. Immutable.</summary>
        public J1939DtcReport Dm1Report
        {
            get { return _dm1; }
        }

        /// <summary>True when at least one DM1 has been received from this address.</summary>
        public bool HasDm1
        {
            get { return _dm1 != null; }
        }

        /// <summary>
        /// Milliseconds between the arrival of the newest DM1 and the snapshot
        /// reference time. <see cref="double.NaN"/> when no DM1 has ever been
        /// received - NaN rather than 0, because 0 would render as a fresh
        /// reading that never happened.
        /// </summary>
        public double Dm1AgeMs
        {
            get { return _dm1AgeMs; }
        }

        /// <summary>
        /// True when the newest DM1 is older than
        /// <see cref="J1939FaultTracker.StaleAfterMs"/>. When this is set the
        /// codes and lamps below are what the ECU LAST said, not what it is
        /// saying now.
        /// </summary>
        public bool Dm1Stale
        {
            get { return _dm1Stale; }
        }

        /// <summary>How many DM1 messages have been received from this address since the last reset.</summary>
        public int Dm1ReportCount
        {
            get { return _dm1ReportCount; }
        }

        /// <summary>
        /// Lamp status from the newest DM1, or null when no DM1 has been
        /// received. Null is deliberate: there is no honest "all lamps off"
        /// value to substitute for a message that never arrived.
        /// </summary>
        public J1939LampStatus Dm1Lamps
        {
            get { return _dm1 == null ? null : _dm1.Lamps; }
        }

        /// <summary>
        /// Codes carried by the newest DM1. Empty when none was received.
        /// When <see cref="Dm1Stale"/> is true these are historical.
        /// </summary>
        public ReadOnlyCollection<J1939Dtc> ActiveDtcs
        {
            get { return _activeDtcs; }
        }

        /// <summary>
        /// One rendered line per entry in <see cref="ActiveDtcs"/>, resolved
        /// through the tracker's SPN name resolver. An SPN the resolver does
        /// not know renders as "no definition loaded" with its standardised FMI
        /// text, never as a named parameter.
        /// </summary>
        public ReadOnlyCollection<string> ActiveDtcDescriptions
        {
            get { return _activeDescriptions; }
        }

        /// <summary>Count of <see cref="ActiveDtcs"/>, regardless of staleness.</summary>
        public int ActiveDtcCount
        {
            get { return _activeDtcs.Count; }
        }

        // -----------------------------------------------------------------
        // DM2 - previously active faults
        // -----------------------------------------------------------------

        /// <summary>The most recent DM2, or null when none has been received. Immutable.</summary>
        public J1939DtcReport Dm2Report
        {
            get { return _dm2; }
        }

        /// <summary>True when at least one DM2 has been received from this address.</summary>
        public bool HasDm2
        {
            get { return _dm2 != null; }
        }

        /// <summary>
        /// Age of the newest DM2 in milliseconds, or <see cref="double.NaN"/>
        /// when none has been received.
        /// </summary>
        public double Dm2AgeMs
        {
            get { return _dm2AgeMs; }
        }

        /// <summary>
        /// True when the newest DM2 is older than
        /// <see cref="J1939FaultTracker.Dm2StaleAfterMs"/>. DM2 is an
        /// on-request message, so this is an "ask again" threshold, not a
        /// missed-broadcast one.
        /// </summary>
        public bool Dm2Stale
        {
            get { return _dm2Stale; }
        }

        /// <summary>How many DM2 messages have been received from this address since the last reset.</summary>
        public int Dm2ReportCount
        {
            get { return _dm2ReportCount; }
        }

        /// <summary>Lamp status from the newest DM2, or null when none has been received.</summary>
        public J1939LampStatus Dm2Lamps
        {
            get { return _dm2 == null ? null : _dm2.Lamps; }
        }

        /// <summary>Codes carried by the newest DM2. Empty when none was received.</summary>
        public ReadOnlyCollection<J1939Dtc> PreviouslyActiveDtcs
        {
            get { return _previouslyActiveDtcs; }
        }

        /// <summary>One rendered line per entry in <see cref="PreviouslyActiveDtcs"/>.</summary>
        public ReadOnlyCollection<string> PreviouslyActiveDtcDescriptions
        {
            get { return _previouslyActiveDescriptions; }
        }

        /// <summary>Count of <see cref="PreviouslyActiveDtcs"/>.</summary>
        public int PreviouslyActiveDtcCount
        {
            get { return _previouslyActiveDtcs.Count; }
        }

        // -----------------------------------------------------------------
        // Lamps
        // -----------------------------------------------------------------

        /// <summary>
        /// Worst lamp severity the newest DM1 actually reported, whether or not
        /// it is still current. Use this only alongside <see cref="Dm1Stale"/>;
        /// for a live indicator use <see cref="EffectiveLampSeverity"/>.
        /// </summary>
        public J1939LampSeverity ReportedLampSeverity
        {
            get { return _dm1 == null ? J1939LampSeverity.None : _dm1.Lamps.Severity; }
        }

        /// <summary>
        /// Lamp severity that may be shown as CURRENT. Falls back to
        /// <see cref="J1939LampSeverity.None"/> when the DM1 is stale or
        /// missing, because a lamp that has not been re-asserted for several
        /// seconds cannot be claimed to be lit. Absence of a lit lamp here is
        /// not evidence of health - check <see cref="ActiveState"/> for that.
        /// </summary>
        public J1939LampSeverity EffectiveLampSeverity
        {
            get
            {
                if (_dm1 == null || _dm1Stale)
                {
                    return J1939LampSeverity.None;
                }

                return _dm1.Lamps.Severity;
            }
        }

        /// <summary>
        /// True when a current DM1 commands a lamp on but carries no codes.
        /// Real and worth showing: the dash is lit and the module is not saying
        /// why, which is a different job from chasing a listed code.
        /// </summary>
        public bool LampOnWithoutCodes
        {
            get
            {
                return _dm1 != null && !_dm1Stale && _dm1.Lamps.AnyLampOn && _activeDtcs.Count == 0;
            }
        }

        /// <summary>Never-null one-line lamp summary, safe to bind directly to a grid cell.</summary>
        public string LampHeadline
        {
            get
            {
                if (_dm1 == null)
                {
                    return "No DM1 received - lamp state unknown";
                }

                if (_dm1Stale)
                {
                    return string.Format(CultureInfo.InvariantCulture,
                        "DM1 stale ({0}) - lamp state unknown; last reported: {1}",
                        FormatAge(_dm1AgeMs), _dm1.Lamps.Headline);
                }

                return _dm1.Lamps.Headline;
            }
        }

        // -----------------------------------------------------------------
        // Degradation
        // -----------------------------------------------------------------

        /// <summary>
        /// True when any code held for this ECU was decoded with conversion
        /// method 1, whose SPN is a best-supported reading rather than a
        /// certainty (J1939-73 5.7.1.11).
        /// </summary>
        public bool AnyConversionUncertain
        {
            get
            {
                if (_dm1 != null && _dm1.AnyConversionUncertain) return true;
                if (_dm2 != null && _dm2.AnyConversionUncertain) return true;
                return false;
            }
        }

        /// <summary>True when the newest DM1 or DM2 could not be fully decoded.</summary>
        public bool IsMalformed
        {
            get
            {
                if (_dm1 != null && _dm1.IsMalformed) return true;
                if (_dm2 != null && _dm2.IsMalformed) return true;
                return false;
            }
        }

        /// <summary>Why a held report could not be fully decoded, or null when both were well formed.</summary>
        public string MalformedReason
        {
            get
            {
                if (_dm1 != null && _dm1.IsMalformed) return _dm1.MalformedReason;
                if (_dm2 != null && _dm2.IsMalformed) return _dm2.MalformedReason;
                return null;
            }
        }

        // -----------------------------------------------------------------
        // Rendering
        // -----------------------------------------------------------------

        /// <summary>
        /// One line for a bus-overview row, for example
        /// "SA 0x00 NAME 0x800000FF00000001: 3 active code(s) - RED STOP lamp on
        /// - ECU is requesting the vehicle be stopped (DM1 0.4 s old)".
        /// </summary>
        public string Headline
        {
            get
            {
                StringBuilder sb = new StringBuilder(200);
                sb.AppendFormat(CultureInfo.InvariantCulture, "SA 0x{0:X2}", _sourceAddress);

                if (_name != null)
                {
                    sb.AppendFormat(CultureInfo.InvariantCulture, " NAME 0x{0:X16}", _name.Value);
                }

                sb.Append(": ");

                switch (_state)
                {
                    case J1939EcuFaultState.Unheard:
                        sb.Append("no DM1 received - active fault state UNKNOWN");
                        break;

                    case J1939EcuFaultState.Stale:
                        sb.AppendFormat(CultureInfo.InvariantCulture,
                            "DM1 STALE ({0} since last broadcast) - active fault state UNKNOWN; last reported {1} code(s), {2}",
                            FormatAge(_dm1AgeMs), _activeDtcs.Count, _dm1.Lamps.Headline);
                        break;

                    case J1939EcuFaultState.Undecodable:
                        sb.AppendFormat(CultureInfo.InvariantCulture,
                            "DM1 received but not decodable - {0}",
                            string.IsNullOrEmpty(MalformedReason) ? "no lamp bytes and no codes" : MalformedReason);
                        break;

                    case J1939EcuFaultState.ActiveFaults:
                        sb.AppendFormat(CultureInfo.InvariantCulture,
                            "{0} active code(s) - {1} (DM1 {2} old)",
                            _activeDtcs.Count, _dm1.Lamps.Headline, FormatAge(_dm1AgeMs));
                        break;

                    default:
                        sb.AppendFormat(CultureInfo.InvariantCulture,
                            "no active faults reported (DM1 {0} old)", FormatAge(_dm1AgeMs));
                        break;
                }

                if (_dm2 != null)
                {
                    sb.AppendFormat(CultureInfo.InvariantCulture,
                        "; {0} previously active code(s) from DM2 {1} old{2}",
                        _previouslyActiveDtcs.Count, FormatAge(_dm2AgeMs), _dm2Stale ? " (stale)" : string.Empty);
                }

                if (LampOnWithoutCodes)
                {
                    sb.Append(" [lamp commanded on with no code listed]");
                }

                if (AnyConversionUncertain)
                {
                    sb.Append(" [some SPNs uncertain - legacy CM=1 conversion]");
                }

                return sb.ToString();
            }
        }

        /// <summary>Headline followed by one line per code.</summary>
        /// <returns>The formatted entry.</returns>
        public override string ToString()
        {
            StringBuilder sb = new StringBuilder(320);
            sb.Append(Headline);

            for (int i = 0; i < _activeDescriptions.Count; i++)
            {
                sb.Append(Environment.NewLine);
                sb.Append("  ACTIVE      ");
                sb.Append(_activeDescriptions[i]);
            }

            for (int i = 0; i < _previouslyActiveDescriptions.Count; i++)
            {
                sb.Append(Environment.NewLine);
                sb.Append("  PREVIOUSLY  ");
                sb.Append(_previouslyActiveDescriptions[i]);
            }

            return sb.ToString();
        }

        /// <summary>
        /// Renders an age for a technician: milliseconds below a second,
        /// seconds above it, and an explicit "age unknown" for NaN so a missing
        /// measurement never prints as a number.
        /// </summary>
        /// <param name="ms">Age in milliseconds, or <see cref="double.NaN"/>.</param>
        /// <returns>The formatted age.</returns>
        public static string FormatAge(double ms)
        {
            if (double.IsNaN(ms))
            {
                return "age unknown";
            }

            if (ms < 1000.0)
            {
                return string.Format(CultureInfo.InvariantCulture, "{0:F0} ms", ms);
            }

            return string.Format(CultureInfo.InvariantCulture, "{0:F1} s", ms / 1000.0);
        }

        private static J1939EcuFaultState ClassifyActiveState(J1939DtcReport dm1, bool stale)
        {
            if (dm1 == null)
            {
                return J1939EcuFaultState.Unheard;
            }

            if (stale)
            {
                return J1939EcuFaultState.Stale;
            }

            bool anyLampOn = dm1.Lamps.AnyLampOn;

            // A malformed payload that yielded neither a code nor a lamp told us
            // nothing. Falling through to NoActiveFaults here would turn a parse
            // failure into an all-clear.
            if (dm1.IsMalformed && !dm1.HasFaults && !anyLampOn)
            {
                return J1939EcuFaultState.Undecodable;
            }

            if (dm1.HasFaults || anyLampOn)
            {
                return J1939EcuFaultState.ActiveFaults;
            }

            return J1939EcuFaultState.NoActiveFaults;
        }

        private static ReadOnlyCollection<J1939Dtc> CopyDtcs(J1939DtcReport report)
        {
            if (report == null || report.Dtcs.Count == 0)
            {
                return EmptyDtcs;
            }

            J1939Dtc[] copy = new J1939Dtc[report.Dtcs.Count];
            report.Dtcs.CopyTo(copy, 0);
            return new ReadOnlyCollection<J1939Dtc>(copy);
        }

        private static ReadOnlyCollection<string> DescribeAll(ReadOnlyCollection<J1939Dtc> dtcs,
            Func<uint, string> spnNameResolver)
        {
            if (dtcs.Count == 0)
            {
                return EmptyLines;
            }

            string[] lines = new string[dtcs.Count];
            for (int i = 0; i < dtcs.Count; i++)
            {
                lines[i] = dtcs[i].Describe(spnNameResolver);
            }

            return new ReadOnlyCollection<string>(lines);
        }
    }

    /// <summary>
    /// Immutable snapshot of the fault and lamp state of every ECU the tracker
    /// has heard from, plus the one-line headline a technician reads first.
    /// </summary>
    public sealed class J1939BusFaultSnapshot
    {
        private readonly ReadOnlyCollection<J1939EcuFaultEntry> _entries;
        private readonly double _referenceTimeMs;
        private readonly bool _ageReferenceIsLastReport;
        private readonly double _staleAfterMs;
        private readonly double _dm2StaleAfterMs;
        private readonly int _unattributedReportCount;
        private readonly string _timebaseNote;

        private readonly J1939LampSeverity _worstLampSeverity;
        private readonly int _totalActiveDtcCount;
        private readonly int _totalPreviouslyActiveDtcCount;
        private readonly int _reportingEcuCount;
        private readonly int _staleEcuCount;
        private readonly int _faultedEcuCount;
        private readonly int _undecodableEcuCount;
        private readonly bool _anyConversionUncertain;
        private readonly bool _anyMalformed;
        private readonly bool _anyLampOnWithoutCodes;
        private readonly string _degradedNotice;

        internal J1939BusFaultSnapshot(IList<J1939EcuFaultEntry> entries, double referenceTimeMs,
            bool ageReferenceIsLastReport, double staleAfterMs, double dm2StaleAfterMs,
            int unattributedReportCount, string timebaseNote)
        {
            J1939EcuFaultEntry[] copy = new J1939EcuFaultEntry[entries == null ? 0 : entries.Count];
            for (int i = 0; i < copy.Length; i++)
            {
                copy[i] = entries[i];
            }

            _entries = new ReadOnlyCollection<J1939EcuFaultEntry>(copy);
            _referenceTimeMs = referenceTimeMs;
            _ageReferenceIsLastReport = ageReferenceIsLastReport;
            _staleAfterMs = staleAfterMs;
            _dm2StaleAfterMs = dm2StaleAfterMs;
            _unattributedReportCount = unattributedReportCount;
            _timebaseNote = timebaseNote;

            J1939LampSeverity worst = J1939LampSeverity.None;

            for (int i = 0; i < copy.Length; i++)
            {
                J1939EcuFaultEntry entry = copy[i];

                if (entry.ActiveState == J1939EcuFaultState.Stale)
                {
                    _staleEcuCount++;
                }

                if (entry.ActiveState == J1939EcuFaultState.Undecodable)
                {
                    _undecodableEcuCount++;
                }

                // Only ECUs whose DM1 is still current may contribute to the
                // headline or to the totals. A stale ECU contributes UNKNOWN,
                // which is counted separately and must never be rolled into a
                // fault count or a lamp colour.
                if (entry.HasDm1 && !entry.Dm1Stale)
                {
                    _reportingEcuCount++;
                    _totalActiveDtcCount += entry.ActiveDtcCount;

                    if (entry.EffectiveLampSeverity > worst)
                    {
                        worst = entry.EffectiveLampSeverity;
                    }

                    if (entry.ActiveState == J1939EcuFaultState.ActiveFaults)
                    {
                        _faultedEcuCount++;
                    }
                }

                if (entry.HasDm2 && !entry.Dm2Stale)
                {
                    _totalPreviouslyActiveDtcCount += entry.PreviouslyActiveDtcCount;
                }

                if (entry.AnyConversionUncertain) _anyConversionUncertain = true;
                if (entry.IsMalformed) _anyMalformed = true;
                if (entry.LampOnWithoutCodes) _anyLampOnWithoutCodes = true;
            }

            _worstLampSeverity = worst;
            _degradedNotice = BuildDegradedNotice();
        }

        /// <summary>
        /// One entry per source address heard from, ordered by address. A
        /// private copy: safe to enumerate on the UI thread.
        /// </summary>
        public ReadOnlyCollection<J1939EcuFaultEntry> Entries
        {
            get { return _entries; }
        }

        /// <summary>
        /// False when no DM1 and no DM2 has been received from anything. When
        /// this is false the machine's fault state is UNKNOWN and must not be
        /// presented as clear.
        /// </summary>
        public bool AnythingReceived
        {
            get { return _entries.Count > 0; }
        }

        /// <summary>Number of source addresses with any held report.</summary>
        public int EcuCount
        {
            get { return _entries.Count; }
        }

        /// <summary>Number of ECUs whose DM1 is current, and therefore whose state is actually known.</summary>
        public int ReportingEcuCount
        {
            get { return _reportingEcuCount; }
        }

        /// <summary>Number of ECUs whose DM1 has expired. Their state is unknown, not clear.</summary>
        public int StaleEcuCount
        {
            get { return _staleEcuCount; }
        }

        /// <summary>Number of ECUs currently reporting at least one active code or lamp.</summary>
        public int FaultedEcuCount
        {
            get { return _faultedEcuCount; }
        }

        /// <summary>Number of ECUs whose current DM1 could not be decoded at all.</summary>
        public int UndecodableEcuCount
        {
            get { return _undecodableEcuCount; }
        }

        /// <summary>
        /// Worst lamp commanded by any ECU whose DM1 is current. Stale ECUs are
        /// excluded, so a lamp that stopped being broadcast stops driving this
        /// value. DM2 lamps are excluded too: DM2 describes previously active
        /// faults and says nothing about what is lit now.
        /// </summary>
        public J1939LampSeverity WorstLampSeverity
        {
            get { return _worstLampSeverity; }
        }

        /// <summary>Total active codes across all ECUs with a current DM1. Stale ECUs contribute nothing.</summary>
        public int TotalActiveDtcCount
        {
            get { return _totalActiveDtcCount; }
        }

        /// <summary>Total previously active codes across all ECUs with a non-stale DM2.</summary>
        public int TotalPreviouslyActiveDtcCount
        {
            get { return _totalPreviouslyActiveDtcCount; }
        }

        /// <summary>The clock value every age in this snapshot was measured against.</summary>
        public double ReferenceTimeMs
        {
            get { return _referenceTimeMs; }
        }

        /// <summary>
        /// True when the ages were measured against the newest received report
        /// rather than a caller-supplied "now". Ages then understate: nothing
        /// can go stale while the caller is not ticking the tracker. Call
        /// <see cref="J1939FaultTracker.Evaluate"/> on a UI timer to clear this.
        /// </summary>
        public bool AgeReferenceIsLastReport
        {
            get { return _ageReferenceIsLastReport; }
        }

        /// <summary>DM1 staleness threshold this snapshot was evaluated with, in milliseconds.</summary>
        public double StaleAfterMs
        {
            get { return _staleAfterMs; }
        }

        /// <summary>DM2 staleness threshold this snapshot was evaluated with, in milliseconds. Zero means DM2 is never marked stale.</summary>
        public double Dm2StaleAfterMs
        {
            get { return _dm2StaleAfterMs; }
        }

        /// <summary>Reports discarded because they carried no source address and could not be attributed to an ECU.</summary>
        public int UnattributedReportCount
        {
            get { return _unattributedReportCount; }
        }

        /// <summary>True when at least one held code carries an uncertain CM=1 SPN.</summary>
        public bool AnyConversionUncertain
        {
            get { return _anyConversionUncertain; }
        }

        /// <summary>True when at least one held report could not be fully decoded.</summary>
        public bool AnyMalformed
        {
            get { return _anyMalformed; }
        }

        /// <summary>True when at least one ECU commands a lamp on without listing a code.</summary>
        public bool AnyLampOnWithoutCodes
        {
            get { return _anyLampOnWithoutCodes; }
        }

        /// <summary>Description of a detected clock problem, or null when the timebase looked consistent.</summary>
        public string TimebaseNote
        {
            get { return _timebaseNote; }
        }

        /// <summary>True when the tracker was handed inconsistent time values and the ages here may be wrong.</summary>
        public bool TimebaseSuspect
        {
            get { return !string.IsNullOrEmpty(_timebaseNote); }
        }

        /// <summary>
        /// Everything about this snapshot that a technician needs warning
        /// about - stale ECUs, undecodable reports, uncertain SPNs, a suspect
        /// clock - or null when the picture is clean.
        /// </summary>
        public string DegradedNotice
        {
            get { return _degradedNotice; }
        }

        /// <summary>
        /// The single line for the top of the screen. Distinguishes "nothing
        /// received", "nothing current" and "nothing wrong", because they look
        /// identical in a naive fault count and mean completely different
        /// things on a machine.
        /// </summary>
        public string Headline
        {
            get
            {
                if (_entries.Count == 0)
                {
                    return "No DM1 or DM2 received - fault state UNKNOWN (this is not the same as no faults)";
                }

                if (_reportingEcuCount == 0)
                {
                    return string.Format(CultureInfo.InvariantCulture,
                        "No current DM1 from any of {0} known ECU(s) - fault state UNKNOWN (nothing newer than {1:F0} ms)",
                        _entries.Count, _staleAfterMs);
                }

                StringBuilder sb = new StringBuilder(200);

                switch (_worstLampSeverity)
                {
                    case J1939LampSeverity.StopNow:
                        sb.Append("RED STOP lamp on - stop the machine");
                        break;
                    case J1939LampSeverity.Warning:
                        sb.Append("AMBER WARNING lamp on");
                        break;
                    case J1939LampSeverity.Protect:
                        sb.Append("PROTECT lamp on - system outside its protected range");
                        break;
                    case J1939LampSeverity.Emissions:
                        sb.Append("MALFUNCTION INDICATOR lamp on - emissions fault");
                        break;
                    default:
                        sb.Append(_totalActiveDtcCount > 0 ? "No lamp commanded" : "No faults reported");
                        break;
                }

                sb.AppendFormat(CultureInfo.InvariantCulture,
                    " - {0} active code(s) across {1} of {2} reporting ECU(s)",
                    _totalActiveDtcCount, _faultedEcuCount, _reportingEcuCount);

                if (_staleEcuCount > 0)
                {
                    sb.AppendFormat(CultureInfo.InvariantCulture,
                        "; {0} ECU(s) stale - their state is UNKNOWN", _staleEcuCount);
                }

                return sb.ToString();
            }
        }

        /// <summary>
        /// Finds the entry for a source address in this snapshot.
        /// </summary>
        /// <param name="sourceAddress">Address to look up.</param>
        /// <param name="entry">The entry, or null when this snapshot holds nothing for that address.</param>
        /// <returns>True when an entry was found.</returns>
        public bool TryGetEntry(byte sourceAddress, out J1939EcuFaultEntry entry)
        {
            for (int i = 0; i < _entries.Count; i++)
            {
                if (_entries[i].SourceAddress == sourceAddress)
                {
                    entry = _entries[i];
                    return true;
                }
            }

            entry = null;
            return false;
        }

        /// <summary>Headline, degraded notice and one block per ECU.</summary>
        /// <returns>The formatted snapshot.</returns>
        public override string ToString()
        {
            StringBuilder sb = new StringBuilder(512);
            sb.Append(Headline);

            if (!string.IsNullOrEmpty(_degradedNotice))
            {
                sb.Append(Environment.NewLine);
                sb.Append("DEGRADED: ");
                sb.Append(_degradedNotice);
            }

            for (int i = 0; i < _entries.Count; i++)
            {
                sb.Append(Environment.NewLine);
                sb.Append(_entries[i].ToString());
            }

            return sb.ToString();
        }

        private string BuildDegradedNotice()
        {
            List<string> parts = new List<string>(6);

            if (_staleEcuCount > 0)
            {
                parts.Add(string.Format(CultureInfo.InvariantCulture,
                    "{0} ECU(s) have not broadcast DM1 within {1:F0} ms - their fault state is unknown, not clear",
                    _staleEcuCount, _staleAfterMs));
            }

            if (_undecodableEcuCount > 0)
            {
                parts.Add(string.Format(CultureInfo.InvariantCulture,
                    "{0} ECU(s) sent a DM1 that could not be decoded", _undecodableEcuCount));
            }

            if (_anyMalformed)
            {
                parts.Add("at least one held report was only partly decodable");
            }

            if (_anyConversionUncertain)
            {
                parts.Add("at least one SPN is uncertain (legacy CM=1 conversion, J1939-73 5.7.1.11)");
            }

            if (_anyLampOnWithoutCodes)
            {
                parts.Add("at least one ECU commands a lamp on without listing a code");
            }

            if (_unattributedReportCount > 0)
            {
                parts.Add(string.Format(CultureInfo.InvariantCulture,
                    "{0} report(s) discarded - no source address, could not be attributed to an ECU",
                    _unattributedReportCount));
            }

            if (!string.IsNullOrEmpty(_timebaseNote))
            {
                parts.Add(_timebaseNote);
            }

            if (_ageReferenceIsLastReport && _entries.Count > 0)
            {
                parts.Add("ages are measured to the last received message, not to now - nothing can expire until Evaluate is called");
            }

            if (parts.Count == 0)
            {
                return null;
            }

            StringBuilder sb = new StringBuilder(256);
            for (int i = 0; i < parts.Count; i++)
            {
                if (i > 0) sb.Append("; ");
                sb.Append(parts[i]);
            }

            return sb.ToString();
        }
    }

    /// <summary>
    /// Collects DM1 and DM2 reports from every ECU on a J1939 bus, ages them
    /// against a caller-supplied clock, and hands the UI a coherent snapshot.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Wiring: the receive pump parses each DM1/DM2 with
    /// <see cref="J1939Diagnostics.ParseDm1(byte[],byte)"/> or
    /// <see cref="J1939Diagnostics.ParseDm2(byte[],byte)"/> and calls
    /// <see cref="OnDtcReport"/> with the frame's
    /// <see cref="J1939Message.TimestampMs"/>. A WinForms timer calls
    /// <see cref="Evaluate"/> with a value from the SAME clock and marshals the
    /// returned snapshot onto the UI with BeginInvoke.
    /// </para>
    /// <para>
    /// Both time values must come from one clock. Feeding adapter hardware
    /// timestamps to <see cref="OnDtcReport"/> and Environment.TickCount to
    /// <see cref="Evaluate"/> mixes two unrelated epochs; the tracker detects
    /// the disagreement and reports it through
    /// <see cref="J1939BusFaultSnapshot.TimebaseNote"/> rather than inventing a
    /// conversion between them.
    /// </para>
    /// </remarks>
    public sealed class J1939FaultTracker
    {
        /// <summary>
        /// Default DM1 staleness threshold, in milliseconds.
        /// </summary>
        /// <remarks>
        /// J1939-73 5.7.1 puts the DM1 broadcast period at 1 s while any DTC is
        /// active, so three whole missed periods is the smallest gap that
        /// cannot be explained by ordinary jitter. Two would be too tight: a
        /// DM1 carrying more than one DTC goes out over a J1939-21 BAM, and the
        /// mandatory 50 ms inter-packet delay plus adapter queueing routinely
        /// pushes the observed period past 1.2 s on a loaded bus. Three periods
        /// also sits comfortably under the 5 s DM13 broadcast hold, so a tool
        /// that has silenced the bus with DM13 sees "stale" - which correctly
        /// reads as unknown - well before the hold expires.
        /// </remarks>
        public const double DEFAULT_STALE_AFTER_MS = 3000.0;

        /// <summary>
        /// Default DM2 staleness threshold, in milliseconds.
        /// </summary>
        /// <remarks>
        /// DM2 is an on-request message (J1939-73 5.7.2), not a 1 Hz broadcast,
        /// so there is no missed period to count and the DM1 threshold does not
        /// apply - using it would mark every DM2 stale three seconds after it
        /// was requested. Thirty seconds is an "ask again" threshold instead: a
        /// previously-active list older than that has probably been invalidated
        /// by a DM3 clear, a key cycle, or a fault ageing from active to
        /// previously active. Set to zero to age DM2 without ever marking it
        /// stale.
        /// </remarks>
        public const double DEFAULT_DM2_STALE_AFTER_MS = 30000.0;

        /// <summary>
        /// How far a time value may run backwards before the timebase is called
        /// into question, in milliseconds. Frames do not reorder by a second,
        /// and neither does a sane UI timer, so a regression this large means
        /// two different clocks or a wrapped adapter counter.
        /// </summary>
        private const double CLOCK_REGRESSION_TOLERANCE_MS = 1000.0;

        private readonly object _sync = new object();
        private readonly Dictionary<byte, EcuRecord> _records = new Dictionary<byte, EcuRecord>();

        private double _staleAfterMs = DEFAULT_STALE_AFTER_MS;
        private double _dm2StaleAfterMs = DEFAULT_DM2_STALE_AFTER_MS;

        private bool _hasReport;
        private double _newestReportMs;
        private bool _hasEvaluated;
        private double _evaluatedNowMs;

        private int _unattributedReportCount;
        private string _timebaseNote;

        private Func<uint, string> _spnNameResolver;
        private Func<byte, J1939Name> _nameResolver;

        /// <summary>Creates a tracker with no SPN name database.</summary>
        public J1939FaultTracker()
            : this(null)
        {
        }

        /// <summary>
        /// Creates a tracker that resolves SPN names through the supplied
        /// lookup.
        /// </summary>
        /// <param name="spnNameResolver">
        /// Returns the parameter name for an SPN, or null when the SPN has no
        /// definition. May be null, in which case every code renders as having
        /// no definition loaded. A code the lookup does not resolve is always
        /// rendered as unresolved alongside its standardised FMI text and is
        /// never dressed up as a known parameter.
        /// </param>
        public J1939FaultTracker(Func<uint, string> spnNameResolver)
        {
            _spnNameResolver = spnNameResolver;
        }

        /// <summary>
        /// SPN name lookup used to render code descriptions. Null means no
        /// database is loaded. Invoked outside the internal lock.
        /// </summary>
        public Func<uint, string> SpnNameResolver
        {
            get { return _spnNameResolver; }
            set { _spnNameResolver = value; }
        }

        /// <summary>
        /// Optional lookup that supplies the NAME claimed by a source address -
        /// wire it to <see cref="J1939AddressClaim.TryGetName"/> through a small
        /// adapter. Null means NAMEs are simply not shown; a NAME is never
        /// guessed from the address. Invoked outside the internal lock.
        /// </summary>
        public Func<byte, J1939Name> NameResolver
        {
            get { return _nameResolver; }
            set { _nameResolver = value; }
        }

        /// <summary>
        /// Age at which a stored DM1 stops being believed, in milliseconds.
        /// Defaults to <see cref="DEFAULT_STALE_AFTER_MS"/>.
        /// </summary>
        /// <exception cref="ArgumentOutOfRangeException">Value is not greater than zero, or is not a finite number.</exception>
        public double StaleAfterMs
        {
            get
            {
                lock (_sync)
                {
                    return _staleAfterMs;
                }
            }

            set
            {
                if (double.IsNaN(value) || double.IsInfinity(value) || value <= 0.0)
                {
                    throw new ArgumentOutOfRangeException("value",
                        "StaleAfterMs must be a finite value greater than zero. Disabling DM1 staleness is not offered: an unaged DM1 shows a lamp that may have gone out minutes ago.");
                }

                lock (_sync)
                {
                    _staleAfterMs = value;
                }
            }
        }

        /// <summary>
        /// Age at which a stored DM2 is marked stale, in milliseconds. Zero
        /// means DM2 is aged but never marked stale, which is defensible
        /// because DM2 is on-request and has no broadcast period to miss.
        /// Defaults to <see cref="DEFAULT_DM2_STALE_AFTER_MS"/>.
        /// </summary>
        /// <exception cref="ArgumentOutOfRangeException">Value is negative, or is not a finite number.</exception>
        public double Dm2StaleAfterMs
        {
            get
            {
                lock (_sync)
                {
                    return _dm2StaleAfterMs;
                }
            }

            set
            {
                if (double.IsNaN(value) || double.IsInfinity(value) || value < 0.0)
                {
                    throw new ArgumentOutOfRangeException("value", "Dm2StaleAfterMs must be a finite value of zero or greater.");
                }

                lock (_sync)
                {
                    _dm2StaleAfterMs = value;
                }
            }
        }

        /// <summary>True when at least one attributable DM1 or DM2 has been received since the last <see cref="Clear"/>.</summary>
        public bool AnythingReceived
        {
            get
            {
                lock (_sync)
                {
                    return _records.Count > 0;
                }
            }
        }

        /// <summary>Number of source addresses currently held.</summary>
        public int TrackedEcuCount
        {
            get
            {
                lock (_sync)
                {
                    return _records.Count;
                }
            }
        }

        /// <summary>
        /// Records a parsed DM1 or DM2. Called from the receive pump thread.
        /// </summary>
        /// <param name="report">
        /// The parsed report. Its <see cref="J1939DtcReport.Kind"/> selects the
        /// active or previously-active slot and its
        /// <see cref="J1939DtcReport.SourceAddress"/> selects the ECU.
        /// </param>
        /// <param name="receivedAtMs">
        /// Arrival time on the caller's clock, normally
        /// <see cref="J1939Message.TimestampMs"/>. Must be the same clock later
        /// passed to <see cref="Evaluate"/>.
        /// </param>
        /// <exception cref="ArgumentNullException"><paramref name="report"/> is null.</exception>
        /// <remarks>
        /// A report parsed without a source address cannot be attributed to an
        /// ECU, so it is discarded and counted rather than filed under a made-up
        /// address. The count surfaces on the snapshot.
        /// </remarks>
        public void OnDtcReport(J1939DtcReport report, double receivedAtMs)
        {
            if (report == null)
            {
                throw new ArgumentNullException("report");
            }

            lock (_sync)
            {
                if (!report.SourceAddressKnown)
                {
                    _unattributedReportCount++;
                    return;
                }

                if (double.IsNaN(receivedAtMs) || double.IsInfinity(receivedAtMs))
                {
                    // Without a usable arrival time the report cannot be aged,
                    // and an un-ageable report is exactly the thing this class
                    // exists to prevent. Count it and drop it.
                    _unattributedReportCount++;
                    NoteTimebaseProblem("a report arrived with a non-finite timestamp and was discarded");
                    return;
                }

                if (_hasReport && receivedAtMs < _newestReportMs - CLOCK_REGRESSION_TOLERANCE_MS)
                {
                    NoteTimebaseProblem(string.Format(CultureInfo.InvariantCulture,
                        "message timestamps went backwards by {0:F0} ms (adapter counter wrap, or two clocks mixed) - ages may be wrong",
                        _newestReportMs - receivedAtMs));
                }

                if (!_hasReport || receivedAtMs > _newestReportMs)
                {
                    _newestReportMs = receivedAtMs;
                    _hasReport = true;
                }

                EcuRecord record;
                if (!_records.TryGetValue(report.SourceAddress, out record))
                {
                    record = new EcuRecord();
                    record.SourceAddress = report.SourceAddress;
                    _records.Add(report.SourceAddress, record);
                }

                if (report.Kind == J1939DtcReportKind.ActiveDm1)
                {
                    record.Dm1 = report;
                    record.Dm1AtMs = receivedAtMs;
                    record.Dm1Count++;
                }
                else
                {
                    record.Dm2 = report;
                    record.Dm2AtMs = receivedAtMs;
                    record.Dm2Count++;
                }
            }
        }

        /// <summary>
        /// Advances the tracker's notion of "now" and returns the aged
        /// snapshot. Call this on a UI timer even when no traffic has arrived -
        /// silence is itself a result, and nothing can expire until the clock
        /// moves.
        /// </summary>
        /// <param name="nowMs">
        /// Current time on the same clock used for
        /// <see cref="OnDtcReport"/>.
        /// </param>
        /// <returns>The snapshot aged to <paramref name="nowMs"/>.</returns>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="nowMs"/> is not a finite number.</exception>
        public J1939BusFaultSnapshot Evaluate(double nowMs)
        {
            if (double.IsNaN(nowMs) || double.IsInfinity(nowMs))
            {
                throw new ArgumentOutOfRangeException("nowMs", "nowMs must be a finite millisecond value from the same clock as OnDtcReport.");
            }

            lock (_sync)
            {
                if (_hasEvaluated && nowMs < _evaluatedNowMs - CLOCK_REGRESSION_TOLERANCE_MS)
                {
                    NoteTimebaseProblem(string.Format(CultureInfo.InvariantCulture,
                        "the evaluation clock went backwards by {0:F0} ms - ages may be wrong",
                        _evaluatedNowMs - nowMs));
                }

                if (_hasReport && nowMs < _newestReportMs - CLOCK_REGRESSION_TOLERANCE_MS)
                {
                    // The caller is ticking with a clock that reads earlier than
                    // the message timestamps it fed in. That is two epochs, not
                    // one late frame. No conversion between them is invented;
                    // the reference falls back to the newest report, ages stop
                    // advancing, and the snapshot says so.
                    NoteTimebaseProblem(string.Format(CultureInfo.InvariantCulture,
                        "the evaluation clock is {0:F0} ms behind the newest message timestamp - pass the same clock to OnDtcReport and Evaluate; staleness cannot be judged until you do",
                        _newestReportMs - nowMs));
                }

                if (!_hasEvaluated || nowMs > _evaluatedNowMs)
                {
                    _evaluatedNowMs = nowMs;
                    _hasEvaluated = true;
                }
            }

            return BuildSnapshot();
        }

        /// <summary>
        /// Returns the current snapshot without advancing the clock, aged
        /// against the later of the last <see cref="Evaluate"/> call and the
        /// newest received message.
        /// </summary>
        /// <returns>The snapshot.</returns>
        /// <remarks>
        /// When <see cref="Evaluate"/> has never been called the ages here are
        /// measured to the last message received, so nothing will ever go
        /// stale. <see cref="J1939BusFaultSnapshot.AgeReferenceIsLastReport"/>
        /// reports that condition instead of letting it pass as a live reading.
        /// </remarks>
        public J1939BusFaultSnapshot GetSnapshot()
        {
            return BuildSnapshot();
        }

        /// <summary>
        /// Looks up one ECU, aged against the current reference time.
        /// </summary>
        /// <param name="sourceAddress">Address to look up.</param>
        /// <param name="entry">The entry, or null when nothing has been received from that address.</param>
        /// <returns>True when a report is held for that address.</returns>
        public bool TryGetEntry(byte sourceAddress, out J1939EcuFaultEntry entry)
        {
            EcuRecord capture;
            double referenceMs;
            double staleAfterMs;
            double dm2StaleAfterMs;

            lock (_sync)
            {
                EcuRecord record;
                if (!_records.TryGetValue(sourceAddress, out record))
                {
                    entry = null;
                    return false;
                }

                capture = record.Capture();
                referenceMs = CurrentReferenceMs();
                staleAfterMs = _staleAfterMs;
                dm2StaleAfterMs = _dm2StaleAfterMs;
            }

            entry = BuildEntry(capture, referenceMs, staleAfterMs, dm2StaleAfterMs,
                WrapSpnResolver(_spnNameResolver), WrapNameResolver(_nameResolver));
            return true;
        }

        /// <summary>
        /// Looks up one ECU and never returns null: an address nothing has been
        /// received from comes back as an explicit
        /// <see cref="J1939EcuFaultState.Unheard"/> entry rather than a blank
        /// record a caller could mistake for a clean bill of health.
        /// </summary>
        /// <param name="sourceAddress">Address to look up.</param>
        /// <returns>The entry for that address.</returns>
        public J1939EcuFaultEntry GetEntry(byte sourceAddress)
        {
            J1939EcuFaultEntry entry;
            if (TryGetEntry(sourceAddress, out entry))
            {
                return entry;
            }

            return J1939EcuFaultEntry.CreateUnheard(sourceAddress);
        }

        /// <summary>
        /// Drops every stored report, both clocks and every degradation
        /// counter. Call this on disconnect, so the next session cannot show
        /// the previous machine's faults.
        /// </summary>
        public void Clear()
        {
            lock (_sync)
            {
                _records.Clear();
                _hasReport = false;
                _newestReportMs = 0.0;
                _hasEvaluated = false;
                _evaluatedNowMs = 0.0;
                _unattributedReportCount = 0;
                _timebaseNote = null;
            }
        }

        /// <summary>
        /// Drops everything held for one source address, returning it to the
        /// "never heard from" state. Use when an ECU has been confirmed off the
        /// bus; do not use to hide a stale entry, because stale is information.
        /// </summary>
        /// <param name="sourceAddress">Address to forget.</param>
        /// <returns>True when an entry was removed.</returns>
        public bool Forget(byte sourceAddress)
        {
            lock (_sync)
            {
                return _records.Remove(sourceAddress);
            }
        }

        // -----------------------------------------------------------------
        // Internals
        // -----------------------------------------------------------------

        private J1939BusFaultSnapshot BuildSnapshot()
        {
            List<EcuRecord> captures;
            double referenceMs;
            bool referenceIsLastReport;
            double staleAfterMs;
            double dm2StaleAfterMs;
            int unattributed;
            string timebaseNote;

            // Copy everything the snapshot needs while holding the lock, then
            // let the pump run again. Caller-supplied resolvers are deliberately
            // NOT called in here - foreign code must never execute while the
            // receive thread is blocked behind this lock.
            lock (_sync)
            {
                captures = new List<EcuRecord>(_records.Count);
                foreach (KeyValuePair<byte, EcuRecord> pair in _records)
                {
                    captures.Add(pair.Value.Capture());
                }

                referenceMs = CurrentReferenceMs();
                referenceIsLastReport = ReferenceIsLastReport();
                staleAfterMs = _staleAfterMs;
                dm2StaleAfterMs = _dm2StaleAfterMs;
                unattributed = _unattributedReportCount;
                timebaseNote = _timebaseNote;
            }

            captures.Sort(CompareBySourceAddress);

            Func<uint, string> spnResolver = WrapSpnResolver(_spnNameResolver);
            Func<byte, J1939Name> nameResolver = WrapNameResolver(_nameResolver);

            List<J1939EcuFaultEntry> entries = new List<J1939EcuFaultEntry>(captures.Count);
            for (int i = 0; i < captures.Count; i++)
            {
                entries.Add(BuildEntry(captures[i], referenceMs, staleAfterMs, dm2StaleAfterMs,
                    spnResolver, nameResolver));
            }

            return new J1939BusFaultSnapshot(entries, referenceMs, referenceIsLastReport,
                staleAfterMs, dm2StaleAfterMs, unattributed, timebaseNote);
        }

        private static J1939EcuFaultEntry BuildEntry(EcuRecord record, double referenceMs,
            double staleAfterMs, double dm2StaleAfterMs,
            Func<uint, string> spnResolver, Func<byte, J1939Name> nameResolver)
        {
            double dm1Age = record.Dm1 == null ? double.NaN : AgeOf(referenceMs, record.Dm1AtMs);
            bool dm1Stale = record.Dm1 != null && dm1Age > staleAfterMs;

            double dm2Age = record.Dm2 == null ? double.NaN : AgeOf(referenceMs, record.Dm2AtMs);
            bool dm2Stale = record.Dm2 != null && dm2StaleAfterMs > 0.0 && dm2Age > dm2StaleAfterMs;

            J1939Name name = null;
            if (nameResolver != null)
            {
                J1939Name resolved = nameResolver(record.SourceAddress);

                // Clone: J1939Name has settable properties and the address-claim
                // map keeps mutating its own copy. A snapshot that aliased it
                // would change under the UI's feet.
                if (resolved != null)
                {
                    name = resolved.Clone();
                }
            }

            return new J1939EcuFaultEntry(record.SourceAddress, name,
                record.Dm1, dm1Age, dm1Stale, record.Dm1Count,
                record.Dm2, dm2Age, dm2Stale, record.Dm2Count,
                spnResolver);
        }

        private static double AgeOf(double referenceMs, double atMs)
        {
            double age = referenceMs - atMs;

            // A negative age means the reference is behind the report, which is
            // already flagged on the snapshot as a timebase problem. Reporting
            // "just arrived" is the least wrong reading: the message did arrive.
            return age < 0.0 ? 0.0 : age;
        }

        private static int CompareBySourceAddress(EcuRecord left, EcuRecord right)
        {
            return left.SourceAddress.CompareTo(right.SourceAddress);
        }

        /// <summary>Caller must hold <see cref="_sync"/>.</summary>
        private double CurrentReferenceMs()
        {
            if (_hasEvaluated && (!_hasReport || _evaluatedNowMs >= _newestReportMs))
            {
                return _evaluatedNowMs;
            }

            if (_hasReport)
            {
                return _newestReportMs;
            }

            return _hasEvaluated ? _evaluatedNowMs : 0.0;
        }

        /// <summary>Caller must hold <see cref="_sync"/>.</summary>
        private bool ReferenceIsLastReport()
        {
            if (!_hasReport)
            {
                return false;
            }

            return !_hasEvaluated || _evaluatedNowMs < _newestReportMs;
        }

        /// <summary>Caller must hold <see cref="_sync"/>. Keeps the first problem seen; later ones do not overwrite it.</summary>
        private void NoteTimebaseProblem(string note)
        {
            if (string.IsNullOrEmpty(_timebaseNote))
            {
                _timebaseNote = note;
            }
        }

        /// <summary>
        /// Wraps a caller's SPN lookup so a throwing database cannot take down
        /// the diagnostics screen. A failed lookup degrades to "no definition
        /// loaded", which renders the code honestly with its FMI text.
        /// </summary>
        private static Func<uint, string> WrapSpnResolver(Func<uint, string> inner)
        {
            if (inner == null)
            {
                return null;
            }

            return delegate(uint spn)
            {
                try
                {
                    return inner(spn);
                }
                catch (Exception)
                {
                    return null;
                }
            };
        }

        /// <summary>
        /// Wraps a caller's NAME lookup so a throwing claim map degrades to "no
        /// NAME known" rather than killing the snapshot.
        /// </summary>
        private static Func<byte, J1939Name> WrapNameResolver(Func<byte, J1939Name> inner)
        {
            if (inner == null)
            {
                return null;
            }

            return delegate(byte sourceAddress)
            {
                try
                {
                    return inner(sourceAddress);
                }
                catch (Exception)
                {
                    return null;
                }
            };
        }

        /// <summary>
        /// Mutable per-ECU state. Only ever touched under <see cref="_sync"/>;
        /// <see cref="Capture"/> produces the detached copy that leaves the lock.
        /// </summary>
        private sealed class EcuRecord
        {
            internal byte SourceAddress;
            internal J1939DtcReport Dm1;
            internal double Dm1AtMs;
            internal int Dm1Count;
            internal J1939DtcReport Dm2;
            internal double Dm2AtMs;
            internal int Dm2Count;

            internal EcuRecord Capture()
            {
                EcuRecord copy = new EcuRecord();
                copy.SourceAddress = SourceAddress;
                copy.Dm1 = Dm1;
                copy.Dm1AtMs = Dm1AtMs;
                copy.Dm1Count = Dm1Count;
                copy.Dm2 = Dm2;
                copy.Dm2AtMs = Dm2AtMs;
                copy.Dm2Count = Dm2Count;
                return copy;
            }
        }
    }
}
