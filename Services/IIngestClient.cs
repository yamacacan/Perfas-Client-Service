using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace WorkerService1.Services
{
    /// <summary>Verinin uzak API'ye iletilmesi soyutlaması.</summary>
    public interface IIngestClient
    {
        Task<bool> SendAsync<T>(string type, List<T> data, CancellationToken ct = default);
    }

    /// <summary>Senkronizasyon yapılandırılmadığında kullanılan boş istemci (Null Object).</summary>
    public sealed class DisabledIngestClient : IIngestClient
    {
        public Task<bool> SendAsync<T>(string type, List<T> data, CancellationToken ct = default) => Task.FromResult(false);
    }
}
