# MacDesk public preview

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
