<div align="center">

<img src="branding/squid-logo.png" alt="Squid" width="160">

### 免費、簡單、開箱即用的新一代發布平台

企業級發布能力，涵蓋 Kubernetes、Linux、Windows、SSH 與 AI Agent。<br>一條流水線，從 Release 到正式環境 —— 不限專案，不限使用者，不限目標。

<br>

[![CI](https://github.com/SolarifyDev/Squid/actions/workflows/tests.yml/badge.svg)](https://github.com/SolarifyDev/Squid/actions/workflows/tests.yml)
[![Release](https://img.shields.io/github/v/release/SolarifyDev/Squid?sort=semver&label=release&color=0F766E)](https://github.com/SolarifyDev/Squid/releases)
[![Platforms](https://img.shields.io/badge/platform-Linux%20%C2%B7%20Windows%20%C2%B7%20Docker-0B1F3A)](#-快速開始)
[![Kubernetes](https://img.shields.io/badge/Kubernetes-native-326CE5?logo=kubernetes&logoColor=white)](#-快速開始)
[![.NET 9](https://img.shields.io/badge/.NET-9-512BD4?logo=dotnet&logoColor=white)](https://dotnet.microsoft.com)
[![License: MIT](https://img.shields.io/badge/license-MIT-0F766E)](#-授權條款)

[English](README.md) · **繁體中文**

<br>

[![快速開始](https://img.shields.io/badge/%E5%BF%AB%E9%80%9F%E9%96%8B%E5%A7%8B-Docker-0B1F3A?style=for-the-badge&logo=docker&logoColor=white)](#-快速開始)
&nbsp;
[![下載](https://img.shields.io/badge/%E4%B8%8B%E8%BC%89-Tentacle%20%C2%B7%20Agent-0F766E?style=for-the-badge&logo=github&logoColor=white)](https://github.com/SolarifyDev/Squid/releases)

<br>

<picture>
  <source media="(prefers-color-scheme: dark)" srcset="branding/readme/orbit-zh-tw-dark.svg">
  <img src="branding/readme/orbit-zh-tw-light.svg" alt="一條流水線，涵蓋每一個目標：Kubernetes、Linux、Windows、SSH 與 AI Agent" width="100%">
</picture>

</div>

---

<picture>
  <source media="(prefers-color-scheme: dark)" srcset="branding/readme/features-zh-tw-dark.svg">
  <img src="branding/readme/features-zh-tw-light.svg" alt="為團隊打造的企業級發布能力：免費自架、開箱即用、統一發布模型、雲原生、企業級治理、可擴充" width="100%">
</picture>

<br><br>

<picture>
  <source media="(prefers-color-scheme: dark)" srcset="branding/readme/free-zh-tw-dark.svg">
  <img src="branding/readme/free-zh-tw-light.svg" alt="永久免費。MIT 授權開源，全部功能可用；專案、使用者與部署目標均不限，商業發布平台通常按方案訂閱計費。" width="100%">
</picture>

<br>

## 🚀 快速開始

**執行 Server**（需要 PostgreSQL 與 Redis）

```bash
docker build -f Dockerfile.Api -t squid-api .
docker run --rm -p 8080:8080 -p 10943:10943 squid-api
```

**連接部署目標**

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

> 正式環境請透過環境變數或金鑰管理系統注入資料庫、憑證與加密金鑰。

## 🔄 從 Octopus 遷移

上傳 Octopus 匯出檔，預覽、驗證後一鍵匯入專案、環境、變數與部署流程。敏感值不會從匯出檔還原，需在 Squid 中重新填寫。

## 📄 授權條款

[MIT](LICENSE) © Squid contributors.
