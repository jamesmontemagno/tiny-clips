cask "tiny-clips" do
  auto_updates true
  version "1.6.1"
  sha256 "78d38e22eae6c09b6c15eb08af786ff2f123771621a9e5aaba2cd689e09ecae6"

  url "https://github.com/jamesmontemagno/tiny-clips/releases/download/v#{version}-mac/TinyClips-v#{version}-mac.zip"
  name "TinyClips"
  desc "Menu bar app for screenshot, video, and GIF capture"
  homepage "https://github.com/jamesmontemagno/tiny-clips"

  app "TinyClips.app"

  postflight do
    system "xattr", "-dr", "com.apple.quarantine", "#{appdir}/TinyClips.app"
  end

  zap trash: [
    "~/Library/Preferences/com.tinyclips.app.plist",
  ]
end
