// Cold Waters MP - gun shell hits on a ship show a hit, not a water splash. C# 5 / .NET 3.5.
// In the game a naval shell never touches a hull: it flies down to the waterline, splashes, and its blast zone damages
// whatever compartment it overlaps. So a hit looked like a miss (a splash next to or inside the ship). DotMod's Database
// has an impact effect by calibre (CreateImpactEffect) that nothing calls. When a shell reaches the water inside a
// vessel's hull, the splash is replaced by that impact effect. Damage is unchanged. Applies in single player too
// (Raf, 28 Nov: "changing DotMod, so be it"); in multiplayer both games show the same, since copies fly identically.
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace ColdWatersMP
{
    public static class HitFx
    {
        static bool _swallow;      // the next splash from this FixedUpdate is a hull hit
        static Vector3 _pos;
        static float _calibre;
        static int _logged;
        const int CompartmentLayer = 9; // compartment trigger colliders (HITVOL dump, 27 Nov)

        /// <summary>Calibre in inches by shell name (no calibre in the data); else a guess from the warhead.</summary>
        static readonly Dictionary<string, float> Calibres = new Dictionary<string, float>
        {
            { "plan_type_79a_shell", 5.1f }, { "uk_mark_8_shell", 4.5f }, { "usn_mark_12_shell", 5f }, { "usn_mark_42_shell", 5f },
            { "usn_mark_45_shell", 5f }, { "usn_mark_75_shell", 3f }, { "usn_mark_7_shell", 16f }, { "wp_4x57mm_shell", 2.2f },
            { "wp_6x3_b-38_shell", 6f }, { "wp_ak-100_shell", 3.9f }, { "wp_ak-130_shell", 5.1f }, { "wp_ak-725_shell", 2.2f },
            { "wp_ak-726_shell", 3f }, { "wp_b-34_shell", 3.9f }, { "wp_sm2_shell", 5.1f },
        };

        static float Calibre(int id)
        {
            try
            {
                DatabaseDepthChargeData d = UIFunctions.globaluifunctions.database.databasedepthchargedata[id];
                float c;
                if (d.depthchargeName != null && Calibres.TryGetValue(d.depthchargeName, out c)) return c;
                if (d.depthChargePrefabReference != null && Calibres.TryGetValue(d.depthChargePrefabReference, out c)) return c;
                return Mathf.Clamp(d.warhead / 3f, 2f, 16f);
            }
            catch (System.Exception) { return 5f; }
        }

        /// <summary>The vessel whose hull (compartment colliders) contains this point, or null.</summary>
        static Vessel HullAt(Vector3 p)
        {
            Collider[] hits = Physics.OverlapSphere(p, 0.03f, 1 << CompartmentLayer, QueryTriggerInteraction.Collide);
            foreach (Collider c in hits)
            {
                Compartment comp = c.GetComponent<Compartment>();
                if (comp != null && comp.activeVessel != null && !comp.activeVessel.isSinking) return comp.activeVessel;
            }
            return null;
        }

        /// <summary>FixedUpdate prefix: a shell about to splash. If the splash point is inside a hull, swallow the splash.</summary>
        public static void BeforeFixedUpdate(Projectile_DepthCharge dc)
        {
            _swallow = false;
            if (dc == null || !dc.isNavalShell || dc.hasSplashed) return;
            Vector3 p = dc.transform.position;
            if (p.y >= 1000f) return;
            Vessel v = HullAt(new Vector3(p.x, 1000f, p.z));
            if (v == null) return;
            _swallow = true; _pos = new Vector3(p.x, 1000f, p.z); _calibre = Calibre(dc.depthChargeID);
            if (_logged++ < 10) Duel.Log("~Rounds: shell hit on " + (v.playercontrolled ? "our vessel" : v.databaseshipdata.shipPrefabName) + ", impact effect instead of splash (" + _calibre.ToString("F1") + " in)");
        }

        public static void AfterFixedUpdate() { _swallow = false; }

        /// <summary>CreatePooled prefix: replace the swallowed splash with the impact effect.</summary>
        public static bool BeforeCreatePooled(GameObject prefab, ref GameObject result)
        {
            if (!_swallow || prefab == null) return true;
            GameObject[] splashes = UIFunctions.globaluifunctions.database.splashes;
            if (splashes == null || System.Array.IndexOf(splashes, prefab) < 0) return true;
            _swallow = false; // before the call: CreateImpactEffect itself uses CreatePooled
            // Doubled calibre (capped below the 18-in magazine explosion): the effect for the real calibre looked small (Raf, 28 Nov).
            // Steps in CreateImpactEffect: 5, 7, 12, 18 in. So 57 mm stays smallest, 76 mm one up, 100 mm-5 in two up, 6-16 in largest.
            try { UIFunctions.globaluifunctions.database.CreateImpactEffect(Mathf.Min(_calibre * 2f, 17f), _pos); }
            catch (System.Exception ex) { Duel.Log("~Impact effect failed: " + ex.Message); return true; }
            try { PlayHitSound(_calibre, _pos, prefab); } catch (System.Exception ex) { Duel.Log("~Hit sound failed: " + ex.Message); }
            result = null; // FixedUpdate discards it
            return false;
        }

        static AudioClip _small, _medium;

        /// <summary>The splash prefab carries its own sound; the impact effect has none, so hits were silent (Raf, 28 Nov). DotMod
        /// ships two explosion sounds nothing uses (audio/guns): small below 5 in, medium from 5 in. Set up like the gun's shot
        /// sound (3D, linear, 10-40 units), on the splash's mixer group so the game's volume settings apply.</summary>
        static void PlayHitSound(float calibre, Vector3 pos, GameObject splashPrefab)
        {
            TextParser tp = UIFunctions.globaluifunctions.textparser;
            if (_small == null) _small = tp.GetAudioClip("audio/guns/Small Explosion 3.wav");
            if (_medium == null) _medium = tp.GetAudioClip("audio/guns/Med Explosion 3.wav");
            AudioClip clip = calibre >= 5f ? (_medium ?? _small) : (_small ?? _medium);
            if (clip == null) return;
            GameObject go = new GameObject("CWMP hit sound");
            go.transform.position = pos;
            AudioSource a = go.AddComponent<AudioSource>();
            AudioSource like = splashPrefab != null ? splashPrefab.GetComponentInChildren<AudioSource>(true) : null;
            if (like != null) { a.outputAudioMixerGroup = like.outputAudioMixerGroup; a.volume = like.volume; }
            a.clip = clip; a.rolloffMode = AudioRolloffMode.Linear; a.minDistance = 10f; a.maxDistance = 40f;
            a.spatialBlend = 1f; a.loop = false; a.playOnAwake = false;
            a.Play();
            Object.Destroy(go, clip.length + 0.5f);
        }

        public static void Reset() { _swallow = false; _logged = 0; }
    }

    [HarmonyPatch(typeof(Projectile_DepthCharge), "FixedUpdate")]
    static class Patch_DepthChargeFixedUpdate_HitFx
    {
        static void Prefix(Projectile_DepthCharge __instance) { try { HitFx.BeforeFixedUpdate(__instance); } catch (System.Exception) { } }
        static void Postfix() { HitFx.AfterFixedUpdate(); }
    }

    [HarmonyPatch(typeof(ObjectPoolManager), "CreatePooled")]
    static class Patch_CreatePooled_HitFx
    {
        static bool Prefix(GameObject prefab, ref GameObject __result) { return HitFx.BeforeCreatePooled(prefab, ref __result); }
    }
}
