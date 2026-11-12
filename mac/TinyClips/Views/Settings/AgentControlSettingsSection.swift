import AppKit
import SwiftUI

struct AgentControlSettingsSection: View {
    @ObservedObject private var controller = AutomationController.shared
    @AppStorage(AutomationController.enabledKey) private var automationEnabled = false
    @State private var permissionsClientID: String?

    var body: some View {
        SettingsFormSection("Agent Control", icon: "terminal") {
            VStack(alignment: .leading, spacing: 6) {
                HStack(alignment: .center, spacing: 12) {
                    ZStack {
                        RoundedRectangle(cornerRadius: 10, style: .continuous)
                            .fill(automationEnabled ? Color.accentColor.opacity(0.16) : Color.primary.opacity(0.06))
                            .frame(width: 42, height: 42)
                        Image(systemName: "terminal")
                            .font(.title3.weight(.semibold))
                            .foregroundStyle(automationEnabled ? Color.accentColor : Color.secondary)
                    }

                    VStack(alignment: .leading, spacing: 3) {
                        Text("Allow agents to control Tiny Clips")
                            .font(.headline)
                        Text("Use the Agent Plugin, MCP server, or command-line tool.")
                            .font(.callout)
                            .foregroundStyle(.secondary)
                    }

                    Spacer(minLength: 12)

                    Toggle("Enabled", isOn: $automationEnabled)
                        .toggleStyle(.switch)
                        .accessibilityLabel("Allow agents to control Tiny Clips")
                        .help("Starts a local endpoint that only apps running as your user can reach.")
                }

                Text("Each agent needs approval once and only gets the permissions you allow. Deleting, uploading, and discarding always ask you first.")
                    .font(.callout)
                    .foregroundStyle(.secondary)
                    .fixedSize(horizontal: false, vertical: true)

                if automationEnabled {
                    Divider()

                    HStack(alignment: .center, spacing: 10) {
                        statusRow
                        Spacer()
                        Text("Local endpoint")
                            .font(.caption)
                            .foregroundStyle(.secondary)
                    }

                    HStack(spacing: 8) {
                        Text(controller.endpointPath)
                            .font(.caption.monospaced())
                            .foregroundStyle(.primary)
                            .textSelection(.enabled)
                            .lineLimit(2)
                            .truncationMode(.middle)
                            .frame(maxWidth: .infinity, alignment: .leading)

                        Button {
                            let pasteboard = NSPasteboard.general
                            pasteboard.clearContents()
                            pasteboard.setString(controller.endpointPath, forType: .string)
                        } label: {
                            Image(systemName: "doc.on.doc")
                        }
                        .buttonStyle(.bordered)
                        .controlSize(.small)
                        .help("Copy endpoint path")
                        .accessibilityLabel("Copy endpoint path")
                    }
                    .padding(10)
                    .background(.quaternary.opacity(0.45), in: RoundedRectangle(cornerRadius: 8, style: .continuous))
                    .overlay(
                        RoundedRectangle(cornerRadius: 8, style: .continuous)
                            .strokeBorder(.quaternary)
                    )
                    .accessibilityElement(children: .contain)
                }
            }
            .padding(.vertical, 4)
        }

        SettingsFormSection("Get Started", icon: "sparkles") {
            AgentControlSetupView()
        }

        SettingsFormSection("Paired Agents", icon: "person.2") {
            if controller.clients.isEmpty {
                HStack(alignment: .top, spacing: 10) {
                    Image(systemName: "person.crop.circle.badge.questionmark")
                        .font(.title3)
                        .foregroundStyle(.secondary)
                    VStack(alignment: .leading, spacing: 10) {
                        Text("No agents are paired")
                            .font(.headline)
                        Text("Follow Get Started to install the plugin or CLI, then pair an agent.")
                            .font(.callout)
                            .foregroundStyle(.secondary)
                    }
                }
                .padding(.vertical, 6)
            } else {
                ForEach(controller.clients) { client in
                    clientRow(client)
                }
            }
        }

        SettingsFormSection("Activity", icon: "text.page") {
            HStack(alignment: .center, spacing: 12) {
                ZStack {
                    RoundedRectangle(cornerRadius: 8, style: .continuous)
                        .fill(Color.primary.opacity(0.06))
                        .frame(width: 36, height: 36)
                    Image(systemName: "text.page")
                        .foregroundStyle(.secondary)
                }
                VStack(alignment: .leading, spacing: 3) {
                    Text("Agent activity")
                        .font(.headline)
                    Text("Every agent request is recorded in a local audit log.")
                        .font(.callout)
                        .foregroundStyle(.secondary)
                }
                Spacer()
                Button("Open Audit Log") {
                    openAuditLog()
                }
                .buttonStyle(.bordered)
            }
            .padding(.vertical, 6)
        }
    }

    @ViewBuilder
    private var statusRow: some View {
        if controller.isRunning {
            Label("Listening for agents", systemImage: "dot.radiowaves.left.and.right")
                .foregroundStyle(.green)
                .accessibilityElement(children: .combine)
                .accessibilityLabel("Status: listening for agents")
        } else if let error = controller.lastError {
            HStack {
                Label(error, systemImage: "exclamationmark.triangle.fill")
                    .foregroundStyle(.red)
                    .accessibilityElement(children: .combine)
                    .accessibilityLabel("Agent control error: \(error)")
                Spacer()
                Button("Try Again") { controller.retryStart() }
            }
        } else {
            Label("Starting…", systemImage: "hourglass")
                .foregroundStyle(.secondary)
        }
    }

    private func clientRow(_ client: AutomationClient) -> some View {
        HStack(alignment: .center, spacing: 12) {
            ZStack {
                Circle()
                    .fill(Color.accentColor.opacity(0.12))
                    .frame(width: 36, height: 36)
                Image(systemName: "person.2")
                    .foregroundStyle(Color.accentColor)
            }

            VStack(alignment: .leading, spacing: 3) {
                Text(client.name)
                    .font(.headline)
                Text(client.scopes.joined(separator: ", "))
                    .font(.caption)
                    .foregroundStyle(.secondary)
                    .lineLimit(2)
                Text(lastUsedText(client))
                    .font(.caption2)
                    .foregroundStyle(.secondary)
            }
            .accessibilityElement(children: .combine)

            Spacer()

            HStack(spacing: 8) {
                Button("Permissions…") {
                    permissionsClientID = client.id
                }
                .buttonStyle(.bordered)
                .help("Choose what this agent is allowed to do.")
                .accessibilityLabel("Change permissions for \(client.name)")
                .popover(isPresented: Binding(
                    get: { permissionsClientID == client.id },
                    set: { if !$0, permissionsClientID == client.id { permissionsClientID = nil } }
                )) {
                    AgentPermissionsEditor(clientID: client.id)
                }

                Button("Revoke", role: .destructive) {
                    controller.revoke(client)
                }
                .buttonStyle(.bordered)
                .help("Remove this agent's access. It will need to pair again.")
                .accessibilityLabel("Revoke \(client.name)")
            }
        }
        .padding(.vertical, 6)
    }

    private func lastUsedText(_ client: AutomationClient) -> String {
        guard let lastUsed = client.lastUsedAt else { return "Paired \(client.createdAt.formatted(date: .abbreviated, time: .shortened)), never used" }
        return "Last used \(lastUsed.formatted(.relative(presentation: .named)))"
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

struct AgentControlSetupView: View {
    @State private var copyStatus = ""

    var body: some View {
        VStack(alignment: .leading, spacing: 4) {
            setupGroup(title: "Before you begin", icon: "checklist") {
                Link("Install the .NET 10 SDK", destination: URL(string: "https://dotnet.microsoft.com/download/dotnet/10.0")!)
                Text("Turn on Agent Control above and keep Tiny Clips running. Pairing prompts appear in the app so you can choose the permissions to grant.")
                    .font(.callout)
                    .foregroundStyle(.secondary)
                    .fixedSize(horizontal: false, vertical: true)
            }

            Divider()

            setupGroup(title: "Recommended: Install the Agent Plugin", icon: "puzzlepiece.extension") {
                Text("Choose one install method. Both install the same plugin; you only need one.")
                    .foregroundStyle(.secondary)
                    .fixedSize(horizontal: false, vertical: true)

                VStack(alignment: .leading, spacing: 8) {
                    VStack(alignment: .leading, spacing: 6) {
                        Text("VS Code")
                            .font(.subheadline.weight(.semibold))
                        Text("Run Chat: Install Plugin from Source from the Command Palette, then enter this repository URL.")
                            .fixedSize(horizontal: false, vertical: true)
                        command("https://github.com/jamesmontemagno/tiny-clips.git", label: "Copy VS Code plugin repository URL")
                    }

                    VStack(alignment: .leading, spacing: 6) {
                        Text("GitHub Copilot CLI")
                            .font(.subheadline.weight(.semibold))
                        command("copilot plugin marketplace add jamesmontemagno/tiny-clips", label: "Copy Copilot CLI marketplace command")
                        command("copilot plugin install tinyclips@tinyclips", label: "Copy Copilot CLI plugin install command")
                    }
                }

                Divider()

                VStack(alignment: .leading, spacing: 8) {
                    Text("Pair once, then approve the requested permissions in Tiny Clips.")
                    command("dnx TinyClips --yes -- pair --name \"My agent\"", label: "Copy pair command")

                    Text("Check the connection:")
                        .foregroundStyle(.secondary)
                    command("dnx TinyClips --yes -- status", label: "Copy status command")
                }

                VStack(alignment: .leading, spacing: 6) {
                    Text("Try it in a fresh agent session")
                        .font(.subheadline.weight(.semibold))
                    Text("“Use Tiny Clips to capture the Copilot window, inspect the image, add an arrow toward Create PR and an eyes emoji, and save a new copy.”")
                        .font(.callout)
                        .foregroundStyle(.secondary)
                        .textSelection(.enabled)
                        .fixedSize(horizontal: false, vertical: true)
                }
            }

            Divider()

            setupGroup(title: "Use the CLI directly", icon: "terminal") {
                Text("For terminal agents and scripts without plugin support, choose either dnx or a global CLI install.")
                    .foregroundStyle(.secondary)
                    .fixedSize(horizontal: false, vertical: true)

                VStack(alignment: .leading, spacing: 8) {
                    Text("Run without a global install")
                        .font(.subheadline.weight(.semibold))
                    command("dnx TinyClips --yes -- pair --name \"My agent\"", label: "Copy dnx pair command")
                    command("dnx TinyClips --yes -- status", label: "Copy dnx status command")
                }

                VStack(alignment: .leading, spacing: 8) {
                    Text("Or install once, then use the tinyclips command")
                        .font(.subheadline.weight(.semibold))
                    command("dotnet tool install -g TinyClips", label: "Copy global CLI install command")
                    command("tinyclips pair --name \"My agent\"", label: "Copy global CLI pair command")
                    command("tinyclips status", label: "Copy global CLI status command")
                }
            }

            Divider()

            DisclosureGroup {
                VStack(alignment: .leading, spacing: 10) {
                    Text("For agents that support MCP but not plugins, pair using either route above, then add this local stdio server in the agent's MCP settings. The agent must run as the same macOS user. Restart or reload its MCP connection after configuring it.")
                        .fixedSize(horizontal: false, vertical: true)
                    command(
                        "{\"mcpServers\":{\"tinyclips\":{\"command\":\"dotnet\",\"args\":[\"dnx\",\"TinyClips\",\"--verbosity\",\"quiet\",\"--yes\",\"--\",\"mcp\"]}}}",
                        label: "Copy MCP configuration"
                    )
                    Text("If you installed the global CLI, use command tinyclips with args [\"mcp\"] instead. MCP setup varies by agent; workflow Skills are included with the plugin, not this manual configuration.")
                        .font(.callout)
                        .foregroundStyle(.secondary)
                        .fixedSize(horizontal: false, vertical: true)
                }
                .padding(.top, 10)
            } label: {
                Label("Manual MCP setup (without a plugin)", systemImage: "slider.horizontal.3")
                    .font(.headline)
            }
            .padding(.vertical, 4)

            Link("Full setup, Skills, and example prompts", destination: URL(string: "https://github.com/jamesmontemagno/tiny-clips/blob/main/docs/agent-integration.md")!)
            if !copyStatus.isEmpty {
                Text(copyStatus)
                    .font(.callout)
                    .foregroundStyle(.secondary)
            }
        }
    }

    private func setupGroup<Content: View>(title: String, icon: String, @ViewBuilder content: () -> Content) -> some View {
        VStack(alignment: .leading, spacing: 6) {
            Label(title, systemImage: icon)
                .font(.headline)
            content()
        }
        .padding(.vertical, 4)
        .frame(maxWidth: .infinity, alignment: .leading)
    }

    private func command(_ value: String, label: String) -> some View {
        HStack(alignment: .center, spacing: 8) {
            Text(value)
                .font(.caption.monospaced())
                .textSelection(.enabled)
                .fixedSize(horizontal: false, vertical: true)
                .frame(maxWidth: .infinity, alignment: .leading)
            Button {
                NSPasteboard.general.clearContents()
                copyStatus = NSPasteboard.general.setString(value, forType: .string)
                    ? "\(label) copied."
                    : "Could not copy. Select the command text and copy it manually."
            } label: {
                Image(systemName: "doc.on.doc")
            }
            .buttonStyle(.bordered)
            .controlSize(.small)
            .help(label)
            .accessibilityLabel(label)
        }
        .padding(.leading, 10)
        .padding(.trailing, 6)
        .padding(.vertical, 6)
        .background(.background.opacity(0.72), in: RoundedRectangle(cornerRadius: 8, style: .continuous))
        .overlay(
            RoundedRectangle(cornerRadius: 8, style: .continuous)
                .strokeBorder(.quaternary)
        )
    }
}
