# Changelog

All notable changes to this project are documented here. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and versions follow [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [0.6.0-alpha.3] - 2026-11-01

### Changed
- Updates are checked at launch, before the main window opens. If a newer version is available, an update window offers Update now, Not now or Skip this version. Update now downloads, verifies and restarts straight into the new version.
- Updates found while the app is running download in the background, and a banner at the top of every page offers to restart into the new version.
- The controller bridge starts after the update check, so an update no longer connects the controller only to drop it again.

### Fixed
- Clicking a tray notification now opens the app.
- The update button was only on the Overview page. It is now also in Settings, next to the update status.

## [0.6.0-alpha.2] - 2026-11-01

### Fixed
- The controller speaker stayed silent when the DualSense was picked as the Windows output device. The speaker volume and mute state are now sent to the controller when audio starts.
- Speaker audio cut out because it reached the controller in bursts. Audio reports are now buffered and sent on a steady clock.
- The mic button did nothing. It now toggles the microphone mute and the mute light, and the virtual microphone sends silence while muted.
- After a crash, or when running from a new folder, the app could not see a controller that HidHide was still hiding. The app now registers itself with HidHide at startup.

### Added
- `--speaker-test` diagnostic that plays a test tone through the controller speaker.

## [0.6.0-alpha.1] - 2026-10-31

First public pre-release.

### Added
- Virtual wired DualSense over Bluetooth through usbip-win2: HD haptics, speaker, headset jack, microphone, adaptive triggers and lightbar.
- Virtual Xbox 360 controller for games without DualSense support, with rumble played as HD haptics or classic rumble and player LEDs.
- Desktop app with tray icon, battery level and low battery alert.
- Game library for Steam, Epic, GOG, Ubisoft Connect, Xbox, EA, Battle.net and game folders, with icons and Steam cover art.
- PCGamingWiki lookup of each game's DualSense support, manual matching, and PlayStation button prompt mod hints.
- In-game overlay (Ctrl+Alt+Z) and per-game profiles.
- Haptics generated from the game's own audio.
- Trigger Lab: build, capture and replay adaptive trigger effects.
- Lightbar effects: breathing, rainbow, battery level, audio reactive.
- Guided install of usbip-win2 and HidHide from their official GitHub releases.
- Automatic updates from GitHub releases with checksum verification.
- Showcase mode (`--demo`) and diagnostics (`--selftest`, `--probe`, `--games`, `--capture`).

### Known issues
- The overlay doesn't appear over exclusive-fullscreen games.
- A DualSense Edge is presented as a regular DualSense.
- Covers are shown for Steam games only.

[0.6.0-alpha.3]: https://github.com/florinp93/ZerkSense5/releases/tag/v0.6.0-alpha.3
[0.6.0-alpha.2]: https://github.com/florinp93/ZerkSense5/releases/tag/v0.6.0-alpha.2
[0.6.0-alpha.1]: https://github.com/florinp93/ZerkSense5/releases/tag/v0.6.0-alpha.1
