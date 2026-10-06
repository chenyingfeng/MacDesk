# Third-party notices

MacDesk contains modified Stage Manager sources and a companion Dock. The project license in [LICENSE](LICENSE) applies to project code; it does not replace the licenses of dependencies, operating-system components, or application icons obtained from the local machine.

## Source provenance

- The Stage Manager portion is based on [StageManagerForWindows](https://github.com/BruhTheMomentum/StageManagerForWindows), from the locally used 0.8.3 source tree. At publication preparation, that upstream URL redirects to [depoledna/StageManagerForWindows](https://github.com/depoledna/StageManagerForWindows). Its upstream license is MIT, retaining Copyright (c) 2022 Andreas Wäscher.
- That project is based on [awaescher/StageManager](https://github.com/awaescher/StageManager). See its [MIT license](https://github.com/awaescher/StageManager/blob/main/LICENSE).
- The upstream projects acknowledge window-tracking code from [workspacer](https://github.com/workspacer/workspacer). Its [MIT license](https://github.com/workspacer/workspacer/blob/master/LICENSE) retains Copyright (c) 2018 Rick Button; a copy is included in [licenses/workspacer-MIT.txt](licenses/workspacer-MIT.txt). The notice and MIT terms below also apply to those derived portions.
- MacDesk changes include window recovery, taskbar visibility coordination, right-sidebar interaction, multi-window behavior, and the companion Dock. This repository starts with a sanitized source snapshot rather than the local development history.

## Managed NuGet dependencies

The versions below are the direct and resolved transitive packages for the supplied Stage Manager project. License identifiers and copyright notices were checked against the versioned NuGet metadata. Packages are restored through NuGet; their source and license links are provided for inspection.

| Package | Version | License | Copyright / source |
| --- | --- | --- | --- |
| [AsyncAwaitBestPractices](https://www.nuget.org/packages/AsyncAwaitBestPractices/9.0.0) | 9.0.0 | MIT | ©Copyright 2023 Brandon Minnick. All rights reserved. — [source](https://github.com/brminnick/AsyncAwaitBestPractices) |
| [ControlzEx](https://www.nuget.org/packages/ControlzEx/7.0.1) | 7.0.1 | MIT | Copyright © 2015 - 2025 Jan Karger, Bastian Schmidt, James Willock — [source](https://github.com/ControlzEx/ControlzEx) |
| [Hardcodet.NotifyIcon.Wpf](https://www.nuget.org/packages/Hardcodet.NotifyIcon.Wpf/2.0.1) | 2.0.1 | MIT | Copyright (c) 2009 - 2019 Philipp Sumi, 2019 - 2024 Philipp Sumi, Robin Krom, Jan Karger — [source](https://github.com/hardcodet/wpf-notifyicon) |
| [SharpHook](https://www.nuget.org/packages/SharpHook/6.1.2) | 6.1.2 | MIT for managed wrapper; native dependency below | (c) 2021 Anatoliy Pylypchuk — [version source](https://github.com/TolikPylypchuk/SharpHook/tree/853fa5d5f6295070b233d192ede7acd7761a99ef) |
| [Vortice.Direct3D11](https://www.nuget.org/packages/Vortice.Direct3D11/3.8.1) | 3.8.1 | MIT | Copyright (c) Amer Koleci and Contributors — [source](https://github.com/amerkoleci/Vortice.Windows) |
| [Vortice.DXGI](https://www.nuget.org/packages/Vortice.DXGI/3.8.1) | 3.8.1 | MIT | Copyright (c) Amer Koleci and Contributors — [source](https://github.com/amerkoleci/Vortice.Windows) |
| [Vortice.DirectX](https://www.nuget.org/packages/Vortice.DirectX/3.8.1) | 3.8.1 | MIT | Copyright (c) Amer Koleci and Contributors — [source](https://github.com/amerkoleci/Vortice.Windows) |
| [Vortice.Mathematics](https://www.nuget.org/packages/Vortice.Mathematics/2.0.0) | 2.0.0 | MIT | Copyright (c) Amer Koleci and Contributors — [source](https://github.com/amerkoleci/Vortice.Mathematics) |
| [SharpGen.Runtime](https://www.nuget.org/packages/SharpGen.Runtime/2.4.2-beta) | 2.4.2-beta | MIT | (c) 2010-2017 Alexandre Mutel, 2017-2023 Jeremy Koritzinsky, 2023-2024 Amer Koleci — [source](https://github.com/SharpGenTools/SharpGenTools) |
| [SharpGen.Runtime.COM](https://www.nuget.org/packages/SharpGen.Runtime.COM/2.4.2-beta) | 2.4.2-beta | MIT | (c) 2010-2017 Alexandre Mutel, 2017-2023 Jeremy Koritzinsky, 2023-2024 Amer Koleci — [source](https://github.com/SharpGenTools/SharpGenTools) |
| [Microsoft.Xaml.Behaviors.Wpf](https://www.nuget.org/packages/Microsoft.Xaml.Behaviors.Wpf/1.1.77) | 1.1.77 | MIT | © Microsoft Corporation. All rights reserved. — [source](https://github.com/microsoft/XamlBehaviorsWpf) |
| [WpfScreenHelper](https://www.nuget.org/packages/WpfScreenHelper/2.1.1) | 2.1.1 | MIT | Copyright Michael Denny 2019 — [source](https://github.com/micdenny/WpfScreenHelper) |

## libuiohook native library

SharpHook 6.1.2 includes the native shared library `uiohook.dll`. Its managed MIT declaration does not change the native library license. [libuiohook](https://github.com/TolikPylypchuk/libuiohook) is licensed under **LGPL-3.0-or-later**, as stated in its source headers:

Copyright (C) 2006-2023 Alexander Barker. All Rights Reserved.

The SharpHook package source commit `853fa5d5f6295070b233d192ede7acd7761a99ef` records the libuiohook submodule at `a8d1578835f0d88d751a31153a1169208c389039`. [GitHub gitlink metadata](https://api.github.com/repos/TolikPylypchuk/SharpHook/contents/libuiohook?ref=853fa5d5f6295070b233d192ede7acd7761a99ef) identifies that exact revision. The native sources retain their original copyright notices.

The full GPL and LGPL license texts accompany this project in `licenses/`. The complete matching source tree is included in [third_party/libuiohook](third_party/libuiohook), with [build and library replacement instructions](third_party/libuiohook/STAGEDOCK-SOURCE.md). Keep these notices, the matching source, and the instructions with redistributed binary bundles. Do not prevent users from replacing the shared native DLL with an interface-compatible modified version. Modification and reverse engineering for debugging modifications to this LGPL library are permitted under its license.

The Hardcodet package's additional original license notice, Copyright (c) Philipp Sumi; All rights reserved, is preserved in [licenses/Hardcodet.NotifyIcon.Wpf-MIT.txt](licenses/Hardcodet.NotifyIcon.Wpf-MIT.txt).

## MIT license terms for the MIT components above

The copyright notices above are retained with the following permission notice:

```text
Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

## Bundled .NET runtime

The self-contained Windows x64 release includes Microsoft.NETCore.App and Microsoft.WindowsDesktop.App **10.0.12**, as recorded in the generated `StageManager.runtimeconfig.json`. [NETCore runtime package metadata](https://www.nuget.org/packages/Microsoft.NETCore.App.Runtime.win-x64/10.0.12) and [WindowsDesktop runtime package metadata](https://www.nuget.org/packages/Microsoft.WindowsDesktop.App.Runtime.win-x64/10.0.12) identify their MIT license, © Microsoft Corporation. All rights reserved, and source commit `95017c711e6afc1085133d440e42b4bd78155701` in [dotnet/dotnet](https://github.com/dotnet/dotnet/tree/95017c711e6afc1085133d440e42b4bd78155701).

The following official license and third-party notices accompany the release:

- [NETCore runtime MIT license](licenses/runtime-NETCore-MIT-10.0.12.txt), retaining Copyright (c) .NET Foundation and Contributors; All rights reserved.
- [NETCore runtime third-party notices](licenses/runtime-NETCore-ThirdPartyNotices-10.0.12.txt), copied unmodified from the matching runtime package.
- [WindowsDesktop runtime MIT license](licenses/runtime-WindowsDesktop-MIT-10.0.12.txt), retaining Copyright (c) .NET Foundation and Contributors; All rights reserved.
- [WPF third-party notices](licenses/runtime-WPF-ThirdPartyNotices-10.0.12.txt), copied from [the runtime's source revision](https://github.com/dotnet/dotnet/blob/95017c711e6afc1085133d440e42b4bd78155701/src/wpf/THIRD-PARTY-NOTICES.TXT).
- [Windows Forms third-party notices](licenses/runtime-WindowsForms-ThirdPartyNotices-10.0.12.txt), copied from [the same source revision](https://github.com/dotnet/dotnet/blob/95017c711e6afc1085133d440e42b4bd78155701/src/winforms/THIRD-PARTY-NOTICES.TXT).

Keep these files when redistributing a bundle containing the runtime. If the build resolves a different runtime version, refresh its version-specific official licenses and notices before publishing; the MacDesk project MIT license does not replace those notices.

## Platform and artwork

Windows and the locally installed .NET Framework components used by the companion Dock have their own terms. MacDesk calls Windows APIs and depends on those locally installed platform components. Application icons are read from applications and shortcuts on the user's computer; third-party application executables and icon collections are not distributed as project artwork.

The public project includes no university emblem, personal photograph, personal profile card, mailbox credentials, or private website list. The project is independent and is not endorsed by Apple, Microsoft, Tencent, Lenovo, or the upstream maintainers. Product names identify compatibility or inspiration; they remain the property of their respective owners.
