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

namespace ColdWatersMP
{
    public static class Ghosts
    {
        public const int Slots = 2;               // decoys that can run at the same time per side
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
        }

        static readonly List<Ghost> _ghosts = new List<Ghost>();
        static Vector3 _park;
        static System.Random _rng = new System.Random();
        static Texture2D _ghostSig; static string _ghostSigFor;
        static readonly HashSet<int> _marked = new HashSet<int>();   // contacts the player tagged as DECOY
        const string MarkSuffix = " DECOY";

        public static void Reset()
        {
            _ghosts.Clear(); _ghostSig = null; _ghostSigFor = null; _marked.Clear();
            _rng = new System.Random();
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
            if (t.tacMapTorpedoIcon != null) t.tacMapTorpedoIcon.gameObject.SetActive(false);

            Ghost g = null;
            foreach (Ghost x in _ghosts) if (x.TorpId < 0 && x.V != null) { g = x; break; }
            if (g == null) { Duel.Log("~Ghosts: no free ghost for decoy #" + torpId); return; }
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
                        continue;
                    }
                    g.LastPos += g.Vel * Time.deltaTime; // keeps going straight while it fades
                    Follow(g, g.LastPos, g.V.transform.eulerAngles.y);
                }
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
            MapContact mc = pf.sensormanager.tacticalmap.mapContact[i];
            if (_marked.Remove(i))
            {
                if (mc != null && mc.contactText.text.EndsWith(MarkSuffix)) mc.contactText.text = mc.contactText.text.Substring(0, mc.contactText.text.Length - MarkSuffix.Length);
                Duel.Log("Contact no longer tagged as decoy");
            }
            else { _marked.Add(i); Duel.Log("Contact tagged as DECOY (your note only)"); }
            KeepMarks();
        }

        static void KeepMarks()
        {
            if (_marked.Count == 0) return;
            MapContact[] mcs = UIFunctions.globaluifunctions.playerfunctions.sensormanager.tacticalmap.mapContact;
            foreach (int i in _marked)
                if (i < mcs.Length && mcs[i] != null && !mcs[i].contactText.text.EndsWith(MarkSuffix)) mcs[i].contactText.text += MarkSuffix;
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
