window.loadImage = async function(path) {
    return new Promise((resolve, reject) => {
        const image = new Image();
        image.__src = path;
        image.onload = () => resolve(image);
        image.onerror = reject;
        image.src = path;
    });
}