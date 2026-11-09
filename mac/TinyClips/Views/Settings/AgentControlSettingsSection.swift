import AppKit
import SwiftUI

struct AgentControlSettingsSection: View {
    @ObservedObject private var controller = AutomationController.shared
    @AppStorage(AutomationController.enabledKey) private var automationEnabled = false
    @State private var permissionsClientID: String?

    var body: some View {
        Section("Agent Control") {
            Text("Let AI agents and scripts on this Mac drive Tiny Clips through the Agent Plugin, MCP server, or command-line tool: take screenshots, record, open editors, and manage your library. Each agent has to be approved here once and only gets the permissions you allow. Deleting, uploading, and discarding always ask you first.")
                .font(.caption)
                .foregroundStyle(.secondary)

            Toggle("Allow agents to control Tiny Clips", isOn: $automationEnabled)
                .help("Starts a local endpoint that only apps running as your user can reach.")

            if automationEnabled {
                statusRow
                LabeledContent("Endpoint:") {
                    HStack(spacing: 6) {
                        Text(controller.endpointPath)
                            .font(.caption.monospaced())
                            .textSelection(.enabled)
                            .lineLimit(2)
                            .truncationMode(.middle)
                        Button {
                            let pasteboard = NSPasteboard.general
                            pasteboard.clearContents()
                            pasteboard.setString(controller.endpointPath, forType: .string)
                        } label: {
                            Image(systemName: "doc.on.doc")
                        }
                        .buttonStyle(.borderless)
                        .help("Copy endpoint path")
                        .accessibilityLabel("Copy endpoint path")
                    }
                }
                .accessibilityElement(children: .contain)
            }
        }

        Section("Get Started") {
            AgentControlSetupView()
        }

        Section("Paired Agents") {
            if controller.clients.isEmpty {
                Text("No agents are paired. Follow Get Started to install the plugin and pair an agent.")
                    .font(.caption)
                    .foregroundStyle(.secondary)
            } else {
                ForEach(controller.clients) { client in
                    clientRow(client)
                }
            }
        }

        Section("Activity") {
            Text("Every agent request is recorded in a local audit log.")
                .font(.caption)
                .foregroundStyle(.secondary)
            Button("Open Audit Log") {
                openAuditLog()
            }
            .help("Show the agent activity log in Finder.")
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
        HStack(alignment: .firstTextBaseline) {
            VStack(alignment: .leading, spacing: 2) {
                Text(client.name)
                Text(client.scopes.joined(separator: ", "))
                    .font(.caption)
                    .foregroundStyle(.secondary)
                Text(lastUsedText(client))
                    .font(.caption2)
                    .foregroundStyle(.secondary)
            }
            .accessibilityElement(children: .combine)
            Spacer()
            Button("Permissions…") {
                permissionsClientID = client.id
            }
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
            .help("Remove this agent's access. It will need to pair again.")
            .accessibilityLabel("Revoke \(client.name)")
        }
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
        VStack(alignment: .leading, spacing: 12) {
            GroupBox("Before you begin") {
                VStack(alignment: .leading, spacing: 8) {
                    Link("Install the .NET 10 SDK", destination: URL(string: "https://dotnet.microsoft.com/download/dotnet/10.0")!)
                    Text("Turn on Agent Control above and keep Tiny Clips running. Pairing prompts appear in the app so you can choose the permissions to grant.")
                        .foregroundStyle(.secondary)
                }
                .frame(maxWidth: .infinity, alignment: .leading)
            }

            GroupBox {
                VStack(alignment: .leading, spacing: 10) {
                    Text("Choose the install method for your agent. Both install the same plugin; you only need one.")
                        .foregroundStyle(.secondary)
                    VStack(alignment: .leading, spacing: 6) {
                        Text("VS Code")
                            .font(.subheadline.weight(.semibold))
                        Text("Run Chat: Install Plugin from Source from the Command Palette, then enter this repository URL. No manual clone or marketplace is required.")
                        command("https://github.com/jamesmontemagno/tiny-clips.git", label: "Copy VS Code plugin repository URL")
                    }
                    VStack(alignment: .leading, spacing: 6) {
                        Text("GitHub Copilot CLI")
                            .font(.subheadline.weight(.semibold))
                        command("copilot plugin install jamesmontemagno/tiny-clips", label: "Copy Copilot CLI plugin install command")
                    }
                    Divider()
                    Text("Pair once, then approve the requested permissions in Tiny Clips.")
                    command("dnx TinyClips --yes -- pair --name \"My agent\"", label: "Copy pair command")
                    Text("Check the connection:")
                    command("dnx TinyClips --yes -- status", label: "Copy status command")
                    Text("Start a fresh agent session, then ask: “Use Tiny Clips to capture the Copilot window, inspect the image, add an arrow toward Create PR and an eyes emoji, and save a new copy.”")
                        .textSelection(.enabled)
                        .foregroundStyle(.secondary)
                }
                .frame(maxWidth: .infinity, alignment: .leading)
            } label: {
                Label("Recommended: Install the Agent Plugin", systemImage: "puzzlepiece.extension")
                    .font(.headline)
            }

            GroupBox {
                VStack(alignment: .leading, spacing: 8) {
                    Text("For terminal agents and scripts without plugin support, choose either dnx or a global CLI install.")
                        .foregroundStyle(.secondary)
                    Text("Run without a global install:")
                        .font(.subheadline.weight(.semibold))
                    command("dnx TinyClips --yes -- pair --name \"My agent\"", label: "Copy dnx pair command")
                    command("dnx TinyClips --yes -- status", label: "Copy dnx status command")
                    Text("Or install once, then use the tinyclips command:")
                        .font(.subheadline.weight(.semibold))
                    command("dotnet tool install -g TinyClips", label: "Copy global CLI install command")
                    command("tinyclips pair --name \"My agent\"", label: "Copy global CLI pair command")
                    command("tinyclips status", label: "Copy global CLI status command")
                }
                .frame(maxWidth: .infinity, alignment: .leading)
            } label: {
                Label("Use the CLI directly (without a plugin)", systemImage: "terminal")
                    .font(.headline)
            }

            DisclosureGroup("Manual MCP setup (without a plugin)") {
                VStack(alignment: .leading, spacing: 8) {
                    Text("For agents that support MCP but not plugins, pair using either route above, then add this local stdio server in the agent's MCP settings. The agent must run as the same macOS user. Restart or reload its MCP connection after configuring it.")
                    command(
                        "{\"mcpServers\":{\"tinyclips\":{\"command\":\"dotnet\",\"args\":[\"dnx\",\"TinyClips\",\"--verbosity\",\"quiet\",\"--yes\",\"--\",\"mcp\"]}}}",
                        label: "Copy MCP configuration"
                    )
                    Text("If you installed the global CLI, use command tinyclips with args [\"mcp\"] instead. MCP setup varies by agent; workflow Skills are included with the plugin, not this manual configuration.")
                }
                .padding(.top, 6)
            }
            Text("Agents request permissions when they pair, and you choose which to grant. Change an agent's permissions under Paired Agents at any time. Deleting, uploading, and discarding always need approval. Turn off Agent Control or revoke an agent to stop access. Screen Recording, Microphone, and Camera permissions still apply.")
                .foregroundStyle(.secondary)
            Link("Full setup, Skills, and example prompts", destination: URL(string: "https://github.com/jamesmontemagno/tiny-clips/blob/main/docs/agent-integration.md")!)
            if !copyStatus.isEmpty {
                Text(copyStatus)
                    .foregroundStyle(.secondary)
            }
        }
        .font(.caption)
    }

    private func command(_ value: String, label: String) -> some View {
        HStack(alignment: .top, spacing: 8) {
            Text(value)
                .font(.caption.monospaced())
                .textSelection(.enabled)
                .frame(maxWidth: .infinity, alignment: .leading)
            Button {
                NSPasteboard.general.clearContents()
                copyStatus = NSPasteboard.general.setString(value, forType: .string)
                    ? "\(label) copied."
                    : "Could not copy. Select the command text and copy it manually."
            } label: {
                Image(systemName: "doc.on.doc")
            }
            .buttonStyle(.borderless)
            .help(label)
            .accessibilityLabel(label)
        }
    }
}
