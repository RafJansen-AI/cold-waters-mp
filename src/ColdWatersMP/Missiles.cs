// Cold Waters MP - anti-ship missiles: chaff. C# 5 / .NET 3.5.
// A missile's guidance runs in the firer's game; the defender's game holds a copy that follows the firer's states.
// The defender decides, as for torpedo hits: shoot-downs by guns or SAMs (Weapons, G_TORP_DOWN) and chaff (here).
// Player_AntiMissileGuns.DeployChaff(incomingMissile) throws a chaff cloud and rolls the ship's chaff chance against that
// missile; on success the missile is pointed at the chaff. The defender sends where the cloud is and whether it worked;
// every game shows the cloud, and the firer points its real missile at it.
using System.IO;
using HarmonyLib;
using UnityEngine;

namespace ColdWatersMP
{
    public static class Missiles
    {
        public const byte G_CHAFF = 24;

        static bool _inChaff;
        static GameObject _chaffObj;

        public static void BeforeDeployChaff() { _inChaff = Duel.InDuel; _chaffObj = null; }

        /// <summary>CreatePooled postfix: the first object made inside DeployChaff is the chaff cloud.</summary>
        public static void OnCreated(GameObject go) { if (_inChaff && _chaffObj == null) _chaffObj = go; }

        public static void AfterDeployChaff(Player_AntiMissileGuns amg, Torpedo incoming)
        {
            bool was = _inChaff; _inChaff = false;
            if (!was || _chaffObj == null || Duel.Session == null) return;
            try
            {
                int cm = amg.parentVessel != null ? amg.parentVessel.databaseshipdata.chaffID : -1;
                if (cm < 0) return;
                Vector3 pos = _chaffObj.transform.position; Vector3 eul = _chaffObj.transform.eulerAngles;
                int firer, id;
                bool copy = Weapons.CopyOf(incoming, out firer, out id);
                bool fooled = copy && incoming.chaffed;
                Duel.Session.SendGame(G_CHAFF, w =>
                {
                    w.Write(cm); w.Write(pos.x); w.Write(pos.y); w.Write(pos.z); w.Write(eul.x); w.Write(eul.y); w.Write(eul.z);
                    w.Write(firer); w.Write(id); w.Write(fooled);
                }, true);
                Duel.Log("~Chaff launched" + (copy ? " against missile #" + id + " of player " + firer + (fooled ? ": it went for the chaff" : ": not fooled") : ""));
            }
            catch (System.Exception ex) { Duel.Log("~Chaff send failed: " + ex.Message); }
        }

        public static bool OnMessage(byte type, BinaryReader r)
        {
            if (type != G_CHAFF) return false;
            int cm = r.ReadInt32();
            Vector3 pos = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
            Vector3 eul = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
            int firer = r.ReadInt32(); int id = r.ReadInt32(); bool fooled = r.ReadBoolean();
            if (!Duel.InDuel || Duel.Session == null || Duel.Session.LastSender == Duel.Session.MyId) return true;
            try
            {
                DatabaseCountermeasureData[] cms = UIFunctions.globaluifunctions.database.databasecountermeasuredata;
                if (cm < 0 || cm >= cms.Length || cms[cm] == null || cms[cm].countermeasureObject == null) return true;
                // As the AI's DeployChaff: a pooled chaff cloud with its countermeasure data (no noisemaker list).
                GameObject go = ObjectPoolManager.CreatePooled(cms[cm].countermeasureObject, pos, Quaternion.Euler(eul));
                go.SetActive(true);
                Noisemaker n = go.GetComponent<Noisemaker>();
                if (n != null) n.databasecountermeasuredata = cms[cm];
                if (firer == Duel.Session.MyId && fooled)
                {
                    Torpedo t = Weapons.Own(id);
                    if (t != null && !t.shotDown) { t.chaffed = true; t.targetTransform = go.transform; }
                    Duel.Log("Our missile #" + id + " was decoyed by chaff");
                }
                else Duel.Log("~Opponent chaff launched");
            }
            catch (System.Exception ex) { Duel.Log("~Chaff copy failed: " + ex.Message); }
            return true;
        }
    }

    [HarmonyPatch(typeof(Player_AntiMissileGuns), "DeployChaff")]
    static class Patch_PlayerDeployChaff
    {
        static void Prefix() { Missiles.BeforeDeployChaff(); }
        static void Postfix(Player_AntiMissileGuns __instance, Torpedo incomingMissile) { Missiles.AfterDeployChaff(__instance, incomingMissile); }
    }

    [HarmonyPatch(typeof(ObjectPoolManager), "CreatePooled")]
    static class Patch_CreatePooled_Chaff
    {
        static void Postfix(GameObject __result) { if (__result != null) Missiles.OnCreated(__result); }
    }
}
