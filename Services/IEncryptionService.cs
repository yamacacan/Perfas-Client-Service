namespace WorkerService1.Services
{
    /// <summary>Şifreleme soyutlaması (DIP) - taşıma katmanı somut algoritmaya bağımlı olmaz.</summary>
    public interface IEncryptionService
    {
        string Encrypt(string plainText);
    }
}
