# SLDataAPI

**版本：** 2.5.0（开发代号 **Rebirth**）  
**架构：** **LabAPI 原生插件**（v2.4 起脱离 EXILED，运行于 Northwood 官方 LabAPI 框架）  
**依赖：** LabAPI（游戏服务器自带） · MEC · Newtonsoft.Json · Harmony（服务器自带，不打包）  
**用途：** 在 SCP:SL 游戏服务器上暴露一个轻量 HTTP 接口，供 WebUI / AstrBot 等外部程序轮询实时服务器数据，并通过 `/control/*` 控制接口远程执行管理操作；内置**语音转发**（WebSocket 实时收听全频道语音，代号 SPY）；v2.5 起新增**控制接口 WebSocket 长连接**。

> **v2.4.0 Rebirth —— 架构迁移说明：** 本插件已从 EXILED 插件迁移为 **LabAPI 原生插件**（不再依赖 EXILED），并完成源码目录/命名空间分类重构。
> - 安装位置变更：`LabAPI/plugins/global/`（不再是 `EXILED/Plugins/`）
> - 配置位置变更：`LabAPI/configs/<端口>/SLDataAPI/config.yml`（旧 EXILED 配置文件不会被读取，需把值抄到新文件；键名同为 snake_case，删掉 `is_enabled` 即可）
> - 插件启停由 LabAPI 的 `properties.yml` 管理（`/control/plugins` 可代写）
> - 若服务器同时装有 EXILED，SLPlayer / OmegaWarhead 等 EXILED 插件的探测与控制、以及 `/control/plugins` 对它们的列表/启停依然可用（运行时反射桥，未装 EXILED 时自动降级）

---

## 功能总览

| 能力 | 说明 |
|------|------|
| 实时数据查询 | `GET /get_sl_data`：人数、回合、核弹、玩家列表（含 SteamID / 坐标）、DNT_OF 系列插件状态 |
| 控制接口 | `/control/` 公用控制命名空间，按 `control_transport` 二选一：`POST /control/*`（命令执行、玩家管理、回合控制、CASSIE、核弹、地图、封禁、日志、文件等）**或** WebSocket 长连接（升级 `/control`，call 信封调用同样的端点，见下文） |
| 命令输出捕获 | Harmony 补丁捕获 `ServerConsole.AddLog` / `CommandSender` 输出，`/control/command` 可拿到命令回显；点命令（`.m` 等）在 DotCommandHandler 上直连执行、响应直接返回（v2.5 修复：原生路径在专用服上不执行且无回显） |
| 地图数据 | 按 seed 提供本回合布局（LCZ/HCZ 每回合随机），可导出 atlas 等原始数据供外部重建地图 |
| 插件管理 | 列表读取配置文件启停状态（LabAPI 插件 + 同服 EXILED 插件）；启停走"暂存 → 保存"批量模式，LabAPI 插件重启生效（SLDataAPI 自身禁止禁用） |
| 文件防线 | 文件端点四重防线：路径白名单 / Windows 目录禁写 / 仅配置文件扩展名 / 游戏数据与自身配置目录禁访问 |
| 自动更新 | 启动时检查 GitHub Releases；检测到新版本自动下载并替换 DLL（程序集/名称/强名称签名三重校验，重启游戏服生效，旧版备份 .bak） |
| 语音转发（SPY） | WebSocket 实时推送全频道语音（近距离/对讲机/Intercom/SCP 频道等），Opus 解码为 48kHz float32 PCM，含说话者信息帧与 `/status` 状态查询，ControlToken 鉴权 |

---

## 安装

1. 运行：
```
dotnet build -c Release
```
2. 将 `SLDataAPI.dll` 放入服务器的 `LabAPI/plugins/global/` 目录（`%AppData%/SCP Secret Laboratory/LabAPI/plugins/global/`）
3. 启动服务器，LabAPI 会自动生成配置文件
4. 按需修改配置（见下方），重启服务器生效

> ⚠️ 许可证说明：游戏程序集（`Assembly-CSharp.dll` 等）禁止二次分发，仓库不携带这些 DLL。编译要求本机已安装 SCP:SL 专用服务器（自带 `LabApi.dll`），程序集默认按下列路径引用（其它机器可用 MSBuild 参数覆盖）：
>
> ```
> -p:SCPSL_DIR="D:\...\SCP Secret Laboratory Dedicated Server\SCPSL_Data\Managed"
> -p:LABAPI_DIR="C:\Users\...\AppData\Roaming\SCP Secret Laboratory\LabAPI"
> ```

---

## 配置

配置文件路径（LabAPI 首次启动时自动生成，键名为 **snake_case**，与旧 EXILED 配置同风格）：
`%AppData%/SCP Secret Laboratory/LabAPI/configs/<端口或 global>/SLDataAPI/config.yml`

```yaml
debug: false
verify_token: "your_secret_token"     # 只读接口鉴权 token（/get_sl_data）
http_port: 8081                       # HTTP 监听端口
push_interval_seconds: 8              # 后台数据刷新间隔（秒）

# ===== 控制接口（v2.1）=====
control_enabled: false                # 是否启用 /control/*（默认关闭；关闭时一律 404）
control_token: ''                     # 控制接口专用 token，与 verify_token 分离
control_transport: http             # 控制接口传输方式 http|ws（二选一硬互斥：ws 模式下 HTTP /control/* 一律 404，http 模式下 WS 握手拒绝；被拒一方收到 transport_mismatch 协商信号）

# ===== 自动更新 / 文件 / 日志 =====
auto_update_check: true               # 启动时检查 GitHub Releases 新版本
auto_update_install: true             # 检测到新版本时自动下载并替换 DLL（重启游戏服生效，旧版备份 .bak）
file_root: ''                         # /control/files/* 根目录（绝对路径）；留空=禁用文件端点
log_directory: ''                     # 服务器日志目录；留空=自动探测

# ===== 语音转发（v2.3 / SPY）=====
voice_enabled: false                  # 是否启用语音转发 WebSocket（默认关闭）
voice_port: 8082                      # 语音 WebSocket 监听端口（独立于 http_port）
```

> ⚠️ **键名必须是 snake_case**（LabAPI 使用 UnderscoredNamingConvention）：`verify_token` 而不是 `verifyToken`。
> 错误的键名会被**静默忽略**（无任何报错），对应配置保持默认值——鉴权失败但"token 明明是对的"基本就是这个原因。
> 好消息：键名风格与旧 EXILED 配置一致，直接把旧值抄过来即可（删掉 `is_enabled`，新键照上表拼写）。
>
> ⚠️ **任何一个值格式错误会导致整个文件被静默回退默认值**（LabAPI 的 LoadConfigs 行为，控制台无报错）：
> 布尔值不要加引号（`true` 而非 `"true"`）、缩进用空格不要用 Tab、含 `#` 等特殊字符的 token 必须加双引号。
> v2.5 起插件启动时会自检配置文件：解析失败会在控制台打出 YamlDotNet 的**精确错误（含行号）**，
> 并输出一行"配置摘要"（端口/token 长度/开关状态），默认值状态一眼可辨。
> 验证配置是否被读到：启动日志若出现 "VerifyToken 仍为出厂默认值" 警告，说明 `verify_token` 没有生效。

> 插件本身的启停开关不再出现在此文件中：LabAPI 用插件目录下的 `properties.yml`（`is_enabled`）管理插件加载与否，可通过 `/control/plugins` 端点修改。

**字段说明：**

| 字段 | 说明 |
|------|------|
| `control_token` | 控制接口专用 token。要求长度 ≥ 8，且同时包含大写字母、小写字母、数字、特殊符号；格式不合法时本次运行会**强制禁用控制接口**并在日志报错 |
| `control_transport` | 控制接口传输方式（**二选一硬互斥**）：`http`（默认，仅 `/control/*` HTTP POST）或 `ws`（仅 WebSocket 长连接）。选了 ws 就不留 HTTP 刷包面、选了 http 就不开放 WS，任何时刻只有一条控制通路；被拒一方返回 404 带 `data.code = "transport_mismatch"` 供平台自动切换（互斥检查在鉴权之前，刷被拒通道不消耗失败锁定额度）。只读接口 `/get_sl_data` 不受影响 |
| `auto_update_install` | 检测到新版本时自动下载并替换 `SLDataAPI.dll`（覆盖后重启游戏服生效；旧版备份 `.bak`）。校验：下载文件必须为合法程序集、名称一致；当前已强名称签名时还要求签名一致（防篡改）。关闭则仅日志提示 |
| `file_root` | 文件管理端点的根目录（绝对路径），所有文件操作被限制在该目录内（防 `..` 路径穿越）；留空 = 禁用。建议指向 `SCPSL_Data` 或某个只读配置目录 |
| `log_directory` | `/control/logs` 读取的日志目录。留空自动探测：`%AppData%/SCP Secret Laboratory/ServerLogs`（含端口子目录）→ `SCPSL_Data/Logs` |

> ⚠️ 请确保服务器防火墙放行对应端口（默认 8081/TCP）。

---

## 鉴权模型

系统有两种互不相同的 token：

| Token | 用途 | 传入方式 | 强度要求 |
|-------|------|----------|----------|
| `verify_token` | 只读数据接口 `/get_sl_data` | URL 查询参数 `?token=` | 无强制要求（建议复杂化） |
| `control_token` | 所有 `/control/*` 控制接口 **和语音转发 WebSocket** | `X-Control-Token` 请求头（也兼容 `?token=`）；语音 WS 用 `?key=` | 启动时强制校验格式 |

**暴力破解防护**（`ControlAuth.cs`）：三个入口（数据接口、控制接口、语音 WS）共用同一套防护——同一 IP 在 5 分钟窗口内失败 10 次即锁定 5 分钟；token 比较使用常量时间算法，避免时序侧信道。语音 WS 未配置 `control_token` 时一律拒绝（不开放匿名监听）。

---

## HTTP 接口

### `GET /get_sl_data`

| 参数 | 类型 | 说明 |
|------|------|------|
| `token` | string | 必填，与配置中 `verify_token` 一致 |

**响应示例：**
```json
{
  "success": true,
  "server_name": "我的服务器",
  "online": true,
  "players_count": 19,
  "max_players": 32,
  "round_started": true,
  "round_duration": 674,
  "current_phase": "进行中",
  "nuke_status": "未激活",
  "nuke_countdown": 0,
  "d_count": 7,
  "foundation_count": 6,
  "scp_count": 3,
  "spectator_count": 3,
  "ping": 42,
  "players": [
    { "nickname": "PlayerOne", "steam_id": "76561198000000000", "role": "D级人员", "team": "D级", "x": 12.3, "y": 100.5, "z": -45.6 }
  ],
  "dntof_plugins": {
    "sl_player": { "present": true, "source_mode": "local", "remote_url": null, "now_playing": "SCP-2295 主题曲" },
    "omega_warhead": { "present": true, "phase": "collecting", "coin_holders": [ { "nickname": "PlayerOne", "count": 3, "position": "LCZ" } ], "controller_holder": null, "countdown": null }
  }
}
```

**字段说明：**

| 字段 | 说明 |
|------|------|
| `round_duration` | 回合已进行秒数 |
| `nuke_status` | `未激活` / `倒计时:XX秒` / `已爆炸` |
| `nuke_countdown` | 核弹倒计时秒数（实时值，不受刷新间隔影响） |
| `d_count` | D级人员阵营（含混沌分裂者） |
| `ping` | 所有真实玩家平均延迟（ms） |
| `players[].steam_id` | 玩家 SteamID（`p.UserId`） |
| `players[].x/y/z` | 玩家世界坐标（地图追踪用，只取平面 x/z + 高度 y） |
| `players` | 仅包含已分配职业的真实玩家，排除 NPC/Dummy |
| `dntof_plugins` | DNT_OF 系列插件（SLPlayer / OmegaWarhead）运行时信息；对应插件未加载时子字段为 `null` |

**错误响应：**

| HTTP 状态码 | 含义 |
|------------|------|
| `403` | token 错误或缺失 |
| `404` | 路径不存在 |
| `405` | 方法不允许（`/get_sl_data` 仅支持 GET） |

---

### `POST /control/*`

所有控制端点：**仅 POST**，请求体为 JSON，鉴权用 `X-Control-Token` 请求头。响应统一为：

> 需要 `control_transport: http`（默认）。ws 模式下本节端点返回 404（带 `transport_mismatch` 协商信号，平台应改走 WS call 通道）。

```json
{ "success": true, "message": "操作成功", "data": { } }
```

| 端点 | 请求字段 | 说明 |
|------|----------|------|
| `/control/command` | `command` | 执行服务器控制台命令（等价本机控制台权限，见安全警告）。**点命令（`.m` 等客户端命令）走专用执行通道**：以主机玩家身份在 DotCommandHandler 上执行并直接返回响应文本（原生控制台路径在专用服上是死胡同，命令不会执行更没有回显） |
| `/control/player/kick` | `target`, `reason` | 踢出玩家（target 支持昵称/ID/SteamID） |
| `/control/player/ban` | `target`, `reason`, `duration` | 封禁玩家（duration 单位：分钟，0=永久） |
| `/control/player/role` | `target`, `role` | 设置玩家角色 |
| `/control/player/teleport` | `target`, `x`, `y`, `z` | 传送玩家到指定坐标 |
| `/control/player/mute` | `target`, `mute` | 语音禁言/解除（bool） |
| `/control/player/msg` | `target`, `message`, `msg_type`(`hint`/`broadcast`), `duration_seconds` | 私聊消息/广播 |
| `/control/player/effect` | `target`, `effect`, `effect_duration` | 施加状态效果 |
| `/control/player/state` | `target`, `godmode`?, `bypass`?, `health`?, `intercom`? | 查询/设置玩家状态（均为可选字段） |
| `/control/round` | `action`: `restart` / `end` / `start` | 回合控制 |
| `/control/cassie` | `message`, `isNoisy`? | CASSIE 播报（LabAPI 新版 TTS 体系不再细分 isHeld/isSubtitles，传入会被忽略） |
| `/control/warhead` | `action`: `start` / `stop` / `detonate` | 核弹控制 |
| `/control/map` | `action`: `seed` / `layout` / `doors` / `lights` | 地图信息与控制（seed 每回合固定，同 seed 布局恒定） |
| `/control/map/export` | — | 导出地图原始数据（atlas RGBA base64、glyph_pairs、zone_candidates 等），供外部重建 |
| `/control/slplayer` | `action`: `status` / `list` / `play` / `next` / `stop` / `volume` / `shuffle` / `reload` | 控制 SLPlayer 音乐（需服务器装有 SLPlayer 插件） |
| `/control/plugins` | `action`?: `stage`/`clear`/`apply`/`reload`（空=列表） | 插件管理。列表同时列出 **LabAPI 原生插件**（`source: labapi`）与同服 **EXILED 插件**（`source: exiled`，需装有 EXILED）；`enabled` 读配置文件；`stage` 暂存启停（不写文件，SLDataAPI 自身禁止禁用）；`apply` 统一写入——LabAPI 插件写 `properties.yml` 后**重启服务器生效**，EXILED 插件立即重载生效；`reload` 仅热重载各插件**配置文件**（等价控制台 `reload configs`，不重载插件本体） |
| `/control/ban_list` | — | 游戏封禁列表 |
| `/control/ban/add` | `userId`?, `reason`, `duration` | 添加封禁 |
| `/control/ban/revoke` | `userId` | 解除封禁 |
| `/control/logs` | `lines`?（默认 200）, `filter`?, `path`?, `action`? | 读取服务器日志尾部（自动探测日志目录）。`action=list` 列出全部可用日志文件（名称/大小/时间）；`path` 指定读取某个日志文件（仅限日志目录内 .log/.txt，防任意文件读取） |
| `/control/files/list` | `path` | 列出目录（受 `file_root` 白名单限制） |
| `/control/files/read` | `path` | 读取文件内容 |
| `/control/files/write` | `path`, `content` | 写入文件 |

**示例：**
```
POST /control/round
X-Control-Token: 你的control_token
Content-Type: application/json

{ "action": "restart" }
```

**错误响应：**

| HTTP 状态码 | 含义 |
|------------|------|
| `403` | token 错误/缺失，或该来源已被暴力破解锁定 |
| `404` | 控制接口未启用（`control_enabled: false`），或端点不存在 |
| `405` | 控制接口仅支持 POST |
| `413` | 请求体超过 64KB |
| `400/500` | 业务错误（响应 `message` 内附详情） |

---

## 控制接口 WebSocket（v2.5 · `/control`）

`/control/` 是公用的控制命名空间：一次性调用走 HTTP POST，高频调用走 WebSocket 长连接（复用连接、仅剩帧级开销），两种方式调用同样的端点、语义完全一致——但**同一时刻只有一条通路开放**（`control_transport` 二选一硬互斥，不设双通道，避免选了 ws 还留着 HTTP 刷包面）。`/get_sl_data` 数据接口始终走 HTTP 不变。

**端点：** `ws://<服务器IP>:8081/control?key=<control_token>`（也接受别名 `/ws/control`；与 HTTP 同端口；token 支持 `?token=` 或 `X-Control-Token` 请求头）

**门控：** 需要 `control_transport: ws`；`http` 模式下握手 404（带 `transport_mismatch` 协商信号）；`control_enabled: false` 时一律 404；鉴权失败 403 并计入按 IP 的失败锁定。

**协议（JSON 文本帧）：**

| 方向 | 消息 | 说明 |
|------|------|------|
| S→C | `{"type":"hello","version":"2.5.0","endpoints":"/control/*"}` | 建连后立即推送 |
| C→S | `{"type":"ping"}` | 心跳（建议 ~25s 一次） |
| S→C | `{"type":"pong"}` | 心跳应答 |
| C→S | `{"type":"call","reqId":"c1","path":"/control/player/kick","body":{...}}` | 控制调用；`path` + `body` 与 HTTP POST 完全一致 |
| S→C | `{"type":"result","reqId":"c1","ok":true,"status":200,"data":{"success":true,...}}` | 调用结果；`data` 即 HTTP 响应体；失败时 `ok:false` 且带 `message`（对应 400/403/500 文案） |
| S→C | `{"type":"error","message":"..."}` | 协议层错误（非法 JSON / 未知类型等） |

- `reqId` 由调用方生成，仅需连接内唯一；**结果允许乱序返回**（并发调用时按完成顺序回包）
- 限制：全局连接上限 8；单连接并发 `call` 上限 4（超出立即回 `result{ok:false,status:429}`）；单消息上限 256KB；90s 无入站消息判定超时断开
- 除 JSON 层 `ping/pong` 外，协议层 WS ping 帧也会被应答

**关闭码 / 状态码语义**（供上游中继实现退避与错误透传，不应重试风暴）：

| 码 | 阶段 | 含义 | 上游应对 |
|----|------|------|----------|
| HTTP `400` | 握手前 | 非升级请求 / 缺 Sec-WebSocket-Key | 客户端实现缺陷，修代码而非重试 |
| HTTP `403` | 握手前 | 鉴权失败（token 错误/锁定中） | 丢弃连接 + 错误透传；**不要立即重试**（会计入按 IP 失败锁定，5 分钟 10 次锁 5 分钟） |
| HTTP `404` | 握手前 | 控制接口未启用 / 当前为 HTTP 模式（互斥） | 错误透传，提示管理员改配置 |
| HTTP `503` | 握手前 | 连接数满（上限 8） | 丢弃连接 + 指数退避重连（服务端负载信号） |
| WS `1000` | 会话中 | 对端正常关闭 | 正常清理 |
| WS `1002` | 会话中 | 协议错误（RSV 位 / 未知 opcode / 意外续帧） | 客户端实现缺陷，修代码 |
| WS `1003` | 会话中 | 收到二进制帧（仅支持文本） | 同上 |
| WS `1008` | 会话中 | **消息组装超时**（分片慢速滴流防护，单消息 30s 上限） | 丢弃连接 + 退避重连（可能是滥用/网络劣化信号） |
| WS `1009` | 会话中 | 消息超过 256KB 上限 | 客户端把请求拆小 |

另：服务端 90s 无入站消息主动断开（无 close 码层面的区分，对端表现为连接被关闭）——中继侧靠心跳维持，断开后常规退避重连即可。

---

## 语音转发（v2.3 / 代号 SPY）

启用 `voice_enabled` 后，插件在独立端口（默认 8082）上提供语音 WebSocket，实时推送服务器内**所有频道**的语音（近距离、对讲机、Intercom 全局广播、SCP 频道、旁观者等），解码为 48kHz 单声道 float32 PCM。

**端点：**

| 端点 | 说明 |
|------|------|
| `GET /ws?key=control_token` | WebSocket 升级，实时语音流（需要 `control_token` 鉴权） |
| `GET /status?key=control_token` | JSON：当前正在说话的玩家（昵称/UserID/角色/频道，1.5s 无新包视为停止） |

**帧格式：**

- 连接成功：文本帧 `{"type":"hello","sampleRate":48000,"channels":1,"format":"float32"}`
- 说话者信息（新一轮讲话 / 频道或角色变化时推送）：文本帧 `{"type":"speaker","nickname":"...","userid":"...","playerid":n,"role":"...","channel":n}`
- 语音帧（二进制）：`[0]=0x01 [1]=channel [2-3]=playerId(LE) [4-7]=seq(LE) [8..]=float32 PCM`，每包为 10ms Opus 帧（480 样本，约 100 包/秒）

**实现要点：**

- 基于 LabAPI `PlayerEvents.SendingVoiceMessage` 事件（挂在 `VoiceTransceiver.ServerReceiveMessage` 上，服务器收到的每个语音包都会触发；保留按内容哈希去重的纵深防御，避免重复解码破坏 Opus 解码器状态）
- 解码器按说话者（netId）分开维护；解码异常时自动重建自愈
- 所有状态以 netId 为键，不用 ReferenceHub（玩家断开后 Hub 销毁会抛 NRE）
- 主循环 MEC 协程内全程 try/catch 保护，任何异常都不会杀死转发管道
- **主线程零阻塞保证（v2.5）**：读侧仅在有数据时非阻塞收取；发送侧先经 `Poll` 可写检查（缓冲满跳帧，实时流允许丢帧），持续 3 秒不可写判死连接，外加 250ms 发送超时兜底——对端停止收数据绝不可能冻结服务器主线程
- 连接层防护：监听连接上限 8（满载时**新连接在 TCP 层直接被关闭**，中继侧应对为退避重连，不会得到 WS 层错误码）、握手超时 10 秒、鉴权失败当场断开

---

## 安全注意事项

- `/control/command` **等价于本机控制台权限**：大多数 RA 命令通过 `GameConsoleCommandHandler` 同时注册，可在此执行。`control_token` 一旦泄露即完全沦陷——务必只通过受信内网或反向代理白名单暴露，`control_enabled` 默认关闭是有意为之。
- `control_token` 与 `verify_token` 分离：只读查询可以放心交给监控/机器人，控制权限单独保管。
- **连接层 DoS 防护**（v2.5 全面加固）：
  - **Slowloris 防护**：HTTP 请求头+体必须在 **15 秒**内整体送达，超时回 408 并断开——慢速滴字节无法绕过（单次 Read 超时会被周期性字节重置，必须总时限兜底）；每个连接最长占用 ≈ 一个时限 + 单次读取超时（30s），64 并发闸位不会被长期钉死
  - **WS 控制通道**：单条消息组装超时 30 秒（防"慢发分片 + 夹 ping 保活"绕过空闲超时）+ 90s 无入站消息空闲断开 + 256KB 消息上限 + 全局 8 连接上限 + 单连接 4 并发调用上限
  - **语音监听**：连接数上限 8（超出立即拒绝）+ 握手超时 10 秒（连上不完成握手的连接强制断开）+ 鉴权失败的连接当场关闭
- 文件端点默认关闭；开启后所有操作都被限制在 `file_root` 内（路径规范化 + 前缀校验，防 `..` 穿越）。
- **文件端点四重防线**（任何角色一视同仁，读/写同规则）：
  1. 路径白名单：仅 `file_root` 内（防 `..` 穿越）
  2. 系统目录保护：**Windows 目录及其子目录禁止浏览/读取/写入**
  3. 扩展名白名单：**只允许操作配置文件**（`yml/yaml/txt/json/cfg/ini/conf/config/xml/properties`），exe/dll/bat/ps1 等一律拒绝（列表中以黄色标记且无法打开）
  4. 顶级防线：**游戏数据目录**（`%AppData%/SCP Secret Laboratory`，LabAPI 的插件/依赖/配置目录都在其中）与 **SLDataAPI 自身配置目录**（`LabAPI/configs/<端口>/SLDataAPI/`）禁止读/写/访问（列表中以黄色标记且无法打开）——防止篡改游戏配置/管理员名单实现提权，或改写插件自身配置

---

## 数据刷新机制

- 插件启用时**立即**采集一次数据（无需等待第一个刷新周期）
- 之后每隔 `push_interval_seconds` 秒刷新一次缓存（默认 8 秒）
- 回合开始 / 结束 / 等待玩家阶段切换时**立即触发**一次额外刷新
- 核弹倒计时在**每次 HTTP 请求时实时读取**，不受刷新间隔影响，始终与游戏内同步
- 地图布局在**每回合开始时**采集（LCZ/HCZ 每回合随机）；`/control/map seed` 返回的 seed 与布局一一对应

---

## 已知过滤规则

以下类型的玩家对象会被排除在数据之外，不计入人数也不出现在玩家列表：

- 通过 `dummy` 命令或插件创建的 **NPC/Dummy 玩家**（`p.IsNpc == true`）与服务器主机（`p.IsHost == true`）
- `RoleTypeId.None` 的玩家（回合开始瞬间尚未完成职业分配，下一个刷新周期会正常出现）

---

## 与 AstrBot 集成

客户端插件：[SCP：SL 查询插件](https://github.com/DNTOF/astrbot_plugin_sl_query)
在 AstrBot 插件中使用 `/bindex <IP> <token>` 绑定本接口后，`/sl` 命令查询时会优先调用本接口，并在服务器名称后显示 `[EX]` 标记：

```
名称: 我的服务器[EX]
人数: 19/32
玩家列表: PlayerOne[D级人员], TestUser[SCP-173], ...
回合: 已开始 11 分钟
核弹状态: 未激活
D级人员阵营: 7 
基金会阵营: 6
SCP阵营: 3
观察者: 3
延迟: 42ms
```

---

## 源文件结构（v2.4.0：LabAPI 架构 + 命名空间分类）

```
SLDataAPI/
├── Plugin.cs                       # [SLDataAPI] 插件入口：LabAPI 生命周期（Enable/Disable）、事件注册、服务编排
├── Config.cs                       # [SLDataAPI] LabAPI 配置类（config.yml）
├── Log.cs                          # [SLDataAPI] 日志门面（LabAPI Logger + Debug 开关门控）
├── Data/
│   ├── Models.cs                   # [SLDataAPI.Data] ServerData / PlayerInfo / DntofInfo 数据模型
│   └── ControlModels.cs            # [SLDataAPI.Data] 控制接口请求/响应 DTO
├── Control/
│   ├── ControlAuth.cs              # [SLDataAPI.Control] 鉴权：token 格式校验、常量时间比较、按 IP 失败锁定
│   ├── ControlController.cs        # [SLDataAPI.Control] /control/* 端点业务逻辑（游戏调用经主线程派发）
│   └── WsControlService.cs         # [SLDataAPI.Control] /ws/control 控制长连接（v2.5，call/result 信封）
├── Services/
│   ├── HttpServer.cs               # [SLDataAPI.Services] TcpListener HTTP 实现（0.0.0.0 绑定，不依赖 http.sys）
│   ├── DataCollector.cs            # [SLDataAPI.Services] 数据采集、缓存、定时刷新
│   ├── MainThreadExecutor.cs       # [SLDataAPI.Services] 游戏调用主线程派发 + 同步等待
│   ├── FileService.cs              # [SLDataAPI.Services] 文件端点：FileRoot 白名单、路径防穿越
│   ├── ServerLogService.cs         # [SLDataAPI.Services] 服务器日志尾部读取（自动探测日志目录）
│   └── UpdateChecker.cs            # [SLDataAPI.Services] GitHub Releases 自动更新检查/替换
├── Voice/
│   └── VoiceService.cs             # [SLDataAPI.Voice] 语音转发（SPY）：WebSocket、Opus 解码、PCM 推送
├── Map/
│   ├── MapLayoutService.cs         # [SLDataAPI.Map] 本回合房间布局采集（RoomIdentifier 反射）
│   └── MapExportService.cs         # [SLDataAPI.Map] 地图原始数据导出（atlas / glyph / 候选权重）
├── Integrations/
│   ├── ExiledInterop.cs            # [SLDataAPI.Integrations] EXILED 运行时反射桥（零编译期依赖，未装时安全降级）
│   ├── DntofDetector.cs            # [SLDataAPI.Integrations] 探测 SLPlayer / OmegaWarhead 运行时状态
│   └── SlPlayerController.cs       # [SLDataAPI.Integrations] 反射控制 SLPlayer 音乐控制器
├── Capture/
│   └── CommandOutputCapture.cs     # [SLDataAPI.Capture] Harmony 补丁：捕获 ServerConsole.AddLog / CommandSender 输出
└── SLDataAPI.csproj                # net48；引用本机游戏程序集 + LabApi.dll（路径可用 -p: 参数覆盖）
```
