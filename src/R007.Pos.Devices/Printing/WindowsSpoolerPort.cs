using System.Runtime.InteropServices;

namespace R007.Pos.Devices.Printing;

/// <summary>
/// Sends raw bytes (ESC/POS) to a Windows printer queue by name through the spooler with datatype RAW, so the driver
/// does not re-render them. Windows only; on other platforms it reports <see cref="PrinterStatus.Offline"/>.
/// This needs verification on real hardware (see docs/windows-hardware-verification.md).
/// </summary>
public sealed class WindowsSpoolerPort(string printerName) : IRawPrinterPort
{
    public Task<PrinterStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows())
        {
            return Task.FromResult(PrinterStatus.Offline);
        }

        try
        {
            if (!NativeMethods.OpenPrinter(printerName, out var handle, IntPtr.Zero))
            {
                return Task.FromResult(PrinterStatus.Offline);
            }

            NativeMethods.ClosePrinter(handle);
            return Task.FromResult(PrinterStatus.Ready);
        }
        catch (DllNotFoundException)
        {
            return Task.FromResult(PrinterStatus.Offline);
        }
    }

    public Task WriteAsync(byte[] data, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("The Windows spooler port only works on Windows.");
        }

        return Task.Run(() => WriteRaw(data), cancellationToken);
    }

    private void WriteRaw(byte[] data)
    {
        if (!NativeMethods.OpenPrinter(printerName, out var handle, IntPtr.Zero))
        {
            throw new IOException($"Cannot open printer '{printerName}' (error {Marshal.GetLastWin32Error()}).");
        }

        try
        {
            var doc = new NativeMethods.DocInfo { DocName = "007 Resort POS receipt", OutputFile = null, DataType = "RAW" };
            if (NativeMethods.StartDocPrinter(handle, 1, doc) == 0)
            {
                throw new IOException($"StartDocPrinter failed (error {Marshal.GetLastWin32Error()}).");
            }

            try
            {
                if (!NativeMethods.StartPagePrinter(handle))
                {
                    throw new IOException($"StartPagePrinter failed (error {Marshal.GetLastWin32Error()}).");
                }

                var buffer = Marshal.AllocCoTaskMem(data.Length);
                try
                {
                    Marshal.Copy(data, 0, buffer, data.Length);
                    if (!NativeMethods.WritePrinter(handle, buffer, data.Length, out var written) || written != data.Length)
                    {
                        throw new IOException($"WritePrinter wrote {written}/{data.Length} bytes (error {Marshal.GetLastWin32Error()}).");
                    }
                }
                finally
                {
                    Marshal.FreeCoTaskMem(buffer);
                }

                NativeMethods.EndPagePrinter(handle);
            }
            finally
            {
                NativeMethods.EndDocPrinter(handle);
            }
        }
        finally
        {
            NativeMethods.ClosePrinter(handle);
        }
    }

    private static class NativeMethods
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal sealed class DocInfo
        {
            [MarshalAs(UnmanagedType.LPWStr)]
            public string? DocName;

            [MarshalAs(UnmanagedType.LPWStr)]
            public string? OutputFile;

            [MarshalAs(UnmanagedType.LPWStr)]
            public string? DataType;
        }

#pragma warning disable SYSLIB1054 // struct-with-strings marshalling: DllImport is simplest and Windows-only.
        [DllImport("winspool.drv", EntryPoint = "OpenPrinterW", SetLastError = true, CharSet = CharSet.Unicode, ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool OpenPrinter(string printerName, out IntPtr handle, IntPtr defaults);

        [DllImport("winspool.drv", SetLastError = true, ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool ClosePrinter(IntPtr handle);

        [DllImport("winspool.drv", EntryPoint = "StartDocPrinterW", SetLastError = true, CharSet = CharSet.Unicode, ExactSpelling = true)]
        internal static extern int StartDocPrinter(IntPtr handle, int level, [In, MarshalAs(UnmanagedType.LPStruct)] DocInfo docInfo);

        [DllImport("winspool.drv", SetLastError = true, ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool EndDocPrinter(IntPtr handle);

        [DllImport("winspool.drv", SetLastError = true, ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool StartPagePrinter(IntPtr handle);

        [DllImport("winspool.drv", SetLastError = true, ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool EndPagePrinter(IntPtr handle);

        [DllImport("winspool.drv", SetLastError = true, ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool WritePrinter(IntPtr handle, IntPtr buffer, int count, out int written);
#pragma warning restore SYSLIB1054
    }
}
