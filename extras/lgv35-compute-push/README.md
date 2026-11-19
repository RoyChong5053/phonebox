# Compute-units collector for Termux

Pushes CPU, memory, battery and temperature of several machines into a PhoneBox agent, so the panel's
**Compute units** widget can show them. The collection runs on an Android phone in Termux — the computer that
runs the agent is not polled and does no extra work beyond storing the last JSON payload.

```
phone (Termux)  --ssh-->  Linux / Android nodes
      |
      |  POST /api/push/compute?ttl=30   (USB: 127.0.0.1:8765 via adb reverse,
      |                                   Wi-Fi: https://<agent>:8766 + webhook token)
      v
PhoneBox agent (PC)  --/api/push-->  <compute> widget
```

Only the Python standard library is used; nothing has to be installed with pip.

## Requirements

- Termux with `openssh` (`pkg install openssh`), `python` and `ping`.
- `termux-api` for `termux-battery-status` on Android nodes (the phone that collects and any Android node).
- SSH access from the collecting phone to the nodes (a key in each node's `authorized_keys`).
- The PhoneBox agent running on the computer, reachable over USB (`adb reverse`) or its optional Wi-Fi mode.

## Setup

```sh
mkdir -p ~/phonebox && cd ~/phonebox
# copy compute_push.py, nodes.json, run.sh, start.sh, stop.sh here
chmod +x run.sh start.sh stop.sh

# one key for the collector, then add id_compute.pub to every node
ssh-keygen -t ed25519 -N '' -f ~/.ssh/id_compute -C phonebox-compute
cat ~/.ssh/id_compute.pub
```

Copy `nodes.example.json` to `nodes.json` and edit the hosts:

| field | meaning |
|---|---|
| `id`, `name` | shown in the widget (`name` is optional) |
| `host`, `user`, `port` | SSH target |
| `kind` | `linux` (CPU from `/proc/stat`) or `android` (memory/battery/temperature only) |
| `local: true` | probe the phone itself, no SSH |
| `key` | private key (default `~/.ssh/id_compute`) |
| `ping` | address to ping for latency (defaults to `host`) |
| `enabled: false` | keep the entry but do not collect it |

Top-level settings:

- `interval` seconds between rounds (default 5).
- `ttl` seconds the agent keeps the value; after that the widget shows "no data" (default 30).
- `endpoints` tried in order. `http://127.0.0.1:8765` works over USB (`adb reverse`). To push while the phone is
  on a charger, enable the agent's Wi-Fi mode and add `https://<agent-ip>:8766`.
- `token_file` with the agent's **webhook token**, used only for non-loopback endpoints. Keep it out of git:
  `mkdir -p ~/.config/phonebox-compute && printf %s '<token>' > ~/.config/phonebox-compute/token && chmod 600 ...`

The webhook token is in the agent's settings (`Settings › General › Webhooks`) or in `config.json` as
`lan.webhookToken`. The laptop HTTPS certificate is self-signed; the collector skips verification because only
the token authenticates the request.

## Run

```sh
python3 compute_push.py --once --dry-run   # print the payload
python3 compute_push.py --once             # collect and push once
./start.sh                                 # background + wake lock, log in logs/compute.log
./stop.sh
tail -f logs/compute.log
```

`run.sh` is the supervisor (`start.sh` starts it with `nohup`): it keeps a Termux wake lock so Android does not
freeze the collector, restarts it after a crash, and releases the lock on exit. Add Termux to the battery
optimisation whitelist as well (`adb shell dumpsys deviceidle whitelist +com.termux`).

### Autostart

- With **Termux:Boot** installed, put a launcher in `~/.termux/boot/`:

  ```sh
  #!/data/data/com.termux/files/usr/bin/sh
  termux-wake-lock
  cd ~/phonebox && ./start.sh
  ```

- With **termux-services**: create `~/.termux/service/phonebox-compute/run` that `exec`s `./run.sh`, then
  `sv-enable phonebox-compute`.
- Without either, starting Termux and running `~/phonebox/start.sh` is enough; the collector then keeps running
  while the device is awake.

## Payload

```json
{
  "v": 1,
  "ts": 1700000000,
  "nodes": [
    { "id": "loq", "name": "Lenovo LOQ", "kind": "linux", "online": true, "ping": 3,
      "cpu": 42.1, "cores": 16, "load": 1.2, "uptime": 12345,
      "ram_used": 8234, "ram_total": 16384,
      "bat": 87, "bat_state": "Discharging", "ac": false, "temp": 71,
      "gpu": 58, "gpu_temp": 77, "gpu_mem_used": 1200, "gpu_mem_total": 8188 },
    { "id": "gt20", "name": "GT20", "kind": "android", "online": true, "ping": 4,
      "cpu": null, "cores": 8, "ram_used": 4100, "ram_total": 11442,
      "bat": 100, "bat_state": "Charging", "ac": true, "temp": null }
  ]
}
```

Memory is in MB. A value the node cannot report is `null`, never `0`.

## Limits

- **Android CPU is not available.** Android 12+ hides `/proc/stat` from apps and toybox `top` reports a constant
  `800%idle`, so `cpu` stays `null` for `kind: "android"` (the widget shows `—`).
- Thermal sensors differ per device; a node without readable `thermal_zone`/`hwmon` reports `temp: null`.
- `nvidia-smi` is used when present; AMD/Intel GPUs are not polled.
- When neither endpoint is reachable (PC off, or phone on a charger without Wi-Fi mode), the collector keeps
  running and retries every interval; the agent's TTL removes the stale value.
