# LG-V35 计算节点采集器（Termux）

把几台机器的 CPU、内存、电池、温度推到 PhoneBox agent，供面板上的 **Compute units** widget 显示。
采集完全跑在这台 LG-V35 的 Termux 里：主力电脑只负责接收并保存最后一份 JSON，不轮询、不跑脚本。

```
LG-V35 (Termux)  --ssh-->  LOQ / M6400 / D3180 / GT20 / 本机
      |
      |  POST /api/push/compute?ttl=30
      |    USB：http://127.0.0.1:8765（adb reverse，无需 token）
      |    充电器：https://<电脑IP>:8766（需开 agent Wi-Fi 模式 + webhook token）
      v
PhoneBox agent（电脑） --/api/push--> <compute> widget
```

## 目录内容

| 文件 | 作用 |
|---|---|
| `compute_push.py` | 采集 + 上报（纯标准库，无需 pip） |
| `nodes.json` | 节点列表和上报设置（含内网 IP，不要提交到公开仓库） |
| `run.sh` | 常驻守护：wake-lock、崩溃后重启 |
| `start.sh` / `stop.sh` | 后台启动 / 停止 |
| `logs/compute.log` | 运行日志 |

## 依赖

- Termux 里已装：`openssh`、`python`、`ping`
- Android 节点（含本机）要能执行 `termux-battery-status`（Termux:API）
- 本机到各节点的 SSH 免密：`~/.ssh/id_compute` 已加到各节点 `authorized_keys`

## 首次部署

```sh
mkdir -p ~/phonebox && cd ~/phonebox
chmod +x run.sh start.sh stop.sh

# 专用密钥（只用于采集，不要复用 gt20 的私钥）
ssh-keygen -t ed25519 -N '' -f ~/.ssh/id_compute -C phonebox-compute
cat ~/.ssh/id_compute.pub     # 追加到各节点的 ~/.ssh/authorized_keys

cp nodes.example.json nodes.json   # 按实际 IP 修改
python3 compute_push.py --once --dry-run   # 只看 payload，不上报
python3 compute_push.py --once             # 真上报
curl -s http://127.0.0.1:8765/api/push/compute | head -c 500
```

## 启动 / 停止

```sh
./start.sh          # 后台运行（内部 nohup + run.sh），日志 logs/compute.log
./stop.sh
tail -f logs/compute.log
```

`run.sh` 会持有 `termux-wake-lock`，避免息屏后 Termux 被冻结；退出时释放。
另外建议把 Termux 加入电池优化白名单：

```sh
adb shell dumpsys deviceidle whitelist +com.termux
```

### 开机自启（任选）

1. **Termux:Boot**：新建 `~/.termux/boot/00-phonebox`：

   ```sh
   #!/data/data/com.termux/files/usr/bin/sh
   termux-wake-lock
   cd ~/phonebox && ./start.sh
   ```

2. **termux-services**：`~/.termux/service/phonebox-compute/run` 里 `exec ~/phonebox/run.sh`，然后
   `sv-enable phonebox-compute`。
3. 都没有：打开 Termux 手动 `~/phonebox/start.sh`。只要设备醒着，采集就一直跑。

## 配置说明

`nodes.json`：

| 字段 | 说明 |
|---|---|
| `id` / `name` | widget 里显示的名字 |
| `host` / `user` / `port` | SSH 地址 |
| `kind` | `linux`（走 `/proc/stat` 算 CPU）或 `android`（只有内存/电池/温度） |
| `local: true` | 本机，不走 SSH |
| `enabled: false` | 保留配置但不采集 |
| `ping` | 延迟探测地址，默认用 `host` |
| `key` | 私钥路径，默认 `~/.ssh/id_compute` |

顶层字段：

- `interval`：轮询秒数，默认 5
- `ttl`：agent 保留秒数，默认 30；超时后 widget 显示无数据
- `endpoints`：按顺序尝试
  1. `http://127.0.0.1:8765` —— USB 连接电脑时可用（adb reverse）
  2. `https://192.168.100.200:8766` —— 手机只插充电器、电脑开着且启用了 Wi-Fi 模式时可用
- `token_file`：webhook token 文件（仅非 loopback 端点使用）。

```sh
mkdir -p ~/.config/phonebox-compute
printf %s '<agent 设置里的 webhook token>' > ~/.config/phonebox-compute/token
chmod 600 ~/.config/phonebox-compute/token
```

## 已知限制

- **Android 节点没有 CPU%**：Android 12+ 禁止应用读 `/proc/stat`，toybox `top` 恒报 `800%idle`，所以
  `cpu` 为 `null`，widget 显示 `—`。PC 节点不受影响。
- GT20 的温度传感器不可读，`temp` 为 `null`。
- GPU 只支持 `nvidia-smi`（LOQ 的 RTX 4060），AMD/Intel 不采集。
- 电脑关机或手机插充电器但没开 Wi-Fi 模式时，上报会失败并每 5s 重试；agent 的 TTL 会把旧值清掉，
  面板显示“无数据”，不会显示过期数字。
