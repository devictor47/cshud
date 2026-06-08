window.OverviewAssets = (() => {

    const parseOverview = (() => {

        const mapCfgs = {
            "default": {
                mediumHeight: -35, // Mean Z coord of the map that allows for differentiating floor/ceiling in some maps. Adjust as needed per map.
                heightScaleFactor: 0.0012, // How much the player circle changes size based on height difference. Adjust as needed per map.
                minRadiusMult: 0.6, // Player circle cannot shrink past 60% its original size.
                maxRadiusMult: 1.6 // Player circle cannot grow past 160% its original size.
            },
            "de_dust2": {
                mediumHeight: -35,
                heightScaleFactor: 0.0012,
                minRadiusMult: 0.6,
                maxRadiusMult: 1.6
            }
        };

        return (txt, mapName) => {

            const zoomMatch = txt.match(/ZOOM\s+([-\d.]+)/i);
            const originMatch = txt.match(/ORIGIN\s+([-\d.]+)\s+([-\d.]+)\s+([-\d.]+)/i);
            const rotatedMatch = txt.match(/ROTATED\s+(\d+)/i);

            return {
                zoom: zoomMatch ? parseFloat(zoomMatch[1]) : null,
                origin: originMatch ? [
                    parseFloat(originMatch[1]),
                    parseFloat(originMatch[2]),
                    parseFloat(originMatch[3])
                ] : null,
                rotated: rotatedMatch ? parseInt(rotatedMatch[1]) : 0,
                mediumHeight: mapCfgs[mapName]?.mediumHeight ?? mapCfgs["default"].mediumHeight,
                heightScaleFactor: mapCfgs[mapName]?.heightScaleFactor ?? mapCfgs["default"].heightScaleFactor,
                minRadiusMult: mapCfgs[mapName]?.minRadiusMult ?? mapCfgs["default"].minRadiusMult,
                maxRadiusMult: mapCfgs[mapName]?.maxRadiusMult ?? mapCfgs["default"].maxRadiusMult
            };
        };
    })();

    async function loadOverview(mapName) {

        const imagePath = `overviews/${mapName}_ol.png`;
        const imagePathFallback = `overviews/${mapName}.png`;
        const descriptionPath = `overviews/${mapName}.txt`;
        const bgImagePath = `overviews/${mapName}_bg.png`;

        let img;
        try {
            img = await loadImage(imagePath);
        } catch (e1) {

            console.warn(`Failed to load overview image at ${imagePath}, trying fallback...`, e1);

            try {
                img = await loadImage(imagePathFallback);
            }
            catch (e2) {
                console.warn(`Failed to load fallback overview image at ${imagePathFallback}`, e2);
                //throw e2; // Propagate error.
                img = null;
            }
        }

        const [bgImg, descTxt] = await Promise.all([
            loadImage(bgImagePath).catch((e) => {
                console.warn(`Failed to load background image at ${bgImagePath}, proceeding without it...`, e);
                return null;
            }),
            fetch(descriptionPath).then((response) => {
                if (response.ok)
                    return response.text();

                console.warn(`Failed to load overview text at ${descriptionPath}: ${response.status} - ${response.statusText}`);
                return null;
            })
                .catch(e => {
                    console.warn(`Failed to load description text at ${descriptionPath}`, e);
                    return null;
                })
        ]);

        return {
            image: img,
            bgImage: bgImg,
            text: descTxt ? parseOverview(descTxt, mapName) : null,
            attemptedMap: mapName
        };
    }

    return {
        load: loadOverview
    };
})();

window.IconAssets = (() => {

    const cache = new Map();
    
    function missingWeaponIcon() {
        const el = document.createElement("span");
        el.className = "icon icon--missing";
        return el;
    }

    async function loadWeaponIcon(iconName) {

        if (cache.has(iconName))
            return;

        const path =
            `hud/weapons/${iconName}.svg`;

        const res = await fetch(path);

        if (!res.ok)
            throw new Error(`Failed to load ${iconName}`);

        const text = await res.text();

        const template = document.createElement("template");
        template.innerHTML = text;

        const svg = template.content.querySelector("svg");

        svg.style.overflow = "visible";

        svg.classList.add("icon");

        svg.setAttribute(
            "preserveAspectRatio",
            "xMaxYMid meet"
        );

        cache.set(iconName, svg);
    }

    async function preloadWeaponIcons() {

        const loads = [];

        for (const weapon of Object.values(Consts.WEAPONS)) {

            if (!weapon?.iconName)
                continue;

            loads.push(loadWeaponIcon(weapon.iconName));
        }

        await Promise.all(loads);
    }

    function createWeaponIcon(id) {

        const iconName = Consts.WEAPONS[id]?.iconName;

        if (!iconName)
            return missingWeaponIcon();

        switch (id) {

            case Consts.WEAPONS_IDS.HE:
                return createItemIcon(Consts.ITEMS_ICONS.HE);

            case Consts.WEAPONS_IDS.FB:
                return createItemIcon(Consts.ITEMS_ICONS.FB);

            case Consts.WEAPONS_IDS.SMOKE:
                return createItemIcon(Consts.ITEMS_ICONS.SMOKE);

            case Consts.WEAPONS_IDS.C4:
                return createItemIcon(Consts.ITEMS_ICONS.C4);

            default: {

                const svg = cache.get(iconName);

                return svg
                    ? svg.cloneNode(true)
                    : missingWeaponIcon();
            }
        }
    }

    const createItemIcon = (() => {

        const cache = new Map();

        return function (itemData) {

            if (!itemData
                || !itemData.src
                || !itemData.className)
                return null;

            let factory = cache.get(itemData);

            if (!factory) {

                factory = () => {
                    const img = document.createElement("img");
                    img.src = itemData.src;
                    img.className = itemData.className;
                    return img;
                };

                cache.set(itemData, factory);
            }

            return factory();
        };

    })();

    const loadFeedIcon = (() => {

        const cache = new Map();

        const loadFc = async function (icon) {

            if (!icon)
                return;

            const path = icon.path;

            const res = await fetch(path);

            if (!res.ok)
                throw new Error(`Failed to load ${icon.name}`);

            const text = await res.text();

            const template = document.createElement("template");
            template.innerHTML = text;

            const svg = template.content.querySelector("svg");

            svg.style.overflow = "visible";

            svg.classList.add("icon");

            svg.setAttribute(
                "preserveAspectRatio",
                "xMaxYMid meet"
            );

            cache.set(icon.name, svg);            
        };

        loadFc.cache__ = cache;
        return loadFc;
    })();

    async function preloadFeedIcons() {

        const loads = [];

        for (const icon of Object.values(Consts.FEED_ICONS)) {
            loads.push(loadFeedIcon(icon));
        }

        await Promise.all(loads);
    }

    function createFeedIcon(icon) {

        const iconName = icon.name;

        if (!iconName)
            return missingWeaponIcon();

        const svg = loadFeedIcon.cache__.get(iconName);

        return svg
            ? svg.cloneNode(true)
            : missingWeaponIcon();
    }

    async function loadOverviewIcons() {

        const promises  = [];

        Object.values(Consts.OVERVIEW_ICONS).forEach(icon => {
            
            if (icon.src)
                promises .push(loadImage(icon.src));
        });

        const imgs = await Promise.all(promises );

        getOverviewIcon.cache__ ??= new Map();
        imgs.forEach((img, idx) => {
            getOverviewIcon.cache__.set(img.__src, img);
        });        
    }

    function getOverviewIcon(icon) {
        return getOverviewIcon
        .cache__
        .get(icon?.src || icon?.path);
    }

    return {
        VEST_SRC: "hud/vest.png",
        VESTHELM_SRC: "hud/vesthelm.png",
        createWeaponIcon,
        createItemIcon,
        createFeedIcon,
        getOverviewIcon,
        Init: async () => {
            await preloadWeaponIcons();
            await preloadFeedIcons();
            await loadOverviewIcons();
        }
    };
})();

