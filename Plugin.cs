using BepInEx;
using HarmonyLib;
using GameNetcodeStuff;
using Unity.Netcode;
using System.Reflection;
using System.Numerics;
using UnityEngine;
using UnityEngine.Networking;
using System.Linq;

namespace RoombaMines
{
    [BepInPlugin(PluginInfo.PLUGIN_GUID, PluginInfo.PLUGIN_NAME, PluginInfo.PLUGIN_VERSION)]
    [BepInProcess("Lethal Company.exe")]
    public class Plugin : BaseUnityPlugin
    {
        public static AssetBundle ModAssets;

        private void Awake()
        {
            // Plugin startup logic
            Logger.LogInfo($"Plugin {PluginInfo.PLUGIN_GUID} is loaded!");
            Logger.LogInfo($"Embedded resources found:");
            foreach (string s in GetType().Assembly.GetManifestResourceNames())
            {
                Logger.LogInfo($"{s}");
            }

            ModAssets = AssetBundle.LoadFromStream(Assembly.GetExecutingAssembly().GetManifestResourceStream("RoombaMines.ModAssets"));

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

            // load network prefab
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

            // spawn roomba object as child of landmine
            [HarmonyPatch(typeof(Landmine), "Start")]
            [HarmonyPostfix]
            static void SpawnRoomba(Landmine __instance)
            {
                if (NetworkManager.Singleton.IsHost || NetworkManager.Singleton.IsServer)
                {
                    var roomba = Instantiate(networkPrefab, __instance.gameObject.transform.parent.position, __instance.gameObject.transform.parent.rotation);
                    roomba.GetComponent<Roomba>().mine = __instance;
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
                Rotate,
                Stop
            }

            public MovementState state;
            public Landmine mine;

            private int _tick_timer = 0;
            private readonly int _tick_length = 30;
            private readonly float _move_rate = 0.5f;
            private readonly float _rotate_rate = 20;
            private readonly float _scale = 0.5f;

            void Start()
            {
                state = MovementState.Rotate;
                UnityEngine.Debug.Log("Spawned Roomba");
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
                else if (state == MovementState.Rotate)
                {
                    transform.Rotate(transform.up, Time.deltaTime * _rotate_rate);
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

                    bool left_clear = !Physics.Raycast(transform.position - transform.right * _scale, transform.forward, 1);
                    bool right_clear = !Physics.Raycast(transform.position + transform.right * _scale, transform.forward, 1);
                    bool left_grounded = Physics.Raycast(transform.position + transform.forward * _scale - transform.right * _scale, -transform.up, 1);
                    bool right_grounded = Physics.Raycast(transform.position + transform.forward * _scale + transform.right * _scale, -transform.up, 1);

                    if (right_clear && left_clear && left_grounded && right_grounded)
                    {
                        state = MovementState.Forward;
                    }
                    else
                    {
                        state = MovementState.Rotate;
                    }
                }
            }
        }
    }
}
