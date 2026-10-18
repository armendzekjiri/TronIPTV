# Contributing to TronIPTV

Thanks for helping improve TronIPTV! This guide covers how to build the project, the conventions we follow, and how releases are made.

By participating you agree to follow the [Code of Conduct](CODE_OF_CONDUCT.md).

## Getting started

Prerequisites:

- Windows 10 1809 or later (the app is WPF; the core library and tests also build on other platforms)
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0). The exact feature band is pinned in [`global.json`](global.json).
- An IDE: Visual Studio 2026, JetBrains Rider, or VS Code with C# Dev Kit

```bash
dotnet build            # build everything
dotnet test             # run the test suite
dotnet run --project src/TronIPTV
```

Set `TRONIPTV_DATA_DIR` to a scratch folder while developing, so your real settings and cache stay untouched.

## Project layout

| Path | Contents |
| --- | --- |
| `src/TronIPTV.Core` | UI-independent logic: M3U parser, Xtream Codes client, streaming XMLTV parser, downloads, saved libraries, settings. Keep it free of WPF. |
| `src/TronIPTV` | The WPF app: view models (CommunityToolkit.Mvvm), libVLC player wrapper, views, and the theme in `Themes/Dark.xaml`. |
| `tests/TronIPTV.Core.Tests` | xUnit tests for the core library. |
| `installer/` | Inno Setup script used by the release workflow. |
| `.github/workflows/` | CI, CodeQL, PR title check, and release automation. |

Build settings shared by every project live in `Directory.Build.props`. NuGet versions are managed centrally in `Directory.Packages.props`, so never put a `Version` on a `PackageReference`.

## Making changes

1. Open an issue first for anything non-trivial, so we can agree on the approach.
2. Create a branch from `main`, for example `feat/catchup` or `fix/epg-timezone`.
3. Keep changes focused, and add or update tests for behavior in `TronIPTV.Core`.
4. Follow the existing style. [`.editorconfig`](.editorconfig) is the source of truth, and CI builds with warnings as errors.
5. Add a line under **Unreleased** in [CHANGELOG.md](CHANGELOG.md) for user-visible changes.
6. Open a pull request. Its title must follow [Conventional Commits](https://www.conventionalcommits.org/), because it becomes the squash-merge commit message:

   ```
   feat: add catch-up playback for Xtream providers
   fix(epg): handle programmes without a stop time
   docs: clarify portable mode
   ```

   Allowed types: `feat`, `fix`, `perf`, `refactor`, `docs`, `test`, `build`, `ci`, `chore`, `revert`. Mark breaking changes with `!` (for example `feat!:`).

### Ground rules

- Never commit real playlist URLs, Xtream credentials, or provider data, not even in tests. Use `example.com` hosts.
- Secrets at rest must go through `SecretProtector` (DPAPI).
- Don't add telemetry or background network calls; the app only talks to the user's providers, plus GitHub when the user checks for updates.
- New dependencies must have a license compatible with GPL-3.0-or-later. Add them to [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

## Versioning and releases

TronIPTV follows [Semantic Versioning](https://semver.org/). The version is not stored in any file: [MinVer](https://github.com/adamralph/minver) derives it from the latest `v*` git tag at build time. Untagged builds get a pre-release version such as `0.1.1-alpha.0.3`.

To publish a release (maintainers):

1. Move the **Unreleased** entries in `CHANGELOG.md` under a new `## [x.y.z] - YYYY-MM-DD` heading, update the compare links, and merge to `main`.
2. Tag the merge commit and push the tag:

   ```bash
   git tag -a v0.2.0 -m "TronIPTV 0.2.0"
   git push origin v0.2.0
   ```

3. The [Release workflow](.github/workflows/release.yml) then:
   - runs the tests;
   - publishes a self-contained, ReadyToRun `win-x64` build and checks the version stamp;
   - builds the portable zip and the installer, and writes `SHA256SUMS.txt`;
   - attests build provenance;
   - creates the GitHub release with notes from the changelog.

   `0.x` versions and tags with a pre-release suffix (`v1.0.0-rc.1`) are published as pre-releases.

## License

By contributing, you agree that your contributions are licensed under the [GNU General Public License v3.0 or later](LICENSE), the same license as the project.
