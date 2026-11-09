cask "tiny-clips" do
  version "2.1.0.1"
  sha256 "12dfa18888bd2975022b8c8383c650586bee9154686a26f816dd2980008e7385"

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
