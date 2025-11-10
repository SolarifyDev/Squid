using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Squid.Core.Persistence;
using Squid.Message.Models.Deployments.Variable;
using Squid.Message.Enums;

namespace Squid.Core.Services.Deployments;

public class DeploymentVariableResolver : IDeploymentVariableResolver
{
    private readonly SquidDbContext _dbContext;

    public DeploymentVariableResolver(SquidDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<VariableSetSnapshotData> ResolveVariablesAsync(int deploymentId)
    {
        var deployment = await _dbContext.Set<Squid.Message.Domain.Deployments.Deployment>().FindAsync(deploymentId);

        if (deployment == null)
        {
            throw new InvalidOperationException($"Deployment {deploymentId} not found.");
        }

        var project = await _dbContext.Set<Squid.Message.Domain.Deployments.Project>().FirstOrDefaultAsync(p => p.Id == deployment.ProjectId);

        if (project == null)
        {
            throw new InvalidOperationException($"Project {deployment.ProjectId} not found.");
        }

        var variableSet = await _dbContext.Set<Squid.Message.Domain.Deployments.VariableSet>().FirstOrDefaultAsync(vs => vs.OwnerId == project.Id);

        if (variableSet != null)
        {
            // 优先查找快照
            var snapshotEntity = await _dbContext.Set<Squid.Message.Domain.Deployments.VariableSetSnapshot>()
                .Where(s => s.OriginalVariableSetId == variableSet.Id)
                .OrderByDescending(s => s.Version)
                .FirstOrDefaultAsync();

            if (snapshotEntity != null && snapshotEntity.SnapshotData != null && snapshotEntity.SnapshotData.Length > 0)
            {
                // 假设快照为 JSON 格式
                var json = System.Text.Encoding.UTF8.GetString(snapshotEntity.SnapshotData);
                var snapshot = JsonSerializer.Deserialize<VariableSetSnapshotData>(json);
                if (snapshot != null)
                {
                    return snapshot;
                }
            }
        }

        // 无快照时组装原始表数据
        var variables = new List<VariableSnapshotData>();

        if (variableSet != null)
        {
            var variableEntities = await _dbContext.Set<Squid.Message.Domain.Deployments.Variable>()
                .Where(v => v.VariableSetId == variableSet.Id)
                .ToListAsync();

            foreach (var v in variableEntities)
            {
                variables.Add(new VariableSnapshotData
                {
                    Name = v.Name,
                    Value = v.Value,
                    Type = v.Type,
                    IsSensitive = false,
                    Description = "",
                    SortOrder = 0,
                    Scopes = new List<Squid.Message.Models.Deployments.Variable.VariableScopeData>()
                });
            }
        }

        var fallbackSnapshot = new VariableSetSnapshotData
        {
            Id = 0,
            OwnerId = project.Id,
            OwnerType = VariableSetOwnerType.Project,
            Version = 1,
            CreatedAt = DateTimeOffset.UtcNow,
            Variables = variables,
            ScopeDefinitions = new Dictionary<string, List<string>>() // 可后续补充
        };

        return fallbackSnapshot;
    }
}
