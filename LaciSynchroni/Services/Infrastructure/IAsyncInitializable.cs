namespace LaciSynchroni.Services.Infrastructure;

/// <summary>
/// Interface for services that require async initialization.
/// Services implementing this interface will have their InitializeAsync method
/// called during application startup.
/// </summary>
public interface IAsyncInitializable
{
    /// <summary>
    /// Performs async initialization of the service.
    /// This is called after the service is constructed but before it's used.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token for the initialization.</param>
    /// <returns>A task representing the async operation.</returns>
    Task InitializeAsync(CancellationToken cancellationToken = default);
}
