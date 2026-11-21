import AppKit
import SwiftUI

@MainActor
class GuideWindow: NSWindow, NSWindowDelegate {
    private var onDismiss: (() -> Void)?
    private var didClose = false
    private let navigation = GuideNavigation()

    var selectedTopic: GuideSection { navigation.selection }

    func selectTopic(_ topic: GuideSection) {
        navigation.select(topic)
    }

    convenience init(onDismiss: @escaping () -> Void) {
        self.init(
            contentRect: NSRect(x: 0, y: 0, width: 780, height: 680),
            styleMask: [.titled, .closable, .miniaturizable, .resizable],
            backing: .buffered,
            defer: false
        )

        self.onDismiss = onDismiss
        self.delegate = self
        self.isReleasedWhenClosed = false
        self.title = "TinyClips Guide"
        self.contentMinSize = NSSize(width: 700, height: 460)
        self.center()
        self.contentView = NSHostingView(rootView: GuideWindowView(navigation: navigation))
    }

    func windowWillClose(_ notification: Notification) {
        guard !didClose else { return }
        didClose = true
        let callback = onDismiss
        onDismiss = nil
        callback?()
    }
}

enum GuideSection: String, CaseIterable, Identifiable {
    case capture, editor, recording, studio, library, agents, help

    var id: String { rawValue }
    var tabTitle: String { rawValue.capitalized }

    var title: String {
        switch self {
        case .capture: "Capture anything on your screen"
        case .editor: "Make your screenshot explain itself"
        case .recording: "Record a clear demo"
        case .studio: "Record now. Choose the look later."
        case .library: "Find, organize, and share your clips"
        case .agents: "Give your agent capture tools"
        case .help: "Make Tiny Clips work your way"
        }
    }

    var introduction: String {
        switch self {
        case .capture: "Start from the camera icon in your menu bar. Capture a screenshot, record an MP4 or GIF, stitch a long page, or copy text from the screen."
        case .editor: "Annotate, hide sensitive details, and frame your image before you copy or save it. Reopen a screenshot whenever you need another edit."
        case .recording: "Capture your screen with optional audio and a webcam overlay, then trim the result. Ordinary recordings keep the look you chose while recording."
        case .studio: "Studio keeps screen and camera as separate layers. Change layouts, guide attention, and polish your story without recording it again."
        case .library: "Your screenshots, videos, GIFs, and Studio projects are close at hand, without digging through folders."
        case .agents: "Bring your own AI agent. Tiny Clips provides local capture, editing, recording, and Studio tools through its plugin, CLI, and MCP server."
        case .help: "Choose what happens after a capture, learn your current shortcuts, and resolve the most common permission and setup problems."
        }
    }

    var documentationURL: URL {
        let page: String
        switch self {
        case .capture: page = "docs-capture"
        case .editor: page = "docs-editor"
        case .recording: page = "docs-recording"
        case .studio: page = "docs-studio"
        case .library: page = "docs-library"
        case .agents: page = "agents"
        case .help: page = "docs-help"
        }
        return URL(string: "https://tinyclips.app/\(page).html")!
    }
}

@MainActor
final class GuideNavigation: ObservableObject {
    @Published var selection: GuideSection = .capture

    func select(_ topic: GuideSection?) {
        if let topic { selection = topic }
    }
}

private struct GuideWindowView: View {
    @ObservedObject var navigation: GuideNavigation
    @ObservedObject private var settings = CaptureSettings.shared

    var body: some View {
        VStack(alignment: .leading, spacing: 16) {
            VStack(alignment: .leading, spacing: 6) {
                Text("Tiny Clips Guide")
                    .font(.title.bold())
                    .accessibilityAddTraits(.isHeader)
                Text("Capture, polish, and share. Explore a feature to see what it does and how to use it.")
                    .foregroundStyle(.secondary)
            }
            TabView(selection: $navigation.selection) {
                ForEach(GuideSection.allCases) { section in
                    ScrollView {
                        VStack(alignment: .leading, spacing: 20) {
                            VStack(alignment: .leading, spacing: 8) {
                                Text(section.title)
                                    .font(.title2.bold())
                                    .accessibilityAddTraits(.isHeader)
                                Text(section.introduction)
                                    .foregroundStyle(.secondary)
                            }
                            GuideIllustration(section: section)
                            sectionContent(for: section)
                            Link("Read more about \(section.tabTitle.lowercased()) on the website",
                                 destination: section.documentationURL)
                        }
                        .fixedSize(horizontal: false, vertical: true)
                        .padding(20)
                        .frame(maxWidth: 820, alignment: .topLeading)
                        .frame(maxWidth: .infinity, alignment: .top)
                    }
                    .tabItem { Text(section.tabTitle) }
                    .tag(section)
                    .accessibilityIdentifier("Guide\(section.tabTitle)Tab")
                }
            }
            .accessibilityLabel("Guide topics")
        }
        .padding(20)
        .frame(minWidth: 700, maxWidth: .infinity, minHeight: 460, maxHeight: .infinity, alignment: .topLeading)
        .background(.background)
    }

    @ViewBuilder
    private func sectionContent(for section: GuideSection) -> some View {
        switch section {
        case .capture: captureContent
        case .editor: editorContent
        case .recording: recordingContent
        case .studio: studioContent
        case .library: libraryContent
        case .agents:
            sectionCard(title: "Stay in control", icon: "lock.shield") {
                feature("Local and opt-in", "Enable Settings → Agent Control and keep the app running. Your agent must run on this Mac as the same OS user; a remote container cannot automatically reach it.")
                feature("Choose permissions", "Approve pairing and grant only the scopes you need. Delete, upload, and discard actions always need separate approval. Revoke access or turn it off in Settings; requests appear in the local audit log.")
                feature("Tools, not an AI model", "Agents can capture, recognize text, annotate, record, and edit/export Studio projects. They need their own browser or desktop tools to click, type, or scroll in other apps.")
            }
            sectionCard(title: "Connect your agent", icon: "terminal") {
                AgentControlSetupView()
            }
        case .help: helpContent
        }
    }

    private var captureContent: some View {
        VStack(alignment: .leading, spacing: 20) {
            sectionCard(title: "Your first capture", icon: "camera") {
                steps([
                    "Click the menu bar camera icon and choose Screenshot, Record Video, or Record GIF.",
                    "Choose Region (R), Screen (S), or Window (W). Draw a region, select a display, or choose the highlighted window. Esc cancels.",
                    "For recordings, confirm the options in the start panel and click Record. Stop using the floating controls or your assigned stop shortcut.",
                    "Edit or trim when enabled, then copy or save. Find the result in Recent Captures or Clips Library."
                ])
            }
            sectionCard(title: "More than one screenful", icon: "arrow.down.doc") {
                feature("Scrolling screenshots", "Start a screenshot and choose Scroll (P). Select the region, then scroll gradually with overlapping content. Click Done to stitch one tall image, or Cancel to discard.")
                feature("Keep the overlap", "Large jumps may be skipped if there is not enough clear overlap. Very long captures stop and save automatically at a safety limit; check that the end includes everything you need.")
                Link("Scrolling capture and OCR walkthrough", destination: URL(string: "https://tinyclips.app/docs-scrolling-ocr.html")!)
            }
            sectionCard(title: "Text and files", icon: "text.viewfinder") {
                feature("Copy Text from Region", "Choose Copy Text from Region… in the menu bar, then select the text. Recognition happens on your Mac and the result goes to the clipboard; its global shortcut is optional.")
                feature("Choose what happens next", "Screenshot settings choose PNG, JPEG, or WebP and the save folder. Each capture type has its own clipboard, saving, and editor/trimmer preferences. Video exports MP4; GIF makes a looping image.")
            }
        }
    }

    private var editorContent: some View {
        VStack(alignment: .leading, spacing: 20) {
            sectionCard(title: "From capture to explanation", icon: "pencil.tip.crop.circle") {
                steps([
                    "Enable the screenshot editor in Settings, or reopen an image from Recent Captures or Clips Library. You can also open a PNG, JPEG, or WebP file with Tiny Clips.",
                    "Choose an arrow, shape, line, freehand drawing, text, numbered badge, or emoji. Drag on the canvas, then select an annotation to move or resize it.",
                    "Crop away distractions. Use Undo to step back, and zoom or pan to work on small details.",
                    "Review the finished image, then copy or save it. Save a separate copy when you want to keep the original."
                ])
            }
            sectionCard(title: "Hide sensitive details", icon: "eye.slash") {
                feature("Solid means covered", "Redact offers blur, pixelate, and solid styles. Use solid for passwords, keys, and other secrets: blur and pixelation can leave the underlying text readable.")
                feature("Find sensitive text", "Run Find sensitive text in the Redact panel to suggest email addresses, phone numbers, and IP addresses using on-device OCR. Review and approve or dismiss each suggestion, then apply opaque redactions.")
                feature("Review before sharing", "Detection can miss things. Check the whole image, including annotations and surrounding context, before copying, saving, or uploading it.")
            }
            sectionCard(title: "Frame the result", icon: "photo.on.rectangle") {
                feature("Background and spacing", "Add a solid or gradient background, padding, rounded corners, and a shadow to give the screenshot room to breathe.")
                feature("Export frames", "Choose Original or a landscape, square, or portrait frame. Use the alignment control to place the screenshot inside it; the frame sets the output shape.")
            }
        }
    }

    private var recordingContent: some View {
        VStack(alignment: .leading, spacing: 20) {
            sectionCard(title: "Make a recording", icon: "record.circle") {
                steps([
                    "Choose Record Video or Record GIF from the menu bar. Choose a region, screen, or window and an optional countdown.",
                    "Confirm your recording options, then click Record. Use video for audio and camera; choose GIF for a short silent loop.",
                    "Use the floating controls to pause/resume or stop. An assigned Stop Recording shortcut is global only while recording.",
                    "If a trimmer opens, drag the range handles around the part to keep and save the result."
                ])
            }
            sectionCard(title: "Set up your demo", icon: "mic") {
                feature("Audio and camera", "Video can include system audio, microphone audio, or both, plus a webcam overlay. Choose devices and overlay appearance in Settings → Video; review the options before each take.")
                feature("Attention and timing", "Configure mouse-click highlights, countdown, time limit, and keep-awake behavior. Video saves as MP4; GIF settings choose frame rate and maximum width.")
                feature("Teleprompter", "In Settings → Teleprompter, enter or load a script, choose its scroll speed, and preview it. The script panel stays on your screen and out of the recording.")
            }
            sectionCard(title: "Trim or take a frame", icon: "scissors") {
                feature("Keep only what matters", "Enable the video or GIF trimmer in Settings, or reopen a capture from the library. Adjust the range and playback speed; video can be saved without audio.")
                feature("Still images", "Step through frames and save a frame as an image. The video trimmer can also copy the current frame to the clipboard without saving a file.")
                feature("Need editable layers?", "Record Video always makes an ordinary recording. Start Studio Recording… instead when you want to change the camera layout or add timeline edits afterward.")
            }
        }
    }

    private var studioContent: some View {
        VStack(alignment: .leading, spacing: 20) {
            sectionCard(title: "Start with separate layers", icon: "square.stack.3d.up") {
                feature("Studio", "Record once, then change the composition and refine your video with editable layers and a timeline.")
                steps([
                    "Choose Studio Recording… in the menu bar, select a target, and confirm screen, camera, and audio options.",
                    "Stop the recording to open Studio. You can also open an existing MP4 or MOV with Tiny Clips or from Clips Library to edit it in Studio.",
                    "Choose a background, padding, and screen corners. Pick screen only, camera bubble, side by side, or camera only; crop layers and position the bubble.",
                    "Preview your edits, save the project, and export a new MP4 when it is ready."
                ])
            }
            sectionCard(title: "Tell the story on the timeline", icon: "timeline.selection") {
                feature("Scenes and zooms", "Scenes change the layout partway through. Add zooms by hand or use Suggest Zooms to propose them from recorded clicks; adjust the ranges and focal points.")
                feature("Cuts and speed", "Remove a range or speed it up/slow it down. Speed ranges run from 0.25× to 8×; changed-speed stretches play without sound. Select an edit to adjust or delete it.")
                feature("Annotations", "Add arrows, callouts, text, shapes, emoji, or redactions. Attach them to the recording so they follow zooms, or to the frame so they stay in place.")
            }
            sectionCard(title: "Captions and exports", icon: "captions.bubble") {
                feature("Generate, import, and review", "Generate captions on device on macOS 26 or later with supported hardware, or import SRT/WebVTT. Correct text and timing, split/merge cues, and review Auto Fix results.")
                feature("Draw or provide a file", "Drawing captions onto the picture is optional and off by default. Viewers cannot turn these off; export SRT or WebVTT too when selectable captions matter.")
                feature("Export MP4", "Choose a resolution and quality in Export. Check the shown pixel size and estimated file size; Studio remembers your choices and does not upscale the canvas.")
            }
            sectionCard(title: "Keep your work editable", icon: "folder") {
                feature("Save Project and Save Project As", "Save an editable project folder with its media and .tinyclips file, or save another portable copy without changing the original video.")
                feature("Reopen and manage", "Find projects in Clips Library → Studio Projects or Settings → Studio → Drafts. Open a .tinyclips project with Tiny Clips. Review draft/source-retention choices before closing unsaved work.")
            }
        }
    }

    private var libraryContent: some View {
        VStack(alignment: .leading, spacing: 20) {
            sectionCard(title: "Find a capture", icon: "photo.stack") {
                steps([
                    "Choose Clips Library… in the menu bar. Use Recent Captures for the newest files, or Studio Projects for editable work.",
                    "Search names, tags, and notes. Narrow the results with collections, capture types, and Sort & Filter.",
                    "Switch between grid and list, select a clip, and use its details to preview it or edit its name, tags, notes, and collection.",
                    "Reopen it in the editor, trimmer, or Studio, or copy/share it when it is ready."
                ])
            }
            sectionCard(title: "Keep things organized", icon: "tag") {
                feature("Favorites, tags, and collections", "Star important clips, add searchable tags and notes, and group related captures into collections. Smart collections give you shortcuts to common views.")
                feature("Work with a selection", "Use selection mode for supported batch actions such as favorite, tagging, copying, sharing, and deleting. Reveal files in Finder when you need the actual folder.")
                feature("Library preferences", "Settings → Clips Library controls default views, quick actions, refresh, and archiving old clips. Review delete prompts carefully; deletion cannot be undone from the library on macOS.")
            }
            sectionCard(title: "Share deliberately", icon: "square.and.arrow.up") {
                feature("Local sharing", "Copy a clip, drag it into another app, use Share, or reveal it in Finder. Saving stays local unless Auto-upload new captures to Uploadcare is enabled; review or disable it in Clips Library settings before saving sensitive content.")
                feature("Uploadcare links", "Configure your own Uploadcare account in Clips Library settings before uploading. Credentials stay in the macOS Keychain; selected files go to your Uploadcare project under its terms.")
                feature("Review the output", "Check images for private details and play videos through before sharing. Sharing an exported MP4 is different from sharing an editable project folder containing source media.")
            }
        }
    }

    private var helpContent: some View {
        VStack(alignment: .leading, spacing: 20) {
            sectionCard(title: "Set your defaults", icon: "gearshape") {
                steps([
                    "Open Settings from the menu bar or press ⌘,.",
                    "Choose Screenshot, Video, or GIF to set its save folder, clipboard behavior, and editor or trimmer preference.",
                    "Visit Shortcuts to assign optional global bindings. Leave actions unassigned if you prefer the menu bar.",
                    "Take a short test capture and check Recent Captures or Clips Library to confirm where it went."
                ])
            }
            sectionCard(title: "Your current global shortcuts", icon: "command") {
                Text("Only Screenshot is assigned by default. Change, clear, or reset bindings in Settings → Shortcuts. An unassigned action is still available from the menu bar.")
                shortcutRow(title: "Screenshot", action: .screenshot)
                shortcutRow(title: "Screenshot Region (no picker)", action: .screenshotRegion)
                shortcutRow(title: "Screenshot Window (no picker)", action: .screenshotWindow)
                shortcutRow(title: "Screenshot Screen (no picker)", action: .screenshotScreen)
                shortcutRow(title: "Record Video", action: .recordVideo)
                shortcutRow(title: "Record GIF", action: .recordGif)
                shortcutRow(title: "Stop Recording (during recording only)", action: .stopRecording)
                shortcutRow(title: "Copy Text from Region", action: .copyTextFromRegion)
            }
            sectionCard(title: "Keys inside the app", icon: "keyboard") {
                feature("Picker", "R: Region · S: Screen · W: Window · P: Scroll (screenshots) · Esc: Cancel. Window selection also supports Tab/Shift-Tab and Enter.")
                feature("Editors, Studio, and library", "Their fixed shortcuts work only in the relevant window; they are not global bindings. See the separate references in Settings → Shortcuts and each feature's menus.")
                feature("Settings and Quit", "⌘, opens Settings; ⌘Q quits Tiny Clips. Use the floating Stop button if you have not assigned a recording shortcut.")
            }
            sectionCard(title: "Preferences and permissions", icon: "slider.horizontal.3") {
                feature("After capture", "Choose save folders, clipboard behavior, and whether the editor or trimmer opens for each capture type. Launch at login, notifications, theme, and local analytics live in Settings.")
                feature("Nothing captures?", "Check System Settings → Privacy & Security → Screen Recording, grant access, and restart Tiny Clips. Microphone and camera also need OS permission and the recording option enabled.")
                feature("Missing files or windows?", "Look in Recent Captures, Clips Library, and your configured save folder. Check the editor/trimmer preference if you expected a window; check Settings → Studio if Studio is unavailable.")
                feature("Include Tiny Clips in captures", "Enable the capture-behavior preference when making a demo of the app itself. The teleprompter and live webcam preview stay excluded; the separately recorded camera is unchanged.")
            }
            sectionCard(title: "Updates and help", icon: "questionmark.circle") {
                #if APPSTORE
                feature("Mac App Store", "Update Tiny Clips through the Mac App Store.")
                #else
                feature("Direct download", "Choose Check for Updates… in the menu bar to check for a new version.")
                #endif
                feature("Agent connection", "Keep the app running and Agent Control enabled. Pair as the same local user; check permissions and the audit log in Settings.")
                Link("Agent setup and troubleshooting", destination: URL(string: "https://github.com/jamesmontemagno/tiny-clips/blob/main/docs/agent-integration.md")!)
            }
        }
    }

    private func sectionCard<Content: View>(title: String, icon: String, @ViewBuilder content: () -> Content) -> some View {
        VStack(alignment: .leading, spacing: 14) {
            Label(title, systemImage: icon)
                .font(.headline)
                .accessibilityAddTraits(.isHeader)
            content()
        }
        .padding(16)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(.regularMaterial, in: RoundedRectangle(cornerRadius: 12, style: .continuous))
    }

    private func feature(_ title: String, _ text: String) -> some View {
        VStack(alignment: .leading, spacing: 4) {
            Text(title).font(.subheadline.weight(.semibold))
            Text(text).foregroundStyle(.secondary)
        }
        .accessibilityElement(children: .combine)
    }

    private func steps(_ items: [String]) -> some View {
        VStack(alignment: .leading, spacing: 12) {
            ForEach(Array(items.enumerated()), id: \.offset) { index, text in
                HStack(alignment: .top, spacing: 12) {
                    Text("\(index + 1)")
                        .font(.caption.bold())
                        .frame(width: 24, height: 24)
                        .background(Color.accentColor.opacity(0.12), in: Circle())
                        .accessibilityHidden(true)
                    Text(text).frame(maxWidth: .infinity, alignment: .leading)
                }
                .accessibilityElement(children: .ignore)
                .accessibilityLabel("Step \(index + 1): \(text)")
            }
        }
    }

    private func shortcutRow(title: String, action: HotKeyAction) -> some View {
        LabeledContent(title) {
            Text(settings.hotKeyBinding(for: action).displayString)
                .font(.system(.body, design: .monospaced))
                .foregroundStyle(.secondary)
        }
        .padding(.vertical, 2)
    }
}

private struct GuideIllustration: View {
    let section: GuideSection

    var body: some View {
        VStack(spacing: 16) {
            drawing
            Text("Overview illustration · \(caption)")
                .font(.caption)
                .foregroundStyle(.secondary)
        }
        .padding(20)
        .frame(maxWidth: .infinity)
        .background(
            LinearGradient(colors: [Color.accentColor.opacity(0.12), Color.accentColor.opacity(0.03)],
                           startPoint: .topLeading, endPoint: .bottomTrailing),
            in: RoundedRectangle(cornerRadius: 16)
        )
        .overlay(RoundedRectangle(cornerRadius: 16).stroke(Color.accentColor.opacity(0.18)))
        .accessibilityElement(children: .ignore)
        .accessibilityLabel("Overview illustration. \(caption)")
    }

    private var caption: String {
        switch section {
        case .capture: "Choose a target, capture it, then edit or save."
        case .editor: "An arrow explains the image; a solid block hides private text."
        case .recording: "Screen, audio, and camera become one recording; trim what you keep. The script stays out."
        case .studio: "Separate screen and camera layers combine into a preview, with scenes, zooms, and captions on a timeline."
        case .library: "Collections, a searchable capture grid, and clip details help you find and share your work."
        case .agents: "Your agent connects through CLI or MCP to the local app, behind pairing and permission checks."
        case .help: "Capture, polish, and save; your settings and permissions support each step."
        }
    }

    @ViewBuilder
    private var drawing: some View {
        switch section {
        case .capture:
            HStack(spacing: 16) {
                window(title: "Capture picker") {
                    HStack(spacing: 16) {
                        symbol("selection.pin.in.out", "Region")
                        symbol("display", "Screen")
                        symbol("macwindow", "Window")
                    }
                    RoundedRectangle(cornerRadius: 6)
                        .strokeBorder(Color.accentColor, style: StrokeStyle(lineWidth: 2, dash: [5]))
                        .frame(height: 50)
                        .overlay(Text("Your selection").font(.caption))
                }
                arrow
                symbol("photo.badge.checkmark", "Edit or save")
            }
        case .editor:
            window(title: "Screenshot editor") {
                HStack(spacing: 12) {
                    Image(systemName: "cursorarrow")
                    Image(systemName: "crop")
                    Image(systemName: "arrow.up.right")
                    Image(systemName: "textformat")
                    Image(systemName: "eye.slash")
                    Spacer()
                    Text("Copy · Save").font(.caption)
                }
                .foregroundStyle(.secondary)
                HStack(spacing: 20) {
                    VStack(alignment: .leading, spacing: 10) {
                        Text("Make this clear").font(.headline)
                        RoundedRectangle(cornerRadius: 3).fill(.primary).frame(width: 120, height: 18)
                        Text("Private text covered").font(.caption).foregroundStyle(.secondary)
                    }
                    Spacer()
                    Image(systemName: "arrow.up.left").font(.system(size: 36, weight: .bold)).foregroundStyle(Color.accentColor)
                }
                .padding(14)
                .background(Color.accentColor.opacity(0.08), in: RoundedRectangle(cornerRadius: 8))
            }
        case .recording:
            HStack(alignment: .top, spacing: 16) {
                window(title: "Recording") {
                    ZStack(alignment: .bottomTrailing) {
                        RoundedRectangle(cornerRadius: 8).fill(Color.accentColor.opacity(0.12))
                            .frame(height: 80)
                            .overlay(Image(systemName: "display").font(.largeTitle))
                        Image(systemName: "person.fill")
                            .frame(width: 38, height: 38)
                            .background(.background, in: Circle())
                            .overlay(Circle().stroke(Color.accentColor, lineWidth: 2))
                            .padding(8)
                    }
                    HStack {
                        Label("Audio", systemImage: "waveform")
                        Spacer()
                        Label("Stop", systemImage: "stop.circle")
                    }.font(.caption)
                    timeline("Keep", pieces: 3)
                }
                VStack(spacing: 10) {
                    symbol("text.alignleft", "Script")
                    Text("Not captured").font(.caption).foregroundStyle(.secondary)
                }.padding(.top, 18)
            }
        case .studio:
            VStack(spacing: 14) {
                HStack(spacing: 16) {
                    VStack(spacing: 10) {
                        symbol("display", "Screen")
                        symbol("person.crop.rectangle", "Camera")
                    }
                    arrow
                    window(title: "Studio preview") {
                        HStack {
                            RoundedRectangle(cornerRadius: 6).fill(Color.accentColor.opacity(0.15))
                                .overlay(Image(systemName: "display"))
                            Image(systemName: "person.fill")
                                .frame(width: 42, height: 60)
                                .background(Color.accentColor.opacity(0.12), in: RoundedRectangle(cornerRadius: 6))
                        }.frame(height: 70)
                        Text("Captions appear here").font(.caption)
                    }
                }
                VStack(spacing: 6) {
                    timeline("Scenes", pieces: 3)
                    timeline("Zooms", pieces: 2)
                    timeline("Captions", pieces: 4)
                }
            }
        case .library:
            window(title: "Clips Library · Search") {
                HStack(alignment: .top, spacing: 16) {
                    VStack(alignment: .leading, spacing: 10) {
                        Label("Favorites", systemImage: "star")
                        Label("Collections", systemImage: "folder")
                        Label("Tags", systemImage: "tag")
                    }.font(.caption)
                    Divider()
                    VStack(spacing: 8) {
                        ForEach(0..<2) { _ in
                            HStack(spacing: 8) {
                                ForEach(0..<3) { index in
                                    RoundedRectangle(cornerRadius: 5)
                                        .fill(Color.accentColor.opacity(0.1))
                                        .frame(minWidth: 30, maxWidth: .infinity)
                                        .frame(height: 40)
                                        .overlay(Image(systemName: index == 1 ? "play.rectangle" : "photo"))
                                }
                            }
                        }
                    }
                    Divider()
                    VStack(alignment: .leading, spacing: 10) {
                        Text("Details").font(.caption.bold())
                        Text("Tags · Notes").font(.caption)
                        Label("Share", systemImage: "square.and.arrow.up").font(.caption)
                    }
                }
            }
        case .agents:
            VStack(spacing: 14) {
                HStack(spacing: 16) {
                    symbol("terminal", "Your agent")
                    arrow
                    symbol("point.3.connected.trianglepath.dotted", "CLI / MCP")
                    arrow
                    symbol("camera", "Tiny Clips")
                }
                Label("Pairing · Scopes · Approval · Audit", systemImage: "lock.shield")
                    .font(.caption.weight(.medium))
                    .padding(10)
                    .background(.background, in: Capsule())
            }
        case .help:
            VStack(spacing: 16) {
                HStack(spacing: 18) {
                    symbol("camera", "Capture")
                    arrow
                    symbol("wand.and.stars", "Polish")
                    arrow
                    symbol("folder", "Save & find")
                }
                HStack(spacing: 24) {
                    Label("Settings", systemImage: "slider.horizontal.3")
                    Label("Permissions", systemImage: "lock.shield")
                }.font(.caption).foregroundStyle(.secondary)
            }
        }
    }

    private var arrow: some View {
        Image(systemName: "arrow.right").foregroundStyle(Color.accentColor)
    }

    private func symbol(_ icon: String, _ title: String) -> some View {
        VStack(spacing: 8) {
            Image(systemName: icon)
                .font(.system(size: 24))
                .foregroundStyle(Color.accentColor)
                .frame(width: 48, height: 42)
                .background(.background, in: RoundedRectangle(cornerRadius: 10))
            Text(title).font(.caption.weight(.medium))
        }
    }

    private func window<Content: View>(title: String, @ViewBuilder content: () -> Content) -> some View {
        VStack(alignment: .leading, spacing: 12) {
            HStack(spacing: 4) {
                ForEach(0..<3) { _ in Circle().fill(.secondary.opacity(0.4)).frame(width: 6, height: 6) }
                Text(title).font(.caption.weight(.medium)).padding(.leading, 6)
                Spacer(minLength: 0)
            }
            Divider()
            content()
        }
        .padding(12)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(.background, in: RoundedRectangle(cornerRadius: 10))
        .overlay(RoundedRectangle(cornerRadius: 10).stroke(.secondary.opacity(0.2)))
    }

    private func timeline(_ title: String, pieces: Int) -> some View {
        HStack(spacing: 6) {
            Text(title).font(.caption).frame(width: 60, alignment: .leading)
            ForEach(0..<pieces, id: \.self) { _ in
                RoundedRectangle(cornerRadius: 3)
                    .fill(Color.accentColor.opacity(0.3))
                    .frame(height: 12)
                    .frame(maxWidth: .infinity)
            }
        }
    }
}
