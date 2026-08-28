# SLDataAPI

> ## ⚠️ 安全警告（请先阅读）
>
> **本插件的服务端链路全部为明文传输，未加密：**
> - 数据接口 / 控制接口为裸 **HTTP**（默认端口 8081）
> - 控制长连接为裸 **WebSocket**（`ws://`，非 `wss://`）
> - 语音转发 / 语音录音监听为裸 **WebSocket**（默认端口 8082）
>
> 明文链路上的任何中间节点（路由器、运营商、公共网络、反向代理等）都可以**窃听你的 token、语音内容与控制命令**；token 一旦泄露，攻击者可完全控制你的服务器。
>
> **使用本插件即表示你理解并接受以下责任划分：**
> - 服务器的安全防护（防火墙、端口白名单、反向代理 + HTTPS/WSS 加密、`control_token` 强度与保管等）由**使用者自行负责**
> - 因未加密链路导致的 token 泄露、语音内容泄露、服务器被入侵等一切后果，**本项目与作者不承担任何责任**
>
> 强烈建议：仅在内网/受信网络使用；对外暴露时务必经反向代理加 HTTPS/WSS（详见下方「安全注意事项」）。

版本： 2.5.4（开发代号 GIS,GNSS,RS!）  
**架构：** **LabAPI 原生插件**（v2.4 起脱离 EXILED，运行于 Northwood 官方 LabAPI 框架）  
**依赖：** LabAPI（游戏自带） · 0Harmony（2.3.x） · Newtonsoft.Json（13.0.x）——后两者**游戏本身不自带**，由 LabAPI 依赖目录提供，缺失会加载失败，见下方「⚠️ 运行依赖要求」  
**用途：** 在 SCP:SL 游戏服务器上暴露一个轻量 HTTP 接口，供 WebUI / AstrBot 等外部程序轮询实时服务器数据，并通过 `/control/*` 控制接口远程执行管理操作；内置**语音转发**（WebSocket 实时收听全频道语音，代号 SPY）；v2.5 起新增**控制接口 WebSocket 长连接**。

> 📚 **完整文档见 Wiki**：https://github.com/DNTOF/SLDataAPI/wiki —— 接口 curl 示例与响应、WS 协议与事件 payload、配置参考、安全模型、录音格式、开发者指南等详细内容均在 wiki 中，README 仅保留安装与核心概览。

> **v2.4.0（代号 Rebirth）—— 架构迁移说明：** 本插件已从 EXILED 插件迁移为 **LabAPI 原生插件**（不再依赖 EXILED），并完成源码目录/命名空间分类重构。
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
| 事件流推送（v2.5.4） | 控制长连接 `subscribe_events` 订阅后实时推送服务器事件：回合开始/结束、玩家加入/离开/死亡、电梯使用、权限门交互（见「控制接口 WebSocket」章节） |
| 举报功能（v2.5.4） | 玩家在 Esc → 服务器设置 面板中下拉选择在线玩家、填写原因、长按按钮提交举报（限流+记录上限清理）；平台经 `/control/reports` 读取未处理记录并标记已处理。默认关闭 |
| 命令输出捕获 | Harmony 补丁捕获 `ServerConsole.AddLog` / `CommandSender` 输出，`/control/command` 可拿到命令回显；点命令（`.m` 等）在 DotCommandHandler 上直连执行、响应直接返回（v2.5 修复：原生路径在专用服上不执行且无回显） |
| 地图数据 | 按 seed 提供本回合布局（LCZ/HCZ 每回合随机），可导出 atlas 等原始数据供外部重建地图 |
| 插件管理 | 列表读取配置文件启停状态（LabAPI 插件 + 同服 EXILED 插件）；启停走"暂存 → 保存"批量模式，LabAPI 插件重启生效（SLDataAPI 自身禁止禁用） |
| 文件防线 | 文件端点四重防线：路径白名单 / Windows 目录禁写 / 仅配置文件扩展名 / 游戏数据与自身配置目录禁访问 |
| 自动更新 | 启动时检查 GitHub Releases；检测到新版本自动下载并替换 DLL（程序集/名称/强名称签名三重校验，重启游戏服生效，旧版备份 .bak） |
| 语音转发（SPY） | WebSocket 实时推送全频道语音（近距离/对讲机/Intercom/SCP 频道等），Opus 解码为 48kHz float32 PCM，含说话者信息帧与 `/status` 状态查询，ControlToken 鉴权 |
| 语音录音取证（v2.5） | 每局自动保存混合音轨（WAV 48kHz/16bit/单声道）+ 时间轴日志（谁在何时说了多久），用于游戏不公平问题取证；按 `voice_record_max_rounds` 自动清理旧局 |

---

## ⚠️ 运行依赖要求（重点）

插件运行时需要两个程序集，**游戏本身不自带**（`SCPSL_Data/Managed/` 里没有），由 LabAPI 的依赖目录提供——**手动安装/精简安装 LabAPI 的服务器很可能缺失**，缺失时插件会加载失败或启动即报错：

| 依赖 | 版本要求 | 缺失现象 |
|------|----------|----------|
| `0Harmony.dll` | 2.3.x（建议与 LabAPI 自带一致） | 插件加载失败 / 事件补丁不生效 |
| `Newtonsoft.Json.dll` | 13.0.x（程序集版本 13.0.0.0） | 插件加载失败 / 启动即崩 |

**检查方法**：确认以下目录存在这两个文件（`%AppData%/SCP Secret Laboratory/` 为服务器数据根目录）：

```
LabAPI/dependencies/global/0Harmony.dll
LabAPI/dependencies/global/Newtonsoft.Json.dll
```

**获取渠道**：
- `0Harmony.dll`：Harmony 官方仓库 https://github.com/pardeike/Harmony/releases （选择 **net472** 版本，文件名为 `0Harmony.dll`）
- `Newtonsoft.Json.dll`：NuGet https://www.nuget.org/packages/Newtonsoft.Json （13.0.x 版本）；或在已装 LabAPI 的其他服务器上直接复制（LabAPI 完整安装自带这两个文件，位于上述 `dependencies/global/`）

**安装路径**：将两个 DLL 放入 `%AppData%/SCP Secret Laboratory/LabAPI/dependencies/global/`（所有端口共享的全局依赖目录；LabAPI 从这里加载插件依赖）。放入后重启服务器生效。

---

## 安装

1. 运行：
```
dotnet build -c Release
```
2. 确认依赖齐全（见上方「⚠️ 运行依赖要求」：`LabAPI/dependencies/global/` 下有 `0Harmony.dll` 与 `Newtonsoft.Json.dll`）
3. 将 `SLDataAPI.dll` 放入服务器的 `LabAPI/plugins/global/` 目录（`%AppData%/SCP Secret Laboratory/LabAPI/plugins/global/`）
4. 启动服务器，LabAPI 会自动生成配置文件
5. 按需修改配置（见下方），重启服务器生效

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
control_token: "你的token"           # 控制接口专用 token，与 verify_token 分离。填法：保留 ASCII 双引号，在引号内写 token（⚠️ 裸值有特殊字符坑、弯引号不行，见下方「token 写法建议」）
control_transport: http             # 控制接口传输方式 http|ws（二选一硬互斥：ws 模式下 HTTP /control/* 一律 404，http 模式下 WS 握手拒绝；被拒一方收到 transport_mismatch 协商信号）

# ===== 自动更新 / 文件 / 日志 =====
auto_update_check: true               # 启动时检查 GitHub Releases 新版本
auto_update_install: true             # 检测到新版本时自动下载并替换 DLL（重启游戏服生效，旧版备份 .bak）
file_root: ''                         # /control/files/* 根目录（绝对路径）；留空=禁用文件端点
log_directory: ''                     # 服务器日志目录；留空=自动探测

# ===== 语音转发（v2.3 / SPY）=====
voice_enabled: false                  # 是否启用语音转发 WebSocket（默认关闭）
voice_port: 8082                      # 语音 WebSocket 监听端口（独立于 http_port）

# ===== 语音录音取证（v2.5.1 推出 · 代号 Yagami Light；v2.5.2 音质修复 · Bay of Pigs Invasion；v2.5.3 时间轴对齐 · Apollo 11's Tapes；当前 GIS,GNSS,RS!）=====
voice_record_enabled: false           # 每局自动保存录音（WAV 混合音轨 + 时间轴日志）；需 voice_enabled=true
voice_record_max_rounds: 10           # 最多保留多少局录音（0/负数=不清理；参考 5.5MB/分钟/局）
voice_record_dir: ''                  # 录音保存目录（留空=默认 %AppData%/SCP Secret Laboratory/SLDataAPI/VoiceRecords）

# ===== 举报功能（v2.5.4 推出 · 代号 GIS,GNSS,RS!）=====
report_enabled: false                 # 举报功能总开关（SSS 面板 + /control/reports 端点，默认关闭）
report_max_records: 50                # 举报记录最大条数（超出自动删最旧已处理；全未处理则 WARN 提示）
report_rate_limit: 5                  # 限流窗口内每人最多提交举报次数
report_rate_window_minutes: 30        # 举报限流窗口（分钟），默认半小时
```

> ⚠️ **键名必须是 snake_case**（LabAPI 使用 UnderscoredNamingConvention）：`verify_token` 而不是 `verifyToken`。
> 错误的键名会被**静默忽略**（无任何报错），对应配置保持默认值——鉴权失败但"token 明明是对的"基本就是这个原因。
> 好消息：键名风格与旧 EXILED 配置一致，直接把旧值抄过来即可（删掉 `is_enabled`，新键照上表拼写）。
>
> ⚠️ **任何一个值格式错误会导致整个文件被静默回退默认值**（LabAPI 的 LoadConfigs 行为，控制台无报错）：
> 布尔值不要加引号（`true` 而非 `"true"`）、缩进用空格不要用 Tab。
> v2.5 起插件启动时会自检配置文件：解析失败会在控制台打出 YamlDotNet 的**精确错误（含行号）**，
> 并输出一行"配置摘要"（端口/token 长度/开关状态），默认值状态一眼可辨。
> 验证配置是否被读到：启动日志若出现 "VerifyToken 仍为出厂默认值" 警告，说明 `verify_token` 没有生效。
>
> ⚠️ **token 写法建议（重点）**：`control_token` / `verify_token` **推荐用 ASCII 双引号包裹**，如 `control_token: "Qq10086@"`。实测过的坑：
>
> - **裸值特殊字符坑**：token 以 `*` 开头（如 `*Arisl14514`）会被 YAML 当成**别名引用**——整个配置文件解析失败、**全部配置回退默认值**；以 `&` 或 `!` 开头（如 `&abc` / `!abc`）值会被**吞成空字符串**（零报错但 token 变空）。token 中的 `#`（前面有空格时）会被当成注释截断。
> - **弯引号坑**：从聊天软件/网页复制来的弯引号（`‘’“”`）不是 YAML 引号语法，会被当成 **token 内容的一部分**（长度+1、格式校验照常通过、启动零报错，但鉴权必然 403）。
> - **引号包裹则全部安全**：`*` / `&` / `!` / `#` 等特殊字符在 ASCII 引号内都是普通字符（双引号内 `\` 与 `"` 需转义；单引号内 `'` 需写两个 `''`）。
> v2.5.4 起插件启动时会检测引号字符并直接报错点破；配置解析失败时也会输出含行号的精确错误与特殊字符提示。

> 插件本身的启停开关不再出现在此文件中：LabAPI 用插件目录下的 `properties.yml`（`is_enabled`）管理插件加载与否，可通过 `/control/plugins` 端点修改。

**字段说明：**

| 字段 | 说明 |
|------|------|
| `control_token` | 控制接口专用 token。要求长度 ≥ 8，且同时包含大写字母、小写字母、数字、特殊符号；格式不合法时本次运行会**强制禁用控制接口**并在日志报错 |
| `control_transport` | 控制接口传输方式（**二选一硬互斥**）：`http`（默认，仅 `/control/*` HTTP POST）或 `ws`（仅 WebSocket 长连接）。选了 ws 就不留 HTTP 刷包面、选了 http 就不开放 WS，任何时刻只有一条控制通路；被拒一方返回 404 带 `data.code = "transport_mismatch"` 供平台自动切换（互斥检查在鉴权之前，刷被拒通道不消耗失败锁定额度）。只读接口 `/get_sl_data` 不受影响 |
| `auto_update_install` | 检测到新版本时自动下载并替换 `SLDataAPI.dll`（覆盖后重启游戏服生效；旧版备份 `.bak`）。校验：下载文件必须为合法程序集、名称一致；当前已强名称签名时还要求签名一致（防篡改）。**只自动接受稳定版**：预发布版本（GitHub prerelease/draft 标记，或 tag 含 beta/alpha/rc/preview/dev 等标识）不会自动下载。关闭则仅日志提示 |
| `file_root` | 文件管理端点的根目录（绝对路径），所有文件操作被限制在该目录内（防 `..` 路径穿越）；留空 = 禁用。建议指向 `SCPSL_Data` 或某个只读配置目录 |
| `log_directory` | `/control/logs` 读取的日志目录。留空自动探测：`%AppData%/SCP Secret Laboratory/ServerLogs`（含端口子目录）→ `SCPSL_Data/Logs` |
| `voice_record_enabled` | 每局自动保存语音录音：混合音轨 WAV（48kHz/16bit/单声道）+ 时间轴日志。**需同时开启 `voice_enabled`**（复用语音解码管线）。回合开始建档、回合结束定稿；停服时兜底保存 |
| `voice_record_max_rounds` | 最多保留的录音局数（按最近时间排序，超出自动删除最旧的 wav + 时间轴）。0/负数 = 不清理。磁盘参考：约 5.5MB/分钟/局（一小时局 ≈ 330MB） |
| `voice_record_dir` | 录音保存目录（绝对路径）；留空 = `%AppData%/SCP Secret Laboratory/SLDataAPI/VoiceRecords`。该目录在游戏数据目录内，天然受文件端点顶级防线保护 |
| `report_enabled` | 举报功能总开关（默认关闭）。开启后：玩家在 Esc → 服务器设置 面板中下拉选择在线玩家、填写原因、长按按钮（3 秒）提交举报；平台端通过 `/control/reports` 端点读取未处理记录并标记已处理。记录写入插件配置目录下的 `reports.json`（含举报人/被举报人 steam64、IP、原因、时间，status: pending/handled） |
| `report_max_records` | 举报记录最大条数。超出后自动删除最旧的**已处理**记录；未处理记录不删除，若全部未处理则 LocalAdmin 输出 WARN 提示（仅提示一次，避免刷屏） |
| `report_rate_limit` / `report_rate_window_minutes` | 举报限流：每人在窗口（默认 30 分钟）内最多提交 `report_rate_limit`（默认 5）次，超出拒绝并提示 |

> ⚠️ 请确保服务器防火墙放行对应端口（默认 8081/TCP）。

---

## 鉴权模型

系统有两种互不相同的 token：

| Token | 用途 | 传入方式 | 强度要求 |
|-------|------|----------|----------|
| `verify_token` | 只读数据接口 `/get_sl_data` | URL 查询参数 `?token=` | 无强制要求（建议复杂化） |
| `control_token` | 所有 `/control/*` 控制接口 **和语音转发 WebSocket** | `X-Control-Token` 请求头（也兼容 `?token=`）；语音 WS 支持 `X-Control-Token` 头（也兼容 `?key=`） | 启动时强制校验格式 |

> 🔒 **token 传递建议**：优先用 `X-Control-Token` 请求头——`?token=` / `?key=` 查询串会落入反向代理/访问日志，属于弱一环。三个入口（数据/控制/语音 WS）均已支持头鉴权，新客户端应一律走请求头。

**暴力破解防护**：按 IP 失败锁定**分权限级**（数据接口与控制/语音各一张失败表，刷数据接口不会锁死高权限通道）；5 分钟窗口失败 10 次锁 5 分钟；token 比较使用常量时间算法。语音 WS 未配置 `control_token` 时一律拒绝。详见 wiki [Security-Model](https://github.com/DNTOF/SLDataAPI/wiki/Security-Model)。

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

**端点一览**（请求字段、curl 示例与响应示例见 wiki [HTTP-API](https://github.com/DNTOF/SLDataAPI/wiki/HTTP-API)）：

| 类别 | 端点 |
|------|------|
| 命令 | `/control/command`（含点命令直连执行） |
| 玩家 | `/control/player/{kick|ban|role|teleport|mute|msg|effect|state}` |
| 回合 | `/control/round`（restart/end/start）、`/control/wave`（重生波次） |
| 播报/核弹 | `/control/cassie`（含中文字幕）、`/control/warhead` |
| 地图 | `/control/map`（layout/doors/elevators/lights/seed）、`/control/map/export` |
| 举报 | `/control/reports`（list/handle，需 `report_enabled: true`） |
| 插件 | `/control/plugins`（列表/暂存/apply/reload）、`/control/slplayer` |
| 封禁 | `/control/ban_list`、`/control/ban/add`、`/control/ban/revoke` |
| 运维 | `/control/logs`、`/control/files/{list|read|write}`（受 `file_root` 白名单限制） |

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

`/control/` 是公用的控制命名空间：一次性调用走 HTTP POST，高频调用走 WebSocket 长连接（复用连接、仅剩帧级开销），两种方式调用同样的端点、语义完全一致——但**同一时刻只有一条通路开放**（`control_transport` 二选一硬互斥）。`/get_sl_data` 数据接口始终走 HTTP 不变。

**端点：** `ws://<服务器IP>:8081/control?key=<control_token>`（别名 `/ws/control`；token 支持 `?token=` 或 `X-Control-Token` 头）

**协议要点**（JSON 文本帧，完整消息示例与七类事件 payload 见 wiki [WS-Control-Protocol](https://github.com/DNTOF/SLDataAPI/wiki/WS-Control-Protocol)）：

- 建连即收 `hello`；`ping/pong` 心跳（建议 ~25s，90s 无入站消息断开）
- `call{reqId,path,body}` → `result{reqId,ok,status,data}`：path+body 与 HTTP POST 完全一致，结果可乱序返回
- `subscribe_events` / `unsubscribe_events` 订阅事件流：`round_started` / `round_ended` / `player_joined` / `player_left` / `player_died` / `elevator_used` / `door_opened` 七类事件，尽力而为投递
- 限制：全局 8 连接 / 单连接 4 并发 call（超限 429）/ 单消息 256KB；关闭码语义见 wiki

---

## 语音转发（v2.3 / 代号 SPY）

启用 `voice_enabled` 后，插件在独立端口（默认 8082）上提供语音 WebSocket，实时推送服务器内**所有频道**的语音（近距离、对讲机、Intercom 全局广播、SCP 频道、旁观者等），解码为 48kHz 单声道 float32 PCM。

**端点：**

| 端点 | 说明 |
|------|------|
| `GET /ws?key=control_token`（或 `X-Control-Token` 头） | WebSocket 升级，实时语音流（需要 `control_token` 鉴权） |
| `GET /status?key=control_token`（或 `X-Control-Token` 头） | JSON：当前正在说话的玩家（昵称/UserID/角色/频道，1.5s 无新包视为停止） |

**帧格式：**

- 连接成功：文本帧 `{"type":"hello","sampleRate":48000,"channels":1,"format":"float32"}`
- 说话者信息（新一轮讲话 / 频道或角色变化时推送）：文本帧 `{"type":"speaker","nickname":"...","userid":"...","playerid":n,"role":"...","channel":n}`
- 语音帧（二进制）：`[0]=0x01 [1]=channel [2-3]=playerId(LE) [4-7]=seq(LE) [8..]=float32 PCM`，每包为 10ms Opus 帧（480 样本，约 100 包/秒）

**实现要点：** 基于 LabAPI `SendingVoiceMessage` 事件（服务器收到的每个语音包触发，内容哈希去重防重复解码）；解码器按说话者 netId 分开维护、异常自动重建；**主线程零阻塞保证**——读侧非阻塞收取、发送侧 Poll 可写检查（缓冲满跳帧）+ 3s 判死 + 250ms 发送超时兜底，对端停止收数据绝不可能冻结服务器主线程；连接上限 8、握手超时 10s、鉴权失败当场断开。

---

## 语音录音取证（v2.5.1 推出 · 代号 Yagami Light → 当前 GIS,GNSS,RS!）

开启 `voice_record_enabled`（需同时开启 `voice_enabled`）后，**每局游戏自动保存一个压缩包**到录音目录（默认 `%AppData%/SCP Secret Laboratory/SLDataAPI/VoiceRecords`）：

```
voice_round_3_20260818_223100.zip
└─ 内含:
   ├─ voice_round_3_20260818_223100.Proximity.wav  # 近距离频道音轨（人类阵营主频道）
   ├─ voice_round_3_20260818_223100.Radio.wav      # 对讲机频道音轨
   ├─ voice_round_3_20260818_223100.Intercom.wav   # Intercom 频道音轨
   ├─ voice_round_3_20260818_223100.Scp.wav        # SCP 频道音轨（SCP 阵营专用）
   └─ voice_round_3_20260818_223100.timeline.log   # 时间轴：谁在什么时候说了多久
```

**核心特性**：按频道分轨（SCP 与人类频道独立 WAV 绝不混合，仅同频道多人同时说话逐采样混合）；时间轴 TSV 与音频**采样级对齐**（`采样号 = 回合内秒 × 48000 = WAV 内字节位置 ÷ 2`，跨频道同一采样号对齐同一瞬间，可直接切段取证）；回合开始建档、结束定稿，zip 打包在后台线程完成、停服同步等待；隐私告知（每局开局向玩家显示录音声明）；按 `voice_record_max_rounds` 自动清理最旧局。

完整格式、对齐原理与行为细节见 wiki [Voice-Recording](https://github.com/DNTOF/SLDataAPI/wiki/Voice-Recording)。

---

## 安全注意事项

- `/control/command` **等价于本机控制台权限**：`control_token` 一旦泄露即完全沦陷——务必只通过受信内网或反向代理白名单暴露，`control_enabled` 默认关闭是有意为之。
- `control_token` 与 `verify_token` 分离：只读查询可以放心交给监控/机器人，控制权限单独保管。
- **连接层 DoS 防护**：HTTP Slowloris 总时限 15s（408）；WS 控制分片组装 30s + 空闲 90s + 256KB/连接/并发上限；语音监听连接上限 8 + 握手超时 10s + 发送 Poll 守卫（对端停收数据不冻结主线程）。
- **文件端点四重防线**：路径白名单（`file_root` 内，防 `..` 穿越）→ Windows 系统目录禁读禁写 → 仅配置文件扩展名（yml/yaml/txt/json/cfg/ini/conf/config/xml/properties）→ **游戏数据目录与插件自身配置目录禁止访问**（防提权/防改写自身配置）。完整安全模型见 wiki [Security-Model](https://github.com/DNTOF/SLDataAPI/wiki/Security-Model)。

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
在 AstrBot 插件中使用 `/bindlab <IP> <token>` 绑定本接口后，`/sl` 命令查询时会优先调用本接口，并在服务器名称后显示 `[LAB]` 标记：

```
名称: 我的服务器[LAB]
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
├── Data/                           # [SLDataAPI.Data] Models.cs（数据快照）/ ControlModels.cs（控制 DTO）
├── Control/                        # [SLDataAPI.Control] ControlController（端点业务）/ WsControlService（WS 长连接）/ ControlAuth（鉴权）
├── Services/                       # [SLDataAPI.Services] HttpServer / DataCollector / MainThreadExecutor / FileService / ReportService 等
├── Voice/                          # [SLDataAPI.Voice] VoiceService（语音转发 SPY）/ VoiceRecorder（录音取证）
├── Map/                            # [SLDataAPI.Map] 地图布局采集与导出
├── Integrations/                   # [SLDataAPI.Integrations] EXILED 反射桥 + SLPlayer / OmegaWarhead 探测
├── Capture/                        # [SLDataAPI.Capture] Harmony 补丁：控制台输出捕获
└── SLDataAPI.csproj                # net48；引用本机游戏程序集 + LabApi.dll（路径可用 -p: 参数覆盖）
```

开发者上手（结构详解、端点添加流程、事件链保护/主线程派发约定、SSS UI 集成、构建发布）见 wiki [Development-Guide](https://github.com/DNTOF/SLDataAPI/wiki/Development-Guide) 与 [Architecture](https://github.com/DNTOF/SLDataAPI/wiki/Architecture)。
