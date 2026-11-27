using System.Globalization;

namespace AgentIsland.Localization;

/// String lookup with an English key namespace, mirroring the macOS L10n
/// helper. The zh-Hans table is ported from Resources/zh-Hans.lproj; keys
/// missing from the table fall back to the key itself (English).
public static class L10n
{
    public enum Language
    {
        Auto,
        English,
        SimplifiedChinese,
    }

    public static Language Current { get; set; } = Language.Auto;

    private static readonly AsyncLocal<EnglishScope?> _english = new();

    /// Within the returned scope this thread localizes to English, whatever
    /// the app language: the pixel panels set their text in a Latin pixel face.
    /// The scope belongs to the code that opened it, on its thread: an async
    /// handler that holds it across an await has it again when it resumes,
    /// but nothing else the thread runs meanwhile does, and scopes closed out
    /// of order can't leave it stuck.
    public static IDisposable English()
    {
        var scope = new EnglishScope(_english.Value);
        _english.Value = scope;
        return scope;
    }

    private sealed class EnglishScope(EnglishScope? previous) : IDisposable
    {
        public int Thread { get; } = Environment.CurrentManagedThreadId;
        public bool Open { get; private set; } = true;

        public void Dispose()
        {
            Open = false;
            _english.Value = previous;
        }
    }

    private static bool InEnglish => _english.Value is { Open: true } scope && scope.Thread == Environment.CurrentManagedThreadId;

    public static bool IsChinese => !InEnglish && Current switch
    {
        Language.SimplifiedChinese => true,
        Language.English => false,
        _ => CultureInfo.CurrentUICulture.Name.StartsWith("zh", StringComparison.OrdinalIgnoreCase),
    };

    public static string Tr(string key)
    {
        if (IsChinese) return ChineseTable.TryGetValue(key, out var value) ? value : key;
        // The English layer mirrors macOS en.lproj: keys stay historical so
        // call sites never churn, while display copy evolves (trailing full
        // stops stripped, Thread→Session, AgentIsland→Agent Island).
        return EnglishTable.TryGetValue(key, out var english) ? english : key;
    }

    private static Dictionary<string, string>? _keysByChinese;

    /// Text some store localized when it was set, as it reads now: a Chinese
    /// caption shown inside English() turns back into its English key's copy.
    /// Text that was not a table entry comes back unchanged.
    public static string Relocalize(string text)
    {
        if (IsChinese) return text;
        var keys = _keysByChinese ??= ChineseTable
            .GroupBy(entry => entry.Value)
            .ToDictionary(group => group.Key, group => group.First().Key);
        return keys.TryGetValue(text, out var key) ? Tr(key) : text;
    }

    /// Composite-format lookup: the key is an English format string with
    /// {0}-style holes; the zh table carries the translated format.
    public static string TrFormat(string key, params object[] args) =>
        string.Format(Tr(key), args);

    /// Grows as features land; seeded with the strings the shell needs.
    /// The zh table, for the test that scans it.
    internal static IReadOnlyDictionary<string, string> ChineseEntries => ChineseTable;

    private static readonly Dictionary<string, string> ChineseTable = new()
    {
        ["Settings…"] = "设置…",
        ["Quit Limen"] = "退出 Limen",
        ["idle"] = "空闲",
        ["running"] = "正在跑",
        ["your turn"] = "该你回复",
        ["stalled"] = "暂停",
        ["rate limited"] = "查询暂停",
        ["auth required"] = "需要登录",
        ["Continue"] = "继续",
        ["Usage refresh failed"] = "用量刷新失败",
        ["Claude stale"] = "Claude 数据过期",
        ["Codex stale"] = "Codex 数据过期",
        ["5h"] = "5 小时",
        ["week"] = "周",
        ["Week"] = "周",
        ["Usage"] = "用量",
        ["Cost"] = "成本",
        ["Overview"] = "概览",
        ["Triggers"] = "自动续跑",
        ["today"] = "今天",
        ["{0} this month"] = "本月 {0}",
        ["Manage"] = "管理",
        ["Settings"] = "设置",
        ["General"] = "通用",
        ["Display"] = "显示器",
        ["Providers"] = "服务",
        ["License"] = "许可证",
        ["Status"] = "状态说明",
        ["Language"] = "语言",
        ["Launch at login"] = "登录时启动",
        ["Refresh"] = "刷新",
        ["Accounts"] = "账户",
        ["OK"] = "好",
        ["Later"] = "以后再说",
        ["Quota shows"] = "额度显示",
        ["Used"] = "已用",
        ["Remaining"] = "剩余",
        // macOS zh-Hans: the Tokens section header reads "Token" (singular,
        // capital T) — en keeps "Tokens".
        ["Tokens"] = "Token",
        ["Weekly"] = "周报",
        ["Monthly"] = "月报",
        ["Others"] = "其他",
        ["less"] = "少",
        ["more"] = "多",
        ["expired"] = "已过期",
        ["Sound"] = "声音",
        ["this month"] = "本月",
        ["Claude Code CLI not found. Log in from a terminal with: claude /login"] = "未找到 Claude Code CLI。请在终端中运行 claude /login 登录",
        ["Codex CLI not found. Log in from a terminal with: codex login"] = "未找到 Codex CLI。请在终端中运行 codex login 登录",
        ["Retry"] = "重试",
        // Settings window (macOS-parity copy)
        ["Updates"] = "更新",
        ["Refresh interval"] = "刷新间隔",
        // No second ["Cost"] entry: these are indexer assignments, so a repeat
        // silently overwrites the earlier one. "Cost" is already the island's
        // page tab above ("成本", macOS zh-Hans.lproj) — re-declaring it for a
        // report-card label renamed the tab.
        ["Share"] = "占比",
        ["Value"] = "价值",
        ["Calm"] = "清爽",
        ["network drop"] = "网络中断,已保留上次数据",
        ["Warning"] = "警告",
        ["Critical"] = "危急",
        ["Check"] = "检查",
        ["Quit"] = "退出",
        ["Screen"] = "屏幕",
        ["Auto"] = "自动",
        ["All tokens"] = "全部 token",
        ["Input + output"] = "输入 + 输出",
        ["synced {0}"] = "已同步 {0}",
        ["Open"] = "打开",
        ["AUTO"] = "自动",
        // P4: visual pickers + exact zh copy from the shipped app
        ["Ring"] = "环形",
        ["Bar"] = "柱状",
        ["Stepped"] = "阶梯",
        ["Numeric"] = "数字",
        // Report donut hole (macOS v4) + pager labels
        ["the primary display"] = "主显示器",
        // ["Top bar"] lives once, above with the display section. These are
        // indexer assignments, so a second row would silently win and a later
        // edit to the first would vanish.
        ["Token counting"] = "Token 统计",
        // Status guide (exact zh-Hans.lproj keys and values)
        ["Running"] = "正在跑",
        ["Working"] = "正在跑",
        ["Your turn"] = "该你回复",
        ["Needs attention"] = "需要处理",
        // No "Subagent alarms" rows: the toggle is gone. SessionScanner now
        // drops subagent transcripts unconditionally (macOS parity), so the
        // setting had nothing left to switch and its strings were dead.
        // Inline new-rule form (exact zh-Hans.lproj keys and values)
        ["Tool"] = "服务",
        ["Thread"] = "要续跑的会话",
        ["Message"] = "消息",
        ["When"] = "时机",
        ["Change"] = "更换",
        ["Live"] = "实时",
        ["Rate"] = "查询暂停",
        ["Auth"] = "登录",
        // Claude sign-in: the loopback callback page, the failure reason the
        // caller now surfaces instead of silently spawning a terminal, and
        // the paste-code fallback dialog.
        ["Sign in"] = "登录",
        ["Cut"] = "剪切",
        ["Copy"] = "复制",
        ["Cancel"] = "取消",
        // Five-provider settings rows (2.x). The %d/%@ holes of the macOS
        // strings become {0} here; the copy itself is byte-identical.
        ["Next"] = "下一页",
        ["Back"] = "上一页",
        ["Notes"] = "版本说明",
        ["About"] = "关于",
        ["View"] = "查看",
        ["Monitor"] = "监视器",
        ["Not detected — run antigravity once"] = "未检测到 — 先运行一次 antigravity",
        ["Not detected — run grok once, or open Grok Build desktop (Cursor) once"] = "未检测到 — 先运行一次 grok，或打开一次 Grok Build 桌面版（Cursor）",
        ["Not detected — open Grok Build desktop (Cursor) once"] = "未检测到 — 先打开一次 Grok Build 桌面版（Cursor）",
        ["Not detected — open Kimi once"] = "未检测到 — 先打开一次 Kimi",
        ["Not detected — open DeepSeek Harness once"] = "未检测到 — 先打开一次 DeepSeek Harness",
        ["Save"] = "保存",
        // Guest-provider status captions. Values are the shipped zh-Hans.lproj
        // strings; without these rows the guest strips fell back to English
        // inside an otherwise Chinese panel.
        ["24h"] = "24 小时",

        // Side drawer: placement, live phases, character menu.
        ["Show drawer"] = "显示抽屉",
        ["Hide drawer"] = "隐藏抽屉",
        ["{0}% left"] = "剩 {0}%",
        ["Thinking"] = "思考中",
        ["Writing"] = "输出中",
        ["Reading files"] = "读文件",
        ["Searching code"] = "搜代码",
        ["Searching the web"] = "上网查资料",
        ["Editing code"] = "改代码",
        ["Running commands"] = "跑命令",
        ["Operating apps"] = "操作界面",
        ["Starting a subagent"] = "派子任务",
        ["Waiting on subagents"] = "等子任务",
        ["Sending a message"] = "发消息",
        ["Asking you"] = "在问你",
        ["Compacting context"] = "压缩上下文",
        ["Waiting for your reply"] = "等你回复",
        ["Something went wrong"] = "出错了",
        ["Network trouble"] = "网络异常",
        ["No internet connection"] = "没有网络连接",
        ["Reconnecting · attempt {0} of {1}"] = "正在重连 · 第 {0}/{1} 次",
        ["Interrupted by a rate limit"] = "被限流打断",
        ["Sign-in needed"] = "需要登录",
        ["Idle"] = "空闲",
        ["Resting"] = "休息中",
        ["Sleeping"] = "睡觉中",
        ["App closed"] = "应用已关闭",
        ["Grok and Grok Build desktop closed"] = "Grok 和 Grok Build 桌面版都已关闭",
        ["Grok, Grok\u00A0Bot and Grok Build desktop closed"] = "Grok、Grok\u00A0Bot 和 Grok Build 桌面版都已关闭",
        ["Minimized"] = "已最小化",
        ["In the tray"] = "在托盘中",
        ["Shape"] = "形状",
        ["Color"] = "颜色",
        ["Dock to the left edge"] = "贴到屏幕左边",
        ["Dock to the right edge"] = "贴到屏幕右边",
        ["Blob"] = "圆团",
        ["Hex"] = "六边形",
        ["Gem"] = "宝石",
        ["Arch"] = "拱门",
        ["Cloud"] = "云朵",
        ["Square"] = "方块",
        ["Heart"] = "爱心",
        ["Star"] = "星星",
        ["Black"] = "黑色",
        ["Red"] = "红色",
        // WinUI settings, drawer card and menus (2026-10-11).
        ["Agents"] = "智能体",
        ["Drawer"] = "抽屉",
        ["Usage & cost"] = "用量与成本",
        ["Usage & cost…"] = "用量与成本…",
        ["On"] = "开",
        ["Off"] = "关",
        ["Quota, tokens and live activity for every coding agent on this computer, in the drawer's order. Drag a card by its grip or use its arrows to move it; switch one off to hide it everywhere."] = "这台电脑上每个编程智能体的配额、Token 和实时动态，按抽屉里的顺序排列。拖动卡片左侧的手柄或用箭头调整顺序；关掉开关即可在各处隐藏它。",
        ["Drag to reorder"] = "拖动以调整顺序",
        ["Right now"] = "现在",
        ["active {0}"] = "{0}活跃",
        ["5-hour window"] = "5 小时额度",
        ["Weekly window"] = "每周额度",
        ["Daily window"] = "每日额度",
        ["Resets {0}"] = "{0}重置",
        ["Sign in again"] = "重新登录",
        ["Waiting for login…"] = "等待登录…",
        ["Local sessions only — no quota to sync"] = "只读取本机会话——没有可同步的配额",
        ["Waiting for the first sync"] = "等待首次同步",
        ["Awaiting sync"] = "等待同步",
        ["Updates after you next use Codex"] = "用一次 Codex 后更新",
        ["Not detected — install Claude Code and sign in"] = "未检测到——请安装 Claude Code 并登录",
        ["Not detected — install the Codex CLI and sign in"] = "未检测到——请安装 Codex CLI 并登录",
        ["Opens a terminal running claude /login"] = "会打开终端运行 claude /login",
        ["Opens a terminal running codex login"] = "会打开终端运行 codex login",
        ["Sign in with the antigravity CLI or the Antigravity app"] = "请用 antigravity CLI 或 Antigravity 应用登录",
        ["Sign in with the grok CLI, or inside Grok Build desktop (Cursor)"] = "请用 grok CLI 登录，或在 Grok Build 桌面版（Cursor）里登录",
        ["Sign in inside Grok Build desktop (Cursor)"] = "请在 Grok Build 桌面版（Cursor）里登录",
        ["Read from DeepSeek Harness's local sessions; no sign-in needed"] = "读取 DeepSeek Harness 的本机会话，无需登录",
        ["The column of characters on the edge of your screen. Hover one for its status, click it for its settings, drag it to move the drawer, right-click it for options."] = "屏幕边缘那一列小角色。悬停查看状态，单击打开它的设置，上下拖动移动抽屉，右键打开选项。",
        ["Placement"] = "位置",
        ["Edge"] = "停靠边",
        ["Which side of the screen the drawer docks to"] = "抽屉停靠在屏幕的哪一边",
        ["Right edge"] = "右边",
        ["Left edge"] = "左边",
        ["Automatic (primary display)"] = "自动（主显示器）",
        ["Display {0}"] = "显示器 {0}",
        ["primary"] = "主显示器",
        ["A pinned display falls back to automatic while it is unplugged"] = "指定的显示器拔掉后会自动回到主显示器",
        ["Position"] = "高度",
        ["Drag any character up or down to move the drawer"] = "上下拖动任意角色即可移动抽屉",
        ["Center on the edge"] = "回到中间",
        ["How the bars and percentages read on the drawer's cards and in Settings"] = "抽屉卡片和设置里的进度条、百分比按哪种方式显示",
        ["Motion"] = "动效",
        ["Animation"] = "动画",
        ["With Windows animation effects off, characters slow down and stop following the pointer"] = "关闭 Windows 动画效果后，角色会放慢，也不再跟随指针",
        ["Windows animation settings"] = "Windows 动画设置",
        ["Quota"] = "配额",
        ["Providers rate-limit their usage endpoints, so nothing shorter than five minutes is offered"] = "各家的用量接口都有频率限制，所以最短只提供 5 分钟",
        ["Every {0} minutes"] = "每 {0} 分钟",
        ["All tokens (including cache)"] = "全部 Token（含缓存）",
        ["Input + output (excluding cache reads)"] = "输入 + 输出（不含缓存读取）",
        ["Excludes cache reads; new input written to cache still counts. History is kept."] = "只排除缓存读取，首次写入缓存的新输入仍计入；历史记录保留。",
        ["{0} · Local time zone: {1}"] = "{0} · 本机时区：{1}",
        ["Today starts at this computer's midnight. Previous days stay in history."] = "今天从本机午夜开始累计，之前的数据仍保留在历史中。",
        ["Snapshot dates differ; scan to refresh them together."] = "部分智能体的统计日期不同，可立即扫描以同步更新。",
        ["Last scanned {0}"] = "上次扫描：{0}",
        ["Not scanned yet"] = "还没扫描",
        ["Characters"] = "角色",
        ["Character style"] = "角色风格",
        ["Lock position"] = "固定位置",
        ["Locked"] = "已固定",
        ["Unlocked"] = "未固定",
        ["Characters stay put: dragging won't move the drawer, and a character appearing or leaving won't shift the ones above it"] = "角色保持不动：拖动不会移动抽屉，角色增减时上方的角色也不会移位",
        ["Shown"] = "显示",
        ["Show {0}"] = "显示 {0}",
        ["Shown agents"] = "显示的智能体",
        ["Hidden"] = "隐藏",
        ["Show in the drawer, its menus and Usage & cost"] = "在抽屉、菜单和用量与成本页中显示",
        ["Move up"] = "上移",
        ["Move down"] = "下移",
        ["Locked in place.\nRight-click to unlock."] = "已经固定住啦。\n右键可以解锁。",
        ["Pixel bots drawn for each agent, or the classic shapes you can recolour"] = "为每个智能体设计的像素小人，或可以换色的经典形状",
        ["Pixel"] = "像素",
        ["Classic"] = "经典",
        ["Quota updated {0}"] = "额度更新于 {0}",
        ["Scan now"] = "立即扫描",
        ["Startup"] = "启动",
        ["Start Limen when you sign in to Windows"] = "登录 Windows 时自动启动 Limen",
        ["App language"] = "应用语言",
        ["Same as Windows"] = "跟随 Windows",
        ["Thanks"] = "致谢",
        ["The original app, by {0} · MIT License"] = "原版应用，作者 {0} · MIT 许可证",
        ["The pixel font, by {0} · SIL Open Font License 1.1"] = "像素字体，作者 {0} · SIL 开放字体许可证 1.1",
        ["The pixel font for Chinese, by {0} · SIL Open Font License 1.1"] = "中文像素字体，作者 {0} · SIL 开放字体许可证 1.1",
        ["Crash log"] = "崩溃日志",
        ["Open folder"] = "打开文件夹",
        ["Closes the drawer and the tray icon"] = "关闭抽屉和托盘图标",
        ["Estimated at API list prices from this computer's session logs — not a bill."] = "按 API 公开价格，根据本机会话日志估算——不是账单。",
        ["Frame rate while an agent works"] = "智能体工作时的帧率",
        ["Calm characters always breathe at 30 fps"] = "空闲的角色始终以 30 fps 呼吸",
        ["Match the display"] = "跟随显示器刷新率",
        ["{0} fps"] = "{0} fps",
        ["Save battery"] = "省电",
        ["In full screen"] = "全屏时显示",
        ["Keep the characters on screen while a full-screen app is in front"] = "全屏应用在前台时，角色也继续显示",
        ["On battery, characters run at 60 fps at most"] = "使用电池时，角色最高 60 fps",
        ["Month"] = "月",
        ["Monthly window"] = "每月额度",
        ["open Kimi Code to sync its plan usage"] = "打开 Kimi Code 即可同步套餐用量",
        ["Month:"] = "本月：",
        ["Kimi Code {0}% · Kimi app {1}%"] = "Kimi Code {0}% · Kimi 应用 {1}%",
        ["Booster balance {0}"] = "加油包余额 {0}",
        ["Sign in inside Kimi Code; its plan usage syncs whenever the app or the CLI signs in"] = "请在 Kimi Code 里登录；应用或 CLI 每次登录时都会同步套餐用量",
        ["Waiting for your approval"] = "等你批准",
        ["Permission prompts"] = "权限确认",
        ["Shows when Claude Code waits for you to approve a tool. Adds a small hook to Claude Code's settings.json that records only the session and the prompt type; sessions started afterwards pick it up."] = "Claude Code 等你批准工具时，角色会显示出来。会在 Claude Code 的 settings.json 加一个小 hook，只记录会话和提示类型；之后新开的会话生效。",
        ["Couldn't change Claude Code's settings"] = "无法修改 Claude Code 的设置",
        ["Couldn't change Grok's hooks"] = "无法修改 Grok 的 hook",
        ["Shows when Grok waits for you to approve a tool. Adds one hook file, agentisland-notify.json, to ~/.grok/hooks that records only the session and the prompt type; sessions started afterwards pick it up."] = "Grok 等你批准工具时，角色会显示出来。会在 ~/.grok/hooks 加一个 hook 文件 agentisland-notify.json，只记录会话和提示类型；之后新开的会话生效。",
        ["A file named agentisland-notify.json is already in ~/.grok/hooks and isn't Limen's, so this stays off."] = "~/.grok/hooks 里已有一个叫 agentisland-notify.json 的文件，不是 Limen 的，所以这个开关保持关闭。",
        ["Tasks"] = "任务",
        ["{0} running"] = "{0} 个运行中",
        ["Character lines"] = "角色台词",
        ["A character speaks up in a bubble when a session is done, asks you something, needs your approval or hits an error"] = "会话完成、向你提问、需要你批准或出错时，角色会在气泡里说一声",
        ["Done"] = "完成",
        ["Asking"] = "提问",
        ["Needs OK"] = "待批准",
        ["Question"] = "提问",
        ["Approval"] = "批准",
        ["Error"] = "出错",
        ["{0} sessions need you."] = "{0} 个会话在等你",
        ["+{0} more"] = "另有 {0} 个",
        ["A scheduled task couldn't run."] = "有个计划任务没能运行。",
        ["Done — your turn!"] = "做完啦，该你了！",
        ["All set. Take a look?"] = "搞定了，来看看？",
        ["Finished. Waiting on you."] = "这轮结束了，等你哦",
        ["Can I run this? Needs your OK."] = "这一步要你点头才能继续",
        ["Waiting for your approval."] = "等你批准一下～",
        ["Quick question for you."] = "有个问题想问你",
        ["I need your input."] = "需要你拿个主意",
        ["Something broke…"] = "出错了……",
        ["Hit an error."] = "遇到报错了",
        ["My connection dropped."] = "网好像断了",
        ["Can't reach the server — retrying."] = "连不上服务器，正在重试",
        ["Please sign me in again."] = "需要你帮我重新登录一下",
        ["Today"] = "今天",
        ["This month"] = "本月",
        ["Last 7 days"] = "近 7 天",
        ["Estimated cost"] = "估算花费",
        ["At API list prices"] = "按 API 公开价格",
        ["{0} models have no price and count as $0"] = "{0} 个模型没有价格，按 $0 计",
        ["{0} model has no price and counts as $0"] = "{0} 个模型没有价格，按 $0 计",
        ["Busiest agent"] = "最忙的智能体",
        ["No activity yet"] = "还没有活动",
        ["{0} tokens"] = "{0} Token",
        ["Grok and Grok Build desktop"] = "Grok 和 Grok Build 桌面版",
        ["Models"] = "模型",
        ["Models, last 5 hours"] = "模型（最近 5 小时）",
        ["no price"] = "未定价",
        ["free"] = "免费",
        // 第 9 批 Usage 页：按模型的明细、未定价、额度。
        ["Unpriced"] = "未定价",
        ["partly unpriced"] = "部分未定价",
        ["{0} model unpriced, not in the total"] = "{0} 个模型未定价，不计入合计",
        ["{0} models unpriced, not in the total"] = "{0} 个模型未定价，不计入合计",
        ["Prices as of {0}."] = "价格截至 {0}。",
        ["Input {0}"] = "输入 {0}",
        ["Output {0}"] = "输出 {0}",
        ["Cache write {0}"] = "缓存写入 {0}",
        ["Cache read {0}"] = "缓存读取 {0}",
        ["K2 models aren't listed; their tokens count in the totals"] = "K2 模型不单列，token 计入合计",
        ["Each app's own quota report, refreshed on the interval below"] = "各家官方程序自己报的额度，按下面的刷新间隔更新",
        ["No quota to show yet. Claude, Codex and Kimi show theirs here once detected and synced."] = "暂时没有额度可显示。检测到 Claude、Codex 或 Kimi 并同步后，会显示在这里。",
        ["No sessions in this period"] = "这段时间没有会话",
        ["No sessions in the last 5 hours"] = "最近 5 小时没有会话",
        ["Tokens per hour (unpriced)"] = "每小时 Token（未定价）",
        ["Tokens per day (unpriced)"] = "每天 Token（未定价）",
        ["Antigravity has no conversations to count yet."] = "Antigravity 还没有可统计的对话。",
        ["Model not named"] = "未命名的模型",
        ["Memory (background)"] = "记忆（后台）",
        ["Mail"] = "邮件",
        ["You've got mail."] = "有新邮件。",
        ["A message came in."] = "来消息了。",
        ["See its quota on the Agents page"] = "在「智能体」页查看它的配额",
        // The look pickers: each look's name (Classic's and Pixel's; Pixel shows English).
        ["Look"] = "外观",
        ["Succulent"] = "多肉盆栽",
        ["Clay timer"] = "陶土计时器",
        ["Desk computer"] = "台式电脑",
        ["Coffee mug"] = "咖啡杯",
        ["Keycap"] = "键帽",
        ["Cloud cushion"] = "云朵靠垫",
        ["Cloud terminal"] = "云朵终端",
        ["Flying saucer"] = "飞碟",
        ["Paper plane"] = "纸飞机",
        ["Crowned arch"] = "王冠拱门",
        ["Ringed planet"] = "带环行星",
        ["Orbit ring"] = "轨道环",
        ["Astronaut helmet"] = "宇航员头盔",
        ["Moon lamp"] = "月亮灯",
        ["Moon rabbit"] = "月兔",
        ["Moonshot rocket"] = "登月火箭",
        ["Note clip"] = "便签夹",
        ["Submarine"] = "潜水艇",
        ["Whale stapler"] = "鲸鱼订书机",
        ["Tape whale"] = "鲸鱼胶带座",
        ["Clawd"] = "Clawd",
        ["Notebook"] = "笔记本",
        ["Puff cloud"] = "蓬蓬云",
        ["Folio"] = "书页",
        ["Scroll"] = "卷轴",
        ["Eraser"] = "纸套橡皮",
        ["Balloon"] = "热气球",
        ["Rainbow A"] = "彩虹 A",
        ["Aeri"] = "Aeri",
        ["Blocks"] = "积木",
        ["Kite"] = "风筝",
        ["Owl"] = "猫头鹰",
        ["Sea otter"] = "海獭",
        ["{0}. What {1} wears in the drawer, on its card and in menus"] = "{0} · {1} 在抽屉、卡片和菜单里的样子",
        ["{0}. What {1} wear in the drawer, on its card and in menus"] = "{0} · {1} 在抽屉、卡片和菜单里的样子",
        ["Waiting"] = "等待中",
        ["Sharpener"] = "黑洞卷笔刀",
        ["Slashed ring"] = "斜线圆环",
        ["Ring bot"] = "圆环机器人",
        ["Event horizon"] = "黑洞",
        ["Black cat"] = "黑猫",
        ["Astronaut"] = "宇航员",
        ["Martian"] = "火星人",
        ["Blob and terminal"] = "小团子",
        ["Terminal pal"] = "终端小屏",
        ["Moonlet"] = "小月牙",
        ["Rocket"] = "火箭",
        ["Whale"] = "鲸鱼",
        // 悬浮卡的任务列表（第 7 批）。带“(short)”“(card)”的键只给卡用，英文显示见 EnglishTable。
        // 状态词：完整说法（主对话第二行）和短词（子任务右侧、放不下时），文案见设计稿 §5.1。
        ["Thinking (card)"] = "思考中",
        ["Writing (card)"] = "写回复",
        ["Reading files (card)"] = "读文件",
        ["Searching code (card)"] = "搜代码",
        ["Searching the web (card)"] = "上网查资料",
        ["Editing code (card)"] = "改代码",
        ["Running commands (card)"] = "跑命令",
        ["Operating apps (card)"] = "操作应用",
        ["Starting a subagent (card)"] = "起子任务",
        ["Waiting on subagents (card)"] = "等子任务",
        ["Sending a message (card)"] = "发消息",
        ["Compacting context (card)"] = "压缩上下文",
        ["Working (card)"] = "在跑",
        ["Asking you (card)"] = "在问你",
        ["Waiting for your approval (card)"] = "等你批准",
        ["Waiting for your reply (card)"] = "等你回复",
        ["Something went wrong (card)"] = "出错",
        ["Network trouble (card)"] = "网络中断",
        ["Sign-in needed (card)"] = "需要重新登录",
        ["Thinking (short)"] = "思考中",
        ["Writing (short)"] = "写回复",
        ["Reading (short)"] = "读文件",
        ["Searching (short)"] = "搜代码",
        ["Web (short)"] = "查资料",
        ["Editing (short)"] = "改代码",
        ["Running commands (short)"] = "跑命令",
        ["Apps (short)"] = "操作应用",
        ["Spawning (short)"] = "起子任务",
        ["Waiting on subagents (short)"] = "等子任务",
        ["Sending (short)"] = "发消息",
        ["Compacting (short)"] = "压缩中",
        ["Working (short)"] = "在跑",
        ["Asking you (short)"] = "在问你",
        ["Needs OK (short)"] = "等你批准",
        ["Your turn (short)"] = "等你回复",
        ["Done (short)"] = "完成",
        ["Failed (short)"] = "出错",
        ["Offline (short)"] = "断网",
        ["Sign in (short)"] = "要登录",
        ["Mail (short)"] = "有消息",
        ["5h (card)"] = "5h",
        ["Wk"] = "周",
        ["Cache"] = "缓存",
        ["syncing"] = "同步中",
        ["Total"] = "合计",
        ["{0} need you"] = "{0} 个在等你",
        ["{0} failed (tasks)"] = "{0} 个出错",
        ["{0} running (tasks)"] = "{0} 个在跑",
        ["All idle"] = "都空闲",
        ["+{0} more conversations"] = "+{0} 个对话",
        ["+{0} subtasks"] = "+{0} 个子任务",
        ["+{0} subtasks · {1} running"] = "+{0} 个子任务 · {1} 个在跑",
        ["Untitled conversation"] = "未命名对话",
        ["Subtask · {0}"] = "子任务 · {0}",
        ["New mail"] = "有新消息",
        ["{0} new messages"] = "{0} 条新消息",
        ["Reading sessions…"] = "正在读取会话…",
    };
    /// English display overrides, mirroring macOS en.lproj where key and
    /// value diverge: the key namespace is frozen history, the values carry
    /// the copy sweep (no trailing full stops, Thread->Session wording,
    /// "Agent Island" spelled as two words).
    private static readonly Dictionary<string, string> EnglishTable = new()
    {
        ["stalled"] = "paused",
        ["rate limited"] = "usage check paused",
        ["auth required"] = "login required",
        ["Triggers"] = "Auto Resume",
        ["today"] = "Today",
        ["Status"] = "Status guide",
        ["Launch at login"] = "Launch at Login",
        ["All tokens"] = "All Tokens",
        ["AUTO"] = "Auto",
        ["Tool"] = "Provider",
        ["Thread"] = "Session to resume",
        ["Rate"] = "Usage check",
        ["network drop"] = "network drop — showing last data",
        // No ["Cost"] override: it is the island's page tab, which macOS
        // en.lproj renders "Cost". SectionLabel uppercases on its own, so a
        // report-card all-caps entry here bought nothing and renamed the tab.
        ["Share"] = "SHARE",
        ["Thinking (card)"] = "Thinking",
        ["Writing (card)"] = "Writing",
        ["Reading files (card)"] = "Reading files",
        ["Searching code (card)"] = "Searching code",
        ["Searching the web (card)"] = "Searching the web",
        ["Editing code (card)"] = "Editing code",
        ["Running commands (card)"] = "Running commands",
        ["Operating apps (card)"] = "Operating apps",
        ["Starting a subagent (card)"] = "Starting a subagent",
        ["Waiting on subagents (card)"] = "Waiting on subagents",
        ["Sending a message (card)"] = "Sending a message",
        ["Compacting context (card)"] = "Compacting context",
        ["Working (card)"] = "Working",
        ["Asking you (card)"] = "Asking you",
        ["Waiting for your approval (card)"] = "Waiting for your approval",
        ["Waiting for your reply (card)"] = "Waiting for your reply",
        ["Something went wrong (card)"] = "Something went wrong",
        ["Network trouble (card)"] = "Network trouble",
        ["Sign-in needed (card)"] = "Sign-in needed",
        ["Thinking (short)"] = "Thinking",
        ["Writing (short)"] = "Writing",
        ["Reading (short)"] = "Reading",
        ["Searching (short)"] = "Searching",
        ["Web (short)"] = "Web",
        ["Editing (short)"] = "Editing",
        ["Running commands (short)"] = "Running",
        ["Apps (short)"] = "Apps",
        ["Spawning (short)"] = "Spawning",
        ["Waiting on subagents (short)"] = "Waiting",
        ["Sending (short)"] = "Sending",
        ["Compacting (short)"] = "Compacting",
        ["Working (short)"] = "Working",
        ["Asking you (short)"] = "Asking you",
        ["Needs OK (short)"] = "Needs OK",
        ["Your turn (short)"] = "Your turn",
        ["Done (short)"] = "Done",
        ["Failed (short)"] = "Failed",
        ["Offline (short)"] = "Offline",
        ["Sign in (short)"] = "Sign in",
        ["Mail (short)"] = "Mail",
        ["5h (card)"] = "5h",
        ["{0} running (tasks)"] = "{0} running",
        ["{0} failed (tasks)"] = "{0} failed",
    };
}
