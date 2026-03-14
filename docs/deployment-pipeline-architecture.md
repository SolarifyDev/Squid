# Squid 部署流水線完整架構分析

本文檔詳細分析 Squid 中從數據準備到真正部署到 Deployment Target 的完整流程，涵蓋所有實體、服務、快照機制、通信協議和執行策略。

---

## 目錄

1. [全局架構總覽](#1-全局架構總覽)
2. [基礎數據準備](#2-基礎數據準備)
   - [Environment（環境）](#21-environment環境)
   - [Lifecycle（生命週期）](#22-lifecycle生命週期)
   - [External Feed（外部源）](#23-external-feed外部源)
   - [Deployment Account（部署帳戶）](#24-deployment-account部署帳戶)
   - [Variable Set（變量集）](#25-variable-set變量集)
   - [Project（項目）](#26-project項目)
   - [Channel（通道）](#27-channel通道)
   - [Machine（部署目標）](#28-machine部署目標)
3. [Process Step 設置與擴展](#3-process-step-設置與擴展)
   - [DeploymentProcess（部署流程）](#31-deploymentprocess部署流程)
   - [DeploymentStep（部署步驟）](#32-deploymentstep部署步驟)
   - [DeploymentAction（部署動作）](#33-deploymentaction部署動作)
   - [ActionType 與 Handler 映射](#34-actiontype-與-handler-映射)
   - [Step 條件與過濾](#35-step-條件與過濾)
   - [如何擴展新的 Step 類型](#36-如何擴展新的-step-類型)
4. [Release 創建與 Snapshot 機制](#4-release-創建與-snapshot-機制)
   - [Release 創建流程](#41-release-創建流程)
   - [Snapshot 架構原理](#42-snapshot-架構原理)
   - [Snapshot 壓縮與去重](#43-snapshot-壓縮與去重)
5. [Deployment 創建與任務調度](#5-deployment-創建與任務調度)
   - [Deployment 創建流程](#51-deployment-創建流程)
   - [ServerTask 與 Hangfire 調度](#52-servertask-與-hangfire-調度)
6. [部署執行流水線](#6-部署執行流水線)
   - [ProcessAsync 頂層管道](#61-processasync-頂層管道)
   - [Phase 1：加載部署數據](#62-phase-1加載部署數據)
   - [Phase 2：準備所有目標](#63-phase-2準備所有目標)
   - [Phase 3：執行部署步驟](#64-phase-3執行部署步驟)
7. [變量系統](#7-變量系統)
   - [變量作用域與優先級](#71-變量作用域與優先級)
   - [變量替換流水線](#72-變量替換流水線)
   - [變量序列化與加密](#73-變量序列化與加密)
   - [輸出變量捕獲](#74-輸出變量捕獲)
8. [Machine 匹配與目標查找](#8-machine-匹配與目標查找)
9. [Tentacle 通信機制](#9-tentacle-通信機制)
   - [Halibut Polling 架構](#91-halibut-polling-架構)
   - [IScriptService 服務契約](#92-iscriptservice-服務契約)
   - [KubernetesAgent 執行策略（遠程 Halibut）](#93-kubernetesagent-執行策略遠程-halibut)
   - [KubernetesApi 執行策略（本地執行）](#94-kubernetesapi-執行策略本地執行)
   - [Polling 觀察循環](#95-polling-觀察循環)
10. [Calamari 任務執行](#10-calamari-任務執行)
    - [CalamariPayload 構建](#101-calamaripayload-構建)
    - [DeployByCalamari.ps1 模板](#102-deploybycalamarips1-模板)
    - [DirectScript vs PackagedPayload](#103-directscript-vs-packagedpayload)
11. [Transport 抽象層](#11-transport-抽象層)
12. [端到端完整流程圖](#12-端到端完整流程圖)

---

## 1. 全局架構總覽

Squid 的部署流程遵循以下層次結構：

```
用戶操作層
  ├─ 創建 Environment、Lifecycle、Feed、Account、Machine (基礎設施)
  ├─ 創建 Project + VariableSet + Channel (項目配置)
  ├─ 設計 DeploymentProcess → Steps → Actions (流程定義)
  ├─ 創建 Release (快照流程+變量，選定包版本)
  └─ 創建 Deployment (觸發部署到目標環境)

後台執行層 (Hangfire)
  ├─ DeploymentTaskExecutor.ProcessAsync()
  ├─ 加載快照數據 → 準備目標 → 執行步驟
  └─ 通過 IExecutionStrategy 執行腳本

目標通信層
  ├─ KubernetesApi (本地 kubectl 執行)
  └─ KubernetesAgent (Halibut Polling RPC → 遠程 Tentacle Pod)
```

**API 層模式**：Controller → Mediator → CommandHandler → Service → DataProvider → Repository

---

## 2. 基礎數據準備

### 2.1 Environment（環境）

**實體路徑**：`src/Squid.Core/Persistence/Entities/Deployments/Environment.cs`

| 屬性 | 類型 | 說明 |
|------|------|------|
| `Id` | int | 主鍵 |
| `SpaceId` | int | 所屬空間 |
| `Name` | string | 環境名稱（如 Development、Staging、Production） |
| `Slug` | string | URL 友好標識符 |
| `SortOrder` | int | 顯示排序 |
| `UseGuidedFailure` | bool | 是否啟用引導式故障處理 |
| `AllowDynamicInfrastructure` | bool | 是否允許動態基礎設施 |

**API**：`POST /api/environments/create` → `EnvironmentController` → `EnvironmentService`

**核心關係**：
- `LifecyclePhaseEnvironment` — 將環境映射到生命週期階段
- `Machine.EnvironmentIds` — CSV 格式的環境 ID 列表
- `Deployment.EnvironmentId` — 每次部署指定單一目標環境
- `ActionEnvironment` — 將動作限定到特定環境
- `VariableScope` — 變量可按環境作用域

---

### 2.2 Lifecycle（生命週期）

**實體路徑**：`src/Squid.Core/Persistence/Entities/Deployments/Lifecycle.cs`

Lifecycle 定義了部署在不同環境之間的推進規則。

| 屬性 | 類型 | 說明 |
|------|------|------|
| `Id` | int | 主鍵 |
| `Name` | string | 生命週期名稱 |
| `ReleaseRetentionQuantity` | int | Release 保留數量 |
| `TentacleRetentionQuantity` | int | Tentacle 版本保留數量 |

**Lifecycle Phase（階段）**：

```
Lifecycle "Standard"
  ├─ Phase 1: "Development" → Environment: Dev (Automatic)
  ├─ Phase 2: "Testing" → Environment: Staging (Automatic)
  └─ Phase 3: "Production" → Environment: Production (Optional)
```

| Phase 屬性 | 說明 |
|------------|------|
| `MinimumEnvironmentsBeforePromotion` | 階段內至少成功部署 N 個環境才能推進 |
| `IsOptionalPhase` | 是否可跳過 |

**LifecyclePhaseEnvironment（階段-環境映射）**：

| 屬性 | 說明 |
|------|------|
| `PhaseId` | 階段 ID |
| `EnvironmentId` | 環境 ID |
| `TargetType` | `Automatic`（自動部署）或 `Optional`（手動部署） |

**核心邏輯**：
- `Project.LifecycleId` — 項目定義默認生命週期
- `Channel.LifecycleId` — 通道可覆蓋生命週期（nullable）
- 部署時 `ILifecycleProgressionEvaluator` 驗證是否允許部署到目標環境

---

### 2.3 External Feed（外部源）

**實體路徑**：`src/Squid.Core/Persistence/Entities/Deployments/ExternalFeed.cs`

External Feed 配置外部包源（Docker Registry、NuGet、Maven 等）。

| 屬性 | 類型 | 說明 |
|------|------|------|
| `Id` | int | 主鍵 |
| `FeedType` | string | 源類型（"Docker"、"NuGet"、"Maven"） |
| `FeedUri` | string | 完整 Feed URL |
| `RegistryPath` | string | Registry 或倉庫路徑 |
| `Username` | string | 認證用戶名 |
| `Password` | string | 加密的認證密碼 |

**在部署中的作用**：
- `DeploymentAction.FeedId` — 動作引用 Feed 進行包獲取
- `KubernetesApiEndpointVariableContributor.ContributeAdditionalVariablesAsync` — 從 Feed + Package + 版本構建 `ContainerImage` 變量
- 格式：`ContainerImage = "{feedUri}/{packageId}:{releaseVersion}"`

**API**：`POST /api/external-feeds/create` → `ExternalFeedController` → `ExternalFeedService`

---

### 2.4 Deployment Account（部署帳戶）

**實體路徑**：`src/Squid.Core/Persistence/Entities/Deployments/DeploymentAccount.cs`

| 屬性 | 類型 | 說明 |
|------|------|------|
| `Id` | int | 主鍵 |
| `Name` | string | 帳戶名稱 |
| `AccountType` | AccountType | 憑證類型 |
| `Credentials` | string | 加密的 JSON 憑證 |
| `EnvironmentId` | string | 可按環境限定作用域 |

**AccountType 枚舉**：

| 值 | 說明 | 憑證字段 |
|----|------|----------|
| `Token` (3) | Bearer Token | token |
| `UsernamePassword` (1) | 用戶名密碼 | username, password |
| `ClientCertificate` (8) | 客戶端證書 | certificateData, keyData |
| `AmazonWebServicesAccount` (6) | AWS | accessKey, secretKey |
| `SshKeyPair` (2) | SSH 密鑰對 | username, privateKey |
| `AzureServicePrincipal` (5) | Azure SP | subscriptionId, clientId, tenantId, key |

**在部署中的作用**：
- Machine 的 Endpoint JSON 中引用 `AccountId`
- `IEndpointVariableContributor.ParseResourceReferences()` 從 Endpoint 解析帳戶引用
- 帳戶憑證解密後貢獻為端點變量（Token、Username、Password 等）

**API**：`POST /api/deployment-accounts/create` → `DeploymentAccountController`

---

### 2.5 Variable Set（變量集）

**實體路徑**：`src/Squid.Core/Persistence/Entities/Deployments/VariableSet.cs`

| 屬性 | 類型 | 說明 |
|------|------|------|
| `Id` | int | 主鍵 |
| `Name` | string | 變量集名稱 |
| `OwnerType` | VariableSetOwnerType | 所有者類型（Project / LibraryVariableSet / Environment） |
| `OwnerId` | int | 所有者 ID |
| `Version` | int | 版本號 |

**Variable（變量）**：

| 屬性 | 類型 | 說明 |
|------|------|------|
| `Name` | string | 變量名（如 "DatabasePassword"） |
| `Value` | string | 值（敏感值加密存儲） |
| `Type` | VariableType | String / Number / Boolean / Password / Certificate / MultiLineText / SelectList |
| `IsSensitive` | bool | 是否敏感（加密標記） |

**VariableScope（變量作用域）**：

| ScopeType | 說明 | ScopeValue |
|-----------|------|------------|
| `Environment` (1) | 按環境限定 | 環境 ID |
| `Machine` (2) | 按機器限定 | 機器 ID |
| `Role` (3) | 按目標角色限定 | 角色名稱 |
| `Channel` (4) | 按通道限定 | 通道 ID |

**變量如何替代模板**：
- 在 Release 創建時，所有關聯的 VariableSet（Project 自有 + IncludedLibraryVariableSetIds）被快照
- 執行時 `VariableExpander.ExpandString()` 將 `#{VariableName}` 替換為實際值
- Action Properties 和 Script Body 都經過變量展開
- 支持多層作用域：相同名稱的變量根據 scope 優先級決定最終值

**API**：`POST /api/variable-sets/create` → `VariableSetController` → `VariableService`

---

### 2.6 Project（項目）

**實體路徑**：`src/Squid.Core/Persistence/Entities/Deployments/Project.cs`

| 屬性 | 類型 | 說明 |
|------|------|------|
| `Id` | int | 主鍵 |
| `Name` | string | 項目名稱 |
| `VariableSetId` | int | 主變量集 FK |
| `DeploymentProcessId` | int | 部署流程 FK |
| `ProjectGroupId` | int | 項目組 FK |
| `LifecycleId` | int | 默認生命週期 FK |
| `IncludedLibraryVariableSetIds` | string | CSV 格式的庫變量集 ID 列表 |
| `DiscreteChannelRelease` | bool | 每個通道獨立 Release |

**項目創建流程（3 次提交）**：

```
Commit 1: 保存 Project 實體 → 獲得 project.Id
Commit 2: 創建子實體
  ├─ DeploymentProcess (Version=1, ProjectId=project.Id)
  ├─ VariableSet (OwnerType=Project, OwnerId=project.Id)
  └─ Channel (Name="Default", IsDefault=true)
Commit 3: 回鏈外鍵
  ├─ project.DeploymentProcessId = process.Id
  └─ project.VariableSetId = variableSet.Id
```

**API**：`POST /api/projects/create` → `ProjectController` → `ProjectService`

---

### 2.7 Channel（通道）

**實體路徑**：`src/Squid.Core/Persistence/Entities/Deployments/Channel.cs`

| 屬性 | 類型 | 說明 |
|------|------|------|
| `Id` | int | 主鍵 |
| `Name` | string | 通道名稱（如 "Default"、"Beta"） |
| `ProjectId` | int | 所屬項目 |
| `LifecycleId` | int? | 可選覆蓋生命週期（null = 使用項目默認） |
| `IsDefault` | bool | 是否為默認通道 |

**作用**：
- Release 通過 `ChannelId` 指定發佈通道
- `ActionChannel` 可將動作限定到特定通道
- 通道可覆蓋項目的默認 Lifecycle

---

### 2.8 Machine（部署目標）

**實體路徑**：`src/Squid.Core/Persistence/Entities/Deployments/Machine.cs`

| 屬性 | 類型 | 說明 |
|------|------|------|
| `Id` | int | 主鍵 |
| `Name` | string | 機器名稱 |
| `IsDisabled` | bool | 是否禁用 |
| `Roles` | string | CSV 格式的目標角色（如 "web,api,worker"） |
| `EnvironmentIds` | string | CSV 格式的環境 ID 列表 |
| `Endpoint` | string | JSON 端點配置（通信方式特定） |
| `Thumbprint` | string | Halibut 證書指紋（Polling 用） |
| `Uri` | string | 通信端點 URL |
| `PollingSubscriptionId` | string | Halibut Polling 訂閱 ID |
| `OperatingSystem` | OperatingSystemType | Windows / Linux / MacOS |
| `ShellName` | string | Shell 類型（powershell / bash） |

**兩種 Kubernetes 註冊方式**：

**KubernetesApi（直接 API 訪問）**：
```json
{
  "CommunicationStyle": "KubernetesApi",
  "ClusterUrl": "https://kubernetes.default.svc",
  "AccountId": 123,
  "Namespace": "production",
  "SkipTlsVerification": false,
  "ClusterCertificate": "..."
}
```

**KubernetesAgent（Halibut Polling）**：
```json
{
  "CommunicationStyle": "KubernetesAgent",
  "PollingSubscriptionId": "abc123",
  "Thumbprint": "ABCDEF..."
}
```

**API**：
- `POST /api/machines/register/kubernetes-api` — 註冊 API 直連目標
- `POST /api/machines/register/kubernetes-agent` — 註冊 Polling Agent 目標（同時調用 `halibutRuntime.Trust(thumbprint)`）

---

## 3. Process Step 設置與擴展

### 3.1 DeploymentProcess（部署流程）

**實體路徑**：`src/Squid.Core/Persistence/Entities/Deployments/DeploymentProcess.cs`

```csharp
public class DeploymentProcess : IEntity<int>
{
    public int Id { get; set; }
    public int ProjectId { get; set; }      // FK → Project
    public int Version { get; set; } = 1;   // 版本遞增
    public int SpaceId { get; set; }
}
```

每個 Project 有且只有一個 DeploymentProcess，包含有序的 Steps。

---

### 3.2 DeploymentStep（部署步驟）

**實體路徑**：`src/Squid.Core/Persistence/Entities/Deployments/DeploymentStep.cs`

| 屬性 | 類型 | 說明 |
|------|------|------|
| `ProcessId` | int | FK → DeploymentProcess |
| `StepOrder` | int | 執行順序 |
| `Name` | string | 步驟名稱 |
| `Condition` | string | 執行條件：`"Success"` / `"Failure"` / `"Always"` / `"Variable"` |
| `StartTrigger` | string | 啟動觸發：null（順序）或 `"StartWithPrevious"`（並行） |
| `IsDisabled` | bool | 是否禁用 |
| `IsRequired` | bool | 是否必需 |

**Step Properties（鍵值存儲）**：
- 存儲在 `deployment_step_property` 表
- 複合主鍵：`(StepId, PropertyName)`
- 關鍵屬性：`"Squid.Action.TargetRoles"` = `"web,api"` — 連接步驟與機器的角色

---

### 3.3 DeploymentAction（部署動作）

**實體路徑**：`src/Squid.Core/Persistence/Entities/Deployments/DeploymentAction.cs`

| 屬性 | 類型 | 說明 |
|------|------|------|
| `StepId` | int | FK → DeploymentStep |
| `ActionOrder` | int | 步驟內執行順序 |
| `Name` | string | 動作名稱 |
| `ActionType` | string | 類型字符串（如 "Squid.KubernetesRunScript"） |
| `FeedId` | int? | FK → ExternalFeed |
| `PackageId` | string | 包 ID |

**Action Properties（鍵值存儲）**：
- 存儲在 `deployment_action_property` 表
- 複合主鍵：`(ActionId, PropertyName)`
- 通過 `ctx.Action.GetProperty("Squid.Action.Script.ScriptBody")` 訪問（大小寫不敏感）

**常見 Property 鍵**：

| PropertyName | 用途 | Handler |
|-------------|------|---------|
| `Squid.Action.Script.ScriptBody` | 用戶腳本內容 | KubernetesRunScript |
| `Squid.Action.Script.Syntax` | "Bash" 或 "PowerShell" | KubernetesRunScript |
| `Squid.Action.KubernetesYaml.InlineYaml` | 內聯 YAML 內容 | KubernetesDeployRawYaml |
| `Squid.Action.Helm.*` | Helm Chart 配置 | HelmChartUpgrade |
| `Squid.Action.KubernetesContainers.*` | 容器部署配置 | KubernetesDeployContainers |

**環境/通道過濾**：
- `ActionEnvironment` — 多對多：動作限定到特定環境（空 = 所有環境）
- `ActionChannel` — 多對多：動作限定到特定通道（空 = 所有通道）
- `ExcludedEnvironments` — 從特定環境排除動作

---

### 3.4 ActionType 與 Handler 映射

**DeploymentActionType 枚舉**：

```csharp
public enum DeploymentActionType
{
    KubernetesRunScript = 1,        // 用戶自定義腳本
    KubernetesDeployRawYaml = 2,    // 內聯 YAML 部署
    KubernetesDeployContainers = 3, // 容器部署（生成 YAML）
    HelmChartUpgrade = 4,           // Helm Chart 安裝/升級
    KubernetesDeployIngress = 5     // Ingress 部署
}
```

**字符串映射**：`DeploymentActionTypeParser.TryParse()`

```
"Squid.KubernetesRunScript"      → KubernetesRunScript
"Squid.KubernetesDeployRawYaml"  → KubernetesDeployRawYaml
"Squid.KubernetesDeployContainers" → KubernetesDeployContainers
"Squid.HelmChartUpgrade"         → HelmChartUpgrade
"Squid.KubernetesDeployIngress"  → KubernetesDeployIngress
```

**ActionHandlerRegistry 解析邏輯**：

```csharp
public IActionHandler Resolve(DeploymentActionDto action)
{
    // 1. 解析字符串 → 枚舉
    if (!DeploymentActionTypeParser.TryParse(action.ActionType, out var actionType))
        return null;
    // 2. 字典查找
    if (!_handlers.TryGetValue(actionType, out var handler))
        return null;
    // 3. 最終守衛
    return handler.CanHandle(action) ? handler : null;
}
```

**各 Handler 行為概覽**：

| Handler | ActionType | ScriptBody | Files | ExecutionMode |
|---------|-----------|------------|-------|---------------|
| `KubernetesRunScriptActionHandler` | KubernetesRunScript | 用戶腳本 | 無 | DirectScript |
| `KubernetesDeployYamlActionHandler` | KubernetesDeployRawYaml | `kubectl apply -f inline-deployment.yaml` | `{inline-deployment.yaml}` | DirectScript |
| `HelmUpgradeActionHandler` | HelmChartUpgrade | Helm 模板腳本 | `{rawYamlValues.yaml}` | DirectScript |
| `KubernetesYamlActionHandler` | KubernetesDeployContainers | (Calamari) | YAML 文件集 | PackagedPayload |
| `KubernetesDeployIngressActionHandler` | KubernetesDeployIngress | `kubectl apply -f ingress.yaml` | `{ingress.yaml}` | DirectScript |

---

### 3.5 Step 條件與過濾

**步驟條件評估**（`StepEligibilityEvaluator`）：

| Condition | 行為 | 說明 |
|-----------|------|------|
| `"Success"` (默認) | 僅在之前無失敗時執行 | null/空字符串默認為此 |
| `"Failure"` | 僅在之前有失敗時執行 | 用於回滾/恢復步驟 |
| `"Always"` | 無論之前是否失敗都執行 | 清理、通知 |
| `"Variable"` | 評估變量條件表達式 | 表達式在 Step Property 中 |

**FailureEncountered 標誌**：
- `DeploymentTaskContext` 中的**粘性累積標誌**
- 一旦設為 true，所有後續步驟都保持 true
- "Success" 條件檢查此標誌

**動作過濾**（`StepEligibilityEvaluator.ShouldExecuteAction`）：
1. 是否禁用 → 跳過
2. 環境過濾 → 動作的 `Environments` 列表是否包含當前環境
3. 排除環境過濾 → 動作的 `ExcludedEnvironments` 是否包含當前環境
4. 通道過濾 → 動作的 `Channels` 列表是否包含當前通道

---

### 3.6 如何擴展新的 Step 類型

添加新的 Action Type 只需 4 步，**無需修改 DeploymentTaskExecutor**：

```
步驟 1：添加枚舉值
  DeploymentActionType.cs → NewCustomAction = 6

步驟 2：更新解析器
  DeploymentActionTypeParser.cs → "Squid.NewCustomAction" → DeploymentActionType.NewCustomAction

步驟 3：創建 Handler
  NewCustomActionHandler : IActionHandler
    - ActionType => DeploymentActionType.NewCustomAction
    - CanHandle() 守衛
    - PrepareAsync() → 返回 ActionExecutionResult

步驟 4：自動註冊（無需額外更改）
  - IActionHandler : IScopedDependency → SquidModule 自動掃描註冊
  - ActionHandlerRegistry 通過構造函數注入所有 IActionHandler 實現
```

**數據庫無需更改**：ActionType 是字符串，任何新類型值直接可用。

---

## 4. Release 創建與 Snapshot 機制

### 4.1 Release 創建流程

**Release 實體**：

| 屬性 | 類型 | 說明 |
|------|------|------|
| `Version` | string | 版本號（如 "1.0.0"） |
| `ProjectId` | int | FK → Project |
| `ChannelId` | int | FK → Channel |
| `ProjectVariableSetSnapshotId` | int | FK → VariableSetSnapshot |
| `ProjectDeploymentProcessSnapshotId` | int | FK → DeploymentProcessSnapshot |

**創建流程**（`ReleaseService.CreateReleaseAsync`）：

```
POST /api/releases/create { Version, ChannelId, ProjectId, SelectedPackages }
  ↓
1. 映射 Command → Release 實體
2. 快照變量集：SnapshotVariableSetFromReleaseAsync(release)
   └─ 加載 Project 所有關聯的 VariableSet
   └─ 序列化為 JSON → GZIP 壓縮 → 計算 SHA256 哈希
   └─ 去重檢查：相同哈希 → 複用已有快照
   └─ 保存 VariableSetSnapshot → 獲得 snapshotId
3. 快照部署流程：SnapshotProcessFromReleaseAsync(release)
   └─ 加載 DeploymentProcess + Steps + Actions + Properties + Environments + Channels + Roles
   └─ 構建嵌套結構：DeploymentProcessSnapshotDataDto
   └─ 序列化 → 壓縮 → 哈希 → 去重
   └─ 保存 DeploymentProcessSnapshot → 獲得 snapshotId
4. release.ProjectVariableSetSnapshotId = variableSnapshotId
5. release.ProjectDeploymentProcessSnapshotId = processSnapshotId
6. 保存 Release 實體
7. 持久化 ReleaseSelectedPackage 記錄（action name → version）
8. 發佈 ReleaseCreatedEvent
```

**ReleaseSelectedPackage**：

```csharp
public class ReleaseSelectedPackage
{
    public int ReleaseId { get; set; }
    public string ActionName { get; set; }   // 對應的 DeploymentAction 名稱
    public string Version { get; set; }      // 選定的包版本
}
```

---

### 4.2 Snapshot 架構原理

**核心理念**：Release 在創建時「凍結」當前的流程定義和變量值，確保後續對流程或變量的修改不會影響已有的 Release/Deployment。

**DeploymentProcessSnapshot 存儲結構**：

```csharp
public class DeploymentProcessSnapshot : IEntity<int>
{
    public int OriginalProcessId { get; set; }    // 原始流程 ID
    public byte[] SnapshotData { get; set; }      // GZIP 壓縮的 JSON
    public string ContentHash { get; set; }       // SHA256 哈希（去重用）
    public string CompressionType { get; set; }   // "GZIP"
    public int UncompressedSize { get; set; }     // 原始大小
}
```

**快照數據 DTO 結構**：

```
DeploymentProcessSnapshotDataDto
  ├─ StepSnapshots: List<DeploymentStepSnapshotDataDto>
  │    ├─ Id, Name, StepType, StepOrder, Condition, StartTrigger, IsDisabled
  │    ├─ Properties: Dictionary<string, string>    // 扁平化的 KV 屬性
  │    └─ ActionSnapshots: List<DeploymentActionSnapshotDataDto>
  │         ├─ Id, Name, ActionType, ActionOrder, FeedId, PackageId
  │         ├─ Properties: Dictionary<string, string>
  │         ├─ Environments: List<int>
  │         ├─ ExcludedEnvironments: List<int>
  │         ├─ Channels: List<int>
  │         └─ MachineRoles: List<string>
  └─ ScopeDefinitions: Dictionary<string, List<string>>
```

**VariableSetSnapshot 存儲結構**：

```csharp
public class VariableSetSnapshot : IEntity<int>
{
    public byte[] SnapshotData { get; set; }      // GZIP 壓縮的 JSON
    public string ContentHash { get; set; }       // SHA256 哈希
}
```

**快照數據內容**：

```
VariableSetSnapshotDataDto
  └─ Variables: List<VariableDto>
       ├─ Name, Value, Type, IsSensitive
       └─ Scopes: List<VariableScopeDto>
            └─ ScopeType, ScopeValue
```

---

### 4.3 Snapshot 壓縮與去重

**壓縮流程**（`UtilService.BuildSnapshotBlob`）：

```
原始數據 → JSON 序列化 → 計算 SHA256(JSON) → GZIP 壓縮(JSON)
  → SnapshotBlob { CompressedData, ContentHash, UncompressedSize }
```

**去重邏輯**：
1. 計算新快照的 ContentHash
2. 查詢是否已有相同 ContentHash 的快照
3. 若存在 → 直接複用已有快照 ID（不創建新記錄）
4. 若不存在 → 創建新快照記錄

**好處**：當流程或變量未變化時，多個 Release 共享同一個 Snapshot，節省存儲空間。

---

## 5. Deployment 創建與任務調度

### 5.1 Deployment 創建流程

**Deployment 實體**：

| 屬性 | 類型 | 說明 |
|------|------|------|
| `TaskId` | int? | FK → ServerTask |
| `ReleaseId` | int | FK → Release |
| `EnvironmentId` | int | 目標環境 |
| `MachineId` | int | 特定機器（0 = 環境中所有） |
| `ProcessSnapshotId` | int? | FK → DeploymentProcessSnapshot（從 Release 複製） |
| `VariableSetSnapshotId` | int? | FK → VariableSetSnapshot（從 Release 複製） |

**創建流程**（`DeploymentService.CreateDeploymentAsync`）：

```
POST /api/deployments/create { ReleaseId, EnvironmentId, Name, ... }
  ↓
1. 驗證環境：ValidateDeploymentEnvironmentAsync()
   ├─ Release 存在
   ├─ Environment 存在
   ├─ 環境中有可用 Machine
   └─ Lifecycle 允許部署到此環境（ILifecycleProgressionEvaluator）

2. 創建 ServerTask：
   { State: "Pending", ServerTaskType: "Deploy", ProjectId, EnvironmentId }

3. 創建 Deployment：
   { TaskId: serverTask.Id,
     ProcessSnapshotId: release.ProjectDeploymentProcessSnapshotId,  // 從 Release 複製！
     VariableSetSnapshotId: release.ProjectVariableSetSnapshotId }   // 從 Release 複製！

4. 入隊後台任務：
   _backgroundJobClient.Enqueue<IDeploymentTaskExecutor>(
       executor => executor.ProcessAsync(serverTask.Id, CancellationToken.None),
       queue: "deployment")

5. 更新 serverTask.JobId = jobId

6. 返回 DeploymentCreatedEvent { Deployment, TaskId }
```

**關鍵點**：Deployment 從 Release 複製 Snapshot ID，確保部署使用 Release 時的流程和變量版本。

---

### 5.2 ServerTask 與 Hangfire 調度

**ServerTask 實體**：

| 屬性 | 說明 |
|------|------|
| `State` | "Pending" → "Executing" → "Success" / "Failed" |
| `QueueTime` | 入隊時間 |
| `StartTime` | 開始執行時間 |
| `CompletedTime` | 完成時間 |
| `ErrorMessage` | 失敗錯誤信息 |
| `JobId` | Hangfire Job ID |

**Hangfire 配置**：
- **存儲**：Redis
- **重試策略**：不自動重試（Attempts = 0）
- **序列化**：完整類型名稱
- **過期**：任務 12 小時後過期
- **隊列**：`"deployment"` 獨立隊列

**執行流程**：
```
Hangfire Worker 從 Redis "deployment" 隊列出隊
  ↓
從 DI 容器解析 IDeploymentTaskExecutor
  ↓
調用 ProcessAsync(serverTaskId, CancellationToken.None)
  ↓
部署執行流水線開始...
```

---

## 6. 部署執行流水線

### 6.1 ProcessAsync 頂層管道

**文件**：`src/Squid.Core/Services/DeploymentExecution/DeploymentTaskExecutor.cs`

```csharp
public async Task ProcessAsync(int serverTaskId, CancellationToken ct)
{
    _ctx = new DeploymentTaskContext();
    try
    {
        await LoadDeploymentDataAsync(serverTaskId, ct);    // [Prepare.cs]
        await CreateTaskActivityNodeAsync(ct);               // [Logging.cs]
        await PrepareAllTargetsAsync(ct);                    // [Prepare.cs]
        await ExecuteDeploymentStepsAsync(ct);               // [Execute.cs]
        await RecordSuccessAsync(ct);                        // [Logging.cs]
    }
    catch (Exception ex)
    {
        await RecordFailureAsync(serverTaskId, ex, ct);      // [Logging.cs] → rethrow
    }
}
```

**Partial Class 組織**：

| 文件後綴 | 職責 |
|---------|------|
| `.cs` | 接口、構造函數、`_ctx` 字段、頂層管道 |
| `.Prepare.cs` | 數據加載、目標準備 |
| `.Execute.cs` | 步驟/動作執行編排 |
| `.Filter.cs` | 委託給 StepEligibilityEvaluator |
| `.Logging.cs` | 活動日誌、任務完成記錄 |

---

### 6.2 Phase 1：加載部署數據

**文件**：`DeploymentTaskExecutor.Prepare.cs`

```
LoadDeploymentDataAsync(serverTaskId, ct)
  ├─ LoadTaskAsync(serverTaskId)
  │   └─ 加載 ServerTask，狀態轉換 Pending → Executing
  │
  ├─ LoadDeploymentAsync()
  │   └─ 通過 TaskId 加載 Deployment + Release
  │
  ├─ LoadSelectedPackagesAsync()
  │   └─ 加載 ReleaseSelectedPackage 列表
  │
  ├─ LoadOrSnapshotAsync()
  │   ├─ 如果 deployment.ProcessSnapshotId 存在：
  │   │   └─ 加載並解壓快照 → DeploymentProcessSnapshotDto
  │   └─ 否則（回退）：
  │       └─ 從當前流程創建新快照
  │
  ├─ ResolveVariablesAsync()
  │   └─ 通過 IDeploymentVariableResolver 加載快照中的變量
  │
  ├─ FindTargetsAsync()
  │   └─ 查找匹配環境的已啟用 Machine
  │
  ├─ ConvertSnapshotToSteps()
  │   └─ ProcessSnapshotStepConverter.Convert()
  │       將 Snapshot DTO → List<DeploymentStepDto>
  │
  └─ PreFilterTargetsByRoles()
      └─ 優化：按步驟角色預過濾機器
```

**快照到步驟的轉換**（`ProcessSnapshotStepConverter`）：

```
DeploymentProcessSnapshot.SnapshotData (GZIP bytes)
  ↓ 解壓 + 反序列化
DeploymentProcessSnapshotDataDto
  ↓ 轉換
List<DeploymentStepDto>
  每個步驟：
    - 按 StepOrder 排序
    - Dictionary Properties → List<DeploymentStepPropertyDto>
    - 嵌套 DeploymentActionDto（按 ActionOrder 排序）
    - 保留所有條件、禁用、必需、角色信息
```

---

### 6.3 Phase 2：準備所有目標

**文件**：`DeploymentTaskExecutor.Prepare.cs`

對 `_ctx.AllTargets` 中的每台機器：

```
PrepareAllTargetsAsync(ct)
  └─ foreach target in _ctx.AllTargets:

     1. LoadTransportForTarget
        ├─ CommunicationStyleParser.Parse(endpointJson) → 解析通信方式
        └─ TransportRegistry.Resolve(style) → IDeploymentTransport
            包含：Variables, ScriptWrapper, Strategy

     2. LoadAuthenticationAsync
        ├─ IEndpointVariableContributor.ParseResourceReferences()
        │   → EndpointResourceReferences
        └─ 按類型解析：AuthenticationAccount、ClientCertificate、ClusterCertificate
            存入 EndpointContext

     3. ContributeEndpointVariablesForTarget
        └─ tc.Transport.Variables.ContributeVariables(tc.EndpointContext)
            → 返回隔離的端點變量列表
            KubernetesApi 示例：ClusterUrl, Token, Namespace, SkipTlsVerification 等 9+ 變量

     4. ContributeAdditionalVariablesForTargetAsync
        └─ tc.Transport.Variables.ContributeAdditionalVariablesAsync(...)
            KubernetesApi 示例：ContainerImage = "{feedUri}/{packageId}:{releaseVersion}"
```

**DeploymentTargetContext（每目標上下文）**：

```csharp
public class DeploymentTargetContext
{
    public Machine Machine { get; set; }
    public EndpointContext EndpointContext { get; set; } = new();
    public CommunicationStyle CommunicationStyle { get; set; }
    public IDeploymentTransport Transport { get; set; }
    public List<VariableDto> EndpointVariables { get; set; } = new();  // 隔離！不污染全局
}
```

---

### 6.4 Phase 3：執行部署步驟

**文件**：`DeploymentTaskExecutor.Execute.cs`

```
ExecuteDeploymentStepsAsync(ct)
  ├─ StepBatcher 將步驟分批
  │   連續的 StartTrigger="StartWithPrevious" 步驟歸入同一批
  │   同批步驟通過 Task.WhenAll 並行執行
  │
  └─ foreach batch:
     └─ ExecuteStepAcrossTargetsAsync(step, ct)

        1. 查找匹配目標
           └─ TargetStepMatcher 按步驟角色過濾

        2. 構建有效變量（每目標）
           └─ EffectiveVariableBuilder.Build()
               合併：_ctx.Variables（按 scope 過濾）+ tc.EndpointVariables
               作用域權重：Machine(1M) > Role(10K) > Environment(100) > Channel(10)
               同名變量：最高權重值勝出

        3. 步驟可執行性檢查
           └─ StepEligibilityEvaluator.ShouldExecuteStep()
               ├─ 是否禁用？
               ├─ 條件評估（Success/Failure/Always/Variable）
               └─ 目標角色匹配？

        4. 創建步驟活動日誌節點

        5. 準備步驟動作 — PrepareStepActionsAsync
           └─ foreach action (按 ActionOrder):
              ├─ ShouldExecuteAction 檢查（禁用、環境、通道）
              ├─ ActionHandlerRegistry.Resolve(action) → 找到 Handler
              ├─ 構建動作變量（添加 PackageVersion）
              ├─ VariableExpander.ExpandActionProperties() — 展開屬性中的 #{var}
              ├─ handler.PrepareAsync(context) → ActionExecutionResult
              │   { ScriptBody, Files, CalamariCommand, ExecutionMode, Syntax }
              ├─ VariableExpander.ExpandString(scriptBody) — 展開腳本中的 #{var}
              └─ WrapScriptIfApplicable — IScriptContextWrapper
                  僅對 DirectScript + ContextPreparationPolicy.Apply 生效

        6. 執行動作結果 — ExecuteActionResultsAsync
           └─ foreach prepared action:
              ├─ 創建動作活動日誌節點
              ├─ 構建 ScriptExecutionRequest
              ├─ strategy.ExecuteScriptAsync(request, ct)
              │   → ScriptExecutionResult { Success, LogLines, ExitCode }
              ├─ ServiceMessageParser 捕獲輸出變量
              │   模式：##squid[setVariable name='X' value='Y' sensitive='True/False']
              │   存為：Squid.Action[{StepName}].Output.{VarName} + 無限定名副本
              └─ 合併結果：更新 _ctx.Variables，更新 FailureEncountered 標誌
```

---

## 7. 變量系統

### 7.1 變量作用域與優先級

**VariableScopeEvaluator 評估邏輯**：

```
權重系統（Rank Weights）：
  Machine  = 1,000,000
  Role     = 10,000
  Environment = 100
  Channel  = 10

相同名稱的變量：
  - 跨 scope 類型：必須全部匹配（AND 邏輯）
  - 同一 scope 類型內：任一匹配即可（OR 邏輯）
  - 最高權重的 scope 值勝出

示例：
  Variable "DbHost" 有兩個定義：
    - ScopeType=Environment, ScopeValue="Production" → 值 "prod-db.internal"  (rank=100)
    - ScopeType=Machine, ScopeValue="web-01" → 值 "web01-db.internal"  (rank=1000000)

  部署到 Machine "web-01" 時 → 使用 "web01-db.internal"（Machine rank 更高）
```

---

### 7.2 變量替換流水線

```
Stage 1: 加載基礎變量
  └─ ResolveVariablesAsync → _ctx.Variables（從快照加載）

Stage 2: 貢獻端點變量（每目標）
  ├─ contributor.ContributeVariables(endpointContext)
  │   → KubernetesApi: ClusterUrl, Token, Namespace 等
  └─ contributor.ContributeAdditionalVariablesAsync(...)
      → ContainerImage = "{feedUri}/{packageId}:{releaseVersion}"

Stage 3: 構建有效變量
  └─ EffectiveVariableBuilder.Build()
      = _ctx.Variables（按 scope 過濾）+ tc.EndpointVariables

Stage 4: 展開 Action Properties
  └─ VariableExpander.ExpandActionProperties(action, variableDictionary)
      "#{Namespace}" → "production"

Stage 5: 展開 Script Body
  └─ VariableExpander.ExpandString(scriptBody, variableDictionary)

Stage 6: 序列化以執行
  ├─ 非敏感 → variables.json（明文 JSON）
  └─ 敏感 → sensitiveVariables.json（AES 加密）

Stage 7: 捕獲輸出變量（執行後）
  └─ 解析日誌中 ##squid[setVariable name='X' value='Y' sensitive='True/False']
      → 添加為 "Squid.Action[{StepName}].Output.{VarName}" + 無限定名副本
```

---

### 7.3 變量序列化與加密

**ScriptExecutionHelper.CreateVariableFileContents()**：

```
輸入：List<VariableDto>
  ↓
分區：敏感 vs 非敏感

非敏感變量：
  → Dictionary<string, string> → JSON 序列化 → variables.json（明文）

敏感變量：
  → Dictionary<string, string> → JSON 序列化
  → 生成隨機密碼：Guid.NewGuid().ToString("N")  // 32 字符
  → SquidVariableEncryption(password).Encrypt(json)
  → sensitiveVariables.json（AES 加密）

輸出：(variableJson bytes, sensitiveJson bytes, password string)
```

**AES 加密細節**（`SquidVariableEncryption`）：
- **密鑰派生**：PBKDF2(password, salt="SquidDep", iterations=1000, outputLength=16)
- **算法**：AES-128-CBC + PKCS7 填充
- **IV**：每次加密隨機生成 16 字節 IV
- **文件格式**：`"IV__"` (4 bytes) + IV (16 bytes) + 密文

---

### 7.4 輸出變量捕獲

**ServiceMessageParser**：

```
日誌行格式：##squid[setVariable name='DeployedUrl' value='https://app.example.com' sensitive='False']

解析結果：
  ├─ 限定名：Squid.Action[Deploy Web].Output.DeployedUrl = "https://app.example.com"
  └─ 無限定名副本：DeployedUrl = "https://app.example.com"

後續步驟可通過 #{Squid.Action[Deploy Web].Output.DeployedUrl} 或 #{DeployedUrl} 引用
```

---

## 8. Machine 匹配與目標查找

**DeploymentTargetFinder 查找邏輯**：

```
FindTargetsAsync(deployment, ct)
  1. GetCandidatePoolAsync()
     ├─ 如果 deployment.MachineId > 0：返回特定機器
     └─ 否則：返回部署環境中所有機器

  2. FilterByEnvironment(candidates, envId)
     └─ 保留 EnvironmentIds 包含 envId 的機器

  3. FilterDisabled(candidates)
     └─ 移除 IsDisabled 的機器

預過濾角色（優化）：
  CollectAllTargetRoles(steps)
    ├─ 掃描所有啟用步驟的 TargetRoles 屬性
    ├─ 如果任何步驟沒有角色 → 返回空集（所有機器都需要）
    └─ 否則：返回所有步驟角色的並集

  FilterByRoles(candidates, allRoles)
    └─ 保留 Roles 與 allRoles 有交集的機器（OR 邏輯）
    └─ 空角色集 = 不過濾（所有機器匹配）
```

**步驟級角色匹配**（在執行時）：

```
TargetStepMatcher.MatchesTargetRoles(step, machine)
  ├─ 獲取步驟的 "Squid.Action.TargetRoles" 屬性
  ├─ 解析為角色集合（逗號分隔）
  └─ 檢查機器的 Roles 是否與步驟角色有交集
      交集非空 → 匹配
      步驟無角色 → 匹配所有機器
```

---

## 9. Tentacle 通信機制

### 9.1 Halibut Polling 架構

**服務端啟動**（`HalibutModule`）：

```
HalibutModule.Load()
  ├─ 加載服務器證書：SelfCertSetting.Base64 → X509Certificate2
  ├─ 創建 DelegateServiceFactory（空的）
  ├─ 構建 HalibutRuntime（WithServerCertificate, WithHalibutTimeoutsAndLimits）
  ├─ StartPollingListenerIfEnabled：
  │   └─ halibutRuntime.Listen(port=10943)  // 默認端口
  └─ 註冊 HalibutTrustInitializer 為 IStartable
```

**信任初始化**（`HalibutTrustInitializer`）：

```
HalibutTrustInitializer.Start()  // Autofac IStartable，容器啟動時執行
  ├─ 查詢所有 Polling Machine：PollingSubscriptionId != NULL AND !IsDisabled
  └─ foreach machine:
      └─ halibutRuntime.Trust(machine.Thumbprint)
```

**RPC 客戶端創建**（`HalibutClientFactory`）：

```csharp
public IAsyncScriptService CreateClient(ServiceEndPoint endpoint)
    => _halibutRuntime.CreateAsyncClient<IScriptService, IAsyncScriptService>(endpoint);
// 需要兩個類型參數：IScriptService（同步，Agent 端）+ IAsyncScriptService（異步，Server 端）
```

---

### 9.2 IScriptService 服務契約

**同步接口（Agent 實現）**：

```csharp
public interface IScriptService
{
    ScriptTicket StartScript(StartScriptCommand command);
    ScriptStatusResponse GetStatus(ScriptStatusRequest request);
    ScriptStatusResponse CompleteScript(CompleteScriptCommand command);
    ScriptStatusResponse CancelScript(CancelScriptCommand command);
}
```

**異步接口（Server 調用）**：

```csharp
public interface IScriptServiceAsync
{
    Task<ScriptTicket> StartScriptAsync(StartScriptCommand command, CancellationToken ct);
    Task<ScriptStatusResponse> GetStatusAsync(ScriptStatusRequest request, CancellationToken ct);
    Task<ScriptStatusResponse> CompleteScriptAsync(CompleteScriptCommand command, CancellationToken ct);
    Task<ScriptStatusResponse> CancelScriptAsync(CancelScriptCommand command, CancellationToken ct);
}
```

**核心數據模型**：

```
StartScriptCommand
  ├─ ScriptBody: string              // Bash/PowerShell 腳本
  ├─ Isolation: ScriptIsolationLevel  // NoIsolation | FullIsolation
  ├─ Files: List<ScriptFile>         // 附加文件
  │    ├─ Name: string               // 相對文件名（如 "variables.json"）
  │    ├─ Contents: DataStream       // Halibut DataStream（流式二進制）
  │    └─ EncryptionPassword: string // 可選加密密碼
  └─ Arguments: string[]             // 命令行參數

ScriptTicket
  └─ TaskId: string                  // StartScript 返回的唯一 ID

ScriptStatusResponse
  ├─ Ticket: ScriptTicket
  ├─ Logs: List<ProcessOutput>       // 累積日誌
  │    ├─ Source: StdOut | StdErr | Debug
  │    ├─ Occurred: DateTimeOffset
  │    └─ Text: string
  ├─ NextLogSequence: long           // 增量日誌序列號
  ├─ State: ProcessState             // Pending | Running | Complete
  └─ ExitCode: int                   // Complete 時的退出碼
```

---

### 9.3 KubernetesAgent 執行策略（遠程 Halibut）

**文件**：`src/Squid.Core/Services/DeploymentExecution/Infrastructure/HalibutMachineExecutionStrategy.cs`

```
ExecuteScriptAsync(request, ct)
  ├─ ScriptExecutionPlanFactory.Create(request) → DirectScript 或 PackagedPayload
  ├─ ParseMachineEndpoint(machine)
  │   ├─ 如果 Uri 為空 + PollingSubscriptionId 存在：
  │   │   → uri = "poll://{subscriptionId}/"
  │   └─ 構建 ServiceEndPoint(uri, thumbprint, timeouts)
  │
  ├─ _halibutClientFactory.CreateClient(endpoint) → IAsyncScriptService
  │
  ├─ 如果 PackagedPayload：ExecuteCalamariViaHalibutAsync()
  │   ├─ _payloadBuilder.Build(request) → CalamariPayload
  │   │   打包 YAML 文件為 NuGet 包
  │   ├─ 準備 3 個文件：
  │   │   ├─ squid.{version}.nupkg（YAML 文件包）
  │   │   ├─ variables.json（明文變量）
  │   │   └─ sensitiveVariables.json（AES 加密的敏感變量）
  │   ├─ 填充 DeployByCalamari.ps1 模板
  │   ├─ 構建 StartScriptCommand → scriptClient.StartScriptAsync()
  │   └─ _observer.ObserveAndCompleteAsync() → 輪詢等待完成
  │
  └─ 如果 DirectScript：ExecuteDirectScriptViaHalibutAsync()
      ├─ 創建變量文件（明文 + 加密）
      ├─ 構建 ScriptFile[] = 用戶文件 + variables.json + sensitiveVariables.json
      ├─ 構建 StartScriptCommand → scriptClient.StartScriptAsync()
      └─ _observer.ObserveAndCompleteAsync() → 輪詢等待完成
```

---

### 9.4 KubernetesApi 執行策略（本地執行）

**文件**：`src/Squid.Core/Services/DeploymentExecution/Infrastructure/LocalProcessExecutionStrategy.cs`

```
ExecuteScriptAsync(request, ct)
  ├─ CreateWorkDirectory() → /tmp/squid-exec/{guid}/
  ├─ ScriptExecutionPlanFactory.Create(request)
  │
  ├─ 如果 PackagedPayload：ExecuteCalamariLocallyAsync()
  │   ├─ 寫入文件到工作目錄
  │   ├─ _payloadBuilder.Build(request) → CalamariPayload
  │   ├─ 寫入 nupkg + variables.json + sensitiveVariables.json
  │   ├─ 填充模板，應用 kubectl context 包裝
  │   └─ _processRunner.RunAsync("pwsh", script, workDir) → 捕獲 stdout/stderr
  │
  ├─ 如果 DirectScript：ExecuteScriptLocallyAsync()
  │   ├─ 寫入文件到工作目錄
  │   ├─ 根據 Syntax 選擇：bash script.sh 或 pwsh script.ps1
  │   └─ _processRunner.RunAsync() → 捕獲 stdout/stderr
  │
  └─ finally：CleanupWorkDirectory(workDir)
```

**Script Context 包裝**（僅 KubernetesApi）：

`KubernetesApiScriptContextWrapper` 在腳本前插入 kubectl context 設置：
```bash
# 設置 kubectl context
kubectl config set-cluster squid-cluster --server="#{ClusterUrl}" ...
kubectl config set-credentials squid-user --token="#{Token}" ...
kubectl config set-context squid-context --cluster=squid-cluster --user=squid-user --namespace="#{Namespace}"
kubectl config use-context squid-context

# 用戶腳本
{UserScript}
```

`KubernetesAgentScriptContextWrapper` 僅設置 namespace：
```bash
kubectl config set-context --current --namespace="#{Namespace}" > /dev/null 2>&1
{UserScript}
```

---

### 9.5 Polling 觀察循環

**文件**：`src/Squid.Core/Services/DeploymentExecution/Infrastructure/HalibutScriptObserver.cs`

```
ObserveAndCompleteAsync(machine, scriptClient, ticket, timeout, ct)

  初始狀態：ProcessState.Pending，空日誌

  while (state != Complete):
    ├─ 超時檢查（30 分鐘默認）
    │   超時 → CancelScriptAsync() → 返回失敗
    │
    ├─ 取消令牌檢查
    │
    ├─ scriptClient.GetStatusAsync(ticket, lastLogSequence)
    │   → StatusResponse { State, Logs, NextLogSequence, ExitCode }
    │
    ├─ 累積日誌
    │
    └─ 自適應退避等待：
        初始 1s → ×1.5 → 1.5s → 2.25s → ... → 最大 10s

  完成後：
    scriptClient.CompleteScriptAsync(ticket, lastLogSequence)
    → 最終日誌 + 退出碼

  返回：ScriptExecutionResult { Success: exitCode==0, LogLines, ExitCode }
```

---

## 10. Calamari 任務執行

### 10.1 CalamariPayload 構建

**文件**：`src/Squid.Core/Services/DeploymentExecution/Infrastructure/CalamariPayloadBuilder.cs`

```
CalamariPayloadBuilder.Build(request)
  ├─ 打包 YAML 文件 → NuGet 包
  │   _yamlNuGetPacker.CreateNuGetPackageFromYamlBytes(request.Files)
  │   { deployment.yaml, service.yaml, configmap.yaml, ... } → squid.{version}.nupkg
  │
  ├─ 創建變量文件
  │   ScriptExecutionHelper.CreateVariableFileContents(request.Variables)
  │   → variables.json（明文）+ sensitiveVariables.json（AES 加密）
  │
  └─ 返回 CalamariPayload:
      { PackageFileName: "squid.1.0.0.nupkg",
        PackageBytes, VariableBytes, SensitiveBytes, SensitivePassword,
        TemplateBody: DeployByCalamari.ps1 }
```

---

### 10.2 DeployByCalamari.ps1 模板

**文件**：`src/Squid.Core/TentaclesScripts/DeployByCalamari.ps1`

```powershell
# 檢查 squid-calamari 是否存在
$squidCalamari = Get-Command "squid-calamari" -ErrorAction SilentlyContinue
if ($null -eq $squidCalamari) { Write-Error "squid-calamari not found"; Exit 1 }

# 檢查 kubectl
if ($null -eq (Get-Command "kubectl" -ErrorAction SilentlyContinue)) { Exit 1 }

# 構建命令參數
$commandArgs = @(
    "apply-yaml",
    "--file={{PackageFilePath}}",           # .nupkg 文件
    "--variables={{VariableFilePath}}"       # variables.json
)

# 可選加密敏感變量
if ("{{SensitiveVariableFile}}" -ne "") {
    $commandArgs += "--sensitive={{SensitiveVariableFile}}"
    $commandArgs += "--password={{SensitiveVariablePassword}}"
}

# 調用 squid-calamari
& $squidCalamari.Source @commandArgs
```

**squid-calamari 工作流程**：
1. 從 nupkg 提取 YAML 文件
2. 讀取 variables.json + 解密 sensitiveVariables.json
3. 在 YAML 中進行變量替換
4. 執行 `kubectl apply -f` 部署

---

### 10.3 DirectScript vs PackagedPayload

| 特性 | DirectScript | PackagedPayload |
|------|-------------|-----------------|
| **ExecutionMode** | `ExecutionMode.DirectScript` | `ExecutionMode.PackagedPayload` |
| **腳本來源** | 用戶提供或 Handler 生成 | DeployByCalamari.ps1 模板 |
| **文件** | 可選的附加文件 | YAML → NuGet 包 + 變量文件 |
| **Calamari** | 不需要 | 需要 squid-calamari |
| **Context Wrapping** | 應用（kubectl context 設置） | 可選應用 |
| **使用場景** | RunScript、DeployYaml、HelmUpgrade | DeployContainers |
| **Shell** | bash 或 pwsh（取決於 Syntax） | pwsh（始終） |

---

## 11. Transport 抽象層

**DeploymentTransport 基類**：

```csharp
public abstract class DeploymentTransport : IDeploymentTransport
{
    public CommunicationStyle CommunicationStyle { get; }
    public IEndpointVariableContributor Variables { get; }
    public IScriptContextWrapper ScriptWrapper { get; }
    public IExecutionStrategy Strategy { get; }
}
```

**兩種 Transport 實現**：

| Transport | 通信方式 | 變量貢獻者 | 腳本包裝器 | 執行策略 |
|-----------|---------|-----------|-----------|---------|
| `KubernetesApiTransport` | KubernetesApi | KubernetesApiEndpointVariableContributor | KubernetesApiScriptContextWrapper | LocalProcessExecutionStrategy |
| `KubernetesAgentTransport` | KubernetesAgent | KubernetesAgentEndpointVariableContributor | KubernetesAgentScriptContextWrapper | HalibutMachineExecutionStrategy |

**擴展新的目標類型**（如 SSH、Azure、Docker）：

```
1. 實現 IEndpointVariableContributor — 解析端點 JSON，貢獻變量
2. 實現 IScriptContextWrapper — 包裝腳本（認證、環境設置）
3. 實現 IExecutionStrategy — 執行腳本（本地、SSH、RPC）
4. 創建 Transport 類 — 5 行聲明式連接
5. (可選) 實現 IActionHandler — 如果目標需要自定義動作類型
6. 無需修改 DeploymentTaskExecutor
```

---

## 12. 端到端完整流程圖

```
═══════════════════════════════════════════════════════════════
                    數據準備階段
═══════════════════════════════════════════════════════════════

用戶 → 創建 Environment (Dev, Staging, Prod)
用戶 → 創建 Lifecycle (Dev → Staging → Prod)
用戶 → 創建 ExternalFeed (Docker Hub)
用戶 → 創建 DeploymentAccount (Token / Certificate)
用戶 → 創建 Machine (KubernetesApi 或 KubernetesAgent 端點)
       ↳ KubernetesAgent 註冊時: halibutRuntime.Trust(thumbprint)
用戶 → 創建 Project
       ↳ 自動創建: DeploymentProcess + VariableSet + Default Channel
用戶 → 配置 VariableSet (定義變量 + 作用域)
用戶 → 設計 DeploymentProcess
       ↳ Step 1: "Deploy API" (TargetRoles="api", Condition="Success")
           ↳ Action 1.1: "Run Script" (Type=Squid.KubernetesRunScript)
              Properties: { ScriptBody: "echo hello", Syntax: "Bash" }
       ↳ Step 2: "Deploy YAML" (TargetRoles="web")
           ↳ Action 2.1: "Deploy Ingress" (Type=Squid.KubernetesDeployRawYaml)
              Properties: { InlineYaml: "apiVersion: v1\nkind: Service..." }

═══════════════════════════════════════════════════════════════
                    Release 創建階段
═══════════════════════════════════════════════════════════════

用戶 → POST /api/releases/create { Version: "1.0.0", ProjectId, ChannelId }
  ↓
ReleaseService.CreateReleaseAsync()
  ├─ 快照變量: VariableSet → GZIP + SHA256 → VariableSetSnapshot
  ├─ 快照流程: Process → Steps → Actions → Properties → GZIP + SHA256 → ProcessSnapshot
  │   （去重：相同哈希 → 複用已有快照）
  ├─ 保存 Release { VariableSnapshotId, ProcessSnapshotId }
  └─ 保存 ReleaseSelectedPackage { ActionName → Version }

═══════════════════════════════════════════════════════════════
                    Deployment 創建階段
═══════════════════════════════════════════════════════════════

用戶 → POST /api/deployments/create { ReleaseId, EnvironmentId }
  ↓
DeploymentService.CreateDeploymentAsync()
  ├─ 驗證: Release 存在、Environment 存在、Machine 可用、Lifecycle 允許
  ├─ 創建 ServerTask { State: "Pending" }
  ├─ 創建 Deployment { TaskId, ReleaseId, EnvironmentId,
  │    ProcessSnapshotId: release.ProcessSnapshotId,   ← 從 Release 複製
  │    VariableSnapshotId: release.VariableSnapshotId } ← 從 Release 複製
  ├─ Hangfire 入隊: IDeploymentTaskExecutor.ProcessAsync(taskId)
  └─ HTTP 200 返回 { DeploymentId, TaskId }

═══════════════════════════════════════════════════════════════
                    後台執行階段（Hangfire Worker）
═══════════════════════════════════════════════════════════════

DeploymentTaskExecutor.ProcessAsync(serverTaskId)
  │
  ├─ Phase 1: 加載數據
  │   ├─ ServerTask: Pending → Executing
  │   ├─ Deployment → Release
  │   ├─ 解壓 ProcessSnapshot → List<DeploymentStepDto>
  │   ├─ 解壓 VariableSnapshot → List<VariableDto>
  │   ├─ 查找目標 Machine（環境 + 啟用 + 角色預過濾）
  │   └─ 加載 ReleaseSelectedPackage
  │
  ├─ Phase 2: 準備目標
  │   └─ foreach Machine:
  │       ├─ 解析 CommunicationStyle → 解析 Transport
  │       ├─ 加載 Account 憑證 → 解密
  │       ├─ 貢獻端點變量 (ClusterUrl, Token, Namespace...)
  │       └─ 貢獻額外變量 (ContainerImage...)
  │
  ├─ Phase 3: 執行步驟
  │   └─ foreach Step (按 StepOrder，支持批次並行):
  │       └─ foreach 匹配的 Machine:
  │           ├─ 構建有效變量 = 全局變量(scope過濾) + 端點變量
  │           ├─ 條件檢查 (Success/Failure/Always)
  │           ├─ foreach Action (按 ActionOrder):
  │           │   ├─ 環境/通道過濾
  │           │   ├─ Handler.PrepareAsync() → { ScriptBody, Files, ExecutionMode }
  │           │   ├─ 變量展開: #{var} → 實際值
  │           │   └─ 腳本包裝: kubectl context 設置
  │           └─ foreach PreparedAction:
  │               ├─ 構建 ScriptExecutionRequest
  │               ├─ Strategy.ExecuteScriptAsync()
  │               │   ├─ KubernetesApi: 本地 bash/pwsh 執行
  │               │   └─ KubernetesAgent: Halibut RPC
  │               │       ├─ poll://{subscriptionId}/ → ServiceEndPoint
  │               │       ├─ StartScriptAsync() → ScriptTicket
  │               │       ├─ GetStatusAsync() 輪詢 (1s→10s 退避)
  │               │       └─ CompleteScriptAsync() → 最終日誌 + 退出碼
  │               ├─ 捕獲輸出變量 (##squid[setVariable])
  │               └─ 更新 FailureEncountered 標誌
  │
  ├─ 成功: RecordSuccessAsync()
  │   ├─ ServerTask: State → Success
  │   └─ 觸發自動部署（如果配置）
  │
  └─ 失敗: RecordFailureAsync()
      ├─ ServerTask: State → Failed, ErrorMessage
      └─ 記錄 DeploymentCompletion

═══════════════════════════════════════════════════════════════
                    Agent 側（生產環境 Tentacle Pod）
═══════════════════════════════════════════════════════════════

Tentacle Pod 啟動:
  ├─ 創建自己的 HalibutRuntime + DelegateServiceFactory
  ├─ 註冊 IScriptService 實現（本地 bash 執行器）
  ├─ halibutRuntime.Trust(serverThumbprint)
  └─ halibutRuntime.Poll(poll://{subscriptionId}/, serverEndpoint)
      → 發起出站連接到 Squid Server 的 Polling 端口

收到 StartScriptCommand:
  ├─ 創建臨時工作目錄
  ├─ 寫入 script.sh + 附加文件（variables.json 等）
  ├─ 啟動 bash 進程執行腳本
  └─ 異步捕獲 stdout/stderr 到 ConcurrentQueue

Server 輪詢 GetStatus:
  └─ 排空 ConcurrentQueue → 返回日誌 + 運行狀態

Server 調用 CompleteScript:
  ├─ 等待進程退出（30s 超時）
  ├─ 排空最終日誌
  ├─ 清理臨時工作目錄
  └─ 返回最終退出碼
```

---

## 附錄：關鍵源文件索引

| 文件路徑 | 用途 |
|---------|------|
| `Services/DeploymentExecution/DeploymentTaskExecutor.cs` | 流水線入口，ProcessAsync |
| `Services/DeploymentExecution/DeploymentTaskExecutor.Prepare.cs` | 數據加載、目標準備 |
| `Services/DeploymentExecution/DeploymentTaskExecutor.Execute.cs` | 步驟/動作執行編排 |
| `Services/DeploymentExecution/DeploymentTaskContext.cs` | 共享流水線狀態 |
| `Services/DeploymentExecution/DeploymentActionType.cs` | ActionType 枚舉 |
| `Services/DeploymentExecution/DeploymentActionTypeParser.cs` | 字符串→枚舉解析 |
| `Services/DeploymentExecution/ActionHandlerRegistry.cs` | Handler 解析 |
| `Services/DeploymentExecution/VariableExpander.cs` | 變量替換 |
| `Services/DeploymentExecution/VariableScopeEvaluator.cs` | 作用域評估 |
| `Services/DeploymentExecution/EffectiveVariableBuilder.cs` | 有效變量構建 |
| `Services/DeploymentExecution/StepEligibilityEvaluator.cs` | 步驟過濾 |
| `Services/DeploymentExecution/StepBatcher.cs` | 步驟批次 |
| `Services/DeploymentExecution/TargetStepMatcher.cs` | 目標角色匹配 |
| `Services/DeploymentExecution/ServiceMessageParser.cs` | 輸出變量捕獲 |
| `Services/DeploymentExecution/ScriptExecutionHelper.cs` | 變量序列化 |
| `Services/DeploymentExecution/DeploymentTransport.cs` | Transport 基類 |
| `Services/DeploymentExecution/Infrastructure/HalibutMachineExecutionStrategy.cs` | Halibut 執行 |
| `Services/DeploymentExecution/Infrastructure/LocalProcessExecutionStrategy.cs` | 本地執行 |
| `Services/DeploymentExecution/Infrastructure/HalibutScriptObserver.cs` | Polling 觀察 |
| `Services/DeploymentExecution/Infrastructure/CalamariPayloadBuilder.cs` | Calamari 打包 |
| `Services/DeploymentExecution/Targets/Kubernetes/Transport/` | K8s Transport 實現 |
| `Services/DeploymentExecution/Targets/Kubernetes/Handlers/` | K8s Handler 實現 |
| `Halibut/HalibutModule.cs` | Halibut 運行時配置 |
| `Halibut/HalibutTrustInitializer.cs` | Agent 信任初始化 |
| `Services/Deployments/Snapshots/DeploymentSnapshotService.cs` | 快照創建與加載 |
| `Services/Deployments/Release/ReleaseService.cs` | Release 創建 |
| `Services/Deployments/Deployments/DeploymentService.cs` | Deployment 創建 |
| `Services/Common/SquidVariableEncryption.cs` | AES 變量加密 |
| `TentaclesScripts/DeployByCalamari.ps1` | Calamari 執行模板 |
