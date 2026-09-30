<div align="center">

<picture>
  <source media="(prefers-color-scheme: dark)" srcset="branding/squid-wordmark-reverse.png">
  <img src="branding/squid-wordmark.png" alt="Squid" width="300">
</picture>

### Free, self-hosted application and Kubernetes deployment

One clear release model for applications, Kubernetes workloads, Windows Services, and scripts.
No software limits on projects, users, or deployment targets. Self-hosted, extensible, and familiar to Octopus users.

<br>

[![CI](https://github.com/SolarifyDev/Squid/actions/workflows/tests.yml/badge.svg)](https://github.com/SolarifyDev/Squid/actions/workflows/tests.yml)
[![.NET](https://img.shields.io/badge/.NET-9.0-512BD4?style=flat-square&logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/)
[![PostgreSQL](https://img.shields.io/badge/PostgreSQL-required-4169E1?style=flat-square&logo=postgresql&logoColor=white)](https://www.postgresql.org/)
[![Deploy](https://img.shields.io/badge/deploy-Kubernetes%20%C2%B7%20Linux%20%C2%B7%20Windows-0F766E?style=flat-square)](#-supported-deployment-targets)
[![Octopus Import](https://img.shields.io/badge/Octopus-import%20supported-2F81F7?style=flat-square)](#-migrating-from-octopus)
[![License: MIT](https://img.shields.io/badge/license-MIT-2EA44F?style=flat-square)](LICENSE)

**English** · [简体中文](README.md)

<br>

[![Quick Start](https://img.shields.io/badge/Quick%20Start-Deploy%20Squid-0F766E?style=for-the-badge&logo=rocket&logoColor=white)](#-quick-start)
&nbsp;
[![Import Octopus](https://img.shields.io/badge/Import-Octopus%20Project-2F81F7?style=for-the-badge&logo=octopusdeploy&logoColor=white)](#-migrating-from-octopus)

<br>

<img src="branding/squid-overview.svg" alt="Squid orchestrates projects, releases, approvals, and deployment targets in one delivery pipeline" width="1000">

</div>

---

**Squid** gives delivery teams a familiar deployment model with fewer constraints. Define projects and variables, create releases, orchestrate approvals, scripts, packages, and Kubernetes operations, then deploy to agents, Tentacles, SSH hosts, or custom targets.

> 🧩 **Octopus-style model** · 🆓 **Free software** · 🏠 **Self-hosted** · ☸️ **Kubernetes native** · 🔐 **Secret-safe** · 📜 **Audited**

## ✨ Why Squid

| | |
|---|---|
| 🆓 **Free self-hosting** | No software charge by project, user seat, or deployment target count. You remain in control of infrastructure cost. |
| 🧩 **A familiar delivery model** | Spaces, projects, environments, lifecycles, channels, releases, variables, and deployment history. |
| ☸️ **Kubernetes native** | Kubernetes Agent / API, kubectl, Helm, Kustomize, and native resource orchestration. |
| 🖥️ **One pipeline, many targets** | Kubernetes, Windows Tentacle, Linux Tentacle, SSH, and HTTP API targets share the same workflow. |
| 🔁 **Octopus project import** | Upload an Octopus export, preview compatibility, validate conflicts, and import current configuration transactionally. |
| 🔐 **Careful with secrets** | Secrets are never silently recovered during import. Variables support encryption, output capture, and log masking. |
| 🚀 **Extensible execution** | Transport, Intent, and Action Handler layers let new targets and actions plug in without rewriting the pipeline. |
| 📜 **Governance built in** | JWT / API keys, RBAC, teams, permission scopes, manual approvals, and audit events. |

> **In short:** keep the release model Octopus users know while removing tiered limits on projects, users, and deployment targets.

---

## ⚖️ Squid vs Octopus

### 💰 Pricing Model

| | Squid | Octopus Deploy |
|---|---:|---:|
| Software cost | **Free self-hosted** | Tiered by project count and edition |
| Project count | **Unlimited** | Free: 10; Professional starts at 20 |
| User seats | **Unlimited** | Free: 10; unlimited on paid plans |
| Deployment targets / nodes | **Unlimited** | Priced by plan and usage model |
| Public pricing reference | — | Professional: from USD 4,330 / year |

> Octopus pricing is included only as a public reference from `octopus.com/pricing` (2026-09-29). Actual prices and entitlements are governed by the Octopus website.

### 🧮 Feature Comparison

| Capability | Squid | Octopus Deploy |
|---|:---:|:---:|
| Self-hosting | Yes | Yes (Server) |
| Projects / environments / lifecycles | Yes | Yes |
| Releases and deployment history | Yes | Yes |
| Variable scopes and sensitive variables | Yes | Yes |
| Kubernetes Agent | Yes | Yes |
| Kubernetes API | Yes | Yes |
| Helm / Kustomize / YAML | Yes | Yes |
| SSH targets | Yes | Yes |
| Windows Tentacle | Yes | Yes |
| Windows Service / IIS | Yes | Yes |
| Manual approvals | Yes | Yes |
| RBAC / API keys / audit | Yes | Yes |
| Octopus project import | Yes | Native |
| Tenant model | Partial | Yes |
| Tenant tag filtering | No | Yes |
| Global deployment freezes | Partial | Yes |
| SIEM audit streaming | Partial | Yes |
| Hosted cloud service | No (self-hosted) | Yes |

---

## 🚀 Capabilities

| Area | Capabilities |
|---|---|
| Delivery model | Spaces, project groups, projects, environments, lifecycles, channels, releases, deployment history |
| Deployment orchestration | Step conditions, parallel steps, delayed starts, target roles, environment/channel filters, timeouts, and retries |
| Variables | Variable scopes, sensitive variables, variable snapshots, output variables, and configuration variable replacement |
| Security and governance | JWT / API keys, RBAC, permission scopes, teams, audit events, and manual approvals |
| Execution | Bash, PowerShell, Python, C#, HTTP, package deployment, health checks, and rollback |
| Cloud native | Kubernetes Agent / API, kubectl, Helm, Kustomize, and native Kubernetes resources |
| Platforms | Windows, Linux, Docker, and Kubernetes |

### 🗺️ Deployment Pipeline

```mermaid
flowchart LR
    A["Projects + Variables<br/>Environments + Lifecycles"] --> B["Release"]
    B --> C{"Deployment Orchestration"}
    C --> D["Manual Approval"]
    C --> E["Scripts / HTTP / Packages"]
    C --> F["Kubernetes<br/>Helm / Kustomize / YAML"]
    C --> G["Windows Service<br/>IIS"]
    D --> H["Deployment Targets"]
    E --> H
    F --> H
    G --> H
    H --> I["Kubernetes Agent / API"]
    H --> J["Tentacle<br/>Polling / Listening"]
    H --> K["SSH"]
    H --> L["OpenClaw"]
```

---

## 🎯 Supported Deployment Targets

| Target | Communication | Best For | Main Capabilities |
|---|---|---|---|
| Kubernetes Agent | Long-lived Halibut connection | In-cluster execution and restricted networks | kubectl, Helm, Kustomize, and native resources |
| Kubernetes API | Direct connection from the server | Quickly connecting an existing cluster | kubectl, Helm, Kustomize, and native resources |
| Tentacle Polling | Agent-initiated outbound connection | Internal networks and restrictive firewalls | Scripts, packages, Windows Service, and IIS |
| Tentacle Listening | Server-initiated connection | Targets reachable on a fixed port | Scripts, packages, Windows Service, and IIS |
| SSH | SSH / SFTP | Linux hosts without an installed agent | Bash, PowerShell, Python, and package upload |
| OpenClaw | HTTP API | Agent tool calls and automation | Tools, agents, waits, assertions, and result extraction |

---

## 🔄 Migrating From Octopus

Squid includes a backend import workflow, so you do not have to rebuild every project by hand.

```text
Upload -> Extract -> Preview -> Validate -> Confirm -> Succeeded
```

| Stage | Purpose |
|---|---|
| Upload | Upload an Octopus export archive or JSON file |
| Extract | Safely extract files, classify resources, and build the dependency graph |
| Preview | Show resources that will be created, reused, skipped, or blocked |
| Validate | Check conflicts, missing references, credentials, and targets |
| Confirm | Create Squid resources inside a transaction |
| Status | Inspect results, mappings, and diagnostics |

**Current import scope**

- Imports current deployable configuration: projects, project groups, environments, lifecycles, channels, variables, deployment processes, steps, feeds, selected accounts, Tentacles, and certificates.
- Maps common Octopus actions to Squid actions, including Kubernetes Containers, Ingress, Script, Manual, IIS, and Windows Service.
- Does not import releases, deployments, server tasks, or historical snapshots. They are detected and reported as out of scope.
- Imports sensitive variables with empty values and marks them as required input. Feed, account, certificate, and target secrets are never silently recovered from the export.
- Skips unsupported actions or creates disabled, redacted placeholder actions.

---

## 📦 Quick Start

### Requirements

| Component | Requirement |
|---|---|
| Squid Server | .NET 9 / Docker |
| Database | PostgreSQL |
| Background jobs and distributed locks | Redis |
| Source build | .NET SDK 9 |

### Run Locally

```bash
git clone https://github.com/SolarifyDev/Squid.git
cd Squid

# Start PostgreSQL and Redis first, then update these values for your environment:
#   SquidStore:ConnectionString
#   RedisCacheConnectionString
#   Security:VariableEncryption:MasterKey
#   SelfCert:Base64 / SelfCert:Password

dotnet run --project src/Squid.Api
```

After startup:

- API / Swagger: `https://localhost:7078/swagger`
- HTTP: `http://localhost:5078`
- Halibut polling: `10943`

> Production deployments must replace the development certificate, JWT key, database password, and variable encryption master key.

### Docker

```bash
docker build -f Dockerfile.Api -t squid-api .
docker run --rm -p 8080:8080 -p 10943:10943 squid-api
```

For a real deployment, inject PostgreSQL, Redis, certificate, and encryption settings through environment variables or a secret manager.

### Install Tentacle

Linux:

```bash
curl -fsSL https://raw.githubusercontent.com/SolarifyDev/Squid/main/deploy/scripts/install-tentacle.sh | bash
```

Windows PowerShell:

```powershell
irm https://raw.githubusercontent.com/SolarifyDev/Squid/main/deploy/scripts/install-tentacle.ps1 | iex
```

Deploy the Kubernetes Agent:

```bash
helm upgrade --install squid-agent deploy/helm/kubernetes-agent
```

---

## 🏗️ Architecture and Layout

```mermaid
flowchart TB
    subgraph Server["Squid Server"]
        API["Squid.Api"]
        CORE["Squid.Core"]
        DB[("PostgreSQL")]
        REDIS[("Redis / Hangfire")]
        API --> CORE
        CORE --> DB
        CORE --> REDIS
    end

    subgraph Agents["Deployment Targets"]
        AGENT["Kubernetes Agent"]
        TENTACLE["Tentacle"]
        SSH["SSH Host"]
    end

    CORE -->|"Halibut"| AGENT
    CORE -->|"Halibut"| TENTACLE
    CORE -->|"SSH"| SSH

    AGENT --> WORK["Script Pods<br/>kubectl / helm / bash"]
    TENTACLE --> CALAMARI["squid-calamari<br/>scripts / packages"]
    SSH --> REMOTE["Remote Shell<br/>uploads / executions"]
```

```text
Squid/
|-- src/
|   |-- Squid.Api/                  # HTTP API, auth, controllers, Hangfire
|   |-- Squid.Core/                 # Deployment domain, execution engine, import, persistence
|   |-- Squid.Message/              # Commands, events, DTOs, cross-process contracts
|   |-- Squid.Tentacle/             # Linux / Windows agent
|   |-- Squid.Tentacle.Watchdog/    # Kubernetes Agent watchdog
|   `-- Squid.Calamari/             # Script, package, configuration, and K8s executor
|-- deploy/
|   |-- helm/kubernetes-agent/      # Kubernetes Agent Helm chart
|   |-- docker/linux-tentacle/      # Linux Tentacle Compose
|   `-- scripts/                    # Linux / Windows install scripts
|-- docs/                           # Architecture, API, installation, and operations docs
|-- samples/                        # Windows Service / NuGet feed samples
`-- tests/                          # Unit, integration, and E2E tests
```

---

## 🧪 Tests

```bash
dotnet test Squid.sln
```

The test suite covers domain logic, the deployment pipeline, Octopus import, Kubernetes, SSH, Tentacle, Calamari, Windows Service, and end-to-end scenarios.

---

## 📚 Documentation

| Document | Contents |
|---|---|
| [`docs/deployment-pipeline-architecture.md`](docs/deployment-pipeline-architecture.md) | Full deployment pipeline architecture |
| [`docs/k8s-deployment-architecture.md`](docs/k8s-deployment-architecture.md) | Kubernetes deployment architecture |
| [`docs/windows-tentacle-install.md`](docs/windows-tentacle-install.md) | Windows Tentacle installation and troubleshooting |
| [`docs/api-key-permissions.md`](docs/api-key-permissions.md) | API key and permission model |
| [`docs/tentacle-self-upgrade-design.md`](docs/tentacle-self-upgrade-design.md) | Tentacle self-upgrade design |
| [`CLAUDE.md`](CLAUDE.md) | Developer architecture reference |

---

## 🧭 Limitations

Squid aims to cover the majority of application and Kubernetes delivery scenarios. It does not attempt to clone every commercial Octopus capability. Current major differences:

- The tenant model and tenant tag filtering are incomplete.
- Octopus import focuses on current deployable configuration and does not migrate historical releases, deployments, or server tasks.
- Sensitive values are not recovered from Octopus exports and must be entered again in Squid.
- Squid does not provide an official hosted cloud service. Production deployments are self-hosted.
- Enterprise governance capabilities such as global deployment freezes and SIEM audit streaming do not yet reach full Octopus parity.

---

## 📄 License

This project is licensed under the [MIT License](LICENSE).

You are free to use, copy, modify, merge, publish, distribute, sublicense, and sell copies of Squid, provided that the original copyright notice and license notice are retained. The software is provided "as is", without warranty of any kind.

Third-party components remain subject to their respective licenses.

## 🤝 Contributing

Issues, pull requests, deployment scenarios, and documentation improvements are welcome. When adding a new execution target, action, or import mapping, follow the existing Transport, Intent, Handler, Mediator, and test patterns.

<div align="center"><sub>Built with .NET · PostgreSQL · Redis · Kubernetes · Halibut</sub></div>
