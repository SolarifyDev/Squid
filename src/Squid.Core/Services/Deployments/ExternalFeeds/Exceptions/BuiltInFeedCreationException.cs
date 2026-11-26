namespace Squid.Core.Services.Deployments.ExternalFeeds.Exceptions;

public sealed class BuiltInFeedCreationException() : InvalidOperationException("The built-in feed type is reserved: each space gets its built-in feed automatically and cannot have a second one");
