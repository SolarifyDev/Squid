# API Key Permissions Reference

> **Audience**: operators issuing or troubleshooting API keys for Tentacle
> registration, deployment automation, and external integrations.
> **Status**: applies to Squid 1.7.0+ (structured 403 hint responses) — see
> "Legacy behaviour" at the bottom for 1.6.x.

---

## 1 — The core model

A Squid **API key is a credential bound to a user**. When an API call presents an API key, Squid resolves the owner user, then checks whether THAT user has the permissions the API call requires. The API key itself doesn't carry permissions — the owner user does.

**Consequence**: if you hit `403 Permission denied: X`, the API key is fine. The user it belongs to is missing permission `X`. The fix is to grant the user the permission, **not** to issue a new key.

---

## 2 — Permission required for common operations

| Operation | HTTP path | Permission required |
|---|---|---|
| Register a Tentacle Polling target | `POST /api/machines/register/tentacle-polling` | `MachineCreate` |
| Register a Tentacle Listening target | `POST /api/machines/register/tentacle-listening` | `MachineCreate` |
| Register a Kubernetes Agent target | `POST /api/machines/register/kubernetes-agent` | `MachineCreate` |
| Register an SSH target | `POST /api/machines/register/ssh` | `MachineCreate` |
| Register an OpenClaw target | `POST /api/machines/register/openclaw` | `MachineCreate` |
| Generate an install script for a Tentacle | `POST /api/machines/generate-install-script` | `MachineCreate` |
| Edit a deployment target | `PUT /api/machines/{id}` | `MachineEdit` |
| Delete a deployment target | `DELETE /api/machines/{id}` | `MachineDelete` |
| Create / queue a deployment | `POST /api/deployments` | `DeploymentCreate` |
| Cancel a running task | `POST /api/tasks/{id}/cancel` | `TaskCancel` |

> All `*Machine*` permissions are **space-scoped** (`PermissionScope.SpaceOnly`). They apply within a single space; an API key user with `MachineCreate` in Space A cannot register a machine in Space B.

---

## 3 — Built-in roles + their permissions

Squid ships with six built-in roles. The relevant ones for Tentacle register operations:

| Built-in role | Has `MachineCreate`? | Has `MachineView`? | Has `DeploymentCreate`? |
|---|---|---|---|
| **System Administrator** | ❌ | ❌ | ❌ |
| **Space Owner** | ✅ | ✅ | ✅ |
| **Environment Manager** | ✅ | ✅ | ❌ |
| **Project Deployer** | ❌ | ✅ | ✅ |
| **Project Contributor** | ❌ | ✅ | ❌ |
| **Project Viewer** | ❌ | ✅ | ❌ |

> **Why `System Administrator` lacks `MachineCreate`**: by design. `System Administrator` is the system-wide role that manages spaces, users, teams, and identity-provider configuration. It deliberately does NOT have space-scoped resource permissions like `MachineCreate` / `DeploymentCreate` / etc. — those belong to `Space Owner` (full space access) and the resource-specific roles (`Environment Manager` for machines, etc.). If a system admin needs to register a machine, they assign themselves the `Space Owner` role on that space.

---

## 4 — Resolving a "403 Permission denied" on register

The Tentacle CLI exits with code **403** and prints:

```
Permission denied: 'MachineCreate' permission required to register the machine.

Resolve via the Squid Web UI:
  1. Assign one of these roles to the API key user: Environment Manager, Space Owner, OR
  2. Add 'MachineCreate' permission to the user's current role, OR
  3. Issue a new API key from a user with one of the roles above.

Built-in roles that grant MachineCreate: Environment Manager, Space Owner
```

Three remediation paths, pick whichever fits your access model:

### Option A — Assign a built-in role

In **Configuration → Users → {the API key owner}**:

1. Open the user.
2. Under **Space Memberships**, find the relevant space row.
3. Assign **Environment Manager** (machine-scoped) OR **Space Owner** (full access).
4. Save.
5. Re-run the install snippet — no need to re-issue the API key.

### Option B — Add the permission to an existing custom role

If you run with custom roles (e.g. "DevOps", "Platform Engineer"):

1. **Configuration → User Roles → {custom role}**.
2. Add **`MachineCreate`** to the permission list (and typically also `MachineView`, `MachineEdit`, `MachineDelete` so the role can manage its own targets).
3. Save.
4. Re-run the install snippet.

### Option C — Issue a new API key from a different user

1. Switch to a user that has `MachineCreate` (e.g. yourself as `Space Owner`).
2. **Profile → API Keys → Generate New Key**.
3. Replace the old `--api-key` value in your install snippet with the new key.
4. Re-run.

---

## 5 — Structured 403 response shape

Squid 1.7.0+ enriches 403 responses with structured fields:

```json
{
  "code": 403,
  "msg": "Permission denied: MachineCreate. Missing permission 'MachineCreate'. Built-in roles that grant it: Environment Manager, Space Owner. Assign one of these roles to the API key user, or add 'MachineCreate' to their existing role.",
  "missingPermission": "MachineCreate",
  "suggestedRoles": ["Environment Manager", "Space Owner"]
}
```

Programmatic consumers (CI scripts, custom integrations) can parse `missingPermission` + `suggestedRoles` directly without scraping the prose `msg` field. The Tentacle CLI does exactly this and surfaces the structured hint in its stderr output.

---

## 6 — Audit + revocation

API keys are recorded in the audit log on every authenticated request. To find which API key was used:

```sql
-- Squid stores API keys hashed; the audit log records the resolved user + the key prefix.
SELECT * FROM audit_events
WHERE event_type = 'machine.register'
  AND occurred_at > NOW() - INTERVAL '1 day';
```

To revoke an API key:

1. **Profile → API Keys → {the key} → Revoke**.
2. Any subsequent call with that key returns `401 Unauthorized`.

To rotate an API key in an install snippet across many hosts:

1. Generate a new key.
2. Update the central install playbook (Ansible / Chef / SCCM / Intune script) with the new key.
3. Re-run the install snippet on each host.
4. Revoke the old key.

---

## 7 — Best practices

1. **One API key per integration**. Don't share keys between scripts, CI pipelines, and operator hands. Each gets its own key so revoking one doesn't break the others.
2. **Use service-account users for automation**, not your personal user. If you leave the company / change roles, the integration shouldn't break.
3. **Assign the LEAST-PRIVILEGED role that works**. For a host that only needs to register Tentacles, `Environment Manager` is enough — don't give the service account `Space Owner`.
4. **Rotate keys quarterly**. The audit log + revocation flow make this cheap.
5. **Don't commit API keys to git**. Even in private repos. Use a secrets manager (HashiCorp Vault, Azure Key Vault, AWS Secrets Manager, GitHub Actions secrets).

---

## 8 — Legacy behaviour (1.6.x)

Before 1.7.0, the 403 response body was a plain prose `msg` field with no structured `missingPermission` / `suggestedRoles`. The Tentacle CLI exited with generic code `1`, not `403`. The install script's exit-code wrapper couldn't programmatically detect permission denials — operators had to read the error message + cross-reference the source for the role list.

If you're on 1.6.x and hit a register 403:

```
Registration failed with code 403: {"code":403,"msg":"Permission denied: MachineCreate. ..."}
```

The remediation paths in section §4 still apply — just no structured-error help text from the CLI.

---

## 9 — Related docs

- [Windows Tentacle Install](windows-tentacle-install.md) — operator runbook for Windows installs including the 403 troubleshooting walkthrough
- `src/Squid.Message/Enums/Permission.cs` — the full Permission enum
- `src/Squid.Core/Services/DataSeeding/BuiltInRoleSeeder.cs` — built-in role definitions (source of truth)
- `src/Squid.Core/Services/Authorization/PermissionRoleResolver.cs` — server-side helper that drives the structured 403 response
