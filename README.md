# kite-showcase

> 把仿真结果变成能给外面看的东西：Unity 场景、模型闭环视频、图表素材。

这个仓库只负责「展示与可视化」，不含训练与评测逻辑。数据和策略来自 [`kite-bench`](https://github.com/Kite-Robotics-Special-Interest-Group/kite-bench)，端侧推理来自 [`Kite`](https://github.com/Kite-Robotics-Special-Interest-Group/Kite)。

## 里面有什么

| 内容 | 位置 | 说明 |
| --- | --- | --- |
| Unity 工程 | `unity/KiteWidowX/` | WidowX-250 场景、TCP 桥、推理 HUD，可构建独立播放器 |
| Unity 教程 | [`docs/unity-setup.md`](docs/unity-setup.md) | 安装 → 配置 → URDF 导入 → 构建 → MCP → 排障 |
| 视频渲染 | [`docs/video.md`](docs/video.md) | 模型闭环视频怎么录、标注什么、不能标注什么 |
| 渲染脚本 | `scripts/render_demo.py` | 直接从策略 + MuJoCo 场景出 MP4（依赖 kite-bench） |

## 快速开始

```bash
# 1) Unity 场景（见 docs/unity-setup.md）
#    用 Unity 2022.3 LTS 打开 unity/KiteWidowX，菜单 Kite → Build Windows Player

# 2) 渲染模型闭环视频（需要 kite-bench 的权重与场景）
pip install -e "D:/JLU_tools/kite-bench"      # 或 git+ssh 安装
python scripts/render_demo.py \
  --model <X-VLA-WidowX 权重目录> \
  --head <头权重 .pt> \
  --out out/model_pick.mp4
```

## 标注规范（重要）

对外素材必须写清三件事，缺一条就不要发：

1. **是仿真**（MuJoCo / Unity），不是真机
2. **动作来源**：模型推理 / 脚本专家 / 混合
3. **样本口径**：成功率与样本数（例如 `8/8，20 条目标`）

模板：

```
X-VLA fine-tuned on WidowX-250
closed-loop grasp in MuJoCo
success 8/8 | actions from the model
```

## 许可

MIT，见 [`LICENSE`](LICENSE)。Unity 工程内的 WidowX-250 模型来自 [`mujoco_menagerie`](https://github.com/google-deepmind/mujoco_menagerie)（Apache-2.0）。
