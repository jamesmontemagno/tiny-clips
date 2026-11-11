import AppKit
import SwiftUI

@MainActor
class OnboardingWizardWindow: NSWindow, NSWindowDelegate {
    private var onComplete: ((Bool) -> Void)?
    private var didComplete = false

    convenience init(
        applyShortcut: @escaping (HotKeyBinding, HotKeyAction) -> String?,
        onComplete: @escaping (Bool) -> Void
    ) {
        self.init(
            contentRect: NSRect(x: 0, y: 0, width: 680, height: 640),
            styleMask: [.titled, .closable, .resizable],
            backing: .buffered,
            defer: false
        )

        self.onComplete = onComplete
        self.delegate = self
        self.isReleasedWhenClosed = false
        self.contentMinSize = NSSize(width: 580, height: 530)
        self.title = "Welcome to Tiny Clips"
        self.center()

        let hostingView = NSHostingView(rootView: OnboardingWizardView(
            applyShortcut: applyShortcut,
            onFinish: { [weak self] in
                self?.finish(completed: true)
            },
            onSkip: { [weak self] in
                self?.finish(completed: true)
            }
        ))
        self.contentView = hostingView
    }

    func windowWillClose(_ notification: Notification) {
        guard !didComplete else { return }
        didComplete = true
        let callback = onComplete
        onComplete = nil
        callback?(false)
    }

    private func finish(completed: Bool) {
        guard !didComplete else { return }
        didComplete = true
        let callback = onComplete
        onComplete = nil
        callback?(completed)
        close()
    }
}

private enum OnboardingStep: Int, CaseIterable {
    case welcome
    case screen
    case optional
    case settings
    case shortcuts
#if !APPSTORE
    case updates
#endif
    case complete

    var title: String {
        switch self {
        case .welcome:
            return "Get started quickly"
        case .screen:
            return "Allow Screen Recording"
        case .optional:
            return "Optional Permissions"
        case .settings:
            return "Make it yours"
        case .shortcuts:
            return "Your capture shortcuts"
#if !APPSTORE
        case .updates:
            return "Automatic Updates"
#endif
        case .complete:
            return "You're all set"
        }
    }
}

private struct OnboardingWizardView: View {
    @ObservedObject private var settings = CaptureSettings.shared
    @ObservedObject private var launchAtLogin = LaunchAtLoginManager.shared
    @AppStorage("copyScreenshotToClipboard") private var copyScreenshotToClipboard = true
    @AppStorage("showScreenshotEditor") private var showScreenshotEditor = true
    @AppStorage("showSaveNotifications") private var showSaveNotifications = false
    @AppStorage("videoCountdownEnabled") private var videoCountdownEnabled = true
    @AppStorage("gifCountdownEnabled") private var gifCountdownEnabled = true
#if !APPSTORE
    @ObservedObject private var sparkleController = SparkleController.shared
#endif
    @State private var step: OnboardingStep = .welcome
    @State private var screenGranted = false
    @State private var microphoneGranted = false
    @State private var notificationsGranted = false
    @State private var showSkipConfirmation = false
    @State private var shortcutError: String?
    @State private var shortcutRevision = 0
    @State private var notificationPermission: NotificationPermissionState?
    @State private var isRequestingNotifications = false

    let applyShortcut: (HotKeyBinding, HotKeyAction) -> String?
    let onFinish: () -> Void
    let onSkip: () -> Void

    var body: some View {
        VStack(alignment: .leading, spacing: 14) {
            Text(step.title)
                .font(.headline)
                .accessibilityAddTraits(.isHeader)

            ScrollView {
                stepContent
                    .frame(maxWidth: .infinity, alignment: .topLeading)
            }
            .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topLeading)

            Divider()

            HStack {
                Text("Step \(step.rawValue + 1) of \(OnboardingStep.allCases.count)")
                    .font(.caption)
                    .foregroundStyle(.secondary)

                Spacer()

                if step != .welcome {
                    Button("Back") {
                        previousStep()
                    }
                    .help("Go to the previous step.")
                }

                Button("Skip") {
                    showSkipConfirmation = true
                }
                .keyboardShortcut(.cancelAction)
                .help("Skip setup for now.")

                Button(primaryButtonTitle) {
                    handlePrimaryAction()
                }
                .keyboardShortcut(.defaultAction)
                .help(isLastStep ? "Finish setup." : "Continue to the next step.")
            }
        }
        .padding(20)
        .onAppear {
            refreshStatus()
        }
        .onReceive(NotificationCenter.default.publisher(for: NSApplication.didBecomeActiveNotification)) { _ in
            refreshStatus()
            shortcutRevision += 1
        }
        .alert("Skip setup?", isPresented: $showSkipConfirmation) {
            Button("Cancel", role: .cancel) {}
                .keyboardShortcut(.defaultAction)
                .help("Stay in setup.")
            Button("Skip Setup") {
                onSkip()
            }
            .help("Close setup and use TinyClips with current settings.")
        } message: {
            Text("Changes already made will be kept. You can run setup later from Settings.")
        }
    }

    @ViewBuilder
    private var stepContent: some View {
        switch step {
        case .welcome:
            welcomeContent
        case .screen:
            screenPermissionContent
        case .optional:
            optionalPermissionsContent
        case .settings:
            commonSettingsContent
        case .shortcuts:
            shortcutsContent
        case .complete:
            completionContent
#if !APPSTORE
        case .updates:
            updatesContent
#endif
        }
    }

    private var welcomeContent: some View {
        VStack(alignment: .leading, spacing: 14) {
            HStack(spacing: 12) {
                if let appIcon = NSImage(named: "AppIcon") {
                    Image(nsImage: appIcon)
                        .resizable()
                        .frame(width: 56, height: 56)
                        .cornerRadius(12)
                }

                VStack(alignment: .leading, spacing: 2) {
                    Text("TinyClips")
                        .font(.title3.weight(.semibold))
                    Text("Fast captures from your menu bar")
                        .font(.subheadline)
                        .foregroundStyle(.secondary)
                }
            }

            VStack(alignment: .leading, spacing: 10) {
                Label("Screen Recording is required to capture your screen.", systemImage: "display")
                Label("Microphone and Notifications are optional.", systemImage: "slider.horizontal.3")
                Label("With Studio enabled in Settings, choose Studio Recording... from the menu bar to record your screen and optional camera separately, edit the project, and export an MP4.", systemImage: "square.stack.3d.up")
                Label("This setup only takes a moment.", systemImage: "sparkles")
            }
            .font(.callout)
            .padding(12)
            .background(.quaternary.opacity(0.5), in: RoundedRectangle(cornerRadius: 10))

            VStack(alignment: .leading, spacing: 6) {
                Text("Capture shortcuts")
                    .font(.subheadline.weight(.semibold))

                shortcutRow("Screenshot", action: .screenshot)
                shortcutRow("Record Video", action: .recordVideo)
                shortcutRow("Record GIF", action: .recordGif)

                Text("Change these anytime in Settings → Shortcuts.")
                    .font(.caption)
                    .foregroundStyle(.secondary)
            }
            .padding(12)
            .background(.quaternary.opacity(0.5), in: RoundedRectangle(cornerRadius: 10))
        }
    }

    private func shortcutRow(_ title: String, action: HotKeyAction) -> some View {
        HStack {
            Text(title)
            Spacer()
            Text(settings.hotKeyBinding(for: action).displayString)
                .font(.system(.callout, design: .monospaced))
                .foregroundStyle(.secondary)
        }
        .accessibilityElement(children: .combine)
    }

    private var screenPermissionContent: some View {
        VStack(alignment: .leading, spacing: 12) {
            permissionRow(
                title: "Screen Recording",
                isGranted: screenGranted,
                grantedText: "Allowed",
                deniedText: "Not allowed"
            )

            HStack(spacing: 10) {
                Button("Allow Screen Recording") {
                    requestScreenPermission()
                }
                .help("Request Screen Recording access.")

                Button("Re-check") {
                    recheckScreenPermission()
                }
                .help("Check Screen Recording access again.")

                Button("Open System Settings") {
                    PermissionManager.shared.openScreenRecordingSettings()
                }
                .help("Open Screen Recording settings.")
            }

            HStack(alignment: .top, spacing: 8) {
                Image(systemName: "info.circle")
                    .foregroundStyle(.secondary)

                Text("After enabling Screen Recording in System Settings, you must restart TinyClips for the change to apply.")
                    .font(.caption)
                    .foregroundStyle(.secondary)
            }
            .padding(10)
            .background(.quaternary.opacity(0.5), in: RoundedRectangle(cornerRadius: 8))
        }
    }

    private var optionalPermissionsContent: some View {
        VStack(alignment: .leading, spacing: 12) {
            permissionRow(
                title: "Microphone",
                isGranted: microphoneGranted,
                grantedText: "Allowed",
                deniedText: "Not allowed"
            )

            HStack(spacing: 10) {
                Button(microphoneGranted ? "Re-check" : "Continue") {
                    requestMicrophonePermission()
                }
                .help(microphoneGranted ? "Check microphone access again." : "Continue without microphone access.")

                Button("Open Microphone Settings") {
                    PermissionManager.shared.openMicrophoneSettings()
                }
                .help("Open microphone settings.")
            }

            Divider()

            permissionRow(
                title: "Notifications",
                isGranted: notificationsGranted,
                grantedText: "Allowed",
                deniedText: "Not allowed"
            )

            HStack(spacing: 10) {
                Button(notificationsGranted ? "Re-check" : "Allow Notifications") {
                    requestNotificationPermission()
                }
                .help(notificationsGranted ? "Check notification access again." : "Request notification access.")

                Button("Open Notifications Settings") {
                    guard PermissionManager.shared.openNotificationSettings() else {
                        SaveService.shared.showError("Could not open macOS Notification Settings.")
                        return
                    }
                }
                .help("Open notification settings.")
            }
        }
    }

    private var commonSettingsContent: some View {
        VStack(alignment: .leading, spacing: 12) {
            Text("Changes apply as you make them. You can change these later in Settings.")
                .font(.subheadline)
                .foregroundStyle(.secondary)

            preferenceToggleRow(
                title: "Launch at login",
                description: "Keep menu bar capture shortcuts ready when you sign in.",
                binding: Binding(get: { launchAtLogin.isEnabled }, set: { launchAtLogin.setEnabled($0) })
            )

            preferenceToggleRow(
                title: "Copy screenshots to clipboard",
                description: "Paste screenshots straight into a chat or document.",
                binding: $copyScreenshotToClipboard
            )

            preferenceToggleRow(
                title: "Open screenshot editor",
                description: "Annotate or crop after capturing. Turn off for fewer interruptions.",
                binding: Binding(
                    get: { showScreenshotEditor },
                    set: { enabled in
                        showScreenshotEditor = enabled
                        if !enabled { settings.saveImmediatelyScreenshot = true }
                    }
                )
            )

            preferenceToggleRow(
                title: "Recording countdown",
                description: "Get ready before recording video or GIF.",
                binding: Binding(
                    get: { videoCountdownEnabled && gifCountdownEnabled },
                    set: { settings.setRecordingCountdown($0) }
                )
            )
            if videoCountdownEnabled != gifCountdownEnabled {
                Text("Video and GIF currently have different settings.")
                    .font(.caption)
                    .foregroundStyle(.secondary)
            }

            preferenceToggleRow(
                title: "Notify after saving",
                description: "Confirm when your capture has been saved. System permission is separate.",
                binding: $showSaveNotifications
            )

            notificationStatusContent

            DisclosureGroup("Additional capture options") {
                VStack(alignment: .leading, spacing: 12) {
                    preferenceToggleRow(
                        title: "Save screenshots immediately",
                        description: "Save right away instead of waiting for editor actions.",
                        binding: $settings.saveImmediatelyScreenshot
                    )
                    .disabled(!showScreenshotEditor)
                    if !showScreenshotEditor {
                        Text("Save immediately stays enabled while the screenshot editor is turned off.")
                            .font(.caption)
                            .foregroundStyle(.secondary)
                    }

                    capturePreferenceCard(
                        title: "Videos",
                        subtitle: "Decide whether recordings open in the trimmer or save immediately.",
                        symbolName: "video",
                        accentColor: .red,
                        openLabel: "Open trimmer after recording",
                        openBinding: $settings.showTrimmer,
                        saveBinding: $settings.saveImmediatelyVideo,
                        copyBinding: $settings.copyVideoToClipboard,
                        openHelp: "Open the video trimmer after recording so you can trim before finishing.",
                        saveHelp: "When enabled, videos save right away instead of waiting for trimmer actions.",
                        copyHelp: "Copy saved videos to the clipboard as a file URL."
                    )

                    capturePreferenceCard(
                        title: "GIFs",
                        subtitle: "Set the default post-capture flow for GIF recordings.",
                        symbolName: "sparkles.rectangle.stack",
                        accentColor: .orange,
                        openLabel: "Open trimmer after recording",
                        openBinding: $settings.showGifTrimmer,
                        saveBinding: $settings.saveImmediatelyGif,
                        copyBinding: $settings.copyGifToClipboard,
                        openHelp: "Open the GIF trimmer after recording so you can trim before finishing.",
                        saveHelp: "When enabled, GIFs save right away instead of waiting for trimmer actions.",
                        copyHelp: "Copy saved GIFs to the clipboard as a file URL."
                    )
                }
                .padding(.top, 8)
            }
        }
    }

    private var notificationStatusContent: some View {
        VStack(alignment: .leading, spacing: 6) {
            Text("System notifications: \(notificationPermission?.summary ?? "Checking…")")
                .font(.caption)
                .foregroundStyle(.secondary)
                .accessibilityElement(children: .combine)
            if let notificationPermission {
                if notificationPermission.canRequest {
                    Button("Allow Notifications") { requestNotificationPermission() }
                        .disabled(isRequestingNotifications)
                } else {
                    Button("Open Notification Settings") {
                        guard PermissionManager.shared.openNotificationSettings() else {
                            SaveService.shared.showError("Could not open macOS Notification Settings.")
                            return
                        }
                    }
                }
            }
        }
    }

    private var shortcutsContent: some View {
        VStack(alignment: .leading, spacing: 12) {
            Text("These shortcuts work system-wide. Record Video makes an ordinary recording, not Studio.")
                .font(.subheadline)
                .foregroundStyle(.secondary)

            if let shortcutError {
                Label(shortcutError, systemImage: "exclamationmark.triangle.fill")
                    .foregroundStyle(.red)
                    .accessibilityLabel("Shortcut error: \(shortcutError)")
            }

            ForEach([HotKeyAction.screenshot, .recordVideo, .recordGif], id: \.self) { action in
                ShortcutRecorderField(
                    label: action.displayName,
                    iconName: nil,
                    binding: settings.hotKeyBinding(for: action),
                    defaultBinding: HotKeyBinding.defaultBinding(for: action),
                    onBindingRecorded: { binding in
                        shortcutError = applyShortcut(binding, action)
                        shortcutRevision += 1
                    }
                )
            }

            Text("Stop Recording: \(HotKeyBinding.stopRecording.displayString) (fixed)")
            Text("More shortcuts are available in Settings → Shortcuts. Changes already made remain if you skip setup.")
                .font(.caption)
                .foregroundStyle(.secondary)
        }
        .id(shortcutRevision)
    }

#if !APPSTORE
    private var updatesContent: some View {
        VStack(alignment: .leading, spacing: 14) {
            Text("TinyClips can automatically check for new versions so you always have the latest features and fixes.")
                .font(.subheadline)
                .foregroundStyle(.secondary)

            Toggle("Automatically check for updates", isOn: Binding(
                get: { sparkleController.automaticallyChecksForUpdates },
                set: { sparkleController.automaticallyChecksForUpdates = $0 }
            ))
            .help("When enabled, TinyClips periodically checks for updates and shows a notification when one is available.")

            HStack(alignment: .top, spacing: 8) {
                Image(systemName: "info.circle")
                    .foregroundStyle(.secondary)

                Text("When an update is found, you'll see what's new and choose when to install. You can change this later in Settings → About.")
                    .font(.caption)
                    .foregroundStyle(.secondary)
            }
            .padding(10)
            .background(.quaternary.opacity(0.5), in: RoundedRectangle(cornerRadius: 8))
        }
    }
#endif

    private var completionContent: some View {
        VStack(alignment: .leading, spacing: 16) {
            HStack(spacing: 16) {
                Image(systemName: "apple.logo")
                Text("Tiny Clips")
                    .fontWeight(.semibold)
                Spacer()
                Image(systemName: "camera.viewfinder")
                    .padding(8)
                    .background(Color.accentColor.opacity(0.2), in: RoundedRectangle(cornerRadius: 6))
                    .overlay(RoundedRectangle(cornerRadius: 6).stroke(Color.accentColor, lineWidth: 2))
                Image(systemName: "wifi")
                Text("12:00")
            }
            .padding(12)
            .background(.quaternary.opacity(0.5), in: RoundedRectangle(cornerRadius: 10))
            .accessibilityElement(children: .ignore)
            .accessibilityLabel("macOS menu bar illustration. Tiny Clips is the camera icon near the clock.")

            Label("Find Tiny Clips in your menu bar", systemImage: "arrow.up")
                .font(.headline)
            Text("Click the camera icon at the top of your screen to start a capture or open Settings.")
                .foregroundStyle(.secondary)

            Text("Or use a key binding")
                .font(.subheadline.weight(.semibold))
            shortcutRow("Screenshot", action: .screenshot)
            shortcutRow("Record Video", action: .recordVideo)
            shortcutRow("Record GIF", action: .recordGif)
            Text("You can change your preferences and shortcuts anytime in Settings.")
                .font(.caption)
                .foregroundStyle(.secondary)
        }
    }

    private var isLastStep: Bool {
        step == OnboardingStep.allCases.last
    }

    private var primaryButtonTitle: String {
        switch step {
        case .welcome:
            return "Get Started"
        default:
            return isLastStep ? "Finish" : "Next"
        }
    }

    private func handlePrimaryAction() {
        if isLastStep {
            onFinish()
        } else if let next = OnboardingStep(rawValue: step.rawValue + 1) {
            step = next
        }
    }

    private func previousStep() {
        guard let previous = OnboardingStep(rawValue: step.rawValue - 1) else { return }
        step = previous
    }

    private func refreshStatus() {
        launchAtLogin.refresh()
        screenGranted = PermissionManager.shared.hasScreenRecordingPermission()
        microphoneGranted = PermissionManager.shared.microphonePermissionGranted()

        Task {
            notificationsGranted = await PermissionManager.shared.notificationPermissionGranted()
            notificationPermission = await PermissionManager.shared.notificationPermissionState()
        }
    }

    private func requestScreenPermission() {
        Task {
            let granted = await PermissionManager.shared.checkPermission()
            screenGranted = granted || PermissionManager.shared.hasScreenRecordingPermission()
        }
    }

    private func recheckScreenPermission() {
        screenGranted = PermissionManager.shared.hasScreenRecordingPermission()
    }

    private func requestMicrophonePermission() {
        Task {
            microphoneGranted = await PermissionManager.shared.requestMicrophonePermission()
        }
    }

    private func requestNotificationPermission() {
        isRequestingNotifications = true
        Task {
            defer { isRequestingNotifications = false }
            notificationsGranted = await PermissionManager.shared.requestNotificationPermission()
            notificationPermission = await PermissionManager.shared.notificationPermissionState()
            if notificationPermission?.canRequest == true {
                SaveService.shared.showError("Could not request notification permission. Try again or enable notifications in macOS System Settings.")
            }
        }
    }

    private func permissionRow(title: String, isGranted: Bool, grantedText: String, deniedText: String) -> some View {
        HStack(spacing: 8) {
            Image(systemName: isGranted ? "checkmark.circle.fill" : "xmark.circle")
                .foregroundStyle(isGranted ? .green : .secondary)

            Text(title)
                .fontWeight(.medium)

            Spacer()

            Text(isGranted ? grantedText : deniedText)
                .foregroundStyle(.secondary)
        }
        .accessibilityElement(children: .ignore)
        .accessibilityLabel(title)
        .accessibilityValue(isGranted ? grantedText : deniedText)
    }

    private func capturePreferenceCard(
        title: String,
        subtitle: String,
        symbolName: String,
        accentColor: Color,
        openLabel: String,
        openBinding: Binding<Bool>,
        saveBinding: Binding<Bool>,
        copyBinding: Binding<Bool>,
        openHelp: String,
        saveHelp: String,
        copyHelp: String
    ) -> some View {
        VStack(alignment: .leading, spacing: 12) {
            HStack(alignment: .top, spacing: 12) {
                Image(systemName: symbolName)
                    .font(.title3)
                    .foregroundStyle(accentColor)
                    .frame(width: 28)
                    .accessibilityHidden(true)

                VStack(alignment: .leading, spacing: 3) {
                    Text(title)
                        .font(.headline)

                    Text(subtitle)
                        .font(.subheadline)
                        .foregroundStyle(.secondary)
                }
            }

            preferenceToggleRow(
                title: openLabel,
                description: openHelp,
                binding: openBinding
            )
                .onChange(of: openBinding.wrappedValue) { _, isEnabled in
                    if !isEnabled {
                        saveBinding.wrappedValue = true
                    }
                }

            preferenceToggleRow(
                title: "Save immediately",
                description: saveHelp,
                binding: saveBinding
            )
                .disabled(!openBinding.wrappedValue)

            if !openBinding.wrappedValue {
                Text("Save immediately stays enabled while the editor or trimmer is turned off.")
                    .font(.caption)
                    .foregroundStyle(.secondary)
                    .padding(.top, -4)
            }

            preferenceToggleRow(
                title: "Copy to clipboard",
                description: copyHelp,
                binding: copyBinding
            )
        }
        .padding(14)
        .background(.quaternary.opacity(0.45), in: RoundedRectangle(cornerRadius: 14))
    }

    private func preferenceToggleRow(title: String, description: String, binding: Binding<Bool>) -> some View {
        HStack(alignment: .center, spacing: 10) {
            Toggle("", isOn: binding)
                .labelsHidden()
                .accessibilityLabel(title)
                .toggleStyle(.switch)

            VStack(alignment: .leading, spacing: 2) {
                Text(title)
                    .font(.callout.weight(.medium))

                Text(description)
                    .font(.caption)
                    .foregroundStyle(.secondary)
                    .fixedSize(horizontal: false, vertical: true)
            }

            Spacer(minLength: 0)
        }
        .frame(maxWidth: .infinity, alignment: .leading)
        .help(description)
    }
}
