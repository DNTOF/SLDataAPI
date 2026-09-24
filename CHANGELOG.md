# Changelog

## 2.6.0 PEAK

SLDataAPI **2.6.0 PEAK**。Git 分支仍叫 `preview/v2.6.0-DevOnly`（历史名称）；产品版本是 2.6.0。

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
