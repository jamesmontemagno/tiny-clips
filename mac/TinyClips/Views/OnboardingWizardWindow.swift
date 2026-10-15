import AppKit
import SwiftUI

@MainActor
class OnboardingWizardWindow: NSWindow, NSWindowDelegate {
    private var onComplete: ((Bool) -> Void)?
    private var didComplete = false

    convenience init(onComplete: @escaping (Bool) -> Void) {
        self.init(
            contentRect: NSRect(x: 0, y: 0, width: 460, height: 390),
            styleMask: [.titled, .closable],
            backing: .buffered,
            defer: false
        )

        self.onComplete = onComplete
        self.delegate = self
        self.isReleasedWhenClosed = false
        self.title = "Welcome to Tiny Clips"
        self.center()

        let hostingView = NSHostingView(rootView: OnboardingWizardView(
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
        onComplete?(false)
        onComplete = nil
    }

    private func finish(completed: Bool) {
        guard !didComplete else { return }
        didComplete = true
        onComplete?(completed)
        onComplete = nil
        close()
    }
}

private enum OnboardingStep: Int, CaseIterable {
    case welcome
    case screen
    case optional
    case settings
#if !APPSTORE
    case updates
#endif

    var title: String {
        switch self {
        case .welcome:
            return "Get started quickly"
        case .screen:
            return "Allow Screen Recording"
        case .optional:
            return "Optional Permissions"
        case .settings:
            return "Common Capture Settings"
#if !APPSTORE
        case .updates:
            return "Automatic Updates"
#endif
        }
    }
}

private struct OnboardingWizardView: View {
    @ObservedObject private var settings = CaptureSettings.shared
#if !APPSTORE
    @ObservedObject private var sparkleController = SparkleController.shared
#endif
    @State private var step: OnboardingStep = .welcome
    @State private var screenGranted = false
    @State private var microphoneGranted = false
    @State private var notificationsGranted = false
    @State private var showSkipConfirmation = false

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
        .alert("Skip setup?", isPresented: $showSkipConfirmation) {
            Button("Cancel", role: .cancel) {}
                .help("Stay in setup.")
            Button("Skip Setup", role: .destructive) {
                onSkip()
            }
            .help("Close setup and use TinyClips with current settings.")
        } message: {
            Text("You can run setup later from Settings.")
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
                Label("This setup only takes a moment.", systemImage: "sparkles")
            }
            .font(.callout)
            .padding(12)
            .background(.quaternary.opacity(0.5), in: RoundedRectangle(cornerRadius: 10))
        }
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
                    PermissionManager.shared.openNotificationSettings()
                }
                .help("Open notification settings.")
            }
        }
    }

    private var commonSettingsContent: some View {
        VStack(alignment: .leading, spacing: 12) {
            Text("Choose your default capture behavior. You can change this any time in Settings.")
                .font(.subheadline)
                .foregroundStyle(.secondary)

            capturePreferenceCard(
                title: "Screenshots",
                subtitle: "Control what happens after each screenshot finishes.",
                symbolName: "camera.viewfinder",
                accentColor: .blue,
                openLabel: "Open editor after capture",
                openBinding: $settings.showScreenshotEditor,
                saveBinding: $settings.saveImmediatelyScreenshot,
                copyBinding: $settings.copyScreenshotToClipboard,
                openHelp: "Open the screenshot editor after capture so you can annotate or crop before finishing.",
                saveHelp: "When enabled, screenshots save right away instead of waiting for editor actions.",
                copyHelp: "Copy screenshots to the clipboard as an image, even before saving in the editor."
            )

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
        screenGranted = PermissionManager.shared.hasScreenRecordingPermission()
        microphoneGranted = PermissionManager.shared.microphonePermissionGranted()

        Task {
            notificationsGranted = await PermissionManager.shared.notificationPermissionGranted()
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
        Task {
            notificationsGranted = await PermissionManager.shared.requestNotificationPermission()
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
