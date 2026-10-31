# Changelog

All notable changes to this project are documented here. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and versions follow [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

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

[0.6.0-alpha.1]: https://github.com/florinp93/ZerkSense5/releases/tag/v0.6.0-alpha.1
