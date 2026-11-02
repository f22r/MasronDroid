# MasronDroid interface

The workspace serves operators managing one or more connected Android devices. The selected device and transport appear above the active page. A change of target clears the previous package selection.

The dark sidebar holds the five pages, version, and language selector. Window controls sit beside the page heading. Maximizing uses the current monitor's work area, including the space reserved for the Windows taskbar. At the minimum size of 790 × 560 DIPs, page content scrolls.

The [logo](src/MasronDroid/Assets/masrondroid.svg) combines a terminal prompt, antennae, and a USB port. The sidebar uses the PNG and Windows uses the multi-resolution ICO. [Theme.xaml](src/MasronDroid/Theme.xaml) defines shared controls. The rail is #152E3E, the action accent is #126782, and the workspace is #F5F8FA. Use Segoe UI for labels and Consolas for technical output.

The portrait mirror occupies the right side while the active management page stays visible. Back, Home, and Recent apps act on the mirrored serial. Keep screen on applies only to that device's powered mirror session. Switching targets ends the old session. A known lock state suspends the preview and prompts the operator to unlock the phone.

Use the shared keyboard-accessible dropdown for language, target, and package state. Confirmations identify the target and consequence and initially focus Cancel. The resizable bottom log retains the latest 200 session entries.
