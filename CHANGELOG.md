# Changelog

## Unreleased（内测 / beta，默认全关）

> 仅供内测：两个开关默认 `false`，关闭时新端点一律 404、行为与 2.6.0 PEAK 一致。

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

- FileRoot 沙箱拒绝路径中的符号链接 / junction（reparse point），防止经链接逃逸出 `file_root`

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
