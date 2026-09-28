cask "tiny-clips" do
  version "1.8.0.0"
  sha256 "8abae6bac37223a478210687dfa5f532ac3c2c61b74cb014ac70beef17f46c51"

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
