using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using Photon.Pun;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.Assertions;

namespace ScoutFallFlat
{
    [BepInAutoPlugin]
    public partial class Plugin : BaseUnityPlugin
    {
        internal static ManualLogSource Log { get; private set; } = null!;
        private static ConfigEntry<string> Level = null!;
        public static ConfigEntry<bool> AdvanceLevels = null!;
        private static ConfigEntry<float> ropeLength = null!;
        private static ConfigEntry<bool> enableRopePatch = null!;

        internal static Plugin Instance { get; private set; } = null!;
        private static Dictionary<string, Shader> _peakShaders;

        // -----------------------------------------------------------------------
        // CONFIGURE THESE to match your AssetBundle and prefab
        // -----------------------------------------------------------------------

        // Name of the AssetBundle file (must sit next to this DLL)
        private const string BundleFileName = "maps.bundle";
        //private const string BundleFileName = "dropper_assets";

        // Exact name of the prefab asset inside the bundle
        private const string path = "Assets/_Maps/Maps/";
        private string PrefabName = path + "Intro.prefab";
        //private const string PrefabName = "Dropper Prefab";

        // Far from the vanilla map so your prefab doesn't clip into it.
        // Increase further if the vanilla map is still visible.
        private static readonly Vector3 SpawnPosition = new Vector3(0f, 10f, 0f);
        private static readonly Quaternion SpawnRotation = Quaternion.identity;
        private static AssetBundle _loadedBundle = null;
        static string[] Levels = ["Intro", "Train", "Carry", "Climb", "Break"];// for now break%
        // -----------------------------------------------------------------------

        private void Awake()
        {
            Instance = this;
            Log = Logger;
            Level = Config.Bind("Map", "Map Name", "Intro", "Name of the prefab to spawn, without the .prefab extension. Must match an asset in the bundle.");
            AdvanceLevels = Config.Bind("Map", "Advance Levels", true);
            enableRopePatch = Config.Bind("Map", "CustomRope", false);
            ropeLength = Config.Bind("Map", "Rope Length", 100f);
            PrefabName = path + Level.Value + ".prefab";
            Level.SettingChanged += (s, e) =>
            {
                foreach (string level in Levels)
                {
                    if (level.Equals(Level.Value, System.StringComparison.OrdinalIgnoreCase))
                    {
                        Level.Value = level;
                        PrefabName = path + Level.Value + ".prefab";
                        Log.LogInfo($"ScoutFallFlat: Map name changed to \"{Level.Value}\". Will spawn \"{PrefabName}\".");
                        return;
                    }
                }
                Log.LogWarning($"ScoutFallFlat: Map name \"{Level.Value}\" not found in Levels array. Will spawn \"{PrefabName}\".");
            };

            Harmony.CreateAndPatchAll(typeof(SceneWatcher), Id);
            Harmony.CreateAndPatchAll(typeof(CustomRope));
            Log.LogInfo($"Plugin {Name} is loaded! Will spawn \"{PrefabName}\" from bundle \"{BundleFileName}\" on map load.");
        }

        private void OnDestroy()
        {
            if (Instance == this)
                Instance = null!;
        }

        // -----------------------------------------------------------------------
        // Harmony patch: fires when the ScoutmasterSpawner wakes up in the
        // gameplay scene — same hook the Dropper mod uses as its "map is ready"
        // signal.
        // -----------------------------------------------------------------------
        private static class SceneWatcher
        {
            [HarmonyPatch(typeof(ScoutmasterSpawner), "Awake")]
            [HarmonyPostfix]
            private static void Postfix()
            {
                if (Instance == null) return;
                Instance.StartCoroutine(SpawnMapWhenReady());
            }
            //Hunger patch
            [HarmonyPatch(typeof(CharacterAfflictions), "AddStatus")]
            [HarmonyPrefix]
            private static bool No_Hunger(CharacterAfflictions.STATUSTYPE statusType)
            {
                if (statusType == CharacterAfflictions.STATUSTYPE.Hunger)
                    return false;
                
                return true;
            }

        }

        //make rope longer (configurable)
        private static class CustomRope
        {
            [HarmonyPatch(typeof(RopeAnchorWithRope), "SpawnRope")]
            [HarmonyPrefix]
            static void Prefix(RopeAnchorWithRope __instance)
            {
                if (!enableRopePatch.Value) return;
                if (__instance.ropePrefab.name.Contains("Anti"))
                {
                    Plugin.Log.LogInfo("Rope anchor has antigravity, skipping rope change");
                    return;
                }
                __instance.ropePrefab = Resources.Load<GameObject>("RopeDynamicBreakable");
                __instance.ropeSegmentLength = ropeLength.Value;
                Plugin.Log.LogInfo("Rope prefab changed to breakable version");
            }

            [HarmonyPatch(typeof(Rope), nameof(Rope.MaxSegments), MethodType.Getter)]
            [HarmonyPrefix]
            public static bool max_Prefix(ref int __result)
            {
                __result = 9999; // Your new custom max limit
                return false;   // Skip the original method execution
            }
        }

        // ----------------------------------------------w-------------------------
        // Wait until the vanilla scene objects are present, then spawn the prefab.
        // Mirrors the guard used by Dropper's DelayedActivation.
        // -----------------------------------------------------------------------
        private static IEnumerator SpawnMapWhenReady()
        {
            // Brief settle — let the scene finish constructing
            yield return new WaitForSeconds(2f);

            float timeout = Time.realtimeSinceStartup + 15f;
            while (Time.realtimeSinceStartup < timeout)
            {
                // Same scene-readiness checks as the Dropper mod
                if (GameObject.Find("Map") != null && GameObject.Find("GAME") != null)
                    break;

                yield return null;
            }

            if (GameObject.Find("Map") == null || GameObject.Find("GAME") == null)
            {
                Log.LogWarning("CustomMap: timed out waiting for scene objects (Map / GAME). Prefab not spawned.");
                yield break;
            }

            // Already spawned this run?
            if (GameObject.Find(Instance.PrefabName + "(Clone)") != null ||
                GameObject.Find(Instance.PrefabName) != null)
            {
                Log.LogInfo("CustomMap: prefab already present in scene, skipping spawn.");
                yield break;
            }

            ClearNormalMap();
            // Give Unity a single frame frame buffer to clear memory pointers
            yield return null;

            // 2. CRITICAL: yield return makes this coroutine wait until the map is completely loaded,
            // materials are set up, and scripts are attached.
            yield return Instance.StartCoroutine(SpawnMapPrefabAsync());

            /// Give physics and layout engines one frame to settle
            yield return null;

            yield return Instance.StartCoroutine(WarpToSpawnWhenReady());
        }

        // -----------------------------------------------------------------------
        // Load the AssetBundle from disk and instantiate the prefab.
        // -----------------------------------------------------------------------
        private static IEnumerator SpawnMapPrefabAsync()
        {
            string dllDirectory = Path.GetDirectoryName(Instance.Info.Location) ?? ".";
            string bundlePath = Path.Combine(dllDirectory, BundleFileName);
            
            if (!File.Exists(bundlePath))
            {
                Log.LogError($"CustomMap: AssetBundle not found at \"{bundlePath}\". " +
                             $"Place \"{BundleFileName}\" next to the plugin DLL.");
                yield break;
            }

            if (_loadedBundle == null)
            {
                AssetBundleCreateRequest bundleLoadRequest = AssetBundle.LoadFromFileAsync(bundlePath);
                yield return bundleLoadRequest;
                _loadedBundle = bundleLoadRequest.assetBundle;
            }
            AssetBundle bundle = _loadedBundle;
            if (bundle == null)
            {
                Log.LogError($"CustomMap: Failed to load AssetBundle from \"{bundlePath}\".");
                yield break;
            }
            Log.LogInfo($"CustomMap: AssetBundle contains assets: {string.Join(", ", bundle.GetAllAssetNames())}");
            GameObject prefab = bundle.LoadAsset<GameObject>(Instance.PrefabName);
            if (prefab == null)
            {
                Log.LogError($"CustomMap: Prefab \"{Instance.PrefabName}\" not found inside the bundle. ");
                yield break;
            }
            SetupMaterial(prefab);
            //if (prefab.GetComponentInChildren<Collider>() == null)
            //{
            //    prefab.AddComponent<BoxCollider>().isTrigger = true;
            //}

            GameObject map = Object.Instantiate(prefab, SpawnPosition, SpawnRotation);
            map.name = Instance.PrefabName; // remove "(Clone)" for the duplicate-check above
            map.transform.Find("Directional Light")?.gameObject.SetActive(false);
            AttachScriptsToGameObjets(map);
            //DebugLogRendererState(map);
            ClearSceneFog();
            HideSceneWalls();
            Log.LogInfo($"CustomMap: Spawned \"{Instance.PrefabName}\" at {SpawnPosition}.");
        }

        // -----------------------------------------------------------------------
        // Wait for both the local Character and the spawn marker to exist in the
        // scene, then warp the player there.
        // -----------------------------------------------------------------------
        public static IEnumerator WarpToSpawnWhenReady()
        {
            string[] SpawnMarkersName = {"Spawnpoint","SpawnPoint"};
            float timeout = Time.realtimeSinceStartup + 15f;

            while (Time.realtimeSinceStartup < timeout)
            {
                Character local = Character.localCharacter;
                if (local == null)
                {
                    yield return null;
                    continue;
                }

                GameObject rootPrefab = GameObject.Find(Instance.PrefabName);
                if (rootPrefab == null) {
                    yield return null;
                    continue;
                }

                GameObject marker = null!;
                foreach (Transform t in rootPrefab.GetComponentsInChildren<Transform>())
                {
                    bool isfound = false;
                    foreach (string spawnMarkerName in SpawnMarkersName)
                    {
                        if (t.name == spawnMarkerName)
                        {
                            marker = t.gameObject;
                            isfound = true;
                            if (marker != null) break;
                        }
                    }
                    if (isfound) break;
                }
                if (marker == null)
                {
                    yield return null;
                    continue;
                }

                Vector3 spawnPos = marker.transform.position + Vector3.up * 1.5f;
                local.data.sinceGrounded = -15f;
                local.data.sinceJump = -15f;
                foreach (Rigidbody rb in local.GetComponentsInChildren<Rigidbody>())
                {
                    rb.linearVelocity = Vector3.zero;
                    rb.angularVelocity = Vector3.zero;
                }
                local.WarpPlayerRPC(spawnPos + Vector3.up * 15f, false);
                local.Fall(1);
                Log.LogInfo($"CustomMap: Warped local player to \"{marker.name}\" at {spawnPos}.");
                yield break;
            }

            Log.LogWarning($"CustomMap: Timed out waiting for SpawnPoint or local character. Player not warped.");
        }
        
        private static Dictionary<string, Shader> PeakShaders
        {
            get
            {
                bool flag = Plugin._peakShaders == null;
                if (flag)
                {
                    string[] array = new string[]
                    {
                        "W/Peak_Standard", "W/Character", "W/Peak_Transparent", "W/Peak_Glass", "W/Peak_Clip", "W/Peak_glass_liquid", "W/Peak_GroundTransition", "W/Peak_Guidebook", "W/Peak_Honey", "W/Peak_Ice",
                        "W/Peak_Rock", "W/Peak_Rope", "W/Peak_Splash", "W/Peak_Waterfall", "W/Vine", "Universal Render Pipeline/Lit"
                    };
                    Plugin._peakShaders = new Dictionary<string, Shader>();
                    foreach (string text in array)
                    {
                        Shader shader = Shader.Find(text);
                        bool flag2 = shader != null;
                        if (flag2)
                        {
                            Plugin._peakShaders[text] = shader!;
                        }
                    }
                }
                return Plugin._peakShaders!;
            }
        }
        private static void SetupMaterial(GameObject go)
        {
            foreach (Renderer renderer in go.GetComponentsInChildren<Renderer>())
            {
                foreach (Material material in renderer.materials)
                {
                    Shader shader;
                    bool flag = Plugin.PeakShaders.TryGetValue(material.shader.name, out shader);
                    if (flag)
                    {
                        material.shader = shader;
                    }
                }
            }
            foreach (Renderer renderer2 in go.GetComponentsInChildren<Renderer>())
            {
                foreach (Material material2 in renderer2.materials)
                {
                    bool flag2 = material2.shader.name == "Universal Render Pipeline/Lit" && !material2.IsKeywordEnabled("_EMISSION") && !Plugin.IsTransparent(material2) && !Plugin.HasAlphaClipping(material2);
                    if (flag2)
                    {
                        Texture texture = material2.GetTexture("_BaseMap");
                        bool flag3 = texture != null;
                        if (flag3)
                        {
                            material2.shader = Shader.Find("W/Peak_Standard");
                            material2.SetTexture("_BaseTexture", texture);
                        }
                    }
                }
            }
        }
        private static bool IsTransparent(Material mat)
        {
            bool flag = mat.HasProperty("_Surface");
            bool flag2;
            if (flag)
            {
                flag2 = mat.GetFloat("_Surface") > 0.5f;
            }
            else
            {
                flag2 = mat.renderQueue >= 3000;
            }
            return flag2;
        }
        private static bool HasAlphaClipping(Material mat)
        {
            return mat.IsKeywordEnabled("_ALPHATEST_ON");
        }
        private static void ClearSceneFog()
        {
            RenderSettings.fog = false;
            Shader.SetGlobalFloat("FogHeight", -9999f);
            Shader.SetGlobalFloat("FogEnabled", 0f);
            Shader.SetGlobalFloat("_FogSphereSize", 99999f);
            Shader.SetGlobalFloat("EXTRAFOG", 0f);
            Shader.SetGlobalFloat("HeightFogAmount", 0f);
            Shader.SetGlobalFloat("_WeatherBlend", 0f);
            Shader.SetGlobalFloat("_GlobalHazeAmount", 0f);
            GameObject gameObject = GameObject.Find("Fog");
            bool flag = gameObject != null;
            if (flag)
            {
                gameObject?.SetActive(false);
            }
            GameObject gameObject2 = GameObject.Find("FogSphereSystem");
            bool flag2 = gameObject2 != null;
            if (flag2)
            {
                gameObject2?.SetActive(false);
            }
            GameObject gameObject3 = GameObject.Find("FogCutouts");
            bool flag3 = gameObject3 != null;
            if (flag3)
            {
                gameObject3?.SetActive(false);
            }
            GameObject gameObject4 = GameObject.Find("Post Fog");
            if (gameObject4 != null)
            {
                gameObject4?.SetActive(false);
            }
            }
        private static void HideSceneWalls()
        {
            string[] array = new string[] { "EdgeWalls", "Global" };
            foreach (string text in array)
            {
                GameObject gameObject = GameObject.Find(text);
                bool flag = gameObject != null;
                if (flag)
                {
                    gameObject?.SetActive(false);
                }
            }
        }
        //Claude Debug
        private static void DebugLogRendererState(GameObject root)
        {
            Log.LogInfo("=== CustomMap Renderer Debug Dump ===");

            foreach (Renderer renderer in root.GetComponentsInChildren<Renderer>(true))
            {
                string path = GetHierarchyPath(renderer.transform);
                bool isMeshRenderer = renderer is MeshRenderer;
                MeshFilter mf = renderer.GetComponent<MeshFilter>();
                SkinnedMeshRenderer smr = renderer as SkinnedMeshRenderer;

                Mesh mesh = mf != null ? mf.sharedMesh : (smr != null ? smr.sharedMesh : null);

                string meshInfo = mesh == null
                    ? "MESH=NULL"
                    : $"MESH='{mesh.name}' verts={mesh.vertexCount} subMeshes={mesh.subMeshCount} bounds={mesh.bounds}";

                string matInfo = string.Join(" | ", System.Array.ConvertAll(renderer.sharedMaterials, m =>
                    m == null ? "MAT=NULL" : $"{m.name}[shader={m.shader?.name ?? "NULL"}, renderQueue={m.renderQueue}]"));

                bool staticFlagBatching = GameObjectUtility_IsBatchingStatic(renderer.gameObject);

                Log.LogInfo(
                    $"[{path}] " +
                    $"enabled={renderer.enabled} activeInHierarchy={renderer.gameObject.activeInHierarchy} " +
                    $"isVisible={renderer.isVisible} layer={LayerMask.LayerToName(renderer.gameObject.layer)} " +
                    $"boundsWorld={renderer.bounds} " +
                    $"{meshInfo} | {matInfo}"
                );
            }

            Log.LogInfo("=== End Renderer Debug Dump ===");
        }

        private static bool GameObjectUtility_IsBatchingStatic(GameObject go)
        {
            // staticEditorFlags isn't available at runtime/in builds; this is a placeholder
            // in case you want to check go.isStatic, which DOES survive into builds.
            return go.isStatic;
        }

        private static string GetHierarchyPath(Transform t)
        {
            string path = t.name;
            while (t.parent != null)
            {
                t = t.parent;
                path = t.name + "/" + path;
            }
            return path;
        }
    
        static void AttachScriptsToGameObjets(GameObject map)
        {
            foreach (Transform child in map.GetComponentsInChildren<Transform>(true))
            {
                string name = child.name;
                if (name.Contains("PlateButton") || child.name.Contains("Button_Apply")) child.gameObject.AddComponent<PressurePlate>();
                if (name.Contains("AutomaticDoor"))
                {
                    if (child.Find("Left") == null || child.Find("Right") == null)
                    {
                        Log.LogWarning($"CustomMap: AutomaticDoor '{child.name}' is missing 'Left' or 'Right' child. Skipping script attachment.");
                        continue;
                    }
                    child.gameObject.AddComponent<AutomaticDoor>();
                }
                if (name.Contains("Elevator_Apply")) child.gameObject.AddComponent<ElevatorLinear>();
                if (name.Contains("ElevatorButton")) child.gameObject.AddComponent<ElevatorButton>();
                if (child.GetComponent<Rigidbody>() != null)
                {
                    GameObject go = child.gameObject;
                    if (go.GetComponent<Rigidbody>().mass < 700)
                        go.AddComponent<RigidBodyStandable>();
                }
                if (name.Contains("Crate"))
                {
                    GameObject go = child.gameObject;
                    go.AddComponent<PhotonView>();
                    //go.AddComponent<PhotonTransformView>();
                    go.AddComponent<PhotonRigidbodyView>();
                    //if (go.GetComponent<Rigidbody>().mass < 700)
                    //    go.AddComponent<RigidBodyStandable>();
                    child.gameObject.layer = LayerMask.NameToLayer("Character");
                }
                if (name.Contains("Shatter")) child.transform.parent.gameObject.AddComponent<VoronoiShatter>();
                if (name.Contains("FallTrigger")) child.gameObject.AddComponent<FallTrigger>();
                if (name.Contains("PassTrigger")) child.gameObject.AddComponent<PassTrigger>();
            }
            map.AddComponent<PVSyncer>();
        }

        static void ClearNormalMap()
        {
            GameObject map = GameObject.Find("Map");
            if (map != null)
            {
                PhotonView[] viewsInMap = map.GetComponentsInChildren<PhotonView>(true);
                foreach (PhotonView view in viewsInMap)
                {
                    if (view != null) PhotonNetwork.LocalCleanPhotonView(view);
                }
                foreach (Transform t in map.transform)
                {
                    Destroy(t.gameObject);
                }
            }
            foreach (Transform t in FindObjectsByType<Transform>(FindObjectsSortMode.None))
            {
                GameObject go = t.gameObject;
                if (go.layer == LayerMask.NameToLayer("Water")) Destroy(go);
                if (go.GetComponent<ItemParticles>() != null) Destroy(go);
            }
            GameObject Misc = GameObject.Find("Misc");
            foreach (Transform t in Misc.transform)
            {
                if (t.name.Contains("Spawn")) Destroy(t.gameObject);
            }
            //GameObject spawnPoints = GameObject.Find("Misc/SpawnPoints");
            //if (spawnPoints != null) Destroy(spawnPoints);
            //spawnPoints = GameObject.Find("Misc/SpawnPoints (1)");
            //if (spawnPoints != null) Destroy(spawnPoints);
        }
        public static void LoadNextLevel(bool ResetCurrentLevel = false)
        {
            //do stuff
            int numberInArray = 0;
            foreach (string level in Levels)
            {
                string cleanName = Instance.PrefabName.Replace(path, "").Replace(".prefab", "");
                if (level.Equals(cleanName))
                {
                    if (!ResetCurrentLevel) numberInArray++;
                    LoadLevelRemoveLastLevel(numberInArray);
                    break;
                }
                numberInArray++;
            }
        }
        private static void LoadLevelRemoveLastLevel(int numberInArray)
        {
            if (numberInArray >= Levels.Length)
            {
                Log.LogInfo("Last Level!");
                return;
            }
            string nextLevel = Levels[numberInArray];
            Log.LogInfo("Loading Level: " + nextLevel);
            GameObject lastLevel = GameObject.Find(Instance.PrefabName);
            if (lastLevel != null)
            {
                PhotonView[] viewsInMap = lastLevel.GetComponentsInChildren<PhotonView>(true);
                foreach (PhotonView view in viewsInMap)
                {
                    if (view != null) PhotonNetwork.LocalCleanPhotonView(view);
                }
                Destroy(lastLevel);
            }
            Instance.PrefabName = path + nextLevel + ".prefab";
            Instance.StartCoroutine(SpawnMapWhenReady());
        }
        public static bool isLastLevel()
        {
            int numberInArray = 0;
            foreach (string level in Levels)
            {
                string cleanName = Instance.PrefabName.Replace(path, "").Replace(".prefab", "");
                if (level.Equals(cleanName))
                {
                    numberInArray++;
                    break;
                }
                numberInArray++;
            }
            if (numberInArray >= Levels.Length)
            {
                Log.LogInfo("Last Level!");
                return true;
            }
            return false;
        }
    }
}