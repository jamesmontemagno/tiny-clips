import Foundation
import SwiftUI
import AppKit

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
