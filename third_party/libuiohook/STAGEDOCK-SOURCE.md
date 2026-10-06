# libuiohook source included with MacDesk

This directory contains the complete unmodified text-file tree of [TolikPylypchuk/libuiohook](https://github.com/TolikPylypchuk/libuiohook/tree/a8d1578835f0d88d751a31153a1169208c389039), revision:

`a8d1578835f0d88d751a31153a1169208c389039`

It is the submodule revision recorded by the [SharpHook 6.1.2 package source](https://github.com/TolikPylypchuk/SharpHook/tree/853fa5d5f6295070b233d192ede7acd7761a99ef). All 52 upstream blobs were retrieved from that revision and checked against their Git blob SHA-1 and byte size. This explanatory file is the only MacDesk addition to the upstream tree.

The source headers license the library under LGPL-3.0-or-later. Read [COPYING.LESSER.md](COPYING.LESSER.md), [COPYING.md](COPYING.md), and [AUTHORS](AUTHORS). MacDesk does not modify the native library. Its binary is supplied by the SharpHook NuGet package.

## Build and replace the Windows shared library

Install CMake and the Visual Studio C++ build tools. For the x64 MacDesk bundle, run from this directory:

```powershell
cmake -S . -B build -G "Visual Studio 17 2022" -A x64 -D BUILD_SHARED_LIBS=ON -D BUILD_DEMO=OFF
cmake --build build --parallel 2 --config RelWithDebInfo
```

The corresponding upstream workflow, [.github/workflows/package.yml](.github/workflows/package.yml), documents the Windows architectures and shared-library flags used by this revision. See [README.md](README.md) for additional build options.

To use an interface-compatible modified library, fully exit MacDesk, including its Stage Manager process. Back up the existing `uiohook.dll` beside `StageManager.exe`, then replace that DLL with your rebuilt x64 DLL and restart. Retain the same exported interface expected by SharpHook 6.1.2. For another bundle architecture, build a matching architecture. Modification and reverse engineering for debugging modifications to this library are permitted under its license.

The release must keep `uiohook.dll` as a separate replaceable shared library. Do not embed it in a single-file executable or apply an integrity policy that refuses interface-compatible replacement libraries. The MacDesk source provides the corresponding application code; redistribute this native source directory, its build information, and the license texts with binary releases.

These are upstream build instructions, not a claim that MacDesk maintainers reproduced the NuGet library bit for bit. The vendored sources match the exact submodule revision declared by the package source.
