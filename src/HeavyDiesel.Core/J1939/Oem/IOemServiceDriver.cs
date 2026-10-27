// ///////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
//  IOemServiceDriver — what the SERVICE FUNCTIONS panel needs from an OEM service layer.
//
//  Cummins and MaxxForce speak completely different protocols: Cummins is a proprietary
//  frame on PGN 61184 with two rolling counters and an ECU-echo resync; MaxxForce is three
//  PGNs with an ECM-side dead-man heartbeat. Neither resembles the other, and neither
//  should be bent to look like the other.
//
//  What they DO share is the shape of the operator's workflow: open a session, pick a
//  function from a list, confirm it, run it, watch it, stop it if it can be stopped, close
//  the session. That workflow — and nothing more — is what this interface captures, so the
//  panel can be written once and OEM number three costs an adapter rather than a screen.
//
//  DELIBERATELY NOT IN HERE: parameter catalogues, wire framing, counters, heartbeats,
//  password paths. Those differ per OEM and belong to the OEM's own driver, which callers
//  needing them still hold concretely.
// ///////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
using System;
using System.Collections.Generic;

namespace HeavyDiesel.J1939
{
    /// <summary>What a service function does to the machine, for display.</summary>
    public enum OemServiceEffect
    {
        /// <summary>Changes ECU state or a stored value. Nothing moves, nothing heats.</summary>
        StateChange = 0,
        /// <summary>Drives an actuator. Something moves.</summary>
        Actuator = 1,
        /// <summary>Drives exhaust to extreme temperature for an extended period.</summary>
        HighExhaustTemperature = 2,
    }

    /// <summary>Whether a function takes a commanded value, and whether it can run without one.</summary>
    public enum OemServiceValueUse
    {
        /// <summary>No value. Run it as it is.</summary>
        None = 0,
        /// <summary>Runs without a value, but can be commanded to one — e.g. drive a throttle to a position.</summary>
        Optional = 1,
        /// <summary>Cannot run without a value. No fixed-mode frame was recovered for it.</summary>
        Required = 2,
    }

    /// <summary>One selectable service function, described for an operator.</summary>
    public sealed class OemServiceItem
    {
        public OemServiceItem(string key, string displayName, OemServiceEffect effect,
                              bool stoppable, bool requiresPassword, bool isToggle,
                              bool argumentUnverified, string confirmation, object tag,
                              OemServiceValueUse valueUse = OemServiceValueUse.None,
                              string valueLabel = null)
        {
            Key = key;
            DisplayName = displayName;
            Effect = effect;
            Stoppable = stoppable;
            RequiresPassword = requiresPassword;
            IsToggle = isToggle;
            ArgumentUnverified = argumentUnverified;
            Confirmation = confirmation;
            Tag = tag;
            ValueUse = valueUse;
            ValueLabel = valueLabel;
        }

        public string Key { get; private set; }
        public string DisplayName { get; private set; }
        public OemServiceEffect Effect { get; private set; }

        /// <summary>False when the ECM offers no way to stop it once sent.</summary>
        public bool Stoppable { get; private set; }

        public bool RequiresPassword { get; private set; }

        /// <summary>True when start and stop are the SAME command, so a second press may restart it.</summary>
        public bool IsToggle { get; private set; }

        /// <summary>True when the command's bytes are known but an argument's meaning is not.</summary>
        public bool ArgumentUnverified { get; private set; }

        /// <summary>Sentence to show before sending, or null when nothing physical happens.</summary>
        public string Confirmation { get; private set; }

        /// <summary>The OEM-specific object this wraps.</summary>
        public object Tag { get; private set; }

        /// <summary>Whether this function takes a commanded value.</summary>
        public OemServiceValueUse ValueUse { get; private set; }

        /// <summary>
        /// What the value means, for the input's label. Null when there is no value. Say if
        /// the scale is unverified — the operator is the one who has to decide what to type.
        /// </summary>
        public string ValueLabel { get; private set; }
    }

    /// <summary>
    /// The operator workflow, implemented once per OEM. Implementations create no threads
    /// and touch no hardware; the panel pumps them.
    /// </summary>
    public interface IOemServiceDriver
    {
        /// <summary>Name shown in the engine-make selector.</summary>
        string MakeName { get; }

        /// <summary>Functions the operator can choose, in display order.</summary>
        IList<OemServiceItem> Items { get; }

        /// <summary>Open the service session. False on failure; see <see cref="LastError"/>.</summary>
        bool Start();

        /// <summary>Close the service session.</summary>
        bool Stop();

        /// <summary>
        /// Run a function. The panel owns the confirmation dialog and passes the answer —
        /// a driver that opens dialogs cannot be tested or driven headlessly.
        /// </summary>
        /// <param name="commandedValue">
        /// The operator's value for a function whose <see cref="OemServiceItem.ValueUse"/> is
        /// not <see cref="OemServiceValueUse.None"/>; null otherwise. A driver whose OEM has
        /// no value-commanded functions rejects a non-null value rather than dropping it —
        /// silently ignoring an operator's number is how you drive the wrong actuator.
        /// </param>
        bool Run(OemServiceItem item, bool operatorConfirmed, byte? commandedValue);

        /// <summary>Stop the running function, where one can be stopped.</summary>
        bool AbortActive();

        /// <summary>
        /// Called on every UI tick. Each OEM does something different here: Cummins issues
        /// the next cyclic parameter read, MaxxForce keeps the ECM's dead-man fed. Both
        /// decide internally whether anything is due.
        /// </summary>
        void Tick();

        /// <summary>The function currently running, or null.</summary>
        OemServiceItem ActiveItem { get; }

        /// <summary>Why the last operation failed, or empty.</summary>
        string LastError { get; }

        /// <summary>One line describing the current state, for the panel's status label.</summary>
        string DescribeState();

        /// <summary>Raised for each step, so the panel can show the running commentary.</summary>
        event EventHandler<OemServiceStepEventArgs> Step;

        /// <summary>Release event subscriptions. Called when the panel closes the session.</summary>
        void Detach();
    }

    /// <summary>A step in an OEM service sequence.</summary>
    public sealed class OemServiceStepEventArgs : EventArgs
    {
        public OemServiceStepEventArgs(string message, bool isError)
        {
            Message = message; IsError = isError;
        }
        public string Message { get; private set; }
        public bool IsError { get; private set; }
    }
}
