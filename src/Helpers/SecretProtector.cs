using System;
using System.Security.Cryptography;
using System.Text;

namespace CustomBrowser.Helpers
{
    /// <summary>
    /// Protects small secrets (login cookies kept for unfinished downloads) with the Windows account's DPAPI key, so the
    /// database file alone - copied, backed up or synced elsewhere - does not reveal them. Safe to call from any thread.
    /// </summary>
    public static class SecretProtector
    {
        /// <summary>Marks a stored value as protected. A stored value without it is an old, unprotected one.</summary>
        public const string Prefix = "dpapi1:";

        private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("MintBrowser.DownloadCookies.v1");

        public static bool IsProtected(string? stored) =>
            !string.IsNullOrEmpty(stored) && stored.StartsWith(Prefix, StringComparison.Ordinal);

        /// <summary>Returns the protected form. Returns an empty string when there is nothing to protect or protecting failed (never the plain text).</summary>
        public static string Protect(string? plain)
        {
            if (string.IsNullOrEmpty(plain)) return string.Empty;
            if (IsProtected(plain)) return plain;
            try
            {
                byte[] sealedBytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), Entropy, DataProtectionScope.CurrentUser);
                return Prefix + Convert.ToBase64String(sealedBytes);
            }
            catch (Exception ex) when (ex is CryptographicException or PlatformNotSupportedException)
            {
                AppLog.Warn("Could not protect a stored secret - it is not saved", ex);
                return string.Empty;
            }
        }

        /// <summary>
        /// Returns the plain text. A value without the prefix is an older, unprotected one and is returned as it is (it is
        /// protected the next time it is saved). A value that cannot be opened (another Windows account) gives an empty string.
        /// </summary>
        public static string Unprotect(string? stored)
        {
            if (string.IsNullOrEmpty(stored)) return string.Empty;
            if (!IsProtected(stored)) return stored;
            try
            {
                byte[] sealedBytes = Convert.FromBase64String(stored[Prefix.Length..]);
                return Encoding.UTF8.GetString(ProtectedData.Unprotect(sealedBytes, Entropy, DataProtectionScope.CurrentUser));
            }
            catch (Exception ex) when (ex is CryptographicException or FormatException or PlatformNotSupportedException)
            {
                AppLog.Debug("A stored secret could not be opened - treated as empty", ex);
                return string.Empty;
            }
        }
    }
}
