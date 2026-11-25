namespace Squid.Core.Services.Publishing.Exceptions;

/// <summary>A project that was not created by publishing already has the name the new project would take.</summary>
public sealed class PublishProjectNameConflictException(string name) : InvalidOperationException($"A project named '{name}' already exists and was not created by publishing a package. Choose another project name.")
{
    public string Name { get; } = name;
}
