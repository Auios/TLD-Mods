using HarmonyLib;

namespace MountAnything
{
    /// Carried items with an R of their own (pickupable.ammo: R pours a jerrycan into the can or tank looked at, reloads
    /// the gun in the other hand from a magazine or box, fills it from the same ammo in the inventory) get the game's
    /// glue like every other item, so G, R's collision toggle and all of Shift+R's modes work for them too. R stays theirs
    /// where the game offers it:
    ///  - R: while the game has a target for the item's own R (fpscontroller.canReload, this frame or the last: the game
    ///    works it out partly after the held-item code), R does that and the collision line is not shown; elsewhere R
    ///    toggles collision as for every item.
    ///  - Shift+R: only the attach mode. The game's pour/reload sees no R from that press until R is let go.
    internal static class OwnRKeys
    {
        private static bool _targetLastFrame;   // canReload at the end of the last frame's player work
        private static bool _shiftHold;         // R went down with Shift for such an item: R isn't theirs until let go

        /// R presses left to the item's own R (the collision toggle undone); for the test.
        internal static long Kept;
        /// Set by the unattended test: R goes down in the next held-item frame (TestR), and the game has a target for the
        /// item's own R (TestTarget).
        internal static bool TestR, TestTarget;

        private static bool Applies(fpscontroller pl, pickupable pP, bool hand) =>
            Plugin.Enabled.Value && pl != null && !hand && pP != null && pP == pl.pickedUp && Mounting.OwnR(pP) &&
            pP.tosave != null && pP.tosave.attachable != null && !pP.tosave.attachable.attached;

        internal struct Saved { public bool Active, Toggle, CanR, Reload, ReloadUp, Hid; public string R; }

        private static void Hide(ref Saved s)
        {
            var inp = inputscript.i;
            if (!_shiftHold || inp == null) return;
            s.Hid = true; s.Reload = inp.reload; s.ReloadUp = inp.reloadUp;
            inp.reload = false; inp.reloadUp = false;
        }

        private static void Show(Saved s)
        {
            var inp = inputscript.i;
            if (!s.Hid || inp == null) return;
            inp.reload = s.Reload; inp.reloadUp = s.ReloadUp;
        }

        [HarmonyPatch(typeof(fpscontroller), nameof(fpscontroller.usePickedUpStuff))]
        private static class Use
        {
            [HarmonyPrefix]
            private static void Prefix(fpscontroller __instance, pickupable pP, bool hand, out Saved __state)
            {
                __state = default;
                var inp = inputscript.i;
                if (inp == null || !Applies(__instance, pP, hand)) return;
                if (TestR) { TestR = false; inp.reloadDown = true; inp.run = false; }
                __state.Active = true;
                __state.Toggle = __instance.attachableColliderDisableToggle;
                __state.CanR = __instance.canR; __state.R = __instance.R;
                if (inp.run && inp.reloadDown) _shiftHold = true;
                Hide(ref __state);
            }

            [HarmonyPostfix]
            private static void Postfix(fpscontroller __instance, pickupable pP, Saved __state)
            {
                if (!__state.Active) return;
                Show(__state);
                if (!(__instance.canReload || _targetLastFrame || TestTarget)) return;
                // the item's own R: no collision toggle, and its line instead of "toggle collision"
                if (__instance.attachableColliderDisableToggle != __state.Toggle)
                {
                    __instance.attachableColliderDisableToggle = __state.Toggle;
                    pP.tosave.attachable.SetCollider(!__state.Toggle, _childrenToo: true);
                    Kept++;
                }
                __instance.R = __state.R; __instance.canR = __state.CanR;
            }
        }

        private static bool Carrying(fpscontroller pl) => pl != null && pl.pickedUp != null && Mounting.OwnR(pl.pickedUp);

        [HarmonyPatch(typeof(fpscontroller), nameof(fpscontroller.RayStuff))]
        private static class Ray
        {
            [HarmonyPrefix]
            private static void Prefix(fpscontroller __instance, out Saved __state) { __state = default; if (Carrying(__instance)) Hide(ref __state); }

            [HarmonyPostfix]
            private static void Postfix(Saved __state) => Show(__state);
        }

        [HarmonyPatch(typeof(fpscontroller), nameof(fpscontroller.ReloadStuff))]
        private static class Reload
        {
            [HarmonyPrefix]
            private static void Prefix(fpscontroller __instance, out Saved __state) { __state = default; if (Carrying(__instance)) Hide(ref __state); }

            /// The end of the player's frame: whether the game has a target for the carried item's own R, and whether a
            /// Shift+R press has been let go.
            [HarmonyPostfix]
            private static void Postfix(fpscontroller __instance, Saved __state)
            {
                Show(__state);
                if (mainscript.s == null || __instance != mainscript.s.player) return;
                _targetLastFrame = __instance.canReload;
                var inp = inputscript.i;
                if (inp != null && !inp.reload && !inp.reloadUp && !inp.reloadDown) _shiftHold = false;
            }
        }
    }
}
