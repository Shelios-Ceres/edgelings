import SwiftUI
import AppKit

@main
struct AgentIslandApp: App {
    @NSApplicationDelegateAdaptor(AppDelegate.self) var appDelegate

    init() {
        // Before anything touches UserDefaults: stores read their keys in
        // `init`, and the first store is created during the scene build.
        LegacyPrefsMigrator.run()
    }

    var body: some Scene {
        MenuBarExtra {
            MenuBarStatusView()
        } label: {
            MenuBarStatusLabel()
        }
        .menuBarExtraStyle(.menu)
    }
}

@MainActor
final class AppDelegate: NSObject, NSApplicationDelegate {
    var island: IslandWindowController?
    private var settingsShortcutMonitor: Any?
    private var backgroundActivity: NSObjectProtocol?
    private var recordingBackdrop: NSWindow?
    private var recordingStrip: NSWindow?

    func applicationDidFinishLaunching(_ notification: Notification) {
        NSApp.setActivationPolicy(.accessory)

        // Keep monitoring while the screen is locked. As an `.accessory` /
        // LSUIElement app with no visible window, we're a prime App Nap target:
        // once the screen locks, macOS suspends our timers, so a quota window
        // that resets while you're away is never detected and auto-resume never
        // fires — exactly the "locked screen = no resume" report. A background
        // activity assertion opts out of App Nap so the refresh and trigger
        // timers keep running. `.background` only disables App Nap; it does NOT
        // set `idleSystemSleepDisabled`, so it never keeps the Mac awake — a
        // Mac that actually sleeps still can't resume, which is expected.
        backgroundActivity = ProcessInfo.processInfo.beginActivity(
            options: .background,
            reason: "Monitor quota resets to auto-resume sessions while the screen is locked"
        )
        UserDefaults.standard.removeObject(forKey: "NSWindow Frame com_apple_SwiftUI_Settings_window")
        island = IslandWindowController()
        island?.show()

        settingsShortcutMonitor = NSEvent.addLocalMonitorForEvents(matching: .keyDown) { event in
            if event.modifierFlags.intersection(.deviceIndependentFlagsMask) == .command,
               event.charactersIgnoringModifiers == "," {
                SettingsWindowController.shared.show()
                return nil
            }
            return event
        }

        // Start fetching at app launch — NOT on view appear — so the panel
        // already has cached values the first time the user hovers, instead
        // of flashing "0%" while the first request lands.
        UsageStore.shared.startAutoRefresh()
        CostStore.shared.startAutoRefresh()

        // Wire the alert engine after the usage store so its initial
        // recompute sees whatever values the first refresh has produced.
        AlertEngine.shared.start()

        // Quota-exhaustion alarm rides the same usage signal: a window hitting
        // 100% pops the distinct "out of quota until <time>" alarm.
        UsageExhaustionAlarm.shared.start()

        // Auto-resume is retired (product call, 2026-07-13): the engine no
        // longer starts, so nothing is ever spawned — the page, settings tab,
        // and this start are the three gates; restore by re-enabling them.

        AgentReminderCenter.shared.start()
        ActivityMonitor.shared.start()
        showDemoTurnAlarmIfNeeded()

        // Touch the shared updater so Sparkle starts its background scheduler.
        _ = UpdaterController.shared

        // Release nudge (GitHub Releases lookup) — never in demo/debug or
        // any headless snapshot/recording rig, where a modal alert would
        // wedge the run or photobomb a clip.
        let env = ProcessInfo.processInfo.environment
        let isRig = env.keys.contains { $0.hasPrefix("AGENTISLAND_") }
        if AppEnvironment.current == .normal && !isRig {
            UpdateNudge.shared.start()
            // Post-update guide: once the island has settled, show this
            // version's highlights (once per version).
            DispatchQueue.main.asyncAfter(deadline: .now() + 1.2) {
                WhatsNewGate.maybeShow()
            }
        }

        // Headless card snapshot for tooling: waits for the cost scan to
        // land, renders the weekly report PNG, and exits. Mirrors the alarm
        // snapshot tooling used for release screenshots.
        if let path = ProcessInfo.processInfo.environment["AGENTISLAND_REPORT_SNAPSHOT"] {
            Task { @MainActor in
                try? await Task.sleep(nanoseconds: 5_000_000_000)
                WeeklyReportRenderer.writePNG(to: path)
                NSApp.terminate(nil)
            }
        }
        // Headless render of every release-notes/guide page — the QA
        // channel for the paged cards, so layout regressions are caught by
        // machine instead of on the owner's desktop.
        if let dir = ProcessInfo.processInfo.environment["AGENTISLAND_CARD_SNAPSHOT"] {
            Task { @MainActor in
                try? await Task.sleep(nanoseconds: 1_000_000_000)
                PagedCardSnapshot.writeAll(to: dir)
                NSApp.terminate(nil)
            }
        }
        // Weekly report moment: once per ISO week, surface the card ~10s
        // after launch (the cost scan needs a beat). Sharing needs a moment
        // put in front of people, not a buried menu item.
        if AppEnvironment.current == .normal,
           ProcessInfo.processInfo.environment["AGENTISLAND_REPORT_SNAPSHOT"] == nil,
           ProcessInfo.processInfo.environment["AGENTISLAND_MONTHLY_SNAPSHOT"] == nil,
           ProcessInfo.processInfo.environment["AGENTISLAND_CHIP_SNAPSHOT"] == nil {
            Task { @MainActor in
                try? await Task.sleep(nanoseconds: 10_000_000_000)
                let cal = Calendar(identifier: .iso8601)
                let comps = cal.dateComponents([.yearForWeekOfYear, .weekOfYear], from: Date())
                let weekKey = "\(comps.yearForWeekOfYear ?? 0)-W\(comps.weekOfYear ?? 0)"
                let shownKey = "AgentIsland.weeklyReportShownForWeek"
                if UserDefaults.standard.string(forKey: shownKey) != weekKey {
                    UserDefaults.standard.set(weekKey, forKey: shownKey)
                    WeeklyReportWindowController.shared.show()
                }
            }
        }

        // Monthly card counterpart of the weekly snapshot hook.
        if let path = ProcessInfo.processInfo.environment["AGENTISLAND_MONTHLY_SNAPSHOT"] {
            Task { @MainActor in
                try? await Task.sleep(nanoseconds: 5_000_000_000)
                MonthlyReportRenderer.writePNG(to: path)
                NSApp.terminate(nil)
            }
        }

        // Recording backdrop: a fullscreen card-style gradient UNDER the
        // island, so scripted clips never leak the real desktop (private
        // windows, notes, chats). Recording-rig only.
        if ProcessInfo.processInfo.environment["AGENTISLAND_BACKDROP"] != nil,
           let screen = NSScreen.main {
            let win = NSWindow(contentRect: screen.frame,
                               styleMask: [.borderless],
                               backing: .buffered, defer: false)
            // .floating, NOT .normal: the stage must cover OTHER APPS' windows
            // too — at .normal, Finder/docs/chat windows float above it and
            // leak into recordings (the report-clip privacy incident). Report
            // panels are also .floating but order in later, so they stay on
            // top; the island window sits higher still.
            win.level = .floating
            win.isOpaque = true
            win.contentView = NSHostingView(rootView: RecordingBackdropView())
            win.orderFrontRegardless()
            recordingBackdrop = win

            // Menu-bar extras (WeChat badge & co.) photobomb every
            // peek-state clip, and presentationOptions can't hide the bar
            // from an .accessory app — so cover it: a full-width wallpaper
            // strip at statusBar+1, still far below the island (.popUpMenu).
            let stripHeight: CGFloat = 44
            let strip = NSWindow(
                contentRect: NSRect(x: screen.frame.minX,
                                    y: screen.frame.maxY - stripHeight,
                                    width: screen.frame.width,
                                    height: stripHeight),
                styleMask: [.borderless], backing: .buffered, defer: false
            )
            strip.level = NSWindow.Level(rawValue: NSWindow.Level.statusBar.rawValue + 1)
            strip.isOpaque = true
            strip.contentView = NSHostingView(
                rootView: RecordingTopStripView(screenSize: screen.frame.size,
                                                stripHeight: stripHeight)
            )
            strip.orderFrontRegardless()
            recordingStrip = strip
        }

        // Recording rig: AGENTISLAND_UI_SCRIPT="wait:2,peek,wait:1.5,expand,
        // wait:3,page:cost,wait:3,collapse,wait:2,quit" — comma-separated
        // steps, executed in order; pairs with `screencapture -v -R` for
        // hands-free, cursor-free product clips.
        if let script = ProcessInfo.processInfo.environment["AGENTISLAND_UI_SCRIPT"] {
            Task { @MainActor in
                for rawStep in script.split(separator: ",") {
                    let step = rawStep.trimmingCharacters(in: .whitespaces)
                    if step.hasPrefix("wait:"), let seconds = Double(step.dropFirst(5)) {
                        try? await Task.sleep(nanoseconds: UInt64(seconds * 1_000_000_000))
                    } else if step == "quit" {
                        NSApp.terminate(nil)
                    } else {
                        switch step {
                        case "style:next": StylePref.shared.cycle()
                        case "costpage:toggle":
                            CostPanelVisibilityStore.shared.showInTopPanel.toggle()
                        case "alwaysshow:toggle":
                            AlwaysShowUsageStore.shared.enabled.toggle()
                        case "spacing:toggle":
                            IslandSpacingStore.shared.mode =
                                IslandSpacingStore.shared.mode == .compact ? .notchStyle : .compact
                        case "coststyle:next": CostStylePref.shared.cycle()
                        case "quota:toggle":
                            QuotaDisplayModeStore.shared.showsRemaining.toggle()
                        case "whatsnew:show": WhatsNewWindowController.shared.show()
                        case "report:weekly": WeeklyReportWindowController.shared.show()
                        case "report:monthly": MonthlyReportWindowController.shared.show()
                        case "settings:show": SettingsWindowController.shared.show()
                        case "visual:toggle": LowPowerModeStore.shared.enabled.toggle()
                        case "glow:teal": GlowColorStore.shared.choice = .teal
                        case "glow:cobalt": GlowColorStore.shared.choice = .cobalt
                        case "glow:violet": GlowColorStore.shared.choice = .violet
                        case "glow:silver": GlowColorStore.shared.choice = .silver
                        default:
                            NotificationCenter.default.post(
                                name: .islandDemoCommand, object: nil, userInfo: ["cmd": step]
                            )
                        }
                    }
                }
            }
        }

        // Settings-footer layout check (EN overflow regression had to be
        // seen to be believed): renders the footer at window width and exits.
        if let path = ProcessInfo.processInfo.environment["AGENTISLAND_FOOTER_SNAPSHOT"] {
            Task { @MainActor in
                try? await Task.sleep(nanoseconds: 1_000_000_000)
                let view = SettingsFooter()
                    .frame(width: 480)
                    .background(Color(red: 0.03, green: 0.03, blue: 0.04))
                let renderer = ImageRenderer(content: view)
                renderer.scale = 3
                if let tiff = renderer.nsImage?.tiffRepresentation,
                   let rep = NSBitmapImageRep(data: tiff),
                   let png = rep.representation(using: .png, properties: [:]) {
                    try? png.write(to: URL(fileURLWithPath: path))
                }
                NSApp.terminate(nil)
            }
        }

        // Same idea for the reset-card chip (taste iterations need eyes):
        // renders the chip at 10x against the panel background and exits.
        if let path = ProcessInfo.processInfo.environment["AGENTISLAND_CHIP_SNAPSHOT"] {
            Task { @MainActor in
                try? await Task.sleep(nanoseconds: 1_000_000_000)
                let sample = [
                    ResetCard(id: "a", title: "Full reset",
                              expiresAt: Date().addingTimeInterval(29 * 86400)),
                    ResetCard(id: "b", title: "Full reset",
                              expiresAt: Date().addingTimeInterval(29 * 86400)),
                ]
                let view = ResetCardChip(count: 2, cards: sample)
                    .padding(24)
                    .background(Color(red: 0.02, green: 0.02, blue: 0.027))
                let renderer = ImageRenderer(content: view)
                renderer.scale = 10
                if let tiff = renderer.nsImage?.tiffRepresentation,
                   let rep = NSBitmapImageRep(data: tiff),
                   let png = rep.representation(using: .png, properties: [:]) {
                    try? png.write(to: URL(fileURLWithPath: path))
                }
                NSApp.terminate(nil)
            }
        }
    }

    func applicationWillTerminate(_ notification: Notification) {
        // Give the background activity a defined end. It is meant to be held
        // for the whole session (App Nap off while we monitor), and quitting is
        // the one moment that ends the session — so release it here rather than
        // leaning on process teardown.
        if let backgroundActivity {
            ProcessInfo.processInfo.endActivity(backgroundActivity)
            self.backgroundActivity = nil
        }
    }

    private func showDemoTurnAlarmIfNeeded() {
        guard AppEnvironment.isDemo else { return }
        // Headless marketing/release-notes renders: writes both alarm cards as
        // PNGs (in-process ImageRenderer, no screen-recording permission
        // needed) into the given directory, then quits.
        if let path = ProcessInfo.processInfo.environment["AGENTISLAND_SETTINGS_SNAPSHOT"] {
            Task { @MainActor in
                SettingsWindowController.shared.show()
                // Two beats: one for the window to lay out, one for the
                // stores the page observes to publish their first values.
                try? await Task.sleep(nanoseconds: 2_500_000_000)
                Self.writeSettingsSnapshot(to: path)
                NSApp.terminate(nil)
            }
            return
        }
        if let dir = ProcessInfo.processInfo.environment["AGENTISLAND_DEMO_ALARM_SNAPSHOT"] {
            Task { @MainActor in
                try? await Task.sleep(nanoseconds: 400_000_000)
                let thread = ActivityMonitor.ActiveThread(
                    sessionId: "00000000-0000-0000-0000-000000000000",
                    label: L10n.tr("Demo thread"),
                    cwd: NSHomeDirectory() + "/Documents/Agent Island",
                    modified: Date(),
                    transcriptPath: nil,
                    turnKey: "demo",
                    launchTarget: .cli
                )
                Self.writeAlarmSnapshot(
                    TurnAlarmView(provider: .codex, providerName: "Codex", thread: thread, kind: .yourTurn, dismiss: {}),
                    to: dir + "/turn-alarm.png"
                )
                Self.writeAlarmSnapshot(
                    TurnAlarmView(
                        provider: .claude, providerName: "Claude", thread: nil,
                        kind: .quotaExhausted(window: .fiveHour, resetAt: Date().addingTimeInterval(2 * 3600 + 7 * 60)),
                        dismiss: {}
                    ),
                    to: dir + "/quota-alarm.png"
                )
                Self.writeAlarmSnapshot(
                    TurnAlarmView(
                        provider: .codex, providerName: "Codex", thread: nil,
                        kind: .quotaExhausted(window: .fiveHour, resetAt: Date().addingTimeInterval(3 * 3600 + 22 * 60)),
                        dismiss: {}
                    ),
                    to: dir + "/quota-alarm-codex.png"
                )
                NSApp.terminate(nil)
            }
            return
        }
        guard let raw = ProcessInfo.processInfo.environment["AGENTISLAND_DEMO_TURN_ALARM"] else { return }
        let value = raw.lowercased()
        // "quota" / "quota-codex" previews the out-of-quota alarm instead of
        // the finished-turn one (screenshots and launch videos need both).
        if value.hasPrefix("quota") {
            Task { @MainActor in
                try? await Task.sleep(nanoseconds: 600_000_000)
                TurnAlarmWindowController.shared.show(
                    provider: value.contains("codex") ? .codex : .claude,
                    thread: nil,
                    kind: .quotaExhausted(window: .fiveHour, resetAt: Date().addingTimeInterval(2 * 3600 + 7 * 60))
                )
            }
            return
        }
        let provider: AlertEngine.Provider = value == "claude" ? .claude : .codex
        Task { @MainActor in
            try? await Task.sleep(nanoseconds: 600_000_000)
            let thread = ActivityMonitor.ActiveThread(
                sessionId: "00000000-0000-0000-0000-000000000000",
                label: L10n.tr("Demo thread"),
                cwd: NSHomeDirectory() + "/Documents/Agent Island",
                modified: Date(),
                transcriptPath: nil,
                turnKey: "demo",
                launchTarget: provider == .claude ? .claudeDesktop : .cli
            )
            TurnAlarmWindowController.shared.show(provider: provider, thread: thread)
        }
    }

    /// Pin the app to the run loop until the user explicitly quits.
    func applicationShouldTerminateAfterLastWindowClosed(_ sender: NSApplication) -> Bool {
        false
    }

    @MainActor
    /// Settings-page snapshot for docs and QA — the Windows sweep's twin.
    /// `-Settings.activeTab display` picks the page. Renders the REAL
    /// window's view tree: an off-screen ImageRenderer never runs the
    /// scroll content's layout pass and writes an empty column.
    private static func writeSettingsSnapshot(to path: String) {
        guard let view = SettingsWindowController.shared.window?.contentView,
              let rep = view.bitmapImageRepForCachingDisplay(in: view.bounds)
        else {
            NSLog("AgentIsland: settings snapshot has no window for %@", path)
            return
        }
        view.cacheDisplay(in: view.bounds, to: rep)
        try? rep.representation(using: .png, properties: [:])?
            .write(to: URL(fileURLWithPath: path))
    }

    private static func writeAlarmSnapshot(_ view: TurnAlarmView, to path: String) {
        let card = view
            .frame(width: 520, height: 520)
            .clipShape(RoundedRectangle(cornerRadius: CardWindow.cornerRadius, style: .continuous))
        let renderer = ImageRenderer(content: card)
        renderer.scale = 2
        guard let cg = renderer.cgImage else {
            NSLog("AgentIsland: alarm snapshot render failed for %@", path)
            return
        }
        let rep = NSBitmapImageRep(cgImage: cg)
        try? rep.representation(using: .png, properties: [:])?
            .write(to: URL(fileURLWithPath: path))
    }
}
