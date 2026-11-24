# Security policy

## Reporting a vulnerability

Please **don't open a public issue** for a security problem. Report it privately instead: on the repository's **Security** tab, click **Report a vulnerability**. Only the maintainers see the report.

To help us reproduce the problem, please include:
* the CalcLink version, and whether you used the installer or the portable zip;
* the Windows version and architecture (x64 or ARM64);
* the steps to reproduce, and what an attacker gains.

We'll acknowledge the report, keep you informed, and credit you in the release notes unless you'd rather not be named. CalcLink is maintained by volunteers, so please allow reasonable time for a fix before you disclose the problem publicly.

## Supported versions

Only the latest release gets security fixes.

## What deserves extra care

**`calclink-driver.exe` runs as administrator.** It's the only part of CalcLink that does. It runs only when the user clicks **Set up USB driver** and approves Windows' prompt, and it:
* writes and signs a WinUSB driver package for one TI device;
* adds the signing certificate to the machine's trusted root and trusted publisher stores;
* installs the package on the device.

[docs/DRIVERS.md](docs/DRIVERS.md) §3 describes its safeguards: it accepts only TI hardware IDs, works in an administrator-only folder, deletes the certificate's private key after signing, and loads Windows libraries from System32 only. A way around any of these, for example to install a driver for a non-TI device, plant a file, or reuse the certificate, is a vulnerability. So is anything that runs the helper elevated without the user's click.

**Files from other people.** CalcLink reads calculator files (`.8xp`, `.8xg`, `.tig`, ...) and backups that may come from anyone. A crafted file that crashes the app is a bug. One that corrupts memory, writes outside the folder you chose, or runs code is a vulnerability.

**Not in scope:**
* Programs that run on the calculator itself.
* Problems in tilibs, GLib, libusb or the other libraries, unless CalcLink uses them unsafely. Please report those to their projects; [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) has the links.
