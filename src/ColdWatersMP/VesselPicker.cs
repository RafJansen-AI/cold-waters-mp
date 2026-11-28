// Cold Waters MP - a vessel picker for the multiplayer vessel choice. C# 5 / .NET 3.5.
// In multiplayer every sub and player ship can be chosen (Match.cs widens the mission's playerVesselList to 111), and
// the game's selection screen (the museum viewer, MissionManager.SelectShip) only steps through them one at a time with
// next/previous (Raf, 28 Nov: "a huge list you have to click through"). This panel lists them all, with a search box
// and nation and sub/ship filters; a click jumps the game's viewer to that vessel the way next/previous do
// (KeybindManagerMuseum.GetNextMuseumItem: currentShipForSelection, LevelLoadManager.currentMuseumItem,
// BackgroundMuseumRender). The game's own Select button then takes it as usual.
using System.Collections.Generic;
using UnityEngine;

namespace ColdWatersMP
{
    public static class VesselPicker
    {
        class Entry { public int Index, Id; public string Nation, Name, Search; public bool Ship; }

        static List<Entry> _entries;
        static int[] _builtFor;
        static List<string> _nations = new List<string>();
        static string _nation = "", _filter = "";
        static int _kind;                 // 0 all, 1 subs, 2 ships
        static bool _collapsed;
        static Vector2 _scroll;
        static Rect _rect = new Rect(10, 70, 330, 600);
        static GUIStyle _current;

        static readonly Dictionary<string, string> NationNames = new Dictionary<string, string>
        {
            { "usn", "USA" }, { "wp", "USSR" }, { "uk", "UK" }, { "rn", "UK" }, { "aus", "Australia" }, { "knm", "Norway" },
            { "plan", "China" }, { "fn", "France" }, { "fr", "France" }, { "mn", "France" }, { "de", "Germany" }, { "bm", "Germany" },
            { "nl", "Netherlands" }, { "rnln", "Netherlands" }, { "rnn", "Netherlands" }, { "can", "Canada" }, { "rcn", "Canada" }, { "it", "Italy" }, { "mm", "Italy" },
            { "jp", "Japan" }, { "jmsdf", "Japan" }, { "swe", "Sweden" }, { "hswms", "Sweden" }, { "ind", "India" }, { "ins", "India" }
        };

        static string NationOf(string prefab)
        {
            int u = prefab.IndexOf('_');
            string p = u > 0 ? prefab.Substring(0, u) : prefab;
            string n;
            return NationNames.TryGetValue(p.ToLowerInvariant(), out n) ? n : p.ToUpperInvariant();
        }

        /// <summary>The selection screen is up, in a multiplayer session, with the widened list.</summary>
        static bool Showing(out KeybindManagerMuseum kbm, out int[] units)
        {
            kbm = null; units = null;
            if (!GameDataManager.missionMode || Duel.Session == null || Duel.Session.State != SessionState.Ready) return false;
            if (!KeybindManagerMuseum.selectionScreen || UIFunctions.globaluifunctions == null) return false;
            kbm = UIFunctions.globaluifunctions.keybindManagerMuseum;
            if (kbm == null || !kbm.gameObject.activeInHierarchy) return false;
            units = kbm.unitsToDisplay;
            return units != null && units.Length > 1;
        }

        static void Build(int[] units)
        {
            _entries = new List<Entry>();
            HashSet<string> nations = new HashSet<string>();
            DatabaseShipData[] db = UIFunctions.globaluifunctions.database.databaseshipdata;
            for (int i = 0; i < units.Length; i++)
            {
                int id = units[i];
                if (id < 0 || id >= db.Length || db[id] == null) continue;
                DatabaseShipData d = db[id];
                string prefab = d.shipPrefabName ?? "";
                bool ship = Ships.IsPlayerShip(prefab);
                string cls = string.IsNullOrEmpty(d.shipclass) ? prefab : d.shipclass;
                string des = !string.IsNullOrEmpty(d.shipDisplayDesignation) ? d.shipDisplayDesignation : d.shipDesignation;
                Entry e = new Entry { Index = i, Id = id, Nation = NationOf(prefab), Ship = ship, Name = cls + (string.IsNullOrEmpty(des) ? "" : " (" + des + ")") };
                e.Search = (e.Name + " " + e.Nation + " " + prefab).ToLowerInvariant();
                _entries.Add(e);
                nations.Add(e.Nation);
            }
            _entries.Sort((a, b) =>
            {
                int c = string.Compare(a.Nation, b.Nation, System.StringComparison.Ordinal);
                if (c != 0) return c;
                if (a.Ship != b.Ship) return a.Ship ? 1 : -1;
                return string.Compare(a.Name, b.Name, System.StringComparison.OrdinalIgnoreCase);
            });
            _nations = new List<string>(nations);
            _nations.Sort();
            _builtFor = units;
        }

        static void Select(KeybindManagerMuseum kbm, int[] units, Entry e)
        {
            if (e.Index < 0 || e.Index >= units.Length) return;
            kbm.currentShipForSelection = e.Index;
            LevelLoadManager llm = UIFunctions.globaluifunctions.levelloadmanager;
            llm.currentMuseumItem = units[e.Index];
            llm.BackgroundMuseumRender(false);
        }

        public static void OnGUI()
        {
            KeybindManagerMuseum kbm; int[] units;
            try { if (!Showing(out kbm, out units)) return; }
            catch (System.Exception) { return; }
            if (_entries == null || _builtFor != units) { try { Build(units); } catch (System.Exception ex) { Duel.Log("~Vessel picker failed: " + ex.Message); return; } }
            if (_current == null) { _current = new GUIStyle(GUI.skin.button); _current.normal.textColor = Color.yellow; _current.fontStyle = FontStyle.Bold; }
            if (_collapsed)
            {
                if (GUI.Button(new Rect(_rect.x, _rect.y, 160, 26), "Vessel list (" + _entries.Count + ")")) _collapsed = false;
                return;
            }
            _rect.height = Mathf.Min(_rect.height, Screen.height - _rect.y - 10);
            _rect = GUILayout.Window(0xC3A2, _rect, w => Panel(kbm, units), "Choose a vessel (" + _entries.Count + ")");
        }

        static void Panel(KeybindManagerMuseum kbm, int[] units)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label("Search", GUILayout.Width(50));
            _filter = GUILayout.TextField(_filter ?? "");
            if (GUILayout.Button("x", GUILayout.Width(22))) _filter = "";
            if (GUILayout.Button("Hide", GUILayout.Width(44))) _collapsed = true;
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            _kind = GUILayout.Toolbar(_kind, new[] { "All", "Subs", "Ships" });
            GUILayout.EndHorizontal();

            // Nations: a wrapping row of toggles (All + every nation in the list).
            List<string> opts = new List<string> { "All" };
            opts.AddRange(_nations);
            int sel = _nation.Length == 0 ? 0 : Mathf.Max(0, opts.IndexOf(_nation));
            int now = GUILayout.SelectionGrid(sel, opts.ToArray(), 4);
            _nation = now <= 0 ? "" : opts[now];

            int current = kbm.currentShipForSelection;
            string f = (_filter ?? "").Trim().ToLowerInvariant();
            _scroll = GUILayout.BeginScrollView(_scroll);
            string lastNation = null;
            int shown = 0;
            foreach (Entry e in _entries)
            {
                if (_kind == 1 && e.Ship) continue;
                if (_kind == 2 && !e.Ship) continue;
                if (_nation.Length > 0 && e.Nation != _nation) continue;
                if (f.Length > 0 && !e.Search.Contains(f)) continue;
                if (e.Nation != lastNation) { GUILayout.Label("<b>" + e.Nation + "</b>"); lastNation = e.Nation; }
                bool isCurrent = e.Index == current;
                if (GUILayout.Button((e.Ship ? "Ship  " : "Sub   ") + e.Name, isCurrent ? _current : GUI.skin.button))
                {
                    try { Select(kbm, units, e); }
                    catch (System.Exception ex) { Duel.Log("~Vessel picker select failed: " + ex.Message); }
                }
                shown++;
            }
            if (shown == 0) GUILayout.Label("No vessel matches.");
            GUILayout.EndScrollView();
            GUILayout.Label("Click a vessel, then the game's Select button.");
            GUI.DragWindow();
        }
    }
}
