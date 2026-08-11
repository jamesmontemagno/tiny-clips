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
}
