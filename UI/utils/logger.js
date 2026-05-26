window.Logger = (() => {

    const DEBUG = true;

    const original = {
        log: console.log,
        debug: console.debug,
        warn: console.warn,
        error: console.error
    };

    return {
        debug: (...args) => {
            if (DEBUG) original.debug(...args);
        },

        log: (...args) => {
            if (DEBUG) original.log(...args);
        },

        warn: (...args) => original.warn(...args),
        error: (...args) => original.error(...args)
    };

})();