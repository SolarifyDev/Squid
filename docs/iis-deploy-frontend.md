# Frontend Integration Guide — Deploy to IIS WebSite

> **Audience**: SquidWeb frontend engineers building the `Squid.DeployToIISWebSite`
> step UI.
> **Reference**: Octopus's "Deploy to IIS" step UI — Squid's UI should be
> 1:1 visually + structurally so operators carrying Octopus IIS step specs
> across to Squid see exactly what they expect.
> **Backend status**: shipped end-to-end through 1.6.9. Server, agent, drift
> detector, real-host E2E all green.
> **Status of this doc**: living spec. Update when the property surface in
> `src/Squid.Core/Services/DeploymentExecution/Constants/IISDeployProperties.cs`
> changes.

---

## 1 — Quick reference

| What you want to do | Where |
|---|---|
| Action type identifier (goes into `DeploymentActionDto.ActionType`) | `Squid.DeployToIISWebSite` |
| Property-name authority (every constant in this doc maps to a `public const string` in core) | `src/Squid.Core/Services/DeploymentExecution/Constants/IISDeployProperties.cs` |
| Behavioural contract (what the agent actually does with each property) | `src/Squid.Core/Resources/Deploy/IIS/DeployToIISWebSite.ps1` |
| Octopus parity reference | `Calamari/source/Calamari/Scripts/Octopus.Features.IISWebSite_BeforePostDeploy.ps1` + `IisWebSiteBeforeDeployFeature.cs` + `IisWebSiteAfterPostDeployFeature.cs` |

**Submission shape** — when the operator clicks Save, post the assembled action to
the standard deployment-process update endpoint. The shape of one IIS action:

```jsonc
{
  "id": <action-id>,
  "name": "Deploy OrderApi to IIS",
  "actionType": "Squid.DeployToIISWebSite",
  "properties": [
    { "propertyName": "Squid.Action.IISWebSite.CreateOrUpdateWebSite", "propertyValue": "True" },
    { "propertyName": "Squid.Action.IISWebSite.WebSiteName",            "propertyValue": "OrderApi" },
    { "propertyName": "Squid.Action.IISWebSite.WebRoot",                "propertyValue": "C:\\inetpub\\OrderApi" },
    { "propertyName": "Squid.Action.IISWebSite.ApplicationPoolName",    "propertyValue": "OrderApiPool" },
    { "propertyName": "Squid.Action.IISWebSite.Bindings",
      "propertyValue": "[{\"protocol\":\"https\",\"port\":\"443\",\"host\":\"orders.example.com\",\"ipAddress\":\"*\",\"enabled\":true,\"requireSni\":true,\"certificateVariable\":\"OrderApiCert\"}]"
    }
    // … rest of properties …
  ]
}
```

> **All property values are `string`.** Booleans submit as `"True"` / `"False"`
> (case-sensitive — agent does `eq 'True'` check). Numbers submit as decimal
> string. Complex objects (Bindings) submit as JSON-encoded strings. Empty / unset
> properties may be omitted entirely.

---

## 2 — UI layout (mirrors Octopus's "Deploy to IIS" step)

```
┌──────────────────────────────────────────────────────────────────────────────┐
│  Step name + Step description (standard step header)                         │
├──────────────────────────────────────────────────────────────────────────────┤
│                                                                              │
│  ▸  Deployment Type                          [Card 1 — see §3]              │
│     ☐ Create or Update Website                                               │
│     ☐ Create or Update Web Application                                       │
│     ☐ Create or Update Virtual Directory                                     │
│                                                                              │
│  ▸  Web Site                                 [Card 2 — visible if §3.1 ON]  │
│     • Name, Physical path (WebRoot), Bindings list, Authentication           │
│                                                                              │
│  ▸  Application Pool                         [Card 3 — visible if §3.1 ON]  │
│     • Pool name, Identity type, Framework version, Start flag                │
│                                                                              │
│  ▸  Web Application                          [Card 4 — visible if §3.2 ON]  │
│  ▸  Virtual Directory                        [Card 5 — visible if §3.3 ON]  │
│                                                                              │
│  ▸  Package Extraction                       [Card 6 — see §4]              │
│     • Source path, Extract to, Purge before extract                          │
│     • SkipIfAlreadyInstalled checkbox (Octopus parity)                       │
│                                                                              │
│  ▸  Certificate (HTTPS auto-import)          [Card 7 — see §5]              │
│     • PFX variable, Password variable, Thumbprint variable name              │
│                                                                              │
│  ▸  Custom Deployment Scripts                [Card 8 — see §6]              │
│     • Inline PreDeploy / PostDeploy textareas                                │
│     • Toggle: "package contains PreDeploy.ps1 / PostDeploy.ps1" — info only  │
│                                                                              │
│  ▸  Configuration Variables (.config)        [Card 9 — see §7]              │
│  ▸  Configuration Transforms (XDT)           [Card 10 — see §7]             │
│  ▸  Substitute Variables in Files            [Card 11 — see §7]             │
│  ▸  Structured Configuration Variables       [Card 12 — see §7]             │
│  ▸  Additional Paths                         [Card 13 — see §7]             │
│                                                                              │
│  ▸  Advanced (collapsed by default)          [Card 14 — see §8]              │
│     • IIS metabase mutex retry knobs                                         │
│     • 3 error-tolerance toggles                                              │
│                                                                              │
│  [ Roles / Channels / Environments ]         (standard step controls)        │
└──────────────────────────────────────────────────────────────────────────────┘
```

Card visibility / disable rules are documented per-card below.

---

## 3 — Deployment Type (Card 1)

Three independent checkboxes. **At least one MUST be checked** (the agent's PS1
exits with code 0 immediately if all three are off — so the UI's Save button
should be disabled in that state to give operators inline feedback).

| Property | Type | UI | Required |
|---|---|---|---|
| `Squid.Action.IISWebSite.CreateOrUpdateWebSite` | `bool` | Checkbox "Create or Update Website" | one-of-three |
| `Squid.Action.IISWebSite.WebApplication.CreateOrUpdate` | `bool` | Checkbox "Create or Update Web Application" | one-of-three |
| `Squid.Action.IISWebSite.VirtualDirectory.CreateOrUpdate` | `bool` | Checkbox "Create or Update Virtual Directory" | one-of-three |

**Conditional rendering**:
- Card 2 + Card 3 visible iff `CreateOrUpdateWebSite=True`
- Card 4 visible iff `WebApplication.CreateOrUpdate=True`
- Card 5 visible iff `VirtualDirectory.CreateOrUpdate=True`

---

## 4 — Web Site card (Card 2 — visible iff `CreateOrUpdateWebSite=True`)

| Property | Type | Default | UI | Required |
|---|---|---|---|---|
| `Squid.Action.IISWebSite.WebSiteName` | string | — | Text input "Website name" | ✅ |
| `Squid.Action.IISWebSite.WebRoot` | path | — | Text input "Physical path" (placeholder `C:\inetpub\OrderApi`) | ✅ |
| `Squid.Action.IISWebSite.Bindings` | JSON array (see §4.1) | `[]` | Repeater list of binding rows | ✅ (at least one) |
| `Squid.Action.IISWebSite.ExistingBindings` | enum (`Merge` / `Replace`) | `Replace` | Radio "Existing bindings" | optional |
| `Squid.Action.IISWebSite.EnableAnonymousAuthentication` | bool | `False` | Checkbox "Anonymous authentication" | optional |
| `Squid.Action.IISWebSite.EnableBasicAuthentication` | bool | `False` | Checkbox "Basic authentication" | optional |
| `Squid.Action.IISWebSite.EnableWindowsAuthentication` | bool | `False` | Checkbox "Windows authentication" | optional |
| `Squid.Action.IISWebSite.StartWebSite` | bool | `True` | Checkbox "Start website" | optional |

### 4.1 — Bindings JSON shape

The `Bindings` property value is a **JSON-encoded string** containing an array of
binding objects. The frontend builds the array via a repeater UI, then `JSON.stringify`s
it as the property value at save time.

```jsonc
[
  {
    "protocol":    "http"  | "https" | "net.tcp" | "net.pipe" | "net.msmq" | "msmq.formatname",
    "port":        "80",            // string-encoded int 1-65535. Required for http/https.
    "host":        "orders.example.com",  // optional; empty string = all hostnames
    "ipAddress":   "*",             // optional; "*" or specific IPv4/IPv6
    "enabled":     true,            // optional; defaults true; disabled bindings registered but stopped
    "thumbprint":  "ABCDEF…",       // https only — direct thumbprint (mutually exclusive with certificateVariable)
    "certificateVariable": "OrderApiCert",  // https only — references a Squid cert variable; the agent looks up `<name>.Thumbprint` at deploy time
    "requireSni":  true,            // https only — SNI hostname-based binding
    "sslFlags":    "0"              // https only — netsh sslFlags bitmask; "0" / "1" / "2" / "3"; default "0"
  }
]
```

**Validation hints for the UI**:
- Port required for `http` / `https`
- For `https`: exactly one of `thumbprint` OR `certificateVariable` must be set
- `requireSni=true` + non-empty `host` is the standard HTTPS-with-SNI form
- `host=""` + `requireSni=false` is the standard catch-all HTTPS binding
- Frontend should render two separate sub-forms: HTTP (port + host + ip + enabled) vs HTTPS (adds cert + SNI fields)

### 4.2 — Variable-references everywhere

Every TEXT INPUT field supports Squid variable references via `#{VariableName}`
syntax. The agent expands these before applying. Render an "insert variable"
helper button next to every text input that drops `#{}` in place.

Optionally — filter form `#{X | ToUpper}` for the 7 Octostache filters (see §11).

---

## 5 — Application Pool card (Card 3 — visible iff `CreateOrUpdateWebSite=True`)

| Property | Type | Default | UI | Required |
|---|---|---|---|---|
| `Squid.Action.IISWebSite.ApplicationPoolName` | string | — | Text input "App pool name" | ✅ |
| `Squid.Action.IISWebSite.ApplicationPoolIdentityType` | enum | `ApplicationPoolIdentity` | Select | optional |
| `Squid.Action.IISWebSite.ApplicationPoolUsername` | string | — | Text input "Username" (visible iff IdentityType=`SpecificUser`) | conditional |
| `Squid.Action.IISWebSite.ApplicationPoolPassword` | string (sensitive) | — | Password input (visible iff IdentityType=`SpecificUser`) | conditional |
| `Squid.Action.IISWebSite.ApplicationPoolFrameworkVersion` | enum | `v4.0` | Select | optional |
| `Squid.Action.IISWebSite.StartApplicationPool` | bool | `True` | Checkbox "Start app pool" | optional |

**`ApplicationPoolIdentityType` values** (operator-facing labels in parentheses):
- `ApplicationPoolIdentity` (Application Pool Identity — default, recommended)
- `LocalService` (Local Service)
- `LocalSystem` (Local System)
- `NetworkService` (Network Service)
- `SpecificUser` (Custom user — shows Username + Password fields)

**`ApplicationPoolFrameworkVersion` values**:
- `v4.0` (.NET CLR v4.0 — default)
- `v2.0` (.NET CLR v2.0)
- `No Managed Code` (No Managed Code — for ASP.NET Core / FastCGI hosts)

**Sensitivity**: `ApplicationPoolPassword` MUST be marked as a sensitive Squid
variable when operators reference it. The frontend's password input should:
1. Refuse plain-text values longer than ~20 chars (force operator to bind to a
   variable like `#{OrderApiPoolPassword}`)
2. Show a hint "Use a sensitive Squid variable: `#{MyPassword}`"

---

## 6 — Web Application card (Card 4 — visible iff `WebApplication.CreateOrUpdate=True`)

| Property | Type | Default | UI | Required |
|---|---|---|---|---|
| `Squid.Action.IISWebSite.WebApplication.WebSiteName` | string | — | Text input "Parent website" | ✅ |
| `Squid.Action.IISWebSite.WebApplication.VirtualPath` | string | — | Text input "Virtual path" (e.g. `/api`) | ✅ |
| `Squid.Action.IISWebSite.WebApplication.PhysicalPath` | path | — | Text input "Physical path" | ✅ |
| `Squid.Action.IISWebSite.WebApplication.ApplicationPoolName` | string | — | Text input "App pool" | ✅ |
| `Squid.Action.IISWebSite.WebApplication.ApplicationPoolIdentityType` | enum | `ApplicationPoolIdentity` | Select (same options as §5) | optional |
| `Squid.Action.IISWebSite.WebApplication.ApplicationPoolUsername` | string | — | conditional on IdentityType=SpecificUser | conditional |
| `Squid.Action.IISWebSite.WebApplication.ApplicationPoolPassword` | sensitive | — | conditional on IdentityType=SpecificUser | conditional |
| `Squid.Action.IISWebSite.WebApplication.ApplicationPoolFrameworkVersion` | enum | `v4.0` | Select (same options as §5) | optional |

---

## 7 — Virtual Directory card (Card 5 — visible iff `VirtualDirectory.CreateOrUpdate=True`)

| Property | Type | Default | UI | Required |
|---|---|---|---|---|
| `Squid.Action.IISWebSite.VirtualDirectory.WebSiteName` | string | — | Text input "Parent website" | ✅ |
| `Squid.Action.IISWebSite.VirtualDirectory.VirtualPath` | string | — | Text input "Virtual path" | ✅ |
| `Squid.Action.IISWebSite.VirtualDirectory.PhysicalPath` | path | — | Text input "Physical path" | ✅ |

---

## 8 — Package Extraction card (Card 6)

The operator pre-stages a `.zip` / `.nupkg` on the Tentacle agent (via a prior
`Squid.Script` step, fileserver mount, or pre-baked artifact location) and
points this card at it.

| Property | Type | Default | UI | Required |
|---|---|---|---|---|
| `Squid.Action.IISWebSite.Package.SourcePath` | path | — | Text input "Package path on agent" | optional |
| `Squid.Action.IISWebSite.Package.ExtractTo` | path | `WebRoot` | Text input "Extract to (defaults to WebRoot)" | optional |
| `Squid.Action.IISWebSite.Package.PurgeBeforeExtract` | bool | `False` | Checkbox "Purge target before extract" | optional |
| `Squid.Action.IISWebSite.Package.SkipIfAlreadyInstalled` | bool | `False` | Checkbox "Skip if already installed" | optional |

**SkipIfAlreadyInstalled** maintains a per-site deployment journal at
`%PROGRAMDATA%\Squid\IISDeploy\journal\<siteName>.json`. When enabled, a re-run
with the same package fingerprint short-circuits the entire deploy. Frontend
should show a tooltip explaining this idempotence guarantee.

---

## 9 — Certificate auto-import card (Card 7)

Operator-friendly HTTPS deploys. Operator pastes a base64-encoded PFX into a
**sensitive** Squid variable; this card references that variable. At deploy time
the agent imports the PFX into `LocalMachine\My` (chain certs go to `\CA` and
`\Root`), exposes the thumbprint via `$SquidVariables["<VarName>.Thumbprint"]`,
then HTTPS bindings can reference it via `"certificateVariable": "<VarName>"`.

| Property | Type | Default | UI | Required |
|---|---|---|---|---|
| `Squid.Action.IISWebSite.Certificate.PfxBase64` | string (sensitive) | — | Variable picker "PFX variable" | optional |
| `Squid.Action.IISWebSite.Certificate.PfxPassword` | string (sensitive) | — | Variable picker "PFX password variable" (visible iff PfxBase64 set) | optional |
| `Squid.Action.IISWebSite.Certificate.ThumbprintVariableName` | string | — | Text input "Thumbprint variable name (e.g. OrderApiCert)" | conditional |

**Workflow tooltip**:
1. Operator creates a sensitive variable `MyPfxBase64` = base64 of their PFX file
2. (If password-protected) operator creates `MyPfxPassword` = password
3. Operator sets ThumbprintVariableName to `OrderApiCert`
4. Operator's HTTPS binding uses `"certificateVariable": "OrderApiCert"`
5. At deploy time, the agent: imports PFX → exposes `OrderApiCert.Thumbprint` →
   netsh-binds the cert to the binding's port + host
6. AppPool private-key ACL grant happens automatically (Octopus parity)

---

## 10 — Custom Deployment Scripts card (Card 8)

Mirrors Octopus's "Custom Deployment Scripts" feature plus Squid's
packaged-script discovery (1.6.9 P1-3 — Octopus `PackagedScriptBehaviour` parity).

| Property | Type | Default | UI |
|---|---|---|---|
| `Squid.Action.CustomScripts.PreDeploy.ps1` | string (multiline PowerShell) | — | Code editor "PreDeploy script" |
| `Squid.Action.CustomScripts.PostDeploy.ps1` | string (multiline PowerShell) | — | Code editor "PostDeploy script" |

**Packaged scripts** (no property to wire — automatic):

The agent automatically detects `PreDeploy.ps1` / `PostDeploy.ps1` files at the
package root after extraction. Render an info banner:

> 💡 If your package contains `PreDeploy.ps1` / `PostDeploy.ps1` at its root,
> they will run automatically after the inline scripts above.

**Execution order** (pinned by drift detector):
1. Inline PreDeploy.ps1
2. Packaged PreDeploy.ps1
3. Rewriters (Substitute → Transforms → ConfigVars → StructuredJsonVars)
4. IIS configure (sites, pools, bindings, auth, cert ACL)
5. Inline PostDeploy.ps1
6. Packaged PostDeploy.ps1
7. Journal entry (Success)

---

## 11 — Variable-substitution feature cards (Cards 9-13)

Five separate operator-facing feature cards, each independently togglable.
Each is a single enable checkbox + (sometimes) a target-files multiline input.

### 11.1 — Configuration Variables (.NET XML config rewrite)

Replaces `<appSettings><add key=...>` / `<connectionStrings><add name=...>` /
`<applicationSettings><setting name=...>` entries in every `*.config` file under
WebRoot. Match key is the Squid variable name (case-sensitive).

| Property | UI |
|---|---|
| `Squid.Action.IISWebSite.ConfigurationVariables.Enabled` | Checkbox "Replace entries in .config files" |

### 11.2 — Configuration Transforms (XDT)

Applies `*.Release.config` and `*.{Environment}.config` XDT overlays.

| Property | UI |
|---|---|
| `Squid.Action.IISWebSite.ConfigurationTransforms.Enabled` | Checkbox "Run XDT transformations" |
| `Squid.Action.IISWebSite.ConfigurationTransforms.EnvironmentName` | Text input "Environment name" (visible iff Enabled; typical value `#{Squid.Environment.Name}`) |
| `Squid.Action.IISWebSite.ConfigurationTransforms.AdditionalTransforms` | Textarea "Additional transforms (CSV `source => target`)" (visible iff Enabled) |

### 11.3 — Substitute Variables in Files (`#{X}` token replacement)

Replaces every `#{X}` token in operator-specified files with the matching Squid
variable. Works on ANY text file (JSON, YAML, properties, .config, .txt).

| Property | UI |
|---|---|
| `Squid.Action.IISWebSite.SubstituteInFiles.Enabled` | Checkbox "Substitute variables in files" |
| `Squid.Action.IISWebSite.SubstituteInFiles.TargetFiles` | Textarea "Target files (newline-separated paths or globs relative to WebRoot)" (visible iff Enabled) |

**Filter form** (1.6.9 P0-3): operators can write `#{X | Filter}` for these 7
filters: `ToUpper`, `ToLower`, `Trim`, `ToBase64`, `FromBase64`, `HtmlEscape`,
`UrlEncode`. Frontend should mention this in the field help text.

### 11.4 — Structured Configuration Variables (JSON nested leaves)

Walks JSON object structure; for each leaf, if a Squid variable matches the path
(both `:` and `.` separators), replaces the value. **1.6.9 P0-4 type preservation**:
integer / boolean / array variable values stay as their native JSON types (not
stringified).

| Property | UI |
|---|---|
| `Squid.Action.IISWebSite.StructuredConfigurationVariables.Enabled` | Checkbox "Replace JSON configuration entries" |
| `Squid.Action.IISWebSite.StructuredConfigurationVariables.Targets` | Textarea "Target files (newline-separated relative paths)" (visible iff Enabled) |

### 11.5 — Additional Paths (1.6.9 P1-4)

Extends ALL FOUR rewriters above to scan additional directories OUTSIDE WebRoot.
Real-world use case: secrets at `<install>/config/` next to `<install>/wwwroot/`.

| Property | UI |
|---|---|
| `Squid.Action.IISWebSite.AdditionalPaths` | Textarea "Additional scan paths (newline-separated absolute dirs)" |

---

## 12 — Advanced card (Card 14, collapsed by default)

### 12.1 — IIS metabase retry knobs

| Property | Type | Default | UI |
|---|---|---|---|
| `Squid.Action.IISWebSite.MaxRetryFailures` | int | `5` | Number input "Max retry attempts" |
| `Squid.Action.IISWebSite.SleepBetweenRetryFailuresInSeconds` | int | `1–3` random | Number input "Sleep between retries (s)" |

### 12.2 — Error-tolerance toggles (1.6.9 P2-1, Octopus parity)

| Property | Default | UI |
|---|---|---|
| `Squid.Action.Package.IgnoreVariableReplacementErrors` | `False` | Checkbox "Ignore variable-replacement errors" |
| `Squid.Action.SubstituteInFiles.ShouldFailDeploymentOnSubstitutionFails` | `False` | Checkbox "Fail deployment on unresolved `#{X}` tokens" |
| `Squid.Action.Package.EnableDiagnosticsConfigTransformationLogging` | `False` | Checkbox "Enable XDT diagnostic logging" |

---

## 13 — Validation rules (frontend-side, before Save)

| Rule | UX |
|---|---|
| At least one of the three "Deployment Type" checkboxes must be checked | Disable Save button + show inline error on Card 1 |
| If `CreateOrUpdateWebSite=True`: WebSiteName, WebRoot, AppPoolName, Bindings (≥1) required | Show field-level required indicators |
| For each https binding: exactly one of `thumbprint` / `certificateVariable` | Inline error on that binding row |
| If `ApplicationPoolIdentityType=SpecificUser`: Username + Password required | Hide-show + field-required |
| If `Package.SkipIfAlreadyInstalled=True`: Package.SourcePath required (no source = nothing to fingerprint) | Inline error |
| If `Certificate.PfxBase64` set: ThumbprintVariableName required | Inline error |
| If any `SubstituteInFiles.Enabled=True`: TargetFiles required | Inline error |
| `Bindings` JSON-encoded string must parse + match the schema in §4.1 | Pre-save validation on the Bindings property |

---

## 14 — Variable reference (operator-facing helper)

Render an "insert variable" floating helper next to every text input that pops
the project's variable list. Format reference for operator help text:

| Form | What it does |
|---|---|
| `#{MyVariable}` | Plain replacement — Squid variable value substituted as text |
| `#{MyVariable \| ToUpper}` | Uppercase the value before substitution (1.6.9 filter form) |
| `#{MyVariable \| Trim}` | Trim whitespace |
| `#{MyVariable \| ToBase64}` / `\| FromBase64` | Base64 encode / decode |
| `#{MyVariable \| HtmlEscape}` / `\| UrlEncode` | HTML / URL escape |

Variable references work in EVERY property's value — Bindings JSON, ScriptBody,
file paths, anywhere.

---

## 15 — Example operator workflows

### 15.1 — Minimal: HTTP-only site, no rewriters

```jsonc
{
  "actionType": "Squid.DeployToIISWebSite",
  "properties": [
    { "propertyName": "Squid.Action.IISWebSite.CreateOrUpdateWebSite", "propertyValue": "True" },
    { "propertyName": "Squid.Action.IISWebSite.WebSiteName",            "propertyValue": "MyApp" },
    { "propertyName": "Squid.Action.IISWebSite.WebRoot",                "propertyValue": "C:\\inetpub\\MyApp" },
    { "propertyName": "Squid.Action.IISWebSite.ApplicationPoolName",    "propertyValue": "MyAppPool" },
    { "propertyName": "Squid.Action.IISWebSite.Bindings",
      "propertyValue": "[{\"protocol\":\"http\",\"port\":\"80\",\"host\":\"\",\"ipAddress\":\"*\",\"enabled\":true}]" }
  ]
}
```

### 15.2 — Production HTTPS with auto-imported cert + full rewriter pipeline

```jsonc
{
  "actionType": "Squid.DeployToIISWebSite",
  "properties": [
    { "propertyName": "Squid.Action.IISWebSite.CreateOrUpdateWebSite",        "propertyValue": "True" },
    { "propertyName": "Squid.Action.IISWebSite.WebSiteName",                  "propertyValue": "OrderApi" },
    { "propertyName": "Squid.Action.IISWebSite.WebRoot",                      "propertyValue": "C:\\inetpub\\OrderApi" },
    { "propertyName": "Squid.Action.IISWebSite.ApplicationPoolName",          "propertyValue": "OrderApiPool" },
    { "propertyName": "Squid.Action.IISWebSite.ApplicationPoolIdentityType",  "propertyValue": "ApplicationPoolIdentity" },
    { "propertyName": "Squid.Action.IISWebSite.ApplicationPoolFrameworkVersion","propertyValue": "v4.0" },
    { "propertyName": "Squid.Action.IISWebSite.Bindings",
      "propertyValue": "[{\"protocol\":\"https\",\"port\":\"443\",\"host\":\"orders.example.com\",\"ipAddress\":\"*\",\"enabled\":true,\"requireSni\":true,\"certificateVariable\":\"OrderApiCert\"}]" },
    { "propertyName": "Squid.Action.IISWebSite.EnableAnonymousAuthentication","propertyValue": "True" },
    { "propertyName": "Squid.Action.IISWebSite.StartApplicationPool",         "propertyValue": "True" },
    { "propertyName": "Squid.Action.IISWebSite.StartWebSite",                 "propertyValue": "True" },

    { "propertyName": "Squid.Action.IISWebSite.Certificate.PfxBase64",        "propertyValue": "#{OrderApiPfxBase64}" },
    { "propertyName": "Squid.Action.IISWebSite.Certificate.PfxPassword",      "propertyValue": "#{OrderApiPfxPassword}" },
    { "propertyName": "Squid.Action.IISWebSite.Certificate.ThumbprintVariableName","propertyValue": "OrderApiCert" },

    { "propertyName": "Squid.Action.IISWebSite.Package.SourcePath",           "propertyValue": "C:\\Squid\\Packages\\OrderApi-#{Octopus.Release.Number}.zip" },
    { "propertyName": "Squid.Action.IISWebSite.Package.PurgeBeforeExtract",   "propertyValue": "True" },
    { "propertyName": "Squid.Action.IISWebSite.Package.SkipIfAlreadyInstalled","propertyValue": "True" },

    { "propertyName": "Squid.Action.CustomScripts.PreDeploy.ps1",             "propertyValue": "Stop-WebAppPool 'OrderApiPool' -ErrorAction SilentlyContinue" },
    { "propertyName": "Squid.Action.CustomScripts.PostDeploy.ps1",            "propertyValue": "Invoke-WebRequest 'https://orders.example.com/health' -UseBasicParsing | Out-Null" },

    { "propertyName": "Squid.Action.IISWebSite.SubstituteInFiles.Enabled",    "propertyValue": "True" },
    { "propertyName": "Squid.Action.IISWebSite.SubstituteInFiles.TargetFiles","propertyValue": "appsettings.json\nappsettings.Production.json" },

    { "propertyName": "Squid.Action.IISWebSite.ConfigurationTransforms.Enabled","propertyValue": "True" },
    { "propertyName": "Squid.Action.IISWebSite.ConfigurationTransforms.EnvironmentName","propertyValue": "#{Squid.Environment.Name}" },

    { "propertyName": "Squid.Action.IISWebSite.ConfigurationVariables.Enabled","propertyValue": "True" },

    { "propertyName": "Squid.Action.IISWebSite.StructuredConfigurationVariables.Enabled","propertyValue": "True" },
    { "propertyName": "Squid.Action.IISWebSite.StructuredConfigurationVariables.Targets","propertyValue": "appsettings.json\nappsettings.Production.json" },

    { "propertyName": "Squid.Action.IISWebSite.AdditionalPaths",              "propertyValue": "C:\\inetpub\\OrderApi-secrets" }
  ]
}
```

---

## 16 — End-to-end UX checklist for frontend implementation

- [ ] Add `Squid.DeployToIISWebSite` to the step-picker catalog with icon + description
- [ ] Build the 14 cards as listed in §2 with conditional visibility
- [ ] Wire the Bindings repeater + HTTPS sub-form
- [ ] Sensitive-variable picker for PfxBase64 / PfxPassword / ApplicationPoolPassword
- [ ] PowerShell code editor (monaco-style) for inline PreDeploy / PostDeploy
- [ ] Variable-picker dropdown next to every text input (insert `#{X}`)
- [ ] Filter-form helper for SubstituteInFiles target-file textareas (`#{X | ToUpper}` etc.)
- [ ] Pre-save validation per §13
- [ ] Live preview of the Bindings JSON string the operator just constructed
- [ ] Help text linking to this doc + the Octopus reference for operators carrying specs

---

## 17 — Backend-side reference (where each property is consumed)

| Property family | Consumed by |
|---|---|
| Deployment type toggles | `DeployToIISWebSite.ps1:39-48` |
| WebSite + AppPool | `DeployToIISWebSite.ps1` `SetUp-ApplicationPool` + IIS configure dispatch |
| Bindings | `DeployToIISWebSite.ps1` netsh + appcmd cert binding |
| Package extraction | `DeployToIISWebSite.ps1` `Expand-IISPackage` |
| Cert auto-import + ACL | `DeployToIISWebSite.ps1` `Import-IISCertificateFromPfxBase64` + `Grant-AppPoolPrivateKeyAccess` |
| Custom scripts | `DeployToIISWebSite.ps1` PreDeploy/PostDeploy invoke blocks |
| Rewriters | `DeployToIISWebSite.ps1` `Update-IIS{ConfigurationVariables,ConfigurationTransforms,FilesWithVariableSubstitution,StructuredJsonConfiguration}` |
| AdditionalPaths | `DeployToIISWebSite.ps1` `Get-IISDeployScanPaths` called by every rewriter |
| Journal | `DeployToIISWebSite.ps1` `{Read,Write,Get}-IISDeployJournalEntry` |
| Error-tolerance toggles | `DeployToIISWebSite.ps1` rewriter-specific catch blocks |

---

## 18 — Versioning & compatibility

- All properties shipped through 1.6.9. Older Squid agents (≤1.6.8) accept the
  same property names but silently ignore unknown ones (forward-compat).
- Frontend can safely emit every property; old agents that don't recognise them
  just no-op.
- Drift detector at `IISDeployScriptDriftDetectorTests.cs` pins the agent-side
  consumer of every operator-facing property. If a property name changes, the
  test fails in CI and this doc must be updated in the same PR.

---

## 19 — Open questions / future work

- **Step-level retry** — not yet exposed at the step level (operator can wrap in a
  Squid retry container). Punt to v1.7.x.
- **YAML structured config rewriter** — `StructuredConfigurationVariables` ships
  JSON only; YAML target files would need a sibling target list.
- **AWS / Azure App Service** — out of scope; tracked as a separate target type.
- **Linux IIS Tentacle** — N/A (IIS is Windows-only by definition).
