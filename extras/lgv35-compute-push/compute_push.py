#!/usr/bin/env python3
"""PhoneBox compute-units collector for Termux.

Runs on an Android phone (Termux) and pushes CPU, memory, battery and temperature of
other machines into a PhoneBox agent:

    POST /api/push/compute?ttl=30
    {"v":1,"ts":1700000000,"nodes":[{"id":"loq","name":"Lenovo LOQ","online":true,...}]}

The agent keeps the last payload; the panel's <compute> widget reads it. Nothing is
polled on the computer itself, and no SSH server is needed on the phone.

Linux nodes are sampled over SSH with a single command per node: two /proc/stat reads
0.7 s apart for the CPU percentage, then memory, battery, thermal/hwmon sensors and
nvidia-smi. Android/Termux nodes report memory, battery and temperature only: Android
blocks /proc/stat for apps, and toybox top reports a constant "800%idle", so CPU is
left null instead of inventing a number.

Usage:
    python3 compute_push.py --once --dry-run     # print the payload, push nothing
    python3 compute_push.py --once               # collect and push once
    python3 compute_push.py --interval 5         # loop (default from nodes.json)

Only the Python standard library is used; it works in Termux without pip.
"""

import argparse
import concurrent.futures
import json
import os
import re
import shutil
import signal
import ssl
import subprocess
import sys
import time
import urllib.error
import urllib.parse
import urllib.request
from datetime import datetime

HERE = os.path.dirname(os.path.abspath(__file__))
STOP = False


def log(msg):
    print(f"{datetime.now():%Y-%m-%d %H:%M:%S} {msg}", flush=True)


def run(cmd, timeout=6, stdin=None):
    """Run a shell command locally, returning stdout+stderr (never raises)."""
    try:
        p = subprocess.run(cmd, shell=isinstance(cmd, str), input=stdin, capture_output=True,
                           text=True, timeout=timeout)
        return (p.stdout or "") + (p.stderr or "")
    except Exception as ex:
        return f"__ERROR__ {ex}"


# ------------------------------------------------------------------ sensor snippets
PWR = (
    '[ -e /sys/class/power_supply/AC/online ] && echo "AC:$(cat /sys/class/power_supply/AC/online 2>/dev/null)"; '
    '[ -e /sys/class/power_supply/ACAD/online ] && echo "AC:$(cat /sys/class/power_supply/ACAD/online 2>/dev/null)"; '
    '[ -e /sys/class/power_supply/ADP0/online ] && echo "AC:$(cat /sys/class/power_supply/ADP0/online 2>/dev/null)"; '
    '[ -e /sys/class/power_supply/ADP1/online ] && echo "AC:$(cat /sys/class/power_supply/ADP1/online 2>/dev/null)"; '
    '[ -e /sys/class/power_supply/BAT0/capacity ] && echo "BAT:$(cat /sys/class/power_supply/BAT0/capacity 2>/dev/null):$(cat /sys/class/power_supply/BAT0/status 2>/dev/null)"; '
    '[ -e /sys/class/power_supply/BAT1/capacity ] && echo "BAT:$(cat /sys/class/power_supply/BAT1/capacity 2>/dev/null):$(cat /sys/class/power_supply/BAT1/status 2>/dev/null)"; '
    'echo "USB:$(cat /sys/class/power_supply/usb/online 2>/dev/null)"'
)

TEMP = (
    'i=0; while [ $i -le 15 ]; do z=/sys/class/thermal/thermal_zone$i; '
    '[ -e $z/temp ] && echo "TZ:$(cat $z/type 2>/dev/null):$(cat $z/temp 2>/dev/null)"; i=$((i+1)); done; '
    'for f in /sys/class/hwmon/hwmon*/temp*_input; do [ -e "$f" ] && '
    'echo "HWN:$(cat $(dirname $f)/name 2>/dev/null):$(cat $f 2>/dev/null)"; done'
)

GPU = (
    'command -v nvidia-smi >/dev/null 2>&1 && '
    'nvidia-smi --query-gpu=utilization.gpu,temperature.gpu,memory.used,memory.total '
    '--format=csv,noheader,nounits 2>/dev/null | head -1 || echo NOGPU'
)

PWR_ANDROID = (
    'echo "USB:$(cat /sys/class/power_supply/usb/online 2>/dev/null)"; '
    'for d in battery BAT0; do [ -e /sys/class/power_supply/$d/capacity ] && '
    'echo "BTS:$(cat /sys/class/power_supply/$d/capacity 2>/dev/null):$(cat /sys/class/power_supply/$d/status 2>/dev/null)"; done; '
    'command -v termux-battery-status >/dev/null 2>&1 && termux-battery-status 2>/dev/null'
)

LINUX_CMD = (
    "cat /proc/stat | head -1; sleep 0.7; cat /proc/stat | head -1; "
    "echo ---MEM; head -3 /proc/meminfo; "
    "echo ---SYS; nproc; cat /proc/loadavg; cat /proc/uptime; "
    "echo ---PWR; " + PWR + "; "
    "echo ---TEMP; " + TEMP + "; "
    "echo ---GPU; " + GPU
)

ANDROID_CMD = (
    "top -n 1 -b 2>/dev/null | head -5; "
    "echo ---MEM; head -3 /proc/meminfo; "
    "echo ---SYS; nproc; "
    "echo ---PWR; " + PWR_ANDROID + "; "
    "echo ---TEMP; " + TEMP
)

TEMP_PREFS = ("x86_pkg", "k10temp", "coretemp", "cpu", "core", "pkg", "acpi", "soc", "tsens", "therm")


# ------------------------------------------------------------------ parsers
def split_sections(out):
    marks = {"MEM", "SYS", "PWR", "TEMP", "GPU"}
    parts, cur, buf = {}, "HEAD", []
    for line in out.splitlines():
        tag = line.strip()
        if tag.startswith("---") and tag[3:] in marks:
            parts[cur] = "\n".join(buf)
            cur, buf = tag[3:], []
        else:
            buf.append(line)
    parts[cur] = "\n".join(buf)
    return parts


def cpu_pair(head):
    rows = [ln.split() for ln in head.splitlines() if ln.startswith("cpu ")]
    if len(rows) < 2:
        return None
    try:
        a = [int(x) for x in rows[0][1:9]]
        b = [int(x) for x in rows[1][1:9]]
        idle = (b[3] + b[4]) - (a[3] + a[4])
        total = sum(b) - sum(a)
        if total <= 0:
            return None
        return max(0.0, min(100.0, 100.0 - idle * 100.0 / total))
    except Exception:
        return None


def meminfo(text):
    total = avail = None
    for line in text.splitlines():
        if line.startswith("MemTotal:"):
            m = re.search(r"\d+", line)
            if m:
                total = int(m.group())
        elif line.startswith("MemAvailable:"):
            m = re.search(r"\d+", line)
            if m:
                avail = int(m.group())
    if not total:
        return None, None
    used = total - (avail if avail is not None else 0)
    return round(used / 1024), round(total / 1024)


def system_info(text):
    lines = [ln.strip() for ln in text.splitlines() if ln.strip()]
    cores = load = uptime = None
    for ln in lines:
        if cores is None and re.fullmatch(r"\d+", ln):
            cores = int(ln)
        elif load is None and re.fullmatch(r"[\d.]+ [\d.]+ [\d.]+ \S+ \S+", ln):
            load = float(ln.split()[0])
        elif uptime is None and re.fullmatch(r"[\d.]+ [\d.]+", ln):
            uptime = int(float(ln.split()[0]))
    return cores, load, uptime


def parse_battery(text):
    pct = state = None
    ac = False
    for raw in text.splitlines():
        line = raw.strip()
        if line.startswith(("AC:", "USB:")):
            val = line.split(":", 1)[1].strip()
            if val == "1":
                ac = True
        elif line.startswith(("BAT:", "BTS:")):
            bits = line.split(":")
            if len(bits) >= 2:
                m = re.search(r"\d+", bits[1])
                if m and pct is None and 0 <= int(m.group()) <= 100:
                    pct = int(m.group())
            if len(bits) >= 3 and bits[2].strip() in ("Charging", "Full"):
                ac = True
                state = bits[2].strip()
            elif len(bits) >= 3 and bits[2].strip():
                state = bits[2].strip()
    m = re.search(r'"percentage":\s*(\d+)', text)
    if m:
        pct = int(m.group(1))
    m = re.search(r'"plugged":\s*"([A-Z_]+)"', text)
    if m:
        if m.group(1) != "UNPLUGGED":
            ac = True
            state = "Charging"
        else:
            state = "Discharging"
    return pct, (state or ("AC" if ac else None)), ac


def temp_c(raw):
    try:
        v = float(raw)
    except Exception:
        return None
    if v > 1000:
        v /= 1000.0
    elif v > 150:
        v /= 10.0
    return v if 0 < v < 125 else None


def parse_temp(text):
    zones = []
    for raw in text.splitlines():
        line = raw.strip()
        if line.startswith("TZ:"):
            bits = line.split(":", 2)
            if len(bits) == 3:
                v = temp_c(bits[2])
                if v is not None:
                    zones.append((bits[1].lower(), v))
        elif line.startswith("HWN:"):
            bits = line.split(":", 2)
            if len(bits) == 3:
                v = temp_c(bits[2])
                if v is not None:
                    zones.append((bits[1].lower(), v))
    for pref in TEMP_PREFS:
        for kind, v in zones:
            if pref in kind:
                return round(v)
    return round(zones[0][1]) if zones else None


def parse_gpu(text):
    line = (text.strip().splitlines() or [""])[0]
    if not line or "NOGPU" in line or "Not Supported" in line or "N/A" in line:
        return None
    nums = []
    for cell in [c.strip() for c in line.split(",")][:4]:
        m = re.search(r"[\d.]+", cell)
        nums.append(float(m.group()) if m else None)
    if len(nums) < 4 or nums[0] is None:
        return None
    return {"gpu": round(nums[0]), "gpu_temp": round(nums[1]) if nums[1] is not None else None,
            "gpu_mem_used": round(nums[2]) if nums[2] is not None else None,
            "gpu_mem_total": round(nums[3]) if nums[3] is not None else None}


def ping_ms(host):
    if not host:
        return None
    out = run(["ping", "-c", "1", "-W", "1", "-q", host], timeout=3)
    m = re.search(r"time[=<]([\d.]+)\s*ms", out) or re.search(r"rtt min/avg/max/mdev = [\d.]+/([\d.]+)/", out)
    return round(float(m.group(1))) if m else None


# ------------------------------------------------------------------ probes
def ssh(node, remote, timeout=7):
    key = os.path.expanduser(node.get("key") or "~/.ssh/id_compute")
    cmd = [
        "ssh", "-T", "-o", "BatchMode=yes", "-o", "ConnectTimeout=2",
        "-o", "StrictHostKeyChecking=accept-new", "-o", "LogLevel=ERROR",
        "-o", "ControlMaster=auto", "-o", f"ControlPath={HERE}/.ssh-%r@%h:%p",
        "-o", "ControlPersist=120", "-p", str(node.get("port", 22)),
    ]
    if os.path.exists(key):
        cmd += ["-i", key]
    cmd.append(f"{node.get('user', 'root')}@{node['host']}")
    cmd.append(remote)
    try:
        p = subprocess.run(cmd, capture_output=True, text=True, timeout=timeout)
        return (p.stdout or "") + (p.stderr or "")
    except Exception:
        return None


def probe_linux(node):
    out = ssh(node, LINUX_CMD)
    if not out:
        return {"online": False}
    sec = split_sections(out)
    cpu = cpu_pair(sec.get("HEAD", ""))
    used, total = meminfo(sec.get("MEM", ""))
    cores, load, uptime = system_info(sec.get("SYS", ""))
    bat, state, ac = parse_battery(sec.get("PWR", ""))
    res = {
        "online": cpu is not None or total is not None,
        "cpu": None if cpu is None else round(cpu, 1),
        "cores": cores, "load": load, "uptime": uptime,
        "ram_used": used, "ram_total": total,
        "bat": bat, "bat_state": state, "ac": ac,
        "temp": parse_temp(sec.get("TEMP", "")),
    }
    gpu = parse_gpu(sec.get("GPU", ""))
    if gpu:
        res.update(gpu)
    return res


def probe_android(node):
    out = ssh(node, ANDROID_CMD)
    if not out:
        return {"online": False}
    sec = split_sections(out)
    used, total = meminfo(sec.get("MEM", ""))
    cores, _, _ = system_info(sec.get("SYS", ""))
    bat, state, ac = parse_battery(sec.get("PWR", ""))
    return {
        "online": total is not None,
        # Android hides /proc/stat from apps and toybox top always reports 800%idle
        "cpu": None, "cores": cores, "load": None, "uptime": None,
        "ram_used": used, "ram_total": total,
        "bat": bat, "bat_state": state, "ac": ac,
        "temp": parse_temp(sec.get("TEMP", "")),
    }


def probe_local(node):
    used, total = meminfo(open("/proc/meminfo", encoding="utf-8", errors="ignore").read())
    bat, state, ac = parse_battery(run("termux-battery-status", timeout=8) if shutil.which("termux-battery-status") else "")
    zones = []
    for base in ("/sys/class/thermal",):
        try:
            for name in os.listdir(base):
                if not name.startswith("thermal_zone"):
                    continue
                d = os.path.join(base, name)
                try:
                    t = open(os.path.join(d, "temp"), encoding="utf-8").read().strip()
                    kind = open(os.path.join(d, "type"), encoding="utf-8").read().strip()
                except OSError:
                    continue
                zones.append(f"TZ:{kind}:{t}")
        except OSError:
            pass
    return {
        "online": True, "cpu": None, "cores": os.cpu_count(),
        "load": None, "uptime": None,
        "ram_used": used, "ram_total": total,
        "bat": bat, "bat_state": state, "ac": ac,
        "temp": parse_temp("\n".join(zones)),
    }


def probe(node):
    kind = node.get("kind", "linux")
    try:
        res = probe_local(node) if node.get("local") else (probe_android(node) if kind == "android" else probe_linux(node))
    except Exception as ex:
        res = {"online": False, "error": str(ex)}
    if node.get("local"):
        res["ping"] = None
    else:
        res["ping"] = ping_ms(node.get("ping") or node.get("host"))
    res["id"] = node.get("id") or node.get("name")
    res["name"] = node.get("name") or node.get("id")
    res["kind"] = kind
    res["local"] = bool(node.get("local"))
    return res


def collect(nodes):
    with concurrent.futures.ThreadPoolExecutor(max_workers=max(1, len(nodes))) as ex:
        return list(ex.map(probe, nodes))


# ------------------------------------------------------------------ push
def read_token(cfg):
    path = os.path.expanduser(cfg.get("token_file") or "~/.config/phonebox-compute/token")
    try:
        return open(path, encoding="utf-8").read().strip()
    except OSError:
        return None


def post(url, body, token, timeout=3):
    headers = {"Content-Type": "application/json", "User-Agent": "phonebox-compute/1"}
    host = urllib.parse.urlparse(url).hostname or ""
    if token and host not in ("127.0.0.1", "localhost", "::1"):
        headers["Authorization"] = "Bearer " + token
    ctx = ssl._create_unverified_context() if url.startswith("https:") else None
    req = urllib.request.Request(url, data=body, headers=headers, method="POST")
    with urllib.request.urlopen(req, timeout=timeout, context=ctx) as r:
        return r.status


def push(payload, cfg, state):
    body = json.dumps(payload, separators=(",", ":")).encode()
    endpoints = cfg.get("endpoints") or ["http://127.0.0.1:8765"]
    ttl = int(cfg.get("ttl", 30))
    token = read_token(cfg)
    order = endpoints[state.get("good", 0):] + endpoints[:state.get("good", 0)]
    last = None
    for base in order:
        url = base.rstrip("/") + f"/api/push/compute?ttl={ttl}"
        try:
            code = post(url, body, token)
            state["good"] = endpoints.index(base)
            return True, f"{base} ({code})"
        except Exception as ex:
            last = f"{base}: {ex}"
    return False, last


# ------------------------------------------------------------------ main
def main():
    ap = argparse.ArgumentParser(description="Push compute-node stats into a PhoneBox agent.")
    ap.add_argument("--config", default=os.path.join(HERE, "nodes.json"))
    ap.add_argument("--interval", type=float, default=None, help="seconds between rounds (default: nodes.json)")
    ap.add_argument("--once", action="store_true", help="one round, then exit")
    ap.add_argument("--dry-run", action="store_true", help="print the payload instead of pushing it")
    args = ap.parse_args()

    with open(args.config, encoding="utf-8") as f:
        cfg = json.load(f)
    nodes = [n for n in cfg.get("nodes", []) if n.get("enabled", True)]
    interval = args.interval if args.interval is not None else float(cfg.get("interval", 5))
    state = {"good": 0, "fail": 0}

    def stop(*_):
        global STOP
        STOP = True
    signal.signal(signal.SIGTERM, stop)
    signal.signal(signal.SIGINT, stop)

    while not STOP:
        t0 = time.time()
        nodes_out = collect(nodes)
        payload = {"v": 1, "ts": int(time.time()), "nodes": nodes_out}
        online = sum(1 for n in nodes_out if n.get("online"))
        if args.dry_run:
            print(json.dumps(payload, indent=2, ensure_ascii=False))
        else:
            ok, detail = push(payload, cfg, state)
            if ok:
                state["fail"] = 0
                log(f"{online}/{len(nodes_out)} online -> {detail}")
            else:
                state["fail"] += 1
                # one line a minute is enough while the phone is on a charger or the PC is off
                if state["fail"] == 1 or state["fail"] % 12 == 0:
                    log(f"{online}/{len(nodes_out)} online, push failed: {detail}")
        if args.once:
            return 0 if any(n.get("online") for n in nodes_out) else 1
        time.sleep(max(0.2, interval - (time.time() - t0)))
    return 0


if __name__ == "__main__":
    sys.exit(main())
