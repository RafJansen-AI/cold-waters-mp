// Cold Waters MP - mobile decoys (MOSS, MG-74 Korund, MG-14) as "ghost subs". C# 5 / .NET 3.5.
// The decoy runs in the launcher's game as a normal decoy torpedo. In the opponent's game a hidden copy of it
// follows the network (torpedo seekers there can still go for it, as in the game), and a ghost sub follows that
// copy: a second enemy vessel of the launcher's class, sounding like the launcher did at launch, so the crew
// classifies it as that sub. Its tells: steady course and speed, no reactions or transients, a limited run, and
// one signature line shifted by one pixel. It can't be hit and never counts as a kill.
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
        public static int Slots = 5;              // decoys that can run at the same time per side (Debug.GhostSlots overrides, testing)
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
            public float Knots;                    // the decoy's run speed: steady, for the lead marker and TMA
            public DatabaseWeaponData Wd;
            public byte Towed;                     // the launcher's towed array at launch
        }

        static readonly List<Ghost> _ghosts = new List<Ghost>();
        static Vector3 _park;
        static System.Random _rng = new System.Random();
        static Texture2D _ghostSig; static string _ghostSigFor;
        static readonly HashSet<int> _marked = new HashSet<int>();   // contacts the player tagged as DECOY: a note only, never verified
        static readonly HashSet<int> _uncloaked = new HashSet<int>();  // ghosts our torpedo passed: confirmed decoys, can't be untagged
        static readonly Dictionary<int, Sprite> _savedIcon = new Dictionary<int, Sprite>();
        static readonly Dictionary<int, Quaternion> _savedRot = new Dictionary<int, Quaternion>();
        const string NoteSuffix = " decoy?", ConfirmedSuffix = " DECOY";
        static readonly Color MarkColor = new Color(0.6f, 0.6f, 0.6f, 1f);

        // Our torpedoes that chase a decoy copy here: when one gets close and then opens again without a hit, it has
        // passed the decoy, and the crew reassesses the contact as a decoy. Only the label changes: seekers still go for it.
        class Chase { public Torpedo Torp; public Ghost G; public float MinYd = float.MaxValue; public float LastChase; }
        static readonly List<Chase> _chases = new List<Chase>();
        const float YdPerUnit = 75.13f, PassCloseYd = 100f, PassAwayYd = 60f, ChaseMemorySeconds = 10f;

        static GameObject _decoyButton;
        static Text _decoyLabel;
        static bool _buttonMissingLogged;

        public static void Reset()
        {
            RestoreRay();
            _ghosts.Clear(); _ghostSig = null; _ghostSigFor = null; _marked.Clear(); _uncloaked.Clear(); _savedIcon.Clear(); _savedRot.Clear();
            foreach (Look l in _looks.Values) if (l.Proxy != null) Object.Destroy(l.Proxy);
            _looks.Clear();
            _chases.Clear();
            _rng = new System.Random();
            if (_decoyButton != null) _decoyButton.SetActive(false);
        }

        /// <summary>AfterLoadLevel: enemies 1.. are the ghosts. Turn their AI off and send them away.</summary>
        /// <summary>This mission's ghost vessels, kept after the duel ends (Reset) until the next mission is set up: the
        /// action report is built after the mod has already left the duel.</summary>
        public static readonly List<Vessel> ReportHidden = new List<Vessel>();

        public static void Setup()
        {
            Reset();
            ReportHidden.Clear();
            Duel.LastPuppetNoise = 100f; // the first sonar check sets the real value
            Vessel[] ev = GameDataManager.enemyvesselsonlevel;
            if (ev == null) return;
            _park = new Vector3(0f, 0f, ParkNm * Duel.UnitsPerNm);
            for (int i = 1; i < ev.Length && _ghosts.Count < Slots; i++) // only the first Slots extra enemies are ghosts
            {
                Vessel v = ev[i];
                if (v == null) continue;
                Duel.AiOff(v);
                foreach (Collider col in v.GetComponentsInChildren<Collider>(true)) col.enabled = false; // no hits, no collisions
                Park(v);
                _ghosts.Add(new Ghost { V = v });
                ReportHidden.Add(v);
            }
            Duel.Log("~Ghosts: " + _ghosts.Count + " ghost sub(s) ready for decoys");
            try { SetupButton(); } catch (System.Exception ex) { Duel.Log("~Code book DECOY button failed: " + ex.Message + " (Ctrl+F5 still works)"); }
        }

        static void Park(Vessel v)
        {
            Vector3 p = _park; p.y = 1000f - 200f / GameDataManager.unitsToFeet; // 200 ft: safe for every sub, never crushed
            v.transform.position = p;
            // Silent at once: the mission's own sonar checks before battlestations read currentNoise before our
            // GetVesselNoiseLevel postfix has run, and heard parked ghosts at 300 kyd with the class's 140.8 dB
            // (named S2, S3 at load; seen 26 and 27 Nov).
            if (v.acoustics != null) v.acoustics.currentNoise = -1000f;
            Duel.ApplyTowed(v, 0);
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

        // ---------- sonar masking ----------
        // SonarCheck masks contact i when the ray to it crosses the "MaskingNoise" collider of a vessel k whose LAST reading
        // is higher than i's LAST reading (signal / 2..4). A ghost starts beside its launcher, so the ray to it crosses
        // the launcher's masking volume; once masked its last reading stays low and it lost that comparison every time,
        // even when the decoy was 15 dB louder (seen 26 Nov: ghost 10-11 vs launcher 22-23). Before each check, give an
        // active ghost the last reading it would have unmasked: the launcher's reading plus the noise difference,
        // corrected for range. A ghost that is really quieter (fading in or out) is still masked. SonarCheck overwrites
        // the reading with the fresh value before detection, so only the masking comparison sees this estimate.
        static System.Reflection.FieldInfo _spreadF, _attenF;

        public static void BeforeSonarCheck(SensorManager sm)
        {
            if (!Duel.InDuel || Duel.Puppet == null || sm == null || sm.signalLastPassiveReading == null || _ghosts.Count == 0) return;
            Vessel own = GameDataManager.playervesselsonlevel != null && GameDataManager.playervesselsonlevel.Length > 0 ? GameDataManager.playervesselsonlevel[0] : null;
            if (own == null || Duel.Puppet.acoustics == null) return;
            float[] sig = sm.signalLastPassiveReading;
            int p = Duel.Puppet.vesselListIndex;
            if (p < 0 || p >= sig.Length || sig[p] < -100f) return; // launcher in our baffles or unheard: no reference
            if (_spreadF == null) { _spreadF = AccessTools.Field(typeof(SensorManager), "spreadingFactor"); _attenF = AccessTools.Field(typeof(SensorManager), "attenuationFactor"); }
            float spread = _spreadF != null ? (float)_spreadF.GetValue(sm) : 0f, atten = _attenF != null ? (float)_attenF.GetValue(sm) : 0f;
            float rP = Mathf.Max(1f, Vector3.Distance(own.transform.position, Duel.Puppet.transform.position) * GameDataManager.yardsScale * 0.9144f);
            foreach (Ghost g in _ghosts)
            {
                if (g.TorpId < 0 || g.V == null || g.V.acoustics == null) continue;
                int gi = g.V.vesselListIndex;
                if (gi < 0 || gi >= sig.Length) continue;
                float rG = Mathf.Max(1f, Vector3.Distance(own.transform.position, g.V.transform.position) * GameDataManager.yardsScale * 0.9144f);
                float est = sig[p] + (g.V.acoustics.currentNoise - Duel.Puppet.acoustics.currentNoise)
                          - spread * (Mathf.Log10(rG) - Mathf.Log10(rP)) - atten * (rG - rP);
                if (est > sig[gi]) sig[gi] = est;
            }
        }

        // A ghost rides on its hidden decoy copy, whose "decoyAcoustics" collider is on layer 14 (Acoustics). SonarCheck's
        // masking ray towards the ghost then hit that sphere and divided the ghost's signal by 3..4, by its own decoy
        // (seen 26 Nov). GetVesselNoiseLevel runs for each vessel just before its masking ray: while the vessel is a
        // ghost, its own decoy's layer-14 colliders sit on "Ignore Raycast" (2); for every other vessel, and after the
        // check, they are back on 14, so a decoy still masks the real sub behind it as in the game.
        static readonly List<GameObject> _rayHidden = new List<GameObject>();
        static Ghost _rayFocus;

        public static void RayFocus(Vessel v)
        {
            Ghost g = null;
            foreach (Ghost x in _ghosts) if (x.V == v && x.T != null) { g = x; break; }
            if (g != null && g == _rayFocus) return;
            RestoreRay();
            if (g == null) return;
            foreach (Collider c in g.T.GetComponentsInChildren<Collider>(true))
                if (c != null && c.gameObject.layer == 14) { _rayHidden.Add(c.gameObject); c.gameObject.layer = 2; }
            _rayFocus = g;
        }

        public static void RestoreRay()
        {
            foreach (GameObject go in _rayHidden) if (go != null) go.layer = 14;
            _rayHidden.Clear();
            _rayFocus = null;
        }

        /// <summary>The opponent-side slots whose contacts follow the track rule (Tracks): the puppet and every ghost.</summary>
        public static void OpponentSlots(List<int> into)
        {
            into.Clear();
            if (Duel.Puppet != null) into.Add(Duel.Puppet.vesselListIndex);
            foreach (Ghost g in _ghosts) if (g.V != null) into.Add(g.V.vesselListIndex);
        }

        public static bool IsUncloaked(int i) { return _uncloaked.Contains(i); }

        /// <summary>A contact track moved to another slot: the player's "decoy?" note goes with it.</summary>
        /// <summary>Two contact tracks swapped slots (Tracks split): the player's notes go with their labels.</summary>
        public static void SwapMarks(int x, int y)
        {
            bool nx = _marked.Remove(x), ny = _marked.Remove(y);
            bool cx = _uncloaked.Remove(x), cy = _uncloaked.Remove(y);
            if (nx || cx) Unmark(x);
            if (ny || cy) Unmark(y);
            if (nx) _marked.Add(y);
            if (ny) _marked.Add(x);
            if (cx) _uncloaked.Add(y);
            if (cy) _uncloaked.Add(x);
        }

        public static void MoveTrackMarks(int from, int to)
        {
            bool note = _marked.Remove(from), confirmed = _uncloaked.Remove(from);
            if (note || confirmed) Unmark(from);
            if (note) _marked.Add(to);
        }

        /// <summary>A contact track was dropped: no tag survives on that slot.</summary>
        public static void ClearMarks(int i) { if (_marked.Remove(i) | _uncloaked.Remove(i)) Unmark(i); }

        public static bool IsDecoy(DatabaseWeaponData wd) { return wd != null && (wd.weaponType == "DECOY" || wd.isDecoy); }

        /// <summary>The opponent launched a decoy; t is its hidden copy here.</summary>
        public static void OnDecoySpawned(int torpId, Torpedo t, DatabaseWeaponData wd)
        {
            // The copy itself stays a torpedo object (seekers here may chase it) but is silent and not drawn.
            foreach (AudioSource a in t.GetComponentsInChildren<AudioSource>(true)) a.enabled = false;
            foreach (Renderer r in t.GetComponentsInChildren<Renderer>(true)) r.enabled = false;
            if (t.tacMapTorpedoIcon != null) t.tacMapTorpedoIcon.gameObject.SetActive(false);

            // Prefer a free ghost without a contact track: reusing a named one has to drop its (faded) track now.
            Ghost g = null;
            foreach (Ghost x in _ghosts)
                if (x.TorpId < 0 && x.V != null && (g == null || (Tracks.HasName(g.V.vesselListIndex) && !Tracks.HasName(x.V.vesselListIndex)))) g = x;
            if (g == null) { Duel.Log("~Ghosts: no free ghost for decoy #" + torpId); return; }
            Tracks.Drop(g.V.vesselListIndex, "ghost reused for decoy #" + torpId);
            float ydsPerSec = Mathf.Max(1f, wd.runSpeed) * 2025.37f / 3600f;
            float run = wd.rangeInYards > 0f ? wd.rangeInYards / ydsPerSec : 1200f;
            g.TorpId = torpId; g.T = t; g.Start = Time.time; g.Until = Time.time + run + 10f; g.FadeOutStart = -1f;
            // Sound like the launcher would at the decoy's speed: the hull's noise without the launcher's own speed
            // (and cavitation), plus the game's per-knot noise at the decoy's run speed.
            float perKnot = UIFunctions.globaluifunctions.playerfunctions.sensormanager.targetNoisePerKnot;
            g.Noise = Duel.LastPuppetNoise - Duel.LastPuppetSpeedNoise + Mathf.Max(0f, wd.runSpeed) * perKnot;
            g.Knots = wd.runSpeed; g.Wd = wd; g.Towed = Duel.OppTowed;
            g.LastPos = t.transform.position; g.Vel = Vector3.zero;
            Follow(g, t.transform.position, t.transform.eulerAngles.y);
            Duel.Log("~Ghosts: ghost " + g.V.vesselListIndex + " follows decoy #" + torpId + " (" + wd.weaponName + ", run " + (run / 60f).ToString("F0") + " min, noise " + g.Noise.ToString("F1") + ", launcher " + Duel.LastPuppetNoise.ToString("F1") + ")");
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
                float knots = g.Knots > 0f ? g.Knots : new Vector3(g.Vel.x, 0f, g.Vel.z).magnitude * 75.13f * 3600f / 2025.37f; // steady, not per-frame
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
                    Duel.ApplyTowed(g.V, g.Towed);
                }
                else if (g.FadeOutStart >= 0f)
                {
                    if (Time.time - g.FadeOutStart > FadeOutSeconds)
                    {
                        Duel.Log("~Ghosts: ghost " + g.V.vesselListIndex + " gone");
                        g.TorpId = -1; Park(g.V);
                        int gi = g.V.vesselListIndex;
                        bool had = _marked.Remove(gi) | _uncloaked.Remove(gi);
                        if (had) Unmark(gi); // a reused ghost must not come back tagged
                        continue;
                    }
                    g.LastPos += g.Vel * Time.deltaTime; // keeps going straight while it fades
                    Follow(g, g.LastPos, g.V.transform.eulerAngles.y);
                }
            }
            CheckPasses();
            KeepMarks();
            UpdateButtonLabel();
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
                            if (!chasing || _uncloaked.Contains(g.V.vesselListIndex)) continue;
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
            if (_uncloaked.Add(i))
            {
                Duel.Log("Our torpedo passed a decoy (closest " + minYd.ToString("F0") + " yd), contact uncloaked as DECOY");
                SensorManager sm = UIFunctions.globaluifunctions.playerfunctions.sensormanager;
                string name = ContactName(i);
                PlayerFunctions pf = UIFunctions.globaluifunctions.playerfunctions;
                Color col; if (!pf.messageLogColors.TryGetValue("Sonar", out col)) col = MarkColor;
                // Never detected by our sonar: no message (it would tell the player about a contact they don't have).
                if (name != null) pf.PlayerMessage("Sonar: " + name + " reassessed as a DECOY, our torpedo passed it", col, "CWMP_Decoy");
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

        /// <summary>The player's name for contact i (e.g. "S3"), or null if our sonar never detected it.</summary>
        static string ContactName(int i)
        {
            PlayerFunctions pf = UIFunctions.globaluifunctions.playerfunctions;
            SensorManager sm = pf.sensormanager;
            if (sm.initialDetectedByPlayerName == null || i < 0 || i >= sm.initialDetectedByPlayerName.Length) return null;
            string n = sm.initialDetectedByPlayerName[i];
            if (string.IsNullOrEmpty(n)) return null;
            try { return pf.GetBasicContactName(n); } catch (System.Exception) { return n; }
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
                int d = _rng.Next(2) == 0 ? -1 : 1;
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
            if (_uncloaked.Contains(i)) { Duel.Log("Contact is a confirmed decoy, the tag stays"); return; }
            if (_marked.Remove(i)) { Unmark(i); Duel.Log("Contact no longer tagged as decoy"); }
            else { _marked.Add(i); Duel.Log("Contact tagged as decoy? (your note only)"); }
            KeepMarks();
        }

        /// <summary>A label without the "decoy?" / "DECOY" suffix.</summary>
        public static string StripTags(string t) { return Strip(t); }

        static string Strip(string t)
        {
            if (t == null) return t;
            if (t.EndsWith(NoteSuffix)) return t.Substring(0, t.Length - NoteSuffix.Length);
            if (t.EndsWith(ConfirmedSuffix)) return t.Substring(0, t.Length - ConfirmedSuffix.Length);
            if (t.EndsWith(NoteSuffix.ToUpperInvariant())) return t.Substring(0, t.Length - NoteSuffix.Length);
            return t;
        }

        static string WithSuffix(string t, string suffix) { return t == null || t.EndsWith(suffix) ? t : Strip(t) + suffix; }

        static Sprite TorpedoSprite()
        {
            TacticalMap tm = UIFunctions.globaluifunctions.playerfunctions.sensormanager.tacticalmap;
            if (tm == null || tm.torpedoMapContactObject == null) return null;
            MapContact mc = tm.torpedoMapContactObject.GetComponent<MapContact>();
            return mc != null && mc.shipDisplayIcon != null ? mc.shipDisplayIcon.sprite : null;
        }

        /// <summary>Back to the game's own look: name, colour, and the icon the contact had before the tag.</summary>
        static void Unmark(int i)
        {
            RemoveLook(i);
            PlayerFunctions pf = UIFunctions.globaluifunctions.playerfunctions;
            MapContact[] mcs = pf.sensormanager.tacticalmap.mapContact;
            Sprite sp; Quaternion rot;
            bool hadIcon = _savedIcon.TryGetValue(i, out sp); _savedIcon.Remove(i);
            bool hadRot = _savedRot.TryGetValue(i, out rot); _savedRot.Remove(i);
            if (i >= mcs.Length || mcs[i] == null) return;
            MapContact mc = mcs[i];
            mc.contactText.text = Strip(mc.contactText.text);
            mc.contactTextShort = Strip(mc.contactTextShort);
            if (pf.currentTargetIndex == i && pf.contactDataName != null) pf.contactDataName.text = Strip(pf.contactDataName.text);
            Color c = pf.sensormanager.GetContactColor(i);
            mc.shipDisplayIcon.color = c; mc.contactText.color = c;
            if (hadIcon) mc.shipDisplayIcon.sprite = sp;
            if (hadRot) mc.shipDisplayIcon.transform.rotation = rot;
        }

        static readonly List<int> _lookIdx = new List<int>();

        /// <summary>The game rewrites contact names, colours and icons now and then; put the decoy look back each frame.
        /// A note and a confirmed decoy look alike (grey, torpedo icon) except for the label: "decoy?" vs "DECOY".</summary>
        static void KeepMarks()
        {
            if (_marked.Count == 0 && _uncloaked.Count == 0) return;
            PlayerFunctions pf = UIFunctions.globaluifunctions.playerfunctions;
            MapContact[] mcs = pf.sensormanager.tacticalmap.mapContact;
            Sprite torp = TorpedoSprite();
            _lookIdx.Clear(); _lookIdx.AddRange(_uncloaked);
            foreach (int k in _marked) if (!_uncloaked.Contains(k)) _lookIdx.Add(k);
            foreach (int i in _lookIdx)
            {
                if (i >= mcs.Length || mcs[i] == null) continue;
                try { ApplyLook(i); } catch (System.Exception ex) { Duel.Log("~Decoy look failed: " + ex.Message); }
                MapContact mc = mcs[i];
                string suffix = _uncloaked.Contains(i) ? ConfirmedSuffix : NoteSuffix;
                mc.contactText.text = WithSuffix(mc.contactText.text, suffix);
                mc.contactTextShort = WithSuffix(mc.contactTextShort, suffix);
                if (pf.currentTargetIndex == i && pf.contactDataName != null) pf.contactDataName.text = WithSuffix(pf.contactDataName.text, suffix.ToUpperInvariant());
                if (mc.contactText.color != MarkColor) mc.contactText.color = MarkColor;
                Image im = mc.shipDisplayIcon;
                if (im == null) continue;
                if (im.color != MarkColor) im.color = MarkColor;
                if (torp != null && im.sprite != torp)
                {
                    _savedIcon[i] = im.sprite; // the game's latest icon for this contact (it may have changed since the tag)
                    if (!_savedRot.ContainsKey(i)) _savedRot[i] = im.transform.rotation;
                    im.sprite = torp;
                }
            }
        }

        // ---------- DECOY button in the code book (signature panel), a copy of its MARK button ----------

        public static bool OwnsArray(DotModTowedArray ta)
        {
            foreach (Ghost g in _ghosts) if (g.V != null && g.V.vesselmovement != null && g.V.vesselmovement.towedArray == ta) return true;
            return false;
        }

        /// <summary>Our torpedo hit the opponent: a "decoy?" note on the real sub is plainly wrong, take it off.</summary>
        public static void OnOpponentHit()
        {
            if (!Duel.InDuel || Duel.Puppet == null) return;
            int i = Duel.Puppet.vesselListIndex;
            if (!_marked.Remove(i)) return;
            Unmark(i);
            PlayerFunctions pf = UIFunctions.globaluifunctions.playerfunctions;
            SensorManager sm = pf.sensormanager;
            string name = ContactName(i) ?? "the contact";
            Color col; if (!pf.messageLogColors.TryGetValue("Sonar", out col)) col = MarkColor;
            pf.PlayerMessage("Sonar: hit on " + name + ", that is the real submarine", col, "CWMP_RealSub");
            Duel.Log("Our torpedo hit a contact tagged decoy?: tag removed, it is the real sub");
        }

        // ---------- 3D look: a tagged contact (note or confirmed) shows as a decoy torpedo ----------
        // Only drawing changes: hits, noise and the opponent's game are untouched. The vessel's own renderers are switched
        // off and a mesh-only copy of the decoy weapon's model rides at its position.

        class Look { public Vessel V; public GameObject Proxy; public readonly List<Renderer> Hidden = new List<Renderer>(); public readonly List<bool> Was = new List<bool>(); public float NextScan; }
        static readonly Dictionary<int, Look> _looks = new Dictionary<int, Look>();

        static DatabaseWeaponData DecoyWeaponFor(int i, Vessel v)
        {
            foreach (Ghost g in _ghosts) if (g.V == v && g.Wd != null) return g.Wd;
            // the real sub: the decoy its class carries (a class without one can't have ghosts, so nothing to imitate)
            Database db = UIFunctions.globaluifunctions.database;
            int[] ids = v.databaseshipdata.torpedoIDs;
            if (ids != null) foreach (int id in ids) if (id >= 0 && id < db.databaseweapondata.Length && IsDecoy(db.databaseweapondata[id])) return db.databaseweapondata[id];
            foreach (Ghost g in _ghosts) if (g.Wd != null) return g.Wd;
            return null;
        }

        static GameObject BuildProxy(GameObject prefab)
        {
            if (prefab == null) return null;
            Transform pr = prefab.transform;
            GameObject root = new GameObject("CWMP_DecoyModel");
            root.layer = prefab.layer;
            root.transform.localScale = pr.lossyScale;
            int parts = 0;
            foreach (MeshFilter mf in prefab.GetComponentsInChildren<MeshFilter>(true))
            {
                MeshRenderer src = mf.GetComponent<MeshRenderer>();
                if (src == null || mf.sharedMesh == null) continue;
                GameObject part = new GameObject("part");
                part.layer = mf.gameObject.layer; // cameras cull by layer (above / below water)
                part.transform.SetParent(root.transform, false);
                part.transform.localPosition = pr.InverseTransformPoint(mf.transform.position);
                part.transform.localRotation = Quaternion.Inverse(pr.rotation) * mf.transform.rotation;
                Vector3 a = mf.transform.lossyScale, b = pr.lossyScale;
                part.transform.localScale = new Vector3(b.x != 0f ? a.x / b.x : 1f, b.y != 0f ? a.y / b.y : 1f, b.z != 0f ? a.z / b.z : 1f);
                part.AddComponent<MeshFilter>().sharedMesh = mf.sharedMesh;
                part.AddComponent<MeshRenderer>().sharedMaterials = src.sharedMaterials;
                parts++;
            }
            if (parts == 0) { Object.Destroy(root); return null; }
            return root;
        }

        static void ApplyLook(int i)
        {
            Vessel[] ev = GameDataManager.enemyvesselsonlevel;
            if (ev == null || i < 0 || i >= ev.Length || ev[i] == null) return;
            Vessel v = ev[i];
            Look l;
            if (!_looks.TryGetValue(i, out l))
            {
                l = new Look { V = v };
                DatabaseWeaponData wd = DecoyWeaponFor(i, v);
                if (wd != null) l.Proxy = BuildProxy(wd.weaponObject);
                if (l.Proxy == null) Duel.Log("~Decoy look: no decoy model for contact " + i + ", only the map changes");
                _looks[i] = l;
            }
            if (l.Proxy == null) return; // never hide the sub without something to show instead
            if (Time.time >= l.NextScan)
            {
                l.NextScan = Time.time + 2f; // new renderers appear now and then (array trail, wake)
                foreach (Renderer r in v.GetComponentsInChildren<Renderer>(true))
                    if (r != null && !l.Hidden.Contains(r)) { l.Hidden.Add(r); l.Was.Add(r.enabled); }
            }
            foreach (Renderer r in l.Hidden) if (r != null && r.enabled) r.enabled = false;
            // the game hides unclear or lost contacts by switching the mesh holder off: follow that
            bool shown = v.meshHolder == null || v.meshHolder.gameObject.activeSelf;
            if (l.Proxy.activeSelf != shown) l.Proxy.SetActive(shown);
            l.Proxy.transform.position = v.transform.position;
            l.Proxy.transform.rotation = v.transform.rotation;
        }

        static void RemoveLook(int i)
        {
            Look l;
            if (!_looks.TryGetValue(i, out l)) return;
            _looks.Remove(i);
            for (int k = 0; k < l.Hidden.Count; k++) if (l.Hidden[k] != null) l.Hidden[k].enabled = l.Was[k];
            if (l.Proxy != null) Object.Destroy(l.Proxy);
        }

        // A small text button "DECOY" on the ID book page, left of the four class icons (own / merchant / warship / sub)
        // in its top-right corner. Only the button's own box takes clicks.
        static void SetupButton()
        {
            if (_decoyButton == null)
            {
                List<Button> classButtons = new List<Button>();
                foreach (Button b in Resources.FindObjectsOfTypeAll<Button>())
                {
                    if (b == null || !b.gameObject.scene.IsValid()) continue; // skip prefab assets
                    if (b.gameObject.name == "CWMP_DecoyButton") { _decoyButton = b.gameObject; _decoyLabel = b.GetComponentInChildren<Text>(); break; }
                    int n = b.onClick.GetPersistentEventCount();
                    for (int k = 0; k < n; k++) if (b.onClick.GetPersistentMethodName(k) == "GoToProfile") { classButtons.Add(b); break; }
                }
                if (_decoyButton == null)
                {
                    if (classButtons.Count == 0)
                    {
                        if (!_buttonMissingLogged) { _buttonMissingLogged = true; Duel.Log("~ID book: class icons not found, no DECOY button (Ctrl+F5 still works)"); }
                        return;
                    }
                    _decoyButton = MakeButton(classButtons);
                }
            }
            if (_decoyButton != null) _decoyButton.SetActive(true);
        }

        static GameObject MakeButton(List<Button> classButtons)
        {
            // leftmost class icon in the row, and the spacing between icons
            Button left = null; RectTransform lr = null; float step = float.MaxValue;
            string layout = "";
            foreach (Button b in classButtons)
            {
                RectTransform r = b.GetComponent<RectTransform>();
                if (r == null) continue;
                layout += " " + b.gameObject.name + "@" + r.anchoredPosition.x.ToString("F0") + "," + r.anchoredPosition.y.ToString("F0") + " " + r.rect.width.ToString("F0") + "x" + r.rect.height.ToString("F0");
                if (left == null || r.anchoredPosition.x < lr.anchoredPosition.x) { left = b; lr = r; }
            }
            if (left == null) return null;
            foreach (Button b in classButtons)
            {
                RectTransform r = b.GetComponent<RectTransform>();
                if (r == null || b.transform.parent != left.transform.parent) continue;
                float gap = Mathf.Abs(r.anchoredPosition.x - lr.anchoredPosition.x);
                if (gap > 1f && gap < step) step = gap;
            }
            Duel.Log("~ID book class icons:" + layout);

            GameObject go = (GameObject)Object.Instantiate(left.gameObject);
            go.name = "CWMP_DecoyButton";
            go.transform.SetParent(left.transform.parent, false);
            for (int i = 0; i < go.transform.childCount; i++) go.transform.GetChild(i).gameObject.SetActive(false); // no icon children
            Image img = go.GetComponent<Image>();
            if (img != null) { Color c = img.color; c.a = 0f; img.color = c; } // invisible, but it is the click box
            const float w = 52f;
            float h = lr.rect.height > 4f ? lr.rect.height : 18f;
            RectTransform rt = go.GetComponent<RectTransform>();
            rt.sizeDelta = new Vector2(w, h);
            float gapPx = step < float.MaxValue ? step - lr.rect.width : 6f;
            if (gapPx < 4f) gapPx = 4f;
            rt.pivot = lr.pivot;
            float iconLeft = lr.anchoredPosition.x + lr.rect.xMin;   // icon's left edge, whatever its pivot
            rt.anchoredPosition = new Vector2(iconLeft - gapPx - w * (1f - lr.pivot.x), lr.anchoredPosition.y);

            // the caption: a copy of one of the page's own texts, so font and colour match
            Text src = null;
            foreach (InGameReference r in Resources.FindObjectsOfTypeAll<InGameReference>())
                if (r != null && r.gameObject.scene.IsValid() && r.ingameReferenceTexts != null && r.ingameReferenceTexts.Length > 0) { src = r.ingameReferenceTexts[0]; break; }
            if (src != null)
            {
                GameObject cap = (GameObject)Object.Instantiate(src.gameObject);
                cap.name = "CWMP_DecoyCaption";
                cap.transform.SetParent(go.transform, false);
                cap.SetActive(true);
                for (int i = 0; i < cap.transform.childCount; i++) cap.transform.GetChild(i).gameObject.SetActive(false);
                foreach (TextLocaliser tl in cap.GetComponentsInChildren<TextLocaliser>(true)) tl.enabled = false;
                RectTransform cr = cap.GetComponent<RectTransform>();
                if (cr != null) { cr.anchorMin = new Vector2(0f, 0f); cr.anchorMax = new Vector2(1f, 1f); cr.pivot = new Vector2(0.5f, 0.5f); cr.offsetMin = new Vector2(0f, 0f); cr.offsetMax = new Vector2(0f, 0f); } // fill the button
                _decoyLabel = cap.GetComponent<Text>();
                if (_decoyLabel != null)
                {
                    _decoyLabel.text = "DECOY";
                    _decoyLabel.alignment = TextAnchor.MiddleCenter;
                    _decoyLabel.horizontalOverflow = HorizontalWrapMode.Overflow;
                    _decoyLabel.raycastTarget = false;
                    _decoyLabel.fontSize = 12;
                }
            }
            else Duel.Log("~ID book: no page text to copy, DECOY button has no caption");

            Button btn = go.GetComponent<Button>();
            btn.onClick = new Button.ButtonClickedEvent();
            btn.onClick.AddListener(new UnityAction(delegate { ToggleMarkCurrent(); }));
            Duel.Log("ID book: DECOY button added left of the class icons at " + rt.anchoredPosition.x.ToString("F0") + "," + rt.anchoredPosition.y.ToString("F0") + " (same as Ctrl+F5)");
            return go;
        }

        /// <summary>Show on the button whether the selected contact is tagged.</summary>
        static void UpdateButtonLabel()
        {
            if (_decoyLabel == null) return;
            int i = UIFunctions.globaluifunctions.playerfunctions.currentTargetIndex;
            string want = (_marked.Contains(i) || _uncloaked.Contains(i)) ? "[DECOY]" : "DECOY";
            if (_decoyLabel.text != want) _decoyLabel.text = want;
        }
    }

    /// <summary>The action report (UIFunctions.GetEngagedContactsList) lists every enemy-list vessel that isn't
    /// biological, an oil rig, civilian or removed from combat: in a duel that was the opponent plus all decoy ghost
    /// slots, used or not (Raf, 27 Nov). The ghosts count as removed from combat while the list is built only.</summary>
    [HarmonyPatch(typeof(UIFunctions), "GetEngagedContactsList")]
    static class Patch_GetEngagedContactsList
    {
        static void Prefix(out List<Vessel> __state)
        {
            __state = null;
            try
            {
                Vessel[] ev = GameDataManager.enemyvesselsonlevel;
                if (ev == null) return;
                foreach (Vessel v in ev)
                    if (v != null && !v.vesselRemovedFromCombat && Ghosts.ReportHidden.Contains(v))
                    {
                        if (__state == null) __state = new List<Vessel>();
                        v.vesselRemovedFromCombat = true; __state.Add(v);
                    }
            }
            catch (System.Exception) { }
        }

        static void Postfix(List<Vessel> __state)
        {
            if (__state != null) foreach (Vessel v in __state) if (v != null) v.vesselRemovedFromCombat = false;
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

namespace ColdWatersMP
{
    [HarmonyPatch(typeof(SensorManager), "SonarCheck")]
    static class Patch_SonarCheck_GhostMasking
    {
        static void Prefix(SensorManager __instance)
        {
            try { Ghosts.BeforeSonarCheck(__instance); }
            catch (System.Exception ex) { Duel.Log("~Ghost masking fix failed: " + ex.Message); }
        }

        static void Postfix()
        {
            try { Ghosts.RestoreRay(); } catch (System.Exception) { }
        }
    }
}
