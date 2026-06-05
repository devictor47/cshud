
window.Chat = (() => {

    const ARTICLE_EXCEPTIONS = {
        "m4a1": "an",
        "mp5": "an",
        "he": "an",
        "usp": "a",
        "ak47": "an",
        "awp": "an",
        "sg550": "an",
        "sg552": "an",
        "aug": "an",
        "elite": "a",
        "fiveseven": "a",
        "deagle": "a",
        "p90": "a",
        "ump45": "a",
        "m249": "a",
        "knife": "a",
        "c4": "a",
        "xm1014": "an",
        "m3": "a",
        "mac10": "a",
        "g3sg1": "a",
        "galil": "a",
        "famas": "a"
    };

    function getArticleFor(word) {

        const lower = word.toLowerCase();

        if (ARTICLE_EXCEPTIONS[lower])
            return ARTICLE_EXCEPTIONS[lower];

        return /^[aeiou]/i.test(word) ? "an" : "a";
    }

    function buildRichNameSpan(name, team) {

        const player = document.createElement("span");
        player.textContent = name;

        switch (team) {
            case Consts.TEAM.TERRORIST:
                player.className = "player t";
                break;
            case Consts.TEAM.CT:
                player.className = "player ct";
                break;
            default:
                player.className = "player";
                break;
        }

        return player;
    }

    function buildChatEntry(addTimestamp = true) {
        const line = document.createElement("div");
        const ts = document.createElement("span");
        const msg = document.createElement("span");
        
        line.className = "line";
        ts.className = "line-time";
        msg.className = "line-msg";
        
        if (addTimestamp)
            ts.textContent = `[${new Date().toLocaleTimeString('en-GB', { hour12: false })}] `;

        line.replaceChildren(ts, msg);

        return {
            line,
            ts,
            msg
        };
    }

    function addEntryToChat(entry) {
        DOM.chat.sayEl.appendChild(entry.line);
        DOM.chat.sayEl.parentElement.scrollTop = DOM.chat.sayEl.parentElement.scrollHeight;
    }

    function addLog(message) {
        const chatEntry = buildChatEntry();
        chatEntry.msg.textContent = message;
        addEntryToChat(chatEntry);
    }

    function addGenericTeamMessage(playerSpan, message, emphasiseMsg = false) {

        const chatEntry = buildChatEntry();

        const msg = document.createElement("span");

        if (emphasiseMsg) {
            const emphMsg = document.createElement("span");
            emphMsg.className = "emphasis";
            emphMsg.textContent = message;

            msg.append(
            playerSpan,
            " ",
            emphMsg);
        }
        else {
            msg.append(
            playerSpan,
            " ",
            message);
        }
        
        chatEntry.msg.append(msg);
        addEntryToChat(chatEntry);
    }

    function addGenericTMessage(name, message, emphasiseMsg = false) {
        const chatEntry = buildChatEntry();
        const msg = document.createElement("span");
        const player = buildRichNameSpan(name, Consts.TEAM.TERRORIST);
        addGenericTeamMessage(player, message, emphasiseMsg);
    }

    function addGenericCTMessage(name, message, emphasiseMsg = false) {
        const chatEntry = buildChatEntry();
        const msg = document.createElement("span");
        const player = buildRichNameSpan(name, Consts.TEAM.CT);
        addGenericTeamMessage(player, message, emphasiseMsg);
    }

    function addKill(killEvent) {

        const chatEntry = buildChatEntry();

        const msg = document.createElement("span");

        const killer = buildRichNameSpan(killEvent.killerName, killEvent.killerTeam);
        msg.append(killer);

        // Add the " + ..." if assistant was present.
        if (killEvent.assistantName) {
            const assistant = buildRichNameSpan(killEvent.assistantName, killEvent.assistantTeam);

            msg.append(" + ");

            if (killEvent.rarity & Consts.KILL_RARITY.ASSISTEDFLASH) {
                const weaponPart = document.createElement("span");
                weaponPart.className = "kill-weapon";
                weaponPart.append(IconAssets.createFeedIcon(Consts.FEED_ICONS.ASSISTEDFLASH));
                msg.append(weaponPart);
            }

            msg.append(assistant);
        }

        const victim = buildRichNameSpan(killEvent.victimName, killEvent.victimTeam);

        let flags = killEvent.rarity;

        const weaponPart = document.createElement("span");
        weaponPart.className = "kill-weapon";
        weaponPart.append(IconAssets.createWeaponIcon(killEvent.weapon.id));
        
        if (flags) {
            
            do {

                const bit =
                    flags & -flags;

                switch (bit) {
                    case Consts.KILL_RARITY.HEADSHOT: {
                        weaponPart.append(IconAssets.createFeedIcon(Consts.FEED_ICONS.HEADSHOT));
                        break;
                    }
                    case Consts.KILL_RARITY.KILLER_BLIND: {
                        weaponPart.append(IconAssets.createFeedIcon(Consts.FEED_ICONS.KILLER_BLIND));
                        break;
                    }
                    case Consts.KILL_RARITY.NOSCOPE: {
                        weaponPart.append(IconAssets.createFeedIcon(Consts.FEED_ICONS.NOSCOPE));
                        break;
                    }
                    case Consts.KILL_RARITY.PENETRATED: {
                        weaponPart.append(IconAssets.createFeedIcon(Consts.FEED_ICONS.PENETRATED));
                        break;
                    }
                    case Consts.KILL_RARITY.THRUSMOKE: {
                        weaponPart.append(IconAssets.createFeedIcon(Consts.FEED_ICONS.THRUSMOKE));
                        break;
                    }
                    // case Consts.KILL_RARITY.ASSISTEDFLASH: {
                    //     weaponPart.append(IconAssets.createFeedIcon(Consts.FEED_ICONS.ASSISTEDFLASH));
                    //     break;
                    // }
                    case Consts.KILL_RARITY.INAIR: {
                        weaponPart.append(IconAssets.createFeedIcon(Consts.FEED_ICONS.INAIR));
                        break;
                    }
                }

                flags ^= bit;

            } while (flags !== 0);
        }
        
        msg.append(
            weaponPart,
            victim,
        );

        chatEntry.msg.append(msg);
        addEntryToChat(chatEntry);
    }

    function addDeath(name, team) {

        const chatEntry = buildChatEntry();
        const msg = document.createElement("span");
        const player = buildRichNameSpan(name, team);

        msg.append(player, " died");

        chatEntry.msg.append(msg);
        addEntryToChat(chatEntry);
    }

    function addBombDrop(name) {
        addGenericTMessage(name, "dropped the bomb", true);
    }

    function addBombPickedUp(name, gotItFromSpawn) {

        if (gotItFromSpawn)
            addGenericTMessage(name, "spawned with the bomb");
        else
            addGenericTMessage(name, "picked up the bomb");
    }

    function addBombPlanting(name) {
        addGenericTMessage(name, "is planting the bomb");
    }

    function addBombPlantAborted(name) {
        addGenericTMessage(name, "stopped planting the bomb");
    }

    function addBombPlanted(name) {
        addGenericTMessage(name, "planted the bomb", true);
    }

    function addBombExploded(planterName, defuserName) {
        const chatEntry = buildChatEntry();

        const msg = document.createElement("span");

        const player = buildRichNameSpan(planterName, Consts.TEAM.TERRORIST);

        const bomb = document.createElement("span");
        bomb.className = "emphasis";
        bomb.textContent = "'s bomb EXPLODED";

        if (!defuserName) {
            msg.append(player, bomb);
        }
        else {
            const defuser = buildRichNameSpan(defuserName, Consts.TEAM.CT);
            msg.append(
            player,
            bomb,
            " while ",
            defuser,
            " defused it");
        }        

        chatEntry.msg.append(msg);
        addEntryToChat(chatEntry);
    }

    function addBombDefusing(name) {
        addGenericCTMessage(name, "is defusing the bomb", true);
    }

    function addBombDefuseAborted(name) {
        addGenericCTMessage(name, "stopped defusing the bomb");
    }

    function addBombDefused(name) {
        addGenericCTMessage(name, "defused the bomb");
    }

    function addPlayerFlashed(victimName, victimTeam, throwerName, throwerTeam) {

        const chatEntry = buildChatEntry();

        const msg = document.createElement("span");
        const victimEl = buildRichNameSpan(victimName, victimTeam);

        msg.append(victimEl);

        if (throwerName) {
            const throwerEl = buildRichNameSpan(throwerName, throwerTeam);

            msg.append(
                victimEl,
                " got flashed by ",
                throwerEl
            );
        }
        else {
            msg.append(
                victimEl,
                " got flashed"
            );
        }

        chatEntry.msg.append(msg);
        addEntryToChat(chatEntry);
    }

    function _addSay(type, name, message, team, alive) {

        const chatEntry = buildChatEntry();
        chatEntry.line.classList.add("is-chat");

        const msg = document.createElement("span");

        const player = document.createElement("span");
        player.textContent = name;

        const prefix = document.createElement("span");
        prefix.className = "emphasis";

        let chatEl = DOM.chat.sayEl;
        prefix.textContent = !alive ? "[DEAD] " : "";

        switch (team) {
             case 1:
                player.className = "player t";
                if (type === "say_team")
                    chatEl = DOM.chat.tChatEl;
                break;
            case 2:
                player.className = "player ct";
                if (type === "say_team")
                    chatEl = DOM.chat.ctChatEl;
                break;
            default:
                player.className = "player";

                prefix.textContent = type === "say"
                    ? "[SAY - SPEC] "
                    : "[TEAM - SPEC] ";

                break;
        }

        msg.append(
            prefix,
            player,
            ": ",
            message
        );

        chatEntry.msg.append(msg);

        chatEl.appendChild(chatEntry.line);
        chatEl.scrollTop = DOM.chat.sayEl.scrollHeight;
    }

    function addSay(name, message, team, alive) {
        _addSay("say", name, message, team, alive);
    }

    function addSayTeam(name, message, team, alive) {
        _addSay("say_team", name, message, team, alive);
    }
    
    function addPlayerDropped(name, team) {

    }

    return {
        getArticleFor,
        addLog,
        addGenericTMessage,
        addGenericCTMessage,
        addKill,
        addDeath,
        addBombDrop,
        addBombPickedUp,
        addBombPlanting,
        addBombPlantAborted,
        addBombPlanted,
        addBombExploded,
        addBombDefusing,
        addBombDefuseAborted,
        addBombDefused,
        addPlayerFlashed,
        addSay,
        addSayTeam,
        hideLogEntries: () => {
            document
            .querySelectorAll(".line:not(.is-chat)")
            .forEach(el => el.classList.add("hide"));
        },
        showLogEntries: () => {
            document
            .querySelectorAll(".line:not(.is-chat)")
            .forEach(el => el.classList.remove("hide"));
        },
    };

})();