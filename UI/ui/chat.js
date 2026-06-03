
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
        DOM.chat.sayEl.scrollTop = DOM.chat.sayEl.scrollHeight;
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

        const player = document.createElement("span");
        player.className = "player t";
        player.textContent = name;

        addGenericTeamMessage(player, message, emphasiseMsg);
    }

    function addGenericCTMessage(name, message, emphasiseMsg = false) {
        const chatEntry = buildChatEntry();

        const msg = document.createElement("span");

        const player = document.createElement("span");
        player.className = "player ct";
        player.textContent = name;

        addGenericTeamMessage(player, message, emphasiseMsg);
    }

    function addKill(killEvent) {

        const chatEntry = buildChatEntry();

        const msg = document.createElement("span");

        const killer = document.createElement("span");

        switch (killEvent.killerTeam) {
            case 1:
                killer.className = "player t";
                break;
            case 2:
                killer.className = "player ct";
                break;
            default:
                killer.className = "player";
                break;
        }

        killer.textContent = killEvent.killerName;

        msg.append(killer);

        if (killEvent.assistantName) {

            const assistant = document.createElement("span");
            
            switch (killEvent.assistantTeam) {
                case 1:
                    assistant.className = "player t";
                    break;
                case 2:
                    assistant.className = "player ct";
                    break;
                default:
                    assistant.className = "player";
                    break;
            }

            assistant.textContent = killEvent.assistantName;

            msg.append(" + ", assistant);
        }

        const victim = document.createElement("span");
        
        switch (killEvent.victimTeam) {
            case 1:
                victim.className = "player t";
                break;
            case 2:
                victim.className = "player ct";
                break;
            default:
                victim.className = "player";
                break;
        }
        
        victim.textContent = killEvent.victimName;

        if (killEvent.rarity & Consts.KILL_RARITY.HEADSHOT) {
            const hs = document.createElement("span");
            hs.className = "emphasis";
            hs.textContent = " headshot ";
            msg.append(hs);
        }
        else {

            if (killEvent.rarity & Consts.KILL_RARITY.KILLER_BLIND) {
                const hs = document.createElement("span");
                hs.className = "emphasis";
                hs.textContent = " BLIND-killed ";
                 msg.append(hs);
            }
            else {
                msg.append(" killed ");
            }
        }

        const weaponPart = document.createElement("span");
        weaponPart.className = "kill-weapon";

        weaponPart.append(
            `with ${Chat.getArticleFor(killEvent.weapon.name)} ${killEvent.weapon.name}`,
        );

        try {
            const wepIco = IconAssets.createWeaponIcon(killEvent.weapon.id);
            weaponPart.append(wepIco);
        }
        catch (err) {
            console.error(`Failed to add weapon icon to kill event message: ${err}`);
        }
        
        msg.append(
            victim,
            " ",
            weaponPart
        );


        chatEntry.msg.append(msg);

        addEntryToChat(chatEntry);
    }

    function addDeath(name, team) {

        const chatEntry = buildChatEntry();

        const msg = document.createElement("span");

        const killer = document.createElement("span");

        switch (team) {
            case 1:
                killer.className = "player t";
                break;
            case 2:
                killer.className = "player ct";
                break;
            default:
                killer.className = "player";
                break;
        }

        killer.textContent = name;

        msg.append(killer, " died");

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

        const player = document.createElement("span");
        player.className = "player t";
        player.textContent = planterName;

        const bomb = document.createElement("span");
        bomb.className = "emphasis";
        bomb.textContent = "'s bomb EXPLODED";

        if (!defuserName) {
            msg.append(player, bomb);
        }
        else {
            const defuser = document.createElement("span");
            defuser.className = "player ct";
            defuser.textContent = defuserName;
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
        const victimEl = document.createElement("span");

        switch (victimTeam) {
            case 1:
                victimEl.className = "player t";
                break;
            case 2:
                victimEl.className = "player ct";
                break;
            default:
                victimEl.className = "player";
                break;
        }

        victimEl.textContent = victimName;

        msg.append(victimEl);

        if (throwerName) {

            const throwerEl = document.createElement("span");

            switch (throwerTeam) {
                case 1:
                    throwerEl.className = "player t";
                    break;
                case 2:
                    throwerEl.className = "player ct";
                    break;
                default:
                    throwerEl.className = "player";
                    break;
            }

            throwerEl.textContent = throwerName;

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

    function addKillLocale(killEvent) {

        const chatEntry = buildChatEntry();

        const msg = document.createElement("span");

        const killer = document.createElement("span");

        switch (killEvent.killerTeam) {
            case 1:
                killer.className = "player t";
                break;
            case 2:
                killer.className = "player ct";
                break;
            default:
                killer.className = "player";
                break;
        }

        killer.textContent = killEvent.killerName;

        msg.append(killer);

        if (killEvent.assistantName) {

            const assistant = document.createElement("span");
            
            switch (killEvent.assistantTeam) {
                case 1:
                    assistant.className = "player t";
                    break;
                case 2:
                    assistant.className = "player ct";
                    break;
                default:
                    assistant.className = "player";
                    break;
            }

            assistant.textContent = killEvent.assistantName;

            msg.append(" + ", assistant);
        }

        const victim = document.createElement("span");
        
        switch (killEvent.victimTeam) {
            case 1:
                victim.className = "player t";
                break;
            case 2:
                victim.className = "player ct";
                break;
            default:
                victim.className = "player";
                break;
        }
        
        victim.textContent = killEvent.victimName;

        if (killEvent.rarity & Consts.KILL_RARITY.HEADSHOT) {
            const hs = document.createElement("span");
            hs.className = "emphasis";
            hs.textContent = " headshot ";
            msg.append(hs);
        }
        else {

            if (killEvent.rarity & Consts.KILL_RARITY.KILLER_BLIND) {
                const hs = document.createElement("span");
                hs.className = "emphasis";
                hs.textContent = " BLIND-killed ";
                 msg.append(hs);
            }
            else {
                msg.append(" killed ");
            }
        }

        const weaponPart = document.createElement("span");
        weaponPart.className = "kill-weapon";

        weaponPart.append(
            `with ${Chat.getArticleFor(killEvent.weapon.name)} ${killEvent.weapon.name}`,
        );

        try {
            const wepIco = IconAssets.createWeaponIcon(killEvent.weapon.id);
            weaponPart.append(wepIco);
        }
        catch (err) {
            console.error(`Failed to add weapon icon to kill event message: ${err}`);
        }
        
        msg.append(
            victim,
            " ",
            weaponPart
        );


        chatEntry.msg.append(msg);

        addEntryToChat(chatEntry);
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
        addPlayerFlashed
    };

})();