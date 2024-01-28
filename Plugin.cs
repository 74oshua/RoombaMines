using BepInEx;
using HarmonyLib;
using GameNetcodeStuff;
using Unity.Netcode;
using System.Reflection;
using System.Numerics;
using UnityEngine;
using UnityEngine.Networking;
using System.Linq;
using BepInEx.Configuration;
using System.Collections.Generic;
using System;
using System.IO;

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
        public static List<string> roombaNames;
        public static string roombaNameFilePath = Application.persistentDataPath + "/roomba_names.txt";

        private void Awake()
        {
            // Plugin startup logic
            Logger.LogInfo($"Plugin {PluginInfo.PLUGIN_GUID} is loaded!");
            Logger.LogInfo($"Embedded resources found:");
            foreach (string s in GetType().Assembly.GetManifestResourceNames())
            {
                Logger.LogInfo($"{s}");
            }

            // load assets from asset bundle
            ModAssets = AssetBundle.LoadFromStream(Assembly.GetExecutingAssembly().GetManifestResourceStream("RoombaMines.ModAssets"));

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
                                                true,
                                                "If true, Roombas will turn left 50% of the time, otherwise they will always turn right");

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
            static GameObject networkPrefab;

            // load Roomba prefab
            [HarmonyPatch(typeof(GameNetworkManager), "Start")]
            [HarmonyPostfix]
            public static void Init()
            {
                if (networkPrefab != null)
                    return;

                networkPrefab = (GameObject)ModAssets.LoadAsset("NetworkManager");
                networkPrefab.AddComponent<Roomba>();

                NetworkManager.Singleton.AddNetworkPrefab(networkPrefab);
            }

            // spawn Roomba object as parent of landmine
            [HarmonyPatch(typeof(Landmine), "Start")]
            [HarmonyPostfix]
            static void SpawnRoomba(Landmine __instance)
            {
                if (NetworkManager.Singleton.IsHost || NetworkManager.Singleton.IsServer)
                {

                    // spawn Roomba
                    var roomba = Instantiate(networkPrefab, __instance.gameObject.transform.parent.position, __instance.gameObject.transform.parent.rotation);
                    roomba.GetComponent<Roomba>().mine = __instance;
                    // give Roomba a random name
                    roomba.GetComponent<Roomba>().scanName = roombaNames[UnityEngine.Random.Range(0, roombaNames.Count)];
                    roomba.GetComponent<NetworkObject>().Spawn();
                    __instance.transform.parent.GetComponent<NetworkObject>().TrySetParent(roomba);
                }
            }
        }

        public class Roomba : NetworkBehaviour
        {
            public enum MovementState
            {
                Forward,
                RotateRight,
                RotateLeft
            }

            public MovementState state;
            public Landmine mine;
            public string scanName = "Roomba";

            private int _tick_timer = 0;
            private int _tick_length = roombaUpdateTickLength.Value;
            private float _fixed_tick_time = 0;
            private float _move_rate = roombaMoveSpeed.Value;
            private float _rotate_rate = roombaTurnSpeed.Value;
            private readonly float _scale = 0.55f;
            private int _mask = StartOfRound.Instance.allPlayersCollideWithMask;

            void Start()
            {
                state = MovementState.RotateRight;
                UnityEngine.Debug.Log("Spawned Roomba");

                _fixed_tick_time = Time.fixedDeltaTime * _tick_length;
            }

            public override void OnNetworkSpawn()
            {
                base.OnNetworkSpawn();

                // set scannode to show new name
                mine.transform.parent.GetComponentInChildren<ScanNodeProperties>().headerText = scanName;

                // increase height of the mesh slightly
                mine.transform.localScale = new UnityEngine.Vector3(1, 1, 1.1f);
            }

            // tick for Roomba movement
            void Update()
            {
                // return if not host
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
                // return if not host
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
                        Physics.Raycast(transform.position, -transform.up, out hit, 50f, _mask, QueryTriggerInteraction.Ignore);
                        transform.position = hit.point;
                    }

                    bool forward_clear = !Physics.CheckBox(transform.position + transform.forward * (_scale + _fixed_tick_time * _move_rate / 2), new UnityEngine.Vector3(_scale, 0.01f, _fixed_tick_time * _move_rate / 2), transform.rotation, _mask, QueryTriggerInteraction.Ignore);
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
