# KubernetesAgent "Deploy Containers" 全链路通信详解

## 概览

当一个 Container Resource step 发布到 KubernetesAgent 目标时，整个流程跨越 **3 个进程边界**：

```
Squid Server (Pipeline)  ──TLS/Halibut RPC──>  Tentacle Pod  ──K8s API──>  Script Pod
```

---

## 1. Pipeline 入口：`ProcessAsync`

```
ProcessAsync(serverTaskId, ct)                       [DeploymentTaskExecutor.cs]
  try:
    LoadDeploymentDataAsync(serverTaskId, ct)         [Prepare.cs]
    CreateTaskActivityNodeAsync(ct)                   [Logging.cs]
    PrepareAllTargetsAsync(ct)                        [Prepare.cs]
    ExecuteDeploymentStepsAsync(ct)                   [Execute.cs]
    RecordSuccessAsync(ct)                            [Logging.cs]
  catch:
    RecordFailureAsync → rethrow
```

共享状态全部通过 `_ctx`（`DeploymentTaskContext`）字段传递，无参数穿透。

---

## 2. 数据加载阶段

`LoadDeploymentDataAsync` 按序执行 8 步：

| 步骤 | 作用 |
|------|------|
| `LoadTaskAsync` | 加载 ServerTask，状态 Pending → Executing |
| `LoadDeploymentAsync` | 加载 Deployment + Release |
| `LoadSelectedPackagesAsync` | 加载 container name → version 的包选择 |
| `LoadOrSnapshotAsync` | 加载/创建流程快照 |
| `ResolveVariablesAsync` | 解析部署级变量 → `_ctx.Variables` |
| `FindTargetsAsync` | 按环境查找 Machine 实体 |
| `ConvertSnapshotToSteps` | 快照转 `List<DeploymentStepDto>` |
| `PreFilterTargetsByRoles` | 去掉 roles 不匹配的目标 |

---

## 3. Transport 解析 — KubernetesAgent 的组件绑定

### 3.1 Transport 注册体系

`LoadTransportForTarget` 三步：

1. 从 Machine.Endpoint JSON 解析 `CommunicationStyle`
2. `TransportRegistry.Resolve(KubernetesAgent)` → `KubernetesAgentTransport`

```csharp
public sealed class KubernetesAgentTransport(
    KubernetesAgentEndpointVariableContributor variables,
    KubernetesAgentScriptContextWrapper scriptWrapper,
    HalibutMachineExecutionStrategy strategy)
    : DeploymentTransport(
        CommunicationStyle.KubernetesAgent, variables, scriptWrapper, strategy,
        ExecutionLocation.RemoteTentacle,
        ExecutionBackend.HalibutScriptService,
        requiresContextPreparationForPackagedPayload: false);
```

5 行声明式绑定，关键元数据：

- **ExecutionLocation.RemoteTentacle** — 脚本在远端 agent 执行
- **ExecutionBackend.HalibutScriptService** — 通过 Halibut RPC 通信
- **requiresContextPreparationForPackagedPayload: false** — 打包负载不在 server 端做 context wrapping（agent 内部有 kubeconfig）

### 3.2 KubernetesAgent vs KubernetesApi 的核心区别

| 维度 | KubernetesAgent | KubernetesApi |
|------|----------------|---------------|
| 认证方式 | Agent Pod 的 ServiceAccount（无需凭据） | 数据库存储的 Token/Cert/AK/SK |
| 贡献的变量 | **3 个**（Namespace, SuppressEnvLog, PrintVars） | **13+ 个**（ClusterUrl, Token, Cert, ...） |
| 账号/证书解析 | **无**（ParseResourceReferences 返回空） | 解析 AccountId + CertificateId |
| Script Wrapping | 仅 `kubectl config set-context --namespace` | 完整 kubeconfig 构建（cluster, user, context） |
| 执行位置 | 远端 Tentacle Pod | 本地进程 |

### 3.3 变量贡献

`KubernetesAgentEndpointVariableContributor` 只贡献 3 个变量：

```csharp
EndpointVariableFactory.Make(KubernetesProperties.LegacyNamespace, endpoint.Namespace ?? "")
EndpointVariableFactory.Make(KubernetesScriptProperties.SuppressEnvironmentLogging, "False")
EndpointVariableFactory.Make(KubernetesCommonVariableNames.PrintEvaluatedVariables, "True")
```

因为 agent pod 已经有 kubeconfig，不需要 ClusterUrl、Token、Certificate 等。

---

## 4. Action Handler 准备 — YAML 生成

### 4.1 Handler 解析

`ActionHandlerRegistry.Resolve(action)` → `KubernetesYamlActionHandler`（ActionType = `Squid.KubernetesDeployContainers`）

### 4.2 PrepareAsync 流程

```csharp
public async Task<ActionExecutionResult> PrepareAsync(ActionExecutionContext ctx, CancellationToken ct)
{
    var generator = _yamlGenerators.FirstOrDefault(g => g.CanHandle(ctx.Action));
    await ResolveContainerImagesAsync(ctx, ct);
    var secretYaml = await GenerateFeedSecretAsync(ctx, ct);
    var yamlFiles = await generator.GenerateAsync(ctx.Step, ctx.Action, ct);

    return new ActionExecutionResult
    {
        CalamariCommand = "calamari-kubernetes-deploy",
        ExecutionMode = ExecutionMode.PackagedPayload,
        PayloadKind = PayloadKind.YamlBundle,
        Files = yamlFiles,
        Syntax = ScriptSyntax.PowerShell
    };
}
```

### 4.3 YAML 生成

`KubernetesContainersActionYamlGenerator` 最多生成 5 个 YAML 文件：

| 文件 | 生成器 | 条件 |
|------|--------|------|
| `deployment.yaml` | `DeploymentResourceGenerator` | 始终生成 |
| `service.yaml` | `ServiceResourceGenerator` | 有 Service 配置时 |
| `configmap.yaml` | `ConfigMapResourceGenerator` | 有 ConfigMap 配置时 |
| `ingress.yaml` | `IngressResourceGenerator` | 有 Ingress 配置时 |
| `secret.yaml` | `SecretResourceGenerator` | 有 Secret 配置时 |
| `feedsecrets.yaml` | （内联生成） | `CreateFeedSecrets = "True"` 时 |

### 4.4 Container Image 解析

对每个 container 的 `PackageId` + `FeedId`：

1. 查 `ExternalFeed` 记录获取 feed URI
2. 匹配 `SelectedPackages` 获取 version
3. 拼接 `{feedUri}/{packageId}:{version}` → 写回 container 的 `Image` 属性

---

## 5. Script Context Wrapping — Agent 特殊处理

### 5.1 直接脚本（DirectScript）

`KubernetesAgentScriptContextWrapper` 只做 namespace 切换：

```bash
kubectl config set-context --current --namespace="production" > /dev/null 2>&1
<用户脚本>
```

比 `KubernetesApiScriptContextWrapper`（需要构建完整 kubeconfig）简单得多。

### 5.2 打包负载（PackagedPayload）— Deploy Containers 的情况

**不做 wrapping。** 原因链：

```
ExecutionMode = PackagedPayload
  → 不满足 `executionMode == ExecutionMode.DirectScript` 条件
  → WrapScriptIfApplicable 被跳过
```

且 `KubernetesAgentTransport.RequiresContextPreparationForPackagedPayload = false`，所以即使是 PackagedPayload 也不会在 server 端做 context preparation。Calamari 工具在 agent 内部自行处理 kubectl context。

---

## 6. Halibut 通信 — 完整协议

### 6.1 Endpoint URI 构造

```csharp
// HalibutMachineExecutionStrategy.cs
private static ServiceEndPoint? ParseMachineEndpoint(Machine machine)
{
    var uri = machine.Uri;
    if (string.IsNullOrEmpty(uri) && !string.IsNullOrEmpty(machine.PollingSubscriptionId))
        uri = $"poll://{machine.PollingSubscriptionId}/";
    return new ServiceEndPoint(uri, machine.Thumbprint, HalibutTimeoutsAndLimits.RecommendedValues());
}
```

KubernetesAgent 的 `Machine.Uri` 通常为空（agent 在防火墙后面无法被直接连接），使用 `poll://{subscriptionId}/` scheme，告诉 Halibut 将 RPC 调用入队等 agent 轮询。

### 6.2 TLS 双向信任模型

```
┌─────────────────┐           ┌──────────────────┐
│   Squid Server   │           │   Tentacle Pod   │
│                 │           │                  │
│ HalibutRuntime  │←──TLS────│ HalibutRuntime   │
│   listens:10943 │ outbound  │   polls server   │
│                 │ connection│                  │
│ Trust store:    │           │ Trust store:     │
│  - agent1 thumb │           │  - server thumb  │
│  - agent2 thumb │           │                  │
└─────────────────┘           └──────────────────┘
```

**Server 端信任建立：**

`HalibutTrustInitializer`（IStartable，启动时运行）：

```csharp
var machines = machineDataProvider.GetTrustedPollingMachinesAsync();
foreach (var machine in machines)
    halibutRuntime.Trust(machine.Thumbprint);
```

查询所有 `PollingSubscriptionId != NULL AND !IsDisabled` 的 machine，将 thumbprint 加入信任。

**Agent 端信任建立：**

注册时从 server API 获取 `ServerThumbprint`，在 `StartPolling` 时调用 `_runtime.Trust(serverThumbprint)`。

**握手过程：**

1. Agent 主动发起 TCP 出站连接到 server:10943
2. TLS 双向认证：双方出示 X.509 自签名证书
3. Server 验证 agent thumbprint ∈ 信任列表
4. Agent 验证 server thumbprint ∈ 信任列表
5. Agent 发送 long-poll 请求，标识 `poll://{subscriptionId}/`
6. Server 有 RPC 调用时通过此连接下发

### 6.3 执行计划解析

`ScriptExecutionPlanFactory.Create(request)` 根据 `ExecutionMode`：

- `PackagedPayload` → `PackagedPayloadExecutionPlan`（Deploy Containers 走此路径）
- `DirectScript` → `DirectScriptExecutionPlan`

### 6.4 CalamariPayloadBuilder — 打包传输内容

```csharp
public CalamariPayload Build(ScriptExecutionRequest request)
{
    var packageBytes = _yamlNuGetPacker.CreateNuGetPackageFromYamlBytes(request.Files);
    var (variableBytes, sensitiveBytes, password) =
        ScriptExecutionHelper.CreateVariableFileContents(request.Variables);

    return new CalamariPayload
    {
        PackageFileName = $"squid.{request.ReleaseVersion}.nupkg",
        PackageBytes = packageBytes,
        VariableBytes = variableBytes,
        SensitiveBytes = sensitiveBytes,
        SensitivePassword = password,
        TemplateBody = UtilService.GetEmbeddedScriptContent("DeployByCalamari.ps1")
    };
}
```

三步打包：

1. **YAML → NuGet**：deployment.yaml, service.yaml 等打包为 `squid.1.0.0.nupkg`
2. **变量序列化**：非敏感 → 明文 JSON；敏感 → AES 加密（随机密码 `Guid.NewGuid().ToString("N")`）
3. **模板加载**：`DeployByCalamari.ps1` 嵌入资源

### 6.5 StartScriptCommand 构造

```csharp
var scriptFiles = new[]
{
    new ScriptFile(payload.PackageFileName,
        DataStream.FromBytes(payload.PackageBytes), null),
    new ScriptFile("variables.json",
        DataStream.FromBytes(payload.VariableBytes), null),
    new ScriptFile("sensitiveVariables.json",
        DataStream.FromBytes(payload.SensitiveBytes), payload.SensitivePassword)
};

var command = new StartScriptCommand(
    scriptBody,
    ScriptIsolationLevel.FullIsolation,
    TimeSpan.FromMinutes(30),
    null,
    Array.Empty<string>(),
    scriptFiles);
```

| 字段 | 值 |
|------|------|
| `ScriptBody` | `DeployByCalamari.ps1` 模板，`{{PackageFilePath}}` 等占位符已替换 |
| `Isolation` | `FullIsolation` |
| `Files[0]` | `squid.1.0.0.nupkg`（YAML 打包） |
| `Files[1]` | `variables.json`（明文变量） |
| `Files[2]` | `sensitiveVariables.json`（AES 加密） + `EncryptionPassword` |

`DataStream` 是 Halibut 的流式二进制传输抽象，文件内容在 TLS 连接上分块传输。

### 6.6 DeployByCalamari.ps1 模板

模板替换后生成的脚本（示例）：

```powershell
$squidCalamari = Get-Command "squid-calamari" -ErrorAction SilentlyContinue
if ($null -eq $squidCalamari) { Write-Error "squid-calamari not found in PATH"; Exit 1 }

$commandArgs = @(
    "apply-yaml",
    "--file=.\squid.1.0.0.nupkg",
    "--variables=.\variables.json"
)
if (".\sensitiveVariables.json" -ne "") {
    $commandArgs += "--sensitive=.\sensitiveVariables.json"
    $commandArgs += "--password=<generated-password>"
}
& $squidCalamari.Source @commandArgs
```

此脚本调用 `squid-calamari apply-yaml`，解包 NuGet，读取 YAML 文件，执行变量替换，然后 `kubectl apply` 每个资源。

### 6.7 Halibut RPC 调用序列

```
Server                                    Agent (Tentacle)
  |                                          |
  |  ←────── TLS long-poll connection ───────|  (agent 主动连接)
  |                                          |
  |── StartScriptAsync(command) ───────────→ |  (RPC 序列化下发)
  |  ←─── ScriptTicket ─────────────────────|  (返回 ticket)
  |                                          |
  |── GetStatusAsync(ticket, seq=0) ───────→ |  (1s 后)
  |  ←─── {Running, logs: [...]} ───────────|
  |                                          |
  |── GetStatusAsync(ticket, seq=5) ───────→ |  (1.5s 后)
  |  ←─── {Running, logs: [...]} ───────────|
  |                                          |
  |── GetStatusAsync(ticket, seq=12) ──────→ |  (2.25s 后)
  |  ←─── {Complete, exit=0, logs: [...]} ──|
  |                                          |
  |── CompleteScriptAsync(ticket, seq=18) ─→ |  (获取最终日志)
  |  ←─── {Complete, exit=0, finalLogs} ────|
  |                                          |
```

**轮询策略（`HalibutScriptObserver`）：**

- 初始间隔 1s
- 每次 × 1.5，最大 10s
- 超时 30 分钟 → 调用 `CancelScriptAsync`
- 日志通过 `lastLogSequence` 增量收集，去重

---

## 7. Agent 端执行 — 两种后端

### 7.1 路径选择

由 `KubernetesSettings.UseScriptPods` 决定：

| UseScriptPods | 后端 | 执行方式 |
|---------------|------|---------|
| `false` | `LocalScriptService` | Tentacle Pod 内直接 bash/pwsh 执行 |
| `true` | `ScriptPodService` | 创建独立的 Kubernetes Script Pod |

### 7.2 LocalScriptService 路径

**StartScript：**

1. 创建临时目录 `/tmp/squid-tentacle-{ticketId}/`
2. 写入 `script.sh`（DeployByCalamari.ps1 内容）
3. 通过 `DataStream.Receiver().SaveToAsync()` 写入 `.nupkg`、`variables.json`、`sensitiveVariables.json`
4. 如果有 `EncryptionPassword`，写入 `sensitiveVariables.json.key`
5. 检测到 `variables.json` 存在 → 启动 `squid-calamari run-script --script=... --variables=... --sensitive=...`
6. 异步读取 stdout/stderr → `ConcurrentQueue<ProcessOutput>`

**GetStatus：** 排空 queue，返回 Running/Complete + 日志

**CompleteScript：** 等待进程退出（30s），排空最终日志，清理目录

### 7.3 ScriptPodService 路径（UseScriptPods=true）

**StartScript：**

1. 写文件到 PVC 共享存储：`/squid/work/{ticketId}/`
2. `KubernetesPodManager.CreatePod(ticketId)` 创建 K8s Pod

**Pod Spec（含 init container）：**

```
Pod: squid-script-{ticketId[0:12]}
├── Labels: managed-by=kubernetes-agent, ticket-id={ticketId}
├── RestartPolicy: Never
├── ActiveDeadlineSeconds: 1800
├── ServiceAccountName: squid-script-sa
├── Volumes:
│   ├── workspace (PVC)
│   └── squid-bin (EmptyDir)
├── InitContainers:
│   └── copy-calamari:
│       image: {TentacleImage}  ← 从 Helm chart 注入
│       command: ["cp", "/squid/bin/squid-calamari", "/squid-bin/squid-calamari"]
│       mount: squid-bin → /squid-bin
└── Containers:
    └── script:
        image: bitnami/kubectl:latest  ← ScriptPodImage
        command: ["/squid/bin/squid-calamari"]
        args: ["run-script", "--script=/squid/work/{ticketId}/script.sh",
               "--variables=/squid/work/{ticketId}/variables.json"]
        mounts: workspace → /squid/work, squid-bin → /squid/bin
```

**GetStatus：** 读 pod phase（`Succeeded`/`Failed` → Complete，`null` → Running）+ `kubectl logs`

**CompleteScript：** 等待 pod 终止，读取 exit code，删除 pod，清理 workspace

---

## 8. 日志回收与输出变量

执行完成后，回到 `ExecuteActionResultsAsync`：

```
execResult = await strategy.ExecuteScriptAsync(request, ct)
  ↓
CaptureOutputVariables(actionResult, execResult.LogLines)
  → 解析 ##squid[setVariable name='X' value='Y' sensitive='True/False']
  → 写入 actionResult.OutputVariables
  ↓
PersistScriptOutputAsync(_ctx.Task.Id, execResult.LogLines, tc.Machine.Name, ct)
  → 持久化到 server_task_log 表
  ↓
if (!execResult.Success)
  → throw DeploymentScriptException
  ↓
CollectOutputVariables(result, step.Name, actionResult)
  → 生成 qualified name: Squid.Action.{StepName}.{VarName}
  → 加入 StepExecutionResult.OutputVariables
  → 后续 batch 可通过 #{Squid.Action.StepName.VarName} 引用
```

---

## 9. 完整调用图

```
ProcessAsync(serverTaskId)
│
├─ LoadDeploymentDataAsync
│   └─ 8 sub-steps → _ctx populated
│
├─ PrepareAllTargetsAsync
│   └─ foreach target:
│       ├─ CommunicationStyleParser.Parse → "KubernetesAgent"
│       ├─ TransportRegistry.Resolve → KubernetesAgentTransport
│       │   (Variables + ScriptWrapper + HalibutMachineExecutionStrategy)
│       ├─ LoadAuthenticationAsync → no-op (agent 无需凭据)
│       └─ ContributeEndpointVariables → 3 vars
│
├─ ExecuteDeploymentStepsAsync
│   └─ foreach step batch → foreach target:
│       ├─ BuildEffectiveVariables (base + endpoint)
│       ├─ ShouldExecuteStep (condition + role check)
│       ├─ PrepareStepActionsAsync
│       │   ├─ KubernetesYamlActionHandler.PrepareAsync
│       │   │   ├─ ResolveContainerImagesAsync (Feed + version → image ref)
│       │   │   ├─ GenerateFeedSecretAsync (optional)
│       │   │   └─ YamlGenerator → {deployment.yaml, service.yaml, ...}
│       │   └─ WrapScript → SKIPPED (PackagedPayload, not DirectScript)
│       │
│       └─ ExecuteActionResultsAsync
│           ├─ HalibutMachineExecutionStrategy.ExecuteScriptAsync
│           │   ├─ ParseMachineEndpoint → poll://{subscriptionId}/
│           │   ├─ HalibutClientFactory.CreateClient → IAsyncScriptService proxy
│           │   ├─ CalamariPayloadBuilder.Build
│           │   │   ├─ YAML → NuGet package
│           │   │   ├─ Variables → JSON + AES encrypted
│           │   │   └─ DeployByCalamari.ps1 template
│           │   ├─ StartScriptCommand(script, files[3])
│           │   │       ↓ Halibut TLS RPC ↓
│           │   │   ┌─────────────────────────────────────────┐
│           │   │   │ Agent: StartScript                       │
│           │   │   │   Write files to workspace               │
│           │   │   │   [ScriptPod] Create K8s Pod             │
│           │   │   │     InitContainer: cp squid-calamari     │
│           │   │   │     MainContainer: squid-calamari        │
│           │   │   │       apply-yaml → kubectl apply -f .    │
│           │   │   │   [Local] squid-calamari run-script      │
│           │   │   └─────────────────────────────────────────┘
│           │   └─ ObserveAndCompleteAsync
│           │       └─ poll loop: GetStatus (1s→1.5s→...→10s max)
│           │       └─ CompleteScript → final logs + cleanup
│           │
│           ├─ CaptureOutputVariables (##squid[setVariable])
│           ├─ PersistScriptOutputAsync → server_task_log
│           └─ CollectOutputVariables → next batch available
│
└─ RecordSuccessAsync / RecordFailureAsync
```

---

## 10. 变量安全传输

```
Variables (List<VariableDto>)
    │
    ├─ IsSensitive = false ──→ variables.json (明文 JSON)
    │                           传输: ScriptFile + DataStream (TLS 加密通道)
    │
    └─ IsSensitive = true  ──→ sensitiveVariables.json (AES-256 加密)
                                密钥: Guid.NewGuid().ToString("N")
                                传输: ScriptFile.EncryptionPassword 字段
                                Agent 端: 写 sensitiveVariables.json.key
                                Calamari: 用 key 文件解密后使用
```

双重保护：TLS 通道加密 + 敏感变量 AES 加密。

---

## 11. Tentacle 启动与注册流程

### 11.1 启动序列

1. **证书管理**（`TentacleCertificateManager`）：加载或创建 RSA 2048-bit 自签名证书（PKCS#12, CN=squid-tentacle, 5 年有效期），同时加载或创建 subscription ID（GUID）。
2. **Flavor 选择**：`TentacleFlavorResolver` 根据配置解析为 `KubernetesAgentFlavor`。
3. **注册**：`KubernetesAgentRegistrar` 调用 server 的 `/api/machines/register/kubernetes-agent` 端点，携带 subscription ID 和 thumbprint。Server 返回 `MachineId`、`ServerThumbprint`。
4. **ScriptBackend 创建**：根据 `KubernetesSettings.UseScriptPods` 选择 `LocalScriptService` 或 `ScriptPodService`。
5. **Halibut Host 创建并开始轮询**。

### 11.2 Halibut Host 构建

```csharp
public TentacleHalibutHost(X509Certificate2 tentacleCert, IScriptService scriptService, TentacleSettings settings)
{
    var asyncAdapter = new AsyncScriptServiceAdapter(scriptService);
    var serviceFactory = new DelegateServiceFactory();
    serviceFactory.Register<IScriptService, IScriptServiceAsync>(() => asyncAdapter);

    _runtime = new HalibutRuntimeBuilder()
        .WithServiceFactory(serviceFactory)
        .WithServerCertificate(tentacleCert)
        .WithHalibutTimeoutsAndLimits(HalibutTimeoutsAndLimits.RecommendedValues())
        .Build();
}
```

### 11.3 轮询启动

```csharp
public void StartPolling(string serverThumbprint, string subscriptionId)
{
    _runtime.Trust(serverThumbprint);
    var pollUri = $"poll://{subscriptionId}/";
    var serverEndpoint = new ServiceEndPoint(new Uri(_settings.ServerCommsUrl), serverThumbprint, ...);
    _runtime.Poll(pollUri, serverEndpoint, CancellationToken.None);
}
```

启动后 agent 持续保持到 server 的 TLS 出站连接，等待 RPC 调用下发。

---

## 12. Calamari 命令 vs 直接脚本

### 12.1 区分方式

由 `ActionExecutionResult.ResolveExecutionMode()` 决定：

| 模式 | 条件 | 使用场景 |
|------|------|---------|
| **PackagedPayload** | `CalamariCommand != null` | Deploy Containers, Deploy YAML, Helm Upgrade |
| **DirectScript** | `CalamariCommand == null` | Run Script |

### 12.2 PackagedPayload 路径（Deploy Containers）

1. YAML 文件打包为 NuGet package
2. 变量序列化为 `variables.json` + `sensitiveVariables.json`
3. `DeployByCalamari.ps1` 模板填充文件路径和密码
4. 三个文件作为 `ScriptFile[]` 发送
5. Agent 端 `squid-calamari apply-yaml` 解包、变量替换、`kubectl apply`

### 12.3 DirectScript 路径（Run Script）

1. 用户脚本直接作为 `StartScriptCommand.ScriptBody`
2. Handler 生成的文件（如 inline YAML）作为 `ScriptFile[]`
3. 变量同样作为 `ScriptFile[]` 的 `variables.json` / `sensitiveVariables.json`
4. KubernetesAgent 直接脚本会被 `KubernetesAgentScriptContextWrapper` 包装 `kubectl config set-context`
5. Agent 端通过 `bash script.sh` 或 `squid-calamari run-script` 执行

---

## 13. 关键架构观察

1. **KubernetesAgent 目标在 server 端不需要任何认证凭据。** Agent pod 通过 Kubernetes ServiceAccount 认证到集群。这就是为什么 `ParseResourceReferences` 返回空且只贡献 3 个端点变量。

2. **PackagedPayload 在 KubernetesAgent 上跳过 server 端 context wrapping**（`requiresContextPreparationForPackagedPayload: false`）。Calamari 工具在 agent 内部原生处理 kubectl context。

3. **`poll://` scheme** 是允许与防火墙后 agent 通信的关键机制。Agent 发起出站连接；server 仅通过 subscription ID 将 RPC 调用入队。

4. **Agent 上的双执行后端**：`LocalScriptService` 用于简单部署（直接运行 bash），`ScriptPodService` 用于隔离执行（为每个脚本创建临时 Kubernetes pod）。

5. **信任模型是双向的**：server 信任 agent thumbprint（启动时从数据库加载），agent 信任 server thumbprint（注册时接收）。双方证书均为自签名。

6. **变量安全**：敏感变量从不以明文传输。它们使用每次执行随机生成的密码进行 AES 加密，密码本身通过 `ScriptFile.EncryptionPassword` 字段在已加密的 TLS 连接上传输。
