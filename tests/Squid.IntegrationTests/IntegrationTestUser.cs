namespace Squid.IntegrationTests;

public class IntegrationTestUser : IUserContext
{
    public string GetCurrentUserId()
    {
        return "__squid_tester";
    }

    public void SetCurrentUserId(string userId = null)
    {
    }
}