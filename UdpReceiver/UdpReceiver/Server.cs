using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Net;
using System.Text;

namespace UdpReceiver
{
    internal class Server
    {
        public readonly ulong Id;

        public readonly IPAddress IP;

        public readonly uint IPv4;

        public readonly ushort Port;

        public readonly int BroadcastDelaySeconds;

        public readonly string? Tag;

        public ServerContext Context;

        public Server(string ip, ushort port, int delaySeconds, string? tag)
        {
            if (!IPAddress.TryParse(ip, out IPAddress? ipAddress))
                throw new ArgumentException($"IPAddress.Parse failed for ip \"{ip}\".");

            IP = ipAddress;
            IPv4 = BinaryPrimitives.ReadUInt32BigEndian(ipAddress.GetAddressBytes());
            Port = port;
            BroadcastDelaySeconds = delaySeconds;
            Tag = tag;

            Context = new();

            Id = BuildIdFromIPv4AndPort(ipAddress, Port);
        }

        public static ulong BuildIdFromIPv4AndPort(IPAddress ip, ushort port)
        {
            Span<byte> bytes = stackalloc byte[4];
            ip.TryWriteBytes(bytes, out _);

            var ipAsInt = BinaryPrimitives.ReadUInt32BigEndian(bytes);

            // bits = 63    48 47..16 15...0
            //   FlashedId = [000...] [ ip ] [port]
            return ((ulong)ipAsInt << 16) | port;
        }

    }

    internal class ServerQuery
    {
        public string Map = "";

        public int NumPlayers;
    }
}
