# EQ Mob Overlay V4

Adds three EQ attachment methods:

1. Auto Detect
2. Select a top-level window (including untitled windows)
3. Enter the EQ client PID manually

For manual PID: open Windows Task Manager, choose **Details**, find the actual EverQuest client process, note its PID, enter that number in the overlay control window, then click **Attach to PID**.

If attachment reports Access Denied, run EQMobOverlay as Administrator.

# EQ Mob Overlay

Standalone Windows x86 overlay for Project Quarm / the classic EQ client.

## What this build adds

- A normal **taskbar control window** so the program never appears to be doing nothing.
- A **system tray icon**. Double-click it to reopen the controls; right-click for Auto Detect, overlay toggle, and Exit.
- Automatic detection of a running `eqgame.exe`.
- Manual selection of a specific running EQ client by **window title + PID** when more than one client is running or auto-detection chooses the wrong one.
- Live status showing whether the app attached to EQ, whether a target can be read, and whether the NPC database is ready.
- A useful access-denied message if EQ is elevated and the overlay needs to be run as Administrator too.
- The actual mob panel remains constrained to the selected EQ client area and only appears while that EQ window is active.

## Mob panel

When a readable target is selected it shows:

- target name
- target level and Spawn ID
- live distance
- live HP percentage
- estimated remaining HP / database max HP
- MR, CR, DR, FR and PR badges

The NPC database is cached locally from the public Quarm database release used by the prototype.

## First run

1. Start EverQuest.
2. Start `EQMobOverlay.exe`.
3. The **EQ Mob Overlay** control window should appear in the taskbar and a tray icon should appear near the clock.
4. Leave **Auto Detect** enabled, or choose a running EQ entry from the list and click **Use selected EQ**.
5. Look at **Connection**. It should say that it attached to `eqgame.exe` and show the PID.
6. Target a mob in EQ. The **Target** status should change from `No target selected` to the mob name.
7. Switch back to EQ; the mob panel should appear inside the EQ client area.

If Connection says **Access denied**, run the overlay at the same privilege level as EverQuest (for example, Run as administrator if EQ itself is elevated).

## Build

The included GitHub Actions workflow builds a self-contained Windows x86 executable and uploads it as the `EQMobOverlay-win-x86` artifact.

## If auto-detect cannot find EverQuest

The control window now lists **all visible application windows**, not only processes named `eqgame.exe`.
Choose the actual EverQuest/Project Quarm window from the list and click **Use selected window**.
The entry shows the window title, process name and PID. This also works if a launcher or wrapper gives the client a different process name.

If the correct EQ window appears but attachment says **Access denied**, run `EQMobOverlay.exe` as Administrator and try again.
