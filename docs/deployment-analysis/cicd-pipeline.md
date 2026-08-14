# CI/CD Pipeline Analysis — Squid

> Generated: 2026-08-14
> Source: `.github/workflows/` + `build/` + `GitVersion.yml`

---

## Table of Contents

1. [Pipeline Architecture Overview](#1-pipeline-architecture-overview)
2. [GitVersion.yml — Version Strategy](#2-gitversionyml--version-strategy)
3. [Build Matrix Strategy](#3-build-matrix-strategy)
4. [Docker Image Build & Push Flow](#4-docker-image-build--push-flow)
5. [Test Automation Stages](#5-test-automation-stages)
6. [Deployment Stages & Target Environments](#6-deployment-stages--target-environments)
7. [Secrets & Environment Variables](#7-secrets--environment-variables)
8. [Service-to-Stage Mapping](#8-service-to-stage-mapping)
9. [Artifact Consumption by Deployment Scripts](#9-artifact-consumption-by-deployment-scripts)

---

## 1. Pipeline Architecture Overview

The repository has **14 workflow files** organized into five functional pipelines:

| Workflow File | Primary Responsibility |
|---|---|
| `tests.yml` | Unit + Calamari + Tentacle + Integration tests (every push/PR) |
| `e2e-k8s-pipeline.yml` | Full K8s deployment pipeline E2E (PR + main + daily) |
| `e2e-upgrade-matrix.yml` | Real install script smoke across 8 distros (PR + manual) |
| `build-api-docker.yml` | Squid API server Docker image (main + tag + manual) |
| `build-publish-kubernetes-agent.yml` | K8s agent images + Helm chart (main + tag + manual) |
| `build-publish-linux-tentacle.yml` | Linux Tentacle tarballs + Docker + release (main + tag + manual) |
| `build-publish-windows-tentacle.yml` | Windows Tentacle zip archives + release (main + tag + manual) |
| `publish-linux-packages.yml` | APT/RPM package build + repo publish (workflow_run from Linux Tentacle) |
| `verify-linux-packages.yml` | Live install verification on real distros (daily + upstream complete + manual) |
| `tentacle-windows-e2e.yml` | Windows lifecycle E2E on windows-2022 + 2025 (daily + main + manual) |
| `tentacle-windows-smoke-e2e.yml` | PR gate: Windows lifecycle smoke (detect → tests → gate sentinel) |
| `tentacle-linux-e2e.yml` | Linux lifecycle E2E on ubuntu-latest (daily + main + manual) |
| `tentacle-linux-smoke-e2e.yml` | PR gate: Linux lifecycle smoke (detect → tests → gate sentinel) |
| `bootstrap-gpg-key.yml` | One-time GPG key bootstrap for package signing (workflow_dispatch only) |

### Trigger Conditions Summary

| Workflow | push (main) | pull_request | push (tag) | schedule | workflow_run | workflow_dispatch |
|---|---|---|---|---|---|---|
| `tests.yml` | ✅ | ✅ | — | — | — | — |
| `e2e-k8s-pipeline.yml` | ✅ | ✅ (path-filtered) | — | ✅ daily 09:00 UTC | — | ✅ |
| `e2e-upgrade-matrix.yml` | — | ✅ (path-filtered) | — | — | — | ✅ |
| `build-api-docker.yml` | ✅ | — | ✅ | — | — | ✅ |
| `build-publish-kubernetes-agent.yml` | ✅ | — | ✅ | — | — | ✅ |
| `build-publish-linux-tentacle.yml` | ✅ | — | ✅ | — | — | ✅ |
| `build-publish-windows-tentacle.yml` | ✅ | — | ✅ | — | — | ✅ |
| `publish-linux-packages.yml` | — | — | — | — | ✅ (from Linux Tentacle) | ✅ |
| `verify-linux-packages.yml` | — | — | — | ✅ daily 03:15 UTC | ✅ (from Linux Tentacle) | ✅ |
| `tentacle-windows-e2e.yml` | ✅ | — | — | ✅ daily 09:00 UTC | — | ✅ |
| `tentacle-windows-smoke-e2e.yml` | — | ✅ | — | — | — | ✅ |
| `tentacle-linux-e2e.yml` | ✅ | — | — | ✅ daily 09:00 UTC | — | ✅ |
| `tentacle-linux-smoke-e2e.yml` | — | ✅ | — | — | — | ✅ |

### Cross-Workflow Dependency Chain

```
push tag
  ├─ build-publish-linux-tentacle.yml    (creates GitHub Release + tarballs)
  │     └─ workflow_run → publish-linux-packages.yml (deb/rpm + gh-pages APT/RPM repo)
  │           └─ workflow_run → verify-linux-packages.yml (live install verification)
  │
  ├─ build-publish-windows-tentacle.yml  (appends .zip to same Release)
  │
  ├─ build-publish-kubernetes-agent.yml  (K8s images + Helm chart)
  │
  └─ build-api-docker.yml                (API server image)

push main
  ├─ tests.yml                           (all unit + integration tests)
  ├─ build-publish-*.yml                 (versioned images, no :latest update)
  ├─ tentacle-windows-e2e.yml            (daily + post-merge)
  ├─ tentacle-linux-e2e.yml              (daily + post-merge)
  └─ e2e-k8s-pipeline.yml                (K8s E2E on path-filtered PRs + main + daily)

pull_request (path-filtered)
  ├─ e2e-k8s-pipeline.yml                (path: src/**, tests/Squid.*Tests/**)
  ├─ e2e-upgrade-matrix.yml              (path: install-tentacle.sh, upgrade-*.sh, deploy/packaging/**)
  ├─ tentacle-windows-smoke-e2e.yml      (path-detect sentinel → 8 categories)
  └─ tentacle-linux-smoke-e2e.yml        (path-detect sentinel → 3 categories)
```

---

## 2. GitVersion.yml — Version Strategy

**File:** `GitVersion.yml` (repository root)

```yaml
mode: ContinuousDelivery
tag-prefix: '[vV]?'
branches:
  main:
    regex: ^main$
    tag: ''
    increment: Patch
```

### SemVer Derivation

| GitVersion Output | Value | Example |
|---|---|---|
| `fullSemVer` | `{Major}.{Minor}.{Patch}+{CommitCount}` | `1.4.2+20` |
| Docker tag | `fullSemVer` with `+` → `-` | `1.4.2-20` |
| Git tag (manual) | `v` or `V` prefix (optional) | `v1.4.2` |

### Versioning Rules

- **Mode:** `ContinuousDelivery` — every commit on `main` increments the patch component.
- **Branch matching:** Only `main` is versioned. Feature branches produce `0.1.0-{commits}+{count}` by GitVersion's default fallback (not overridden here).
- **Tag prefix:** `[vV]?` — both `v1.4.2` and `V1.4.2` and bare `1.4.2` are recognized.
- **Increment:** `Patch` on every `main` push. No `tag` value means no pre-release suffix on `main` builds.
- **Build metadata (`+{n}`):** Exposed in `fullSemVer` (e.g., `1.4.2-20` = 20 commits since last tag). Stripped for Docker tag, retained in GitHub Release artifact filenames.
- **No version override on `main` push:** The `version` input in `workflow_dispatch` only overrides for Docker image tags — the GitVersion computed value is still used as-is for tarball names.

### Image Tag Lifecycle

| Event | Tag Produced | `:latest` Updated? |
|---|---|---|
| Push to `main` | `{fullSemVer with +→-}` (e.g., `1.4.2-20`) | No |
| Push a Git tag | `{fullSemVer with +→-}` (e.g., `1.4.2`) | Yes (after manifest push) |
| `workflow_dispatch` with `version` input | User-specified string | No (unless also a tag push) |

---

## 3. Build Matrix Strategy

### 3.1 Docker Build Matrix (Cross-Platform)

All Docker workflows use a two-platform matrix:

```yaml
strategy:
  fail-fast: false   # one platform's failure must not mask the other's
  matrix:
    platform: [amd64, arm64]
```

- **QEMU + Docker Buildx** enables native ARM64 builds on `linux/amd64` runners via user-space emulation.
- **Builder prune before arm64:** `docker builder prune --all --force` runs before the arm64 job to reclaim disk space consumed by the amd64 build, preventing disk exhaustion on the runner.
- **Concurrency group:** All Docker workflows use `concurrency: group: ${{ github.workflow }}-${{ github.ref }}` with `cancel-in-progress: false` — this serializes concurrent "push main" + "push tag" runs on the same commit that would otherwise race at the Docker Hub push step.

### 3.2 Linux Binary Build Matrix

`build-publish-linux-tentacle.yml` builds four Runtime Identifiers (RIDs):

| RID | Arch Label | libc | Notes |
|---|---|---|---|
| `linux-x64` | `amd64` | glibc | Standard Linux (Ubuntu, Debian, RHEL) |
| `linux-arm64` | `arm64` | glibc | ARM64 Linux |
| `linux-musl-x64` | `musl-x64` | musl | Alpine Linux x64 |
| `linux-musl-arm64` | `musl-arm64` | musl | Alpine Linux ARM64 |

- **D2 motivation (1.6.0):** Alpine's musl libc is incompatible with glibc-built .NET binaries (`symbol not found` at runtime). `linux-musl-x64` is .NET's officially supported musl RID. The `install-tentacle.sh` detects the host libc via `ldd --version` and downloads the matching archive.
- **No musl Docker image** is built (only glibc `Dockerfile.Tentacle.Linux`). Alpine users use the tarball, not the container.

### 3.3 Windows Binary Build Matrix

`build-publish-windows-tentacle.yml` cross-compiles from `ubuntu-latest` (avoids 2× billing of `windows-latest`):

| RID | Arch Label | Notes |
|---|---|---|
| `win-x64` | `x64` | Standard Windows x64 |
| `win-arm64` | `arm64` | Windows on ARM (Surface Pro X, Azure Dpsv5/Eps ARM, AWS Graviton with Windows preview) |

Cross-compilation from Linux produces bit-identical output to native Windows publish (officially supported by .NET 9).

### 3.4 Upgrade Matrix (Systemd Container)

`e2e-upgrade-matrix.yml` uses real systemd-enabled Docker containers for 8 distros:

**APT family (7 images):**

| Distro | Image | Init System |
|---|---|---|
| Ubuntu 20.04 | `jrei/systemd-ubuntu:20.04` | systemd |
| Ubuntu 22.04 | `jrei/systemd-ubuntu:22.04` | systemd |
| Ubuntu 24.04 | `jrei/systemd-ubuntu:24.04` | systemd |
| Debian 12 | `jrei/systemd-debian:12` | systemd |
| Rocky Linux 9 | `rockylinux/rockylinux:9-ubi-init` | systemd |
| AlmaLinux 9 | `almalinux/9-init:9` | systemd |
| Fedora 41 | `jrei/systemd-fedora:41` | systemd |

**Alpine track (separate job):**

| Distro | Image | Init System |
|---|---|---|
| Alpine 3.20 | `alpine:3.20` | OpenRC |

**Why distro-official init images for Rocky/Alma:** `jrei/systemd-*` does not publish a `systemd-rockylinux` or `systemd-almalinux` image. The distro-official UBI-init images are used instead, avoiding third-party pruning risk.

### 3.5 K8s E2E Matrix

`e2e-k8s-pipeline.yml` does not use a matrix — all tests share one `KindClusterFixture` initialized once per workflow run. The Kind cluster is provisioned via:

```yaml
uses: helm/kind-action@v1.10.0
with:
  version: v0.24.0
  kubectl_version: v1.31.0
  cluster_name: squid-e2e
  wait: 120s
```

Pinned versions prevent the historical E2E flakiness root cause of silent kind/kubectl version drift.

### 3.6 Windows E2E Matrix

`tentacle-windows-e2e.yml` uses a two-OS matrix:

| Runner | Purpose |
|---|---|
| `windows-2022` | Older SCM / TLS defaults — proves upgrade compatibility |
| `windows-2025` | Current windows-latest — current compatibility baseline |

`windows-2019` is excluded because it has been retired from GitHub-hosted runners (queues indefinitely).

---

## 4. Docker Image Build & Push Flow

### 4.1 Image Registry Configuration

| Workflow | Registry Variable | Image Name Variable |
|---|---|---|
| `build-api-docker.yml` | `vars.DOCKER_HUB` | `vars.DOCKER_NAME` |
| `build-publish-kubernetes-agent.yml` | `vars.SQUID_DOCKER_HUB` | hardcoded per-image |
| `build-publish-linux-tentacle.yml` | `vars.SQUID_DOCKER_HUB` | hardcoded `squid-tentacle-linux` |

> **Note:** `vars.DOCKER_HUB` vs `vars.SQUID_DOCKER_HUB` may point at the same or different Docker Hub organizations. These are repository-level GitHub Actions variables.

### 4.2 Per-Platform Build & Provenance Verification

Each Docker build job follows this sequence:

```
1. actions/checkout@v4          — ref pinned to github.sha
2. Verify checkout SHA          — git rev-parse HEAD == github.sha (anti-race)
3. docker/setup-qemu-action@v3  — enable ARM64 emulation on amd64 runners
4. docker/setup-buildx-action@v3
5. docker/login-action@v3       — credentials from secrets.*
6. docker builder prune         — (arm64 job only) free disk for QEMU
7. docker/build-push-action@v6  — no-cache, pull, push
8. docker pull + docker inspect — verify org.opencontainers.image.revision label == github.sha
```

The provenance verification step (step 8) is a belt-and-suspenders guard against stale cached layers or ghost images from a previous run being pushed.

### 4.3 Build Artifacts per Workflow

| Workflow | Dockerfile | Platforms | Images Produced |
|---|---|---|---|
| `build-api-docker.yml` | `Dockerfile.Api` | amd64, arm64 | `{DOCKER_HUB}/{DOCKER_NAME}:{tag}-{platform}` → manifest `{DOCKER_HUB}/{DOCKER_NAME}:{tag}` |
| `build-publish-kubernetes-agent.yml` | `Dockerfile.Tentacle` | amd64, arm64 | `{SQUID_DOCKER_HUB}/squid-tentacle:{tag}-{platform}` |
| | `Dockerfile.Tentacle.Watchdog` | amd64, arm64 | `{SQUID_DOCKER_HUB}/squid-watchdog:{tag}-{platform}` |
| | `Dockerfile.NfsServer` | amd64, arm64 | `{SQUID_DOCKER_HUB}/nfs-server:{tag}-{platform}` |
| `build-publish-linux-tentacle.yml` | `Dockerfile.Tentacle.Linux` | amd64, arm64 | `{SQUID_DOCKER_HUB}/squid-tentacle-linux:{tag}-{platform}` |

### 4.4 Manifest Assembly & `:latest` Tag Policy

The `manifest` job in each Docker workflow:

1. **Creates and pushes the versioned multi-arch manifest** (e.g., `squid-tentacle:1.4.2-20`) using `docker manifest create` combining both platform-specific images.
2. **Conditionally updates `:latest`** — only on tag pushes, via `docker manifest create --amend`. The `if: startsWith(github.ref, 'refs/tags/')` guard prevents a later `push main` run from overwriting `:latest` with a pre-tag binary that raced a concurrent tag-push run (the incident that motivated this policy: `v1.3.2`).

### 4.5 Helm Chart Packaging

`build-publish-kubernetes-agent.yml` additionally packages and pushes a Helm chart:

```yaml
# Step 4: helm job (after manifest)
- cp -r deploy/helm/kubernetes-agent /tmp/kubernetes-agent
- sed -i "s/^version:.*/version: ${IMAGE_TAG}/" /tmp/kubernetes-agent/Chart.yaml
- sed -i "s/^appVersion:.*/appVersion: \"${IMAGE_TAG}\"/" /tmp/kubernetes-agent/Chart.yaml
- helm package /tmp/kubernetes-agent/
- helm push kubernetes-agent-${IMAGE_TAG}.tgz oci://registry-1.docker.io/${{ env.DOCKER_HUB }}
```

Chart version and `appVersion` are dynamically set from the resolved image tag. The chart is pushed to the Docker Hub OCI registry under the same Docker Hub org.

---

## 5. Test Automation Stages

### 5.1 Stage Hierarchy

```
Stage 1: tests.yml          (every push/PR — fast feedback)
  ├─ unit-tests              — Squid.UnitTests (Release build, no services)
  ├─ calamari-tests          — Squid.Calamari.Tests (Release build, no services)
  ├─ tentacle-tests          — Squid.Tentacle.Tests (Release build, no services)
  └─ integration-tests       — Squid.IntegrationTests (Postgres + Redis services)

Stage 2: PR gates            (on pull_request — path-detect sentinel)
  ├─ tentacle-windows-smoke-e2e.yml    — 8 Windows lifecycle categories
  ├─ tentacle-linux-smoke-e2e.yml      — 3 Linux lifecycle categories
  └─ e2e-upgrade-matrix.yml            — install script on 8 distros (path-filtered)

Stage 3: K8s E2E             (path-filtered PR + main + daily 09:00 UTC)
  └─ e2e-k8s-pipeline.yml    — Kind cluster + Postgres + Redis + Helm
       (Squid.E2ETests, Category=E2E)

Stage 4: Tentacle E2E        (daily 09:00 UTC + post-merge + manual)
  ├─ tentacle-windows-e2e.yml         — 15 Windows categories, windows-2022 + 2025
  └─ tentacle-linux-e2e.yml           — 6 Linux categories, ubuntu-latest

Stage 5: Upgrade Matrix      (post-merge full suite + path-filtered PR)
  └─ e2e-upgrade-matrix.yml           — 8 systemd containers + Alpine

Stage 6: Package Verification (daily 03:15 UTC + upstream complete + manual)
  └─ verify-linux-packages.yml        — live apt-get / yum install on real distros
```

### 5.2 Test Projects & Dependencies

| Test Project | Framework | Runner | Services Required | Notes |
|---|---|---|---|---|
| `Squid.UnitTests` | xUnit | `ubuntu-latest` | — | Pure unit tests |
| `Squid.Calamari.Tests` | xUnit | `ubuntu-latest` | — | Calamari script execution |
| `Squid.Tentacle.Tests` | xUnit | `ubuntu-latest` | — | Core agent logic |
| `Squid.IntegrationTests` | xUnit | `ubuntu-latest` | Postgres 17 + Redis 7 | DB + cache integration |
| `Squid.E2ETests` | xUnit | `ubuntu-latest` | Postgres 17 + Redis 7 + Kind | Full K8s pipeline |
| `Squid.WindowsTentacleE2ETests` | xUnit | `windows-2022/2025` | — | Windows lifecycle |
| `Squid.LinuxTentacleE2ETests` | xUnit | `ubuntu-latest` | — | Linux lifecycle |

### 5.3 Redis Service in Integration Tests

Redis was added as a required service for `Squid.IntegrationTests` on 2026-04-24 because `UpgradeDispatchLockReconcilerIntegrationTests` verifies the reconciler's actual `KeyDeleteAsync` call against a real Redis instance. Without Redis, 2 tests fail on CI while passing locally (where operators typically run a dev Redis). The service connects via `127.0.0.1:6379` matching `src/Squid.Api/appsettings.json`'s `RedisCacheConnectionString` default.

### 5.4 PR Gate Sentinel Pattern

Both smoke E2E workflows (`tentacle-windows-smoke-e2e.yml` and `tentacle-linux-smoke-e2e.yml`) use a three-job sentinel layout:

```
detect (ubuntu, ~20s)      — Always runs. Uses gh pr view --json files to
                             check if any relevant paths changed. Outputs
                             relevant=true|false.

tests (windows-latest or ubuntu) — Runs ONLY when detect.outputs.relevant=='true'.
                             Skipped (0 minutes) on unrelated PRs. The real
                             lifecycle E2E subset runs here.

gate (ubuntu, ~5s)         — Always runs. Carries the required status check.
                             Green when tests==success OR tests==skipped.
                             Red when tests==failure OR tests==cancelled OR
                             detect!=success.
```

This pattern enables making "Windows/Linux Lifecycle Smoke (PR gate)" a **required status check** on the repository without blocking PRs that don't touch Tentacle code — a plain paths-filtered workflow cannot be required because it never reports on unrelated PRs (they get stuck "Expected — waiting for status").

---

## 6. Deployment Stages & Target Environments

### 6.1 Deployment Artifacts

| Artifact Type | Produced By | Distribution Mechanism |
|---|---|---|
| Linux tar.gz (glibc + musl, x64 + arm64) | `build-publish-linux-tentacle.yml` → `build-binary` job | GitHub Release + `install-tentacle.sh` from raw GitHub URL |
| Windows .zip (x64 + arm64) | `build-publish-windows-tentacle.yml` → `build-binary` job | GitHub Release + `install-tentacle.ps1` from raw GitHub URL |
| .deb packages (amd64 + arm64) | `publish-linux-packages.yml` → `build` job | APT repo at `squid.solarifyai.com/apt` |
| .rpm packages (x86_64 + aarch64) | `publish-linux-packages.yml` → `build` job | RPM repo at `squid.solarifyai.com/rpm` |
| Docker images (multi-arch manifest) | `build-publish-linux-tentacle.yml` → `manifest` job | Docker Hub `{SQUID_DOCKER_HUB}/squid-tentacle-linux:{tag}` |
| K8s agent Docker images | `build-publish-kubernetes-agent.yml` | Docker Hub (squid-tentacle, squid-watchdog, nfs-server) |
| K8s API server Docker image | `build-api-docker.yml` | Docker Hub `{DOCKER_HUB}/{DOCKER_NAME}:{tag}` |
| Helm chart (OCI) | `build-publish-kubernetes-agent.yml` → `helm` job | Docker Hub OCI registry |
| GPG public key | `bootstrap-gpg-key.yml` (manual) | `squid.solarifyai.com/public.key` |

### 6.2 Package Repository Layout (gh-pages)

```
gh-pages/
├── apt/
│   ├── conf/
│   │   ├── distributions    # reprepro config, signed by GPG
│   │   └── options
│   ├── db/                  # reprepro state (local only, not published)
│   ├── dists/stable/
│   │   ├── InRelease        # signed by GPG
│   │   ├── Release          # signed by GPG, includes SHA256 of Packages
│   │   └── main/binary-{amd64,arm64}/Packages.gz
│   └── pool/main/s/squid-tentacle/
│       ├── squid-tentacle_{version}_amd64.deb
│       └── squid-tentacle_{version}_arm64.deb
├── rpm/
│   ├── squid-tentacle.repo  # user-facing .repo descriptor
│   ├── squid-tentacle-{version}-1.x86_64.rpm
│   ├── squid-tentacle-{version}-1.aarch64.rpm
│   ├── repodata/
│   │   ├── repomd.xml       # signed by GPG (enables repo_gpgcheck=1)
│   │   └── repomd.xml.asc   # GPG signature
│   └── (up to 5 versions retained per arch)
├── public.key               # GPG public key (armored)
└── install.sh               # convenience copy of install-tentacle.sh
```

### 6.3 Target Environments

| Environment | Delivery Mechanism | Notes |
|---|---|---|
| **Development / Local** | `dotnet build` / `dotnet run` | No artifacts consumed |
| **GitHub Release** | Download from `github.com/SolarifyDev/Squid/releases/tag/v{version}` | tar.gz (Linux), .zip (Windows), .deb/.rpm (tag runs only) |
| **APT/RPM Repo** | `apt-get install squid-tentacle` / `yum install squid-tentacle` | Always installs latest; squid.solarifyai.com |
| **Docker Hub** | `docker pull {org}/{image}:{tag}` | Multi-arch manifests; :latest only on tag pushes |
| **Helm OCI** | `helm pull oci://.../kubernetes-agent --version {tag}` | K8s agent Helm chart |
| **Kubernetes (Squid API server)** | `kubectl apply -f deploy/k8s/` | Squid API server deployment via `deploy/k8s/` manifests |
| **Kubernetes (Tentacle agent)** | `helm upgrade --install` using `deploy/helm/kubernetes-agent/` | Tentacle agent deployed via Helm chart into customer K8s cluster |

### 6.4 Package Signing Infrastructure

**GPG Key Bootstrap (`bootstrap-gpg-key.yml`):**

- One-time manual workflow that generates a 4096-bit RSA key (no passphrase).
- Prints the ASCII-armored private key to the run log **once** for the operator to copy to GitHub Secrets as `SQUID_GPG_PRIVATE_KEY`.
- The operator must then delete the workflow run from the Actions UI to wipe the key from GitHub's log store.
- Public key is committed to `deploy/packaging/public.key` and the fingerprint is documented in `docs/phase-2-apt-rpm-setup.md`.

**Signing scope:**

| Artifact | Signing Mechanism | Verification on Client |
|---|---|---|
| `.deb` | Not individually signed | SHA256 in signed Release file |
| `.rpm` | `rpmsign --addsign` (embedded RSA signature) | `dnf` verifies against signed `repomd.xml` |
| APT `InRelease` / `Release` | `reprepro --signwith` (GPG) | `apt-get` validates signed Release |
| RPM `repomd.xml` | `gpg --detach-sign` (GPG) | `dnf` with `repo_gpgcheck=1` |
| Docker images | Not signed by this pipeline | Docker Hub's own image trust |
| Helm chart | Helm OCI registry authentication | Helm OCI pull authentication |

**RPM signature verification limitation:** `rpm --checksig` on Ubuntu runners fails because `rpm --import` writes to a stub DB that doesn't persist. The pipeline instead inspects embedded signature headers via `rpm -qpi *.rpm | grep "Signature : RSA/"` to confirm `rpmsign` actually embedded the signature block.

---

## 7. Secrets & Environment Variables

### 7.1 Secrets (GitHub → `secrets.*`)

| Secret Name | Used By | Purpose |
|---|---|---|
| `DOCKER_USERNAME` | `build-api-docker.yml` | Docker Hub authentication for API image push |
| `DOCKER_PASSWORD` | `build-api-docker.yml` | Docker Hub authentication for API image push |
| `SQUID_DOCKER_USERNAME` | `build-publish-kubernetes-agent.yml`, `build-publish-linux-tentacle.yml`, `publish-linux-packages.yml` | Docker Hub authentication for K8s agent + Linux Tentacle image push |
| `SQUID_DOCKER_PASSWORD` | `build-publish-kubernetes-agent.yml`, `build-publish-linux-tentacle.yml`, `publish-linux-packages.yml` | Docker Hub authentication for K8s agent + Linux Tentacle image push |
| `SQUID_GPG_PRIVATE_KEY` | `bootstrap-gpg-key.yml` (write), `publish-linux-packages.yml` (read) | GPG private key for signing .rpm, APT Release, RPM repomd.xml |

### 7.2 Repository Variables (GitHub → `vars.*`)

| Variable Name | Used By | Value |
|---|---|---|
| `DOCKER_HUB` | `build-api-docker.yml` | Docker Hub organization/account for API image |
| `DOCKER_NAME` | `build-api-docker.yml` | Image name for API server (e.g., `squid-api`) |
| `SQUID_DOCKER_HUB` | `build-publish-kubernetes-agent.yml`, `build-publish-linux-tentacle.yml` | Docker Hub organization for agent images |

> These are repository-level variables (Settings → Variables → Variables tab), not secrets. They are publicly readable and safe for use in `env:` blocks.

### 7.3 Workflow-Injected Environment Variables

| Variable | Workflow | Value | Purpose |
|---|---|---|---|
| `NUGET_CONFIG` | All .NET workflows | `${{ github.workspace }}/NuGet.Config` | Custom NuGet config path |
| `EXPECTED_SHA` | All Docker + binary build workflows | `${{ github.sha }}` | Single source of truth for checkout verification |
| `E2E__KubeconfigPath` | `e2e-k8s-pipeline.yml` | `$HOME/.kube/config` | Tells `KindClusterFixture` to use external Kind cluster instead of creating one |
| `DEBIAN_FRONTEND` | APT install steps | `noninteractive` | Prevents `apt-get` from prompting for user input |
| `GITHUB_TOKEN` | All `gh` CLI calls | `secrets.GITHUB_TOKEN` (implicit) | GitHub API authentication for release downloads/uploads |

### 7.4 Checkout SHA Verification

Every build job includes a SHA verification step:

```bash
ACTUAL=$(git rev-parse HEAD)
if [ "$ACTUAL" != "$EXPECTED_SHA" ]; then
  echo "::error::Checkout SHA mismatch. Expected $EXPECTED_SHA, got $ACTUAL"
  exit 1
fi
```

This prevents a class of race conditions where the runner's `actions/checkout` could checkout a different commit than the workflow's `${{ github.sha }}` reference (e.g., a concurrent push modified the branch while the runner was spinning up).

---

## 8. Service-to-Stage Mapping

### Which Stages Build Which Artifacts

```
tests.yml
  └─ Squid.UnitTests, Squid.Calamari.Tests, Squid.Tentacle.Tests, Squid.IntegrationTests
       (no artifacts produced — validation only)

e2e-k8s-pipeline.yml
  └─ Squid.E2ETests (Kind cluster, Postgres, Redis)
       (no artifacts produced — validation only)

e2e-upgrade-matrix.yml
  └─ install-tentacle.sh smoke (8 systemd containers)
       (no artifacts produced — validation only)

build-api-docker.yml
  └─ Dockerfile.Api → {DOCKER_HUB}/{DOCKER_NAME}:{tag}-{amd64,arm64}
  └─ Manifest → {DOCKER_HUB}/{DOCKER_NAME}:{tag}
  └─ :latest tag (tag pushes only)

build-publish-kubernetes-agent.yml
  ├─ Dockerfile.Tentacle → {SQUID_DOCKER_HUB}/squid-tentacle:{tag}-{amd64,arm64}
  ├─ Dockerfile.Tentacle.Watchdog → {SQUID_DOCKER_HUB}/squid-watchdog:{tag}-{amd64,arm64}
  ├─ Dockerfile.NfsServer → {SQUID_DOCKER_HUB}/nfs-server:{tag}-{amd64,arm64}
  ├─ Manifests (3) → versioned multi-arch manifests per image
  ├─ :latest tags (tag pushes only)
  └─ deploy/helm/kubernetes-agent/ → Helm OCI chart push

build-publish-linux-tentacle.yml
  ├─ Dockerfile.Tentacle.Linux → {SQUID_DOCKER_HUB}/squid-tentacle-linux:{tag}-{amd64,arm64}
  ├─ Manifest → {SQUID_DOCKER_HUB}/squid-tentacle-linux:{tag}
  ├─ :latest tag (tag pushes only)
  ├─ dotnet publish → dist/squid-tentacle-{version}-{rid}.tar.gz (4 RIDs)
  └─ GitHub Release (tag pushes only) → tar.gz + .sha256 files

build-publish-windows-tentacle.yml
  ├─ dotnet publish → dist/squid-tentacle-{version}-{rid}.zip (2 RIDs)
  └─ GitHub Release append (tag pushes only) → .zip + .sha256 files

publish-linux-packages.yml
  ├─ fpm → squid-tentacle_{version}_{arch}.deb (2 archs)
  ├─ fpm → squid-tentacle-{version}-1.{arch}.rpm (2 archs)
  ├─ rpmsign → .rpm signed
  ├─ Attach to GitHub Release
  ├─ reprepro → APT repo (gh-pages/apt/)
  ├─ createrepo_c + gpg sign → RPM repo (gh-pages/rpm/)
  └─ Update gh-pages

verify-linux-packages.yml
  └─ Live install verification on Ubuntu 22.04, 24.04, Debian 12, Rocky 9, Fedora 40
       (no artifacts produced — validation only)

tentacle-windows-e2e.yml
  └─ Squid.Tentacle.Tests (Category=WindowsTentacleE2E) + Squid.WindowsTentacleE2ETests
       (windows-2022 + windows-2025)

tentacle-windows-smoke-e2e.yml
  └─ Squid.WindowsTentacleE2ETests (8 smoke categories)
       (windows-latest only, PR-gated)

tentacle-linux-e2e.yml
  └─ Squid.LinuxTentacleE2ETests (6 categories)
       (ubuntu-latest, nightly + main)

tentacle-linux-smoke-e2e.yml
  └─ Squid.LinuxTentacleE2ETests (3 smoke categories)
       (ubuntu-latest, PR-gated)

bootstrap-gpg-key.yml
  └─ GPG key generation + print (manual only, one-time)
```

### Dockerfile Inventory

| Dockerfile | Base Image | Purpose |
|---|---|---|
| `Dockerfile.Api` | (not specified in workflow — check file) | Squid API server |
| `Dockerfile.Tentacle` | (not specified in workflow — check file) | K8s Tentacle agent (used by K8s agent workflow) |
| `Dockerfile.Tentacle.Linux` | (not specified in workflow — check file) | Linux Tentacle Docker container |
| `Dockerfile.Tentacle.Watchdog` | (not specified in workflow — check file) | Watchdog companion container for Tentacle |
| `Dockerfile.NfsServer` | (not specified in workflow — check file) | NFS server for K8s shared storage |

---

## 9. Artifact Consumption by Deployment Scripts

### 9.1 Linux Installation (`deploy/scripts/install-tentacle.sh`)

The install script consumes artifacts in the following priority order:

```bash
# Priority 1: Package manager (apt/dnf) — uses APT/RPM repo published to gh-pages
curl -fsSL https://squid.solarifyai.com/install.sh | sudo bash
# → installs from squid.solarifyai.com/apt or /rpm (latest version)

# Priority 2: Direct tarball download (specific version, GitHub Release)
curl -fsSL https://raw.githubusercontent.com/SolarifyDev/Squid/main/deploy/scripts/install-tentacle.sh \
  | sudo bash -s -- --version 1.4.2
# → downloads https://github.com/SolarifyDev/Squid/releases/download/v1.4.2/squid-tentacle-1.4.2-linux-x64.tar.gz

# Priority 3: Latest tarball from GitHub Releases
curl -fsSL https://raw.githubusercontent.com/SolarifyDev/Squid/main/deploy/scripts/install-tentacle.sh | sudo bash
# → downloads https://github.com/SolarifyDev/Squid/releases/download/v1.4.2/squid-tentacle-linux-x64.tar.gz
#   (no version prefix = /releases/latest/download/)
```

**RID detection logic (D2 / 1.6.0):**

```bash
ldd --version 2>&1 | grep -qi musl && RID="linux-musl-x64" || RID="linux-x64"
```

The script probes `ldd --version` to detect musl vs glibc and selects `linux-musl-x64` or `linux-x64` accordingly. The musl tarballs are produced by the `linux-musl-x64` RID build in `build-publish-linux-tentacle.yml`.

**SHA256 verification (opportunistic):**

The script fetches `${DOWNLOAD_URL}.sha256` if present and verifies the archive hash. If the companion file is absent (older releases), it falls through without verification.

### 9.2 Windows Installation (`deploy/scripts/install-tentacle.ps1`)

```powershell
# Latest from GitHub Releases
irm https://raw.githubusercontent.com/SolarifyDev/Squid/main/deploy/scripts/install-tentacle.ps1 | iex

# Specific version
& ([scriptblock]::Create((irm https://raw.githubusercontent.com/SolarifyDev/Squid/main/deploy/scripts/install-tentacle.ps1))) -Version 1.4.2
```

The script downloads `squid-tentacle-{version}-win-x64.zip` or `squid-tentacle-win-x64.zip` (for latest) from GitHub Releases. Same SHA256 opportunistic verification as the Linux script.

### 9.3 After-Install Hooks (`deploy/packaging/`)

| Script | Trigger | Purpose |
|---|---|---|
| `deploy/packaging/after-install.sh` | `dpkg -i *.deb` / `rpm -i *.rpm` | systemd service setup, symlink creation |
| `deploy/packaging/before-uninstall.sh` | `dpkg -r squid-tentacle` / `rpm -e squid-tentacle` | Graceful service stop before removal |

These scripts are embedded in the .deb/.rpm via fpm's `--after-install` / `--before-remove` flags in `publish-linux-packages.yml`.

### 9.4 K8s Agent Installation (Helm)

```bash
helm pull oci://registry-1.docker.io/{SQUID_DOCKER_HUB}/kubernetes-agent --version {tag}
helm upgrade --install squid-agent oci://registry-1.docker.io/{SQUID_DOCKER_HUB}/kubernetes-agent
```

The Helm chart references the Docker images (squid-tentacle, squid-watchdog, nfs-server) by tag. The `appVersion` in `Chart.yaml` is set to the image tag, and `values.yaml` contains the image repository URLs.

### 9.5 Build Scripts (`build/`)

| Script | Purpose | Input | Output |
|---|---|---|---|
| `build/publish-tentacle.sh` | Publish self-contained Tentacle binaries | `VERSION` env arg (default `0.0.0-dev`) | `dist/squid-tentacle-{VERSION}-{rid}.tar.gz` + `dist/squid-tentacle-{rid}.tar.gz` (unversioned latest) |
| `build/release-tentacle.sh` | Create/update GitHub Release | `VERSION` positional arg | GitHub Release with tarballs + SHA256 companions |

**`publish-tentacle.sh` RID matrix:**

```bash
for RID in linux-x64 linux-arm64; do
    dotnet publish src/Squid.Tentacle/Squid.Tentacle.csproj -c Release -r "$RID" --self-contained true \
        -p:PublishSingleFile=true -p:Version="$VERSION" -o "dist/$RID"
    dotnet publish src/Squid.Calamari/Squid.Calamari.csproj -c Release -r "$RID" --self-contained true \
        -o "dist/$RID"
    tar czf "dist/squid-tentacle-${VERSION}-${RID}.tar.gz" -C "dist/$RID" .
    cp "dist/squid-tentacle-${VERSION}-${RID}.tar.gz" "dist/squid-tentacle-${RID}.tar.gz"
done
```

**`release-tentacle.sh` consumption:**

- Verifies versioned and unversioned archives exist.
- Creates a new GitHub Release via `gh release create` or adds to an existing one via `gh release upload --clobber`.
- Produces the same Release body format as `build-publish-linux-tentacle.yml` for consistency.

### 9.6 APT/RPM Repository Structure

```
squid.solarifyai.com
├── apt/
│   └── stable/main/binary-{amd64,arm64}/Packages.gz  # signed by GPG
├── rpm/
│   └── repodata/repomd.xml                           # signed by GPG
├── squid-tentacle.repo                               # user's onboarding descriptor
└── public.key                                        # GPG public key for import
```

The `squid-tentacle.repo` file (deployed to `gh-pages/rpm/`) contains:

```ini
[squid-tentacle]
name=Squid Tentacle
baseurl=https://squid.solarifyai.com/rpm/
gpgcheck=1
gpgkey=https://squid.solarifyai.com/public.key
enabled=1
```

---

## Appendix: Key Design Decisions

### A. `:latest` Tag Discipline

`push main` runs produce versioned images (e.g., `1.4.2-20`) but **must not** update `:latest`. This prevents a race where a `push main` run on the same commit as a `push tag` run could overwrite `:latest` with a pre-tag binary. The fix was applied after `v1.3.2` ended up pointing at a pre-`v1.3.2` binary.

### B. Cross-Platform Docker Build

ARM64 Docker images are built on `linux/amd64` runners using QEMU user-space emulation. The `docker builder prune` before the arm64 job is critical — without it, the combined amd64 + arm64 layer cache exhausts the runner's disk.

### C. Windows Cross-Compilation from Linux

`build-publish-windows-tentacle.yml` runs on `ubuntu-latest` (1× billing) instead of `windows-latest` (2× billing). .NET 9's cross-compilation produces bit-identical output to native Windows publish. The Windows-specific runtime behavior (sc.exe, WindowsServiceHost, WindowsPowerShell) is exercised by the `tentacle-windows-e2e.yml` workflow on real `windows-latest` runners.

### D. Three-Job Sentinel for Required PR Gates

Making a paths-filtered workflow a required status check causes unrelated PRs to get stuck "Expected — waiting for status." The three-job sentinel (detect → tests → gate) always reports a status (green or red), enabling required checks without blocking unrelated PRs.

### E. Repository Retention Policy

- **APT:** reprepro's Packages-index model is one-version-per-(package, arch) — only the latest version is available in the repo. Downgrades must use GitHub Release direct .deb downloads.
- **RPM:** createrepo_c supports multi-version. Retention keeps the last 5 versions per arch. Without this, gh-pages grows unbounded and hits GitHub's 5GB soft limit after ~1 year of weekly releases.
- **GitHub Release artifacts:** Permanently retained. No automatic deletion.

### F. GPG Key Design

- **No passphrase:** Signing is automated from CI with no human at the console. Security relies on GitHub Secrets encryption + restricted workflow access. A passphrase in a second Secret adds breakage surface without meaningfully improving security.
- **4096-bit RSA:** Industry standard. 2-year expiration default (rotatable via re-running bootstrap-gpg-key.yml).
- **Per-package .deb signatures not used:** APT's security model signs the repo-level `Release` file; per-package signatures are redundant for modern distros. `dpkg-sig` was removed from Ubuntu 24.04+ repos, further confirming this direction.
