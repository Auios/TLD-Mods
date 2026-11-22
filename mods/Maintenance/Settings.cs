using BepInEx.Configuration;
using TLDRevamp.Multiplayer;

namespace Maintenance
{
    /// The options. All are session-wide in multiplayer: everyone plays by the host's rules (RevaMP sends the host's
    /// values at join and whenever they change, and gives each player their own back when the session ends).
    internal static class Settings
    {
        static ConfigEntry<bool> _partWearConfig, _brushRadiatorsConfig;
        static ConfigEntry<float> _rateConfig;
        static SessionSetting _partWearSession, _brushSession, _rateSession;
        static volatile bool _partWear, _brushRadiators;
        static float _rate = 1f;

        /// Engines, radiators and tyres wear; the wire brush repairs engines and radiators.
        internal static bool PartWear => _partWear;
        /// The wire brush repairs radiators (also on with PartWear).
        internal static bool BrushRadiators => _brushRadiators;
        /// How fast parts wear: 1 = the normal rates (about four hours of driving per engine step), 2 = twice as fast.
        internal static float WearRate => _rate;

        internal static void Bind(ConfigFile config)
        {
            _partWearConfig = config.Bind("Wear", "PartWear", false,
                "Engines, radiators and tyres wear with use, and a worn engine loses power and burns more oil. The wire brush " +
                "repairs a switched-off engine and radiators. Off by default; in multiplayer the host's value is used.");
            _brushRadiatorsConfig = config.Bind("Repair", "BrushRadiators", false,
                "The wire brush repairs radiators even with part wear off. In multiplayer the host's value is used.");
            _rateConfig = config.Bind("Wear", "WearRate", 1f, new ConfigDescription(
                "How fast parts wear with PartWear on: 1 = normal (about four hours of driving per engine condition step), 2 = " +
                "twice as fast. In multiplayer the host's value is used.", new AcceptableValueRange<float>(0.1f, 1000f)));
            _partWear = _partWearConfig.Value;
            _rate = _rateConfig.Value;
            _rateConfig.SettingChanged += (s, e) => { if (_rateSession == null || !_rateSession.FromHost) _rate = _rateConfig.Value; };
            _brushRadiators = _brushRadiatorsConfig.Value;
            _partWearConfig.SettingChanged += (s, e) => { if (_partWearSession == null || !_partWearSession.FromHost) _partWear = _partWearConfig.Value; };
            _brushRadiatorsConfig.SettingChanged += (s, e) => { if (_brushSession == null || !_brushSession.FromHost) _brushRadiators = _brushRadiatorsConfig.Value; };
        }

        internal static void Register(ModInfo mod)
        {
            _partWearSession = MpApi.RegisterSetting(mod, "partwear", () => Format(_partWearConfig.Value), v => _partWear = Parse(v));
            _brushSession = MpApi.RegisterSetting(mod, "brushradiators", () => Format(_brushRadiatorsConfig.Value), v => _brushRadiators = Parse(v));
            _rateSession = MpApi.RegisterSetting(mod, "wearrate", () => FormatRate(_rateConfig.Value), v => _rate = ParseRate(v));
        }

        internal static string Format(bool on) => on ? "on" : "off";
        internal static bool Parse(string value) => value == "on";
        internal static string FormatRate(float rate) => rate.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
        /// A host's rate, kept within the config's range; anything unreadable is the normal rate.
        internal static float ParseRate(string value) =>
            float.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float r) && r >= 0.1f && r <= 1000f ? r : 1f;
    }
}
