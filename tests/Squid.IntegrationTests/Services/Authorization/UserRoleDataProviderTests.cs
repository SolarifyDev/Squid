using Squid.Core.Persistence.Entities.Account;
using Squid.Core.Services.Authorization;

namespace Squid.IntegrationTests.Services.Authorization;

public class UserRoleDataProviderTests : TestBase
{
    public UserRoleDataProviderTests()
        : base("UserRoleDataProvider", "squid_it_user_role_data_provider")
    {
    }

    [Fact]
    public async Task Add_ThenGetById_ReturnsRole()
    {
        var roleId = 0;

        await Run<IUserRoleDataProvider>(async provider =>
        {
            var role = new UserRole { Name = "Test Role", Description = "Test description", IsBuiltIn = false };
            await provider.AddAsync(role).ConfigureAwait(false);
            roleId = role.Id;
        }).ConfigureAwait(false);

        await Run<IUserRoleDataProvider>(async provider =>
        {
            var role = await provider.GetByIdAsync(roleId).ConfigureAwait(false);
            role.ShouldNotBeNull();
            role.Name.ShouldBe("Test Role");
            role.Description.ShouldBe("Test description");
            role.IsBuiltIn.ShouldBeFalse();
        }).ConfigureAwait(false);
    }

    [Fact]
    public async Task GetByName_ReturnsMatchingRole()
    {
        await Run<IUserRoleDataProvider>(async provider =>
        {
            await provider.AddAsync(new UserRole { Name = "FindMe", Description = "d", IsBuiltIn = false }).ConfigureAwait(false);

            var found = await provider.GetByNameAsync("FindMe").ConfigureAwait(false);
            found.ShouldNotBeNull();
            found.Name.ShouldBe("FindMe");

            var notFound = await provider.GetByNameAsync("NotExist").ConfigureAwait(false);
            notFound.ShouldBeNull();
        }).ConfigureAwait(false);
    }

    [Fact]
    public async Task GetAll_ReturnsAllRoles()
    {
        await Run<IUserRoleDataProvider>(async provider =>
        {
            await provider.AddAsync(new UserRole { Name = "Role A", IsBuiltIn = false }).ConfigureAwait(false);
            await provider.AddAsync(new UserRole { Name = "Role B", IsBuiltIn = true }).ConfigureAwait(false);

            var all = await provider.GetAllAsync().ConfigureAwait(false);
            all.Count.ShouldBeGreaterThanOrEqualTo(2);
        }).ConfigureAwait(false);
    }

    [Fact]
    public async Task SetPermissions_ThenGetPermissions_ReturnsCorrect()
    {
        await Run<IUserRoleDataProvider>(async provider =>
        {
            var role = new UserRole { Name = "PermRole", IsBuiltIn = false };
            await provider.AddAsync(role).ConfigureAwait(false);

            await provider.SetPermissionsAsync(role.Id, new List<string> { "ProjectView", "ProjectEdit" }).ConfigureAwait(false);

            var perms = await provider.GetPermissionsAsync(role.Id).ConfigureAwait(false);
            perms.Count.ShouldBe(2);
            perms.ShouldContain("ProjectView");
            perms.ShouldContain("ProjectEdit");
        }).ConfigureAwait(false);
    }

    [Fact]
    public async Task SetPermissions_ReplacesExisting()
    {
        await Run<IUserRoleDataProvider>(async provider =>
        {
            var role = new UserRole { Name = "ReplacePerms", IsBuiltIn = false };
            await provider.AddAsync(role).ConfigureAwait(false);

            await provider.SetPermissionsAsync(role.Id, new List<string> { "ProjectView" }).ConfigureAwait(false);
            await provider.SetPermissionsAsync(role.Id, new List<string> { "ProjectEdit", "ProcessView" }).ConfigureAwait(false);

            var perms = await provider.GetPermissionsAsync(role.Id).ConfigureAwait(false);
            perms.Count.ShouldBe(2);
            perms.ShouldContain("ProjectEdit");
            perms.ShouldContain("ProcessView");
            perms.ShouldNotContain("ProjectView");
        }).ConfigureAwait(false);
    }

    [Fact]
    public async Task Delete_RemovesRole()
    {
        await Run<IUserRoleDataProvider>(async provider =>
        {
            var role = new UserRole { Name = "DeleteMe", IsBuiltIn = false };
            await provider.AddAsync(role).ConfigureAwait(false);

            await provider.DeleteAsync(role).ConfigureAwait(false);

            var deleted = await provider.GetByIdAsync(role.Id).ConfigureAwait(false);
            deleted.ShouldBeNull();
        }).ConfigureAwait(false);
    }

    [Fact]
    public async Task DeleteRole_CascadeDeletesPermissions()
    {
        var roleId = 0;

        await Run<IUserRoleDataProvider>(async provider =>
        {
            var role = new UserRole { Name = "CascadeRole", IsBuiltIn = false };
            await provider.AddAsync(role).ConfigureAwait(false);
            roleId = role.Id;

            await provider.SetPermissionsAsync(role.Id, new List<string> { "ProjectView", "ProjectEdit" }).ConfigureAwait(false);
            await provider.DeleteAsync(role).ConfigureAwait(false);
        }).ConfigureAwait(false);

        await Run<IUserRoleDataProvider>(async provider =>
        {
            var perms = await provider.GetPermissionsAsync(roleId).ConfigureAwait(false);
            perms.ShouldBeEmpty();
        }).ConfigureAwait(false);
    }

    [Fact]
    public async Task Update_PersistsChanges()
    {
        var roleId = 0;

        await Run<IUserRoleDataProvider>(async provider =>
        {
            var role = new UserRole { Name = "BeforeUpdate", Description = "Old", IsBuiltIn = false };
            await provider.AddAsync(role).ConfigureAwait(false);
            roleId = role.Id;

            role.Name = "AfterUpdate";
            role.Description = "New";
            await provider.UpdateAsync(role).ConfigureAwait(false);
        }).ConfigureAwait(false);

        await Run<IUserRoleDataProvider>(async provider =>
        {
            var role = await provider.GetByIdAsync(roleId).ConfigureAwait(false);
            role.ShouldNotBeNull();
            role.Name.ShouldBe("AfterUpdate");
            role.Description.ShouldBe("New");
        }).ConfigureAwait(false);
    }
}
