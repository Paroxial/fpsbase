using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using UnityEngine;

namespace FpsBase
{
    public partial class NetSession
    {
        private const float HelloInterval = 0.25f;
        private const float ProbeInterval = 0.5f;
        private const float KeepAliveInterval = 0.1f;
        private const int MaxPredictions = 128;
        private const int MaxCommandsPerPacket = 32;
        // The server clock estimate jumps instead of easing when it is off by more than this.
        private const double ClockSnapTicks = 6.0;
        private const double ClockCorrection = 0.1;
        // Predicted and authoritative states this close count as agreeing.
        private const float PositionTolerance = 0.02f;
        private const float VelocityTolerance = 0.1f;

        private struct Prediction
        {
            public PlayerInput Input;
            public MotorState State;
        }

        private readonly List<Prediction> predictions = new();
        private readonly List<NetCommand> pendingCommands = new();
        private readonly Dictionary<int, PoseBuffer> itemPoses = new();
        private readonly HashSet<int> seenIds = new();
        private readonly List<int> unseenIds = new();
        private readonly InventoryItem[] slotItems = new InventoryItem[PlayerInventory.SlotCount];
        private PoseBuffer[] bodyPoses;
        private IPEndPoint hostEndPoint;
        private NetPlayer self;
        private int nonce;
        private bool connecting;
        private float lastHeardTime;
        private float lastHandshakeTime;
        private float lastSendTime;
        private int lastSentTick = int.MinValue;
        private int lastAppliedTick = int.MinValue;
        private int nextCommandSeq = 1;
        private int lastEventSeq;
        private double serverTick;

        // Corrections since joining: how often prediction disagreed with the host.
        public int Corrections { get; private set; }

        // The newest round trip to the host in milliseconds, -1 before the first reply.
        public int PingMs { get; private set; } = -1;

        // The server tick remote entities are rendered at, between the snapshots around it.
        private float RenderTick => (float)(serverTick - NetProtocol.InterpolationDelayTicks);

        // A blank address searches the LAN for a host.
        public void Join(string address)
        {
            if (socket != null)
            {
                return;
            }

            address = address.Trim();
            hostEndPoint = null;
            if (address.Length > 0 && !TryParseEndPoint(address, out hostEndPoint))
            {
                status = $"Found No Address for {address}";
                return;
            }
            if (!OpenSocket(0))
            {
                return;
            }

            SavedAddress = address;
            nonce = UnityEngine.Random.Range(1, int.MaxValue);
            connecting = true;
            lastHeardTime = Time.unscaledTime;
            lastHandshakeTime = float.NegativeInfinity;
            Time.timeScale = 1f;
            // A session must keep ticking when its window loses focus, or everyone else stalls.
            Application.runInBackground = true;
            status = hostEndPoint == null ? "Searching the LAN for a Host" : $"Connecting to {hostEndPoint}";
        }

        public void SendFire(Vector3 origin, Vector3 direction, HitscanWeapon.ShotResult result)
        {
            var command = new NetCommand
            {
                Type = CommandType.Fire,
                Origin = origin,
                Direction = direction,
                Hit = result.Hit,
                Point = result.Point,
                Normal = result.Normal,
                Damage = result.Damage,
            };
            if (result.Hit)
            {
                (command.Entity, command.Value) = DescribeHit(result.Collider);
            }
            QueueCommand(command);
        }

        // Names a struck collider by the id both ends share for its owner.
        private (NetEntity Entity, int Id) DescribeHit(Collider collider)
        {
            Damageable damageable = collider.GetComponentInParent<Damageable>();
            if (damageable is PlayerHealth)
            {
                foreach (NetPlayer player in players.Values)
                {
                    if (player.Health == damageable)
                    {
                        return (NetEntity.Player, player.Id);
                    }
                }
            }
            else if (damageable != null)
            {
                int target = Array.IndexOf(targets, damageable);
                if (target >= 0)
                {
                    return (NetEntity.Target, target);
                }
            }

            Rigidbody body = collider.attachedRigidbody;
            if (body != null)
            {
                int item = body.TryGetComponent(out InventoryItem struck) ? ItemId(struck) : -1;
                if (item >= 0)
                {
                    return (NetEntity.Item, item);
                }
                int index = Array.IndexOf(bodies, body);
                if (index >= 0)
                {
                    return (NetEntity.Body, index);
                }
            }
            return (NetEntity.None, 0);
        }

        public void SendSelect(int slot)
        {
            QueueCommand(new NetCommand { Type = CommandType.Select, Value = slot });
        }

        // Picks up the hovered item, or drops the selected one when nothing is in reach.
        public void SendInteract(InventoryItem hovered)
        {
            int id = ItemId(hovered);
            QueueCommand(id >= 0 ? new NetCommand { Type = CommandType.PickUp, Value = id } : new NetCommand { Type = CommandType.Drop });
        }

        private void QueueCommand(NetCommand command)
        {
            command.Seq = nextCommandSeq++;
            pendingCommands.Add(command);
        }

        private void PollClient()
        {
            while (socket.TryReceive(out NetReader packet, out MessageType type, out IPEndPoint sender))
            {
                if (type == MessageType.ProbeReply)
                {
                    if (connecting && hostEndPoint == null)
                    {
                        hostEndPoint = sender;
                        status = $"Connecting to {hostEndPoint}";
                    }
                    continue;
                }
                if (hostEndPoint == null || !sender.Equals(hostEndPoint))
                {
                    continue;
                }

                lastHeardTime = Time.unscaledTime;
                try
                {
                    switch (type)
                    {
                        case MessageType.Welcome:
                            if (connecting && packet.ReadInt() == nonce)
                            {
                                ReceiveWelcome(packet.ReadInt(), packet.ReadInt(), packet.ReadInt());
                            }
                            break;
                        case MessageType.Snapshot:
                            if (!connecting)
                            {
                                ApplySnapshot(packet);
                            }
                            break;
                        case MessageType.Bye:
                            EndSession("Host Ended the Session");
                            return;
                        case MessageType.Ping:
                            ReplyToPing(packet, sender);
                            break;
                        case MessageType.Pong:
                            PingMs = ReadRoundTrip(packet);
                            break;
                        case MessageType.Scores:
                            if (!connecting)
                            {
                                ReceiveScores(packet);
                            }
                            break;
                    }
                }
                catch (FormatException)
                {
                    // A truncated packet: whatever it applied before ending still stands.
                }
            }

            if (Time.unscaledTime - lastHeardTime > NetProtocol.TimeoutSeconds)
            {
                if (connecting)
                {
                    CloseSocket();
                    connecting = false;
                    status = hostEndPoint == null ? "Found No Host on the LAN" : $"No Answer from {hostEndPoint}";
                }
                else
                {
                    EndSession("Lost Connection to Host");
                }
                return;
            }

            if (connecting && Time.unscaledTime - lastHandshakeTime >= (hostEndPoint == null ? ProbeInterval : HelloInterval))
            {
                lastHandshakeTime = Time.unscaledTime;
                if (hostEndPoint == null)
                {
                    writer.Begin(MessageType.Probe);
                    socket.Send(writer, new IPEndPoint(IPAddress.Broadcast, port));
                }
                else
                {
                    writer.Begin(MessageType.Hello);
                    writer.Write(nonce);
                    writer.Write(PlayerName.Value);
                    socket.Send(writer, hostEndPoint);
                }
            }

            if (mode == NetMode.Client && Time.unscaledTime - lastPingTime >= NetProtocol.PingInterval)
            {
                lastPingTime = Time.unscaledTime;
                SendPing(hostEndPoint);
            }
        }

        private void ReceiveWelcome(int id, int tick, int eventSeq)
        {
            connecting = false;
            mode = NetMode.Client;
            serverTick = tick;
            lastEventSeq = eventSeq;
            status = $"Connected to {hostEndPoint}";

            Vector3 spawn = SpawnPosition(id);
            localPlayer.Teleport(spawn);
            localPlayer.Health.SetSpawnPosition(spawn);
            self = RegisterPlayer(id, localPlayer, true);
            localPlayer.Ticked += RecordPrediction;

            bodyPoses = new PoseBuffer[bodies.Length];
            for (int i = 0; i < bodies.Length; i++)
            {
                bodies[i].isKinematic = true;
                bodies[i].interpolation = RigidbodyInterpolation.None;
                bodyPoses[i] = new PoseBuffer(16);
            }
        }

        private void RecordPrediction(PlayerInput input)
        {
            if (predictions.Count == MaxPredictions)
            {
                predictions.RemoveAt(0);
            }
            predictions.Add(new Prediction { Input = input, State = localPlayer.State });
        }

        private void AdvanceServerClock()
        {
            serverTick += Time.deltaTime * Simulation.TickRate;
        }

        // Sends every frame that ran ticks, repeating the newest unacknowledged inputs and every
        // unacknowledged command; idle frames send a keepalive now and then.
        private void SendClientInput()
        {
            if (mode != NetMode.Client)
            {
                return;
            }

            int newest = predictions.Count > 0 ? predictions[predictions.Count - 1].Input.Tick : lastSentTick;
            if (newest == lastSentTick && pendingCommands.Count == 0 && Time.unscaledTime - lastSendTime < KeepAliveInterval)
            {
                return;
            }

            writer.Begin(MessageType.Input);
            int first = Mathf.Max(0, predictions.Count - NetProtocol.InputRedundancy);
            writer.Write((byte)(predictions.Count - first));
            for (int i = first; i < predictions.Count; i++)
            {
                predictions[i].Input.Write(writer);
            }

            int commands = Mathf.Min(pendingCommands.Count, MaxCommandsPerPacket);
            writer.Write((byte)commands);
            for (int i = 0; i < commands; i++)
            {
                pendingCommands[i].Write(writer);
            }

            socket.Send(writer, hostEndPoint);
            lastSentTick = newest;
            lastSendTime = Time.unscaledTime;
        }

        private void ApplySnapshot(NetReader packet)
        {
            int tick = packet.ReadInt();
            if (tick <= lastAppliedTick)
            {
                return;
            }
            lastAppliedTick = tick;
            serverTick = Math.Abs(tick - serverTick) > ClockSnapTicks ? tick : serverTick + (tick - serverTick) * ClockCorrection;

            ReadPlayersAndItems(packet, tick);

            int bodyCount = packet.ReadInt();
            for (int i = 0; i < bodyCount; i++)
            {
                Vector3 position = packet.ReadVector3();
                Quaternion rotation = packet.ReadQuaternion();
                if (i < bodyPoses.Length)
                {
                    bodyPoses[i].Add(tick, position, rotation);
                }
            }

            int targetCount = packet.ReadInt();
            for (int i = 0; i < targetCount; i++)
            {
                float health = packet.ReadFloat();
                if (i < targets.Length)
                {
                    targets[i].SetHealth(health);
                }
            }

            int eventCount = packet.ReadByte();
            for (int i = 0; i < eventCount; i++)
            {
                NetEvent netEvent = NetEvent.Read(packet);
                if (netEvent.Seq > lastEventSeq)
                {
                    lastEventSeq = netEvent.Seq;
                    PlayEvent(netEvent);
                }
            }
        }

        private struct ItemRecord
        {
            public int Id;
            public int Kind;
            public bool Dropped;
            public Vector3 Position;
            public Quaternion Rotation;
        }

        private struct PlayerRecord
        {
            public int Id;
            public MotorState State;
            public float Yaw;
            public float Pitch;
            public float Health;
            public int LastInputTick;
            public int LastCommandSeq;
            public int Selected;
            public int[] Slots;
        }

        private readonly List<ItemRecord> itemRecords = new();
        private readonly List<PlayerRecord> playerRecords = new();

        // Reads both lists before applying either: new players spawn first, so their starting
        // items take their ids before a missing item would be spawned from a prefab.
        private void ReadPlayersAndItems(NetReader packet, int tick)
        {
            itemRecords.Clear();
            int itemCount = packet.ReadInt();
            for (int i = 0; i < itemCount; i++)
            {
                var record = new ItemRecord { Id = packet.ReadInt(), Kind = packet.ReadByte(), Dropped = packet.ReadBool() };
                if (record.Dropped)
                {
                    record.Position = packet.ReadVector3();
                    record.Rotation = packet.ReadQuaternion();
                }
                itemRecords.Add(record);
            }

            int playerCount = packet.ReadByte();
            while (playerRecords.Count < playerCount)
            {
                playerRecords.Add(new PlayerRecord { Slots = new int[PlayerInventory.SlotCount] });
            }
            for (int i = 0; i < playerCount; i++)
            {
                PlayerRecord record = playerRecords[i];
                record.Id = packet.ReadInt();
                record.State = MotorState.Read(packet);
                record.Yaw = packet.ReadFloat();
                record.Pitch = packet.ReadFloat();
                record.Health = packet.ReadFloat();
                record.LastInputTick = packet.ReadInt();
                record.LastCommandSeq = packet.ReadInt();
                record.Selected = packet.ReadByte();
                for (int slot = 0; slot < record.Slots.Length; slot++)
                {
                    record.Slots[slot] = packet.ReadInt();
                }
                playerRecords[i] = record;
            }

            seenIds.Clear();
            for (int i = 0; i < playerCount; i++)
            {
                int id = playerRecords[i].Id;
                seenIds.Add(id);
                if (!players.ContainsKey(id))
                {
                    SpawnRemotePlayer(id);
                }
            }
            unseenIds.Clear();
            unseenIds.AddRange(players.Keys.Where(id => !seenIds.Contains(id)));
            foreach (int id in unseenIds)
            {
                RemovePlayer(id);
            }

            ApplyItems(tick);
            for (int i = 0; i < playerCount; i++)
            {
                ApplyPlayer(playerRecords[i], tick);
            }
        }

        private void ApplyItems(int tick)
        {
            seenIds.Clear();
            foreach (ItemRecord record in itemRecords)
            {
                seenIds.Add(record.Id);
                if (!items.TryGetValue(record.Id, out InventoryItem item) || item == null)
                {
                    if (record.Kind >= itemPrefabs.Length)
                    {
                        continue;
                    }
                    item = Instantiate(itemPrefabs[record.Kind]);
                    RegisterItem(record.Id, item);
                }

                if (!record.Dropped)
                {
                    itemPoses.Remove(record.Id);
                    continue;
                }
                if (!item.IsDropped)
                {
                    item.Drop(record.Position, record.Rotation, Vector3.zero);
                }
                if (!itemPoses.TryGetValue(record.Id, out PoseBuffer poses))
                {
                    poses = new PoseBuffer(16);
                    itemPoses[record.Id] = poses;
                }
                poses.Add(tick, record.Position, record.Rotation);
            }

            unseenIds.Clear();
            unseenIds.AddRange(items.Keys.Where(id => !seenIds.Contains(id)));
            foreach (int id in unseenIds)
            {
                InventoryItem item = items[id];
                ForgetItem(id);
                if (item != null)
                {
                    Destroy(item.gameObject);
                }
            }
        }

        private void ApplyPlayer(PlayerRecord record, int tick)
        {
            for (int slot = 0; slot < slotItems.Length; slot++)
            {
                slotItems[slot] = items.TryGetValue(record.Slots[slot], out InventoryItem item) ? item : null;
            }

            NetPlayer player = players[record.Id];
            if (player == self)
            {
                Reconcile(record.LastInputTick, record.State);
                pendingCommands.RemoveAll(command => command.Seq <= record.LastCommandSeq);
                // Until the host has applied every command sent, its slots predate them.
                if (pendingCommands.Count == 0)
                {
                    player.Inventory.ApplyNetworkState(slotItems, record.Selected);
                }
            }
            else
            {
                player.Poses.Add(tick, record.State.Position, Quaternion.Euler(record.Pitch, record.Yaw, 0f));
                player.ProxyVelocity = record.State.Velocity;
                player.ProxyGrounded = record.State.Grounded;
                player.Inventory.ApplyNetworkState(slotItems, record.Selected);
            }
            player.Health.SetHealth(record.Health);
        }

        // The host's state for the newest input it has simulated. If prediction reached a
        // different state at that tick, the motor takes the host's and replays every input since.
        private void Reconcile(int ackTick, MotorState authoritative)
        {
            int acked = 0;
            while (acked < predictions.Count && predictions[acked].Input.Tick < ackTick)
            {
                acked++;
            }
            predictions.RemoveRange(0, acked);

            MotorState predicted;
            if (predictions.Count == 0)
            {
                predicted = localPlayer.State;
            }
            else if (predictions[0].Input.Tick == ackTick)
            {
                predicted = predictions[0].State;
                predictions.RemoveAt(0);
            }
            else
            {
                return;
            }

            if (Agrees(predicted, authoritative))
            {
                return;
            }

            Corrections++;
            localPlayer.SetState(authoritative);
            for (int i = 0; i < predictions.Count; i++)
            {
                localPlayer.Simulate(predictions[i].Input);
                predictions[i] = new Prediction { Input = predictions[i].Input, State = localPlayer.State };
            }
        }

        private static bool Agrees(MotorState a, MotorState b)
        {
            return (a.Position - b.Position).sqrMagnitude <= PositionTolerance * PositionTolerance &&
                   (a.Velocity - b.Velocity).sqrMagnitude <= VelocityTolerance * VelocityTolerance &&
                   a.Grounded == b.Grounded;
        }

        // Players the snapshots haven't spawned yet are picked up by the next update.
        private void ReceiveScores(NetReader packet)
        {
            int count = packet.ReadByte();
            for (int i = 0; i < count; i++)
            {
                int id = packet.ReadInt();
                string name = packet.ReadString();
                int kills = packet.ReadInt();
                int deaths = packet.ReadInt();
                int ping = packet.ReadInt();
                if (!players.TryGetValue(id, out NetPlayer player))
                {
                    continue;
                }
                if (!player.IsLocal)
                {
                    // The scoreboard renders rich text, so a modified host can't restyle it.
                    player.Health.DisplayName = PlayerName.Clean(name, $"Player {id}");
                }
                player.Kills = kills;
                player.Deaths = deaths;
                player.PingMs = ping;
            }
        }

        private void PlayEvent(NetEvent netEvent)
        {
            if (netEvent.Type == NetEventType.Feed)
            {
                // Strips brackets so a modified host can't send rich-text markup.
                EventFeed.Post(netEvent.Message.Replace("<", "").Replace(">", ""));
                return;
            }

            // This client already showed its own shots.
            if (netEvent.Shooter != self.Id && players.TryGetValue(netEvent.Shooter, out NetPlayer shooter) &&
                shooter.Inventory.EquippedWeapon != null)
            {
                shooter.Inventory.EquippedWeapon.PlayShotEffects(netEvent.Point, netEvent.Normal, netEvent.Hit);
            }
        }

        private void InterpolateRemoteEntities()
        {
            if (mode != NetMode.Client)
            {
                return;
            }

            float renderTick = RenderTick;
            foreach (NetPlayer player in players.Values)
            {
                if (!player.IsLocal && player.Poses.Sample(renderTick, out Vector3 position, out Quaternion aim))
                {
                    player.Motor.ApplyProxyState(position, player.ProxyVelocity, player.ProxyGrounded);
                    Vector3 angles = aim.eulerAngles;
                    player.Look.SetRemoteAngles(angles.y, Mathf.DeltaAngle(0f, angles.x));
                }
            }

            foreach (KeyValuePair<int, PoseBuffer> entry in itemPoses)
            {
                if (items.TryGetValue(entry.Key, out InventoryItem item) && item != null && item.IsDropped &&
                    entry.Value.Sample(renderTick, out Vector3 position, out Quaternion rotation))
                {
                    item.transform.SetPositionAndRotation(position, rotation);
                }
            }

            for (int i = 0; i < bodies.Length; i++)
            {
                if (bodies[i] != null && bodyPoses[i].Sample(renderTick, out Vector3 position, out Quaternion rotation))
                {
                    bodies[i].transform.SetPositionAndRotation(position, rotation);
                }
            }
        }

        // Accepts "address" or "address:port", where the address may be a LAN host name.
        private bool TryParseEndPoint(string text, out IPEndPoint endPoint)
        {
            endPoint = null;
            string hostName = text;
            int targetPort = port;
            int colon = text.LastIndexOf(':');
            if (colon > 0 && int.TryParse(text.Substring(colon + 1), out int parsedPort))
            {
                hostName = text.Substring(0, colon);
                targetPort = parsedPort;
            }

            if (!IPAddress.TryParse(hostName, out IPAddress address))
            {
                try
                {
                    address = Array.Find(Dns.GetHostAddresses(hostName), candidate => candidate.AddressFamily == AddressFamily.InterNetwork);
                }
                catch (SocketException)
                {
                    return false;
                }
                catch (ArgumentException)
                {
                    return false;
                }
            }
            if (address == null)
            {
                return false;
            }
            endPoint = new IPEndPoint(address, targetPort);
            return true;
        }
    }
}
