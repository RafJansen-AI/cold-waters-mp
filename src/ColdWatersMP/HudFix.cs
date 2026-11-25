// Cold Waters MP - fix for a DotMod HUD bug (also in single player). TextParser.BuildHUD runs at every mission start.
// With ToolbarsPos z = 0 ("no anchor", what all DotMod HUD files use) it parents the helm toolbars to their fixed
// anchor only if they aren't there yet; otherwise it falls into the next branch and parents them to the minimap with
// a -232.5 shift. BuildHUD skips an unchanged HUD file, so on every change of HUD file (e.g. Soviet "default" to US
// "default_usn_towedonly") the panels flip onto the minimap and drop when the map opens. Here: if a build moved
// them from the anchor to the minimap, move them back and undo the shift.
// Limitation: a HUD file that really wants them on the minimap (z != 0) after one with z = 0 is undone too; no stock
// DotMod HUD file does that. C# 5 / .NET 3.5.
using HarmonyLib;
using UnityEngine;

namespace ColdWatersMP
{
    [HarmonyPatch(typeof(TextParser), "BuildHUD")]
    static class Patch_BuildHUD_ToolbarFix
    {
        static void Prefix(out bool __state)
        {
            __state = false;
            try
            {
                HelmManager h = UIFunctions.globaluifunctions.playerfunctions.helmmanager;
                __state = h != null && h.helmToolbars != null && h.helmToolbars.parent == h.helmmanagerAnchor;
            }
            catch (System.Exception) { }
        }

        static void Postfix(bool __state)
        {
            if (!__state) return;
            try
            {
                HelmManager h = UIFunctions.globaluifunctions.playerfunctions.helmmanager;
                if (h.helmToolbars.parent == h.helmmanagerAnchor) return; // built correctly
                h.helmToolbars.SetParent(h.helmmanagerAnchor, true); // same as the game's SetParent(x): keep world position
                h.helmToolbars.localPosition += new Vector3(0f, 232.5f, 0f);
                Duel.Log("~HUD fix: helm toolbars put back on their anchor (DotMod BuildHUD flip)");
            }
            catch (System.Exception ex) { Duel.Log("~HUD fix failed: " + ex.Message); }
        }
    }
}

namespace ColdWatersMP
{
    /// <summary>Dev only (DevKeys): log every sonar check, to measure how often the own-noise label refreshes in single
    /// player and in a duel.</summary>
    [HarmonyPatch(typeof(SensorManager), "SonarCheck")]
    static class Patch_SonarCheckTiming
    {
        static float _last = -1f;
        static void Postfix(SensorManager __instance)
        {
            if (!CwmpPlugin.DevKeysOn) return;
            try
            {
                Vessel own = GameDataManager.playervesselsonlevel != null && GameDataManager.playervesselsonlevel.Length > 0 ? GameDataManager.playervesselsonlevel[0] : null;
                float now = Time.realtimeSinceStartup;
                Duel.Log("~SonarCheck: real " + now.ToString("F2") + " (+" + (_last < 0f ? 0f : now - _last).ToString("F2") + " s), game " + Time.time.ToString("F2")
                    + ", timeScale " + Time.timeScale.ToString("F2") + ", fixedDt " + Time.fixedDeltaTime.ToString("F3") + ", timer " + __instance.timer.ToString("F2")
                    + ", own noise " + (own != null ? own.acoustics.currentNoise.ToString("F1") : "-") + ", duel " + Duel.InDuel);
                _last = now;
            }
            catch (System.Exception) { }
        }
    }
}
