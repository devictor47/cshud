const Network = (() => {

    const isLocal =
    location.hostname === "localhost" ||
    location.hostname === "127.0.0.1";

    const WS_URL = isLocal
        ? "ws://localhost:5000/ws"
        : "wss://ws.victoroak.site/ws";

    const RECONNECT_DELAY_MS = 2000;

    let socket = null;

    const textDecoder = new TextDecoder();

    const listeners = new Map();

    function emit(event, data) {
        listeners.get(event)?.forEach(fn => fn(data));
    }

    function on(event, callback) {
        if (!listeners.has(event))
            listeners.set(event, []);

        listeners.get(event).push(callback);
    }

    function send(payload) {
        if (socket?.readyState === WebSocket.OPEN)
            socket.send(JSON.stringify(payload));
    }

    function connect() {

        socket = new WebSocket(WS_URL);
        socket.binaryType = "arraybuffer";

        socket.onopen = () => {
            emit("connected");
        };

        socket.onmessage = (event) => {
            try {
                const raw =
                    typeof event.data !== "string"
                        ? textDecoder.decode(event.data)
                        : event.data;

                const data = JSON.parse(raw);

                //Logger.log("[WS] Received data", data);
                
                if (data.servers) {
                    emit("servers", data.servers);
                    return;
                }

                if (data.subscribe === "success") {
                    emit("subscribed", data.id);
                    return;
                }

                if (data.tick) {
                    emit("tick", data.tick);
                }

                if (data.global) {
                    emit("global", data.global);
                }

                if (data.players) {
                    emit("players", data.players);
                }

                if (data.events) {
                    emit("events", data.events);
                }

            } catch (err) {
                console.error("[WS] Invalid JSON", err);
            }
        };

        socket.onerror = (err) => {
            console.error(`[WS] Error: ${err.message}`, err);
            emit("error", err);
        };

        socket.onclose = () => {
            console.warn(`[WS] Disconnected from server.`);

            emit("disconnected");

            setTimeout(connect, RECONNECT_DELAY_MS);
        };
    }

    return {
        connect,
        send,
        on,
    };

})();