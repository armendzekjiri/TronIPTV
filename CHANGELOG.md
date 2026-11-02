# Changelog

All notable changes to TronIPTV are documented here.
The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the project uses [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added

- winget (`ArmendZekjiri.TronIPTV`) and Scoop (`tron-iptv` bucket in this repository) packages, updated automatically by the release workflow.

## [0.1.0] - 2026-10-19

First public pre-release.

### Added

- Playlist sources: Xtream Codes (live, movies, series), M3U/M3U8 URLs, and local M3U files.
- Live TV list with channel logos, numbers, now-playing programme, time left, and progress.
- Poster grid for movies and series with ratings; series open into seasons and episodes, and episodes auto-advance.
- XMLTV TV guide (plain or gzip) from playlist headers, Xtream `xmltv.php`, or a custom URL, with name-based matching when `tvg-id` is missing.
- Separate category filter and channel/title search.
- Favorites and recently watched lists, kept per playlist.
- libVLC playback with hardware decoding, audio/subtitle track menus, aspect ratios, seeking, fullscreen with auto-hiding controls, and keyboard shortcuts.
- Automatic reconnect with backoff for dropped live streams.
- Saved playlist library: after the first load, playlists open from disk and only re-download on **Update**; a failed update keeps the saved library.
- DPAPI encryption for stored passwords, playlist URLs, and saved libraries.
- In-app version display, **About**, **Check for updates**, and **Report an issue**.
- Windows installer (per-user, no admin needed), portable zip, SHA-256 checksums, and build-provenance attestations.

[Unreleased]: https://github.com/armendzekjiri/TronIPTV/compare/v0.1.0...HEAD
[0.1.0]: https://github.com/armendzekjiri/TronIPTV/releases/tag/v0.1.0
