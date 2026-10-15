using System;
using System.Security.Cryptography;
using System.Text;
using Writersword.Modules.TextEditor.Models.Document;

namespace Writersword.Modules.TextEditor.Services
{
    /// <summary>
    /// Пароль защиты записи исправлений: отпечаток при установке и проверка при
    /// снятии.
    ///
    /// Отпечаток считается так же, как его считает Word для w:documentProtection
    /// (ECMA-376, часть 1, 17.15.1.29): пароль сначала сворачивается в старый
    /// 32-битный ключ Word, ключ записывается шестнадцатеричной строкой в UTF-16 и
    /// хешируется с солью, затем хеш прогоняется ещё <see cref="DefaultSpinCount"/>
    /// раз с номером прохода. Поэтому документ, защищённый здесь, Word откроет
    /// защищённым и снимет защиту тем же паролем, и наоборот.
    /// </summary>
    public static class RevisionLock
    {
        /// <summary>Повторов хеша — как у Word 2013 и новее.</summary>
        public const int DefaultSpinCount = 100_000;

        /// <summary>Хеш-функция новых защит — как у Word 2013 и новее.</summary>
        public const string DefaultAlgorithm = "SHA-512";

        private const int SaltLength = 16;

        // Старый ключ Word: из пароля берутся первые 15 знаков.
        private const int MaxPasswordLength = 15;

        private static readonly int[] InitialCodeArray =
        {
            0xE1F0, 0x1D0F, 0xCC9C, 0x84C0, 0x110C, 0x0E10, 0xF1CE,
            0x313E, 0x1872, 0xE139, 0xD40F, 0x84F9, 0x280C, 0xA96A,
            0x4EC3
        };

        private static readonly int[,] EncryptionMatrix =
        {
            { 0xAEFC, 0x4DD9, 0x9BB2, 0x2745, 0x4E8A, 0x9D14, 0x2A09 },
            { 0x7B61, 0xF6C2, 0xFDA5, 0xEB6B, 0xC6F7, 0x9DCF, 0x2BBF },
            { 0x4563, 0x8AC6, 0x05AD, 0x0B5A, 0x16B4, 0x2D68, 0x5AD0 },
            { 0x0375, 0x06EA, 0x0DD4, 0x1BA8, 0x3750, 0x6EA0, 0xDD40 },
            { 0xD849, 0xA0B3, 0x5147, 0xA28E, 0x553D, 0xAA7A, 0x44D5 },
            { 0x6F45, 0xDE8A, 0xAD35, 0x4A4B, 0x9496, 0x390D, 0x721A },
            { 0xEB23, 0xC667, 0x9CEF, 0x29FF, 0x53FE, 0xA7FC, 0x5FD9 },
            { 0x47D3, 0x8FA6, 0x0F6D, 0x1EDA, 0x3DB4, 0x7B68, 0xF6D0 },
            { 0xB861, 0x60E3, 0xC1C6, 0x93AD, 0x377B, 0x6EF6, 0xDDEC },
            { 0x45A0, 0x8B40, 0x06A1, 0x0D42, 0x1A84, 0x3508, 0x6A10 },
            { 0xAA51, 0x4483, 0x8906, 0x022D, 0x045A, 0x08B4, 0x1168 },
            { 0x76B4, 0xED68, 0xCAF1, 0x85C3, 0x1BA7, 0x374E, 0x6E9C },
            { 0x3730, 0x6E60, 0xDCC0, 0xA9A1, 0x4363, 0x86C6, 0x1DAD },
            { 0x3331, 0x6662, 0xCCC4, 0x89A9, 0x0373, 0x06E6, 0x0DCC },
            { 0x1021, 0x2042, 0x4084, 0x8108, 0x1231, 0x2462, 0x48C4 }
        };

        /// <summary>
        /// Новая защита. Пустой пароль — защита без пароля: её снимает любой, как у
        /// Word.
        /// </summary>
        public static RevisionProtection Create(string? password)
        {
            if (string.IsNullOrEmpty(password))
                return new RevisionProtection();

            byte[] salt = RandomNumberGenerator.GetBytes(SaltLength);
            byte[] hash = HashWordLegacy(password, salt, DefaultAlgorithm, DefaultSpinCount);

            return new RevisionProtection
            {
                Format = RevisionProtectionFormat.WordLegacyKey,
                AlgorithmName = DefaultAlgorithm,
                HashValue = Convert.ToBase64String(hash),
                SaltValue = Convert.ToBase64String(salt),
                SpinCount = DefaultSpinCount
            };
        }

        /// <summary>
        /// Пароль снимает защиту. Защита без пароля снимается любым, в том числе
        /// пустым. Отпечаток в неизвестном виде пароля не принимает.
        /// </summary>
        public static bool Verify(RevisionProtection protection, string? password)
        {
            if (!protection.HasPassword) return true;
            if (string.IsNullOrEmpty(password)) return false;

            byte[] expected;
            byte[] salt;
            try
            {
                expected = Convert.FromBase64String(protection.HashValue!);
                salt = string.IsNullOrEmpty(protection.SaltValue)
                    ? Array.Empty<byte>()
                    : Convert.FromBase64String(protection.SaltValue);
            }
            catch (FormatException)
            {
                return false;
            }

            string algorithm = protection.AlgorithmName ?? "SHA-1";
            if (CreateAlgorithm(algorithm) is null) return false;

            if (Matches(HashWordLegacy(password, salt, algorithm, protection.SpinCount), expected))
                return true;

            // Атрибуты строгой схемы пишут по-разному: одни программы — через старый
            // ключ Word, другие — прямо от пароля, с номером прохода перед хешем.
            return protection.Format == RevisionProtectionFormat.AlgorithmName
                   && Matches(HashDirect(password, salt, algorithm, protection.SpinCount), expected);
        }

        /// <summary>Хеш-функция по имени из файла; null — неизвестна.</summary>
        public static HashAlgorithm? CreateAlgorithm(string? name)
        {
            switch (NormalizeAlgorithm(name))
            {
                case "SHA-1": return SHA1.Create();
                case "SHA-256": return SHA256.Create();
                case "SHA-384": return SHA384.Create();
                case "SHA-512": return SHA512.Create();
                default: return null;
            }
        }

        /// <summary>Имя хеш-функции в виде, как его пишет Word: «SHA-512».</summary>
        public static string? NormalizeAlgorithm(string? name)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;

            string compact = name.Replace("-", string.Empty).Replace("_", string.Empty).Trim().ToUpperInvariant();
            return compact switch
            {
                "SHA1" => "SHA-1",
                "SHA256" => "SHA-256",
                "SHA384" => "SHA-384",
                "SHA512" => "SHA-512",
                _ => null
            };
        }

        /// <summary>Номер хеш-функции в w:cryptAlgorithmSid.</summary>
        public static int AlgorithmSid(string? name) => NormalizeAlgorithm(name) switch
        {
            "SHA-1" => 4,
            "SHA-256" => 12,
            "SHA-384" => 13,
            "SHA-512" => 14,
            _ => 4
        };

        /// <summary>Хеш-функция по номеру w:cryptAlgorithmSid; null — не поддерживается.</summary>
        public static string? AlgorithmFromSid(int sid) => sid switch
        {
            4 => "SHA-1",
            12 => "SHA-256",
            13 => "SHA-384",
            14 => "SHA-512",
            _ => null
        };

        // ── Отпечаток ────────────────────────────────────────────────────────

        /// <summary>
        /// Отпечаток, как его считает Word: старый ключ шестнадцатеричной строкой в
        /// UTF-16, хеш соли с ключом, затем повторы с номером прохода после хеша.
        /// </summary>
        public static byte[] HashWordLegacy(string password, byte[] salt, string algorithmName, int spinCount)
        {
            uint key = LegacyKey(password);

            // Байты ключа в обратном порядке: 0x64CEED7E становится «7EEDCE64».
            uint reversed = ((key & 0xFF) << 24) | ((key & 0xFF00) << 8) | ((key >> 8) & 0xFF00) | (key >> 24);
            byte[] keyBytes = Encoding.Unicode.GetBytes(reversed.ToString("X8"));

            return Iterate(keyBytes, salt, algorithmName, spinCount, iteratorFirst: false);
        }

        /// <summary>Отпечаток прямо от пароля в UTF-16, номер прохода — перед хешем.</summary>
        public static byte[] HashDirect(string password, byte[] salt, string algorithmName, int spinCount)
            => Iterate(Encoding.Unicode.GetBytes(password), salt, algorithmName, spinCount, iteratorFirst: true);

        private static byte[] Iterate(byte[] input, byte[] salt, string algorithmName, int spinCount, bool iteratorFirst)
        {
            using var algorithm = CreateAlgorithm(algorithmName)
                                  ?? throw new NotSupportedException($"Хеш-функция {algorithmName} не поддерживается");

            var first = new byte[salt.Length + input.Length];
            Buffer.BlockCopy(salt, 0, first, 0, salt.Length);
            Buffer.BlockCopy(input, 0, first, salt.Length, input.Length);

            byte[] hash = algorithm.ComputeHash(first);

            var buffer = new byte[hash.Length + 4];
            for (int i = 0; i < spinCount; i++)
            {
                if (iteratorFirst)
                {
                    WriteInt32(buffer, 0, i);
                    Buffer.BlockCopy(hash, 0, buffer, 4, hash.Length);
                }
                else
                {
                    Buffer.BlockCopy(hash, 0, buffer, 0, hash.Length);
                    WriteInt32(buffer, hash.Length, i);
                }

                hash = algorithm.ComputeHash(buffer);
            }

            return hash;
        }

        private static void WriteInt32(byte[] buffer, int offset, int value)
        {
            buffer[offset] = (byte)value;
            buffer[offset + 1] = (byte)(value >> 8);
            buffer[offset + 2] = (byte)(value >> 16);
            buffer[offset + 3] = (byte)(value >> 24);
        }

        /// <summary>
        /// Старый 32-битный ключ пароля Word: старшее слово — из таблицы кодов по
        /// битам знаков, младшее — сдвиговая свёртка знаков с длиной.
        /// </summary>
        public static uint LegacyKey(string password)
        {
            int length = Math.Min(password.Length, MaxPasswordLength);
            if (length == 0) return 0;

            // Знак пароля — младший байт, а если он нулевой — старший.
            var bytes = new byte[length];
            for (int i = 0; i < length; i++)
            {
                char ch = password[i];
                byte low = (byte)(ch & 0xFF);
                bytes[i] = low != 0 ? low : (byte)(ch >> 8);
            }

            int high = InitialCodeArray[length - 1];
            for (int i = 0; i < length; i++)
            {
                int row = MaxPasswordLength - length + i;
                for (int bit = 0; bit < 7; bit++)
                {
                    if ((bytes[i] & (1 << bit)) != 0)
                        high ^= EncryptionMatrix[row, bit];
                }
            }

            int low16 = 0;
            for (int i = length - 1; i >= 0; i--)
                low16 = (((low16 >> 14) & 0x0001) | ((low16 << 1) & 0x7FFF)) ^ bytes[i];

            low16 = (((low16 >> 14) & 0x0001) | ((low16 << 1) & 0x7FFF)) ^ length ^ 0xCE4B;

            return ((uint)(high & 0xFFFF) << 16) | (uint)(low16 & 0xFFFF);
        }

        private static bool Matches(byte[] actual, byte[] expected)
            => CryptographicOperations.FixedTimeEquals(actual, expected);
    }
}
