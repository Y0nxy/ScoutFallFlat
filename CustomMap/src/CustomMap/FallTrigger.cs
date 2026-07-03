using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace ScoutFallFlat
{//Check why this doesn't work!
    public class FallTrigger : MonoBehaviour
    {
        public bool isLevelBeat = false;
        Transform Checkpoint = null!;
        float nextAllowedTp = 0;
        private void OnTriggerEnter(Collider other)
        {
            GameObject go = other.gameObject;
            var Log = Plugin.Log;
            Log.LogInfo("Entered " + go.name);
            //Log.LogInfo("is CharacterLayer!");
            if (go.GetComponent<RigCreatorCollider>() != null) //if Player
            {
                if (Time.time < nextAllowedTp) return;
                nextAllowedTp = Time.time + 1f;
                if (isLevelBeat)
                {
                    if (!Plugin.AdvanceLevels.Value || Plugin.isLastLevel())
                    {
                        if (!Plugin.AdvanceLevels.Value) Log.LogInfo("Level Beat! Resetting level...");
                        else Log.LogInfo("Last Level! Resetting level...");
                        Plugin.TryLoadLevel();
                        Destroy(gameObject);
                        return;
                    }
                    Log.LogInfo("Level Beat! Trying to load next level...");
                    Plugin.TryLoadLevel();
                    Destroy(gameObject);
                    return;
                }
                //Log.LogInfo("isPlayer");
                go = go.GetComponentInParent<Character>().gameObject;
                if (!go.GetComponent<Character>().IsLocal) return; //not local player
                Character localplayer = go.GetComponent<Character>();
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
            RigidBodyStandable rbs = go.GetComponentInParent<RigidBodyStandable>();
            if (rbs != null)
            {
                go = rbs.gameObject;
                Log.LogInfo($"{go.name} is a RigidBodyStandable attempting to tp...");
                Rigidbody rb = go.GetComponent<Rigidbody>();
                rb.linearVelocity = Vector3.zero;
                rb.angularVelocity = Vector3.zero;
                go.transform.position = rbs.originalPos;
                go.transform.rotation = rbs.originalRot;
            }
        }
        //Fallback for players
        float timeSinceCheck = 0f;
        void Update()
        {
            timeSinceCheck += Time.deltaTime;
            if (timeSinceCheck < 15f) return; //every 15 seconds
            timeSinceCheck = 0f;
            Character localplayer = Character.localCharacter;
            if (localplayer == null) return;
            if (localplayer.gameObject == null) return;
            if (localplayer.Center.y < -10)
            {
                Plugin.Log.LogWarning($"FALLBACK! TP Player {localplayer.name} to Checkpoint");
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
