
window.ScoreboardUI = (() => {

    function setScore(ct, t) {
        DOM.scoreboard.scoreEl.textContent = `T ${t} x ${ct} CT`;
    }

    function renderRoundTime(timeSeconds) {
        DOM.scoreboard.timerEl.textContent = formatTime(timeSeconds);
    }

    function formatTime(seconds) {
        const m = Math.floor(seconds / 60);
        const s = Math.floor(seconds % 60);
        return `${m}:${s.toString().padStart(2, '0')}`;
    }


    return {
        setScore,
        renderRoundTime
    };

})();