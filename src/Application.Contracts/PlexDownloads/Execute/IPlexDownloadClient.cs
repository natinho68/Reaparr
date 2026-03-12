using FluentResults;
using Reaparr.Domain;

namespace Reaparr.Application.Contracts;

public record PlexDownloadSource
{
    public required string DownloadUrl { get; init; }

    public required VideoQuality Quality { get; init; }
}

public interface IPlexDownloadClient : IAsyncDisposable
{
    /// <summary>
    /// Starts the download workers for the <see cref="DownloadTaskGeneric"/> given during setup.
    /// </summary>
    /// <returns>Is successful.</returns>
    Task<Result> Start(
        DownloadTaskKey downloadTaskKey,
        CancellationToken cancellationToken = default,
        PlexDownloadSource? source = null
    );

    Task<Result> StopAsync();
}
