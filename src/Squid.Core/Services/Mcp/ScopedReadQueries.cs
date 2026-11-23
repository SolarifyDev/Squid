using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Squid.Core.Persistence.Db;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Authorization;
using Squid.Core.Services.Authorization.Exceptions;
using Squid.Core.Services.Identity;
using Squid.Message.Enums;
using Squid.Message.Requests.Mcp;
using Environment = Squid.Core.Persistence.Entities.Deployments.Environment;

namespace Squid.Core.Services.Mcp;

public partial interface IScopedReadQueries : IScopedDependency
{
    Task<ScopedReadPage<SpaceReadItem>> ListVisibleSpacesAsync(ListVisibleSpacesRequest request,
        CancellationToken cancellationToken = default);

    Task<ScopedReadPage<ProjectReadItem>?> ListVisibleProjectsAsync(ListVisibleProjectsRequest request,
        CancellationToken cancellationToken = default);

    Task<ScopedReadPage<EnvironmentReadItem>?> ListVisibleEnvironmentsAsync(
        ListVisibleEnvironmentsRequest request, CancellationToken cancellationToken = default);

    Task<ScopedReadPage<ReleaseReadItem>?> ListVisibleReleasesAsync(ListVisibleReleasesRequest request,
        CancellationToken cancellationToken = default);
}

public partial class ScopedReadQueries(
    ICurrentUser currentUser,
    IAuthorizationService authorizationService,
    IRepository repository) : IScopedReadQueries
{
    private const int MaxPageSize = 100;

    public async Task<ScopedReadPage<SpaceReadItem>> ListVisibleSpacesAsync(
        ListVisibleSpacesRequest request,
        CancellationToken cancellationToken = default)
    {
        var pageSize = ValidatePageSize(request.PageSize);
        var grants = await GetGrantsAsync(Permission.ProjectView, cancellationToken).ConfigureAwait(false);
        var visibleProjectSpaceIds = QueryVisibleProjects(grants)
            .Select(project => project.SpaceId)
            .Distinct();
        var query = repository.QueryNoTracking<Space>().Where(space => visibleProjectSpaceIds.Contains(space.Id));

        if (request.After != null)
            query = query.Where(space => space.Id > request.After.Id);

        var rows = await query
            .OrderBy(space => space.Id)
            .Take(pageSize + 1)
            .Select(space => new SpaceReadItem(space.Id, space.Name))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return BuildPage(rows, pageSize, item => new ScopedReadPosition(item.Id, null));
    }

    public async Task<ScopedReadPage<ProjectReadItem>?> ListVisibleProjectsAsync(
        ListVisibleProjectsRequest request,
        CancellationToken cancellationToken = default)
    {
        var pageSize = ValidatePageSize(request.PageSize);
        var grants = await GetGrantsAsync(Permission.ProjectView, cancellationToken).ConfigureAwait(false);

        if (!await CanAccessSpaceAsync(grants, request.SpaceId, cancellationToken).ConfigureAwait(false))
            return null;

        var query = QueryVisibleProjects(grants).Where(project => project.SpaceId == request.SpaceId);
        query = ApplyProjectKeyword(query, request.Keyword);

        if (request.After != null)
            query = query.Where(project => project.Id > request.After.Id);

        var rows = await query
            .OrderBy(project => project.Id)
            .Take(pageSize + 1)
            .Select(project => new ProjectReadItem(project.Id, project.Name, project.Slug))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return BuildPage(rows, pageSize, item => new ScopedReadPosition(item.Id, null));
    }

    public async Task<ScopedReadPage<EnvironmentReadItem>?> ListVisibleEnvironmentsAsync(
        ListVisibleEnvironmentsRequest request,
        CancellationToken cancellationToken = default)
    {
        var pageSize = ValidatePageSize(request.PageSize);
        var grants = await GetGrantsAsync(Permission.EnvironmentView, cancellationToken).ConfigureAwait(false);
        var project = await GetVisibleProjectAsync(grants, request.SpaceId, request.ProjectId, cancellationToken)
            .ConfigureAwait(false);

        if (project == null) return null;

        var query = ScopedReadAuthorizationFilter
            .Apply(repository.QueryNoTracking<Environment>(), grants, environment => environment.SpaceId,
                _ => project.Id, _ => project.ProjectGroupId, environment => environment.Id)
            .Where(environment => environment.SpaceId == project.SpaceId);
        query = ApplyEnvironmentKeyword(query, request.Keyword);

        if (request.After != null)
            query = query.Where(environment => environment.Id > request.After.Id);

        var rows = await query
            .OrderBy(environment => environment.Id)
            .Take(pageSize + 1)
            .Select(environment => new EnvironmentReadItem(environment.Id, environment.Name))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return BuildPage(rows, pageSize, item => new ScopedReadPosition(item.Id, null));
    }

    public async Task<ScopedReadPage<ReleaseReadItem>?> ListVisibleReleasesAsync(
        ListVisibleReleasesRequest request,
        CancellationToken cancellationToken = default)
    {
        var pageSize = ValidatePageSize(request.PageSize);
        var grants = await GetGrantsAsync(Permission.ReleaseView, cancellationToken).ConfigureAwait(false);
        var project = await GetVisibleProjectAsync(grants, request.SpaceId, request.ProjectId, cancellationToken)
            .ConfigureAwait(false);

        if (project == null) return null;

        var query = ScopedReadAuthorizationFilter
            .Apply(repository.QueryNoTracking<Release>(), grants, release => release.SpaceId,
                _ => project.Id, _ => project.ProjectGroupId)
            .Where(release => release.SpaceId == project.SpaceId && release.ProjectId == project.Id);

        if (request.After != null)
        {
            if (request.After.CreatedAt == null)
                throw new ArgumentException("Release cursors require CreatedAt.", nameof(request));

            var afterCreatedAt = request.After.CreatedAt.Value;
            query = query.Where(release => release.CreatedDate < afterCreatedAt ||
                                           (release.CreatedDate == afterCreatedAt && release.Id < request.After.Id));
        }

        var rows = await query
            .OrderByDescending(release => release.CreatedDate)
            .ThenByDescending(release => release.Id)
            .Take(pageSize + 1)
            .Select(release => new ReleaseReadItem(
                release.Id,
                release.Version,
                release.ChannelId,
                release.CreatedDate))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return BuildPage(rows, pageSize, item => new ScopedReadPosition(item.Id, item.CreatedAt));
    }

    private async Task<IReadOnlyList<ResourceScopeGrant>> GetGrantsAsync(Permission permission,
        CancellationToken cancellationToken)
    {
        if (currentUser.Id == null)
            throw new PermissionDeniedException(permission, "Authorization rejected: user identity not resolved.");

        return await authorizationService
            .GetResourceScopeGrantsAsync(currentUser.Id.Value, permission, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<bool> CanAccessSpaceAsync(IReadOnlyList<ResourceScopeGrant> grants, int spaceId,
        CancellationToken cancellationToken)
    {
        if (!ScopedReadAuthorizationFilter.CanAccessSpace(grants, spaceId)) return false;

        if (grants.Any(grant => (grant.SpaceId == null || grant.SpaceId == spaceId) &&
                                grant.ProjectIds.Count == 0 && grant.ProjectGroupIds.Count == 0))
            return await repository.AnyAsync<Space>(space => space.Id == spaceId, cancellationToken).ConfigureAwait(false);

        return await QueryVisibleProjects(grants)
            .AnyAsync(project => project.SpaceId == spaceId, cancellationToken)
            .ConfigureAwait(false);
    }

    private Task<VisibleProject?> GetVisibleProjectAsync(IReadOnlyList<ResourceScopeGrant> grants, int spaceId,
        int projectId, CancellationToken cancellationToken)
    {
        return QueryVisibleProjects(grants)
            .Where(project => project.Id == projectId && project.SpaceId == spaceId)
            .Select(project => new VisibleProject(project.Id, project.SpaceId, project.ProjectGroupId))
            .SingleOrDefaultAsync(cancellationToken);
    }

    private IQueryable<Project> QueryVisibleProjects(IReadOnlyList<ResourceScopeGrant> grants)
    {
        var projects = from project in repository.QueryNoTracking<Project>()
            join space in repository.QueryNoTracking<Space>() on project.SpaceId equals space.Id
            join projectGroup in repository.QueryNoTracking<ProjectGroup>() on
                new { GroupId = project.ProjectGroupId, project.SpaceId } equals
                new { GroupId = projectGroup.Id, projectGroup.SpaceId }
            select project;

        return ScopedReadAuthorizationFilter.Apply(projects, grants, project => project.SpaceId,
            project => project.Id, project => project.ProjectGroupId);
    }

    private static IQueryable<Project> ApplyProjectKeyword(IQueryable<Project> query, string? keyword)
    {
        var term = NormalizeKeyword(keyword);
        if (term == null) return query;

        return query.Where(project =>
            project.Name.ToLower().Contains(term) || project.Slug.ToLower().Contains(term));
    }

    private static IQueryable<Environment> ApplyEnvironmentKeyword(IQueryable<Environment> query, string? keyword)
    {
        var term = NormalizeKeyword(keyword);
        if (term == null) return query;

        return query.Where(environment => environment.Name.ToLower().Contains(term));
    }

    private static string? NormalizeKeyword(string? keyword)
    {
        var term = keyword?.Trim();
        return string.IsNullOrEmpty(term) ? null : term.ToLowerInvariant();
    }

    private static int ValidatePageSize(int pageSize)
    {
        if (pageSize is < 1 or > MaxPageSize)
            throw new ArgumentOutOfRangeException(nameof(pageSize), pageSize, "PageSize must be between 1 and 100.");

        return pageSize;
    }

    private static ScopedReadPage<T> BuildPage<T>(List<T> rows, int pageSize,
        Func<T, ScopedReadPosition> getPosition)
    {
        var hasNextPage = rows.Count > pageSize;
        if (hasNextPage) rows.RemoveAt(rows.Count - 1);

        var items = rows.Take(pageSize).ToList();
        var nextPosition = hasNextPage ? getPosition(items[^1]) : null;

        return new ScopedReadPage<T>(items, nextPosition);
    }

    private sealed record VisibleProject(int Id, int SpaceId, int ProjectGroupId);
}

internal static class ScopedReadAuthorizationFilter
{
    public static bool CanAccessSpace(IReadOnlyList<ResourceScopeGrant> grants, int spaceId)
    {
        return grants.Any(grant => grant.SpaceId == null || grant.SpaceId == spaceId);
    }

    public static IQueryable<T> Apply<T>(IQueryable<T> query, IReadOnlyList<ResourceScopeGrant> grants,
        Expression<Func<T, int>> spaceId,
        Expression<Func<T, int>>? projectId = null,
        Expression<Func<T, int>>? projectGroupId = null,
        Expression<Func<T, int>>? environmentId = null)
    {
        return query.Where(BuildPredicate(grants, spaceId, projectId, projectGroupId, environmentId));
    }

    private static Expression<Func<T, bool>> BuildPredicate<T>(IReadOnlyList<ResourceScopeGrant> grants,
        Expression<Func<T, int>> spaceId,
        Expression<Func<T, int>>? projectId,
        Expression<Func<T, int>>? projectGroupId,
        Expression<Func<T, int>>? environmentId)
    {
        var parameter = Expression.Parameter(typeof(T), "entity");
        Expression? body = null;

        foreach (var grant in grants)
        {
            Expression? grantBody = null;

            if (grant.SpaceId != null)
                grantBody = AndAlso(grantBody, Equals(spaceId, parameter, grant.SpaceId.Value));

            if (projectId != null && grant.ProjectIds.Count > 0)
                grantBody = AndAlso(grantBody, Contains(projectId, parameter, grant.ProjectIds));

            if (projectGroupId != null && grant.ProjectGroupIds.Count > 0)
                grantBody = AndAlso(grantBody, Contains(projectGroupId, parameter, grant.ProjectGroupIds));

            if (environmentId != null && grant.EnvironmentIds.Count > 0)
                grantBody = AndAlso(grantBody, Contains(environmentId, parameter, grant.EnvironmentIds));

            body = OrElse(body, grantBody ?? Expression.Constant(true));
        }

        return Expression.Lambda<Func<T, bool>>(body ?? Expression.Constant(false), parameter);
    }

    private static Expression AndAlso(Expression? left, Expression right)
    {
        return left == null ? right : Expression.AndAlso(left, right);
    }

    private static Expression OrElse(Expression? left, Expression right)
    {
        return left == null ? right : Expression.OrElse(left, right);
    }

    private static Expression Equals<T>(Expression<Func<T, int>> selector, ParameterExpression parameter, int value)
    {
        return Expression.Equal(ReplaceParameter(selector, parameter), Expression.Constant(value));
    }

    private static Expression Contains<T>(Expression<Func<T, int>> selector, ParameterExpression parameter,
        IEnumerable<int> values)
    {
        var array = Expression.NewArrayInit(typeof(int), values.Select(value => Expression.Constant(value)));
        return Expression.Call(typeof(Enumerable), nameof(Enumerable.Contains), [typeof(int)], array,
            ReplaceParameter(selector, parameter));
    }

    private static Expression ReplaceParameter<T>(Expression<Func<T, int>> selector, ParameterExpression parameter)
    {
        return new ParameterReplaceVisitor(selector.Parameters[0], parameter).Visit(selector.Body)!;
    }

    private sealed class ParameterReplaceVisitor(ParameterExpression source, ParameterExpression target) : ExpressionVisitor
    {
        protected override Expression VisitParameter(ParameterExpression node)
        {
            return node == source ? target : base.VisitParameter(node);
        }
    }
}
