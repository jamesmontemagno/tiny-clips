import AppKit
import SwiftUI

struct AgentControlSettingsSection: View {
    @ObservedObject private var controller = AutomationController.shared
    @AppStorage(AutomationController.enabledKey) private var automationEnabled = false

    var body: some View {
        Section("Agent Control") {
            Text("Let AI agents and scripts on this Mac drive Tiny Clips through the tinyclips command-line tool: take screenshots, record, open editors, and manage your library. Each agent has to be approved here once and only gets the permissions you allow. Deleting, uploading, and discarding always ask you first.")
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
                Text("No agents are paired. Run tinyclips pair from a terminal to connect one.")
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

struct AgentControlSetupView: View {
    @State private var copyStatus = ""

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            Text("1. Install the .NET 10 SDK, then run this in Terminal.")
            Link("Download .NET 10", destination: URL(string: "https://dotnet.microsoft.com/download/dotnet/10.0")!)
            command("dotnet tool install -g TinyClips", label: "Install TinyClips CLI")
            Text("2. Turn on Agent Control in Settings, keep Tiny Clips running, then pair your agent.")
            command("tinyclips pair --name \"My agent\"", label: "Pair an agent")
            Text("3. Approve the requested permissions in Tiny Clips, then check the connection.")
            command("tinyclips status", label: "Check agent connection")
            Text("Ask an agent with terminal access: “Use Tiny Clips to capture the Copilot window, inspect the image, add an arrow toward Create PR and an eyes emoji, and save a new copy.”")
                .textSelection(.enabled)

            DisclosureGroup("Connect through MCP") {
                VStack(alignment: .leading, spacing: 8) {
                    Text("Pair first, then add this local stdio server in your agent's MCP settings. The agent must run as the same macOS user. Restart or reload its MCP connection after configuring it.")
                    command(
                        "{\"mcpServers\":{\"tinyclips\":{\"command\":\"dotnet\",\"args\":[\"dnx\",\"TinyClips\",\"--verbosity\",\"quiet\",\"--yes\",\"--\",\"mcp\"]}}}",
                        label: "Copy MCP configuration"
                    )
                    Text("Already installed the tool? Use command tinyclips with args [\"mcp\"] instead. MCP setup varies by agent.")
                }
                .padding(.top, 6)
            }
            Text("Default pairing grants read, capture, record, edit, and export. Deleting, uploading, and discarding always need approval. Turn off Agent Control or revoke an agent to stop access. Screen Recording, Microphone, and Camera permissions still apply.")
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
