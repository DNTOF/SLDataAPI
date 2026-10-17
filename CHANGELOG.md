# Changelog

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
