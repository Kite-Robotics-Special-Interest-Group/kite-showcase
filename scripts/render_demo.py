"""Render a model-driven pick video (two views, subtitles, MP4).

Requires ``kite-bench`` on the path (policy adapter, MuJoCo scene, demo-manifold IK):

    pip install -e ../kite-bench
    python scripts/render_demo.py --model <weights> --head out/head_v7.pt --out out/demo.mp4
"""

from __future__ import annotations

import argparse
from pathlib import Path

import cv2
import mujoco
import numpy as np
from kitebench.demo_ik import DemoIK
from kitebench.eval import XVLAAdapter
from kitebench.sim.oracle import GRIP_CLOSED, GRIP_OPEN, reset
from kitebench.sim.scene import IMAGE_SIZE, render

INSTRUCTION = "pick up the red block"
SIZE, FPS, EVERY = 448, 30, 9


def label(frame, text, org, scale=0.55, color=(255, 255, 255), thick=1):
    font = cv2.FONT_HERSHEY_SIMPLEX
    cv2.putText(frame, text, org, font, scale, (0, 0, 0), thick + 2, cv2.LINE_AA)
    cv2.putText(frame, text, org, font, scale, color, thick, cv2.LINE_AA)


def main() -> int:
    parser = argparse.ArgumentParser(description="render a model-driven pick video")
    parser.add_argument("--model", required=True, help="X-VLA-WidowX weights directory")
    parser.add_argument("--head", type=Path, required=True, help="fine-tuned head checkpoint")
    parser.add_argument("--demo-source", type=Path, default=Path("out/episodes_v4"),
                        help="episodes used by the demo-manifold IK")
    parser.add_argument("--out", type=Path, default=Path("out/model_pick.mp4"))
    parser.add_argument("--chunks", type=int, default=11)
    parser.add_argument("--cube", type=float, nargs=2, default=(0.266, 0.06))
    parser.add_argument("--industrial", action="store_true", help="use the industrial set")
    args = parser.parse_args()

    policy = XVLAAdapter(args.model, steps=5, seed=0, head=args.head)
    demo_ik = DemoIK(args.demo_source)

    if args.industrial:
        from kitebench.sim.industrial import build as build_scene
    else:
        from kitebench.sim.scene import build as build_scene

    model = build_scene(tuple(args.cube))
    data = mujoco.MjData(model)
    reset(model, data)
    gid = mujoco.mj_name2id(model, mujoco.mjtObj.mjOBJ_BODY, "wx250s/gripper_link")

    cam = mujoco.MjvCamera()
    cam.type = mujoco.mjtCamera.mjCAMERA_FREE
    cam.lookat[:] = [0.20, 0.03, 0.10]
    cam.distance = 0.72
    cam.azimuth = 216.0
    cam.elevation = -24.0

    renderer = mujoco.Renderer(model, SIZE, SIZE)
    args.out.parent.mkdir(parents=True, exist_ok=True)
    writer = cv2.VideoWriter(str(args.out), cv2.VideoWriter_fourcc(*"mp4v"), FPS,
                            (SIZE * 2, SIZE))
    frames = []
    best = 0.0
    counter = 0

    for _ in range(args.chunks):
        image = render(model, data, "front", IMAGE_SIZE)
        proprio = np.zeros(20, dtype=np.float32)
        proprio[0:3] = data.xpos[gid]
        proprio[3:10] = [1.0, 0.0, 0.0, 1.0, 0.0, 0.0, 0.0]
        chunk = policy.act(image, proprio, INSTRUCTION)
        for step in range(10):
            target = np.clip(chunk[step, 0:3], -0.5, 0.6)
            target[2] = max(target[2], 0.01)
            q = demo_ik.solve(chunk[step : step + 3, 0:3], data.qpos[0:6].copy())
            data.ctrl[0:6] = q
            data.ctrl[6] = GRIP_CLOSED if chunk[step, 9] >= 0.5 else GRIP_OPEN
            for _ in range(25):
                mujoco.mj_step(model, data)
                counter += 1
                best = max(best, float(data.qpos[10]))
                if counter % EVERY:
                    continue
                renderer.update_scene(data, camera="front")
                left = cv2.cvtColor(renderer.render(), cv2.COLOR_RGB2BGR)
                renderer.update_scene(data, camera=cam)
                right = cv2.cvtColor(renderer.render(), cv2.COLOR_RGB2BGR)
                frame = np.concatenate([left, right], axis=1)
                label(frame, "MuJoCo  |  WidowX-250  |  fine-tuned X-VLA", (14, 26), 0.6)
                label(frame, "actions from the model", (14, 50), 0.55, (200, 255, 200))
                label(frame, f"cube z = {data.qpos[10] * 100:.1f} cm", (14, SIZE - 16),
                      0.55, (180, 220, 255))
                frames.append(frame)

    if not frames:
        print("no frames captured")
        return 1

    card = np.zeros_like(frames[0])
    label(card, "X-VLA fine-tuned on WidowX-250", (60, SIZE // 2 - 30), 0.9, thick=2)
    label(card, "closed-loop grasp in MuJoCo", (60, SIZE // 2 + 10), 0.7, (200, 220, 255), 2)
    label(card, "actions from the model", (60, SIZE // 2 + 50), 0.6, (170, 255, 170))
    for _ in range(FPS * 2):
        writer.write(card)
    for frame in frames:
        writer.write(frame)
    for _ in range(FPS):
        writer.write(frames[-1])

    writer.release()
    renderer.close()
    print(f"wrote {args.out}  cube_z_max={best:.3f}  frames={len(frames) + FPS * 3}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
