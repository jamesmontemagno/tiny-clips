export function initCelebration() {
    const layer = document.querySelector("#celebration");
    const reducedMotion = matchMedia("(prefers-reduced-motion: reduce)");
    const animations = new Set();
    function stop() {
        for (const animation of animations) animation.cancel();
        animations.clear();
        layer.replaceChildren();
        layer.hidden = true;
    }
    function burst() {
        stop();
        if (reducedMotion.matches || document.hidden || document.querySelector("dialog[open]")) return;
        layer.hidden = false;
        const width = window.innerWidth;
        const height = window.innerHeight;
        for (let index = 0; index < (width < 640 ? 28 : 48); index++) {
            const piece = document.createElement("span");
            piece.className = "confetti-piece";
            layer.append(piece);
            const direction = index % 2 ? -1 : 1;
            const x = width * (direction === 1 ? .2 : .8);
            const y = Math.min(100, height * .18);
            const distance = 40 + Math.random() * width * .32;
            const finishX = x + direction * distance;
            const finishY = Math.min(height - 16, y + 140 + Math.random() * height * .5);
            const rotation = Math.random() * 180;
            const animation = piece.animate([
                { transform: `translate3d(${x}px, ${y}px, 0) rotate(${rotation}deg)`, opacity: 1 },
                { offset: .3, transform: `translate3d(${x + direction * distance * .7}px, ${y - 40 - Math.random() * 60}px, 0) rotate(${rotation + 120}deg)`, opacity: 1 },
                { transform: `translate3d(${finishX}px, ${finishY}px, 0) rotate(${rotation + 480}deg)`, opacity: 0 },
            ], { duration: 1500 + Math.random() * 300, delay: Math.random() * 100, easing: "cubic-bezier(.2,.65,.45,1)", fill: "both" });
            animations.add(animation);
            animation.onfinish = () => {
                animations.delete(animation);
                piece.remove();
                if (!animations.size) layer.hidden = true;
            };
        }
    }
    reducedMotion.addEventListener("change", () => { if (reducedMotion.matches) stop(); });
    document.addEventListener("visibilitychange", () => { if (document.hidden) stop(); });
    window.addEventListener("pagehide", stop);
    return { burst, stop };
}
