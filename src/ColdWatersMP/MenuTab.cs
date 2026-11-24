// Cold Waters MP - a MULTIPLAYER button on DotMod's main menu that opens the multiplayer window. C# 5 / .NET 3.5.
// The button is a copy of the menu's own Options button, so it looks and lays out like the others.
using HarmonyLib;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace ColdWatersMP
{
    public static class MenuTab
    {
        public static System.Action OpenWindow;   // set by the plugin
        static GameObject _button;

        public static void AddButton(DotModMainMenuController menu)
        {
            if (_button != null) return; // already there (Initialise runs each time the menu opens)
            Button template = null;
            foreach (Button b in menu.GetComponentsInChildren<Button>(true))
            {
                int n = b.onClick.GetPersistentEventCount();
                for (int i = 0; i < n; i++)
                    if (b.onClick.GetPersistentMethodName(i) == "On_Options") template = b;
                if (template != null) break;
            }
            if (template == null) { Duel.Log("Main menu: Options button not found, no MULTIPLAYER button (use Ctrl+F6)"); return; }

            GameObject go = (GameObject)Object.Instantiate(template.gameObject);
            go.name = "CWMP_MultiplayerButton";
            go.transform.SetParent(template.transform.parent, false);
            go.transform.SetSiblingIndex(template.transform.GetSiblingIndex()); // just above Options
            TextLocaliser loc = go.GetComponentInChildren<TextLocaliser>();
            if (loc != null) loc.enabled = false; // keep our own label
            Text label = go.GetComponentInChildren<Text>();
            if (label != null) label.text = "MULTIPLAYER";
            Button btn = go.GetComponent<Button>();
            btn.onClick = new Button.ButtonClickedEvent();
            btn.onClick.AddListener(new UnityAction(delegate { if (OpenWindow != null) OpenWindow(); }));
            go.SetActive(true);
            _button = go;
            Duel.Log("Main menu: MULTIPLAYER button added");
        }
    }

    [HarmonyPatch(typeof(DotModMainMenuController), "Initialise")]
    static class Patch_MainMenuInitialise
    {
        static void Postfix(DotModMainMenuController __instance)
        {
            try { MenuTab.AddButton(__instance); }
            catch (System.Exception ex) { Duel.Log("Main menu button failed: " + ex.Message + " (use Ctrl+F6)"); }
        }
    }
}
