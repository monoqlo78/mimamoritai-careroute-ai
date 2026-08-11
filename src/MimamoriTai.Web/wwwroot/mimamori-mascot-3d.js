import * as THREE from "./vendor/three/three.module.min.js";
import { GLTFLoader } from "./vendor/three/GLTFLoader.js";

const controllers = new Set();

// 顔寄せのときに画面に入れる高さ（モデルは最大辺が 3.45 になるよう正規化される）。
// 円で切り抜かれるので、頭がちょうど収まる大きさに合わせてある。
const faceWindow = 2.12;

// 頭のてっぺんを画面の上端ぴったりに置くと窮屈なので、少しだけ空ける。
const headroom = 0.06;

// The GLB ships four named clips: MimamoIdle / MimamoFaceIdle (looping) and
// MimamoWave / MimamoBanzai (one-shot reactions).
const idleClip = "MimamoIdle";

// 顔だけはGLBのクリップを使わず、ここでボーンを直接動かす。
// MimamoFaceIdle は 5.04 秒の決まった繰り返しなので、見ているうちに
// 「同じ動きの置物」に見えてくる。瞬きの間隔をその都度変え、
// 話しかけられたときだけ口を動かすほうが、生きているように見える。
//
// GLB の Blink / MouthOpen / Talk モーフは使わない。Blink は影響値を
// -1〜10 まで振って確認したが、まぶたが下りるのではなく目そのものの
// 形が変わるだけで、閉じているようには見えなかったため。
// 代わりにリグの eye_L / eye_R（目の中心にある）と jaw を動かす。

// まぶたを閉じたときに目を縦へ何割つぶすか。1.0 だと線になって消えるので少し残す。
const blinkSquash = 0.93;

// 口は閉じる方向に動かす。このモデルは初期状態が「開いた笑顔」なので、
// jaw を負に回すと閉じ、0 で元の笑顔に戻る（-1.2〜+0.65 で目視確認）。
// しゃべっている間だけ閉じ開きを繰り返し、黙っているときは笑顔のまま。
const jawCloseRad = 0.30;

// しゃべっているとき眉をどれだけ持ち上げるか（モデル座標）。
const browLift = 0.018;

// 人のまばたきは 2〜6 秒に1回くらい。たまに2回続けて閉じる。
const blinkGapMin = 2.2;
const blinkGapMax = 6.4;
const blinkCloseSec = 0.09;
const blinkHoldSec = 0.05;
const blinkOpenSec = 0.14;
const doubleBlinkChance = 0.18;
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
                this.collectFaceBones();

                if (gltf.animations.length > 0) {
                    this.setupAnimations(gltf.animations);
                }

                this.host.classList.add("is-ready");
                this.poster?.setAttribute("aria-hidden", "true");
                this.resize();
                this.renderer.setAnimationLoop(() => this.render());
                this.host.dispatchEvent(new CustomEvent("mascotready"));
                this.greet();
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

        // MimamoFaceIdle はあえて再生しない。ミキサーが毎フレーム表情の値を
        // 上書きしてしまい、こちらで動かした瞬きが打ち消されるため。
        this.idleAction = this.actions.get(idleClip) ?? this.mixer.clipAction(clips[0]);
        this.idleAction.setLoop(THREE.LoopRepeat, Infinity);
        this.idleAction.timeScale = 0.72;
        this.idleAction.play();
        this.bodyAction = this.idleAction;

        this.mixer.addEventListener("finished", (event) => {
            if (event.action === this.bodyAction) this.playIdle();
        });
    }

    // 表情はボーンで動かす。GLB の Blink モーフは目の形を変えるだけで
    // まぶたが閉じないため使わない（-1〜10 まで振って目視確認済み）。
    // eye_L / eye_R は目の中心に置かれているので、Y を潰すとその場で閉じる。
    collectFaceBones() {
        this.faceBones = { eyes: [], brows: [], jaw: null };
        this.model.traverse((node) => {
            if (!node.isBone) return;
            if (node.name === 'eye_L' || node.name === 'eye_R') {
                this.faceBones.eyes.push(node);
            } else if (node.name === 'eyebrow_L' || node.name === 'eyebrow_R') {
                this.faceBones.brows.push({ bone: node, baseY: node.position.y });
            } else if (node.name === 'jaw') {
                this.faceBones.jaw = { bone: node, baseX: node.rotation.x };
            }
        });

        this.blinkAt = this.nextBlinkDelay();
        this.blinkPhase = 0;
        this.blinkQueued = 0;
        this.speakUntil = 0;
        this.faceTime = 0;
    }

    nextBlinkDelay() {
        // ゆっくり見せたい設定のときは、動きが目立たないよう間隔を広げる。
        const scale = this.reducedMotion ? 1.8 : 1;
        return (blinkGapMin + Math.random() * (blinkGapMax - blinkGapMin)) * scale;
    }

    // 0（開いている）〜1（閉じている）を、閉じる→保つ→開くの順に返す。
    blinkValue(t) {
        if (t < blinkCloseSec) return t / blinkCloseSec;
        if (t < blinkCloseSec + blinkHoldSec) return 1;
        const open = (t - blinkCloseSec - blinkHoldSec) / blinkOpenSec;
        return open >= 1 ? -1 : 1 - open;
    }

    updateFace(delta) {
        if (!this.faceBones) return;
        this.faceTime += delta;

        // まばたき
        let blink = 0;
        if (this.blinkPhase > 0) {
            this.blinkPhase += delta;
            const value = this.blinkValue(this.blinkPhase);
            if (value < 0) {
                this.blinkPhase = 0;
                if (this.blinkQueued > 0) {
                    this.blinkQueued--;
                    this.blinkPhase = 0.0001;
                } else {
                    this.blinkAt = this.nextBlinkDelay();
                }
            } else {
                blink = value;
            }
        } else {
            this.blinkAt -= delta;
            if (this.blinkAt <= 0) {
                this.blinkPhase = 0.0001;
                this.blinkQueued = Math.random() < doubleBlinkChance ? 1 : 0;
            }
        }

        this.faceBones.eyes.forEach((bone) => {
            bone.scale.y = 1 - blink * blinkSquash;
        });

        // 口。話している間だけ動かす。ずっとぱくぱくさせると落ち着かない。
        // open = 1 が元の笑顔、0 が閉じた口。
        let open = 1;
        let speaking = 0;
        if (this.faceTime < this.speakUntil && !this.reducedMotion) {
            // 一定の周期だと機械的なので、速さの違う波を重ねて音節のようにする。
            const t = this.faceTime * 2 * Math.PI;
            const wave = 0.5 + 0.32 * Math.sin(t * 7.3) + 0.18 * Math.sin(t * 3.1 + 1.7);
            speaking = 1;

            // 言い終わりは笑顔に戻す。
            const left = this.speakUntil - this.faceTime;
            if (left < 0.25) speaking = left / 0.25;

            open = 1 - speaking * (1 - Math.max(0, Math.min(1, wave)));
        }

        if (this.faceBones.jaw) {
            const { bone, baseX } = this.faceBones.jaw;
            bone.rotation.x = baseX - (1 - open) * jawCloseRad;
        }
        // しゃべっているときだけ眉をわずかに上げる。表情がついて見える。
        this.faceBones.brows.forEach(({ bone, baseY }) => {
            bone.position.y = baseY + speaking * browLift;
        });
    }

    // 返事の長さに合わせて口を動かす。読み上げているように見せるだけで、
    // 音は出さない（音が急に鳴ると驚かせてしまうため）。
    speak(seconds = 2.2) {
        if (!this.faceBones) return;
        this.speakUntil = this.faceTime + Math.max(0.6, Math.min(seconds, 8));
    }

    // 画面を開いた瞬間に、今日の様子に合った出迎え方をする。
    // 落ち着いている日は万歳、気になる日は控えめに、急ぎのときは速く手を振る。
    greet() {
        const mood = this.host.dataset.mascotGreeting;
        if (!mood || this.reducedMotion) return;

        // 読み込み直後に動かすと、文字が出るより先に動いて目が散る。
        // 利用者が状態の文言を読み終えるころに動き出す。
        window.setTimeout(() => this.react(mood), 900);
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

        // 小さな円の中に全身を入れると顔がつぶれて表情が読めない。
        // data-mascot-frame="face" のときは頭のあたりまで寄る。
        // 円で切り抜かれるぶん、四隅は捨てる前提で近づける。
        if (this.host.dataset.mascotFrame === "face") {
            // 距離を決め打ちにするとCGを作り直すたびに寄りすぎ・引きすぎになる。
            // 見せたい高さ（faceWindow）から必要な距離を画角で逆算する。
            const half = THREE.MathUtils.degToRad(this.camera.fov) / 2;
            const z = faceWindow / (2 * Math.tan(half));

            // 頭の上（アンテナの先）を基準にする。顔の位置を割合で決めると、
            // アンテナが伸びただけで頭のてっぺんが切れてしまう。
            const top = bounds.max.y * scale + this.model.position.y;
            const headY = top - faceWindow / 2 + headroom;

            this.camera.position.set(0, headY, z);
            this.camera.lookAt(0, headY, 0);
            return;
        }

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
        // 触ったら応える。ただし気がかりな日に万歳させると、画面の文言と
        // 態度が食い違う。その日の様子に合わせた返し方をする。
        this.host.addEventListener("click", () => this.react(this.host.dataset.mascotGreeting ?? "okay"));
    }

    react(name) {
        // 表情は「動きを控えめに」の設定でも動かす。まばたきまで止めると
        // 具合が悪そうに見えてしまい、伝えたいことと食い違う。
        this.speak(1.8);
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
        this.updateFace(delta);

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
    },
    // 見守りAIが返事をしたときに呼ぶ。文字数から読み上げにかかる時間を
    // ざっくり見積もって、その間だけ口を動かす。
    speak(text) {
        const length = typeof text === "string" ? text.length : Number(text) || 0;
        const seconds = typeof text === "string" ? 0.9 + length * 0.09 : length;
        controllers.forEach((controller) => controller.speak(seconds));
    }
};

document.addEventListener("pointerover", (event) => {
    const reactionTarget = event.target.closest("[data-mascot-reaction]");
    if (reactionTarget) window.mimamoriMascot.react(reactionTarget.dataset.mascotReaction);
});
