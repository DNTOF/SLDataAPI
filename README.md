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

版本： 2.6.0-preview-DevOnly（开发代号 Kerckhoffs）  
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
| 控制接口 | `/control/*` 控制端点：命令执行（含点命令回显）、玩家管理、回合/波次、CASSIE、核弹、地图、封禁、日志、文件、插件管理等；支持 HTTP POST 或 WebSocket 长连接（二选一） |
| 事件流推送（v2.5.4） | 控制长连接订阅后实时推送服务器事件：回合开始/结束、玩家进出/死亡、电梯使用、权限门交互 |
| 举报功能（v2.5.4） | 玩家在 Esc → 服务器设置 面板中举报违规玩家（下拉选人 + 原因 + 长按提交）；平台经 `/control/reports` 读取/处理记录。默认关闭 |
| 地图数据 | 按 seed 提供本回合布局（LCZ/HCZ 每回合随机），可导出原始数据供外部重建地图 |
| 插件管理 | LabAPI 插件 + 同服 EXILED 插件列表/启停暂存/apply/reload |
| 自动更新 | 启动时检查 GitHub Releases，自动下载替换 DLL（程序集/名称/强名称签名三重校验） |
| 语音转发（SPY） | WebSocket 实时推送全频道语音（近距离/对讲机/Intercom/SCP 频道等），ControlToken 鉴权 |
| 语音录音取证（v2.5） | 每局自动保存分轨 WAV + 时间轴日志（谁在何时说了多久），用于不公平问题取证，自动清理旧局 |
| 控制操作审计日志（v2.5.5-preview） | 远程控制的主动侵入性操作自动记录到 `control_log.json`（不计 IP，只记时间与操作细节），管理层问题追责用，默认开启 |

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

# ===== 控制操作审计日志（v2.5.5-preview 推出 · 代号 Everest C1）=====
control_log_enabled: true             # 记录远程控制的主动侵入性操作（命令/玩家管理/回合/播报/核弹/波次/门梯灯/举报处理/插件/封禁/文件写等），不计 IP，只记时间与操作细节
control_log_max_records: 500          # 控制日志最大条数（超出删最旧；0/负数=不清理）
```

> ⚠️ **配置常见坑**（键名必须 snake_case、值格式错误导致整个文件回退默认值、token 写法与弯引号/特殊字符坑、启动自检说明）与各配置项完整字段说明见 wiki [Configuration](https://github.com/DNTOF/SLDataAPI/wiki/Configuration)。
>
> 插件本身的启停开关不再出现在此文件中：LabAPI 用插件目录下的 `properties.yml`（`is_enabled`）管理插件加载与否，可通过 `/control/plugins` 端点修改。
> ⚠️ 请确保服务器防火墙放行对应端口（默认 8081/TCP）。

---

## 鉴权模型（2.6 双轨）

| 通道 | 鉴权 | 配置 |
|------|------|------|
| `GET /get_sl_data` | **保持原样** `verify_token`（`?token=` 等与 2.5 相同） | `config.yml` |
| `/control/*`、控制 WS、语音口 | **API Key**（明文只在创建时显示一次） | 独立文件 `apikey.config` |

- 控制面请求头：`Authorization: Bearer <key>` 或 `X-SLDataAPI-Key: <key>`（**已移除**控制面 `X-Control-Token` / `?token=` / `?key=`）。
- 生成：游戏内 / LocalAdmin 命令 `sldataapi apikey create <id> <duty|admin>`；`revoke` / `list`。
- 模板：`duty`（只读信息含地图读，默认无传送/管理/语音）· `admin`（控制面全开）。可用 `endpoints_override` 细调。
- `control_token` **已废弃**（启动警告并忽略）；`control_enabled` 仍门控控制面。
- 路径已按 RA 面板重分（旧扁平路径 **404**，无别名）。契约见 [`docs/AUTH_AND_API_CONTRACT_2.6.md`](docs/AUTH_AND_API_CONTRACT_2.6.md)。

**暴力破解防护**：数据口与控制/语音分表锁定；API Key 用 SHA-256 指纹比对。详见 wiki [Security-Model](https://github.com/DNTOF/SLDataAPI/wiki/Security-Model)。
## HTTP 接口

- **数据接口**：`GET /get_sl_data?token=<verify_token>` —— 服务器实时状态快照（人数/回合/核弹/玩家列表/插件状态）
- **控制接口**：`POST /control/*`，请求体 JSON，鉴权 `X-Control-Token` 头，响应统一 `{success, message, data}`

每个端点的 curl 示例、响应示例、字段说明、错误码见 wiki [HTTP-API](https://github.com/DNTOF/SLDataAPI/wiki/HTTP-API)。

---

## 控制接口 WebSocket（v2.5 · `/control`）

控制接口的 WebSocket 长连接通道（`control_transport: ws` 时可用，与 HTTP 二选一硬互斥）：`call/result` 信封调用与 HTTP POST 完全同义，另支持 `subscribe_events` 七类服务器事件流推送。端点 `ws://<服务器IP>:8081/control?key=<control_token>`（别名 `/ws/control`）。完整协议、消息示例与事件 payload 见 wiki [WS-Control-Protocol](https://github.com/DNTOF/SLDataAPI/wiki/WS-Control-Protocol)。

---

## 语音转发（v2.3 / 代号 SPY）

启用 `voice_enabled` 后，插件在独立端口（默认 8082）实时推送服务器内**所有频道**的语音（近距离/对讲机/Intercom/SCP 频道等，48kHz float32 PCM），`control_token` 鉴权。端点（`/ws` 语音流、`/status` 说话者查询）、帧格式与实现要点见 wiki [Voice-Forwarding](https://github.com/DNTOF/SLDataAPI/wiki/Voice-Forwarding)。

---

## 语音录音取证（v2.5.1 推出 · 代号 Yagami Light → 当前 GIS,GNSS,RS!）

开启 `voice_record_enabled`（需 `voice_enabled`）后，**每局自动保存一个压缩包**到录音目录（默认 `%AppData%/SCP Secret Laboratory/SLDataAPI/VoiceRecords`）：按频道分轨 WAV（SCP 与人类频道绝不混合）+ 时间轴日志（TSV，与音频采样级对齐，可直接切段取证）；回合结束定稿、自动清理旧局。完整格式与行为细节见 wiki [Voice-Recording](https://github.com/DNTOF/SLDataAPI/wiki/Voice-Recording)。

---

## 安全注意事项

- `/control/command` **等价于本机控制台权限**：`control_token` 一旦泄露即完全沦陷——务必只通过受信内网或反向代理白名单暴露，`control_enabled` 默认关闭是有意为之。
- `control_token` 与 `verify_token` 分离：只读查询可以放心交给监控/机器人，控制权限单独保管。
- **连接层 DoS 防护**：HTTP Slowloris 总时限 15s（408）；WS 控制分片组装 30s + 空闲 90s + 256KB/连接/并发上限；语音监听连接上限 8 + 握手超时 10s + 发送 Poll 守卫（对端停收数据不冻结主线程）。
- **文件端点四重防线**：路径白名单（`file_root` 内，防 `..` 穿越）→ Windows 系统目录禁读禁写 → 仅配置文件扩展名（yml/yaml/txt/json/cfg/ini/conf/config/xml/properties）→ **游戏数据目录与插件自身配置目录禁止访问**（防提权/防改写自身配置）。完整安全模型见 wiki [Security-Model](https://github.com/DNTOF/SLDataAPI/wiki/Security-Model)。

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

## 开发者文档

源码结构、命名空间映射、添加控制端点流程、事件链保护/主线程派发约定、SSS UI 集成、构建与发布规范见 wiki [Development-Guide](https://github.com/DNTOF/SLDataAPI/wiki/Development-Guide) 与 [Architecture](https://github.com/DNTOF/SLDataAPI/wiki/Architecture)。

---

## 社区与支持

- 需要**开箱即用的管理平台**或想**提出新功能建议**？欢迎加入 QQ 群：**984840871**
- 问题反馈：https://github.com/DNTOF/SLDataAPI/issues

## 许可证

本项目基于 [GNU General Public License v3.0](LICENSE)（GPLv3）开源发布。
