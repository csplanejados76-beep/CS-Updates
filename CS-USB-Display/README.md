# CS USB Display v0.1.0

Use an Android tablet as a low-latency USB screen for Windows.

## Current mode
- USB-only transport using Android Debug Bridge (ADB reverse tunnel)
- Mirrors the Windows primary display to the tablet
- Fullscreen Android viewer
- Windows host bundles ADB platform-tools in the release package

## Requirements
1. Windows 10/11 x64
2. Android tablet with Android 8.0+ (API 26+)
3. Developer options + USB debugging enabled on the tablet
4. USB data cable

## Usage
1. Install \`CS-USB-Display.apk\` on the tablet.
2. Extract \`CS-USB-Display-Windows.zip\` on Windows.
3. Connect the tablet by USB and approve the USB debugging prompt.
4. Run \`CSUsbDisplayHost.exe\`.
5. Click **Connect / Start**. The host configures \`adb reverse tcp:27183 tcp:27183\` and launches the Android app.
6. The tablet connects to \`127.0.0.1:27183\` through the USB tunnel and starts showing the Windows screen.

## Notes
This v0.1 mirrors the primary display. A true Windows extended-desktop mode requires a virtual display/indirect display driver and is intentionally separated into a later version.
