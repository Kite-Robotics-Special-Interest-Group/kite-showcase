# Unity 仿真工程：安装、配置、MCP、使用

本目录 `unity/KiteWidowX/` 是 Unity 侧的全部源码（场景、脚本、URDF），从零到跑通的完整步骤见下。

> 版本：Unity **2022.3.62f3c1**（Unity China 版）；URP 14.0.12。其它 2022.3 LTS 版本通常也可用。

## 1. 安装 Unity

1. 下载 **Unity Hub**：<https://unity.com/download>
2. Hub → `Installs` → `Install Editor` → 选择 **2022.3 LTS**（本项目在 2022.3.62f3c1 上验证）
3. 安装时勾选 **Windows Build Support (IL2CPP)**；模块里再勾 `Universal Windows Platform` 可选
4. 首次启动需要登录 Unity 账号并激活许可证

国内网络下载慢时，可在 Hub 的 `Preferences → Installs` 里换镜像；本机实际使用的是 Unity China 发行版（版本号带 `c1` 后缀）。

## 2. 打开工程

仓库里只有源码目录，Unity 会自己生成 `Library/`、`Logs/`、`Temp/`：

```
unity/KiteWidowX/
  Assets/
    Scripts/KiteWidowXAgent.cs     # TCP 桥 + HUD（运行时）
    Editor/KiteBuildWidowX.cs      # 一键重建场景
    Editor/KiteBuildPlayer.cs      # 一键构建播放器
    Scenes/KiteWidowX.unity        # 已搭好的场景
    URDF/wx250s/                   # WidowX-250 URDF + STL
  Packages/manifest.json
  ProjectSettings/ProjectVersion.txt
```

操作：

1. Unity Hub → `Add` → 选择 `unity/KiteWidowX` 目录（**不要**选里面的 Assets）
2. 用 2022.3.62f3c1 打开；首次导入约 2–5 分钟
3. 打开场景 `Assets/Scenes/KiteWidowX.unity`

如果场景丢失或想重建，菜单 **`Kite → Build WidowX Scene`**（会自动导入 URDF、建桌面/相机/桥接组件并保存场景）。

## 3. 依赖包

`Packages/manifest.json` 已包含：

| 包 | 用途 |
| --- | --- |
| `com.unity.render-pipelines.universal@14.0.12` | URP 渲染 |
| `com.unity.robotics.urdf-importer` | URDF 导入（git URL） |
| `com.coplaydev.unity-mcp` | 可选，MCP 控制（见 §6） |

URDF Importer 走 GitHub，首次导入需要能访问 `github.com`；不通时可在别的网络环境把包缓存好再拷 `Library/PackageCache`。

## 4. WidowX 场景说明

| 对象 | 说明 |
| --- | --- |
| `wx250s` | Interbotix WidowX-250，URDF 导入，6 转动关节 + 2 手指 |
| `table` / `floor` | 桌面 0.9×0.7 m（台面 y=0）与地面 |
| `cube_red` | 4 cm 方块（训练用的目标） |
| `cam_front` | 第三人称 224×224，机位在机械臂后上方 |
| `cam_wrist` | 腕部 224×224，跟随 `gripper_link` |
| `KiteWidowXAgent` | 挂在 `wx250s` 上：TCP 桥 + HUD |

### 4.1 URDF 导入的六个坑（都已踩过）

1. **网格用 STL**：`.obj` 走 `AssetDatabase` 查找容易失败；`.stl` 由 `StlAssetPostProcessor` 生成带 MeshFilter 的 prefab
2. **`chosenAxis` 选 `yAxis`**：选 `zAxis` 会把整条臂拆散
3. **驱动单位是度**：`ArticulationDrive.target` 是度，`jointPosition` 是弧度；按弧度下发会"站不稳"
4. **根节点 `immovable = true`**：否则整条臂是自由刚体，会倾倒
5. **禁用导入器自带的 `Controller`**：它每帧写 `xDrive`，会和外部控制打架
6. **限位留 2° 余量**：驱动会过冲

## 5. 构建并运行

菜单 **`Kite → Build Windows Player`** → 产物 `Build/KiteWidowX/KiteWidowX.exe`。

双击运行（窗口左上角会出现推理 HUD）。运行时监听 **TCP 127.0.0.1:7777**，行协议：

| 命令 | 返回 | 说明 |
| --- | --- | --- |
| `OBS` | `{joints, grip, ee, ee_quat, images[2]}` | 状态 + 两路 224×224 PNG（base64） |
| `STATE` | 同上不带图 | 供 20 Hz 伺服 |
| `ACT q0..q5 grip` | `{ok}` | 关节目标（弧度）+ 夹爪 0..1（1=闭合） |
| `HUD ...` | `{ok}` | 推理面板数据（框架/延迟/模型输出） |
| `READY` | `{ok}` | 回预设位姿 |

Python 侧的闭环脚本（`kitebench` 里的 rollout 或历史上的 `closed_loop.py`）直接连这个端口即可。

## 6. MCP for Unity（可选）

用于让 AI 直接改场景，**不是运行必需**。

1. Unity 里安装包（manifest 已含）：`com.coplaydev.unity-mcp`
2. 启动服务端：
   ```bash
   uvx --from mcpforunityserver mcp-for-unity --transport http --http-url http://127.0.0.1:8080
   ```
3. Kilo 配置（`~/.config/kilo/kilo.jsonc`）：
   ```jsonc
   "mcp": { "unityMCP": { "type": "remote", "url": "http://127.0.0.1:8080/mcp", "enabled": true } }
   ```
4. Unity 菜单 `Window > MCP for Unity` → Transport 选 **HTTP** → **Start Bridge**

> **重要**：不要用 MCP 触发 Play Mode。实测会让 Unity 主线程卡死（所有 MCP 命令超时，需要重启编辑器）。要跑闭环请用 §5 的独立播放器。

## 7. 已知限制

- 相机与 MuJoCo 训练机位不同：**Unity 画面里的策略不能直接复用 MuJoCo 微调权重**（外观/机位都属于分布外）
- 本工程用于**演示与跨渲染器验证**；数据采集和训练在 MuJoCo（见 `docs/mujoco-sim.md`）
- 场景文件 3.7 MB，属于正常范围（含导入网格引用）

## 8. 常见问题

| 现象 | 处理 |
| --- | --- |
| 场景打开后机器人在原点很小 | 选中 `wx250s` 按 `F` 聚焦；或运行 `Kite → Build WidowX Scene` 重建 |
| 播放器启动后连接 7777 失败 | 确认窗口已打开、防火墙允许本地回环；`INFO` 命令可查关节表 |
| 机械臂塌在桌上 | 检查根节点 `immovable`、驱动单位（度）、`Controller` 是否禁用 |
| 构建时报 URDF Importer 找不到 | 检查 `Packages/manifest.json` 的 git URL 是否拉取成功 |
