// Cold Waters MP - shared time: time compression by mutual agreement, and the host's pause setting. C# 5 / .NET 3.5.
// Time compression (the game's own 10x) runs only while both players have asked for it. Either player stopping it,
// or the game stopping it for an event (contact, damage, shallow water ...), stops it for both.
// Pause: blocked by default (the exit menu opens but the game runs). With the host's "pause both" setting, one
// player's exit menu pauses both games until that player closes it.
using System.Collections.Generic;
using System.IO;
using HarmonyLib;
using UnityEngine;

namespace ColdWatersMP
{
    public static class TimeSync
    {
        public const byte G_TC = 16, G_PAUSE = 17;
        static bool _ownWantsTC, _bypass;
        static bool _ownPaused;
        // Other players who ask for time compression / have paused, by player number. With more than one other
        // player, TC needs every one of them; one stop stops it for all.
        static readonly HashSet<int> _wantTC = new HashSet<int>(), _paused = new HashSet<int>();

        static bool _oppWantsTC
        {
            get
            {
                if (Duel.InGame.Count == 0) return false;
                foreach (Opponent o in Duel.InGame) if (!_wantTC.Contains(o.Id) && Duel.Opps.Contains(o)) return false;
                return true;
            }
        }
        static bool _oppPaused { get { return _paused.Count > 0; } }
        static string Them { get { return Duel.Opps.Count > 1 ? "the other players" : "your opponent"; } }

        public static bool OppPaused { get { return _oppPaused; } }
        public static string StatusLine
        {
            get
            {
                if (!Duel.InDuel) return "";
                string s = "";
                if (_ownWantsTC && !_oppWantsTC) s += "TC requested, waiting for " + (Duel.Opps.Count > 1 ? "the other players (" + _wantTC.Count + " of " + Duel.InGame.Count + ")" : "opponent");
                else if (_wantTC.Count > 0 && !_ownWantsTC) s += (Duel.Opps.Count > 1 ? "Time compression requested" : "Opponent requests TC") + " (press time compression to agree)";
                else if (_ownWantsTC && _oppWantsTC) s += "TC on (" + (Duel.Opps.Count > 1 ? "all" : "both") + " agreed)";
                if (_oppPaused) s += (s.Length > 0 ? " | " : "") + "PAUSED by " + (Duel.Opps.Count > 1 ? "another player" : "opponent");
                return s;
            }
        }

        static PlayerFunctions PF { get { return UIFunctions.globaluifunctions != null ? UIFunctions.globaluifunctions.playerfunctions : null; } }
        static bool TCOn { get { return PF != null && PF.timeCompressionToggle == 1; } }

        static void Say(string msg)
        {
            Duel.Log(msg);
            try { PF.PlayerMessage(msg, PF.messageLogColors["Helm"], "CWMP"); } catch (System.Exception) { }
        }

        static void Send(byte type, bool on) { if (Duel.Session != null) Duel.Session.SendGame(type, w => w.Write(on), true); }

        /// <summary>Run the game's own toggle without our checks.</summary>
        static void ToggleTC()
        {
            _bypass = true;
            try { PF.SetTimeCompression(); }
            finally { _bypass = false; }
        }

        public static void Reset()
        {
            bool wasPausedByOpp = _oppPaused;
            if (TCOn && Duel.InDuel) { try { ToggleTC(); } catch (System.Exception) { } } // back to 1x
            _ownWantsTC = false; _wantTC.Clear(); _ownPaused = false; _paused.Clear();
            if (wasPausedByOpp && Time.timeScale == 0f) { Time.timeScale = 1f; Time.fixedDeltaTime = 0.02f; }
        }

        // ---- time compression ----

        /// <summary>SetTimeCompression prefix. false = don't toggle (yet).</summary>
        public static bool OnSetTimeCompression()
        {
            if (!Duel.InDuel || _bypass || PF == null) return true;
            if (TCOn)
            {
                // Turning off: always allowed, and stops it for the opponent too. Both must ask again to restart.
                _ownWantsTC = false; _wantTC.Clear();
                Send(G_TC, false);
                Duel.Log("Time compression stopped (for all)");
                return true;
            }
            if (!Match.TimeCompressionAllowed) { Say("Time compression is off in this match"); return false; }
            if (_oppPaused) return false;
            if (_ownWantsTC && !_oppWantsTC)
            {
                // Second press while waiting: withdraw the request.
                _ownWantsTC = false; Send(G_TC, false);
                Say("Time compression request withdrawn");
                return false;
            }
            _ownWantsTC = true;
            Send(G_TC, true);
            if (_oppWantsTC) { Duel.Log("Time compression on (all agreed)"); return true; }
            Say("Time compression requested: it starts when " + Them + " also ask for it");
            return false;
        }

        /// <summary>StopTimeCompression prefix (the "stop" key): stop for both, or withdraw a request.</summary>
        public static void OnStopTimeCompression()
        {
            if (!Duel.InDuel || _bypass || PF == null) return;
            if (TCOn || _ownWantsTC) { _ownWantsTC = false; if (TCOn) _wantTC.Clear(); Send(G_TC, false); Duel.Log("Time compression stopped/withdrawn"); }
        }

        /// <summary>PlayerMessage postfix: a crew callout of a launch transient or a torpedo locking on to us
        /// ("torpedo in the water") stops time compression, for both. The base game doesn't do this itself.</summary>
        public static void OnPlayerMessage(string key)
        {
            if (!Duel.InDuel || _bypass || PF == null || !TCOn) return;
            if (key != "TransientKnown" && key != "TransientUnknown" && key != "TorpedoInTheWater") return;
            Duel.Log("Time compression stopped by callout " + key);
            PF.SetTimeCompression(); // our prefix: turning off stops it for the opponent too
        }

        // ---- pause ----

        /// <summary>BringInExitMenu postfix: the menu paused us (timeScale 0).</summary>
        public static void OnExitMenuOpened()
        {
            if (!Duel.InDuel || Time.timeScale != 0f) return;
            if (Match.PauseBoth)
            {
                _ownPaused = true;
                Send(G_PAUSE, true);
                Duel.Log("Paused: the other games are paused too");
                return;
            }
            Time.timeScale = 1f;
            Time.fixedDeltaTime = 0.02f;
            Duel.Log("Pause blocked in multiplayer: the game keeps running");
        }

        /// <summary>DismissExitMenu postfix: resume both (unless the opponent is still paused).</summary>
        public static void OnExitMenuClosed()
        {
            if (!Duel.InDuel) return;
            if (_ownPaused) { _ownPaused = false; Send(G_PAUSE, false); Duel.Log("Resumed"); }
            if (_oppPaused) Time.timeScale = 0f;
        }

        public static bool OnMessage(byte type, BinaryReader r)
        {
            if (type != G_TC && type != G_PAUSE) return false;
            bool on = r.ReadBoolean();
            if (!Duel.InDuel || PF == null) return true;
            int from = Duel.Session.LastSender;
            bool many = Duel.Opps.Count > 1;
            if (type == G_TC)
            {
                if (on) _wantTC.Add(from); else _wantTC.Remove(from);
                if (on)
                {
                    if (_ownWantsTC && _oppWantsTC && !TCOn) { ToggleTC(); Duel.Log("Time compression on (all agreed)"); }
                    else if (!_ownWantsTC) Say((many ? "Another player requests" : "Opponent requests") + " time compression: press time compression to agree");
                }
                else
                {
                    // one player's stop stops it for everyone: forget all requests
                    _wantTC.Clear();
                    if (TCOn) { _ownWantsTC = false; ToggleTC(); Say((many ? "Another player" : "Opponent") + " stopped time compression"); }
                    else if (_ownWantsTC) { _ownWantsTC = false; Say("Time compression: " + (many ? "another player" : "opponent") + " declined or stopped; ask again"); }
                }
                return true;
            }
            // G_PAUSE
            if (on) _paused.Add(from); else _paused.Remove(from);
            if (on)
            {
                if (TCOn) { _ownWantsTC = false; ToggleTC(); }
                Time.timeScale = 0f;
                Say((many ? "Another player" : "Opponent") + " paused the game");
            }
            else
            {
                if (!_ownPaused && !_oppPaused) { Time.timeScale = 1f; Time.fixedDeltaTime = 0.02f; }
                Say((many ? "Another player" : "Opponent") + " resumed the game");
            }
            return true;
        }
    }

    [HarmonyPatch(typeof(PlayerFunctions), "SetTimeCompression")]
    static class Patch_SetTimeCompression
    {
        static bool Prefix() { return TimeSync.OnSetTimeCompression(); }
    }

    [HarmonyPatch(typeof(PlayerFunctions), "StopTimeCompression")]
    static class Patch_StopTimeCompression
    {
        static void Prefix() { TimeSync.OnStopTimeCompression(); }
    }

    [HarmonyPatch(typeof(PlayerFunctions), "PlayerMessage")]
    static class Patch_PlayerMessage
    {
        // __2 = the message key (3rd parameter).
        static void Postfix(string __2)
        {
            try { TimeSync.OnPlayerMessage(__2); }
            catch (System.Exception ex) { Duel.Log("Callout TC stop failed: " + ex.Message); }
        }
    }

    [HarmonyPatch(typeof(MissionManager), "DismissExitMenu")]
    static class Patch_DismissExitMenu
    {
        static void Postfix() { TimeSync.OnExitMenuClosed(); }
    }
}
