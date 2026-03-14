# Squid K8s Deployment Architecture

## Halibut

Halibut is the secure RPC communication protocol between Squid Server and Tentacle Agents.

```
Squid Server                          Target Machine
┌──────────────┐     Halibut TLS     ┌──────────────┐
│ SquidModule   │ ──────────────────→ │  Tentacle    │
│  └ HalibutRuntime                   │  Agent       │
│     (X509 cert)│    RPC: StartScript│  (listening)  │
│               │ ←──────────────────│  Port 10933   │
│               │    RPC: GetStatus   │              │
│               │    RPC: Complete    │  Executes:   │
│               │                     │  - kubectl   │
│               │                     │  - helm      │
│               │                     │  - calamari  │
└──────────────┘                     └──────────────┘
```

`HalibutModule` creates a singleton `HalibutRuntime` with an X509 certificate, used by `DeploymentTaskExecutor` to establish encrypted RPC connections to target machines.

## Required Configuration Entities

```
1. Machine (deployment target)
   ├─ Uri = "https://tentacle.example.com:10933"    <- Tentacle listening address
   ├─ Thumbprint = "ABC123..."                       <- TLS certificate thumbprint
   ├─ EnvironmentIds = "1"                           <- which environment(s)
   ├─ Roles = "k8s-prod"                             <- role tags (step filtering)
   └─ Endpoint = JSON:
       {
         "CommunicationStyle": "Kubernetes",
         "ClusterUrl": "https://k8s-api:6443",
         "Namespace": "production",
         "SkipTlsVerification": "False",
         "AccountId": "5"
       }

2. DeploymentAccount (auth credentials)
   ├─ AccountType = Token | UsernamePassword | ClientCertificate
   └─ Token = "eyJhbGc..."  (or Username/Password, or ClientCert)

3. Project -> Release -> Deployment
   └─ EnvironmentId = 1  (must match Machine's EnvironmentIds)

4. DeploymentProcess (step definitions)
   └─ Step: "Deploy to K8s"
       ├─ Properties: { "Squid.Action.TargetRoles": "k8s-prod" }
       └─ Action: ActionType = "Squid.KubernetesRunScript" (or other K8s types)
```

## Execution Flow

```
ProcessAsync(serverTaskId)
│
├─ 1. LoadTaskAsync -> set ServerTask to Executing
├─ 2. LoadDeploymentAsync -> load Deployment + Release
├─ 3. Load DeploymentProcess Snapshot -> convert to Step DTOs
├─ 4. ResolveVariablesAsync (project variables + snapshot variables)
├─ 5. FindTargetsAsync (query Machine table by EnvironmentId)
├─ 6. PreFilterTargetsByRoles (filter machines by step TargetRoles)
│
└─ 7. foreach Target Machine:
    │
    ├─ LoadAccountAsync
    │   Parse Endpoint JSON -> CommunicationStyle = "Kubernetes"
    │   Match KubernetesApiEndpointVariableContributor
    │   Load DeploymentAccount from AccountId (Token/cert)
    │
    ├─ ContributeEndpointVariablesAsync
    │   Inject variables: ClusterUrl, Namespace, Token, SkipTls...
    │   These fill into the kubectl context script template later
    │
    ├─ ExtractCalamariAsync (if Calamari needed)
    │   Extract Calamari package on target machine via Halibut
    │
    └─ PrepareAndExecuteStepsAsync
        │
        foreach Step (by StepOrder):
        │  ├─ ShouldExecuteStep?
        │  │   (1) IsDisabled? -> skip
        │  │   (2) Condition: Success/Failure/Always? -> check previous step result
        │  │   (3) MatchesTargetRoles? -> machine roles ∩ step roles (OR logic)
        │  │
        │  foreach Action (by ActionOrder):
        │     ├─ ShouldExecuteAction?
        │     │   IsDisabled? Environment match? Channel match?
        │     │
        │     ├─ ActionHandlerRegistry.Resolve(action)
        │     │   Match by ActionType:
        │     │
        │     │   ┌─────────────────────────────────────────────────┐
        │     │   │ ActionType                     │ Handler         │
        │     │   ├─────────────────────────────────────────────────┤
        │     │   │ Squid.KubernetesRunScript      │ RunScript       │
        │     │   │ Squid.KubernetesDeployRawYaml  │ DeployYaml      │
        │     │   │ Squid.HelmChartUpgrade         │ HelmUpgrade     │
        │     │   │ Squid.KubernetesDeployContainers│ YamlAction     │
        │     │   └─────────────────────────────────────────────────┘
        │     │
        │     ├─ handler.PrepareAsync() -> ActionExecutionResult
        │     │   Returns: ScriptBody + Files + (optional) CalamariCommand
        │     │
        │     └─ Wrap script:
        │        KubernetesApiScriptContextWrapper.WrapScript()
        │        Load KubectlContext.sh template, substitute:
        │        {{ClusterUrl}}, {{Token}}, {{Namespace}}...
        │        Final script = kubectl config setup + user script
        │
        Execute:
        ├─ No CalamariCommand -> ExecuteDirectScriptAsync
        │   Halibut RPC -> Tentacle -> execute wrapped script
        │
        └─ Has CalamariCommand -> ExecuteCalamariActionAsync
            Package YAML/variables -> Halibut RPC -> Tentacle -> Calamari engine

        Poll results:
        ObserveDeploymentScriptAsync()
          Loop GetStatusAsync -> fetch logs
          CompleteScriptAsync -> finalize
          Parse Output Variables (##octopus[setVariable])
```

## K8s Action Handlers

| ActionType | Handler | Execution Mode | What It Does |
|------------|---------|---------------|--------------|
| Squid.KubernetesRunScript | KubernetesRunScriptActionHandler | Direct script | User-provided bash/PowerShell, wrapped with kubectl context |
| Squid.KubernetesDeployRawYaml | KubernetesDeployYamlActionHandler | Direct script | `kubectl apply -f` with inline YAML files |
| Squid.HelmChartUpgrade | HelmUpgradeActionHandler | Direct script | Helm upgrade from template (HelmUpgrade.sh/.ps1) |
| Squid.KubernetesDeployContainers | KubernetesYamlActionHandler | Calamari | Full manifest generation (Deployment, Service, ConfigMap) via Calamari engine |

## Variable Contribution Chain

```
KubernetesApiEndpointVariableContributor:
  CanHandle("Kubernetes") = true
  ContributeVariables():
    - Squid.Action.Kubernetes.ClusterUrl
    - Squid.Action.Kubernetes.Namespace (default: "default")
    - Squid.Action.Kubernetes.SkipTlsVerification
    - Squid.Action.Kubernetes.ClusterCertificate
    - Squid.Account.Token (sensitive)
    - Squid.Account.Username / Password / ClientCertificateData
  ContributeAdditionalVariablesAsync():
    - ContainerImage from external feed + release version
```

## Script Context Wrapping

```
KubernetesApiScriptContextWrapper:
  CanWrap("Kubernetes") = true
  WrapScript():
    Load KubectlContext.sh (or .ps1) template
    Substitute: {{ClusterUrl}}, {{Token}}, {{Namespace}}, {{SkipTlsVerification}}...
    Result = kubectl config commands + user script
```

## Key Concepts

| Concept | Description |
|---------|-------------|
| Tentacle | Agent process on target machine, listens on port 10933 |
| Halibut | Encrypted RPC protocol (Server <-> Tentacle) |
| Machine | Deployment target entity (Uri + Thumbprint + Endpoint JSON) |
| CommunicationStyle | Target type identifier in Endpoint JSON ("Kubernetes", "Ssh", etc.) |
| Account | Auth credentials (Token/Cert/Password) for the target |
| Calamari | Deployment engine executed on target via Halibut |
| Role/Tag | Step-to-machine association labels (OR logic matching) |
| ActionHandler | Per-ActionType handler that prepares scripts/files |
| ScriptContextWrapper | Per-CommunicationStyle script wrapping (kubectl context, ssh, etc.) |
| EndpointVariableContributor | Per-CommunicationStyle variable injection |

## Extension Pattern

To add a new target type (e.g., SSH, Azure, Docker):
1. Implement `IEndpointVariableContributor` (match CommunicationStyle, inject variables)
2. Implement `IScriptContextWrapper` (match CommunicationStyle, wrap scripts)
3. (Optional) Implement `IActionHandler` subclasses for custom action types
4. No changes to `DeploymentTaskExecutor` required (fully generic)

## Minimum Configuration for a Successful K8s Deployment

1. Tentacle Agent running on a machine with kubectl access to K8s API
2. Machine record pointing to that Tentacle (Uri + Thumbprint + Endpoint JSON with ClusterUrl)
3. DeploymentAccount storing K8s auth Token (Service Account Token)
4. Squid Server holding Halibut certificate (SelfCert.Base64)
5. Complete Project -> Process -> Release -> Deployment chain
