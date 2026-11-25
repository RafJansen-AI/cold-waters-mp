// Cold Waters MP - fix for a DotMod HUD bug (also in single player). TextParser.BuildHUD runs at every mission start.
// With ToolbarsPos z = 0 ("no anchor", what all DotMod HUD files use) it parents the helm toolbars to their fixed
// anchor only if they aren't there yet; otherwise it falls into the next branch and parents them to the minimap with
// a -232.5 shift. So every second mission of a session the bottom panels follow the minimap and drop when the map
// opens. Here: if a build moved them from the anchor to the minimap, move them back and undo the shift.
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
