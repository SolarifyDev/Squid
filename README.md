<div align="center">

<img src="branding/squid-logo.png" alt="Squid" width="160">

### 免费、简单、开箱即用的新一代发布平台

企业级发布能力，覆盖 Kubernetes、Linux、Windows、SSH 与 AI Agent。<br>一条流水线，从 Release 到生产 —— 不限项目，不限用户，不限目标。

<br>

[![CI](https://github.com/SolarifyDev/Squid/actions/workflows/tests.yml/badge.svg)](https://github.com/SolarifyDev/Squid/actions/workflows/tests.yml)
[![Release](https://img.shields.io/github/v/release/SolarifyDev/Squid?sort=semver&label=release&color=0F766E)](https://github.com/SolarifyDev/Squid/releases)
[![Platforms](https://img.shields.io/badge/platform-Linux%20%C2%B7%20Windows%20%C2%B7%20Docker-0B1F3A)](#-快速开始)
[![Kubernetes](https://img.shields.io/badge/Kubernetes-native-326CE5?logo=kubernetes&logoColor=white)](#-快速开始)
[![.NET 9](https://img.shields.io/badge/.NET-9-512BD4?logo=dotnet&logoColor=white)](https://dotnet.microsoft.com)
[![License: MIT](https://img.shields.io/badge/license-MIT-0F766E)](#-许可证)

**简体中文** · [English](README.en.md)

<br>

[![快速开始](https://img.shields.io/badge/%E5%BF%AB%E9%80%9F%E5%BC%80%E5%A7%8B-Docker-0B1F3A?style=for-the-badge&logo=docker&logoColor=white)](#-快速开始)
&nbsp;
[![下载](https://img.shields.io/badge/%E4%B8%8B%E8%BD%BD-Tentacle%20%C2%B7%20Agent-0F766E?style=for-the-badge&logo=github&logoColor=white)](https://github.com/SolarifyDev/Squid/releases)

<br>

<picture>
  <source media="(prefers-color-scheme: dark)" srcset="branding/readme/orbit-zh-dark.svg">
  <img src="branding/readme/orbit-zh-light.svg" alt="一条流水线，覆盖每一个目标：Kubernetes、Linux、Windows、SSH 与 AI Agent" width="100%">
</picture>

</div>

---

<picture>
  <source media="(prefers-color-scheme: dark)" srcset="branding/readme/features-zh-dark.svg">
  <img src="branding/readme/features-zh-light.svg" alt="为团队打造的企业级发布能力：免费自托管、开箱即用、统一发布模型、云原生、企业级治理、可扩展" width="100%">
</picture>

<br><br>

<picture>
  <source media="(prefers-color-scheme: dark)" srcset="branding/readme/free-zh-dark.svg">
  <img src="branding/readme/free-zh-light.svg" alt="永久免费。MIT 协议开源，全部功能可用；项目、用户与部署目标均不限，商业发布平台通常按档位订阅计费。" width="100%">
</picture>

<br>

## 🚀 快速开始

**运行 Server**（需 PostgreSQL 与 Redis）

```bash
docker build -f Dockerfile.Api -t squid-api .
docker run --rm -p 8080:8080 -p 10943:10943 squid-api
```

**连接部署目标**

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

> 生产环境请通过环境变量或密钥管理系统注入数据库、证书与加密密钥。

## 🔄 从 Octopus 迁移

上传 Octopus 导出文件，预览、校验后一键导入项目、环境、变量与部署流程。敏感值不会从导出文件恢复，需在 Squid 中重新填写。

## 📄 许可证

[MIT](LICENSE) © Squid contributors.
