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
const PUSH = /\bgit\s+push\b/i
const BRIEF_GAP = 18 * 1000 // 进度简报：两次步骤简报至少隔这么久，没到点的攒着只说最新的（和 Claude 的 fish_hook.py 一样）
const BEAT_AFTER = 60 * 1000 // 一条命令跑这么久还没完，报一次“仍在进行”
const THINK_TIMEOUT = 3 * 60 * 1000 // 收到消息 3 分钟后这一轮还没做完、用户也没再发新消息，算「思考超时」（用户 2026-11-14：30 秒改成 5 分钟又改成 3 分钟，并去掉“没有工具调用”这个条件）

export function apply(ctx, config) {
  // 配置里没填 / 还留着 <插件完整路径> 占位符时，用插件自己旁边的 api.json（hooks/dsh/dafeiyu-hooks → ../../../api.json）
  const configured = typeof config?.apiPath === 'string' ? config.apiPath.trim() : ''
  const apiPath = configured && !configured.includes('<插件完整路径>') ? configured : fileURLToPath(new URL('../../../api.json', import.meta.url))
  const logPath = new URL('./hook.log', import.meta.url) // 调试用：最近转发了什么、桌宠怎么回的

  async function log(msg) {
    try { await appendFile(logPath, `${new Date().toISOString()} ${msg}\n`, 'utf-8') } catch {}
  }

  // vars：进度简报的占位符（{step} 这类），桌宠按 events.txt 的模板填进去（用户 2026-11-16）
  // attempt：开工/收到 撞上她在说话时重试用（本机加，2026-11-15）
  async function sendEvent(eventName, vars, attempt = 0) {
    try {
      const api = JSON.parse(await readFile(apiPath, 'utf-8'))
      const res = await fetch(`${api.url}/call/event`, {
        method: 'POST',
        headers: {
          Authorization: `Bearer ${api.token}`,
          'Content-Type': 'application/json',
          'X-FishTray': '1',
        },
        body: JSON.stringify(vars ? { name: eventName, vars } : { name: eventName }),
      })
      const text = await res.text()
      await log(`sent ${eventName}${vars ? ' ' + JSON.stringify(vars) : ''}${attempt ? ` retry ${attempt}` : ''}: ${text.slice(0, 160)}`)
      // 开工/收到 撞上她在说话时会被丢；等一两秒再试，别把开场白吞掉（本机加，2026-11-15）
      if (attempt < 4 && (eventName === '收到' || eventName === '开工')) {
        let reason = ''
        try {
          const outer = JSON.parse(text)
          const inner = JSON.parse(outer?.content?.[0]?.text || '{}')
          reason = String(inner?.reason || '')
        } catch {}
        if (reason.includes('她在说话') || reason.includes('她在忙别的')) {
          await new Promise((resolve) => setTimeout(resolve, 1800))
          return sendEvent(eventName, vars, attempt + 1)
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
  const toolStarts = new Map() // 进度简报（用户 2026-11-16）：callId -> 命令开始的毫秒时间，算耗时用
  const briefs = new Map() // agentId -> { plan, spoken, last, timer, turnPlan }：任务清单和“上次说过的清单”
  const beats = new Map() // callId -> { timer }：命令的心跳计时器

  // ---- 进度简报：接上台词（用户 2026-11-16）。规则、节奏和 Claude 的 fish_hook.py 完全一样 ----
  // 步骤：todo/write 每次是整份清单，和“上次说过的”比：新清单 -> 步骤开始；有步骤做完 -> 步骤完成；只换了正在做的 -> 步骤接续；全做完 -> 步骤全完。
  //       两次至少隔 BRIEF_GAP，没到点的攒着，到点只说最新的状态。只有清单两步以上才报；子代理不报。
  // 阶段：命令开始前认得出编译、测试、推送就报（桌宠那边冷却 45 秒）。心跳：命令跑 BEAT_AFTER 还没完，报一次“仍在进行”。
  // 收尾：这一轮的清单全做完了，turn-stopping 就不再发“完成”。每报一次步骤或心跳，思考超时重新数 3 分钟（思考一分钟不动）。
  function cleanName(text, limit = 16) {
    const raw = String(text ?? '').replace(/\s+/g, ' ').trim()
    let t = raw.replace(/[（(][^（）()]*[）)]/g, '').replace(/[（(][^）)]*$/, '')
    t = t.replace(/\s+/g, ' ').replace(/^[\s：:，,。、]+|[\s：:，,。、]+$/g, '') || raw
    return t.length <= limit ? t : t.slice(0, limit - 1).trimEnd() + '…'
  }
  function briefOfAgent(id) {
    if (!briefs.has(id)) briefs.set(id, { plan: [], spoken: null, last: 0, timer: null, turnPlan: false })
    return briefs.get(id)
  }
  function planNews(spoken, plan) {
    if (plan.length < 2) return null
    const total = plan.length
    const left = plan.filter((p) => p.status !== 'completed').length
    const current = plan.find((p) => p.status === 'in_progress')
    const same = !!spoken && spoken.length === plan.length && spoken.every((s, i) => s.content === plan[i].content)
    if (left === 0) return same && spoken.every((s) => s.status === 'completed') ? null : ['步骤全完', { total: String(total) }]
    if (!same) {
      const first = current || plan.find((p) => p.status !== 'completed')
      const done = total - left // 清单一出现就已经做完几步（先干活后补清单）：台词说“已经做完几步”，不说“第一步”
      return ['步骤开始', { total: String(total), left: String(left), step: cleanName(first.content), ...(done ? { done: String(done) } : { fresh: '' }) }]
    }
    const newly = plan.filter((p, i) => p.status === 'completed' && spoken[i].status !== 'completed')
    if (newly.length) {
      const vars = { step: cleanName(newly[newly.length - 1].content), left: String(left), total: String(total) }
      if (current) vars.next = cleanName(current.content)
      return ['步骤完成', vars]
    }
    const was = spoken.find((s) => s.status === 'in_progress')
    if (current && current.content !== was?.content) return ['步骤接续', { step: cleanName(current.content) }]
    return null
  }
  function briefDue(id) {
    const st = briefOfAgent(id)
    const news = planNews(st.spoken, st.plan)
    if (!news) return
    const wait = BRIEF_GAP - (Date.now() - st.last)
    if (wait > 0) {
      if (!st.timer) st.timer = setTimeout(() => { st.timer = null; briefDue(id) }, wait)
      return
    }
    st.spoken = st.plan.map((p) => ({ ...p }))
    st.last = Date.now()
    void sendEvent(news[0], news[1])
    pushThink(id)
  }
  // 简报时顺延：这一轮还没结束，就只把“思考超时”那个计时器从现在起重新数（思考一分钟不动）
  function pushThink(id) {
    if (!turnStart.has(id)) return
    const timers = thinkTimers.get(id) || []
    if (timers[1]) clearTimeout(timers[1])
    timers[1] = setTimeout(() => { const t = thinkTimers.get(id); if (t) t[1] = null; void sendEvent('思考超时') }, THINK_TIMEOUT)
    thinkTimers.set(id, timers)
  }
  function beat(callId, agentId, desc) {
    beats.delete(callId)
    const st = agentId ? briefOfAgent(agentId) : null
    const current = st?.turnPlan ? st.plan.find((p) => p.status === 'in_progress') : null
    const step = current ? cleanName(current.content) : desc
    if (!step || (st && Date.now() - st.last < BRIEF_GAP)) return
    if (st) st.last = Date.now()
    void sendEvent('仍在进行', { step })
    if (agentId) pushThink(agentId)
  }

  // ---- 进度简报第一步：只记日志（用户 2026-11-16 开工；不发事件、不碰桌宠、不写模型上下文）----
  // 记三样：任务清单（session/event 的 todo/write）、命令开始（tools/pre-execute）、命令结束（tools/result）。
  // pre-execute 是 waterfall，挡在命令前面，所以整段 try/catch、日志一律 void 不等，最后无条件 return next()。
  function briefOf(exec) {
    const a = exec?.arguments
    const desc = a && typeof a === 'object' && typeof a.description === 'string' ? a.description : ''
    const cmd = commandOf(exec).replace(/\s+/g, ' ').slice(0, 80)
    return { desc, cmd }
  }

  function briefStart(exec, where) {
    try {
      const { desc, cmd } = briefOf(exec)
      if (!cmd && !desc) return
      const id = String(exec?.callId ?? '')
      if (id) toolStarts.set(id, Date.now())
      void log(`tool-start where=${where} name=${exec?.name} call=${id} root=${exec?.rootCallId ?? '-'} desc=${desc} cmd=${cmd}`)
    } catch (e) { void log('tool-start 日志出错：' + (e?.message || e)) }
  }

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
        briefOfAgent(agent.id).turnPlan = false
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
            const t = thinkTimers.get(agent.id)
            if (t) t[1] = null
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
        const st = briefs.get(agent.id)
        if (st?.turnPlan && st.plan.length >= 2 && st.plan.every((p) => p.status === 'completed')) {
          return void log('这一轮的清单全做完了，不再发「完成」')
        }
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
        // 进度简报第一步：任务清单（整份快照，content 是模型写的步骤名）
        if (event.type === 'todo/write') {
          const todos = Array.isArray(data.todos) ? data.todos : []
          const brief = todos.map((t) => `${t?.status}:${String(t?.content ?? '').slice(0, 40)}`).join(' | ')
          void log(`todo agent=${agent?.id} n=${todos.length} ${brief}`)
          if (!isRoot(agent)) return // 子代理的清单不报
          const st = briefOfAgent(agent.id)
          st.plan = todos.map((t) => ({ content: String(t?.content ?? ''), status: String(t?.status ?? 'pending') }))
          st.turnPlan = true
          briefDue(agent.id)
          return
        }
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

  // 4. 授权和用户提问是 root 级 waterfall，不是 agent 级；之前挂在 agent 上收不到（本机修，2026-11-15）。
  const offApproval = ctx.on('approval/request', (_req, next) => { void sendEvent('授权'); return next() })
  const offQuestion = ctx.on('user-questions/request', (_req, next) => { void sendEvent('等你'); return next() })

  // 3.5 进度简报第一步：命令开始，根 ctx 上也挂一份（日志里 where=root，和 agent 那份对照看收几遍）。
  // 实测根 ctx 和 agent ctx 都收得到同一条命令，只留根这一份（和 tools/result 挂在一处，好用 callId 配对）。
  // 挡在命令前面：整段 try/catch，发事件一律 void 不等，最后无条件 return next()。
  const offPreRoot = ctx.on('tools/pre-execute', (exec, next) => {
    try {
      briefStart(exec, 'root')
      const command = commandOf(exec)
      const agent = exec?.agent
      if (command && (!agent || isRoot(agent))) {
        const stage = BUILD.test(command) ? '编译中' : TEST.test(command) ? '测试中' : PUSH.test(command) ? '推送中' : null
        if (stage) void sendEvent(stage)
        const callId = String(exec?.callId ?? '')
        const desc = cleanName(briefOf(exec).desc)
        if (callId) beats.set(callId, { timer: setTimeout(() => beat(callId, agent?.id, desc), BEAT_AFTER) })
      }
    } catch (e) { void log('pre-execute 出错：' + (e?.message || e)) }
    return next()
  })

  // 4. tools/result 挂在 root 上（Scoped<ToolsService>，和 subagent/end 一样不是 agent 级）：命令执行完。
  const offTools = ctx.on('tools/result', (exec, result) => {
    try {
      const id = String(exec?.callId ?? '')
      const b = beats.get(id)
      if (b) { clearTimeout(b.timer); beats.delete(id) }
    } catch {}
    const command = commandOf(exec)
    if (!command) {
      try { toolStarts.delete(String(exec?.callId ?? '')) } catch {}
      return
    }
    const { text, exitCode, isError } = outcomeOf(result)
    // 进度简报第一步：命令结束（callId、退出码、从开始到现在的毫秒数）
    try {
      const id = String(exec?.callId ?? '')
      const started = toolStarts.get(id)
      toolStarts.delete(id)
      void log(`tool-end call=${id} name=${exec?.name} exit=${exitCode === null ? '-' : exitCode} ms=${started === undefined ? '-' : Date.now() - started}`)
    } catch (e) { void log('tool-end 日志出错：' + (e?.message || e)) }
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
    offPreRoot?.()
    offTools?.()
    clearAllThink()
    for (const b of beats.values()) clearTimeout(b.timer)
    for (const st of briefs.values()) if (st.timer) clearTimeout(st.timer)
    for (const d of disposers.values()) d?.()
  }
}
