import SwiftUI
import AppKit

/// Settings window — three tabs (General / Display / Providers) sandwiched
/// between a fixed brand header on top and the version/links/Quit footer
/// on the bottom. Tabs let each topical group stay short enough to fit a
/// modest window without scrolling, and the window itself is now resizable
/// rather than locked at 480×720, so the user controls the visible space.
struct SettingsView: View {
    @ObservedObject private var launchStore = LaunchAtLoginStore.shared
    @ObservedObject private var stylePref = StylePref.shared
    @ObservedObject private var costStylePref = CostStylePref.shared
    @ObservedObject private var visibility = ProviderVisibilityStore.shared
    @ObservedObject private var refreshStore = RefreshIntervalStore.shared
    @ObservedObject private var tokenMode = TokenCountModeStore.shared
    @ObservedObject private var lowPower = LowPowerModeStore.shared
    @ObservedObject private var glowColor = GlowColorStore.shared
    @ObservedObject private var interfaceScale = InterfaceScaleStore.shared
    @ObservedObject private var alwaysShow = AlwaysShowUsageStore.shared
    @ObservedObject private var costPanelVisibility = CostPanelVisibilityStore.shared
    @ObservedObject private var quotaMode = QuotaDisplayModeStore.shared
    @ObservedObject private var missionControlHide = MissionControlHideStore.shared
    @ObservedObject private var alertPrefs = AlertThresholdStore.shared
    @ObservedObject private var spacing = IslandSpacingStore.shared
    @ObservedObject private var targetDisplay = IslandTargetDisplayStore.shared
    @ObservedObject private var appLanguage = AppLanguageStore.shared
    @ObservedObject private var usage = UsageStore.shared
    @ObservedObject private var grokStore = GrokUsageStore.shared
    @ObservedObject private var cursorStore = CursorUsageStore.shared
    @ObservedObject private var antigravityStore = AntigravityUsageStore.shared
    @ObservedObject private var cost = CostStore.shared
    @ObservedObject private var updater = UpdaterController.shared

    @AppStorage("Settings.activeTab") private var activeTabRaw: String = SettingsTab.providers.rawValue

    /// Flashes the copy-login-link confirmation caption for a few seconds.

    /// Browser/profile landscape for the Claude sign-in target picker,
    /// loaded when the Providers section appears.
    @State private var providerLimitAlert = false
    @State private var hoveredTab: SettingsTab?
    /// Bumped after any parked-account change so the menu rebuilds.
    @State private var codexAccountsToken = 0
    @State private var codexAccountError = false
    @Namespace private var sidebarSelection
    @State private var hoveredProvider: DisplayProvider?
    @State private var railHovered: String?

    private var activeTab: SettingsTab {
        get {
            let tab = SettingsTab(rawValue: activeTabRaw) ?? .providers
            // A window last parked on the retired Triggers tab lands on
            // Providers instead of an orphaned tab with no button.
            return tab
        }
        nonmutating set { activeTabRaw = newValue.rawValue }
    }

    private var version: String {
        Bundle.main.infoDictionary?["CFBundleShortVersionString"] as? String ?? ""
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            HStack(alignment: .top, spacing: 0) {
                sidebar

                Rectangle()
                    .fill(.white.opacity(0.05))
                    .frame(width: 1)

                VStack(alignment: .leading, spacing: 0) {
                    // Traffic-light strip stays over the content column —
                    // the sidebar runs full-height beside it.
                    Color.clear.frame(height: 24)

                    // ScrollView keeps the footer parked at the bottom —
                    // overflow scrolls instead of pushing chrome off-screen.
                    ScrollView(.vertical, showsIndicators: false) {
                        VStack(alignment: .leading, spacing: 0) {
                            Text(L10n.tr(activeTab.label))
                                .font(SettingsType.pageTitle)
                                .foregroundStyle(.white.opacity(0.94))
                                .padding(.horizontal, 24)
                                .padding(.top, 6)
                                .padding(.bottom, 2)
                            switch activeTab {
                            case .providers:    providersTab
                            case .display:      displayTab
                            case .alerts:       alertsTab
                            case .general:      generalTab
                            case .statusGuide:  StatusGuideView()
                            case .releaseNotes: releaseNotesTab
                            case .about:        aboutTab
                            }
                        }
                        // Cadence B6: content replacement is a blur cross-
                        // dissolve in place — zero slide, zero scale.
                        .id(activeTab)
                        .transition(.blurFade)
                        .animation(.spring(response: 0.38, dampingFraction: 0.85), value: activeTab)
                        .frame(maxWidth: .infinity, alignment: .top)
                    }
                    .frame(maxWidth: .infinity, maxHeight: .infinity)
                    // Recording rig: below-the-fold rows need live-window
                    // screenshots too (ImageRenderer is banned for chrome).
                    .modifier(RigScrollAnchor())

                    hairline

                    SettingsFooter()
                }
            }
        }
        .frame(minWidth: 600, minHeight: 460)
        .background(Color(red: 0.020, green: 0.020, blue: 0.027))
        .preferredColorScheme(.dark)
        .id(appLanguage.language)
        .alert(L10n.tr("Pick at most two — turn one off first"), isPresented: $providerLimitAlert) {
            Button(L10n.tr("OK"), role: .cancel) { }
        }
        .alert(L10n.tr("Could not switch Codex account"), isPresented: $codexAccountError) {
            Button(L10n.tr("OK"), role: .cancel) { }
        } message: {
            Text(L10n.tr("Could not read or save the account files. Check file access and try again."))
        }
    }

    // MARK: - Sidebar

    /// Left rail: compact brand up top, one item per page, the version pill
    /// at the bottom. Replaces the inherited top tab strip — navigation
    /// reads as structure, not as a row of pills fighting for width.
    private var sidebar: some View {
        VStack(alignment: .leading, spacing: 0) {
            // Clears the traffic lights, which float over the sidebar.
            Color.clear.frame(height: 30)

            HStack(spacing: 8) {
                // The bare transparent five-blade mark — no tile, no hover
                // motion (owner review ×2, 2026-08-09).
                BrandMark(side: 24)
                Text("Agent Island")
                    .font(.system(size: 12.5, weight: .semibold))
                    .foregroundStyle(.white.opacity(0.92))
            }
            .padding(.horizontal, 14)
            .padding(.bottom, 14)

            ForEach(SettingsTab.allCases, id: \.self) { tab in
                sidebarItem(tab)
            }

            Spacer(minLength: 8)

            // Version pill (opens release notes) with Quit at its right —
            // app-level controls live on the rail, not in the page footer
            // (owner call, 2.1.1).
            HStack(spacing: 8) {
                Button {
                    WhatsNewWindowController.shared.show()
                } label: {
                    Text("v\(version)")
                        .font(Typography.bodyNumber)
                        .foregroundStyle(.white.opacity(railHovered == "version" ? 0.75 : 0.38))
                        .padding(.horizontal, 9)
                        .padding(.vertical, 4)
                        .background(Capsule().fill(.white.opacity(railHovered == "version" ? 0.10 : 0.05)))
                }
                .buttonStyle(TactileButtonStyle())
                .help(L10n.tr("What's new in this version"))
                .onHover { railHovered = $0 ? "version" : nil }

                Spacer(minLength: 4)

                Button {
                    NSApp.terminate(nil)
                } label: {
                    Text(L10n.tr("Quit"))
                        .font(SettingsType.data)
                        .fixedSize()
                        .foregroundStyle(.white.opacity(railHovered == "quit" ? 0.92 : 0.55))
                        .padding(.horizontal, 9)
                        .padding(.vertical, 4)
                        .background(Capsule().fill(.white.opacity(railHovered == "quit" ? 0.10 : 0.05)))
                }
                .buttonStyle(TactileButtonStyle())
                .help(L10n.tr("Quit AgentIsland"))
                .onHover { railHovered = $0 ? "quit" : nil }
            }
            .animation(.easeOut(duration: 0.14), value: railHovered)
            .padding(.horizontal, 14)
            .padding(.bottom, 14)
        }
        .frame(width: 158, alignment: .leading)
        .frame(maxHeight: .infinity, alignment: .top)
        .background(Color.white.opacity(0.016))
    }

    // MARK: - Tabs

    /// Recording rig: AGENTISLAND_SETTINGS_SCROLL=bottom opens the settings
    /// scroll at the end, so below-the-fold rows can be screenshotted from a
    /// live window. No-op on macOS 13 (the anchor API is 14+) and without
    /// the env var.
    private struct RigScrollAnchor: ViewModifier {
        func body(content: Content) -> some View {
            if #available(macOS 14.0, *),
               ProcessInfo.processInfo.environment["AGENTISLAND_SETTINGS_SCROLL"] == "bottom" {
                content.defaultScrollAnchor(.bottom)
            } else {
                content
            }
        }
    }

    enum SettingsTab: String, CaseIterable {
        // Declaration order IS the sidebar order (2.1.1 IA): the providers
        // page leads because it is what the app is about; alerts stand
        // alone instead of hiding at the bottom of General; General keeps
        // only the app-level odds and ends.
        case providers, display, alerts, general, statusGuide, releaseNotes, about

        var label: String {
            switch self {
            case .providers:    "Providers"
            case .display:      "Display"
            case .alerts:       "Alerts"
            case .general:      "General"
            case .statusGuide:  "Status"
            case .releaseNotes: "Notes"
            case .about:        "About"
            }
        }

        /// SF Symbols shipped since macOS 13 — no availability gymnastics.
        var icon: String {
            switch self {
            case .providers:    "square.grid.2x2"
            case .display:      "display"
            case .alerts:       "bell.badge"
            case .general:      "gearshape"
            case .statusGuide:  "eye"
            case .releaseNotes: "sparkles"
            case .about:        "info.circle"
            }
        }
    }

    /// One sidebar row: icon + label, a soft capsule for the selection, a
    /// fainter one on hover. Restraint over spectacle (Cadence E16): the
    /// selected state is the only strong signal.
    @ViewBuilder
    private func sidebarItem(_ tab: SettingsTab) -> some View {
        let isOn = (activeTab == tab)
        Button {
            // High-damping spring: the block glides, never overshoots.
            withAnimation(.spring(response: 0.34, dampingFraction: 0.86)) { activeTab = tab }
        } label: {
            HStack(spacing: 8) {
                Image(systemName: tab.icon)
                    .font(.system(size: 11.5, weight: .semibold))
                    .frame(width: 16)
                    .foregroundStyle(isOn ? IslandColor.chrome : .white.opacity(0.48))
                    // Cadence C9: the icon gives one small confirming pop on
                    // landing, then settles.
                    .scaleEffect(isOn ? 1.12 : 1)
                    .animation(.spring(response: 0.28, dampingFraction: 0.55), value: isOn)
                Text(L10n.tr(tab.label))
                    .font(SettingsType.tabLabel)
                    .lineLimit(1)
                    .foregroundStyle(isOn ? .white.opacity(0.95) : .white.opacity(0.55))
                Spacer(minLength: 0)
            }
            .padding(.horizontal, 10)
            .padding(.vertical, 7)
            .background {
                // Cadence C11: ONE selection block that slides between rows
                // (matchedGeometryEffect), not per-row fills popping in and
                // out. Hover is a separate, fainter layer underneath.
                ZStack {
                    if hoveredTab == tab, !isOn {
                        RoundedRectangle(cornerRadius: 7, style: .continuous)
                            .fill(.white.opacity(0.04))
                    }
                    if isOn {
                        RoundedRectangle(cornerRadius: 7, style: .continuous)
                            .fill(IslandColor.chrome.opacity(0.13))
                            .matchedGeometryEffect(id: "sidebarSelection", in: sidebarSelection)
                    }
                }
            }
            .contentShape(RoundedRectangle(cornerRadius: 7))
        }
        .buttonStyle(.plain)
        .onHover { hovering in
            withAnimation(.easeOut(duration: 0.12)) {
                hoveredTab = hovering ? tab : (hoveredTab == tab ? nil : hoveredTab)
            }
        }
        .padding(.horizontal, 8)
        .padding(.vertical, 1)
        .accessibilityLabel(L10n.tr("%@ tab", L10n.tr(tab.label)))
        .accessibilityAddTraits(isOn ? [.isButton, .isSelected] : .isButton)
    }

    // MARK: - Tab content

    private var generalTab: some View {
        VStack(alignment: .leading, spacing: 0) {
            generalSection
            updatesSection
        }
    }

    /// Alerts get their own page in the 2.1.1 IA — they were buried at the
    /// bottom of General, which is where nobody looks for "why did the
    /// island flash amber".
    private var alertsTab: some View {
        VStack(alignment: .leading, spacing: 0) {
            alertsSection
        }
    }

    /// The 关于 page (owner spec, 2026-07-18, studied from Cadence's 理念
    /// tab): serif manifesto prose — who made this and why — plus the
    /// open-source facts. System serif (New York), not a bundled typeface.
    /// The ghosted brand mark low-right — Cadence's 理念 page signature
    /// (owner spec, 2026-07-18): a large, blurred, near-invisible echo of
    /// the logo that gives the page depth without competing with the prose.
    private var aboutGhostMark: some View {
        Group {
            if let logo = Bundle.main.url(forResource: "agentisland_logo", withExtension: "png")
                .flatMap({ NSImage(contentsOf: $0) }) {
                Image(nsImage: logo)
                    .resizable()
                    .aspectRatio(contentMode: .fit)
                    .frame(width: 205)
                    .blur(radius: 2.5)
                    .opacity(0.07)
                    .rotationEffect(.degrees(-9))
                    .offset(x: 30, y: 76)
                    .allowsHitTesting(false)
            }
        }
    }

    private var aboutTab: some View {
        ZStack(alignment: .bottomTrailing) {
            aboutGhostMark
            aboutContent
        }
        .frame(minHeight: 395, alignment: .top)
        .clipped()
    }

    private var aboutContent: some View {
        VStack(alignment: .leading, spacing: 0) {
            VStack(alignment: .leading, spacing: 16) {
                Text("Agent Island")
                    .font(.system(size: 26, weight: .bold))
                    .foregroundStyle(.white.opacity(0.95))
                    .padding(.top, 0)

                // The owner's own words (2026-07-18): the origin is
                // efficiency, the ask is a share or a star — nothing else.
                VStack(alignment: .leading, spacing: 12) {
                    Text(L10n.tr("Agents are part of every builder's day now — Claude Code, Codex, Gemini, Grok, Cursor… and whatever ships next month. They run, you wait, and nobody tells you when it is your turn again"))
                    Text(L10n.tr("Agent Island 2.0 puts them all on one island. Who is working, whose turn it is, how much quota is left, what it cost — one glance at the notch, no window switching, no terminal tabs to hunt through"))
                    Text(L10n.tr("Every number is computed on your own computer — Mac or Windows — from logs the agents already write. No account, no telemetry, nothing uploaded"))
                    Text(L10n.tr("If Agent Island helps you, a star on GitHub and a share with a friend are the two things that keep it going"))
                }
                .font(.system(size: 12.5, weight: .regular))
                .foregroundStyle(.white.opacity(0.74))
                .lineSpacing(6)
                .fixedSize(horizontal: false, vertical: true)

                Rectangle()
                    .fill(.white.opacity(0.07))
                    .frame(height: 0.5)
                    .padding(.top, 20)

                VStack(alignment: .leading, spacing: 10) {
                    aboutFact(L10n.tr("Made by"), value: "Tristan Tang") {
                        NSWorkspace.shared.open(URL(string: "https://tristan.media")!)
                    }
                    aboutFact(L10n.tr("Sponsor"), value: L10n.tr("Star on GitHub")) {
                        NSWorkspace.shared.open(URL(string: "https://github.com/tristan666666/agent-island")!)
                    }
                }
                .padding(.top, 12)
            }
            .padding(.horizontal, 24)
            .padding(.bottom, 6)
        }
    }

    private func aboutFact(_ label: String, value: String, action: @escaping () -> Void) -> some View {
        Button(action: action) {
            HStack(spacing: 8) {
                Text(label)
                    .font(SettingsType.data)
                    .foregroundStyle(.white.opacity(0.40))
                    .frame(width: 74, alignment: .leading)
                Text(value)
                    .font(SettingsType.data.weight(.semibold))
                    .foregroundStyle(.white.opacity(0.80))
                Image(systemName: "arrow.up.right")
                    .font(.system(size: 8, weight: .bold))
                    .foregroundStyle(.white.opacity(0.30))
            }
        }
        .buttonStyle(TactileButtonStyle())
    }

    private var releaseNotesTab: some View {
        VStack(alignment: .leading, spacing: 0) {
            releaseNotesSection
        }
    }

    private var displayTab: some View {
        VStack(alignment: .leading, spacing: 0) {
            chartSection
            costStyleSection
            topPanelSection
            targetDisplaySection
            missionControlSection
        }
    }

    private var providersTab: some View {
        VStack(alignment: .leading, spacing: 0) {
            providersSection
            refreshIntervalSection
            tokenCountingSection
            costSection
        }
    }

    // MARK: - Pieces

    private var hairline: some View {
        LinearGradient(
            colors: [.clear, .white.opacity(0.055), .white.opacity(0.055), .clear],
            startPoint: .leading, endPoint: .trailing
        )
        .frame(height: 1)
    }

    @ViewBuilder
    private func sectionLabel(_ text: String, hint: String? = nil) -> some View {
        HStack(alignment: .firstTextBaseline) {
            Text(L10n.tr(text))
                .font(SettingsType.section)
                .tracking(1.05)
                .textCase(.uppercase)
                .foregroundStyle(.white.opacity(0.34))
            Spacer(minLength: 8)
            if let hint {
                Text(L10n.tr(hint))
                    .font(SettingsType.data)
                    .foregroundStyle(.white.opacity(0.18))
            }
        }
        .padding(.horizontal, 10)
        .padding(.bottom, 6)
    }

    // MARK: - Sections

    private var generalSection: some View {
        VStack(alignment: .leading, spacing: 0) {
            SettingsRow(
                title: "Launch at Login",
                subtitle: launchStore.errorMessage
            ) {
                SettingsToggle(isOn: launchStore.isEnabled) { launchStore.toggle() }
            }
            SettingsRow(
                title: "Language",
                subtitle: nil
            ) {
                languagePicker
            }
        }
        .padding(.horizontal, 14)
        .padding(.top, 18)
        .padding(.bottom, 6)
    }

    /// Polling cadence lives with the providers it polls — moved out of
    /// General in the 2.1.1 IA pass (owner call: group by logic, not by
    /// where rows historically sat).
    private var refreshIntervalSection: some View {
        VStack(alignment: .leading, spacing: 0) {
            SettingsRow(
                title: "Refresh interval",
                subtitle: nil
            ) {
                refreshSegmented
            }
        }
        .padding(.horizontal, 14)
        .padding(.top, 4)
        .padding(.bottom, 6)
    }

    /// Approaching-limit alerts. Default off — opt-in via the toggle.
    /// When on, the silhouette glow tints amber/red while a tracked 5h
    /// window is at or above the configured percentages, and the peek
    /// pill auto-extends once when a window first crosses each threshold.
    private var alertsSection: some View {
        VStack(alignment: .leading, spacing: 0) {
            SettingsRow(
                title: "Approaching-limit alerts",
                subtitle: nil
            ) {
                SettingsToggle(isOn: alertPrefs.enabled) {
                    // withAnimation here so the threshold rows + Preview row
                    // crossfade their disabled/enabled state instead of
                    // snapping. The dim/undim is the user's signal that the
                    // controls became interactive.
                    withAnimation(.strongEaseOut) {
                        alertPrefs.enabled.toggle()
                    }
                }
            }
            thresholdsBlock
                .disabled(!alertPrefs.enabled)
                .opacity(alertPrefs.enabled ? 1.0 : 0.40)
            if alertPrefs.enabled && isDevMode {
                SettingsRow(
                    title: "Preview",
                    subtitle: "Inject test percentages. Visible only when launched with AGENTISLAND_DEBUG=1."
                ) {
                    previewButtons
                }
            }
        }
        .padding(.horizontal, 14)
        .padding(.top, 14)
        .padding(.bottom, 6)
    }

    private var previewButtons: some View {
        HStack(spacing: 4) {
            previewButton("Live") { usage.refresh() }
                .keyboardShortcut("1", modifiers: .command)
                .help("⌘1 — pull real provider data")
            previewButton("Warn") { runPreview(claude: 0.85, codex: 0.55) }
                .keyboardShortcut("2", modifiers: .command)
                .help("⌘2 — Claude 85%, Codex 55%")
            previewButton("Crit") { runPreview(claude: 0.96, codex: 0.55) }
                .keyboardShortcut("3", modifiers: .command)
                .help("⌘3 — Claude 96%, Codex 55%")
            previewButton("Both") { runPreview(claude: 0.86, codex: 0.97) }
                .keyboardShortcut("4", modifiers: .command)
                .help("⌘4 — Claude 86%, Codex 97%")
        }
    }

    @ViewBuilder
    private func previewButton(_ label: String, action: @escaping () -> Void) -> some View {
        Button(action: action) {
            Text(L10n.tr(label))
                .font(Typography.bodyNumber)
                .foregroundStyle(.white.opacity(0.85))
                .lineLimit(1)
                .fixedSize()
                .padding(.horizontal, 9)
                .padding(.vertical, 4)
                .background {
                    RoundedRectangle(cornerRadius: 5)
                        .fill(.white.opacity(0.06))
                        .overlay {
                            RoundedRectangle(cornerRadius: 5)
                                .strokeBorder(.white.opacity(0.10), lineWidth: 0.5)
                        }
                }
        }
        .buttonStyle(PressableButtonStyle())
    }

    private var isDevMode: Bool {
        AppEnvironment.isDebug
    }

    /// Resets the engine's crossing memory before injecting so each click
    /// fires a fresh pulse — otherwise the second "Warn" click would be a
    /// no-op (key already in memory from the first click).
    private func runPreview(claude: Double, codex: Double) {
        AlertEngine.shared.prepareForPreview()
        usage.injectPreviewUsage(claudeFiveHour: claude, codexFiveHour: codex)
    }

    /// Single paired block listing both thresholds inline, each tagged
    /// with its own colored dot so the visual mapping (amber → warning,
    /// red → critical) reads at a glance. Replaces what used to be two
    /// near-duplicate SettingsRows whose subtitles only differed by one
    /// word.
    private var thresholdsBlock: some View {
        VStack(spacing: 6) {
            thresholdLine(
                color: IslandColor.alertAmber,
                label: "Warning",
                value: Binding(
                    get: { alertPrefs.warningPercent },
                    set: { alertPrefs.warningPercent = $0 }
                ),
                range: warningStepperRange
            )
            thresholdLine(
                color: IslandColor.alertRed,
                label: "Critical",
                value: Binding(
                    get: { alertPrefs.criticalPercent },
                    set: { alertPrefs.criticalPercent = $0 }
                ),
                range: criticalStepperRange
            )
        }
        .padding(.horizontal, 10)
        .padding(.vertical, 8)
    }

    @ViewBuilder
    private func thresholdLine(
        color: Color,
        label: String,
        value: Binding<Int>,
        range: ClosedRange<Int>
    ) -> some View {
        HStack(spacing: 10) {
            Circle()
                .fill(color)
                .frame(width: 7, height: 7)
                .shadow(color: color.opacity(0.7), radius: 4)
                .accessibilityHidden(true)
            Text(L10n.tr(label))
                .font(SettingsType.rowTitle)
                .tracking(-0.07)
                .foregroundStyle(.white.opacity(0.92))
            Spacer(minLength: 8)
            thresholdStepper(value: value, range: range)
        }
        .padding(.vertical, 5)
    }

    /// Warning's upper bound is `critical - 1` so the steppers can't drift
    /// the pair into an invalid state. Same idea in reverse for critical.
    private var warningStepperRange: ClosedRange<Int> {
        let lo = AlertThresholdStore.warningRange.lowerBound
        let hi = min(AlertThresholdStore.warningRange.upperBound, alertPrefs.criticalPercent - 1)
        return lo...max(lo, hi)
    }

    private var criticalStepperRange: ClosedRange<Int> {
        let lo = max(AlertThresholdStore.criticalRange.lowerBound, alertPrefs.warningPercent + 1)
        let hi = AlertThresholdStore.criticalRange.upperBound
        return min(lo, hi)...hi
    }

    @ViewBuilder
    private func thresholdStepper(
        value: Binding<Int>,
        range: ClosedRange<Int>
    ) -> some View {
        // Wrapped binding clamps anything the user types so out-of-range
        // direct entry (e.g. "999") snaps to the dynamic range on commit.
        // The dynamic range already enforces `warning < critical`, so this
        // also covers the cross-field constraint without a separate check.
        let clamped = Binding<Int>(
            get: { value.wrappedValue },
            set: { newValue in
                value.wrappedValue = max(range.lowerBound, min(range.upperBound, newValue))
            }
        )
        HStack(spacing: 3) {
            TextField("", value: clamped, format: .number)
                .textFieldStyle(.plain)
                .multilineTextAlignment(.center)
                .font(Typography.bodyNumber)
                .foregroundStyle(.white.opacity(0.95))
                .monospacedDigit()
                .frame(width: 22, height: 18)
                .clipped()
            Text("%")
                .font(Typography.bodyNumber)
                .foregroundStyle(.white.opacity(0.55))
        }
        .frame(width: 64, height: 28)
        .background {
            RoundedRectangle(cornerRadius: 7)
                .fill(.white.opacity(0.05))
                .overlay {
                    RoundedRectangle(cornerRadius: 7)
                        .strokeBorder(.white.opacity(0.10), lineWidth: 0.5)
                }
        }
    }

    private var updatesSection: some View {
        VStack(alignment: .leading, spacing: 0) {
            sectionLabel("Updates")
            SettingsRow(
                title: "Check for updates automatically",
                subtitle: nil
            ) {
                SettingsToggle(isOn: updater.automaticallyChecks) {
                    updater.automaticallyChecks.toggle()
                }
            }
            SettingsRow(
                title: "Check now",
                subtitle: nil
            ) {
                // Backed by the GitHub Releases lookup, not Sparkle — the
                // Sparkle feed is unset, so its check would answer nothing.
                PillButton(label: "Check") {
                    Task { await UpdateNudge.shared.checkNow() }
                }
            }
        }
        .padding(.horizontal, 14)
        .padding(.top, 14)
        .padding(.bottom, 6)
    }

    /// Release-notes tab (owner call, 1.7.2): reopen this version's
    /// highlights, the global guide, the after-update popup switch, and the
    /// full changelog on the website — all in one rail-level place.
    private var releaseNotesSection: some View {
        VStack(alignment: .leading, spacing: 0) {
            SettingsRow(
                title: "What's new in this version",
                subtitle: nil
            ) {
                PillButton(label: "View") {
                    WhatsNewWindowController.shared.show()
                }
            }
            SettingsRow(
                title: "Product guide",
                subtitle: nil
            ) {
                PillButton(label: "Open") {
                    GuideWindowController.shared.show()
                }
            }
            SettingsRow(
                title: "Full changelog on the website",
                subtitle: nil
            ) {
                PillButton(label: "Open") {
                    NSWorkspace.shared.open(URL(string: "https://agent-island.dev/changelog/")!)
                }
            }
        }
        .padding(.horizontal, 14)
        .padding(.top, 14)
        .padding(.bottom, 6)
    }

    private var languagePicker: some View {
        Picker("", selection: languageSelection) {
            ForEach(AppLanguage.allCases, id: \.self) { language in
                Text(language.menuLabel).tag(language)
            }
        }
        .labelsHidden()
        .pickerStyle(.menu)
        .fixedSize()
        .accessibilityLabel(L10n.tr("Language"))
    }

    private var languageSelection: Binding<AppLanguage> {
        Binding(
            get: { appLanguage.language },
            set: { newLanguage in
                if appLanguage.select(newLanguage) {
                    showLanguageRestartPrompt()
                }
            }
        )
    }

    private var interfaceScalePicker: some View {
        Picker("", selection: Binding(
            get: { interfaceScale.factor },
            set: { interfaceScale.factor = $0 }
        )) {
            ForEach(InterfaceScaleStore.steps, id: \.self) { step in
                Text("\(Int((step * 100).rounded()))%").tag(step)
            }
        }
        .labelsHidden()
        .pickerStyle(.menu)
        .fixedSize()
        .accessibilityLabel(L10n.tr("Interface scale"))
    }

    /// Calm / Vivid — the ambient-effects choice (LowPowerModeStore's
    /// `enabled` is true for Calm).
    private var effectsPicker: some View {
        Picker("", selection: Binding(
            get: { lowPower.enabled },
            set: { newValue in
                // Animated so the Glow color row slides in/out with the
                // mode instead of popping.
                withAnimation(.strongEaseOut) { lowPower.enabled = newValue }
            }
        )) {
            Text(L10n.tr("Calm")).tag(true)
            Text(L10n.tr("Vivid")).tag(false)
        }
        .labelsHidden()
        .pickerStyle(.menu)
        .fixedSize()
        .accessibilityLabel(L10n.tr("Visual mode"))
    }

    /// Four curated dots, teal first (the default). Swatches only — a color
    /// needs no sentence, and the island previews the choice live.
    private var glowColorSwatches: some View {
        HStack(spacing: 7) {
            ForEach(GlowColorStore.Choice.allCases, id: \.self) { choice in
                let selected = glowColor.choice == choice
                Button {
                    withAnimation(.easeOut(duration: 0.18)) { glowColor.choice = choice }
                } label: {
                    Circle()
                        .fill(choice.color)
                        .frame(width: 13, height: 13)
                        .overlay {
                            Circle().strokeBorder(
                                .white.opacity(selected ? 0.92 : 0.16),
                                lineWidth: selected ? 1.5 : 0.5
                            )
                        }
                        .shadow(color: choice.color.opacity(selected ? 0.55 : 0), radius: 4)
                        .padding(2)
                        .contentShape(Circle().inset(by: -3))
                }
                .buttonStyle(.plain)
                .accessibilityLabel(L10n.tr(choice.label))
                .accessibilityAddTraits(selected ? [.isSelected] : [])
            }
        }
    }

    /// The switch is live — settings and island text re-render right away.
    /// Only a few date/number labels sit behind formatters created at app
    /// launch, so the prompt offers (not demands) a restart.
    private func showLanguageRestartPrompt() {
        let alert = NSAlert()
        alert.messageText = L10n.tr("Language changed")
        alert.informativeText = L10n.tr("The new language applies now. A few date and number labels update after AgentIsland restarts.")
        alert.addButton(withTitle: L10n.tr("OK"))
        alert.addButton(withTitle: L10n.tr("Restart now"))
        if alert.runModal() == .alertSecondButtonReturn {
            appLanguage.restartApp()
        }
    }

    /// Providers as CARDS — real brand marks leading, live status inside,
    /// the slot counter riding the group header. Nothing here shares a
    /// silhouette with the inherited flat row list.
    private var providersSection: some View {
        VStack(alignment: .leading, spacing: 0) {
            // Slot occupancy, spoken by structure instead of a caption
            // sentence: the enabled marks + the count.
            HStack {
                Spacer(minLength: 0)
                HStack(spacing: 7) {
                    ForEach(visibility.enabled, id: \.self) { provider in
                        ProviderMark(provider: provider, size: 12,
                                     tint: provider.brandColor.opacity(0.9))
                    }
                    Text("\(visibility.enabled.count) / 2")
                        .font(SettingsType.data)
                        .foregroundStyle(IslandColor.chrome.opacity(0.92))
                }
                .padding(.horizontal, 10)
                .padding(.vertical, 5)
                .background(Capsule().fill(IslandColor.chrome.opacity(0.10)))
            }
            .padding(.bottom, 2)

            providerCard(.claude,
                         status: providerSubtitle(usage.claude),
                         chip: usage.claude.plan?.uppercased(),
                         extra: {
                AnyView(HStack(spacing: 8) {
                    // ONE button. The default browser opens claude.ai and
                    // the loopback catches the redirect. Only after a
                    // failed round does the same spot offer the code
                    // fallback — progressive disclosure, not a toolbar
                    // (owner review, 2026-08-08: 搞成这样子很奇怪).
                    if claudeReauthAvailable {
                        if usage.claudeReauthFailureCaption != nil, !usage.claudeReauthInProgress {
                            PillButton(label: "Sign in with a code") { startClaudePasteLogin() }
                        }
                        PillButton(
                            label: usage.claudeReauthInProgress ? "waiting for login…" : "Re-authenticate",
                            isLoading: usage.claudeReauthInProgress
                        ) {
                            usage.reauthenticateClaude()
                        }
                    }
                })
            })

            providerCard(.codex,
                         status: providerSubtitle(usage.codex),
                         chip: usage.codex.plan?.uppercased(),
                         extra: {
                AnyView(HStack(spacing: 8) {
                    codexAccountMenu
                    if CodexCredentials.canPromptReauth(usage: usage.codex) {
                        PillButton(
                            label: usage.codexReauthInProgress ? "waiting for login…" : "Re-authenticate",
                            isLoading: usage.codexReauthInProgress
                        ) {
                            usage.reauthenticateCodex()
                        }
                    }
                })
            })

            providerCard(.antigravity, status: antigravitySubtitle,
                         chip: visibility.antigravityDetected ? antigravityStore.tierBadge : nil)
            providerCard(.grok, status: grokSubtitle,
                         chip: visibility.grokDetected ? grokStore.authModeBadge : nil)
            providerCard(.cursor, status: cursorSubtitle,
                         chip: visibility.cursorDetected ? cursorStore.planBadge : nil)
        }
        .padding(.horizontal, 24)
        .padding(.top, 14)
        .padding(.bottom, 6)
    }

    /// One provider card: mark → name + plan chip → live status line,
    /// actions and the slot toggle on the trailing edge.
    private func providerCard(
        _ provider: DisplayProvider,
        status: String,
        chip: String?,
        extra: () -> AnyView = { AnyView(EmptyView()) },
        footer: () -> AnyView? = { nil }
    ) -> some View {
        let isHovered = hoveredProvider == provider
        return VStack(alignment: .leading, spacing: 0) {
            HStack(spacing: 12) {
                ProviderMark(provider: provider, size: 20,
                             tint: provider.brandColor.opacity(0.95))
                    .frame(width: 24)
                VStack(alignment: .leading, spacing: 3) {
                    HStack(spacing: 7) {
                        Text(provider.displayName)
                            .font(SettingsType.rowTitle.weight(.semibold))
                            .foregroundStyle(.white.opacity(0.95))
                        if let chip {
                            Text(chip)
                                .font(Typography.chip)
                                .tracking(0.8)
                                .foregroundStyle(.white.opacity(0.62))
                                .padding(.horizontal, 5)
                                .padding(.vertical, 2)
                                .background(
                                    RoundedRectangle(cornerRadius: 3)
                                        .fill(.white.opacity(0.07))
                                )
                        }
                    }
                    Text(status)
                        .font(SettingsType.data)
                        .foregroundStyle(.white.opacity(0.66))
                        .lineLimit(1)
                        .truncationMode(.middle)
                        // Cadence B7: numbers dissolve into their successor
                        // instead of snapping.
                        .id(status)
                        .transition(.blurFade)
                        .animation(.easeOut(duration: 0.28), value: status)
                }
                Spacer(minLength: 10)
                extra()
                SettingsToggle(isOn: visibility.isEnabled(provider)) {
                    toggleProvider(provider)
                }
            }
            if let footer = footer() {
                footer
            }
        }
        .padding(.leading, 14)
        .padding(.trailing, 4)
        .padding(.vertical, 13)
        // No boxes: a brand-tinted rule on the leading edge marks the row,
        // and separation comes from a hairline + whitespace (owner call,
        // 2026-08-08: 不喜欢卡片质感). Hover breathes a faint brand wash
        // across the row and widens the rule a hair.
        .background {
            LinearGradient(
                colors: provider.brandStops.map { $0.opacity(isHovered ? 0.05 : 0) } + [.clear],
                startPoint: .leading, endPoint: .trailing
            )
        }
        .overlay(alignment: .leading) {
            // The rule IS the row's identity, so it carries the provider's
            // full ramp: one hue top-to-bottom for four of them, Google's
            // blue→green→yellow→red for Antigravity.
            RoundedRectangle(cornerRadius: 1)
                .fill(provider.brandGradient(
                    opacity: visibility.isEnabled(provider) ? (isHovered ? 1 : 0.85)
                                                            : (isHovered ? 0.45 : 0.22),
                    from: .top, to: .bottom
                ))
                .frame(width: isHovered ? 3 : 2)
                .padding(.vertical, 6)
        }
        .overlay(alignment: .bottom) {
            Rectangle().fill(.white.opacity(0.05)).frame(height: 1)
        }
        .contentShape(Rectangle())
        .onHover { hovering in
            withAnimation(.easeOut(duration: 0.16)) {
                hoveredProvider = hovering ? provider : (hoveredProvider == provider ? nil : hoveredProvider)
            }
        }
    }

    /// One-click Codex account switching: park the current login under a
    /// name, swap between parked logins, and opt into auto-rotation when
    /// the live account runs dry (owner call, 2026-08-08 — the codex-auto
    /// borrow, driven by real usage numbers instead of terminal scraping).
    private var codexAccountMenu: some View {
        Menu {
            let accounts = CodexAccountSwitcher.accounts()
            let active = CodexAccountSwitcher.activeLabel()
            if accounts.isEmpty {
                Text(L10n.tr("No saved accounts yet"))
            } else {
                ForEach(accounts) { account in
                    Button {
                        if CodexAccountSwitcher.activate(account) {
                            codexAccountsToken += 1
                            usage.refresh()
                        } else {
                            codexAccountError = true
                        }
                    } label: {
                        Text(account.label == active
                             ? "✓ \(account.label)"
                             : account.label)
                    }
                }
                Divider()
                Menu(L10n.tr("Remove saved account")) {
                    ForEach(accounts) { account in
                        Button(account.label) {
                            CodexAccountSwitcher.forget(account)
                            codexAccountsToken += 1
                        }
                    }
                }
                Divider()
            }
            Button(L10n.tr("Save current account…")) { promptParkCodexAccount() }
            Divider()
            Button {
                CodexAccountSwitcher.autoSwitchEnabled.toggle()
                codexAccountsToken += 1
            } label: {
                Text(CodexAccountSwitcher.autoSwitchEnabled
                     ? "✓ \(L10n.tr("Auto-switch when exhausted"))"
                     : L10n.tr("Auto-switch when exhausted"))
            }
        } label: {
            Image(systemName: usage.codexAutoSwitched == nil
                  ? "person.crop.circle" : "person.crop.circle.badge.checkmark")
                .font(.system(size: 11, weight: .semibold))
                .foregroundStyle(usage.codexAutoSwitched == nil
                                 ? .white.opacity(0.85)
                                 : IslandColor.chrome.opacity(0.9))
                .padding(.horizontal, 7)
                .padding(.vertical, 6)
                .background {
                    RoundedRectangle(cornerRadius: 6)
                        .fill(.white.opacity(0.09))
                }
        }
        .menuStyle(.button)
        .buttonStyle(.borderless)
        .menuIndicator(.hidden)
        .fixedSize()
        .id(codexAccountsToken)
        .help(L10n.tr("Switch Codex account"))
    }

    private func promptParkCodexAccount() {
        let alert = NSAlert()
        alert.messageText = L10n.tr("Save current account")
        alert.informativeText = L10n.tr("Give this login a name so you can switch back to it later")
        let field = NSTextField(frame: NSRect(x: 0, y: 0, width: 220, height: 24))
        field.placeholderString = L10n.tr("work / personal")
        alert.accessoryView = field
        alert.addButton(withTitle: L10n.tr("Save"))
        alert.addButton(withTitle: L10n.tr("Cancel"))
        guard alert.runModal() == .alertFirstButtonReturn else { return }
        if CodexAccountSwitcher.parkCurrent(as: field.stringValue) {
            codexAccountsToken += 1
        }
    }

    /// Paste-code sign-in: open Anthropic's page, let it show a code, take
    /// the code back through a plain text field. Independent of which
    /// browser or Google account the machine happens to have.
    private func startClaudePasteLogin() {
        guard let request = ClaudeCredentials.pasteLoginRequest() else { return }
        NSWorkspace.shared.open(request.url)
        let alert = NSAlert()
        alert.messageText = L10n.tr("Sign in with a code")
        alert.informativeText = L10n.tr("Approve the page that just opened, then paste the code it shows here")
        let field = NSTextField(frame: NSRect(x: 0, y: 0, width: 300, height: 24))
        field.placeholderString = L10n.tr("Paste the code")
        alert.accessoryView = field
        alert.addButton(withTitle: L10n.tr("Sign in"))
        alert.addButton(withTitle: L10n.tr("Cancel"))
        alert.addButton(withTitle: L10n.tr("Copy link"))
        let response = alert.runModal()
        if response == .alertThirdButtonReturn {
            NSPasteboard.general.clearContents()
            NSPasteboard.general.setString(request.url.absoluteString, forType: .string)
            return
        }
        guard response == .alertFirstButtonReturn else { return }
        let pasted = field.stringValue
        Task {
            let ok = await ClaudeCredentials.completePasteLogin(
                pasted: pasted, verifier: request.verifier, state: request.state
            )
            await MainActor.run {
                if ok {
                    usage.refresh()
                } else {
                    let fail = NSAlert()
                    fail.messageText = L10n.tr("That code did not work")
                    fail.informativeText = L10n.tr("Copy the whole code from the page and try once more")
                    fail.addButton(withTitle: L10n.tr("OK"))
                    fail.runModal()
                }
            }
        }
    }

    private func toggleProvider(_ provider: DisplayProvider) {
        withAnimation(.openMorph) {
            if !visibility.requestToggle(provider) {
                providerLimitAlert = true
            }
        }
        // A guest slot just turned on: fetch now instead of waiting out the
        // next usage poll (the guest stores gate themselves on the
        // selection, so an unselected provider never fetched).
        if visibility.isEnabled(provider) {
            switch provider {
            case .antigravity: AntigravityUsageStore.shared.kickRefresh()
            case .grok: GrokUsageStore.shared.kickRefresh()
            case .cursor: CursorUsageStore.shared.kickRefresh()
            case .claude, .codex: break
            }
        }
    }

    /// Guest rows read like the Claude/Codex rows — sync freshness first,
    /// then the quota numbers. The account email stays out of the default-
    /// visible line (owner report, 2026-08-08 — the row led with a bare
    /// email and read as a glitch); identity lives in the usage-strip hover.
    private var antigravitySubtitle: String {
        if visibility.antigravitySignedOut {
            // Installed but never signed in — name the one command that fixes
            // it instead of the flat "not detected" the owner hit after a
            // successful login (repro, 2026-08-08).
            return L10n.tr("Installed — run agy to sign in")
        }
        guard visibility.antigravityDetected else {
            return L10n.tr("Not detected — sign in with the antigravity CLI")
        }
        var parts: [String] = [guestSyncCaption(antigravityStore.lastUpdated)]
        if let caption = antigravityStore.statusCaption {
            parts.append("⚠ \(caption)")
        } else if let bucket = antigravityStore.snapshot?.primary {
            // One number, phrased exactly like Grok's row: the Gemini pool.
            // The Claude/GPT pool Antigravity also meters stays hidden
            // everywhere — those models are other providers' tiles in this
            // app (owner call, 2026-08-09).
            parts.append(L10n.tr("week %d%%", Int((bucket.usedPercent * 100).rounded())))
        }
        return parts.joined(separator: " · ")
    }

    private var grokSubtitle: String {
        guard visibility.grokDetected else {
            return L10n.tr("Not detected — sign in with the grok CLI")
        }
        var parts: [String] = [guestSyncCaption(grokStore.lastUpdated)]
        if let caption = grokStore.errorCaption {
            parts.append("⚠ \(caption)")
        } else if let snapshot = grokStore.snapshot {
            let percent = Int((snapshot.weeklyUsedPercent * 100).rounded())
            parts.append(L10n.tr("week %d%%", percent))
        }
        return parts.joined(separator: " · ")
    }

    private var cursorSubtitle: String {
        guard visibility.cursorDetected else {
            return L10n.tr("Not detected — sign in inside Cursor")
        }
        var parts: [String] = [guestSyncCaption(cursorStore.lastUpdated)]
        if let caption = cursorStore.errorCaption {
            parts.append("⚠ \(caption)")
        } else if let snapshot = cursorStore.snapshot {
            let percent = Int((snapshot.usedPercent * 100).rounded())
            parts.append(L10n.tr("cycle %d%%", percent))
        }
        return parts.joined(separator: " · ")
    }

    private func guestSyncCaption(_ updated: Date?) -> String {
        guard let updated else { return L10n.tr("idle") }
        return L10n.tr("synced %@", Self.relativeFormatter.localizedString(for: updated, relativeTo: Date()))
    }

    /// #31: the re-auth button rides the CURRENT auth state, not the mere
    /// presence of the `claude` binary on disk — the old
    /// `canPromptReauth()` gate meant the button never disappeared, even
    /// with a perfectly healthy login. Kept visible while a login flow is
    /// in flight so the "waiting" state doesn't vanish mid-flow. The
    /// in-app web login needs no CLI, so the binary check is gone entirely.
    private var claudeReauthAvailable: Bool {
        if usage.claudeReauthInProgress { return true }
        return ClaudeCredentials.isAuthRecoverableError(usage.claude.fiveHour.error)
            || ClaudeCredentials.isAuthRecoverableError(usage.claude.weekly.error)
    }



    /// Lets the user pick which token total drives the TOKENS hero on the
    /// cost screen. Anthropic's claude.ai stats panel reports input + output
    /// only; ccusage (and our default) sums every token type that crossed
    /// the wire — the two diverge by ~10× because cache_read_input_tokens
    /// dominates Claude Code workflows. Both totals are computed every
    /// scan, so flipping this is instant — no rescan.
    private var tokenCountingSection: some View {
        VStack(alignment: .leading, spacing: 0) {
            sectionLabel("Tokens")
            SettingsRow(
                title: "Token counting",
                subtitle: nil
            ) {
                tokenModeSegmented
            }
        }
        .padding(.horizontal, 14)
        .padding(.top, 14)
        .padding(.bottom, 4)
    }


    private var tokenModeSegmented: some View {
        SegmentedControl(
            items: TokenCountMode.allCases,
            selected: $tokenMode.mode,
            label: { $0.label },
            accessibilityPrefix: "Token counting"
        )
    }

    /// Single-row Cost section. Re-uses the section-label typography on the
    /// left and inlines the freshness caption + refresh button on the right
    /// — compact so it sits cleanly under the Providers list.
    private var costSection: some View {
        HStack(alignment: .center, spacing: 10) {
            Text(L10n.tr("Cost"))
                .font(SettingsType.section)
                .tracking(1.05)
                .textCase(.uppercase)
                .foregroundStyle(.white.opacity(0.34))

            Text(costSubtitle())
                .font(SettingsType.data)
                .foregroundStyle(.white.opacity(0.42))
                .lineLimit(1)
                .truncationMode(.tail)

            Spacer(minLength: 8)

            PillButton(
                label: cost.loading ? "Refreshing…" : "Refresh",
                isLoading: cost.loading
            ) { cost.refresh() }
        }
        .padding(.horizontal, 24)
        .padding(.top, 14)
        .padding(.bottom, 14)
    }

    /// Days past the embedded pricing snapshot before the Cost section
    /// admits the data may be stale. Anthropic re-tiered Opus once already,
    /// so two months without a refresh is the point where dollar totals
    /// could meaningfully drift from reality.
    private static let pricingFreshnessThreshold = 60

    // Computed, not cached: a `static let` would freeze the locale at first
    // use and keep formatting dates in the old language after a switch.
    private static var relativeFormatter: RelativeDateTimeFormatter {
        let f = RelativeDateTimeFormatter()
        f.locale = L10n.locale
        f.unitsStyle = .abbreviated
        return f
    }

    private func costSubtitle() -> String {
        let days = Pricing.daysSinceSnapshot
        let isStale = days > Self.pricingFreshnessThreshold

        if cost.loading {
            return isStale
                ? L10n.tr("scanning local logs… · pricing data %dd old", days)
                : L10n.tr("scanning local logs…")
        } else if let updated = cost.lastUpdated {
            let relative = Self.relativeFormatter.localizedString(for: updated, relativeTo: Date())
            return isStale
                ? L10n.tr("last scan %@ · pricing data %dd old", relative, days)
                : L10n.tr("last scan %@", relative)
        }

        return isStale
            ? L10n.tr("swipe panel to view · pricing data %dd old", days)
            : L10n.tr("swipe panel to view")
    }

    private var chartSection: some View {
        VStack(alignment: .leading, spacing: 0) {
            sectionLabel("Usage display")
            ChartStylePicker(selected: $stylePref.style)
                .padding(.top, 4)
                .padding(.horizontal, 10)
            SettingsRow(
                title: "Quota shows",
                subtitle: nil
            ) {
                SegmentedControl(
                    items: [false, true],
                    selected: $quotaMode.showsRemaining,
                    label: { $0 ? "Remaining" : "Used" },
                    accessibilityPrefix: "Quota shows"
                )
            }
        }
        .padding(.horizontal, 14)
        .padding(.top, 18)
        .padding(.bottom, 14)
    }

    private var costStyleSection: some View {
        VStack(alignment: .leading, spacing: 0) {
            // The enable toggle rides the section header itself (owner call,
            // 1.7.2 planning) — the old ⌘-click hint slot; no separate row.
            HStack(alignment: .center) {
                Text(L10n.tr("Cost display"))
                    .font(SettingsType.section)
                    .tracking(1.05)
                    .textCase(.uppercase)
                    .foregroundStyle(.white.opacity(0.34))
                Spacer(minLength: 8)
                SettingsToggle(isOn: costPanelVisibility.showInTopPanel) {
                    withAnimation(.pageSwipe) {
                        costPanelVisibility.showInTopPanel.toggle()
                        ScreenPref.shared.ensureVisibleScreen()
                    }
                }
            }
            .padding(.horizontal, 10)
            .padding(.bottom, 6)
            if costPanelVisibility.showInTopPanel {
                CostStylePicker(selected: $costStylePref.style)
                    .padding(.top, 4)
                    .padding(.horizontal, 10)
                    .transition(.opacity.combined(with: .move(edge: .top)))
            }
        }
        .padding(.horizontal, 14)
        .padding(.top, 14)
        .padding(.bottom, 14)
    }

    private var topPanelSection: some View {
        VStack(alignment: .leading, spacing: 0) {
            sectionLabel("Top bar")
            // One explanatory line per mode (owner call, 1.7.2: the picker
            // alone left users guessing what each mode costs).
            SettingsRow(
                title: "Visual mode",
                subtitle: lowPower.enabled
                    ? "Calm: no ambient light, the lowest GPU and CPU cost"
                    : "Vivid: halo and orbit sweep, a touch more GPU"
            ) {
                effectsPicker
            }
            // Glow options belong to Vivid: Calm is the zero-effects mode,
            // so a color row under it would configure nothing.
            if !lowPower.enabled {
                SettingsRow(
                    title: "Glow color",
                    subtitle: nil
                ) {
                    glowColorSwatches
                }
            }
            SettingsRow(
                title: "Interface scale",
                subtitle: nil
            ) {
                interfaceScalePicker
            }
            SettingsRow(
                title: "Always show usage in top bar",
                subtitle: nil
            ) {
                SettingsToggle(isOn: alwaysShow.enabled) {
                    alwaysShow.enabled.toggle()
                }
            }
        }
        .padding(.horizontal, 14)
        .padding(.top, 14)
        .padding(.bottom, 14)
    }



    private var missionControlSection: some View {
        VStack(alignment: .leading, spacing: 0) {
            sectionLabel("Mission Control", hint: nil)
            SettingsRow(
                title: "Hide in Mission Control",
                subtitle: nil
            ) {
                SettingsToggle(isOn: missionControlHide.enabled) {
                    missionControlHide.enabled.toggle()
                }
            }
        }
        .padding(.horizontal, 14)
        .padding(.bottom, 14)
    }

    private var targetDisplaySection: some View {
        VStack(alignment: .leading, spacing: 0) {
            sectionLabel("Screen")
            SettingsRow(
                title: "Show on",
                subtitle: targetDisplaySubtitle
            ) {
                targetDisplayPicker
            }
        }
        .padding(.horizontal, 14)
        .padding(.top, 14)
        .padding(.bottom, 14)
    }

    /// Subtitle shows the resolved current display when the user is on
    /// `.auto` — answers "where is the island actually?" without making
    /// the user open another setting.
    private var targetDisplaySubtitle: String {
        switch targetDisplay.choice {
        case .auto:
            if let resolved = DisplayInfo.currentTarget() {
                return L10n.tr("Auto — showing on %@.", resolved.displayName)
            }
            return L10n.tr("Auto — picks the best available screen.")
        case .stable:
            return L10n.tr("Pinned to a specific display. Falls back to Auto if unplugged.")
        }
    }

    private var targetDisplayPicker: some View {
        let displays = DisplayInfo.all()
        let autoTag = IslandTargetDisplayStore.Choice.auto.rawValue
        return Picker("", selection: pickerSelection) {
            Text(L10n.tr("Auto")).tag(autoTag)
            ForEach(displays, id: \.stableID) { d in
                Text(d.displayName)
                    .tag(d.stableID)
            }
        }
        .labelsHidden()
        .pickerStyle(.menu)
        .frame(maxWidth: 220)
        .accessibilityLabel(L10n.tr("Screen position"))
    }

    /// Bridges the enum `Choice` to a `String` selection that SwiftUI's
    /// `Picker` can use as tags.
    private var pickerSelection: Binding<String> {
        Binding(
            get: { targetDisplay.choice.rawValue },
            set: { newValue in
                targetDisplay.choice = IslandTargetDisplayStore.Choice(rawValue: newValue)
            }
        )
    }

    // MARK: - Refresh segmented

    private var refreshSegmented: some View {
        SegmentedControl(
            items: RefreshIntervalStore.allowed,
            selected: $refreshStore.seconds,
            label: { Self.label(for: $0) },
            accessibilityPrefix: "Refresh interval"
        )
    }

    private static func label(for seconds: Int) -> String {
        switch seconds {
        case 300: return "5m"
        case 900: return "15m"
        case 1800: return "30m"
        default: return "\(seconds)s"
        }
    }

    // MARK: - Subtitle composition

    private func providerSubtitle(_ u: AppUsage) -> String {
        let synced: String = {
            guard let updated = usage.lastUpdated else { return L10n.tr("idle") }
            return L10n.tr("synced %@", Self.relativeFormatter.localizedString(for: updated, relativeTo: Date()))
        }()
        // Codex's weekly-only world: one window, one number — the second
        // slot would just read "⚠ no data" forever.
        let five = Self.windowCaption(u.fiveHour)
        let week = Self.windowCaption(u.weekly)
        // Both windows failing the same way is ONE fact — "⚠ login required
        // / ⚠ login required" read as a stutter bug (owner screenshot,
        // 2026-08-08).
        let nums = (u.secondaryMissing || (five == week && five.hasPrefix("⚠")))
            ? five
            : "\(five) / \(week)"
        return "\(synced) · \(nums)"
    }

    private static func windowCaption(_ w: WindowUsage) -> String {
        // Known error sentinels carry Localizable entries; unknown strings
        // pass through L10n.tr unchanged.
        if let err = w.error, w.percentInt == 0 { return "⚠ \(L10n.tr(err))" }
        return "\(w.percentInt)%"
    }
}
