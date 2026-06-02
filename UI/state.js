const State = (() => {

    const listeners = new Map();

    const state = {
        time: {
            tick: 0, // Curent game tick.
            prevTick: 0, // Previous tick.
            receivedAt: 0 // Timestamp of when current tick was received.
        },
        global: {},
        players: {}, // id -> player data
        radar: {
            overview: null
        },
        c4: {
            timeLeft: null,
            startedAt: null
        }
    };

    function on(event, cb) {
        if (!listeners.has(event))
            listeners.set(event, []);

        listeners.get(event).push(cb);
    }

    function emit(event, data) {
        listeners.get(event)?.forEach(fn => fn(data));
    }

    function upsertPlayer(id, delta) {

        let p = state.players[id];

        if (!p) {
            p = state.players[id] = createEmptyPlayer(id);
        }

        applyDelta(p, delta);

        emit("playerUpdated", { player: p, delta });
    }

    function removePlayer(id) {
        delete state.players[id];
        emit("playerRemoved", id);
    }

    function createEmptyPlayer(id) {
        return {
            id,

            team: 0,
            yaw: 0,
            x: 0, y: 0, z: 0,

            hp: 0,
            armorType: 0,
            armorValue: 0,

            money: 0,
            frags: 0,
            deaths: 0,

            currentWeapon: 0,
            primaryWeapon: 0,
            secondaryWeapon: 0,

            grenades: 0,
            hasC4: false,
            items: 0,

            name: "",

            isDead: false
        };
    }

    function applyDelta(p, d) {

        if (d.team !== undefined) p.team = d.team;

        if (d.yaw !== undefined) p.yaw = d.yaw;

        if (d.pos !== undefined) {
            p.x = d.pos[0];
            p.y = d.pos[1];
            p.z = d.pos[2];
        }

        if (d.hp !== undefined) {
            p.hp = Math.max(0, d.hp);
            p.isDead = d.hp <= 0;
        }

        if (d.armorVal !== undefined) {
            p.armorValue = d.armorVal;
            p.armorType = d.armorType;
        }

        if (d.money !== undefined) p.money = d.money;
        if (d.frags !== undefined) p.frags = d.frags;
        if (d.deaths !== undefined) p.deaths = d.deaths;

        if (d.wep !== undefined) p.currentWeapon = d.wep;
        if (d.pwep !== undefined) p.primaryWeapon = d.pwep;
        if (d.swep !== undefined) p.secondaryWeapon = d.swep;

        if (d.gren !== undefined) p.grenades = d.gren;
        if (d.c4 !== undefined) p.hasC4 = d.c4;
        if (d.items !== undefined) p.items = d.items;

        if (d.name !== undefined) p.name = d.name;
    }

    function setGlobal(delta) {

        // Global state is sent as delta
        // and periodically as full snapshot,
        // so we need ti check if any of the 
        // fields actually changed.
        const g = state.global;

        let changed = false;

        // Handle round time change if needed.
        // "rt" is the supposedly round end tick,
        // which means we can calculate time left by
        // comparing it to the current tick.
        if (delta.rt !== undefined
            && g.roundEndTick !== delta.rt
        ) {
            g.roundEndTick = delta.rt;
            changed = true;
            emit("roundEndTimeChanged", getRoundTimeLeft());
        }

        // Scores are always sent together,
        // i.e., if one is present, both are.
        if (delta.t !== undefined
            && (g.t !== delta.t || g.ct !== delta.ct)
        ) {

            g.t = delta.t;
            g.ct = delta.ct;
            
            changed = true
            ;
            emit("scoreChanged", {
                t: g.t,
                ct: g.ct
            });
        }

        if (delta.map !== undefined
                && g.map !== delta.map
        ) {
            const mapName = delta.map;
            g.map = mapName;
            changed = true;
            handleMapChange(mapName)
            .then(success => {
                if (success)
                    emit("overviewChanged", mapName);
            });
        }

        if (changed) {
            emit("globalUpdated", g);
        }
    }

    function getCurrentGameTime() {

        const t = state.time;

        return t.tick +
            ((performance.now() - t.receivedAt) / 1000);
    }

    function getRoundTimeLeft() {

        const now = getCurrentGameTime();

        return Math.max(
            0,
            Math.ceil(state.global.roundEndTick - now)
        );
    }

    const handleMapChange = (() => {

        let latestMapName = null;
        let loadToken = 0;

        return async function (mapName) {

            if (mapName === latestMapName)
                return false;

            latestMapName = mapName;

            const token = ++loadToken;

            const overview =
                await OverviewAssets.load(mapName);

            if (token !== loadToken) {

                console.warn(
                    `Discarding overview load for ${mapName}`
                );

                return false;
            }

            state.radar.overview = overview;

            return true;
        };

    })();

    function startC4Timer(timeLeft) {
        state.c4.timeLeft = timeLeft;
        state.c4.startedAt = performance.now();
    }

    function c4Exploded() {
        state.c4.timeLeft = null;
        state.c4.startedAt = null;
    }

    return {
        time: state.time,
        global: state.global,
        players: state.players,
        radar: state.radar,
        on,
        upsertPlayer,
        removePlayer,
        setGlobal,
        getRoundTimeLeft,
        startC4Timer,
        c4Exploded,
        c4Running: () => state.c4.timeLeft !== null,
        c4TimeLeft: () => state.c4.timeLeft,
        c4StartedAt: () => state.c4.startedAt
    };

})();