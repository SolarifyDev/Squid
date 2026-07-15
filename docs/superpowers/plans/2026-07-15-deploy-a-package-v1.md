# Deploy a Package V1 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 在 Squid / SquidWeb 实现 `Squid.TentaclePackage` 的 Deploy a Package 核心闭环：配置外部 NuGet package、Release 固化版本、Server 下载并 SHA-256 校验、上传到 Tentacle/SSH、安全提交到持久安装目录，并运行 PreDeploy/PostDeploy conventions。

**Architecture:** 采用语义化 Intent + 按 transport 复用现有能力。`DeployPackageActionHandler` 产出 `DeployPackageIntent`；Tentacle renderer/strategy 传原始 archive 并调用 Calamari `deploy-package`；SSH renderer/strategy 复用 package cache/staging + Bash 生命周期；SquidWeb 新增 `DeployPackageEditor` 并注册到 `STEP_EDITOR_MAP`。

**Tech Stack:** .NET / xUnit + Moq + Shouldly；Calamari + Halibut Tentacle；SSH.NET；React + TypeScript + Ant Design + Vitest + Testing Library；NuGet Feed `.nupkg`/`.zip`。

## Global Constraints

- 工作区：后端 `/Users/nacho/Documents/GitHub/SolarifyDev/Squid`，前端 `/Users/nacho/Documents/GitHub/SolarifyDev/SquidWeb`；均在 `feature/deploy-package-v1` 分支上直接工作，不创建 git worktree。
- Action type 固定：`Squid.TentaclePackage`。
- Package 来源：外部 NuGet Feed only；格式：`.nupkg` / `.zip`。
- Server 下载后上传目标机，不下发 Feed 凭据。
- 版本事实来源：Release 固定版本；部署不得回退 latest，也不得使用 action property 版本兜底。
- Hash：全链路统一 SHA-256，字段名仍为 `Hash`，禁止 MD5/SHA-256 混用；hex 统一小写。
- 目标：Tentacle Listening、Tentacle Polling、SSH POSIX/Bash。
- 安装目录：默认版本化目录，或自定义绝对目录。
- Convention：Windows `.ps1`，Linux/SSH `.sh`；工作目录 = 最终安装目录；可读普通/敏感变量。
- V1 不做：内置 package repository、GitHub/Helm/Docker 源、目标机直连 Feed、purge/preserve、配置转换/structured vars/文件替换、UI 自定义部署脚本、skip-if-installed、旧版本 retention、自动回滚/current 软链接、Kubernetes/Server Worker、自动提权。
- 测试框架不新增；禁止 barrel exports；不生成/手改 `.d.ts`。
- 不 revert 用户现有改动（尤其 `SquidWeb/pnpm-lock.yaml`）；不碰无关 `.env`。
- 始终使用简体中文回复；只做本计划明确要求的事。

---

## File Structure

### 后端新增
- `src/Squid.Core/Services/DeploymentExecution/Handlers/DeployPackageActionHandler.cs` — 解析 action/Release，产出 `DeployPackageIntent`
- `src/Squid.Core/Services/DeploymentExecution/Packages/PackageInstallationPath.cs` — 路径片段安全化 + 自定义路径校验
- `src/Squid.Core/Services/DeploymentExecution/Targets/Ssh/Packages/SshPackageDeploymentScriptBuilder.cs` — 生成 SSH 安装生命周期 Bash
- `src/Squid.Calamari/Host/DeployPackageCliCommandHandler.cs` — CLI 入口 `deploy-package`
- `src/Squid.Calamari/Commands/Package/DeployPackageCommand.cs` — 命令编排
- `src/Squid.Calamari/Commands/Package/PackageInstallationCoordinator.cs` — staging/hash/extract/commit/conventions

### 后端修改
- `src/Squid.Message/Constants/SpecialVariables.cs` — 安装目录模式/输出变量常量
- `src/Squid.Message/Models/Deployments/Execution/ExecutionSemantics.cs` — `PayloadKind.PackageArchive`
- `src/Squid.Core/Services/Deployments/Process/DeploymentPackageReferenceService.cs` — `PackageReferenceName = PackageId`
- `src/Squid.Core/Services/Deployments/Release/ReleaseService.cs` — 拒绝空版本
- `src/Squid.Core/Services/DeploymentExecution/Packages/PackageAcquisitionService.cs` — SHA-256 + 基础校验
- `src/Squid.Core/Services/DeploymentExecution/Packages/PackageAcquisitionResult.cs` / `PackageRequirement.cs` / `PackageStagingPlan.cs` / `DeploymentPackageContext.cs` — 注释改为 SHA-256
- `src/Squid.Core/Services/DeploymentExecution/Intents/DeployPackageIntent.cs` — 目录模式/路径片段/自定义路径
- `src/Squid.Core/Services/DeploymentExecution/Variables/IntentVariableExpander.cs` — 展开自定义目录
- `src/Squid.Core/Services/DeploymentExecution/Pipeline/Phases/6_ExecuteStepsPhase.Execute.cs` — acquisition 失败终止
- `src/Squid.Core/Services/DeploymentExecution/Targets/Tentacle/Transport/TentacleListeningTransport.cs` / `TentaclePollingTransport.cs` / `Ssh/Transport/SshTransport.cs` — 支持 `TentaclePackage`
- `src/Squid.Core/Services/DeploymentExecution/Targets/Tentacle/Rendering/TentacleListeningIntentRenderer.cs` / `TentaclePollingIntentRenderer.cs` — 渲染 `DeployPackageIntent`
- `src/Squid.Core/Services/DeploymentExecution/Targets/Tentacle/Transport/HalibutMachineExecutionStrategy.cs` — 原始 archive + `deploy-package`
- `src/Squid.Core/Services/DeploymentExecution/Infrastructure/CalamariPayloadBuilder.cs` / `ICalamariPayloadBuilder.cs` / `CalamariPayload.cs` — package-archive 分支
- `src/Squid.Core/Services/DeploymentExecution/Targets/Ssh/Rendering/SshIntentRenderer.cs`
- `src/Squid.Core/Services/DeploymentExecution/Targets/Ssh/Transport/SshExecutionStrategy.cs`
- `src/Squid.Core/Services/DeploymentExecution/Targets/Ssh/Connectivity/SshPaths.cs`
- `src/Squid.Core/Services/DeploymentExecution/Targets/Ssh/Connectivity/SshFileTransfer.cs`
- `src/Squid.Core/Services/DeploymentExecution/Targets/Ssh/Connectivity/SshCachedPackageLookup.cs`
- `src/Squid.Core/Services/DeploymentExecution/Targets/Ssh/Connectivity/SshPackageTransfer.cs`
- `src/Squid.Calamari/Host/CoreCommandModule.cs`
- 相关既有测试与注释中的 MD5 断言

### 前端新增/修改
- `SquidWeb/src/pages/project-detail/step-editors/deploy-package/deploy-package-model.ts`
- `SquidWeb/src/pages/project-detail/step-editors/deploy-package/DeployPackageEditor.tsx`
- `SquidWeb/src/pages/project-detail/step-editors/deploy-package/deploy-package-model.test.ts`
- `SquidWeb/src/pages/project-detail/DeploymentProcess.tsx` — 注册 editor

---

### Task 1: Package Identity + SHA-256 Acquisition + 失败终止

**Files:**
- Modify: `src/Squid.Core/Services/Deployments/Process/DeploymentPackageReferenceService.cs:177-200`
- Modify: `src/Squid.Core/Services/DeploymentExecution/Packages/PackageAcquisitionService.cs`
- Modify: `src/Squid.Core/Services/DeploymentExecution/Packages/Staging/PackageRequirement.cs`
- Modify: `src/Squid.Core/Services/DeploymentExecution/Packages/Staging/PackageStagingPlan.cs`
- Modify: `src/Squid.Core/Services/DeploymentExecution/Lifecycle/DeploymentPackageContext.cs`
- Modify: `src/Squid.Core/Services/DeploymentExecution/Pipeline/Phases/6_ExecuteStepsPhase.Execute.cs:624-820`
- Modify: `src/Squid.Core/Services/Deployments/Release/ReleaseService.cs:244-261`
- Modify: `src/Squid.Core/Services/DeploymentExecution/Targets/Ssh/Connectivity/SshFileTransfer.cs`
- Modify: `src/Squid.Core/Services/DeploymentExecution/Targets/Ssh/Connectivity/SshCachedPackageLookup.cs`
- Modify: `tests/Squid.UnitTests/Services/Deployments/Process/DeploymentPackageReferenceServiceTests.cs`
- Modify: `tests/Squid.UnitTests/Services/Deployments/Execution/PackageAcquisitionServiceTests.cs`
- Modify: `tests/Squid.UnitTests/Services/Deployments/Ssh/SshFileTransferTests.cs`
- Create: `tests/Squid.UnitTests/Services/Deployments/Execution/AcquirePackagesFailureTerminationTests.cs`
- Create: `tests/Squid.UnitTests/Services/Deployments/Release/ReleaseSelectedPackageValidationTests.cs`

**Interfaces:**
- Consumes: `PackageReferenceDto { ActionName, PackageReferenceName, PackageId, FeedId }`；`IPackageAcquisitionService.AcquireAsync(ExternalFeed feed, string packageId, string version, int deploymentId, CancellationToken ct)`；`AcquirePackagesAsync` 使用 `pkg.PackageReferenceName` 作为 package ID
- Produces: action-level `PackageReferenceName == PackageId`；`PackageAcquisitionResult.Hash` = lowercase SHA-256 hex；acquisition 任一失败抛 `DeploymentAbortedException`；Release 拒绝空 `Version`

- [ ] **Step 1: 写失败测试 — PackageReferenceName 必须等于 PackageId**

在 `DeploymentPackageReferenceServiceTests.cs` 中修改现有断言，并新增显式测试：

```csharp
[Fact]
public async Task GetPackageReferences_ActionLevel_UsesPackageIdAsReferenceName()
{
    var actions = new List<DeploymentAction>
    {
        new() { Id = 1, Name = "DeployPackage", StepId = 100 }
    };
    var properties = new List<DeploymentActionProperty>
    {
        new() { ActionId = 1, PropertyName = SpecialVariables.Action.PackageFeedId, PropertyValue = "5" },
        new() { ActionId = 1, PropertyName = SpecialVariables.Action.PackageId, PropertyValue = "Acme.Web" }
    };
    SetupBasicProjectPipeline(actions, properties);

    var refs = await CreateService().GetPackageReferencesAsync(1);

    refs.Count.ShouldBe(1);
    refs[0].PackageId.ShouldBe("Acme.Web");
    refs[0].PackageReferenceName.ShouldBe("Acme.Web");
    refs[0].FeedId.ShouldBe(5);
}

// 同步修改 BothContainerAndActionLevel 中 action-level 断言：
// refs.ShouldContain(r => r.PackageId == "k8s-manifests" && r.PackageReferenceName == "k8s-manifests");
```

- [ ] **Step 2: 运行测试确认失败**

Run:

```bash
dotnet test tests/Squid.UnitTests/Squid.UnitTests.csproj --filter "FullyQualifiedName~DeploymentPackageReferenceServiceTests.GetPackageReferences_ActionLevel_UsesPackageIdAsReferenceName"
```

Expected: FAIL，实际 `PackageReferenceName` 为空字符串。

- [ ] **Step 3: 最小实现 — PackageReferenceName = PackageId**

修改 `DetectActionLevelPackageReferences`：

```csharp
references.Add(new PackageReferenceDto
{
    ActionName = action.Name,
    PackageReferenceName = packageIdProp.PropertyValue,
    PackageId = packageIdProp.PropertyValue,
    FeedId = actionFeedId
});
```

- [ ] **Step 4: 写失败测试 — SHA-256 与空 packageId/version**

重写 `PackageAcquisitionServiceTests` 中 hash 相关断言：

```csharp
private static string ComputeSha256(byte[] bytes)
    => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();

[Fact]
public async Task AcquireAsync_Succeeds_ComputesLowercaseSha256Hash()
{
    _fetcherMock.Setup(f => f.FetchAsync(_feed, "nginx", "1.21.0", It.IsAny<CancellationToken>()))
        .ReturnsAsync(new PackageFetchResult(new Dictionary<string, byte[]>(), new List<string>(), SampleBytes));

    var result = await _sut.AcquireAsync(_feed, "nginx", "1.21.0", 123, CancellationToken.None);

    result.Hash.ShouldBe(ComputeSha256(SampleBytes));
    result.Hash.Length.ShouldBe(64);
    result.Hash.ShouldMatch("^[a-f0-9]{64}$");
}

[Theory]
[InlineData("", "1.0.0")]
[InlineData("   ", "1.0.0")]
[InlineData("pkg", "")]
[InlineData("pkg", "   ")]
public async Task AcquireAsync_BlankPackageIdOrVersion_Throws(string packageId, string version)
{
    await Should.ThrowAsync<InvalidOperationException>(
        () => _sut.AcquireAsync(_feed, packageId, version, 1, CancellationToken.None));
}
```

- [ ] **Step 5: 运行测试确认失败**

Run:

```bash
dotnet test tests/Squid.UnitTests/Squid.UnitTests.csproj --filter "FullyQualifiedName~PackageAcquisitionServiceTests"
```

Expected: FAIL，当前 hash 为 MD5（长度 32）且不校验空 ID/版本。

- [ ] **Step 6: 最小实现 — SHA-256 acquisition**

```csharp
public async Task<PackageAcquisitionResult> AcquireAsync(ExternalFeed feed, string packageId, string version, int deploymentId, CancellationToken ct)
{
    if (string.IsNullOrWhiteSpace(packageId))
        throw new InvalidOperationException("Package ID is required for package acquisition.");
    if (string.IsNullOrWhiteSpace(version))
        throw new InvalidOperationException($"Package version is required for package '{packageId}'.");
    if (feed is null)
        throw new InvalidOperationException($"Feed is required for package '{packageId}' v{version}.");

    var feedType = feed.FeedType ?? string.Empty;
    if (feedType.Contains("Helm", StringComparison.OrdinalIgnoreCase)
        || feedType.Contains("GitHub", StringComparison.OrdinalIgnoreCase)
        || feedType.Contains("Docker", StringComparison.OrdinalIgnoreCase))
    {
        throw new InvalidOperationException(
            $"Feed {feed.Id} type '{feed.FeedType}' is not a NuGet feed. Deploy a Package V1 only supports external NuGet feeds.");
    }

    var fetchResult = await packageContentFetcher.FetchAsync(feed, packageId, version, ct).ConfigureAwait(false);

    if (fetchResult.RawBytes.Length == 0)
        throw new InvalidOperationException($"Package {packageId} v{version} from feed {feed.Id} returned empty content.");

    var storageDir = PackageAcquisitionServiceExtensions.BuildPackageStoragePath(deploymentId);
    Directory.CreateDirectory(storageDir);
    var localPath = Path.Combine(storageDir, $"{packageId}.{version}.nupkg");
    await File.WriteAllBytesAsync(localPath, fetchResult.RawBytes, ct).ConfigureAwait(false);

    var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(fetchResult.RawBytes)).ToLowerInvariant();
    return new PackageAcquisitionResult(localPath, packageId, version, fetchResult.RawBytes.Length, hash);
}
```

同步把 `PackageRequirement` / `PackageStagingPlan` / `DeploymentPackageContext` 注释中的 MD5 改为 SHA-256。

- [ ] **Step 7: 写失败测试 — SSH hash 改为 SHA-256**

在 `SshFileTransferTests` 中新增/修改：

```csharp
[Fact]
public void ComputeLocalSha256_ReturnsLowercaseHex()
{
    var data = "hello"u8.ToArray();
    var hash = SshFileTransfer.ComputeLocalSha256(data);
    hash.ShouldBe(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(data)).ToLowerInvariant());
    hash.Length.ShouldBe(64);
}
```

并把生产代码中 `ComputeLocalMd5` / `CalculateRemoteMd5` / `md5sum` 全部切到 `ComputeLocalSha256` / `CalculateRemoteSha256` / `sha256sum`。旧 MD5 方法删除，避免混用。

- [ ] **Step 8: 写失败测试 — acquisition 失败必须终止**

```csharp
// tests/Squid.UnitTests/Services/Deployments/Execution/AcquirePackagesFailureTerminationTests.cs
public class AcquirePackagesFailureTerminationTests
{
    [Fact]
    public void AcquirePackages_Contract_AnyFailureMustAbortDeployment()
    {
        var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "../../../../../src/Squid.Core/Services/DeploymentExecution/Pipeline/Phases/6_ExecuteStepsPhase.Execute.cs"));
        var source = File.ReadAllText(path);
        source.ShouldContain("DeploymentAbortedException");
        source.ShouldContain("Failed to acquire package");
        source.ShouldContain("throw new DeploymentAbortedException");
    }
}
```

并在 `ReleaseSelectedPackageValidationTests`：

```csharp
[Fact]
public void PersistSelectedPackages_Contract_RejectsBlankVersion()
{
    var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
        "../../../../../src/Squid.Core/Services/Deployments/Release/ReleaseService.cs"));
    var source = File.ReadAllText(path);
    source.ShouldContain("Package version is required");
}
```

> 当前 `AcquirePackagesAsync` 是 `6_ExecuteStepsPhase` 的 private 成员，V1 用源码契约 + Task 7 集成测试锁定行为。若实现时能抽出可测协作对象，优先改成行为测试。

- [ ] **Step 9: 实现 acquisition 失败终止 + Release 空版本校验**

`AcquirePackagesAsync` 的失败路径：

```csharp
catch (Exception ex)
{
    Log.Error(ex, "[Deploy] Failed to acquire package {PackageId} v{Version}", pkg.PackageReferenceName, pkg.Version);
    await lifecycle.EmitAsync(new PackageDownloadFailedEvent(failedCtx), ct).ConfigureAwait(false);
    throw new DeploymentAbortedException(
        $"Package acquisition failed for '{pkg.PackageReferenceName}' v{pkg.Version} (feed {pkg.FeedId}): {ex.Message}");
}
```

对 invalid FeedId / feed not found 同样 `throw new DeploymentAbortedException(...)`，不再 `continue`。

`PersistSelectedPackagesAsync`：

```csharp
foreach (var sp in selectedPackages.Where(x => !string.IsNullOrWhiteSpace(x.ActionName)))
{
    if (string.IsNullOrWhiteSpace(sp.Version))
        throw new InvalidOperationException(
            $"Package version is required for action '{sp.ActionName}' package '{sp.PackageReferenceName}'.");
}
```

SSH：

```csharp
public static void UploadBytesVerified(SftpClient sftp, SshClient ssh, byte[] data, string remotePath)
{
    UploadBytes(sftp, data, remotePath);
    var localHash = ComputeLocalSha256(data);
    var remoteHash = CalculateRemoteSha256(ssh, remotePath);
    if (!string.IsNullOrEmpty(remoteHash) && !string.Equals(localHash, remoteHash, StringComparison.OrdinalIgnoreCase))
        throw new InvalidOperationException($"SHA-256 checksum mismatch for {remotePath}: local={localHash}, remote={remoteHash}");
}

public static string CalculateRemoteSha256(SshClient ssh, string remotePath)
{
    var result = SshRemoteShellExecutor.Execute(ssh, $"sha256sum \"{remotePath}\" | awk '{{ print $1 }}'", TimeSpan.FromSeconds(10));
    return result.ExitCode == 0 ? result.Output.Trim().ToLowerInvariant() : string.Empty;
}

internal static string ComputeLocalSha256(byte[] data)
    => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
```

`SshCachedPackageLookup` 同步改用 SHA-256。

- [ ] **Step 10: 运行相关测试确认通过**

Run:

```bash
dotnet test tests/Squid.UnitTests/Squid.UnitTests.csproj --filter "FullyQualifiedName~DeploymentPackageReferenceServiceTests|FullyQualifiedName~PackageAcquisitionServiceTests|FullyQualifiedName~SshFileTransfer|FullyQualifiedName~AcquirePackagesFailureTerminationTests|FullyQualifiedName~ReleaseSelectedPackageValidationTests"
```

Expected: PASS

- [ ] **Step 11: Commit（仅 Squid）**

```bash
git add \
  src/Squid.Core/Services/Deployments/Process/DeploymentPackageReferenceService.cs \
  src/Squid.Core/Services/DeploymentExecution/Packages/PackageAcquisitionService.cs \
  src/Squid.Core/Services/DeploymentExecution/Packages/Staging/PackageRequirement.cs \
  src/Squid.Core/Services/DeploymentExecution/Packages/Staging/PackageStagingPlan.cs \
  src/Squid.Core/Services/DeploymentExecution/Lifecycle/DeploymentPackageContext.cs \
  src/Squid.Core/Services/DeploymentExecution/Pipeline/Phases/6_ExecuteStepsPhase.Execute.cs \
  src/Squid.Core/Services/Deployments/Release/ReleaseService.cs \
  src/Squid.Core/Services/DeploymentExecution/Targets/Ssh/Connectivity/SshFileTransfer.cs \
  src/Squid.Core/Services/DeploymentExecution/Targets/Ssh/Connectivity/SshCachedPackageLookup.cs \
  tests/Squid.UnitTests/Services/Deployments/Process/DeploymentPackageReferenceServiceTests.cs \
  tests/Squid.UnitTests/Services/Deployments/Execution/PackageAcquisitionServiceTests.cs \
  tests/Squid.UnitTests/Services/Deployments/Ssh/SshFileTransferTests.cs \
  tests/Squid.UnitTests/Services/Deployments/Execution/AcquirePackagesFailureTerminationTests.cs \
  tests/Squid.UnitTests/Services/Deployments/Release/ReleaseSelectedPackageValidationTests.cs
git commit -m "$(cat <<'EOF'
fix(deploy-package): unify package identity and SHA-256 acquisition

Use PackageId as action-level PackageReferenceName, switch package hashes
to SHA-256, reject blank release package versions, and abort deployment
when any package acquisition fails.
EOF
)"
```

---

### Task 2: DeployPackageIntent + Path Planner + ActionHandler

**Files:**
- Modify: `src/Squid.Message/Constants/SpecialVariables.cs`
- Modify: `src/Squid.Core/Services/DeploymentExecution/Intents/DeployPackageIntent.cs`
- Create: `src/Squid.Core/Services/DeploymentExecution/Packages/PackageInstallationPath.cs`
- Create: `src/Squid.Core/Services/DeploymentExecution/Handlers/DeployPackageActionHandler.cs`
- Modify: `src/Squid.Core/Services/DeploymentExecution/Variables/IntentVariableExpander.cs`
- Modify: `src/Squid.Core/Services/DeploymentExecution/Targets/Tentacle/Transport/TentacleListeningTransport.cs`
- Modify: `src/Squid.Core/Services/DeploymentExecution/Targets/Tentacle/Transport/TentaclePollingTransport.cs`
- Modify: `src/Squid.Core/Services/DeploymentExecution/Targets/Ssh/Transport/SshTransport.cs`
- Create: `tests/Squid.UnitTests/Services/DeploymentExecution/Packages/PackageInstallationPathTests.cs`
- Create: `tests/Squid.UnitTests/Services/Deployments/Handlers/DeployPackageActionHandlerTests.cs`
- Create/Modify: capability / action-type drift 相关既有测试（若有 `SupportedActionTypes` 固定列表测试则更新）

**Interfaces:**
- Consumes: Task 1 的 Package identity；`ActionExecutionContext.SelectedPackages`；`SpecialVariables.Action.PackageFeedId/PackageId/CustomInstallationDirectory`
- Produces:

```csharp
// SpecialVariables.Action 新增：
public const string InstallationDirectoryMode = "Squid.Action.Package.InstallationDirectoryMode";
public const string InstallationDirectoryPath = "Squid.Action.Package.InstallationDirectoryPath"; // output

public sealed record PackageInstallationPathSegments(
    string EnvironmentName,
    string ProjectName,
    string PackageId,
    string Version);

public sealed record DeployPackageIntent : ExecutionIntent
{
    public required IntentPackageReference Package { get; init; }
    public string InstallationDirectoryMode { get; init; } = "Versioned";
    public string CustomInstallationDirectory { get; init; } = string.Empty;
    public required PackageInstallationPathSegments PathSegments { get; init; }
    public ScriptSyntax ScriptSyntax { get; init; } = ScriptSyntax.Bash;
}

public static class PackageInstallationPath
{
    public static string SanitizeSegment(string value, string segmentName);
    public static void ValidateCustomPath(string path, bool windowsRules);
    public static string CombineVersionedRelative(PackageInstallationPathSegments segments, char separator);
}
```

- [ ] **Step 1: 写失败测试 — 路径安全化**

```csharp
public class PackageInstallationPathTests
{
    [Theory]
    [InlineData("Dev", "Dev")]
    [InlineData("My Project", "My Project")]
    public void SanitizeSegment_AcceptsSafeNames(string input, string expected)
        => PackageInstallationPath.SanitizeSegment(input, "Environment").ShouldBe(expected);

    [Theory]
    [InlineData("../x")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("a\nb")]
    public void SanitizeSegment_RejectsUnsafe(string input)
    {
        Should.Throw<InvalidOperationException>(() => PackageInstallationPath.SanitizeSegment(input, "Environment"));
    }

    [Theory]
    [InlineData("/opt/app")]
    [InlineData("/var/www/myapp")]
    public void ValidateCustomPath_AcceptsPosixAbsolute(string path)
        => PackageInstallationPath.ValidateCustomPath(path, windowsRules: false);

    [Theory]
    [InlineData("/")]
    [InlineData("relative")]
    [InlineData("/opt/../etc")]
    [InlineData("/opt/#{Unexpanded}")]
    [InlineData("C:\\app")]
    public void ValidateCustomPath_RejectsInvalidPosix(string path)
    {
        Should.Throw<InvalidOperationException>(() => PackageInstallationPath.ValidateCustomPath(path, windowsRules: false));
    }

    [Theory]
    [InlineData(@"C:\apps\myapp")]
    [InlineData(@"D:\deploy\site")]
    public void ValidateCustomPath_AcceptsWindowsAbsolute(string path)
        => PackageInstallationPath.ValidateCustomPath(path, windowsRules: true);

    [Theory]
    [InlineData(@"C:\")]
    [InlineData(@"C:")]
    [InlineData(@"\apps")]
    [InlineData(@"C:\apps\..\Windows")]
    [InlineData(@"C:\apps\#{x}")]
    public void ValidateCustomPath_RejectsInvalidWindows(string path)
    {
        Should.Throw<InvalidOperationException>(() => PackageInstallationPath.ValidateCustomPath(path, windowsRules: true));
    }
}
```

- [ ] **Step 2: 运行测试确认失败**

Run:

```bash
dotnet test tests/Squid.UnitTests/Squid.UnitTests.csproj --filter "FullyQualifiedName~PackageInstallationPathTests"
```

Expected: FAIL，类型不存在。

- [ ] **Step 3: 实现 `PackageInstallationPath` + SpecialVariables 常量**

```csharp
public static class PackageInstallationPath
{
    private static readonly char[] InvalidSegmentChars = Path.GetInvalidFileNameChars()
        .Concat(new[] { '/', '\\' })
        .Distinct()
        .ToArray();

    public static string SanitizeSegment(string value, string segmentName)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidOperationException($"{segmentName} path segment is empty.");

        var trimmed = value.Trim();
        if (trimmed is "." or "..")
            throw new InvalidOperationException($"{segmentName} path segment '{trimmed}' is not allowed.");
        if (trimmed.IndexOfAny(InvalidSegmentChars) >= 0 || trimmed.Any(char.IsControl))
            throw new InvalidOperationException($"{segmentName} path segment '{trimmed}' contains illegal characters.");

        return trimmed;
    }

    public static void ValidateCustomPath(string path, bool windowsRules)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new InvalidOperationException("Custom installation directory is required when mode is Custom.");
        if (path.Contains("#{") || path.Contains('\0') || path.Any(char.IsControl))
            throw new InvalidOperationException("Custom installation directory contains unresolved variables or illegal characters.");

        var normalized = path.Replace('\\', '/');
        if (normalized.Split('/', StringSplitOptions.RemoveEmptyEntries).Any(s => s == ".."))
            throw new InvalidOperationException("Custom installation directory must not contain '..' segments.");

        if (windowsRules)
        {
            if (path.Length < 3 || !char.IsLetter(path[0]) || path[1] != ':' || (path[2] != '\\' && path[2] != '/'))
                throw new InvalidOperationException("Custom installation directory must be a Windows absolute path.");
            if (path.TrimEnd('\\', '/').Length == 2)
                throw new InvalidOperationException("Custom installation directory must not be a drive root.");
            return;
        }

        if (!path.StartsWith('/'))
            throw new InvalidOperationException("Custom installation directory must be a POSIX absolute path.");
        if (path == "/" || path.TrimEnd('/') == string.Empty)
            throw new InvalidOperationException("Custom installation directory must not be filesystem root.");
    }

    public static string CombineVersionedRelative(PackageInstallationPathSegments segments, char separator)
    {
        var env = SanitizeSegment(segments.EnvironmentName, "Environment");
        var project = SanitizeSegment(segments.ProjectName, "Project");
        var package = SanitizeSegment(segments.PackageId, "Package");
        var version = SanitizeSegment(segments.Version, "Version");
        return string.Join(separator, env, project, package, version);
    }
}
```

`SpecialVariables.Action` 增加：

```csharp
public const string InstallationDirectoryMode = "Squid.Action.Package.InstallationDirectoryMode";
public const string InstallationDirectoryPath = "Squid.Action.Package.InstallationDirectoryPath";
```

- [ ] **Step 4: 写失败测试 — DeployPackageActionHandler**

```csharp
public class DeployPackageActionHandlerTests
{
    private readonly DeployPackageActionHandler _handler = new();

    private static ActionExecutionContext CreateCtx(
        string feedId = "7",
        string packageId = "Acme.Web",
        string mode = "Versioned",
        string customDir = "",
        string version = "1.2.3",
        string packageReferenceName = "Acme.Web")
    {
        return new ActionExecutionContext
        {
            Step = new DeploymentStepDto { Name = "Install Web" },
            Action = new DeploymentActionDto
            {
                Name = "Deploy Web",
                ActionType = SpecialVariables.ActionTypes.TentaclePackage,
                Properties = new List<DeploymentActionPropertyDto>
                {
                    new() { PropertyName = SpecialVariables.Action.PackageFeedId, PropertyValue = feedId },
                    new() { PropertyName = SpecialVariables.Action.PackageId, PropertyValue = packageId },
                    new() { PropertyName = SpecialVariables.Action.InstallationDirectoryMode, PropertyValue = mode },
                    new() { PropertyName = SpecialVariables.Action.CustomInstallationDirectory, PropertyValue = customDir },
                }
            },
            Variables = new List<VariableDto>
            {
                new() { Name = "Squid.Environment.Name", Value = "Production" },
                new() { Name = "Squid.Project.Name", Value = "WebApp" },
            },
            SelectedPackages = new List<SelectedPackageDto>
            {
                new() { ActionName = "Deploy Web", PackageReferenceName = packageReferenceName, Version = version }
            }
        };
    }

    [Fact]
    public async Task DescribeIntent_Succeeds_ForVersionedMode()
    {
        var intent = (DeployPackageIntent)await ((IActionHandler)_handler).DescribeIntentAsync(CreateCtx(), CancellationToken.None);
        intent.Package.PackageId.ShouldBe("Acme.Web");
        intent.Package.Version.ShouldBe("1.2.3");
        intent.Package.FeedId.ShouldBe("7");
        intent.InstallationDirectoryMode.ShouldBe("Versioned");
        intent.PathSegments.EnvironmentName.ShouldBe("Production");
        intent.PathSegments.ProjectName.ShouldBe("WebApp");
        intent.RequiredCapabilities.ShouldContain(IntentCapabilityKeys.PackageStaging);
        intent.Packages.Count.ShouldBe(1);
    }

    [Fact]
    public async Task DescribeIntent_MissingReleaseVersion_Throws()
    {
        var ctx = CreateCtx(version: "");
        await Should.ThrowAsync<DeploymentValidationException>(
            () => ((IActionHandler)_handler).DescribeIntentAsync(ctx, CancellationToken.None));
    }

    [Fact]
    public async Task DescribeIntent_PackageIdentityMismatch_Throws()
    {
        var ctx = CreateCtx(packageReferenceName: "Other.Package");
        await Should.ThrowAsync<DeploymentValidationException>(
            () => ((IActionHandler)_handler).DescribeIntentAsync(ctx, CancellationToken.None));
    }

    [Fact]
    public async Task DescribeIntent_CustomModeWithoutPath_Throws()
    {
        var ctx = CreateCtx(mode: "Custom", customDir: "");
        await Should.ThrowAsync<DeploymentValidationException>(
            () => ((IActionHandler)_handler).DescribeIntentAsync(ctx, CancellationToken.None));
    }

    [Fact]
    public async Task DescribeIntent_CustomMode_KeepsCustomPath()
    {
        var ctx = CreateCtx(mode: "Custom", customDir: "/opt/apps/web");
        var intent = (DeployPackageIntent)await ((IActionHandler)_handler).DescribeIntentAsync(ctx, CancellationToken.None);
        intent.CustomInstallationDirectory.ShouldBe("/opt/apps/web");
    }
}
```

- [ ] **Step 5: 运行测试确认失败**

Run:

```bash
dotnet test tests/Squid.UnitTests/Squid.UnitTests.csproj --filter "FullyQualifiedName~DeployPackageActionHandlerTests"
```

Expected: FAIL，handler 不存在。

- [ ] **Step 6: 实现 DeployPackageIntent + Handler + Expander + Transport support**

`DeployPackageIntent`：

```csharp
public sealed record DeployPackageIntent : ExecutionIntent
{
    public required IntentPackageReference Package { get; init; }
    public string InstallationDirectoryMode { get; init; } = "Versioned";
    public string CustomInstallationDirectory { get; init; } = string.Empty;
    public required PackageInstallationPathSegments PathSegments { get; init; }
    public ScriptSyntax ScriptSyntax { get; init; } = ScriptSyntax.Bash;
}
```

`DeployPackageActionHandler`：

```csharp
public class DeployPackageActionHandler : IActionHandler
{
    public string ActionType => SpecialVariables.ActionTypes.TentaclePackage;

    public IReadOnlyDictionary<string, IReadOnlySet<string>> StaticRequirements { get; } =
        CapabilityRequirements.Empty
            .Require(CapabilityKeys.OsSlot, CapabilityKeys.Os.Windows, CapabilityKeys.Os.Linux, CapabilityKeys.Os.MacOS);

    Task<ExecutionIntent> IActionHandler.DescribeIntentAsync(ActionExecutionContext ctx, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        var action = ctx.Action ?? throw new DeploymentValidationException("DeployPackage action is missing.");
        var feedId = action.GetProperty(SpecialVariables.Action.PackageFeedId);
        var packageId = action.GetProperty(SpecialVariables.Action.PackageId);
        if (string.IsNullOrWhiteSpace(feedId) || string.IsNullOrWhiteSpace(packageId))
            throw new DeploymentValidationException(
                $"DeployPackage action '{action.Name}' requires FeedId and PackageId.");

        var mode = action.GetProperty(SpecialVariables.Action.InstallationDirectoryMode);
        if (string.IsNullOrWhiteSpace(mode)) mode = "Versioned";
        if (!string.Equals(mode, "Versioned", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(mode, "Custom", StringComparison.OrdinalIgnoreCase))
            throw new DeploymentValidationException($"Unsupported installation directory mode '{mode}'.");

        var selected = ctx.SelectedPackages?
            .Where(sp => string.Equals(sp.ActionName, action.Name, StringComparison.OrdinalIgnoreCase)
                         && string.Equals(sp.PackageReferenceName, packageId, StringComparison.OrdinalIgnoreCase))
            .ToList() ?? [];

        if (selected.Count != 1 || string.IsNullOrWhiteSpace(selected[0].Version))
            throw new DeploymentValidationException(
                $"DeployPackage action '{action.Name}' has no unique Release-selected version for package '{packageId}'.");

        var version = selected[0].Version;
        var env = ctx.Variables?.FirstOrDefault(v => v.Name == "Squid.Environment.Name")?.Value
                  ?? throw new DeploymentValidationException("Squid.Environment.Name is required.");
        var project = ctx.Variables?.FirstOrDefault(v => v.Name == "Squid.Project.Name")?.Value
                      ?? throw new DeploymentValidationException("Squid.Project.Name is required.");

        var segments = new PackageInstallationPathSegments(
            PackageInstallationPath.SanitizeSegment(env, "Environment"),
            PackageInstallationPath.SanitizeSegment(project, "Project"),
            PackageInstallationPath.SanitizeSegment(packageId, "Package"),
            PackageInstallationPath.SanitizeSegment(version, "Version"));

        var customDir = action.GetProperty(SpecialVariables.Action.CustomInstallationDirectory) ?? string.Empty;
        if (string.Equals(mode, "Custom", StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(customDir))
            throw new DeploymentValidationException("Custom installation directory is required when mode is Custom.");

        return Task.FromResult<ExecutionIntent>(new DeployPackageIntent
        {
            Name = "deploy-package",
            StepName = ctx.Step?.Name ?? string.Empty,
            ActionName = action.Name,
            Package = new IntentPackageReference
            {
                PackageId = packageId,
                Version = version,
                FeedId = feedId
            },
            InstallationDirectoryMode = string.Equals(mode, "Custom", StringComparison.OrdinalIgnoreCase) ? "Custom" : "Versioned",
            CustomInstallationDirectory = customDir,
            PathSegments = segments,
            ScriptSyntax = ScriptSyntax.Bash,
            Packages = new[]
            {
                new IntentPackageReference { PackageId = packageId, Version = version, FeedId = feedId }
            },
            RequiredCapabilities = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                IntentCapabilityKeys.PackageStaging
            }
        });
    }
}
```

`IntentVariableExpander` 增加：

```csharp
DeployPackageIntent dp => ExpandDeployPackage(dp, variableDictionary),

private static DeployPackageIntent ExpandDeployPackage(DeployPackageIntent intent, VariableDictionary dict)
{
    if (!string.Equals(intent.InstallationDirectoryMode, "Custom", StringComparison.OrdinalIgnoreCase))
        return intent;

    var expanded = ExpandString(intent.CustomInstallationDirectory, dict) ?? intent.CustomInstallationDirectory;
    return intent with { CustomInstallationDirectory = expanded };
}
```

Transport `SupportedActionTypes` 增加 `SpecialVariables.ActionTypes.TentaclePackage`（Listening / Polling / SSH）。

Handler 通过 `IScopedDependency` + `IActionHandler` 自动注册，无需手写 DI。

- [ ] **Step 7: 运行测试确认通过**

Run:

```bash
dotnet test tests/Squid.UnitTests/Squid.UnitTests.csproj --filter "FullyQualifiedName~PackageInstallationPathTests|FullyQualifiedName~DeployPackageActionHandlerTests|FullyQualifiedName~CapabilityValidator|FullyQualifiedName~SupportedActionTypes|FullyQualifiedName~ActionTypes"
```

Expected: PASS（若 drift 测试因 SupportedActionTypes 失败，按测试期望同步更新固定列表）。

- [ ] **Step 8: Commit**

```bash
git add \
  src/Squid.Message/Constants/SpecialVariables.cs \
  src/Squid.Core/Services/DeploymentExecution/Intents/DeployPackageIntent.cs \
  src/Squid.Core/Services/DeploymentExecution/Packages/PackageInstallationPath.cs \
  src/Squid.Core/Services/DeploymentExecution/Handlers/DeployPackageActionHandler.cs \
  src/Squid.Core/Services/DeploymentExecution/Variables/IntentVariableExpander.cs \
  src/Squid.Core/Services/DeploymentExecution/Targets/Tentacle/Transport/TentacleListeningTransport.cs \
  src/Squid.Core/Services/DeploymentExecution/Targets/Tentacle/Transport/TentaclePollingTransport.cs \
  src/Squid.Core/Services/DeploymentExecution/Targets/Ssh/Transport/SshTransport.cs \
  tests/Squid.UnitTests/Services/DeploymentExecution/Packages/PackageInstallationPathTests.cs \
  tests/Squid.UnitTests/Services/Deployments/Handlers/DeployPackageActionHandlerTests.cs
git commit -m "$(cat <<'EOF'
feat(deploy-package): add DeployPackageIntent, path planner, and action handler

Introduce installation directory path validation, release-bound package
intent generation, and Tentacle/SSH transport support for Squid.TentaclePackage.
EOF
)"
```

---

### Task 3: Tentacle Renderer + Halibut Raw Archive Dispatch

**Files:**
- Modify: `src/Squid.Message/Models/Deployments/Execution/ExecutionSemantics.cs`
- Modify: `src/Squid.Core/Services/DeploymentExecution/Targets/Tentacle/Rendering/TentacleListeningIntentRenderer.cs`
- Modify: `src/Squid.Core/Services/DeploymentExecution/Targets/Tentacle/Rendering/TentaclePollingIntentRenderer.cs`
- Modify: `src/Squid.Core/Services/DeploymentExecution/Infrastructure/ICalamariPayloadBuilder.cs`
- Modify: `src/Squid.Core/Services/DeploymentExecution/Infrastructure/CalamariPayloadBuilder.cs`
- Modify: `src/Squid.Core/Services/DeploymentExecution/Infrastructure/CalamariPayload.cs`
- Modify: `src/Squid.Core/Services/DeploymentExecution/Targets/Tentacle/Transport/HalibutMachineExecutionStrategy.cs`
- Create: `tests/Squid.UnitTests/Services/DeploymentExecution/Targets/Tentacle/TentacleDeployPackageRendererTests.cs`
- Create/Modify: `tests/Squid.UnitTests/Services/Deployments/Kubernetes/HalibutMachineExecutionStrategyTests.cs` 或同目录新测试文件

**Interfaces:**
- Consumes: `DeployPackageIntent`；`IntentRenderContext.PackageReferences : IReadOnlyList<PackageAcquisitionResult>`
- Produces: `ScriptExecutionRequest` 满足：

```csharp
ExecutionMode = ExecutionMode.PackagedPayload;
PayloadKind = PayloadKind.PackageArchive; // 新增枚举值
ActionType = SpecialVariables.ActionTypes.TentaclePackage;
CalamariCommand = "deploy-package";
PackageReferences = [acquired archive];
// Variables includes package id/version/feed/mode/custom path/path segments/hash/original path
```

- [ ] **Step 1: 写失败测试 — renderer 接受 DeployPackageIntent**

```csharp
public class TentacleDeployPackageRendererTests
{
    [Theory]
    [InlineData(typeof(TentacleListeningIntentRenderer))]
    [InlineData(typeof(TentaclePollingIntentRenderer))]
    public async Task Render_DeployPackageIntent_SetsPackageArchiveSemantics(Type rendererType)
    {
        var renderer = (IIntentRenderer)Activator.CreateInstance(rendererType)!;
        var intent = new DeployPackageIntent
        {
            Name = "deploy-package",
            StepName = "Install",
            ActionName = "Deploy Web",
            Package = new IntentPackageReference { PackageId = "Acme.Web", Version = "1.0.0", FeedId = "3" },
            InstallationDirectoryMode = "Versioned",
            PathSegments = new PackageInstallationPathSegments("Production", "WebApp", "Acme.Web", "1.0.0"),
            ScriptSyntax = ScriptSyntax.Bash
        };
        var acquired = new PackageAcquisitionResult("/tmp/Acme.Web.1.0.0.nupkg", "Acme.Web", "1.0.0", 12, "abc");
        var context = CreateRenderContext(packageReferences: new[] { acquired });

        renderer.CanRender(intent).ShouldBeTrue();
        var request = await renderer.RenderAsync(intent, context, CancellationToken.None);

        request.ExecutionMode.ShouldBe(ExecutionMode.PackagedPayload);
        request.PayloadKind.ShouldBe(PayloadKind.PackageArchive);
        request.ActionType.ShouldBe(SpecialVariables.ActionTypes.TentaclePackage);
        request.CalamariCommand.ShouldBe("deploy-package");
        request.PackageReferences.Single().PackageId.ShouldBe("Acme.Web");
        request.Variables.ShouldContain(v => v.Name == SpecialVariables.Action.PackageId && v.Value == "Acme.Web");
        request.Variables.ShouldContain(v => v.Name == SpecialVariables.Action.PackageVersion && v.Value == "1.0.0");
    }
}
```

`CreateRenderContext` 复用现有 renderer 测试 helper；若无 helper，用最小 stub 构造 `IntentRenderContext`。

- [ ] **Step 2: 运行测试确认失败**

Run:

```bash
dotnet test tests/Squid.UnitTests/Squid.UnitTests.csproj --filter "FullyQualifiedName~TentacleDeployPackageRendererTests"
```

Expected: FAIL，`CanRender` 只接受 `RunScriptIntent`。

- [ ] **Step 3: 实现 renderer**

两个 Tentacle renderer 同步：

```csharp
public bool CanRender(ExecutionIntent intent) => intent is RunScriptIntent or DeployPackageIntent;

public Task<ScriptExecutionRequest> RenderAsync(...)
{
    return intent switch
    {
        RunScriptIntent runScript => Task.FromResult(RenderRunScript(runScript, context)),
        DeployPackageIntent deployPackage => Task.FromResult(RenderDeployPackage(deployPackage, context)),
        _ => throw new IntentRenderingException(...)
    };
}

private static ScriptExecutionRequest RenderDeployPackage(DeployPackageIntent intent, IntentRenderContext context)
{
    var acquired = context.PackageReferences
        .FirstOrDefault(p => string.Equals(p.PackageId, intent.Package.PackageId, StringComparison.OrdinalIgnoreCase)
                             && string.Equals(p.Version, intent.Package.Version, StringComparison.OrdinalIgnoreCase))
        ?? throw new IntentRenderingException(CommunicationStyle, intent,
            $"No acquired package for {intent.Package.PackageId} v{intent.Package.Version}.");

    var scriptSyntax = ResolveTargetScriptSyntax(context); // Windows => PowerShell, else Bash

    if (string.Equals(intent.InstallationDirectoryMode, "Custom", StringComparison.OrdinalIgnoreCase))
        PackageInstallationPath.ValidateCustomPath(intent.CustomInstallationDirectory, windowsRules: scriptSyntax == ScriptSyntax.PowerShell);

    var variables = context.EffectiveVariables.ToList();
    void Set(string name, string value)
    {
        variables.RemoveAll(v => string.Equals(v.Name, name, StringComparison.OrdinalIgnoreCase));
        variables.Add(new VariableDto { Name = name, Value = value });
    }

    Set(SpecialVariables.Action.PackageId, intent.Package.PackageId);
    Set(SpecialVariables.Action.PackageVersion, intent.Package.Version);
    Set(SpecialVariables.Action.PackageFeedId, intent.Package.FeedId);
    Set(SpecialVariables.Action.InstallationDirectoryMode, intent.InstallationDirectoryMode);
    Set(SpecialVariables.Action.CustomInstallationDirectory, intent.CustomInstallationDirectory ?? string.Empty);
    Set("Squid.Action.Package.Path.Environment", intent.PathSegments.EnvironmentName);
    Set("Squid.Action.Package.Path.Project", intent.PathSegments.ProjectName);
    Set("Squid.Action.Package.Path.Package", intent.PathSegments.PackageId);
    Set("Squid.Action.Package.Path.Version", intent.PathSegments.Version);
    Set("Squid.Action.Package.Hash", acquired.Hash);
    Set("Squid.Action.Package.OriginalPath", $"./{Path.GetFileName(acquired.LocalPath)}");

    return new ScriptExecutionRequest
    {
        ScriptBody = string.Empty,
        Syntax = scriptSyntax,
        StepName = intent.StepName,
        ActionName = intent.ActionName,
        ActionType = SpecialVariables.ActionTypes.TentaclePackage,
        CalamariCommand = "deploy-package",
        ExecutionMode = ExecutionMode.PackagedPayload,
        ContextPreparationPolicy = ContextPreparationPolicy.Skip,
        PayloadKind = PayloadKind.PackageArchive,
        Variables = variables,
        Machine = context.Target.Machine,
        EndpointContext = context.Target.EndpointContext,
        ServerTaskId = context.ServerTaskId,
        ReleaseVersion = context.ReleaseVersion,
        Timeout = intent.Timeout ?? context.StepTimeout,
        PackageReferences = new List<PackageAcquisitionResult> { acquired }
    };
}
```

- [ ] **Step 4: 写失败测试 — 原始 archive，不走 YAML packer**

```csharp
[Fact]
public void Build_PackageArchive_ReadsRawBytesAndDeployPackageBootstrap()
{
    var bytes = "raw-package-bytes"u8.ToArray();
    var path = Path.GetTempFileName();
    File.WriteAllBytes(path, bytes);
    try
    {
        var request = new ScriptExecutionRequest
        {
            PayloadKind = PayloadKind.PackageArchive,
            PackageReferences = { new PackageAcquisitionResult(path, "Acme.Web", "1.0.0", bytes.Length, "deadbeef") },
            Variables = new List<VariableDto>(),
            ReleaseVersion = "1.0.0",
            CalamariCommand = "deploy-package"
        };

        var builder = new CalamariPayloadBuilder(Mock.Of<IYamlNuGetPacker>(MockBehavior.Strict));
        var payload = builder.Build(request, ScriptSyntax.Bash);

        payload.PackageBytes.ShouldBe(bytes);
        payload.PackageFileName.ShouldBe(Path.GetFileName(path));
        payload.TemplateBody.ShouldContain("deploy-package");
    }
    finally
    {
        File.Delete(path);
    }
}
```

- [ ] **Step 5: 实现 PayloadKind.PackageArchive + builder/strategy 分支**

```csharp
public enum PayloadKind
{
    Unspecified = 0,
    None = 1,
    YamlBundle = 2,
    PackageArchive = 3
}
```

`CalamariPayloadBuilder.Build`：

```csharp
if (request.PayloadKind == PayloadKind.PackageArchive)
{
    var pkg = request.PackageReferences?.FirstOrDefault()
        ?? throw new InvalidOperationException("PackageArchive payload requires PackageReferences.");
    var packageBytes = File.ReadAllBytes(pkg.LocalPath);
    var (variableBytes, sensitiveBytes, password) = ScriptExecutionHelper.CreateVariableFileContents(request.Variables);
    return new CalamariPayload
    {
        PackageFileName = Path.GetFileName(pkg.LocalPath),
        PackageBytes = packageBytes,
        VariableBytes = variableBytes,
        SensitiveBytes = sensitiveBytes,
        SensitivePassword = password,
        TemplateBody = BuildDeployPackageBootstrap(syntax, Path.GetFileName(pkg.LocalPath), request.CalamariCommand ?? "deploy-package")
    };
}
// 旧 YAML 路径保持不变
```

`HalibutMachineExecutionStrategy.ExecuteCalamariViaHalibutAsync` 继续复用 payload builder；PackageArchive 分支不调用 `_yamlNuGetPacker`。

- [ ] **Step 6: 运行测试确认通过**

Run:

```bash
dotnet test tests/Squid.UnitTests/Squid.UnitTests.csproj --filter "FullyQualifiedName~TentacleDeployPackageRendererTests|FullyQualifiedName~CalamariPayloadBuilder|FullyQualifiedName~HalibutMachineExecutionStrategy"
```

Expected: PASS

- [ ] **Step 7: Commit**

```bash
git add \
  src/Squid.Message/Models/Deployments/Execution/ExecutionSemantics.cs \
  src/Squid.Core/Services/DeploymentExecution/Targets/Tentacle/Rendering/TentacleListeningIntentRenderer.cs \
  src/Squid.Core/Services/DeploymentExecution/Targets/Tentacle/Rendering/TentaclePollingIntentRenderer.cs \
  src/Squid.Core/Services/DeploymentExecution/Infrastructure/ICalamariPayloadBuilder.cs \
  src/Squid.Core/Services/DeploymentExecution/Infrastructure/CalamariPayloadBuilder.cs \
  src/Squid.Core/Services/DeploymentExecution/Infrastructure/CalamariPayload.cs \
  src/Squid.Core/Services/DeploymentExecution/Targets/Tentacle/Transport/HalibutMachineExecutionStrategy.cs \
  tests/Squid.UnitTests/Services/DeploymentExecution/Targets/Tentacle/TentacleDeployPackageRendererTests.cs \
  tests/Squid.UnitTests/Services/Deployments/Kubernetes/HalibutMachineExecutionStrategyTests.cs
git commit -m "$(cat <<'EOF'
feat(deploy-package): render Tentacle package deploys with raw archives

Add PackageArchive payload kind, Tentacle DeployPackageIntent rendering,
and Calamari bootstrap that uploads acquired package bytes without YAML repack.
EOF
)"
```

---

### Task 4: Calamari `deploy-package` + 目录提交 + Conventions

**Files:**
- Create: `src/Squid.Calamari/Host/DeployPackageCliCommandHandler.cs`
- Create: `src/Squid.Calamari/Commands/Package/DeployPackageCommand.cs`
- Create: `src/Squid.Calamari/Commands/Package/PackageInstallationCoordinator.cs`
- Modify: `src/Squid.Calamari/Host/CoreCommandModule.cs`
- Create: `tests/Squid.Calamari.Tests/Calamari/Package/PackageInstallationCoordinatorTests.cs`
- Create: `tests/Squid.Calamari.Tests/Calamari/Package/DeployPackageCliCommandHandlerTests.cs`
- Create: `tests/Squid.Calamari.Tests/TestSupport/TestPackageBuilder.cs`

**Interfaces:**
- Consumes: archive path、SHA-256、mode、path segments/custom path、variables/sensitive
- Produces: 最终安装目录；输出变量：
  - `Squid.Action.Package.InstallationDirectoryPath`
  - `Squid.Action.Package.PackageId`
  - `Squid.Action.Package.PackageVersion`
- Commit 语义：
  - Versioned first: staging rename → final
  - Versioned redeploy: final → backup，staging → final，成功删 backup，失败恢复 backup
  - Custom: copy existing final → staging，extract package over staging（覆盖同名，保留旧文件），final → backup，staging → final

- [ ] **Step 1: 写失败测试 — coordinator 核心行为**

```csharp
public class PackageInstallationCoordinatorTests
{
    [Fact]
    public async Task Install_VersionedFirstDeploy_ExtractsAndCommits()
    {
        using var root = new TempDir();
        var archive = TestPackageBuilder.CreateZip(root.Path, files: new Dictionary<string,string>
        {
            ["app.txt"] = "v1",
            ["PreDeploy.sh"] = "#!/bin/bash\necho pre > pre.txt\n",
            ["PostDeploy.sh"] = "#!/bin/bash\necho post > post.txt\n"
        });
        var hash = Sha256(archive);
        var finalDir = Path.Combine(root.Path, "Applications", "Production", "WebApp", "Acme.Web", "1.0.0");

        var result = await PackageInstallationCoordinator.InstallAsync(new PackageInstallRequest
        {
            ArchivePath = archive,
            ExpectedSha256 = hash,
            Mode = "Versioned",
            FinalInstallationDirectory = finalDir,
            PreferredSyntax = ScriptSyntax.Bash
        }, CancellationToken.None);

        result.InstallationDirectory.ShouldBe(finalDir);
        File.ReadAllText(Path.Combine(finalDir, "app.txt")).ShouldBe("v1");
        File.Exists(Path.Combine(finalDir, "pre.txt")).ShouldBeTrue();
        File.Exists(Path.Combine(finalDir, "post.txt")).ShouldBeTrue();
    }

    [Fact]
    public async Task Install_HashMismatch_DoesNotTouchFinalDirectory()
    {
        using var root = new TempDir();
        var archive = TestPackageBuilder.CreateZip(root.Path, new Dictionary<string,string> { ["app.txt"] = "x" });
        var finalDir = Path.Combine(root.Path, "final");
        Directory.CreateDirectory(finalDir);
        File.WriteAllText(Path.Combine(finalDir, "keep.txt"), "old");

        await Should.ThrowAsync<InvalidOperationException>(() => PackageInstallationCoordinator.InstallAsync(new PackageInstallRequest
        {
            ArchivePath = archive,
            ExpectedSha256 = "0".PadLeft(64, '0'),
            Mode = "Versioned",
            FinalInstallationDirectory = finalDir,
            PreferredSyntax = ScriptSyntax.Bash
        }, CancellationToken.None));

        File.ReadAllText(Path.Combine(finalDir, "keep.txt")).ShouldBe("old");
    }

    [Fact]
    public async Task Install_CustomMode_PreservesFilesNotInPackage()
    {
        using var root = new TempDir();
        var finalDir = Path.Combine(root.Path, "custom-app");
        Directory.CreateDirectory(finalDir);
        File.WriteAllText(Path.Combine(finalDir, "local-only.txt"), "keep-me");
        var archive = TestPackageBuilder.CreateZip(root.Path, new Dictionary<string,string> { ["app.txt"] = "new" });

        await PackageInstallationCoordinator.InstallAsync(new PackageInstallRequest
        {
            ArchivePath = archive,
            ExpectedSha256 = Sha256(archive),
            Mode = "Custom",
            FinalInstallationDirectory = finalDir,
            PreferredSyntax = ScriptSyntax.Bash
        }, CancellationToken.None);

        File.ReadAllText(Path.Combine(finalDir, "local-only.txt")).ShouldBe("keep-me");
        File.ReadAllText(Path.Combine(finalDir, "app.txt")).ShouldBe("new");
    }

    [Fact]
    public async Task Install_PreDeployFailure_KeepsCommittedDirectory_AndFails()
    {
        using var root = new TempDir();
        var archive = TestPackageBuilder.CreateZip(root.Path, new Dictionary<string,string>
        {
            ["app.txt"] = "v1",
            ["PreDeploy.sh"] = "#!/bin/bash\nexit 9\n"
        });
        var finalDir = Path.Combine(root.Path, "Applications", "E", "P", "Pkg", "1.0.0");

        await Should.ThrowAsync<InvalidOperationException>(() => PackageInstallationCoordinator.InstallAsync(new PackageInstallRequest
        {
            ArchivePath = archive,
            ExpectedSha256 = Sha256(archive),
            Mode = "Versioned",
            FinalInstallationDirectory = finalDir,
            PreferredSyntax = ScriptSyntax.Bash
        }, CancellationToken.None));

        File.Exists(Path.Combine(finalDir, "app.txt")).ShouldBeTrue();
    }
}
```

`TestPackageBuilder` 使用 `System.IO.Compression.ZipArchive` 创建 zip/nupkg。

- [ ] **Step 2: 运行测试确认失败**

Run:

```bash
dotnet test tests/Squid.Calamari.Tests/Squid.Calamari.Tests.csproj --filter "FullyQualifiedName~PackageInstallationCoordinatorTests"
```

Expected: FAIL，类型不存在。

- [ ] **Step 3: 实现 coordinator + command + CLI handler**

`PackageInstallationCoordinator` 关键步骤：

1. 校验 archive 存在 + SHA-256
2. 解析 finalDir（调用方已算好绝对路径）
3. 在 `Directory.GetParent(finalDir)` 下创建 `staging-<guid>` / `backup-<guid>`
4. 安全解压到 staging（`PackageExtractorRegistry` + `ArchiveSafety`）
5. 若 Custom 且 final 存在：先把 final 内容复制到 staging，再解压 package 覆盖
6. 提交：
   - 若 final 存在：`Directory.Move(final, backup)`
   - `Directory.Move(staging, final)`
   - 成功：删 backup
   - 失败：若 backup 存在则恢复，并记录附加错误
7. 以 `final` 为 working directory 运行 PreDeploy →（空主动作）→ PostDeploy
8. Convention 通过 `ConventionScriptResolver.Resolve(final, name, preferredSyntax)` + bootstrap/script engine
9. 通过既有 service message 输出三个结果变量

`DeployPackageCliCommandHandler`：

```csharp
public CommandDescriptor Descriptor { get; } = new(
    "deploy-package",
    "deploy-package --archive=<path> --hash=<sha256> --mode=<Versioned|Custom> --final-dir=<path> [--variables=] [--sensitive=] [--password=]",
    "Deploy a package archive into a durable installation directory and run conventions.");
```

最终路径解析（command 层）：

```text
Linux Tentacle default:
/var/lib/squid-tentacle/Applications/<Env>/<Project>/<Package>/<Version>

Windows Tentacle default:
%ProgramData%\Squid\Tentacle\Applications\<Env>\<Project>\<Package>\<Version>
```

注册到 `CoreCommandModule`。

- [ ] **Step 4: 运行测试确认通过**

Run:

```bash
dotnet test tests/Squid.Calamari.Tests/Squid.Calamari.Tests.csproj --filter "FullyQualifiedName~Package|FullyQualifiedName~DeployPackage|FullyQualifiedName~Convention"
```

Expected: PASS

- [ ] **Step 5: Commit**

```bash
git add \
  src/Squid.Calamari/Host/DeployPackageCliCommandHandler.cs \
  src/Squid.Calamari/Commands/Package/DeployPackageCommand.cs \
  src/Squid.Calamari/Commands/Package/PackageInstallationCoordinator.cs \
  src/Squid.Calamari/Host/CoreCommandModule.cs \
  tests/Squid.Calamari.Tests/Calamari/Package/PackageInstallationCoordinatorTests.cs \
  tests/Squid.Calamari.Tests/Calamari/Package/DeployPackageCliCommandHandlerTests.cs \
  tests/Squid.Calamari.Tests/TestSupport/TestPackageBuilder.cs
git commit -m "$(cat <<'EOF'
feat(calamari): add deploy-package install coordinator and conventions

Implement durable directory commit with staging/backup recovery and
platform-selected PreDeploy/PostDeploy hooks for package deployments.
EOF
)"
```

---

### Task 5: SSH Package Deployment Path

**Files:**
- Modify: `src/Squid.Core/Services/DeploymentExecution/Targets/Ssh/Rendering/SshIntentRenderer.cs`
- Modify: `src/Squid.Core/Services/DeploymentExecution/Targets/Ssh/Transport/SshExecutionStrategy.cs`
- Modify: `src/Squid.Core/Services/DeploymentExecution/Targets/Ssh/Connectivity/SshPaths.cs`
- Modify: `src/Squid.Core/Services/DeploymentExecution/Targets/Ssh/Connectivity/SshPackageTransfer.cs`
- Create: `src/Squid.Core/Services/DeploymentExecution/Targets/Ssh/Packages/SshPackageDeploymentScriptBuilder.cs`
- Create: `tests/Squid.UnitTests/Services/DeploymentExecution/Targets/Ssh/SshDeployPackageRendererTests.cs`
- Create: `tests/Squid.UnitTests/Services/DeploymentExecution/Targets/Ssh/SshPackageDeploymentScriptBuilderTests.cs`
- Modify: 现有 `SshExecutionStrategy` 相关测试

**Interfaces:**
- Consumes: `DeployPackageIntent`；`IPackageStagingPlanner`；SHA-256 cache/upload
- Produces: Bash direct-script request + 单个 `PackageReferences`；cache 只存 archive；最终安装到：
  - 默认：`$HOME/.squid/Applications/<Env>/<Project>/<Package>/<Version>`
  - 自定义：已校验 POSIX 绝对路径
- RunScript package attachment 语义不变：仍可解压到 cache extract dir（仅非 TentaclePackage）

- [ ] **Step 1: 写失败测试 — renderer 与 script builder**

```csharp
[Fact]
public async Task Render_DeployPackageIntent_UsesDirectBashAndPackageReference()
{
    var renderer = new SshIntentRenderer();
    var intent = /* DeployPackageIntent Versioned */;
    var acquired = new PackageAcquisitionResult("/tmp/a.nupkg", "Acme.Web", "1.0.0", 10, "ab");
    var request = await renderer.RenderAsync(intent, CreateContext(acquired), CancellationToken.None);

    request.ExecutionMode.ShouldBe(ExecutionMode.DirectScript);
    request.Syntax.ShouldBe(ScriptSyntax.Bash);
    request.ActionType.ShouldBe(SpecialVariables.ActionTypes.TentaclePackage);
    request.PackageReferences.Count.ShouldBe(1);
    request.ScriptBody.ShouldContain("sha256sum");
    request.ScriptBody.ShouldContain("PreDeploy.sh");
    request.ScriptBody.ShouldContain("PostDeploy.sh");
    request.ScriptBody.ShouldContain(".squid/Applications");
}

[Fact]
public void Build_QuotesPathsWithSpacesAndSingleQuotes()
{
    var script = SshPackageDeploymentScriptBuilder.Build(new SshPackageDeployScriptModel
    {
        ArchiveRemotePath = "/home/me/.squid/Packages/Acme.Web.1.0.0.nupkg",
        ExpectedSha256 = "abc",
        Mode = "Versioned",
        EnvironmentSegment = "Prod Env",
        ProjectSegment = "Web's App",
        PackageSegment = "Acme.Web",
        VersionSegment = "1.0.0",
        PackageId = "Acme.Web",
        PackageVersion = "1.0.0"
    });

    script.ShouldContain("Prod Env");
    script.ShouldContain("Web's App");
    script.ShouldContain("sha256sum");
    script.ShouldContain("unzip");
}
```

- [ ] **Step 2: 运行测试确认失败**

Run:

```bash
dotnet test tests/Squid.UnitTests/Squid.UnitTests.csproj --filter "FullyQualifiedName~SshDeployPackageRendererTests|FullyQualifiedName~SshPackageDeploymentScriptBuilderTests"
```

Expected: FAIL

- [ ] **Step 3: 实现 SSH 路径**

1. `SshIntentRenderer` 增加 `DeployPackageIntent` 分支，生成 `ScriptBody = SshPackageDeploymentScriptBuilder.Build(...)`，`ActionType = TentaclePackage`，`PackageReferences = [acquired]`。
2. `SshExecutionStrategy.StageAndExtractPackagesAsync`：
   - 若 `request.ActionType == SpecialVariables.ActionTypes.TentaclePackage`：只 staging/cache archive，**不**调用 `ExtractPackage` 到 cache extract dir。
   - 否则保留旧行为（RunScript attachment）。
3. `SshPaths` 增加：

```csharp
public static string ApplicationsRoot(string homeDir)
    => $"{homeDir.TrimEnd('/')}/.squid/Applications";

public static string VersionedInstallationDirectory(string homeDir, PackageInstallationPathSegments segments)
    => $"{ApplicationsRoot(homeDir)}/{segments.EnvironmentName}/{segments.ProjectName}/{segments.PackageId}/{segments.Version}";
```

4. `SshPackageDeploymentScriptBuilder` 生成 Bash：
   - preflight: `command -v sha256sum`、`command -v unzip`、`$HOME` 非空
   - hash verify
   - staging/backup on same parent as final
   - unzip to staging
   - versioned/custom commit
   - `cd final && [ -f PreDeploy.sh ] && bash PreDeploy.sh`
   - empty main
   - `PostDeploy.sh`
   - service messages for output variables
   - cleanup staging/backup on success；失败恢复 backup
5. 所有路径通过单一 quoting 函数（C# 生成 `'` + `'\''` + `'`）。

- [ ] **Step 4: 运行测试确认通过**

Run:

```bash
dotnet test tests/Squid.UnitTests/Squid.UnitTests.csproj --filter "FullyQualifiedName~Ssh"
```

Expected: PASS

- [ ] **Step 5: Commit**

```bash
git add \
  src/Squid.Core/Services/DeploymentExecution/Targets/Ssh/Rendering/SshIntentRenderer.cs \
  src/Squid.Core/Services/DeploymentExecution/Targets/Ssh/Transport/SshExecutionStrategy.cs \
  src/Squid.Core/Services/DeploymentExecution/Targets/Ssh/Connectivity/SshPaths.cs \
  src/Squid.Core/Services/DeploymentExecution/Targets/Ssh/Connectivity/SshPackageTransfer.cs \
  src/Squid.Core/Services/DeploymentExecution/Targets/Ssh/Packages/SshPackageDeploymentScriptBuilder.cs \
  tests/Squid.UnitTests/Services/DeploymentExecution/Targets/Ssh/SshDeployPackageRendererTests.cs \
  tests/Squid.UnitTests/Services/DeploymentExecution/Targets/Ssh/SshPackageDeploymentScriptBuilderTests.cs
git commit -m "$(cat <<'EOF'
feat(deploy-package): add SSH durable package installation path

Stage archives in the SSH package cache, install into versioned or custom
directories, and run Bash PreDeploy/PostDeploy conventions safely.
EOF
)"
```

---

### Task 6: SquidWeb DeployPackageEditor

**Files:**
- Create: `/Users/nacho/Documents/GitHub/SolarifyDev/SquidWeb/src/pages/project-detail/step-editors/deploy-package/deploy-package-model.ts`
- Create: `/Users/nacho/Documents/GitHub/SolarifyDev/SquidWeb/src/pages/project-detail/step-editors/deploy-package/DeployPackageEditor.tsx`
- Create: `/Users/nacho/Documents/GitHub/SolarifyDev/SquidWeb/src/pages/project-detail/step-editors/deploy-package/deploy-package-model.test.ts`
- Modify: `/Users/nacho/Documents/GitHub/SolarifyDev/SquidWeb/src/pages/project-detail/DeploymentProcess.tsx`

**Interfaces:**
- Consumes: `StepEditorProps` / `StepEditorHandle`；`getExternalFeeds`；`searchExternalFeedPackages`；`TargetTagSelect`；`StepConditionsSection`
- Produces: step DTO properties：
  - `Squid.Action.Package.FeedId`
  - `Squid.Action.Package.PackageId`
  - `Squid.Action.Package.InstallationDirectoryMode` = `Versioned` | `Custom`
  - `Squid.Action.Package.CustomInstallationDirectory`
  - `Squid.Action.TargetRoles`
  - 保留未知既有属性

- [ ] **Step 1: 写失败测试 — model 纯函数**

```ts
import { describe, expect, it } from 'vitest'
import {
  buildDeployPackageStepDto,
  normalizeDeployPackageForm,
  validateDeployPackageForm,
  type DeployPackageFormState,
} from './deploy-package-model'

describe('deploy-package-model', () => {
  it('builds create dto with required properties', () => {
    const form: DeployPackageFormState = {
      stepName: 'Deploy Web',
      feedId: 3,
      packageId: 'Acme.Web',
      targetRoles: ['web'],
      installationDirectoryMode: 'Versioned',
      customInstallationDirectory: '',
      conditions: {} as any,
    }
    const dto = buildDeployPackageStepDto({ form, processId: 1, existingStep: null })
    const props = dto.actions[0].properties.map((p) => [p.propertyName, p.propertyValue])
    expect(props).toContainEqual(['Squid.Action.Package.FeedId', '3'])
    expect(props).toContainEqual(['Squid.Action.Package.PackageId', 'Acme.Web'])
    expect(props).toContainEqual(['Squid.Action.Package.InstallationDirectoryMode', 'Versioned'])
  })

  it('preserves unknown existing properties on edit', () => {
    const existing = {
      id: 9,
      name: 'Deploy Web',
      actions: [{
        id: 1,
        name: 'Deploy Web',
        actionType: 'Squid.TentaclePackage',
        properties: [
          { propertyName: 'Squid.Action.Package.FeedId', propertyValue: '3' },
          { propertyName: 'Squid.Action.Package.PackageId', propertyValue: 'Acme.Web' },
          { propertyName: 'Custom.Unknown', propertyValue: 'keep-me' },
        ],
      }],
      properties: [],
    } as any
    const form = normalizeDeployPackageForm(existing)
    form.packageId = 'Acme.Web'
    const dto = buildDeployPackageStepDto({ form, processId: 1, existingStep: existing })
    expect(dto.actions[0].properties.some((p) => p.propertyName === 'Custom.Unknown' && p.propertyValue === 'keep-me')).toBe(true)
  })

  it('validates required fields and custom path', () => {
    expect(validateDeployPackageForm({
      stepName: '',
      feedId: null,
      packageId: '',
      targetRoles: [],
      installationDirectoryMode: 'Custom',
      customInstallationDirectory: '',
      conditions: {} as any,
    }).ok).toBe(false)
  })
})
```

- [ ] **Step 2: 运行测试确认失败**

Run（在 SquidWeb 根目录）：

```bash
pnpm test -- --run src/pages/project-detail/step-editors/deploy-package/deploy-package-model.test.ts
```

Expected: FAIL，模块不存在。

- [ ] **Step 3: 实现 model + editor + 注册**

`deploy-package-model.ts`：只放纯函数，不导出 barrel。

`DeployPackageEditor.tsx`：
- `forwardRef<StepEditorHandle, StepEditorProps>`
- 字段：步骤名、NuGet Feed（过滤 `feedType` 含 `NuGet`）、Package 搜索（防抖 + stale guard）、Target roles、安装目录模式 radio、Custom 路径输入、Conditions
- Feed 变化清空 package
- Custom 模式显示固定文案：`V1 不会删除包中不存在的旧文件。`
- 不选择 package 版本
- 复用 IIS/RunScript 的 feed/package UX 模式，但只显示 NuGet

`DeploymentProcess.tsx`：

```ts
import { DeployPackageEditor } from './step-editors/deploy-package/DeployPackageEditor'

const STEP_EDITOR_MAP = {
  // ...
  'Squid.TentaclePackage': DeployPackageEditor,
}
```

- [ ] **Step 4: 运行前端验证**

Run:

```bash
pnpm test -- --run src/pages/project-detail/step-editors/deploy-package/deploy-package-model.test.ts
pnpm typecheck
pnpm lint
```

Expected: PASS

- [ ] **Step 5: Commit（仅 SquidWeb，勿 add 用户无关的 pnpm-lock.yaml 改动）**

```bash
git add \
  src/pages/project-detail/step-editors/deploy-package/deploy-package-model.ts \
  src/pages/project-detail/step-editors/deploy-package/DeployPackageEditor.tsx \
  src/pages/project-detail/step-editors/deploy-package/deploy-package-model.test.ts \
  src/pages/project-detail/DeploymentProcess.tsx
git commit -m "$(cat <<'EOF'
feat(deploy-package): add Deploy a Package step editor

Register Squid.TentaclePackage editor with NuGet feed/package selection,
installation directory mode, and unknown property preservation.
EOF
)"
```

---

### Task 7: 集成 / E2E / 浏览器验证 / 收尾

**Files:**
- Create: `tests/Squid.IntegrationTests/Services/DeploymentExecution/DeployPackagePipelineIntegrationTests.cs`（或现有 Integration 目录等价位置）
- Create: `tests/Squid.E2ETests/Deployments/Package/DeployPackageSshE2ETests.cs`（若 SSH fixture 可用）
- Create: `tests/Squid.LinuxTentacleE2ETests/DeployPackageLinuxTentacleE2ETests.cs`（若 Linux fixture 可用）
- Create: `tests/Squid.WindowsTentacleE2ETests/DeployPackageWindowsTentacleE2ETests.cs`（若 Windows fixture 可用）
- Modify: 设计文档仅补充实现后稳定事实（如最终 CLI 参数名、变量名），不写过程元信息

**Interfaces:**
- Consumes: Tasks 1–6 全链路
- Produces: 满足完成标准 1–7 的证据

- [ ] **Step 1: 写 pipeline integration 测试**

```csharp
[Fact]
public async Task Process_Release_Acquisition_UsesPackageIdIdentity()
{
    // Arrange: 创建 project + Squid.TentaclePackage action properties FeedId/PackageId=Acme.Web
    // Act: GetPackageReferences → PackageReferenceName == Acme.Web
    // Act: CreateRelease with selected package version 1.2.3
    // Act: acquisition lookup key == Acme.Web
    // Assert: acquired package id/version match release selection
}
```

- [ ] **Step 2: 准备测试 package fixture**

最小 zip/nupkg 内容：
- `app.txt`
- `PreDeploy.sh` / `PreDeploy.ps1`（写变量可见标记）
- `PostDeploy.sh` / `PostDeploy.ps1`

测试必须调用生产 extractor/coordinator/script builder，禁止在测试中重写安装逻辑。

- [ ] **Step 3: 按环境门控跑 E2E**

```bash
dotnet test tests/Squid.IntegrationTests/Squid.IntegrationTests.csproj --filter "FullyQualifiedName~DeployPackage"
dotnet test tests/Squid.E2ETests/Squid.E2ETests.csproj --filter "FullyQualifiedName~DeployPackage|FullyQualifiedName~Ssh"
dotnet test tests/Squid.LinuxTentacleE2ETests/Squid.LinuxTentacleE2ETests.csproj --filter "FullyQualifiedName~DeployPackage"
dotnet test tests/Squid.WindowsTentacleE2ETests/Squid.WindowsTentacleE2ETests.csproj --filter "FullyQualifiedName~DeployPackage"
```

Expected:
- 有 fixture 的环境：PASS
- 无 fixture：跳过并在结果中记录，不伪造通过

E2E 断言清单：
1. 最终目录存在且含 `app.txt`
2. Linux Tentacle 默认路径前缀 `/var/lib/squid-tentacle/Applications/...`
3. Windows Tentacle 默认路径含 `Squid\Tentacle\Applications\...`
4. SSH 默认路径含 `/.squid/Applications/...`
5. PreDeploy/PostDeploy 产生副作用文件
6. 同版本 redeploy 替换内容
7. PreDeploy 失败时 action 失败且诊断目录保留
8. 输出变量 `InstallationDirectoryPath` / `PackageId` / `PackageVersion`

- [ ] **Step 4: 浏览器验证 SquidWeb**

```bash
pnpm dev
```

手动/浏览器自动化检查：
- 新建 Deploy a Package 步骤
- 编辑已有步骤
- 只显示 NuGet Feed
- Feed 变更清空 package
- 默认/自定义目录切换
- 保存后 Release 创建页出现 Package ID
- 桌面与窄视口无重叠/溢出
- console 无新增错误

- [ ] **Step 5: 全量相关回归**

后端：

```bash
dotnet build Squid.sln --no-restore
dotnet test tests/Squid.UnitTests/Squid.UnitTests.csproj --filter "FullyQualifiedName~Package|FullyQualifiedName~DeployPackage|FullyQualifiedName~Ssh|FullyQualifiedName~Tentacle|FullyQualifiedName~HalibutMachineExecutionStrategy|FullyQualifiedName~Capability"
dotnet test tests/Squid.Calamari.Tests/Squid.Calamari.Tests.csproj --filter "FullyQualifiedName~Package|FullyQualifiedName~Convention|FullyQualifiedName~DeployPackage"
```

前端：

```bash
pnpm test -- --run
pnpm typecheck
pnpm lint
pnpm build
```

- [ ] **Step 6: 完成标准核对**

| # | 标准 | 证据 |
| --- | --- | --- |
| 1 | SquidWeb 可创建/编辑 Deploy a Package | 浏览器验证 + editor 测试 |
| 2 | Release 显示 package 并固化版本 | integration + ReleasesCreate 回归 |
| 3 | Server 下载 Package ID 与 Release 一致且非空 | acquisition/identity 测试 |
| 4 | Linux/Windows Tentacle + SSH Linux 部署到预期目录 | E2E |
| 5 | conventions 可读变量并影响结果 | Calamari/SSH/E2E |
| 6 | 各失败阶段有明确日志 | 单元/集成失败路径 |
| 7 | 默认版本化目录失败不破坏先前成功版本 | coordinator 恢复测试 |

- [ ] **Step 7: Commit 测试与文档稳定事实**

```bash
# Squid
git add tests/ docs/superpowers/specs/2026-07-15-deploy-a-package-design.md
git commit -m "$(cat <<'EOF'
test(deploy-package): cover package deploy pipeline and target installs

Add integration/E2E coverage for identity, acquisition, Tentacle/SSH
installation paths, conventions, and failure diagnostics.
EOF
)"
```

---

## Spec Coverage

| 设计章节 | 对应 Task |
| --- | --- |
| 2.1 支持范围 / 2.2 暂不包含 | Global Constraints + 各 Task 不实现排除项 |
| 3.1 语义化 Intent | Task 2 |
| 3.2 Release 版本事实来源 | Task 1 + Task 2 |
| 3.3 Server 下载 | Task 1 |
| 3.4 持久安装目录 | Task 4 + Task 5 |
| 4.1 Action 属性 | Task 2 + Task 6 |
| 4.2 Package identity | Task 1 |
| 4.3 Intent 字段与失败条件 | Task 2 |
| 4.4 输出变量 | Task 4 + Task 5 |
| 5 默认/自定义安装目录 | Task 2/4/5 |
| 6 前端编辑器 | Task 6 |
| 7 端到端数据流 | Task 1–7 |
| 8 Tentacle 执行 | Task 3 + Task 4 |
| 9 SSH 执行 | Task 5 |
| 10 Acquisition 与 SHA-256 / 失败终止 | Task 1 |
| 11 错误处理与清理 | Task 4 + Task 5 |
| 12 日志与可观察性 | Task 1/3/4/5（阶段化错误与 hash/path 日志） |
| 13 测试与验收 / 完成标准 | Task 7 |

## Placeholder Scan

- 无 TBD / TODO / “similar to Task N” 占位。
- 每个代码步骤包含具体类型/方法/断言。
- 若某 E2E 项目缺少 fixture，Task 7 明确要求跳过并记录，不假装完成。

## Type Consistency

- Action type：`SpecialVariables.ActionTypes.TentaclePackage` = `"Squid.TentaclePackage"`
- Package identity：`PackageReferenceName == PackageId`
- Hash 字段名：`Hash`；算法：SHA-256 lowercase hex
- Intent：`DeployPackageIntent`
- Mode：`"Versioned"` / `"Custom"`
- Payload：`PayloadKind.PackageArchive`
- Calamari command：`deploy-package`
- 输出变量：
  - `Squid.Action.Package.InstallationDirectoryPath`
  - `Squid.Action.Package.PackageId`
  - `Squid.Action.Package.PackageVersion`
- 前端 map key：`STEP_EDITOR_MAP['Squid.TentaclePackage'] = DeployPackageEditor`

## Execution Handoff

Plan complete and saved to `docs/superpowers/plans/2026-07-15-deploy-a-package-v1.md`.

**Two execution options:**

1. **Subagent-Driven (recommended)** — 每个 Task 派发新 subagent，Task 间审查，迭代快  
2. **Inline Execution** — 本会话用 executing-plans 按 Task 批量执行并设检查点  

确认计划后选择一种方式，再开始写业务代码。
