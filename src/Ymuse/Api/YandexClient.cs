using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;

namespace Ymuse.Api;

/// <summary>
/// Тонкий клиент неофициального API Яндекс Музыки.
/// Токен приходит из OAuth device flow или вставляется вручную и хранится локально.
/// </summary>
public sealed class YandexClient
{
    const string Api = "https://api.music.yandex.net";
    const string OAuth = "https://oauth.yandex.ru";
    const string ClientId = "23cabbbdc6cd418abb4b39c32c41195d";
    const string ClientSecret = "53bc75238f0c4d08a118e51fe9203300";
    const string SignSalt = "XGRlBW9FXlekgbPrRHuSiA";

    static readonly HttpClient Http = CreateHttp();

    static HttpClient CreateHttp()
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            ConnectTimeout = TimeSpan.FromSeconds(15),
        };
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Ymuse/0.1 (Windows 10; YandexMusicAPI/2.2)");
        client.DefaultRequestHeaders.TryAddWithoutValidation("X-Yandex-Music-Client", "YandexMusicAndroid/24023621");
        client.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Language", "ru");
        return client;
    }

    public string? Token { get; set; }
    public string? Uid { get; private set; }

    /* ----------------------------- auth ------------------------------- */

    public async Task<DeviceCode> RequestDeviceCodeAsync(string deviceId)
    {
        var form = new Dictionary<string, string>
        {
            ["client_id"] = ClientId,
            ["device_id"] = deviceId,
            ["device_name"] = "Ymuse",
        };
        using var resp = await Http.PostAsync($"{OAuth}/device/code", new FormUrlEncodedContent(form));
        var text = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode) throw new YandexException(OAuthError(text, resp.StatusCode));

        using var doc = JsonDocument.Parse(text);
        var e = doc.RootElement;
        return new DeviceCode(
            J.Str(e, "device_code") ?? "",
            J.Str(e, "user_code") ?? "",
            J.StrAny(e, "verification_url", "verification_uri") ?? "https://ya.ru/device",
            J.Int(e, "interval") ?? 5,
            J.Int(e, "expires_in") ?? 300);
    }

    /// <summary>null — пользователь ещё не подтвердил код.</summary>
    public async Task<string?> PollDeviceTokenAsync(string deviceCode)
    {
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "device_code",
            ["code"] = deviceCode,
            ["client_id"] = ClientId,
            ["client_secret"] = ClientSecret,
        };
        using var resp = await Http.PostAsync($"{OAuth}/token", new FormUrlEncodedContent(form));
        var text = await resp.Content.ReadAsStringAsync();

        if (resp.IsSuccessStatusCode)
        {
            using var doc = JsonDocument.Parse(text);
            return J.Str(doc.RootElement, "access_token");
        }
        if (text.Contains("authorization_pending") || text.Contains("slow_down")) return null;
        throw new YandexException(OAuthError(text, resp.StatusCode));
    }

    static string OAuthError(string body, HttpStatusCode code)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var e = doc.RootElement;
            var err = J.Str(e, "error");
            var desc = J.Str(e, "error_description");
            if (!string.IsNullOrEmpty(desc)) return desc!;
            if (!string.IsNullOrEmpty(err))
            {
                return err switch
                {
                    "invalid_client" => "OAuth-клиент не принят",
                    "authorization_pending" => "Ожидание подтверждения",
                    "bad_verification_code" => "Неверный или истёкший код",
                    "expired_token" => "Код истёк",
                    _ => err!,
                };
            }
        }
        catch (JsonException) { }
        return $"Ошибка авторизации ({(int)code})";
    }

    /* ---------------------------- account ----------------------------- */

    public async Task<AccountInfo> GetAccountAsync()
    {
        if (string.IsNullOrEmpty(Token)) return new AccountInfo(false, null, null, null, false);
        try
        {
            var r = await GetResultAsync($"{Api}/account/status");
            var acc = J.Obj(r, "account");
            if (acc.ValueKind != JsonValueKind.Object) return new AccountInfo(false, null, null, null, false);
            Uid = J.Str(acc, "uid");
            var name = J.StrAny(acc, "displayName", "fullName", "login");
            var plus = acc.TryGetProperty("plus", out var p) && p.ValueKind == JsonValueKind.Object;
            return new AccountInfo(true, name, J.Str(acc, "login"), Uid, plus);
        }
        catch (YandexException)
        {
            return new AccountInfo(false, null, null, null, false);
        }
    }

    async Task<string> RequireUidAsync()
    {
        if (Uid != null) return Uid;
        var acc = await GetAccountAsync();
        return Uid ?? throw new YandexException("Требуется вход в аккаунт");
    }

    /* ----------------------------- search ----------------------------- */

    async Task<SearchResult> SearchAsync(string text, string type)
    {
        var r = await GetResultAsync($"{Api}/search?text={Uri.EscapeDataString(text)}&type={type}&page=0&nocorrect=false");
        var tracks = new List<TrackItem>();
        foreach (var t in J.Arr(J.Obj(r, "tracks"), "results")) tracks.Add(MapTrack(t));
        var albums = new List<AlbumItem>();
        foreach (var a in J.Arr(J.Obj(r, "albums"), "results")) albums.Add(MapAlbum(a));
        var playlists = new List<PlaylistItem>();
        foreach (var p in J.Arr(J.Obj(r, "playlists"), "results")) playlists.Add(MapPlaylist(p, null));
        return new SearchResult(tracks, albums, playlists);
    }

    public async Task<SearchResult> SearchAsync(string text)
    {
        var all = await SearchAsync(text, "all");
        if (all.Tracks.Count == 0 && all.Albums.Count == 0 && all.Playlists.Count == 0)
        {
            // старые аккаунты иногда не отдают секции в type=all — добираем по отдельности
            var tracks = await SearchAsync(text, "track");
            var albums = await SearchAsync(text, "album");
            var playlists = await SearchAsync(text, "playlist");
            return new SearchResult(tracks.Tracks, albums.Albums, playlists.Playlists);
        }
        return all;
    }

    /* ---------------------------- playlists --------------------------- */

    public async Task<List<PlaylistItem>> GetPlaylistsAsync()
    {
        var uid = await RequireUidAsync();
        var r = await GetResultAsync($"{Api}/users/{uid}/playlists/list");
        var list = new List<PlaylistItem>();
        if (r.ValueKind == JsonValueKind.Array)
        {
            foreach (var p in r.EnumerateArray()) list.Add(MapPlaylist(p, uid));
        }
        return list.Where(p => !string.IsNullOrEmpty(p.Kind)).ToList();
    }

    public async Task<CollectionPage> GetPlaylistAsync(string uid, string kind)
    {
        var r = await GetResultAsync($"{Api}/users/{Uri.EscapeDataString(uid)}/playlists/{Uri.EscapeDataString(kind)}");
        var title = J.Str(r, "title") ?? "Плейлист";
        var owner = J.Str(J.Obj(r, "owner"), "name");
        var description = J.Str(r, "description");
        var cover = J.Cover(r, "600x600");
        var tracks = await MapTrackRefsAsync(J.Arr(r, "tracks"), cover);
        return new CollectionPage(title, owner, description, cover, tracks);
    }

    public async Task<AlbumPage> GetAlbumAsync(string albumId)
    {
        var r = await GetResultAsync($"{Api}/albums/{Uri.EscapeDataString(albumId)}/with-tracks");
        var title = J.Str(r, "title") ?? "Альбом";
        var year = J.Int(r, "year") ?? 0;
        var artist = string.Join(", ", J.Arr(r, "artists").Select(a => J.Str(a, "name")).Where(n => !string.IsNullOrEmpty(n)));
        var cover = J.Cover(r, "600x600");

        var tracks = new List<TrackItem>();
        foreach (var volume in J.Arr(r, "volumes"))
        {
            foreach (var t in volume.EnumerateArray())
            {
                var mapped = MapTrack(t);
                if (mapped.Cover == null && cover != null) mapped = mapped with { Cover = cover };
                tracks.Add(mapped);
            }
        }
        return new AlbumPage(title, artist, cover, year, tracks);
    }

    /// <summary>Элементы плейлиста бывают как полными треками, так и ссылками {track:{id}}.</summary>
    async Task<List<TrackItem>> MapTrackRefsAsync(IEnumerable<JsonElement> items, string? coverFallback)
    {
        var order = new List<string>();
        var known = new Dictionary<string, TrackItem>();

        foreach (var item in items)
        {
            var t = J.Obj(item, "track");
            if (t.ValueKind != JsonValueKind.Object) t = item;
            var id = J.Str(t, "id");
            if (string.IsNullOrEmpty(id)) continue;
            order.Add(id!);
            if (t.TryGetProperty("title", out _))
            {
                var mapped = MapTrack(t);
                if (mapped.Cover == null && coverFallback != null) mapped = mapped with { Cover = coverFallback };
                known[id!] = mapped;
            }
        }

        var missing = order.Distinct().Where(id => !known.ContainsKey(id)).ToList();
        if (missing.Count > 0)
        {
            foreach (var chunk in Chunk(missing, 100))
            {
                var url = $"{Api}/tracks?track-ids={string.Join(",", chunk.Select(Uri.EscapeDataString))}";
                var r = await GetResultAsync(url);
                if (r.ValueKind != JsonValueKind.Array) continue;
                foreach (var t in r.EnumerateArray())
                {
                    var mapped = MapTrack(t);
                    if (mapped.Cover == null && coverFallback != null) mapped = mapped with { Cover = coverFallback };
                    known[mapped.Id] = mapped;
                }
            }
        }

        return order.Select(id => known.TryGetValue(id, out var t) ? t : null)
                    .Where(t => t != null)
                    .Select(t => t!)
                    .ToList();
    }

    static IEnumerable<List<string>> Chunk(List<string> src, int size)
    {
        for (var i = 0; i < src.Count; i += size)
            yield return src.GetRange(i, Math.Min(size, src.Count - i));
    }

    /* ------------------------------ rotor ----------------------------- */

    static readonly string[] StationTypes = ["mood", "activity", "genre", "epoch", "editorial", "tempo"];

    static readonly Dictionary<string, string> StationTypeTitles = new()
    {
        ["mood"] = "Настроение",
        ["activity"] = "Занятие",
        ["genre"] = "Жанры",
        ["epoch"] = "Эпохи",
        ["editorial"] = "Подборки",
        ["tempo"] = "Темп",
    };

    public async Task<List<StationGroup>> GetStationsAsync()
    {
        List<(string Id, string Name, string Type, string? Icon)> stations = [];
        foreach (var url in new[] { $"{Api}/rotor/stations/list", $"{Api}/rotor/stations/dashboard" })
        {
            try
            {
                stations = await FetchStationListAsync(url);
                if (stations.Count > 0) break;
            }
            catch (YandexException)
            {
                // пробуем следующий вариант
            }
        }

        var buckets = StationTypes.ToDictionary(t => t, _ => new List<StationItem>());
        var seen = new HashSet<string>();

        foreach (var (id, name, type, icon) in stations)
        {
            if (!buckets.TryGetValue(type, out var list)) continue;
            if (string.IsNullOrEmpty(id) || string.IsNullOrWhiteSpace(name)) continue;
            if (!seen.Add(id)) continue;
            list.Add(new StationItem(id, name, icon));
        }

        var groups = new List<StationGroup>
        {
            new("user", "Для вас", [new StationItem("user:onyourwave", "Моя волна")]),
        };
        foreach (var type in StationTypes)
        {
            if (buckets[type].Count > 0)
                groups.Add(new StationGroup(type, StationTypeTitles[type], buckets[type]));
        }
        return groups;
    }

    /// <summary>Ответ ручек станций — массив обёрток { station: {...} }, где id бывает объектом {type, tag}.</summary>
    async Task<List<(string Id, string Name, string Type, string? Icon)>> FetchStationListAsync(string url)
    {
        var r = await GetResultAsync(url);

        List<JsonElement> items = [];
        if (r.ValueKind == JsonValueKind.Array)
        {
            items.AddRange(r.EnumerateArray());
        }
        else if (r.ValueKind == JsonValueKind.Object)
        {
            if (r.TryGetProperty("stations", out var s) && s.ValueKind == JsonValueKind.Array) items.AddRange(s.EnumerateArray());
            else
            {
                foreach (var prop in r.EnumerateObject())
                {
                    if (prop.Value.ValueKind != JsonValueKind.Array) continue;
                    items.AddRange(prop.Value.EnumerateArray());
                    break;
                }
            }
        }

        var list = new List<(string, string, string, string?)>();
        foreach (var item in items)
        {
            var st = J.Obj(item, "station");
            if (st.ValueKind != JsonValueKind.Object) st = item;

            var name = J.StrAny(st, "name", "title");
            if (string.IsNullOrWhiteSpace(name)) continue;

            string id;
            string type;
            if (st.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.Object)
            {
                type = J.Str(idEl, "type") ?? "";
                var tag = J.Str(idEl, "tag") ?? "";
                id = type.Length > 0 ? $"{type}:{tag}" : tag;
            }
            else
            {
                id = J.Str(st, "id") ?? J.Str(st, "idForFrom") ?? "";
                type = id.Contains(':') ? id[..id.IndexOf(':')] : "";
            }

            if (id.Length == 0) continue;

            var iconUrl = J.Str(J.Obj(st, "icon"), "imageUrl");
            var icon = iconUrl == null ? null : J.Url(iconUrl.Replace("%%", "300x300"));
            list.Add((id, name!, type, icon));
        }
        return list;
    }

    /// <summary>Двоеточие в id станции (mood:aggressive) оставляем как есть — это валидный символ пути.</summary>
    static string StationPath(string station) => Uri.EscapeDataString(station).Replace("%3A", ":").Replace("%3a", ":");

    public async Task<object> GetStationInfoAsync(string station)
    {
        var root = await GetTrendingSafeAsync($"{Api}/rotor/station/{StationPath(station)}/info");
        JsonElement st = default;
        if (root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 0) st = root[0];
        else if (root.ValueKind == JsonValueKind.Object) st = root;

        var settings2 = J.Obj(st, "settings2");
        var settings = J.Obj(st, "settings");

        var stationObj = J.Obj(st, "station");
        var restrictions2 = J.Obj(stationObj, "restrictions2");
        var restrictions = restrictions2.ValueKind == JsonValueKind.Object
            ? restrictions2
            : J.Obj(stationObj, "restrictions");

        var options = new Dictionary<string, List<object>>();
        var values = new Dictionary<string, string?>();

        foreach (var key in new[] { "moodEnergy", "diversity", "language" })
        {
            values[key] = J.Str(settings2, key) ?? J.Str(settings, key);

            var field = J.Obj(restrictions, key);
            if (field.ValueKind != JsonValueKind.Object) field = J.Obj(settings2, key);

            var opts = new List<object>();
            if (field.ValueKind == JsonValueKind.Object && field.TryGetProperty("possibleValues", out var pv) && pv.ValueKind == JsonValueKind.Array)
            {
                foreach (var v in pv.EnumerateArray())
                {
                    if (v.ValueKind == JsonValueKind.String)
                    {
                        var val = v.GetString()!;
                        opts.Add(new { value = val, label = Label(val) });
                    }
                    else if (v.ValueKind == JsonValueKind.Object)
                    {
                        var val = J.StrAny(v, "value", "name");
                        if (val == null) continue;
                        opts.Add(new { value = val, label = J.Str(v, "name") ?? Label(val) });
                    }
                }
            }
            if (opts.Count > 0) options[key] = opts;
        }

        return new { options, values };
    }

    static string Label(string value) => value switch
    {
        "fun" => "Весело",
        "active" => "Бодро",
        "calm" => "Спокойно",
        "sad" => "Грустно",
        "all" => "Всё",
        "default" => "Любое",
        "favorite" => "Любимое",
        "discover" => "Незнакомое",
        "popular" => "Популярное",
        "diverse" => "Редкие",
        "any" => "Любой",
        "russian" => "Русский",
        "not-russian" => "Иностранный",
        "without-words" => "Без слов",
        _ => value,
    };

    public async Task<bool> SetStationSettingsAsync(string station, string? moodEnergy, string? diversity, string? language)
    {
        var body = new Dictionary<string, string?> { ["type"] = "rotor" };
        if (!string.IsNullOrEmpty(moodEnergy)) body["moodEnergy"] = moodEnergy;
        if (!string.IsNullOrEmpty(diversity)) body["diversity"] = diversity;
        if (!string.IsNullOrEmpty(language)) body["language"] = language;

        var url = $"{Api}/rotor/station/{StationPath(station)}/settings2";
        await PostRawAsync(url, JsonSerializer.Serialize(body));
        return true;
    }

    public async Task<StationTracks> GetStationTracksAsync(string station, string? queue)
    {
        var url = $"{Api}/rotor/station/{StationPath(station)}/tracks?settings2=true";
        if (!string.IsNullOrEmpty(queue)) url += $"&queue={Uri.EscapeDataString(queue)}";

        var r = await GetTrendingSafeAsync(url);
        var batchId = J.Str(r, "batchId");
        var tracks = new List<TrackItem>();
        var seen = new HashSet<string>();
        foreach (var item in J.Arr(r, "sequence"))
        {
            var t = J.Obj(item, "track");
            if (t.ValueKind != JsonValueKind.Object) continue;
            var mapped = MapTrack(t);
            if (!seen.Add(mapped.Id)) continue;
            tracks.Add(mapped);
        }
        return new StationTracks(tracks, batchId);
    }

    public async Task<bool> FeedbackAsync(string station, string type, string? trackId, string? batchId, int playedSeconds)
    {
        var payload = new Dictionary<string, object?>
        {
            ["type"] = type,
            ["timestamp"] = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture),
            ["trackId"] = trackId,
            ["batchId"] = batchId,
            ["totalPlayedSeconds"] = playedSeconds,
        };
        var url = $"{Api}/rotor/station/{StationPath(station)}/feedback";
        var json = JsonSerializer.Serialize(payload);
        try
        {
            await PostRawAsync(url, json);
        }
        catch (YandexException)
        {
            // часть серверов станций ждёт имя поля "event"
            var alt = new Dictionary<string, object?>(payload) { ["event"] = type };
            await PostRawAsync(url, JsonSerializer.Serialize(alt));
        }
        return true;
    }

    /* ------------------------------ stream ---------------------------- */

    public async Task<(string Url, bool Preview)> GetStreamAsync(string trackId)
    {
        var r = await GetResultAsync($"{Api}/tracks/{Uri.EscapeDataString(trackId)}/download-info?can_use_streaming=true");
        if (r.ValueKind != JsonValueKind.Array) throw new YandexException("Нет вариантов потока");

        JsonElement? best = null;
        JsonElement? preview = null;
        long bestRate = -1;

        foreach (var v in r.EnumerateArray())
        {
            var codec = J.Str(v, "codec") ?? "";
            if (!codec.Equals("mp3", StringComparison.OrdinalIgnoreCase)) continue;
            if (J.Bool(v, "direct")) continue;

            var isPreview = J.Bool(v, "preview");
            var rate = J.Long(v, "bitrateInKbps") ?? J.Long(v, "bitrate") ?? 0;
            if (isPreview) { preview ??= v; continue; }
            if (rate > bestRate) { best = v; bestRate = rate; }
        }

        JsonElement pick;
        if (best is not null) pick = best.Value;
        else if (preview is not null) pick = preview.Value;
        else throw new YandexException("Трек недоступен для потока (нужна подписка Плюс)");

        var infoUrl = J.Str(pick, "downloadInfoUrl") ?? throw new YandexException("Нет ссылки загрузки");

        var (host, path, ts, s) = await FetchDownloadInfoAsync(infoUrl);
        if (string.IsNullOrEmpty(host) || string.IsNullOrEmpty(path) || string.IsNullOrEmpty(ts) || string.IsNullOrEmpty(s))
            throw new YandexException("Не удалось получить подпись потока");

        var sign = Md5Hex(SignSalt + path.TrimStart('/') + s);
        return ($"https://{host}/get-mp3/{sign}/{ts}{path}", best is null);
    }

    static async Task<(string? host, string? path, string? ts, string? s)> FetchDownloadInfoAsync(string infoUrl)
    {
        var url = infoUrl + (infoUrl.Contains('?') ? "&" : "?") + "format=json";
        using var resp = await Http.GetAsync(url);
        var text = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode) throw new YandexException("Сервер загрузки недоступен");

        try
        {
            using var doc = JsonDocument.Parse(text);
            var e = doc.RootElement;
            return (J.Str(e, "host"), J.Str(e, "path"), J.Str(e, "ts"), J.Str(e, "s"));
        }
        catch (JsonException)
        {
            // старый XML-формат
        }

        try
        {
            var doc = XDocument.Parse(text);
            string? Val(string name) => doc.Root?.Element(name)?.Value;
            return (Val("host"), Val("path"), Val("ts"), Val("s"));
        }
        catch (Exception)
        {
            throw new YandexException("Неизвестный формат ответа сервера загрузки");
        }
    }

    static string Md5Hex(string input)
    {
        var bytes = MD5.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    /* --------------------------- http helpers ------------------------- */

    async Task<JsonElement> GetResultAsync(string url)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        AddAuth(req);
        using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
        var text = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode) throw new YandexException(ApiError(text, resp.StatusCode));
        if (string.IsNullOrWhiteSpace(text)) return default;

        using var doc = JsonDocument.Parse(text);
        var root = doc.RootElement;
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("result", out var result)) return result.Clone();
        return default;
    }

    /// <summary>Некоторые ручки возвращают массив прямо в result, некоторые — вложенный объект.</summary>
    async Task<JsonElement> GetTrendingSafeAsync(string url)
    {
        var r = await GetResultAsync(url);
        if (r.ValueKind == JsonValueKind.Object && r.TryGetProperty("result", out var inner)) return inner.Clone();
        return r;
    }

    async Task PostRawAsync(string url, string json)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        AddAuth(req);
        using var resp = await Http.SendAsync(req);
        var text = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode) throw new YandexException(ApiError(text, resp.StatusCode));
        if (text.Trim() is "\"error\"" or "error") throw new YandexException("Сервер отклонил настройку потока");
    }

    void AddAuth(HttpRequestMessage req)
    {
        if (string.IsNullOrEmpty(Token)) return;
        req.Headers.Authorization = new AuthenticationHeaderValue("OAuth", Token);
    }

    static string ApiError(string body, HttpStatusCode code)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.TryGetProperty("error", out var err))
            {
                if (err.ValueKind == JsonValueKind.Object)
                {
                    var msg = J.Str(err, "message");
                    if (!string.IsNullOrEmpty(msg)) return Friendly(msg!);
                    var name = J.Str(err, "name");
                    if (!string.IsNullOrEmpty(name)) return Friendly(name!);
                }
                else if (err.ValueKind == JsonValueKind.String)
                {
                    return Friendly(err.GetString()!);
                }
            }
            var direct = J.Str(root, "message");
            if (!string.IsNullOrEmpty(direct)) return Friendly(direct!);
        }
        catch (JsonException) { }

        return code switch
        {
            HttpStatusCode.Unauthorized => "Требуется вход в аккаунт Яндекс Музыки",
            HttpStatusCode.Forbidden => "Доступ запрещён",
            HttpStatusCode.NotFound => "Не найдено",
            HttpStatusCode.TooManyRequests => "Слишком много запросов, попробуйте позже",
            _ => $"Ошибка сервера ({(int)code})",
        };
    }

    static string Friendly(string message) => message switch
    {
        _ when message.Contains("Cannot build either user id", StringComparison.OrdinalIgnoreCase)
            => "Поток и персональные станции доступны после входа в аккаунт",
        _ when message.Contains("not available", StringComparison.OrdinalIgnoreCase)
            => "Трек или альбом недоступен",
        _ => message,
    };

    /* ---------------------------- mapping ----------------------------- */

    static string TrackId(JsonElement e) =>
        J.StrAny(e, "realId", "id") ?? "";

    static TrackItem MapTrack(JsonElement e)
    {
        var id = TrackId(e);
        var title = J.Str(e, "title") ?? "Без названия";

        var artist = string.Join(", ", J.Arr(e, "artists")
            .Select(a => J.Str(a, "name"))
            .Where(n => !string.IsNullOrWhiteSpace(n)));

        string album = "";
        string? albumId = null;
        foreach (var a in J.Arr(e, "albums"))
        {
            album = J.Str(a, "title") ?? "";
            albumId = J.Str(a, "id");
            break;
        }
        if (albumId == null) albumId = J.Str(e, "albumId");

        var ms = J.Long(e, "durationMs");
        if (ms == null)
        {
            var sec = J.Long(e, "duration");
            ms = sec * 1000;
        }

        var available = !e.TryGetProperty("available", out var av) || av.ValueKind != JsonValueKind.False;

        return new TrackItem(id, title, artist, album, albumId, ms ?? 0, J.Cover(e), available);
    }

    static AlbumItem MapAlbum(JsonElement e)
    {
        var id = J.StrAny(e, "id", "realId") ?? "";
        var title = J.Str(e, "title") ?? "Альбом";
        var artist = string.Join(", ", J.Arr(e, "artists")
            .Select(a => J.Str(a, "name"))
            .Where(n => !string.IsNullOrWhiteSpace(n)));
        return new AlbumItem(id, title, artist, J.Cover(e), J.Int(e, "year") ?? 0);
    }

    static PlaylistItem MapPlaylist(JsonElement e, string? ownerUid)
    {
        var kind = J.StrAny(e, "kind", "id") ?? "";
        var title = J.Str(e, "title") ?? "Плейлист";
        var count = J.Int(e, "trackCount") ?? 0;
        var owner = J.Str(J.Obj(e, "owner"), "name");
        var uid = J.StrAny(e, "uid", "ownerUid") ?? J.Str(J.Obj(e, "owner"), "uid") ?? ownerUid ?? "";
        var description = J.Str(e, "description");
        return new PlaylistItem(uid, kind, title, count, J.Cover(e), description, owner);
    }
}
