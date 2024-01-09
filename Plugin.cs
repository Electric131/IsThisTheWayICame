using BepInEx;
using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Reflection;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.SceneManagement;
using Random = System.Random;

namespace IsThisTheWayICame
{
    [BepInPlugin(modGUID, modName, modVersion)]
    public class Plugin : BaseUnityPlugin
    {
        private const string modGUID = "Electric.IsThisTheWayICame";
        private const string modName = "IsThisTheWayICame";
        private const string modVersion = "1.1.0";

        private readonly Harmony harmony = new Harmony(modGUID);

        private static int fakeChance;
        private static bool changeOnUse;
        private static int changeOnUseChance;

        private static GameObject NetworkerPrefab;
        
        private static Random? random;
        private static List<Transform> realExits = new List<Transform>();
        private static List<Tuple<Vector3, Quaternion>> realExitTPData = new List<Tuple<Vector3, Quaternion>>();
        private static Tuple<Vector3, Quaternion>? tempLoc;

        private void Awake()
        {
            AssetBundle data = AssetBundle.LoadFromMemory(Properties.Resources.networker);
            NetworkerPrefab = data.LoadAsset<GameObject>("Assets/Networker.prefab");
            NetworkerPrefab.AddComponent<Networker>();

            fakeChance = Mathf.Clamp(Config.Bind("General", "Fake Chance", 40, "Chance that the fire exit will teleport you to a mimic. (0-100)").Value, 0, 100);
            changeOnUse = Config.Bind("General", "Change on Use", true, "If true, the destination will change whenever a player uses the exterior door").Value;
            changeOnUseChance = Mathf.Clamp(Config.Bind("General", "Change on Use Chance", 25, "Chance that the destination will change when used. (0-100)").Value, 0, 100);

            NetcodeWeaver();

            harmony.PatchAll();
            Logger.LogInfo($"{modName} loaded!");
        }

        private static void NetcodeWeaver()
        {
            var types = Assembly.GetExecutingAssembly().GetTypes();
            foreach (var type in types)
            {
                var methods = type.GetMethods(BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);
                foreach (var method in methods)
                {
                    var attributes = method.GetCustomAttributes(typeof(RuntimeInitializeOnLoadMethodAttribute), false);
                    if (attributes.Length > 0)
                    {
                        method.Invoke(null, null);
                    }
                }
            }
        }

        [HarmonyPatch(typeof(RoundManager))]
        internal class RoundManagerPatch
        {
            [HarmonyPatch("Start")]
            [HarmonyPostfix]
            static void StartPatch(ref RoundManager __instance)
            {
                random = null; // Ensure random is purged
                if (__instance.IsServer && Networker.Instance == null)
                {
                    GameObject networker = Instantiate<GameObject>(NetworkerPrefab);
                    networker.GetComponent<NetworkObject>().Spawn(true);

                    Networker.fakeChance.Value = fakeChance;
                    Networker.changeOnUse.Value = changeOnUse;
                    Networker.changeOnUseChance.Value = changeOnUseChance;
                }
            }
        }

        [HarmonyPatch(typeof(EntranceTeleport))]
        internal class EntranceTeleportPatch
        {
            [HarmonyPatch("TeleportPlayer")]
            [HarmonyPrefix] // Make this prefix so that location changes before teleport
            static void TeleportPlayerPatch(ref EntranceTeleport __instance)
            {
                if (__instance.entranceId >= 1 && __instance.isEntranceToBuilding)
                {
                    if (random == null)
                    {
                        Networker.Instance.FullRelocation();
                        return;
                    }
                    if (Networker.changeOnUse.Value)
                    {
                        var rng = random.NextDouble();
                        if (rng < (Networker.changeOnUseChance.Value / 100f))
                        {
                            Networker.Instance.FullRelocation();
                        }
                    }
                }
            }
        }

        [HarmonyPatch(typeof(GameNetworkManager))]
        internal class GameNetworkManagerPatch
        {
            [HarmonyPatch("Start")]
            [HarmonyPostfix]
            static void StartPatch()
            {
                GameNetworkManager.Instance.GetComponent<NetworkManager>().AddNetworkPrefab(NetworkerPrefab);
            }
        }

        private static void Relocate(Transform root, Transform obj)
        {
            Transform newTransform = PickDoor(root, obj, StartOfRound.Instance.randomMapSeed);
            Transform childTransform = obj.GetChild(0);
            GameObject go = new GameObject("TemporaryAudioSource");
            TemporaryAudioSource audio = go.AddComponent<TemporaryAudioSource>();
            if (!(newTransform.gameObject.name == "MimicDoor(Clone)")) {
                if (tempLoc == null)
                {
                    Debug.LogError("Could not find real exit. Report to developer!");
                } else
                {
                    childTransform.rotation = tempLoc.Item2;
                    childTransform.position = tempLoc.Item1;
                    go.transform.position = childTransform.position;
                    obj.GetComponent<EntranceTeleport>().entrancePointAudio = audio.audioSource;
                }
                return;
            }
            // Local position based on defaults: 2.9 -0.435 0.002 with forward of -0.9999 0 -0.0167
            Vector3 localPosition = new Vector3(0f, 0.065f, 0.002f);
            localPosition += newTransform.forward * -1.5f;
            childTransform.position = newTransform.position + localPosition;
            childTransform.rotation = newTransform.rotation;
            go.transform.position = childTransform.position;
            obj.GetComponent<EntranceTeleport>().entrancePointAudio = audio.audioSource;
        }

        private static Transform PickDoor(Transform root, Transform original, int seed)
        {
            if (random == null) return original;
            var rng = random.NextDouble();
            if (rng < (fakeChance / 100f)) // Pick random double and check if % chance allows for a fake door tp
            {
                List<Transform> mimics = FindMimics(root, new List<Transform>());
                if (mimics.Count == 0)
                { // No mimics to swap to, so: Pick a random real door
                    rng = random.NextDouble();
                    tempLoc = realExitTPData.ToArray()[(int)Mathf.Floor((float)rng * realExitTPData.Count)];
                    return original;
                }
                rng = random.NextDouble();
                return mimics.ToArray()[(int)Mathf.Floor((float)rng * mimics.Count)];
            }
            // Pick a random real door
            rng = random.NextDouble();
            tempLoc = realExitTPData.ToArray()[(int)Mathf.Floor((float)rng * realExitTPData.Count)];
            return original;
        }

        // Deep search for mimics
        private static List<Transform> FindMimics(Transform findFrom, List<Transform> current)
        {
            if (findFrom.gameObject.name == "MimicDoor(Clone)") // Find name of mimic door
            {
                current.Add(findFrom);
            }
            foreach (Transform child in findFrom)
            {
                FindMimics(child, current);
            }
            return current;
        }

        public class Networker : NetworkBehaviour
        {
            public static Networker Instance;

            public static NetworkVariable<int> fakeChance = new NetworkVariable<int>();
            public static NetworkVariable<bool> changeOnUse = new NetworkVariable<bool>();
            public static NetworkVariable<int> changeOnUseChance = new NetworkVariable<int>();

            private void Awake()
            {
                Instance = this;
            }

            public void FullRelocation()
            {
                if (base.IsOwner)
                {
                    FullRelocationClientRpc();
                }
                else
                {
                    FullRelocationServerRpc();
                }
            }

            [ClientRpc]
            public void FullRelocationClientRpc()
            {
                bool first = false;
                if (random == null)
                {
                    first = true;
                    random = new Random(StartOfRound.Instance.randomMapSeed + 172); // Random seed offset for random number generator
                }
                Scene teleporterScene = SceneManager.GetSceneByName("SampleSceneRelay");
                Scene levelScene = SceneManager.GetSceneByName(RoundManager.Instance.currentLevel.sceneName);
                if (GameNetworkManager.Instance.isHostingGame) { teleporterScene = levelScene; }
                GameObject[] objects = teleporterScene.GetRootGameObjects();
                List<GameObject> exits = new List<GameObject>();
                foreach (GameObject obj in objects)
                {
                    if (obj.name.StartsWith("EntranceTeleport") && obj.name != "EntranceTeleportA(Clone)")
                    {
                        if (first)
                        {
                            realExits.Add(obj.transform);
                            realExitTPData.Add(new Tuple<Vector3, Quaternion>(obj.transform.GetChild(0).position, obj.transform.GetChild(0).rotation));
                        }
                        exits.Add(obj);
                    }
                }
                foreach (GameObject obj in exits)
                {
                    Relocate(levelScene.GetRootGameObjects()[0].transform, obj.transform);
                }
            }

            [ServerRpc(RequireOwnership = false)]
            public void FullRelocationServerRpc()
            {
                FullRelocationClientRpc();
            }
        }

        private class TemporaryAudioSource : MonoBehaviour
        {
            public AudioSource audioSource;
            public float deletionTime = 0f;

            public TemporaryAudioSource()
            {
                audioSource = gameObject.AddComponent<AudioSource>();
            }

            public void Update()
            {
                deletionTime += Time.deltaTime;
                if (deletionTime > 5f) // Destroy after 5s
                {
                    Destroy(transform.gameObject); // Destroy parent gameobject
                }
            }
        }
    }
}
