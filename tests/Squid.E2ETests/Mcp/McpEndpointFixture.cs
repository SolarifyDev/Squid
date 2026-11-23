using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Squid.Core.Persistence;
using Squid.Core.Persistence.Db;
using Squid.Core.Persistence.Entities.Account;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Account;
using Squid.Core.Services.Authorization;
using Squid.Core.Services.Teams;
using Squid.Message.Commands.Account;
using Squid.Message.Enums;
using Squid.Message.Requests.Account;
using Xunit;

namespace Squid.E2ETests.Mcp;

public sealed class McpEndpointFixture : IAsyncLifetime
{
    public const string ProtocolVersion = "2026-07-28";
    public const string AllowedOrigin = "https://allowed.example";
    public const string JwtKey = "squid-e2e-mcp-endpoint-test-signing-key";

    public const string ToolsListBody = """
        {
          "jsonrpc": "2.0",
          "id": 1,
          "method": "tools/list",
          "params": {
            "_meta": {
              "io.modelcontextprotocol/protocolVersion": "2026-07-28",
              "io.modelcontextprotocol/clientCapabilities": {}
            }
          }
        }
        """;

    private IConfiguration _baseConfiguration = null!;
    private string _databaseName = null!;
    private string _databaseConnectionString = null!;

    public McpApiFactory Enabled { get; private set; } = null!;
    public McpApiFactory Disabled { get; private set; } = null!;
    public McpApiFactory MissingApiKeyScheme { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        _baseConfiguration = LoadConfiguration();
        _databaseName = $"squid_e2e_mcp_{Guid.NewGuid():N}";
        _databaseConnectionString = CreateConnectionString(_databaseName);

        await CreateDatabaseAsync().ConfigureAwait(false);
        new DbUpRunner(_databaseConnectionString).Run();

        Enabled = CreateFactory(enabled: true, registerApiKeyScheme: true);
        Disabled = CreateFactory(enabled: false, registerApiKeyScheme: true);
        MissingApiKeyScheme = CreateFactory(enabled: true, registerApiKeyScheme: false);
    }

    public async Task DisposeAsync()
    {
        await Enabled.DisposeAsync().ConfigureAwait(false);
        await Disabled.DisposeAsync().ConfigureAwait(false);
        await MissingApiKeyScheme.DisposeAsync().ConfigureAwait(false);

        await DropDatabaseAsync().ConfigureAwait(false);
    }

    public async Task<ApiKeyResult> CreateApiKeyAsync()
    {
        using var scope = Enabled.Services.CreateScope();
        var accountService = scope.ServiceProvider.GetRequiredService<IAccountService>();
        var suffix = Guid.NewGuid().ToString("N")[..12];
        var user = await accountService.CreateUserAsync(new CreateUserCommand
        {
            UserName = $"mcp-{suffix}",
            Password = "123456",
            DisplayName = "MCP Endpoint Test"
        }).ConfigureAwait(false);
        var key = await accountService.CreateApiKeyAsync(
            user.UserAccount.Id,
            "MCP endpoint test").ConfigureAwait(false);

        return new ApiKeyResult(user.UserAccount.Id, key.Id, key.ApiKey);
    }

    public async Task<ReadToolSeed> CreateReadToolSeedAsync()
    {
        using var scope = Enabled.Services.CreateScope();
        var services = scope.ServiceProvider;
        var accountService = services.GetRequiredService<IAccountService>();
        var db = services.GetRequiredService<SquidDbContext>();
        var suffix = Guid.NewGuid().ToString("N")[..12];
        var marker = $"mcp-tool-{suffix}";
        var user = await accountService.CreateUserAsync(new CreateUserCommand
        {
            UserName = $"mcp-tool-{suffix}",
            Password = "123456",
            DisplayName = "MCP Read Tool Test"
        }).ConfigureAwait(false);
        var key = await accountService.CreateApiKeyAsync(user.UserAccount.Id, "MCP read tool test")
            .ConfigureAwait(false);

        var seedId = int.Parse(suffix[..7], System.Globalization.NumberStyles.HexNumber);
        var spaceId = seedId;
        var projectGroupId = seedId + 1_000_000;
        var firstProjectId = seedId + 2_000_000;
        var secondProjectId = seedId + 2_000_001;
        var firstProjectName = $"{marker} Alpha";
        var secondProjectName = $"{marker} Beta";

        db.Set<Space>().Add(new Space
        {
            Id = spaceId,
            Name = "MCP Read Tool Space",
            Slug = "mcp-read-tool-space",
            Json = "{}",
            Description = string.Empty
        });
        db.Set<ProjectGroup>().Add(new ProjectGroup
        {
            Id = projectGroupId,
            SpaceId = spaceId,
            Name = "MCP Read Tool Group",
            Slug = "mcp-read-tool-group",
            Description = string.Empty
        });
        db.Set<Project>().AddRange(
            new Project
            {
                Id = firstProjectId,
                SpaceId = spaceId,
                ProjectGroupId = projectGroupId,
                Name = firstProjectName,
                Slug = $"{marker}-alpha",
                Json = "{}",
                IncludedLibraryVariableSetIds = "[]",
                IsDisabled = false
            },
            new Project
            {
                Id = secondProjectId,
                SpaceId = spaceId,
                ProjectGroupId = projectGroupId,
                Name = secondProjectName,
                Slug = $"{marker}-beta",
                Json = "{}",
                IncludedLibraryVariableSetIds = "[]",
                IsDisabled = false
            });
        await db.SaveChangesAsync().ConfigureAwait(false);

        var teamProvider = services.GetRequiredService<ITeamDataProvider>();
        var roleProvider = services.GetRequiredService<IUserRoleDataProvider>();
        var scopedProvider = services.GetRequiredService<IScopedUserRoleDataProvider>();
        var team = new Team
        {
            Name = $"MCP Read Tool Team {suffix}",
            SpaceId = spaceId,
            IsBuiltIn = false
        };
        await teamProvider.AddAsync(team).ConfigureAwait(false);
        await teamProvider.AddMemberAsync(new TeamMember { TeamId = team.Id, UserId = user.UserAccount.Id })
            .ConfigureAwait(false);

        var role = new UserRole { Name = $"MCP Read Tool Role {suffix}", IsBuiltIn = false };
        await roleProvider.AddAsync(role).ConfigureAwait(false);
        await roleProvider.SetPermissionsAsync(role.Id, [Permission.ProjectView.ToString()])
            .ConfigureAwait(false);

        var scopedRole = new ScopedUserRole
        {
            TeamId = team.Id,
            UserRoleId = role.Id,
            SpaceId = spaceId
        };
        await scopedProvider.AddAsync(scopedRole).ConfigureAwait(false);
        await scopedProvider.SetProjectScopeAsync(scopedRole.Id, [firstProjectId, secondProjectId])
            .ConfigureAwait(false);
        await scopedProvider.SetEnvironmentScopeAsync(scopedRole.Id, []).ConfigureAwait(false);
        await scopedProvider.SetProjectGroupScopeAsync(scopedRole.Id, []).ConfigureAwait(false);

        return new ReadToolSeed(spaceId, firstProjectId, secondProjectId, marker, firstProjectName, key.ApiKey);
    }

    public async Task<string> CreateJwtAsync()
    {
        using var scope = Enabled.Services.CreateScope();
        var accountService = scope.ServiceProvider.GetRequiredService<IAccountService>();
        var suffix = Guid.NewGuid().ToString("N")[..12];
        var user = await accountService.CreateUserAsync(new CreateUserCommand
        {
            UserName = $"mcp-jwt-{suffix}",
            Password = "123456",
            DisplayName = "MCP JWT Test"
        }).ConfigureAwait(false);
        var login = await accountService.LoginAsync(new LoginRequest
        {
            UserName = user.UserAccount.UserName,
            Password = "123456"
        }).ConfigureAwait(false);

        return login.AccessToken;
    }

    public async Task RevokeApiKeyAsync(int userId, int apiKeyId)
    {
        using var scope = Enabled.Services.CreateScope();
        var accountService = scope.ServiceProvider.GetRequiredService<IAccountService>();

        await accountService.DeleteApiKeyAsync(apiKeyId, userId).ConfigureAwait(false);
    }

    private McpApiFactory CreateFactory(bool enabled, bool registerApiKeyScheme)
    {
        var jwtKey = registerApiKeyScheme ? JwtKey : string.Empty;
        var configuration = new ConfigurationBuilder()
            .AddConfiguration(_baseConfiguration)
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["SquidStore:ConnectionString"] = _databaseConnectionString,
                ["Mcp:Enabled"] = enabled ? "true" : "false",
                ["Mcp:AllowedOrigins:0"] = AllowedOrigin,
                ["Authentication:Jwt:SymmetricKey"] = jwtKey,
                ["Authentication:Jwt:UserTokenExpiryHours"] = "1",
                ["Halibut:Polling:Enabled"] = "false",
                ["ServerUrl:ExternalUrl"] = "http://localhost"
            })
            .Build();

        return new McpApiFactory(configuration);
    }

    private IConfiguration LoadConfiguration()
    {
        return new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json")
            .AddEnvironmentVariables()
            .Build();
    }

    private string CreateConnectionString(string databaseName)
    {
        var builder = new NpgsqlConnectionStringBuilder(
            _baseConfiguration["SquidStore:ConnectionString"])
        {
            Database = databaseName
        };

        return builder.ToString();
    }

    private async Task CreateDatabaseAsync()
    {
        var builder = new NpgsqlConnectionStringBuilder(_databaseConnectionString)
        {
            Database = "postgres"
        };

        await using var connection = new NpgsqlConnection(builder.ToString());
        await connection.OpenAsync().ConfigureAwait(false);
        await using var command = connection.CreateCommand();

        command.CommandText = $"CREATE DATABASE \"{_databaseName}\"";
        await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    private async Task DropDatabaseAsync()
    {
        var builder = new NpgsqlConnectionStringBuilder(_databaseConnectionString)
        {
            Database = "postgres"
        };

        await using var connection = new NpgsqlConnection(builder.ToString());
        await connection.OpenAsync().ConfigureAwait(false);
        await using var command = connection.CreateCommand();

        command.CommandText = $"""
            SELECT pg_terminate_backend(pid)
            FROM pg_stat_activity
            WHERE datname = '{_databaseName}' AND pid <> pg_backend_pid()
            """;
        await command.ExecuteNonQueryAsync().ConfigureAwait(false);

        command.CommandText = $"DROP DATABASE IF EXISTS \"{_databaseName}\"";
        await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    public sealed record ApiKeyResult(int UserId, int ApiKeyId, string ApiKey);

    public sealed record ReadToolSeed(
        int SpaceId,
        int FirstProjectId,
        int SecondProjectId,
        string Marker,
        string ProjectName,
        string ApiKey)
    {
        public int ProjectId => FirstProjectId;
    }
}
