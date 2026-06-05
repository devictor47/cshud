
window.DOM = (() => {

    return {
        scoreboard: {
            scoreEl: document.getElementById("score"),
            timerEl: document.querySelector(".stream-grid-footer.timer span"),
        },

        players: {
            tListEl: document.querySelector("#t-column .team-list"),
            ctListEl: document.querySelector("#ct-column .team-list"),
            tCountEl: document.getElementById("t-count"),
            ctCountEl: document.getElementById("ct-count"),
        },

        radar: {
            containerEl: document.getElementById('map-column'),
            mapCanvasEl: document.getElementById("map-canvas"),
            entitiesCanvasEl: document.getElementById("entities-canvas"),
        },

        chat: {
            sayEl: document.getElementById("say-chat"),
            tChatEl: document.getElementById("t-chat"),
            ctChatEl: document.getElementById("ct-chat"),
        },

        overlays: {
            banner: document.getElementById("overlay-banner"),
            c4: {
                overlayEl: document.getElementById("c4-overlay"),
                timerEl: document.getElementById("c4-timer")
            }
        }
    };

})();