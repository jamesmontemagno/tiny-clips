cask "tiny-clips" do
  version "2.3.0.2"
  sha256 "c5cb3aa2186a5e40114fa8da6e0f5c581ab6c5532fbd41ec73237897c64fcbde"

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
