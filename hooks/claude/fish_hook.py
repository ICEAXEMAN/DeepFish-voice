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
# 定时任务（哄睡、午饭提醒）自己开的会话：发出的事件都带 auto，桌宠不把它当成你本人在（不记动静、不加亲密度）。
# 会话状态（这一轮几点开始、是不是定时任务开的）记在同目录 sessions.json，一天前的自动清掉。
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

# 编译、测试命令（只看 Bash 的命令行）
BUILD = re.compile(r"\b(dotnet\s+(build|publish|msbuild)|msbuild|npm\s+run\s+build|yarn\s+build|pnpm\s+build|cargo\s+build|go\s+build|make\b|cmake\s+--build|gradle|mvn|tsc\b|vite\s+build|setup\.ps1)", re.I)
TEST = re.compile(r"(\bdotnet\s+test\b|\.Tests\b|\bpytest\b|python\S*\s+-m\s+(pytest|unittest)|\bnpm\s+(run\s+)?test\b|\byarn\s+test\b|\bpnpm\s+test\b|\bjest\b|\bvitest\b|\bcargo\s+test\b|\bgo\s+test\b|\bmvn\s+test\b|\bgradle\w*\s+test\b)", re.I)
FAILED = re.compile(r"(\b[1-9]\d*\s+(failed|failing|errors?)\b|\bFAILED\b|\bFailed!|失败\s*[:：]?\s*[1-9]|Build FAILED|\berror\s+[A-Z]{2,}\d+)")


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
            return None
        command = command_of(data) if data.get("tool_name") == "Bash" else ""
        return "编译失败" if command and (BUILD.search(command) or TEST.search(command)) else "出错"
    if event == "PostToolUse":
        command = command_of(data) if data.get("tool_name") == "Bash" else ""
        if not command or not TEST.search(command):
            return None
        return "编译失败" if FAILED.search(output_of(data)) else "测试通过"
    if event == "SessionStart":
        return "开工" if data.get("source", "startup") == "startup" else None
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
    name = pick(data, session)
    if not name:
        return
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
        log(f"{data.get('hook_event_name')} -> {name}{'（定时任务）' if session.get('auto') else ''}: {text}")
    except Exception as error:
        log(f"{data.get('hook_event_name')} -> {name}: 没送到（{type(error).__name__}）")


if __name__ == "__main__":
    try:
        main()
    except Exception:
        pass
    sys.exit(0)
