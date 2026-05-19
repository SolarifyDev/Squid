# Windows Tentacle Install — Operator Guide

> **Audience**: operators installing the Squid Tentacle on a Windows host.
> **Status**: applies to Squid 1.7.0+ (the install-info.json discovery file
> + UAC auto-elevation surfaces). For 1.6.x and earlier, see the section
> "Legacy 1.6.x install" at the bottom.

---

## 1 — Quick start (the canonical workflow)

In the Squid Web UI:

1. Go to **Infrastructure → Deployment Targets → Add Deployment Target → Windows Tentacle**.
2. Pick **Listening** (server dials into your host) or **Polling** (your host dials out to Squid).
3. Copy the generated PowerShell snippet.
4. On your Windows host, open **PowerShell** (does NOT need to be elevated — the script auto-elevates).
5. Paste + press Enter.

The script does four things:

```
Step 1 — Download + extract Tentacle (auto-elevates via UAC if needed)
Step 2 — Locate the binary via %ProgramData%\Squid\Tentacle\install-info.json
Step 3 — Register with the Squid server
Step 4 — Install + start the Windows service
```

If everything works, your Tentacle is registered and visible in the Squid Web UI within ~30 seconds.

---

## 2 — UAC + elevation behaviour

The install script must write to `C:\Program Files\Squid Tentacle` (default install dir) which requires Administrator privileges. The script handles this automatically:

| Scenario | What happens |
|---|---|
| You're already running PowerShell as Administrator | Script proceeds in-process — no UAC prompt. |
| You're running a normal PowerShell with default install dir | Script triggers a UAC prompt ("Do you want to allow this app to make changes…?"). Click Yes. A new elevated PowerShell window opens, runs the install, exits when done. Your original window stays open. |
| You're running a normal PowerShell with a user-owned install dir (e.g. `-InstallDir "$env:USERPROFILE\squid"`) | No UAC prompt — user-owned paths don't need admin. |
| You're running via `irm <url> \| iex` (in-memory pipe) as non-admin | Script writes itself to `%TEMP%\squid-install-{guid}.ps1` first, then UAC-re-launches that file. |
| UAC is fully disabled (`EnableLUA=0`) | Script runs as admin token directly — no prompt. |

### Opting out of auto-elevation

Some scenarios can't / shouldn't trigger UAC:
- **CI environments** (no interactive desktop)
- **WinRM remote sessions** (UAC requires interactive desktop)
- **Scheduled tasks running as SYSTEM** (already system-level)

Pass `-NoAutoElevate` to the script. If admin is genuinely required, the script will print a clear error pointing you at one of:

```powershell
# Option 1 — run from an already-elevated PowerShell
powershell -NoProfile -ExecutionPolicy Bypass -File install-tentacle.ps1 -NoAutoElevate

# Option 2 — install to a user-owned path (no admin needed)
.\install-tentacle.ps1 -InstallDir "$env:USERPROFILE\squid-tentacle"
```

---

## 3 — Install paths + custom locations

Default install dir is `C:\Program Files\Squid Tentacle`. Override via:

```powershell
# Command-line parameter
.\install-tentacle.ps1 -InstallDir "D:\squid-tentacle"

# Environment variable (useful for MDM / Intune deployments)
$env:INSTALL_DIR = "D:\squid-tentacle"
.\install-tentacle.ps1
```

After installation, the **discovery file** at `%ProgramData%\Squid\Tentacle\install-info.json` records exactly where the binary went:

```json
{
  "Schema": 1,
  "BinaryPath": "D:\\squid-tentacle\\Squid.Tentacle.exe",
  "InstallDir": "D:\\squid-tentacle",
  "Version": "1.7.0",
  "Architecture": "win-x64",
  "InstalledAt": "2026-05-19T05:14:00Z",
  "InstalledBy": "DOMAIN\\admin",
  "ServiceName": "squid-tentacle"
}
```

Server-generated register / service-install snippets **read this file** to locate the binary — so a custom install dir doesn't break the copy-paste experience.

### Paths with spaces

Fully supported. Operators commonly use paths like `D:\My Apps\Squid Tentacle`. The script + discovery file roundtrip the spaced path correctly; downstream snippets quote it.

### Non-default drives

`D:\squid`, `E:\path\to\install`, etc. all work. The only constraint: the drive must be writable by the install identity (admin for `C:\Program Files\…`, current user for user-owned paths).

---

## 4 — Troubleshooting

### "Permission denied — 'MachineCreate' permission required" on Step 3

The API key user lacks the `MachineCreate` permission. **The fix is operator-side, not script-side.** In the Squid Web UI:

1. **Either** assign the API key user one of these built-in roles:
   - **Environment Manager** (manages machines + accounts + environments)
   - **Space Owner** (full space access)
2. **Or** add the `MachineCreate` permission to their existing custom role.
3. **Or** issue a new API key from a user with one of the roles above.

> **Note**: `System Administrator` does NOT grant `MachineCreate` — it's a system-level role for managing spaces / users / teams, not space-scoped resources like machines. The install-script hint deliberately omits it.

### "install-info.json not found at …" on Step 2

Step 1 (the install-tentacle.ps1 download + extract) didn't complete. Check the output above Step 2 for the actual failure:

- Network failure downloading from GitHub (firewall / proxy)
- Disk full
- Antivirus blocked the binary mid-extract (Defender SmartScreen / 3rd-party AV)

### "Binary '…' (recorded in install-info.json) does not exist" on Step 2

Something deleted the binary after Step 1 finished. Common causes:

- Antivirus quarantined `Squid.Tentacle.exe` after extract
- Manual cleanup of the install dir
- Failed prior upgrade left partial state

Just re-run Step 1 — the script is idempotent.

### "Service install failed (exit X)" on Step 4

Service installation hit the Windows Service Control Manager. Check:

- `sc query squid-tentacle` — does the service exist already?
- If exists: `sc stop squid-tentacle` + `sc delete squid-tentacle`, then re-run Step 4
- Check the SCM diagnostic log at `%ProgramData%\Squid\Tentacle\scm-diagnostic.log`

### Auto-elevation hangs / UAC prompt doesn't appear

You're likely in a non-interactive context (WinRM remote, scheduled task, SSH). UAC requires an interactive desktop. Fixes:

1. Pass `-NoAutoElevate` — the script will error out cleanly with remediation steps.
2. Use `psexec -s` to run as SYSTEM (already elevated, no prompt).
3. Use CredSSP if running over WinRM (`Enable-WSManCredSSP`).
4. Connect via RDP and run interactively.

### "Unexpected token" / parser error in the script

You're running an older script with non-ASCII characters that PowerShell 5.1 can't decode under your locale's OEM codepage. Update to the latest `install-tentacle.ps1` (1.7.0+) — em-dashes were replaced with ASCII `--` in 1.7.0 specifically to fix this.

---

## 4b — Download mode (manual archive download)

Some operators prefer to download the Tentacle zip themselves and skip the auto-download in Step 1. The Squid Web UI's "Or download the installer manually" tab gives direct URLs from GitHub Releases.

Workflow:

1. **Download** `squid-tentacle-{version}-win-x64.zip` (or `win-arm64.zip`) from the wizard's URL.
2. **Extract** to `C:\Program Files\Squid Tentacle\` (the canonical default — Step 2's smart discovery finds it here automatically).
3. **Run Steps 3 + 4** from the wizard's script. Step 2's discovery handles three scenarios:

   | Scenario | Step 2 result |
   |---|---|
   | Paste mode (you DID run Step 1) → `install-info.json` exists | Uses `BinaryPath` field from the JSON |
   | Download mode (you skipped Step 1) → binary at default location | Falls back to `%ProgramFiles%\Squid Tentacle\Squid.Tentacle.exe` |
   | Custom dir on PATH | Uses `Get-Command Squid.Tentacle.exe` lookup |

If you extracted to a non-standard path (e.g. `D:\squid\`) AND skipped Step 1, manually set `$tentacle` before pasting:

```powershell
$tentacle = 'D:\squid\Squid.Tentacle.exe'
# ... then paste Steps 3 + 4 from the wizard ...
```

The smart-discovery error message names all three lookup paths it tried — never guessing.

---

## 5 — Air-gapped installs

For hosts with no internet access:

1. Download `squid-tentacle-{version}-win-x64.zip` (or `win-arm64.zip`) from GitHub Releases on a machine WITH internet.
2. Stand up a private HTTP server hosting the file at e.g. `https://internal-mirror.corp.example.com/squid/releases/...`.
3. Pass `-DownloadBase https://internal-mirror.corp.example.com/squid/releases` (or set `$env:DOWNLOAD_BASE`).

The script's URL resolution mirrors the GitHub Release layout:

```
{DownloadBase}/latest/download/squid-tentacle-{rid}.zip
{DownloadBase}/download/{version}/squid-tentacle-{version}-{rid}.zip
{DownloadBase}/download/v{version}/squid-tentacle-{version}-{rid}.zip  ← fallback
```

---

## 6 — Architecture support

- **x64 (AMD64)** — fully supported.
- **ARM64** — fully supported. The script auto-detects via `$env:PROCESSOR_ARCHITECTURE` and picks the right zip (`win-arm64`).
- **x86 (32-bit)** — NOT supported. The script refuses with a clear error.

---

## 7 — Reinstalling / upgrading

The install script is idempotent. Re-running it on a host with an existing install:

- Stops the running service (if `-NoServiceInstall` was NOT passed).
- Downloads + extracts the new version (overwriting existing files via `Expand-Archive -Force`).
- Updates `install-info.json` with the new version + timestamp.
- Re-installs the service (idempotent — updates existing service config).
- Restarts the service.

For controlled upgrades (where you want to manage the upgrade flow yourself), use the Squid Web UI's **Upgrade Tentacle** action — it goes through the staged upgrade pipeline with rollback support.

---

## 8 — Removing the Tentacle

```powershell
# Stop + delete the service
& "C:\Program Files\Squid Tentacle\Squid.Tentacle.exe" service uninstall

# OR via sc.exe directly
sc.exe stop squid-tentacle
sc.exe delete squid-tentacle

# Remove the install dir
Remove-Item -Path "C:\Program Files\Squid Tentacle" -Recurse -Force

# Remove the discovery file + config
Remove-Item -Path "C:\ProgramData\Squid\Tentacle" -Recurse -Force

# Remove the firewall rule (Listening Tentacle only)
Remove-NetFirewallRule -DisplayName "Squid Tentacle (Listening)"
```

In the Squid Web UI, also delete the corresponding deployment target.

---

## 9 — Reference

| Item | Value |
|---|---|
| Default install dir | `C:\Program Files\Squid Tentacle` |
| Binary name | `Squid.Tentacle.exe` (NOT `squid-tentacle.exe` — that's the Linux shell wrapper) |
| Default service name | `squid-tentacle` |
| Discovery file | `%ProgramData%\Squid\Tentacle\install-info.json` |
| Listening port (default) | 10933 |
| Polling target (Squid server's polling listener) | typically 10943 |
| Install log | `%ProgramData%\Squid\Tentacle\scm-diagnostic.log` |
| Env-var overrides | `TENTACLE_VERSION`, `INSTALL_DIR`, `DOWNLOAD_BASE` |
| Script parameters | `-Version`, `-InstallDir`, `-DownloadBase`, `-ServiceName`, `-NoServiceInstall`, `-NoAutoElevate` |

---

## 10 — Related docs

- [API Key Permissions](api-key-permissions.md) — which permissions an API key needs for various operations, including the `MachineCreate` permission required for register
- [E2E Scenario Matrix](e2e-scenario-matrix.md) — the test-coverage ledger Phase 12.M tracks Windows install scenarios
- `deploy/scripts/install-tentacle.ps1` — the install script itself
- `src/Squid.Core/Services/Machines/Scripts/Tentacle/WindowsPowerShellScriptBuilder.cs` — server-side script generator

---

## Legacy 1.6.x install

Before 1.7.0, the Squid-generated snippet hardcoded `'C:\Program Files\Squid Tentacle\squid-tentacle.exe'` (wrong binary name; default path only). If you're on 1.6.x and see "file not found" at Step 2 / 3:

1. Manually rename the executable: the actual published binary is `Squid.Tentacle.exe`, not `squid-tentacle.exe`. The legacy snippet's literal won't find it.
2. Manually replace `'C:\Program Files\Squid Tentacle\…'` references in the copy-pasted snippet with your actual install dir.
3. Upgrade to Squid 1.7.0+ — the discovery-file mechanism removes both of these hand-edit steps.
