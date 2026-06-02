window.PlayersUI = (() => {

    const tNodes = new Map();
    const ctNodes = new Map();

    function createPlayerNode() {
        const cardEl = document.createElement("div");
        cardEl.className = "player-card";

        cardEl.innerHTML = `
            <div class="row row-1">
                <div class="player-name"></div>
            
                <div class="kills">
                    <img src="hud/kill.png" />
                    <span></span>
                </div>
            
                <div class="deaths">
                    <img src="hud/death.png" />
                    <span></span>
                </div>
            </div>
            
            <div class="row row-2">
            
                <div class="money">
                    <img src="hud/cash.png" />
                    <span></span>
                </div>
            
                <div class="items"></div>
            
            </div>
        
            <div class="row row-3">
            
                <div class="hp-bar"></div>
            
                <div class="hp">
                    <img class="hp-ico" src="hud/hp_16x16.png" />
                    <span></span>
                    <img class="armor" />
                </div>
            
                <div class="secondary"></div>
                <div class="primary"></div>
            
            </div>
        `;

        return {
            cardEl,
            nameEl: cardEl.querySelector(".player-name"),
            hpEl: cardEl.querySelector(".hp span"),
            hpBar: cardEl.querySelector(".hp-bar"),
            armorEl: cardEl.querySelector(".armor"),
            fragsEl: cardEl.querySelector(".kills span"),
            deathsEl: cardEl.querySelector(".deaths span"),
            moneyEl: cardEl.querySelector(".money span"),
            primaryEl: cardEl.querySelector(".primary"),
            secondaryEl: cardEl.querySelector(".secondary"),
            itemsEl: cardEl.querySelector(".items"),
            itemsNodes: {
                he: null,
                fb: null,
                smoke: null,
                c4: null,
                kit: null
            }
        };
    }

    function upsert(player, delta) {

        if (player.team === 1)
            return render(player, delta, upsertT(player));

        if (player.team === 2)
            return render(player, delta, upsertCT(player));

        // If any other team, then we remove
        // the player from the UI.
        remove(player.id);
    }

    function upsertT(player) {

        let node = tNodes.get(player.id);

        if (!node) {

            node = ctNodes.get(player.id);
            if (node) {
                // Then we are switching teams.
                ctNodes.delete(player.id);
                DOM.players.ctCountEl.textContent = ` (${ctNodes.size})`;
            }
            else {
                node = createPlayerNode();
            }

            tNodes.set(player.id, node);
            DOM.players.tListEl.appendChild(node.cardEl);
            DOM.players.tCountEl.textContent = ` (${tNodes.size})`;
        }

        return node;
    }

    function upsertCT(player) {

        let node = ctNodes.get(player.id);

        if (!node) {

            node = tNodes.get(player.id);
            if (node) {
                // Then we are switching teams.
                tNodes.delete(player.id);
                DOM.players.tCountEl.textContent = ` (${tNodes.size})`;
            }
            else {
                node = createPlayerNode();
            }
            
            ctNodes.set(player.id, node);
            DOM.players.ctListEl.appendChild(node.cardEl);
            DOM.players.ctCountEl.textContent = ` (${ctNodes.size})`;
        }

        return node;
    }

    function applyItemDelta(pCard, nodeName, deltaValue, iconData) {

        const el = pCard.itemsEl;

        if (deltaValue === undefined)
            return;

        if (deltaValue) {
            const iconEl = IconAssets.createItemIcon(iconData);
            
            if(iconEl) {
                pCard.itemsNodes[nodeName] = iconEl;
                el.appendChild(iconEl);
            }
        }
        else {
            pCard.itemsNodes[nodeName]?.remove();
            pCard.itemsNodes[nodeName] = null;
        }
    }

    function render(player, delta, pCardNode) {

        if (delta.name !== undefined)
            pCardNode.nameEl.textContent = delta.name;

        if (delta.hp !== undefined) {
            const hp = Math.max(0, delta.hp);
            pCardNode.hpEl.textContent = hp;
            pCardNode.hpBar.style.setProperty("--hp", hp / 100);
        }

        if (delta.armorVal !== undefined) {

            if (delta.armorVal > 0) {

                const iconSrc = delta.armorType === 1
                ? IconAssets.VESTHELM_SRC
                : IconAssets.VEST_SRC;
                
                if (!pCardNode.armorEl.src.includes(iconSrc)) {
                    pCardNode.armorEl.src = iconSrc;
                }

                pCardNode.armorEl.style.display = "block";
            }
            else {
                pCardNode.armorEl.style.display = "none";
            }
        }

        if (delta.money !== undefined)
            pCardNode.moneyEl.textContent = delta.money;
        
        if (delta.frags !== undefined)
            pCardNode.fragsEl.textContent = delta.frags;
        
        if (delta.deaths !== undefined)
            pCardNode.deathsEl.textContent = delta.deaths;

        if (delta.wep !== undefined)
        {
            const slot = Consts.WEAPONS[delta.wep]?.slot;
            
            if (slot === Consts.WEAPON_SLOT.PRIMARY) {
                pCardNode.primaryEl.classList.add('active-wpn');
                pCardNode.secondaryEl.classList.remove('active-wpn');
            }
            else if (slot === Consts.WEAPON_SLOT.SECONDARY) {
                pCardNode.secondaryEl.classList.add('active-wpn');
                pCardNode.primaryEl.classList.remove('active-wpn');
            }
            else {
                pCardNode.primaryEl.classList.remove('active-wpn');
                pCardNode.secondaryEl.classList.remove('active-wpn');
            }
        }

        if (delta.pwep !== undefined) {
            const wepIco = IconAssets.createWeaponIcon(delta.pwep);
            pCardNode.primaryEl.replaceChildren(wepIco);
        }

        if (delta.swep !== undefined) {
            const wepIco = IconAssets.createWeaponIcon(delta.swep);
            pCardNode.secondaryEl.replaceChildren(wepIco);
        }

        applyItemDelta(
            pCardNode,
            "he",
            delta.he,
            Consts.ITEMS_ICONS.HE
        );

        applyItemDelta(
            pCardNode,
            "fb",
            delta.fb,
            Consts.ITEMS_ICONS.FB
        );

        applyItemDelta(
            pCardNode,
            "smoke",
            delta.smk,
            Consts.ITEMS_ICONS.SMOKE
        );

        applyItemDelta(
            pCardNode,
            "c4",
            delta.c4,
            Consts.ITEMS_ICONS.C4
        );

        applyItemDelta(
            pCardNode,
            "kit",
            delta.kit,
            Consts.ITEMS_ICONS.KIT
        );

    }

    function remove(id) {
        const tNode = tNodes.get(id);
        if (tNode) {
            tNode.cardEl.remove();
            tNodes.delete(id);
            DOM.players.tCountEl.textContent = ` (${tNodes.size})`;
            return;
        }

        const ctNode = ctNodes.get(id);
        if (ctNode) {
            ctNode.cardEl.remove();
            ctNodes.delete(id);
            DOM.players.ctCountEl.textContent = ` (${ctNodes.size})`;
        }
    }

    function blinded(playerId, holdTime, fadeTime) {

        const node = tNodes.get(playerId) || ctNodes.get(playerId);
        
        node.cardEl.classList.add("blind-hold");

        setTimeout(() => {
            requestAnimationFrame(() => {
                node.cardEl.style.transition = `background-color ${fadeTime}s linear`;
                node.cardEl.classList.remove("blind-hold");
            });
        }, holdTime * 1000);
    }

    return {
        upsert,
        remove,
        blinded
    };

})();