// Cold Waters MP - mobile decoys (MOSS, MG-74 Korund, MG-14) as "ghost subs". C# 5 / .NET 3.5.
// The decoy runs in the launcher's game as a normal decoy torpedo. In the opponent's game a hidden copy of it
// follows the network (torpedo seekers there can still go for it, as in the game), and a ghost sub follows that
// copy: a second enemy vessel of the launcher's class, sounding like the launcher did at launch, so the crew
// classifies it as that sub. Its tells: steady course and speed, no reactions or transients, a limited run, and
// one signature line shifted by a pixel or two. It can't be hit and never counts as a kill.
// Ghost vessels are spawned with the mission (enemies 1..Slots) and wait far away, silent, until a decoy comes.
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace ColdWatersMP
{
    public static class Ghosts
    {
        public const int Slots = 5;               // decoys that can run at the same time per side
        const float ParkNm = 150f;                // dormant ghosts wait this far from the map centre (the map is about +-95 nm)
        const float QuietSeconds = 15f;           // silent while the decoy swims clear of the launcher
        const float FadeInSeconds = 20f, FadeOutSeconds = 30f;

        class Ghost
        {
            public Vessel V;
            public int TorpId = -1; public Torpedo T;
            public float Start, Until, FadeOutStart = -1f;
            public float Noise;                    // the launcher's noise at launch
            public Vector3 LastPos, Vel;
            public Sprite Icon;                    // the decoy's torpedo map icon, shown once it is uncloaked
        }

        static readonly List<Ghost> _ghosts = new List<Ghost>();
        static Vector3 _park;
        static System.Random _rng = new System.Random();
        static Texture2D _ghostSig; static string _ghostSigFor;
        static readonly HashSet<int> _marked = new HashSet<int>();   // contacts the player tagged as DECOY: a note only, never verified
        static readonly Dictionary<int, Sprite> _uncloaked = new Dictionary<int, Sprite>(); // ghosts our torpedo passed: shown as the decoy they are
        const string MarkSuffix = " DECOY";
        static readonly Color MarkColor = new Color(0.6f, 0.6f, 0.6f, 1f);

        // Our torpedoes that chase a decoy copy here: when one gets close and then opens again without a hit, it has
        // passed the decoy, and the crew reassesses the contact as a decoy. Only the label changes: seekers still go for it.
        class Chase { public Torpedo Torp; public Ghost G; public float MinYd = float.MaxValue; public float LastChase; }
        static readonly List<Chase> _chases = new List<Chase>();
        const float YdPerUnit = 75.13f, PassCloseYd = 100f, PassAwayYd = 60f, ChaseMemorySeconds = 10f;

        static GameObject _decoyButton;
        static bool _buttonMissingLogged;

        public static void Reset()
        {
            _ghosts.Clear(); _ghostSig = null; _ghostSigFor = null; _marked.Clear(); _uncloaked.Clear();
            _chases.Clear();
            _rng = new System.Random();
            if (_decoyButton != null) _decoyButton.SetActive(false);
        }

        /// <summary>AfterLoadLevel: enemies 1.. are the ghosts. Turn their AI off and send them away.</summary>
        public static void Setup()
        {
            Reset();
            Duel.LastPuppetNoise = 100f; // the first sonar check sets the real value
            Vessel[] ev = GameDataManager.enemyvesselsonlevel;
            if (ev == null) return;
            _park = new Vector3(0f, 0f, ParkNm * Duel.UnitsPerNm);
            for (int i = 1; i < ev.Length; i++)
            {
                Vessel v = ev[i];
                if (v == null) continue;
                Duel.AiOff(v);
                foreach (Collider col in v.GetComponentsInChildren<Collider>(true)) col.enabled = false; // no hits, no collisions
                Park(v);
                _ghosts.Add(new Ghost { V = v });
            }
            Duel.Log("~Ghosts: " + _ghosts.Count + " ghost sub(s) ready for decoys");
            try { SetupButton(); } catch (System.Exception ex) { Duel.Log("~Code book DECOY button failed: " + ex.Message + " (Ctrl+F5 still works)"); }
        }

        static void Park(Vessel v)
        {
            Vector3 p = _park; p.y = 1000f - 200f / GameDataManager.unitsToFeet; // 200 ft: safe for every sub, never crushed
            v.transform.position = p;
            if (v.vesselmovement != null) { v.vesselmovement.telegraphValue = 0; Vector3 s = v.vesselmovement.shipSpeed; s.z = 0f; v.vesselmovement.shipSpeed = s; }
        }

        public static bool IsGhostAI(VesselAI ai)
        {
            if (ai == null || !Duel.InDuel) return false;
            foreach (Ghost g in _ghosts) if (g.V != null && g.V.vesselai == ai) return true;
            return false;
        }

        public static bool IsGhost(Vessel v)
        {
            if (v == null || !Duel.InDuel) return false;
            foreach (Ghost g in _ghosts) if (g.V == v) return true;
            return false;
        }

        static Ghost ByIndex(int vesselIndex)
        {
            foreach (Ghost g in _ghosts) if (g.V != null && g.V.vesselListIndex == vesselIndex) return g;
            return null;
        }

        public static bool IsDecoy(DatabaseWeaponData wd) { return wd != null && (wd.weaponType == "DECOY" || wd.isDecoy); }

        /// <summary>The opponent launched a decoy; t is its hidden copy here.</summary>
        public static void OnDecoySpawned(int torpId, Torpedo t, DatabaseWeaponData wd)
        {
            // The copy itself stays a torpedo object (seekers here may chase it) but is silent and not drawn.
            foreach (AudioSource a in t.GetComponentsInChildren<AudioSource>(true)) a.enabled = false;
            foreach (Renderer r in t.GetComponentsInChildren<Renderer>(true)) r.enabled = false;
            Sprite icon = (t.tacMapTorpedoIcon != null && t.tacMapTorpedoIcon.shipDisplayIcon != null) ? t.tacMapTorpedoIcon.shipDisplayIcon.sprite : null;
            if (t.tacMapTorpedoIcon != null) t.tacMapTorpedoIcon.gameObject.SetActive(false);

            Ghost g = null;
            foreach (Ghost x in _ghosts) if (x.TorpId < 0 && x.V != null) { g = x; break; }
            if (g == null) { Duel.Log("~Ghosts: no free ghost for decoy #" + torpId); return; }
            g.Icon = icon;
            float ydsPerSec = Mathf.Max(1f, wd.runSpeed) * 2025.37f / 3600f;
            float run = wd.rangeInYards > 0f ? wd.rangeInYards / ydsPerSec : 1200f;
            g.TorpId = torpId; g.T = t; g.Start = Time.time; g.Until = Time.time + run + 10f; g.FadeOutStart = -1f;
            g.Noise = Duel.LastPuppetNoise;
            g.LastPos = t.transform.position; g.Vel = Vector3.zero;
            Follow(g, t.transform.position, t.transform.eulerAngles.y);
            Duel.Log("~Ghosts: ghost " + g.V.vesselListIndex + " follows decoy #" + torpId + " (" + wd.weaponName + ", run " + (run / 60f).ToString("F0") + " min, noise " + g.Noise.ToString("F1") + ")");
        }

        /// <summary>The decoy copy ended here (end message or removed): fade the ghost out along its last course.</summary>
        public static void OnDecoyEnded(int torpId)
        {
            foreach (Ghost g in _ghosts)
                if (g.TorpId == torpId && g.FadeOutStart < 0f) { g.FadeOutStart = Time.time; g.T = null; Duel.Log("~Ghosts: decoy #" + torpId + " ended, ghost fading out"); }
        }

        static void Follow(Ghost g, Vector3 pos, float heading)
        {
            Transform tr = g.V.transform;
            tr.position = pos;
            tr.rotation = Quaternion.Euler(0f, heading, 0f);
            // The game's speed field (knots / 10) for the lead marker, baffles and TMA displays.
            VesselMovement m = g.V.vesselmovement;
            if (m != null)
            {
                float knots = new Vector3(g.Vel.x, 0f, g.Vel.z).magnitude * 75.13f * 3600f / 2025.37f;
                Vector3 s = m.shipSpeed; s.z = knots / 10f; m.shipSpeed = s;
            }
        }

        public static void Update()
        {
            if (!Duel.InDuel) return;
            foreach (Ghost g in _ghosts)
            {
                if (g.V == null) continue;
                if (g.TorpId < 0) { Park(g.V); continue; } // keep it there whatever its movement code does
                if (g.FadeOutStart < 0f && (g.T == null || Time.time > g.Until))
                {
                    g.FadeOutStart = Time.time; g.T = null;
                    Duel.Log("~Ghosts: decoy #" + g.TorpId + " run over, ghost fading out");
                }
                if (g.T != null)
                {
                    Vector3 p = g.T.transform.position;
                    if (Time.deltaTime > 0f) g.Vel = Vector3.Lerp(g.Vel, (p - g.LastPos) / Time.deltaTime, 0.1f);
                    g.LastPos = p;
                    Follow(g, p, g.T.transform.eulerAngles.y);
                }
                else if (g.FadeOutStart >= 0f)
                {
                    if (Time.time - g.FadeOutStart > FadeOutSeconds)
                    {
                        Duel.Log("~Ghosts: ghost " + g.V.vesselListIndex + " gone");
                        g.TorpId = -1; Park(g.V);
                        int gi = g.V.vesselListIndex;
                        bool wasUncloaked = _uncloaked.Remove(gi);
                        bool had = _marked.Remove(gi) | wasUncloaked;
                        if (had) Unmark(gi, wasUncloaked); // a reused ghost must not come back tagged
                        continue;
                    }
                    g.LastPos += g.Vel * Time.deltaTime; // keeps going straight while it fades
                    Follow(g, g.LastPos, g.V.transform.eulerAngles.y);
                }
            }
            CheckPasses();
            KeepMarks();
        }

        static void CheckPasses()
        {
            Torpedo[] tos = UIFunctions.globaluifunctions.playerfunctions.sensormanager.torpedoObjects;
            if (tos != null)
                foreach (Torpedo t in tos)
                {
                    if (t == null || !Weapons.IsOwnTorpedo(t) || IsDecoy(t.databaseweapondata)) continue;
                    foreach (Ghost g in _ghosts)
                    {
                        if (g.T == null || g.V == null) continue;
                        bool chasing = t.targetTransform == g.T.transform;
                        Chase c = null;
                        foreach (Chase x in _chases) if (x.Torp == t && x.G == g) { c = x; break; }
                        if (c == null)
                        {
                            if (!chasing || _uncloaked.ContainsKey(g.V.vesselListIndex)) continue;
                            c = new Chase { Torp = t, G = g }; _chases.Add(c);
                        }
                        if (chasing) c.LastChase = Time.time;
                        float yd = Vector3.Distance(t.transform.position, g.T.transform.position) * YdPerUnit;
                        if (yd < c.MinYd) c.MinYd = yd;
                        if (c.MinYd < PassCloseYd && yd > c.MinYd + PassAwayYd) { Passed(t, g, c.MinYd); c.Torp = null; }
                    }
                }
            // forget ended torpedoes, finished decoys and chases the seeker gave up on long ago
            _chases.RemoveAll(c => c.Torp == null || c.G.T == null || Time.time - c.LastChase > ChaseMemorySeconds);
        }

        static void Passed(Torpedo t, Ghost g, float minYd)
        {
            int i = g.V.vesselListIndex;
            if (!_uncloaked.ContainsKey(i))
            {
                _uncloaked[i] = g.Icon;
                Duel.Log("Our torpedo passed a decoy (closest " + minYd.ToString("F0") + " yd), contact uncloaked as DECOY");
                SensorManager sm = UIFunctions.globaluifunctions.playerfunctions.sensormanager;
                string name = i < sm.initialDetectedByPlayerName.Length ? UIFunctions.globaluifunctions.playerfunctions.GetBasicContactName(sm.initialDetectedByPlayerName[i]) : "Contact";
                PlayerFunctions pf = UIFunctions.globaluifunctions.playerfunctions;
                Color col; if (!pf.messageLogColors.TryGetValue("Sonar", out col)) col = MarkColor;
                pf.PlayerMessage("Sonar: " + name + " reassessed as a DECOY, our torpedo passed it", col, "CWMP_Decoy");
            }
            KeepMarks();
        }

        /// <summary>GetVesselNoiseLevel postfix for ghosts: silent while dormant, else the launcher's noise with a fade.</summary>
        public static bool Noise(Vessel v, ref float result)
        {
            foreach (Ghost g in _ghosts)
            {
                if (g.V != v) continue;
                if (g.TorpId < 0) { result = -1000f; return true; }
                float age = Time.time - g.Start;
                float f;
                if (g.FadeOutStart >= 0f) f = 1f - Mathf.Clamp01((Time.time - g.FadeOutStart) / FadeOutSeconds);
                else if (age < QuietSeconds) f = 0f;
                else f = Mathf.Clamp01((age - QuietSeconds) / FadeInSeconds);
                result = f <= 0f ? -1000f : g.Noise - (1f - f) * 20f + Random.Range(-0.5f, 0.5f);
                return true;
            }
            return false;
        }

        /// <summary>Torpedoes never pick a ghost as a target (the decoy copy itself is what seekers may chase).</summary>
        public static void RemoveFromTargets(List<Transform> targets)
        {
            if (!Duel.InDuel || targets == null) return;
            foreach (Ghost g in _ghosts) if (g.V != null) targets.Remove(g.V.transform);
        }

        // ---------- signature: one line moved a little ----------

        /// <summary>SetContactProfileSprite postfix: when the current contact is a ghost, show the tweaked signature.</summary>
        public static void OnProfileSprite(string graphicName)
        {
            if (!Duel.InDuel || graphicName == "blank_sig") return;
            PlayerFunctions pf = UIFunctions.globaluifunctions.playerfunctions;
            Ghost g = ByIndex(pf.currentTargetIndex);
            if (g == null) return;
            Texture2D t = GhostSignature(graphicName);
            if (t != null) pf.signatureMaterials[1].SetTexture("_Signature", t);
        }

        static Texture2D GhostSignature(string graphicName)
        {
            if (_ghostSig != null && _ghostSigFor == graphicName) return _ghostSig;
            try
            {
                Texture2D src = TextParser.GetTexture(UIFunctions.globaluifunctions.textparser.GetFilePathFromString("vessels/signature/" + graphicName + ".png"));
                if (src == null) return null;
                int w = src.width, h = src.height;
                Color[] px = src.GetPixels();
                // Brightest columns = the signature's lines. Pick one of the three strongest (random per match).
                float[] col = new float[w];
                for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) { Color c = px[y * w + x]; col[x] += c.r + c.g + c.b; }
                List<int> peaks = new List<int>();
                for (int x = 3; x < w - 3; x++) if (col[x] >= col[x - 1] && col[x] > col[x + 1]) peaks.Add(x);
                peaks.Sort((a, b) => col[b].CompareTo(col[a]));
                if (peaks.Count == 0) return null;
                int line = peaks[_rng.Next(System.Math.Min(3, peaks.Count))];
                int d = _rng.Next(2) == 0 ? -2 : 2;
                // Move that line (and its one-pixel shoulders) by d columns; the gap gets the neighbouring background.
                Color[] outPx = (Color[])px.Clone();
                for (int y = 0; y < h; y++)
                {
                    int row = y * w;
                    Color bg = px[row + System.Math.Max(0, System.Math.Min(w - 1, line - 4 * System.Math.Sign(d)))];
                    for (int k = -1; k <= 1; k++) outPx[row + line + k] = bg;
                    for (int k = -1; k <= 1; k++) outPx[row + System.Math.Max(0, System.Math.Min(w - 1, line + k + d))] = px[row + line + k];
                }
                Texture2D t = new Texture2D(w, h);
                t.SetPixels(outPx); t.Apply();
                _ghostSig = t; _ghostSigFor = graphicName;
                Duel.Log("~Ghosts: signature line at x=" + line + " moved " + d + " px for " + graphicName);
                return t;
            }
            catch (System.Exception ex) { Duel.Log("~Ghosts: signature copy failed (" + ex.Message + "), using the normal one"); return null; }
        }

        // ---------- DECOY tag on a contact (the player's own note, never verified) ----------

        public static void ToggleMarkCurrent()
        {
            if (!Duel.InDuel) return;
            PlayerFunctions pf = UIFunctions.globaluifunctions.playerfunctions;
            int i = pf.currentTargetIndex;
            if (i < 0) return;
            if (_marked.Remove(i)) { if (!_uncloaked.ContainsKey(i)) Unmark(i); Duel.Log("Contact no longer tagged as decoy"); }
            else { _marked.Add(i); Duel.Log("Contact tagged as DECOY (your note only)"); }
            KeepMarks();
        }

        static string Strip(string t) { return t != null && t.EndsWith(MarkSuffix) ? t.Substring(0, t.Length - MarkSuffix.Length) : t; }

        static void Unmark(int i) { Unmark(i, false); }

        static void Unmark(int i, bool resetIcon)
        {
            PlayerFunctions pf = UIFunctions.globaluifunctions.playerfunctions;
            MapContact[] mcs = pf.sensormanager.tacticalmap.mapContact;
            if (i >= mcs.Length || mcs[i] == null) return;
            MapContact mc = mcs[i];
            mc.contactText.text = Strip(mc.contactText.text);
            mc.contactTextShort = Strip(mc.contactTextShort);
            if (pf.currentTargetIndex == i && pf.contactDataName != null) pf.contactDataName.text = Strip(pf.contactDataName.text);
            Color c = pf.sensormanager.GetContactColor(i);
            mc.shipDisplayIcon.color = c; mc.contactText.color = c;
            // put the class icon back (the game sets it the same way when it classifies)
            if (!resetIcon) return; // only an uncloaked contact had its icon changed
            SensorManager sm = pf.sensormanager;
            mc.shipDisplayIcon.transform.rotation = Quaternion.identity;
            if (sm.classifiedByPlayer[i]) mc.shipDisplayIcon.sprite = sm.sonarPaintImages[sm.shipTypes[i]];
            else mc.shipDisplayIcon.sprite = sm.sonarPaintImages[0];
        }

        /// <summary>The game rewrites contact names and colours now and then; put the DECOY look back each frame.</summary>
        static readonly List<int> _lookIdx = new List<int>();

        static void KeepMarks()
        {
            if (_marked.Count == 0 && _uncloaked.Count == 0) return;
            PlayerFunctions pf = UIFunctions.globaluifunctions.playerfunctions;
            MapContact[] mcs = pf.sensormanager.tacticalmap.mapContact;
            _lookIdx.Clear(); _lookIdx.AddRange(_marked);
            foreach (int k in _uncloaked.Keys) if (!_marked.Contains(k)) _lookIdx.Add(k);
            foreach (int i in _lookIdx)
            {
                if (i >= mcs.Length || mcs[i] == null) continue;
                MapContact mc = mcs[i];
                if (!mc.contactText.text.EndsWith(MarkSuffix)) mc.contactText.text += MarkSuffix;
                if (mc.contactTextShort != null && !mc.contactTextShort.EndsWith(MarkSuffix)) mc.contactTextShort += MarkSuffix;
                if (pf.currentTargetIndex == i && pf.contactDataName != null && !pf.contactDataName.text.EndsWith(MarkSuffix)) pf.contactDataName.text += MarkSuffix;
                if (mc.shipDisplayIcon != null && mc.shipDisplayIcon.color != MarkColor) mc.shipDisplayIcon.color = MarkColor;
                if (mc.contactText.color != MarkColor) mc.contactText.color = MarkColor;
                Sprite icon;
                if (_uncloaked.TryGetValue(i, out icon) && icon != null && mc.shipDisplayIcon != null && mc.shipDisplayIcon.sprite != icon) mc.shipDisplayIcon.sprite = icon;
            }
        }

        // ---------- DECOY button in the code book (signature panel), a copy of its MARK button ----------

        static void SetupButton()
        {
            if (_decoyButton == null)
            {
                Button mark = null;
                foreach (Button b in Resources.FindObjectsOfTypeAll<Button>())
                {
                    if (b == null || !b.gameObject.scene.IsValid()) continue; // skip prefab assets
                    if (b.gameObject.name == "CWMP_DecoyButton") { _decoyButton = b.gameObject; break; }
                    int n = b.onClick.GetPersistentEventCount();
                    for (int k = 0; k < n; k++)
                    {
                        string m = b.onClick.GetPersistentMethodName(k);
                        if (m == "SigAnalysisSet" || m == "ContactClassifiedManuallyByPlayer") mark = b;
                    }
                }
                if (_decoyButton == null)
                {
                    if (mark == null)
                    {
                        if (!_buttonMissingLogged) { _buttonMissingLogged = true; Duel.Log("~Code book: MARK button not found, no DECOY button (Ctrl+F5 still works)"); }
                        return;
                    }
                    _decoyButton = CloneButton(mark);
                }
            }
            if (_decoyButton != null) _decoyButton.SetActive(true);
        }

        static GameObject CloneButton(Button mark)
        {
            Transform parent = mark.transform.parent;
            string layout = "";
            if (parent != null)
                foreach (Button b in parent.GetComponentsInChildren<Button>(true))
                {
                    RectTransform br = b.GetComponent<RectTransform>();
                    Text bt = b.GetComponentInChildren<Text>();
                    layout += " " + b.gameObject.name + "\"" + (bt != null ? bt.text : "") + "\"" + (br != null ? "@" + br.anchoredPosition.x.ToString("F0") + "," + br.anchoredPosition.y.ToString("F0") : "");
                }
            Duel.Log("~Code book buttons:" + layout);

            GameObject go = (GameObject)Object.Instantiate(mark.gameObject);
            go.name = "CWMP_DecoyButton";
            go.transform.SetParent(parent, false);
            if (parent != null && parent.GetComponent<LayoutGroup>() != null)
                go.transform.SetSiblingIndex(mark.transform.GetSiblingIndex() + 1);
            else
            {
                // PREV, MARK, NEXT sit in a row: put ours one step right of the rightmost button in that row.
                RectTransform rt = go.GetComponent<RectTransform>(), mr = mark.GetComponent<RectTransform>();
                if (rt != null && mr != null)
                {
                    float right = mr.anchoredPosition.x, step = float.MaxValue;
                    foreach (Button b in parent.GetComponentsInChildren<Button>(true))
                    {
                        if (b == mark || b.gameObject == go || b.transform.parent != parent) continue;
                        RectTransform br = b.GetComponent<RectTransform>();
                        if (br == null || Mathf.Abs(br.anchoredPosition.y - mr.anchoredPosition.y) > 2f) continue;
                        float gap = Mathf.Abs(br.anchoredPosition.x - mr.anchoredPosition.x);
                        if (gap > 1f && gap < step) step = gap;
                        if (br.anchoredPosition.x > right) right = br.anchoredPosition.x;
                    }
                    if (step == float.MaxValue) step = mr.rect.height * 1.2f;
                    rt.anchoredPosition = new Vector2(right + step, mr.anchoredPosition.y);
                }
            }
            TextLocaliser loc = go.GetComponentInChildren<TextLocaliser>();
            if (loc != null) loc.enabled = false;
            Text label = go.GetComponentInChildren<Text>();
            if (label == null)
            {
                // Icon button with its caption elsewhere: give ours its own caption.
                Text any = null;
                foreach (Text tx in parent.GetComponentsInChildren<Text>(true)) { any = tx; break; }
                if (any != null)
                {
                    GameObject cap = (GameObject)Object.Instantiate(any.gameObject);
                    cap.transform.SetParent(go.transform, false);
                    RectTransform cr = cap.GetComponent<RectTransform>();
                    RectTransform mr2 = mark.GetComponent<RectTransform>();
                    if (cr != null) cr.anchoredPosition = new Vector2(0f, mr2 != null ? -mr2.rect.height * 0.8f : 0f); // under the icon, like the others
                    TextLocaliser cl = cap.GetComponent<TextLocaliser>(); if (cl != null) cl.enabled = false;
                    label = cap.GetComponent<Text>();
                    if (label != null) label.text = "DECOY";
                }
            }
            else
            {
                Text mt = mark.GetComponentInChildren<Text>();
                string t = mt != null ? mt.text : null;
                label.text = (t != null && t.Length > 0 && t == t.ToLowerInvariant()) ? "decoy"
                           : (t != null && t.Length > 1 && t.Substring(1) == t.Substring(1).ToLowerInvariant()) ? "Decoy" : "DECOY";
            }
            Button btn = go.GetComponent<Button>();
            btn.onClick = new Button.ButtonClickedEvent();
            btn.onClick.AddListener(new UnityAction(delegate { ToggleMarkCurrent(); }));
            Duel.Log("Code book: DECOY button added (same as Ctrl+F5)");
            return go;
        }
    }

    [HarmonyPatch(typeof(Torpedo), "SetTorpedoTargetsList")]
    static class Patch_SetTorpedoTargetsList
    {
        static void Postfix(Torpedo __instance) { Ghosts.RemoveFromTargets(__instance.targetsList); }
    }

    [HarmonyPatch(typeof(PlayerFunctions), "SetContactProfileSprite")]
    static class Patch_SetContactProfileSprite
    {
        static void Postfix(string graphicName)
        {
            try { Ghosts.OnProfileSprite(graphicName); } catch (System.Exception) { }
        }
    }
}
