
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

    function showC4Timer() {
        DOM.overlays.banner.classList.add("visible");
        DOM.overlays.c4.overlayEl.classList.add("visible");
    }

    function hideC4Timer() {
        DOM.overlays.banner.classList.remove("visible");
        DOM.overlays.c4.overlayEl.classList.remove("visible");
    }

    function setC4TimeLeft(timeSeconds) {
        const seconds = Math.floor(timeSeconds);
        const centiseconds = Math.floor((timeSeconds * 100) % 100);
        DOM.overlays.c4.timerEl.textContent = `
        ${seconds.toString().padStart(2, "0")}
        :${centiseconds.toString().padStart(2, "0")}`;
    }

    return {
        setScore,
        renderRoundTime,
        showC4Timer,
        hideC4Timer,
        setC4TimeLeft,
    };

})();