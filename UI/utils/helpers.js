window.TakeLatestAsync = (() => {

    const tasks = new Map();

    const CANCELLED = Symbol("cancelled");

    async function run(taskId, fn, ...args) {

        const execId = (tasks.get(taskId) ?? 0) + 1;

        tasks.set(taskId, execId);

        const result = await fn(...args);

        if (tasks.get(taskId) !== execId)
            return CANCELLED;

        return result;
    }

    return async function(taskId, fn, applyFn, ...args) {

        const result = await run(taskId, fn, ...args);

        if (result === CANCELLED)
            return;

        applyFn(result);
    };

})();