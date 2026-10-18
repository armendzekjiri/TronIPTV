# Third-party notices

TronIPTV is licensed under the GNU General Public License v3.0 or later. It includes or uses the third-party components below, each under its own license.

## Redistributed with TronIPTV

| Component | Version | License | Source |
| --- | --- | --- | --- |
| libVLC (VideoLAN) and its plugins | 3.0.24 | LGPL-2.1-or-later; some plugins are GPL-2.0-or-later | https://code.videolan.org/videolan/vlc |
| LibVLCSharp, LibVLCSharp.WPF | 3.10.2 | LGPL-2.1-or-later | https://code.videolan.org/videolan/LibVLCSharp |
| .NET runtime and Windows Desktop runtime (self-contained) | 10.0 | MIT | https://github.com/dotnet/runtime, https://github.com/dotnet/wpf |
| CommunityToolkit.Mvvm | 8.4.2 | MIT | https://github.com/CommunityToolkit/dotnet |
| System.Security.Cryptography.ProtectedData | 10.0.13 | MIT | https://github.com/dotnet/runtime |

The libVLC binaries come from the `VideoLAN.LibVLC.Windows` NuGet package. Their source code is available from VideoLAN at the link above. TronIPTV loads libVLC dynamically and does not modify it.

## Used only to build, test, or package

| Component | License | Source |
| --- | --- | --- |
| MinVer | Apache-2.0 | https://github.com/adamralph/minver |
| xUnit.net | Apache-2.0 | https://github.com/xunit/xunit |
| Microsoft.NET.Test.Sdk | MIT | https://github.com/microsoft/vstest |
| coverlet | MIT | https://github.com/coverlet-coverage/coverlet |
| Inno Setup | Inno Setup License | https://jrsoftware.org/isinfo.php |

## Other

- Icons come from the Segoe Fluent Icons / Segoe MDL2 Assets fonts, and text uses Segoe UI Variable. All ship with Windows and are not redistributed.
- The README screenshot shows *Big Buck Bunny*, © Blender Foundation, licensed under [CC BY 3.0](https://creativecommons.org/licenses/by/3.0/) (https://peach.blender.org).
