
window.Consts = (() => {

    function deepFreeze(obj) {

        Object.values(obj).forEach(value => {

            if (
                value &&
                typeof value === "object"
            ) {
                deepFreeze(value);
            }
        });

        return Object.freeze(obj);
    }

    const WEAPON_SLOT = Object.freeze({
        NONE: "none",
        PRIMARY: "primary",
        SECONDARY: "secondary",
        GRENADE: "grenade",
        KNIFE: "knife",
        C4: "c4"
    });
    
    const WEAPONS_IDS = deepFreeze({

        // Pistols
        P228: 1,
        ELITE: 10,
        FIVESEVEN: 11,
        USP: 16,
        GLOCK: 17,
        DEAGLE: 26,

        // Rifles / snipers
        SCOUT: 3,
        AUG: 8,
        G3SG1: 13,
        GALIL: 14,
        FAMAS: 15,
        AWP: 18,
        M4A1: 22,
        SG550: 24,
        SG552: 27,
        AK47: 28,

        // SMGs
        MAC10: 7,
        MP5: 19,
        TMP: 23,
        UMP45: 12,
        P90: 30,

        // Shotguns
        XM1014: 5,
        M3: 21,

        // Machine gun
        M249: 20,

        // Grenades
        HE: 4,
        SMOKE: 9,
        FB: 25,

        // Knife / C4
        KNIFE: 29,
        C4: 6
    });

    const WEAPONS = deepFreeze({
        0: { id: 0, name: null, iconName: null, slot: WEAPON_SLOT.NONE },

        // Pistols
        1: { id: 1, name: "P228", iconName: "weapon_p250", slot: WEAPON_SLOT.SECONDARY }, // P228
        2: { id: 2, name: null, iconName: null, slot: WEAPON_SLOT.NONE }, // unused
        10: { id: 10, name: "Elite", iconName: "weapon_elite", slot: WEAPON_SLOT.SECONDARY },
        11: { id: 11, name: "Five-Seven", iconName: "weapon_fiveseven", slot: WEAPON_SLOT.SECONDARY },
        16: { id: 16, name: "USP", iconName: "weapon_usp_silencer", slot: WEAPON_SLOT.SECONDARY },
        17: { id: 17, name: "Glock", iconName: "weapon_glock", slot: WEAPON_SLOT.SECONDARY },
        26: { id: 26, name: "Deagle", iconName: "weapon_deagle", slot: WEAPON_SLOT.SECONDARY },

        // Rifles / snipers
        3: { id: 3, name: "Scout", iconName: "weapon_ssg08", slot: WEAPON_SLOT.PRIMARY }, // scout
        8: { id: 8, name: "AUG", iconName: "weapon_aug", slot: WEAPON_SLOT.PRIMARY },
        13: { id: 13, name: "G3SG1", iconName: "weapon_g3sg1", slot: WEAPON_SLOT.PRIMARY },
        14: { id: 14, name: "Galil", iconName: "weapon_galilar", slot: WEAPON_SLOT.PRIMARY },
        15: { id: 15, name: "Famas", iconName: "weapon_famas", slot: WEAPON_SLOT.PRIMARY },
        18: { id: 18, name: "AWP", iconName: "weapon_awp", slot: WEAPON_SLOT.PRIMARY },
        22: { id: 22, name: "M4A1", iconName: "weapon_m4a1", slot: WEAPON_SLOT.PRIMARY },
        24: { id: 24, name: "SG550", iconName: "weapon_g3sg1", slot: WEAPON_SLOT.PRIMARY }, // sg550 fallback
        27: { id: 27, name: "SG552", iconName: "weapon_sg556", slot: WEAPON_SLOT.PRIMARY }, // sg552
        28: { id: 28, name: "AK47", iconName: "weapon_ak47", slot: WEAPON_SLOT.PRIMARY },

        // SMGs
        7: { id: 7, name: "MAC-10", iconName: "weapon_mac10", slot: WEAPON_SLOT.PRIMARY },
        19: { id: 19, name: "MP5", iconName: "weapon_mp7", slot: WEAPON_SLOT.PRIMARY }, // mp5n
        23: { id: 23, name: "TMP", iconName: "weapon_mp9", slot: WEAPON_SLOT.PRIMARY }, // tmp
        12: { id: 12, name: "UMP-45", iconName: "weapon_ump45", slot: WEAPON_SLOT.PRIMARY },
        30: { id: 30, name: "P90", iconName: "weapon_p90", slot: WEAPON_SLOT.PRIMARY },

        // Shotguns
        5: { id: 5, name: "XM1014", iconName: "weapon_xm1014", slot: WEAPON_SLOT.PRIMARY },
        21: { id: 21, name: "M3", iconName: "weapon_nova", slot: WEAPON_SLOT.PRIMARY },

        // Machine gun
        20: { id: 20, name: "M249", iconName: "weapon_m249", slot: WEAPON_SLOT.PRIMARY },

        // Grenades
        4: { id: 4, name: "HE", iconName: "weapon_hegrenade", slot: WEAPON_SLOT.GRENADE },
        9: { id: 9, name: "Smoke", iconName: "weapon_smokegrenade", slot: WEAPON_SLOT.GRENADE },
        25: { id: 25, name: "FB", iconName: "weapon_flashbang", slot: WEAPON_SLOT.GRENADE },

        // Knife / C4
        29: { id: 29, name: "Knife", iconName: "weapon_knife", slot: WEAPON_SLOT.KNIFE },
        6: { id: 6, name: "C4", iconName: "weapon_c4", slot: WEAPON_SLOT.C4 }
    });

    const ITEMS_ICONS = deepFreeze({
        HE: { src: "hud/he.png", className: "he" },
        FB: { src: "hud/fb.png", className: "fb" },
        SMOKE: { src: "hud/sg.png", className: "sg" },
        C4: { src: "hud/c4.png", className: "c4" },
        KIT: { src: "hud/kit.png", className: "kit" }
    });

    const OVERVIEW_ICONS = deepFreeze({
        C4: { src: "hud/c4.png" },
        C4_PLANTED: { src: "hud/c4_planted_ol.png" },
        // hud/csgo-icons/svg_normal/weapon_c4.svg
    });

    const EVENT_TYPE = Object.freeze({
        ROUND_ENDED: 0,
        BOMB_PLANTING: 1,
        BOMB_PLANT_ABORTED: 2,
        BOMB_PLANTED: 3,
        BOMB_DROPPED: 4,
        BOMB_PICKED_UP: 5,
        BOMB_DEFUSING: 6,
        BOMB_DEFUSE_ABORTED: 7,
        BOMB_DEFUSED: 8,
        BOMB_EXPLODED: 9,
        FLASHED: 10,
        DIED: 11,
    });

    const KILL_RARITY = Object.freeze({
        NONE: 0,
        HEADSHOT: 1,
        KILLER_BLIND: 2,
        NOSCOPE: 4,
        PENETRATED: 8,
        THRUSMOKE: 16,
        ASSISTEDFLASH: 32,
        DOMINATION_BEGAN: 64,
        DOMINATION: 128,
        REVENGE: 256,
        INAIR: 512
    });

    const local = deepFreeze({
        ptBr: {
            overview: {
                notFound: "OVERVIEW NÃO ENCONTRADO",
                loading: "Aguardando dados do servidor..."
            },
            map: "Mapa",
            chat: {
                kill: (killer, victim, weapon, headshot, blind) =>{

                    let message;

                    if (headshot) {
                        message = `${killer} deu um headshot em ${victim} com ${weapon}`;
                    }
                    else {
                        message = `${killer} matou ${victim} com ${weapon}`;
                    }

                    return message;
                },
            }
        }
    });

    return {
        WEAPON_SLOT,
        WEAPONS_IDS,
        WEAPONS,
        ITEMS_ICONS,
        OVERVIEW_ICONS,
        EVENT_TYPE,
        KILL_RARITY,
        LOCALE: local.ptBr
    };

})();