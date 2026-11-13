#!/usr/bin/env python3
# Claude Code hook -> 大肥鱼：把“发生了什么”告诉桌宠，让她按 events.txt 演一小下。
# 全程不经过模型，不花 token；任何出错都静悄悄地退出（退出码 0、没有任何输出），绝不影响 Claude Code 干活。
#
# 事件对应（hook 事件 -> events.txt 里的事件名）：
#   Notification(权限请求/需要你回答)  -> 授权        Notification(闲置等你)  -> 等你
#   Stop 这一轮做完                    -> 完成（这一轮从你发消息算起超过 3 分钟 -> 长任务完成）
#   SubagentStop                       -> 子任务完成
#   PostToolUseFailure 工具出错         -> 出错（是编译、测试命令 -> 编译失败）
#   PostToolUse Bash 成功               -> 只有测试命令才发：测试通过（输出里看得出有失败的 -> 编译失败）
#   SessionStart(新开会话)              -> 开工        UserPromptSubmit 你发了消息  -> 收到
#   SessionStart(接着上次 / 清空重来)   -> 新会话      PostToolUseFailure 被你中断  -> 中断
#   思考超时：你发了消息 30 秒内，会话记录里没有新动静、最后一条也不是在等工具跑完，就喊一次（每轮最多一次）。
#     做法：发消息那一刻拉起一个后台小进程（--think）睡 30 秒再看一眼；这一轮已经做完或你又发了新消息，它就悄悄走。
# 定时任务（哄睡、午饭提醒）自己开的会话：发出的事件都带 auto，桌宠不把它当成你本人在（不记动静、不加亲密度）。
# 会话状态（这一轮几点开始、是不是定时任务开的）记在同目录 sessions.json，一天前的自动清掉。
# 桌面实况（本机加，2026-11-12）：让 Claude 也知道你在用什么软件、什么网页：
#   · 会话开始、你发消息时，如果「识别当前窗口并说话」的观察器没在跑（DSH 没开），就把它拉起来（node index.js --claude，只看窗口、不说话）；
#   · 你每次发消息，把观察器写在 status.json 里的「桌面实况」那一段作为额外上下文带给 Claude（定时任务开的会话不带）。
#   托盘控制台里「她现在看到的是」读的也是这份 status.json，所以两边是同一个状态。托盘里关掉总开关，这里就什么都不带。
# 她收着、在说话、冷却没到时桌宠自己会不演（也不会叫她出场），这里不用管。
#
# 配置在 ~/.claude/settings.json 的 hooks 里；调试：同目录 hook.log 记着最近收到的事件和桌宠的回答。
import json
import os
import subprocess
import sys

# 这台电脑上的 Python 不开 UTF-8 模式时，带中文的路径（“大肥鱼”）会读不到；hook 是 Claude Code 直接拉起的，没人帮我们设环境变量，
# 所以没开的话，自己带着 -X utf8 再跑一遍（stdin 原样交给它），用环境变量防止套娃。
if not sys.flags.utf8_mode and not os.environ.get("FISH_HOOK_UTF8"):
    try:
        done = subprocess.run([sys.executable, "-X", "utf8", os.path.abspath(__file__)], stdin=sys.stdin, env={**os.environ, "FISH_HOOK_UTF8": "1"}, timeout=8)
        sys.exit(0)
    except SystemExit:
        raise
    except Exception:
        pass
import re
import shutil
import time
import urllib.request
from pathlib import Path

HERE = Path(__file__).resolve().parent


def find_api():
    # 插件在哪，托盘控制台的 config.json（同一份）里写着；FISH_API 环境变量可以直接指到 api.json。
    if os.environ.get("FISH_API"):
        return Path(os.environ["FISH_API"])
    # 放在插件里（插件\hooks\claude\fish_hook.py）时，往上两层就是插件
    plugin = HERE.parent.parent
    if (plugin / "setup.ps1").exists():
        return plugin / "api.json"
    try:
        root = json.loads((HERE.parent / "config.json").read_text(encoding="utf-8-sig"))["PluginRoot"]
        return Path(root) / "api.json"
    except Exception:
        return Path.home() / "大肥鱼" / "WHALEL2D-main" / "api.json"


API = find_api()
LOG = HERE / "hook.log"
SESSIONS = HERE / "sessions.json"
LONG_TASK = 180  # 秒：这一轮超过这么久才算“长任务”
THINK_TIMEOUT = 30  # 秒：发消息后这么久没有任何动静，算“思考超时”

# 编译、测试命令（只看 Bash 的命令行）
BUILD = re.compile(r"\b(dotnet\s+(build|publish|msbuild)|msbuild|npm\s+run\s+build|yarn\s+build|pnpm\s+build|cargo\s+build|go\s+build|make\b|cmake\s+--build|gradle|mvn|tsc\b|vite\s+build|setup\.ps1)", re.I)
TEST = re.compile(r"(\bdotnet\s+test\b|\.Tests\b|\bpytest\b|python\S*\s+-m\s+(pytest|unittest)|\bnpm\s+(run\s+)?test\b|\byarn\s+test\b|\bpnpm\s+test\b|\bjest\b|\bvitest\b|\bcargo\s+test\b|\bgo\s+test\b|\bmvn\s+test\b|\bgradle\w*\s+test\b)", re.I)
FAILED = re.compile(r"(\b[1-9]\d*\s+(failed|failing|errors?)\b|\bFAILED\b|\bFailed!|失败\s*[:：]?\s*[1-9]|Build FAILED|\berror\s+[A-Z]{2,}\d+)")


# ---- 桌面实况：让 Claude 也能用「她现在看到的是」 ----

STATUS_FRESH = 20  # 秒：status.json 超过这么久没刷新，就当观察器没在跑


def context_plugin_dir():
    # 插件（dafeiyu-context）在哪：桌宠目录里的 context_chat.json 是它启动时写的名片；没有就找 DSH 的默认位置
    try:
        pointer = json.loads((API.parent / "context_chat.json").read_text(encoding="utf-8-sig"))
        folder = Path(pointer["dir"])
        if (folder / "index.js").exists():
            return folder
    except Exception:
        pass
    fallback = Path.home() / ".dsh" / "plugins" / "dafeiyu-context"
    return fallback if (fallback / "index.js").exists() else None


def read_status(folder):
    # 状态没过期才返回（过期了就是观察器没在跑，里面的内容是旧的）
    try:
        status = json.loads((folder / "status.json").read_text(encoding="utf-8-sig"))
        stamp = time.mktime(time.strptime(str(status["at"])[:19], "%Y-%m-%dT%H:%M:%S")) - time.timezone  # 插件写的是 UTC
        return status if time.time() - stamp < STATUS_FRESH else None
    except Exception:
        return None


def ensure_watcher(folder):
    # 先报个平安（独立运行的观察器十分钟没听到动静就自己退出），再看观察器在不在，不在就拉起来
    try:
        (folder / "claude-alive.json").write_text(json.dumps({"at": int(time.time() * 1000)}), encoding="utf-8")
    except Exception:
        return
    if read_status(folder) is not None:
        return
    lock = folder / "claude-spawn.json"
    try:
        if time.time() - float(json.loads(lock.read_text(encoding="utf-8")).get("at", 0)) < 25:
            return  # 刚拉起来过，还在启动，别重复拉
    except Exception:
        pass
    node = shutil.which("node")
    if not node:
        log("桌面实况：找不到 node，没法拉起观察器")
        return
    try:
        lock.write_text(json.dumps({"at": time.time()}), encoding="utf-8")
        env = {**os.environ, "DAFEIYU_API_PATH": str(API)}
        quiet = {"stdin": subprocess.DEVNULL, "stdout": subprocess.DEVNULL, "stderr": subprocess.DEVNULL, "env": env, "cwd": str(folder), "close_fds": True}
        args = [node, str(folder / "index.js"), "--claude"]
        if os.name == "nt":
            detached = 0x00000008 | 0x00000200  # DETACHED_PROCESS | CREATE_NEW_PROCESS_GROUP
            try:
                subprocess.Popen(args, creationflags=detached | 0x01000000, **quiet)  # 能挣脱 Claude Code 的作业对象就挣脱
            except OSError:
                subprocess.Popen(args, creationflags=detached, **quiet)
        else:
            subprocess.Popen(args, start_new_session=True, **quiet)
        log("桌面实况：观察器没在跑，已拉起（node index.js --claude）")
    except Exception as error:
        log(f"桌面实况：没拉起来（{type(error).__name__}）")


def desktop_context(folder):
    # 观察器写好的那一段「【桌面实况】……」；总开关关着、没场景、状态过期时是空的
    status = read_status(folder)
    text = str((status or {}).get("context", "")).strip()
    return text[:600]


def load_sessions():
    try:
        data = json.loads(SESSIONS.read_text(encoding="utf-8"))
        now = time.time()
        return {key: value for key, value in data.items() if now - value.get("seen", 0) < 86400}
    except Exception:
        return {}


def save_sessions(data):
    try:
        SESSIONS.write_text(json.dumps(data, ensure_ascii=False), encoding="utf-8")
    except Exception:
        pass


def scheduled_prompt(prompt):
    # 定时任务的提示词就是 ~/.claude/scheduled-tasks/<ID>/SKILL.md 去掉开头 --- 那段之后的正文
    text = " ".join(str(prompt).split())[:80]
    if len(text) < 20:
        return False
    try:
        for skill in (Path.home() / ".claude" / "scheduled-tasks").glob("*/SKILL.md"):
            body = skill.read_text(encoding="utf-8")
            if body.startswith("---"):
                body = body.split("---", 2)[-1]
            if text in " ".join(body.split()):
                return True
    except Exception:
        pass
    return False


def command_of(data):
    tool = data.get("tool_input") or {}
    return str(tool.get("command", "")) if isinstance(tool, dict) else ""


def output_of(data):
    reply = data.get("tool_response")
    if isinstance(reply, dict):
        return str(reply.get("stdout", "")) + chr(10) + str(reply.get("stderr", ""))
    return str(reply or "")


def pick(data, session):
    event = data.get("hook_event_name", "")
    if event == "Notification":
        kind = str(data.get("notification_type", ""))
        text = str(data.get("message", "")).lower()
        if kind in ("permission_prompt", "elicitation_dialog") or "permission" in text:
            return "授权"
        if kind == "auth_success":
            return None
        return "等你"
    if event == "Stop":
        started = session.get("start")
        return "长任务完成" if started and time.time() - started > LONG_TASK and not session.get("auto") else "完成"
    if event == "SubagentStop":
        return "子任务完成"
    if event == "PostToolUseFailure":
        if data.get("is_interrupt"):
            return "中断"
        command = command_of(data) if data.get("tool_name") == "Bash" else ""
        return "编译失败" if command and (BUILD.search(command) or TEST.search(command)) else "出错"
    if event == "PostToolUse":
        command = command_of(data) if data.get("tool_name") == "Bash" else ""
        if not command or not TEST.search(command):
            return None
        return "编译失败" if FAILED.search(output_of(data)) else "测试通过"
    if event == "SessionStart":
        source = data.get("source", "startup")
        if source == "startup":
            return "开工"
        return "新会话" if source in ("resume", "clear") else None
    if event == "UserPromptSubmit":
        return "收到"
    return None


def log(line):
    try:
        if LOG.exists() and LOG.stat().st_size > 40000:
            LOG.write_text("".join(LOG.read_text(encoding="utf-8").splitlines(True)[-100:]), encoding="utf-8")
        with LOG.open("a", encoding="utf-8") as out:
            out.write(time.strftime("%m-%d %H:%M:%S ") + line + "\n")
    except Exception:
        pass


def main():
    try:
        data = json.loads(sys.stdin.buffer.read().decode("utf-8", "replace") or "{}")
    except Exception:
        data = {}
    sid = str(data.get("session_id", ""))
    sessions = load_sessions()
    session = sessions.get(sid, {})
    if data.get("hook_event_name") == "UserPromptSubmit":
        session = {"start": time.time(), "auto": scheduled_prompt(data.get("prompt", "")) or session.get("auto", False)}
    session["seen"] = time.time()
    if sid:
        sessions[sid] = session
        save_sessions(sessions)
    event_name = data.get("hook_event_name", "")
    # 桌面实况：每个事件都报个平安；会话开始 / 发消息时看看观察器在不在；发消息时把实况带给 Claude
    try:
        folder = context_plugin_dir()
        if folder is not None:
            if event_name in ("SessionStart", "UserPromptSubmit", "Stop"):
                ensure_watcher(folder)
            if event_name == "UserPromptSubmit" and not session.get("auto"):
                text = desktop_context(folder)
                if text:
                    print(json.dumps({"hookSpecificOutput": {"hookEventName": "UserPromptSubmit", "additionalContext": text}}))
                    sys.stdout.flush()
                    log(f"桌面实况：带给 Claude {len(text)} 字")
    except Exception as error:
        log(f"桌面实况：出错（{type(error).__name__}）")
    name = pick(data, session)
    if data.get("hook_event_name") == "Stop" and sid and sid in sessions:
        sessions[sid]["done"] = True  # 这一轮做完了：思考计时看到这个就不喊
        save_sessions(sessions)
    if data.get("hook_event_name") == "UserPromptSubmit" and not session.get("auto") and sid:
        arm_think(sid, session["start"], str(data.get("transcript_path", "")))
    if not name:
        return
    send_event(name, session, data.get("hook_event_name", ""))


def send_event(name, session, source):
    try:
        api = json.loads(API.read_text(encoding="utf-8"))
        request = urllib.request.Request(
            api["url"] + "/call/event",
            data=json.dumps({"name": name, "auto": bool(session.get("auto"))}, ensure_ascii=False).encode("utf-8"),
            # X-FishTray：告诉桌宠这是被动来的请求，不算“有人在用她”，不会拖着她不退场
            headers={"Authorization": "Bearer " + api["token"], "Content-Type": "application/json", "X-FishTray": "1"},
        )
        with urllib.request.urlopen(request, timeout=2) as reply:
            answer = reply.read().decode("utf-8", "replace")
        text = json.loads(answer).get("content", [{}])[0].get("text", answer)
        try:
            text = json.dumps(json.loads(text), ensure_ascii=False)
        except Exception:
            pass
        log(f"{source} -> {name}{'（定时任务）' if session.get('auto') else ''}: {text}")
    except Exception as error:
        log(f"{source} -> {name}: 没送到（{type(error).__name__}）")


# ---- 思考超时：发消息那一刻拉起一个后台小进程，睡 THINK_TIMEOUT 秒再看一眼 ----

def arm_think(sid, token, transcript):
    if not transcript:
        return
    args = [sys.executable, "-X", "utf8", os.path.abspath(__file__), "--think", sid, repr(token), transcript]
    env = {**os.environ, "FISH_HOOK_UTF8": "1"}
    quiet = {"stdin": subprocess.DEVNULL, "stdout": subprocess.DEVNULL, "stderr": subprocess.DEVNULL, "env": env, "close_fds": True}
    try:
        if os.name == "nt":
            # 脱离 Claude Code 的进程组，hook 退出了它还活着；能挣脱作业对象（job）就挣脱，不行就退一步
            detached = 0x00000008 | 0x00000200  # DETACHED_PROCESS | CREATE_NEW_PROCESS_GROUP
            try:
                subprocess.Popen(args, creationflags=detached | 0x01000000, **quiet)  # CREATE_BREAKAWAY_FROM_JOB
            except OSError:
                subprocess.Popen(args, creationflags=detached, **quiet)
        else:
            subprocess.Popen(args, start_new_session=True, **quiet)
        log(f"思考计时开始（{THINK_TIMEOUT} 秒）")
    except Exception as error:
        log(f"思考计时没拉起来（{type(error).__name__}）")


def last_entry_waits_for_tool(transcript):
    # 会话记录（jsonl）的最后一条是不是“助手发了工具调用、还在等结果”：是的话是工具在跑，不是在发呆
    try:
        with open(transcript, "rb") as handle:
            handle.seek(0, os.SEEK_END)
            size = handle.tell()
            handle.seek(max(0, size - 65536))
            lines = handle.read().decode("utf-8", "replace").splitlines()
        for line in reversed(lines):
            line = line.strip()
            if not line:
                continue
            entry = json.loads(line)
            content = (entry.get("message") or {}).get("content")
            if entry.get("type") == "assistant" and isinstance(content, list):
                return any(isinstance(block, dict) and block.get("type") == "tool_use" for block in content)
            return False
    except Exception:
        pass
    return False


def think_watch(sid, token, transcript):
    time.sleep(THINK_TIMEOUT)
    sessions = load_sessions()
    session = sessions.get(sid, {})
    if repr(session.get("start")) != token or session.get("done"):
        log("思考计时：这一轮已经结束或有了新消息，不喊")
        return
    try:
        age = time.time() - os.path.getmtime(transcript)
    except Exception:
        log("思考计时：读不到会话记录，不喊")
        return
    if age < THINK_TIMEOUT - 3:
        log(f"思考计时：{age:.0f} 秒前还有动静，不喊")
        return
    if last_entry_waits_for_tool(transcript):
        log("思考计时：最后一条是工具调用，在等工具跑完，不喊")
        return
    send_event("思考超时", session, "思考计时")


if __name__ == "__main__":
    try:
        if sys.argv[1:2] == ["--think"] and len(sys.argv) >= 5:
            think_watch(sys.argv[2], sys.argv[3], sys.argv[4])
        else:
            main()
    except Exception:
        pass
    sys.exit(0)
