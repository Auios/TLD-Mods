using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using HeadUnit;

// The Head Unit's YouTube sign-in (SignIn.cs): which errors are YouTube's sign-in check, where Firefox-format profiles
// are looked for (Windows, Linux through Proton's drive Z:), that the profile used last is picked, and what yt-dlp is
// given. Made-up profiles in a temporary folder: never the machine's own browser.
static class HeadUnitSignInTest
{
    static int checks;
    static void Check(bool ok, string why) { checks++; if (!ok) throw new Exception("FAIL: " + why); }

    static int Main()
    {
        Check(SignIn.IsBotCheck("ERROR: [youtube] 3BFTio5296w: Sign in to confirm you’re not a bot. This helps protect our community."), "YouTube's check, curly apostrophe");
        Check(SignIn.IsBotCheck("ERROR: [youtube] x: Sign in to confirm you're not a bot"), "YouTube's check, plain apostrophe");
        Check(!SignIn.IsBotCheck("ERROR: [youtube] x: Video unavailable"), "another error isn't the check");
        Check(!SignIn.IsBotCheck("ERROR: Requested format is not available"), "an old yt-dlp's error isn't the check");
        Check(!SignIn.IsBotCheck(null), "no error");

        var win = SignIn.FirefoxRoots(@"C:\Users\p\AppData\Roaming", null);
        Check(win.Exists(r => r.EndsWith(@"Mozilla\Firefox\Profiles")), "Windows: Firefox's profiles in %APPDATA%");
        var lin = SignIn.FirefoxRoots(null, "/h");
        foreach (var w in new[] { ".mozilla/firefox", ".config/mozilla/firefox", "org.mozilla.firefox", "snap/firefox", ".librewolf", ".zen" })
            Check(lin.Exists(r => r.Replace('\\', '/').Contains(w)), "Linux: " + w);

        Check(SignIn.WineHome(n => n == "WINEHOMEDIR" ? @"\??\Z:\home\p" : null) == @"Z:\home\p", "Wine's WINEHOMEDIR, its \\??\\ dropped");
        Check(SignIn.WineHome(n => null) == null, "Windows itself: no Linux home");

        string tmp = Path.Combine(Path.GetTempPath(), "husignin-" + Guid.NewGuid().ToString("N"));
        try
        {
            string root = Path.Combine(tmp, "firefox"), root2 = Path.Combine(tmp, "zen");
            string a = Path.Combine(root, "aaaa.default"), b = Path.Combine(root, "bbbb.default-release"), c = Path.Combine(root2, "cccc.Default"), d = Path.Combine(root, "dddd.empty");
            foreach (var x in new[] { a, b, c, d }) Directory.CreateDirectory(x);
            File.WriteAllText(Path.Combine(a, "cookies.sqlite"), "");
            File.SetLastWriteTimeUtc(Path.Combine(a, "cookies.sqlite"), DateTime.UtcNow.AddDays(-3));
            File.WriteAllText(Path.Combine(b, "cookies.sqlite"), "");
            File.SetLastWriteTimeUtc(Path.Combine(b, "cookies.sqlite"), DateTime.UtcNow.AddDays(-2));
            File.WriteAllText(Path.Combine(c, "cookies.sqlite"), "");
            File.SetLastWriteTimeUtc(Path.Combine(c, "cookies.sqlite"), DateTime.UtcNow.AddDays(-1));
            var roots = new List<string> { root, root2, Path.Combine(tmp, "missing") };
            Check(SignIn.NewestProfile(roots) == c, "the profile whose cookies changed last, across browsers: " + SignIn.NewestProfile(roots));
            File.WriteAllText(Path.Combine(a, "cookies.sqlite-wal"), "");   // written now: Firefox writes its cookies here first
            Check(SignIn.NewestProfile(roots) == a, "a -wal file written last counts: " + SignIn.NewestProfile(roots));
            Check(SignIn.NewestProfile(new[] { Path.Combine(tmp, "missing") }) == null, "no profile: null");
            Check(SignIn.UsableProfile(a) && !SignIn.UsableProfile(d) && !SignIn.UsableProfile("") && !SignIn.UsableProfile(null), "a profile with cookies is usable, one without isn't");
        }
        finally { try { Directory.Delete(tmp, true); } catch { } }

        Check(SignIn.Args(@"Z:\home\p\.mozilla\firefox\x.default", null) == "--cookies-from-browser \"firefox:Z:\\home\\p\\.mozilla\\firefox\\x.default\" ", "a profile: yt-dlp reads its cookies");
        Check(SignIn.Args(@"C:\p", @"C:\c.txt") == "--cookies-from-browser \"firefox:C:\\p\" ", "the profile before a cookies.txt");
        Check(SignIn.Args(null, @"C:\cfg\cookies-run-1.txt") == "--cookies \"C:\\cfg\\cookies-run-1.txt\" ", "a cookies.txt copy");
        Check(SignIn.Args("", null) == "", "no sign-in: nothing");
        Check(!SignIn.Args("C:\\a\"b", null).Contains("a\"b"), "a quote in the setting doesn't end yt-dlp's argument");

        Console.WriteLine($"Head Unit sign-in: {checks} checks passed");
        return 0;
    }
}
