"""Run MediaPipe Objectron (Chair, legacy solution) on a recorded session's frames on the PC.

Writes <session>/pc_objectron_chair.jsonl in the device detections schema plus, per detection,
"keypoints" (9 viewport points, origin bottom-left, MediaPipe order: centre + 8 corners) and
"lifted" (MediaPipe's up-to-scale 3D keypoints converted to the Unity camera frame: x, y, -z).
The replay (Tools/offline/run_tests.sh replay ... --detections pc_objectron_chair.jsonl) feeds them to the
app's BoxSolver with the recorded depth, exactly like the Objectron path on the headset.
Same settings as the app: tracking mode, max 4 objects, detection 0.40, tracking 0.60, image not mirrored.
"""
import json, sys, os, time
import cv2
import mediapipe as mp

def main(session, max_objects=4):
    frames = [json.loads(l) for l in open(os.path.join(session, "frames.jsonl"))]
    cams = {}
    pc = [f for f in os.listdir(session) if f.startswith("pc_onnx") and f.endswith(".jsonl")]
    src = os.path.join(session, pc[0] if pc else "detections.jsonl")
    for l in open(src):
        r = json.loads(l)
        cams[r["frame_id"]] = (r["camera"], r.get("t_realtime"), r.get("unix_ms"))
    out_path = os.path.join(session, "pc_objectron_chair.jsonl")
    obj = mp.solutions.objectron.Objectron(static_image_mode=False, max_num_objects=max_objects,
                                           min_detection_confidence=0.4, min_tracking_confidence=0.6, model_name="Chair")
    n = nd = 0
    t0 = time.time()
    with open(out_path, "w") as w:
        for f in frames:
            fid = f["frame_id"]
            if fid not in cams:
                continue
            img = cv2.imread(os.path.join(session, f["file"]))
            if img is None:
                continue
            res = obj.process(cv2.cvtColor(img, cv2.COLOR_BGR2RGB))
            dets = []
            for o in (res.detected_objects or []):
                kp = [[lm.x, 1.0 - lm.y] for lm in o.landmarks_2d.landmark]
                lifted = [[lm.x, lm.y, -lm.z] for lm in o.landmarks_3d.landmark]
                xs = [min(max(p[0], 0), 1) for p in kp]; ys = [min(max(p[1], 0), 1) for p in kp]
                dets.append({"label": "chair", "category": "Chair", "class_id": -1, "score": 1.0,
                             "viewport": [min(xs), min(ys), max(xs), max(ys)], "keypoints": kp, "lifted": lifted})
            cam, t, ums = cams[fid]
            w.write(json.dumps({"frame_id": fid, "source": "pc:objectron_chair", "t_realtime": t, "unix_ms": ums,
                                "camera": cam, "detections": dets}) + "\n")
            n += 1; nd += len(dets)
    print(json.dumps({"session": os.path.basename(session.rstrip('/')), "frames": n, "detections": nd,
                      "seconds": round(time.time() - t0, 1), "out": out_path}))

if __name__ == "__main__":
    main(sys.argv[1])
