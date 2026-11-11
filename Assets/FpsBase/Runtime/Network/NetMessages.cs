using System;
using UnityEngine;

namespace FpsBase
{
    // A client's request to the host. Commands carry increasing sequence numbers and repeat in
    // every input packet until a snapshot acknowledges them, so none is lost or applied twice.
    public struct NetCommand
    {
        public int Seq;
        public CommandType Type;
        public Vector3 Origin;
        public Vector3 Direction;
        // A shot's result as the shooter saw it: where it landed, what it struck, and the damage.
        public bool Hit;
        public Vector3 Point;
        public Vector3 Normal;
        public NetEntity Entity;
        public float Damage;
        // The slot to select, the item to pick up, or the id of the entity a shot struck.
        public int Value;

        public void Write(NetWriter writer)
        {
            writer.Write(Seq);
            writer.Write((byte)Type);
            switch (Type)
            {
                case CommandType.Fire:
                    writer.Write(Origin);
                    writer.Write(Direction);
                    writer.Write(Hit);
                    writer.Write(Point);
                    writer.Write(Normal);
                    writer.Write((byte)Entity);
                    writer.Write(Value);
                    writer.Write(Damage);
                    break;
                case CommandType.Select:
                case CommandType.PickUp:
                    writer.Write(Value);
                    break;
            }
        }

        public static NetCommand Read(NetReader reader)
        {
            var command = new NetCommand { Seq = reader.ReadInt(), Type = (CommandType)reader.ReadByte() };
            switch (command.Type)
            {
                case CommandType.Fire:
                    command.Origin = reader.ReadVector3();
                    command.Direction = reader.ReadVector3();
                    command.Hit = reader.ReadBool();
                    command.Point = reader.ReadVector3();
                    command.Normal = reader.ReadVector3();
                    command.Entity = (NetEntity)reader.ReadByte();
                    command.Value = reader.ReadInt();
                    command.Damage = reader.ReadFloat();
                    break;
                case CommandType.Select:
                case CommandType.PickUp:
                    command.Value = reader.ReadInt();
                    break;
            }
            return command;
        }
    }

    // Something that happened on the host for clients to show: a shot's sound and impact, or a feed
    // message. Rides in snapshots for a short while, keyed by sequence.
    public struct NetEvent
    {
        public int Seq;
        public int Tick;
        public NetEventType Type;
        public int Shooter;
        public Vector3 Point;
        public Vector3 Normal;
        public bool Hit;
        public string Message;

        public void Write(NetWriter writer)
        {
            writer.Write(Seq);
            writer.Write((byte)Type);
            switch (Type)
            {
                case NetEventType.Shot:
                    writer.Write(Shooter);
                    writer.Write(Point);
                    writer.Write(Normal);
                    writer.Write(Hit);
                    break;
                case NetEventType.Feed:
                    writer.Write(Message);
                    break;
            }
        }

        public static NetEvent Read(NetReader reader)
        {
            var netEvent = new NetEvent { Seq = reader.ReadInt(), Type = (NetEventType)reader.ReadByte() };
            switch (netEvent.Type)
            {
                case NetEventType.Shot:
                    netEvent.Shooter = reader.ReadInt();
                    netEvent.Point = reader.ReadVector3();
                    netEvent.Normal = reader.ReadVector3();
                    netEvent.Hit = reader.ReadBool();
                    break;
                case NetEventType.Feed:
                    netEvent.Message = reader.ReadString();
                    break;
                default:
                    throw new FormatException($"Unknown event type {netEvent.Type}.");
            }
            return netEvent;
        }
    }
}
