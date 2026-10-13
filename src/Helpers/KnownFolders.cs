using System;
using System.IO;
using System.Runtime.InteropServices;

namespace CustomBrowser.Helpers
{
    /// <summary>
    /// Finds the user's real Downloads folder. The folder can be moved (Properties > Location, OneDrive, a policy),
    /// so the Windows known-folder API is asked first; the old profile\Downloads path is only the fallback.
    /// </summary>
    public static class KnownFolders
    {
        // FOLDERID_Downloads
        private static readonly Guid DownloadsId = new Guid("374DE290-123F-4565-9164-39C4925E467B");

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int SHGetKnownFolderPath(
            [MarshalAs(UnmanagedType.LPStruct)] Guid rfid, uint dwFlags, IntPtr hToken, out IntPtr ppszPath);

        public static string GetDownloadsFolder()
        {
            try
            {
                int hr = SHGetKnownFolderPath(DownloadsId, 0, IntPtr.Zero, out IntPtr pathPtr);
                try
                {
                    if (hr == 0 && pathPtr != IntPtr.Zero)
                    {
                        string? path = Marshal.PtrToStringUni(pathPtr);
                        if (!string.IsNullOrWhiteSpace(path)) return path;
                    }
                }
                finally
                {
                    // The caller must free the string whether the call succeeded or not.
                    if (pathPtr != IntPtr.Zero) Marshal.FreeCoTaskMem(pathPtr);
                }
            }
            catch (Exception ex)
            {
                AppLog.Debug("Could not read the Downloads known folder - using the profile fallback", ex);
            }
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        }
    }
}
