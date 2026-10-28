# 多人合作射击 · 服务器端（Unity Headless）

《基于 Unity 的多人合作射击游戏网络同步与服务器权威 AI 的设计与实现》的**战斗服**工程。

与另外两个仓库配合使用：

| 仓库 | 职责 | 端口 |
|---|---|---|
| 客户端 | 画面、输入、表现层 | — |
| `GameServer`（.NET 网关） | 注册/登录、token 会话、云存档、房间管理 | 8888 |
| 本仓库 | 权威世界状态、敌人 AI、命中判定、波次与奖励 | 7777 |

## 职责边界

战斗服不渲染画面，只跑逻辑。所有判定都在这里发生，客户端只发输入、只画结果：

- 玩家位置由客户端上报，服务器做速度校验；命中判定与敌人行为由服务器计算
- 开火走服务器射线检测，支持 `ping/2 + 100ms` 的位置回溯（延迟补偿）
- 敌人 AI 直接在服务器跑状态机，客户端只按快照插值显示
- 击杀奖励、局内商店扣款全部由服务器裁决，客户端无法伪造

## 运行

1. 用 Unity 打开本工程
2. File → Build Settings → 添加 `Assets/Scenes/SampleScene` → Windows x86_64 → Build
3. 启动命令：

```powershell
# 正常启动
& ".\BattleServer.exe" -nographics -logFile -

# 关闭延迟补偿（对比实验用）
& ".\BattleServer.exe" -nographics -logFile - -norewind

# 指定端口
& ".\BattleServer.exe" -nographics -logFile - -port 7777
```

启动日志里会打印 `tickRate=20` 和 `RewindEnabled=True/False`，用它确认参数是否生效。

## 协议

统一封包：4 字节小端长度 + 4 字节消息 ID + UTF-8 JSON 正文。

| ID | 方向 | 名称 | 说明 |
|---|---|---|---|
| 1 | 双向 | Heartbeat | RTT 测量与断线检测 |
| 400 | C→S | Input | 位置、朝向、动作、传送标记 |
| 401 | S→C | Snapshot | 玩家 + 敌人状态快照（20Hz） |
| 402 | C→S | Fire | 开火起点、方向、客户端 ping |
| 403 | S→C | Damage | 伤害结果（玩家或敌人造成） |
| 404 | S→C | PlayerJoinLeave | 玩家进出房间 |
| 405 | C→S | Join | 加入战斗服 |
| 406 | S→C | JoinAccepted | 加入成功、玩家 id、本局金币 |
| 407 | S→C | EnemyHit | 玩家命中敌人的表现数据 |
| 408 | S→C | WaveState | 波次、剩余数量、是否清空 |
| 410 | S→C | EnemyShot | 远程敌人弹道起点与终点 |
| 411 | S→C | Reward | 击杀/清波金币奖励 |
| 412 | C→S | Buy | 购买请求 |
| 413 | S→C | BuyResult | 购买结果 |

## 核心模块

| 文件 | 职责 |
|---|---|
| `BattleServerNetwork` | TCP 收发、线程安全队列、按房间广播 |
| `BattleServerLoop` | 20Hz 定频心跳，逻辑帧与渲染帧解耦 |
| `BattleServerRouter` | 消息分发 |
| `ServerWorld` | 玩家权威状态、开火判定、延迟补偿、商店 |
| `ServerEnemyRegistry` | 敌人状态机、波次编排、精英怪 |

## 已实现的服务器权威 AI

| 类型 | 行为 |
|---|---|
| 近战 | 直线追击，进入攻击距离后按冷却出伤 |
| 远程 | 保持 7 米距离，太近后撤、太远靠近，射程内横移射击 |
| 史莱姆 | 慢速高血量，死亡时对 3 米内玩家造成范围伤害 |

波次规则：每 8 秒推进一波，数量从 3 只递增到 6 只，血量每波 +25，每 3 波出现一只精英（2 倍血、1.5 倍伤害）。

## 已知限制

- 移动采用「客户端上报 + 服务器校验」，因为根运动动画无法在无渲染服务器上复现
- 敌人位置不做回溯（移速 2.5 m/s，100ms 误差约 0.25 米，小于判定半径）
- 敌人追击为直线，不含寻路；复杂地形需要接入 NavMesh 或流场
- 战斗服入房暂未校验网关下发的 token 票据
