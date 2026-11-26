using System;

namespace HeadUnit
{
    /// YouTube answers some connections with "Sign in to confirm you're not a bot" instead of the video: VPNs above all
    /// (a VPN server's address is shared by many people), also shared or very busy lines. A newer yt-dlp doesn't help
    /// with it, so the screen says what is going on instead of offering UPDATE. Plain C# (no Unity):
    /// tests/HeadUnitScreenTest.
    public static class YouTubeCheck
    {
        /// What the + ADD dialog shows when a link fails this way (fits its banner).
        public const string Text = "YouTube said no: VPN? Try without it";

        /// yt-dlp's error is YouTube's check, not an old yt-dlp.
        public static bool Is(string err) =>
            err != null && (err.IndexOf("not a bot", StringComparison.OrdinalIgnoreCase) >= 0
                            || err.IndexOf("Sign in to confirm", StringComparison.OrdinalIgnoreCase) >= 0);
    }
}
