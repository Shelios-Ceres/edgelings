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
    private static readonly Dictionary<string, string> ChineseTable = new()
    {
        ["Settings…"] = "设置…",
        ["Quit Agent Island"] = "退出 Agent Island",
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
        ["5h"] = "5小时",
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
        ["Display"] = "显示",
        ["Providers"] = "服务",
        ["License"] = "许可证",
        ["Status"] = "状态说明",
        ["Language"] = "语言",
        ["Launch at login"] = "登录时启动",
        ["Refresh"] = "刷新",
        ["Accounts"] = "账户",
        ["Remove"] = "移除",
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
        ["Installed — run agy to sign in"] = "已安装 — 运行 agy 登录",
        ["Next"] = "下一页",
        ["Back"] = "上一页",
        ["Notes"] = "版本说明",
        ["About"] = "关于",
        ["View"] = "查看",
        ["Monitor"] = "监视器",
        ["Not detected — sign in with the antigravity CLI"] = "未检测到 — 用 antigravity CLI 登录",
        ["Not detected — sign in with the grok CLI"] = "未检测到 grok CLI 登录",
        ["Not detected — sign in with the grok CLI or inside Cursor"] = "未检测到：请用 grok CLI 或在 Cursor 里登录",
        ["Not detected — sign in inside Cursor"] = "未检测到 — 请在 Cursor 里登录",
        ["Not detected — open Kimi Code once"] = "未检测到 — 先打开一次 Kimi Code",
        ["Not detected — open DeepSeek Harness once"] = "未检测到 — 先打开一次 DeepSeek Harness",
        // Codex account switching
        ["Switch Codex account"] = "切换 Codex 账号",
        ["Auto-switched to {0}"] = "已自动切换到 {0}",
        ["No saved accounts yet"] = "还没有保存的账号",
        ["Remove saved account"] = "删除已保存的账号",
        ["Save current account…"] = "保存当前账号…",
        ["Save current account"] = "保存当前账号",
        ["Give this login a name so you can switch back to it later"] = "给这个登录起个名字，之后可以一键切回来",
        ["work / personal"] = "工作 / 个人",
        ["Auto-switch when exhausted"] = "额度用完自动切换",
        ["Save"] = "保存",
        // Guest-provider status captions. Values are the shipped zh-Hans.lproj
        // strings; without these rows the guest strips fell back to English
        // inside an otherwise Chinese panel.
        ["sign in again — run grok login"] = "登录已失效，运行 grok login 重新登录",
        ["sign in again — open Cursor"] = "需要重新登录 — 打开 Cursor",
        // Tile window labels the guests need: Gemini meters a daily bucket,
        // Cursor a ~30-day billing cycle. Values from zh-Hans.lproj:201-202.
        ["24h"] = "24小时",
        ["30d"] = "30天",

        // Side drawer: placement, live phases, character menu.
        ["Show drawer"] = "显示抽屉",
        ["Hide drawer"] = "隐藏抽屉",
        ["{0}% left"] = "剩 {0}%",
        ["{0} reset cards"] = "重置卡 {0} 张",
        ["{0} reset card"] = "重置卡 {0} 张",
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
        ["Out of quota"] = "额度用完",
        ["Sign-in needed"] = "需要登录",
        ["Idle"] = "空闲",
        ["Resting"] = "休息中",
        ["Sleeping"] = "睡觉中",
        ["App closed"] = "应用已关闭",
        ["Grok and Cursor closed"] = "Grok 和 Cursor 都已关闭",
        ["Grok, Grok\u00A0Bot and Cursor closed"] = "Grok、Grok\u00A0Bot 和 Cursor 都已关闭",
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
        ["Billing cycle"] = "计费周期",
        ["Resets {0}"] = "{0}重置",
        ["Account"] = "账户",
        ["Not saved yet"] = "尚未保存",
        ["Switch"] = "切换",
        ["Sign in again"] = "重新登录",
        ["Waiting for login…"] = "等待登录…",
        ["Local sessions only — no quota to sync"] = "只读取本机会话——没有可同步的配额",
        ["Waiting for the first sync"] = "等待首次同步",
        ["Awaiting sync"] = "等待同步",
        ["Cursor data unavailable"] = "暂时无法读取 Cursor 数据",
        ["Couldn't switch account"] = "无法切换账号",
        ["Switch canceled. Credentials could not be read or safely saved. Check file access and try again."] = "切换已取消：无法读取或安全保存登录凭据。请检查文件访问权限后重试。",
        ["Couldn't save account"] = "无法保存账号",
        ["The current login could not be read or saved. Check file access and try again."] = "无法读取或保存当前登录凭据。请检查文件访问权限后重试。",
        ["Not detected — install Claude Code and sign in"] = "未检测到——请安装 Claude Code 并登录",
        ["Not detected — install the Codex CLI and sign in"] = "未检测到——请安装 Codex CLI 并登录",
        ["Opens a terminal running claude /login"] = "会打开终端运行 claude /login",
        ["Opens a terminal running codex login"] = "会打开终端运行 codex login",
        ["Sign in with the antigravity CLI or the Antigravity app"] = "请用 antigravity CLI 或 Antigravity 应用登录",
        ["Sign in with the grok CLI, and inside the Cursor app for Cursor"] = "请用 grok CLI 登录；Cursor 在 Cursor 应用里登录",
        ["Sign in inside the Cursor app"] = "请在 Cursor 应用里登录",
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
        ["Locked in place.\nRight-click to unlock."] = "已经固定住啦，右键可以解锁",
        ["Pixel bots drawn for each agent, or the classic shapes you can recolour"] = "为每个智能体设计的像素小人，或可以换色的经典形状",
        ["Pixel"] = "像素",
        ["Classic"] = "经典",
        ["Quota updated {0}"] = "额度更新于 {0}",
        ["Scan now"] = "立即扫描",
        ["Startup"] = "启动",
        ["Start Agent Island when you sign in to Windows"] = "登录 Windows 时自动启动 Agent Island",
        ["App language"] = "应用语言",
        ["Same as Windows"] = "跟随 Windows",
        ["The pixel style shows Settings in English"] = "像素风格下，设置以英文显示",
        ["Thanks"] = "致谢",
        ["The original app, by {0} · MIT License"] = "原版应用，作者 {0} · MIT 许可证",
        ["The pixel font, by {0} · SIL Open Font License 1.1"] = "像素字体，作者 {0} · SIL 开放字体许可证 1.1",
        ["Crash log"] = "崩溃日志",
        ["Open folder"] = "打开文件夹",
        ["Closes the drawer and the tray icon"] = "关闭抽屉和托盘图标",
        ["Estimated at API list prices from this computer's session logs — not a bill."] = "按 API 公开价格，根据本机会话日志估算——不是账单。",
        ["Frame rate while an agent works"] = "智能体工作时的帧率",
        ["Calm characters always breathe at 30 fps"] = "空闲的角色始终以 30 fps 呼吸",
        ["Match the display"] = "跟随显示器刷新率",
        ["{0} fps"] = "{0} fps",
        ["Save battery"] = "省电",
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
        ["Shows when Claude Code waits for you to approve a tool. Adds a small hook to ~/.claude/settings.json that records only the session and the prompt type; sessions started afterwards pick it up."] = "Claude Code 等你批准工具时，角色会显示出来。会在 ~/.claude/settings.json 加一个小 hook，只记录会话和提示类型；之后新开的会话生效。",
        ["Couldn't change Claude Code's settings"] = "无法修改 Claude Code 的设置",
        ["Couldn't change Grok's hooks"] = "无法修改 Grok 的 hook",
        ["Shows when Grok waits for you to approve a tool. Adds one hook file, agentisland-notify.json, to ~/.grok/hooks that records only the session and the prompt type; sessions started afterwards pick it up."] = "Grok 等你批准工具时，角色会显示出来。会在 ~/.grok/hooks 加一个 hook 文件 agentisland-notify.json，只记录会话和提示类型；之后新开的会话生效。",
        ["A file named agentisland-notify.json is already in ~/.grok/hooks and isn't Agent Island's, so this stays off."] = "~/.grok/hooks 里已有一个叫 agentisland-notify.json 的文件，不是 Agent Island 的，所以这个开关保持关闭。",
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
        ["Out of quota for now."] = "额度用完了",
        ["Taking a break — quota's used up."] = "额度用光了，先歇会儿",
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
        ["tokens · billed by Cursor's own plan"] = "Token · 按 Cursor 自己的套餐计费",
        ["Same X Subscription as Grok: one quota pool"] = "与 Grok 同一个 X 订阅：共用一个额度池",
        ["Models"] = "模型",
        ["Models, last 5 hours"] = "模型（最近 5 小时）",
        ["no price"] = "无价格",
        ["No sessions in this period"] = "这段时间没有会话",
        ["No sessions in the last 5 hours"] = "最近 5 小时没有会话",
        ["Tokens per hour (no price)"] = "每小时 Token（无价格）",
        ["Tokens per day (no price)"] = "每天 Token（无价格）",
        ["Antigravity has no conversations to count yet."] = "Antigravity 还没有可统计的对话。",
        ["Model not named"] = "未命名的模型",
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
        ["Balloon"] = "热气球",
        ["Rainbow A"] = "彩虹 A",
        ["Aeri"] = "Aeri",
        ["Blocks"] = "积木",
        ["Kite"] = "风筝",
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
    };
}
