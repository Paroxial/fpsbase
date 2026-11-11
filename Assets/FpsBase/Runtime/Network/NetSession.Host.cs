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
        private const int MaxPlayers = 16;
        // Inputs beyond this many waiting are simulated faster to catch up, which caps how far the
        // host's copy of a player can lag behind that player's own input.
        private const int InputBufferTarget = 2;
        private const int MaxInputsPerTick = 3;
        private const int MaxBufferedInputs = 32;

        private class Peer
        {
            public IPEndPoint EndPoint;
            public int Nonce;
            public NetPlayer Player;
            public float LastHeard;
            public int PingMs = -1;
            public readonly SortedList<int, PlayerInput> Inputs = new();
        }

        private readonly List<Peer> peers = new();
        private readonly List<NetEvent> events = new();
        private readonly List<int> staleItemIds = new();
        private int nextPlayerId = 1;
        private int nextEventSeq = 1;
        private int lastSnapshotTick = int.MinValue;
        private string hostAddress;
        private UpnpMapping upnp;

        // Each connected player's name and newest round trip in milliseconds, -1 before the first reply.
        public IEnumerable<(string Name, int PingMs)> PeerPings => peers.Select(peer => (peer.Player.Health.DisplayName, peer.PingMs));

        public void Host()
        {
            if (socket != null || !OpenSocket(port))
            {
                return;
            }

            mode = NetMode.Host;
            hostAddress = LocalAddress();
            upnp = new UpnpMapping(port);
            EventFeed.Posted += AddFeedEvent;
            Time.timeScale = 1f;
            // A session must keep ticking when its window loses focus, or everyone else stalls.
            Application.runInBackground = true;
            RegisterPlayer(0, localPlayer, true);
        }

        // Called by the host's own weapons, so clients hear and see the host's shots.
        public void ReportShot(PlayerLook shooter, HitscanWeapon.ShotResult result)
        {
            if (mode != NetMode.Host)
            {
                return;
            }
            NetPlayer player = FindPlayer(shooter);
            if (player != null)
            {
                AddShotEvent(player, result);
            }
        }

        private string HostStatus()
        {
            string count = players.Count == 1 ? "1 player" : $"{players.Count} players";
            string status = $"Hosting on {hostAddress}:{port}, {count}";
            if (upnp == null)
            {
                return status;
            }
            if (upnp.IsPending)
            {
                return $"{status}, Opening Internet Port";
            }
            return upnp.Failure != null ? $"{status}, {upnp.Failure}" : $"{status}, Internet: {upnp.ExternalAddress}:{upnp.ExternalPort}";
        }

        private void PollHost()
        {
            while (socket.TryReceive(out NetReader packet, out MessageType type, out IPEndPoint sender))
            {
                try
                {
                    switch (type)
                    {
                        case MessageType.Probe:
                            writer.Begin(MessageType.ProbeReply);
                            socket.Send(writer, sender);
                            break;
                        case MessageType.Hello:
                            ReceiveHello(packet.ReadInt(), packet.ReadString(), sender);
                            break;
                        case MessageType.Input:
                            Peer peer = FindPeer(sender);
                            if (peer != null)
                            {
                                ReceiveInput(peer, packet);
                            }
                            break;
                        case MessageType.Bye:
                            Peer leaving = FindPeer(sender);
                            if (leaving != null)
                            {
                                RemovePeer(leaving);
                            }
                            break;
                        case MessageType.Ping:
                            if (FindPeer(sender) != null)
                            {
                                ReplyToPing(packet, sender);
                            }
                            break;
                        case MessageType.Pong:
                            Peer pinged = FindPeer(sender);
                            if (pinged != null)
                            {
                                pinged.PingMs = ReadRoundTrip(packet);
                            }
                            break;
                    }
                }
                catch (FormatException)
                {
                    // A truncated packet: whatever it applied before ending still stands.
                }
            }

            float now = Time.unscaledTime;
            for (int i = peers.Count - 1; i >= 0; i--)
            {
                if (now - peers[i].LastHeard > NetProtocol.TimeoutSeconds)
                {
                    RemovePeer(peers[i]);
                }
            }

            if (now - lastPingTime >= NetProtocol.PingInterval)
            {
                lastPingTime = now;
                foreach (Peer target in peers)
                {
                    SendPing(target.EndPoint);
                }
            }
        }

        // Clients resend Hello until a Welcome arrives, so a repeat gets the same answer.
        private void ReceiveHello(int nonce, string name, IPEndPoint sender)
        {
            Peer peer = FindPeer(sender);
            if (peer != null && peer.Nonce != nonce)
            {
                RemovePeer(peer);
                peer = null;
            }
            if (peer == null)
            {
                if (players.Count >= MaxPlayers)
                {
                    return;
                }
                peer = new Peer { EndPoint = sender, Nonce = nonce, Player = SpawnRemotePlayer(nextPlayerId++) };
                peers.Add(peer);
                PlayerHealth health = peer.Player.Health;
                health.DisplayName = PlayerName.Clean(name, $"Player {peer.Player.Id}");
                EventFeed.ReportJoin(health.DisplayName);
            }
            peer.LastHeard = Time.unscaledTime;

            writer.Begin(MessageType.Welcome);
            writer.Write(nonce);
            writer.Write(peer.Player.Id);
            writer.Write(Simulation.CurrentTick);
            writer.Write(nextEventSeq - 1);
            socket.Send(writer, sender);
        }

        private void ReceiveInput(Peer peer, NetReader packet)
        {
            peer.LastHeard = Time.unscaledTime;
            NetPlayer player = peer.Player;

            int inputCount = packet.ReadByte();
            for (int i = 0; i < inputCount; i++)
            {
                PlayerInput input = PlayerInput.Read(packet);
                if (input.Tick > player.LastInputTick && !peer.Inputs.ContainsKey(input.Tick))
                {
                    peer.Inputs.Add(input.Tick, input);
                }
            }
            while (peer.Inputs.Count > MaxBufferedInputs)
            {
                peer.Inputs.RemoveAt(0);
            }

            int commandCount = packet.ReadByte();
            for (int i = 0; i < commandCount; i++)
            {
                NetCommand command = NetCommand.Read(packet);
                if (command.Seq == player.LastCommandSeq + 1)
                {
                    player.LastCommandSeq = command.Seq;
                    ApplyCommand(peer, command);
                }
            }
        }

        private void ApplyCommand(Peer peer, NetCommand command)
        {
            NetPlayer player = peer.Player;
            switch (command.Type)
            {
                case CommandType.Select:
                    player.Inventory.SelectSlot(command.Value);
                    break;
                case CommandType.PickUp:
                    if (items.TryGetValue(command.Value, out InventoryItem item) && item != null && item.IsDropped &&
                        Vector3.Distance(item.transform.position, EyePosition(player)) <= player.Inventory.InteractRange + 1f)
                    {
                        player.Inventory.PickUp(item);
                    }
                    break;
                case CommandType.Drop:
                    player.Inventory.DropSelected();
                    break;
                case CommandType.Fire:
                    ApplyRemoteShot(player, command);
                    break;
            }
        }

        // Clients decide their own hits; the host applies whatever a shot reports.
        private void ApplyRemoteShot(NetPlayer shooter, NetCommand command)
        {
            HitscanWeapon weapon = shooter.Inventory.EquippedWeapon;
            var result = new HitscanWeapon.ShotResult { Hit = command.Hit, Point = command.Point, Normal = command.Normal };
            Damageable target = null;
            switch (command.Entity)
            {
                case NetEntity.Player:
                    target = players.TryGetValue(command.Value, out NetPlayer victim) ? victim.Health : null;
                    break;
                case NetEntity.Target:
                    target = command.Value >= 0 && command.Value < targets.Length ? targets[command.Value] : null;
                    break;
                case NetEntity.Body:
                    if (weapon != null && command.Value >= 0 && command.Value < bodies.Length)
                    {
                        weapon.Push(bodies[command.Value], command.Point, command.Direction);
                    }
                    break;
                case NetEntity.Item:
                    if (weapon != null && items.TryGetValue(command.Value, out InventoryItem item) && item != null)
                    {
                        weapon.Push(item.Rigidbody, command.Point, command.Direction);
                    }
                    break;
            }
            if (target != null)
            {
                target.ApplyDamage(command.Damage, shooter.Health);
            }
            if (DebugOverlay.Enabled)
            {
                DebugOverlay.Note($"Host applied Player {shooter.Id}'s shot: {command.Entity} {command.Value}, {command.Damage:0.#} dmg" +
                                  (target != null ? $", {target.name} now {target.Health:0.#} hp" : ""));
            }

            if (weapon != null)
            {
                weapon.PlayShotEffects(result.Point, result.Normal, result.Hit);
            }
            AddShotEvent(shooter, result);
        }

        private static Vector3 EyePosition(NetPlayer player)
        {
            return player.Motor.GetSimulationPosition(player.Look.CameraTransform);
        }

        private void AddShotEvent(NetPlayer shooter, HitscanWeapon.ShotResult result)
        {
            events.Add(new NetEvent
            {
                Seq = nextEventSeq++,
                Tick = Simulation.CurrentTick,
                Type = NetEventType.Shot,
                Shooter = shooter.Id,
                Point = result.Point,
                Normal = result.Normal,
                Hit = result.Hit,
            });
        }

        private void AddFeedEvent(string message)
        {
            events.Add(new NetEvent { Seq = nextEventSeq++, Tick = Simulation.CurrentTick, Type = NetEventType.Feed, Message = message });
        }

        // Runs before the motors' own ticks. A starved player waits rather than repeating input:
        // the host then simulates exactly the client's inputs in order, and the client's
        // prediction stays right.
        private void SimulateRemotePlayers()
        {
            foreach (Peer peer in peers)
            {
                int steps = 0;
                while (peer.Inputs.Count > 0 && steps < MaxInputsPerTick && (steps == 0 || peer.Inputs.Count > InputBufferTarget))
                {
                    PlayerInput input = peer.Inputs.Values[0];
                    peer.Inputs.RemoveAt(0);
                    NetPlayer player = peer.Player;
                    player.LastInputTick = input.Tick;
                    player.Look.SetRemoteAngles(input.Yaw, input.Pitch);
                    if (player.Health.IsAlive)
                    {
                        player.Motor.Simulate(input);
                    }
                    steps++;
                }
            }
        }

        private void SendSnapshot()
        {
            int tick = Simulation.CurrentTick;
            if (tick == lastSnapshotTick || peers.Count == 0)
            {
                return;
            }
            lastSnapshotTick = tick;
            while (events.Count > 0 && events[0].Tick < tick - NetProtocol.EventLifetimeTicks)
            {
                events.RemoveAt(0);
            }

            writer.Begin(MessageType.Snapshot);
            writer.Write(tick);
            WriteItems();
            WritePlayers();

            writer.Write(bodies.Length);
            foreach (Rigidbody body in bodies)
            {
                writer.Write(body.position);
                writer.Write(body.rotation);
            }

            writer.Write(targets.Length);
            foreach (Damageable target in targets)
            {
                writer.Write(target.Health);
            }

            writer.Write((byte)Mathf.Min(events.Count, byte.MaxValue));
            for (int i = Mathf.Max(0, events.Count - byte.MaxValue); i < events.Count; i++)
            {
                events[i].Write(writer);
            }

            foreach (Peer peer in peers)
            {
                socket.Send(writer, peer.EndPoint);
            }
        }

        private void WriteItems()
        {
            staleItemIds.Clear();
            staleItemIds.AddRange(items.Where(entry => entry.Value == null).Select(entry => entry.Key));
            foreach (int id in staleItemIds)
            {
                ForgetItem(id);
            }

            writer.Write(items.Count);
            foreach (KeyValuePair<int, InventoryItem> entry in items)
            {
                InventoryItem item = entry.Value;
                writer.Write(entry.Key);
                writer.Write((byte)ItemKind(item));
                writer.Write(item.IsDropped);
                if (item.IsDropped)
                {
                    writer.Write(item.Rigidbody.position);
                    writer.Write(item.Rigidbody.rotation);
                }
            }
        }

        private void WritePlayers()
        {
            writer.Write((byte)players.Count);
            foreach (NetPlayer player in players.Values)
            {
                writer.Write(player.Id);
                player.Motor.State.Write(writer);
                writer.Write(player.Look.Yaw);
                writer.Write(player.Look.Pitch);
                writer.Write(player.Health.Health);
                writer.Write(player.LastInputTick);
                writer.Write(player.LastCommandSeq);
                writer.Write((byte)player.Inventory.SelectedIndex);
                for (int slot = 0; slot < PlayerInventory.SlotCount; slot++)
                {
                    writer.Write(ItemId(player.Inventory.GetItem(slot)));
                }
            }
        }

        private Peer FindPeer(IPEndPoint endPoint)
        {
            foreach (Peer peer in peers)
            {
                if (peer.EndPoint.Equals(endPoint))
                {
                    return peer;
                }
            }
            return null;
        }

        private void RemovePeer(Peer peer)
        {
            peers.Remove(peer);
            RemovePlayer(peer.Player.Id);
        }

        private static string LocalAddress()
        {
            try
            {
                IPAddress address = Dns.GetHostAddresses(Dns.GetHostName())
                    .FirstOrDefault(candidate => candidate.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(candidate));
                return address != null ? address.ToString() : IPAddress.Loopback.ToString();
            }
            catch (SocketException)
            {
                return IPAddress.Loopback.ToString();
            }
        }
    }
}
