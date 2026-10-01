# AdaptedPluginSample

Registers `dntof.sample_adapted` with SLDataAPI:

- capabilities: `sample.hello`
- status: `{ "ok": true, "message": "sample online", "bumps": 0 }`
- route `hello`: `{ "ok": true, "plugin": "dntof.sample_adapted" }`

Build after SLDataAPI Release build, then copy DLL to LabAPI `plugins/global`.

## Beta: write / action routes

Registered via `PluginEndpointRegistry.TryRegisterActions` (SLDataAPI with beta support; older
builds are detected via `MissingMethodException` and the sample stays read-only):

| action | body | result |
|---|---|---|
| `echo` | any JSON | `{ "echo": <body>, "actor": "<key id>", "transport": "http\|ws" }` |
| `bump` | `{ "by": 1..100 }` (optional) | `{ "bumps": <new total> }` |

Called through the control plane (HTTP `POST /control/adapted/dntof.sample_adapted/echo`, or WS
`call` with the same path). Requirements:

1. `beta_adapted_plugin_actions: true` in SLDataAPI `config.yml` (otherwise 404, and `actions` is
   not listed in `/plugins/adapted`).
2. The API key needs an explicit grant — `/control/adapted/` is **never** granted by `admin`
   duty or `all_control_true`. Use `endpoints_override`, e.g.
   `"/control/adapted/dntof.sample_adapted/": true` (one plugin) or `"/control/adapted/": true`.
3. Every call is a write: it is recorded in `control_log` (body redacted for secrets).

Handlers run on the Unity main thread with a 3 s timeout; throw `AdaptedActionException(message, status)`
to return a 4xx.
