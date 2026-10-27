// =============================================================================
//  Program.cs - entry point for the HeavyDiesel bench app.
// =============================================================================

using System;
using System.Windows.Forms;

namespace HeavyDiesel.Bench
{
    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            // Loud, early, and before any adapter is touched. A 64-bit process cannot
            // load a single RP1210 vendor DLL, and the symptom without this check is
            // "LoadLibrary failed, error 193" three screens deep.
            if (IntPtr.Size != 4)
            {
                MessageBox.Show(
                    "HeavyDiesel.Bench is running as a 64-bit process.\n\n"
                    + "RP1210 vendor DLLs are 32-bit, so no adapter can be loaded from here. "
                    + "Adapter enumeration (which only reads INI files) will still work, but every "
                    + "Connect will fail with Win32 error 193.\n\n"
                    + "Build the project with PlatformTarget=x86 (the shipped setting) and run that EXE.",
                    "HeavyDiesel.Bench - wrong bitness",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }

            Application.Run(new MainForm());
        }
    }
}
