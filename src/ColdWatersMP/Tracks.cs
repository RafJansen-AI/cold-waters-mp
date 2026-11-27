// Cold Waters MP - contact tracks for the opponent's sub and its decoy ghosts. C# 5 / .NET 3.5.
// The game gives every vessel slot a Sierra number at its first detection (SensorManager.ContactDetected, when
// initialDetectedByPlayerName[i] is null) and keeps it forever. A decoy ghost is its own slot, so it always came up as
// a new Sierra, a giveaway when the real sub had been heard before. One rule for the puppet and every ghost:
//  - a lost track is dropped once its solution has decayed below the map's course threshold (TacticalMap.qualityToCourse),
//    the point where the game would show it again as a plain circle (Raf, 27 Nov: follow the game, no fixed timer).
//    The game takes 10-15 points off at the loss and 4-10 at every sonar check (8 s) after it, floor 1-10%.
//    Its next detection is a new contact with a new number, and a faded "S1 Barbel?" marker stays at its last position.
//    A contact that is still detected keeps its number, however poor its solution;
//  - a first detection within BearingLimit of a lost track that has not been dropped yet continues that track (the
//    game then calls "regained contact"), and the old slot's track is dropped.
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
        public const float BearingLimit = 15f;  // degrees between the new contact and the lost track's last bearing
        static bool _thresholdsLogged;

        static readonly Dictionary<int, float> _lostAt = new Dictionary<int, float>();
        static readonly Dictionary<int, float> _lastBrg = new Dictionary<int, float>();
        static readonly List<int> _slots = new List<int>();
        // Slots our sonar has detected since the duel began. The mission's own load-time sonar checks name contacts
        // ("designate Sierra 1") before the duel moves the subs; those were never seen in the duel and are cleared
        // quietly, without a last-known marker at a position that means nothing (seen 27 Nov).
        static readonly HashSet<int> _seen = new HashSet<int>();

        static SensorManager SM { get { return UIFunctions.globaluifunctions.playerfunctions.sensormanager; } }

        // A dropped track leaves a last-known marker: one of the game's own nav markers (the shift-click cross) in the
        // contact's faded colour, labelled "S1 Barbel?" (Raf, 26 and 27 Nov). It sits in TacticalMap.navMarkers, so the
        // player removes it with shift-click or "delete all" like any nav marker. While it is on the map, a new opponent
        // contact within BearingLimit of its bearing revives the track (name, classification, the game's "regained
        // contact"), however long ago it was dropped: the player decides by keeping or deleting the marker.
        // Map zoom only changes the camera; the game resizes hazard icons and contact labels on zoom (ScaleHazards,
        // TacticalMapZoom). The label is a copy of the contact's own map marker with its icon hidden (a label inside the
        // nav marker came out tiny and on top of the cross, Raf 27 Nov), and takes the slot label's scale each frame.
        class LastKnown
        {
            public GameObject Marker, Holder; public Transform Label; public RectTransform Ship;
            public string Name, Text, TextShort; public bool Classified; public int ClassAs = -1, Slot = -1;
            public float X, Z;
        }
        static readonly List<LastKnown> _lastKnown = new List<LastKnown>();

        public static void Reset()
        {
            _lostAt.Clear(); _lastBrg.Clear(); _seen.Clear(); _thresholdsLogged = false;
            foreach (LastKnown lk in _lastKnown) if (lk.Holder != null) Object.Destroy(lk.Holder);
            _lastKnown.Clear(); // the crosses are the game's nav markers: its own mission cleanup removes them
        }

        /// <summary>Place a nav marker at the dropped contact's map position, faded in its colour, with a "name?" label.</summary>
        static void LeaveLastKnown(MapContact mc, LastKnown lk)
        {
            TacticalMap tm = SM.tacticalmap;
            if (tm == null || tm.zoomFactor == 0f || tm.navMarkers == null) return;
            Vector3 lp = mc.transform.localPosition; // where the map drew the contact: true or estimated position
            lk.X = lp.x / tm.zoomFactor; lk.Z = lp.y / tm.zoomFactor;
            int free = -1;
            for (int k = 0; k < tm.navMarkers.Length; k++)
            {
                GameObject m = tm.navMarkers[k];
                if (m == null) { if (free < 0) free = k; continue; }
                // PlaceHazardIconOnMap deletes a marker this close instead of placing one (that's its shift-click toggle)
                if (Vector3.Distance(m.transform.localPosition, new Vector3(lp.x, lp.y, -5f)) < 2f * tm.orthFactor) { lk.X += 3f * tm.orthFactor / tm.zoomFactor; }
            }
            if (free < 0) { Duel.Log("~Last known marker for " + lk.Text + " not placed: all " + tm.navMarkers.Length + " nav markers in use"); return; }
            Color saved = TacticalMapIcon.navMarkerColor;
            Color c = mc.shipDisplayIcon != null ? mc.shipDisplayIcon.color : saved; c.a = 0.6f;
            TacticalMapIcon.navMarkerColor = c;
            try { tm.PlaceHazardIconOnMap(new Vector3(lk.X, 1000f, lk.Z), 4); }
            finally { TacticalMapIcon.navMarkerColor = saved; }
            lk.Marker = tm.navMarkers[free];
            if (lk.Marker == null) { Duel.Log("~Last known marker for " + lk.Text + " not placed"); return; }
            lk.Marker.name = "CWMP last known " + lk.Text;
            // The cross is only the handle (slot, shift-click delete): invisible. What shows is a faded copy of the
            // contact's own map marker (icon and label, as the game draws contacts) at the same spot.
            Image handle = lk.Marker.GetComponent<Image>();
            if (handle != null) { Color hc0 = handle.color; hc0.a = 0f; handle.color = hc0; }
            GameObject h = (GameObject)Object.Instantiate(mc.gameObject);
            h.transform.SetParent(mc.transform.parent, false);
            h.transform.localRotation = mc.transform.localRotation;
            h.transform.localScale = mc.transform.localScale;
            h.transform.localPosition = new Vector3(lk.X * tm.zoomFactor, lk.Z * tm.zoomFactor, -5f);
            h.name = "CWMP last known " + lk.Text;
            MapContact hc = h.GetComponent<MapContact>();
            Text txt = hc != null && hc.contactText != null ? hc.contactText : h.GetComponentInChildren<Text>(true);
            Image icon = hc != null ? hc.shipDisplayIcon : null;
            lk.Ship = hc != null ? hc.shipRectTransform : null;
            if (hc != null) Object.Destroy(hc);
            foreach (Button b in h.GetComponentsInChildren<Button>(true)) Object.Destroy(b);
            foreach (LineRenderer l in h.GetComponentsInChildren<LineRenderer>(true)) l.enabled = false;
            if (txt != null) { txt.text = lk.Text + "?"; txt.raycastTarget = false; Color tc = txt.color; tc.a = 0.45f; txt.color = tc; lk.Label = txt.transform; }
            if (icon != null) { Color ic = icon.color; ic.a = 0.35f; icon.color = ic; icon.raycastTarget = false; }
            h.SetActive(true);
            lk.Holder = h;
            SizeLastKnown(lk, tm);
            _lastKnown.Add(lk);
            Duel.Log("~Last known marker \"" + lk.Text + "?\" left at map " + lp.x.ToString("F0") + "," + lp.y.ToString("F0") + " (nav marker " + (free + 1) + " of " + tm.navMarkers.Length + ")");
        }

        /// <summary>Forget markers the player deleted; keep the labels the size of the game's contact labels.</summary>
        static void UpdateLastKnown()
        {
            if (_lastKnown.Count == 0) return;
            TacticalMap tm = SM.tacticalmap;
            for (int k = _lastKnown.Count - 1; k >= 0; k--)
            {
                LastKnown lk = _lastKnown[k];
                if (lk.Marker == null)
                {
                    Duel.Log("~Last known marker \"" + lk.Text + "?\" removed by the player");
                    if (lk.Holder != null) Object.Destroy(lk.Holder);
                    _lastKnown.RemoveAt(k); continue;
                }
                SizeLastKnown(lk, tm);
            }
        }

        /// <summary>What TacticalMapZoom does to every mapContact on a zoom: label scale and offset, icon size.</summary>
        static void SizeLastKnown(LastKnown lk, TacticalMap tm)
        {
            if (tm == null) return;
            float t = tm.textMinMaxSize, icon = tm.iconSize * tm.orthFactor;
            if (lk.Label != null)
            {
                lk.Label.localScale = new Vector3(t, t, t);
                lk.Label.localPosition = new Vector3(0f, tm.textYOffset * tm.orthFactor, 0f);
            }
            if (lk.Ship != null) lk.Ship.sizeDelta = new Vector2(icon, icon);
        }

        /// <summary>The live last-known marker nearest in bearing to brg (within BearingLimit), or null.</summary>
        static LastKnown NearestLastKnown(float brg, out float diff)
        {
            diff = BearingLimit;
            LastKnown best = null;
            Vessel own = GameDataManager.playervesselsonlevel != null && GameDataManager.playervesselsonlevel.Length > 0 ? GameDataManager.playervesselsonlevel[0] : null;
            if (own == null) return null;
            foreach (LastKnown lk in _lastKnown)
            {
                if (lk.Marker == null) continue;
                float b = Mathf.Atan2(lk.X - own.transform.position.x, lk.Z - own.transform.position.z) * Mathf.Rad2Deg;
                float d = Mathf.Abs(Mathf.DeltaAngle(brg, b));
                if (d <= diff) { diff = d; best = lk; }
            }
            return best;
        }

        /// <summary>A new contact on slot `to` takes back a dropped track: name, classification, label; the marker goes.</summary>
        static void Revive(LastKnown lk, int to, float diff)
        {
            SensorManager sm = SM;
            sm.initialDetectedByPlayerName[to] = lk.Name;
            if (sm.initialDetectedByPlayer != null) sm.initialDetectedByPlayer[to] = true;
            if (sm.classifiedByPlayer != null) sm.classifiedByPlayer[to] = lk.Classified;
            if (sm.classifiedByPlayerAsClass != null && lk.ClassAs >= 0) sm.classifiedByPlayerAsClass[to] = lk.ClassAs;
            MapContact[] mcs = sm.tacticalmap.mapContact;
            if (to < mcs.Length && mcs[to] != null)
            {
                if (lk.Text != null) mcs[to].contactText.text = lk.Text;
                if (lk.TextShort != null) mcs[to].contactTextShort = lk.TextShort;
            }
            // the old track's solution, decayed since the loss, as for a real sub regained after a while
            float carried = float.NaN;
            if (lk.Slot >= 0 && lk.Slot != to && !HasName(lk.Slot) && !Detected(lk.Slot))
            {
                carried = CopySlot(sm.solutionQualityOfContacts, lk.Slot, to);
                CopySlot(sm.timeTrackingContact, lk.Slot, to);
                CopySlot(sm.solutionRangeErrors, lk.Slot, to);
            }
            if (lk.Marker != null) Object.Destroy(lk.Marker);
            if (lk.Holder != null) Object.Destroy(lk.Holder);
            _lastKnown.Remove(lk);
            try { if (lk.Classified) SetContactName.Invoke(sm, new object[] { to }); }
            catch (System.Exception ex) { Duel.Log("~Track name refresh failed: " + ex.Message); }
            Duel.Log("~Track " + lk.Name + " revived on slot " + to + " from its last-known marker (" + diff.ToString("F0") + " deg off"
                + (float.IsNaN(carried) ? "" : ", solution " + carried.ToString("F0") + "%") + ")");
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

        /// <summary>Solution quality of slot i (0..100), or NaN.</summary>
        static float Quality(int i)
        {
            float[] q = SM.solutionQualityOfContacts;
            return q != null && i >= 0 && i < q.Length ? q[i] : float.NaN;
        }

        /// <summary>Every frame in a duel: note when tracks are lost and drop the ones whose solution has decayed to a circle.</summary>
        public static void Update()
        {
            if (!Duel.InDuel) return;
            UpdateLastKnown();
            TacticalMap tm = SM.tacticalmap;
            if (tm == null || !Duel.PlacementApplied) return; // before placement every position is the mission's, not the duel's
            if (!_thresholdsLogged)
            {
                _thresholdsLogged = true;
                Duel.Log("~Tracks: map thresholds course " + tm.qualityToCourse.ToString("F0") + "%, true position/tails " + tm.qualityToDrawTails.ToString("F0") + "%; a lost track is dropped below " + tm.qualityToCourse.ToString("F0") + "%");
            }
            Ghosts.OpponentSlots(_slots);
            foreach (int i in _slots)
            {
                if (!HasName(i)) { _lostAt.Remove(i); continue; }
                if (Detected(i))
                {
                    _seen.Add(i);
                    _lostAt.Remove(i);
                    float b = BearingTo(i);
                    if (!float.IsNaN(b)) _lastBrg[i] = b;
                    continue;
                }
                if (!_seen.Contains(i)) { Drop(i, "named before the duel began, never detected in it", false); continue; }
                float at;
                if (!_lostAt.TryGetValue(i, out at)) { _lostAt[i] = at = Time.time; }
                float q = Quality(i);
                if (!float.IsNaN(q) && q < tm.qualityToCourse)
                    Drop(i, "lost " + (Time.time - at).ToString("F0") + " s, solution " + q.ToString("F0") + "% below the course threshold " + tm.qualityToCourse.ToString("F0") + "%");
            }
        }

        /// <summary>Forget slot i's track: no name, no classification, marker hidden. Its next detection is a new contact.</summary>
        public static void Drop(int i, string why) { Drop(i, why, true); }

        static void Drop(int i, string why, bool leaveMarker)
        {
            SensorManager sm = SM;
            if (!HasName(i)) return;
            string name = sm.initialDetectedByPlayerName[i];
            MapContact mc = sm.tacticalmap != null && i < sm.tacticalmap.mapContact.Length ? sm.tacticalmap.mapContact[i] : null;
            LastKnown lk = null;
            if (leaveMarker && mc != null && !Detected(i))
                lk = new LastKnown
                {
                    Name = name, Slot = i,
                    Classified = sm.classifiedByPlayer != null && sm.classifiedByPlayer[i],
                    ClassAs = sm.classifiedByPlayerAsClass != null ? sm.classifiedByPlayerAsClass[i] : -1,
                    Text = mc.contactText != null ? Ghosts.StripTags(mc.contactText.text) : name,
                    TextShort = Ghosts.StripTags(mc.contactTextShort)
                };
            sm.initialDetectedByPlayerName[i] = null;
            if (sm.initialDetectedByPlayer != null) sm.initialDetectedByPlayer[i] = false;
            if (sm.classifiedByPlayer != null) sm.classifiedByPlayer[i] = false;
            if (sm.identifiedByPlayer != null) sm.identifiedByPlayer[i] = false;
            Ghosts.ClearMarks(i); // back to the game's own label first ("decoy?" notes don't carry over)
            if (mc != null && !Detected(i))
            {
                if (lk != null) try { LeaveLastKnown(mc, lk); }
                catch (System.Exception ex) { Duel.Log("~Last-known marker failed: " + ex.Message); }
                mc.gameObject.SetActive(false);
            }
            _lostAt.Remove(i); _lastBrg.Remove(i);
            Duel.Log("~Track " + name + " (slot " + i + ") dropped: " + why);
        }

        /// <summary>ContactDetected prefix: a first detection near a recently lost opponent track continues that track.</summary>
        public static void BeforeContactDetected(int i)
        {
            if (!Duel.InDuel) return;
            // Count every detection after placement as seen: under time compression a contact can be detected and lost
            // again between two rendered frames, so Update alone could miss it.
            if (Duel.PlacementApplied) _seen.Add(i);
            if (HasName(i)) return;
            Ghosts.OpponentSlots(_slots);
            if (!_slots.Contains(i)) return; // neutrals keep the game's own numbering
            float brg = BearingTo(i);
            if (float.IsNaN(brg)) return;
            int donor = -1; float best = BearingLimit;
            string rejected = "";
            foreach (int d in _slots)
            {
                float at, last;
                if (d == i || !HasName(d) || Detected(d) || Ghosts.IsUncloaked(d)) continue;
                if (!_lostAt.TryGetValue(d, out at)) continue; // not seen lost yet (lost this very frame): skip
                if (!_lastBrg.TryGetValue(d, out last)) continue;
                float diff = Mathf.Abs(Mathf.DeltaAngle(brg, last));
                if (diff <= best) { best = diff; donor = d; }
                else rejected += " " + SM.initialDetectedByPlayerName[d] + " (lost " + (Time.time - at).ToString("F0") + " s, last brg " + last.ToString("F0") + ", " + diff.ToString("F0") + " deg off)";
            }
            if (donor >= 0) { Transfer(donor, i, best); return; }
            float lkDiff;
            LastKnown lk = NearestLastKnown(brg, out lkDiff);
            if (lk != null) { Revive(lk, i, lkDiff); return; }
            if (rejected.Length > 0 || _lastKnown.Count > 0)
                Duel.Log("~Track: new contact on slot " + i + " at brg " + brg.ToString("F0") + " is not near a lost track or last-known marker:" + rejected
                    + (_lastKnown.Count > 0 ? " (" + _lastKnown.Count + " marker(s) further off)" : ""));
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
            // The track's solution goes with it: a regained real sub keeps its (decayed) solution, so a continued track
            // starting from the new slot's own few percent was a tell (seen 27 Nov: 4% where the old track had 74%).
            float carried = CopySlot(sm.solutionQualityOfContacts, from, to);
            CopySlot(sm.timeTrackingContact, from, to);
            CopySlot(sm.solutionRangeErrors, from, to);
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
            Duel.Log("~Track " + name + " continues on slot " + to + " (was slot " + from + ", " + diff.ToString("F0") + " deg from its last bearing, solution " + carried.ToString("F0") + "%)");
        }

        static float CopySlot(float[] a, int from, int to)
        {
            if (a == null || from < 0 || to < 0 || from >= a.Length || to >= a.Length) return float.NaN;
            a[to] = a[from];
            return a[to];
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
                b.Append("slot " + i + " " + (HasName(i) ? sm.initialDetectedByPlayerName[i] : "-") + " det " + Detected(i) + " sig " + sig.ToString("F1") + " " + kyd.ToString("F1") + " kyd sol " + Quality(i).ToString("F0") + "%" + lost);
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
