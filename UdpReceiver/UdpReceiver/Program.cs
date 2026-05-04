
using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace UdpReceiver
{
    [Flags]
    enum GlobalFlags : byte
    {
        ROUND_TIME = 1 << 0,
        SCORE = 1 << 1,
        MAP = 1 << 2
    };

    [Flags]
    enum PlayerFlags : ushort
    {
        NONE = 0,

        // Sent if dead or alive.
        TEAM = 1 << 0,
        NAME = 1 << 1,
        MONEY = 1 << 2,
        FRAGS = 1 << 3,
        DEATHS = 1 << 4,

        // Sent only if alive.
        YAW = 1 << 5,
        POS = 1 << 6,
        HP = 1 << 7,
        ARMOR = 1 << 8,
        CURWEP = 1 << 9,
        INV = 1 << 10,
        ITEMS = 1 << 11,

        DROPPED = 1 << 12
    };

    enum EventType : byte
    {
        ROUND_ENDED,
        DIED, // killer, victim, assistant, weapon, rarity flags

        BOMB_PLANTING,
        BOMB_PLANT_ABORTED,
        BOMB_PLANTED,

        BOMB_DROPPED,
        BOMB_PICKED_UP,

        BOMB_DEFUSING,
        BOMB_DEFUSE_ABORTED,

        BOMB_DEFUSED,
        BOMB_EXPLODED,

        FLASHED, // EVENT_PLAYER_BLINDED_BY_FLASHBANG
        KILL_FLASHBANGED
    };

    enum PacketType
    {
        PCKT_GLOBAL = 'G',
        PCKT_PLAYERS = 'P',
        PCKT_EVENTS = 'E'
    }

    // cssdk_const.inc
    enum WeaponId
    {
        NONE,
        P228,
        GLOCK, // Actually not used in game. Glock18 is.
        SCOUT,
        HEGRENADE,
        XM1014,
        C4,
        MAC10,
        AUG,
        SMOKEGRENADE,
        ELITE,
        FIVESEVEN,
        UMP45,
        SG550,
        GALIL,
        FAMAS,
        USP,
        GLOCK18,
        AWP,
        MP5N,
        M249,
        M3,
        M4A1,
        TMP,
        G3SG1,
        FLASHBANG,
        DEAGLE,
        SG552,
        AK47,
        KNIFE,
        P90,
        /*WEAPON_SHIELDGUN = 99*/ // Ignore shield. The game will give the pistol as the secondary.
    };

    enum WeaponSlot
    {
        None,
        Primary,
        Secondary,
        Knife,
        Grenade,
        C4
    }

    [Flags]
    enum Grenades
    {
        None = 0x00,
        HE = 0x01,
        FLASH = 0x02,
        SMOKE = 0x04
    }

    enum ArmorType
    {
        Vest,
        VestHelm,
        None,
    }

    enum ItemsHeld
    {
        None,
        Nightvision,
        DefuseKit
    }

    enum Team
    {
        Unassigned,
        Terrorist,
        CT,
        Spectator
    }

    enum WinStatus
    {
        NONE,
        CTS,
        TERRORISTS,
        DRAW,
    };

    enum RoundEndReason
    {
        NONE,
        TARGET_BOMB,
        VIP_ESCAPED,
        VIP_ASSASSINATED,
        TERRORISTS_ESCAPED,
        CTS_PREVENT_ESCAPE,
        ESCAPING_TERRORISTS_NEUTRALIZED,
        BOMB_DEFUSED,
        CTS_WIN,
        TERRORISTS_WIN,
        END_DRAW,
        ALL_HOSTAGES_RESCUED,
        TARGET_SAVED,
        HOSTAGE_NOT_RESCUED,
        TERRORISTS_NOT_ESCAPED,
        VIP_NOT_ESCAPED,
        GAME_COMMENCE,
        GAME_RESTART,
        GAME_OVER
    };

    [Flags]
    enum KillRarity
    {
        NONE = 0,
        HEADSHOT = 0x001, // Headshot
        KILLER_BLIND = 0x002, // Killer was blind
        NOSCOPE = 0x004, // No-scope sniper rifle kill
        PENETRATED = 0x008, // Penetrated kill (through walls)
        THRUSMOKE = 0x010, // Smoke grenade penetration kill (bullets went through smoke)
        ASSISTEDFLASH = 0x020, // Assister helped with a flash
        DOMINATION_BEGAN = 0x040, // Killer player began dominating the victim (NOTE: this flag is set once)
        DOMINATION = 0x080, // Continues domination by the killer
        REVENGE = 0x100, // Revenge by the killer
        INAIR = 0x200  // Killer was in the air (skill to deal with high inaccuracy)
    };

    class GlobalDelta
    {
        public GlobalFlags Flags = 0;

        public float? RoundEndTick;
        public byte? TScore;
        public byte? CTScore;
        public string? Map;
    }

    class PlayerDelta
    {
        public PlayerFlags Flags = 0;

        public byte Id;

        public Team? Team;
        public float? Yaw;
        public (short x, short y, short z)? Pos;
        public sbyte? Hp;
        public (ArmorType ArmorType, byte ArmorValue)? Armor;
        public WeaponId? CurrentWeapon;
        public ushort? Money;
        public sbyte? Frags;
        public byte? Deaths;

        // --- Translated inventory ---
        public WeaponId? PrimaryWeapon;
        public WeaponId? SecondaryWeapon;
        public Grenades? Grenades;
        public bool? HasC4;

        public ItemsHeld? Items;
        public string? Name;

        public bool? Dropped;

        public bool HasInventory =>
        PrimaryWeapon.HasValue &&
        SecondaryWeapon.HasValue &&
        Grenades.HasValue &&
        HasC4.HasValue;
    }

    class PlayerState
    {
        public byte Id;

        public Team Team;

        // Position
        public float Yaw;
        public short X;
        public short Y;
        public short Z;

        // Vital stats
        public sbyte RawHp; // keep raw (can be negative)
        public byte Deaths;
        public sbyte Frags;

        // Economy
        public ushort Money;

        // Equipment
        public ArmorType ArmorType;
        public byte ArmorValue;

        public WeaponId CurrentWeapon;

        // Inventory (normalized)
        public WeaponId PrimaryWeapon;
        public WeaponId SecondaryWeapon;
        public Grenades Grenades;
        public bool HasC4;
        public ItemsHeld Items;

        // Identity
        public string Name = string.Empty;

        // --- Convenience (computed, not stored) ---
        public int Hp => Math.Max(0, (int)RawHp);

        public PlayerState() : this(0) { }

        public PlayerState(byte id)
        {
            Id = id;
            Team = Team.Unassigned;
            ArmorType = ArmorType.None;
            ArmorValue = 0;
            CurrentWeapon = WeaponId.NONE;
            PrimaryWeapon = WeaponId.NONE;
            SecondaryWeapon = WeaponId.NONE;
            Grenades = Grenades.None;
            HasC4 = false;
            Items = ItemsHeld.None;
            Name = string.Empty;
        }

        public void Apply(PlayerDelta d)
        {
            if (d.Id != Id)
            {
#if DEBUG
                Debug.Fail($"Delta applied to wrong player ({d.Id} -> {Id})");
#endif
                return;
            }

            if (d.Team.HasValue) Team = d.Team.Value;

            // --- Position ---
            if (d.Yaw.HasValue) Yaw = d.Yaw.Value;
            if (d.Pos.HasValue)
            {
                var p = d.Pos.Value;
                X = p.x; Y = p.y; Z = p.z;
            }

            // --- Vital Stats ---
            if (d.Hp.HasValue) RawHp = d.Hp.Value;
            if (d.Frags.HasValue) Frags = d.Frags.Value;
            if (d.Deaths.HasValue) Deaths = d.Deaths.Value;

            // --- Economy ---
            if (d.Money.HasValue) Money = d.Money.Value;

            // --- Equipment ---
            if (d.Armor.HasValue)
            {
                ArmorValue = d.Armor.Value.ArmorValue;
                ArmorType = d.Armor.Value.ArmorType;
            }

            if (d.CurrentWeapon.HasValue) CurrentWeapon = d.CurrentWeapon.Value;

            // --- Inventory ---
            if (d.HasInventory)
            {
                PrimaryWeapon = d.PrimaryWeapon!.Value;
                SecondaryWeapon = d.SecondaryWeapon!.Value;
                Grenades = d.Grenades!.Value;
                HasC4 = d.HasC4!.Value;
            }
#if DEBUG
            else
            {
                if (d.PrimaryWeapon.HasValue ||
                    d.SecondaryWeapon.HasValue ||
                    d.Grenades.HasValue ||
                    d.HasC4.HasValue)
                {
                    Debug.Fail("Partial inventory delta detected");
                }
            }
#endif

            if (d.Items.HasValue) Items = d.Items.Value;

            // --- Identity ---
            if (d.Name != null) Name = d.Name;
        }

        public override string ToString()
        {
            return $"Name: {Name}" +
                $"\nTeam: {Team}" +
                $"\nMoney: {Money}" +
                $"\nHp: {Hp}" +
                $"\nArmor: {ArmorType} ({ArmorValue})" +
                $"\nCur Weapon: {CurrentWeapon}" +
                $"\nPrimary: {PrimaryWeapon}" +
                $"\nSecondary: {SecondaryWeapon}" +
                $"\nGrenades: {Grenades}" +
                $"\n{(Team == Team.Terrorist ? $"C4: {HasC4}" : $"Items: {Items}")}" +
                $"\nFrags: {Frags}/{Deaths}" +
                $"\nPosition: ({X},{Y},{Z})" +
                $"\nYaw (H angle): {Yaw}" +
                $"";
        }
    }

    abstract class GameEvent
    {
        public EventType Type;
    }

    sealed class RoundEndedEvent : GameEvent
    {
        public WinStatus Status;
        public RoundEndReason Reason;

        public RoundEndedEvent()
        {
            Type = EventType.ROUND_ENDED;
        }
    }

    sealed class DeathEvent : GameEvent
    {
        public byte KillerId;
        public byte VictimId;
        public byte? AssistantId;
        public WeaponId Weapon;
        public KillRarity? Rarity;

        public DeathEvent()
        {
            Type = EventType.DIED;
        }
    }

    class Snapshot
    {
        public float Tick;

        public GlobalDelta? GlobalDelta;
        public List<PlayerDelta>? PlayersDelta;
        public List<GameEvent>? Events;

    }

    static class SnapshotJsonWriter
    {
        public static void Write(Utf8JsonWriter w, Snapshot snapshot)
        {
            w.WriteStartObject();

            w.WriteNumber("tick", snapshot.Tick);

            if (snapshot.GlobalDelta != null)
                WriteGlobalDelta(w, snapshot.GlobalDelta);

            if (snapshot.PlayersDelta != null && snapshot.PlayersDelta.Count > 0)
                WritePlayersDelta(w, snapshot.PlayersDelta);

            if (snapshot.Events != null && snapshot.Events.Count > 0)
                WriteEvents(w, snapshot.Events);

            w.WriteEndObject();
        }

        static void WriteGlobalDelta(Utf8JsonWriter w, GlobalDelta g)
        {
            w.WritePropertyName("global");
            w.WriteStartObject();

            var flags = g.Flags;

            if ((flags & GlobalFlags.ROUND_TIME) != 0)
                w.WriteNumber("rt", g.RoundEndTick!.Value);

            if ((flags & GlobalFlags.SCORE) != 0)
            {
                w.WriteNumber("t", g.TScore!.Value);
                w.WriteNumber("ct", g.CTScore!.Value);
            }

            if ((flags & GlobalFlags.MAP) != 0)
                w.WriteString("map", g.Map);

            w.WriteEndObject();
        }

        static void WritePlayersDelta(Utf8JsonWriter w, List<PlayerDelta> players)
        {
            w.WritePropertyName("players");
            w.WriteStartArray();

            foreach (var p in players)
            {
                w.WriteStartObject();

                w.WriteNumber("id", p.Id);

                var flags = p.Flags;

                if ((flags & PlayerFlags.DROPPED) != 0)
                {
                    w.WriteBoolean("drop", true);
                    w.WriteEndObject();
                    continue;
                }

                if ((flags & PlayerFlags.TEAM) != 0)
                    w.WriteNumber("team", (int)p.Team!.Value);

                if ((flags & PlayerFlags.YAW) != 0)
                    w.WriteNumber("yaw", p.Yaw!.Value);

                if ((flags & PlayerFlags.POS) != 0)
                {
                    var pos = p.Pos!.Value;
                    w.WriteStartArray("pos");
                    w.WriteNumberValue(pos.x);
                    w.WriteNumberValue(pos.y);
                    w.WriteNumberValue(pos.z);
                    w.WriteEndArray();
                }

                if ((flags & PlayerFlags.HP) != 0)
                    w.WriteNumber("hp", p.Hp!.Value);

                if ((flags & PlayerFlags.ARMOR) != 0)
                {
                    var a = p.Armor!.Value;
                    w.WriteNumber("armorVal", a.ArmorValue);
                    w.WriteNumber("armorType", (int)a.ArmorType);
                }

                if ((flags & PlayerFlags.CURWEP) != 0)
                    w.WriteNumber("wep", (int)p.CurrentWeapon!.Value);

                if ((flags & PlayerFlags.MONEY) != 0)
                    w.WriteNumber("money", p.Money!.Value);

                if ((flags & PlayerFlags.FRAGS) != 0)
                    w.WriteNumber("frags", p.Frags!.Value);

                if ((flags & PlayerFlags.DEATHS) != 0)
                    w.WriteNumber("deaths", p.Deaths!.Value);

                if ((flags & PlayerFlags.INV) != 0)
                {
                    w.WriteNumber("pwep", (int)p.PrimaryWeapon!.Value);
                    w.WriteNumber("swep", (int)p.SecondaryWeapon!.Value);
                    w.WriteNumber("gren", (int)p.Grenades!.Value);
                    w.WriteBoolean("c4", p.HasC4!.Value);
                }

                if ((flags & PlayerFlags.ITEMS) != 0)
                    w.WriteNumber("items", (int)p.Items!.Value);

                if ((flags & PlayerFlags.NAME) != 0)
                    w.WriteString("name", p.Name);

                w.WriteEndObject();
            }

            w.WriteEndArray();
        }

        static void WriteEvents(Utf8JsonWriter w, List<GameEvent> events)
        {
            w.WritePropertyName("events");
            w.WriteStartArray();

            foreach (var e in events)
            {
                w.WriteStartObject();

                w.WriteNumber("type", (int)e.Type);

                switch (e)
                {
                    case DeathEvent d:
                        w.WriteNumber("k", d.KillerId);
                        w.WriteNumber("v", d.VictimId);

                        if (d.AssistantId.HasValue)
                            w.WriteNumber("a", d.AssistantId.Value);

                        w.WriteNumber("w", (int)d.Weapon);

                        if (d.Rarity.HasValue)
                            w.WriteNumber("r", (int)d.Rarity.Value);

                        break;

                    case RoundEndedEvent r:
                        w.WriteNumber("s", (int)r.Status);
                        w.WriteNumber("r", (int)r.Reason);
                        break;
                }

                w.WriteEndObject();
            }

            w.WriteEndArray();
        }
    }

    record DelayedPacket(byte[] Data, DateTime SendAt);

    class Program
    {
        const bool DEBUG_VERBOSE = false;

        const int MAX_WEAPONS = 32 - 1; // Ignore shield.
        public const int MAX_PLAYERS = 32;

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

        static volatile float currentTick = 0;
        static readonly PlayerState?[] players = new PlayerState[MAX_PLAYERS + 1];

        static readonly Channel<Snapshot> channel =
        Channel.CreateBounded<Snapshot>(new BoundedChannelOptions(32)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = true
        });

        static void Log(string? message)
        {
            if (DEBUG_VERBOSE)
            {
                Console.WriteLine(message);
            }
        }


        static readonly ConcurrentQueue<DelayedPacket> queue = new();
        static readonly TimeSpan delay = TimeSpan.FromSeconds(
            Environment.OSVersion.Platform == PlatformID.Unix
            ? 30
            : 0
            );

        static readonly List<WebSocket> clients = [];
        static readonly Lock clientsLock = new();

        static async Task Main()
        {
            var udpLoop = Task.Run(GameServerListener);
            var delayedPktLoop = Task.Run(DelayedPacketProcessor);
            var wsLoop = Task.Run(WebSocketServer);
            var bcLoop = Task.Run(BroadcastLoop);

            await Task.WhenAll(udpLoop, delayedPktLoop);
        }

        static async Task GameServerListener()
        {
            int port = 37015;
            using UdpClient udp = new(port);
            Console.WriteLine($"Listening on UDP port {port}...");

            IPEndPoint remoteEndPoint = new(IPAddress.Any, 0);

            while (true)
            {
                var result = await udp.ReceiveAsync();

                queue.Enqueue(new DelayedPacket(
                    result.Buffer,
                    DateTime.UtcNow + delay
                ));
            }
        }

        static async Task DelayedPacketProcessor()
        {
            while (true)
            {
                while (queue.TryPeek(out var pkt))
                {
                    if (DateTime.UtcNow < pkt.SendAt)
                        break;

                    if (queue.TryDequeue(out pkt))
                    {
                        ProcessPacket(pkt.Data);
                    }
                }

                await Task.Delay(1);
            }
        }

        static async Task WebSocketServer()
        {
            // The same for now. Might change later.
            string ws;
            if (Environment.OSVersion.Platform == PlatformID.Unix)
                ws = "http://localhost:5000/ws/";
            else
                ws = "http://localhost:5000/ws/";

            HttpListener listener = new();
            listener.Prefixes.Add(ws);
            listener.Start();
            Log($"Listening on WebSocket ws{ws[4..]}");

            while (true)
            {
                var context = await listener.GetContextAsync();

                if (!context.Request.IsWebSocketRequest)
                {
                    context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                    context.Response.Close();
                    continue;
                }

                _ = HandleClient(context);
            }
        }

        static async Task BroadcastLoop()
        {
            var buffer = new ArrayBufferWriter<byte>(4096);

            await foreach (var snapshot in channel.Reader.ReadAllAsync())
            {
                buffer.Clear();

                using (var writer = new Utf8JsonWriter(buffer))
                {
                    SnapshotJsonWriter.Write(writer, snapshot);
                    writer.Flush();
                }

                var payload = buffer.WrittenMemory;

                List<WebSocket> clientsSnapshot;
                lock (clientsLock)
                    clientsSnapshot = [.. clients];

                if (clientsSnapshot.Count == 0)
                    continue;

                foreach (var ws in clientsSnapshot)
                {
                    if (ws.State == WebSocketState.Open)
                    {
                        _ = ws.SendAsync(
                            payload,
                            WebSocketMessageType.Text,
                            endOfMessage: true,
                            CancellationToken.None
                        ).AsTask().ContinueWith(t =>
                        {
                            if (t.IsFaulted || ws.State != WebSocketState.Open)
                            {
                                lock (clientsLock)
                                    clients.Remove(ws);

#if DEBUG
                                Console.WriteLine($"Broadcast failed for ws. Client dropped.");
#endif
                            }
#if DEBUG
                            else
                            {
                                Console.WriteLine($"Broadcast sent to client ...");
                            }
#endif
                        });
                    }
                }
            }
        }

        static void ProcessPacket(byte[] data)
        {
            // Structure:
            //
            // [game time]
            //   (optional) [G][global flags][data]
            //   (optional) [P][count][id][flags][data][id][flags][data][id][flags][data]...
            //   (optional) [E][count][type][data][type][data][type][data][type][data]...
            //
            // game time: 4 bytes (f32 - 4 bytes float);
            // packet tag: 1 byte (G, P or E) (u8);
            //
            // [G] packet
            // [G][global flags][data]
            // global flags: 1 byte (u8);
            // global flags data size:
            //  -ROUND_TIME = 4 bytes (f32 - 4 bytes float);
            //  -SCORE      = 2 bytes (u16) (t score, ct score);
            //  -MAP        = first byte is name length (u8);
            //
            // [P] packet
            // [P][count][id][flags][data][id][flags][data][id][flags][data]...
            // count: 1 byte (u8);
            // player id: 1 byte (u8);
            // player flags: 2 bytes (u16);
            // player flags data sizes:
            //  -TEAM   = 1 byte (u8);
            //  -POS    = 6 bytes (3 * i16, signed);
            //  -YAW    = 1 byte (i8, signed [-127, +127]);
            //  -HP     = 1 byte (u8);
            //  -ARMOR  = 1 byte (u8);
            //  -CURWEP = 1 byte (u8);
            //  -MONEY  = 2 bytes (u16);
            //  -FRAGS  = 1 byte (i8, signed);
            //  -DEATHS = 1 byte (u8);
            //  -INV    = 4 bytes (u32);
            //  -ITEMS  = 1 byte (u8);
            //  -NAME   = first byte is name length (u8);
            //
            // [E] packet
            // [E][count][type][data][type][data][type][data][type][data]...
            // count: 1 byte (u8);
            // type: 1 byte (u8);
            // data sizes:
            //  -ROUND_ENDED         = 1 byte (u8);
            //  -DIED                = 4 bytes (u32);
            //  -BOMB_PLANTING       = 1 byte (u8);
            //  -BOMB_PLANT_ABORTED  = 1 byte (u8);
            //  -BOMB_PLANTED        = 1 byte (u8);
            //  -BOMB_DROPPED        = 8 bytes (1 byte for id + 3 * i16 for (x,y,z));
            //  -BOMB_PICKED_UP      = 1 byte (u8);
            //  -BOMB_DEFUSING       = 1 byte (u8);
            //  -BOMB_DEFUSE_ABORTED = 1 byte (u8); 
            //  -BOMB_DEFUSED        = 1 byte (u8);
            //  -BOMB_EXPLODED       = 1 byte (u8);
            //  -FLASHED             = 1 byte (u8);
            //  -KILL_FLASHBANGED    = 1 byte (u8);

            if (data.Length == 0) return;

            var snapshot = new Snapshot();

            var reader = new PacketReader(data);
            currentTick = snapshot.Tick = reader.ReadF32();

#if DEBUG
            Console.WriteLine($"=====<[TICK][{snapshot.Tick:F2}]>=====\n");
#endif

            while (reader.Remaining > 0)
            {
                PacketType tag = (PacketType)reader.ReadU8();

                switch (tag)
                {
                    case PacketType.PCKT_GLOBAL:
                        snapshot.GlobalDelta = ProcessGlobalPacket(ref reader);
                        break;

                    case PacketType.PCKT_PLAYERS:
                        snapshot.PlayersDelta = ProcessPlayersPacket(ref reader);
                        break;

                    case PacketType.PCKT_EVENTS:
                        snapshot.Events = ProcessEventsPacket(ref reader);
                        break;
                }
            }

            ApplySnapshot(snapshot);

            channel.Writer.TryWrite(snapshot);

#if DEBUG
            Console.WriteLine($"\n=====</[TICK][{snapshot.Tick:F2}]>=====");
#endif
        }

        private static GlobalDelta ProcessGlobalPacket(ref PacketReader pReader)
        {
            // [G] packet
            // [G][global flags][data]
            // global flags: 1 byte (u8);
            // global flags data size:
            //  -ROUND_TIME = 4 bytes (f32 - 4 bytes float);
            //  -SCORE      = 2 bytes (2 * u8) (t score, ct score);
            //  -MAP        = first byte is name length (u8);

            var flags = pReader.ReadFlags<GlobalFlags>();
            var fReader = new FlagReader<GlobalFlags>(flags);

            var delta = new GlobalDelta
            {
                Flags = flags
            };

#if DEBUG
            var logStr = new StringBuilder(1024);
            logStr.AppendLine($"|--[tag:GLOBAL][flags:{flags}]");
#endif

            while (fReader.Next(out GlobalFlags flag))
            {
                switch (flag)
                {
                    case GlobalFlags.ROUND_TIME:

                        float rt = pReader.ReadF32();
                        delta.RoundEndTick = rt;

#if DEBUG
                        logStr.AppendLine($"  |--[ROUND END TICK <{rt:F2}>]");
#endif
                        break;

                    case GlobalFlags.SCORE:

                        var tScore = pReader.ReadU8();
                        var ctScore = pReader.ReadU8();
                        delta.TScore = tScore;
                        delta.CTScore = ctScore;

#if DEBUG
                        logStr.AppendLine($"  |--[SCORE <T {tScore} x {ctScore} CT>]");
#endif
                        break;

                    case GlobalFlags.MAP:

                        string name = pReader.ReadString();
                        delta.Map = name;

#if DEBUG
                        logStr.AppendLine($"  |--[MAP <{name}>]");
#endif
                        break;

                    default:
                        throw new Exception($"Unknown global flag: {flag}");
                }
            }

#if DEBUG
            Console.Write(logStr);
#endif

            return delta;
        }

        private static List<PlayerDelta> ProcessPlayersPacket(ref PacketReader pReader)
        {
            // [P] packet
            // [P][count][id][flags][data][id][flags][data][id][flags][data]...
            // count: 1 byte (u8);

            byte numPlayers = pReader.ReadU8();
            var deltas = new List<PlayerDelta>(numPlayers);

#if DEBUG
            var logStr = new StringBuilder(1024);
            logStr.AppendLine($"|--[tag:PLAYERS][count:{numPlayers}]");
#endif

            for (int i = 0; i < numPlayers; i++)
            {
                var delta = new PlayerDelta();

                // player id: 1 byte (u8);
                byte playerId = pReader.ReadU8();
                delta.Id = playerId;

                // player flags: 2 bytes (u16);
                // TEAM   = 1 byte (u8);
                // POS    = 6 bytes (3 * i16, signed);
                // YAW    = 1 byte (i8, signed [-127, +127]);
                // HP     = 1 byte (u8);
                // ARMOR  = 1 byte (u8);
                // CURWEP = 1 byte (u8);
                // MONEY  = 2 bytes (u16);
                // FRAGS  = 1 byte (i8, signed);
                // DEATHS = 1 byte (u8);
                // INV    = 4 bytes (u32);
                // ITEMS  = 1 byte (u8);
                // NAME   = first byte is name length (u8);
                var flags = pReader.ReadFlags<PlayerFlags>();
                var fReader = new FlagReader<PlayerFlags>(flags);

                delta.Flags = flags;

#if DEBUG
                var p = players[playerId];
                string theName = p?.Name ?? "";

                logStr.AppendLine($"  |--[id <{playerId}>{(theName.Length > 0 ? $" ({theName})" : $"")}]"
                    + $"[flags:{flags}]");
#endif

                while (fReader.Next(out PlayerFlags flag))
                {
                    switch (flag)
                    {
                        case PlayerFlags.TEAM:

                            Team team = (Team)pReader.ReadU8();
                            delta.Team = team;

#if DEBUG
                            logStr.AppendLine($"    |--[TEAM <{team}>]");
#endif
                            break;

                        case PlayerFlags.POS:

                            short x = pReader.ReadI16();
                            short y = pReader.ReadI16();
                            short z = pReader.ReadI16();

                            delta.Pos = (x, y, z);

#if DEBUG
                            logStr.AppendLine($"    |--[POS <({x},{y},{z})>]");
#endif
                            break;

                        case PlayerFlags.YAW:

                            sbyte encodedYaw = pReader.ReadI8();

                            var decodedYaw = encodedYaw * 180f / 127f;
                            delta.Yaw = decodedYaw;

#if DEBUG
                            logStr.AppendLine($"    |--[YAW <{decodedYaw:F2}º>]");
#endif
                            break;

                        case PlayerFlags.HP:

                            var hp = pReader.ReadI8();

                            // hp may actually be negative sometimes because
                            // the snapshot might capture the overkill value,
                            // such as hp of a player that had 30 and took
                            // a damage greater than 30.
                            delta.Hp = hp;

#if DEBUG
                            logStr.AppendLine($"    |--[HP <{hp}>]");
#endif
                            break;

                        case PlayerFlags.ARMOR:

                            // First bit (8th) holds armor type:
                            // 0 = vest; 1 = vesthelm;
                            // Other 7 bits hold armor value.
                            byte armor = pReader.ReadU8();

                            byte armorValue = (byte)(armor & 0b0111_1111);
                            ArmorType armorType = armorValue > 0 ? (ArmorType)(armor >> 7) : ArmorType.None;

                            delta.Armor = (armorType, armorValue);

#if DEBUG
                            logStr.AppendLine($"    |--[ARMOR <{armorType}/{armorValue}>]");
#endif
                            break;

                        case PlayerFlags.CURWEP:

                            var cw = (WeaponId)pReader.ReadU8();
                            delta.CurrentWeapon = cw;

#if DEBUG
                            logStr.AppendLine($"    |--[CURWEP <{cw}>]");
#endif
                            break;

                        case PlayerFlags.MONEY:

                            ushort money = pReader.ReadU16();
                            delta.Money = money;

#if DEBUG
                            logStr.AppendLine($"    |--[MONEY <{money}>]");
#endif
                            break;

                        case PlayerFlags.FRAGS:

                            sbyte frags = pReader.ReadI8();
                            delta.Frags = frags;

#if DEBUG
                            logStr.AppendLine($"    |--[FRAGS <{frags}>]");
#endif
                            break;

                        case PlayerFlags.DEATHS:

                            byte deaths = pReader.ReadU8();
                            delta.Deaths = deaths;

#if DEBUG
                            logStr.AppendLine($"    |--[DEATHS <{deaths}>]");
#endif

                            break;

                        case PlayerFlags.INV:

                            uint bitmask = pReader.ReadU32();

                            delta.PrimaryWeapon = WeaponId.NONE;
                            delta.SecondaryWeapon = WeaponId.NONE;
                            delta.Grenades = Grenades.None;
                            delta.HasC4 = false;

#if DEBUG
                            var invBuilder = new StringBuilder();

                            for (int wid = 0; wid < MAX_WEAPONS; wid++)
                            {
                                if ((bitmask & (1u << wid)) != 0)
                                {
                                    if (invBuilder.Length > 0)
                                        invBuilder.Append(", ");

                                    invBuilder.Append((WeaponId)wid);
                                }
                            }

                            logStr.AppendLine($"    |--[INV <{invBuilder}>]");
#endif

                            while (bitmask != 0)
                            {
                                // Index of first set bit from right to left.
                                // This returns the number of bits 0 to the right
                                // of the first set bit.
                                // 0100 0000 -> return 6, which is the index of the
                                // first set bit (0-indexed).
                                // The index of the set bit is the index of the
                                // corresponding weapon (bit 29 set = player has a knife).
                                byte bitIdx = (byte)BitOperations.TrailingZeroCount(bitmask);

                                // bitmask - 1 will turn the set bit to 0,
                                // and all other bits to right to 1.
                                // ANDing this number with the original bitmask
                                // (which is 1 on the bit index and all 0 to the right)
                                // will result in zeroing the current set bit along with
                                // everything else to the right of it.
                                //     mask = 1100 0000
                                // mask - 1 = 1011 1111
                                //      AND = 1000 0000 <- next time the set bit index
                                //                         will be the next possessed WP
                                bitmask &= bitmask - 1;

                                switch (WeaponsSlot[bitIdx])
                                {
                                    case WeaponSlot.Primary:
                                        delta.PrimaryWeapon = (WeaponId)bitIdx;
                                        break;
                                    case WeaponSlot.Secondary:
                                        delta.SecondaryWeapon = (WeaponId)bitIdx;
                                        break;
                                    case WeaponSlot.Grenade:
                                        switch ((WeaponId)bitIdx)
                                        {
                                            case WeaponId.HEGRENADE:
                                                delta.Grenades |= Grenades.HE;
                                                break;
                                            case WeaponId.FLASHBANG:
                                                delta.Grenades |= Grenades.FLASH;
                                                break;
                                            case WeaponId.SMOKEGRENADE:
                                                delta.Grenades |= Grenades.SMOKE;
                                                break;
                                        }
                                        break;
                                    case WeaponSlot.C4:
                                        delta.HasC4 = true;
                                        break;
                                }
                            }

                            break;

                        case PlayerFlags.ITEMS:

                            bool hasKit = pReader.ReadU8() == 1;
                            delta.Items = hasKit ? ItemsHeld.DefuseKit : ItemsHeld.None;

#if DEBUG
                            logStr.AppendLine($"    |--[ITEMS <{(hasKit ? "has defuse" : "no defuse")}>]");
#endif
                            break;

                        case PlayerFlags.NAME:

                            string name = pReader.ReadString();
                            delta.Name = name;

#if DEBUG
                            logStr.AppendLine($"    |--[NAME <{name}>]");
#endif
                            break;

                        case PlayerFlags.DROPPED:

                            delta.Dropped = true;

#if DEBUG
                            logStr.AppendLine($"    |--[DROPPED]");
#endif
                            break;

                        default:
                            throw new Exception($"Unknown player flag: {flag}");
                    }
                }

                deltas.Add(delta);
            }

#if DEBUG
            Console.Write(logStr);
#endif

            return deltas;

        }

        private static List<GameEvent> ProcessEventsPacket(ref PacketReader pReader)
        {
            // [E] packet
            // [E][count][type][data][type][data][type][data][type][data]...
            // count: 1 byte (u8);

            byte numEvents = pReader.ReadU8();
            var events = new List<GameEvent>(numEvents);

#if DEBUG
            var logStr = new StringBuilder(1024);
            logStr.AppendLine($"|--[tag:EVENTS][count:{numEvents}]");
#endif

            for (int i = 0; i < numEvents; i++)
            {
                // ROUND_ENDED         = 1 byte (u8);
                // DIED                = 4 bytes (u32);
                // BOMB_PLANTING       = 1 byte (u8);
                // BOMB_PLANT_ABORTED  = 1 byte (u8);
                // BOMB_PLANTED        = 1 byte (u8);
                // BOMB_DROPPED        = 8 bytes (1 byte for id + 3 * i16 for (x,y,z));
                // BOMB_PICKED_UP      = 1 byte (u8);
                // BOMB_DEFUSING       = 1 byte (u8);
                // BOMB_DEFUSE_ABORTED = 1 byte (u8); 
                // BOMB_DEFUSED        = 1 byte (u8);
                // BOMB_EXPLODED       = 1 byte (u8);
                // FLASHED             = 1 byte (u8);
                // KILL_FLASHBANGED    = 1 byte (u8);
                var type = (EventType)pReader.ReadU8();

                switch (type)
                {
                    case EventType.ROUND_ENDED:

                        var roundEndInfo = pReader.ReadU8();

                        // Who won/status.
                        // First 2 bits.
                        var status = (WinStatus)(roundEndInfo & 0x03);

                        // Reason why/how it ended.
                        // Remaining 6 bits (only 5 actually used for now).
                        var reason = (RoundEndReason)((roundEndInfo >> 2) & 0x3F);

                        events.Add(new RoundEndedEvent
                        {
                            Status = status,
                            Reason = reason,
                        });

#if DEBUG
                        logStr.AppendLine($"  |--[ROUND ENDED status:<{status}> reason:<{reason}>]");
#endif

                        break;

                    case EventType.DIED:

                        var deathInfo = pReader.ReadU32();

                        // killer id    = 6 bits (0 to 32)
                        // victim id    = 5 bits (0 to 31 -> id - 1)
                        // assistant id = 6 bits (0 means no assist, 1 to 32 the id)
                        // weapon id    = 5 bits
                        // rarity       = 10 bits (KillRarity has 10 possible flags)
                        // total        = 32 bits = 4 bytes.
                        byte killer = (byte)(deathInfo & 0x3F);
                        byte victim = (byte)(((deathInfo >> 6) & 0x1F) + 1);
                        byte assistant = (byte)((deathInfo >> 11) & 0x3F);
                        var weapon = (WeaponId)((deathInfo >> 17) & 0x1F);
                        var rarity = (KillRarity)((deathInfo >> 22) & 0x3FF);

                        var de = new DeathEvent
                        {
                            KillerId = killer,
                            VictimId = victim,
                            Weapon = weapon,
                        };

                        if (assistant != 0)
                            de.AssistantId = assistant;

                        if (rarity != KillRarity.NONE)
                            de.Rarity = rarity;

                        events.Add(de);

#if DEBUG
                        logStr.AppendLine($"  |--[DIED k:<{killer}> v:<{victim}> assist:<{assistant}> wep:<{weapon}> rarity:<{rarity}>]");
#endif
                        break;

                    case EventType.BOMB_PLANTING:
                        break;
                    case EventType.BOMB_PLANT_ABORTED:
                        break;
                    case EventType.BOMB_PLANTED:
                        break;
                    case EventType.BOMB_DROPPED:
                        break;
                    case EventType.BOMB_PICKED_UP:
                        break;
                    case EventType.BOMB_DEFUSING:
                        break;
                    case EventType.BOMB_DEFUSE_ABORTED:
                        break;
                    case EventType.BOMB_DEFUSED:
                        break;
                    case EventType.BOMB_EXPLODED:
                        break;
                    case EventType.FLASHED:
                        break;
                    case EventType.KILL_FLASHBANGED:
                        break;
                    default:
                        break;
                }

            }

#if DEBUG
            Console.Write(logStr);
#endif

            return events;
        }

        static void ApplySnapshot(Snapshot snapshot)
        {
            if (snapshot.PlayersDelta == null)
                return;

            foreach (var delta in snapshot.PlayersDelta)
            {
                if (delta.Dropped == true)
                {
                    players[delta.Id] = null;
                    continue;
                }

                var player = players[delta.Id];

                if (player == null)
                {
                    player = new PlayerState(delta.Id);
                    players[delta.Id] = player;
                }

                player.Apply(delta);
            }
        }

        static async Task HandleClient(HttpListenerContext context)
        {
            var wsContext = await context.AcceptWebSocketAsync(null);
            var socket = wsContext.WebSocket;

            lock (clientsLock)
            {
                clients.Add(socket);
                Log($"Client connected (total = {clients.Count})");
            }

            // Receive loop (messages from the frontend).
            var buffer = new byte[1024];

            try
            {
                while (socket.State == WebSocketState.Open)
                {
                    var result = await socket.ReceiveAsync(
                        new ArraySegment<byte>(buffer),
                        CancellationToken.None
                    );

                    if (result.MessageType == WebSocketMessageType.Close)
                        break;

                    if (result.MessageType == WebSocketMessageType.Text)
                    {
                        string json = Encoding.UTF8.GetString(
                            buffer,
                            0,
                            result.Count
                        );

#if DEBUG
                        Log($"[WS RECEIVED] {json}");
#endif

                        using var doc = JsonDocument.Parse(json);

                        var root = doc.RootElement;

                        if (root.TryGetProperty("type", out var typeEl))
                        {
                            string? type = typeEl.GetString();

                            if (type == "full_state")
                            {
                                await SendFullState(socket);

#if DEBUG
                                Log($"[WS RESPONSE] Sent full-state to client.");
#endif
                            }
                        }
                    }
                }
            }
            finally
            {
                lock (clientsLock)
                {
                    clients.Remove(socket);
                    Log($"Client disconnected (total = {clients.Count})");
                }
            }
        }

        static async Task SendFullState(WebSocket socket)
        {
            var buffer = new ArrayBufferWriter<byte>(8192);

            using (var w = new Utf8JsonWriter(buffer))
            {
                w.WriteStartObject();

                w.WriteNumber("tick", currentTick);

                w.WritePropertyName("players");

                w.WriteStartArray();

                for (int i = 1; i < players.Length; i++)
                {
                    var p = players[i];

                    if (p == null)
                        continue;

                    w.WriteStartObject();

                    w.WriteNumber("id", p.Id);

                    w.WriteNumber("team", (byte)p.Team);

                    w.WriteNumber("yaw", p.Yaw);

                    w.WriteStartArray("pos");
                    w.WriteNumberValue(p.X);
                    w.WriteNumberValue(p.Y);
                    w.WriteNumberValue(p.Z);
                    w.WriteEndArray();

                    w.WriteNumber("hp", p.Hp);

                    w.WriteNumber("armorVal", p.ArmorValue);
                    w.WriteNumber("armorType", (int)p.ArmorType);

                    w.WriteNumber("wep", (int)p.CurrentWeapon);

                    w.WriteNumber("money", p.Money);

                    w.WriteNumber("frags", p.Frags);
                    w.WriteNumber("deaths", p.Deaths);

                    w.WriteNumber("pwep", (int)p.PrimaryWeapon);
                    w.WriteNumber("swep", (int)p.SecondaryWeapon);
                    w.WriteNumber("gren", (int)p.Grenades);
                    w.WriteBoolean("c4", p.HasC4);

                    w.WriteNumber("items", (byte)p.Items);

                    w.WriteString("name", p.Name);

                    w.WriteEndObject();
                }

                w.WriteEndArray();

                w.WriteEndObject();

                w.Flush();
            }

            await socket.SendAsync(
                buffer.WrittenMemory,
                WebSocketMessageType.Text,
                endOfMessage: true,
                CancellationToken.None
            );
        }

        static string FlagsToString<T>(T flags) where T : struct, Enum
        {
            uint raw = Unsafe.SizeOf<T>() switch
            {
                1 => Unsafe.As<T, byte>(ref flags),
                2 => Unsafe.As<T, ushort>(ref flags),
                4 => Unsafe.As<T, uint>(ref flags),
                _ => throw new NotSupportedException()
            };

            var values = Enum.GetValues<T>();
            var sb = new StringBuilder();
            bool first = true;

            for (int i = 0; i < values.Length; i++)
            {
                var v = values[i];

                uint mask = Unsafe.SizeOf<T>() switch
                {
                    1 => Unsafe.As<T, byte>(ref v),
                    2 => Unsafe.As<T, ushort>(ref v),
                    4 => Unsafe.As<T, uint>(ref v),
                    _ => throw new NotSupportedException()
                };

                if ((raw & mask) != 0)
                {
                    if (!first)
                        sb.Append('|');

                    sb.Append(v.ToString());
                    first = false;
                }
            }

            return sb.ToString();
        }
    }
}
