using BepInEx;
using DunGen;
using GameNetcodeStuff;
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
        private const string modVersion = "0.1.0";

        private readonly Harmony harmony = new Harmony(modGUID);

        private static int fakeChance;
        private static bool changeOnUse;
        private static int changeOnUseChance;

        private static GameObject NetworkerPrefab;

        private static Random random;

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
            [HarmonyPatch("SetExitIDs")]
            [HarmonyPostfix]
            [HarmonyPriority(Priority.LowerThanNormal)]
            static void SetExitIDsPatch(ref RoundManager __instance)
            {
                Networker.Instance.FullRelocation();
                random = new Random(StartOfRound.Instance.randomMapSeed + 172); // Random seed offset for random number generator
            }

            [HarmonyPatch("Start")]
            [HarmonyPostfix]
            static void StartPatch(ref RoundManager __instance)
            {
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
            [HarmonyPostfix]
            static void TeleportPlayerPatch(ref EntranceTeleport __instance)
            {
                if (__instance.entranceId == 1 && __instance.isEntranceToBuilding)
                {
                    if (Networker.changeOnUse.Value)
                    {
                        if (random == null) return;
                        var rng = random.NextDouble();
                        if (rng < ((float)Networker.changeOnUseChance.Value / 100f))
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
            if (!(newTransform.gameObject.name == "MimicDoor(Clone)")) { return; }
            Transform childTransform = obj.GetChild(0);
            // Local position based on defaults: 2.9 -0.435 0.002 with forward of -0.9999 0 -0.0167
            Vector3 localPosition = new Vector3(0f, 0.065f, 0.002f);
            localPosition += newTransform.forward * -1.5f;
            childTransform.position = newTransform.position + localPosition;
            childTransform.rotation = newTransform.rotation;
        }

        private static Transform PickDoor(Transform root, Transform original, int seed)
        {
            if (random == null) return original;
            var rng = random.NextDouble();
            if (rng < ((float)fakeChance / 100f)) // Pick random double and check if % chance allows for a fake door tp
            {
                List<Transform> mimics = FindMimics(root, new List<Transform>());
                if (mimics.Count == 0) return original; // No mimics to swap to
                rng = random.NextDouble();
                return mimics.ToArray()[(int)Mathf.Floor((float)rng * mimics.Count)];
            }
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
                Scene levelScene = SceneManager.GetSceneByName(RoundManager.Instance.currentLevel.sceneName);
                GameObject[] objects = levelScene.GetRootGameObjects();
                foreach (GameObject obj in objects)
                {
                    if (obj.name == "EntranceTeleportB(Clone)")
                    {
                        Relocate(objects[0].transform, obj.transform);
                        break;
                    }
                }
            }

            [ServerRpc(RequireOwnership = false)]
            public void FullRelocationServerRpc()
            {
                FullRelocationClientRpc();
            }
        }
    }
}
