using UnityEngine;

namespace FpsBase
{
    // Everything one movement tick reads: the same struct drives the local player, a client's
    // prediction and replays, and the host's simulation of a remote player.
    public struct PlayerInput
    {
        public int Tick;
        public Vector2 Move;
        public bool Walk;
        // A jump press is still buffered at this tick.
        public bool Jump;
        public float Yaw;
        public float Pitch;

        public void Write(NetWriter writer)
        {
            writer.Write(Tick);
            writer.Write(Move);
            writer.Write((byte)((Walk ? 1 : 0) | (Jump ? 2 : 0)));
            writer.Write(Yaw);
            writer.Write(Pitch);
        }

        public static PlayerInput Read(NetReader reader)
        {
            var input = new PlayerInput { Tick = reader.ReadInt(), Move = reader.ReadVector2() };
            byte flags = reader.ReadByte();
            input.Walk = (flags & 1) != 0;
            input.Jump = (flags & 2) != 0;
            input.Yaw = reader.ReadFloat();
            input.Pitch = reader.ReadFloat();
            return input;
        }
    }

    // The motor state a tick carries forward, enough to rewind and replay movement.
    public struct MotorState
    {
        public Vector3 Position;
        public Vector3 Velocity;
        public bool Grounded;
        public bool LandingGrace;
        public float FallApex;

        public void Write(NetWriter writer)
        {
            writer.Write(Position);
            writer.Write(Velocity);
            writer.Write((byte)((Grounded ? 1 : 0) | (LandingGrace ? 2 : 0)));
            writer.Write(FallApex);
        }

        public static MotorState Read(NetReader reader)
        {
            var state = new MotorState { Position = reader.ReadVector3(), Velocity = reader.ReadVector3() };
            byte flags = reader.ReadByte();
            state.Grounded = (flags & 1) != 0;
            state.LandingGrace = (flags & 2) != 0;
            state.FallApex = reader.ReadFloat();
            return state;
        }
    }
}
