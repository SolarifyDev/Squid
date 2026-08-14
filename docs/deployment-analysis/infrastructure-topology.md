# Squid 部署 — 基礎設施層與網路拓撲分析

> **角色**：Infrastructure & network topology analyst
> **資料來源**：`deploy/`、`docker/`、`Dockerfile.*`、`.github/workflows/`、`docs/`、`src/Squid.*/appsettings*.json`、`src/Squid.Core/Settings/`、`src/Squid.Tentacle/Configuration/`
> **產出日期**：2026-08-14
> **標記慣例**：每條主張標注證據路徑；repo 中無直接證據者標 `【推斷】` 並說明推斷依據。

---

## 0. 執行摘要（TL;DR）

Squid 是一個 **Octopus 風格的部署自動化平台**，由三類可獨立部署的元件組成：

| 元件 | 部署形態 | 主要證據 |
|---|---|---|
| **Squid API Server** | 容器映像（多架構），目標環境為 **Kubernetes**（生產推斷為 Alibaba Cloud ACK） | `Dockerfile.Api`、`docs/k8s-deployment-architecture.md`、`CLAUDE.md §Kubernetes Deployment` |
| **Squid Tentacle（Agent）** | 三種形態：①K8s Helm Chart（`KubernetesAgent` flavor）②Linux 原生 systemd（.deb/.rpm）③Windows 服務（.zip + SCM） | `deploy/helm/kubernetes-agent/`、`deploy/packaging/`、`deploy/scripts/install-tentacle.{sh,ps1}` |
| **Calamari** | 部署引擎，**內嵌**於 API 與 Tentacle 映像內（`/squid/bin/` 或 `/usr/local/bin/`），不獨立部署 | `Dockerfile.Api`、`Dockerfile.Tentacle` |

**核心網路拓撲**：Server 與 Tentacle 之間透過 **Halibut**（自帶 mTLS 的二進位 L4 RPC 協議）通訊。Tentacle 有兩種連接模式：

- **Polling（輪詢）**：Tentacle 主動**撥出**到 Server 的 `:10943`（K8s Agent、防火牆後的主機）— 這是 K8s agent 的唯一模式。
- **Listening（監聽）**：Tentacle 監聽 `:10933`，Server 主動撥入（Windows/Linux 直接安裝的目標）。

Server 對外暴露**兩個獨立端點**（HTTP `:443` + Halibut TCP `:10943`），**不可混用 Ingress**，是整個網路設計的關鍵約束（`deploy/k8s-network-requirements.md`）。

**IaC 工具現況**：**未使用 Terraform / Ansible / Pulumi**。基礎設施以 Helm Chart + 手寫 K8s manifest + GitHub Actions 工作流管理，屬於「半 IaC」狀態（見 §4）。

---

## 1. 基礎設施層 — 目標部署環境

### 1.1 雲端供應商與區域

| 環境 | 供應商 | 證據 | 確定性 |
|---|---|---|---|
| **生產 API Server** | **Alibaba Cloud ACK**（Kubernetes） | `CLAUDE.md:945-956` 與 `deploy/k8s-network-requirements.md:60-100` 大量使用 `service.beta.kubernetes.io/alibaba-cloud-loadbalancer-*` annotation；明確提及 Alibaba SLB 健康檢查來源 `100.64.0.0/10`（RFC 6598，Alibaba 內部 CGNAT 範圍） | 高（生產目標） |
| **APT/RPM 套件庫** | **GitHub Pages + Cloudflare** | `docs/phase-2-apt-rpm-setup.md:13-15`：`squid.solarifyai.com` CNAME 到 `solarifydev.github.io`；`install-tentacle.sh:131`：「squid.solarifyai.com is Cloudflare-fronted」 | 高 |
| **CI/CD runners** | GitHub Actions `ubuntu-latest` / `windows-latest` | 所有 `.github/workflows/*.yml` | 高 |
| **E2E 測試叢集** | **Kind**（in-node Kubernetes） | `.github/workflows/e2e-k8s-pipeline.yml`：`helm/kind-action@v1.10.0`，`cluster_name: squid-e2e` | 高 |

**區域**：repo 無直接區域標註。`【推斷】` 依據文件以中文撰寫（`CLAUDE.md`「異常」「後端伺服器」）且針對 Alibaba ACK 的 SLB 行為有極深入的操作經驗記載，推斷生產部署在 **中國大陸區域的 Alibaba Cloud**（如 cn-hangzhou / cn-beijing）。Cloudflare 前置套件庫則為全球邊緣（`【推斷】` 為規避中國大陸對 GitHub 的連線不穩，與 `install-tentacle.sh:131`「CN/slow-region tarball download」描述一致）。

### 1.2 VM / Bare-metal vs 容器平台

| 目標類型 | 平台 | 證據 |
|---|---|---|
| API Server | **容器（K8s Pod）** | `Dockerfile.Api`（base `aspnet:9.0`，EXPOSE 8080/443/10943）；`CLAUDE.md §Kubernetes Deployment` 給出完整 Deployment + ClusterIP Service + Ingress + LoadBalancer Service manifest |
| K8s Agent Tentacle | **容器（K8s Pod，Helm 部署）** | `deploy/helm/kubernetes-agent/templates/deployment.yaml`；`values.yaml` `tentacle.flavor: "KubernetesAgent"` |
| Linux Tentacle | **VM / bare-metal（systemd 服務）** | `deploy/packaging/after-install.sh`（dpkg/rpm postinst）、`install-tentacle.sh`（下載 tarball 到 `/opt/squid-tentacle`、安裝 systemd unit、建立 `squid-tentacle` 系統使用者） |
| Windows Tentacle | **VM / bare-metal（Windows Service，SCM）** | `install-tentacle.ps1`（安裝到 `C:\Program Files\Squid Tentacle`、註冊 Windows Firewall 規則 TCP 10933）；`docs/windows-tentacle-install.md` |

**容器平台版本**：生產 K8s 版本未明確 pin；E2E 用 Kind + kubectl `v1.31.0`（`.github/workflows/e2e-k8s-pipeline.yml`）。`【推斷】` 生產叢集為 ACK 託管版（相容上游 1.28–1.31 級別）。

### 1.3 映像與套件發佈管線

| 產物 | 發佈目標 | 工作流 |
|---|---|---|
| `squid-api` 映像（amd64+arm64 多架構 manifest） | **Docker Hub**（`DOCKER_HUB/DOCKER_NAME`，vars 驅動） | `build-api-docker.yml` |
| `squid-tentacle` / `squid-watchdog` / `nfs-server` 映像 | **Docker Hub**（`SQUID_DOCKER_HUB`，`squidcd/*` repo） | `build-publish-kubernetes-agent.yml` |
| `kubernetes-agent` Helm chart | **Docker Hub OCI registry**（`oci://registry-1.docker.io/squidcd/kubernetes-agent`） | `build-publish-kubernetes-agent.yml` Step 4 |
| Linux Tentacle 原生二進位 + .deb/.rpm | **GitHub Releases** + **APT/RPM repo**（`squid.solarifyai.com`，gh-pages） | `build-publish-linux-tentacle.yml` + `publish-linux-packages.yml` |
| Windows Tentacle .zip | **GitHub Releases** | `build-publish-windows-tentacle.yml` |

所有映像均帶 provenance label `org.opencontainers.image.revision=<sha>` 並在 push 後驗證（`build-api-docker.yml` Verify 步驟）— 供應鏈完整性內建。

---

## 2. 網路拓撲 — 實例連接、Ingress/Egress、負載均衡

### 2.1 整體拓撲圖

```
                        ┌─────────────── 外部網際網路 ───────────────┐
                        │                                              │
   操作者瀏覽器 ──HTTPS:443──► [Ingress(nginx) + cert-manager/Let's Encrypt]
   Tentacle register CLI ──HTTPS:7078/5078──►        │
   安裝腳本下載 ──HTTPS:443──► squid.solarifyai.com (GitHub Pages+Cloudflare)
                        │                              │
                        ▼                              ▼
   ┌──────────────────────── K8s 叢集 (ACK) ─────────────────────────┐
   │                                                                  │
   │  ClusterIP Service: squid-service:8080  ◄── Ingress 後端(HTTP)   │
   │         ▲                                                         │
   │         │                                                         │
   │  ┌──────┴──────────────────────┐    LoadBalancer Service:         │
   │  │  Squid API Pod              │    squid-halibut:10943 (TCP)     │
   │  │  - :8080  HTTP API          │ ◄── (L4 TCP passthrough, SLB)    │
   │  │  - :10943 Halibut listener │    ▲                             │
   │  │  - SelfCert (mTLS)         │    │ Halibut polling (Tentacle    │
   │  │  - HalibutRuntime         │    │   主動撥出 TLS 長連線)        │
   │  └────────────────────────────┘    │                             │
   │                                    │                             │
   │  Postgres (SquidStore)  ◄─TCP─ API │                             │
   │  Redis (cache)         ◄─TCP─ API  │                             │
   │  Seq (Serilog sink)    ◄─HTTP─ API │                             │
   └────────────────────────────────────┼─────────────────────────────┘
                                        │
            ┌───────────────────────────┴───────────────────────────┐
            │           Tentacle Agents (部署目標)                    │
            │                                                         │
   [Polling] K8s Agent Pod ──poll://─► Server:10943                   │
            │  └─ Script Pod (ephemeral, bitnami/kubectl)             │
   [Polling] Linux Tentacle (systemd) ──► Server:10943                │
   [Polling] Windows Tentacle ──► Server:10943                        │
   [Listening] Windows/Linux Tentacle ◄── Server 撥入 :10933          │
            └─ 執行 kubectl / helm / bash / pwsh / calamari           │
```

### 2.2 連接方向與協議

| 路徑 | 方向 | 協議/埠 | 證據 |
|---|---|---|---|
| 瀏覽器/UI → API | 入站 | HTTPS:443 → Ingress → ClusterIP:8080 | `k8s-network-requirements.md` Endpoints 表 |
| Tentacle → Server（Polling） | **出站** | TCP:10943 + Halibut mTLS | `TentacleSettings.ServerCommsUrl`、`deployment.yaml` env `Tentacle__ServerCommsUrl` |
| Server → Tentacle（Listening） | 入站 | TCP:10933 + Halibut mTLS | `TentacleSettings.ListeningPort=10933`、`windows-tentacle-install.md:258` |
| API → Postgres | 叢集內 | TCP:5432 | `appsettings.json` `SquidStore:ConnectionString` |
| API → Redis | 叢集內 | TCP:6379 | `appsettings.json` `RedisCacheConnectionString` |
| API → Seq | 叢集內/外 | HTTP:5341 | `appsettings.json` `Serilog:Seq:ServerUrl` |
| API → Docker Hub / K8s API | 出站 | HTTPS | `Dockerfile.Api`（pull kubectl/helm/aws-cli）；K8s agent 透過 in-cluster ServiceAccount 存取 K8s API |

### 2.3 負載均衡策略

**關鍵設計：兩個實體隔離的負載均衡器，不可合併。**（`deploy/k8s-network-requirements.md`）

| 端點 | LB 類型 | 協議層 | 原因 |
|---|---|---|---|
| Web / HTTP API | nginx-ingress（L7）→ ClusterIP Service:8080 | HTTP/1.1, HTTP/2 | 標準 L7 路由 + TLS 終止 |
| Halibut polling | **專用 `Service type=LoadBalancer`** → pod:10943 | **L4 TCP passthrough** | Halibut 是 L4 二進位協議 + 自帶 mTLS；nginx-ingress 無 L4 passthrough 能力；混用會導致 per-cloud annotation 脆弱耦合 |

**Halibut LB 的 Alibaba SLB 專屬 annotation**（`k8s-network-requirements.md:85-100`）：
- `alibaba-cloud-loadbalancer-protocol-port: "tcp:10943"` — 強制 TCP listener（CCM 可能誤預設 HTTP，誤判 Halibut 二進位協議後立即 RST）
- `alibaba-cloud-loadbalancer-health-check-type: "tcp"` — 強制 TCP 健康檢查（預設 HTTP GET 打到 Halibut pod → 永久「異常」）
- `alibaba-cloud-loadbalancer-persistence-timeout: "3600"` — 長連線（Tentacle 保持 TCP 開啟直到 Server 有工作）
- `alibaba-cloud-loadbalancer-id`（可選）— pin 特定 SLB 以穩定公網 IP

**K8s Agent 內部無 LB**：`deployment.yaml` `replicas: 1` + `strategy: Recreate`（單實例，避免多實例搶同一 workspace PVC）。Script Pod 由 agent 動態建立（ephemeral），透過 `script-pdb.yaml`（PodDisruptionBudget）保護。

### 2.4 Egress 路徑

- **API Server egress**：Docker Hub（映像 pull 於建置期）、K8s API（in-cluster）、Postgres/Redis/Seq（叢集內或同 VPC）。
- **Tentacle egress（Polling）**：撥出到 `ServerCommsUrl:10943`；可選支援 **出站 HTTP CONNECT proxy**（`TentacleSettings.Proxy`，覆蓋 Squid/Zscaler/BlueCoat 企業代理 — `TentacleSettings.cs` ProxySettings 註解）。
- **Script Pod egress**：可選 `httpProxy`/`httpsProxy`/`noProxy`（`values.yaml` `scriptPod.proxy`），供 K8s API 與外部 registry 使用。

---

## 3. 配置管理 — 執行時配置如何管理與部署

### 3.1 三層配置來源（ASP.NET Core 標準疊加）

Squid 遵循 .NET 配置疊加順序（後者覆蓋前者）：

```
appsettings.json  ──►  環境特定 appsettings  ──►  環境變數（__ 分隔）  ──►  CLI args
```

**關鍵證據**：helm `deployment.yaml` 大量使用 `Tentacle__*` / `Kubernetes__*` env var 覆蓋（雙底線 = `:`層級分隔），即環境變數優先於 appsettings。

### 3.2 各元件配置載體

#### API Server
| 設定 | 載體 | 證據 |
|---|---|---|
| 基礎配置 | `src/Squid.Api/appsettings.json`（已提交） | 含 `SquidStore`、`RedisCacheConnectionString`、`Serilog:Seq`、`SelfCert`、`Authentication:Jwt`、`Security:VariableEncryption`、`Halibut:Polling`、`ServerUrl` |
| 開發覆蓋 | `appsettings.Development.json` | 僅 Logging level |
| **生產敏感值** | **環境變數 / K8s Secret / Secret store**（未提交） | `appsettings.json` 明確標註：「REQUIRED in production: set via deployment config / env / secret store」 |

**已提交的開發用敏感值（⚠️ 僅 dev，勿用於 prod）**：
- `SelfCert.Base64` + `Password: "squid"` — Halibut 自簽憑證（開發用）
- `Jwt:SymmetricKey: "change-this-squid-dev-key..."` — JWT 簽章金鑰
- `SquidStore:Password: "123456"` — Postgres 密碼
- `Security:VariableEncryption:MasterKey: ""` — 故意留空，強制生產自行注入

**生產注入機制**（`【推斷】` + 證據）：依 `CLAUDE.md §Kubernetes Deployment` 的 ConfigMap env var 模式 `ServerUrl__CommsUrl`、`k8s-network-requirements.md` Pre-flight checklist，生產以 K8s ConfigMap（非敏感）+ Secret（敏感：MasterKey、Jwt key、DB 密碼、SelfCert）注入。**repo 內未提交生產 K8s manifest**（僅有 CLAUDE.md 中的範例片段）— 見 §4 缺口。

#### Tentacle
| 設定 | 載體 | 證據 |
|---|---|---|
| Helm 部署 | Helm `values.yaml` → ConfigMap（`configmap.yaml` 生成 `appsettings.json`）+ Secret（`secret.yaml`：bearer-token/api-key）+ env var（`deployment.yaml` 30+ 個 `Tentacle__*`） | `deploy/helm/kubernetes-agent/templates/` |
| Server 憑證 | ConfigMap `server-cert-configmap.yaml`（`serverCertificate`）或 env `Tentacle__ServerCertificate` | 用於 TLS pinning |
| Linux 原生安裝 | `register` CLI 持久化到 `/etc/squid-tentacle/instances/Default.config.json`（`install-tentacle.sh:333-340`） | API key + server thumbprint 註冊後落地 |
| Windows 安裝 | discovery file `%ProgramData%\Squid\Tentacle\install-info.json` + instance config | `windows-tentacle-install.md:90` |

### 3.3 關鍵配置鍵對照

| 用途 | API Server 鍵 | Tentacle 鍵 | 證據 |
|---|---|---|---|
| Server 對外 URL（UI/安裝腳本生成用） | `ServerUrl:ExternalUrl` | — | `ServerUrlSetting.cs` |
| Server Halibut polling URL | `ServerUrl:CommsUrl` | `Tentacle:ServerCommsUrl` | `ServerUrlSetting.cs`、`TentacleSettings.cs` |
| Halibut polling listener 埠 | `Halibut:Polling:Port` (10943) | — | `HalibutSetting.cs`、`appsettings.json` |
| Tentacle 監聽埠 | — | `Tentacle:ListeningPort` (10933) | `TentacleSettings.cs` |
| Halibut 自簽憑證 | `SelfCert:Base64` + `Password` | `Tentacle:ServerCertificate`（pin server） | `SelfCertSetting.cs` |
| 認證 | `Authentication:Jwt:SymmetricKey` | `Tentacle:BearerToken` / `ApiKey` | `appsettings.json`、`TentacleSettings.cs` |
| 變數加密金鑰 | `Security:VariableEncryption:MasterKey` | — | `appsettings.json`（strict 強制） |

### 3.4 配置強制機制（Hardening Three-Mode Enforcement）

`appsettings.json` 揭示兩處 strict-by-default 強制：
- `Security:VariableEncryption.MasterKey` — 空/過短/全零金鑰在加密服務建構時即拒絕；env `SQUID_MASTER_KEY_ENFORCEMENT=strict|warn|off` 控制模式（預設 strict）。
- Script Pod 映像 pinning — `values.yaml` 註解：`SQUID_SCRIPT_POD_IMAGE_ENFORCEMENT=strict` 可拒絕未 pin digest 的 pod 建立（`ScriptPodImageValidator.cs`）。

---

## 4. IaC 工具 — 基礎設施即代碼現況

### 4.1 明確結論：**未使用 Terraform / Ansible / Pulumi**

全 repo 搜尋 `*.tf` / `*.tfvars` / `Pulumi.yaml` / `playbook*.yml` **零結果**。grep `terraform|ansible|pulumi` 命中的 5 個檔案均為**誤報**（測試中的字串常數、CapabilityKeys 中無關的鍵名、`api-key-permissions.md` 中無關詞彙），無任何 IaC 工具使用證據。

### 4.2 實際使用的「半 IaC」工具鏈

| 層 | 工具 | 範圍 | 證據 |
|---|---|---|---|
| **K8s 應用層** | **Helm**（`deploy/helm/kubernetes-agent/`） | K8s Agent Tentacle 的 Deployment/Service/ConfigMap/Secret/RBAC/PVC/NFS/Watchdog | 24 個 template 檔 |
| **K8s 叢集層** | **手寫 manifest**（非 Helm） | API Server 的 Deployment + Ingress + LoadBalancer Service | `CLAUDE.md §Kubernetes Deployment`、`k8s-network-requirements.md` 內嵌 yaml |
| **CI/CD** | **GitHub Actions** | 映像建置/發佈、套件打包/簽章、E2E | 15 個 workflow |
| **套件** | **fpm + reprepro + createrepo**（`【推斷】`） | .deb/.rpm 打包 + APT/RPM repo 索引 | `publish-linux-packages.yml` |
| **版本** | **GitVersion** (`GitVersion.yml`, 6.x) | 語意化版本 → 映像 tag | 所有 build workflow |

### 4.3 缺口與風險（`【推斷】`）

- **叢集基礎設施（VPC、SLB、安全群組、ACK 叢集本身、DNS 記錄）無 IaC**：`k8s-network-requirements.md` 的 Pre-flight checklist（DNS A record、worker node 安全群組 `100.64.0.0/10` 規則、SLB pin）全為**人工操作清單**，無程式化定義。這是可重現性與漂移（drift）的最大風險點 — 文件本身記載的故障模式（「Service delete/recreate 後 CCM 配發新 SLB 新 IP，舊 DNS 失效」）正是缺乏 IaC pin 的症狀。
- **API Server K8s manifest 未入版控**：僅以 doc 內嵌 yaml 存在，無獨立 `deploy/` manifest 或 Helm chart for API。`【推斷】` API Server 可能透過 Octopus/Squid 自身部署（dogfooding），或手動 `kubectl apply`。
- 建議：引入 Terraform 管理叢集 + 網路資源；為 API Server 建立 Helm chart（與 kubernetes-agent chart 對稱）。

---

## 5. 服務發現機制 — 元件如何相互發現與通訊

Squid **不使用 Consul / etcd / DNS-SD 等通用服務發現**。改採**註冊式 + 輪詢式**的混合發現，核心是 Halibut 的雙向 RPC。

### 5.1 Server ↔ Tentacle 發現

| 模式 | 發現方向 | 機制 | 證據 |
|---|---|---|---|
| **Polling** | Tentacle → Server | Tentacle 啟動時讀 `ServerCommsUrl`，主動 `HalibutRuntime.Poll(poll://<subscriptionId>/, serverEndpoint)` 建立 TLS 長連線；Server 在 agent 註冊後 `Trust(thumbprint)` 加入信任清單，有工作時透過該連線派發 `StartScriptCommand` | `TentacleSettings.GetServerCommsUrls()`、`CLAUDE.md §Halibut Polling`、`HalibutTrustInitializer` |
| **Listening** | Server → Tentacle | Tentacle 監聽 `:10933`；Server 透過 `Machine.Uri`（如 `https://tentacle.example.com:10933`）+ `Thumbprint` 主動撥入 | `k8s-deployment-architecture.md:28`、`TentacleSettings.ListeningPort` |

**訂閱識別**：Polling Tentacle 以 `SubscriptionId`（`poll://{subscriptionId}/`）標識自己；Server 端 `HalibutTrustInitializer`（`IStartable`）於啟動時查詢所有 `PollingSubscriptionId != NULL AND !IsDisabled` 的 machine，對每個 thumbprint 呼叫 `Trust()`（`CLAUDE.md §Halibut Polling Infrastructure`）。

**多重 Comms 位址**：`TentacleSettings.ServerCommsAddresses` 支援逗號分隔多個 polling URL（`GetServerCommsUrls()`），供高可用 / 多 SLB 場景。`【推斷】` helm `values.yaml` `tentacle.serverCommsAddresses: []` 為此預留。

### 5.2 Server 內部元件發現

- **DI 自動註冊**：所有 `IScopedDependency` 實作由 Autofac 掃描自動發現（`CLAUDE.md §DI auto-registration`）— 非網路服務發現，而是編譯期契約發現。
- **Handler / Strategy / Renderer / Transport 發現**：以 `FirstOrDefault(CanHandle)` 在運行時按 `CommunicationStyle` / `ActionType` 解析（`ActionHandlerRegistry`、`IExecutionStrategy`、`IIntentRenderer`）。

### 5.3 Tentacle → 目標 K8s 叢集發現

- **K8s Agent**：Pod 內掛載 ServiceAccount token，以 in-cluster config 存取 K8s API（`KubernetesApiIntentRenderer` 註：「agent Pod already owns the kubeconfig context」）。
- **K8s API 直連（KubernetesApi style）**：Server 從 endpoint JSON 的 `ClusterUrl` + `DeploymentAccount`（Token/Cert）注入 kubectl context（`KubernetesApiEndpointVariableContributor` 貢獻 13+ 變數）。
- **Script Pod**：agent 動態建立 ephemeral pod（image `bitnami/kubectl`，serviceAccount `*-script`，RBAC 可限縮 namespace）執行部署腳本。

### 5.4 安裝腳本發現（Tentacle 二進位位置）

- Linux：`register`/`service install`/`upgrade` 腳本透過 `/etc/squid-tentacle` config + 安裝目錄定位二進位。
- Windows：**discovery file** `%ProgramData%\Squid\Tentacle\install-info.json`（`Schema/BinaryPath/InstallDir/Version/...`）— Server 生成的 register snippet 據此找到 binary（`windows-tentacle-install.md:90`）。

---

## 6. DNS 與域名配置

### 6.1 已確認的域名

| 域名 | 用途 | 解析目標 | 證據 |
|---|---|---|---|
| `squid.solarifyai.com` | APT/RPM 套件庫 + GPG public key + tarball 下載 | CNAME → `solarifydev.github.io`（GitHub Pages）+ Cloudflare 前置 | `docs/phase-2-apt-rpm-setup.md:13`、`install-tentacle.sh:131` |
| `#{IngressBaseDomainName}` | API Web/HTTP 入口（佔位變數） | Ingress LB IP | `CLAUDE.md:928`、`k8s-network-requirements.md:35` |
| `#{PollingBaseDomainName}` | Halibut polling 入口（佔位變數） | Halibut SLB IP | `CLAUDE.md:978`、`k8s-network-requirements.md:35` |

### 6.2 域名命名慣例（`【推斷】` 自文件範例）

`k8s-network-requirements.md` Pre-flight checklist 給出命名樣板：
- `squid-api-<env>.<domain>` → ingress IP
- `squid-polling-<env>.<domain>` → Halibut SLB IP

即**每環境兩個獨立主機名**（見 §8 跨環境差異）。

### 6.3 DNS 關鍵風險

`k8s-network-requirements.md` 故障表記載 DNS 相關失敗模式：
- Service `delete+recreate` → CCM 配發新 SLB 新公網 IP → 舊 DNS 仍指向失效 SLB。**緩解**：`alibaba-cloud-loadbalancer-id` annotation pin SLB（穩定 IP）或更新 DNS。
- `ServerUrl__CommsUrl` env var 空 → UI 生成的安裝腳本 polling URL 指向 ingress domain（錯誤）而非 LB domain。

### 6.4 Tentacle 自身主機名解析（Listening 模式）

`TentacleSettings.PublicHostNameConfiguration` 支援四種策略解析 Tentacle 對外可達主機名（對齊 Octopus `PublicHostNameConfiguration`）：
- `Custom`（用 `ListeningHostName`）
- `ComputerName`（`Dns.GetHostName`，legacy 預設）
- `FQDN`（本地反解 DNS）
- `PublicIp`（**AWS/Azure/GCE instance metadata** — `【推斷】` 暗示 Listening 模式也曾考慮雲端 VM 部署，非僅 Alibaba）

---

## 7. TLS / SSL 終止策略

Squid 有**兩條獨立的 TLS 路徑**，終止點不同，這是網路設計的第二關鍵約束。

### 7.1 兩條 TLS 路徑

| 路徑 | TLS 終止點 | 憑證來源 | 驗證 | 證據 |
|---|---|---|---|---|
| **Web / HTTP API** | **Ingress（nginx-ingress）** | cert-manager + Let's Encrypt（終止於 ingress） | 公網 CA | `k8s-network-requirements.md` Endpoints 表 |
| **Halibut polling** | **Pod 內部（Halibut 自帶 mTLS）** | **Halibut 自簽憑證**（`SelfCert.Base64`），SLB 為 **L4 TCP passthrough，不終止 TLS** | Thumbprint pinning | `k8s-network-requirements.md`、`SelfCertSetting.cs` |

### 7.2 為何 Halibut TLS 不可在 ingress/SLB 終止

`k8s-network-requirements.md` 明確論證：
- nginx-ingress 是 L7 HTTP only，**無法處理 Halibut L4 二進位協議**。
- 若 SLB listener 設為 HTTPS（非 TCP），會用自有憑證終止 TLS → Tentacle 看到的 thumbprint 不符 → 連線被拒。
- 診斷指令 `openssl s_client -connect <polling-domain>:10943` 應回傳**與 Server 自簽憑證一致的 thumbprint**；若不符代表中間層終止了 TLS。

### 7.3 Halibut 憑證管理

- **Server 端**：`SelfCertSetting.Base64`（PFX base64）+ `Password`，建構 `HalibutRuntime`（`HalibutModule`）。
  - **生產關鍵約束**：自簽憑證需透過 K8s Secret 跨 replica 共享，**不可每 pod 重新生成**（否則 rollout 時 agent 信任斷裂）— `k8s-network-requirements.md` Pre-flight checklist。
  - repo 提交的 `appsettings.json` 內含開發用憑證（⚠️ 僅 dev）。
- **Tentacle 端**：`TentacleSettings.ServerCertificate`（PEM，helm ConfigMap 或 env 注入）用於 **pin Server 憑證**（TLS pinning，防 MITM）。
- **Tentacle 自身憑證**：`/opt/squid/certs/tentacle-cert.pfx`（Linux），升級時保留（`tentacle-self-upgrade-design.md:204`：cert 在 binary 目錄外，`mv` 不影響 → thumbprint 不變 → Server 信任清單仍有效）。

### 7.4 TLS 版本

- `install-tentacle.ps1:891` 註解提及「forced TLS 1.2」（PS 5.1 WebClient 下載）— 下載通道用 TLS 1.2。
- Halibut mTLS 的協商版本由 Halibut 8.x runtime 控制（`【推斷】` 預設 TLS 1.2+）。

### 7.5 可達性探針（reachability probe）

Server 在**生成安裝腳本時**主動對 `ServerUrl__CommsUrl` 做 TCP + TLS 握手，比對觀測 thumbprint 與預期 Halibut 憑證 thumbprint，回傳 `reachable/skipped/thumbprintMatches/detail`，UI 以警告 banner 顯示 — 將「靜默 EOF 迴圈」轉為配置當下的可操作錯誤（`k8s-network-requirements.md` 最末節）。

---

## 8. 跨環境配置差異（dev / staging / prod）

### 8.1 證據基礎與推斷說明

repo **未提交明確的 dev/staging/prod 環境 manifest**（無 `values-dev.yaml` / `values-prod.yaml` / `appsettings.Production.json`）。以下差異由程式碼預設值、文件 checklist、與 CI 環境推斷。

### 8.2 差異對照

| 維度 | Dev（本機） | Staging / Prod | 證據 / 推斷 |
|---|---|---|---|
| **API Server URL** | `http://localhost:5078`（`ExternalUrl`） | `https://squid-api-<env>.<domain>` | `appsettings.json` vs `k8s-network-requirements.md` |
| **Halibut CommsUrl** | 空（`""`）→ 回退 ExternalUrl + 10943 | `https://squid-polling-<env>.<domain>:10943` | `appsettings.json` `ServerUrl:CommsUrl:""` + 文件警告「空值會導致安裝腳本指向錯誤端點」 |
| **Halibut listener** | `Port:10943, Enabled:true`（appsettings） | 同 + LoadBalancer Service 暴露 | `appsettings.json`、`PollingListenerSetting` |
| **SelfCert** | 提交的開發憑證（`Password:"squid"`） | 獨立自簽憑證，經 K8s Secret 跨 replica 共享 | `appsettings.json` vs Pre-flight checklist |
| **JWT key** | `"change-this-squid-dev-key..."` | 長隨機 secret（env/Secret） | `appsettings.json` 註解 |
| **VariableEncryption MasterKey** | 空（`warn`/`off` 模式可用於 dev） | 必填，`strict` 模式強制 | `appsettings.json` 註解 + `SQUID_MASTER_KEY_ENFORCEMENT` |
| **Postgres** | `Host=127.0.0.1;Password=123456` | 叢集內/外 Postgres，獨立密碼 | `appsettings.json` |
| **Redis** | `127.0.0.1:6379,ssl=False` | 叢集內 Redis | `appsettings.json` |
| **Seq** | `http://localhost:5341` | 叢集內/外 Seq | `appsettings.json` |
| **CORS** | `localhost:3000/5173/5174`（前端 dev port） | 生產前端域名 | `appsettings.json` `AllowableCorsOrigins` |
| **Script Pod image** | `bitnami/kubectl:latest`（warn 模式） | **pin digest** + `strict` 模式 | `values.yaml` `scriptPod.image` 註解 |
| **Tentacle health bind** | Windows: `127.0.0.1`（避防火牆提示）；Linux: `*` | K8s agent: `+`（全介面，供 kubelet probe） | `TentacleSettings.HealthCheckBindHost`、`values.yaml` `healthCheckBindHost:"+"` |
| **E2E/CI** | Kind 叢集（`squid-e2e`）+ ephemeral Postgres/Redis service container | — | `e2e-k8s-pipeline.yml` |

### 8.3 環境切換機制

- **API Server**：`ASPNETCORE_ENVIRONMENT`（`【推斷】`，標準 .NET 慣例，載入 `appsettings.{Environment}.json`）；生產敏感值靠 env var / K8s Secret 覆蓋。
- **Tentacle（Helm）**：透過 `values.yaml` override（`helm install -f values-prod.yaml` 或 `--set`）；`tentacle.serverUrl` / `serverCommsUrl` / `environments` / `roles` 為每環境必填。
- **Tentacle（原生安裝）**：`register --server <url> --environment <env> --role <role>` CLI 參數區分環境（`after-install.sh` onboarding 訊息）。

### 8.4 環境隔離的 DB 層

E2E fixture 以 `squid_e2e_{testclassname}` 隔離 DB（`CLAUDE.md §E2EFixtureBase`），每測試類獨立 DB + DbUp migration。`【推斷】` 生產多環境（staging/prod）亦以獨立 DB 實例隔離，而非 schema 隔離（與連線字串 `Database=squid` 單一庫設計一致）。

---

## 附錄 A：關鍵證據檔案索引

| 證據類型 | 路徑 |
|---|---|
| API Dockerfile | `Dockerfile.Api` |
| Tentacle Dockerfiles | `Dockerfile.Tentacle`、`Dockerfile.Tentacle.Linux`、`Dockerfile.Tentacle.Watchdog` |
| NFS server | `Dockerfile.NfsServer`、`docker/nfs-server/entrypoint.sh` |
| Helm chart | `deploy/helm/kubernetes-agent/`（values.yaml + 24 templates） |
| K8s 網路權威文件 | `deploy/k8s-network-requirements.md` |
| K8s 部署架構 | `docs/k8s-deployment-architecture.md`、`CLAUDE.md §Kubernetes Deployment` |
| 安裝腳本 | `deploy/scripts/install-tentacle.{sh,ps1}` |
| 套件打包 | `deploy/packaging/{after-install,before-uninstall}.sh`、`squid-tentacle.repo` |
| docker-compose（Linux tentacle 範例） | `deploy/docker/linux-tentacle/docker-compose.yml` |
| CI/CD workflows | `.github/workflows/build-api-docker.yml`、`build-publish-kubernetes-agent.yml`、`build-publish-{linux,windows}-tentacle.yml`、`publish-linux-packages.yml`、`e2e-k8s-pipeline.yml` |
| 配置（API） | `src/Squid.Api/appsettings.json`、`appsettings.Development.json` |
| 配置（Tentacle） | `src/Squid.Tentacle/appsettings.json`、`src/Squid.Tentacle/Configuration/TentacleSettings.cs` |
| 設定類 | `src/Squid.Core/Settings/{Server,SelfCert,Halibut}/*.cs` |
| Windows 安裝指南 | `docs/windows-tentacle-install.md` |
| APT/RPM 設定 | `docs/phase-2-apt-rpm-setup.md` |
| 升級設計（含憑證保留） | `docs/tentacle-self-upgrade-design.md` |

## 附錄 B：推斷清單（無直接 repo 證據）

1. 生產區域為中國大陸 Alibaba Cloud（依中文文件深度 + ACK SLB 操作經驗）。
2. Cloudflare 前置套件庫為規避 CN 對 GitHub 連線不穩。
3. 生產以 K8s ConfigMap + Secret 注入敏感值（依 .NET 慣例 + 文件 checklist 模式）。
4. API Server 生產 manifest 未入版控（可能 dogfooding 或手動 apply）。
5. 生產 K8s 版本為 ACK 託管（相容 1.28–1.31 級別）。
6. 多環境以獨立 DB 實例隔離。
7. 套件打包用 fpm + reprepro + createrepo（依 workflow 行為推斷，未直接見工具名）。
8. Listening 模式曾考慮 AWS/Azure/GCE VM 部署（依 `PublicIp` 策略註解提及三雲 metadata）。
