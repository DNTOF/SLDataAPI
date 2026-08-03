# SLDataAPI

**版本：** 2.2.0  
**依赖：** EXILED 9.x · MEC · Newtonsoft.Json · Harmony（服务器自带，不打包）  
**用途：** 在 SCP:SL 游戏服务器上暴露一个轻量 HTTP 接口，供 WebUI / AstrBot 等外部程序轮询实时服务器数据，并通过 `/control/*` 控制接口远程执行管理操作。

---

## 功能总览

| 能力 | 说明 |
|------|------|
| 实时数据查询 | `GET /get_sl_data`：人数、回合、核弹、玩家列表（含 SteamID / 坐标）、DNT_OF 系列插件状态 |
| 控制接口 | `POST /control/*`：命令执行、玩家管理、回合控制、CASSIE、核弹、地图、封禁、日志（任意文件选择）、文件（默认关闭，需显式开启） |
| 命令输出捕获 | Harmony 补丁捕获 `ServerConsole.AddLog` 输出，`/control/command` 可拿到插件命令（如 SLPlayer `.m`）的完整回显 |
| 地图数据 | 按 seed 提供本回合布局（LCZ/HCZ 每回合随机），可导出 atlas 等原始数据供外部重建地图 |
| 插件管理 | 列表读取配置文件启停状态；启停走"暂存 → 保存并重载"批量模式（SLDataAPI 自身禁止禁用） |
| 文件防线 | 文件端点四重防线：路径白名单 / Windows 目录禁写 / 仅配置文件扩展名 / 游戏数据与自身配置目录禁访问 |
| 自动更新 | 启动时检查 GitHub Releases；检测到新版本自动下载并替换 DLL（程序集/名称/强名称签名三重校验，重启游戏服生效，旧版备份 .bak） |

---

## 安装

1. 运行：
```
dotnet build -c Release
```
2. 将 `SLDataAPI.dll` 放入 `EXILED/Plugins/` 目录
3. 启动服务器，EXILED 会自动生成配置文件
4. 按需修改配置（见下方），重启服务器生效

> 编译需要游戏程序集（`dependencies/` 下：`Assembly-CSharp.dll`、`Mirror.dll`、`UnityEngine*.dll` 等），仓库已内置，可直接构建；CI（GitHub Actions）同样依赖这些文件。

---

## 配置

配置文件路径：`EXILED/Configs/Plugins/s_l_data_a_p_i/7777.yml`

```yaml
s_l_data_a_p_i:
  is_enabled: true
  debug: false
  verify_token: "your_secret_token"   # 只读接口鉴权 token（/get_sl_data）
  http_port: 8081                     # HTTP 监听端口
  push_interval_seconds: 8            # 后台数据刷新间隔（秒）

  # ===== 控制接口（v2.1）=====
  control_enabled: false              # 是否启用 /control/*（默认关闭；关闭时一律 404）
  control_token: ""                   # 控制接口专用 token，与 verify_token 分离
  auto_update_check: true             # 启动时检查 GitHub Releases 新版本
  auto_update_install: true           # 检测到新版本时自动下载并替换 DLL（重启游戏服生效，旧版备份 .bak）
  file_root: ""                       # /control/files/* 根目录（绝对路径）；留空=禁用文件端点
  log_directory: ""                   # 服务器日志目录；留空=自动探测
```

**字段说明：**

| 字段 | 说明 |
|------|------|
| `control_token` | 控制接口专用 token。要求长度 ≥ 8，且同时包含大写字母、小写字母、数字、特殊符号；格式不合法时本次运行会**强制禁用控制接口**并在日志报错 |
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
| `control_token` | 所有 `/control/*` 控制接口 | `X-Control-Token` 请求头（也兼容 `?token=`） | 启动时强制校验格式 |

**控制接口暴力破解防护**（`ControlAuth.cs`）：同一 IP 在 5 分钟窗口内失败 10 次即锁定 5 分钟；token 比较使用常量时间算法，避免时序侧信道。

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

```json
{ "success": true, "message": "操作成功", "data": { } }
```

| 端点 | 请求字段 | 说明 |
|------|----------|------|
| `/control/command` | `command` | 执行服务器控制台命令（等价本机控制台权限，见安全警告） |
| `/control/player/kick` | `target`, `reason` | 踢出玩家（target 支持昵称/ID/SteamID） |
| `/control/player/ban` | `target`, `reason`, `duration` | 封禁玩家（duration 单位：天） |
| `/control/player/role` | `target`, `role` | 设置玩家角色 |
| `/control/player/teleport` | `target`, `x`, `y`, `z` | 传送玩家到指定坐标 |
| `/control/player/mute` | `target`, `mute` | 语音禁言/解除（bool） |
| `/control/player/msg` | `target`, `message`, `msg_type`(`hint`/`broadcast`), `duration_seconds` | 私聊消息/广播 |
| `/control/player/effect` | `target`, `effect`, `effect_duration` | 施加状态效果 |
| `/control/player/state` | `target`, `godmode`?, `bypass`?, `health`?, `intercom`? | 查询/设置玩家状态（均为可选字段） |
| `/control/round` | `action`: `restart` / `end` / `start` | 回合控制 |
| `/control/cassie` | `message`, `isHeld`?, `isNoisy`?, `isSubtitles`? | CASSIE 播报 |
| `/control/warhead` | `action`: `start` / `stop` / `detonate` | 核弹控制 |
| `/control/map` | `action`: `seed` / `layout` / `doors` / `lights` | 地图信息与控制（seed 每回合固定，同 seed 布局恒定） |
| `/control/map/export` | — | 导出地图原始数据（atlas RGBA base64、glyph_pairs、zone_candidates 等），供外部重建 |
| `/control/slplayer` | `action`: `status` / `list` / `play` / `next` / `stop` / `volume` / `shuffle` / `reload` | 控制 SLPlayer 音乐（需服务器装有 SLPlayer 插件） |
| `/control/plugins` | `action`?: `stage`/`clear`/`apply`/`reload`（空=列表） | 插件管理。列表的 `enabled` 读**配置文件**的 is_enabled；`stage` 暂存启停（不写文件，SLDataAPI 自身禁止禁用）；`apply` 一次性写入全部暂存并重载插件 |
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

## 安全注意事项

- `/control/command` **等价于本机控制台权限**：大多数 RA 命令通过 `GameConsoleCommandHandler` 同时注册，可在此执行。`control_token` 一旦泄露即完全沦陷——务必只通过受信内网或反向代理白名单暴露，`control_enabled` 默认关闭是有意为之。
- `control_token` 与 `verify_token` 分离：只读查询可以放心交给监控/机器人，控制权限单独保管。
- 文件端点默认关闭；开启后所有操作都被限制在 `file_root` 内（路径规范化 + 前缀校验，防 `..` 穿越）。
- **文件端点四重防线**（任何角色一视同仁，读/写同规则）：
  1. 路径白名单：仅 `file_root` 内（防 `..` 穿越）
  2. 系统目录保护：**Windows 目录及其子目录禁止浏览/读取/写入**
  3. 扩展名白名单：**只允许操作配置文件**（`yml/yaml/txt/json/cfg/ini/conf/config/xml/properties`），exe/dll/bat/ps1 等一律拒绝（列表中以黄色标记且无法打开）
  4. 顶级防线：**游戏数据目录**（`%AppData%/SCP Secret Laboratory`）与 **SLDataAPI 自身配置目录**（`%AppData%/SCP Secret Laboratory/EXILED/Configs/Plugins/s_l_data_a_p_i`）禁止读/写/访问（列表中以黄色标记且无法打开）——防止篡改游戏配置/管理员名单实现提权，或改写插件自身配置

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

- 通过 `dummy` 命令或插件创建的 **NPC/Dummy 玩家**（`p.IsNPC == true`）
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

## 源文件结构

```
SLDataAPI/
├── Plugin.cs               # 插件入口，事件注册，HTTP 服务器生命周期，控制 token 启动校验
├── Config.cs               # EXILED 配置类（只读接口 + 控制接口全部配置项）
├── HttpServer.cs           # TcpListener HTTP 实现（0.0.0.0 绑定，不依赖 http.sys），路由与鉴权
├── DataCollector.cs        # 数据采集、缓存、定时刷新逻辑
├── Models.cs               # ServerData / PlayerInfo / DntofInfo 数据模型
├── ControlAuth.cs          # 控制接口鉴权：token 格式校验、常量时间比较、按 IP 失败锁定
├── ControlController.cs    # /control/* 端点业务逻辑（所有游戏调用经主线程派发）
├── ControlModels.cs        # 控制接口请求/响应 DTO
├── MainThreadExecutor.cs   # 将游戏/Mirror 调用派发到主线程执行并同步等待结果
├── CommandOutputCapture.cs # Harmony 补丁：捕获 ServerConsole.AddLog / CommandSender 输出
├── DntofDetector.cs        # 探测 DNT_OF 系列插件（SLPlayer / OmegaWarhead）运行时状态
├── SlPlayerController.cs   # 反射控制 SLPlayer 音乐控制器（list/play/next/volume 等）
├── MapLayoutService.cs     # 本回合房间布局采集（RoomIdentifier，LCZ/HCZ 每回合随机）
├── MapExportService.cs     # 导出地图原始数据（atlas / glyph / zone 候选等，/control/map/export）
├── ServerLogService.cs     # 服务器日志尾部读取（自动探测日志目录，支持过滤）
├── FileService.cs          # 文件端点：FileRoot 白名单、路径规范化防穿越
├── UpdateChecker.cs        # 启动时检查 GitHub Releases 新版本（仅日志提示）
└── SLDataAPI.csproj        # net48；引用游戏程序集（dependencies/）+ ExMod.Exiled 9.0.0
```
