using FastEndpoints;

namespace Reaparr.PlexApi.Contracts;

public record GetTranscodeUrlCommand : ICommand<Result<GetTranscodeUrlResult>>
{
    public required DownloadTaskKey DownloadTaskKey { get; init; }

    public required string MetaDataPath { get; init; }
}

public enum DeliveryMethod
{
    DirectFile,
    UniversalDash,
}

public enum DeliveryQualityTier
{
    Exact,
    NearExact,
    Lossy,
}

public record PlexDecisionSummary
{
    public required string ProfileName { get; init; }

    public required string ProfileVersion { get; init; }

    public required string Product { get; init; }

    public required string Platform { get; init; }

    public required string Device { get; init; }

    public required string Model { get; init; }

    public required string GeneralDecisionCode { get; init; }

    public required string GeneralDecisionText { get; init; }

    public required string DirectPlayDecisionCode { get; init; }

    public required string DirectPlayDecisionText { get; init; }

    public required string TranscodeDecisionCode { get; init; }

    public required string TranscodeDecisionText { get; init; }

    public required string VideoDecision { get; init; }

    public required string AudioDecision { get; init; }
}

public record GetTranscodeUrlResult
{
    public required string DownloadUrl { get; init; }

    public required DeliveryMethod Method { get; init; }

    public required DeliveryQualityTier QualityTier { get; init; }

    public VideoQuality TranscodedQuality { get; init; }

    public PlexDecisionSummary? DecisionSummary { get; init; }
}
