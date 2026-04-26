namespace Squid.Message.Constants;

public static class CurrentUsers
{
    public static class InternalUser
    {
        /// <summary>
        /// Sentinel UserId for background-job / internal contexts.
        /// <para><b>P1-D.6 (Phase-7)</b>: was a mutable <c>static int</c>; flipped
        /// to <c>const</c> so any code that ever reads it is compile-time-pinned
        /// to <c>8888</c>. Combined with the <c>ICurrentUser.IsInternal</c>
        /// fail-closed guard in <see cref="Squid.Core.Middlewares.Authorization"/>,
        /// this closes the "DI mishap → ApiUser sees null HttpContext → falls
        /// back to 8888 → authorization middleware bypasses every check"
        /// vector.</para>
        /// </summary>
        public const int Id = 8888;

        public const string Name = "internal_user";
        public const string DisplayName = "System";
    }

    public static class AdminUser
    {
        public const string UserName = "admin";
        public const string DefaultPassword = "Admin@123456";
        public const string DisplayName = "Administrator";
    }
}
