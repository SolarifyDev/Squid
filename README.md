<div align="center">

<picture>
  <source media="(prefers-color-scheme: dark)" srcset="branding/squid-wordmark-reverse.png">
  <img src="branding/squid-wordmark.png" alt="Squid" width="300">
</picture>

### 免费、自托管的应用与 Kubernetes 部署平台

用一套清晰的发布模型，把应用、Kubernetes、Windows Service 和脚本部署到任意目标。
项目、用户与部署目标不设软件收费上限。自托管、可扩展、对 Octopus 用户友好。

<br>

[![CI](https://github.com/SolarifyDev/Squid/actions/workflows/tests.yml/badge.svg)](https://github.com/SolarifyDev/Squid/actions/workflows/tests.yml)
[![.NET](https://img.shields.io/badge/.NET-9.0-512BD4?style=flat-square&logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/)
[![PostgreSQL](https://img.shields.io/badge/PostgreSQL-required-4169E1?style=flat-square&logo=postgresql&logoColor=white)](https://www.postgresql.org/)
[![Deploy](https://img.shields.io/badge/deploy-Kubernetes%20%C2%B7%20Linux%20%C2%B7%20Windows-0F766E?style=flat-square)](#-支持的部署目标)
[![Octopus Import](https://img.shields.io/badge/Octopus-import%20supported-2F81F7?style=flat-square)](#-从-octopus-迁移)
[![License: MIT](https://img.shields.io/badge/license-MIT-2EA44F?style=flat-square)](LICENSE)

[English](README.en.md) · **简体中文**

<br>

[![开始部署](https://img.shields.io/badge/开始部署-Quick%20Start-0F766E?style=for-the-badge&logo=rocket&logoColor=white)](#-快速开始)
&nbsp;
[![迁移 Octopus](https://img.shields.io/badge/迁移-Octopus%20Project-2F81F7?style=for-the-badge&logo=octopusdeploy&logoColor=white)](#-从-octopus-迁移)

<br>

<img src="branding/squid-overview.svg" alt="Squid 将项目、发布、审批和部署目标编排在同一条交付流水线上" width="1000">

</div>

---

**Squid** 为交付团队提供熟悉但更开放的部署体验。定义项目与变量，创建 Release，编排审批、脚本、包和 Kubernetes 操作，再发布到 Agent、Tentacle、SSH 或自定义目标。

> 🧩 **Octopus 式模型** · 🆓 **软件免费** · 🏠 **完整自托管** · ☸️ **Kubernetes 原生** · 🔐 **敏感值保护** · 📜 **审计与权限**

## ✨ 为什么选择 Squid

| | |
|---|---|
| 🆓 **免费自托管** | 不按项目、用户席位或部署目标数量收取软件费用。基础设施成本由部署方掌控。 |
| 🧩 **熟悉的交付模型** | Spaces、项目、环境、生命周期、Channel、Release、变量与部署历史，迁移成本更低。 |
| ☸️ **Kubernetes 原生** | 支持 Kubernetes Agent / API、kubectl、Helm、Kustomize 和原生资源编排。 |
| 🖥️ **异构目标统一发布** | Kubernetes、Windows Tentacle、Linux Tentacle、SSH 与 HTTP API 目标使用同一套流程。 |
| 🔁 **Octopus 项目导入** | 上传 Octopus 导出文件，预览兼容性、校验冲突，再事务化导入当前可部署配置。 |
| 🔐 **敏感值默认谨慎** | 导入时不恢复密钥；变量支持加密、输出捕获与日志脱敏。 |
| 🚀 **可扩展执行架构** | Transport、Intent、Action Handler 分层，新增目标与动作不需要重写部署管线。 |
| 📜 **治理能力内建** | JWT / API Key、RBAC、团队、权限范围、人工审批与审计事件。 |

> **一句话：** 保留 Octopus 用户熟悉的发布方式，同时摆脱项目数、用户数和目标节点数的阶梯计费。

---

## ⚖️ Squid vs Octopus

### 💰 收费方式

| | Squid | Octopus Deploy |
|---|---:|---:|
| 软件费用 | **免费自托管** | 按项目和版本分级收费 |
| 项目数量 | **不限制** | Free：10；Professional：20 起 |
| 用户席位 | **不限制** | Free：10；付费版不限 |
| 部署目标 / 节点 | **不限制** | 随档位与使用方式定价 |
| 公开价格参考 | — | Professional：US$4,330 / 年起 |

> Octopus 价格仅作公开资料对比，参考 `octopus.com/pricing`（2026-09-29）。实际价格与权益以 Octopus 官方页面为准。

### 🧮 功能对比

| 能力 | Squid | Octopus Deploy |
|---|:---:|:---:|
| 自托管 | Yes | Yes（Server） |
| 项目 / 环境 / 生命周期 | Yes | Yes |
| Release 与部署历史 | Yes | Yes |
| 变量作用域与敏感变量 | Yes | Yes |
| Kubernetes Agent | Yes | Yes |
| Kubernetes API | Yes | Yes |
| Helm / Kustomize / YAML | Yes | Yes |
| SSH 目标 | Yes | Yes |
| Windows Tentacle | Yes | Yes |
| Windows Service / IIS | Yes | Yes |
| 人工审批 | Yes | Yes |
| RBAC / API Key / 审计 | Yes | Yes |
| Octopus 项目导入 | Yes | Native |
| Tenant 模型 | Partial | Yes |
| Tenant 标签过滤 | No | Yes |
| 全球部署冻结 | Partial | Yes |
| SIEM 审计流 | Partial | Yes |
| 托管云服务 | No（自托管） | Yes |

---

## 🚀 核心能力

| 领域 | 能力 |
|---|---|
| 交付模型 | Spaces、项目组、项目、环境、生命周期、Channel、Release、部署历史 |
| 部署编排 | 步骤条件、并行步骤、延迟启动、目标角色、环境/Channel 过滤、超时与重试 |
| 变量系统 | 变量作用域、敏感变量、变量快照、输出变量、配置变量替换 |
| 安全与治理 | JWT / API Key、RBAC、权限范围、团队、审计事件、人工审批 |
| 执行能力 | Bash、PowerShell、Python、C#、HTTP、包部署、健康检查、回滚 |
| 云原生 | Kubernetes Agent / API、kubectl、Helm、Kustomize、原生 K8s 资源 |
| 平台 | Windows、Linux、Docker、Kubernetes |

### 🗺️ 部署流水线

```mermaid
flowchart LR
    A["项目 + 变量<br/>环境 + 生命周期"] --> B["Release"]
    B --> C{"部署编排"}
    C --> D["人工审批"]
    C --> E["脚本 / HTTP / 包"]
    C --> F["Kubernetes<br/>Helm / Kustomize / YAML"]
    C --> G["Windows Service<br/>IIS"]
    D --> H["部署目标"]
    E --> H
    F --> H
    G --> H
    H --> I["Kubernetes Agent / API"]
    H --> J["Tentacle<br/>Polling / Listening"]
    H --> K["SSH"]
    H --> L["OpenClaw"]
```

---

## 🎯 支持的部署目标

| 目标 | 通信方式 | 适合场景 | 主要能力 |
|---|---|---|---|
| Kubernetes Agent | Halibut 长连接 | 集群内执行、网络受限环境 | kubectl、Helm、Kustomize、原生资源 |
| Kubernetes API | 服务端直连 | 快速接入已有集群 | kubectl、Helm、Kustomize、原生资源 |
| Tentacle Polling | Agent 主动外连 | 内网、防火墙严格的生产环境 | 脚本、包、Windows Service、IIS |
| Tentacle Listening | 服务端主动连接 | 固定端口可达的目标 | 脚本、包、Windows Service、IIS |
| SSH | SSH / SFTP | Linux 主机、无需安装 Agent | Bash、PowerShell、Python、包上传 |
| OpenClaw | HTTP API | Agent 工具调用与自动化 | 工具、Agent、等待、断言、结果提取 |

---

## 🔄 从 Octopus 迁移

Squid 内置后端导入流程，不需要手工重建每一个项目。

```text
Upload -> Extract -> Preview -> Validate -> Confirm -> Succeeded
```

| 阶段 | 作用 |
|---|---|
| Upload | 上传 Octopus 导出文件或 JSON |
| Extract | 安全解压、识别资源、构建依赖图 |
| Preview | 展示将创建、复用、跳过或阻止的资源 |
| Validate | 检查冲突、缺失引用、凭据与目标 |
| Confirm | 在事务中创建 Squid 资源 |
| Status | 查看结果、映射与诊断 |

**当前导入重点**

- 导入当前可部署配置：项目、项目组、环境、生命周期、Channel、变量、部署流程、步骤、Feeds、部分账户、Tentacle、证书。
- 自动映射常见 Octopus Action 到 Squid Action，例如 Kubernetes Containers、Ingress、Script、Manual、IIS、Windows Service。
- 不导入 Release、Deployment、Server Task 和历史快照；它们会被识别并报告为超出范围。
- 敏感变量以空值导入并标记为待填写；Feed、账户、证书和目标密钥不会从导出文件静默恢复。
- 不支持的动作会跳过，或创建为禁用且已脱敏的占位动作。

---

## 📦 快速开始

### 运行条件

| 组件 | 要求 |
|---|---|
| Squid Server | .NET 9 / Docker |
| 数据库 | PostgreSQL |
| 后台任务与分布式锁 | Redis |
| 构建源码 | .NET SDK 9 |

### 本地运行

```bash
git clone https://github.com/SolarifyDev/Squid.git
cd Squid

# 先启动 PostgreSQL 和 Redis，再按你的环境修改：
#   SquidStore:ConnectionString
#   RedisCacheConnectionString
#   Security:VariableEncryption:MasterKey
#   SelfCert:Base64 / SelfCert:Password

dotnet run --project src/Squid.Api
```

启动后：

- API / Swagger：`https://localhost:7078/swagger`
- HTTP：`http://localhost:5078`
- Halibut Polling：`10943`

> 生产环境必须替换开发证书、JWT 密钥、数据库密码和变量加密主密钥。

### Docker

```bash
docker build -f Dockerfile.Api -t squid-api .
docker run --rm -p 8080:8080 -p 10943:10943 squid-api
```

实际部署时，请通过环境变量或密钥管理系统注入 PostgreSQL、Redis、证书和加密密钥配置。

### 安装 Tentacle

Linux：

```bash
curl -fsSL https://raw.githubusercontent.com/SolarifyDev/Squid/main/deploy/scripts/install-tentacle.sh | bash
```

Windows PowerShell：

```powershell
irm https://raw.githubusercontent.com/SolarifyDev/Squid/main/deploy/scripts/install-tentacle.ps1 | iex
```

部署 Kubernetes Agent：

```bash
helm upgrade --install squid-agent deploy/helm/kubernetes-agent
```

---

## 🏗️ 架构与项目结构

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
├── src/
│   ├── Squid.Api/                  # HTTP API、认证、控制器、Hangfire
│   ├── Squid.Core/                 # 部署领域、执行引擎、导入、持久化
│   ├── Squid.Message/              # 命令、事件、DTO、跨进程契约
│   ├── Squid.Tentacle/             # Linux / Windows Agent
│   ├── Squid.Tentacle.Watchdog/    # Kubernetes Agent 看门狗
│   └── Squid.Calamari/             # 脚本、包、配置与 K8s 执行器
├── deploy/
│   ├── helm/kubernetes-agent/      # Kubernetes Agent Helm Chart
│   ├── docker/linux-tentacle/      # Linux Tentacle Compose
│   └── scripts/                    # Linux / Windows 安装脚本
├── docs/                           # 架构、API、安装与运维文档
├── samples/                        # Windows Service / NuGet Feed 示例
└── tests/                          # Unit、Integration、E2E 测试
```

---

## 🧪 测试

```bash
dotnet test Squid.sln
```

测试覆盖领域逻辑、部署流水线、Octopus 导入、Kubernetes、SSH、Tentacle、Calamari、Windows Service 与端到端场景。

---

## 📚 文档

| 文档 | 内容 |
|---|---|
| [`docs/deployment-pipeline-architecture.md`](docs/deployment-pipeline-architecture.md) | 完整部署流水线架构 |
| [`docs/k8s-deployment-architecture.md`](docs/k8s-deployment-architecture.md) | Kubernetes 部署架构 |
| [`docs/windows-tentacle-install.md`](docs/windows-tentacle-install.md) | Windows Tentacle 安装与排障 |
| [`docs/api-key-permissions.md`](docs/api-key-permissions.md) | API Key 与权限模型 |
| [`docs/tentacle-self-upgrade-design.md`](docs/tentacle-self-upgrade-design.md) | Tentacle 自升级设计 |
| [`CLAUDE.md`](CLAUDE.md) | 开发者架构参考 |

---

## 🧭 能力边界

Squid 的目标是覆盖绝大多数应用与 Kubernetes 交付场景，而不是逐项复制 Octopus 的全部商业能力。当前主要差异：

- Tenant 模型和 Tenant 标签过滤不完整。
- Octopus 导入以“当前可部署配置”为主，不迁移历史 Release、Deployment 和 Server Task。
- 敏感值不会从 Octopus 导出文件恢复，需要在 Squid 中重新录入。
- 不提供 Squid 官方托管云，生产环境由使用者自托管。
- 全球部署冻结、SIEM 审计流等企业治理能力尚未达到 Octopus 的完整对等程度。

---

## 📄 许可证

本项目采用 [MIT License](LICENSE)。

你可以自由使用、复制、修改、合并、发布、分发、再许可和销售 Squid 的副本，但需要保留原始版权声明和许可证声明。软件按“原样”提供，不附带任何明示或默示担保。

第三方组件仍适用其各自的许可证。

## 🤝 参与贡献

欢迎提交 Issue、Pull Request、部署场景与文档改进。新增执行目标、Action 或导入映射时，请优先遵循现有 Transport、Intent、Handler、Mediator 与测试模式。

<div align="center"><sub>Built with .NET · PostgreSQL · Redis · Kubernetes · Halibut</sub></div>
