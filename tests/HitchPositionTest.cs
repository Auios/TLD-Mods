using System;
using System.Collections.Generic;
using System.Globalization;
using TrailerHitches;

// The hitch positions Trailer Hitches adds (split from RevaMP's tools/unit/TrailerHitchesTest when the mod moved here;
// RevaMP keeps the slot-loading half as tools/unit/MissingTrailerSlotTest).
static class HitchPositionTest
{
    static int fails;
    static void Check(string name, bool ok) { Console.WriteLine((ok ? "PASS " : "FAIL ") + name); if (!ok) fails++; }
    static int Main()
    {
        var ids = new HashSet<int>();
        var positions = new Dictionary<string, HitchPosition>();
        foreach (var model in HitchPosition.Models)
        {
            positions.Add(model.Key, model.Position);
            foreach (int id in model.Items) Check("unique item " + id, ids.Add(id) && HitchPosition.ForItem(id) == model);
        }
        Check("nine missing stock receivers; existing receivers excluded", ids.Count == 9 && HitchPosition.ForItem(1) == null && HitchPosition.ForItem(140) == null);
        Check("transport frame extends behind van", HitchPosition.ForItem(396).Position.Z < HitchPosition.ForItem(371).Position.Z - 1.5f);
        Check("nominal level target relative to rear axle", Math.Abs(HitchPosition.ForItem(243).Position.Y - (-0.0222f - 0.0723f)) < 0.00001f &&
            Math.Abs(HitchPosition.ForItem(371).Position.Y - (-0.2168f - 0.0723f)) < 0.00001f);
        foreach (string raw in new[] { "", "0,1", "0,1,2,3", "NaN,0,0", "0,Infinity,0", "0,0,-21", "0,0,1e30", "a,b,c" })
            Check("invalid override refused: " + raw, !HitchPosition.TryParse(raw, out _));
        var culture = CultureInfo.CurrentCulture;
        string hash = HitchPosition.Fingerprint(positions);
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            Check("overrides use decimal point in every locale", HitchPosition.TryParse(" 0, -0.0945, -2.26 ", out var p) && p.ToString() == "0,-0.0945,-2.26");
            Check("locale independent multiplayer fingerprint", hash == HitchPosition.Fingerprint(positions));
        }
        finally { CultureInfo.CurrentCulture = culture; }
        var reversed = new Dictionary<string, HitchPosition>();
        for (int i = HitchPosition.Models.Length - 1; i >= 0; i--) reversed.Add(HitchPosition.Models[i].Key, HitchPosition.Models[i].Position);
        Check("dictionary order cannot change identity", hash == HitchPosition.Fingerprint(reversed));
        reversed["car09"] = new HitchPosition(0f, -0.3f, -2.34f);
        Check("different config refuses the same-version peer", hash != HitchPosition.Fingerprint(reversed));

        Console.WriteLine(fails == 0 ? "ALL PASS" : fails + " FAILED");
        return fails == 0 ? 0 : 1;
    }
}
