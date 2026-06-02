using System;
using System.Collections.Generic;
using System.Text;

namespace UdpReceiver
{
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

    sealed class BombPlantingEvent : GameEvent
    {
        public byte Id;

        public BombPlantingEvent()
        {
            Type = EventType.BOMB_PLANTING;
        }
    }

    sealed class BombPlantAbortedEvent : GameEvent
    {
        public byte Id;

        public BombPlantAbortedEvent()
        {
            Type = EventType.BOMB_PLANT_ABORTED;
        }
    }

    sealed class BombPlantedEvent : GameEvent
    {
        public byte Id;
        public short X;
        public short Y;
        public short Z;
        public float ExplodesInSec;

        public BombPlantedEvent()
        {
            Type = EventType.BOMB_PLANTED;
        }
    }

    sealed class BombDroppedEvent : GameEvent
    {
        public byte Id;
        public short X;
        public short Y;
        public short Z;

        public BombDroppedEvent()
        {
            Type = EventType.BOMB_DROPPED;
        }
    }

    sealed class BombPickedUpEvent : GameEvent
    {
        public byte Id;
        public bool FromSpawn;
        public BombPickedUpEvent()
        {
            Type = EventType.BOMB_PICKED_UP;
        }
    }

    sealed class BombDefusingEvent : GameEvent
    {
        public byte Id;

        public BombDefusingEvent()
        {
            Type = EventType.BOMB_DEFUSING;
        }
    }

    sealed class BombDefuseAbortedEvent : GameEvent
    {
        public byte Id;

        public BombDefuseAbortedEvent()
        {
            Type = EventType.BOMB_DEFUSE_ABORTED;
        }
    }

    sealed class BombDefusedEvent : GameEvent
    {
        public byte Id;

        public BombDefusedEvent()
        {
            Type = EventType.BOMB_DEFUSED;
        }
    }

    sealed class BombExplodedEvent : GameEvent
    {
        public byte PlanterId;
        public byte? DefuserId;

        public BombExplodedEvent()
        {
            Type = EventType.BOMB_EXPLODED;
        }
    }

    sealed class PlayerFlashedEvent : GameEvent
    {
        public byte FlashedId;
        public byte ThrowerId;
        public float FadeTime;
        public float HoldTime;
        public float Tick;

        public PlayerFlashedEvent()
        {
            Type = EventType.FLASHED;
        }
    }
}
