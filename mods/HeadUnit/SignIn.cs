using System;
using System.Collections.Generic;
using System.IO;

namespace HeadUnit
{
    /// YouTube answers some addresses (a busy home line, a VPN, a shared one) with "Sign in to confirm you're not a bot"
    /// instead of the video: no video plays and no link adds there until the player is signed in. This is the sign-in
    /// it asks for, the player's own: yt-dlp sends YouTube the cookies of the player's browser login, as the browser
    /// itself would. Nothing here imitates a person or answers YouTube's check for them.
    ///
    /// Two ways, both on this machine only (nothing passes to other players):
    ///  - A Firefox login (Firefox, LibreWolf, Zen: one cookie format). SIGN IN on the screen finds the profile used last
    ///    and keeps its folder in the config ([YouTube] SignIn); yt-dlp reads its cookies on every run, so the login
    ///    stays as fresh as the browser's. Under Proton that is the Linux user's Firefox, through drive Z:.
    ///  - A cookies.txt (Netscape format, from a browser extension) in BepInEx/config/HeadUnit: any browser. Chrome and
    ///    Edge on Windows lock their cookies to themselves (app-bound encryption): yt-dlp can't read them, a cookies.txt
    ///    exported from them it can.
    /// Plain C# (no Unity): tests/HeadUnitSignInTest.
    public static class SignIn
    {
        public const string CookiesTxt = "cookies.txt";

        /// yt-dlp's error is YouTube's sign-in check (not an old yt-dlp: UPDATE doesn't help with this one).
        public static bool IsBotCheck(string err) =>
            err != null && (err.IndexOf("not a bot", StringComparison.OrdinalIgnoreCase) >= 0
                            || err.IndexOf("Sign in to confirm", StringComparison.OrdinalIgnoreCase) >= 0);

        /// Where Firefox-format browsers keep their profiles: Windows' own (appData: %APPDATA%), and the Linux user's
        /// (home: their home folder as this program sees it, Z:\home\name under Proton): the classic place, the XDG one
        /// newer Firefox uses, Flatpak and Snap.
        public static List<string> FirefoxRoots(string appData, string home)
        {
            var roots = new List<string>();
            if (!string.IsNullOrEmpty(appData))
                foreach (var b in new[] { @"Mozilla\Firefox\Profiles", @"librewolf\Profiles", @"zen\Profiles" })
                    roots.Add(Path.Combine(appData, b));
            if (!string.IsNullOrEmpty(home))
                foreach (var b in new[] { ".mozilla/firefox", ".config/mozilla/firefox", ".var/app/org.mozilla.firefox/.mozilla/firefox",
                                          "snap/firefox/common/.mozilla/firefox", ".librewolf", ".var/app/io.gitlab.librewolf-community/.librewolf",
                                          ".zen", ".var/app/app.zen_browser.zen/.zen" })
                    roots.Add(Path.Combine(home, b.Replace('/', Path.DirectorySeparatorChar)));
            return roots;
        }

        /// The profile used last: of the folders under the roots that hold a cookies.sqlite, the one whose cookies
        /// changed last (its -wal file counts: Firefox writes there first). Null if none.
        public static string NewestProfile(IEnumerable<string> roots)
        {
            string best = null; DateTime bestAt = DateTime.MinValue;
            foreach (var root in roots)
            {
                string[] dirs;
                try { if (!Directory.Exists(root)) continue; dirs = Directory.GetDirectories(root); }
                catch { continue; }
                foreach (var d in dirs)
                {
                    try
                    {
                        string db = Path.Combine(d, "cookies.sqlite");
                        if (!File.Exists(db)) continue;
                        DateTime at = File.GetLastWriteTimeUtc(db);
                        string wal = db + "-wal";
                        if (File.Exists(wal) && File.GetLastWriteTimeUtc(wal) > at) at = File.GetLastWriteTimeUtc(wal);
                        if (best == null || at > bestAt) { best = d; bestAt = at; }
                    }
                    catch { }
                }
            }
            return best;
        }

        /// The Linux home folder as a Windows program under Wine/Proton sees it (WINEHOMEDIR \??\Z:\home\name, or HOME
        /// on drive Z:), null on Windows itself.
        public static string WineHome(Func<string, string> env)
        {
            string w = env("WINEHOMEDIR");
            if (!string.IsNullOrEmpty(w)) return w.StartsWith(@"\??\") ? w.Substring(4) : w;
            string h = env("HOME");
            if (!string.IsNullOrEmpty(h) && h.StartsWith("/") && Directory.Exists(@"Z:\")) return @"Z:" + h.Replace('/', '\\');
            return null;
        }

        /// What yt-dlp is given for the sign-in: the setting's Firefox profile folder if it is one, else the cookies.txt
        /// (as `copy`, made by the caller: yt-dlp writes the file back when it ends, and two at once would tear it), else
        /// nothing. Ends in a space when not empty.
        public static string Args(string profile, string cookiesCopy)
        {
            if (!string.IsNullOrEmpty(profile)) return $"--cookies-from-browser \"firefox:{profile.Replace("\"", "")}\" ";
            if (!string.IsNullOrEmpty(cookiesCopy)) return $"--cookies \"{cookiesCopy.Replace("\"", "")}\" ";
            return "";
        }

        /// The setting is a profile folder that still has its cookies (a browser removed or reset: not given to yt-dlp).
        public static bool UsableProfile(string profile) =>
            !string.IsNullOrEmpty(profile) && File.Exists(Path.Combine(profile, "cookies.sqlite"));
    }
}
