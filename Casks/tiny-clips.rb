cask "tiny-clips" do
  version "1.7.5"
  sha256 "9c1422e7dbcf697235f97eaa8841e4a27e18198a16da5111297156892b26ffe0"

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
