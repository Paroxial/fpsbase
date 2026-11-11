using System;
using System.Buffers.Binary;
using System.Text;
using UnityEngine;

namespace FpsBase
{
    // Little-endian packet writer over one reused buffer, so sending allocates nothing.
    public class NetWriter
    {
        private readonly byte[] buffer = new byte[NetProtocol.MaxPacket];

        public byte[] Buffer => buffer;

        public int Length { get; private set; }

        public void Begin(MessageType type)
        {
            Length = 0;
            Write(NetProtocol.Magic);
            Write((byte)type);
        }

        public void Write(byte value)
        {
            Reserve(1);
            buffer[Length++] = value;
        }

        public void Write(bool value)
        {
            Write(value ? (byte)1 : (byte)0);
        }

        public void Write(int value)
        {
            Reserve(4);
            BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(Length), value);
            Length += 4;
        }

        public void Write(float value)
        {
            Write(BitConverter.SingleToInt32Bits(value));
        }

        public void Write(Vector2 value)
        {
            Write(value.x);
            Write(value.y);
        }

        public void Write(Vector3 value)
        {
            Write(value.x);
            Write(value.y);
            Write(value.z);
        }

        public void Write(Quaternion value)
        {
            Write(value.x);
            Write(value.y);
            Write(value.z);
            Write(value.w);
        }

        // UTF-8 behind a one-byte length.
        public void Write(string value)
        {
            int count = Encoding.UTF8.GetByteCount(value);
            if (count > byte.MaxValue)
            {
                throw new ArgumentException($"String exceeds {byte.MaxValue} bytes.", nameof(value));
            }
            Write((byte)count);
            Reserve(count);
            Length += Encoding.UTF8.GetBytes(value, 0, value.Length, buffer, Length);
        }

        private void Reserve(int bytes)
        {
            if (Length + bytes > buffer.Length)
            {
                throw new InvalidOperationException($"Packet exceeds {buffer.Length} bytes.");
            }
        }
    }

    // Reads one received packet. Reading past the end throws, and the caller drops the packet.
    public class NetReader
    {
        private byte[] buffer;
        private int length;
        private int position;

        public void Reset(byte[] source, int count)
        {
            buffer = source;
            length = count;
            position = 0;
        }

        public byte ReadByte()
        {
            Require(1);
            return buffer[position++];
        }

        public bool ReadBool()
        {
            return ReadByte() != 0;
        }

        public int ReadInt()
        {
            Require(4);
            int value = BinaryPrimitives.ReadInt32LittleEndian(buffer.AsSpan(position));
            position += 4;
            return value;
        }

        public float ReadFloat()
        {
            return BitConverter.Int32BitsToSingle(ReadInt());
        }

        public Vector2 ReadVector2()
        {
            return new Vector2(ReadFloat(), ReadFloat());
        }

        public Vector3 ReadVector3()
        {
            return new Vector3(ReadFloat(), ReadFloat(), ReadFloat());
        }

        public Quaternion ReadQuaternion()
        {
            return new Quaternion(ReadFloat(), ReadFloat(), ReadFloat(), ReadFloat());
        }

        public string ReadString()
        {
            int count = ReadByte();
            Require(count);
            string value = Encoding.UTF8.GetString(buffer, position, count);
            position += count;
            return value;
        }

        private void Require(int bytes)
        {
            if (position + bytes > length)
            {
                throw new FormatException("Packet ended early.");
            }
        }
    }
}
