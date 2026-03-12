using System.Net;
using FastEndpoints;
using FluentValidation;
using Flurl;
using LukeHagar.PlexAPI.SDK.Models.Components;
using LukeHagar.PlexAPI.SDK.Models.Requests;
using Reaparr.Data.Contracts;
using Reaparr.PlexApi.Contracts;
using Reaparr.Settings.Contracts;

namespace Reaparr.PlexApi;

public class GetTranscodeUrlCommandValidator : AbstractValidator<GetTranscodeUrlCommand>
{
    public GetTranscodeUrlCommandValidator()
    {
        RuleFor(x => x).NotNull();
        RuleFor(x => x.DownloadTaskKey).NotNull();
        RuleFor(x => x.DownloadTaskKey.IsValid).Equal(true);
        RuleFor(x => x.MetaDataPath).NotEmpty();
        RuleFor(x => x.MetaDataPath).Must(x => x.Contains("/library/metadata/"));
    }
}

public class GetTranscodeUrlCommandHandler : ICommandHandler<GetTranscodeUrlCommand, Result<GetTranscodeUrlResult>>
{
    private const string DownloaderProfileName = "reaparr-downloader";
    private const string DownloaderProfileVersion = "v3";
    private const string DownloaderProduct = "Plex Web";
    private const string DownloaderVersion = "4.132.2";
    private const string DownloaderPlatform = "Linux";
    private const string DownloaderPlatformVersion = "6";
    private const string DownloaderDevice = "Chrome";
    private const string DownloaderModel = "Web";

    private const string ClientProfileExtra =
        "append-transcode-target-codec(type=videoProfile&context=streaming"
        + "&videoCodec=h264,hevc,av1,vp9,mpeg2video,mpeg4,vc1"
        + "&audioCodec=aac,ac3,eac3,dts,dca,mp3,flac,opus,vorbis,truehd"
        + "&protocol=dash)"
        + "+add-limitation(scope=videoCodec&scopeName=*&type=upperBound&name=video.bitDepth&value=12&isRequired=false)"
        + "+add-limitation(scope=videoCodec&scopeName=*&type=upperBound&name=video.width&value=3840&isRequired=false)"
        + "+add-limitation(scope=videoCodec&scopeName=*&type=upperBound&name=video.height&value=2160&isRequired=false)";

    private static readonly string ClientIdentifier = GenerateClientId();

    private readonly ILogger _log;
    private readonly IReaparrDbContext _dbContext;
    private readonly ICommandExecutor _commandExecutor;
    private readonly HttpClient _httpClient;
    private readonly IServerSettingsModule _serverSettingsModule;

    public GetTranscodeUrlCommandHandler(
        ILogger logger,
        IReaparrDbContext dbContext,
        ICommandExecutor commandExecutor,
        IHttpClientFactory httpClientFactory,
        IServerSettingsModule serverSettingsModule
    )
    {
        _log = logger.ForContext<GetTranscodeUrlCommandHandler>();
        _dbContext = dbContext;
        _commandExecutor = commandExecutor;
        _httpClient = httpClientFactory.CreateClient();
        _serverSettingsModule = serverSettingsModule;
    }

    public async Task<Result<GetTranscodeUrlResult>> ExecuteAsync(
        GetTranscodeUrlCommand command,
        CancellationToken cancellationToken
    )
    {
        var plexServerId = command.DownloadTaskKey.PlexServerId;

        var plexServerConnectionResult = await _dbContext.ChoosePlexServerConnection(plexServerId, cancellationToken);
        if (plexServerConnectionResult.IsFailed)
            return plexServerConnectionResult.ToResult().LogError();

        var tokenResult = await _dbContext.GetPlexServerTokenAsync(plexServerId, cancellationToken);
        if (tokenResult.IsFailed)
            return tokenResult.ToResult().LogError();

        var downloadTask = await _dbContext.GetDownloadTaskFileAsync(command.DownloadTaskKey, cancellationToken);
        if (downloadTask is null)
        {
            return ResultExtensions
                .EntityNotFound(nameof(DownloadTaskGeneric), command.DownloadTaskKey.ToString())
                .LogError();
        }

        var plexServerConnection = plexServerConnectionResult.Value;
        var token = tokenResult.Value;

        var directDownloadResult = await TryResolveDirectDownloadAsync(
            plexServerConnection.Url,
            downloadTask.FileLocationUrl,
            token,
            cancellationToken
        );

        if (directDownloadResult.IsSuccess)
        {
            _log.Here()
                .Information(
                    "Resolved direct Plex download URL for {DownloadTaskKey} using exact delivery",
                    command.DownloadTaskKey
                );

            return Result.Ok(
                new GetTranscodeUrlResult
                {
                    DownloadUrl = directDownloadResult.Value,
                    Method = DeliveryMethod.DirectFile,
                    QualityTier = DeliveryQualityTier.Exact,
                    TranscodedQuality = VideoQuality.None,
                    DecisionSummary = null,
                }
            );
        }

        var transcodeSessionId = GenerateSessionId();
        var plexSessionId = GenerateSessionId();
        var playbackSessionId = Guid.NewGuid().ToString();
        var playbackId = Guid.NewGuid().ToString();

        var decisionRequest = CreateDecisionRequest(command.MetaDataPath, transcodeSessionId, plexSessionId);
        var decisionResult = await _commandExecutor.Send(
            new GetDashTranscodeDecisionCommand(plexServerId, decisionRequest),
            cancellationToken
        );

        if (decisionResult.IsFailed)
        {
            _log.Here()
                .Warning(
                    "Direct delivery failed and universal decision request failed for {DownloadTaskKey}. Direct failure: {DirectFailure}",
                    command.DownloadTaskKey,
                    directDownloadResult.Errors[0].Message
                );

            return decisionResult.ToResult().LogError();
        }

        var decisionSummary = decisionResult.Value;
        var universalTierResult = ClassifyUniversalDash(decisionSummary);
        var finalDecisionSummary = CreateDecisionSummary(decisionSummary);

        _log.Here()
            .Information(
                "Delivery decision for {DownloadTaskKey}: directFailure={DirectFailure}, generalCode={GeneralCode}, directPlayCode={DirectPlayCode}, transcodeCode={TranscodeCode}, videoDecision={VideoDecision}, audioDecision={AudioDecision}, profile={ProfileName}, version={ProfileVersion}",
                command.DownloadTaskKey,
                directDownloadResult.Errors[0].Message,
                decisionSummary.GeneralDecisionCode,
                decisionSummary.DirectPlayDecisionCode,
                decisionSummary.TranscodeDecisionCode,
                decisionSummary.VideoDecision,
                decisionSummary.AudioDecision,
                finalDecisionSummary.ProfileName,
                finalDecisionSummary.ProfileVersion
            );

        _log.Here().Debug("Universal DASH decision summary: {@DecisionSummary}", finalDecisionSummary);

        if (universalTierResult.IsFailed)
        {
            return Result
                .Fail<GetTranscodeUrlResult>(
                    $"No viable Plex delivery path. Direct download failed: {directDownloadResult.Errors[0].Message}. "
                        + $"Universal DASH failed: {universalTierResult.Errors[0].Message}"
                )
                .LogError();
        }

        var machineIdentifier = await _dbContext.GetPlexServerMachineIdentifierById(plexServerId, cancellationToken);
        var allowLossyFallback = _serverSettingsModule.GetAllowStreamDownloader(machineIdentifier);
        if (universalTierResult.Value == DeliveryQualityTier.Lossy && !allowLossyFallback)
        {
            return Result
                .Fail<GetTranscodeUrlResult>(
                    $"Universal DASH fallback is available only as lossy video transcode and is disabled for server {machineIdentifier}."
                )
                .LogError();
        }

        var downloadUrl = new Url(plexServerConnection.Url)
            .AppendPathSegment("video/:/transcode/universal/start.mpd")
            .ApplyDashTranscodeQueryParams(decisionRequest, token)
            .SetQueryParam("fastSeek", 1)
            .SetQueryParam("addDebugOverlay", 0)
            .SetQueryParam("Accept-Language", "en")
            .SetQueryParam("X-Plex-Incomplete-Segments", 1)
            .SetQueryParam("X-Plex-Features", "external-media,indirect-media,hub-style-list")
            .SetQueryParam("X-Plex-Device-Screen-Resolution", "3840x2160,3840x2160")
            .SetQueryParam("X-Plex-Language", "en")
            .SetQueryParam("X-Plex-Session-Id", playbackSessionId)
            .SetQueryParam("X-Plex-Playback-Session-Id", playbackSessionId)
            .SetQueryParam("X-Plex-Playback-Id", playbackId)
            .ToString();

        return Result.Ok(
            new GetTranscodeUrlResult
            {
                DownloadUrl = downloadUrl,
                Method = DeliveryMethod.UniversalDash,
                QualityTier = universalTierResult.Value,
                TranscodedQuality = decisionSummary.TranscodedQuality,
                DecisionSummary = finalDecisionSummary,
            }
        );
    }

    private MakeDecisionRequest CreateDecisionRequest(
        string metadataPath,
        string transcodeSessionId,
        string plexSessionId
    ) =>
        new()
        {
            Accepts = Accepts.ApplicationJson,
            ClientIdentifier = ClientIdentifier,
            Product = DownloaderProduct,
            Version = DownloaderVersion,
            Platform = DownloaderPlatform,
            PlatformVersion = DownloaderPlatformVersion,
            Device = DownloaderDevice,
            Model = DownloaderModel,
            DeviceName = DownloaderProfileName,
            TranscodeType = TranscodeType.Video,
            HasMDE = BoolInt.True,
            Path = metadataPath,
            MediaIndex = 0,
            PartIndex = 0,
            Protocol = LukeHagar.PlexAPI.SDK.Models.Requests.Protocol.Dash,
            DirectPlay = BoolInt.False,
            DirectStream = BoolInt.True,
            DirectStreamAudio = BoolInt.True,
            SubtitleSize = 100,
            AudioBoost = 100,
            Location = LukeHagar.PlexAPI.SDK.Models.Requests.Location.Lan,
            AutoAdjustQuality = BoolInt.False,
            AutoAdjustSubtitle = BoolInt.True,
            MediaBufferSize = 102400,
            Subtitles = LukeHagar.PlexAPI.SDK.Models.Requests.Subtitles.None,
            VideoResolution = "3840x2160",
            VideoQuality = 100,
            XPlexClientProfileExtra = ClientProfileExtra,
            XPlexSessionIdentifier = plexSessionId,
            TranscodeSessionId = transcodeSessionId,
        };

    private PlexDecisionSummary CreateDecisionSummary(GetDashTranscodeDecisionResult summary) =>
        new()
        {
            ProfileName = DownloaderProfileName,
            ProfileVersion = DownloaderProfileVersion,
            Product = DownloaderProduct,
            Platform = DownloaderPlatform,
            Device = DownloaderDevice,
            Model = DownloaderModel,
            GeneralDecisionCode = summary.GeneralDecisionCode,
            GeneralDecisionText = summary.GeneralDecisionText,
            DirectPlayDecisionCode = summary.DirectPlayDecisionCode,
            DirectPlayDecisionText = summary.DirectPlayDecisionText,
            TranscodeDecisionCode = summary.TranscodeDecisionCode,
            TranscodeDecisionText = summary.TranscodeDecisionText,
            VideoDecision = summary.VideoDecision,
            AudioDecision = summary.AudioDecision,
        };

    private static Result<DeliveryQualityTier> ClassifyUniversalDash(GetDashTranscodeDecisionResult summary)
    {
        if (string.Equals(summary.TranscodeDecisionCode, "4005", StringComparison.OrdinalIgnoreCase))
        {
            return Result.Fail<DeliveryQualityTier>(
                $"Plex universal DASH is unavailable: {summary.TranscodeDecisionCode} {summary.TranscodeDecisionText}"
            );
        }

        if (string.Equals(summary.VideoDecision, "copy", StringComparison.OrdinalIgnoreCase))
            return Result.Ok(DeliveryQualityTier.NearExact);

        if (string.Equals(summary.VideoDecision, "transcode", StringComparison.OrdinalIgnoreCase))
            return Result.Ok(DeliveryQualityTier.Lossy);

        return Result.Fail<DeliveryQualityTier>(
            $"Plex universal DASH decision did not produce a usable video stream decision. VideoDecision={summary.VideoDecision}"
        );
    }

    private async Task<Result<string>> TryResolveDirectDownloadAsync(
        string connectionUrl,
        string fileLocationUrl,
        string token,
        CancellationToken cancellationToken
    )
    {
        var defaultDownloadUrl = connectionUrl
            .AppendPathSegment(fileLocationUrl)
            .SetQueryParam("X-Plex-Token", token)
            .ToString();
        var initialProbe = await ProbeDownloadUrl(defaultDownloadUrl, cancellationToken);
        if (initialProbe.IsSuccess && IsSuccessStatusCode(initialProbe.Value))
            return Result.Ok(defaultDownloadUrl);

        var initialFailure = initialProbe.IsFailed
            ? initialProbe.Errors[0].Message
            : FormatStatusFailure(defaultDownloadUrl, initialProbe.Value);

        if (initialProbe.IsSuccess && initialProbe.Value != HttpStatusCode.Forbidden)
            return Result.Fail<string>(initialFailure);

        var downloadUrlWithFlag = defaultDownloadUrl.SetQueryParam("download", 1).ToString();
        var fallbackProbe = await ProbeDownloadUrl(downloadUrlWithFlag, cancellationToken);
        if (fallbackProbe.IsSuccess && IsSuccessStatusCode(fallbackProbe.Value))
            return Result.Ok(downloadUrlWithFlag);

        var fallbackFailure = fallbackProbe.IsFailed
            ? fallbackProbe.Errors[0].Message
            : FormatStatusFailure(downloadUrlWithFlag, fallbackProbe.Value);

        return Result.Fail<string>($"{initialFailure} | {fallbackFailure}");
    }

    private async Task<Result<HttpStatusCode>> ProbeDownloadUrl(string downloadUrl, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, downloadUrl);
            using var response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken
            );

            return Result.Ok(response.StatusCode);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Result.Fail<HttpStatusCode>($"Failed to probe Plex direct download URL: {ex.Message}");
        }
    }

    private static string FormatStatusFailure(string downloadUrl, HttpStatusCode statusCode) =>
        $"Plex direct download URL probe failed for {downloadUrl} with status {(int)statusCode} ({statusCode})";

    private static bool IsSuccessStatusCode(HttpStatusCode statusCode) => (int)statusCode is >= 200 and < 300;

    private static string GenerateSessionId() => Guid.NewGuid().ToString("N")[..24];

    private static string GenerateClientId() => $"{Guid.NewGuid():N}"[..25];
}
