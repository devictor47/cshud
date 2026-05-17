using System;
using System.Collections.Generic;
using System.Text;

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
}
