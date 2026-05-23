namespace Squid.Core.Services.DeploymentExecution.Validation;

/// <summary>
/// Well-known slot + value identifiers for the handler-level static-requirement /
/// machine-capability matching surface. Slots are the dimension being checked
/// (e.g. operating system); values are the specific things a target can advertise
/// or a handler can require (e.g. <c>windows</c> / <c>linux</c>).
///
/// <para><b>Semantics</b>:
/// <list type="bullet">
///   <item>A handler advertises requirements as a map of <c>slot → acceptable
///         values</c>. Match succeeds if, for every declared slot, the target
///         advertises AT LEAST ONE value in that slot's acceptable-values set.</item>
///   <item><b>AND across slots</b> — every slot the handler mentions must be
///         satisfied. Slots the handler doesn't mention are ignored.</item>
///   <item><b>OR within a slot</b> — a handler that accepts multiple values
///         declares them all (e.g. RunScript accepts any of windows / linux /
///         macos). The target only needs to match ONE.</item>
/// </list></para>
///
/// <para><b>Examples</b>:
/// <list type="bullet">
///   <item>IIS deploy: <c>{os: {windows}, shell: {powershell, pwsh}}</c></item>
///   <item>RunScript: <c>{os: {windows, linux, macos}}</c></item>
///   <item>Helm: <c>{os: {linux, macos}, bin:helm: {present}, bin:kubectl: {present}}</c></item>
/// </list></para>
///
/// <para><b>Naming convention</b> — slot keys are short lowercase strings; values
/// follow the same convention. For "presence" capabilities (e.g. a binary on PATH,
/// admin privilege) the value is the sentinel <see cref="Present"/>. For
/// "categorical" capabilities (OS, architecture) the value is the specific
/// category. Binaries / shells / etc. that can vary per-target get their own
/// namespaced slot key (e.g. <c>bin:helm</c>, <c>shell:pwsh</c>) so each can be
/// AND-required independently.</para>
///
/// <para><b>Pinned per Rule 8</b> — drift in any of these strings is a wire-
/// contract regression (handler authors hardcode them; targets advertise them).
/// <c>CapabilityKeysConstantsTests</c> in <c>Squid.UnitTests</c> hard-pins every
/// literal so a rename surfaces at build time.</para>
/// </summary>
public static class CapabilityKeys
{
    /// <summary>
    /// Sentinel value for "presence" capabilities (a binary is installed, a
    /// privilege is held, a service is available). Used when the slot itself
    /// implies a specific thing and the value just signals presence.
    /// </summary>
    public const string Present = "present";

    /// <summary>Operating-system slot. Values: <see cref="Os.Windows"/>, <see cref="Os.Linux"/>, <see cref="Os.MacOS"/>.</summary>
    public const string OsSlot = "os";

    /// <summary>Process architecture slot. Values: <see cref="Arch.X64"/>, <see cref="Arch.Arm64"/>, <see cref="Arch.X86"/>.</summary>
    public const string ArchSlot = "arch";

    public static class Os
    {
        public const string Windows = "windows";
        public const string Linux = "linux";
        public const string MacOS = "macos";
    }

    public static class Arch
    {
        public const string X64 = "x64";
        public const string Arm64 = "arm64";
        public const string X86 = "x86";
    }

    /// <summary>
    /// Shell-presence slots. Each installed shell is advertised under its OWN
    /// slot (<c>shell:pwsh</c>, <c>shell:bash</c>, etc.) with value
    /// <see cref="Present"/>. This lets handlers AND-require multiple shells —
    /// e.g. a script that needs both pwsh AND bash can declare both slots.
    /// </summary>
    public static class Shell
    {
        public const string PowerShell = "shell:powershell";
        public const string Pwsh = "shell:pwsh";
        public const string Bash = "shell:bash";
        public const string Cmd = "shell:cmd";
        public const string Sh = "shell:sh";
    }

    /// <summary>
    /// Binary-presence slots — each tool on PATH (kubectl, helm, docker, …) is
    /// its own slot under the <c>bin:</c> namespace with value <see cref="Present"/>.
    /// </summary>
    public static class Bin
    {
        public const string Kubectl = "bin:kubectl";
        public const string Helm = "bin:helm";
        public const string Docker = "bin:docker";
        public const string Kustomize = "bin:kustomize";
        public const string Az = "bin:az";
        public const string Aws = "bin:aws";
        public const string Gcloud = "bin:gcloud";
        public const string Terraform = "bin:terraform";
    }

    /// <summary>
    /// Privilege slots. <c>priv:admin</c> signals the agent runs as Windows
    /// Administrator; <c>priv:sudo</c> signals the agent has passwordless sudo
    /// on the host. Values are always <see cref="Present"/>.
    /// </summary>
    public static class Privilege
    {
        public const string Admin = "priv:admin";
        public const string Sudo = "priv:sudo";
    }

    /// <summary>
    /// H7 — installed-service / role slots. Each system role detected on the
    /// target gets its own slot under the <c>role:</c> namespace with value
    /// <see cref="Present"/>. Distinct from <see cref="Bin"/> (CLI binaries
    /// on PATH) — a "role" is a persistent service / daemon / framework the
    /// agent's OS hosts (IIS web server, Docker daemon, nginx, etc.).
    ///
    /// <para><b>Why this exists</b>: pre-H7 the IIS deploy handler only declared
    /// <c>{os: windows, shell: powershell}</c>. Dispatching IIS deploy to a
    /// Windows machine without IIS installed would dispatch the script to the
    /// agent, run it, and ONLY THEN fail with <c>Import-Module
    /// WebAdministration: module not found</c>. That's a wasted dispatch and
    /// a confusing operator experience. H7 lets the IIS handler declare
    /// <c>role:iis</c> as a requirement; <see cref="CapabilityValidator"/>
    /// catches the missing role at plan-time with an actionable message
    /// ("Install IIS via Add Roles and Features, then re-run health check").</para>
    ///
    /// <para><b>Backward compatibility</b>: old agents that don't yet emit
    /// installed-roles metadata are treated as "unknown" by the validator
    /// (optimistic-allow), so existing fleets continue to work. The new
    /// requirement only activates after the agent is upgraded to a version
    /// that detects + advertises roles.</para>
    /// </summary>
    public static class Role
    {
        /// <summary>Windows IIS web server (probe: <c>Get-Service W3SVC</c>).</summary>
        public const string IIS = "role:iis";

        /// <summary>Docker daemon running on the host (probe: <c>docker info</c> succeeds).</summary>
        public const string Docker = "role:docker";

        /// <summary>nginx service active on the host (probe: <c>systemctl is-active nginx</c> on Linux).</summary>
        public const string Nginx = "role:nginx";

        /// <summary>systemd init system available (probe: <c>systemctl --version</c> succeeds).</summary>
        public const string Systemd = "role:systemd";
    }

    /// <summary>
    /// All slot keys whose values come from a fixed enumeration (the
    /// "categorical" slots: <see cref="OsSlot"/>, <see cref="ArchSlot"/>).
    /// Helper for pin tests asserting slot taxonomy hasn't drifted.
    /// </summary>
    public static readonly IReadOnlySet<string> CategoricalSlots = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        OsSlot,
        ArchSlot,
    };
}
