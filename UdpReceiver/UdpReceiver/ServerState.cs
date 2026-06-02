using Fleck;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Text;
using System.Text.Json;

namespace UdpReceiver
{
    internal class ServerState
    {
        private readonly Lock stateLock = new();

        private float CurrentTick;

        private string? Map;
        private byte TScore;
        private byte CTScore;
        private float RoundEndTick;

        private readonly PlayerState?[] players =
            new PlayerState[Program.MAX_PLAYERS + 1];

        public void ApplySnapshot(Snapshot snapshot)
        {
            lock (stateLock)
            {
                CurrentTick = snapshot.Tick;

                if (snapshot.GlobalDelta != null)
                {
                    Map = snapshot.GlobalDelta.Map ?? Map;

                    if (snapshot.GlobalDelta.TScore.HasValue)
                    {
                        TScore = snapshot.GlobalDelta.TScore.Value;
                        CTScore = snapshot.GlobalDelta.CTScore!.Value;
                    }

                    if (snapshot.GlobalDelta.RoundEndTick.HasValue)
                        RoundEndTick = snapshot.GlobalDelta.RoundEndTick.Value;
                }

                if (snapshot.PlayersDelta != null)
                {
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
            }
        }

        public byte[] SerializeFullState()
        {
            var buffer = new ArrayBufferWriter<byte>(8192);

            using (var w = new Utf8JsonWriter(buffer))
            {
                w.WriteStartObject();

                lock (stateLock)
                {
                    w.WriteNumber("tick", CurrentTick);

                    w.WritePropertyName("global");
                    w.WriteStartObject();

                    w.WriteNumber("rt", RoundEndTick);

                    w.WriteNumber("t", TScore);
                    w.WriteNumber("ct", CTScore);

                    w.WriteString("map", Map);

                    w.WriteEndObject();

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
                        w.WriteBoolean("he", p.HasHE);
                        w.WriteBoolean("fb", p.HasFB);
                        w.WriteBoolean("smk", p.HasSmoke);
                        w.WriteBoolean("c4", p.HasC4);
                        w.WriteBoolean("kit", p.HasDefuseKit);
                        w.WriteBoolean("nvg", p.HasNightvision);

                        w.WriteString("name", p.Name);

                        w.WriteEndObject();
                    }
                }

                w.WriteEndArray();

                w.WriteEndObject();

                w.Flush();
            }

            return buffer.WrittenMemory.ToArray();
        }

        public PlayerState? GetPlayer(int id)
        {
            lock (stateLock)
            {
                var p = players[id];
                if (p == null) return null;

                return new PlayerState(p);
            }
        }
    }

    internal class PlayerState
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
        public bool HasHE;
        public bool HasFB;
        public bool HasSmoke;
        public bool HasC4;
        public bool HasDefuseKit;
        public bool HasNightvision;

        // Identity
        public string Name = string.Empty;

        // --- Convenience (computed, not stored) ---
        public int Hp => Math.Max(0, (int)RawHp);

        public PlayerState(byte id = 0)
        {
            Id = id;
            Team = Team.Unassigned;
            ArmorType = ArmorType.None;
            ArmorValue = 0;
            CurrentWeapon = WeaponId.NONE;
            PrimaryWeapon = WeaponId.NONE;
            SecondaryWeapon = WeaponId.NONE;
            HasHE = false;
            HasFB = false;
            HasSmoke = false;
            HasC4 = false;
            HasDefuseKit = false;
            HasNightvision = false;
            Name = string.Empty;
        }

        public PlayerState(PlayerState other)
        {
            Id = other.Id;
            Team = other.Team;

            Yaw = other.Yaw;
            X = other.X;
            Y = other.Y;
            Z = other.Z;

            RawHp = other.RawHp;
            Deaths = other.Deaths;
            Frags = other.Frags;

            Money = other.Money;

            ArmorType = other.ArmorType;
            ArmorValue = other.ArmorValue;

            CurrentWeapon = other.CurrentWeapon;

            PrimaryWeapon = other.PrimaryWeapon;
            SecondaryWeapon = other.SecondaryWeapon;
            HasHE = other.HasHE;
            HasFB = other.HasFB;
            HasSmoke = other.HasSmoke;
            HasC4 = other.HasC4;
            HasDefuseKit = other.HasDefuseKit;
            HasNightvision = other.HasNightvision;

            Name = other.Name; // string reference copy is fine
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

            if (d.Team.HasValue)
                Team = d.Team.Value;

            // --- Position ---
            if (d.Yaw.HasValue)
                Yaw = d.Yaw.Value;

            if (d.Pos.HasValue)
            {
                var p = d.Pos.Value;
                X = p.x; Y = p.y; Z = p.z;
            }

            // --- Vital Stats ---
            if (d.Hp.HasValue) 
                RawHp = d.Hp.Value;

            if (d.Frags.HasValue) 
                Frags = d.Frags.Value;

            if (d.Deaths.HasValue)
                Deaths = d.Deaths.Value;

            // --- Economy ---
            if (d.Money.HasValue)
                Money = d.Money.Value;

            // --- Equipment ---
            if (d.Armor.HasValue)
            {
                ArmorValue = d.Armor.Value.ArmorValue;
                ArmorType = d.Armor.Value.ArmorType;
            }

            if (d.CurrentWeapon.HasValue)
                CurrentWeapon = d.CurrentWeapon.Value;

            // --- Inventory ---
            if (d.PrimaryWeapon.HasValue)
                PrimaryWeapon = d.PrimaryWeapon.Value;

            if (d.SecondaryWeapon.HasValue)
                SecondaryWeapon = d.SecondaryWeapon.Value;

            if (d.HasHE.HasValue)
                HasHE = d.HasHE.Value;

            if (d.HasFB.HasValue)
                HasFB = d.HasFB.Value;

            if (d.HasSmoke.HasValue)
                HasSmoke = d.HasSmoke.Value;

            if (d.HasC4.HasValue)    
                HasC4 = d.HasC4.Value;

            // --- Items ---
            if (d.HasDefuseKit.HasValue)
                HasDefuseKit = d.HasDefuseKit.Value;

            if (d.HasNightvision.HasValue)
                HasNightvision = d.HasNightvision.Value;


            // --- Identity ---
            if (d.Name != null)
                Name = d.Name;
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
                $"\nGrenades: HE <{HasHE}> FB <{HasFB}> Smoke <{HasSmoke}>" +
                $"\nItems: {(Team == Team.Terrorist ? $"C4 <{HasC4}>" : $"Kit <{HasDefuseKit}>")} NVG <{HasNightvision}>" +
                $"\nFrags: {Frags}/{Deaths}" +
                $"\nPosition: ({X},{Y},{Z})" +
                $"\nYaw (H angle): {Yaw}" +
                $"";
        }
    }

    class Snapshot
    {
        public float Tick;

        public GlobalDelta? GlobalDelta;
        public List<PlayerDelta>? PlayersDelta;
        public List<GameEvent>? Events;

        public static class JsonWriter
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

            static void WritePlayersDelta(Utf8JsonWriter w, List<PlayerDelta> playersDeltas)
            {
                w.WritePropertyName("players");
                w.WriteStartArray();

                foreach (var pDelta in playersDeltas)
                {
                    w.WriteStartObject();

                    w.WriteNumber("id", pDelta.Id);

                    var flags = pDelta.Flags;

                    if ((flags & PlayerFlags.DROPPED) != 0)
                    {
                        w.WriteBoolean("drop", true);
                        w.WriteEndObject();
                        continue;
                    }

                    if ((flags & PlayerFlags.TEAM) != 0)
                        w.WriteNumber("team", (int)pDelta.Team!.Value);

                    if ((flags & PlayerFlags.YAW) != 0)
                        w.WriteNumber("yaw", pDelta.Yaw!.Value);

                    if ((flags & PlayerFlags.POS) != 0)
                    {
                        var pos = pDelta.Pos!.Value;
                        w.WriteStartArray("pos");
                        w.WriteNumberValue(pos.x);
                        w.WriteNumberValue(pos.y);
                        w.WriteNumberValue(pos.z);
                        w.WriteEndArray();
                    }

                    if ((flags & PlayerFlags.HP) != 0)
                        w.WriteNumber("hp", pDelta.Hp!.Value);

                    if ((flags & PlayerFlags.ARMOR) != 0)
                    {
                        var a = pDelta.Armor!.Value;
                        w.WriteNumber("armorVal", a.ArmorValue);
                        w.WriteNumber("armorType", (int)a.ArmorType);
                    }

                    if ((flags & PlayerFlags.CURWEP) != 0)
                        w.WriteNumber("wep", (int)pDelta.CurrentWeapon!.Value);

                    if ((flags & PlayerFlags.MONEY) != 0)
                        w.WriteNumber("money", pDelta.Money!.Value);

                    if ((flags & PlayerFlags.FRAGS) != 0)
                        w.WriteNumber("frags", pDelta.Frags!.Value);

                    if ((flags & PlayerFlags.DEATHS) != 0)
                        w.WriteNumber("deaths", pDelta.Deaths!.Value);

                    if ((flags & PlayerFlags.INV) != 0)
                    {
                        if (pDelta.PrimaryWeapon.HasValue)
                            w.WriteNumber("pwep", (int)pDelta.PrimaryWeapon.Value);

                        if (pDelta.SecondaryWeapon.HasValue)
                            w.WriteNumber("swep", (int)pDelta.SecondaryWeapon.Value);

                        if (pDelta.HasHE.HasValue)
                            w.WriteBoolean("he", pDelta.HasHE.Value);

                        if (pDelta.HasFB.HasValue)
                            w.WriteBoolean("fb", pDelta.HasFB.Value);

                        if (pDelta.HasSmoke.HasValue)
                            w.WriteBoolean("smk", pDelta.HasSmoke.Value); ;

                        if (pDelta.HasC4.HasValue)
                            w.WriteBoolean("c4", pDelta.HasC4.Value);
                    }

                    if ((flags & PlayerFlags.ITEMS) != 0)
                    {
                        if (pDelta.HasDefuseKit.HasValue)
                            w.WriteBoolean("kit", pDelta.HasDefuseKit.Value);

                        if (pDelta.HasNightvision.HasValue)
                            w.WriteBoolean("nvg", pDelta.HasNightvision.Value);
                    }

                    if ((flags & PlayerFlags.NAME) != 0)
                        w.WriteString("name", pDelta.Name);

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

                        case BombPlantingEvent b:
                            w.WriteNumber("i", b.Id);
                            break;

                        case BombPlantAbortedEvent b:
                            w.WriteNumber("i", b.Id);
                            break;

                        case BombPlantedEvent b:
                            w.WriteNumber("i", b.Id);
                            w.WriteNumber("x", b.X);
                            w.WriteNumber("y", b.Y);
                            w.WriteNumber("z", b.Z);
                            w.WriteNumber("t", b.ExplodesInSec);
                            break;

                        case BombDroppedEvent b:
                            w.WriteNumber("i", b.Id);
                            w.WriteNumber("x", b.X);
                            w.WriteNumber("y", b.Y);
                            w.WriteNumber("z", b.Z);
                            break;

                        case BombPickedUpEvent b:
                            w.WriteNumber("i", b.Id);
                            w.WriteBoolean("s", b.FromSpawn);
                            break;

                        case BombDefusingEvent b:
                            w.WriteNumber("i", b.Id);
                            break;

                        case BombDefuseAbortedEvent b:
                            w.WriteNumber("i", b.Id);
                            break;

                        case BombDefusedEvent b:
                            w.WriteNumber("i", b.Id);
                            break;

                        case BombExplodedEvent b:
                            w.WriteNumber("p", b.PlanterId);
                            if (b.DefuserId.HasValue)
                                w.WriteNumber("d", b.DefuserId.Value);
                            break;

                        case PlayerFlashedEvent b:
                            w.WriteNumber("v", b.FlashedId);
                            w.WriteNumber("a", b.ThrowerId);
                            w.WriteNumber("f", b.FadeTime);
                            w.WriteNumber("h", b.HoldTime);
                            w.WriteNumber("t", b.Tick);
                            break;
                    }

                    w.WriteEndObject();
                }

                w.WriteEndArray();
            }
        }
    }
}
