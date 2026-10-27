// ///////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
//  Rp1210Native — P/Invoke surface for TMC RP1210 vehicle diagnostic adapters.
//
//  RP1210 is the ATA Technology & Maintenance Council recommended practice that lets
//  any heavy-duty application talk to any vendor's adapter.  It is the incumbent API
//  in the heavy world: every adapter a truck or mining workshop already owns — Nexiq
//  USB-Link, DG DPA5/XL, DrewLinQ, Jaltest Data-Link — exposes an RP1210 DLL.
//
//  Ported from the TU-RP1210 reference implementation (Python/ctypes):
//      Copyright (c) 2017-2018 Jeremy Daily, The University of Tulsa
//      Used under the MIT License terms of that project's dual licence.
//  Signatures cross-checked against the RP1210B/C recommended practice.
//
//  BITNESS — load-bearing:
//      RP1210 is a 32-bit API by definition (the registry file is literally
//      RP121032.INI) and essentially all vendor DLLs ship x86 only.  The host
//      process MUST therefore be 32-bit.  Verified: this assembly builds AnyCPU
//      with 32BITPREF=1 (corflags), so it runs as a 32-bit process on 64-bit
//      Windows — the same posture the x86 TPGuard/TPSecurity natives already rely
//      on.  Do NOT "modernise" this to 64-bit; RP1210 support dies if you do.
//
//  All entry points are __stdcall (WINAPI).
// ///////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
using System;
using System.Runtime.InteropServices;

namespace HeavyDiesel.Rp1210
{
    /// <summary>RP1210_SendCommand command numbers (RP1210B §Command Definitions).</summary>
    internal static class Rp1210Command
    {
        public const short ResetDevice                    = 0;
        public const short SetAllFiltersStatesToPass      = 3;
        public const short SetMessageFilteringForJ1939    = 4;
        public const short SetMessageFilteringForCan      = 5;
        public const short SetMessageFilteringForJ1708    = 7;
        public const short SetMessageFilteringForJ1850    = 8;
        public const short SetMessageFilteringForIso15765 = 9;
        public const short GenericDriverCommand           = 14;
        public const short SetJ1708Mode                   = 15;
        public const short EchoTransmittedMessages        = 16;
        public const short SetAllFiltersStatesToDiscard   = 17;
        public const short SetMessageReceive              = 18;
        public const short ProtectJ1939Address            = 19;
        public const short SetBroadcastForJ1708           = 20;
        public const short SetBroadcastForCan             = 21;
        public const short SetBroadcastForJ1939           = 22;
        public const short SetBroadcastForJ1850           = 23;
        public const short SetJ1708FilterType             = 24;
        public const short SetJ1939FilterType             = 25;
        public const short SetCanFilterType               = 26;
        public const short SetJ1939InterpacketTime        = 27;
        public const short SetMaxErrorMsgSize             = 28;
        public const short DisallowFurtherConnections     = 29;
        public const short SetJ1850FilterType             = 30;
        public const short ReleaseJ1939Address            = 31;
        public const short SetIso15765FilterType          = 32;
        public const short SetBroadcastForIso15765        = 33;
        public const short SetIso15765FlowControl         = 34;
        public const short ClearIso15765FlowControl       = 35;
        public const short SetIso15765LinkType            = 36;
        public const short SetJ1939Baud                   = 37;
        public const short SetIso15765Baud                = 38;
        public const short SetBlockTimeout                = 215;
        public const short SetJ1708Baud                   = 305;
    }

    /// <summary>Assorted RP1210 constants.</summary>
    internal static class Rp1210Const
    {
        // Blocking behaviour for Send/ReadMessage.
        public const short BlockingIo    = 1;
        public const short NonBlockingIo = 0;

        // Filter states.
        public const short FilterPassNone = 0;   // discard all
        public const short FilterPassSome = 1;
        public const short FilterPassAll  = 2;

        // Echo mode — echo ON means our own transmits come back on the read queue,
        // which is how we distinguish tool traffic from vehicle traffic.
        public const byte EchoOff = 0x00;
        public const byte EchoOn  = 0x01;

        // J1939 filter selector bits.
        public const int FilterPgn         = 0x01;
        public const int FilterPriority    = 0x02;
        public const int FilterSource      = 0x04;
        public const int FilterDestination = 0x08;

        // Filter mode — RP1210B added exclusive (block) filters; the default model
        // is inclusive.
        public const byte FilterInclusive = 0x00;
        public const byte FilterExclusive = 0x01;

        // CAN addressing.
        public const byte StandardCan = 0x00;   // 11-bit
        public const byte ExtendedCan = 0x01;   // 29-bit — J1939

        // J1708 mode.
        public const short ConvertedMode = 1;
        public const short RawMode       = 0;

        /// <summary>Vendor DLL read/write buffer size used throughout the API.</summary>
        public const int BufferSize = 2000;

        /// <summary>A ClientConnect return below this is a client ID; at or above it
        /// the value is an error code.</summary>
        public const short MaxClientId = 128;
    }

    /// <summary>
    /// Raw entry points into a vendor's RP1210 DLL.
    ///
    /// The DLL name is not known until the user picks an adapter, so these cannot be
    /// declared with a static [DllImport] path — they are bound at runtime by
    /// LoadLibrary + GetProcAddress into the delegates below.
    /// </summary>
    internal static class Rp1210Native
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true, BestFitMapping = false, ThrowOnUnmappableChar = true)]
        internal static extern IntPtr LoadLibrary(string lpFileName);

        [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true, BestFitMapping = false, ThrowOnUnmappableChar = true)]
        internal static extern IntPtr GetProcAddress(IntPtr hModule, string lpProcName);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool FreeLibrary(IntPtr hModule);

        // ── Delegate prototypes (all __stdcall) ─────────────────────────────────

        /// <summary>Open a client session on a named protocol.
        /// Returns a client ID (&lt; 128) or an error code (&gt;= 128).
        /// fpchProtocol is an ASCII string, e.g. "J1939:Baud=250" or "J1708".</summary>
        [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Ansi)]
        internal delegate short ClientConnectDelegate(
            IntPtr hwndClient,
            short nDeviceID,
            [MarshalAs(UnmanagedType.LPStr)] string fpchProtocol,
            int lTxBufferSize,
            int lRcvBufferSize,
            short nIsAppPacketizingIncomingMsgs);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        internal delegate short ClientDisconnectDelegate(short nClientID);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        internal delegate short SendMessageDelegate(
            short nClientID,
            byte[] fpchClientMessage,
            short nMessageSize,
            short nNotifyStatusOnTx,
            short nBlockOnSend);

        /// <summary>Returns the number of bytes placed in the buffer, 0 when nothing
        /// is waiting, or a negative value on error.</summary>
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        internal delegate short ReadMessageDelegate(
            short nClientID,
            byte[] fpchAPIMessage,
            short nBufferSize,
            short nBlockOnRead);

        /// <summary>NOTE the argument order: the COMMAND NUMBER comes first, then the
        /// client ID. Getting these the wrong way round is a classic RP1210 bug.</summary>
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        internal delegate short SendCommandDelegate(
            short nCommandNumber,
            short nClientID,
            byte[] fpchClientCommand,
            short nMessageSize);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        internal delegate short ReadVersionDelegate(
            byte[] fpchDLLMajorVersion,
            byte[] fpchDLLMinorVersion,
            byte[] fpchAPIMajorVersion,
            byte[] fpchAPIMinorVersion);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        internal delegate short ReadDetailedVersionDelegate(
            short nClientID,
            byte[] fpchAPIVersionInfo,      // 17 bytes
            byte[] fpchDLLVersionInfo,      // 17 bytes
            byte[] fpchFWVersionInfo);      // 17 bytes

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        internal delegate short GetHardwareStatusDelegate(
            short nClientID,
            byte[] fpchClientInfo,          // 64 bytes
            short nInfoSize,
            short nBlockOnRequest);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        internal delegate short GetErrorMsgDelegate(
            short errCode,
            byte[] fpchDescription);        // 80 bytes

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        internal delegate short GetLastErrorMsgDelegate(
            short errCode,
            ref int SubErrorCode,
            byte[] fpchDescription,         // 80 bytes
            short nClientID);
    }
}
