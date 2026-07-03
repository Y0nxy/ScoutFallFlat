using BepInEx.Logging;
using Photon.Pun;
using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace ScoutFallFlat
{
    public class RPCs : MonoBehaviour
    {
        PhotonView photonView;
        ManualLogSource Log = Plugin.Log;
        float delayGuard = 0;
        void Awake()
        {
            photonView = GetComponent<PhotonView>();
        }
        public void NotifyMasterFinishedLevel()
        {
            photonView.RPC("MapFinished", RpcTarget.MasterClient);
        }
        [PunRPC]
        public void MapFinished()
        {
            //map can only be switched every 5 seconds, to prevent multiple calls from multiple players
            if (Time.time < delayGuard) return;
            delayGuard = Time.time + 5f;
            //host checks config to decide if the level should reset or advance
            Log.LogInfo("NotifyMasterFinishedLevel called");
            if (Plugin.AdvanceLevels.Value)
                Log.LogInfo($"Map finished, advancing to next level");
            else
                Log.LogInfo($"Map finished, resetting current level");
            photonView.RPC("RPC_MapFinished", RpcTarget.All, !Plugin.AdvanceLevels.Value);
        }
        [PunRPC]
        public void RPC_MapFinished(bool ResetCurrentLevel = false)
        {
            Log.LogInfo("RPC_MapFinished called");
            if (Plugin.AdvanceLevels.Value)
                Log.LogInfo($"Map finished, advancing to next level");
            else
                Log.LogInfo($"Map finished, resetting current level");
            Plugin.LoadNextLevel(ResetCurrentLevel);
        }
    }
}
