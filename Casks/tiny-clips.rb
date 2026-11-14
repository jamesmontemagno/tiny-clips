cask "tiny-clips" do
  version "2.1.0.4"
  sha256 "e914633014bb0f50aade8d8fd0da92ea900e1e736d2fc7dd8df463f073944a28"

  url "https://github.com/jamesmontemagno/tiny-clips/releases/download/v#{version}-mac/TinyClips-v#{version}-mac.zip"
  name "TinyClips"
  desc "Menu bar app for screenshot, video, and GIF capture"
  homepage "https://github.com/jamesmontemagno/tiny-clips"

  auto_updates true
  depends_on :macos

  app "TinyClips.app"

  postflight_steps do
    run "/usr/bin/xattr", args: ["-dr", "com.apple.quarantine", "{{appdir}}/TinyClips.app"]
  end

  zap trash: "~/Library/Preferences/com.tinyclips.app.plist"
end
