using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace FpsBase
{
    public enum NetMode
    {
        Offline,
        Host,
        Client,
    }

    // One player as the session tracks it, local or remote.
    public class NetPlayer
    {
        public int Id;
        public bool IsLocal;
        public PlayerMotor Motor;
        public PlayerLook Look;
        public PlayerInventory Inventory;
        public PlayerHealth Health;
        // Counted by the host; clients take them, and every player's ping, from its scoreboard.
        public int Kills;
        public int Deaths;
        public int PingMs = -1;
        // Host: the last input tick simulated and command applied, echoed so the client can
        // reconcile and stop resending.
        public int LastInputTick = int.MinValue;
        public int LastCommandSeq;
        // Client: snapshot poses of a remote player.
        public readonly PoseBuffer Poses = new(16);
        public Vector3 ProxyVelocity;
        public bool ProxyGrounded;
        // Remote players: where the view sat and the head aimed in the last rendered frame, and in
        // this frame while a shot traces between the two.
        public bool WasShown;
        public Vector3 ShownPosition;
        public Quaternion ShownAim;
        public Vector3 FramePosition;
        public Quaternion FrameAim;
    }

    // LAN session over UDP. The host runs the whole simulation: its own player, remote players
    // from their streamed input, physics, damage and items, and sends every client a full
    // snapshot each tick. A client predicts its own movement and replays it on correction, and
    // renders everything else a few ticks behind the newest snapshot.
    [DefaultExecutionOrder(-200)]
    public partial class NetSession : MonoBehaviour
    {
        public const int HostPlayerId = 0;
        private const string AddressKey = "FpsBase.JoinAddress";
        private const string CommandLineHost = "-host";
        private const string CommandLineJoin = "-join";
        private const float PlayerSpacing = 2.5f;

        [SerializeField] private PlayerMotor localPlayer;
        // Instantiated for each remote player.
        [SerializeField] private GameObject playerPrefab;
        // Lets a client spawn an item it has no copy of, such as one a departed player left behind.
        [SerializeField] private InventoryItem[] itemPrefabs;
        [SerializeField] private int port = NetProtocol.DefaultPort;

        // Survives the scene reload that ends a session, so the menu can say why it ended.
        private static string endReason;

        private readonly Dictionary<int, NetPlayer> players = new();
        private readonly Dictionary<int, InventoryItem> items = new();
        private readonly Dictionary<InventoryItem, int> itemIds = new();
        private readonly NetWriter writer = new();
        private Rigidbody[] bodies;
        private Damageable[] targets;
        private Transform spawnRoot;
        private Vector3 spawnOrigin;
        private NetSocket socket;
        private NetMode mode;
        private string status;
        private double shownTime;
        private float lastPingTime;

        public static NetSession Instance { get; private set; }

        public static NetMode Mode => Instance != null ? Instance.mode : NetMode.Offline;

        public static bool IsClient => Mode == NetMode.Client;

        public static bool IsOnline => Mode != NetMode.Offline;

        public bool IsBusy => socket != null;

        public string Status => mode == NetMode.Host ? HostStatus() : status;

        public IEnumerable<NetPlayer> Players => players.Values;

        public string SavedAddress
        {
            get => PlayerPrefs.GetString(AddressKey, "");
            private set => PlayerPrefs.SetString(AddressKey, value);
        }

        // Ends the session and reloads the scene, which resets every replicated object.
        public void Leave()
        {
            EndSession(null);
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics()
        {
            Instance = null;
            endReason = null;
        }

        private void Awake()
        {
            Instance = this;
            status = endReason ?? "Offline";
            endReason = null;

            var root = new GameObject("Spawning");
            root.SetActive(false);
            spawnRoot = root.transform;
            spawnRoot.SetParent(transform, false);
            spawnOrigin = localPlayer.transform.position;

            // Both ends load the same scene, so hierarchy order gives scene objects matching ids.
            bodies = FindObjectsByType<Rigidbody>()
                .Where(body => body.GetComponentInParent<InventoryItem>() == null && body.GetComponentInParent<PlayerMotor>() == null)
                .OrderBy(body => HierarchyKey(body.transform), StringComparer.Ordinal)
                .ToArray();
            targets = FindObjectsByType<Damageable>()
                .Where(target => target is not PlayerHealth)
                .OrderBy(target => HierarchyKey(target.transform), StringComparer.Ordinal)
                .ToArray();
        }

        private void Start()
        {
            string[] args = Environment.GetCommandLineArgs();
            int join = Array.IndexOf(args, CommandLineJoin);
            if (Array.IndexOf(args, CommandLineHost) >= 0)
            {
                Host();
            }
            else if (join >= 0)
            {
                Join(join + 1 < args.Length && !args[join + 1].StartsWith("-") ? args[join + 1] : "");
            }
        }

        private void OnEnable()
        {
            InputPump.Pumped += Poll;
        }

        private void OnDisable()
        {
            InputPump.Pumped -= Poll;
        }

        private void OnDestroy()
        {
            CloseSocket();
            if (Instance == this)
            {
                Instance = null;
            }
        }

        // Runs after the frame's input pump and before its ticks, so ticks see the newest packets.
        private void Poll()
        {
            if (socket == null)
            {
                return;
            }
            if (mode == NetMode.Host)
            {
                PollHost();
            }
            else
            {
                PollClient();
            }
        }

        private void FixedUpdate()
        {
            if (mode == NetMode.Host)
            {
                SimulateRemotePlayers();
            }
        }

        // Poses remote entities before any other Update, so shots resolved in Update trace against
        // the frame about to render.
        private void Update()
        {
            if (mode == NetMode.Client)
            {
                AdvanceServerClock();
                InterpolateRemoteEntities();
            }
        }

        private void LateUpdate()
        {
            if (mode == NetMode.Host)
            {
                SendSnapshot();
            }
            else if (socket != null)
            {
                SendClientInput();
            }
            RecordShownPoses();
        }

        // Traces a local shot against remote players as the screen showed them at the shot's
        // moment, between the last rendered frame and this one: the moment its aim and origin come
        // from too. Each player returns to this frame's pose afterwards.
        public HitscanWeapon.ShotResult TraceAsShown(HitscanWeapon weapon, Vector3 origin, Vector3 direction, double time)
        {
            float blend = Simulation.FrameBlend(time, shownTime);
            foreach (NetPlayer player in players.Values)
            {
                if (!player.WasShown)
                {
                    continue;
                }
                Transform view = player.Motor.View;
                Transform aim = player.Look.CameraTransform;
                player.FramePosition = view.position;
                player.FrameAim = aim.rotation;
                view.position = Vector3.Lerp(player.ShownPosition, player.FramePosition, blend);
                aim.rotation = Quaternion.Slerp(player.ShownAim, player.FrameAim, blend);
            }

            HitscanWeapon.ShotResult result = weapon.TraceShot(origin, direction);

            foreach (NetPlayer player in players.Values)
            {
                if (player.WasShown)
                {
                    player.Motor.View.position = player.FramePosition;
                    player.Look.CameraTransform.rotation = player.FrameAim;
                }
            }
            return result;
        }

        // Every Update has posed the frame by now.
        private void RecordShownPoses()
        {
            foreach (NetPlayer player in players.Values)
            {
                if (!player.IsLocal)
                {
                    player.ShownPosition = player.Motor.View.position;
                    player.ShownAim = player.Look.CameraTransform.rotation;
                    player.WasShown = true;
                }
            }
            shownTime = Time.timeAsDouble;
        }

        private void EndSession(string reason)
        {
            endReason = reason;
            CloseSocket();
            mode = NetMode.Offline;
            SceneManager.LoadScene(SceneManager.GetActiveScene().buildIndex);
        }

        private void CloseSocket()
        {
            if (socket == null)
            {
                return;
            }

            writer.Begin(MessageType.Bye);
            if (mode == NetMode.Host)
            {
                foreach (Peer peer in peers)
                {
                    socket.Send(writer, peer.EndPoint);
                }
            }
            else if (hostEndPoint != null)
            {
                socket.Send(writer, hostEndPoint);
            }
            socket.Dispose();
            socket = null;
            EventFeed.Posted -= AddFeedEvent;
            upnp?.Dispose();
            upnp = null;
        }

        private bool OpenSocket(int localPort)
        {
            try
            {
                socket = new NetSocket(localPort);
                return true;
            }
            catch (SocketException exception)
            {
                status = exception.SocketErrorCode == SocketError.AddressAlreadyInUse
                    ? $"Port {localPort} Is Already in Use"
                    : $"Network Unavailable: {exception.SocketErrorCode}";
                return false;
            }
        }

        // Echoed back untouched, so the sender keeps no record of pings in flight.
        private static int ClockMilliseconds => (int)(Time.realtimeSinceStartupAsDouble * 1000.0);

        private void SendPing(IPEndPoint target)
        {
            writer.Begin(MessageType.Ping);
            writer.Write(ClockMilliseconds);
            socket.Send(writer, target);
        }

        private void ReplyToPing(NetReader ping, IPEndPoint sender)
        {
            int stamp = ping.ReadInt();
            writer.Begin(MessageType.Pong);
            writer.Write(stamp);
            socket.Send(writer, sender);
        }

        private static int ReadRoundTrip(NetReader pong)
        {
            return ClockMilliseconds - pong.ReadInt();
        }

        private NetPlayer RegisterPlayer(int id, PlayerMotor motor, bool isLocal)
        {
            var player = new NetPlayer
            {
                Id = id,
                IsLocal = isLocal,
                Motor = motor,
                Look = motor.GetComponent<PlayerLook>(),
                Inventory = motor.GetComponent<PlayerInventory>(),
                Health = motor.Health,
            };
            players[id] = player;
            player.Health.Died += _ => CountDeath(player);

            InventoryItem[] starting = player.Inventory.StartingItems;
            for (int i = 0; i < starting.Length && i < NetProtocol.ItemsPerPlayer; i++)
            {
                if (starting[i] != null)
                {
                    RegisterItem(id * NetProtocol.ItemsPerPlayer + i, starting[i]);
                }
            }
            return player;
        }

        // Died fires only where damage is applied, so only the host counts. Credits the killer the
        // same way the kill feed names one.
        private void CountDeath(NetPlayer victim)
        {
            victim.Deaths++;
            if (victim.Health.LastAttacker is PlayerHealth shooter && shooter != victim.Health)
            {
                foreach (NetPlayer player in players.Values)
                {
                    if (player.Health == shooter)
                    {
                        player.Kills++;
                        break;
                    }
                }
            }
            scoresChanged = true;
        }

        private void RegisterItem(int id, InventoryItem item)
        {
            items[id] = item;
            itemIds[item] = id;
        }

        // Instantiated under an inactive parent, so each component learns it is remote before its
        // first OnEnable and never touches this machine's input.
        private NetPlayer SpawnRemotePlayer(int id)
        {
            GameObject instance = Instantiate(playerPrefab, SpawnPosition(id), Quaternion.identity, spawnRoot);
            instance.name = $"Player {id}";
            var motor = instance.GetComponent<PlayerMotor>();
            motor.MarkRemote();
            instance.GetComponent<PlayerLook>().MarkRemote();
            instance.GetComponent<PlayerInventory>().MarkRemote();

            Camera head = instance.GetComponentInChildren<Camera>(true);
            head.enabled = false;
            head.tag = "Untagged";
            foreach (AudioListener listener in instance.GetComponentsInChildren<AudioListener>(true))
            {
                listener.enabled = false;
            }
            foreach (AudioSource source in instance.GetComponentsInChildren<AudioSource>(true))
            {
                source.spatialBlend = 1f;
            }
            instance.GetComponent<PlayerLook>().ShowModel();

            instance.transform.SetParent(null, true);
            NetPlayer player = RegisterPlayer(id, motor, false);
            // Replaced by the name from Hello on the host, and from the scoreboard on a client.
            player.Health.DisplayName = $"Player {id}";
            return player;
        }

        private void RemovePlayer(int id)
        {
            if (!players.TryGetValue(id, out NetPlayer player))
            {
                return;
            }

            players.Remove(id);
            Transform root = player.Motor.transform;
            foreach (int itemId in items.Where(entry => entry.Value == null || entry.Value.transform.IsChildOf(root)).Select(entry => entry.Key).ToList())
            {
                ForgetItem(itemId);
            }
            Destroy(root.gameObject);
        }

        private void ForgetItem(int id)
        {
            if (items.TryGetValue(id, out InventoryItem item) && item != null)
            {
                itemIds.Remove(item);
            }
            items.Remove(id);
            itemPoses.Remove(id);
        }

        private Vector3 SpawnPosition(int id)
        {
            int ring = (id + 1) / 2;
            float side = id % 2 == 1 ? 1f : -1f;
            return spawnOrigin + Vector3.right * (ring * side * PlayerSpacing);
        }

        private int ItemId(InventoryItem item)
        {
            return item != null && itemIds.TryGetValue(item, out int id) ? id : -1;
        }

        private int ItemKind(InventoryItem item)
        {
            for (int i = 0; i < itemPrefabs.Length; i++)
            {
                if (itemPrefabs[i] != null && itemPrefabs[i].DisplayName == item.DisplayName)
                {
                    return i;
                }
            }
            return byte.MaxValue;
        }

        private NetPlayer FindPlayer(PlayerLook look)
        {
            foreach (NetPlayer player in players.Values)
            {
                if (player.Look == look)
                {
                    return player;
                }
            }
            return null;
        }

        private static string HierarchyKey(Transform target)
        {
            var key = new StringBuilder();
            for (Transform node = target; node != null; node = node.parent)
            {
                key.Insert(0, node.GetSiblingIndex().ToString("D5") + "/");
            }
            return key.ToString();
        }
    }
}
