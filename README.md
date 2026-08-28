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
control_token: 你的token             # 控制接口专用 token，与 verify_token 分离。⚠️ 直接写裸值，不要带任何引号（见下方引号警告）
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
> ⚠️ **token 引号警告（重点）**：`control_token` / `verify_token` 建议直接写**裸值**（`control_token: Qq10086@`），不要带引号。
> 如果你确实需要给含 `#`、`:` 等特殊字符的 token 加引号，**必须用 ASCII 引号 `'` 或 `"`**——
> 从聊天软件/网页复制来的**弯引号**（`‘’“”`）不是 YAML 引号语法，会被 YamlDotNet 当成 **token 内容的一部分**
> （长度+1、格式校验照常通过、启动零报错，但鉴权必然 403）。v2.5.4 起插件启动时会检测引号字符并直接报错点破。
> 顺带：`''` 与 `""` 表示空字符串（禁用/留空），填值时删掉引号直接写内容即可。

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

**暴力破解防护**（`ControlAuth.cs`）：按 IP 失败锁定**分权限级**——只读数据接口（`verify_token`）与控制/语音（`control_token`）各用一张失败表，攻击者刷数据接口不会锁死管理员的高权限通道；同一 IP 在 5 分钟窗口内失败 10 次即锁定 5 分钟，失败表周期清扫（过期条目自动移除，海量源地址不会无界增长）。token 比较使用常量时间算法，避免时序侧信道。语音 WS 未配置 `control_token` 时一律拒绝（不开放匿名监听）。

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
| `/control/cassie` | `message`, `isNoisy`?, `translation`? | CASSIE 播报。`translation` 非空时：语音播报 `message` 原文（含音效代码），游戏内字幕显示 `translation`（纯文本，不解析音效代码）——"英文播报 + 中文字幕"；空则仅播报。isHeld/isSubtitles 在新 TTS 体系无对应开关（忽略） |
| `/control/warhead` | `action`: `start` / `stop` / `detonate` | 核弹控制 |
| `/control/reports` | `action`: `list` / `handle`, `id`? | 举报记录管理（需 `report_enabled: true`）。`list` 返回全部**未处理**（pending）记录：举报人/被举报人 steam64、举报人 IP、原因、时间、记录 id；`handle` 传 `id` 将该记录标记为已处理（handled）。记录存于插件配置目录 `reports.json`，超 `report_max_records` 自动清理最旧已处理记录 |
| `/control/map` | `action`: `seed` / `layout` / `doors` / `elevators` / `lights` | 地图信息与控制。doors 支持 `scope`: `type`(默认,按 door_type 单门) / `all`(全部门) / `all_not_list`(枚举未列出的门，机关门/随机门等)；elevators 支持 `elevator_type`(当前 ElevatorGroup 名如 Nuke01/LczA01/GateA01=单轿厢粒度，或旧名 Nuke/GateA/GateB/Scp049/LczA/LczB/ServerRoom=整组操作) + `command`: `up`/`down`/`send`（send 直达 `level` 目标楼层，对齐 RA elevator send）+ `scope`: `type`/`all` |
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
| S→C | `{"type":"hello","version":"<插件版本>","endpoints":"/control/*"}` | 建连后立即推送 |
| C→S | `{"type":"ping"}` | 心跳（建议 ~25s 一次） |
| S→C | `{"type":"pong"}` | 心跳应答 |
| C→S | `{"type":"subscribe_events"}` | 订阅服务器事件推送（v2.5.4，见下方「事件推送」） |
| C→S | `{"type":"unsubscribe_events"}` | 取消事件订阅 |
| S→C | `{"type":"events_subscribed"}` / `{"type":"events_unsubscribed"}` | 订阅状态确认 |
| S→C | `{"type":"event","event":"player_died","utc":"...","data":{...}}` | 服务器事件推送（仅订阅后） |
| C→S | `{"type":"call","reqId":"c1","path":"/control/player/kick","body":{...}}` | 控制调用；`path` + `body` 与 HTTP POST 完全一致 |
| S→C | `{"type":"result","reqId":"c1","ok":true,"status":200,"data":{"success":true,...}}` | 调用结果；`data` 即 HTTP 响应体；失败时 `ok:false` 且带 `message`（对应 400/403/500 文案） |
| S→C | `{"type":"error","message":"..."}` | 协议层错误（非法 JSON / 未知类型等） |

- `reqId` 由调用方生成，仅需连接内唯一；**结果允许乱序返回**（并发调用时按完成顺序回包）
- 限制：全局连接上限 8；单连接并发 `call` 上限 4（超出立即回 `result{ok:false,status:429}`）；单消息上限 256KB；90s 无入站消息判定超时断开
- 除 JSON 层 `ping/pong` 外，协议层 WS ping 帧也会被应答

**事件推送（v2.5.4 · 订阅制）：**

订阅 `subscribe_events` 后，服务器实时推送以下事件（`type:"event"`，`event` 为事件名，`data` 为负载）：

| 事件名 | 触发时机 | data 字段 |
|--------|----------|-----------|
| `round_started` | 回合开始 | `started_at`（UTC ISO8601） |
| `round_ended` | 回合结束 | `leading_team`（胜利阵营）、`ended_at` |
| `player_joined` | 玩家加入 | `nickname`、`userid` |
| `player_left` | 玩家离开 | `nickname`、`userid` |
| `player_died` | 玩家死亡 | `nickname`、`userid`、`old_role`、`attacker_nickname`、`attacker_userid`（无攻击者时缺省） |
| `elevator_used` | 玩家使用电梯 | `nickname`、`userid`、`elevator_group`（如 Nuke01 / LczA01） |
| `door_opened` | 玩家交互门（含权限门） | `nickname`、`userid`、`door`（DoorName 枚举名）、`can_open`（该玩家是否有权打开） |

推送与 `call` 调用互不阻塞（同一连接复用）；事件为尽力而为投递——连接断开期间的事件不补发，重连后需重新 `subscribe_events`。

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
| `GET /ws?key=control_token`（或 `X-Control-Token` 头） | WebSocket 升级，实时语音流（需要 `control_token` 鉴权） |
| `GET /status?key=control_token`（或 `X-Control-Token` 头） | JSON：当前正在说话的玩家（昵称/UserID/角色/频道，1.5s 无新包视为停止） |

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

> **按频道分轨**：每个语音频道独立一个 WAV（48kHz/16bit/单声道），**频道之间绝不混合**——SCP 频道与人类频道是游戏里独立的听觉流，混在一起会丢失阵营听觉信息。只有**同频道内**多人同时说话才逐采样混合（那是真实“同听”关系）。未产生流量的频道不生成文件。

**时间轴格式**（制表符分隔，可导入 Excel / 脚本解析；**与音频采样级对齐**）：

```
# SLDataAPI 语音时间轴（v2.5.4 · GIS,GNSS,RS!）
# 局号: 3  回合开始: 2026-08-18 22:31:00.123  采样率: 48000Hz
# 列: 回合内秒	绝对时间	事件	昵称	steamid	角色	频道	netid	详情
# 对齐: 采样号 = 回合内秒 × 48000 = 任一频道 WAV 文件内精确位置（帧间已补静默，可直接切段取证）
# 分轨: 每个语音频道（Proximity/Radio/Intercom/Spectator/Scp…）独立一个 WAV，频道间不混合；同频道多人同时说话才混合
0.000	22:31:00.123	回合开始
1.200	22:31:01.323	通道开始	Proximity	0	voice_round_3_20260818_223100.Proximity.wav
12.345	22:31:12.468	说话开始	PlayerOne	76561198000000000	D级人员	0	1234	起点采样=592560
16.789	22:31:16.912	说话结束	PlayerOne	76561198000000000	D级人员	0	1234	时长=4.444s 起点采样=592560 终点采样=805872
42.000	22:31:42.123	说话开始	Scp173Fan	76561198123456789	SCP-173	4	5678	起点采样=2016000
...
623.456	22:41:23.579	回合结束	时长=623.333s 丢帧=0 终点采样=29920000
623.500	22:41:23.623	通道归档	Proximity	voice_round_3_20260818_223100.Proximity.wav	终点采样=29920000
623.500	22:41:23.623	通道归档	Scp	voice_round_3_20260818_223100.Scp.wav	终点采样=29920000
```

**对齐原理**：各频道 WAV 在说话帧之间**补写了静默**、文件末尾补齐到回合结束点，因此对所有频道文件统一成立 `采样号 = 回合内秒 × 48000 = WAV 内字节位置 ÷ 2`——跨频道对照时用同一个采样号即可对齐同一时刻（如“SCP 频道第 2016000 采样”与“近距离频道第 2016000 采样”是同一瞬间）。用任意支持按采样定位的播放器/编辑器跳转，或用脚本按 `起点采样/终点采样` 直接切段导出。

**行为说明：**

- 回合开始建档、回合结束定稿；**定稿与 zip 打包在后台线程完成**（快照隔离，不占主线程、不阻塞下一局），服务器停服时同步等待打包结束
- **隐私告知**：录音启用时，每局开始 1 秒后向所有玩家显示 3 秒声明"为了保证游戏公平性，本局游戏将会被录音，具体详询服务器管理员。"（中途加入的玩家不重复提示，仅开局告知）
- 打包格式：标准 zip（PCM 压缩率约 40%-60%），内含各频道 WAV + 时间轴；打包完成后删除散件。若打包失败（磁盘满等），散件文件保留在录音目录并记日志，下局清理兜底
- **采样级对齐**：帧间补静默，时间轴秒数 × 48000 = **任一频道文件**内的精确采样位置，跨频道对照用同一采样号
- **频道隔离**：SCP / 人类（近距离/对讲机/Intercom）等各自独立 WAV，不混合；**同频道多人同时说话 = 逐采样混合**（求和钳制防溢出，任何一方不丢失），实现为 0.4s 混合窗口延迟落盘
- 说话段按静默 0.8s 划分；说话者静默 1.5s 被清理时收尾其讲话段（与转发管线的状态一致）
- 磁盘写入在**后台线程**完成，主线程只入队（有界队列 ≈9MB）；写入过慢时丢帧并限频告警，丢帧数记入时间轴末行
- 磁盘占用参考：**每个有流量的频道**约 5.5MB/分钟原始 PCM（一小时局 ≈ 330MB/频道），zip 后约 40%-60%；按 `voice_record_max_rounds` 清理
- 按 `voice_record_max_rounds` 自动清理最旧局（wav 与时间轴成对删除）；`0` 或负数 = 不清理
- 录音目录位于游戏数据目录内，天然受文件端点顶级防线保护（无法通过 `/control/files/*` 读写）

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
│   ├── VoiceService.cs             # [SLDataAPI.Voice] 语音转发（SPY）：WebSocket、Opus 解码、PCM 推送
│   └── VoiceRecorder.cs            # [SLDataAPI.Voice] 每局语音录音取证（WAV 混合音轨 + 时间轴日志，v2.5）
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
