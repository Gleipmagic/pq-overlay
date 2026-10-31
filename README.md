# EQ Mob Overlay V5

Standalone Project Quarm / EverQuest target-information overlay.

## V5 changes

- Fixes long NPC names: reads 64 characters instead of 30, so names such as `a young obsidian tree spiderling` can match the NPC database.
- Adds a tolerant prefix fallback for database matching.
- Compact EQ-style panel.
- HP text is inside the health bar as `26,900   100%`.
- Uses EQ-style resist icons with the resist numbers centered inside the icons.
- Keeps the overlay visible when EQ is tabbed out by default (it still hides if EQ is minimized).
- Adds Lock / Unlock control.
  - Locked: panel is click-through.
  - Unlocked: drag anywhere on the panel and resize using the gold bottom-right handle.
  - Position and size are saved.
- Manual PID attachment and broad window selection remain available.

## Controls

Open the taskbar control window or double-click the tray icon.

- **Show mob overlay** — enable/disable the panel.
- **Keep overlay visible when EQ is not the foreground window** — leave enabled to see it while tabbed out.
- **Lock overlay position and size** — uncheck to move/resize, then re-check when finished.

## Build

The included GitHub Actions workflow publishes a self-contained Windows x86 executable.


## V5.1 display change
- Removed mob level and Spawn ID from the overlay.
- Header now shows only target name and distance.
- Reduced overlay height for a smaller profile.


## V6 compact UI changes
- Resize down to a resist-only strip; header/HP auto-hide at very small height.
- 23x23 native EQ resist icons with centered values.
- Larger bold target/distance/HP text.
- Target name uses con color based on player and target levels.
- Adjustable normal and hovered opacity (0-100%).
- Overlay remains visible when EQ is not foreground when that option is enabled.
