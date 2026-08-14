# 部署腳本與 K8s 配置分析 — `deploy/` 目錄

> 分析範圍：`deploy/` 目錄下所有部署腳本與配置（helm chart、install scripts、packaging hooks、docker-compose、網路需求文件）。
> 分析者角色：Deploy scripts & K8s analyst。
> 產出日期：2026-08-14。

`deploy/` 目錄的整體職責是**部署 Squid Tentacle（agent）**，而非部署 Squid API server 本身。Squid API server 的 K8s manifest 在 `CLAUDE.md` 與 `deploy/k8s-network-requirements.md` 中以文件/範例形式描述（Deployment + ClusterIP Service + Ingress + Halibut LoadBalancer），但並未以實際 manifest 檔案存在於本目錄。目錄結構如下：

```
deploy/
├── helm/kubernetes-agent/        # Helm chart — K8s agent（polling tentacle on K8s）
├── scripts/                      # install-tentacle.{sh,ps1} + e2e 升級測試
├── packaging/                    # deb/rpm 包裝的 maintainer scripts + repo 檔
├── docker/linux-tentacle/        # docker-compose（Linux tentacle 容器）
└── k8s-network-requirements.md   # API server 雙端點網路需求文件
```

---

## 1. 部署腳本的執行流程與目標環境

`deploy/` 內共三類部署腳本，針對三種不同的目標環境與安裝模式：

### 1.1 `scripts/install-tentacle.sh` — Linux 主機安裝器

**目標環境**：裸機 / VM 上的 Linux 主機（glibc 與 musl 皆支援），架構 x64 / arm64。

**執行流程**：

1. **參數解析**：`--version`、`--install-dir`（預設 `/opt/squid-tentacle`），`TENTACLE_VERSION` 環境變數預設 `latest`。
2. **架構偵測**：`uname -m` → `x64` / `arm64`。
3. **libc 偵測**（三階段，D2/1.6.0）：環境變數 `SQUID_LIBC` → `ldd --version` grep musl → `/lib/ld-musl-*` 檔案存在性 fallback。決定 RID 為 `linux-musl-x64` 或 `linux-x64`。
4. **權限檢查**：預設安裝目錄需 root。
5. **安裝執行期相依**：`libicu` + `ca-certificates`（.NET 9 self-contained 需要），偵測 apt/dnf/yum/apk。
6. **套件管理器優先安裝**（`VERSION=latest` 時）：先嘗試 Squid 自有 APT/RPM repo（`squid.solarifyai.com/apt|/rpm`），較 GitHub 直連快 5-10 倍；失敗則 fallback 至 tarball。
7. **Tarball fallback**：從 `DOWNLOAD_BASE`（GitHub Releases）下載 `squid-tentacle-{RID}.tar.gz`，具 `--retry 3 --retry-all-errors`、`--connect-timeout 15`、`--max-time 300`。
8. **Blue-green 版本化佈局**：解壓至 staging → 執行 binary `version` 取得實際版本 → 移至 `versions/<v>/` → 原子性 `current` symlink 切換（`mv -T`）。版本失敗 fallback 至 flat 佈局。
9. **PATH 暴露**：`/usr/local/bin/squid-tentacle` symlink。
10. **建立專屬系統使用者** `squid-tentacle`（non-login, nologin），供 systemd unit 非 root 執行。
11. **建立目錄**：`/etc/squid-tentacle/instances`（config, 0700）、`/squid/work`（workspace）、`/var/lib/squid-tentacle`（升級狀態 + rollback snapshot），chown 給 service user。
12. **自動設定 APT/RPM repo**（冪等）：下載 GPG key、寫入 sources list、為 Squid host 設定 `Acquire::http::Proxy::<host> "DIRECT"` 以繞過透明代理。
13. **安裝 sudoers 規則**（`/etc/sudoers.d/squid-tentacle-upgrade`）：極窄的 NOPASSWD 規則，僅允許 `systemd-run --scope`、套件名釘死為 `squid-tentacle` 的 `apt-get/dnf/yum install`、狀態檔寫入、`dpkg -i --force-downgrade` rollback。產生後以 `visudo -c` 驗證才安裝。
14. **驗證 binary 可執行**（`squid-tentacle help`）。
15. **列印下一步**：`register` + `service install` 命令。

**下游消費**：安裝完成後由 operator 執行 `squid-tentacle register --server ... --api-key ...` 與 `squid-tentacle service install`（不在本腳本範圍）。

### 1.2 `scripts/install-tentacle.ps1` — Windows 主機安裝器

**目標環境**：Windows 主機，win-x64 / win-arm64，PS 5.1+。

**執行流程**：

1. **參數**：`-Version`、`-InstallDir`（預設 `C:\Program Files\Squid Tentacle`）、`-DownloadBase`、`-ServiceName`、`-NoServiceInstall`、`-NoAutoElevate`。
2. **架構偵測**：`$env:PROCESSOR_ARCHITECTURE` → `win-x64` / `win-arm64`（不支援 32-bit）。
3. **UAC 自動提權**：若需寫入 `%ProgramFiles%` 且非 admin → 重啟自身於提權 shell。支援 file-invocation 與 pipe-invocation（`irm | iex`）兩種模式；後者將腳本體物化至 `%TEMP%` 再重啟。`Test-IsAdministrator` / `Invoke-UacRelaunch` 可被測試 harness 覆寫。
4. **下載 URL 解析**：`latest` 走 `/latest/download/` redirect；特定版本嘗試 plain tag 再嘗試 `v` 前綴。
5. **下載**：使用 `System.Net.WebClient`（非 `Invoke-WebRequest`，因 PS 5.1 後者對大檔會截斷）+ 強制 TLS 1.2 + `Invoke-WithRetry`（3 次線性 backoff）+ 預設憑證走代理。
6. **PK magic byte 驗證**：`0x50 0x4B` 確認為 zip，避免錯誤頁 HTTP 200 被當成 zip。
7. **Blue-green 版本化佈局**：解壓至 staging → binary `version` → `versions\<v>\` → `current` junction 切換（junction 免提權；`[Directory]::Delete($false)` 僅刪 reparse point 不刪目標）。版本失敗 fallback flat。
8. **Discovery file**：`%ProgramData%\Squid\Tentacle\install-info.json`（Schema=1），記錄 BinaryPath/InstallDir/Version/Architecture/InstalledAt/InstalledBy/ServiceName。供 server 端生成的 register snippet 自動定位 binary，無需硬編碼路徑。
9. **Windows Firewall 規則**：Listening tentacle 開 TCP `10933` inbound（`Add-ListeningFirewallRule`，冪等）。
10. **服務安裝**（除非 `-NoServiceInstall`）：`Squid.Tentacle.exe service install --instance Default --service-name $ServiceName`。
11. **列印 register / service status 下一步**。

### 1.3 `scripts/e2e-upgrade-test.sh` — E2E 升級驗證腳本

**目標環境**：本地 Docker（systemd-in-docker），驗證 tentacle 升級流程。

**執行流程**（6 步）：

1. 啟動 systemd container（`jrei/systemd-*` 映像，支援 rocky-9 / almalinux-9 / fedora-40 / ubuntu-20.04/22.04/24.04 / debian-12）。
2. 安裝 prerequisites（curl + sudo + gnupg）。
3. 執行 `install-tentacle.sh`（裝 latest）。
4. 視需要 downgrade 至 `START_VERSION`（從 GitHub Releases 直連 `.deb`/`.rpm`，`dpkg -i --force-downgrade` / `rpm -U --oldpackage`）。
5. `register` + `service install` + `systemctl start`，驗證 polling 連線。
6. 等待 operator 在 UI 點 Upgrade（最多 10 分鐘輪詢 `/var/lib/squid-tentacle/last-upgrade.json` 的 `status`）。
7. 斷言最終版本、`status=SUCCESS`、`installMethod`、`schemaVersion>=2`、`startedAt` 存在。

**環境變數**：`SQUID_SERVER_URL`、`SQUID_POLLING_URL`、`SQUID_API_KEY`、`SQUID_SERVER_THUMBPRINT`（皆必填），`SQUID_SPACE_ID`、`SQUID_TEST_ROLE`、`SQUID_TEST_ENVIRONMENT`、`SQUID_KEEP_CONTAINER`。

### 1.4 目標環境總覽

| 腳本 | 目標環境 | 安裝模式 | 產出 |
|---|---|---|---|
| `install-tentacle.sh` | Linux 裸機/VM（glibc + musl, x64/arm64） | APT/RPM repo 優先 → tarball blue-green | systemd tentacle（待 register） |
| `install-tentacle.ps1` | Windows（win-x64/arm64） | GitHub Releases zip → junction blue-green | Windows Service（含 firewall + discovery file） |
| `e2e-upgrade-test.sh` | Docker systemd container | 完整 install→register→upgrade 驗證 | pass/fail + status-file 斷言 |

---

## 2. Kubernetes Manifests（Helm chart）

部署 K8s 用的所有 manifest 都以 **Helm chart** 形式存在於 `deploy/helm/kubernetes-agent/`（chart 版本 `0.2.0`, appVersion `1.0.0`）。**沒有獨立的裸 YAML manifest 檔案** — chart 即唯一 K8s 部署來源。此 chart 部署的是 **Squid Kubernetes Agent（一種在 K8s 內執行的 polling tentacle）**，會自行建立 ephemeral "script pod" 來執行部署腳本（kubectl/helm/bash）。

### 2.1 資源清單

| 模板 | 資源類型 | 角色 | 條件 |
|---|---|---|---|
| `deployment.yaml` | Deployment | Tentacle agent pod（含 nfs-watchdog sidecar） | 恆有 |
| `secret.yaml` | Secret (Opaque) | `bearer-token`（+ 選用 `api-key`） | 恆有 |
| `configmap.yaml` | ConfigMap | `appsettings.json` 子集 | 恆有 |
| `server-cert-configmap.yaml` | ConfigMap | `server-cert.pem` | 僅 `tentacle.serverCertificate` 設定時 |
| `serviceaccount-tentacle.yaml` | ServiceAccount | agent pod 用 | 恆有 |
| `serviceaccount-script.yaml` | ServiceAccount | script pod 用 | 恆有 |
| `clusterrole-tentacle.yaml` | ClusterRole | agent 管理 pod/secrets/configmap | 恆有 |
| `clusterrolebinding-tentacle.yaml` | ClusterRoleBinding | 綁定 tentacle SA | 恆有 |
| `clusterrole-script.yaml` | ClusterRole | script pod 預設**全叢集 cluster-admin 等價** | 預設（非 namespaced 模式） |
| `clusterrolebinding-script.yaml` | ClusterRoleBinding | 綁定 script SA | 同上 |
| `role-script.yaml` | Role | namespaced 模式的 script 權限 | 僅 `scriptPod.rbac.useNamespacedRoles=true` |
| `rolebinding-script.yaml` | RoleBinding | 綁定 script SA 至目標 namespace | 同上 |
| `pv.yaml` | PersistentVolume | NFS CSI / 外部 NFS PV | 內建 NFS 或外部 NFS 模式 |
| `pvc.yaml` | PersistentVolumeClaim | workspace PVC | 恆有 |
| `nfs-statefulset.yaml` | StatefulSet | 內建 NFS server | 僅內建 NFS 模式 |
| `nfs-service.yaml` | Service (headless) | NFS server DNS | 僅內建 NFS 模式 |
| `nfs-backing-pvc.yaml` | PVC | NFS server 持久後盾 | 內建 NFS + `backingVolume.storageClassName` |
| `script-pdb.yaml` | PodDisruptionBudget | script pod `maxUnavailable: 0` | `scriptPod.disruptionBudgetEnabled` |

### 2.2 Deployment 細節（`deployment.yaml`）

- **`replicas: 1`**，**`strategy: Recreate`**（stateful agent，不滾動）。
- **容器 `kubernetes-agent`**：image `squidcd/squid-tentacle:{tag|appVersion}`，port `health:8080`。
- **環境變數**：大量 `Tentacle__*`（ServerUrl/ServerCommsUrl/PollingConnectionCount/Roles/Flavor/MachineName/SubscriptionId/WorkspacePath/CertsPath/...）與 `Kubernetes__*`（Namespace/ScriptPodImage/ScriptPodServiceAccount/Timeout/資源 limits/PersistenceAccessMode/...）。敏感值（`BearerToken`、選用 `ApiKey`）透過 `secretKeyRef` 注入。
- **Volume mounts**：`workspace`（→ PVC `/squid/work`）、`certs`（→ emptyDir `/squid/certs`）。
- **Sidecar `nfs-watchdog`**（條件：`watchdog.enabled` 且非 custom PVC）：監看 `/squid/work`（readOnly），修復 NFS stale handle。env `WATCHDOG_*`。
- **儲存模式三選一**（由 `_helpers.tpl` 的 `useCustomPvc` / `useBuiltinNfs` / `useExternalNfs` 判定）：
  - Custom PVC（`storageClassName`/`volumeName` 設定）
  - 內建 NFS（無 custom PVC 且無外部 NFS server → 部署 StatefulSet + headless Service + CSI PV）
  - 外部 NFS（無 custom PVC 但有 `nfs.server` → PV 用 `nfs` 來源）

### 2.3 注意：API server 的 K8s manifest 不在此目錄

`CLAUDE.md` 與 `k8s-network-requirements.md` 描述了 Squid API server 的 K8s 部署（Deployment + ClusterIP Service + Ingress + Halibut LoadBalancer Service），但這些**僅以文件/範例形式存在**，並未實作為 chart 或 manifest 檔。API server 的部署 manifest 目前**缺失**（見第 4 節與建議）。

---

## 3. Helm chart 的 values 配置與模板分析

### 3.1 `values.yaml` 結構（140 行，四大區塊）

| 區塊 | 關鍵預設值 | 說明 |
|---|---|---|
| `tentacle` | image `squidcd/squid-tentacle` tag="" pullPolicy `IfNotPresent`；`pollingConnectionCount: 5`；`flavor: KubernetesAgent`；roles `["k8s"]`；`spaceId: 1`；`chartRef: oci://registry-1.docker.io/squidcd/kubernetes-agent` | agent 連線與註冊設定 |
| `tentacle.resources` | requests mem 256Mi / cpu 100m；limits mem 512Mi / cpu 500m | agent pod 資源 |
| `tentacle.healthCheckPort` | 8080 | in-pod 健康檢查 server |
| `tentacle.healthCheckBindHost` | `+`（所有介面） | 讓 kubelet httpGet 可達 |
| `tentacle.startupProbe` | failureThreshold 100, periodSeconds 1 | 啟動探針 |
| `tentacle.terminationGracePeriodSeconds` | 600 | 長 polling 連線寬限 |
| `kubernetes.namespace` | `default` | 部署目標 namespace |
| `scriptPod` | image `bitnami/kubectl:latest`；cpuRequest 25m / memRequest 100Mi / cpuLimit 500m / memLimit 512Mi；timeout 1800s；pendingTimeout 5min；isolationMutexTimeout 30min | ephemeral 執行腳本 pod |
| `scriptPod.rbac` | `useNamespacedRoles: false`；clusterRole 全開（`apiGroups: ["*"]`, `resources: ["*"]`, `verbs: ["*"]`） | **預設 cluster-admin 等價** |
| `workspace` | size 10Gi；accessModes `ReadWriteMany`；nfs mountOptions（nfsvers=4.1, soft, timeo=50...）；內建 NFS image `squidcd/nfs-server` | 共享工作區儲存 |
| `watchdog` | enabled true；image `squidcd/squid-watchdog`；resources mem 32-64Mi / cpu 10-50m | NFS stale-handle 修復 |

### 3.2 模板邏輯要點

- **`_helpers.tpl`**：定義 name/fullname/labels/selectorLabels、tentacle-sa-name、script-sa-name、pvc-name，以及三個儲存模式判定函式 `useCustomPvc`/`useBuiltinNfs`/`useExternalNfs`。
- **`_nfs-helpers.tpl`**：內建 NFS 的 name / pv-name / serverAddress（headless service DNS）。
- **`deployment.yaml`**：env 注入邏輯含多個 `{{- if }}` 條件分支（comms addresses、apiKey、subscriptionId、proxy、watchdog），並將 `scriptPod.rbac`、儲存參數等以 env 傳給 agent binary，由 agent 動態建立 script pod。
- **`pv.yaml`**：三段式 — 內建 NFS 用 `nfs.csi.k8s.io` CSI driver；外部 NFS 用傳統 `nfs` 來源；皆帶 `claimRef` 綁定 PVC。
- **`pvc.yaml`**：依儲存模式設 `volumeName`/`storageClassName`（內建/外部 NFS 強制 `storageClassName: ""`）。
- **`configmap.yaml`**：僅放精簡 `appsettings.json`（ServerUrl/CommsUrl/Roles/Namespace/UseScriptPods/ScriptPodImage/ScriptPodTimeoutSeconds）— 與 deployment env 有部分重複，實際以 env 為主。
- **`NOTES.txt`**：安裝後提示 `kubectl get pods/logs`、script pod、驗證 registration。

### 3.3 chart 發佈管線

`build-publish-kubernetes-agent.yml` workflow（Step 4 `helm` job）：以 GitVersion 產生 `IMAGE_TAG`，`sed` 改寫 `Chart.yaml` 的 `version`/`appVersion`，`helm package` 後 `helm push ... oci://registry-1.docker.io/squidcd`。chart 與三個 Docker image（squid-tentacle、squid-watchdog、nfs-server）同版本發佈。`values.yaml` 的 `tentacle.chartRef` 預設即指向此 OCI repo。

---

## 4. 雲端特定部署 artifacts

### 4.1 Alibaba Cloud ACK（雲端特定）

`k8s-network-requirements.md` 與 `CLAUDE.md` 的「Kubernetes Deployment — Exposing Halibut Polling」章節提供 **Alibaba Cloud ACK** 專屬的 LoadBalancer Service 範例，含以下雲端 annotation：

- `service.beta.kubernetes.io/alibaba-cloud-loadbalancer-protocol-port: "tcp:10943"` — 強制 TCP listener（CCM 預設可能為 HTTP，會誤判 Halibut 二進位協定）。
- `service.beta.kubernetes.io/alibaba-cloud-loadbalancer-health-check-type: "tcp"` — 強制 TCP 健康檢查（預設 HTTP GET 會打到 Halibut pod 永久 unhealthy）。
- 健康檢查 threshold / interval annotations。
- `alibaba-cloud-loadbalancer-persistence-timeout: "3600"` — 長 polling 連線。
- `alibaba-cloud-loadbalancer-id`（選用）— 重用預先配置的 SLB 以穩定公網 IP。

**非 manifest 前置條件**（out-of-band，需 ops/雲端團隊處理）：

1. DNS A record `#{PollingBaseDomainName}` → SLB 外網 IP。
2. Worker node security group 允許 `TCP 30000-32767` 來自 `100.64.0.0/10`（Alibaba SLB 健康檢查 CGNAT 範圍，RFC 6598）— **最常見的「backend 異常」根因**。
3. ConfigMap env `ServerUrl__CommsUrl=https://#{PollingBaseDomainName}:10943`。

### 4.2 Squid 自有 APT/RPM repo（`squid.solarifyai.com`）

- `packaging/squid-tentacle.repo` — RPM repo 定義（baseurl `https://squid.solarifyai.com/rpm`，`gpgcheck=1`，`gpgkey=https://squid.solarifyai.com/public.key`，`repo_gpgcheck=1`）。
- APT repo 在 install script 中動態生成（`deb [arch=... signed-by=/etc/apt/keyrings/squid.gpg] https://squid.solarifyai.com/apt stable main`）。
- repo 託管於 GitHub Pages（`gh-pages` branch），由 `publish-linux-packages.yml` workflow 以 GPG 簽署發佈（InRelease/Release/Packages + signed repomd.xml）。

### 4.3 GitHub Releases（CI 產出存放點）

所有 install script 的 fallback 下載來源：`https://github.com/SolarifyDev/Squid/releases`。`DOWNLOAD_BASE` 可覆寫供 air-gapped 鏡像使用。

### 4.4 雲端特定 artifact 缺失

**Alibaba Cloud 的 LoadBalancer Service 範例僅以 markdown 文件形式存在，未實作為可部署 manifest 或 chart 一部分**。operator 需手動貼入 Octopus step 或 kubectl apply。建議將其模板化進 chart 或獨立 overlay（見建議）。

---

## 5. Secrets 管理方式

### 5.1 Helm chart 內（K8s agent）

- **`secret.yaml`**：`bearer-token`（base64 編碼自 `values.tentacle.bearerToken`），選用 `api-key`。`type: Opaque`。
- **注入方式**：Deployment 以 `env.valueFrom.secretKeyRef` 引用，key `bearer-token` / `api-key`。
- **server cert**：`tentacle.serverCertificate` 透過 `server-cert-configmap.yaml` 以 **ConfigMap**（明文 PEM）注入，非 Secret。註：Halibut server cert 為 self-signed，作為信任錨點而非機密，但仍建議評估改用 Secret。

### 5.2 Linux install script

- **API key / server thumbprint** 在 `register` 階段持久化至 `/etc/squid-tentacle/`（目錄 0700，service user 擁有）— 不在 install script 本身。
- **sudoers 規則**：`/etc/sudoers.d/squid-tentacle-upgrade`（440），`visudo -c` 驗證後才安裝；規則釘死套件名 `squid-tentacle` 防止 privilege escalation。
- **GPG key**：`/etc/apt/keyrings/squid.gpg`（a+r），用於驗證 APT repo 簽章。

### 5.3 Windows install script

- **discovery file** `%ProgramData%\Squid\Tentacle\install-info.json` 僅記錄路徑/版本，**不含 secret**。
- API key 同樣在後續 `register` 階段處理。

### 5.4 docker-compose

- `Tentacle__BearerToken: ${SQUID_BEARER_TOKEN}` — 透過環境變數替換，operator 需在 `.env` 或 shell 提供。**未示範 secret 來源管理**（如 Docker secret / 外部 vault）。

### 5.5 CI/CD secrets

- Docker Hub：`secrets.SQUID_DOCKER_USERNAME` / `SQUID_DOCKER_PASSWORD`。
- GPG：`secrets.SQUID_GPG_PRIVATE_KEY`（以 `bootstrap-gpg-key.yml` 產製）。
- 這些為 GitHub Actions secrets，不入庫。

### 5.6 Secrets 管理缺失與建議

- **bearer-token 以明文 base64 進 `values.yaml`** — 若 values 檔入版控會洩漏。建議改用外部 secret 管理工具（External Secrets Operator + Vault/Azure Key Bindung、Sealed Secrets、或 `--set-secret` 由 CI 注入）。
- **server cert 用 ConfigMap 而非 Secret** — 雖為信任錨，仍建議統一改 Secret。
- **無 secret 輪替機制** — bearer-token/api-key 輪替需手動 redeploy chart。

---

## 6. 網路隔離與 NetworkPolicy 配置

### 6.1 現況

**`deploy/` 目錄內沒有任何 NetworkPolicy 資源**。Helm chart 不含 NetworkPolicy 模板，`values.yaml` 亦無相關欄位。

### 6.2 既有的網路控制

- **RBAC**（替代部分隔離需求）：
  - tentacle SA 的 ClusterRole 限於 pods / pods/log / pods/status / events / secrets / configmaps / poddisruptionbudgets / namespaces 的特定 verbs。
  - script SA 預設 `useNamespacedRoles: false` → **全叢集 cluster-admin 等價**（`apiGroups: ["*"]`, `resources: ["*"]`, `verbs: ["*"]`, `nonResourceURLs: ["*"]`）。可設 `useNamespacedRoles: true` 收斂至特定 namespace。
  - **script pod PodDisruptionBudget** `maxUnavailable: 0`（非網路隔離，是排程保護）。
- **Linux host**：`install-tentacle.ps1` 開 Windows Firewall TCP 10933 inbound；Linux install script 不設防火牆（依賴 host 既有多租戶隔離）。
- **Alibaba SLB**：透過 worker node SG（`100.64.0.0/10` → NodePort 30000-32767）控制 LB 健康檢查流量，但這是雲端 LB 層而非 K8s NetworkPolicy。

### 6.3 缺失與建議

- **NetworkPolicy 完全缺失**：agent pod 與其建立的 script pod 目前無 ingress/egress 限制。建議至少補：
  - 限制 agent pod egress 僅至 server polling URL + script pod + DNS。
  - 限制 script pod egress（避免部署腳本任意外連）。
  - 限制對 agent pod health port（8080）的 ingress 僅來自 kubelet。
- **script SA 預設過度權限**：生產應強制 `useNamespacedRoles: true` 並收斂 `clusterRole.rules` 至實際所需資源，而非全開。建議 chart 預設改為 namespaced + 最小權限，以 `useNamespacedRoles: false` 為 opt-in 例外。

---

## 7. 資源配額（resource quotas / limits）

### 7.1 chart 內資源設定

| 元件 | requests | limits | 來源 |
|---|---|---|---|
| tentacle agent pod | mem 256Mi / cpu 100m | mem 512Mi / cpu 500m | `values.tentacle.resources` |
| script pod（agent 動態建立） | mem 100Mi / cpu 25m | mem 512Mi / cpu 500m | `values.scriptPod.{memory,cpu}{Request,Limit}`（以 env 傳給 agent） |
| 內建 NFS server | mem 50Mi / cpu 50m | mem 128Mi / cpu 100m | `values.workspace.nfs.resources` |
| nfs-watchdog sidecar | mem 32Mi / cpu 10m | mem 64Mi / cpu 50m | `values.watchdog.resources` |

### 7.2 namespace 層級配額

**無 `ResourceQuota` 或 `LimitRange` 資源**。chart 不部署任何 namespace 層級配額。script pod 雖有資源 limits，但 agent 建立的每個 script pod 都會消耗 namespace 配額，無上限保護（僅有 `pendingTimeoutMinutes: 5` 與 `timeoutSeconds: 1800` 控制生命週期）。

### 7.3 建議

- 補 `LimitRange` 設定 namespace 預設 request/limit，防止 agent 或 script pod 建立無 limit 的資源。
- 補 `ResourceQuota` 限制 namespace 總 script pod 數 / CPU / memory 上限，避免失控的部署腳本耗盡叢集。
- script pod 的 `cpuLimit: 500m` 對 helm upgrade 等重負載可能偏低，建議文件化調校指引。

---

## 8. 健康 / 就緒探針配置

### 8.1 Helm chart（`deployment.yaml`）— 三探針齊全

| 探針 | 類型 | 路徑/命令 | period | timeout | failureThreshold |
|---|---|---|---|---|---|
| **startupProbe** | exec | `test -f /squid/initialized` | 1s | — | 100（≈100s 啟動寬限） |
| **livenessProbe** | httpGet | `/healthz` port `health`(8080) | 30s | 5s | 3 |
| **readinessProbe** | httpGet | `/readyz` port `health`(8080) | 10s | 5s | 3 |

- `healthCheckBindHost: "+"`（所有介面）確保 kubelet httpGet 可達 — agent binary 預設綁 loopback（避 Windows Firewall prompt），K8s agent 需 opt back in。
- `healthCheckPort: 8080`。
- `terminationGracePeriodSeconds: 600` — 長寬限，容忍 polling 連線與進行中部署腳本。

### 8.2 docker-compose（`docker-compose.yml`）

```
healthcheck: curl -f http://localhost:8080/healthz, interval 30s, timeout 5s, retries 3
```

與 chart liveness 一致。

### 8.3 Dockerfile（`Dockerfile.Tentacle.Linux`）

```
HEALTHCHECK --interval=30s --timeout=5s --retries=3 CMD curl -f http://localhost:8080/healthz || exit 1
EXPOSE 10933 8080
```

### 8.4 評估

- 探針配置完善，三探針齊全且有 startup gate。
- **缺失**：chart 的 liveness/readiness `timeoutSeconds: 5s` 對健康端點合理，但**無 `startupProbe` 對應的 http 探針** — startup 用 exec 檔案存在性，與 liveness 用 httpGet 語意不同；若 `/squid/initialized` 已建立但健康 server 尚未監聽，startup 通過後 liveness 可能立即失敗。建議評估 startup 改用 httpGet 或延長 liveness initialDelaySeconds。
- Alibaba SLB 健康檢查為 TCP 層（非應用層），與 pod 探針分屬不同棧，文件已釐清。

---

## 9. 部署腳本如何消費 CI/CD 產出的 artifacts

### 9.1 CI/CD 產出物（由 `.github/workflows/` 生成）

| Workflow | 產出 artifact | 儲存位置 |
|---|---|---|
| `build-publish-linux-tentacle.yml` | Linux tarball `squid-tentacle-{RID}.tar.gz`（versioned + latest）、GitHub Release | GitHub Releases |
| `build-publish-windows-tentacle.yml` | Windows zip `squid-tentacle-{rid}.zip` | GitHub Releases |
| `publish-linux-packages.yml` | 簽署的 `.deb`/`.rpm`（amd64/arm64 + x86_64/aarch64）、APT/RPM repo metadata、`public.key` | GitHub Release attachments + `gh-pages`（squid.solarifyai.com） |
| `build-publish-kubernetes-agent.yml` | Docker images `squidcd/squid-tentacle`/`squid-watchdog`/`nfs-server`（多架構 manifest）+ Helm chart（OCI） | Docker Hub + OCI registry |
| `build-api-docker.yml` | API server Docker image | Docker Hub |

### 9.2 install scripts 的消費方式

**`install-tentacle.sh`** 的三層 fallback 優先序（與 in-UI 升級流程一致：apt → yum → tarball）：

1. **APT repo**（`squid.solarifyai.com/apt`）— 消費 `publish-linux-packages.yml` 產出的 `.deb`，僅 `VERSION=latest` 時嘗試（reprepro 僅保留單一版本）。
2. **RPM repo**（`squid.solarifyai.com/rpm`）— 消費 `.rpm`，同上。
3. **GitHub Releases tarball**（`DOWNLOAD_BASE`）— 消費 `build-publish-linux-tentacle.yml` 的 tarball，支援 `latest`（`/latest/download/` redirect）與特定版本（嘗試 plain tag 與 `v` 前綴兩種格式）。為 pin 版本的唯一可靠途徑（GitHub Releases 保留所有 tag）。

**`install-tentacle.ps1`**：直接消費 GitHub Releases zip（`Resolve-DownloadUrls`），`latest` 或特定版本（同樣兩種 tag 格式）。

**關鍵設計**：binary 解壓後執行 `version` 子命令取得**實際版本字串**，據以命名版本目錄 — 不依賴 operator 傳入的 `--version`（可能是 `latest`），確保 blue-green 佈局以具體版本為準。

### 9.3 Helm chart 的消費方式

- chart 透過 `values.tentacle.image.tag`（預設空 → fallback `Chart.AppVersion`）消費 `build-publish-kubernetes-agent.yml` 推送的 Docker image tag。
- chart 本身經由同 workflow 的 `helm push` 至 OCI registry（`oci://registry-1.docker.io/squidcd/kubernetes-agent`），`values.tentacle.chartRef` 預設指向此處 — agent 可在 in-UI 升級時 pull 新版 chart 自我升級。
- `scriptPod.image: bitnami/kubectl:latest` 消費**外部** public image（非自有 CI 產出），且明確標註為**未 pin digest 的風險**（`ScriptPodImageValidator` Warn 模式；生產建議 pin `@sha256:` 並設 `SQUID_SCRIPT_POD_IMAGE_ENFORCEMENT=strict`）。

### 9.4 packaging hooks 的角色

`packaging/after-install.sh`（postinst）與 `before-uninstall.sh`（prerm）為 **deb/rpm 包裝內嵌的 maintainer scripts**，由 `publish-linux-packages.yml` 打包進 `.deb`/`.rpm`。它們不在 install 時直接被 install-tentacle.sh 呼叫，而是當 operator 透過 `apt`/`dnf` 安裝套件時由套件管理器觸發：

- **`after-install.sh`**：偵測 fresh install vs upgrade（dpkg `configure` + old version）；upgrade 僅列印「binary 已 staged，請 restart」訊息；**刻意不 `systemctl restart`**（避免 Phase 1 自-殺 bug — running process 持有 binary file open，仍跑舊碼；重啟由 in-UI 升級流程在 detached systemd scope 內觸發）。版本偵測用 `version` 子命令（非 `--version`）+ `</dev/null` + `timeout 5` 三層防 hang。
- **`before-uninstall.sh`**：刻意 no-op（避免升級時短暫停機；service 解除安裝由 operator 顯式 `service --stop --uninstall` + `delete-instance`）。

### 9.5 artifact 消費鏈總圖

```
CI (build-publish-*) ─┬─ GitHub Releases (tarball/zip/deb/rpm)
                      ├─ squid.solarifyai.com (APT/RPM repo, gh-pages)
                      └─ Docker Hub / OCI (images + helm chart)
                              │
install-tentacle.sh ── apt/yum repo → tarball fallback ──┐
install-tentacle.ps1 ── GitHub Releases zip ─────────────┤
helm install ── Docker Hub image + OCI chart ────────────┤
                                                         ↓
                            註冊後的 Tentacle agent（Linux/Windows/K8s）
                              ↓ in-UI upgrade
                            chartRef OCI → agent 自我升級（K8s agent）
                            upgrade-linux-tentacle.sh → apt/yum/tarball（Linux host）
```

---

## 10. 缺失總結與建議

| # | 缺失項目 | 嚴重度 | 建議 |
|---|---|---|---|
| 1 | **API server 的 K8s manifest 未實作** — 僅以 markdown 範例存在於 `CLAUDE.md`/`k8s-network-requirements.md` | 高 | 建立 `helm/squid-api/` chart 或獨立 manifest，涵蓋 Deployment + ClusterIP Service + Ingress + Halibut LoadBalancer Service + ConfigMap（含 `ServerUrl__CommsUrl`）+ Secret（Halibut cert）。 |
| 2 | **NetworkPolicy 完全缺失** | 高 | chart 補 NetworkPolicy 模板（agent 與 script pod 的 ingress/egress 限制），values 提供開關與 CIDR 配置。 |
| 3 | **script SA 預設 cluster-admin 等價** | 高 | 預設改 `useNamespacedRoles: true` + 最小權限 rules；全開改為 opt-in 例外。 |
| 4 | **bearer-token 明文 base64 進 values** | 中 | 改用 External Secrets Operator / Sealed Secrets / CI `--set-secret`；評估 server cert 改 Secret。 |
| 5 | **無 ResourceQuota / LimitRange** | 中 | chart 補 `ResourceQuota` + `LimitRange` 模板（可選開關），限制 script pod 總量。 |
| 6 | **Alibaba LoadBalancer manifest 僅文件化** | 中 | 模板化進 chart（含雲端 annotation + selector），或獨立 overlay chart。 |
| 7 | **`scriptPod.image` 未 pin digest** | 中 | 文件已警示；生產強制 pin `@sha256:` + `SQUID_SCRIPT_POD_IMAGE_ENFORCEMENT=strict`，建議 chart 預設改為 strict。 |
| 8 | **docker-compose 無 secret 來源管理示範** | 低 | 文件化 `SQUID_BEARER_TOKEN` 來源（.env / vault），或改用 Docker secret。 |
| 9 | **startup probe 與 liveness 語意不一致**（exec 檔案 vs httpGet） | 低 | 評估 startup 改 httpGet 或補 liveness initialDelay。 |
| 10 | **無 secret 輪替機制** | 低 | 文件化 bearer-token/api-key 輪替流程。 |
| 11 | **API server 雙端點部署無自動化驗證** | 低 | 文件已提及 server 端 `TentacleCommsUrlProbe` 安裝時探測；可補 chart 的 post-install hook 驗證 polling 端點可達。 |

### 正向觀察

- install scripts 工程品質高：blue-green 版本化、原子切換、多重 fallback、libc/arch 偵測、retry、PK 驗證、UAC 提權、sudoers 收斂 + visudo 驗證。
- chart 探針齊全（startup/liveness/readiness）、儲存三模式彈性、watchdog sidecar、PDB、資源 limits 完整。
- 升級流程設計嚴謹：detached systemd scope 避免自-殺、status-file out-of-band 回報、auto-rollback snapshot、e2e 測試覆蓋多 distro。
- artifact 消費鏈清晰，版本可追溯（GitVersion + tag pin + binary 自報版本）。
