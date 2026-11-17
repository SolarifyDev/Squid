namespace Squid.Message.Models.Deployments.ExternalFeed;

/// <summary>A feed's credentials as far as filling them is concerned: the username and whether a password is stored (never the password).</summary>
public sealed record ExternalFeedCredentialSlot(int FeedId, string Username, bool HasPassword);
