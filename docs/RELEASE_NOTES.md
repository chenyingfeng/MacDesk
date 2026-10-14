# MacDesk public preview

## v0.1.0-preview.3 — Dock 4.8.1 / Stage 0.8.3.26

Released components: Dock 4.8.1 and Stage 0.8.3.26. This preview combines the
following changes. Multi-window coexistence remains the default.

### Window geometry on restore

Ordinary off-screen parking now remembers the full
physical rectangle and owning PID. Repeated stows preserve the first rectangle;
an explicit relocated position retains its saved size. Normal windows recover
their saved size even if the application resized while parked. Maximized window
sizing remains under Windows control. Incoming previews use the same saved bounds.

Restoration leaves already visible normal/maximized windows in their current
show state. A minimized ordinary window preserves Windows' restore-to-maximized
intent. Explicit drag/tiling normalization remains available. Messenger-specific
native restore routes are retained.

Owned-window checks cover resized-while-parked geometry, repeated stows, relocated
positions, maximized/minimized/normal selection and explicit normalization, in
addition to existing QQ/WeChat, sidebar, and Dock checks. These do not establish
universal third-party compatibility or eliminate every source of animation flicker.

### Multi-window selection

Clicking an application with several tracked windows
opens a nonactivating chooser above the Dock instead of cycling blindly. Cards
show live Windows DWM previews when available and window titles. Minimized or
unavailable previews keep a selectable text fallback without restoring the source.
Single-window activation and tray-only recovery keep their existing routes.

Only the current page owns preview resources (at most six). Wheel or arrow buttons
change pages; moving away, closing the chooser, or yielding the Dock releases them.
Selections revalidate HWND, PID, executable identity and the fresh Stage snapshot.
Window contents and titles are not saved by the chooser. No settings are required.

Owned-window fixtures cover pagination, DWM cleanup, hit testing, stale identities,
minimization, source closure and exact selection. A separate desktop fixture checks
real pointer input over DWM pixels; normal user applications need desktop validation.

### Bounded recovery and coordinated reveal

Tray recovery runs in a short-lived, ordinary-privilege
worker connected by a current-user pipe to the exact parent/child PIDs. A Windows
job ends the worker on timeout, cancellation, or parent exit. Uncertain provider
actions are not automatically retried. This bounds provider hangs rather than
claiming every third-party application is compatible.

Sidebar thumbnails and icons share the sidebar fade instead of appearing ahead
of its background. The Dock uses a small eased entrance, and both reveal effects
respect Windows' client-area animation setting. Sidebar pointer geometry stays
fixed throughout the fade; hide and system-navigation yielding remain immediate.

The new fixture exercises real source-owned worker processes, acknowledged hangs,
cancellation, early exit, recovery after failure, and abrupt parent exit. Existing
Dock/Stage checks remain required. Real desktop behavior needs a user-session trial.

## Initial previews

This first public bundle contains Stage 0.8.3.24 and Dock 4.7.7 with a portable
launcher and a generic shortcut center. Personal profile, web defaults, artwork
and the private mailbox integration are excluded.

The public distribution preserves multi-window coexistence, live right sidebar,
Dock pins and hover, explicit taskbar/tray toggle, QQ/WeChat native tray recovery,
and the internal-recovery presentation fix that keeps the Dock visible.

Upstream binary replacement is disabled. Normal launch is ordinary privilege;
administrator launch is an explicit local action. No startup registration is made.
Native LGPL dependencies remain replaceable and their corresponding source is included.

Build validation covers owned-window and policy fixtures. The initial real-user
checks came from one Windows computer, including successful QQ/WeChat opening
and retained Dock after recovery. No claim of universal compatibility is made.

The updated preview uses explicit UTF-8 source compilation and Windows Unicode
Shell Link interfaces for shortcut paths. Chinese shortcut names and targets are
covered by the pin fixture, including round-trip and read-only import checks.
Failed build fixtures print their diagnostics so clean Windows environments can
be investigated without hiding or skipping failed checks.
