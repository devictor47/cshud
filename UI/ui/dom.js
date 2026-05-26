
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
            playersCanvasEl: document.getElementById("players-canvas"),
        },

        events: {
            container: document.querySelector("#events"),
        }
    };

})();