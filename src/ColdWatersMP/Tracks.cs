// Cold Waters MP - contact tracks for the opponent's sub and its decoy ghosts. C# 5 / .NET 3.5.
// The game gives every vessel slot a Sierra number at its first detection (SensorManager.ContactDetected, when
// initialDetectedByPlayerName[i] is null) and keeps it forever. A decoy ghost is its own slot, so it always came up as
// a new Sierra, a giveaway when the real sub had been heard before. One rule for the puppet and every ghost:
//  - a track lost for longer than LostLimit is dropped: its next detection is a new contact with a new number, and a
//    faded "S1 Barbel?" marker stays at its last known position;
//  - a first detection within BearingLimit of a track lost less than LostLimit ago continues that track (the game
//    then calls "regained contact"), and the old slot's track is dropped.
// So a new Sierra of the opponent's class no longer tells decoy from sub, and a decoy launched after the sub was lost
// reads like the sub coming back. Not covered: a decoy launched while the real sub is tracked still gets a new number.
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

namespace ColdWatersMP
{
    public static class Tracks
    {
        public const float LostLimit = 120f;    // game seconds (Time.time follows time compression)
        public const float BearingLimit = 15f;  // degrees between the new contact and the lost track's last bearing

        static readonly Dictionary<int, float> _lostAt = new Dictionary<int, float>();
        static readonly Dictionary<int, float> _lastBrg = new Dictionary<int, float>();
        static readonly List<int> _slots = new List<int>();

        static SensorManager SM { get { return UIFunctions.globaluifunctions.playerfunctions.sensormanager; } }

        // A dropped track leaves a faded "S1 Barbel?" marker at its last known position (Raf, 26 Nov), until mission end.
        class LastKnown { public GameObject Go; public float X, Z; }
        static readonly List<LastKnown> _lastKnown = new List<LastKnown>();
        const int MaxLastKnown = 12;

        public static void Reset()
        {
            _lostAt.Clear(); _lastBrg.Clear();
            foreach (LastKnown lk in _lastKnown) if (lk.Go != null) Object.Destroy(lk.Go);
            _lastKnown.Clear();
        }

        /// <summary>A plain copy of the contact's map marker: no game logic, no button, faded, labelled "name?".</summary>
        static void LeaveLastKnown(MapContact mc, string label)
        {
            TacticalMap tm = SM.tacticalmap;
            if (tm == null || tm.zoomFactor == 0f || mc == null) return;
            bool wasShown = mc.gameObject.activeSelf; // the game may already have hidden a lost contact's marker
            Vector3 lp = mc.transform.localPosition;
            GameObject go = (GameObject)Object.Instantiate(mc.gameObject);
            go.transform.SetParent(mc.transform.parent, false);
            go.transform.localRotation = mc.transform.localRotation;
            go.transform.localScale = mc.transform.localScale;
            go.name = "CWMP last known " + label;
            MapContact c = go.GetComponent<MapContact>();
            Text txt = c != null && c.contactText != null ? c.contactText : go.GetComponentInChildren<Text>(true);
            Image icon = c != null ? c.shipDisplayIcon : null;
            if (c != null) Object.Destroy(c);
            foreach (Button b in go.GetComponentsInChildren<Button>(true)) Object.Destroy(b);
            foreach (LineRenderer l in go.GetComponentsInChildren<LineRenderer>(true)) l.enabled = false;
            if (txt != null) { txt.text = label + "?"; Color col = txt.color; col.a = 0.45f; txt.color = col; }
            if (icon != null) { Color col = icon.color; col.a = 0.35f; icon.color = col; }
            go.SetActive(true);
            _lastKnown.Add(new LastKnown { Go = go, X = lp.x / tm.zoomFactor, Z = lp.y / tm.zoomFactor });
            if (_lastKnown.Count > MaxLastKnown) { if (_lastKnown[0].Go != null) Object.Destroy(_lastKnown[0].Go); _lastKnown.RemoveAt(0); }
            PlaceLastKnown();
            Duel.Log("~Last known marker \"" + label + "?\" left at map " + lp.x.ToString("F0") + "," + lp.y.ToString("F0") + " (zoom " + tm.zoomFactor.ToString("F3") + ", contact marker was " + (wasShown ? "shown" : "hidden") + ")");
        }

        /// <summary>Keep the last-known markers where the game would draw a contact there (follows map zoom).</summary>
        static void PlaceLastKnown()
        {
            if (_lastKnown.Count == 0) return;
            TacticalMap tm = SM.tacticalmap;
            if (tm == null) return;
            float z = tm.zoomFactor;
            foreach (LastKnown lk in _lastKnown)
                if (lk.Go != null) lk.Go.transform.localPosition = new Vector3(lk.X * z, lk.Z * z, -5f);
        }

        public static bool HasName(int i)
        {
            SensorManager sm = SM;
            return sm.initialDetectedByPlayerName != null && i >= 0 && i < sm.initialDetectedByPlayerName.Length && sm.initialDetectedByPlayerName[i] != null;
        }

        static bool Detected(int i) { SensorManager sm = SM; return sm.detectedByPlayer != null && i < sm.detectedByPlayer.Length && sm.detectedByPlayer[i]; }

        static Vessel VesselAt(int i)
        {
            Vessel[] ev = GameDataManager.enemyvesselsonlevel;
            if (ev == null) return null;
            foreach (Vessel v in ev) if (v != null && v.vesselListIndex == i) return v;
            return null;
        }

        /// <summary>True bearing from our sub to vessel slot i, or NaN.</summary>
        static float BearingTo(int i)
        {
            Vessel own = GameDataManager.playervesselsonlevel != null && GameDataManager.playervesselsonlevel.Length > 0 ? GameDataManager.playervesselsonlevel[0] : null;
            Vessel v = VesselAt(i);
            if (own == null || v == null) return float.NaN;
            Vector3 d = v.transform.position - own.transform.position;
            float b = Mathf.Atan2(d.x, d.z) * Mathf.Rad2Deg;
            return b < 0f ? b + 360f : b;
        }

        /// <summary>Every frame in a duel: note when tracks are lost and drop the ones lost too long.</summary>
        public static void Update()
        {
            if (!Duel.InDuel) return;
            PlaceLastKnown();
            Ghosts.OpponentSlots(_slots);
            foreach (int i in _slots)
            {
                if (!HasName(i)) { _lostAt.Remove(i); continue; }
                if (Detected(i))
                {
                    _lostAt.Remove(i);
                    float b = BearingTo(i);
                    if (!float.IsNaN(b)) _lastBrg[i] = b;
                    continue;
                }
                float at;
                if (!_lostAt.TryGetValue(i, out at)) { _lostAt[i] = Time.time; continue; }
                if (Time.time - at > LostLimit) Drop(i, "lost for " + (LostLimit / 60f).ToString("F0") + " min");
            }
        }

        /// <summary>Forget slot i's track: no name, no classification, marker hidden. Its next detection is a new contact.</summary>
        public static void Drop(int i, string why)
        {
            SensorManager sm = SM;
            if (!HasName(i)) return;
            string name = sm.initialDetectedByPlayerName[i];
            sm.initialDetectedByPlayerName[i] = null;
            if (sm.initialDetectedByPlayer != null) sm.initialDetectedByPlayer[i] = false;
            if (sm.classifiedByPlayer != null) sm.classifiedByPlayer[i] = false;
            if (sm.identifiedByPlayer != null) sm.identifiedByPlayer[i] = false;
            Ghosts.ClearMarks(i); // back to the game's own label first ("decoy?" notes don't carry over)
            MapContact mc = sm.tacticalmap != null && i < sm.tacticalmap.mapContact.Length ? sm.tacticalmap.mapContact[i] : null;
            if (mc != null && !Detected(i))
            {
                try { LeaveLastKnown(mc, mc.contactText != null && !string.IsNullOrEmpty(mc.contactText.text) ? mc.contactText.text : name); }
                catch (System.Exception ex) { Duel.Log("~Last-known marker failed: " + ex.Message); }
                mc.gameObject.SetActive(false);
            }
            _lostAt.Remove(i); _lastBrg.Remove(i);
            Duel.Log("~Track " + name + " (slot " + i + ") dropped: " + why);
        }

        /// <summary>ContactDetected prefix: a first detection near a recently lost opponent track continues that track.</summary>
        public static void BeforeContactDetected(int i)
        {
            if (!Duel.InDuel || HasName(i)) return;
            Ghosts.OpponentSlots(_slots);
            if (!_slots.Contains(i)) return; // neutrals keep the game's own numbering
            float brg = BearingTo(i);
            if (float.IsNaN(brg)) return;
            int donor = -1; float best = BearingLimit;
            foreach (int d in _slots)
            {
                float at, last;
                if (d == i || !HasName(d) || Detected(d) || Ghosts.IsUncloaked(d)) continue;
                if (!_lostAt.TryGetValue(d, out at) || Time.time - at > LostLimit) continue;
                if (!_lastBrg.TryGetValue(d, out last)) continue;
                float diff = Mathf.Abs(Mathf.DeltaAngle(brg, last));
                if (diff <= best) { best = diff; donor = d; }
            }
            if (donor >= 0) Transfer(donor, i, best);
        }

        static void Transfer(int from, int to, float diff)
        {
            SensorManager sm = SM;
            string name = sm.initialDetectedByPlayerName[from];
            MapContact[] mcs = sm.tacticalmap.mapContact;
            Ghosts.MoveTrackMarks(from, to);
            sm.initialDetectedByPlayerName[to] = name;
            if (sm.initialDetectedByPlayer != null) sm.initialDetectedByPlayer[to] = true;
            if (sm.classifiedByPlayer != null) sm.classifiedByPlayer[to] = sm.classifiedByPlayer[from];
            if (sm.classifiedByPlayerAsClass != null) sm.classifiedByPlayerAsClass[to] = sm.classifiedByPlayerAsClass[from];
            if (to < mcs.Length && from < mcs.Length && mcs[to] != null && mcs[from] != null)
            {
                mcs[to].contactText.text = mcs[from].contactText.text;
                mcs[to].contactTextShort = mcs[from].contactTextShort;
            }
            // the old slot loses the track (and its faded marker), as if it had never been detected
            sm.initialDetectedByPlayerName[from] = null;
            if (sm.initialDetectedByPlayer != null) sm.initialDetectedByPlayer[from] = false;
            if (sm.classifiedByPlayer != null) sm.classifiedByPlayer[from] = false;
            if (sm.identifiedByPlayer != null) sm.identifiedByPlayer[from] = false;
            if (from < mcs.Length && mcs[from] != null) mcs[from].gameObject.SetActive(false);
            _lostAt.Remove(from); _lastBrg.Remove(from);
            try { if (sm.classifiedByPlayer != null && sm.classifiedByPlayer[to]) SetContactName.Invoke(sm, new object[] { to }); }
            catch (System.Exception ex) { Duel.Log("~Track name refresh failed: " + ex.Message); }
            Duel.Log("~Track " + name + " continues on slot " + to + " (was slot " + from + ", " + diff.ToString("F0") + " deg from its last bearing)");
        }

        /// <summary>For the 10 s sonar diagnostics: every opponent-side slot that is named or audible.</summary>
        public static string Describe()
        {
            SensorManager sm = SM;
            Vessel own = GameDataManager.playervesselsonlevel[0];
            Ghosts.OpponentSlots(_slots);
            System.Text.StringBuilder b = new System.Text.StringBuilder();
            foreach (int i in _slots)
            {
                float sig = sm.signalLastPassiveReading != null && i < sm.signalLastPassiveReading.Length ? sm.signalLastPassiveReading[i] : float.NaN;
                if (!HasName(i) && sig < -100f) continue;
                Vessel v = VesselAt(i);
                float kyd = v != null && own != null ? Vector3.Distance(own.transform.position, v.transform.position) * GameDataManager.yardsScale / 1000f : -1f;
                float at;
                string lost = _lostAt.TryGetValue(i, out at) ? ", lost " + (Time.time - at).ToString("F0") + " s" : "";
                if (b.Length > 0) b.Append(" | ");
                b.Append("slot " + i + " " + (HasName(i) ? sm.initialDetectedByPlayerName[i] : "-") + " det " + Detected(i) + " sig " + sig.ToString("F1") + " " + kyd.ToString("F1") + " kyd" + lost);
                if (v != null && v.acoustics != null) b.Append(" noise " + v.acoustics.currentNoise.ToString("F1"));
                b.Append(MaskCheck(own, v));
            }
            return b.Length > 0 ? b.ToString() : "none";
        }

        /// <summary>Diagnostics: the game's sonar masking raycast (SonarCheck) from our sonar towards v. A hit on layer 14
        /// divides the signal by 3..4; a hit on another vessel's collider masks it with that vessel's signal.</summary>
        static string MaskCheck(Vessel own, Vessel v)
        {
            try
            {
                if (own == null || v == null || own.acoustics == null || own.acoustics.sensorNavigator == null) return "";
                SensorManager sm = SM;
                Vector3 from = own.acoustics.sensorNavigator.position;
                Vector3 d = v.transform.position - from;
                RaycastHit hit;
                bool any;
                Ghosts.RayFocus(v); // as in SonarCheck: a ghost's own decoy doesn't mask it
                try { any = Physics.Raycast(from, d.normalized, out hit, d.magnitude, (int)sm.maskingLayers); }
                finally { Ghosts.RestoreRay(); }
                if (!any) return " mask clear";
                GameObject go = hit.collider.gameObject;
                return " mask HIT " + go.name + " layer " + go.layer + " (" + LayerMask.LayerToName(go.layer) + ") at " + (hit.distance / d.magnitude * 100f).ToString("F0") + "% [mask " + ((int)sm.maskingLayers) + "]";
            }
            catch (System.Exception ex) { return " mask ? " + ex.Message; }
        }

        static MethodInfo _setContactName;
        static MethodInfo SetContactName { get { return _setContactName ?? (_setContactName = AccessTools.Method(typeof(SensorManager), "SetContactName")); } }
    }

    [HarmonyPatch(typeof(SensorManager), "ContactDetected")]
    static class Patch_ContactDetected_Tracks
    {
        static void Prefix(int i)
        {
            try { Tracks.BeforeContactDetected(i); }
            catch (System.Exception ex) { Duel.Log("~Track check failed: " + ex.Message); }
        }
    }
}
