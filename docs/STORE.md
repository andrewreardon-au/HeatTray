# Microsoft Store notes

Internal notes. Factual, not marketing. Modelled on PingTray's `docs/STORE.md`.

## Decision

- Same repo, one source file. The app detects an MSIX package at runtime (`GetCurrentPackageFullName`, `IsPackaged()`).
- Packaged build: settings in `%LOCALAPPDATA%\HeatTray\settings.ini` (redirected into the package's private LocalCache, removed on uninstall); Start with Windows is a link to Windows Settings (manifest `startupTask` `HeatTrayStartup`, Enabled=false); `HeatTray.exe` works from a terminal through an app execution alias.
- Manifest: `store/AppxManifest.xml` (template; tokens filled by `build-store.ps1`). Full trust (`runFullTrust`) because it is a Win32/.NET Framework tray exe. Minimum Windows 10 1809 (10.0.17763), x64.
- Package version comes from `Version` in the source, padded to four parts (2.3.1 -> 2.3.1.0). The Store will not take the same version twice.

## Build

1. Reserve the name in Partner Center and copy the values from the Product identity page.
2. Build in a COPY of the repo (the script rebuilds `dist\HeatTray.exe`, and a rebuild changes the exe's bytes and so the README hash):
   `.\build-store.ps1 -IdentityName "<Package/Identity/Name>" -Publisher "<Package/Identity/Publisher>" -PublisherDisplayName "<Package/Properties/PublisherDisplayName>"`
3. Output: `store\out\HeatTray_<version>_x64.msix`, UNSIGNED: that is the file to upload (the Store signs it).
4. Local test: `.\build-store.ps1 -Sideload` (throwaway identity and certificate; needs an ADMIN shell once to trust it, see the script's closing lines). Never upload that one.
5. `store/make-assets.py` regenerates the logos from `src/HeatTray.ico` (Pillow).

## Listing text (paste into Partner Center)

**Short description (max 100 chars):**
Shows how much slower your CPU is running than its rated speed, as a coloured number in the tray.

**Description:**
HeatTray puts one number and one colour in the system tray: how fast your CPU's working cores are running compared with the CPU's rated clock. 1.0 in green is normal. Orange or red means the CPU is slowed while it works, and the tooltip says whether heat or something else (the power mode, the charger) is the likely cause and what to try.

It uses the busy-core speed Windows already measures, so it needs no drivers and no admin rights. It cannot tell a heat limit from a firmware power cap; it tells you that you are slow and which cause is more likely.

- Speed ratio by default (1.0 is the rated clock); Percent, GHz, CPU usage and temperature are also available, alone or in pairs.
- A tooltip in plain words: the speed, whether it is hot, and what to try (plug in, check the power mode, clear the vents).
- Temperature in Celsius or Fahrenheit.
- Readable on light and dark taskbars.
- Start with Windows from Windows Settings.
- It collects and sends nothing, and makes no network connections.

**Features (one per line, max 200 chars each):**
- One number and one colour: the CPU's speed against its rated clock
- Green is normal, orange is slow, red is very slow (limits are adjustable)
- Tooltip says whether heat or something else is the likely cause
- Choose what the icon shows: speed ratio, percent, GHz, usage or temperature
- Celsius or Fahrenheit
- Light and dark taskbar colours
- No account, no network access, no data collected

**Search terms (max 7, 30 chars each):** cpu throttling, cpu speed, thermal throttle, cpu temperature, system tray monitor, laptop overheating, cpu clock

**Release notes ("What's new in this version"), first submission:** Optional; leave blank or: "First release in the Microsoft Store."

## Partner Center fields

| Section | Field | Value |
|---|---|---|
| Pricing and availability | Price / markets / visibility | Free; all markets; public |
| Properties | Category | Utilities & tools (closest sub-category) |
| Properties | Privacy policy URL | https://github.com/andrewreardon-au/HeatTray/blob/main/PRIVACY.md (needs this file pushed to `main`) |
| Properties | Website | https://github.com/andrewreardon-au/HeatTray |
| Properties | Support contact | https://github.com/andrewreardon-au/HeatTray/issues (keeps the personal email out) |
| Properties | System requirements | none beyond the OS (no keyboard/mouse/etc. needed) |
| Age ratings | Questionnaire | All "No": it is a utility with no user content, chat, purchases, location or violence |
| Packages | Upload | `HeatTray_<version>_x64.msix` (unsigned) |
| Store listing | Description, features, short description, search terms | the text above |
| Store listing | Screenshots (1366x768) | `store/listing/screenshot-1-tray.png`, `-1-tray-hot.png`, `-2-menu.png`, `-3-details.png`, `-4-settings.png` (min 1, up to 10; also `-1-tray-orange`, `-1-tray-red`) |
| Store listing | 1:1 box art (2160x2160) | `store/listing/boxart-1x1.png` |
| Store listing | 9:16 poster art (1440x2160) | `store/listing/poster-2x3.png` (the field only accepts 720x1080 or 1440x2160) |
| Store listing | 16:9 super hero art (1920x1080) | `store/listing/hero-green.png` (also `hero-orange`, `hero-red`, `hero-hot`) |
| Store listing | App tile icon (300x300) | `store/listing/StoreListing_300.png` |
| Submission options | Notes for certification | see below |

**Notes for certification:**
HeatTray is a Win32 (.NET Framework 4) system tray app packaged with the desktop bridge, so it declares runFullTrust. It makes no network connections. It reads CPU speed and load from Windows performance counters and the temperature from the ACPI thermal zones, and writes only its own settings.ini under the app's private storage. After launching it, look in the notification area (click the ^ arrow if the icon is hidden); the icon is grey for about 15 seconds while it takes its first readings. Click the icon for Settings, right-click for the menu (Details, About, Exit). On a machine without temperature sensors it still shows the speed.

## Status

- [x] Partner Center: HeatTray name reserved 2 Nov 2026. Store ID 9P77LQB5Q1FV, Identity Name AndrewReardon.HeatTray, Publisher CN=BFD979DA-5768-4CA3-9A76-86E191905F0E (same as PingTray), PFN AndrewReardon.HeatTray_qnhc6s6t58sdy. These are the defaults in `build-store.ps1`.
- [x] Package built with the real identity: `store\out\HeatTray_2.3.1.0_x64.msix` (71,872 bytes, unsigned; identity, version 2.3.1.0 and the exe file version checked by unpacking it).
- [ ] `PRIVACY.md` pushed to `main` (the policy URL must resolve).
- [x] Sideload test of the packaged build, 2 Nov 2026 (installed 2.3.1.0, run, uninstalled): the exe runs from the package folder; left click opens Settings; Start with Windows is a link (not a checkbox); settings.ini goes to the package LocalCache and NOT to the real `%LOCALAPPDATA%\HeatTray`, the install folder or the Run key; `HeatTray.exe -v` and `--diag` work through the alias (speed counters read, 1 thermal zone); the manifest declares the startup task and the alias; a second launch shows the already-running notice; uninstall removes the package, LocalCache and alias. Not checked: the startup task in Windows Settings > Apps > Startup (not found in the registry by name), the Windows App Certification Kit.
- [x] Submitted to Partner Center on 2 Nov 2026 (package HeatTray_2.3.1.0_x64.msix, Store ID 9P77LQB5Q1FV). Certification result: pending. After it is live: the Store link goes in the README (the author's wording, as for PingTray), and v2.3.1+ updates need a version above 2.3.1.0.
