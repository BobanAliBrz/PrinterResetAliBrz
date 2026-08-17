using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace PrintSpoolerGuardian
{
    /// <summary>
    /// Sends raw printer hardware and interpreter reset sequences (PJL / UEL / ESC E)
    /// directly to the printer device via the Win32 Spooler API without driver rendering.
    /// Used to unhang printers (such as HP LaserJet P1005 / GDI printers) stuck in an
    /// unclosed or corrupted raster session.
    /// </summary>
    public class RawPrinterResetter
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
        private class DOCINFOA
        {
            [MarshalAs(UnmanagedType.LPStr)]
            public string pDocName;
            [MarshalAs(UnmanagedType.LPStr)]
            public string pOutputFile;
            [MarshalAs(UnmanagedType.LPStr)]
            public string pDataType;
        }

        [DllImport("winspool.drv", EntryPoint = "OpenPrinterA", SetLastError = true, CharSet = CharSet.Ansi, ExactSpelling = true, CallingConvention = CallingConvention.StdCall)]
        private static extern bool OpenPrinter(string pPrinterName, out IntPtr phPrinter, IntPtr pDefault);

        [DllImport("winspool.drv", EntryPoint = "ClosePrinter", SetLastError = true, ExactSpelling = true, CallingConvention = CallingConvention.StdCall)]
        private static extern bool ClosePrinter(IntPtr hPrinter);

        [DllImport("winspool.drv", EntryPoint = "StartDocPrinterA", SetLastError = true, CharSet = CharSet.Ansi, ExactSpelling = true, CallingConvention = CallingConvention.StdCall)]
        private static extern bool StartDocPrinter(IntPtr hPrinter, int Level, [In] DOCINFOA pDocInfo);

        [DllImport("winspool.drv", EntryPoint = "EndDocPrinter", SetLastError = true, ExactSpelling = true, CallingConvention = CallingConvention.StdCall)]
        private static extern bool EndDocPrinter(IntPtr hPrinter);

        [DllImport("winspool.drv", EntryPoint = "StartPagePrinter", SetLastError = true, ExactSpelling = true, CallingConvention = CallingConvention.StdCall)]
        private static extern bool StartPagePrinter(IntPtr hPrinter);

        [DllImport("winspool.drv", EntryPoint = "EndPagePrinter", SetLastError = true, ExactSpelling = true, CallingConvention = CallingConvention.StdCall)]
        private static extern bool EndPagePrinter(IntPtr hPrinter);

        [DllImport("winspool.drv", EntryPoint = "WritePrinter", SetLastError = true, ExactSpelling = true, CallingConvention = CallingConvention.StdCall)]
        private static extern bool WritePrinter(IntPtr hPrinter, byte[] pBuf, int cbBuf, out int pcWritten);

        /// <summary>
        /// Sends a raw PJL/UEL flush and printer reset stream to the specified printer queue.
        /// </summary>
        /// <param name="printerName">The name of the target printer queue.</param>
        /// <returns>True if the raw reset packet was successfully delivered to the spooler.</returns>
        public bool SendReset(string printerName)
        {
            if (string.IsNullOrEmpty(printerName))
            {
                Logger.Error("Cannot send raw reset: printer name is empty.");
                return false;
            }

            Logger.Info("Sending raw PJL/UEL hardware reset sequence to printer: " + printerName);

            IntPtr hPrinter = IntPtr.Zero;
            try
            {
                if (!OpenPrinter(printerName, out hPrinter, IntPtr.Zero))
                {
                    int err = Marshal.GetLastWin32Error();
                    Logger.Warn("OpenPrinter failed for '" + printerName + "' (Win32 Error: " + err + ")");
                    return false;
                }

                var docInfo = new DOCINFOA
                {
                    pDocName = "PSG_Hardware_Reset",
                    pOutputFile = null,
                    pDataType = "RAW"
                };

                if (!StartDocPrinter(hPrinter, 1, docInfo))
                {
                    int err = Marshal.GetLastWin32Error();
                    Logger.Warn("StartDocPrinter failed for '" + printerName + "' (Win32 Error: " + err + ")");
                    return false;
                }

                try
                {
                    if (!StartPagePrinter(hPrinter))
                    {
                        int err = Marshal.GetLastWin32Error();
                        Logger.Warn("StartPagePrinter failed for '" + printerName + "' (Win32 Error: " + err + ")");
                        return false;
                    }

                    try
                    {
                        byte[] resetBytes = BuildResetSequence();
                        int written;
                        if (!WritePrinter(hPrinter, resetBytes, resetBytes.Length, out written) || written != resetBytes.Length)
                        {
                            int err = Marshal.GetLastWin32Error();
                            Logger.Warn("WritePrinter failed to send full reset packet for '" + printerName + "' (Written: " + written + "/" + resetBytes.Length + ", Win32 Error: " + err + ")");
                            return false;
                        }

                        Logger.Info("Successfully transmitted " + written + " bytes of raw reset sequence to " + printerName);
                        return true;
                    }
                    finally
                    {
                        EndPagePrinter(hPrinter);
                    }
                }
                finally
                {
                    EndDocPrinter(hPrinter);
                }
            }
            catch (Exception ex)
            {
                Logger.Error("Exception while sending raw reset to " + printerName + ": " + ex.Message);
                return false;
            }
            finally
            {
                if (hPrinter != IntPtr.Zero)
                {
                    ClosePrinter(hPrinter);
                }
            }
        }

        /// <summary>
        /// Builds the raw byte stream containing Universal Exit Language (UEL),
        /// HP PJL Reset commands, and standard PCL/raster reset delimiters.
        /// </summary>
        private static byte[] BuildResetSequence()
        {
            using (var ms = new MemoryStream())
            {
                // 1. Universal Exit Language (UEL) to exit any active interpreter session
                byte[] uel = Encoding.ASCII.GetBytes("\x1B%-12345X");
                ms.Write(uel, 0, uel.Length);

                // 2. PJL Reset and End-Of-Job
                byte[] pjl = Encoding.ASCII.GetBytes("@PJL\r\n@PJL RESET\r\n@PJL EOJ\r\n");
                ms.Write(pjl, 0, pjl.Length);

                // 3. ESC E (PCL / Raster Engine Reset)
                byte[] escE = Encoding.ASCII.GetBytes("\x1BE");
                ms.Write(escE, 0, escE.Length);

                // 4. Closing Universal Exit Language (UEL)
                ms.Write(uel, 0, uel.Length);

                return ms.ToArray();
            }
        }
    }
}
