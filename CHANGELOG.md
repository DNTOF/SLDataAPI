# Changelog

## 未发布（开发中）

以下内容在开发分支上，尚未发布；发布时并入对应版本条目。

### 控制面

- `/control/player/inventory` 由 501 占位改为实现：`list`（默认，只读）/ `add` / `remove` / `clear`。`add`、`remove` 单次上限 8；弹药类型不走本端点（400 `ammo_not_supported`）；物品栏满返回 409 `inventory_full`。支持 `Lantern`、`MarshmallowItem` 等节日物品，节日未开启时返回 409 `holiday_inactive`
- 新增 `/control/player/candy`：给 SCP-330 糖果（指定 `candy` 或随机），单次上限 6。糖果池跟随游戏当前节日（万圣节 13 种，否则 8 种普通糖果），节日专属糖果在节日未开启时返回 409 `holiday_inactive`
- 新增 `/control/map/status`（只读）：回合统计、LCZ 去污、发电机、核弹（拉杆 / 锁定 / 倒计时）、SCP-914 旋钮、节日状态快照
- `/control/map/facility` 的 `lights` 新增 `color`（`#RGB` / `#RRGGBB` / `#RRGGBBAA` / `reset`）、`flicker_seconds`（0.1-300）与 `scope: "all"`（全部房间）
- `/control/player/data` 新增 `max_health`、`artificial_health` / `max_artificial_health`、`hume_shield` / `max_hume_shield`、`stamina`、`items`、`ammo`、`group`、`godmode`、`noclip`、`bypass`、`overwatch`、`spectating`、`zone`、`is_speaking`、`is_muted`、`is_intercom_muted`、`voice_channel`、`is_dummy`（不含 IP）；`target` 在 UserId 未命中时按 PlayerId / 昵称解析（**不按 IP 匹配**）
- `/control/player/effects` 开启节日效果（如 `MarshmallowEffect` 棉花糖人）前检查节日是否开启，未开启返回 409 `holiday_inactive`；关闭效果（`effect_intensity: 0`）不受限制

### 节日门控

- 节日专属功能运行时判定：以游戏自身的 `IsAvailable` 与当前节日为准，游戏重新开启节日时无需改代码即可用
- 不可用时统一返回 `{"success":false,"message":…,"data":{"code":"holiday_inactive","feature":…,"active_holiday":…,"required":[…]}}`（409）；游戏版本缺少所需类型 / 成员时返回 `feature_unavailable`（501），不抛 500
- 节日相关类型 / 成员按名称反射查找并缓存，未来游戏版本删除或改名时 fail-closed
- 测试辅助（随正式版保留）：环境变量 `SLDATAAPI_HOLIDAY_OVERRIDE`（`None` / `Christmas` / `Halloween` / `AprilFools`）只影响插件的门控判定、只收紧不放宽，用于在节日版本上演练「节日未开启」路径；设置时启动告警

### 事件流（控制 WS）

- 新增低频事件：`player_banned`、`player_kicked`、`player_escaped`、`player_role_changed`、`player_reported`（游戏内举报与插件举报面板）、`wave_respawned`（原始 `faction` + 短名 `team`：`mtf` / `chaos`，其他阵营为原值小写）、`decontamination_started`、`generator_activated`、`scp_terminated`、`warhead_started`、`warhead_stopped`、`warhead_detonated`。事件数据不含 IP

### ACL

- 端点目录：`/control/player/inventory` 改为 `true`（admin 默认获得），新增 `/control/player/candy`（admin 默认获得）；duty 默认均拒绝。`/control/map/status` 属于 `/control/map/`，duty 默认可读
- 旧 `apikey.config` 自动迁移：文件没有 `endpoint_catalog_version`（旧版生成）且 `endpoint_catalog` 中 `/control/player/inventory` 为旧 501 占位值 `false` 时，加载时按 `true` 处理并打一行 info 日志；文件本身不改写，只改这一项（模板与 `endpoints_override` 不动，duty 仍拒绝）。新生成 / 保存的文件写 `endpoint_catalog_version: 2`，此后显式写 `false` 会被尊重

### 测试

- 新增 `tests/SLDataAPI.Features.Tests`：节日门控、反射缓存、新端点入参规则

## 2.6.2-preview Holiday26

SLDataAPI **2.6.2-preview Holiday26**（相对 2.6.1 Mores，含其全部 TLS 改动）。以 GitHub **预发布（Pre-release）** 形式发布，适配游戏 **14.2.8（万圣节测试版，Steam `holiday` 分支）** 新增的两处接口。编译引用 14.2.8 的 `Assembly-CSharp.dll`（无法用 14.2.7 的程序集编译）；设计上在 14.2.7 运行时缺少新成员会自动回落，但未实测。

### 数据口

- `get_sl_data` 新增 `game_version`（游戏版本串）与 `active_holiday`（`None` / `Christmas` / `Halloween` / `AprilFools`）

### 控制面

- `/control/player/data` 新增：
  - `effects`：玩家当前生效的效果（`name` / `intensity` / `duration`（0=无限期）/ `time_left` / `classification` / `door_permissions`）。14.2.8 起读 `PlayerEffectsController.EnabledEffects`，14.2.7 回落为 `AllEffects` 中已启用的项
  - `temporary_bypass`：`active` / `intensity` / `level`（`none` / `scp_override`（强度 1）/ `all`（强度 ≥2））/ `time_left` / `grants_door_access`（本服游戏版本中该效果是否参与开门判定：14.2.8+ 为 `true`）
  - `effect_door_permissions`：所有生效效果提供的开门权限合集（`DoorPermissionFlags`，如 `None` / `ScpOverride` / `All`）
- `/control/player/effects` 新增可选 `effect_intensity`（0-255，默认 1 与旧行为一致；0 = 关闭该效果）。例如 `{"target":"2","effect":"TemporaryBypass","effect_intensity":2,"effect_duration":30}` 给玩家 30 秒全部门权限。ACL 与审计沿用原端点（admin 默认开放、duty 默认拒绝，写入 `control_log`）

> 游戏侧说明：14.2.8 开门判定顺序变为「效果 → 角色 → 手持物品」，任一效果给出足够权限即放行。

### 更新检查

- 预发布构建（`InformationalVersion` 含 `preview` 等标记）在同版本号的正式版发布后也会提示更新（例如 2.6.2-preview → 2.6.2）；正式版构建行为不变

### 已知限制

- 本版本无法针对 14.2.7 编译；14.2.7 回落是运行时设计（缺少新成员时自动回落），未在 14.2.7 服务器上运行实测
- 带 TemporaryBypass 的真人开门未实测（假人无法开门）

## 2.6.1 Mores

SLDataAPI **2.6.1 Mores**（相对 2.6.0 PEAK）。以 GitHub **正式版**发布：2.6.1 曾于 2026-10-04 以 **2.6.1-preview RIDGE** 预发布；本正式版在其两项默认关闭的内测功能之上，加入同端口自签 TLS。2.6.0 PEAK 的更新检查会提示本版本。

> TLS：`tls_mode` 默认 `optional`，同端口同时接受 TLS 与明文，现有明文客户端无需改动。

> 内测功能：两个开关默认 `false`，关闭时新端点一律 404、行为与 2.6.0 PEAK 一致。

### 自签 TLS（同端口，contract v1）

- 新配置 `tls_mode: off | optional | required`（默认 `optional`；非法值按 `optional` 处理并告警）
- 首次启动在 `configs/<port>/SLDataAPI/` 生成自签证书 `sld_tls.pfx` 与随机口令 `sld_tls.pwd`（RSA-2048 / SHA-256 / 约 10 年，`CN=SLDataAPI`）；优先用游戏自带 Mono.Security 生成，失败回退 BouncyCastle（DER + 3DES PKCS#12）；口令与证书文件尽力收紧 ACL（Windows 上实际未收紧，见下方已知限制）。证书损坏时改名 `.bad-<时间>` 后重新生成
- HTTP/WS 主端口与语音端口**同端口**首字节嗅探：`0x16` → TLS（服务端 TLS 1.2 / 1.3，以运行时支持为准；Unity Mono 实际协商 TLS 1.2），否则明文。主端口握手计入 Slowloris 时限并另设 10s 上限；语音端口握手与读取在后台线程进行，主线程只做非阻塞收发（发送前仍有可写性检查）
- 启动横幅 `TLS: <mode>, SHA-256 <指纹>`（DER 的 SHA-256，大写十六进制冒号分隔）
- 控制 WS / 语音 WS `hello` 增加 `tls`（本连接是否加密）与 `cert_fingerprint`（`off` 时为 `null`）
- `tls_mode: required`：明文 HTTP 与明文 WS（控制 + 语音）返回 `426`，`data.code = "tls_required"`
- `tls_mode: off`：不生成/加载证书，行为与此前一致
- 新命令 `sldataapi cert show` / `sldataapi cert regen`（仅服务器本地控制台；RemoteAdmin 与远程控制通道均拒绝）。`regen` 热替换证书（仅对新连接生效），提示已固定指纹的客户端须重新确认，并写入控制审计日志
- 修复：语音端口未完成握手的连接超时改用真实时钟——服务器处于 idle mode 时 `Time.time` 停滞，原先静默连接可无限期占用 pending 名额

#### TLS 已知限制

- Windows 上 `sld_tls.pfx` / `sld_tls.pwd` 的 ACL 实际未收紧（沿用目录继承权限），请自行限制配置目录的访问
- `required` 模式的 `426` 响应未带 `Upgrade` 头
- `cert show|regen` 在 RemoteAdmin / 远程通道被拒绝的逻辑尚未在运行时实测
- 游戏自带 Mono 实际只协商 TLS 1.2

### 适配插件写操作（`beta_adapted_plugin_actions`）

- 新增 `PluginEndpointRegistry.TryRegisterActions(id, actions, out error)`：已 `TryRegister` 的插件可追加写 / 动作路由（每插件 ≤16，请求体 ≤64KB）；`TryRegister` 签名不变，只读插件无需改动
- 控制面 `POST /control/adapted/<plugin_id>/<action>`（HTTP 与 WS `call` 同一路由）；处理器在主线程执行，超时 3s；`AdaptedActionException` → 4xx
- ACL：新键 `/control/adapted/` **默认拒绝**，admin / duty / `all_control_true` 均不自动授予，须 `endpoints_override` 显式放开（可按插件 `"/control/adapted/<id>/": true`）
- 一律按写操作计，全部写入 `control_log`
- 开关打开时 `/plugins/adapted` 与 `get_sl_data.adapted_plugins[*]` 额外列出 `actions`；内置探测包装（SLPlayer / OmegaWarhead）不可追加动作
- 示例插件 `examples/AdaptedPluginSample` 增加 `echo` / `bump` 动作（旧版 SLDataAPI 下自动降级只读）

### FileRoot 的 WS 适配（`beta_file_root_ws`）

- `files/list|read|write` 本就可经 WS `call` 使用；受限于单条消息上限（WS 256KB / HTTP 64KB），大文件读写不可行
- 新增 `/control/files/stat`、`/control/files/read_chunk`（offset/length，base64，单块 ≤160KB，offset 0 附整文件 sha256）、`/control/files/write_chunk`（分块上传会话：连续 offset、整体 ≤512KB、120s 过期、每调用方 ≤2 个；`final` 时可校验 sha256，原子替换写入）
- 与原文件端点同一 ACL 键 `/control/files/`（默认拒绝）、同一 `file_root` 沙箱与写入防线；`write_chunk` 写入审计（块数据只记长度）
- WS `hello` 增加 `beta` 数组（`adapted_actions` / `file_chunks`）

### 安全加固（不受开关影响）

- FileRoot 沙箱拒绝路径中的符号链接 / junction（reparse point），防止经链接逃逸出 `file_root`；对全部文件端点（`files/list|read|write` 及新增分块端点）生效

### 修复

- 适配插件动作注册日志不再附带依赖加载顺序的「未开启」提示（适配插件可能先于 SLDataAPI 启用；discovery 实时读取开关，原提示会误导）

### 许可证 / 文档

- 全部源码文件加 SPDX 头（`GPL-3.0-only`，`Copyright (C) 2026 DNT_OF`）；新增双语 `NOTICE`；README 增加许可证说明；`SLDataAPI.csproj` 补充 `Authors` / `Copyright` / `PackageLicenseExpression`
- README 按 2.6.0 PEAK 重写，恢复 Harmony / Newtonsoft 依赖下载链接并修正库来源链接格式
- 版本号 2.6.1（`InformationalVersion` `2.6.1 Mores`）；示例插件 `AdaptedPluginSample` 1.1.0

## 2.6.0 PEAK

SLDataAPI **2.6.0 PEAK**。自 `preview/v2.6.0-DevOnly` 合入 `main`；产品版本是 2.6.0。

### 相对 2.5.5 Nexus

- 控制面 / 语音 / 控制 WS 走 **API Key**（`apikey.config`，仅指纹）；`control_token` **已废弃**（启动警告后忽略）
- 控制路径与 RemoteAdmin 对齐；旧路径别名已移除
- `/control/broadcast`（全服屏幕广播）与 `/control/staffchat`（RA 管理聊天）已实现；admin 默认开放，duty 默认拒绝
- 适配插件发现：`GET /plugins/adapted` + `get_sl_data.adapted_plugins`
- 语音定稿 zip 可选 WebDAV 自动上传（默认关，**仅 https://**）
- 更新检查：启动 + 默认 72h 静默复查同一 GitHub Releases 通道
- 安全加固（含 PR #10）：`verify_token` fail-closed、远程 `sldataapi`/`slda` 硬拒绝、API Key 一次性文件交付、无确认窗口

### 鉴权要点

- 数据口：`verify_token`；优先 `Authorization: Bearer` / `X-SLDataAPI-Token`。`?token=` 仍兼容但已弃用
- 控制面 / 语音：API Key 头；不再接受 `X-Control-Token`、控制面 `?token=` / `?key=`
- 出厂默认 `your_secret_token` **不能当有效口令**（fail-closed 关闭数据口）
