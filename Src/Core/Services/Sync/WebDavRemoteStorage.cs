using System;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Serilog;
using Writersword.Core.Exceptions;
using Writersword.Core.Interfaces.Services.Storage;
using Writersword.Core.Models.Sync;

namespace Writersword.Core.Services.Sync
{
    /// <summary>
    /// Хранилище поверх WebDAV.
    ///
    /// WebDAV выбран потому, что это обычный HTTP с четырьмя глаголами, и его
    /// одинаково отдают и чужие облака, и nginx с dav-модулем на собственном
    /// сервере. То есть переезд «с арендованного на своё» не потребует
    /// переписывать клиент — только сменить адрес в настройках.
    ///
    /// Условные заголовки If-Match и If-None-Match здесь не украшение:
    /// они переносят разрешение гонок на сервер, где оно атомарно. Клиент
    /// не может проверить и записать одним действием, сервер — может.
    /// </summary>
    public sealed class WebDavRemoteStorage : IRemoteStorage
    {
        private static readonly XNamespace Dav = "DAV:";

        private static readonly HttpMethod PropFind = new("PROPFIND");
        private static readonly HttpMethod MkCol = new("MKCOL");

        private const string PropFindBody =
            """
            <?xml version="1.0" encoding="utf-8"?>
            <D:propfind xmlns:D="DAV:">
              <D:prop>
                <D:getetag/>
                <D:getcontentlength/>
                <D:getlastmodified/>
                <D:resourcetype/>
              </D:prop>
            </D:propfind>
            """;

        private readonly HttpClient _http;
        private readonly Uri _rootUri;
        private readonly ILogger _log;
        private readonly bool _ownsClient;
        private bool _disposed;

        /// <summary>
        /// Пересоздание HttpClient на каждый запрос исчерпывает сокеты,
        /// поэтому клиент живёт вместе с хранилищем. Обработчик задаётся
        /// снаружи только в тестах.
        /// </summary>
        public WebDavRemoteStorage(SyncSettings settings, ILogger logger, HttpMessageHandler? handler = null)
        {
            ArgumentNullException.ThrowIfNull(settings);

            if (string.IsNullOrWhiteSpace(settings.ServerUrl))
                throw new ArgumentException("Server URL is not configured.", nameof(settings));

            _log = logger?.ForContext<WebDavRemoteStorage>()
                   ?? throw new ArgumentNullException(nameof(logger));

            _rootUri = BuildRootUri(settings);

            _ownsClient = handler is null;

            // SocketsHttpHandler задаётся явно, а не берётся по умолчанию.
            //
            // На Android платформой по умолчанию выступает нативный обработчик
            // поверх Java, а тот принимает только стандартный набор глаголов
            // HTTP и отвергает PROPFIND и MKCOL — то есть ровно то, чем WebDAV
            // отличается от простого HTTP. Управляемый обработчик .NET шлёт
            // любой метод и ведёт себя одинаково на всех платформах.
            _http = handler is null
                ? new HttpClient(CreateHandler(), disposeHandler: true)
                : new HttpClient(handler, disposeHandler: false);
            _http.Timeout = settings.NetworkTimeout > TimeSpan.Zero
                ? settings.NetworkTimeout
                : TimeSpan.FromSeconds(30);

            if (!string.IsNullOrEmpty(settings.Login))
            {
                var raw = $"{settings.Login}:{settings.Password}";
                _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
                    "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(raw)));
            }
        }

        private static SocketsHttpHandler CreateHandler() => new()
        {
            // Соединения переоткрываются раз в две минуты: мобильная сеть
            // меняет адрес при переходе между вышками и Wi-Fi, и висящее
            // соединение к прежнему маршруту молча отваливается по таймауту
            // вместо честной ошибки.
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            AutomaticDecompression = DecompressionMethods.All
        };

        /// <summary>
        /// Корневой адрес папки хранилища. Слеш на конце обязателен: без него
        /// Uri при разрешении относительного пути отбрасывает последний сегмент,
        /// и файлы уезжают на уровень выше папки.
        /// </summary>
        private static Uri BuildRootUri(SyncSettings settings)
        {
            var baseUrl = settings.ServerUrl.TrimEnd('/');
            var folder = (settings.RemoteFolder ?? string.Empty).Trim('/');

            var full = string.IsNullOrEmpty(folder) ? baseUrl + "/" : $"{baseUrl}/{folder}/";

            if (!Uri.TryCreate(full, UriKind.Absolute, out var uri))
                throw new ArgumentException($"Server URL is not a valid absolute URI: {full}", nameof(settings));

            if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
                throw new ArgumentException($"Unsupported URI scheme: {uri.Scheme}", nameof(settings));

            return uri;
        }

        private Uri KeyToUri(string key)
        {
            if (string.IsNullOrWhiteSpace(key))
                throw new ArgumentException("Storage key must not be empty.", nameof(key));

            // Ключ приходит из HMAC и состоит из hex-символов, но экранирование
            // всё равно уместно: хранилище может получить ключ и из другого места.
            return new Uri(_rootUri, Uri.EscapeDataString(key));
        }

        public async Task<bool> EnsureAvailableAsync(CancellationToken ct = default)
        {
            ThrowIfDisposed();

            try
            {
                using var probe = new HttpRequestMessage(PropFind, _rootUri);
                probe.Headers.Add("Depth", "0");
                probe.Content = new StringContent(PropFindBody, Encoding.UTF8, "application/xml");

                using var response = await _http.SendAsync(probe, ct).ConfigureAwait(false);

                if (response.StatusCode == HttpStatusCode.Unauthorized)
                    throw new RemoteAuthenticationException("Server rejected the credentials.");

                if (response.IsSuccessStatusCode || (int)response.StatusCode == 207)
                    return true;

                if (response.StatusCode == HttpStatusCode.NotFound)
                    return await CreateFolderAsync(ct).ConfigureAwait(false);

                throw new RemoteStorageException(
                    $"Storage probe failed with status {(int)response.StatusCode}.",
                    (int)response.StatusCode);
            }
            catch (HttpRequestException ex)
            {
                // Отсутствие сети — штатное состояние, а не сбой: приложение
                // обязано продолжать работу локально. Наверх идёт false.
                _log.Debug(ex, "WebDAV storage unreachable");
                return false;
            }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested)
            {
                _log.Debug("WebDAV storage probe timed out");
                return false;
            }
        }

        private async Task<bool> CreateFolderAsync(CancellationToken ct)
        {
            using var request = new HttpRequestMessage(MkCol, _rootUri);
            using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);

            if (response.IsSuccessStatusCode)
            {
                _log.Information("Created remote folder {Uri}", _rootUri);
                return true;
            }

            // 405 означает, что папка уже существует — для наших целей это успех.
            if (response.StatusCode == HttpStatusCode.MethodNotAllowed)
                return true;

            if (response.StatusCode == HttpStatusCode.Conflict)
                throw new RemoteStorageException(
                    "Parent folder does not exist on the server. Check the server URL.", 409);

            throw new RemoteStorageException(
                $"Failed to create remote folder, status {(int)response.StatusCode}.",
                (int)response.StatusCode);
        }

        public async Task<RemoteEntry?> GetInfoAsync(string key, CancellationToken ct = default)
        {
            ThrowIfDisposed();

            using var request = new HttpRequestMessage(PropFind, KeyToUri(key));
            request.Headers.Add("Depth", "0");
            request.Content = new StringContent(PropFindBody, Encoding.UTF8, "application/xml");

            using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.NotFound)
                return null;

            if (response.StatusCode == HttpStatusCode.Unauthorized)
                throw new RemoteAuthenticationException("Server rejected the credentials.");

            if ((int)response.StatusCode != 207 && !response.IsSuccessStatusCode)
                throw new RemoteStorageException(
                    $"PROPFIND failed with status {(int)response.StatusCode}.",
                    (int)response.StatusCode);

            var xml = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return ParsePropFind(xml);
        }

        /// <summary>
        /// Разбор ответа 207 Multi-Status.
        ///
        /// Свойства разложены по нескольким propstat — успешные и не найденные
        /// сервером идут отдельными блоками со своими статусами. Берётся первый
        /// response, потому что запрос был с Depth: 0 и адресован одному файлу.
        /// </summary>
        private RemoteEntry? ParsePropFind(string xml)
        {
            XDocument doc;
            try
            {
                doc = XDocument.Parse(xml);
            }
            catch (System.Xml.XmlException ex)
            {
                throw new RemoteStorageException("Server returned malformed WebDAV response.", ex);
            }

            var responseElement = doc.Root?.Element(Dav + "response");
            if (responseElement is null)
                return null;

            string? etag = null;
            long length = 0;
            var modified = DateTimeOffset.MinValue;

            foreach (var prop in responseElement.Elements(Dav + "propstat").Elements(Dav + "prop"))
            {
                var etagValue = prop.Element(Dav + "getetag")?.Value;
                if (!string.IsNullOrWhiteSpace(etagValue))
                    etag = NormalizeETag(etagValue);

                var lengthValue = prop.Element(Dav + "getcontentlength")?.Value;
                if (!string.IsNullOrWhiteSpace(lengthValue)
                    && long.TryParse(lengthValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
                    length = parsed;

                var modifiedValue = prop.Element(Dav + "getlastmodified")?.Value;
                if (!string.IsNullOrWhiteSpace(modifiedValue)
                    && DateTimeOffset.TryParse(modifiedValue, CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsedDate))
                    modified = parsedDate;
            }

            // Сервер без поддержки ETag синхронизировать безопасно нельзя:
            // всё разрешение конфликтов построено на версии, которую он выдаёт.
            if (string.IsNullOrEmpty(etag))
                throw new RemoteStorageException(
                    "Server did not return an ETag. This storage cannot be used for synchronization.");

            return new RemoteEntry
            {
                ETag = etag,
                Length = length,
                LastModified = modified
            };
        }

        /// <summary>
        /// Приведение ETag к сравнимому виду.
        ///
        /// Серверы отдают его по-разному: в кавычках, без них, с префиксом W/
        /// для слабых валидаторов. Сравнивать сырые строки нельзя — один и тот же
        /// файл после смены версии сервера начнёт выглядеть изменившимся.
        /// </summary>
        private static string NormalizeETag(string raw)
        {
            var value = raw.Trim();

            if (value.StartsWith("W/", StringComparison.OrdinalIgnoreCase))
                value = value[2..];

            return value.Trim('"');
        }

        public async Task<RemoteContent?> DownloadAsync(
            string key, string? ifNoneMatch = null, CancellationToken ct = default)
        {
            ThrowIfDisposed();

            using var request = new HttpRequestMessage(HttpMethod.Get, KeyToUri(key));

            if (!string.IsNullOrEmpty(ifNoneMatch))
                request.Headers.TryAddWithoutValidation("If-None-Match", Quote(ifNoneMatch));

            using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.NotModified)
                return null;

            if (response.StatusCode == HttpStatusCode.NotFound)
                return null;

            if (response.StatusCode == HttpStatusCode.Unauthorized)
                throw new RemoteAuthenticationException("Server rejected the credentials.");

            if (!response.IsSuccessStatusCode)
                throw new RemoteStorageException(
                    $"Download failed with status {(int)response.StatusCode}.",
                    (int)response.StatusCode);

            var data = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);

            var etag = response.Headers.ETag?.Tag;

            // Часть серверов не кладёт ETag в ответ GET, зато отдаёт его в PROPFIND.
            // Запрашиваем отдельно, иначе синхронизировать этот файл дальше нечем.
            if (string.IsNullOrEmpty(etag))
            {
                var info = await GetInfoAsync(key, ct).ConfigureAwait(false);
                if (info is null)
                    throw new RemoteStorageException("File disappeared from the server during download.");

                return new RemoteContent { Data = data, ETag = info.ETag };
            }

            return new RemoteContent { Data = data, ETag = NormalizeETag(etag) };
        }

        public async Task<string?> UploadAsync(
            string key,
            byte[] data,
            string? ifMatch = null,
            string? ifNoneMatch = null,
            CancellationToken ct = default)
        {
            ThrowIfDisposed();
            ArgumentNullException.ThrowIfNull(data);

            using var request = new HttpRequestMessage(HttpMethod.Put, KeyToUri(key));
            request.Content = new ByteArrayContent(data);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

            if (!string.IsNullOrEmpty(ifMatch))
                request.Headers.TryAddWithoutValidation("If-Match", Quote(ifMatch));

            if (!string.IsNullOrEmpty(ifNoneMatch))
                request.Headers.TryAddWithoutValidation(
                    "If-None-Match", ifNoneMatch == "*" ? "*" : Quote(ifNoneMatch));

            using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);

            // Условие не выполнилось: на сервере версия не та, которую мы ожидали.
            // Это не ошибка, а штатный исход — сверху решают, что делать дальше.
            if (response.StatusCode == HttpStatusCode.PreconditionFailed)
            {
                _log.Information("Upload rejected by precondition for key {Key}", key);
                return null;
            }

            if (response.StatusCode == HttpStatusCode.Unauthorized)
                throw new RemoteAuthenticationException("Server rejected the credentials.");

            if (response.StatusCode == HttpStatusCode.InsufficientStorage)
                throw new RemoteStorageException("Remote storage is out of space.", 507);

            if (!response.IsSuccessStatusCode)
                throw new RemoteStorageException(
                    $"Upload failed with status {(int)response.StatusCode}.",
                    (int)response.StatusCode);

            var etag = response.Headers.ETag?.Tag;
            if (!string.IsNullOrEmpty(etag))
                return NormalizeETag(etag);

            // Ответ без ETag — берём актуальный отдельным запросом, иначе
            // следующая запись пойдёт без корректного If-Match.
            var info = await GetInfoAsync(key, ct).ConfigureAwait(false);
            return info?.ETag;
        }

        public async Task DeleteAsync(string key, CancellationToken ct = default)
        {
            ThrowIfDisposed();

            using var request = new HttpRequestMessage(HttpMethod.Delete, KeyToUri(key));
            using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);

            if (response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.NotFound)
                return;

            if (response.StatusCode == HttpStatusCode.Unauthorized)
                throw new RemoteAuthenticationException("Server rejected the credentials.");

            throw new RemoteStorageException(
                $"Delete failed with status {(int)response.StatusCode}.",
                (int)response.StatusCode);
        }

        private static string Quote(string etag)
            => etag.StartsWith('"') ? etag : $"\"{etag}\"";

        private void ThrowIfDisposed()
            => ObjectDisposedException.ThrowIf(_disposed, this);

        public void Dispose()
        {
            if (_disposed) return;

            if (_ownsClient)
                _http.Dispose();

            _disposed = true;
        }
    }
}
