import CoreGraphics
import Vision

enum TextRecognitionResult {
    case success(String)
    case noTextFound
}

enum TextRecognitionError: LocalizedError {
    case requestFailed(Error)

    var errorDescription: String? {
        switch self {
        case .requestFailed(let error):
            return error.localizedDescription
        }
    }
}

struct TextRecognitionService {
    private static let queue = DispatchQueue(
        label: "com.tinyclips.text-recognition",
        qos: .userInitiated
    )

    static func recognizeText(in image: CGImage) async throws -> TextRecognitionResult {
        try await withCheckedThrowingContinuation { continuation in
            queue.async {
                do {
                    let request = VNRecognizeTextRequest()
                    request.revision = VNRecognizeTextRequestRevision3
                    request.recognitionLevel = .accurate
                    request.usesLanguageCorrection = true
                    request.automaticallyDetectsLanguage = true

                    let handler = VNImageRequestHandler(cgImage: image, options: [:])
                    try handler.perform([request])

                    let text = (request.results ?? [])
                        .compactMap { $0.topCandidates(1).first?.string }
                        .filter { !$0.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty }
                        .joined(separator: "\n")

                    continuation.resume(
                        returning: text.isEmpty ? .noTextFound : .success(text)
                    )
                } catch {
                    continuation.resume(throwing: TextRecognitionError.requestFailed(error))
                }
            }
        }
    }

    static func findSensitiveText(in image: CGImage) async throws -> [SensitiveTextSuggestion] {
        try await withCheckedThrowingContinuation { continuation in
            queue.async {
                do {
                    let request = VNRecognizeTextRequest()
                    request.revision = VNRecognizeTextRequestRevision3
                    request.recognitionLevel = .accurate
                    request.usesLanguageCorrection = false
                    request.automaticallyDetectsLanguage = true
                    try VNImageRequestHandler(cgImage: image, options: [:]).perform([request])
                    let size = CGSize(width: image.width, height: image.height)
                    var suggestions: [SensitiveTextSuggestion] = []
                    for observation in request.results ?? [] {
                        guard let candidate = observation.topCandidates(1).first else { continue }
                        for match in SensitiveTextDetection.matches(in: candidate.string) {
                            guard let range = Range(match.range, in: candidate.string) else { continue }
                            let bounds: CGRect
                            if let exact = try? candidate.boundingBox(for: range) {
                                bounds = exact.boundingBox
                            } else {
                                var wordBounds = CGRect.null
                                let words = SensitiveTextDetection.wordRanges(overlapping: match.range, in: candidate.string)
                                var hasEveryWord = !words.isEmpty
                                for word in words {
                                    guard let wordRange = Range(word, in: candidate.string),
                                          let box = try? candidate.boundingBox(for: wordRange) else {
                                        hasEveryWord = false
                                        break
                                    }
                                    wordBounds = wordBounds.union(box.boundingBox)
                                }
                                // Never estimate glyph widths or omit a word whose bounds are missing.
                                bounds = hasEveryWord ? wordBounds : observation.boundingBox
                            }
                            if let rect = SensitiveTextDetection.imageRect(fromVision: bounds, pixelSize: size) {
                                suggestions.append(SensitiveTextSuggestion(kind: match.kind, text: String(candidate.string[range]), rect: rect))
                            }
                        }
                    }
                    continuation.resume(returning: suggestions.sorted {
                        if $0.rect.minY != $1.rect.minY { return $0.rect.minY < $1.rect.minY }
                        return $0.rect.minX < $1.rect.minX
                    })
                } catch {
                    continuation.resume(throwing: TextRecognitionError.requestFailed(error))
                }
            }
        }
    }
}
