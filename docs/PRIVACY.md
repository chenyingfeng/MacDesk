# Privacy and local data

MacDesk is a local Windows desktop utility. The public source and release bundle contain generic defaults. Personal profile cards, photographs, university emblems, private bookmarks, mail-integration code and account files from the original local setup are excluded. The repository uses a fresh source history so earlier local development files and runtime data are not published.

## What the program accesses

To list applications, draw live previews, restore windows and coordinate the taskbar, MacDesk reads Windows window information: process names and executable identities, process IDs, window handles, captions, window state, geometry, DPI and monitor information. Live previews can display the contents of other application windows on your own desktop. There is no upload or recording feature for these previews.

The sidebar uses a global input hook for window-management interactions and its shortcuts. The companion Dock also observes pointer position and its own interaction events. The supplied code does not implement a typed-text recorder. QQ and WeChat recovery inspects Windows notification-area accessibility elements and invokes a uniquely identified app icon when you request recovery; it does not read chat history or account credentials. These window-management permissions are broad enough that using administrator mode deserves a deliberate choice.

Ordinary startup runs with your current permissions. The explicit administrator menu requests Windows authorization to manage higher-privilege windows. This does not bypass that authorization prompt or configure automatic elevation.

## Files created locally

The portable layout writes settings and coordination files beside its executables. Use a folder writable by your Windows account.

| Data | Examples | Possible personal information |
| --- | --- | --- |
| Dock pins | `NativeDock/dock-pins.json`, `.bak`, `.pending` | Pin names, application and folder paths, executable identities, and website URLs that you add |
| Dock appearance and location | `NativeDock/theme.txt`, `NativeDock/placement.ini` | Selected theme, edge, offset, and monitor device name |
| Workspace settings | `CampusStage/task-groups.json`, `CampusStage/window-bookmarks.json` | Group names, window-title hints, document paths, bookmarked URLs and monitor/layout information |
| Runtime coordination | `stage-apps.json`, `dock-activation.request.json`, `workspace-launch.request.json`, `workspace-launch.response.json`, taskbar requests, `*.ini`, `*.status`, markers and watchdog state | Process/window IDs, executable paths, activation targets, timestamps, state and diagnostic counters |
| Error and diagnostic output | `dock-error.log`, `stagemanager-crash.log`, manually requested check reports | Exception types and stack traces; diagnostic reports may identify processes or windows |
| Developer debug output | `stagemanager.log` in Debug builds | Window captions, process/executable identities, scene names and operation history |

Settings and bookmarks are ordinary local files, not encrypted secret stores. Avoid putting passwords, access tokens or authenticated session URLs in web pins or bookmarks. A URL can contain sensitive query parameters even if it is a valid HTTP or HTTPS address. Do not share your entire running installation directory: share the clean release archive instead.

Release builds omit the ordinary debug logger, but retain crash logging and selected runtime health/recovery reports. A crash stack trace can include file or build paths. Review and redact logs before posting an issue; a privacy scan of the source does not make every future runtime log anonymous.

The repository ignore rules exclude runtime files, settings, backups, local build output and release staging. Ignore rules are a safeguard, not a substitute for reviewing files before publishing changes. Removing a file after committing it does not remove it from Git history.

## Network behavior

The supplied application source has no background telemetry, analytics endpoint, account login service or mailbox connection. The upstream automatic updater is disabled in this fork so it cannot fetch and overwrite the modified application. Release information is opened only as a website when requested.

When you click a web pin or document bookmark, MacDesk passes the chosen target to the default browser or associated Windows application. That application then has its own network and privacy behavior. Building from source restores dependencies from NuGet, which contacts the configured package service. GitHub and NuGet downloads are outside the application's offline window-management operation.

## Windows settings and exit

Taskbar hiding borrows the visibility of existing taskbar windows and restores it on normal exit, with a separate recovery guard. It does not change the taskbar's registry or appbar auto-hide setting. Theme and desktop-icon detection read Windows settings. Desktop-icon visibility features, where enabled, use the Windows shell's own toggle command; Windows may persist its own shell preference.

The startup preference file does not itself register a Windows startup task or write a registry Run entry. For automatic startup, create a standard shortcut in your own Startup folder if desired, and remove that shortcut to disable it.

On exit, local settings remain so your pins and preferences can be reused. After all MacDesk processes have exited and the taskbar has been restored, deleting the portable folder removes the files stored there. Remove any startup shortcut you created separately. No claim is made that deleting the folder erases operating-system crash records, shell history or data retained by applications that MacDesk opened.

## Publishing screenshots and reports

Use sample applications and sample pages for promotional screenshots. Live thumbnails and window captions can reveal documents, chats and websites. For a bug report, prefer the smallest relevant diagnostic excerpt, omit personal paths and URLs, and never include credentials. Read [THIRD_PARTY_NOTICES.md](../THIRD_PARTY_NOTICES.md) for source provenance and dependency licenses.
