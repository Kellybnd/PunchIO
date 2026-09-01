using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace PunchIO.Configuration;

/// <summary>Resolves file profiles from a bound configuration section.</summary>
public sealed class FileProfileProvider : IFileProfileProvider
{
    private readonly Dictionary<string, FileProfile> _profiles;

    /// <summary>Builds every profile in a configuration, validating each one.</summary>
    /// <param name="configuration">The bound <c>PunchIO</c> section.</param>
    /// <exception cref="ArgumentNullException"><paramref name="configuration"/> is <see langword="null"/>.</exception>
    /// <exception cref="FileProfileException">A profile is invalid.</exception>
    /// <remarks>
    /// Every profile is built here rather than lazily, so a typo in a profile
    /// nobody has opened yet still fails at startup.
    /// </remarks>
    public FileProfileProvider(PunchIoConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        _profiles = new Dictionary<string, FileProfile>(StringComparer.OrdinalIgnoreCase);

        foreach (var (name, profile) in configuration.Files)
            _profiles[name] = FileProfileFactory.Create(name, profile);
    }

    /// <inheritdoc />
    public IReadOnlyCollection<string> Names => _profiles.Keys;

    /// <inheritdoc />
    public FileProfile Get(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        return Find(name) ?? throw new FileProfileException(
            name, null,
            _profiles.Count == 0
                ? "no file profiles are configured under 'PunchIO:Files'."
                : $"is not configured. Known profiles: {string.Join(", ", _profiles.Keys)}.");
    }

    /// <inheritdoc />
    public FileProfile? Find(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        return _profiles.GetValueOrDefault(name);
    }
}

/// <summary>Registers PunchIO's configuration-driven services.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>The configuration section file profiles are read from.</summary>
    public const string SectionName = "PunchIO";

    /// <summary>
    /// Binds the <c>PunchIO</c> section and registers an
    /// <see cref="IFileProfileProvider"/> over it.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configuration">The configuration to bind from.</param>
    /// <returns><paramref name="services"/>, for chaining.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    /// <exception cref="FileProfileException">A configured profile is invalid.</exception>
    /// <remarks>
    /// Profiles are built and validated during registration, not on first use, so
    /// a misconfigured file is reported at startup.
    /// </remarks>
    public static IServiceCollection AddPunchIO(
        this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var bound = Bind(configuration);
        var provider = new FileProfileProvider(bound);

        services.AddSingleton<IFileProfileProvider>(provider);

        return services;
    }

    /// <summary>Binds the <c>PunchIO</c> section without registering anything.</summary>
    /// <param name="configuration">The configuration to bind from.</param>
    /// <returns>The bound section.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="configuration"/> is <see langword="null"/>.</exception>
    public static PunchIoConfiguration Bind(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var bound = new PunchIoConfiguration();

        // A section rather than the root when one is present, so callers can pass
        // either the whole configuration or the PunchIO section itself.
        var section = configuration.GetSection(SectionName);

        (section.Exists() ? section : configuration).Bind(bound);

        return bound;
    }
}
