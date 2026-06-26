using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.PlayerLoop;

namespace CustomMap
{
    internal class DeathZoneTp : MonoBehaviour
    {
        public Transform Checkpoint;
        float timeSince = 0f;
        void Update()
        {
            timeSince += Time.deltaTime;
            if (timeSince < 1f) return;
            timeSince= 0f;
            Character localplayer = Character.localCharacter;
            if (localplayer == null) return;
            if (localplayer.gameObject == null ) return;
            if (localplayer.Center.y < -10)
            {
                Plugin.Log.LogInfo($"TP Player {localplayer.name} to Checkpoint");
                localplayer.data.sinceGrounded = 0f;
                localplayer.data.sinceJump = 0f;
                foreach (Rigidbody rb in localplayer.GetComponentsInChildren<Rigidbody>())
                {
                    rb.linearVelocity = Vector3.zero;
                    rb.angularVelocity = Vector3.zero;
                }
                if (Checkpoint != null)
                {
                    localplayer.WarpPlayerRPC(Checkpoint.position, false);
                    return;
                }
                StartCoroutine(Plugin.WarpToSpawnWhenReady());
            }
        }
    }
}
