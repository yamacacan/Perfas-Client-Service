using System;
using System.Security.Cryptography;
using System.Text;

namespace WorkerService1.Services
{
    /// <summary>
    /// AES-256-CBC şifreleme. Çıktı: Base64(IV[16] + CipherText) - sunucu tarafı formatıyla uyumludur.
    /// </summary>
    public class AesEncryptionService : IEncryptionService
    {
        private readonly byte[] _key;

        public AesEncryptionService(string key)
        {
            // Sessiz \0 dolgusu yerine zayıf/hatalı anahtarı baştan reddet.
            var keyBytes = Encoding.UTF8.GetBytes(key ?? string.Empty);
            if (keyBytes.Length != 32)
                throw new ArgumentException("AES anahtarı UTF-8 olarak tam 32 bayt olmalıdır.", nameof(key));
            _key = keyBytes;
        }

        public string Encrypt(string plainText)
        {
            if (string.IsNullOrEmpty(plainText))
                return string.Empty;

            using var aes = Aes.Create();
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.PKCS7;
            aes.Key = _key;
            aes.GenerateIV(); // her mesaj için rastgele IV

            using var encryptor = aes.CreateEncryptor();
            var plainBytes = Encoding.UTF8.GetBytes(plainText);
            var cipher = encryptor.TransformFinalBlock(plainBytes, 0, plainBytes.Length);

            var combined = new byte[aes.IV.Length + cipher.Length];
            Buffer.BlockCopy(aes.IV, 0, combined, 0, aes.IV.Length);
            Buffer.BlockCopy(cipher, 0, combined, aes.IV.Length, cipher.Length);
            return Convert.ToBase64String(combined);
        }
    }
}
