#define DEBUG_STAT

using Fleck;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Numerics;
using System.Text;
using System.Text.Json;

namespace UdpReceiver
{
    partial class Program
    {
        static readonly Dictionary<ulong, Server> AuthorizedServers = InitServers();

        static readonly ConcurrentDictionary<IWebSocketConnection, ulong> ConnectedClients = new();

        static readonly ConcurrentDictionary<ulong, ServerQuery> OnlineServers =
            new(AuthorizedServers.Count, AuthorizedServers.Count);

        static async Task Main()
        {
            var cts = new CancellationTokenSource();

            // TODO - implement real time reload.
            // This will require atomic config manipulation
            // not simply swapping the references because this brakes
            // the current references to each Server that is bring processed.
            // Idea: add a ServerConfig to ServerContext and build this
            // object and swap it on reload.
            // For now, just restart the app after config.json changes.
            //var cfgLoadTask = Task.Run(async () =>
            //{
            //    while (!cts.Token.IsCancellationRequested)
            //    {
            //        await Task.Delay(60 * 1000, cts.Token);
            //        AuthorizedServers = InitServers();
            //    }
            //}, cts.Token);

            var udpLoop = Task.Run(() =>
            {
                GameServerListener();
            });

            WebSocketServer();

            await Task.WhenAll(udpLoop);
        }

        static void GameServerListener()
        {
            int port = 37015;

            Socket socket = new(
                AddressFamily.InterNetwork,
                SocketType.Dgram,
                ProtocolType.Udp);

            socket.Bind(new IPEndPoint(IPAddress.Any, port));

            Log($"==== INITIATING LOG SESSION ====");
            Log($"Listening on UDP port {port}...");

            EndPoint remote = new IPEndPoint(IPAddress.Any, 0);

            var vpnHostIp = IPAddress.Parse("187.77.240.176");
            var vpnHostEp = new IPEndPoint(vpnHostIp, 0);

#if DEBUG_STAT
            LogDebug($"==== INITIATING LOG ====");
            LogDebug(
                $"COUNT" +
                $"\tRECEIVED" +
                $"\tAVG" +
                $"\tMIN" +
                $"\tMAX" +
                $"\t<=64" +
                $"\t<=128" +
                $"\t<=256" +
                $"\t>256" +
                $"\tPKT RCVD SINCE LAST"
            );
#endif

            HashSet<ulong> serverProcessorStarted = new(AuthorizedServers.Count);

            while (true)
            {
                byte[] recvBuffer = ArrayPool<byte>.Shared.Rent(1024);

                try
                {
                    int received = socket.ReceiveFrom(
                        recvBuffer,
                        SocketFlags.None,
                        ref remote);

                    // Make sure we have at least 2 bytes for the server port.
                    if (received < 2)
                    {
                        ArrayPool<byte>.Shared.Return(recvBuffer);
                        continue;
                    }

                    var ep = (IPEndPoint)remote;

                    if (IPAddress.IsLoopback(ep.Address)
                        && OperatingSystem.IsLinux())
                    {
                        ep = vpnHostEp;
                    }

                    var svPort = BinaryPrimitives.ReadUInt16LittleEndian(recvBuffer);
                    var svId = Server.BuildIdFromIPv4AndPort(ep.Address, svPort);

                    if (!AuthorizedServers.TryGetValue(svId, out var server))
                    {
                        ArrayPool<byte>.Shared.Return(recvBuffer);
                        Log($"IP not authorized: {ep.Address}");
                        Log($"\t|-Connection attempted: {ep.Address}:{svPort} (id = {svId})");
#if DEBUG
                        Log($"\t|-Available: ");

                        var strs = AuthorizedServers.Select(x => $"\t\t|-<{x.Value.Tag}> ({x.Value.IP}:{x.Value.Port})");
                        foreach (var item in strs)
                        {
                            Log(item);
                        }
#endif

                        continue;
                    }                    

                    if (svPort != server.Port)
                    {
                        ArrayPool<byte>.Shared.Return(recvBuffer);
                        Log($"Port does not match IP {ep.Address}:{svPort}. Expected: {server.Port}");
                        continue;
                    }

#if DEBUG_STAT
                    pcktsRcvd++;
                    bytesRcvd += (uint)received;
                    avgPcktSize = (float)bytesRcvd / pcktsRcvd;
                    minPktSize = received < minPktSize ? (uint)received : minPktSize;
                    maxPktSize = received > maxPktSize ? (uint)received : maxPktSize;

                    if (received <= 64)
                        h64++;
                    else if (received <= 128)
                        h128++;
                    else if (received <= 256)
                        h256++;
                    else
                        hUpper++;

                    if (DateTime.UtcNow >= nextWriteAt)
                    {
                        LogDebug(
                            $"{pcktsRcvd}" +
                            $"\t{bytesRcvd}" +
                            $"\t\t{avgPcktSize:F2}" +
                            $"\t{minPktSize}" +
                            $"\t{maxPktSize}" +
                            $"\t{h64}" +
                            $"\t{h128}" +
                            $"\t{h256}" +
                            $"\t{hUpper}" +
                            $"\t{pcktsRcvd - pcktsRcvdPrev}"
                        );

                        pcktsRcvdPrev = pcktsRcvd;
                        nextWriteAt = DateTime.UtcNow + writeInterval;
                    }
#endif

                    server.Context.PacketQueue.Enqueue(
                        new DelayedPacket
                        {
                            Buffer = recvBuffer,
                            Length = received,
                            SendAt = Time.NowPlusSeconds(server.BroadcastDelaySeconds)
                        });

                    // Start processing after first packager arrives.
                    if (!serverProcessorStarted.Contains(svId))
                    {
                        _ = Task.Run(async () => await ProcessServerQueue(server));
                        serverProcessorStarted.Add(svId);
                    }
                }
                catch (Exception ex)
                {
                    ArrayPool<byte>.Shared.Return(recvBuffer);
                    LogErr(ex);
                }
            }
        }

        static async Task ProcessServerQueue(Server server)
        {
            var queue = server.Context.PacketQueue;

            while (true)
            {
                try
                {
                    while (queue.TryPeek(out DelayedPacket pkt))
                    {
                        if (pkt.SendAt > Time.Now)
                        {
                            // Sleep until it's due.
                            int delayMs = Math.Max(
                                1,
                                (int)((pkt.SendAt - Time.Now) * 1000 / Stopwatch.Frequency)
                            );

                            await Task.Delay(delayMs);

                            continue;
                        }

                        queue.Advance();

                        try
                        {
                            // Skip 2 bytes that are the server port.
                            // The processor only expects state data.
                            var snapshot = ProcessServerPacket(pkt.Buffer.AsSpan(0, pkt.Length), server);

                            if (snapshot != null)
                            {
                                server.Context.State.ApplySnapshot(snapshot);
                                _ = Broadcast(server, snapshot);

                                // TODO - recording feat
                                //if (server.Context.IsRecording)
                                //{
                                //    server.Context.ReplayQueue.Enqueue(
                                //        new ReplayPacket
                                //        {
                                //            Buffer = recvBuffer,
                                //            Length = received,
                                //            Timestamp = Stopwatch.GetTimestamp()
                                //        });
                                //}
                            }
                        }
                        finally
                        {
                            pkt.Release();
                        }
                    }

                    // Queue is empty.
                    await queue.AwaitPacket();

                }
                catch (Exception ex)
                {
                    LogErr(ex);
                }
            }
        }

        static Snapshot? ProcessServerPacket(Span<byte> data, Server server)
        {
            // General structure:
            // [server port][game time][G][global flags][data][P][count][FlashedId][flags][data][E][count][type][data]
            //
            // [server port]
            //  port: 2 bytes (u16);
            //
            // [game time]
            //   (optional) [G][global flags][data]
            //   (optional) [P][count][FlashedId][flags][data][FlashedId][flags][data][FlashedId][flags][data]...
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
            // [P][count][FlashedId][flags][data][FlashedId][flags][data][FlashedId][flags][data]...
            // count: 1 byte (u8);
            // player FlashedId: 1 byte (u8);
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
            //  -BOMB_PLANTING       = 1 byte (u8);
            //  -BOMB_PLANT_ABORTED  = 1 byte (u8);
            //  -BOMB_PLANTED        = 8 bytes (6 bits for id + 10 bits for tick delta + 3 * i16 for (x,y,z));
            //  -BOMB_DROPPED        = 8 bytes (1 byte for FlashedId + 3 * i16 for (x,y,z));
            //  -BOMB_PICKED_UP      = 1 byte (1 bit to indicate whether spawned or from the ground, rest for player id);
            //  -BOMB_DEFUSING       = 1 byte (u8);
            //  -BOMB_DEFUSE_ABORTED = 1 byte (u8); 
            //  -BOMB_DEFUSED        = 1 byte (u8);
            //  -BOMB_EXPLODED       = 2 bytes (u16 - 2 ids - planter and defuser, if any);
            //  -FLASHED             = 8 bytes (6 bits for victim, 6 bit for thrower, 20 bits for event tick, 11 bits for fade time, 10 bits for hold time, 8 bits for alpha);
            //  -DIED                = 4 bytes (u32);
            //  -SAY/SAY_TEAM        = 1 byte for meta + string ([5 bits id (must +1 to decode) + 2 bits team + 1 bit dead/alive + 8 bits string len + string])

#if DEBUG
            Console.WriteLine($"Processing packet of {data.Length} bytes...");
#endif

            if (data.Length == 0) return null;

            var snapshot = new Snapshot();

            // Skip the server port header
            // as it has no use for us here.
            var reader = new PacketReader(data[2..]);

            snapshot.Tick = reader.ReadF32();

#if DEBUG
            Console.WriteLine($"=====<[TICK][{snapshot.Tick:F2}][count: {pcktsRcvd} | avg size: {avgPcktSize:F2} | bytes rcvd: {bytesRcvd}]>=====\n");
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
                        snapshot.PlayersDelta = ProcessPlayersPacket(ref reader, server);
                        break;

                    case PacketType.PCKT_EVENTS:
                        snapshot.Events = ProcessEventsPacket(ref reader);
                        break;
                }
            }

#if DEBUG
            Console.WriteLine($"=====</[TICK][{snapshot.Tick:F2}][count: {pcktsRcvd} | avg size: {avgPcktSize:F2} | bytes rcvd: {bytesRcvd}]>=====\n");
#endif

            return snapshot;
        }

        static async Task Broadcast(Server server, Snapshot snapshot)
        {
            var clients = server.Context.Clients;
            int count = clients.Length;

            var buffer = new ArrayBufferWriter<byte>(4096);
            using (var writer = new Utf8JsonWriter(buffer))
            {
                Snapshot.JsonWriter.Write(writer, snapshot);
                writer.Flush();
            }

            var payload = buffer.WrittenMemory.ToArray();

            for (int i = 0; i < count; i++)
            {
                var ws = clients[i];

                if (ws == null) continue;

                if (!ws.IsAvailable)
                {
                    server.Context.RemoveClient(ws);
                    continue;
                }

                _ = SendToClient(ws, payload, server.Context);
            }

            OnlineServers[server.Id] = server.Context.State.QueryServer();
        }

        static async Task SendToClient(IWebSocketConnection ws, byte[] payload, ServerContext? context = null)
        {
            try
            {
                await ws.Send(payload);
            }
            catch (Exception ex)
            {
                context?.RemoveClient(ws);

                Log(
                    $"Broadcast failed for client " +
                    $"{ws.ConnectionInfo.ClientIpAddress}:" +
                    $"{ws.ConnectionInfo.ClientPort}. Dropping client..."
                );

                LogErr(ex);
            }
        }

        static void WebSocketServer()
        {
            string ws = "ws://127.0.0.1:5000/ws";

            var server = new WebSocketServer(ws);

            server.Start(socket =>
            {
                socket.OnOpen = () =>
                {
                    Log($"WebScoket client {socket.ConnectionInfo.ClientIpAddress}:{socket.ConnectionInfo.ClientPort} started a connection.");
                };

                socket.OnClose = () =>
                {
                    if (ConnectedClients.TryRemove(socket, out var svId))
                    {
                        AuthorizedServers[svId].Context.RemoveClient(socket);
                        Log($"WebScoket client {socket.ConnectionInfo.ClientIpAddress}:{socket.ConnectionInfo.ClientPort} disconnected.");
                    }
                    else
                    {
                        Log($"WebScoket client {socket.ConnectionInfo.ClientIpAddress}:{socket.ConnectionInfo.ClientPort} disconnected but was not present in ConnectedClients somehow.");
                    }
                };

                socket.OnMessage = message =>
                {
                    using var doc = JsonDocument.Parse(message);
                    var root = doc.RootElement;

                    if (!root.TryGetProperty("type", out var typeEl))
                    {
                        _ = SendToClient(socket, WsJsonResponses.BadRequest);
                        return;
                    }
                    var type = typeEl.GetString();

                    if (string.IsNullOrWhiteSpace(type))
                    {
                        _ = SendToClient(socket, WsJsonResponses.BadRequest);
                        return;
                    }

                    ulong svId;
                    Server? server;

                    switch (type)
                    {
                        case "full_state":

                            Log($"WebScoket client {socket.ConnectionInfo.ClientIpAddress}:{socket.ConnectionInfo.ClientPort} requested full state");

                            if (!ConnectedClients.TryGetValue(socket, out svId))
                            {
                                // TODO - upon connecting, the client immediately starts receiving eligible server deltas,
                                // which means partial information. To avoid having half a state upon connection,
                                // clients should request a "full_state".
                                // One problem remains: there is a race condition between receiving full state
                                // and partial deltas which may cause a brief rollback/inconsistent state. However, since packets
                                // are sent fairly frequently, synchronizing this is not worth the overhead.
                                _ = SendToClient(socket, WsJsonResponses.ClientNotSubscribed);
                                return;
                            }

                            // This is defensive but should never happen because if a server
                            // does not exist, then the client could not have been subcribed to it,
                            // which means the check above would have failed.
                            if (!AuthorizedServers.TryGetValue(svId, out server))
                            {
                                _ = SendToClient(socket, WsJsonResponses.Subscribe.Failed("server s not authorized"), server?.Context);
                                Log($"[CRITICAL - SHOULD NEVER HAPPEN] WebSocket full_state request to a not registered server: {svId}");
                                break;
                            }

                            var state = server.Context.State.SerializeFullState();
                            _ = SendToClient(socket, state, server.Context);

                            break;

                        case "subscribe":

                            if (!root.TryGetProperty("server", out var svEl)
                            || !svEl.TryGetUInt64(out svId))
                            {
                                _ = SendToClient(socket, WsJsonResponses.BadRequest);
                                break;
                            }

                            if (!AuthorizedServers.TryGetValue(svId, out server))
                            {
                                _ = SendToClient(socket, WsJsonResponses.Subscribe.Failed("server Is not authorized"), server?.Context);
                                Log($"WebSocket connection to unregistered server attempted: {svId}");
                                break;
                            }

                            if (ConnectedClients.TryGetValue(socket, out var currSvId))
                            {
                                // Already subscribed.
                                if (currSvId == svId)
                                {
                                    // TODO - could the client not actually have been added to
                                    // this server's client list?

                                    // Still, we must alert the UI that we are good.
                                    // Nothing else to be done.
                                    _ = SendToClient(socket, WsJsonResponses.Subscribe.SubscribeSuccess(svId), server.Context);
                                    return;
                                }

                                // Remove client from current subscribed server.
                                AuthorizedServers[currSvId].Context.RemoveClient(socket);
                            }

                            ConnectedClients[socket] = svId;
                            server.Context.AddClient(socket);

                            _ = SendToClient(socket, WsJsonResponses.Subscribe.SubscribeSuccess(svId), server.Context);

                            Log($"WebScoket client {socket.ConnectionInfo.ClientIpAddress}:{socket.ConnectionInfo.ClientPort} connected to server {svId}.");

                            break;

                        case "list":

                            _ = SendToClient(
                                socket,
                                WsJsonResponses.ListServers.BuildJson(AuthorizedServers, OnlineServers)
                            );

                            break;
                    }

                };
            });

            Log($"Listening on WebSocket {ws}...");
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

        private static List<PlayerDelta> ProcessPlayersPacket(ref PacketReader pReader, Server server)
        {
            // [P] packet
            // [P][count][FlashedId][flags][data][FlashedId][flags][data][FlashedId][flags][data]...
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

                // player FlashedId: 1 byte (u8);
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

                var playerState = server.Context.State.GetPlayer(playerId);

#if DEBUG
                if (playerState != null && !string.IsNullOrEmpty(playerState.Name))
                {
                    logStr.AppendLine($"  |--[id <{playerId}>({playerState.Name})][flags <{flags}>]");
                }
                else
                {
                    logStr.AppendLine($"  |--[id <{playerId}>][flags <{flags}>]");
                }
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

                            // Prepare delta to reconstruct the
                            // current game server state baseline,
                            // so that we can compare it to local state
                            // and see what has actually changed.
                            delta.PrimaryWeapon = WeaponId.NONE;
                            delta.SecondaryWeapon = WeaponId.NONE;
                            delta.HasHE = delta.HasFB = delta.HasSmoke = false;
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

                                var wpId = (WeaponId)bitIdx;

                                switch (WeaponsSlot[(int)wpId])
                                {
                                    case WeaponSlot.Primary:
                                        delta.PrimaryWeapon = wpId;
                                        break;
                                    case WeaponSlot.Secondary:
                                        delta.SecondaryWeapon = wpId;
                                        break;
                                    case WeaponSlot.Grenade:
                                        switch (wpId)
                                        {
                                            case WeaponId.HEGRENADE:
                                                delta.HasHE = true;
                                                break;
                                            case WeaponId.FLASHBANG:
                                                delta.HasFB = true;
                                                break;
                                            case WeaponId.SMOKEGRENADE:
                                                delta.HasSmoke = true;
                                                break;
                                        }
                                        break;
                                    case WeaponSlot.C4:
                                        delta.HasC4 = true;
                                        break;
                                }
                            }

                            // Remove things that did not actually change from delta.
                            // This is done after the loop because the engine
                            // only sends what weapons the user has, not the ones
                            // they had or dropped, which means we must start from
                            // a "has nothing" state, parse the whole inventory,
                            // and then compare if anything actually changed.
                            if (playerState != null)
                            {
                                if (playerState.PrimaryWeapon == delta.PrimaryWeapon)
                                    delta.PrimaryWeapon = null;

                                if (playerState.SecondaryWeapon == delta.SecondaryWeapon)
                                    delta.SecondaryWeapon = null;

                                if (playerState.HasHE == delta.HasHE)
                                    delta.HasHE = null;

                                if (playerState.HasFB == delta.HasFB)
                                    delta.HasFB = null;

                                if (playerState.HasSmoke == delta.HasSmoke)
                                    delta.HasSmoke = null;

                                if (playerState.HasC4 == delta.HasC4)
                                    delta.HasC4 = null;
                            }

                            break;

                        case PlayerFlags.ITEMS:

                            var items = (ItemsHeld)pReader.ReadU8();
                            delta.HasDefuseKit = (items & ItemsHeld.DefuseKit) != 0;
                            delta.HasNightvision = (items & ItemsHeld.Nightvision) != 0;

                            if (playerState != null)
                            {
                                if (playerState.HasDefuseKit == delta.HasDefuseKit)
                                    delta.HasDefuseKit = null;

                                if (playerState.HasNightvision == delta.HasNightvision)
                                    delta.HasNightvision = null;
                            }
#if DEBUG
                            logStr.AppendLine($"    |--[ITEMS {(items.HasFlag(ItemsHeld.DefuseKit) ? "<kit>" : "")} {(items.HasFlag(ItemsHeld.Nightvision) ? "<nvg>" : "")}]");
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
                // BOMB_PLANTING       = 1 byte (u8);
                // BOMB_PLANT_ABORTED  = 1 byte (u8);
                // BOMB_PLANTED        = 8 bytes (6 bits for id + 10 bits for tick delta + 3 * i16 for (x,y,z));
                // BOMB_DROPPED        = 8 bytes (1 byte for FlashedId + 3 * i16 for (x,y,z));
                // BOMB_PICKED_UP      = 1 byte (1 bit to indicate whether spawned or from the ground, rest for player id);
                // BOMB_DEFUSING       = 1 byte (u8);
                // BOMB_DEFUSE_ABORTED = 1 byte (u8); 
                // BOMB_DEFUSED        = 1 byte (u8);
                // BOMB_EXPLODED       = 2 bytes (u16 - 2 ids - planter and defuser, if any);
                // FLASHED             = 8 bytes (6 bits for victim, 6 bit for thrower, 20 bits for event tick, 11 bits for fade time, 10 bits for hold time, 8 bits for alpha);
                // KILL_FLASHBANGED    = 1 byte (u8);
                // DIED                = 4 bytes (u32);
                // SAY / SAY_TEAM = 1 byte for meta + string([5 bits id (must +1 to decode) + 2 bits team + 1 bit dead / alive + 8 bits string len + string])
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
                        logStr.AppendLine($"  |--[ROUND ENDED status<{status}> reason<{reason}>]");
#endif

                        break;

                    case EventType.BOMB_PLANTING:

                        var pId = pReader.ReadU8();

                        events.Add(new BombPlantingEvent
                        {
                            Id = pId
                        });

#if DEBUG
                        logStr.AppendLine($"  |--[BOMB PLANTING id<{pId}>]");
#endif
                        break;

                    case EventType.BOMB_PLANT_ABORTED:

                        pId = pReader.ReadU8();

                        events.Add(new BombPlantAbortedEvent
                        {
                            Id = pId
                        });

#if DEBUG
                        logStr.AppendLine($"  |--[BOMB PLANTED ABORTED id<{pId}>]");
#endif
                        break;

                    case EventType.BOMB_PLANTED:
                        {
                            var packed = pReader.ReadU16();
                            var planterId = (byte)(packed & 0x3F); // 6 bits for planter id.
                            var explodeInSecEncoded = (short)((packed >> 6) & 0x3FF); // 10 bits for delta between plant and packet assembly.
                            var plantX = pReader.ReadI16();
                            var plantY = pReader.ReadI16();
                            var plantZ = pReader.ReadI16();

                            float tickDelta = explodeInSecEncoded / 20f;
                            events.Add(new BombPlantedEvent
                            {
                                Id = planterId,
                                X = plantX,
                                Y = plantY,
                                Z = plantZ,
                                ExplodesInSec = tickDelta
                            });

#if DEBUG
                            logStr.AppendLine($"  |--[BOMB PLANTED id<{planterId}> where<{plantX},{plantY},{plantZ}>]");
#endif

                            break;
                        }
                    
                    case EventType.BOMB_DROPPED:

                        var dropperId = (byte)pReader.ReadU16();
                        var dropX = pReader.ReadI16();
                        var dropY = pReader.ReadI16();
                        var dropZ = pReader.ReadI16();

                        events.Add(new BombDroppedEvent
                        {
                            Id = dropperId,
                            X = dropX,
                            Y = dropY,
                            Z = dropZ,
                        });

#if DEBUG
                        logStr.AppendLine($"  |--[BOMB DROPPED id<{dropperId}> where<{dropX},{dropY},{dropZ}>]");
#endif

                        break;

                    case EventType.BOMB_PICKED_UP:

                        byte data = pReader.ReadU8();
                        pId = (byte)(data & (0x7F)); // Zero the 7th bit.
                        var gotItWhenSpawning = (byte)(data >> 7) == 1;

                        events.Add(new BombPickedUpEvent
                        {
                            Id = pId,
                            FromSpawn = gotItWhenSpawning
                        });

#if DEBUG
                        logStr.AppendLine($"  |--[BOMB PICKED UP id<{pId}> spawn?<{gotItWhenSpawning}>]");
#endif
                        break;

                    case EventType.BOMB_DEFUSING:

                        pId = pReader.ReadU8();

                        events.Add(new BombDefusingEvent
                        {
                            Id = pId
                        });

#if DEBUG
                        logStr.AppendLine($"  |--[BOMB DEFUSING id<{pId}>]");
#endif
                        break;

                    case EventType.BOMB_DEFUSE_ABORTED:

                        pId = pReader.ReadU8();

                        events.Add(new BombDefuseAbortedEvent
                        {
                            Id = pId
                        });

#if DEBUG
                        logStr.AppendLine($"  |--[BOMB DEFUSE ABORTED id<{pId}>]");
#endif
                        break;

                    case EventType.BOMB_DEFUSED:

                        pId = pReader.ReadU8();

                        events.Add(new BombDefusedEvent
                        {
                            Id = pId
                        });

#if DEBUG
                        logStr.AppendLine($"  |--[BOMB DEFUSED id<{pId}>]");
#endif
                        break;

                    case EventType.BOMB_EXPLODED:

                        pId = pReader.ReadU8();
                        var dId = pReader.ReadU8();

                        events.Add(new BombExplodedEvent
                        {
                            PlanterId = pId,
                            DefuserId = dId != 0 ? dId : null,
                        });

#if DEBUG
                        logStr.AppendLine($"  |--[BOMB EXPLODED planter<{pId}>{(dId != 0 ? $" defuser<{dId}>" : "")}]");
#endif
                        break;

                    case EventType.FLASHED:
                        {
                            var packed = pReader.ReadU32();
                            var flashedId = (byte)(packed & 0x3F);
                            var throwerId = (byte)((packed >> 6) & 0x3F);
                            var evTickEncoded = (int)((packed >> 12) & 0xFFFFF);

                            packed = pReader.ReadU32();
                            int fadeEncoded = (int)(packed & 0x7FF);
                            int holdEncoded = (int)((packed >> 11) & 0x3FF);
                            int alpha = (int)((packed >> 21) & 0xFF);

                            float evTick = evTickEncoded / 100f;
                            float fadeTime = fadeEncoded / 100f;
                            float holdTime = holdEncoded / 100f;

                            events.Add(new PlayerFlashedEvent
                            {
                                FlashedId = flashedId,
                                ThrowerId = throwerId,
                                FadeTime = fadeTime,
                                HoldTime = holdTime,
                                Tick = evTick
                            });

                            break;
                        }
                    
                    case EventType.DIED:

                        var deathInfo = pReader.ReadU32();

                        // killer FlashedId    = 6 bits (0 to 32)
                        // victim FlashedId    = 5 bits (0 to 31 -> FlashedId - 1)
                        // assistant FlashedId = 6 bits (0 means no assist, 1 to 32 the FlashedId)
                        // weapon FlashedId    = 5 bits
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
                        logStr.AppendLine($"  |--[DIED k<{killer}> v<{victim}> assist<{assistant}> wep<{weapon}> rarity<{rarity}>]");
#endif
                        break;

                    case EventType.SAY:
                    case EventType.SAY_TEAM:
                        {
                            var packed = pReader.ReadU8();
                            pId = (byte)((packed & 0x1F) + 1);
                            var team = (Team)((packed & 0x60) >> 5);
                            bool isAlive = (packed & 0x80) != 0;
                            var msg = pReader.ReadString();

                            if (type == EventType.SAY)
                            {
                                events.Add(new SayEvent(msg)
                                {
                                    Id = pId,
                                    Team = team,
                                    IsAlive = isAlive,
                                });
                            }
                            else
                            {
                                events.Add(new SayTeamEvent(msg)
                                {
                                    Id = pId,
                                    Team = team,
                                    IsAlive = isAlive,
                                });
                            }
#if DEBUG
                            logStr.AppendLine($"  |--[CHAT id<{pId}> type<{(type == EventType.SAY ? "say" : "say_team")}> msg<{msg}>]");
#endif
                            break;
                        }
                    default:
                        break;
                }

            }

#if DEBUG
            Console.Write(logStr);
#endif

            return events;
        }

    }
}
