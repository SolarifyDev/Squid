namespace Squid.Message.Constants;

public static class CurrentUsers
{
    public static class InternalUser
    {
        public static int Id = 8888;
        public static string Name = "internal_user";
        public const string DisplayName = "System";
    }

    public static class AdminUser
    {
        public const string UserName = "admin";
        public const string DefaultPassword = "Admin@123456";
        public const string DisplayName = "Administrator";
    }
}
