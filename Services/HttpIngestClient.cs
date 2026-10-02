using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using WorkerService1.Models;

namespace WorkerService1.Services
{
    /// <summary>Payload'ı şifreleyip HTTPS üzerinden ingest API'sine gönderir.</summary>
    public sealed class HttpIngestClient : IIngestClient, IDisposable
    {
        private readonly ILogger<HttpIngestClient> _logger;
        private readonly IEncryptionService _encryption;
        private readonly HttpClient _http;
        private readonly Uri _apiUri;

        public HttpIngestClient(ILogger<HttpIngestClient> logger, IEncryptionService encryption, DataSyncOptions options)
        {
            _logger = logger;
            _encryption = encryption;

            if (!Uri.TryCreate(options.ApiUrl, UriKind.Absolute, out var uri))
                throw new ArgumentException("DataSync:ApiUrl geçerli bir URL değil.");
            // Token ve veri düz HTTP ile gönderilmez (localhost geliştirme hariç).
            if (uri.Scheme != Uri.UriSchemeHttps && !uri.IsLoopback)
                throw new ArgumentException("DataSync:ApiUrl HTTPS olmalıdır.");
            _apiUri = uri;

            _http = new HttpClient(new SocketsHttpHandler
            {
                AllowAutoRedirect = false, // yönlendirmeyle token sızmasını engelle
                PooledConnectionLifetime = TimeSpan.FromMinutes(10)
            })
            {
                Timeout = TimeSpan.FromSeconds(30)
            };
            _http.DefaultRequestHeaders.Add("X-Api-Token", options.ApiToken);
        }

        public async Task<bool> SendAsync<T>(string type, List<T> data, CancellationToken ct = default)
        {
            try
            {
                var inner = JsonSerializer.Serialize(new EncryptedDataWrapper { Type = type, Data = data });
                var body = JsonSerializer.Serialize(new IngestRequest { Payload = _encryption.Encrypt(inner) });
                using var content = new StringContent(body, Encoding.UTF8, "application/json");

                using var response = await _http.PostAsync(_apiUri, content, ct);
                if (response.IsSuccessStatusCode)
                {
                    _logger.LogInformation("Synced {Type} ({Count} kayıt)", type, data.Count);
                    return true;
                }

                // Yanıt gövdesi loglanmaz (hassas bilgi sızıntısı riski).
                _logger.LogWarning("API sync failed for {Type}: HTTP {Status}", type, (int)response.StatusCode);
                return false;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Exception during API sync for {Type}", type);
                return false;
            }
        }

        public void Dispose() => _http.Dispose();
    }
}
