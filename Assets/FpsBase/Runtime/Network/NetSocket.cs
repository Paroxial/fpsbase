using System;
using System.Net;
using System.Net.Sockets;

namespace FpsBase
{
    // Non-blocking UDP socket polled from the main thread. Broadcast is on for LAN discovery.
    public class NetSocket : IDisposable
    {
        // Windows reports an ICMP "port unreachable" from an earlier send as a failed receive;
        // this switches that off so one closed peer can't stall the receive loop.
        private const int SioUdpConnReset = -1744830452;

        private readonly Socket socket;
        private readonly byte[] receiveBuffer = new byte[NetProtocol.MaxPacket];
        private readonly NetReader reader = new();

        public NetSocket(int port)
        {
            socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp)
            {
                Blocking = false,
                EnableBroadcast = true,
            };
            try
            {
                socket.IOControl(SioUdpConnReset, new byte[] { 0, 0, 0, 0 }, null);
            }
            catch (Exception)
            {
                // Not Windows: there is no such report to switch off.
            }
            socket.Bind(new IPEndPoint(IPAddress.Any, port));
        }

        public void Send(NetWriter writer, IPEndPoint destination)
        {
            try
            {
                socket.SendTo(writer.Buffer, 0, writer.Length, SocketFlags.None, destination);
            }
            catch (SocketException)
            {
                // UDP is best effort: a send that fails now is the same as a lost packet.
            }
        }

        // Returns the next packet with a valid header, positioned after its type byte.
        public bool TryReceive(out NetReader packet, out MessageType type, out IPEndPoint sender)
        {
            packet = reader;
            while (true)
            {
                EndPoint remote = new IPEndPoint(IPAddress.Any, 0);
                int count;
                try
                {
                    if (socket.Available == 0)
                    {
                        type = default;
                        sender = null;
                        return false;
                    }
                    count = socket.ReceiveFrom(receiveBuffer, ref remote);
                }
                catch (SocketException exception) when (exception.SocketErrorCode == SocketError.WouldBlock)
                {
                    type = default;
                    sender = null;
                    return false;
                }
                catch (SocketException)
                {
                    continue;
                }

                reader.Reset(receiveBuffer, count);
                try
                {
                    if (reader.ReadInt() != NetProtocol.Magic)
                    {
                        continue;
                    }
                    type = (MessageType)reader.ReadByte();
                }
                catch (FormatException)
                {
                    continue;
                }
                sender = (IPEndPoint)remote;
                return true;
            }
        }

        public void Dispose()
        {
            socket.Dispose();
        }
    }
}
