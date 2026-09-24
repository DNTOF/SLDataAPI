# 安全策略

SLDataAPI 提供服务器数据查询和远程控制能力（含执行控制台命令、踢人/封禁、文件读写、语音转发等），这些接口一旦被未授权访问，影响面等同于拿到服务器控制台权限，请认真对待。

本分支当前产品版本为 **2.6.0 PEAK**（Git 分支名 `preview/v2.6.0-DevOnly` 为历史名称）。

## 支持的版本

只有最新发布版本会收到安全修复，旧版本不回溯打补丁，请保持更新到最新 release。

| 版本 | 是否维护 |
| --- | --- |
| 2.6.0 PEAK | ✅ |
| 更早版本 | ❌ |

## 如何报告漏洞

**请不要通过公开 Issue 或 Discussions 报告安全漏洞。** 在漏洞修复并发布之前，公开细节等于公开一份利用指南。

请通过 **[GitHub Security Advisory](../../security/advisories/new)** 私下提交（GitHub 仓库页面 Security 标签下的 "Report a vulnerability"），只有仓库维护者能看到。

提交时请尽量包含：

- 漏洞类型（鉴权绕过 / 越权 / 路径穿越 / 信息泄露 / 其他）
- 复现步骤，最好附带请求示例（`curl` 命令、请求体等）
- 影响范围：涉及哪个端点、需要什么前置条件（比如是否需要已知 token）
- 你认为的严重程度和理由

## 响应时间

这是个人业余维护的开源项目，不是商业产品，没有 SLA。会尽量在 **7 天内**回复确认收到，视问题严重程度评估修复优先级；高危漏洞（无需 token 即可越权/执行命令类）会优先处理。修复后会在 Release Notes 中致谢报告者（除非你要求匿名），并视情况申请 CVE。

## 范围说明

**在范围内（欢迎报告）：**

- 无需有效 `verify_token` / `control_token` 即可读取数据或执行控制操作
- 持有 `verify_token` 却能执行本应需要 `control_token` 才能做的操作（权限越级）
- `/control/files/*` 的路径穿越、突破 `FileRoot` 限制、读写受保护目录
- 通过精心构造的输入造成插件崩溃、拒绝服务，或使服务器控制台权限被间接获取
- `UpdateChecker` 自动更新流程中的签名校验绕过（伪造/篡改下载的 DLL 被接受）
- Token 比较存在时序侧信道、日志或错误信息中意外泄露 token 明文
- WebSocket 控制通道（`/ws/control`）、语音转发通道鉴权与 HTTP 端不一致

**不在范围内：**

- 已知需要控制面凭据（2.6 起为 API Key，早期为 `control_token`）才能触发的行为——持有凭据本身就等同于拥有服务器控制台权限，这是设计如此，不是漏洞（例如 `/control/console/command` 能执行任意命令）。**例外**：借远程控制通道增发/吊销 API Key 属越权提升，在范围内——`sldataapi` / `slda` 管理 CLI 已被远程执行路径硬拒绝；任何绕过该硬拒绝的路径都请报告。
- 纯粹的资源消耗类拒绝服务（比如无限制发包把带宽打满），除非能绕过已有的连接数/请求体大小限制
- 依赖社会工程学（骗管理员泄露 token）的攻击路径
- 对已经过期不再维护的旧版本的报告

## 部署建议（写给使用者）

以下不算"漏洞"，但是实际部署中最容易出问题的地方，强烈建议照做：

- **不要用默认 / 弱 `verify_token`（fail-closed）**：出厂默认值是 `your_secret_token`（**不能当有效口令用**）。空、仅空白、出厂默认、或未同时包含大写/小写/数字/特殊符号（长度≥8）时，**Enable 会打 Error 并关闭数据口**（`/get_sl_data`、`/plugins/adapted` 等拒绝服务）；若控制面也未启用则**不绑定 HTTP 端口**。已设置的强随机口令不受影响。数据口优先使用 `Authorization: Bearer` 或 `X-SLDataAPI-Token` / `X-SLDataAPI-Verify-Token`；`?token=` 在 2.6.0 PEAK **仍兼容但已弃用**（会入访问日志），后续版本将移除。
- **API Key 一次性文件**：`sldataapi apikey create` 不会把明文写进控制台 response / LocalAdmin 命令历史；明文只出现在配置目录的 `apikey_once_<id>.txt`（同 id 再次创建会覆盖）。创建 **5 分钟后自动删除**该路径（已不在则跳过）；也可自行提前删除。日志不记录明文。写入后会尽力把文件权限收成仅当前用户（Linux `0600` / Windows ACL）；**收紧失败不阻断创建**。Windows 剪贴板复制默认关闭（`apikey_copy_to_clipboard: true` 才开启）。
- **密钥管理只允许本地控制台**：`TryCreate` / `TryRevoke` / `list` 在 `IsRemoteExecution` 上下文一律拒绝。`sldataapi` / `slda` 仍被远程 `/control/console` 字符串硬拒绝。若用权限插件收窄 RemoteAdmin，请同时拒绝 `sldataapi` / `slda`。
- **duty 看不到完整审计**：值班模板默认不授予 `/control/audit/list`。即便 override 打开，非 admin 也只能看到自己这条的请求体，其他 Key 的写操作 payload 会显示 `[redacted]`。落盘时会打码 `sld_live_` / `sld_duty_` / Bearer / 常见 JSON 密钥字段。
- **`control_enabled` 默认关闭，非必要不要开**：只有真的需要外部程序控制服务器时才开启。`control_token` 已废弃，控制面走 API Key。
- **把端口锁在受信网络内**：SLDataAPI 自身没有 TLS，裸 HTTP 暴露在公网上会被中间人窃听 token。建议只监听内网/本机，对外通过反向代理（Nginx/Caddy）加 HTTPS，并做 IP 白名单。
- **`FileRoot` 尽量不要设置成比必要范围更大的目录**，权限最小化。
- **语音转发/录音相关配置**（`voice_enabled` / `voice_record_enabled`）涉及玩家隐私，启用前请确认服务器规则中已告知玩家，并妥善控制录音文件的访问权限。未完成握手的连接占用独立 pending 池（超时仍适用），**不占已鉴权 `MaxClients` 席位**。语音口鉴权是 **API Key**，不是已废弃的 `control_token`。
- **WebDAV 自动上传默认关闭**（`webdav_upload_enabled`）。启用后每局定稿 zip 会以 HTTPS PUT + Basic Auth 发往 `webdav_url`。**只接受 `https://`**，`http://` 会校验失败并跳过上传（不改变插件自身的 HTTP 数据/控制端口）。密码与 `Authorization` 头不会写入日志；视密码为与 API Key 同级的机密。配置无效时启动只 Warn 一次后跳过，不改变录音本身。
- **`AutoUpdateInstall` 只对已强签名的构建生效**：当前 DLL 公钥令牌为空（本地未签名编译）时**拒绝自动安装**（即使 `auto_update_install: true`），只打日志。已签名构建仍要求下载文件的公钥令牌与当前一致。正式发布令牌为 `3ec73bb20070fa9c`。签名密钥（`key.snk`）按设计不入库、由发布者本地保管：发布正式 Release 前请用 `dotnet build -c Release` 本地构建（存在 `key.snk` 时自动启用强签名），并核对产物公钥令牌后再上传附件。`auto_update_check` 为 true 时，除启动外还会按 `auto_update_check_interval_hours`（默认 72）静默复查同一 GitHub Releases 通道；安装/提示规则与启动检查相同。上次检查时刻写在配置目录 `update_check_state.json`，频繁重启不会每次都打 API。无更新只打 Debug；有新版本或检查失败才 Warn。`auto_update_check: false` 则启动与周期都不跑。

## 致谢

感谢所有负责任地报告过漏洞的人。
