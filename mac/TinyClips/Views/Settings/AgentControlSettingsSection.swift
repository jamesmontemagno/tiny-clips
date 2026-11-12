import AppKit
import SwiftUI

struct AgentControlSettingsSection: View {
    @ObservedObject private var controller = AutomationController.shared
    @AppStorage(AutomationController.enabledKey) private var automationEnabled = false
    @State private var permissionsClientID: String?

    var body: some View {
        SettingsFormSection(
            "Agent Control",
            icon: "terminal",
            footer: "Each agent needs approval once and only gets the permissions you allow. Deleting, uploading, and discarding always ask you first."
        ) {
            SettingsToggleRow(
                title: "Allow agents to control Tiny Clips",
                description: "Lets AI agents and scripts on this Mac use Tiny Clips through the Agent Plugin, MCP server, or command-line tool.",
                isOn: $automationEnabled,
                help: "Starts a local endpoint that only apps running as your user can reach."
            )

            if automationEnabled {
                AgentConnectionRows(
                    isRunning: controller.isRunning,
                    lastError: controller.lastError,
                    endpointPath: controller.endpointPath,
                    retry: { controller.retryStart() }
                )
            }
        }

        SettingsFormSection("Paired Agents", icon: "person.2") {
            if controller.clients.isEmpty {
                VStack(alignment: .leading, spacing: 2) {
                    Text("No agents are paired")
                    Text("Follow Get Started below to install the plugin or CLI, then pair an agent.")
                        .settingsDescription()
                }
                .accessibilityElement(children: .combine)
            } else {
                ForEach(controller.clients) { client in
                    AgentClientRow(
                        client: client,
                        isEditingPermissions: Binding(
                            get: { permissionsClientID == client.id },
                            set: { isEditing in
                                if isEditing {
                                    permissionsClientID = client.id
                                } else if permissionsClientID == client.id {
                                    permissionsClientID = nil
                                }
                            }
                        ),
                        revoke: { controller.revoke(client) }
                    )
                }
            }
        }

        SettingsFormSection("Activity", icon: "text.page") {
            SettingsActionRow(
                title: "Audit log",
                description: "Every agent request is recorded in a local audit log."
            ) {
                Button("Open Audit Log") {
                    openAuditLog()
                }
                .help("Show the agent activity log in Finder.")
            }
        }

        SettingsFormSection("Get Started", icon: "sparkles") {
            AgentControlSetupSteps()
        }

        SettingsAdvancedSection("Set Up Without a Plugin", icon: "wrench.and.screwdriver") {
            AgentControlManualSetupSteps()
        }
    }

    private func openAuditLog() {
        let url = controller.auditLog.fileURL
        if FileManager.default.fileExists(atPath: url.path) {
            NSWorkspace.shared.activateFileViewerSelecting([url])
        } else {
            try? FileManager.default.createDirectory(at: url.deletingLastPathComponent(), withIntermediateDirectories: true)
            NSWorkspace.shared.open(url.deletingLastPathComponent())
        }
    }
}

/// The rows under the Agent Control switch while it is on: whether the endpoint is up, and where.
struct AgentConnectionRows: View {
    let isRunning: Bool
    let lastError: String?
    let endpointPath: String
    let retry: () -> Void

    var body: some View {
        if !isRunning, let lastError {
            HStack(alignment: .firstTextBaseline, spacing: 12) {
                Label(lastError, systemImage: "exclamationmark.triangle.fill")
                    .foregroundStyle(.red)
                    .fixedSize(horizontal: false, vertical: true)
                    .accessibilityElement(children: .combine)
                    .accessibilityLabel("Agent control error: \(lastError)")
                Spacer(minLength: 0)
                Button("Try Again", action: retry)
            }
        } else {
            LabeledContent("Status") {
                if isRunning {
                    Label("Listening for agents", systemImage: "dot.radiowaves.left.and.right")
                        .foregroundStyle(.green)
                        .accessibilityElement(children: .combine)
                        .accessibilityLabel("Listening for agents")
                } else {
                    Label("Starting…", systemImage: "hourglass")
                }
            }
        }

        LabeledContent("Endpoint") {
            HStack(spacing: 8) {
                Text(endpointPath)
                    .font(.callout.monospaced())
                    .textSelection(.enabled)
                    .lineLimit(1)
                    .truncationMode(.middle)
                    .frame(maxWidth: 380, alignment: .trailing)
                    .help(endpointPath)
                AgentCopyButton(value: endpointPath, label: "Copy endpoint path")
            }
        }
    }
}

/// One paired agent: who it is and what it may do on the left, its actions on the right.
struct AgentClientRow: View {
    let client: AutomationClient
    @Binding var isEditingPermissions: Bool
    let revoke: () -> Void

    var body: some View {
        HStack(alignment: .center, spacing: 12) {
            VStack(alignment: .leading, spacing: 2) {
                Text(client.name)
                Text(client.scopes.joined(separator: ", "))
                    .settingsDescription()
                    .lineLimit(2)
                Text(lastUsedText)
                    .font(.caption)
                    .foregroundStyle(.tertiary)
            }
            .accessibilityElement(children: .combine)

            Spacer(minLength: 0)

            Button("Permissions…") {
                isEditingPermissions = true
            }
            .help("Choose what this agent is allowed to do.")
            .accessibilityLabel("Change permissions for \(client.name)")
            .popover(isPresented: $isEditingPermissions) {
                AgentPermissionsEditor(clientID: client.id)
            }

            Button("Revoke", role: .destructive, action: revoke)
                .help("Remove this agent's access. It will need to pair again.")
                .accessibilityLabel("Revoke \(client.name)")
        }
    }

    private var lastUsedText: String {
        guard let lastUsed = client.lastUsedAt else {
            return "Paired \(client.createdAt.formatted(date: .abbreviated, time: .shortened)), never used"
        }
        return "Last used \(lastUsed.formatted(.relative(presentation: .named)))"
    }
}

/// Edits one paired agent's scopes. Changes apply immediately; the last enabled scope is locked
/// because an agent needs at least one (Revoke removes it instead).
private struct AgentPermissionsEditor: View {
    @ObservedObject private var controller = AutomationController.shared
    let clientID: String

    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            Text("Permissions")
                .font(.headline)
                .accessibilityAddTraits(.isHeader)
            Text("Changes apply right away. Delete, upload, and discard still ask you each time.")
                .font(.caption)
                .foregroundStyle(.secondary)
                .fixedSize(horizontal: false, vertical: true)
            if let contract = controller.contract, let client = client {
                ForEach(contract.displayScopes, id: \.self) { scope in
                    let isOn = client.scopes.contains(scope)
                    Toggle(isOn: Binding(
                        get: { isOn },
                        set: { newValue in
                            var scopes = Set(client.scopes)
                            if newValue { scopes.insert(scope) } else { scopes.remove(scope) }
                            controller.updateScopes(client, scopes: Array(scopes))
                        }
                    )) {
                        VStack(alignment: .leading, spacing: 1) {
                            Text(contract.isSensitive(scope: scope) ? "\(scope) (asks every time)" : scope)
                            Text(contract.scopes[scope] ?? "")
                                .font(.caption)
                                .foregroundStyle(.secondary)
                                .fixedSize(horizontal: false, vertical: true)
                        }
                    }
                    .toggleStyle(.checkbox)
                    .disabled(isOn && client.scopes.count == 1)
                    .accessibilityLabel("\(scope): \(contract.scopes[scope] ?? "")")
                }
            }
        }
        .padding(16)
        .frame(width: 340)
    }

    private var client: AutomationClient? {
        controller.clients.first { $0.id == clientID }
    }
}

// MARK: - Setup instructions

/// The setup instructions on their own, for the Guide window. Settings shows the same steps as
/// form rows instead.
struct AgentControlSetupView: View {
    var body: some View {
        VStack(alignment: .leading, spacing: 16) {
            AgentControlSetupSteps()

            Divider()

            DisclosureGroup("Set up without a plugin") {
                VStack(alignment: .leading, spacing: 16) {
                    AgentControlManualSetupSteps()
                }
                .padding(.top, 10)
            }
        }
    }
}

/// The recommended way to connect an agent, one step per view. In a form each step is a row.
struct AgentControlSetupSteps: View {
    var body: some View {
        AgentSetupStep(number: 1, title: "Before you begin") {
            Link("Install the .NET 10 SDK", destination: URL(string: "https://dotnet.microsoft.com/download/dotnet/10.0")!)
            AgentSetupNote("Turn on Agent Control above and keep Tiny Clips running. Pairing prompts appear in the app so you can choose the permissions to grant.")
        }

        AgentSetupStep(number: 2, title: "Install the Agent Plugin", badge: "Recommended") {
            AgentSetupNote("Choose one install method. Both install the same plugin; you only need one.")

            AgentSetupSubheading("VS Code")
            Text("Run Chat: Install Plugin from Source from the Command Palette, then enter this repository URL.")
                .fixedSize(horizontal: false, vertical: true)
            AgentCommand("https://github.com/jamesmontemagno/tiny-clips.git", label: "Copy VS Code plugin repository URL")

            AgentSetupSubheading("GitHub Copilot CLI")
            AgentCommand("copilot plugin marketplace add jamesmontemagno/tiny-clips", label: "Copy Copilot CLI marketplace command")
            AgentCommand("copilot plugin install tinyclips@tinyclips", label: "Copy Copilot CLI plugin install command")
        }

        AgentSetupStep(number: 3, title: "Pair your agent") {
            Text("Pair once, then approve the requested permissions in Tiny Clips.")
                .fixedSize(horizontal: false, vertical: true)
            AgentCommand("dnx TinyClips --yes -- pair --name \"My agent\"", label: "Copy pair command")

            AgentSetupSubheading("Check the connection")
            AgentCommand("dnx TinyClips --yes -- status", label: "Copy status command")
        }

        AgentSetupStep(number: 4, title: "Try it") {
            Text("Start a fresh agent session, then ask:")
            AgentCommand(
                "Use Tiny Clips to capture the Copilot window, inspect the image, add an arrow toward Create PR and an eyes emoji, and save a new copy.",
                label: "Copy example prompt",
                isCode: false
            )
            Link(
                "Full setup, Skills, and example prompts",
                destination: URL(string: "https://github.com/jamesmontemagno/tiny-clips/blob/main/docs/agent-integration.md")!
            )
        }
    }
}

/// Ways to connect an agent that cannot install the plugin.
struct AgentControlManualSetupSteps: View {
    var body: some View {
        AgentSetupStep(title: "Use the CLI directly") {
            AgentSetupNote("For terminal agents and scripts without plugin support, choose either dnx or a global CLI install.")

            AgentSetupSubheading("Run without a global install")
            AgentCommand("dnx TinyClips --yes -- pair --name \"My agent\"", label: "Copy dnx pair command")
            AgentCommand("dnx TinyClips --yes -- status", label: "Copy dnx status command")

            AgentSetupSubheading("Or install once, then use the tinyclips command")
            AgentCommand("dotnet tool install -g TinyClips", label: "Copy global CLI install command")
            AgentCommand("tinyclips pair --name \"My agent\"", label: "Copy global CLI pair command")
            AgentCommand("tinyclips status", label: "Copy global CLI status command")
        }

        AgentSetupStep(title: "Manual MCP setup") {
            Text("For agents that support MCP but not plugins, pair using either route above, then add this local stdio server in the agent's MCP settings. The agent must run as the same macOS user. Restart or reload its MCP connection after configuring it.")
                .fixedSize(horizontal: false, vertical: true)
            AgentCommand(
                "{\"mcpServers\":{\"tinyclips\":{\"command\":\"dotnet\",\"args\":[\"dnx\",\"TinyClips\",\"--verbosity\",\"quiet\",\"--yes\",\"--\",\"mcp\"]}}}",
                label: "Copy MCP configuration"
            )
            AgentSetupNote("If you installed the global CLI, use command tinyclips with args [\"mcp\"] instead. MCP setup varies by agent; workflow Skills are included with the plugin, not this manual configuration.")
        }
    }
}

private struct AgentSetupStep<Content: View>: View {
    var number: Int?
    let title: String
    var badge: String?
    @ViewBuilder let content: () -> Content

    var body: some View {
        HStack(alignment: .top, spacing: 10) {
            if let number {
                Text("\(number)")
                    .font(.caption.weight(.bold))
                    .monospacedDigit()
                    .frame(width: 20, height: 20)
                    .background(.quaternary, in: Circle())
                    .accessibilityHidden(true)
            }

            VStack(alignment: .leading, spacing: 8) {
                HStack(spacing: 8) {
                    Text(title)
                        .font(.headline)
                        .accessibilityLabel(number.map { "Step \($0): \(title)" } ?? title)
                        .accessibilityAddTraits(.isHeader)
                    if let badge {
                        Text(badge)
                            .font(.caption.weight(.medium))
                            .foregroundStyle(.secondary)
                            .padding(.horizontal, 6)
                            .padding(.vertical, 2)
                            .background(.quaternary, in: Capsule())
                    }
                }
                .frame(minHeight: 20)

                content()
            }
            .frame(maxWidth: .infinity, alignment: .leading)
        }
        .padding(.vertical, 4)
    }
}

private struct AgentSetupSubheading: View {
    let title: String

    init(_ title: String) {
        self.title = title
    }

    var body: some View {
        Text(title)
            .font(.subheadline.weight(.semibold))
            .foregroundStyle(.secondary)
            .padding(.top, 4)
    }
}

private struct AgentSetupNote: View {
    let text: String

    init(_ text: String) {
        self.text = text
    }

    var body: some View {
        Text(text)
            .foregroundStyle(.secondary)
            .fixedSize(horizontal: false, vertical: true)
    }
}

/// Text to copy into a terminal or an agent, with its copy button beside it.
private struct AgentCommand: View {
    let value: String
    let label: String
    let isCode: Bool

    init(_ value: String, label: String, isCode: Bool = true) {
        self.value = value
        self.label = label
        self.isCode = isCode
    }

    var body: some View {
        HStack(alignment: .center, spacing: 8) {
            Text(value)
                .font(isCode ? .callout.monospaced() : .callout)
                .textSelection(.enabled)
                .fixedSize(horizontal: false, vertical: true)
                .frame(maxWidth: .infinity, alignment: .leading)
            AgentCopyButton(value: value, label: label)
        }
        .padding(.leading, 10)
        .padding(.trailing, 6)
        .padding(.vertical, 6)
        .background(Color(nsColor: .textBackgroundColor).opacity(0.7), in: RoundedRectangle(cornerRadius: 8, style: .continuous))
        .overlay(
            RoundedRectangle(cornerRadius: 8, style: .continuous)
                .strokeBorder(.quaternary)
        )
    }
}

/// Copies its value and says so on the button itself, where the click was.
private struct AgentCopyButton: View {
    private enum Outcome {
        case copied
        case failed
    }

    let value: String
    let label: String
    @State private var outcome: Outcome?

    var body: some View {
        Button {
            copy()
        } label: {
            Image(systemName: iconName)
                .frame(width: 14)
        }
        .buttonStyle(.bordered)
        .controlSize(.small)
        .help(outcome == .failed ? failureText : label)
        .accessibilityLabel(label)
    }

    private var iconName: String {
        switch outcome {
        case .copied: "checkmark"
        case .failed: "exclamationmark.triangle"
        case nil: "doc.on.doc"
        }
    }

    private var failureText: String {
        "Could not copy. Select the text and copy it manually."
    }

    private func copy() {
        let pasteboard = NSPasteboard.general
        pasteboard.clearContents()
        let didCopy = pasteboard.setString(value, forType: .string)
        outcome = didCopy ? .copied : .failed
        AccessibilityNotification.Announcement(didCopy ? "Copied" : failureText).post()

        Task { @MainActor in
            try? await Task.sleep(for: .seconds(2))
            outcome = nil
        }
    }
}
