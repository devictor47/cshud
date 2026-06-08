//#define DEBUG_STAT

using System.Text;
using System.Text.Json;

namespace UdpReceiver
{
    partial class Program
    {
        const int MAX_WEAPONS = 32 - 1; // -1 -> ignore shield.

        public static readonly int MAX_PLAYERS = 32;

        // This array maps every weapon ID to a HUD slot
        static readonly WeaponSlot[] WeaponsSlot =
            [
                WeaponSlot.None,
                WeaponSlot.Secondary,   WeaponSlot.None,        WeaponSlot.Primary,
                WeaponSlot.Grenade,     WeaponSlot.Primary,     WeaponSlot.C4,
                WeaponSlot.Primary,     WeaponSlot.Primary,     WeaponSlot.Grenade,
                WeaponSlot.Secondary,   WeaponSlot.Secondary,   WeaponSlot.Primary,
                WeaponSlot.Primary,     WeaponSlot.Primary,     WeaponSlot.Primary,
                WeaponSlot.Secondary,   WeaponSlot.Secondary,   WeaponSlot.Primary,
                WeaponSlot.Primary,     WeaponSlot.Primary,     WeaponSlot.Primary,
                WeaponSlot.Primary,     WeaponSlot.Primary,     WeaponSlot.Primary,
                WeaponSlot.Grenade,     WeaponSlot.Secondary,   WeaponSlot.Primary,
                WeaponSlot.Primary,     WeaponSlot.Knife,       WeaponSlot.Primary,
                //WeaponSlot.Secondary // Ignore shield.
            ];

        static readonly Logger LogWriter = new("run_log", true);


#if DEBUG_STAT

        static readonly Logger DebugWriter = new("stats_log", true);

        static uint bytesRcvd = 0;
        static uint pcktsRcvd = 0;
        static uint pcktsRcvdPrev = 0;
        static float avgPcktSize = 0;
        static uint minPktSize = uint.MaxValue;
        static uint maxPktSize = uint.MinValue;
        static uint h64, h128, h256, hUpper;
        static readonly TimeSpan writeInterval = TimeSpan.FromSeconds(5);
        static DateTime nextWriteAt = DateTime.UtcNow + writeInterval;

        static void LogDebug(string message)
        {
            if (!string.IsNullOrWhiteSpace(message))
                DebugWriter.WriteLine(message, false);
        }
#endif

        static Dictionary<ulong, Server> InitServers()
        {
            Log("Loading config file...");

            var cfg = JsonSerializer.Deserialize<Config>(
                File.ReadAllText("config.json") ?? throw new Exception("Invalid config file."),
                new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                })
                ?? throw new Exception("Invalid config file.");

            Log($"Found {cfg.Servers.Count} servers.");

            var ret = new Dictionary<ulong, Server>(cfg.Servers.Count + 4);

            foreach (var server in cfg.Servers)
            {
                if (!server.Enabled)
                    continue;

                var sv = new Server(server.Ip, server.Port, server.DelaySec, server.Tag?.Trim());
                ret.Add(sv.Id, sv);

                if (!string.IsNullOrEmpty(sv.Tag))
                    Log($"Server {sv.IP}:{sv.Port} <{sv.Tag}> registered.");
                else
                    Log($"Server {sv.IP}:{sv.Port} registered.");
            }

            return ret;
        }

        static void Log(string message, bool printConsole = true)
        {
            if (!string.IsNullOrWhiteSpace(message))
                LogWriter.WriteLine(message, printConsole);
        }

        static void LogErr(Exception ex, string? append = null, bool printConsole = true)
        {
            string agExMsg = string.Empty;
            if (ex is AggregateException agEx)
            {
                agExMsg = string.Join("\n",
                    agEx.Flatten().InnerExceptions.Select(
                        e => $"{e.GetType().Name}: {e.Message}"
                    ));
            }

            Log($"=== Error ===" +
                $"\n\tException: {ex}" +
                $"\n\tMessage: {ex.Message}" +
                $"{ (agExMsg == string.Empty ? "" : $"\n\tAggregates message: {agExMsg}" ) }" +
                $"\n\tStackTrace: {ex.StackTrace}" +
                (string.IsNullOrWhiteSpace(append) ? string.Empty : append),
                printConsole);
        }

    }
}
