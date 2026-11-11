<div align="center">

<picture>
  <source media="(prefers-color-scheme: dark)" srcset="branding/squid-wordmark-reverse.png">
  <img src="branding/squid-wordmark.png" alt="Squid" width="280">
</picture>

### 免费、简单、开箱即用的下一代发布平台

企业级发布能力，覆盖 Kubernetes、服务器与 AI Agent —— 一条流水线，从 Release 到生产。

<br>

[![CI](https://github.com/SolarifyDev/Squid/actions/workflows/tests.yml/badge.svg)](https://github.com/SolarifyDev/Squid/actions/workflows/tests.yml)
[![License: MIT](https://img.shields.io/badge/license-MIT-2EA44F?style=flat-square)](LICENSE)

[English](README.en.md) · **简体中文**

<br>

<img src="branding/squid-overview.svg" alt="Squid 将项目、发布、审批和部署目标编排在同一条交付流水线上" width="960">

</div>

<br>

## 特性

<table>
<tr>
<td width="50%" valign="top">

**免费自托管**<br>
不限项目、用户与部署目标数量，没有授权费用。

</td>
<td width="50%" valign="top">

**开箱即用**<br>
一个服务、一个数据库即可运行，Agent 一行命令安装。

</td>
</tr>
<tr>
<td valign="top">

**统一发布模型**<br>
项目、环境、生命周期、Channel 与 Release，一套概念贯穿所有目标。

</td>
<td valign="top">

**云原生**<br>
Kubernetes Agent 与 API 双模式，原生支持 Helm、Kustomize 与 YAML。

</td>
</tr>
<tr>
<td valign="top">

**企业级治理**<br>
RBAC、API Key、人工审批、审计事件，敏感变量加密存储与日志脱敏。

</td>
<td valign="top">

**可扩展**<br>
新目标与新动作以独立模块接入，无需改动部署流水线。

</td>
</tr>
</table>

## 完全免费

同类商业平台通常按项目、用户或目标数量阶梯计费。Squid 以 MIT 协议开源，全部功能免费，规模增长不带来额外的软件成本。

| | Squid | 商业发布平台 |
|---|:---:|:---:|
| 软件费用 | 免费 | 按档位订阅 |
| 项目 / 用户 / 目标 | 不限 | 随档位限制 |

## 部署目标

| 目标 | 连接方式 |
|---|---|
| Kubernetes Agent | 集群内 Agent 主动外连 |
| Kubernetes API | 服务端直连集群 |
| Tentacle（Linux / Windows） | Polling 或 Listening |
| SSH | 无需安装 Agent |
| AI Agent | HTTP API 调用 Agent 工具 |

## 快速开始

运行 Squid Server（需 PostgreSQL 与 Redis）：

```bash
docker build -f Dockerfile.Api -t squid-api .
docker run --rm -p 8080:8080 -p 10943:10943 squid-api
```

安装 Tentacle：

```bash
# Linux
curl -fsSL https://raw.githubusercontent.com/SolarifyDev/Squid/main/deploy/scripts/install-tentacle.sh | bash
```

```powershell
# Windows
irm https://raw.githubusercontent.com/SolarifyDev/Squid/main/deploy/scripts/install-tentacle.ps1 | iex
```

部署 Kubernetes Agent：

```bash
helm upgrade --install squid-agent deploy/helm/kubernetes-agent
```

> 生产环境请通过环境变量或密钥管理系统注入数据库、证书与加密密钥。

## 从 Octopus 迁移

上传 Octopus 导出文件，预览、校验后一键导入项目、环境、变量与部署流程。敏感值不会从导出文件恢复，需在 Squid 中重新填写。

## 文档

- [部署流水线架构](docs/deployment-pipeline-architecture.md)
- [Kubernetes 部署架构](docs/k8s-deployment-architecture.md)
- [Windows Tentacle 安装](docs/windows-tentacle-install.md)
- [API Key 与权限模型](docs/api-key-permissions.md)

## 许可证

[MIT](LICENSE)
