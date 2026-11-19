<div align="center">

<img src="branding/squid-logo.png" alt="Squid" width="160">

### Free, simple, ready-to-use — the next-generation release platform.

Enterprise-grade delivery for Kubernetes, Linux, Windows, SSH, and AI agents.<br>One pipeline from release to production — no limits on projects, users, or targets.

<br>

[![CI](https://github.com/SolarifyDev/Squid/actions/workflows/tests.yml/badge.svg)](https://github.com/SolarifyDev/Squid/actions/workflows/tests.yml)
[![Release](https://img.shields.io/github/v/release/SolarifyDev/Squid?sort=semver&label=release&color=0F766E)](https://github.com/SolarifyDev/Squid/releases)
[![Platforms](https://img.shields.io/badge/platform-Linux%20%C2%B7%20Windows%20%C2%B7%20Docker-0B1F3A)](#-quick-start)
[![Kubernetes](https://img.shields.io/badge/Kubernetes-native-326CE5?logo=kubernetes&logoColor=white)](#-quick-start)
[![.NET 9](https://img.shields.io/badge/.NET-9-512BD4?logo=dotnet&logoColor=white)](https://dotnet.microsoft.com)
[![License: MIT](https://img.shields.io/badge/license-MIT-0F766E)](#-license)

**English** · [繁體中文](README.zh-TW.md)

<br>

[![Quick start](https://img.shields.io/badge/Quick%20start-Docker-0B1F3A?style=for-the-badge&logo=docker&logoColor=white)](#-quick-start)
&nbsp;
[![Download](https://img.shields.io/badge/Download-Tentacle%20%C2%B7%20Agent-0F766E?style=for-the-badge&logo=github&logoColor=white)](https://github.com/SolarifyDev/Squid/releases)

<br>

<picture>
  <source media="(prefers-color-scheme: dark)" srcset="branding/readme/orbit-en-dark.svg">
  <img src="branding/readme/orbit-en-light.svg" alt="One pipeline, every target: Kubernetes, Linux, Windows, SSH, and AI agents" width="100%">
</picture>

</div>

---

<picture>
  <source media="(prefers-color-scheme: dark)" srcset="branding/readme/features-en-dark.svg">
  <img src="branding/readme/features-en-light.svg" alt="Built for teams, enterprise-grade by default: free and self-hosted, ready to use, one release model, cloud native, enterprise governance, extensible" width="100%">
</picture>

<br><br>

<picture>
  <source media="(prefers-color-scheme: dark)" srcset="branding/readme/free-en-dark.svg">
  <img src="branding/readme/free-en-light.svg" alt="Free. For good. MIT-licensed with every feature included; unlimited projects, users, and targets, where commercial platforms typically bill by tier." width="100%">
</picture>

<br>

## 🚀 Quick start

**Run the server** (requires PostgreSQL and Redis)

```bash
docker build -f Dockerfile.Api -t squid-api .
docker run --rm -p 8080:8080 -p 10943:10943 squid-api
```

**Connect a target**

```bash
# Linux Tentacle
curl -fsSL https://raw.githubusercontent.com/SolarifyDev/Squid/main/deploy/scripts/install-tentacle.sh | bash

# Kubernetes Agent
helm upgrade --install squid-agent deploy/helm/kubernetes-agent
```

```powershell
# Windows Tentacle
irm https://raw.githubusercontent.com/SolarifyDev/Squid/main/deploy/scripts/install-tentacle.ps1 | iex
```

> In production, inject database, certificate, and encryption-key settings through environment variables or a secret manager.

## 🔄 Migrating from Octopus

Upload an Octopus export, preview and validate it, then import projects, environments, variables, and deployment processes in one step. Sensitive values are never restored from the export and must be re-entered in Squid.

## 📄 License

[MIT](LICENSE) © Squid contributors.
