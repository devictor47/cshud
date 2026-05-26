window.RadarUI = (() => {

    const TAU        = Math.PI * 2;   // full circle
    const HALF_PI    = Math.PI / 2;   // 90° offset
    const DEG_TO_RAD = Math.PI / 180; // yaw conversion

    const mapCanvas = DOM.radar.mapCanvasEl;
    const playersCanvas = DOM.radar.playersCanvasEl;

    const mapCtx = mapCanvas.getContext("2d");
    mapCtx.imageSmoothingEnabled = false;

    const playersCtx = playersCanvas.getContext("2d");

    const camera = {
        x: 0,
        y: 0,
        zoom: 1,
        isDragging: false,
        lastMouseX: 0,
        lastMouseY: 0
    };

    const drawPlayerCache = {
        lerpFactor: 0.06,
        baseLerpFactor: 0.06,
        circleRadius: 7,
        fontSize: 11,
        font: `bold ${11}px Segoe UI`,
        defaultFont: "bold 11px Segoe UI",
        outerStrokeWidth: 4,
        whiteStrokeWidth: 2,
        coneLineWidth: 1.5,
        textPadding: 3,
        textOffsetY: 6,
        textsWidth: Object.create(null),
        updateCache (cameraZoom) {
            const invZoom = 1/cameraZoom;
            this.fontSize = 11 * invZoom;
            this.font = `bold ${11 * invZoom}px Segoe UI`;
            this.outerStrokeWidth = 4 * invZoom;
            this.whiteStrokeWidth = 2 * invZoom;
            this.coneLineWidth = 1.5 * invZoom;
            this.textPadding = 3 * invZoom;
            this.textOffsetY = 6 * invZoom;
        }
    };

    const renderState = new Map();

    // Flag to indicate if we need to fully redraw canvases
    // due to zoom/pan changes or new overview.
    let dirty = false; 

    let overview = null;   

    function setOverview(overview_) {
        overview = overview_;
        dirty = true;
    }

    function redraw(force = false) {

        if (force)
            dirty = true;

        if (!dirty) return;

        dirty = false;

        if (!overview) return;

        // Check current size due to resizing
        // and adjust canvas accordingly.
        // This already reset the transformation for us.
        const rect = mapCanvas.getBoundingClientRect();
        if (mapCanvas.width !== rect.width || mapCanvas.height !== rect.height) {
            mapCanvas.width = rect.width;
            mapCanvas.height = rect.height;
            playersCanvas.width = rect.width;
            playersCanvas.height = rect.height;
        }
        else {
            // Reset transforms.
            mapCtx.setTransform(1, 0, 0, 1, 0, 0);
            playersCtx.setTransform(1, 0, 0, 1, 0, 0);
        }

        // Clear everything on both canvases.
        mapCtx.clearRect(0, 0, mapCanvas.width, mapCanvas.height);

        // Apply camera transformations (pan + zoom)
        // ctx.save();
        mapCtx.translate(camera.x, camera.y);
        mapCtx.scale(camera.zoom, camera.zoom);

        if (!overview.image) {

            mapCtx.fillStyle = "rgba(0, 0, 0, 0.8)";
            mapCtx.fillRect(0, 0, mapCanvas.width, mapCanvas.height);

            // Font setup.
            mapCtx.fillStyle = "#FF4444";
            mapCtx.font = "bold 20px Arial";
            mapCtx.textAlign = "center";
            mapCtx.textBaseline = "middle";

            mapCtx.fillText(Consts.LOCALE.overview.notFound, mapCanvas.width / 2, mapCanvas.height / 2 - 15);
            mapCtx.font = "14px Arial";
            mapCtx.fillStyle = "#AAAAAA";
            mapCtx.fillText(`${Consts.LOCALE.map}: ${overview.attemptedMap}`, mapCanvas.width / 2, mapCanvas.height / 2 + 15);
            return;
        }

        if (overview.attemptedMap == "loading") {

            mapCtx.fillStyle = "rgba(0, 0, 0, 0.8)";
            mapCtx.fillRect(0, 0, mapCanvas.width, mapCanvas.height);

            // Font setup.
            mapCtx.fillStyle = "#FF4444";
            mapCtx.font = "bold 20px Arial";
            mapCtx.textAlign = "center";
            mapCtx.textBaseline = "middle";

            mapCtx.fillText(Consts.LOCALE.overview.loading, mapCanvas.width / 2, mapCanvas.height / 2 - 15);
            return;
        }

        mapCtx.drawImage(
            overview.image, // ALWAYS original image
            0, 0, overview.image.width, overview.image.height,
            0, 0, mapCanvas.width, mapCanvas.height
        );

        if (overview.bgImage) {
            DOM.radar.containerEl.style.setProperty('--map-bg', `url("${overview.bgImage.src}")`);
        }
        else {
            DOM.radar.containerEl.style.setProperty('--map-bg', `url("overviews/loading.png")`);
        }

        drawPlayerCache.updateCache(camera.zoom);
    }

    function drawPlayers(players, timeSinceLast) {

        // Clear the canvas.
        playersCtx.clearRect(0, 0, playersCanvas.width, playersCanvas.height);

        // Save current transformations.
        playersCtx.save();

        playersCtx.translate(camera.x, camera.y);
        playersCtx.scale(camera.zoom, camera.zoom);

        // Adjust lerp factor to 60 fps.
        // timeSinceLast * 60 gives how many frames
        // have passed since the last execution
        // considering a 60 fps scenario.
        // At each actual fps (vary by machine), the interval
        // between each call is different, and from that interval
        // we can calculate how many frames passed considering
        // we are aiming for 1 frame each 60 seconds.
        // at 160fps: timeSinceLast = 0.00625, * 60 = 0.375 frames worth of 60fps time
        // at 60fps:  timeSinceLast = 0.01667, * 60 = 1.0 (exactly one frame)
        // at 30fps:  timeSinceLast = 0.03333, * 60 = 2.0 (two frames worth)
        // So how much do we have to move from where we are to the end
        // considering the number of frames that have passed and
        // the fact that we want to move 5% at a time?
        // After 1 frame, the gap remaining is "gap * 0.95" (because we move 5% at a time),
        // i.e., there will be 95% of the gap remaining after 1 frame.
        // After 2 frames: gap * 0.95 * 0.95
        // After 3 frames: gap * 0.95 * 0.95 * 0.95
        // After N frames: gap * 0.95^N
        // So 0.95^N, where N is number of frames passed, gives us
        // what should be the remaining gap. If we subtract that from 1,
        // we can tell the % we would have moved in N frames,
        // which moves exactly as far in one variable-timestep call
        // as N fixed 60fps frames would have moved cumulatively.
        drawPlayerCache.lerpFactor = 
        1 - Math.pow(1 - drawPlayerCache.baseLerpFactor, timeSinceLast * 60);

        Object.values(players).forEach(player => {
            drawPlayer(player);
        });

        playersCtx.restore();
    }

    function drawPlayer(player) {

        if (!overview
            || !overview.image
            || overview.attemptedMap === "loading"
            || (player.team !== 1 && player.team !== 2)) {
            return;
        }

        let playerRenderState = renderState.get(player.id);

        if (!playerRenderState) {
            // This will make players quickly reach
            // their position from the center.
            // If we initialize this to current world
            // position, then it will just render at
            // correct position at first.
            playerRenderState = {
                renderX: 0,
                renderY: 0,
                renderZ: 0,
                renderYaw: 0
            };
            renderState.set(player.id, playerRenderState);
        }

        // Too high a factor will cause a choppy/jittery effect,
        // and too low will make it look like players are slipping on ice.      
        const lerpFactor = drawPlayerCache.lerpFactor;
        playerRenderState.renderX = lerp(playerRenderState.renderX, player.x, lerpFactor);
        playerRenderState.renderY = lerp(playerRenderState.renderY, player.y, lerpFactor);
        playerRenderState.renderZ = lerp(playerRenderState.renderZ, player.z, lerpFactor);

        const pos = worldToCanvas(
            playerRenderState.renderX,
            playerRenderState.renderY);

        // Calculate Z difference from medium height to determine scaling.
        const zDelta = playerRenderState.renderZ - overview.text.mediumHeight;

        // Map Z delta to scale the circle size,
        // with limits to prevent it from being too small or too large.
        let scaleMult = 1 + (zDelta * overview.text.heightScaleFactor);

        // Clamp the multiplier so it respects overview limits.
        scaleMult = Math.min(
            Math.max(overview.text.minRadiusMult, scaleMult),
            overview.text.maxRadiusMult);

        //const jumpMult = player.jumpEffect || 1.0;

        // Define final radius considering the jump multiplier.
        let r =  drawPlayerCache.circleRadius * scaleMult; // * jumpMult;

        if (player.isDead) {
            playersCtx.globalAlpha = 0.60;
            // TODO - consider introducint a timestamp cache
            // to remove dead players from the map after a while.
        }
        else {
            playersCtx.globalAlpha = 1.0;
        }

        // Player vision cone.
        if (!player.isDead) {

            playerRenderState.renderYaw = lerpAngle(playerRenderState.renderYaw, player.yaw, lerpFactor);

            playersCtx.save();
            playersCtx.translate(pos.x, pos.y);

            // Converte Graus para Radianos
            // No CS 1.6, v_angle[1] = 0 costuma ser o eixo X (Direita)
            // Se o cone estiver apontando para o lado errado, adicione ou subtraia 90 ou 180 aqui.
            const angleRad = playerRenderState.renderYaw * DEG_TO_RAD;
            const offset = overview.text.rotated ? 0 : HALF_PI;

            // Rotacionamos o contexto
            // IMPORTANTE: O sinal de negativo depende se o seu sistema Y do Canvas 
            // está invertido em relação ao jogo (o que geralmente está).
            playersCtx.rotate(-angleRad - offset);

            // Desenha o Cone (Triângulo ou Arco)
            const coneLen = r * 5; // Comprimento do cone
            const fov = 0.785398;  // Abertura do cone em radianos (45 graus)

            const grad = playersCtx.createRadialGradient(0, 0, r, 0, 0, coneLen);
            grad.addColorStop(0, player.team === 2 ? 'rgba(1, 110, 208, 1)' : 'rgba(255, 143, 0, 1)');
            grad.addColorStop(1, player.team === 2 ? 'rgba(1, 110, 208, 0.15)' : 'rgba(255, 143, 0, 0.15)');

            playersCtx.beginPath();
            playersCtx.moveTo(0, 0);
            playersCtx.arc(0, 0, coneLen, -fov, fov);
            playersCtx.closePath();
            playersCtx.fillStyle = grad;
            playersCtx.fill();
            playersCtx.strokeStyle = `rgba(255, 255, 255, 0.5)`; // Branca semi-transparente
            playersCtx.lineWidth = drawPlayerCache.coneLineWidth; // Espessura constante independente do zoom
            playersCtx.lineJoin = "round"; // Bordas arredondadas nos cantos
            playersCtx.stroke();

            playersCtx.restore();
        }

        // Player circle.
        // Keep size constant regardless of zoom by dividing by it.
        playersCtx.beginPath();
        playersCtx.arc(pos.x, pos.y, r, 0, TAU);
        playersCtx.fillStyle = player.isDead
            ? '#666'
            : player.team === 2
                ? '#016ed0'
                : '#ff8f00';

        // TODO - Maybe add extra effects depending on hp, etc.
        playersCtx.fill();

        // Outer black stroke
        playersCtx.strokeStyle = "black";
        playersCtx.lineWidth = drawPlayerCache.outerStrokeWidth;
        playersCtx.stroke();

        // White stroke between the two
        playersCtx.strokeStyle = "white";
        playersCtx.lineWidth = drawPlayerCache.whiteStrokeWidth;
        playersCtx.stroke();

        // Drawing name.
        // Adjust font size based on zoom to
        // keep it readable and consistent.
        const fontSize = drawPlayerCache.fontSize;

        playersCtx.font = drawPlayerCache.font;
        playersCtx.lineJoin = "round";

        const padding = drawPlayerCache.textPadding;
        const offsetY = r + drawPlayerCache.textOffsetY;

        // Measure text
        const textWidth = getTextWidthAtDefaultZoom(playersCtx, player.name) / camera.zoom;
        const textHeight = fontSize;

        // Preferred position above the player
        let textX = pos.x;
        let textY = pos.y - offsetY;

        // 1. Flip vertically ONLY if
        // completely out of top
        if (textY - textHeight < 0) {
            textY = pos.y + offsetY;
        }

        // // 2. Clamp vertically (bottom edge safety)
        if (textY > playersCanvas.height - padding) {
            textY = playersCanvas.height - padding;
        }

        // 3. Horizontal clamping + dynamic alignment
        let left = textX - textWidth / 2;
        let right = textX + textWidth / 2;

        if (left < padding) {
            playersCtx.textAlign = "left";
            textX = padding;
        }
        else if (right > playersCanvas.width - padding) {
            playersCtx.textAlign = "right";
            textX = playersCanvas.width - padding;
        }
        else {
            playersCtx.textAlign = "center";
        }

        playersCtx.strokeStyle = "black";
        playersCtx.lineWidth = drawPlayerCache.whiteStrokeWidth;
        playersCtx.lineJoin = "round";
        playersCtx.strokeText(player.name, textX, textY);

        playersCtx.fillStyle = "white";
        playersCtx.fillText(player.name, textX, textY);

    }

    function worldToCanvas(playerX, playerY) {

        if (!overview || !overview.text)
            return { x: 0, y: 0 };

        const worldSize = 8192.0; // Max GoldSrc map size in any direction.
        const zoom = overview.text.zoom;
        const origin = overview.text.origin;

        // Forced on a 4:3 on CSS to match the game, originally.
        const aspect = playersCanvas.width / playersCanvas.height;

        const viewWidth = worldSize / zoom;

        // From a known width, calculate the height
        // kowning the aspect ratio:
        //     4/3 = width/height
        // ->  height = width/(4/3)
        const viewHeight = viewWidth / aspect;

        let ny, nx;

        if (!overview.text.rotated) {
            ny = -((playerX - origin[0]) / viewHeight);
            nx = -((playerY - origin[1]) / viewWidth);
        } else {
            nx = ((playerX - origin[0]) / viewWidth);
            ny = -((playerY - origin[1]) / viewHeight);
        }

        return {
            x: (nx + 0.5) * playersCanvas.width,
            y: (ny + 0.5) * playersCanvas.height
        };
    }

    function lerp(start, end, factor) {
        // Move "factor"% towards "end".
        // If start = 0, end = 100, and factor is 5%,
        // then return start + 5% to end = 5.
        return start + (end - start) * factor;
    }

    function lerpAngle(start, end, factor) {

        let delta = end - start;

        // Adjust between -180 and 180
        if (delta > 180) delta -= 360;
        if (delta < -180) delta += 360;

        return start + delta * factor;
    }

    function getTextWidthAtDefaultZoom(ctx, name) {
        if (drawPlayerCache.textsWidth[name] !== undefined)
            return drawPlayerCache.textsWidth[name];
        ctx.font = drawPlayerCache.defaultFont;
        return (drawPlayerCache.textsWidth[name] = ctx.measureText(name).width);
    }

    ///////////////////////////
    // Canvas event handlers //
    ///////////////////////////

    const observer = new ResizeObserver(() => {
        dirty = true;
    });

    function zoomOnWheel(e) {

        e.preventDefault();

        if (!overview
            || !overview.image
            || overview.attemptedMap == "loading")
            return false;

        const zoomIntensity = 0.1;
        const delta = e.deltaY > 0 ? -zoomIntensity : zoomIntensity;
        const oldZoom = camera.zoom;

        // Limit zoom from 1x to 5x
        const newZoom = Math.min(Math.max(1, camera.zoom + delta), 5);

        // If it didn't change, no need to recalculate
        if (newZoom === oldZoom)
            return false;

        // Zoom at mouse position
        const rect = mapCanvas.getBoundingClientRect();
        const mouseCanvasX = e.clientX - rect.left;
        const mouseCanvasY = e.clientY - rect.top;

        // Mouse location in world considering current zoom and pan
        const mouseWorldX = (mouseCanvasX - camera.x) / oldZoom;
        const mouseWorldY = (mouseCanvasY - camera.y) / oldZoom;

        // Apply new zoom
        camera.zoom = newZoom;

        // Calculate where the same WORLD point would be with the NEW zoom
        // without moving the camera (hypothetical x=0, y=0)
        const newMouseCanvasX = mouseWorldX * newZoom;
        const newMouseCanvasY = mouseWorldY * newZoom;

        // Adjust camera pan so that the world point under
        // the mouse stays consistent before and after zoom
        camera.x = mouseCanvasX - newMouseCanvasX;
        camera.y = mouseCanvasY - newMouseCanvasY;

        // Reset pan if zoom is 1 (fully visible)
        if (camera.zoom <= 1) {
            camera.x = 0;
            camera.y = 0;
            releaseCanvas();
        }
        else {
            if (camera.isDragging)
                setCursorGrabbing();
            else
                setCursorGrab();
        }

        return true;
    };

    function setCursorGrab() {
        mapCanvas.parentElement.style.cursor = 'grab';
    }

    function setCursorGrabbing() {
        mapCanvas.parentElement.style.cursor = 'grabbing';
    }

    function resetCursor() {
        mapCanvas.parentElement.style.cursor = 'default';
    }

    function grabCanvas (e) {
        if (camera.zoom > 1) {
            camera.isDragging = true;
            camera.lastMouseX = e.clientX;
            camera.lastMouseY = e.clientY;
            setCursorGrabbing();
        }
    };

    function releaseCanvas() {
        camera.isDragging = false;

        if (camera.zoom <= 1) {
            resetCursor();
        }
        else {
            setCursorGrab();
        }
    };

    function panCanvas(e) {
        if (camera.isDragging) {
            // Move camera proportional
            // to how much the mouse moved
            camera.x += e.clientX - camera.lastMouseX;
            camera.y += e.clientY - camera.lastMouseY;
            camera.lastMouseX = e.clientX;
            camera.lastMouseY = e.clientY;
            return true;
        }
        return false;
    };

    mapCanvas.parentElement.addEventListener("wheel",
        (e) => {
            if (zoomOnWheel(e))
                dirty = true;
    });

    mapCanvas.parentElement.addEventListener("mousemove",  (e) => {
            if (panCanvas(e))
                dirty = true;
    });

    mapCanvas.parentElement.addEventListener("mousedown", grabCanvas);
    mapCanvas.parentElement.addEventListener("mouseup", releaseCanvas);

    observer.observe(mapCanvas);

    return {
        setOverview,
        redraw,
        drawPlayers
    }
})();