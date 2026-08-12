import os, pathlib, subprocess, json, urllib.request, difflib, re

KEY = os.environ["SPEECH_KEY"]
REGION = "japaneast"
URL = (f"https://{REGION}.stt.speech.microsoft.com/speech/recognition/"
       "conversation/cognitiveservices/v1?language=ja-JP&format=simple")

BASE = pathlib.Path(__file__).parent
VID = BASE / "mimamoritai-demo-narrated.mp4"
TMP = BASE / "stt"
TMP.mkdir(exist_ok=True)

lengths = [10, 12, 14, 10, 14, 10, 14, 10, 14, 10, 22, 11, 14, 15, 11]
starts, t = [], 0
for L in lengths:
    starts.append(t)
    t += L

script = {}
for line in (BASE / "narration.txt").read_text(encoding="utf-8").splitlines():
    if line.strip():
        i, b, txt = line.strip().split("|", 2)
        script[int(i)] = txt


def norm(s):
    return re.sub(r"[、。「」？！\s]", "", s)


def recognize(wav):
    data = wav.read_bytes()
    req = urllib.request.Request(
        URL, data=data,
        headers={
            "Ocp-Apim-Subscription-Key": KEY,
            "Content-Type": "audio/wav; codecs=audio/pcm; samplerate=16000",
            "Accept": "application/json",
        })
    with urllib.request.urlopen(req, timeout=60) as r:
        return json.loads(r.read().decode("utf-8"))


print(f"{'seg':>3} {'sim':>5}  recognised")
worst = 1.0
for i, s in enumerate(starts, start=1):
    dur = min(lengths[i - 1], 14)
    wav = TMP / f"s{i:02d}.wav"
    subprocess.run(
        ["ffmpeg", "-y", "-v", "error", "-ss", str(s), "-t", str(dur),
         "-i", str(VID), "-vn", "-ac", "1", "-ar", "16000",
         "-c:a", "pcm_s16le", str(wav)], check=True)
    res = recognize(wav)
    got = res.get("DisplayText", "")
    exp = script[i]
    # compare only the part of the script the clip can contain
    ratio = difflib.SequenceMatcher(None, norm(exp)[:len(norm(got))],
                                    norm(got)).ratio() if got else 0.0
    worst = min(worst, ratio)
    print(f"{i:>3} {ratio:>5.2f}  {got}")

print(f"\nworst similarity: {worst:.2f}")
