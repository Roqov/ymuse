namespace Ymuse.Api;

public sealed record TrackItem(
    string Id,
    string Title,
    string Artist,
    string Album,
    string? AlbumId,
    long DurationMs,
    string? Cover,
    bool Available);

public sealed record AlbumItem(
    string Id,
    string Title,
    string Artist,
    string? Cover,
    int Year);

public sealed record PlaylistItem(
    string Uid,
    string Kind,
    string Title,
    int TrackCount,
    string? Cover,
    string? Description,
    string? Owner);

public sealed record StationItem(string Id, string Name, string? Icon = null);

public sealed record StationGroup(string Key, string Title, List<StationItem> Stations);

public sealed record SearchResult(
    List<TrackItem> Tracks,
    List<AlbumItem> Albums,
    List<PlaylistItem> Playlists);

public sealed record StationTracks(List<TrackItem> Tracks, string? BatchId);

public sealed record AccountInfo(bool LoggedIn, string? Name, string? Login, string? Uid, bool Plus);

public sealed record DeviceCode(string Code, string UserCode, string VerificationUrl, int Interval, int ExpiresIn);

public sealed record CollectionPage(
    string Title,
    string? Owner,
    string? Description,
    string? Cover,
    List<TrackItem> Tracks);

public sealed record AlbumPage(
    string Title,
    string Artist,
    string? Cover,
    int Year,
    List<TrackItem> Tracks);

public sealed class YandexException(string message) : Exception(message);
