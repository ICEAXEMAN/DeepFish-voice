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
#   SessionStart(接着上次 / 清空重来)   -> 继续会话    PostToolUseFailure 被你中断  -> 中断
#   PreCompact 开始压缩                 -> 压缩开始    SessionStart(compact 压缩做完)  -> 压缩完成
#   进度简报（2026-11-16）：任务清单 -> 步骤开始/步骤完成/步骤接续/步骤全完；Bash 开始 -> 编译中/测试中/推送中；
#     命令跑 60 秒还没完 -> 仍在进行。细节见下面“进度简报”那段。
#   思考一分钟：你发了消息 1 分钟后这一轮还没做完、你也没再发新消息，演一个“加载”小动作（三个动作轮流不重复，不带台词）。
#   思考超时：你发了消息 3 分钟后，这一轮还没做完、你也没有再发新消息，就喊一次（每轮最多一次）。
#     做法：发消息那一刻拉起一个后台小进程（--think）睡 3 分钟再看一眼；这一轮已经做完或你又发了新消息，它就悄悄走。
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
THINK_LOAD = 60  # 秒：发消息后这么久这一轮还没做完，演“思考一分钟”（半睁/闭眼/斜眼 + 加载，不带台词；用户 2026-11-14 加）
THINK_TIMEOUT = 180  # 秒：发消息后这么久没有任何动静，算“思考超时”（用户 2026-11-14 从 30 秒改成 5 分钟，当天又改成 3 分钟）

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
        if source == "compact":
            return "压缩完成"  # /compact 或自动压缩做完了（用户 2026-11-16 要的）
        return "继续会话" if source in ("resume", "clear") else None
    if event == "PreCompact":
        return "压缩开始"  # 开始压缩前（手动 /compact 和自动压缩都会来）
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


# ---- 进度简报的日志（用户 2026-11-16）：同目录 briefing.log，记收到的工具事件和每次简报说了什么。不往标准输出写东西（写了会进模型上下文）。
# Bash 的开始（PreToolUse）和结束（PostToolUse / PostToolUseFailure）用 tool_use_id 配对；
# 任务清单（TodoWrite、update_plan）和收尾（wrap_up）把参数原样记下来，看看步骤名在不在。
BRIEF_LOG = HERE / "briefing.log"
BRIEF_TOOLS = re.compile(r"^(TodoWrite|.*update_plan|.*wrap_up)$")


def brief_log(line):
    try:
        if BRIEF_LOG.exists() and BRIEF_LOG.stat().st_size > 300000:
            BRIEF_LOG.write_text("".join(BRIEF_LOG.read_text(encoding="utf-8").splitlines(True)[-1000:]), encoding="utf-8")
        with BRIEF_LOG.open("a", encoding="utf-8") as out:
            now = time.time()
            out.write(time.strftime("%m-%d %H:%M:%S", time.localtime(now)) + f".{int(now % 1 * 1000):03d} " + line + "\n")
    except Exception:
        pass


def brief_probe(data):
    event = data.get("hook_event_name", "")
    tool = str(data.get("tool_name", ""))
    args = data.get("tool_input") if isinstance(data.get("tool_input"), dict) else {}
    head = f"{event} {tool} s={str(data.get('session_id', ''))[:8]} id={str(data.get('tool_use_id', ''))[-10:]}"
    if data.get("agent_id"):
        head += f" agent={data.get('agent_id')}"
    if tool == "Bash":
        command = " ".join(str(args.get("command", "")).split())[:80]
        tail = ""
        reply = data.get("tool_response")
        if isinstance(reply, dict):
            tail = f" interrupted={reply.get('interrupted')}"
        if event == "PostToolUseFailure":
            tail += f" interrupt={data.get('is_interrupt')} error={str(data.get('error', ''))[:80]}"
        brief_log(f"{head} desc={args.get('description', '')} cmd={command}{tail}")
    else:
        brief_log(f"{head} keys={','.join(sorted(data))} input={json.dumps(args, ensure_ascii=False)[:800]} reply={str(data.get('tool_response', ''))[:200]}")


# ---- 进度简报：接上台词（用户 2026-11-16）。和 DSH 的 dafeiyu-hooks 用同一套事件名、同一套节奏 ----
# 步骤：读任务清单（TodoWrite 每次是整份；这个 app 的 update_plan 只有第一次带整份，之后是 advance / done / start，要自己推），
#       和“上次说过的清单”比：新清单 -> 步骤开始；有步骤做完 -> 步骤完成；只换了正在做的 -> 步骤接续；全做完 -> 步骤全完。
#       两次步骤简报至少隔 BRIEF_GAP 秒：没到点的先攒着，拉一个后台小进程（--brief-flush）到点只说最新的状态。
# 阶段：Bash 开始前（PreToolUse）认得出是编译、测试、推送就报（桌宠那边冷却 45 秒）。
# 心跳：Bash 开始时拉一个后台小进程（--beat），BEAT_AFTER 秒后这条命令还没结束，就报一次“仍在进行”（当前步骤，没有就用命令说明）。
# 收尾：这一轮的清单全做完了，Stop 就不再发“完成”，免得连说两句。
# 每说一次步骤简报或心跳，思考超时（3 分钟）重新计时（老大定的“简报时顺延”）；思考一分钟不顺延。
# 定时任务开的会话、子代理的调用都不报。只有清单两步以上才报。
BRIEF_STATE = HERE / "briefing.json"
BRIEF_GAP = 18
BEAT_AFTER = 60
PUSH = re.compile(r"\bgit\s+push\b", re.I)
LOCK = HERE / "state.lock"


class state_lock:
    # 几个 hook 进程会同时读写 sessions.json / briefing.json：用一个锁文件排队，拿不到（2 秒）就照样往下走
    def __enter__(self):
        self.fd = None
        for _ in range(100):
            try:
                self.fd = os.open(LOCK, os.O_CREAT | os.O_EXCL | os.O_WRONLY)
                return self
            except FileExistsError:
                try:
                    if time.time() - LOCK.stat().st_mtime > 5:
                        LOCK.unlink()
                except OSError:
                    pass
                time.sleep(0.02)
            except OSError:
                return self
        return self

    def __exit__(self, *_):
        if self.fd is not None:
            os.close(self.fd)
            try:
                LOCK.unlink()
            except OSError:
                pass


def clean_name(text, limit=16):
    # 先去掉括号里的补充（“下载 Cubism SDK（用户操作）” -> “下载 Cubism SDK”），再截断
    raw = " ".join(str(text or "").split())
    text = re.sub(r"[（(][^（）()]*[）)]", "", raw)
    text = re.sub(r"[（(][^）)]*$", "", text)  # 没写完的半个括号（本来就被截过的）也去掉
    text = " ".join(text.split()).strip(" ：:，,。、") or raw  # 整个都在括号里的，就用原文
    return text if len(text) <= limit else text[: limit - 1].rstrip() + "…"


def load_brief():
    try:
        data = json.loads(BRIEF_STATE.read_text(encoding="utf-8"))
        now = time.time()
        return {key: value for key, value in data.items() if now - value.get("seen", 0) < 86400}
    except Exception:
        return {}


def save_brief(data):
    try:
        BRIEF_STATE.write_text(json.dumps(data, ensure_ascii=False), encoding="utf-8")
    except Exception:
        pass


def next_plan(plan, tool, args):
    if tool == "TodoWrite" or "items" in args:
        items = args.get("todos") if tool == "TodoWrite" else args.get("items")
        return [{"content": str(i.get("content") or i.get("activeForm") or ""), "status": str(i.get("status", "pending"))} for i in items or [] if isinstance(i, dict)]
    plan = [dict(p) for p in plan]
    if tool.endswith("wrap_up"):  # wrap_up 会顺手把清单勾完
        for p in plan:
            p["status"] = "completed"
        return plan
    if args.get("advance"):
        for p in plan:
            if p["status"] == "in_progress":
                p["status"] = "completed"
                break
        for p in plan:
            if p["status"] == "pending":
                p["status"] = "in_progress"
                break
    for i in args.get("done") or []:
        if isinstance(i, int) and 1 <= i <= len(plan):
            plan[i - 1]["status"] = "completed"
    start = args.get("start")
    if isinstance(start, int) and 1 <= start <= len(plan):
        plan[start - 1]["status"] = "in_progress"
    return plan


def plan_news(spoken, plan):
    # 和上次说过的比，这次该说什么：(事件名, vars) 或 None
    if len(plan) < 2:
        return None
    total, left = len(plan), sum(p["status"] != "completed" for p in plan)
    current = next((p for p in plan if p["status"] == "in_progress"), None)
    same = spoken is not None and [p["content"] for p in spoken] == [p["content"] for p in plan]
    if left == 0:
        return None if same and all(s["status"] == "completed" for s in spoken) else ("步骤全完", {"total": str(total)})
    if not same:
        first = current or next(p for p in plan if p["status"] != "completed")
        found = {"total": str(total), "left": str(left), "step": clean_name(first["content"])}
        done = total - left  # 清单一出现就已经做完几步（先干活后补清单）：台词说“已经做完几步”，不说“第一步”
        found.update({"done": str(done)} if done else {"fresh": ""})
        return ("步骤开始", found)
    newly = [p for p, s in zip(plan, spoken) if p["status"] == "completed" and s["status"] != "completed"]
    if newly:
        found = {"step": clean_name(newly[-1]["content"]), "left": str(left), "total": str(total)}
        if current:
            found["next"] = clean_name(current["content"])
        return ("步骤完成", found)
    was = next((s["content"] for s in spoken if s["status"] == "in_progress"), None)
    if current and current["content"] != was:
        return ("步骤接续", {"step": clean_name(current["content"])})
    return None


def brief_due(sid, brief):
    # 在锁里调：到点了就返回要说的（并记成“说过了”），没到点就排一个 flush，返回 ("flush", 秒数, 记号)
    state = brief.get(sid)
    if not state:
        return None
    news = plan_news(state.get("spoken"), state.get("plan") or [])
    if not news:
        return None
    now = time.time()
    wait = BRIEF_GAP - (now - state.get("last", 0))
    if wait > 0:
        if state.get("flush_at", 0) > now:
            return None  # 已经排着一个了，到点它会说最新的
        state["flush"], state["flush_at"] = repr(now), now + wait
        return ("flush", wait, state["flush"])
    state["spoken"] = [dict(p) for p in state["plan"]]
    state["last"], state["flush_at"] = now, 0
    return news


def spawn_quiet(args):
    # 拉一个脱离 Claude Code 的后台小进程（和思考计时一样的做法）
    args = [sys.executable, "-X", "utf8", os.path.abspath(__file__), *args]
    quiet = {"stdin": subprocess.DEVNULL, "stdout": subprocess.DEVNULL, "stderr": subprocess.DEVNULL, "env": {**os.environ, "FISH_HOOK_UTF8": "1"}, "close_fds": True}
    try:
        if os.name == "nt":
            detached = 0x00000008 | 0x00000200
            try:
                subprocess.Popen(args, creationflags=detached | 0x01000000, **quiet)
            except OSError:
                subprocess.Popen(args, creationflags=detached, **quiet)
        else:
            subprocess.Popen(args, start_new_session=True, **quiet)
    except Exception as error:
        brief_log(f"后台进程没拉起来（{type(error).__name__}）{args[4:]}")


def brief_speak(sid, result, source):
    # 真的发出去；发出去了就把思考超时重新计时
    if not result:
        return
    if result[0] == "flush":
        spawn_quiet(["--brief-flush", sid, result[2], str(result[1])])
        brief_log(f"简报攒着，{result[1]:.0f} 秒后说")
        return
    name, values = result
    answer = send_event(name, {"auto": False}, source, values)
    brief_log(f"简报 {name} {json.dumps(values, ensure_ascii=False)} -> {answer}")
    push_think(sid)


def push_think(sid):
    # 简报时顺延：这一轮还没结束，就把“思考超时”从现在起重新数 3 分钟（旧的计时进程醒来看到记号变了就不喊）
    with state_lock():
        sessions = load_sessions()
        session = sessions.get(sid)
        if not session or session.get("done") or session.get("auto") or not session.get("start"):
            return
        now = time.time()
        session["think"] = repr(now)
        save_sessions(sessions)
    spawn_think(sid, now, "-", THINK_TIMEOUT, "思考超时")


def brief_hook(data):
    # PreToolUse / PostToolUse / PostToolUseFailure 里和简报有关的部分
    event, tool = data.get("hook_event_name", ""), str(data.get("tool_name", ""))
    sid = str(data.get("session_id", ""))
    args = data.get("tool_input") if isinstance(data.get("tool_input"), dict) else {}
    if not sid or data.get("agent_id") or load_sessions().get(sid, {}).get("auto"):
        return
    call = str(data.get("tool_use_id", ""))
    if tool == "Bash" and event == "PreToolUse":
        command = str(args.get("command", ""))
        with state_lock():
            brief = load_brief()
            state = brief.setdefault(sid, {})
            state["seen"] = time.time()
            state.setdefault("cmds", {})[call] = {"t": time.time(), "desc": clean_name(args.get("description", ""))}
            save_brief(brief)
        spawn_quiet(["--beat", sid, call])
        stage = "编译中" if BUILD.search(command) else "测试中" if TEST.search(command) else "推送中" if PUSH.search(command) else None
        if stage:
            brief_log(f"阶段 {stage} -> {send_event(stage, {'auto': False}, 'PreToolUse', None, wait=1)}")
        return
    if tool == "Bash":  # 命令结束（成功或失败）：心跳不用报了
        with state_lock():
            brief = load_brief()
            if brief.get(sid, {}).get("cmds", {}).pop(call, None) is not None:
                save_brief(brief)
        return
    if event != "PostToolUse" or not BRIEF_TOOLS.match(tool):
        return
    with state_lock():
        brief = load_brief()
        state = brief.setdefault(sid, {})
        state["seen"] = time.time()
        state["plan"] = next_plan(state.get("plan") or [], tool, args)
        state["turn_plan"] = True
        result = brief_due(sid, brief)
        save_brief(brief)
    brief_speak(sid, result, tool)


def brief_flush(sid, token, seconds):
    time.sleep(max(0.0, seconds))
    with state_lock():
        brief = load_brief()
        state = brief.get(sid)
        if not state or state.get("flush") != token:
            return
        state["flush_at"] = 0
        result = brief_due(sid, brief)
        save_brief(brief)
    brief_speak(sid, result, "攒着的简报")


def beat_watch(sid, call):
    time.sleep(BEAT_AFTER)
    with state_lock():
        brief = load_brief()
        state = brief.get(sid) or {}
        command = state.get("cmds", {}).pop(call, None)
        if command is None:
            return  # 命令已经结束了
        current = next((p for p in state.get("plan") or [] if p["status"] == "in_progress"), None)
        step = clean_name(current["content"]) if current and state.get("turn_plan") else command.get("desc", "")
        now = time.time()
        if not step or now - state.get("last", 0) < BRIEF_GAP:
            save_brief(brief)
            return
        state["last"] = now
        save_brief(brief)
    brief_speak(sid, ("仍在进行", {"step": step}), "心跳")


def main():
    try:
        data = json.loads(sys.stdin.buffer.read().decode("utf-8", "replace") or "{}")
    except Exception:
        data = {}
    event = data.get("hook_event_name", "")
    if event in ("PreToolUse", "PostToolUse", "PostToolUseFailure"):
        tool = str(data.get("tool_name", ""))
        if tool == "Bash" or BRIEF_TOOLS.match(tool):
            brief_probe(data)
            try:
                brief_hook(data)
            except Exception as error:
                brief_log(f"简报出错：{type(error).__name__} {error}")
        # PreToolUse 和清单类工具到此为止（挡在命令前面的时间越短越好）
        if event == "PreToolUse" or (tool != "Bash" and event == "PostToolUse"):
            return
    sid = str(data.get("session_id", ""))
    with state_lock():
        sessions = load_sessions()
        session = sessions.get(sid, {})
        if data.get("hook_event_name") == "UserPromptSubmit":
            session = {"start": time.time(), "auto": scheduled_prompt(data.get("prompt", "")) or session.get("auto", False)}
        session["seen"] = time.time()
        if sid:
            sessions[sid] = session
            save_sessions(sessions)
    if event == "UserPromptSubmit" and sid:
        with state_lock():
            brief = load_brief()
            if sid in brief:
                brief[sid]["turn_plan"] = False  # 新的一轮：清单还没动过
                save_brief(brief)
    event_name = data.get("hook_event_name", "")
    # 桌面实况（本机加，2026-11-12）：每个事件都报个平安；会话开始 / 发消息时看看观察器在不在；发消息时把实况带给 Claude
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
    if event == "Stop" and name in ("完成", "长任务完成"):
        state = load_brief().get(sid) or {}
        plan = state.get("plan") or []
        if state.get("turn_plan") and len(plan) >= 2 and all(p["status"] == "completed" for p in plan):
            brief_log(f"这一轮的清单全做完了，不再发“{name}”")
            name = None
    if data.get("hook_event_name") == "Stop" and sid and sid in sessions:
        with state_lock():
            sessions = load_sessions()
            if sid in sessions:
                sessions[sid]["done"] = True  # 这一轮做完了：思考计时看到这个就不喊
                save_sessions(sessions)
    if data.get("hook_event_name") == "UserPromptSubmit" and not session.get("auto") and sid:
        arm_think(sid, session["start"], str(data.get("transcript_path", "")))
    if not name:
        return
    send_event(name, session, data.get("hook_event_name", ""))


def send_event(name, session, source, values=None, wait=2):
    # values：进度简报的占位符（{step} 这类），桌宠按 events.txt 的模板填进去；返回桌宠的回答（日志用）
    try:
        api = json.loads(API.read_text(encoding="utf-8"))
        body = {"name": name, "auto": bool(session.get("auto"))}
        if values:
            body["vars"] = values
        request = urllib.request.Request(
            api["url"] + "/call/event",
            data=json.dumps(body, ensure_ascii=False).encode("utf-8"),
            # X-FishTray：告诉桌宠这是被动来的请求，不算“有人在用她”，不会拖着她不退场
            headers={"Authorization": "Bearer " + api["token"], "Content-Type": "application/json", "X-FishTray": "1"},
        )
        with urllib.request.urlopen(request, timeout=wait) as reply:
            answer = reply.read().decode("utf-8", "replace")
        text = json.loads(answer).get("content", [{}])[0].get("text", answer)
        try:
            text = json.dumps(json.loads(text), ensure_ascii=False)
        except Exception:
            pass
        log(f"{source} -> {name}{'（定时任务）' if session.get('auto') else ''}: {text}")
        return text
    except Exception as error:
        log(f"{source} -> {name}: 没送到（{type(error).__name__}）")
        return f"没送到（{type(error).__name__}）"


# ---- 思考超时：发消息那一刻拉起一个后台小进程，睡 THINK_TIMEOUT 秒再看一眼 ----

def arm_think(sid, token, transcript):
    if not transcript:
        return
    spawn_think(sid, token, transcript, THINK_LOAD, "思考一分钟")
    spawn_think(sid, token, transcript, THINK_TIMEOUT, "思考超时")


def spawn_think(sid, token, transcript, seconds, event):
    args = [sys.executable, "-X", "utf8", os.path.abspath(__file__), "--think", sid, repr(token), transcript, str(seconds), event]
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
        log(f"思考计时开始（{event}，{seconds} 秒）")
    except Exception as error:
        log(f"思考计时没拉起来（{type(error).__name__}）")


def think_watch(sid, token, transcript, seconds=THINK_TIMEOUT, event="思考超时"):
    time.sleep(seconds)
    sessions = load_sessions()
    session = sessions.get(sid, {})
    # 思考超时比的是 think 记号（每次进度简报都会换新的，旧的计时进程就不喊了）；思考一分钟照旧比这一轮的开始时间
    mark = session.get("think", repr(session.get("start"))) if event == "思考超时" else repr(session.get("start"))
    if mark != token or session.get("done"):
        log("思考计时：这一轮已经结束、有了新消息或刚报过进度，不喊")
        return
    # 条件只有上面这一个（用户 2026-11-14 放宽）：这一轮还没做完、你也没再发新消息，到点就喊；
    # 不再看会话记录有没有新动静、也不管最后一条是不是在等工具（所以跑长命令时也会喊）。
    send_event(event, session, "思考计时")


if __name__ == "__main__":
    try:
        if sys.argv[1:2] == ["--brief-flush"] and len(sys.argv) >= 5:
            brief_flush(sys.argv[2], sys.argv[3], float(sys.argv[4]))
        elif sys.argv[1:2] == ["--beat"] and len(sys.argv) >= 4:
            beat_watch(sys.argv[2], sys.argv[3])
        elif sys.argv[1:2] == ["--think"] and len(sys.argv) >= 5:
            think_watch(sys.argv[2], sys.argv[3], sys.argv[4], int(sys.argv[5]) if len(sys.argv) > 5 else THINK_TIMEOUT, sys.argv[6] if len(sys.argv) > 6 else "思考超时")
        else:
            main()
    except Exception:
        pass
    sys.exit(0)
