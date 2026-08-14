# Docker Compose 分析報告

## 概述

本文件分析 `deploy/docker/linux-tentacle/docker-compose.yml` 的完整配置，並交叉對照 `Dockerfile.*` 與 Helm deployment 模板，以揭示容器化部署的整體架構。

---

## 1. Service 定義與職責

### 1.1 唯一的 Service：`squid-tentacle`

```yaml
services:
  squid-tentacle:
    image: squidcd/squid-tentacle-linux:latest
    environment:
      Tentacle__ServerUrl: https://squid-server:7078
      Tentacle__ServerCommsUrl: https://squid-server:10943
      Tentacle__BearerToken: ${SQUID_BEARER_TOKEN}
      Tentacle__Roles: web-server,linux
      Tentacle__Environments: Production
    volumes:
      - tentacle-certs:/opt/squid/certs
      - tentacle-work:/opt/squid/work
    restart: unless-stopped
    healthcheck:
      test: ["CMD", "curl", "-f", "http://localhost:8080/healthz"]
      interval: 30s
      timeout: 5s
      retries: 3
```

**職責說明：** `squid-tentacle` 是一個 Linux Tentacle Agent 容器，負責向 Squid Server 註冊、接收部署任務，並在本地執行腳本（kubectl / helm / bash）。它是 Squid 部署管線的"遠端執行器"——Server 端負責調度，Tentacle 端負責實際的腳本執行。

**注意：** 此 compose 文件中**未包含** `squid-server` service。`squid-server` 須透過其他方式部署（Helm chart、獨立容器或主機安裝）。Compose 中的 `squid-tentacle` 依賴主機網路中的 `squid-server` 主機名（透過 `docker-compose` 預設的 bridge 網路或外部網路實現 DNS 解析）。

### 1.2 裸 YAML Volume 宣告

```yaml
volumes:
  tentacle-certs:
  tentacle-work:
```

兩個命名 volume 由 Docker Engine 管理，支援 `docker volume ls` 檢視、`docker volume inspect` 除錯，以及 `--volumes-from` 或具名 volume 的資料持久化。

---

## 2. Service 依賴關係與啟動順序

**當前狀態：** `squid-tentacle` 沒有宣告 `depends_on`。

這在實務上是**可接受的設計**，因為：

- Tentacle 是向 Server 發起 **polling 連線**（ outbound → `squid-server:10943`），而非等待 Server 推送。
- 即使 Server 尚未啟動，Tentacle 容器啟動後會不斷重試 polling，直到連線成功（`restart: unless-stopped`）。
- 依賴 `restart: unless-stopped` 機制比 `depends_on` 更可靠——`depends_on` 只確保容器啟動順序，不確保服務就緒；polling client 的重試邏輯才是真正的就緒等待。

**若需強制啟動順序**（例如搭配外部 `squid-server` service），可在外部 compose 或 `docker-compose.override.yml` 中加入：

```yaml
services:
  squid-tentacle:
    depends_on:
      squid-server:
        condition: service_healthy  # 需為 squid-server 定義 healthcheck
```

---

## 3. 網路配置與服務發現

### 3.1 網路模型

Compose 文件**未顯式宣告** `networks:`，預設使用 Docker Engine 的 `default bridge` 網路（名稱通常為 `<project>_default`）。

| 屬性 | 值 |
|------|-----|
| 網路類型 | `bridge`（預設） |
| 服務發現 | 容器間透過 service 名（`squid-server`）進行 DNS 解析 |
| 對外暴露 | `image` 為外部預建映像，無 `ports` 映射（Tentacle 使用 outbound polling） |

### 3.2 Polling 通訊路徑

```
squid-tentacle (container)
  └─ outbound HTTPS → squid-server:7078   (HTTP API, 註冊/狀態上報)
  └─ outbound HTTPS → squid-server:10943   (Halibut RPC, 腳本命令下發)
```

Tentacle 為 **polling agent**——主動向外連線，因此不需要 `ports` 映射。這也是 Kubernetes Agent 部署的標準模式（pod 往外 dial server，不需 Ingress）。

### 3.3 自定義網路（建議改進）

正式環境建議显式定義網路，以增加可預測性：

```yaml
networks:
  squid-net:
    driver: bridge

services:
  squid-tentacle:
    networks:
      - squid-net
    # 若有 squid-server service 同在此 compose:
    # squid-server:
    #   networks:
    #     - squid-net
```

---

## 4. Volume Mounts：用途與資料持久化策略

### 4.1 掛載路徑對照

| Volume 名 | 容器內路徑 | 用途 | 持久化策略 |
|-----------|-----------|------|-----------|
| `tentacle-certs` | `/opt/squid/certs` | Server SSL 憑證、Tentacle 身份憑證 | Docker named volume（本地持久化） |
| `tentacle-work` | `/opt/squid/work` | 腳本工作區、Calamari 執行暫存 | Docker named volume（本地持久化） |

### 4.2 資料流向

```
tentacle-certs (/opt/squid/certs)
  ├─ Server thumbprint 快取
  ├─ Tentacle 身份私鑰
  └─ 生命周期: 跨重啟保留（重註冊時無需重新產出）

tentacle-work (/opt/squid/work)
  ├─ 腳本 staging 目錄
  ├─ Calamari 輸出日誌
  └─ 生命周期: 跨重啟保留（日誌歸檔）
```

### 4.3 與 Helm Deployment 的路徑差異（重要）

| 部署方式 | Certs 路徑 | Work 路徑 | Certs 存儲 | Work 存儲 |
|---------|-----------|-----------|-----------|-----------|
| **Docker Compose** | `/opt/squid/certs` | `/opt/squid/work` | Docker named volume | Docker named volume |
| **Helm (K8s)** | `/squid/certs` | `/squid/work` | `emptyDir: {}`（Pod 生命週期） | PVC（跨 Pod 重啟保留） |

**差異原因：**
- **Helm**: Kubernetes pod 可能漂移至不同節點。`emptyDir` certs volume 在 pod 調度時自動重新掛載（無狀態）；實際憑證持久化由 Kubernetes Secret 或其他機制處理。
- **Docker Compose**: 容器固定在同節點，named volume 自然跨重啟保留。

**遷移警告：** 若從 Compose 遷移至 Helm，須注意 `/opt/squid/certs` → `/squid/certs` 的路徑變化。Helm values 中可透過 `tentacle.certsPath` 或環境變數 `Tentacle__CertsPath` 覆寫（見下文交叉引用章節）。

---

## 5. 環境變數與 Secrets 傳遞方式

### 5.1 環境變數矩陣

| 變數名 | 值 | 敏感性 | 說明 |
|-------|-----|-------|------|
| `Tentacle__ServerUrl` | `https://squid-server:7078` | 低 | Squid Server HTTP API 端點 |
| `Tentacle__ServerCommsUrl` | `https://squid-server:10943` | 低 | Halibut polling 端點 |
| `Tentacle__BearerToken` | `${SQUID_BEARER_TOKEN}` | **高** | 認證令牌，從主機 shell 環境注入 |
| `Tentacle__Roles` | `web-server,linux` | 低 | Tentacle 角色標籤（對應 Server 端 Deployment Target） |
| `Tentacle__Environments` | `Production` | 低 | 所屬環境 |

### 5.2 Secrets 傳遞模式

**當前做法：** `${SQUID_BEARER_TOKEN}` 從**主機 shell 環境變數**注入。

```bash
# 主機執行
export SQUID_BEARER_TOKEN="your-secret-token"
docker-compose up -d
```

**風險與改進建議：**

1. **不建議將 secrets 直接寫入 compose 文件**（已遵守）。
2. **建議改用 Docker Compose `env_file`**：
   ```yaml
   env_file:
     - ./secrets.env   # 644 權限，含 SQUID_BEARER_TOKEN=...
   ```
3. **正式環境建議使用 Docker Secrets 或外部 secret manager**（Vault、KMS）。
4. **Kubernetes 部署（Helm）** 使用 `secretKeyRef` 引用 Kubernetes Secret：
   ```yaml
   - name: Tentacle__BearerToken
     valueFrom:
       secretKeyRef:
         name: {{ include "kubernetes-agent.fullname" . }}-secret
         key: bearer-token
   ```

### 5.3 與 Helm 環境變數對照

Helm deployment 傳遞更多變數（見 `deploy/helm/kubernetes-agent/templates/deployment.yaml`），以下是關鍵對照：

| Helm 變數 | Compose 對應 | 說明 |
|-----------|-------------|------|
| `Tentacle__ServerUrl` | ✅ 已定義 | |
| `Tentacle__ServerCommsUrl` | ✅ 已定義 | |
| `Tentacle__BearerToken` | ✅ 已定義 | |
| `Tentacle__Flavor` | ❌ 未定義 | Compose 預設值可能為 `Tentacle`（非 `KubernetesAgent`） |
| `Tentacle__WorkspacePath` | ❌ 未定義 | Compose 預設由 image 內 `ENV` 設定（`Dockerfile.Tentacle.Linux:24`） |
| `Tentacle__CertsPath` | ❌ 未定義 | 同上（`Dockerfile.Tentacle.Linux:25`） |
| `Tentacle__HealthCheckPort` | ❌ 未定義 | 同上（`Dockerfile.Tentacle.Linux:26`） |

**推論：** Compose 省略了這些變數，因為 `Dockerfile.Tentacle.Linux` 已在 image build 時以 `ENV` 硬編碼合理預設值。Helm 则顯式傳遞所有變數，確保在 K8s 環境中的可控性。

---

## 6. 資源限制（`deploy.resources`）

**當前狀態：** `docker-compose.yml` 中**未定義** `deploy.resources`。

```yaml
# 現況——無 resources 區塊
# docker-compose spec v3.9 支援 deploy.resources，但本檔案未使用
```

**隱含風險：**
- Tentacle 容器無 CPU/記憶體上限，單一失控腳本可能耗盡主機資源。
- 無 `mem_limit`、`cpus` 等限制。

**建議補丁（寫入 `docker-compose.override.yml`）：**

```yaml
services:
  squid-tentacle:
    deploy:
      resources:
        limits:
          cpus: '1.0'
          memory: 1G
        reservations:
          cpus: '0.25'
          memory: 256M
```

**對照 Helm values.yaml（`deploy/helm/kubernetes-agent/values.yaml:22-28`）：**

```yaml
# Helm 有明確定義
tentacle:
  resources:
    requests:
      memory: "256Mi"
      cpu: "100m"
    limits:
      memory: "512Mi"
      cpu: "500m"
```

Compose 應同步採用類似限制。

---

## 7. 健康檢查配置

### 7.1 當前配置

```yaml
healthcheck:
  test: ["CMD", "curl", "-f", "http://localhost:8080/healthz"]
  interval: 30s
  timeout: 5s
  retries: 3
```

| 參數 | 值 | 說明 |
|------|-----|------|
| `test` | HTTP GET `/healthz` | Tentacle 進程在 port 8080 提供健康端點 |
| `interval` | 30s | 每 30 秒檢查一次 |
| `timeout` | 5s | 5 秒內無回應視為失敗 |
| `retries` | 3 | 連續 3 次失敗後，Docker 將容器標記為 `unhealthy` |

**健康端點由 `Dockerfile.Tentacle.Linux:26` 的 `ENV Tentacle__HealthCheckPort=8080` 觸發。**

### 7.2 與 Helm 健康探針對照

Helm deployment 使用更完整的三探針模型：

| 探針 | Helm 配置 | Docker healthcheck 對應 |
|------|---------|----------------------|
| `startupProbe` | `test -f /squid/initialized`，failureThreshold=100，periodSeconds=1 | ❌ 無（僅有 healthcheck） |
| `livenessProbe` | HTTP GET `/healthz`，period=30s，timeout=5s，failureThreshold=3 | ✅ 直接對應 |
| `readinessProbe` | HTTP GET `/readyz`，period=10s，timeout=5s，failureThreshold=3 | ❌ 無（`/readyz` 區分就緒與存活） |

**差距分析：** Docker compose 的 healthcheck 僅覆蓋 `livenessProbe`，缺少：
- **startupProbe**：Tentacle 首次啟動時完成註冊/初始化可能需時，無 startupProbe 可能導致 kubelet 過早 kill 未就緒的容器。
- **readinessProbe**：`/readyz` 區分「進程活著」與「已向 Server 註冊」；若 Tentacle 未註冊，`/healthz` 仍返回 200 但 deployment 不應接收流量。

**建議改進：** 擴展 healthcheck 邏輯或使用 `--health-on-startup` 模式（Docker 25.0+）。

---

## 8. Volume Mounts 與部署腳本的跨引用

### 8.1 安裝腳本（`deploy/scripts/install-tentacle.sh`）

安裝腳本在主機上建立以下目錄結構：

```bash
WORKSPACE_DIR="${WORKSPACE_DIR:-/squid/work}"     # 行 344
STATE_DIR="/var/lib/squid-tentacle"                # 行 353
CONFIG_DIR="/etc/squid-tentacle"                   # 行 335
INSTALL_DIR="${INSTALL_DIR:-/opt/squid-tentacle}" # 行 10
```

**對比 Docker volume mount：**

| 腳本路徑 | 用途 | Docker Compose 對應 |
|---------|------|-------------------|
| `/opt/squid-tentacle` | Tentacle 安裝根目錄 | ❌ 不在 container 內（image 自包含） |
| `/squid/work` | 腳本工作區 | `tentacle-work:/opt/squid/work` |
| `/squid/certs` | 憑證目錄 | `tentacle-certs:/opt/squid/certs` |
| `/var/lib/squid-tentacle` | 升級狀態（`last-upgrade.json`） | ❌ 不在 compose volume 中 |
| `/etc/squid-tentacle` | 實例配置 | ❌ 不在 compose volume 中 |

**重要差異：** 主機安裝腳本使用 `/squid/work`（無 `opt` 前綴），而 Docker image 使用 `/opt/squid/work`。這是因為：
- **主機安裝**：使用 `INSTALL_DIR=/opt/squid-tentacle`，工作區在 `/squid/work`（與 `INSTALL_DIR` 分離）。
- **Docker 容器**：整個 `/opt/squid` 結構都在 container 內，`work` 和 `certs` 是相對於 `/opt/squid` 的子目錄。

### 8.2 Helm Deployment 對照

Helm deployment 的 volume mount 定義（`deploy/helm/kubernetes-agent/templates/deployment.yaml:179-215`）：

```yaml
volumeMounts:
- name: workspace
  mountPath: /squid/work
- name: certs
  mountPath: /squid/certs
volumes:
- name: workspace
  persistentVolumeClaim:
    claimName: {{ include "kubernetes-agent.pvc-name" . }}
- name: certs
  emptyDir: {}     # 重要：Helm 使用 emptyDir，Compose 使用 named volume
```

**路徑映射總結：**

| 部署方式 | Certs 容器路徑 | Work 容器路徑 | Work 存儲後端 |
|---------|--------------|-------------|-------------|
| Docker Compose | `/opt/squid/certs` | `/opt/squid/work` | Docker named volume |
| Helm K8s | `/squid/certs` | `/squid/work` | PVC (RWX, NFS/EBStorage) |
| 主機安裝 | `/opt/squid/certs` | `/squid/work` | 主機檔案系統 |

### 8.3 NFS Server 附屬配置

`docker/nfs-server/entrypoint.sh` 提供一個用於 K8s workspace 的 NFS server：

```bash
SHARED_DIRECTORY=${SHARED_DIRECTORY:-/squid-workspace}  # 行 4
mkdir -p "$SHARED_DIRECTORY"
chmod 0777 "$SHARED_DIRECTORY"
echo "$SHARED_DIRECTORY *(rw,sync,no_subtree_check,no_root_squash,fsid=0)" > /etc/exports
```

**用途：** 為 Kubernetes workspace volume 提供 `ReadWriteMany` 存取模式（多個 Pod 同時掛載）。`no_root_squash` 允許 K8s Pod 中的非 root 程序寫入 NFS share。

**與 Compose 的關聯：** 此 NFS server **不在** `docker-compose.yml` 中，屬於 Helm deployment 的可選依賴（透過 `workspace.nfs.server` values 啟用）。

---

## 9. Docker Compose Service 與 Dockerfiles 的跨引用

### 9.1 映像名稱解析

```
docker-compose.yml:
  image: squidcd/squid-tentacle-linux:latest

  ↓ 對應的 source Dockerfile

Dockerfile.Tentacle.Linux  (source/  root)
  FROM mcr.microsoft.com/dotnet/runtime:9.0
  ENV Tentacle__Flavor=LinuxTentacle
  ENV Tentacle__WorkspacePath=/opt/squid/work
  ENV Tentacle__CertsPath=/opt/squid/certs
  ENV Tentacle__HealthCheckPort=8080
  EXPOSE 10933 8080
  ENTRYPOINT ["dotnet", "Squid.Tentacle.dll"]
```

**CI/CD 推論：** `Dockerfile.Tentacle.Linux` 應由 GitHub Actions 或類似 CI 系統構建並推送至 `docker.io/squidcd/squid-tentacle-linux`。標籤 `latest` 對應每次 main 分支的成功構建。

### 9.2 四個 Dockerfile 對照表

| Dockerfile | Base Image | 目標 Runtime | 特殊工具 | 用途 |
|-----------|-----------|-------------|---------|------|
| `Dockerfile.Api` | `mcr.microsoft.com/dotnet/aspnet:9.0` | Squid Server（HTTP API） | kubectl, helm, aws cli, PowerShell | K8s API Server 端 |
| `Dockerfile.Tentacle` | `mcr.microsoft.com/dotnet/runtime:9.0` | Windows Tentacle | kubectl, helm | Windows 主機部署（通用，無 ENV 預設） |
| `Dockerfile.Tentacle.Linux` | `mcr.microsoft.com/dotnet/runtime:9.0` | Linux Tentacle（Docker/K8s） | Calamari（chmod +x） | Docker 容器、Helm chart（ENV 預設 `LinuxTentacle`） |
| `Dockerfile.Tentacle.Watchdog` | `mcr.microsoft.com/dotnet/runtime:9.0-alpine` | NFS 掛載 watchdog | 無（純 .NET） | K8s NFS workspace watchdog（在 `/squid/work` 消失時重啟） |
| `Dockerfile.NfsServer` | `alpine:3.19` | NFSv4 共享存儲 | `nfs-utils`, `rpcbind` | K8s workspace volume provider |

### 9.3 Dockerfile.Tentacle.Linux → Docker Compose 映射

```
Dockerfile.Tentacle.Linux 關鍵 ENV（image-level defaults）
─────────────────────────────────────────────────────────────
ENV Tentacle__Flavor=LinuxTentacle     ←→  compose 未覆寫（使用預設）
ENV Tentacle__WorkspacePath=/opt/squid/work  ←→  volumes: tentacle-work:/opt/squid/work ✅
ENV Tentacle__CertsPath=/opt/squid/certs    ←→  volumes: tentacle-certs:/opt/squid/certs ✅
ENV Tentacle__HealthCheckPort=8080     ←→  healthcheck test → http://localhost:8080/healthz ✅
```

**覆寫鏈：** `Dockerfile ENV`（預設值）< `compose environment:`（顯式覆寫）< `Helm env:`（最高優先）

### 9.4 API Dockerfile 的額外工具棧

`Dockerfile.Api` 在編譯階段安裝額外工具（不影響 runtime image 層大小），這些工具在容器執行期間被調用：

| 工具 | 版本策略 | 用途 |
|------|---------|------|
| `kubectl` | `stable.txt`（最新穩定版） | Kubernetes 部署腳本 |
| `helm` | GitHub Releases latest | Helm chart 部署 |
| `aws` CLI v2 | 官方 installer | AWS ECR / S3 操作 |
| `dotnet-script` | `dotnet tool install -g` | C# 腳本執行 |
| `pwsh` (PowerShell) | GitHub Releases latest | Windows 相容腳本 |

---

## 10. 架構總圖

```
┌─────────────────────────────────────────────────────────────┐
│                    Docker Host                              │
│                                                             │
│  ┌──────────────────────────────────────────────────────┐   │
│  │           squid-tentacle container                   │   │
│  │                                                     │   │
│  │  /opt/squid/certs  ←── tentacle-certs (named vol)   │   │
│  │  /opt/squid/work   ←── tentacle-work  (named vol)    │   │
│  │                                                     │   │
│  │  ┌──────────────────────────────────────────────┐   │   │
│  │  │  Squid.Tentacle.dll (dotnet)                │   │   │
│  │  │  Health endpoint: http://:8080/healthz       │   │   │
│  │  └──────────────────────────────────────────────┘   │   │
│  │                          │                          │   │
│  │        outbound HTTPS ────┼──→ squid-server:7078      │   │
│  │        outbound HTTPS ────┼──→ squid-server:10943     │   │
│  └──────────────────────────────────────────────────────┘   │
│                                                             │
│  SQUID_BEARER_TOKEN=${SQUID_BEARER_TOKEN} (host env)        │
└─────────────────────────────────────────────────────────────┘
                              ▲
                              │ same bridge network or external DNS
                              │
┌─────────────────────────────────────────────────────────────┐
│              squid-server (外部部署)                         │
│  (Helm / 主機 / 另一容器)                                   │
│  HTTP :7078  ←─ API 註冊                                    │
│  Halibut :10943 ←─ 腳本命令下發                              │
└─────────────────────────────────────────────────────────────┘
```

---

## 11. 發現的缺口與改進建議

| # | 缺口 | 嚴重性 | 建議 |
|---|------|--------|------|
| 1 | **無 `squid-server` service** — compose 假設 server 已存在 | 中 | 提供 `docker-compose.override.yml.example` 或獨立的 `server-compose.yml` |
| 2 | **無網路定義** — 使用隱式 default bridge | 低 | 显式定義 `squid-net` bridge 網路 |
| 3 | **無資源限制** — `deploy.resources` 未定義 | 中 | 加入 CPU/memory limits（參考 Helm values） |
| 4 | **缺少 startup/readinessProbe** — 僅有 liveness 等效 | 中 | 添加 startupProbe（等待 `/squid/initialized`）和 `/readyz` readiness |
| 5 | **Secrets 僅靠主機 env 注入** — 無 Docker Secrets | 中 | 改用 `env_file` 或 Docker Secrets |
| 6 | **`last-upgrade.json` 路徑未持久化** — 與主機安裝腳本不一致 | 低 | 考慮將 `/var/lib/squid-tentacle` 也 mount 為 volume |
| 7 | **`latest` tag** — 不可重現部署 | 高 | 改用固定版本 tag（如 `${TENTACLE_VERSION:-latest}`）並在 CI 中設定 |

---

## 12. 附錄：完整路徑對照表

### 12.1 所有相關源文件

| 文件 | 角色 |
|------|------|
| `deploy/docker/linux-tentacle/docker-compose.yml` | 本文件核心分析對象 |
| `Dockerfile.Tentacle.Linux` | Tentacle Linux 容器映像定義 |
| `Dockerfile.Tentacle` | Tentacle Windows 容器映像定義 |
| `Dockerfile.Tentacle.Watchdog` | NFS watchdog 映像定義 |
| `Dockerfile.NfsServer` | NFS server 映像定義 |
| `Dockerfile.Api` | Squid API Server 映像定義 |
| `docker/nfs-server/entrypoint.sh` | NFS server 入口腳本 |
| `deploy/scripts/install-tentacle.sh` | 主機 Linux 安裝腳本 |
| `deploy/scripts/install-tentacle.ps1` | 主機 Windows 安裝腳本 |
| `deploy/helm/kubernetes-agent/templates/deployment.yaml` | K8s Tentacle Deployment 模板 |
| `deploy/helm/kubernetes-agent/values.yaml` | Helm default values |
| `docs/k8s-deployment-architecture.md` | K8s 部署架構文檔 |

### 12.2 環境變數完整清單（對比所有部署方式）

| 變數 | Compose | Helm | 主機腳本 | 說明 |
|------|---------|------|---------|------|
| `Tentacle__ServerUrl` | ✅ | ✅ | ✅ | Server API URL |
| `Tentacle__ServerCommsUrl` | ✅ | ✅ | ✅ | Halibut polling URL |
| `Tentacle__BearerToken` | ✅ env `${}` | ✅ secret | ✅ | 認證令牌 |
| `Tentacle__Roles` | ✅ | ✅ | ❌ | 角色標籤 |
| `Tentacle__Environments` | ✅ | ✅ | ❌ | 環境名 |
| `Tentacle__Flavor` | ❌ (default) | ✅ | ❌ | Tentacle 類型 |
| `Tentacle__WorkspacePath` | ❌ (ENV) | ✅ | ✅ env var | 工作區路徑 |
| `Tentacle__CertsPath` | ❌ (ENV) | ✅ | ✅ env var | 憑證路徑 |
| `Tentacle__HealthCheckPort` | ❌ (ENV) | ✅ | ❌ | 健康端 口 |
| `Tentacle__HealthCheckBindHost` | ❌ | ✅ | ❌ | 監聽介面 |
| `Tentacle__MachineName` | ❌ | ✅ | ❌ | 機器名 |
| `Tentacle__PollingConnectionCount` | ❌ | ✅ | ❌ | Polling 連線數 |
| `Tentacle__SubscriptionId` | ❌ | ✅ | ❌ | K8s Agent subscription ID |
| `Kubernetes__*` | ❌ | ✅ | ❌ | K8s 特有環境變數 |
| `WATCHDOG_*` | ❌ | ✅ | ❌ | Watchdog 配置 |
