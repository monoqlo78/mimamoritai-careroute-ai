import * as THREE from "./vendor/three/three.module.min.js";
import { GLTFLoader } from "./vendor/three/GLTFLoader.js";

const controllers = new Set();

// The GLB ships four named clips: MimamoIdle / MimamoFaceIdle (looping) and
// MimamoWave / MimamoBanzai (one-shot reactions).
const idleClip = "MimamoIdle";
const faceIdleClip = "MimamoFaceIdle";
const reactions = {
    status: { clip: "MimamoWave", speed: 1 },
    contact_family: { clip: "MimamoWave", speed: 0.9 },
    unwell: { clip: "MimamoWave", speed: 0.72 },
    emergency: { clip: "MimamoWave", speed: 1.45 },
    concern: { clip: "MimamoWave", speed: 0.75 },
    okay: { clip: "MimamoBanzai", speed: 1.08 },
    celebrate: { clip: "MimamoBanzai", speed: 1.2 }
};

class MascotController {
    constructor(host) {
        this.host = host;
        this.canvas = host.querySelector("canvas");
        this.poster = host.querySelector(".mascot-poster");
        this.clock = new THREE.Clock();
        this.reducedMotion = window.matchMedia("(prefers-reduced-motion: reduce)").matches;

        this.scene = new THREE.Scene();
        this.camera = new THREE.PerspectiveCamera(28, 1, 0.1, 100);
        this.renderer = new THREE.WebGLRenderer({
            canvas: this.canvas,
            alpha: true,
            antialias: true,
            powerPreference: "high-performance"
        });
        this.renderer.setPixelRatio(Math.min(window.devicePixelRatio, 2));
        this.renderer.outputColorSpace = THREE.SRGBColorSpace;
        this.renderer.setClearAlpha(0);

        this.stage = new THREE.Group();
        this.scene.add(this.stage);
        this.scene.add(new THREE.HemisphereLight(0xffffff, 0xd8ecec, 1.5));
        const keyLight = new THREE.DirectionalLight(0xffffff, 2.1);
        keyLight.position.set(-3, 4, 6);
        this.scene.add(keyLight);
        const fillLight = new THREE.DirectionalLight(0xdff4f2, 1);
        fillLight.position.set(4, 1, 3);
        this.scene.add(fillLight);

        this.resizeObserver = new ResizeObserver(() => this.resize());
        this.resizeObserver.observe(host);
        this.bindPointerReaction();
        this.load();
    }

    load() {
        new GLTFLoader().load(
            this.host.dataset.mascotModel,
            (gltf) => {
                this.model = gltf.scene;
                this.stage.add(this.model);
                this.frameModel();

                if (gltf.animations.length > 0) {
                    this.setupAnimations(gltf.animations);
                }

                this.host.classList.add("is-ready");
                this.poster?.setAttribute("aria-hidden", "true");
                this.resize();
                this.renderer.setAnimationLoop(() => this.render());
                this.host.dispatchEvent(new CustomEvent("mascotready"));
            },
            undefined,
            (error) => {
                console.error("Mascot GLB failed to load.", error);
                this.host.classList.add("is-fallback");
            });
    }

    setupAnimations(clips) {
        this.mixer = new THREE.AnimationMixer(this.model);
        this.actions = new Map();
        clips.forEach((clip) => this.actions.set(clip.name, this.mixer.clipAction(clip)));

        // Face idle runs continuously alongside whichever body clip is active.
        const face = this.actions.get(faceIdleClip);
        if (face) {
            face.setLoop(THREE.LoopRepeat, Infinity).play();
        }

        this.idleAction = this.actions.get(idleClip) ?? this.mixer.clipAction(clips[0]);
        this.idleAction.setLoop(THREE.LoopRepeat, Infinity);
        this.idleAction.timeScale = 0.72;
        this.idleAction.play();
        this.bodyAction = this.idleAction;

        this.mixer.addEventListener("finished", (event) => {
            if (event.action === this.bodyAction) this.playIdle();
        });
    }

    playIdle() {
        if (!this.idleAction || this.bodyAction === this.idleAction) return;
        this.idleAction.reset().setEffectiveWeight(1).play();
        this.bodyAction.crossFadeTo(this.idleAction, 0.35, false);
        this.bodyAction = this.idleAction;
    }

    frameModel() {
        const bounds = new THREE.Box3().setFromObject(this.model);
        const size = bounds.getSize(new THREE.Vector3());
        const center = bounds.getCenter(new THREE.Vector3());
        const scale = 3.45 / Math.max(size.x, size.y, size.z);

        this.model.scale.setScalar(scale);
        this.model.position.set(-center.x * scale, -center.y * scale - 0.12, -center.z * scale);
        this.camera.position.set(0, 0.15, 8.7);
        this.camera.lookAt(0, 0.1, 0);
    }

    resize() {
        const width = Math.max(this.host.clientWidth, 1);
        const height = Math.max(this.host.clientHeight, 1);
        this.renderer.setSize(width, height, false);
        this.camera.aspect = width / height;
        this.camera.updateProjectionMatrix();
    }

    bindPointerReaction() {
        this.host.addEventListener("pointermove", (event) => {
            if (this.reducedMotion) return;
            const rect = this.host.getBoundingClientRect();
            this.pointerX = ((event.clientX - rect.left) / rect.width - 0.5) * 0.22;
            this.pointerY = ((event.clientY - rect.top) / rect.height - 0.5) * 0.1;
        });
        this.host.addEventListener("pointerleave", () => {
            this.pointerX = 0;
            this.pointerY = 0;
        });
        this.host.addEventListener("click", () => this.react("okay"));
    }

    react(name) {
        if (!this.mixer || this.reducedMotion) return;
        const reaction = reactions[name] ?? reactions.status;
        const next = this.actions.get(reaction.clip);
        if (!next || next === this.bodyAction) return;

        next.reset();
        next.timeScale = reaction.speed;
        next.setLoop(THREE.LoopOnce, 1);
        next.clampWhenFinished = true;
        next.setEffectiveWeight(1).play();
        this.bodyAction.crossFadeTo(next, 0.25, false);
        this.bodyAction = next;
    }

    render() {
        if (!this.model) return;

        const delta = Math.min(this.clock.getDelta(), 0.05);
        if (!this.reducedMotion && this.mixer) this.mixer.update(delta);

        this.stage.rotation.y += ((this.pointerX ?? 0) - this.stage.rotation.y) * 0.055;
        this.stage.rotation.x += ((this.pointerY ?? 0) - this.stage.rotation.x) * 0.055;
        this.renderer.render(this.scene, this.camera);
    }
}

window.mimamoriMascot = {
    init() {
        document.querySelectorAll("[data-mascot-model]:not([data-mascot-ready])").forEach((host) => {
            host.dataset.mascotReady = "true";
            controllers.add(new MascotController(host));
        });
    },
    react(name) {
        controllers.forEach((controller) => controller.react(name));
    }
};

document.addEventListener("pointerover", (event) => {
    const reactionTarget = event.target.closest("[data-mascot-reaction]");
    if (reactionTarget) window.mimamoriMascot.react(reactionTarget.dataset.mascotReaction);
});
