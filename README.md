# SLDataAPI

**版本：** 2.0.0  
**依赖：** EXILED 9.x · MEC · Newtonsoft.Json  
**用途：** 在 SCP:SL 游戏服务器上暴露一个轻量 HTTP 接口，供 AstrBot 等外部程序主动轮询实时服务器数据。

---

## 安装
1. 运行:
```
dotnet build -c Release
```
2. 将 `SLDataAPI.dll` 放入 `EXILED/Plugins/` 目录
3. 启动服务器，EXILED 会自动生成配置文件
4. 按需修改配置（见下方），重启服务器生效

---

## 配置

配置文件路径：`EXILED/Configs/Plugins/s_l_data_a_p_i/7777.yml`

```yaml
s_l_data_a_p_i:
  is_enabled: true
  debug: false
  verify_token: "your_secret_token"   # 鉴权 token，请改成复杂字符串
  http_port: 8081                     # HTTP 监听端口
  push_interval_seconds: 8            # 后台数据刷新间隔（秒）
```

> ⚠️ 请确保服务器防火墙放行对应端口（默认 8081/TCP）。

---

## HTTP 接口

### `GET /get_sl_data`

| 参数 | 类型 | 说明 |
|------|------|------|
| `token` | string | 必填，与配置中 `verify_token` 一致 |

**示例请求：**
```
http://游戏服务器公网IP:8081/get_sl_data?token=your_secret_token
```

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
    { "nickname": "PlayerOne", "role": "D级人员", "team": "D级" },
    { "nickname": "TestUser",  "role": "SCP-173",  "team": "SCP"  }
  ]
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
| `players` | 仅包含已分配职业的真实玩家，排除 NPC/Dummy |

**错误响应：**

| HTTP 状态码 | 含义 |
|------------|------|
| `403` | token 错误或缺失 |
| `404` | 路径不是 `/get_sl_data` |

---

## 数据刷新机制

- 插件启用时**立即**采集一次数据（无需等待第一个刷新周期）
- 之后每隔 `push_interval_seconds` 秒刷新一次缓存（默认 8 秒）
- 回合开始 / 结束 / 等待玩家阶段切换时**立即触发**一次额外刷新
- 核弹倒计时在**每次 HTTP 请求时实时读取**，不受刷新间隔影响，始终与游戏内同步

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
├── Plugin.cs          # 插件入口，事件注册，HTTP 服务器生命周期管理
├── DataCollector.cs   # 数据采集、缓存、定时刷新逻辑
├── HttpServer.cs      # HttpListener 封装，token 鉴权，响应序列化
├── Models.cs          # ServerData / PlayerInfo 数据模型
└── Config.cs          # EXILED 配置类
```
