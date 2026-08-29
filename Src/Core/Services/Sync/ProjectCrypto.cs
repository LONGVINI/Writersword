using System;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Writersword.Core.Services.Sync
{
    /// <summary>
    /// Шифрование контейнера проекта и сокрытие имён файлов.
    ///
    /// На сервер не попадает ничего осмысленного: содержимое зашифровано
    /// AES-256-GCM, имя файла — HMAC от имени проекта. Владелец хранилища
    /// видит набор безымянных блобов и не может сказать ни сколько у автора
    /// книг, ни как они называются.
    ///
    /// GCM выбран вместо CBC потому, что даёт не только шифрование, но и
    /// проверку целостности: подменённый или побитый при передаче файл не
    /// расшифруется вовсе, вместо того чтобы тихо развалиться при разборе ZIP.
    /// </summary>
    public sealed class ProjectCrypto : IDisposable
    {
        // Формат контейнера данных:
        //   "WSE1" (4) | nonce (12) | tag (16) | шифротекст (N)
        // Соль здесь не хранится: она одна на всё хранилище и лежит в index.dat.
        private const uint DataMagic = 0x31455357;  // "WSE1" в little-endian
        private const uint VaultMagic = 0x31565357; // "WSV1"

        private const int SaltSize = 16;
        private const int NonceSize = 12;   // рекомендованный размер для GCM
        private const int TagSize = 16;
        private const int KeySize = 32;     // AES-256
        private const int VerifierSize = 32;

        /// <summary>Размер файла-описателя хранилища.</summary>
        public const int VaultFileSize = 4 + SaltSize + VerifierSize;

        // Итераций PBKDF2 по рекомендации OWASP для PBKDF2-SHA256.
        // Занижать нельзя: это единственное, что стоит между слабым паролем
        // и перебором по украденному контейнеру.
        private const int Iterations = 210_000;

        private readonly byte[] _dataKey;
        private readonly byte[] _nameKey;
        private readonly byte[] _salt;
        private bool _disposed;

        private ProjectCrypto(byte[] dataKey, byte[] nameKey, byte[] salt)
        {
            _dataKey = dataKey;
            _nameKey = nameKey;
            _salt = salt;
        }

        /// <summary>Соль хранилища — нужна для записи index.dat.</summary>
        public ReadOnlySpan<byte> Salt => _salt;

        /// <summary>
        /// Создать набор ключей для нового хранилища.
        /// Соль генерируется случайно и потом живёт в index.dat на сервере,
        /// чтобы второе устройство подключалось одним мастер-паролем.
        /// </summary>
        public static ProjectCrypto CreateNew(string masterPassword)
        {
            var salt = RandomNumberGenerator.GetBytes(SaltSize);
            return FromSalt(masterPassword, salt);
        }

        /// <summary>Восстановить ключи по паролю и соли, прочитанной из index.dat.</summary>
        public static ProjectCrypto FromSalt(string masterPassword, byte[] salt)
        {
            if (string.IsNullOrEmpty(masterPassword))
                throw new ArgumentException("Master password must not be empty.", nameof(masterPassword));
            if (salt is null || salt.Length != SaltSize)
                throw new ArgumentException($"Salt must be exactly {SaltSize} bytes.", nameof(salt));

            var root = Rfc2898DeriveBytes.Pbkdf2(
                Encoding.UTF8.GetBytes(masterPassword),
                salt,
                Iterations,
                HashAlgorithmName.SHA256,
                KeySize);

            try
            {
                // Ключи для данных и для имён разводятся через HKDF, чтобы один
                // не выводился из другого. Пароль дорогой (PBKDF2), поэтому
                // считается один раз, а расширение уже дешёвое.
                var dataKey = HKDF.Expand(
                    HashAlgorithmName.SHA256, root, KeySize,
                    Encoding.UTF8.GetBytes("writersword.container.data"));
                var nameKey = HKDF.Expand(
                    HashAlgorithmName.SHA256, root, KeySize,
                    Encoding.UTF8.GetBytes("writersword.container.name"));

                return new ProjectCrypto(dataKey, nameKey, (byte[])salt.Clone());
            }
            finally
            {
                CryptographicOperations.ZeroMemory(root);
            }
        }

        /// <summary>
        /// Содержимое index.dat: магия, соль и верификатор пароля.
        ///
        /// Верификатор позволяет отличить неверный пароль от повреждённого
        /// файла — иначе пользователь получал бы одну и ту же невнятную ошибку
        /// расшифровки в обоих случаях.
        /// </summary>
        public byte[] BuildVaultFile()
        {
            ThrowIfDisposed();

            var result = new byte[VaultFileSize];
            BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(0, 4), VaultMagic);
            _salt.CopyTo(result.AsSpan(4, SaltSize));
            ComputeVerifier().CopyTo(result.AsSpan(4 + SaltSize, VerifierSize));
            return result;
        }

        /// <summary>Разобрать index.dat и достать соль. Пароль здесь ещё не нужен.</summary>
        public static byte[] ReadVaultSalt(byte[] vaultFile)
        {
            if (vaultFile is null || vaultFile.Length < VaultFileSize)
                throw new CryptographicException("Vault descriptor is corrupted or truncated.");

            var magic = BinaryPrimitives.ReadUInt32LittleEndian(vaultFile.AsSpan(0, 4));
            if (magic != VaultMagic)
                throw new CryptographicException("Vault descriptor has an unknown format.");

            return vaultFile.AsSpan(4, SaltSize).ToArray();
        }

        /// <summary>Проверить, что мастер-пароль подходит к этому хранилищу.</summary>
        public bool VerifyAgainstVault(byte[] vaultFile)
        {
            ThrowIfDisposed();

            if (vaultFile is null || vaultFile.Length < VaultFileSize)
                return false;

            var stored = vaultFile.AsSpan(4 + SaltSize, VerifierSize);

            // Сравнение с постоянным временем: обычное сравнение массивов
            // выходит из цикла на первом несовпавшем байте, и по времени
            // ответа верификатор можно подобрать побайтово.
            return CryptographicOperations.FixedTimeEquals(stored, ComputeVerifier());
        }

        /// <summary>
        /// Имя файла на сервере. Детерминированное — одно и то же имя проекта
        /// на разных устройствах даёт один и тот же ключ, — но необратимое:
        /// по имени на сервере исходное не восстановить.
        /// </summary>
        public string BuildRemoteKey(string projectName)
        {
            ThrowIfDisposed();

            if (string.IsNullOrWhiteSpace(projectName))
                throw new ArgumentException("Project name must not be empty.", nameof(projectName));

            // Нормализация нужна, чтобы «Книга» и «книга » не разъехались
            // в два разных файла на сервере при переносе между системами.
            var normalized = projectName.Trim().ToLowerInvariant();

            var mac = HMACSHA256.HashData(_nameKey, Encoding.UTF8.GetBytes(normalized));
            return Convert.ToHexString(mac, 0, 16).ToLowerInvariant() + ".dat";
        }

        /// <summary>Зашифровать содержимое проекта для отправки на сервер.</summary>
        public byte[] Encrypt(byte[] plain)
        {
            ThrowIfDisposed();
            ArgumentNullException.ThrowIfNull(plain);

            var nonce = RandomNumberGenerator.GetBytes(NonceSize);
            var result = new byte[4 + NonceSize + TagSize + plain.Length];

            BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(0, 4), DataMagic);
            nonce.CopyTo(result.AsSpan(4, NonceSize));

            using var gcm = new AesGcm(_dataKey, TagSize);
            gcm.Encrypt(
                nonce,
                plain,
                result.AsSpan(4 + NonceSize + TagSize),
                result.AsSpan(4 + NonceSize, TagSize),
                // Магия идёт в дополнительные данные: подмена заголовка
                // сделает расшифровку невозможной, а не просто странной.
                result.AsSpan(0, 4));

            return result;
        }

        /// <summary>
        /// Расшифровать скачанное с сервера.
        ///
        /// Бросает CryptographicException при неверном пароле или порче данных —
        /// различить эти случаи невозможно и не нужно: в обоих файл непригоден.
        /// </summary>
        public byte[] Decrypt(byte[] container)
        {
            ThrowIfDisposed();

            if (container is null || container.Length < 4 + NonceSize + TagSize)
                throw new CryptographicException("Container is corrupted: payload is too short.");

            var magic = BinaryPrimitives.ReadUInt32LittleEndian(container.AsSpan(0, 4));
            if (magic != DataMagic)
                throw new CryptographicException("Container has an unknown format.");

            var plainLength = container.Length - 4 - NonceSize - TagSize;
            var plain = new byte[plainLength];

            using var gcm = new AesGcm(_dataKey, TagSize);
            gcm.Decrypt(
                container.AsSpan(4, NonceSize),
                container.AsSpan(4 + NonceSize + TagSize),
                container.AsSpan(4 + NonceSize, TagSize),
                plain,
                container.AsSpan(0, 4));

            return plain;
        }

        private byte[] ComputeVerifier()
            => HMACSHA256.HashData(_dataKey, Encoding.UTF8.GetBytes("writersword.vault.verify"));

        private void ThrowIfDisposed()
            => ObjectDisposedException.ThrowIf(_disposed, this);

        public void Dispose()
        {
            if (_disposed) return;

            // Ключи затираются явно: сборщик мусора не даёт гарантий, когда
            // именно освободит массив, а до тех пор он лежит в куче процесса.
            CryptographicOperations.ZeroMemory(_dataKey);
            CryptographicOperations.ZeroMemory(_nameKey);
            _disposed = true;
        }
    }
}
