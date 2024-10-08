﻿using BepInEx;
using HarmonyLib;
using Unity.Netcode;
using System.Reflection;
using UnityEngine;
using BepInEx.Configuration;
using System.Collections.Generic;
using System.IO;
using Unity.Netcode.Components;
using GameNetcodeStuff;

namespace RoombaMines
{
    [BepInPlugin(PluginInfo.PLUGIN_GUID, PluginInfo.PLUGIN_NAME, PluginInfo.PLUGIN_VERSION)]
    [BepInProcess("Lethal Company.exe")]
    public class Plugin : BaseUnityPlugin
    {
        public static AssetBundle ModAssets;

        // config options
        public static ConfigEntry<float> roombaMoveSpeed;
        public static ConfigEntry<float> roombaTurnSpeed;
        public static ConfigEntry<int> roombaUpdateTickLength;
        public static ConfigEntry<bool> roombaAllowRotateLeft;
        public static ConfigEntry<float> mineBecomesRoombaChance;
        public static List<string> roombaNames;
        public static string roombaNameFilePath = UnityEngine.Application.persistentDataPath + "/roomba_names.txt";

        private void Awake()
        {
            // Plugin startup logic
            Logger.LogInfo($"Plugin {PluginInfo.PLUGIN_GUID} is loaded!");
            Logger.LogInfo($"Embedded resources found:");
            foreach (string s in GetType().Assembly.GetManifestResourceNames())
            {
                Logger.LogInfo($"{s}");
            }

            // load config
            roombaMoveSpeed = Config.Bind("Behavior",
                                        "RoombaMoveSpeed",
                                        0.5f,
                                        "Speed of the Roomba when moving forward (measured in m/s)\nNote: Roomba behavior can only be affected by the host. These settings do nothing for the clients.");
            roombaTurnSpeed = Config.Bind("Behavior",
                                        "RoombaTurnSpeed",
                                        50f,
                                        "How quickly the Roomba will rotate when it's hit a wall (measured in degrees/s)");
            roombaUpdateTickLength = Config.Bind("Behavior",
                                                "RoombaUpdateTickLength",
                                                30,
                                                "Roombas check for obstructions at the beginning of every tick, this determines how long those ticks last (measured in 1/60ths of a second)");
            roombaAllowRotateLeft = Config.Bind("Behavior",
                                                "RoombaAllowRotateLeft",
                                                false,
                                                "If true, Roombas will turn left 50% of the time, otherwise they will always turn right");
            mineBecomesRoombaChance = Config.Bind("Behavior",
                                        "MineBecomesRoombaChance",
                                        1.0f,
                                        "The probability a mine will behave like a Roomba after it's spawned. A value of 1.0 will result in every mine becoming a Roomba, a value of 0.0 will effectively disable the mod, 0.5 will result in half normal mines, etc.");

            // load or create roomba name file
            if (!File.Exists(roombaNameFilePath))
            {
                StreamWriter writeStream = new StreamWriter(roombaNameFilePath);
                writeStream.Write("John\nGeorge\nPaul\nRingo\nHenry\nWilliam\nJoshua\nSam\nFred\nVinny\nRoss\nJoey");
                writeStream.Close();
            }
            roombaNames = new List<string>(File.ReadAllText(roombaNameFilePath).Split("\n"));

            // for UnityNetCodePatcher
            NetcodePatcher();

            // patch
            Harmony.CreateAndPatchAll(typeof(NetworkObjectManager));
        }

        private static void NetcodePatcher()
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

        public class NetworkObjectManager
        {
            public static GameObject roombaPrefab;
            public static int mineIndex = 0;

            // load Roomba prefab
            [HarmonyPatch(typeof(GameNetworkManager), "Start")]
            [HarmonyPostfix]
            public static void Init()
            {
                // allow adding prefabs at runtime
                NetworkManager.Singleton.NetworkConfig.ForceSamePrefabs = false;
            }

            // overwrite landmine prefab in current level
            [HarmonyPatch(typeof(RoundManager), "SpawnMapObjects")]
            [HarmonyPrefix]
            public static void MinePrefabPatch(RoundManager __instance)
            {
                if (roombaPrefab == null)
                {
                    // get a landmine prefab from the RoundManager
                    for (mineIndex = 0; mineIndex < __instance.currentLevel.spawnableMapObjects.Length; mineIndex++)
                    {
                        if (__instance.currentLevel.spawnableMapObjects[mineIndex].prefabToSpawn.GetComponentInChildren<Landmine>())
                        {
                            roombaPrefab = __instance.currentLevel.spawnableMapObjects[mineIndex].prefabToSpawn;
                            break;
                        }
                    }

                    // add relevant components to prefab
                    roombaPrefab.AddComponent<NetworkTransform>();
                    roombaPrefab.AddComponent<Roomba>();

                    // unregister old landmine prefab
                    NetworkManager.Singleton.RemoveNetworkPrefab(__instance.currentLevel.spawnableMapObjects[mineIndex].prefabToSpawn);

                    // register modified landmine as new network prefab
                    NetworkManager.Singleton.AddNetworkPrefab(roombaPrefab);
                }

                // replace landmine prefab in current level with new one
                __instance.currentLevel.spawnableMapObjects[mineIndex].prefabToSpawn = roombaPrefab;
            }
        }

        public class Roomba : NetworkBehaviour
        {
            public enum MovementState
            {
                Idle,
                Forward,
                RotateRight,
                RotateLeft
            }

            public MovementState state;
            public Landmine mine;
            public string scanName;

            private int _tick_timer = 0;
            private int _tick_length = roombaUpdateTickLength.Value;
            private float _fixed_tick_time = 0;
            private float _move_rate = roombaMoveSpeed.Value;
            private float _rotate_rate = roombaTurnSpeed.Value;
            private readonly float _scale = 0.55f;
            private readonly float _scale_vert = 0.2f;
            private int _mask = StartOfRound.Instance.allPlayersCollideWithMask;

            void Awake()
            {
                // check if mine is above ground (outside) or whether this mine should behave normally
                if (transform.position.y > -50 || Random.Range(0f, 1f) > mineBecomesRoombaChance.Value)
                {
                    // disable roomba behavior
                    enabled = false;
                    return;
                }

                // if the roomba is clipped in a wall, move away from it
                RaycastHit hit;
                if (Physics.Raycast(transform.position, transform.forward, out hit, _scale * 1.42f, _mask & ~(1 << 21))
                    || Physics.Raycast(transform.position, -transform.forward, out hit, _scale * 1.42f, _mask & ~(1 << 21))
                    || Physics.Raycast(transform.position, transform.right, out hit, _scale * 1.42f, _mask & ~(1 << 21))
                    || Physics.Raycast(transform.position, -transform.right, out hit, _scale * 1.42f, _mask & ~(1 << 21)))
                {
                    Vector3 new_pos = hit.point + hit.normal * _scale * 2;
                    transform.position = new_pos;
                }

            }

            void Start()
            {
                state = MovementState.RotateRight;
                UnityEngine.Debug.Log("Roomba at position: " + transform.position);

                _fixed_tick_time = Time.fixedDeltaTime * _tick_length;
            }

            public override void OnNetworkSpawn()
            {
                base.OnNetworkSpawn();

                mine = gameObject.GetComponentInChildren<Landmine>();

                // increase height of the mesh slightly
                mine.transform.localScale = new UnityEngine.Vector3(1, 1, 1.1f);

                // set roomba name if host
                if (NetworkManager.Singleton.IsHost || NetworkManager.Singleton.IsServer)
                {
                    SetNameServerRPC(roombaNames[UnityEngine.Random.Range(0, roombaNames.Count)]);
                }
            }

            [ServerRpc(RequireOwnership = false)]
            public void SetNameServerRPC(string newName)
            {
                if (!(NetworkManager.Singleton.IsHost || NetworkManager.Singleton.IsServer))
                {
                    return;
                }
                UnityEngine.Debug.Log("Host: Set Roomba name: " + newName);

                SetNameClientRPC(newName);
            }

            [ClientRpc]
            public void SetNameClientRPC(string newName)
            {
                UnityEngine.Debug.Log("Client: Set Roomba name: " + newName);

                // set scannode to show new name
                scanName = newName;
                mine.transform.parent.GetComponentInChildren<ScanNodeProperties>().headerText = scanName;
            }

            // tick for Roomba movement
            void Update()
            {
                if (!mine.enabled)
                {
                    Destroy(gameObject);
                    return;
                }

                // return if not host or mine is inactive
                if (!(NetworkManager.Singleton.IsHost || NetworkManager.Singleton.IsServer) || mine.hasExploded)
                {
                    return;
                }

                if (state == MovementState.Forward)
                {
                    transform.position += transform.forward * Time.deltaTime * _move_rate;
                }
                else if (state == MovementState.RotateRight)
                {
                    transform.Rotate(transform.up, Time.deltaTime * _rotate_rate);
                }
                else if (state == MovementState.RotateLeft)
                {
                    transform.Rotate(-transform.up, Time.deltaTime * _rotate_rate);
                }
            }

            // physics tick for Roomba
            void FixedUpdate()
            {
                // return if not host or mine is inactive
                if (!(NetworkManager.Singleton.IsHost || NetworkManager.Singleton.IsServer) || mine.hasExploded)
                {
                    return;
                }

                _tick_timer += 1;
                if (_tick_timer >= _tick_length)
                {
                    _tick_timer = 0;

                    // if mine spawns in the air somehow, put it on the nearest surface below it
                    if (!Physics.Raycast(transform.position, -transform.up, 0.1f, _mask, QueryTriggerInteraction.Ignore))
                    {
                        RaycastHit hit;
                        if (Physics.Raycast(transform.position, -transform.up, out hit, 5f, _mask, QueryTriggerInteraction.Ignore))
                        {
                            transform.position = hit.point;
                            UnityEngine.Debug.Log("Moving mine to Y coord " + transform.position.y);
                        }
                    }

                    bool forward_clear = !Physics.CheckBox(transform.position + transform.forward * (_scale + _fixed_tick_time * _move_rate / 2) + transform.up * 0.01f, new UnityEngine.Vector3(_scale, 0.005f, _fixed_tick_time * _move_rate / 2), transform.rotation, _mask, QueryTriggerInteraction.Ignore);
                    bool grounded = Physics.Raycast(transform.position + transform.forward * (_scale + _fixed_tick_time * _move_rate), -transform.up, 0.1f, _mask, QueryTriggerInteraction.Ignore);

                    if (forward_clear && grounded)
                    {
                        state = MovementState.Forward;
                    }
                    else if (state == MovementState.Forward)
                    {
                        if (!roombaAllowRotateLeft.Value || UnityEngine.Random.Range(0, 2) == 0)
                        {
                            state = MovementState.RotateRight;
                        }
                        else
                        {
                            state = MovementState.RotateLeft;
                        }
                    }
                }
            }
        }
    }
}
