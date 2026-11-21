using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Ymuse.Core;

/// <summary>
/// Тихий автоапдейт.
///
/// Канал определяется сам, без настройки:
///   1) YMUSE_UPDATE_URL или settings.updateUrl — если заданы явно, это JSON-манифест;
///   2) иначе — git-remote проекта (<c>.git/config</c>, RepositoryUrl в сборке, GITHUB_REPOSITORY)
///      и GitHub Releases API: /releases/latest, затем /releases;
///   3) если ни канала, ни подходящего ассета нет — ничего не происходит, без ошибок в UI.
///
/// Скачивание идёт в staging, SHA-256 сверяется, если источник отдал digest.
/// Применение — отдельным процессом только после штатного выхода: воспроизведение
/// не прерывается, settings.json и токен в %APPDATA% не затрагиваются.
///
/// Манифест (для явного URL):
/// { "version": "0.3.0", "url": "https://.../ymuse-0.3.0.zip", "sha256": "..." }
/// </summary>
public sealed class UpdateService
{
    static readonly HttpClient Http = CreateHttp();
    static readonly object LogLock = new();

    readonly SettingsStore _store;
    readonly Lazy<(string Owner, string Repo)?> _repository;

    public UpdateService(SettingsStore store)
    {
        _store = store;
        _repository = new Lazy<(string Owner, string Repo)?>(FindRepository);
    }

    public static string CurrentVersion
    {
        get
        {
            var v = Assembly.GetExecutingAssembly().GetName().Version;
            return v == null ? "0.0.0" : $"{v.Major}.{v.Minor}.{v.Build}";
        }
    }

    public static string AppDataRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Ymuse");

    public string UpdatesRoot => Path.Combine(AppDataRoot, "updates");

    public string LogPath => Path.Combine(AppDataRoot, "update.log");

    /// <summary>Явно заданный источник (манифест). Пусто — канал ищем сами.</summary>
    public string? ExplicitUrl
    {
        get
        {
            var env = Environment.GetEnvironmentVariable("YMUSE_UPDATE_URL");
            return !string.IsNullOrWhiteSpace(env) ? env : _store.Data.UpdateUrl;
        }
    }

    sealed record ChannelSource(string Kind, string? ManifestUrl, (string Owner, string Repo)? Repo, string Label);

    /// <summary>Порядок: YMUSE_UPDATE_URL → settings.updateUrl → channel.json рядом с exe → git-remote/GitHub.</summary>
    ChannelSource? ResolveSource()
    {
        var envUrl = Environment.GetEnvironmentVariable("YMUSE_UPDATE_URL");
        if (!string.IsNullOrWhiteSpace(envUrl)) return new ChannelSource("manifest", envUrl, null, "manifest (env)");

        if (!string.IsNullOrWhiteSpace(_store.Data.UpdateUrl))
            return new ChannelSource("manifest", _store.Data.UpdateUrl, null, "manifest (settings)");

        var fromFile = ReadChannelFile();
        if (fromFile != null) return fromFile;

        var repo = _repository.Value;
        if (repo == null) return null;
        return new ChannelSource("github", null, repo, $"github:{repo.Value.Owner}/{repo.Value.Repo}");
    }

    public string? Channel => ResolveSource()?.Label;

    public bool Enabled => ResolveSource() != null;

    /// <summary>Канал можно положить рядом с exe: {"manifest":"https://..."} или {"repository":"https://github.com/o/r"}.</summary>
    static ChannelSource? ReadChannelFile()
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "channel.json");
            if (!File.Exists(path)) return null;

            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;

            var manifest = Str(root, "manifest");
            if (!string.IsNullOrWhiteSpace(manifest))
                return new ChannelSource("manifest", manifest, null, "manifest (channel.json)");

            var repository = Str(root, "repository");
            if (TryParseGitHub(repository, out var repo))
                return new ChannelSource("github", null, repo, $"github:{repo.Owner}/{repo.Repo} (channel.json)");
        }
        catch
        {
            // битый channel.json — как будто его нет
        }
        return null;
    }

    public string? PendingVersion => _store.Data.PendingVersion;

    static HttpClient CreateHttp()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Ymuse/" + CurrentVersion);
        client.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/vnd.github+json");
        return client;
    }

    /// <summary>Сбрасывает состояние, если обновление уже применено или staging пропал.</summary>
    public void NormalizeState()
    {
        var pending = _store.Data.PendingVersion;
        var dir = _store.Data.PendingDir;

        var missing = string.IsNullOrEmpty(dir) || !Directory.Exists(dir);
        var applied = !string.IsNullOrEmpty(pending) && CompareVersions(CurrentVersion, pending) >= 0;

        if (missing || applied)
        {
            if (!string.IsNullOrEmpty(pending) || !string.IsNullOrEmpty(dir))
            {
                Log($"pending сброшен (missing={missing}, applied={applied})");
            }
            _store.Data.PendingVersion = null;
            _store.Data.PendingDir = null;
            _store.Save();
        }
    }

    /// <summary>Тихая проверка: ошибки сети/диска проглатываются, воспроизведение не трогаем.</summary>
    public async Task CheckAsync()
    {
        try
        {
            var source = ResolveSource();
            if (source == null)
            {
                Log("канал обновлений не найден — проверка пропущена");
                return;
            }

            var channel = source.Label;
            var release = await ResolveAsync(source);
            if (release == null)
            {
                Log($"{channel}: релиз не получен или нет подходящего ассета");
                return;
            }

            Log($"{channel}: latest={release.Version} current={CurrentVersion} asset={release.AssetName}");

            if (CompareVersions(release.Version, CurrentVersion) <= 0)
            {
                Log("обновление не требуется");
                return;
            }

            if (string.Equals(_store.Data.PendingVersion, release.Version, StringComparison.Ordinal) &&
                !string.IsNullOrEmpty(_store.Data.PendingDir) &&
                Directory.Exists(_store.Data.PendingDir))
            {
                Log($"версия {release.Version} уже в staging, ждём выхода");
                return;
            }

            Directory.CreateDirectory(UpdatesRoot);
            var staging = Path.Combine(UpdatesRoot, release.Version);
            if (Directory.Exists(staging)) Directory.Delete(staging, true);
            Directory.CreateDirectory(staging);

            var zipPath = Path.Combine(UpdatesRoot, release.Version + ".zip");
            await using (var remote = await Http.GetStreamAsync(release.AssetUrl))
            await using (var file = File.Create(zipPath))
            {
                await remote.CopyToAsync(file);
            }
            Log($"скачано {new FileInfo(zipPath).Length} байт в {zipPath}");

            if (!string.IsNullOrWhiteSpace(release.Sha256))
            {
                var actual = Convert.ToHexString(await Sha256Async(zipPath)).ToLowerInvariant();
                if (!string.Equals(actual, release.Sha256!.Trim().ToLowerInvariant(), StringComparison.Ordinal))
                {
                    Log($"SHA-256 НЕ совпал: ожидали {release.Sha256}, получили {actual} — удаляем");
                    File.Delete(zipPath);
                    Directory.Delete(staging, true);
                    return;
                }
                Log($"SHA-256 совпал: {actual}");
            }
            else
            {
                Log("источник не дал контрольную сумму — проверка SHA-256 пропущена");
            }

            ZipFile.ExtractToDirectory(zipPath, staging, overwriteFiles: true);
            File.Delete(zipPath);

            _store.Data.PendingVersion = release.Version;
            _store.Data.PendingDir = staging;
            _store.Save();

            Log($"staging готов: {staging} (применим после выхода)");
        }
        catch (Exception ex)
        {
            Log("ошибка проверки: " + ex.GetType().Name + ": " + ex.Message);
        }
    }

    /// <summary>Ставит обновление после выхода. Проигрывание к этому моменту уже остановлено пользователем.</summary>
    public void ApplyOnExit()
    {
        var staged = _store.Data.PendingDir;
        if (string.IsNullOrEmpty(staged) || !Directory.Exists(staged)) return;

        var appDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (staged.TrimEnd(Path.DirectorySeparatorChar).Equals(appDir, StringComparison.OrdinalIgnoreCase)) return;

        try
        {
            var scriptPath = Path.Combine(Path.GetTempPath(), $"ymuse-update-{Guid.NewGuid():N}.ps1");
            File.WriteAllText(scriptPath, BuildApplyScript(staged, appDir, Environment.ProcessId), new UTF8Encoding(true));

            Process.Start(new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{scriptPath}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            });

            Log($"применение запланировано: {staged} -> {appDir}");
        }
        catch (Exception ex)
        {
            Log("не удалось запустить применение: " + ex.Message);
        }
    }

    /* ------------------------- discovery ------------------------------ */

    sealed record ReleaseInfo(string Version, string AssetName, string AssetUrl, string? Sha256, string Channel);

    async Task<ReleaseInfo?> ResolveAsync(ChannelSource source)
    {
        if (source.Kind == "manifest" && !string.IsNullOrWhiteSpace(source.ManifestUrl))
            return await FromManifestAsync(source.ManifestUrl!);

        if (source.Repo is { } repo)
            return await FromGitHubAsync(repo.Owner, repo.Repo);

        return null;
    }

    async Task<ReleaseInfo?> FromManifestAsync(string url)
    {
        var text = await Http.GetStringAsync(url);
        using var doc = JsonDocument.Parse(text);
        var root = doc.RootElement;

        var version = Str(root, "version");
        var zipUrl = Str(root, "url");
        var sha = Str(root, "sha256");
        if (string.IsNullOrWhiteSpace(version) || string.IsNullOrWhiteSpace(zipUrl)) return null;

        return new ReleaseInfo(version!, Path.GetFileName(new Uri(zipUrl).AbsolutePath), zipUrl!, sha, "manifest");
    }

    async Task<ReleaseInfo?> FromGitHubAsync(string owner, string repo)
    {
        var api = (Environment.GetEnvironmentVariable("YMUSE_GITHUB_API") ?? "https://api.github.com").TrimEnd('/');
        var channel = $"github:{owner}/{repo}";

        var release = await GetGitHubJsonAsync($"{api}/repos/{owner}/{repo}/releases/latest");
        if (release == null || Str(release.Value, "tag_name") == null)
        {
            release = null;
            var list = await GetGitHubJsonAsync($"{api}/repos/{owner}/{repo}/releases?per_page=20");
            if (list is { ValueKind: JsonValueKind.Array } array)
            {
                foreach (var item in array.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object) continue;
                    if (Bool(item, "draft") || Bool(item, "prerelease")) continue;
                    if (Str(item, "tag_name") == null) continue;
                    release = item;
                    break;
                }
            }
        }

        if (release == null) return null;

        var tag = Str(release.Value, "tag_name");
        var version = NormalizeVersion(tag);
        if (string.IsNullOrEmpty(version)) return null;

        var asset = PickAsset(release.Value);
        if (asset == null)
        {
            Log($"{channel}: у релиза {tag} нет ассета под Windows x64");
            return null;
        }

        var assetName = Str(asset.Value, "name") ?? "";
        var assetUrl = Str(asset.Value, "browser_download_url");
        if (string.IsNullOrWhiteSpace(assetUrl)) return null;

        return new ReleaseInfo(version, assetName, assetUrl!, ShaFromDigest(asset.Value), channel);
    }

    /// <summary>Ассет именно под текущую сборку: Windows x64, zip, без установщика.</summary>
    static JsonElement? PickAsset(JsonElement release)
    {
        JsonElement? best = null;
        var bestScore = 0;

        foreach (var asset in Assets(release))
        {
            var name = Str(asset, "name");
            if (string.IsNullOrWhiteSpace(name)) continue;
            var score = ScoreAsset(name!);
            if (score <= 0) continue;
            if (score > bestScore)
            {
                best = asset;
                bestScore = score;
            }
        }
        return best;
    }

    static int ScoreAsset(string name)
    {
        var n = name.ToLowerInvariant();
        if (!n.EndsWith(".zip")) return 0;
        if (n.Contains("setup") || n.Contains("installer")) return 0;
        if (n.Contains("aarch64") || n.Contains("arm64") || n.Contains("armv") ||
            n.Contains("i686") || n.Contains("i386") || n.Contains("win32") || n.Contains("-386")) return 0;

        var windows = n.Contains("windows") || n.Contains("win-") || n.Contains("win_") || n.Contains("win64");
        if (!windows) return 0;

        var x64 = n.Contains("x86_64") || n.Contains("x64") || n.Contains("amd64") ||
                  n.Contains("win64") || n.Contains("64-bit") || n.Contains("64bit");
        if (!x64) return 0;

        var score = 10;
        if (n.Contains("msvc")) score += 5;
        if (n.Contains("portable")) score += 2;
        return score;
    }

    static IEnumerable<JsonElement> Assets(JsonElement release)
    {
        if (release.ValueKind != JsonValueKind.Object ||
            !release.TryGetProperty("assets", out var assets) ||
            assets.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }
        foreach (var asset in assets.EnumerateArray()) yield return asset;
    }

    /// <summary>GitHub отдаёт digest вида "sha256:..."; если поля нет — проверки не будет.</summary>
    static string? ShaFromDigest(JsonElement asset)
    {
        var digest = Str(asset, "digest");
        if (string.IsNullOrWhiteSpace(digest)) return null;
        return digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ? digest[7..].Trim() : null;
    }

    /// <summary>Тег вида "v0.3.0" или "release-1.2.3" -> "0.3.0" / "1.2.3".</summary>
    static string? NormalizeVersion(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        var match = Regex.Match(tag, @"\d+(?:\.\d+){0,3}");
        return match.Success ? match.Value : null;
    }

    static async Task<JsonElement?> GetGitHubJsonAsync(string url)
    {
        using var response = await Http.GetAsync(url);
        if (!response.IsSuccessStatusCode) return null;
        var text = await response.Content.ReadAsStringAsync();
        if (string.IsNullOrWhiteSpace(text)) return null;

        using var doc = JsonDocument.Parse(text);
        return doc.RootElement.Clone();
    }

    /* --------------------------- repository --------------------------- */

    /// <summary>git-remote проекта: .git/config рядом с приложением, RepositoryUrl сборки или GITHUB_REPOSITORY.</summary>
    public static (string Owner, string Repo)? FindRepository()
    {
        var fromEnv = Environment.GetEnvironmentVariable("GITHUB_REPOSITORY");
        if (!string.IsNullOrWhiteSpace(fromEnv) && fromEnv.Contains('/') && TrySplit(fromEnv, out var envRepo))
        {
            return envRepo;
        }

        foreach (var attr in Assembly.GetExecutingAssembly().GetCustomAttributes<AssemblyMetadataAttribute>())
        {
            if (!string.Equals(attr.Key, "RepositoryUrl", StringComparison.OrdinalIgnoreCase)) continue;
            if (TryParseGitHub(attr.Value, out var fromAssembly)) return fromAssembly;
        }

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var depth = 0; depth < 6 && dir != null; depth++, dir = dir.Parent)
        {
            if (TryReadGitConfig(dir.FullName, out var fromGit)) return fromGit;
        }

        return null;
    }

    /// <summary>Обычный репозиторий (.git — папка) и worktree/submodule (.git — файл с gitdir).</summary>
    static bool TryReadGitConfig(string repoDir, out (string Owner, string Repo) repo)
    {
        repo = default;
        var dotGit = Path.Combine(repoDir, ".git");

        if (Directory.Exists(dotGit))
            return TryReadOriginUrl(Path.Combine(dotGit, "config"), out repo);

        if (!File.Exists(dotGit)) return false;

        try
        {
            var text = File.ReadAllText(dotGit).Trim();
            if (!text.StartsWith("gitdir:", StringComparison.OrdinalIgnoreCase)) return false;

            var gitDir = text["gitdir:".Length..].Trim();
            if (!Path.IsPathRooted(gitDir)) gitDir = Path.GetFullPath(Path.Combine(repoDir, gitDir));

            var configPath = Path.Combine(gitDir, "config");
            var commonFile = Path.Combine(gitDir, "commondir");
            if (File.Exists(commonFile))
            {
                var common = File.ReadAllText(commonFile).Trim();
                if (!Path.IsPathRooted(common)) common = Path.GetFullPath(Path.Combine(gitDir, common));
                configPath = Path.Combine(common, "config");
            }

            return File.Exists(configPath) && TryReadOriginUrl(configPath, out repo);
        }
        catch
        {
            return false;
        }
    }

    static bool TrySplit(string value, out (string Owner, string Repo) repo)
    {
        repo = default;
        var parts = value.Split('/', 2);
        if (parts.Length != 2 || parts[0].Length == 0 || parts[1].Length == 0) return false;
        repo = (parts[0], parts[1]);
        return true;
    }

    static bool TryReadOriginUrl(string configPath, out (string Owner, string Repo) repo)
    {
        repo = default;
        try
        {
            var inOrigin = false;
            foreach (var raw in File.ReadLines(configPath))
            {
                var line = raw.Trim();
                if (line.StartsWith('['))
                {
                    inOrigin = line.Replace(" ", "").Equals("[remote\"origin\"]", StringComparison.OrdinalIgnoreCase);
                    continue;
                }
                if (!inOrigin) continue;
                if (!line.StartsWith("url", StringComparison.OrdinalIgnoreCase)) continue;
                var eq = line.IndexOf('=');
                if (eq < 0) continue;
                return TryParseGitHub(line[(eq + 1)..].Trim(), out repo);
            }
        }
        catch
        {
            // битый config — не повод падать
        }
        return false;
    }

    static bool TryParseGitHub(string? url, out (string Owner, string Repo) repo)
    {
        repo = default;
        if (string.IsNullOrWhiteSpace(url)) return false;

        var match = Regex.Match(
            url.Trim(),
            @"github\.com[:/]+(?<owner>[A-Za-z0-9_.-]+)/(?<repo>[A-Za-z0-9_.-]+?)(?:\.git)?/*$",
            RegexOptions.IgnoreCase);

        if (!match.Success) return false;
        repo = (match.Groups["owner"].Value, match.Groups["repo"].Value);
        return true;
    }

    /* ----------------------------- apply ------------------------------ */

    static string BuildApplyScript(string staged, string appDir, int pid)
    {
        const string template = """
            $ErrorActionPreference = 'SilentlyContinue'
            try { Wait-Process -Id __PID__ -Timeout 180 } catch {}
            Start-Sleep -Milliseconds 800
            robocopy "__STAGED__" "__APP__" /E /R:2 /W:1 /NFL /NDL /NJH /NJS /NP | Out-Null
            Remove-Item -LiteralPath "__UPDATES__" -Recurse -Force
            Remove-Item -LiteralPath $PSCommandPath -Force
            """;

        return template
            .Replace("__PID__", pid.ToString())
            .Replace("__STAGED__", staged)
            .Replace("__APP__", appDir)
            .Replace("__UPDATES__", Path.Combine(AppDataRoot, "updates"));
    }

    static async Task<byte[]> Sha256Async(string path)
    {
        await using var stream = File.OpenRead(path);
        return await SHA256.HashDataAsync(stream);
    }

    static string? Str(JsonElement root, string name)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(name, out var v)) return null;
        return v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    }

    static bool Bool(JsonElement root, string name)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(name, out var v)) return false;
        return v.ValueKind == JsonValueKind.True;
    }

    /// <summary>Сравнение версий вида "1.2.3"; хвосты после дефиса игнорируются.</summary>
    public static int CompareVersions(string? a, string? b)
    {
        var left = Parse(a);
        var right = Parse(b);
        for (var i = 0; i < 3; i++)
        {
            var diff = left[i].CompareTo(right[i]);
            if (diff != 0) return diff;
        }
        return 0;

        static int[] Parse(string? value)
        {
            var parts = new int[3];
            if (string.IsNullOrWhiteSpace(value)) return parts;
            var chunks = value.Split('-')[0].Split('.');
            for (var i = 0; i < 3 && i < chunks.Length; i++)
            {
                int.TryParse(chunks[i], out parts[i]);
            }
            return parts;
        }
    }

    /* ------------------------------ log ------------------------------- */

    void Log(string message)
    {
        try
        {
            Directory.CreateDirectory(AppDataRoot);
            lock (LogLock)
            {
                var info = new FileInfo(LogPath);
                if (info.Exists && info.Length > 256 * 1024) File.Delete(LogPath);
                File.AppendAllText(LogPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // логи не должны ломать обновление
        }
    }
}
