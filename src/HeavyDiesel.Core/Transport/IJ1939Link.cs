// =============================================================================
//  IJ1939Link.cs - the seam between the J1939 stack and whatever moves frames.
//
//  J1939Session, J1939AddressClaim and J1939TransportProtocol never touch an
//  adapter. They talk to this interface, and this interface is deliberately small:
//  it is exactly the set of members J1939Session was already calling on
//  Rp1210Provider, extracted so that a J2534 PassThru link, a SocketCAN bridge,
//  a serial slcan dongle, or a bench simulator can stand in the same place with
//  no change above it.
//
//  THE CONTRACT
//  ------------
//  * The link moves SINGLE CAN FRAMES. SendJ1939 refuses anything over 8 data
//    bytes; segmentation and reassembly are J1939TransportProtocol's job, above
//    this interface. A link that reassembles behind the stack's back is a
//    degraded link and must say so through Capabilities.DegradationNotice.
//  * The link owns a receive queue that it fills from its own thread. DrainJ1939
//    is polled by the session pump; it never blocks.
//  * Received messages carry their timestamp provenance (see J1939Message).
//    A link that cannot timestamp in hardware stamps from the host clock and
//    reports HardwareTimestamps = false.
//  * Nothing is dropped silently. Full queues are counted and reported.
// =============================================================================

using System.Collections.Generic;
using HeavyDiesel.J1939;

namespace HeavyDiesel.Transport
{
    /// <summary>
    /// A frame-level J1939 link: one connected client on one bus, able to put a
    /// single frame on the wire and hand back the frames it has queued.
    /// </summary>
    public interface IJ1939Link
    {
        /// <summary>True while a client session is open on the adapter and the link is usable.</summary>
        bool IsConnected { get; }

        /// <summary>
        /// The protocol the link was opened on, in the adapter's own vocabulary
        /// (for RP1210: the ClientConnect protocol string, e.g. "J1939:Baud=250").
        /// Informational; shown to the operator.
        /// </summary>
        string ProtocolString { get; }

        /// <summary>
        /// What this link, on this adapter, can actually do. Never null. Under-claim
        /// where unsure: an over-claimed capability turns into a confidently wrong
        /// reading, an under-claimed one only into a cautious tool.
        /// </summary>
        InterfaceCapabilities Capabilities { get; }

        /// <summary>
        /// Put one J1939 message on the wire. Data must be 8 bytes or fewer.
        /// Returns true only when the adapter accepted the frame - a refused write
        /// must come back as false so that the address-claim machine never reports
        /// an address it did not actually announce.
        /// </summary>
        bool SendJ1939(J1939Message message);

        /// <summary>
        /// Take up to <paramref name="max"/> received J1939 messages off the link's
        /// queue, excluding the adapter's echo of the link's own transmits. Never
        /// blocks; returns an empty list (never null) when nothing is waiting.
        /// </summary>
        List<J1939Message> DrainJ1939(int max);

        /// <summary>
        /// Declares that a thread outside the link may still be executing inside the
        /// vendor driver, so the driver module must not be unmapped on dispose. Used
        /// by the session pump when it cannot prove its own thread has stopped.
        /// </summary>
        void MarkUnloadUnsafe(string reason);
    }
}
