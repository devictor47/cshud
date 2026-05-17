using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json.Serialization;

namespace UdpReceiver
{
    internal sealed class Config
    {
        public List<ServerConfig> Servers { get; set; } = [];
    }

    internal sealed class ServerConfig
    {
        public string Ip { get; set; } = "";
        public ushort Port { get; set; } = 0;

        [JsonPropertyName("delay")]
        public int DelaySec { get; set; } = 35;
        public string? Tag { get; set; } = "";
        public bool Enabled { get; set; } = false;
    }
}
