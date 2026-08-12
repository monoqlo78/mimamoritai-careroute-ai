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

// 口を開け閉めする速さ（1秒あたりの回数）。
// ここは以前 7.3 と 3.1 だった。人の音節に合わせたつもりの数字だが、
// 顔だけを大写しにしている画面で 1秒に7回も開閉すると、
// 「しゃべっている」ではなく「口だけが高速で震えている」ように見える。
// 実際に「ものすごい勢いで口だけ動く」と報告された。
// ゆっくり話しかけるくらいの速さまで落とす。
const speakWaveHz = 2.2;
const speakWaveSubHz = 1.1;

// 黙っている間の口。10秒に1回くらい、軽く「ぱくぱく」と動かす。
// 口元が完全に固まっていると、微笑んだ静止画にしか見えないため。
// ここは「しゃべっている」ように見せる場所ではないので、
// 深さも速さもしゃべるときよりはっきり控えめにしてある。
// この待ち時間は「ぱくぱくが終わってから次が始まるまで」なので、
// 実際の周期は ここ + idleMouthCycles / idleMouthHz（約1.5秒）になる。
// 「10秒に1回」に合わせるため、待ち時間そのものは 10秒より短くしてある。
const idleMouthGapMin = 7.0;
const idleMouthGapMax = 9.5;
const idleMouthHz = 1.3;
const idleMouthCycles = 2;
const idleMouthDepth = 0.4;

// しゃべっているとき眉をどれだけ持ち上げるか（モデル座標）。
const browLift = 0.018;

// 1フレームで進める時間の上限。タブを裏にしていた間の時間をまとめて
// 進めてしまわないための保険。
//
// ここは以前 0.05 だった。しかしそれだと 20fps を下回った瞬間から
// 「実時間より遅い時計」になる。実測では、1ページに2つある canvas を
// ソフトウェア描画で回すと 6.7fps まで落ち、顔の時計が実時間の約1/3で
// しか進まなかった。その結果 2〜6 秒のはずの瞬きの間隔が 30 秒以上に
// 伸び、「しばらくすると瞬きが止まる」ように見えていた。
// 裏に回っていた間を飛ばす役目は 0.25 でも十分果たせる。
const maxFrameDelta = 0.25;

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
        this.bindContextRecovery();
        this.bindVisibility();
        this.load();
    }

    // このページには顔だけの hero と、相談相手の2箇所に同じビューアが載る。
    // 両方を常に描くと、画面に映っていないほうにも同じだけ GPU を使う。
    // 実測でも canvas 2枚がどちらも全力で回っていた。
    // 映っていないほうは描かない。まばたきの間隔も進めない（戻ってきた
    // ときに溜まった分が一気に消化されて、まばたきが連発するのを防ぐ）。
    bindVisibility() {
        this.onScreen = true;
        if (typeof IntersectionObserver !== "function") return;

        this.visibilityObserver = new IntersectionObserver(
            (entries) => {
                entries.forEach((entry) => {
                    this.onScreen = entry.isIntersecting;
                    // 止めている間に進んだ実時間を捨てる。捨てないと復帰の
                    // 1コマ目に巨大な delta が入って動きが飛ぶ。
                    if (this.onScreen) this.clock.getDelta();
                });
            },
            { rootMargin: "120px" },
        );
        this.visibilityObserver.observe(this.host);
    }

    // GPUの再起動やドライバの復帰、タブの切り替えで WebGL の文脈は失われる。
    // three.js 自身は復帰処理を持っているが、失っている間 canvas は最後の
    // コマのまま固まる。3Dが「動かなくなった」と見えるのはたいていこれで、
    // 固まった3Dを見せ続けるより静止画に戻したほうが壊れて見えない。
    bindContextRecovery() {
        this.canvas.addEventListener("webglcontextlost", (event) => {
            // 既定のままだとブラウザは文脈を復帰させない。止めておく。
            event.preventDefault();
            this.contextLost = true;
            this.host.classList.remove("is-ready");
            this.host.classList.add("is-fallback");
            console.warn("Mascot: WebGL context lost - falling back to the still image.");
        });

        this.canvas.addEventListener("webglcontextrestored", () => {
            this.contextLost = false;
            this.host.classList.remove("is-fallback");
            if (this.model) this.host.classList.add("is-ready");
            this.resize();
            console.info("Mascot: WebGL context restored.");
        });
    }

    // ホストがDOMから外れたあとも動き続けると、見えない canvas を GPU が
    // 回し続けたうえ、次に現れたホストと合わせて二重に描くことになる。
    // 世帯を切り替えるたびに @if の分岐が作り直されるので、ここを持たないと
    // 切り替えた回数だけコントローラが積み上がる。
    dispose() {
        this.disposed = true;
        this.renderer.setAnimationLoop(null);
        this.resizeObserver?.disconnect();
        this.visibilityObserver?.disconnect();
        this.renderer.dispose();
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
        this.mouthAt = this.nextIdleMouthDelay();
        this.mouthPhase = 0;
        this.faceTime = 0;
    }

    // 黙っている間、次に軽く口を動かすまでの秒数。
    // 毎回きっちり10秒だと時計のように見えるので、少しばらつかせる。
    nextIdleMouthDelay() {
        return idleMouthGapMin + Math.random() * (idleMouthGapMax - idleMouthGapMin);
    }

    nextBlinkDelay() {
        // ゆっくり見せたい設定のときは、間隔をすこしだけ広げる。
        // ここは以前 1.8 倍だった。まばたきが唯一の動きになる設定では、
        // それだと 4〜11.5 秒に1回・1回 0.28 秒しか動かないことになり、
        // ほぼ確実に見逃されて「完全に静止している」と受け取られる。
        const scale = this.reducedMotion ? 1.25 : 1;
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
            // 1フレームで進める量を「閉じ切って保っている最中」までに抑える。
            // まばたきは close 0.09 + hold 0.05 + open 0.14 = 0.28 秒しかないので、
            // fps が落ちて delta が 0.28 に近づくと、閉じかけと開きかけの
            // 間を1フレームで飛び越えてしまい、閉じた顔が一度も描かれない。
            // 「瞬きが出るときと出ないときがある」の正体はこれ。
            // ここで頭打ちにしておけば、どんな低fpsでも必ず1フレームは
            // 完全に閉じた目が描かれる。60fps なら delta≒0.016 なので
            // この上限には当たらず、通常時の見え方は変わらない。
            this.blinkPhase += Math.min(delta, blinkCloseSec + blinkHoldSec * 0.5);
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

        // 口。しゃべっている間と、黙っているときの軽いぱくぱくで動かす。
        // open = 1 が元の笑顔、0 が閉じた口。
        //
        // 「動きを控えめに」の設定でも口は動かす。まばたきと同じ理由で、
        // 目も口も止めてしまうと画面が流れる類の負担は減らないのに、
        // 話しかけた返事だけが無表情の静止画から返ってくることになる。
        // 体のアイドル・出迎え・視線追従といった大きく動くものは止めたまま。
        let open = 1;
        let speaking = 0;
        if (this.faceTime < this.speakUntil) {
            // 一定の周期だと機械的なので、速さの違う波を重ねて音節のようにする。
            this.mouthPhase = 0;
            const t = this.faceTime * 2 * Math.PI;
            const wave = 0.5 + 0.32 * Math.sin(t * speakWaveHz) + 0.18 * Math.sin(t * speakWaveSubHz + 1.7);
            speaking = 1;

            // 言い終わりは笑顔に戻す。
            const left = this.speakUntil - this.faceTime;
            if (left < 0.25) speaking = left / 0.25;

            open = 1 - speaking * (1 - Math.max(0, Math.min(1, wave)));

            // しゃべり終わったら、間を置いてから次のぱくぱくにする。
            this.mouthAt = this.nextIdleMouthDelay();
        } else if (this.mouthPhase > 0) {
            // 黙っているときのぱくぱく。閉じて開くを idleMouthCycles 回。
            // cos は両端が 0 で傾きも 0 なので、始まりも終わりも
            // 笑顔からなめらかに出入りする（別途フェードを掛けなくてよい）。
            this.mouthPhase += delta;
            const duration = idleMouthCycles / idleMouthHz;
            if (this.mouthPhase >= duration) {
                this.mouthPhase = 0;
                this.mouthAt = this.nextIdleMouthDelay();
            } else {
                const cycle = 0.5 - 0.5 * Math.cos(this.mouthPhase * idleMouthHz * 2 * Math.PI);
                open = 1 - idleMouthDepth * cycle;
            }
        } else {
            this.mouthAt -= delta;
            if (this.mouthAt <= 0) this.mouthPhase = 0.0001;
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
        if (this.disposed || !this.model || this.contextLost) return;
        if (this.onScreen === false) return;

        // setAnimationLoop は「描き終えてから」次のフレームを予約する。
        // つまりここで一度でも例外が出るとループは二度と回らず、顔が
        // 固まったまま戻らない。3Dは装飾なので、握って回し続ける。
        try {
            const delta = Math.min(this.clock.getDelta(), maxFrameDelta);
            if (!this.reducedMotion && this.mixer) this.mixer.update(delta);
            this.updateFace(delta);

            this.stage.rotation.y += ((this.pointerX ?? 0) - this.stage.rotation.y) * 0.055;
            this.stage.rotation.x += ((this.pointerY ?? 0) - this.stage.rotation.x) * 0.055;
            this.renderer.render(this.scene, this.camera);
        } catch (error) {
            // 毎フレーム出すとコンソールが埋まって他の原因が読めなくなる。
            if (!this.renderErrorLogged) {
                this.renderErrorLogged = true;
                console.error("Mascot render failed; keeping the animation loop alive.", error);
            }
        }
    }
}

// ホストを拾って動かす。DOMに現れたぶんだけ起こし、消えたぶんは片付ける。
//
// ページ側は「最初の描画のあと」に一度 init を呼ぶだけだが、マスコットは
// `@if (_model is null) { 読み込み中 } else { ... }` の中にある。つまり
// その一度の呼び出しの時点ではホストはまだ存在せず、走査は何にも当たらない。
// 本番で実際にそうなっていた（three.js は読み込み済み、GLBの取得は0回、
// 静止画のまま）。呼ぶ側に正しい瞬間を探させるのをやめ、現れたら気づく。
//
// コールバックは属性セレクタ1回ぶんの走査で、1フレームにまとめる。Blazor の
// ように DOM がよく動くページでも実質的な負担にならない。
let scanQueued = false;
let hostObserver = null;

function scanForHosts() {
    scanQueued = false;

    controllers.forEach((controller) => {
        if (controller.host.isConnected) return;
        controller.dispose();
        controllers.delete(controller);
    });

    document.querySelectorAll("[data-mascot-model]:not([data-mascot-ready])").forEach((host) => {
        // 監視は DOM が組み上がる途中でも動く。中身が揃う前に掴むと、
        // canvas を持たないまま画面外のバッファを回すことになる。印を
        // 付けずに見送れば、揃ったときの変化で次の走査が拾う。
        if (!host.dataset.mascotModel || !host.querySelector("canvas")) return;

        host.dataset.mascotReady = "true";
        controllers.add(new MascotController(host));
    });
}

function watchForHosts() {
    if (hostObserver || !document.body) return;

    hostObserver = new MutationObserver(() => {
        if (scanQueued) return;
        scanQueued = true;
        requestAnimationFrame(scanForHosts);
    });

    hostObserver.observe(document.body, { childList: true, subtree: true });
}

window.mimamoriMascot = {
    init() {
        scanForHosts();
        watchForHosts();
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
