# HeatTray privacy policy

Last updated 1 November 2026.

HeatTray does not collect or send any personal information. There are no accounts, ads or analytics, and it makes no network connections at all.

**What it reads.** HeatTray reads, on your own computer only, the CPU's speed and load from Windows performance counters, the temperature from the ACPI thermal zones Windows exposes, the CPU's name and rated clock speed from the registry (`HKEY_LOCAL_MACHINE\HARDWARE\DESCRIPTION\System\CentralProcessor`), and whether the taskbar is light or dark (`HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize`) so the icon is readable. Nothing it reads is stored or sent anywhere, and no history of readings is kept.

**What it keeps on your computer.** Your Settings (sample interval, temperature limit, thresholds and display choices) are saved in a small `settings.ini` file, next to the exe in the zip version or in the app's private storage in the Microsoft Store version. The Store version's copy is removed when you uninstall. Apart from the Start with Windows entry below, nothing else is saved.

**Start with Windows.** In the zip version, ticking this in Settings adds one entry under `HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Run` (your own account only, no administrator rights), and unticking it removes the entry. The Microsoft Store version does not write to the registry: start at login is switched on in Windows Settings.

**Contact.** Questions about this policy can go to the project's issue tracker: https://github.com/andrewreardon-au/HeatTray/issues

If this policy changes, the new version will be published here with a new date.
