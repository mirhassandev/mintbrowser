using System;
using System.IO;
using System.Text;

namespace CustomBrowser.Helpers
{
    /// <summary>
    /// Marks a downloaded file as coming from the Internet (NTFS stream "Zone.Identifier", ZoneId=3), like Edge and Chrome do.
    /// Windows SmartScreen and Office Protected View rely on it. Failures (FAT/exFAT drives, network shares) are ignored.
    /// </summary>
    public static class MarkOfTheWeb
    {
        public static void Apply(string? filePath, string? sourceUrl, string? referrerUrl)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath)) return;

                var sb = new StringBuilder();
                sb.Append("[ZoneTransfer]\r\nZoneId=3\r\n");
                string? referrer = Clean(referrerUrl);
                string? host = Clean(sourceUrl);
                if (referrer != null) sb.Append("ReferrerUrl=").Append(referrer).Append("\r\n");
                if (host != null) sb.Append("HostUrl=").Append(host).Append("\r\n");

                File.WriteAllText(filePath + ":Zone.Identifier", sb.ToString(), new UTF8Encoding(false));
            }
            catch (Exception ex)
            {
                AppLog.Debug("Could not write the Mark-of-the-Web for " + filePath, ex);
            }
        }

        /// <summary>Only http(s) addresses, without user name / password and without #fragment.</summary>
        private static string? Clean(string? url)
        {
            if (string.IsNullOrWhiteSpace(url)) return null;
            if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? u)) return null;
            if (u.Scheme != Uri.UriSchemeHttp && u.Scheme != Uri.UriSchemeHttps) return null;
            var b = new UriBuilder(u) { UserName = string.Empty, Password = string.Empty, Fragment = string.Empty };
            return b.Uri.AbsoluteUri;
        }
    }
}
