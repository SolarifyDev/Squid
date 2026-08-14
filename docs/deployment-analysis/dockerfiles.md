# Dockerfile 分析報告

> 範圍：`Dockerfile.Api`、`Dockerfile.NfsServer`、`Dockerfile.Tentacle`、`Dockerfile.Tentacle.Linux`、`Dockerfile.Tentacle.Watchdog`
> 分析日期：2026-08-14
> 對應 CI/CD workflows：`build-api-docker.yml`、`build-publish-linux-tentacle.yml`、`build-publish-kubernetes-agent.yml`

---

## 1. CI/CD 關聯總覽

下表整理每個 Dockerfile 在 CI/CD pipeline 中被建置的對應關係。所有建置皆使用 `docker/build-push-action@v6`、以 `amd64` + `arm64` 雙架構 matrix 推送到 Docker Hub，並以 `provenance: false` 推送（後續有獨立的 provenance 驗證步驟）。

| Dockerfile | CI/CD Workflow | 推送映像名稱 | 觸發條件 | 備註 |
|---|---|---|---|---|
| `Dockerfile.Api` | `build-api-docker.yml` | `{DOCKER_HUB}/{DOCKER_NAME}:<tag>-{platform}` | `tags: ['*']`（所有 tag 推送） | arm64 需先 qemu 模擬；推送後有「Verify pushed image provenance」步驟 |
| `Dockerfile.Tentacle.Linux` | `build-publish-linux-tentacle.yml` | `{DOCKER_HUB}/squid-tentacle-linux:<tag>-{platform}` | `tags: ['*']` | 同上；同 workflow 另有 tarball 打包任務（非 Docker） |
| `Dockerfile.Tentacle` | `build-publish-kubernetes-agent.yml` | `{DOCKER_HUB}/squid-tentacle:<tag>-{platform}` | `tags: ['*']` | Kubernetes Agent（Halibut polling tentacle）生產映像 |
| `Dockerfile.Tentacle.Watchdog` | `build-publish-kubernetes-agent.yml` | `{DOCKER_HUB}/squid-watchdog:<tag>-{platform}` | `tags: ['*']` | 與 Tentacle 同 workflow 內的獨立 job |
| `Dockerfile.NfsServer` | `build-publish-kubernetes-agent.yml` | `{DOCKER_HUB}/nfs-server:<tag>-{platform}` | `tags: ['*']` | 與 Tentacle 同 workflow 內的獨立 job |

**共用 CI 特性：**

- **認證**：`build-api-docker.yml` 使用 `secrets.DOCKER_USERNAME` / `DOCKER_PASSWORD`；其餘三個 workflow 使用 `secrets.SQUID_DOCKER_USERNAME` / `SQUID_DOCKER_PASSWORD`（兩組帳密分離）。
- **建置前清理**：每個 build job 前皆執行 `docker builder prune --all --force`（容忍失敗），這會**抹除 BuildKit 層快取**，使每次建置近乎冷啟動（詳見 §8 的快取策略影響）。
- **多架構**：matrix `platform: [amd64, arm64]`；arm64 透過 QEMU 模擬執行（`if: matrix.platform == 'arm64'` 設定 emulation）。Dockerfile 內透過 `ARG TARGETARCH` + `$BUILDPLATFORM` 跨平台編譯。
- **Provenance**：所有映像 `provenance: false`（關閉 buildx 預設的 provenance/SBOM 產出），但 workflow 另有「Verify pushed image provenance」步驟以 label 形式驗證（commit SHA 已 bake 進 image label）。
- **無 `cache-from` / `cache-to`**：所有 workflow 皆未設定 GitHub Actions cache 或 registry cache，僅依賴 runner 本地 BuildKit 快取（但會被 prune 抹除）。

---

## 2. Dockerfile.Api

> 來源：`Dockerfile.Api`（68 行） · CI：`build-api-docker.yml`

### (1) 基礎映像選擇原因與安全性考量

- **Build stage**：`mcr.microsoft.com/dotnet/sdk:9.0`（Debian-based）。選用 SDK 映像因需 `dotnet publish`、`dotnet tool install`、`curl`、`unzip` 等建置工具。
- **Runtime stage**：`mcr.microsoft.com/dotnet/aspnet:9.0`。使用官方 ASP.NET runtime（已內含 patch 與 runtime），較自建 base 安全。
- **安全性風險**：
  - Runtime stage 額外 `apt-get install python3`，擴大攻擊面（python3 帶入 stdlib 與潛在相依套件）。理由應為支援 Calamari 腳本執行，但未註明。
  - **複製 SDK 進 runtime**：`COPY --from=build /usr/share/dotnet/sdk /usr/share/dotnet/sdk` 與 `COPY --from=build /root/.dotnet/tools /root/.dotnet/tools`，等同把整個 .NET SDK 與 global tools 帶進 production runtime 映像。這**大幅膨脹映像**且將編譯器/工具鏈暴露於生產環境，違反最小權限原則——攻擊者一旦 RCE 即可在容器內編譯任意程式碼。應評估是否真的需要在 runtime 執行 C# scripting（`dotnet-script`）；若需要，宜改用更小的 scripting runtime 或將 SDK 獨立為 sidecar。
  - 外部下載（kubectl/helm/aws-cli/pwsh）來源為 `dl.k8s.io`、`api.github.com`、`awscli.amazonaws.com`、`github.com`，**未校驗 checksum/signature**（僅 `curl -fsSL` 下載後直接 `chmod +x`）。供應鏈風險：若任一來源被劫持或 MITM，可植入惡意二進位。建議 pin 版本 + SHA256 驗證。
  - 版本浮動：kubectl 用 `stable.txt`、helm/pwsh 用 `releases/latest`，導致**不可重現建置**——同 tag 映像在不同時間建置內容不同，違反供應鏈可追溯性。建議 pin 至具體版本。

### (2) Multi-stage build 使用方式與效益

- 兩階段：`build`（sdk:9.0）→ runtime（aspnet:9.0）。
- **效益部分失效**：理論上 runtime stage 只應含 publish 產物，但此處將 SDK、tools、kubectl、helm、aws-cli、pwsh 全部 `COPY --from=build` 進 runtime，使得 multi-stage 的「瘦化身」效益**幾乎歸零**——runtime 映像體積接近 build stage。
- 正面效益：build 工具（unzip、curl、apt cache）未帶入 runtime（`rm -rf /var/lib/apt/lists/*` 已清理）。
- Calamari 以 `--self-contained` 發佈（帶自身 runtime），獨立於主 app runtime。

### (3) 暴露的端口與攻擊面

```
EXPOSE 8080   # HTTP API
EXPOSE 443    # HTTPS（但 ENTRYPOINT 未實際 listen 443，疑為 legacy/ingress 提示）
EXPOSE 10943  # Halibut polling listener（L4 TCP + mTLS）
```

- 攻擊面：8080（Web/REST）、10943（Halibut 二進位協定）。443 僅 EXPOSE 未實際 bind，可能造成誤解。
- 10943 為 Halibut 自有 mTLS，安全性依賴 thumbprint trust list（見 CLAUDE.md Halibut 段落）。應確保 pod 層級僅對必要來源開放 10943（LoadBalancer Service，見 k8s-deployment-architecture）。

### (4) 環境變數與 secrets 注入機制

- Dockerfile 僅設 `ENV PATH=...`（工具路徑），**未硬編碼任何 secret**。
- Secrets 注入全在**運行時**：K8s 透過 `envFrom` ConfigMap（`squid-configmap`）與 Secret 注入（如 `Squid.Account.Token`、DB 連線、`ServerUrl__CommsUrl`）。符合 12-factor。
- `COPY --from=build /root/.dotnet/tools` 使用 root home 目錄路徑，暗示運行時以 root 執行（見 §7）。

### (5) 健康檢查（HEALTHCHECK）配置

- **未設定 HEALTHCHECK**。依賴 K8s liveness/readiness probe（在 deployment manifest 定義）。
- 風險：若 K8s probe 未配置或配置不當，dead container 不會被重啟。建議在 Dockerfile 補 `HEALTHCHECK`（如 `curl -f http://localhost:8080/healthz`）作為底層保險，與 K8s probe 雙重保障。

### (6) 資源限制設定

- Dockerfile **未設定** `--memory` / `--cpu`（這些屬於 runtime 組態，非 Dockerfile 範疇）。
- 應在 K8s deployment `resources.requests/limits` 中設定。需確認對應 manifest 已配置（本分析未涵蓋 manifest，標記為待確認）。

### (7) 用戶權限（non-root user）

- **未設定 `USER`，以 root 執行**。`COPY --from=build /root/.dotnet/tools` 更固化了 root 依賴。
- 風險：容器內程序以 UID 0 執行，若 RCE 攻擊者取得 root 權限，結合 hostPath/privileged 即可逃逸。
- 建議：建立 non-root user（如 `app`，UID 1000），`chown` 對應目錄後 `USER app`。注意 8080 port > 1024 無需 root bind，可行。

### (8) 映像層快取策略與建置效率

- `COPY` 順序：先 `NuGet.Config` 再 `src/` 目錄。NuGet.Config 變動少 → restore 隱含在 `dotnet publish` 中，**無獨立 `dotnet restore` 層**，導致任一原始碼變動都會重跑完整 publish（含 restore）。
- 外部下載（kubectl/helm/aws/pwsh/dotnet-script）各為獨立 `RUN`，但皆依賴網路且版本浮動，**無法被層快取穩定復用**（版本變動即失效）。
- **CI 已 `docker builder prune --all --force`**：每次 CI 建置前清空 BuildKit 快取，使上述層快取在 CI 環境**完全無效**——每次皆從零下載 kubectl/helm/aws/pwsh。建議：(a) 移除或限定 prune 範圍；(b) 改用 `cache-to: type=gha` 啟用 GitHub Actions cache；(c) 將穩定外部工具下載拆為獨立 base 映像，定期重建。
- 多 `RUN` + `rm -rf` 清理 apt cache，層衛生尚可。

---

## 3. Dockerfile.Tentacle（Kubernetes Agent / 生產 Tentacle）

> 來源：`Dockerfile.Tentacle`（44 行） · CI：`build-publish-kubernetes-agent.yml`（映像 `squid-tentacle`）

### (1) 基礎映像選擇原因與安全性考量

- **Build stage**：`mcr.microsoft.com/dotnet/sdk:9.0`。
- **Runtime stage**：`mcr.microsoft.com/dotnet/runtime:9.0`（**非 aspnet**，因 Tentacle 為背景服務非 Web）。
- 風險與 Api 相同：外部下載 kubectl/helm **未校驗 checksum**、版本浮動（`stable.txt` / `releases/latest`），不可重現建置 + 供應鏈風險。
- runtime `apt-get install bash python3`——python3 同樣擴大攻擊面（供 Calamari 腳本用）。
- `COPY --from=build /usr/share/dotnet/sdk` 與 `/root/.dotnet/tools`：與 Api 同樣問題，SDK 與 dotnet-script 帶入 production，違反最小權限。

### (2) Multi-stage build 使用方式與效益

- 兩階段：`build` → `runtime:9.0`。效益部分被 SDK COPY 抵銷（同 Api）。
- Calamari `--self-contained` 獨立發佈。
- `mkdir -p /squid-dirs/squid/{work,certs,bin}` 在 build stage 建立，再 `COPY --from=build /squid-dirs/squid/ /squid/` 帶入 runtime，確保目錄結構與權限。

### (3) 暴露的端口與攻擊面

- **未 `EXPOSE` 任何端口**。Tentacle 為 **polling agent**（主動連出至 server 的 10943），不監聽入站端口，攻擊面最小化——這是正確設計。
- 風險：無 HEALTHCHECK 端口，難以探測健康（見 §5）。

### (4) 環境變數與 secrets 注入機制

- Dockerfile 僅 `ENV PATH=...`。
- Secrets（Halibut thumbprint、server URL、kubeconfig）於運行時透過 `register` CLI 寫入 `Tentacle__CertsPath` / config 檔，或 K8s Secret 掛載。Dockerfile 無硬編碼 secret。
- **注意**：此 Dockerfile **未設** `Tentacle__Flavor` / `WorkspacePath` / `CertsPath` 等 ENV（與 `Dockerfile.Tentacle.Linux` 不同），路徑靠 `/squid/` 目錄慣例與註冊流程。可能導致兩個 tentacle 映像行為不一致。

### (5) 健康檢查（HEALTHCHECK）配置

- **未設定 HEALTHCHECK**。
- polling tentacle 無監聽端口，傳統 HTTP healthcheck 不可行。建議改用進程探測或 sidecar（Watchdog 即扮演此角色，見 §6）。

### (6) 資源限制設定

- Dockerfile 未設定，依賴 K8s `resources`。待確認 manifest。

### (7) 用戶權限（non-root user）

- **未設定 `USER`，以 root 執行**。`COPY /root/.dotnet/tools` 固化 root。
- Tentacle 需執行 kubectl/helm/bash 部署腳本，可能需較高權限，但仍應以 non-root + 受控 capability 處理，而非直接 root。

### (8) 映像層快取策略與建置效率

- 同 Api：無獨立 restore 層、外部下載版本浮動、CI prune 抹除快取。建置效率低。
- `dotnet tool install -g dotnet-script` 與 kubectl/helm 下載為獨立層，但因版本浮動 + prune，CI 端無法復用。

---

## 4. Dockerfile.Tentacle.Linux（Linux Tentacle）

> 來源：`Dockerfile.Tentacle.Linux`（33 行） · CI：`build-publish-linux-tentacle.yml`（映像 `squid-tentacle-linux`）

### (1) 基礎映像選擇原因與安全性考量

- **Build stage**：`mcr.microsoft.com/dotnet/sdk:9.0`。
- **Runtime stage**：`mcr.microsoft.com/dotnet/runtime:9.0`。
- **較 Api / Tentacle 更精簡**：runtime 僅 `apt-get install bash curl`（**無 python3、無 SDK、無 kubectl/helm/aws/pwsh**）。攻擊面顯著縮小，映像體積最小。
- 安全性最佳的一個 tentacle 變體：未帶編譯器、未帶額外腳本語言 runtime。
- Calamari 仍 `--self-contained`。

### (2) Multi-stage build 使用方式與效益

- 兩階段，**真正發揮瘦身效益**：runtime stage 僅含 publish 產物 + calamari + bash/curl。無 SDK 污染。是五個 Dockerfile 中 multi-stage 落實最完整者。
- `mkdir -p /opt/squid/work /opt/squid/certs` 直接在 runtime stage 建立（非 build stage copy），簡潔。

### (3) 暴露的端口與攻擊面

```
EXPOSE 10933 8080
```

- 10933：傳統 Octopus Tentacle listening port（Halibut RPC）。
- 8080：`Tentacle__HealthCheckPort`，healthz 端點。
- 攻擊面：兩個入站端口。10933 須確保僅 server 可達（NetworkPolicy / SG）。8080 healthz 應僅限 cluster 內探測。

### (4) 環境變數與 secrets 注入機制

```
ENV Tentacle__Flavor=LinuxTentacle
ENV Tentacle__WorkspacePath=/opt/squid/work
ENV Tentacle__CertsPath=/opt/squid/certs
ENV Tentacle__HealthCheckPort=8080
```

- 明確的組態 ENV，符合 12-factor。`Tentacle__` 雙底線為 .NET config section 對應。
- 無 secret 硬編碼；secrets 透過註冊流程 / K8s Secret 注入。
- 與 `Dockerfile.Tentacle` 對比：此變體 ENV 完整，路徑用 `/opt/squid/`（Tentacle 用 `/squid/`），**兩者不一致**，可能造成運維混淆。

### (5) 健康檢查（HEALTHCHECK）配置

```
HEALTHCHECK --interval=30s --timeout=5s --retries=3 \
    CMD curl -f http://localhost:8080/healthz || exit 1
```

- **五個 Dockerfile 中唯一設定 HEALTHCHECK 者**。`curl` 已在 runtime 安裝，可執行。
- 間隔 30s、timeout 5s、3 次重試，合理預設。`start-period` 未設，建議補上以避免啟動期間誤判。
- 與 K8s probe 應對齊（避免 Docker HEALTHCHECK 與 K8s liveness 衝突）。

### (6) 資源限制設定

- Dockerfile 未設定，依賴 K8s `resources`。

### (7) 用戶權限（non-root user）

- **未設定 `USER`，以 root 執行**。雖映像精簡，仍以 root 運行。建議加 non-root user（目錄 `/opt/squid/` 可 chown）。

### (8) 映像層快取策略與建置效率

- 同樣無獨立 restore 層，但因外部下載少（僅 bash/curl apt），建置效率**最高**。
- CI prune 仍影響，但層數少、無網路下載，重建成本低。

---

## 5. Dockerfile.Tentacle.Watchdog

> 來源：`Dockerfile.Tentacle.Watchdog`（21 行） · CI：`build-publish-kubernetes-agent.yml`（映像 `squid-watchdog`）

### (1) 基礎映像選擇原因與安全性考量

- **Build stage**：`mcr.microsoft.com/dotnet/sdk:9.0-alpine`（Alpine 變體，較 Debian 更小）。
- **Runtime stage**：`mcr.microsoft.com/dotnet/runtime:9.0-alpine`。
- 選用 Alpine：watchdog 為輕量守護進程，最小化映像體積與攻擊面。
- 安全性最佳：無額外 apt/apk 套件、無外部二進位下載、無 SDK 帶入 runtime。純 .NET runtime + 應用產物。
- Alpine musl libc 與 .NET 兼容性需注意（9.0 已良好支援）。

### (2) Multi-stage build 使用方式與效益

- 兩階段 Alpine，**最乾淨的 multi-stage**：runtime 僅 `COPY --from=build /app .`。
- **使用 `dotnet restore` 獨立層**（先 copy csproj → restore → copy source → publish --no-restore），是五個 Dockerfile 中**唯一正確利用層快取**者：原始碼變動不會重跑 restore。

### (3) 暴露的端口與攻擊面

- **無 `EXPOSE`**。Watchdog 為背景循環進程，監控檔案系統（`WATCHDOG_DIRECTORY=/squid/work`），無網路監聽。攻擊面最小。

### (4) 環境變數與 secrets 注入機制

```
ENV WATCHDOG_DIRECTORY=/squid/work
ENV WATCHDOG_LOOP_SECONDS=5
ENV WATCHDOG_INITIAL_BACKOFF_SECONDS=0.5
ENV WATCHDOG_TIMEOUT_SECONDS=10
```

- 全為可調組態 ENV，無 secret。預設值合理（5s 輪詢、10s timeout）。
- 透過 K8s env 覆寫即可調整，無需重建映像。

### (5) 健康檢查（HEALTHCHECK）配置

- **未設定 HEALTHCHECK**。watchdog 無 HTTP 端點，傳統 healthcheck 不適用。
- 可考慮以檔案 timestamp 探測（watchdog 定期寫 heartbeat 檔），但屬應用層設計，非 Dockerfile 範疇。

### (6) 資源限制設定

- Dockerfile 未設定。watchdog 輕量，K8s 應設小額 requests。

### (7) 用戶權限（non-root user）

- **未設定 `USER`，以 root 執行**。Alpine 預設 root。watchdog 監控 `/squid/work`（可能為 tentacle 寫入目錄），需匹配目錄權限。建議與 tentacle 共用 non-root UID。

### (8) 映像層快取策略與建置效率

- **最佳**：restore 獨立層 + Alpine 小映像 + 無外部下載。原始碼變動只重建 publish 層。
- 惜 CI prune 仍抹除快取，但建置本身極快。

---

## 6. Dockerfile.NfsServer

> 來源：`Dockerfile.NfsServer`（10 行） · CI：`build-publish-kubernetes-agent.yml`（映像 `nfs-server`）

### (1) 基礎映像選擇原因與安全性考量

- **基礎映像**：`alpine:3.19`（非 `mcr.microsoft.com`）。最小化、攻擊面小。
- **`apk add nfs-utils bash`**：nfs-utils 為 NFS 伺服器核心需求；bash 供 entrypoint 腳本。
- 風險：
  - `alpine:3.19` 為 2023 釋出版本，需確認是否持續接收安全更新（3.19 支援至 2025-11，**目前已過 EOL 或接近 EOL**）。建議升級至 `alpine:3.20+`。
  - NFS 服務本身為高風險網路服務（rpcbind、mountd）。

### (2) Multi-stage build 使用方式與效益

- **無 multi-stage**（單一 stage）。NFS server 為純套件安裝，無編譯需求，無需 multi-stage。合理。

### (3) 暴露的端口與攻擊面

```
EXPOSE 2049
```

- 2049：NFS 標準端口。但 entrypoint 另啟 `rpcbind`（111）、`rpc.mountd`（隨機/20048）、`rpc.nfsd`。**僅 EXPOSE 2049 不完整**，rpcbind/mountd 端口未暴露聲明。
- 攻擊面大：NFS + rpcbind 為傳統高風險服務。應確保僅在信任網路/cluster 內暴露，嚴禁公網。

### (4) 環境變數與 secrets 注入機制

- Dockerfile 無 ENV。`entrypoint.sh` 以 `SHARED_DIRECTORY=${SHARED_DIRECTORY:-/squid-workspace}` 讀取運行時 ENV。
- 無 secret 機制。NFS export 設定 `*(rw,sync,no_subtree_check,no_root_squash,fsid=0)`——**`no_root_squash` + `*` 為重大安全風險**：允許任意客戶端以 root 身份寫入，無認證。應限制為特定 CIDR 並移除 `no_root_squash`。

### (5) 健康檢查（HEALTHCHECK）配置

- **未設定 HEALTHCHECK**。可加 `rpcinfo -p localhost | grep nfs` 或檢查 2049 監聽。

### (6) 資源限制設定

- Dockerfile 未設定。

### (7) 用戶權限（non-root user）

- **未設定 `USER`，以 root 執行**。NFS server（rpcbind、rpc.nfsd 需 privileged port 2049）**必須 root**，此處無法簡單降權。需透過 K8s `securityContext.runAsNonRoot: false` + 受控部署。屬固有需求。

### (8) 映像層快取策略與建置效率

- 簡單單層 + apk add。`apk add --no-cache` 無快取殘留。建置極快。
- 無外部二進位下載。

---

## 7. 橫向對照總表

| 分析點 | Api | Tentacle | Tentacle.Linux | Watchdog | NfsServer |
|---|---|---|---|---|---|
| **(1) 基礎映像** | sdk:9.0 → aspnet:9.0 | sdk:9.0 → runtime:9.0 | sdk:9.0 → runtime:9.0 | sdk:9.0-alpine → runtime:9.0-alpine | alpine:3.19 |
| **(2) Multi-stage** | 有但效益被 SDK COPY 抵銷 | 有但效益被 SDK COPY 抵銷 | 有，效益完整 | 有，最乾淨 | 無（無需） |
| **(3) 端口/攻擊面** | 8080/443/10943 | 無（polling） | 10933/8080 | 無 | 2049(+rpc) |
| **(4) ENV/secrets** | 僅 PATH；secret 運行時注入 | 僅 PATH | 完整 Tentacle__ ENV | WATCHDOG__ ENV | 無（entrypoint 讀 ENV） |
| **(5) HEALTHCHECK** | ❌ 無 | ❌ 無 | ✅ curl healthz | ❌ 無（不適用） | ❌ 無 |
| **(6) 資源限制** | ❌ Dockerfile 未設 | ❌ | ❌ | ❌ | ❌ |
| **(7) Non-root** | ❌ root | ❌ root | ❌ root | ❌ root | ❌ root（NFS 固有） |
| **(8) 快取策略** | 弱（無 restore 層、版本浮動、CI prune） | 弱 | 中（下載少） | **最佳**（restore 獨立層） | 簡單高效 |
| **CI workflow** | build-api-docker | build-publish-kubernetes-agent | build-publish-linux-tentacle | build-publish-kubernetes-agent | build-publish-kubernetes-agent |

---

## 8. 主要發現與改善建議（優先級排序）

### 🔴 高優先級（安全/供應鏈）

1. **外部二進位下載無 checksum 驗證**（Api、Tentacle、Tentacle.Linux 的 kubectl/helm/aws/pwsh）。建議 pin 版本 + SHA256 驗證。
2. **版本浮動**（`stable.txt` / `releases/latest`）導致不可重現建置。改為 ARG pin 具體版本。
3. **`alpine:3.19` 接近/已 EOL**（NfsServer）。升級至 3.20+。
4. **NFS export `no_root_squash` + `*`**（entrypoint.sh）為重大風險。限制 CIDR、移除 no_root_squash。
5. **全部映像以 root 執行**。除 NfsServer 固有需求外，其餘應建立 non-root user。

### 🟡 中優先級（映像瘦身/建置效率）

6. **Api 與 Tentacle 將 SDK 帶入 runtime**（`COPY /usr/share/dotnet/sdk`），膨脹映像且暴露編譯器。評估移除或拆 sidecar。
7. **CI `docker builder prune --all --force` 抹除層快取**，使外部下載每次重建。建議改用 `cache-to: type=gha` 或限定 prune 範圍。
8. **無獨立 `dotnet restore` 層**（Api、Tentacle、Tentacle.Linux），原始碼變動重跑 restore。仿效 Watchdog 的 `csproj → restore → publish --no-restore` 模式。

### 🟢 低優先級（一致性/可觀測性）

9. **HEALTHCHECK 僅 Tentacle.Linux 有**。Api 應補 HTTP healthz；其餘依應用特性設計。
10. **Tentacle 與 Tentacle.Linux ENV/路徑不一致**（`/squid/` vs `/opt/squid/`，缺 `Tentacle__Flavor`）。統一慣例。
11. **Api `EXPOSE 443` 未實際 bind**，易誤導。移除或實作。
12. **NfsServer EXPOSE 僅 2049**，rpcbind/mountd 未聲明。補齊或改用靜態端口。

---

## 附錄：分析所依據的來源檔案

- `Dockerfile.Api`、`Dockerfile.Tentacle`、`Dockerfile.Tentacle.Linux`、`Dockerfile.Tentacle.Watchdog`、`Dockerfile.NfsServer`
- `docker/nfs-server/entrypoint.sh`
- `.github/workflows/build-api-docker.yml`
- `.github/workflows/build-publish-linux-tentacle.yml`
- `.github/workflows/build-publish-kubernetes-agent.yml`
- `CLAUDE.md`（Halibut polling、K8s 雙端點架構參照）
