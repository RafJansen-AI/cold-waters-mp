// Cold Waters MP - fix for a DotMod HUD bug (also in single player). TextParser.BuildHUD runs at every mission start.
// With ToolbarsPos z = 0 ("no anchor", what all DotMod HUD files use) it parents the helm toolbars to their fixed
// anchor only if they aren't there yet; otherwise it falls into the next branch and parents them to the minimap with
// a -232.5 shift. BuildHUD skips an unchanged HUD file, so on every second change of HUD file (e.g. Soviet "default"
// to US "default_usn_towedonly") the panels flip onto the minimap (TacMapOn), sit 43 px lower and drop when the map
// opens (seen with HudSelfTest, 26 Nov). Here: if a build moved them off the anchor, put them back at the position
// the anchor branch would have given.
// Limitation: a HUD file that really wants them on the minimap (z != 0) after one with z = 0 is undone too; no stock
// DotMod HUD file does that. C# 5 / .NET 3.5.
using HarmonyLib;
using UnityEngine;

namespace ColdWatersMP
{
    [HarmonyPatch(typeof(TextParser), "BuildHUD")]
    static class Patch_BuildHUD_ToolbarFix
    {
        public static bool Enabled = true; // HudSelfTest mode 1 turns it off to see the game's own behaviour

        // Before the build: the toolbars' local position on the anchor minus the HUD file offset then in force.
        static Vector3 _baseLocal;

        static void Prefix(out bool __state)
        {
            __state = false;
            try
            {
                HelmManager h = UIFunctions.globaluifunctions.playerfunctions.helmmanager;
                __state = h != null && h.helmToolbars != null && h.helmToolbars.parent == h.helmmanagerAnchor;
                if (__state) _baseLocal = h.helmToolbars.localPosition - UI_Offsets.ToolbarsPos;
            }
            catch (System.Exception) { }
        }

        public static string Where(HelmManager h)
        {
            Transform t = h.helmToolbars;
            return "toolbars parent " + (t.parent != null ? t.parent.name : "none") + (t.parent == h.helmmanagerAnchor ? " (anchor)" : "")
                + ", local y " + t.localPosition.y.ToString("F1");
        }

        static void Postfix(bool __state)
        {
            try
            {
                HelmManager h = UIFunctions.globaluifunctions.playerfunctions.helmmanager;
                if (h == null || h.helmToolbars == null) return;
                Duel.Log("~HUD build: was on anchor " + __state + "; now " + Where(h) + "; anchor " + (h.helmmanagerAnchor != null ? h.helmmanagerAnchor.name : "none"));
            }
            catch (System.Exception) { }
            if (!__state || !Enabled) return;
            try
            {
                HelmManager h = UIFunctions.globaluifunctions.playerfunctions.helmmanager;
                if (h.helmToolbars.parent == h.helmmanagerAnchor) return; // built correctly
                // Back on the anchor (worldPositionStays restores its own scale/rotation), then the position the game would
                // have given on the anchor: the pre-build base plus the new file's offset (BuildHUD has stored it by now).
                h.helmToolbars.SetParent(h.helmmanagerAnchor, true);
                h.helmToolbars.localPosition = _baseLocal + UI_Offsets.ToolbarsPos;
                Duel.Log("~HUD fix: helm toolbars put back on their anchor (DotMod BuildHUD flip); now " + Where(h));
            }
            catch (System.Exception ex) { Duel.Log("~HUD fix failed: " + ex.Message); }
        }
    }
}

namespace ColdWatersMP
{
    /// <summary>Dev only (DevKeys + Debug.HudSelfTest): at the first main menu, run the game's BuildHUD with alternating
    /// HUD files (as switching between a Soviet and a US sub does) and log where the helm toolbars end up after each
    /// build. Mode 1 = game's own behaviour (fix off), 2 = with the fix. Needs no input; for test copies only.</summary>
    public static class HudSelfTest
    {
        public static int Mode;
        static bool _done, _pending;
        static float _menuAt;

        /// <summary>Main menu shown: run once the ship database has loaded.</summary>
        public static void RunOnce()
        {
            if (_done || _pending || Mode <= 0 || !CwmpPlugin.DevKeysOn) return;
            _pending = true; _menuAt = Time.realtimeSinceStartup;
        }

        public static void Tick()
        {
            if (!_pending) return;
            UIFunctions ui = UIFunctions.globaluifunctions;
            bool dbReady = ui != null && ui.database != null && ui.database.databaseshipdata != null && ui.database.databaseshipdata.Length > 0;
            if (!dbReady || Time.realtimeSinceStartup - _menuAt < 3f) return;
            _pending = false;
            Run();
        }

        static void Run()
        {
            _done = true;
            Patch_BuildHUD_ToolbarFix.Enabled = Mode == 2;
            Duel.Log("HUD self-test, mode " + Mode + (Mode == 2 ? " (fix on)" : " (fix off)"));
            try
            {
                UIFunctions ui = UIFunctions.globaluifunctions;
                HelmManager h = ui.playerfunctions.helmmanager;
                // At the menu no player sub is chosen; BuildHUD reads the damage-control sprite of playerVesselClass.
                DatabaseShipData[] ships = ui.database.databaseshipdata;
                int cls = -1;
                for (int i = 0; i < ships.Length && cls < 0; i++)
                    if (ships[i] != null && !string.IsNullOrEmpty(ships[i].playerHUD) && ships[i].shipType == "SUBMARINE") cls = i;
                Duel.Log("HUD self-test: playerVesselClass " + ui.playerfunctions.playerVesselClass + " -> " + cls + (cls >= 0 ? " (" + ships[cls].shipPrefabName + ")" : "") + ", " + ships.Length + " classes");
                if (cls >= 0) ui.playerfunctions.playerVesselClass = cls;
                Duel.Log("HUD self-test: start; " + State(h));
                string[] files = { "hud/default", "hud/default_usn_towedonly", "hud/default", "hud/default_usn_towedonly", "hud/default_usn_towedonly" };
                for (int i = 0; i < files.Length; i++)
                {
                    ui.textparser.BuildHUD(ui.textparser.GetFilePathFromString(files[i]));
                    Duel.Log("HUD self-test: build " + (i + 1) + " " + files[i] + "; " + State(h));
                }
            }
            catch (System.Exception ex) { Duel.Log("HUD self-test failed: " + ex); }
            finally { Patch_BuildHUD_ToolbarFix.Enabled = true; }
        }

        static string State(HelmManager h)
        {
            Transform t = h.helmToolbars;
            Transform p0 = UI_Offsets.tacMapOnChild != null ? UI_Offsets.tacMapOnChild.parent : null;
            return Patch_BuildHUD_ToolbarFix.Where(h)
                + ", world " + t.position.ToString("F1")
                + ", initial parent " + (p0 != null ? p0.name : "none") + (p0 != null && p0 == h.helmmanagerAnchor ? " (= anchor)" : "")
                + ", anchor parent " + (h.helmmanagerAnchor != null && h.helmmanagerAnchor.parent != null ? h.helmmanagerAnchor.parent.name : "none")
                + ", initial parent's parent " + (p0 != null && p0.parent != null ? p0.parent.name : "none");
        }
    }

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
