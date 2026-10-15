using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace CustomBrowser.Helpers
{
    /// <summary>Makes a window's taskbar button flash (orange) until the user switches to the window.</summary>
    public static class TaskbarFlash
    {
        private const uint FlashTray = 0x00000002;      // flash the taskbar button
        private const uint FlashUntilForeground = 0x0000000C; // ... until the window comes to the front

        [StructLayout(LayoutKind.Sequential)]
        private struct FlashInfo
        {
            public uint Size;
            public IntPtr Window;
            public uint Flags;
            public uint Count;
            public uint Timeout;
        }

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool FlashWindowEx(ref FlashInfo info);

        public static void Flash(Window window)
        {
            try
            {
                IntPtr handle = new WindowInteropHelper(window).Handle;
                if (handle == IntPtr.Zero) return;
                var info = new FlashInfo
                {
                    Size = (uint)Marshal.SizeOf<FlashInfo>(),
                    Window = handle,
                    Flags = FlashTray | FlashUntilForeground,
                    Count = 3,
                    Timeout = 0
                };
                FlashWindowEx(ref info);
            }
            catch (Exception ex)
            {
                AppLog.Debug("Could not flash the taskbar button", ex); // cosmetic only
            }
        }
    }
}
