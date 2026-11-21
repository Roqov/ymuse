using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Ymuse.Api;
using Ymuse.Core;

namespace Ymuse.Bridge;

/// <summary>Обрабатывает вызовы из JS: { id, method, params } → { id, ok, data | error }.</summary>
public sealed class Rpc
{
    readonly YandexClient _api;
    readonly SettingsStore _store;
    readonly UpdateService _updates;
    readonly Func<Task<string?>>? _browserSignIn;

    public Rpc(YandexClient api, SettingsStore store, UpdateService updates, Func<Task<string?>>? browserSignIn = null)
    {
        _api = api;
        _store = store;
        _updates = updates;
        _browserSignIn = browserSignIn;
    }

    public async Task<object> HandleAsync(string method, JsonElement p)
    {
        switch (method)
        {
            case "auth.status":
            {
                if (string.IsNullOrEmpty(_api.Token)) return new { loggedIn = false };
                var a = await _api.GetAccountAsync();
                if (!a.LoggedIn) return new { loggedIn = false };
                return new { loggedIn = true, name = a.Name, login = a.Login, uid = a.Uid, plus = a.Plus };
            }

            case "auth.deviceStart":
            {
                if (string.IsNullOrEmpty(_store.Data.DeviceId))
                {
                    _store.Data.DeviceId = RandomId(12);
                    _store.Save();
                }
                return await _api.RequestDeviceCodeAsync(_store.Data.DeviceId!);
            }

            case "auth.devicePoll":
            {
                var code = Param(p, "deviceCode") ?? throw new YandexException("Нет кода устройства");
                var token = await _api.PollDeviceTokenAsync(code);
                if (token == null) return new { status = "pending" };

                _api.Token = token;
                var acc = await _api.GetAccountAsync();
                if (!acc.LoggedIn)
                {
                    _api.Token = null;
                    throw new YandexException("Код принят, но аккаунт недоступен. Попробуйте войти токеном.");
                }
                _store.Data.Token = token;
                _store.Save();
                return new { status = "ok", name = acc.Name };
            }

            case "auth.browserLogin":
            {
                if (_browserSignIn == null) throw new YandexException("Окно входа недоступно");
                var token = await _browserSignIn();
                if (string.IsNullOrWhiteSpace(token)) return new { status = "cancelled" };

                _api.Token = token;
                var acc = await _api.GetAccountAsync();
                if (!acc.LoggedIn)
                {
                    _api.Token = null;
                    throw new YandexException("Яндекс не принял вход. Попробуйте ещё раз или войдите по коду.");
                }
                _store.Data.Token = token;
                _store.Save();
                return new { status = "ok", name = acc.Name };
            }

            case "app.info":
                return new
                {
                    version = UpdateService.CurrentVersion,
                    pending = _updates.PendingVersion,
                    autoUpdate = _updates.Enabled,
                    channel = _updates.Channel,
                };

            case "auth.setToken":
            {
                var token = (Param(p, "token") ?? "").Trim();
                if (token.Length < 20) throw new YandexException("Токен слишком короткий");
                _api.Token = token;
                var acc = await _api.GetAccountAsync();
                if (!acc.LoggedIn)
                {
                    _api.Token = null;
                    throw new YandexException("Токен не принят Яндексом");
                }
                _store.Data.Token = token;
                _store.Save();
                return new { ok = true, name = acc.Name };
            }

            case "auth.logout":
            {
                _api.Token = null;
                _store.Data.Token = null;
                _store.Save();
                return new { ok = true };
            }

            case "app.openUrl":
            {
                var url = Param(p, "url") ?? "";
                if (!url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) throw new YandexException("Некорректная ссылка");
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
                return new { ok = true };
            }

            case "rotor.stations":
                return new { groups = await _api.GetStationsAsync() };

            case "rotor.tracks":
                return await _api.GetStationTracksAsync(
                    Param(p, "station") ?? throw new YandexException("Нет станции"),
                    Param(p, "queue"));

            case "rotor.info":
                return await _api.GetStationInfoAsync(
                    Param(p, "station") ?? throw new YandexException("Нет станции"));

            case "rotor.settings":
                return new
                {
                    ok = await _api.SetStationSettingsAsync(
                        Param(p, "station") ?? throw new YandexException("Нет станции"),
                        Param(p, "moodEnergy"),
                        Param(p, "diversity"),
                        Param(p, "language")),
                };

            case "rotor.feedback":
                return new
                {
                    ok = await _api.FeedbackAsync(
                        Param(p, "station") ?? "",
                        Param(p, "type") ?? "trackStarted",
                        Param(p, "trackId"),
                        Param(p, "batchId"),
                        (int)(Num(p, "playedSeconds") ?? 0)),
                };

            case "search":
                return await _api.SearchAsync(Param(p, "text") ?? "");

            case "library.playlists":
                return await _api.GetPlaylistsAsync();

            case "library.playlist":
                return await _api.GetPlaylistAsync(
                    Param(p, "uid") ?? throw new YandexException("Нет пользователя"),
                    Param(p, "kind") ?? throw new YandexException("Нет плейлиста"));

            case "library.album":
                return await _api.GetAlbumAsync(Param(p, "id") ?? throw new YandexException("Нет альбома"));

            case "track.stream":
            {
                var stream = await _api.GetStreamAsync(Param(p, "id") ?? throw new YandexException("Нет трека"));
                return new { url = stream.Url, preview = stream.Preview };
            }
        }

        throw new YandexException("Неизвестный метод: " + method);
    }

    static string? Param(JsonElement p, string name)
    {
        if (p.ValueKind != JsonValueKind.Object || !p.TryGetProperty(name, out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number => v.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null,
        };
    }

    static double? Num(JsonElement p, string name)
    {
        if (p.ValueKind != JsonValueKind.Object || !p.TryGetProperty(name, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d)) return d;
        if (v.ValueKind == JsonValueKind.String && double.TryParse(v.GetString(), out var s)) return s;
        return null;
    }

    static string RandomId(int len)
    {
        const string alphabet = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
        var bytes = RandomNumberGenerator.GetBytes(len);
        var chars = new char[len];
        for (var i = 0; i < len; i++) chars[i] = alphabet[bytes[i] % alphabet.Length];
        return new string(chars);
    }
}
