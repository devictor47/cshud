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
}
