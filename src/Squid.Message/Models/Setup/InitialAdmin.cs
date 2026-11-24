namespace Squid.Message.Models.Setup;

/// <summary>
/// The first administrator of a fresh install: exactly one of <see cref="Password"/> /
/// <see cref="PasswordHash"/> is set. <see cref="GeneratedPasswordFile"/> is set when the password
/// was generated and must be handed to the operator through that file.
/// </summary>
public sealed record InitialAdmin(string UserName, string Password, string PasswordHash, bool MustChangePassword, string GeneratedPasswordFile);
