// Cold Waters MP - a MULTIPLAYER button on the main menu that opens the multiplayer window. C# 5 / .NET 3.5.
// Works with both main menus: DotMod's new GUI (DotModMainMenuController) and the classic menu (UIFunctions
// menuSystem page 0, used when DotMod's UseNewGUI is off). The button is a copy of the menu's own Options
// button (or, failing that, another menu button), so it looks like the others.
using HarmonyLib;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace ColdWatersMP
{
    public static class MenuTab
    {
        public static System.Action OpenWindow;   // set by the plugin
        static GameObject _newGuiButton, _classicButton;
        static bool _loggedButtons;

        static readonly string[] OptionsMethods = { "On_Options", "BringInGameOptions", "OpenOptions", "BringInOptions" };

        static string Describe(Button b)
        {
            string m = "";
            int n = b.onClick.GetPersistentEventCount();
            for (int i = 0; i < n; i++) m += (m.Length > 0 ? "," : "") + b.onClick.GetPersistentMethodName(i);
            Text t = b.GetComponentInChildren<Text>();
            return b.gameObject.name + " [" + m + "] \"" + (t != null ? t.text : "") + "\"";
        }

        static Button FindTemplate(Button[] buttons)
        {
            foreach (Button b in buttons)
            {
                int n = b.onClick.GetPersistentEventCount();
                for (int i = 0; i < n; i++)
                    if (System.Array.IndexOf(OptionsMethods, b.onClick.GetPersistentMethodName(i)) >= 0) return b;
            }
            foreach (Button b in buttons)
            {
                Text t = b.GetComponentInChildren<Text>();
                if (t != null && t.text != null && t.text.ToUpperInvariant().Contains("OPTION")) return b;
            }
            foreach (Button b in buttons)
            {
                int n = b.onClick.GetPersistentEventCount();
                for (int i = 0; i < n; i++)
                    if (b.onClick.GetPersistentMethodName(i) == "QuitApp" || b.onClick.GetPersistentMethodName(i) == "On_Quit") return b;
            }
            return null;
        }

        /// <summary>Clone a menu button next to it, labelled MULTIPLAYER. Returns the clone or null.</summary>
        static GameObject AddButton(GameObject menuRoot, string which)
        {
            Button[] buttons = menuRoot.GetComponentsInChildren<Button>(true);
            if (!_loggedButtons)
            {
                _loggedButtons = true;
                foreach (Button b in buttons) Duel.Log("~Menu button (" + which + "): " + Describe(b));
            }
            Button template = FindTemplate(buttons);
            if (template == null) { Duel.Log("Main menu (" + which + "): no Options button found, no MULTIPLAYER button (use Ctrl+F6)"); return null; }

            GameObject go = (GameObject)Object.Instantiate(template.gameObject);
            go.name = "CWMP_MultiplayerButton";
            Transform parent = template.transform.parent;
            go.transform.SetParent(parent, false);
            if (parent != null && parent.GetComponent<LayoutGroup>() != null)
                go.transform.SetSiblingIndex(template.transform.GetSiblingIndex()); // the layout puts it just above Options
            else
            {
                // Hand-placed buttons: put ours one step below the lowest button in this column.
                RectTransform rt = go.GetComponent<RectTransform>();
                RectTransform tr = template.GetComponent<RectTransform>();
                if (rt != null && tr != null)
                {
                    float lowest = tr.anchoredPosition.y, step = tr.rect.height * 1.2f, bestGap = float.MaxValue;
                    foreach (Button b in buttons)
                    {
                        if (b == template || b.transform.parent != parent) continue;
                        RectTransform br = b.GetComponent<RectTransform>();
                        if (br == null) continue;
                        if (br.anchoredPosition.y < lowest) lowest = br.anchoredPosition.y;
                        float gap = Mathf.Abs(br.anchoredPosition.y - tr.anchoredPosition.y);
                        if (gap > 1f && gap < bestGap) bestGap = gap;
                    }
                    if (bestGap < float.MaxValue) step = bestGap;
                    rt.anchoredPosition = new Vector2(tr.anchoredPosition.x, lowest - step);
                }
            }
            TextLocaliser loc = go.GetComponentInChildren<TextLocaliser>();
            if (loc != null) loc.enabled = false; // keep our own label
            Text label = go.GetComponentInChildren<Text>();
            if (label != null)
            {
                string t = template.GetComponentInChildren<Text>() != null ? template.GetComponentInChildren<Text>().text : "";
                label.text = (t != null && t.Length > 0 && t == t.ToLowerInvariant()) ? "multiplayer"
                           : (t != null && t.Length > 1 && t.Substring(1) == t.Substring(1).ToLowerInvariant()) ? "Multiplayer" : "MULTIPLAYER";
            }
            Button btn = go.GetComponent<Button>();
            btn.onClick = new Button.ButtonClickedEvent();
            btn.onClick.AddListener(new UnityAction(delegate { if (OpenWindow != null) OpenWindow(); }));
            go.SetActive(true);
            Duel.Log("Main menu (" + which + "): MULTIPLAYER button added");
            HudSelfTest.RunOnce();
            return go;
        }

        public static void OnNewGuiMenu(DotModMainMenuController menu)
        {
            if (_newGuiButton != null) return; // Initialise runs each time the menu opens
            _newGuiButton = AddButton(menu.gameObject, "DotMod");
        }

        public static void OnClassicMenu()
        {
            if (_classicButton != null) return;
            UIFunctions ui = UIFunctions.globaluifunctions;
            if (ui == null || ui.menuSystem == null || ui.menuSystem.Length == 0 || ui.menuSystem[0] == null) return;
            _classicButton = AddButton(ui.menuSystem[0], "classic");
        }
    }

    [HarmonyPatch(typeof(DotModMainMenuController), "Initialise")]
    static class Patch_MainMenuInitialise
    {
        static void Postfix(DotModMainMenuController __instance)
        {
            try { MenuTab.OnNewGuiMenu(__instance); }
            catch (System.Exception ex) { Duel.Log("Main menu button failed: " + ex.Message + " (use Ctrl+F6)"); }
        }
    }

    [HarmonyPatch(typeof(UIFunctions), "SetMenuSystem")]
    static class Patch_SetMenuSystem
    {
        static void Postfix(string menuPage)
        {
            if (menuPage != "MAINMENU") return;
            try { MenuTab.OnClassicMenu(); }
            catch (System.Exception ex) { Duel.Log("Main menu button failed: " + ex.Message + " (use Ctrl+F6)"); }
        }
    }
}
