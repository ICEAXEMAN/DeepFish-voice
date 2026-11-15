// 大肥鱼事件转发：把 DSH 的 Agent 事件转发给桌宠，让她按 events.txt 演一小下。
// 不经过模型、不花 token；发不出去静默。事件是 scope-filtered 的（@deepseek-ai/dsh-scope）：
// agent/* 事件要挂在每个 agent 的 ctx 上（agent.ctx），tools/result、subagent/end 挂在 root ctx。
// 压缩（/compact、上下文满了自动压）没有自己的 ctx 事件，compaction/* 只写会话日志，
// 所以挂在 agent.ctx 的 session/event 上，看 compaction/start 与 compaction/end（用户 2026-11-16 加）。
import { readFile, appendFile } from 'node:fs/promises'
import { fileURLToPath } from 'node:url'

export const name = 'dafeiyu-hooks'

// 编译、测试、失败正则：照搬 Claude 的 fish_hook.py。
const BUILD = /\b(dotnet\s+(build|publish|msbuild)|msbuild|npm\s+run\s+build|yarn\s+build|pnpm\s+build|cargo\s+build|go\s+build|make\b|cmake\s+--build|gradle|mvn|tsc\b|vite\s+build|setup\.ps1)/i
const TEST = /(\bdotnet\s+test\b|\.Tests\b|\bpytest\b|python\S*\s+-m\s+(pytest|unittest)|\bnpm\s+(run\s+)?test\b|\byarn\s+test\b|\bpnpm\s+test\b|\bjest\b|\bvitest\b|\bcargo\s+test\b|\bgo\s+test\b|\bmvn\s+test\b|\bgradle\w*\s+test\b)/i
const FAILED = /(\b[1-9]\d*\s+(failed|failing|errors?)\b|\bFAILED\b|\bFailed!|失败\s*[:：]?\s*[1-9]|Build FAILED|\berror\s+[A-Z]{2,}\d+)/
const LONG_TASK = 180 * 1000 // 这一轮从用户发消息算起超过 3 分钟算「长任务」
const THINK_LOAD = 60 * 1000 // 收到消息 1 分钟后这一轮还没做完，演「思考一分钟」（半睁/闭眼/斜眼 + 加载，不带台词；用户 2026-11-14 加）
const THINK_TIMEOUT = 3 * 60 * 1000 // 收到消息 3 分钟后这一轮还没做完、用户也没再发新消息，算「思考超时」（用户 2026-11-14：30 秒改成 5 分钟又改成 3 分钟，并去掉“没有工具调用”这个条件）

export function apply(ctx, config) {
  // 配置里没填 / 还留着 <插件完整路径> 占位符时，用插件自己旁边的 api.json（hooks/dsh/dafeiyu-hooks → ../../../api.json）
  const configured = typeof config?.apiPath === 'string' ? config.apiPath.trim() : ''
  const apiPath = configured && !configured.includes('<插件完整路径>') ? configured : fileURLToPath(new URL('../../../api.json', import.meta.url))
  const logPath = new URL('./hook.log', import.meta.url) // 调试用：最近转发了什么、桌宠怎么回的

  async function log(msg) {
    try { await appendFile(logPath, `${new Date().toISOString()} ${msg}\n`, 'utf-8') } catch {}
  }

  async function sendEvent(eventName, attempt = 0) {
    try {
      const api = JSON.parse(await readFile(apiPath, 'utf-8'))
      const res = await fetch(`${api.url}/call/event`, {
        method: 'POST',
        headers: {
          Authorization: `Bearer ${api.token}`,
          'Content-Type': 'application/json',
          'X-FishTray': '1',
        },
        body: JSON.stringify({ name: eventName }),
      })
      const text = await res.text()
      await log(`sent ${eventName}${attempt ? ` retry ${attempt}` : ''}: ${text.slice(0, 120)}`)
      // 开工/收到 撞上她在说话时会被丢；等一两秒再试，别把开场白吞掉
      if (attempt < 4 && (eventName === '收到' || eventName === '开工')) {
        let reason = ''
        try {
          const outer = JSON.parse(text)
          const inner = JSON.parse(outer?.content?.[0]?.text || '{}')
          reason = String(inner?.reason || '')
        } catch {}
        if (reason.includes('她在说话') || reason.includes('她在忙别的')) {
          await new Promise((resolve) => setTimeout(resolve, 1800))
          return sendEvent(eventName, attempt + 1)
        }
      }
    } catch (e) {
      await log(`FAIL ${eventName}: ${e?.message || e}`)
    }
  }

  // pwsh / bash 工具的命令在 exec.arguments.command 里。
  function commandOf(exec) {
    const a = exec?.arguments
    return a && typeof a === 'object' && typeof a.command === 'string' ? a.command : ''
  }

  // 从工具结果里拼出完整文本、退出码和是否抛错。DSH 的 pwsh/bash 命令即使退出码非 0，
  // 工具本身也不抛异常（isError 只表示基础设施失败），退出码在渲染结果末尾的 `[exit code: N]` 里。
  function outcomeOf(result) {
    const parts = []
    let exitCode = null
    for (const block of result?.content || []) {
      if (block && typeof block === 'object' && typeof block.text === 'string') parts.push(block.text)
    }
    const v = result?.value
    if (typeof v === 'string') parts.push(v)
    else if (v && typeof v === 'object') {
      for (const key of ['stdout', 'stderr', 'output', 'text', 'result']) {
        if (typeof v[key] === 'string') parts.push(v[key])
      }
      if (typeof v.exitCode === 'number') exitCode = v.exitCode
      else if (typeof v.exit_code === 'number') exitCode = v.exit_code
    }
    if (result?.isError) parts.push(String(result?.error?.message ?? ''))
    const text = parts.join('\n')
    if (exitCode === null) {
      const m = /\[exit code:\s*(\d+)\]/i.exec(text)
      if (m) exitCode = Number(m[1])
    }
    return { text, exitCode, isError: result?.isError === true }
  }

  const disposers = new Map()
  const turnStart = new Map() // agentId -> 用户发消息的时间（毫秒）
  const thinkTimers = new Map() // agentId -> 思考超时计时器（一分钟的和三分钟的两个）
  const announced = new Set() // 已经喊过开工/继续会话的 agent（同一个 agent 不喊第二次；冷却交给桌宠那边的 events.txt，和 Claude 一样）

  function clearThink(id) {
    for (const t of thinkTimers.get(id) || []) clearTimeout(t)
    thinkTimers.delete(id)
  }
  function clearAllThink() { for (const id of [...thinkTimers.keys()]) clearThink(id) }

  // 根 agent：没有任何「父」字段。字段名在不同版本可能不一样，多认几个；认不出就当根（再靠间隔去重）。
  function isRoot(agent) {
    for (const key of ['parent', 'parentId', 'parentID', 'parentAgent', 'parentAgentId', 'parentAgentID', 'owner']) {
      if (agent?.[key]) return false
    }
    return true
  }

  // turn-stopping 的载荷里看得出是用户中止的话 → true。看不出就当正常完成。
  function wasInterrupted(payload) {
    if (!payload || typeof payload !== 'object') return false
    if (payload.aborted === true || payload.interrupted === true || payload.cancelled === true || payload.canceled === true) return true
    const why = [payload.reason, payload.cause, payload.stopReason, payload.status, payload.error?.name, payload.error?.message]
      .filter((x) => typeof x === 'string').join(' ')
    return /abort|interrupt|cancel|user[-_ ]?stop|中断|取消/i.test(why)
  }

  function registerAgent(agent) {
    if (!agent?.ctx || typeof agent.ctx.on !== 'function' || disposers.has(agent.id)) return
    const actx = agent.ctx
    const dispose = actx.effect(() => {
      actx.on('agent/error', () => { clearThink(agent.id); void sendEvent('出错') })
      actx.on('agent/inbox/inserted', () => {
        turnStart.set(agent.id, Date.now())
        // 新工作区恢复旧会话时不一定有 agent/created；第一次说话补一次开场白（接着旧会话就说「继续会话」）
        if (isRoot(agent) && !announced.has(agent.id)) {
          announced.add(agent.id)
          const opening = wasResumed(null, agent) ? '继续会话' : '开工'
          void log(`first message -> ${opening} ${agent.id}`)
          void sendEvent(opening)
        } else {
          void sendEvent('收到')
        }
        // 思考超时：3 分钟后这一轮还没结束就喊一声（只喊一次）；中途调用工具不再取消计时。又来一条新消息会重新计时，这一轮结束（turn-stopping）或出错才取消
        clearThink(agent.id)
        thinkTimers.set(agent.id, [
          setTimeout(() => void sendEvent('思考一分钟'), THINK_LOAD),
          setTimeout(() => {
            thinkTimers.delete(agent.id)
            void sendEvent('思考超时')
          }, THINK_TIMEOUT),
        ])
      })
      actx.on('agent/turn-stopping', (payload) => {
        clearThink(agent.id)
        const started = turnStart.get(agent.id)
        turnStart.delete(agent.id)
        void log(`turn-stopping keys=${payload && typeof payload === 'object' ? Object.keys(payload).join(',') : typeof payload}`)
        if (wasInterrupted(payload)) return void sendEvent('中断')
        void sendEvent(started && Date.now() - started > LONG_TASK ? '长任务完成' : '完成')
      })
      // 压缩（用户 2026-11-16：和 Claude Code 的 /compact 对齐，台词在 events.txt 里共用）。
      // compaction/start 在整理前同步追加，compaction/summary 是摘要写完，compaction/end 收尾（失败也追加，带 error）。
      // 手动 /compact 带 sourceCommandId、turn 为 null；上下文满了自动压发生在轮次里。两个都算，和 Claude 的 PreCompact 一样分不出来。
      actx.on('session/event', (session, event) => {
        if (!event) return
        // agent 级订阅本来就只收这个 agent 的会话；id 对得上更稳，形状不对时不拦（万一是别的 id 空间）
        const sid = session?.id
        if (sid && agent?.id && String(sid) !== String(agent.id)) return
        const data = event.data || {}
        if (event.type === 'compaction/start') {
          void log(`compaction/start turn=${data.turn} manual=${data.sourceCommandId ? 'yes' : 'no'}`)
          void sendEvent('压缩开始')
        } else if (event.type === 'compaction/end') {
          void log(`compaction/end turn=${data.turn} error=${data.error || 'none'}`)
          // 压缩失败（error 有值）不演「整理完啦」，不然是骗人
          if (!data.error) void sendEvent('压缩完成')
        }
      })
    })
    disposers.set(agent.id, () => { turnStart.delete(agent.id); clearThink(agent.id); announced.delete(agent.id); dispose() })
    void log(`registered ${agent.id}`)
  }

  // 1. 枚举现有 agent：插件加载前就已经创建的（比如当前会话）也挂上，不用重启。
  try {
    const agents = ctx.get?.('agents')
    const list = agents?.list?.() || agents?.roots?.() || []
    const initiator = agents?.currentInitiator?.()
    void log(`initiator ${initiator?.id || 'none'}`)
    for (const agent of list) {
      registerAgent(agent)
      // 只把发起这次加载的当前 agent 当成“已经打过招呼”；别的开着的工作区等第一次说话再补
      if (agent.id === initiator?.id || (!initiator && agent === list[0])) announced.add(agent.id)
    }
  } catch {}

  // 这个 agent 是不是“接着以前的会话”（用户 2026-11-16：DSH 也要分开工 / 继续会话，和 Claude 一样）。
  // DSH 没有明说的 source 字段，所以多认几种：载荷或 agent 上有“恢复/重开”的标记，或者 agent 是早就创建好的（创建时间远早于现在）。
  // 认不出就当新会话。每个 agent 创建时都会把能看到的字段写进 hook.log（created ... resumed=...），方便对着真实情况再调。
  function wasResumed(payload, agent) {
    for (const holder of [payload, agent]) {
      if (!holder || typeof holder !== 'object') continue
      for (const key of ['resumed', 'restored', 'isRestored', 'isResumed', 'loaded', 'hydrated', 'fromHistory', 'reopened']) {
        if (holder[key] === true) return true
      }
      for (const key of ['source', 'reason', 'kind', 'origin', 'mode', 'trigger']) {
        if (typeof holder[key] === 'string' && /resum|restor|load|hydrat|histor|reopen|continue|recover/i.test(holder[key])) return true
      }
    }
    for (const key of ['createdAt', 'created', 'startedAt', 'startTime', 'createTime']) {
      const raw = agent?.[key] ?? payload?.[key]
      const at = typeof raw === 'number' ? (raw < 1e12 ? raw * 1000 : raw) : typeof raw === 'string' ? Date.parse(raw) : NaN
      if (Number.isFinite(at) && Date.now() - at > 60 * 1000) return true
    }
    return false
  }

  function describe(payload, agent) {
    const brief = (obj) => {
      if (!obj || typeof obj !== 'object') return String(obj)
      return Object.keys(obj).slice(0, 40).map((key) => {
        const v = obj[key]
        return typeof v === 'string' || typeof v === 'number' || typeof v === 'boolean' ? `${key}=${String(v).slice(0, 40)}` : key
      }).join(',')
    }
    return `payload[${brief(payload)}] agent[${brief(agent)}]`
  }

  // 2. 未来新创建的 agent。
  const offCreated = ctx.on('agent/created', (payload) => {
    const agent = payload?.agent
    registerAgent(agent)
    // 只有根 agent 才喊，子代理不喊；新会话演“开工”，接着以前的会话演“继续会话”（台词和冷却都在 events.txt，和 Claude 一样）
    if (!agent || !isRoot(agent) || announced.has(agent.id)) return
    announced.add(agent.id)
    const resumed = wasResumed(payload, agent)
    void log(`created ${agent.id} resumed=${resumed} ${describe(payload, agent)}`)
    void sendEvent(resumed ? '继续会话' : '开工')
  })

  // 3. subagent/end 挂在 root 上（已验证能收到）。
  const offSubagent = ctx.on('subagent/end', () => void sendEvent('子任务完成'))

  // 4. 授权和用户提问是 root 级 waterfall，不是 agent 级；之前挂在 agent 上收不到。
  const offApproval = ctx.on('approval/request', (_req, next) => { void sendEvent('授权'); return next() })
  const offQuestion = ctx.on('user-questions/request', (_req, next) => { void sendEvent('等你'); return next() })

  // 4. tools/result 挂在 root 上（Scoped<ToolsService>，和 subagent/end 一样不是 agent 级）：命令执行完。
  const offTools = ctx.on('tools/result', (exec, result) => {
    const command = commandOf(exec)
    if (!command) return
    const { text, exitCode, isError } = outcomeOf(result)
    const failed = isError || (exitCode !== null && exitCode !== 0)
    const isBuild = BUILD.test(command)
    const isTest = TEST.test(command)
    if (failed) {
      // 命令失败（退出码非 0 或工具抛错）：编译/测试命令 → 编译失败，其余照旧 → 出错。
      const name = isBuild || isTest ? '编译失败' : '出错'
      void log(`tool fail -> ${name} cmd=${command.slice(0, 100)}`)
      void sendEvent(name)
    } else if (isTest) {
      // 测试命令成功：输出里看得出有失败 → 编译失败，否则 → 测试通过。
      const name = FAILED.test(text) ? '编译失败' : '测试通过'
      void log(`tool ok(test) -> ${name} cmd=${command.slice(0, 100)}`)
      void sendEvent(name)
    }
    // 编译命令成功（退出码 0）什么都不发，和 fish_hook 一致。
  })

  return () => {
    offCreated?.()
    offSubagent?.()
    offApproval?.()
    offQuestion?.()
    offTools?.()
    clearAllThink()
    for (const d of disposers.values()) d?.()
  }
}
