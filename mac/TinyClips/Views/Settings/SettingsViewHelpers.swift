import Foundation
import SwiftUI
import AppKit

/// One titled group in the grouped Settings form. Every child of `content` is its own form row:
/// the form draws the card, the separators between rows, and the row spacing, so sections must
/// not add a background or stack their settings into a single row.
struct SettingsFormSection<Content: View>: View {
    let title: String?
    let icon: String?
    let footer: String?
    @ViewBuilder let content: () -> Content

    init(
        _ title: String? = nil,
        icon: String? = nil,
        footer: String? = nil,
        @ViewBuilder content: @escaping () -> Content
    ) {
        self.title = title
        self.icon = icon
        self.footer = footer
        self.content = content
    }

    var body: some View {
        Section {
            content()
        } header: {
            if let title {
                SettingsSectionHeader(title: title, icon: icon)
            }
        } footer: {
            if let footer {
                Text(footer)
                    .fixedSize(horizontal: false, vertical: true)
            }
        }
    }
}

struct SettingsSectionHeader: View {
    let title: String
    let icon: String?

    var body: some View {
        Group {
            if let icon {
                Label(title, systemImage: icon)
            } else {
                Text(title)
            }
        }
        .font(.headline)
        .foregroundStyle(.primary)
        .accessibilityAddTraits(.isHeader)
    }
}

/// A group that starts collapsed, for settings most people never change. Its header matches the
/// other groups and is the control that opens it. Pass `startsExpanded` when one of its settings
/// is already changed from the default, so a setting in effect is never hidden.
struct SettingsAdvancedSection<Content: View>: View {
    let title: String
    let icon: String
    @ViewBuilder let content: () -> Content
    @State private var isExpanded: Bool

    init(
        _ title: String = "Advanced",
        icon: String = "gearshape.2",
        startsExpanded: Bool = false,
        @ViewBuilder content: @escaping () -> Content
    ) {
        self.title = title
        self.icon = icon
        self.content = content
        _isExpanded = State(initialValue: startsExpanded)
    }

    var body: some View {
        Section {
            if isExpanded {
                content()
            }
        } header: {
            Button {
                isExpanded.toggle()
            } label: {
                HStack(spacing: 6) {
                    SettingsSectionHeader(title: title, icon: icon)
                    Image(systemName: "chevron.right")
                        .font(.caption.weight(.semibold))
                        .foregroundStyle(.secondary)
                        .rotationEffect(.degrees(isExpanded ? 90 : 0))
                        .accessibilityHidden(true)
                    Spacer(minLength: 0)
                }
                .contentShape(Rectangle())
            }
            .buttonStyle(.plain)
            .accessibilityLabel(title)
            .accessibilityValue(isExpanded ? "Expanded" : "Collapsed")
            .accessibilityHint(isExpanded ? "Hides these settings." : "Shows these settings.")
        }
    }
}

extension View {
    /// Secondary text inside a row, matching the form's own subtitle style.
    func settingsDescription() -> some View {
        font(.subheadline)
            .foregroundStyle(.secondary)
            .fixedSize(horizontal: false, vertical: true)
    }

    /// A tooltip only where there is something to say.
    @ViewBuilder
    func settingsHelp(_ text: String?) -> some View {
        if let text {
            help(text)
        } else {
            self
        }
    }
}

/// A switch row: title on the left, switch on the right. Give it a description only when the
/// title does not say what the setting does.
struct SettingsToggleRow: View {
    let title: String
    var description: String? = nil
    @Binding var isOn: Bool
    var help: String?
    var accessibilityHint: String?
    var disabled = false

    var body: some View {
        Toggle(isOn: $isOn) {
            Text(title)
            if let description {
                Text(description)
            }
        }
        .settingsHelp(help)
        .accessibilityHint(accessibilityHint ?? help ?? "")
        .disabled(disabled)
    }
}

/// A slider row: title on the left, slider and its current value on the right. Every slider and
/// value column has the same width, so sliders line up down a page.
struct SettingsSliderRow: View {
    let title: String
    @Binding var value: Double
    let range: ClosedRange<Double>
    let step: Double
    let valueText: String
    var help: String?

    var body: some View {
        LabeledContent(title) {
            HStack(spacing: 10) {
                Slider(value: $value, in: range, step: step)
                    .labelsHidden()
                    .frame(width: 220)
                    .accessibilityLabel(title)
                    .accessibilityValue(valueText)
                Text(valueText)
                    .monospacedDigit()
                    .foregroundStyle(.secondary)
                    .lineLimit(1)
                    .frame(width: 64, alignment: .trailing)
                    .accessibilityHidden(true)
            }
        }
        .settingsHelp(help)
    }
}

/// A row for something you do rather than set: what it is on the left, its buttons on the right.
struct SettingsActionRow<Actions: View>: View {
    let title: String
    var description: String? = nil
    @ViewBuilder let actions: () -> Actions

    var body: some View {
        LabeledContent {
            HStack(spacing: 8) {
                actions()
            }
        } label: {
            Text(title)
            if let description {
                Text(description)
            }
        }
    }
}

extension Binding where Value == Int {
	var doubleValue: Binding<Double> {
		Binding<Double>(
			get: { Double(wrappedValue) },
			set: { wrappedValue = Int($0) }
		)
	}
}

struct QuickBugReportContext {
    let platform: String
    let version: String
    let build: String
    let distribution: String
    let osVersion: String
}

enum QuickFeedbackType: String, CaseIterable {
    case bug = "Bug report"
    case featureRequest = "Feature request"
}

enum QuickBugReportURLBuilder {
    static func makeURL(
        type: QuickFeedbackType,
        title: String,
        description: String,
        context: QuickBugReportContext
    ) -> URL {
        let isFeatureRequest = type == .featureRequest
        var components = URLComponents(string: "https://github.com/jamesmontemagno/tiny-clips/issues/new")!
        components.queryItems = [
            URLQueryItem(name: "template", value: isFeatureRequest ? "quick_feature_request.yml" : "quick_bug_report.yml"),
            URLQueryItem(name: "labels", value: isFeatureRequest ? "enhancement" : "bug"),
            URLQueryItem(name: "title", value: "\(isFeatureRequest ? "[Feature]" : "[Bug]"): \(title)"),
            URLQueryItem(name: isFeatureRequest ? "request" : "happened", value: description),
            URLQueryItem(name: "platform", value: context.platform),
            URLQueryItem(name: "version", value: context.version),
            URLQueryItem(name: "build", value: context.build),
            URLQueryItem(name: "distribution", value: context.distribution),
            URLQueryItem(name: "os", value: context.osVersion)
        ]
        return components.url!
    }
}

struct QuickBugReportFormView: View {
    let context: QuickBugReportContext
    let onSubmit: (_ type: QuickFeedbackType, _ title: String, _ description: String) -> Void

    @Environment(\.dismiss) private var dismiss
    @State private var feedbackType: QuickFeedbackType = .bug
    @State private var bugTitle = ""
    @State private var bugDescription = ""
    @State private var featureTitle = ""
    @State private var featureDescription = ""

    private var titleBinding: Binding<String> {
        Binding(
            get: { feedbackType == .bug ? bugTitle : featureTitle },
            set: { newValue in
                if feedbackType == .bug {
                    bugTitle = newValue
                } else {
                    featureTitle = newValue
                }
            }
        )
    }

    private var descriptionBinding: Binding<String> {
        Binding(
            get: { feedbackType == .bug ? bugDescription : featureDescription },
            set: { newValue in
                if feedbackType == .bug {
                    bugDescription = newValue
                } else {
                    featureDescription = newValue
                }
            }
        )
    }

    private var canSubmit: Bool {
        !titleBinding.wrappedValue.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty &&
        !descriptionBinding.wrappedValue.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            Text(feedbackType == .bug ? "File a Bug" : "Request a Feature")
                .font(.title3)
                .bold()

            Picker("Feedback type", selection: $feedbackType) {
                ForEach(QuickFeedbackType.allCases, id: \.self) { type in
                    Text(type.rawValue).tag(type)
                }
            }
            .pickerStyle(.segmented)

            TextField(feedbackType == .bug ? "Bug title" : "Feature title", text: titleBinding)

            VStack(alignment: .leading, spacing: 6) {
                Text(feedbackType == .bug ? "What happened?" : "What feature would you like to see?")
                    .font(.subheadline)
                TextEditor(text: descriptionBinding)
                    .frame(minHeight: 140)
                    .overlay(
                        RoundedRectangle(cornerRadius: 6)
                            .stroke(Color(nsColor: .separatorColor), lineWidth: 1)
                    )
            }

            Text("App info will be auto-filled: \(context.platform), v\(context.version) (build \(context.build)), \(context.distribution), \(context.osVersion)")
                .font(.caption)
                .foregroundStyle(.secondary)

            HStack {
                Spacer()
                Button("Cancel", role: .cancel) {
                    dismiss()
                }
                Button("File on GitHub…") {
                    onSubmit(
                        feedbackType,
                        titleBinding.wrappedValue.trimmingCharacters(in: .whitespacesAndNewlines),
                        descriptionBinding.wrappedValue.trimmingCharacters(in: .whitespacesAndNewlines)
                    )
                    dismiss()
                }
                .disabled(!canSubmit)
            }
        }
        .padding(16)
        .frame(minWidth: 520)
    }
}
