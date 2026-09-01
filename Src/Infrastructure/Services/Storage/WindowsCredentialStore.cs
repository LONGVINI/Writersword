using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Extensions.Logging;
using Writersword.Core.Interfaces.Services.Storage;

namespace Writersword.Infrastructure.Services.Storage
{
    /// <summary>
    /// Секреты в хранилище учётных данных Windows.
    ///
    /// Выбрано вместо файла с собственным шифрованием потому, что ключ от такого
    /// файла пришлось бы держать рядом с ним. Здесь шифрует система, привязывая
    /// запись к учётной записи: другой пользователь той же машины секрет не
    /// прочитает.
    ///
    /// Персистентность LOCAL_MACHINE, а не ENTERPRISE: доменный вариант уезжает
    /// в перемещаемый профиль, где всё равно окажется бесполезным — расшифровать
    /// его на чужой машине нечем.
    /// </summary>
    public sealed class WindowsCredentialStore : ISecretStore
    {
        private const int CRED_TYPE_GENERIC = 1;
        private const int CRED_PERSIST_LOCAL_MACHINE = 2;
        private const int ERROR_NOT_FOUND = 1168;

        // Ограничение самого API: блоб не длиннее 2560 байт.
        private const int MaxBlobBytes = 2560;

        // Префикс нужен, чтобы секреты программы было видно среди чужих
        // в диспетчере учётных данных и можно было убрать руками.
        private const string TargetPrefix = "Writersword:";

        private readonly ILogger<WindowsCredentialStore> _logger;

        public WindowsCredentialStore(ILogger<WindowsCredentialStore> logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public bool IsAvailable => OperatingSystem.IsWindows();

        public string? Read(string key)
        {
            // Проверка написана вызовом OperatingSystem.IsWindows, а не через
            // IsAvailable: анализатор платформ не умеет выводить условие из
            // собственного свойства и ругается на вызов windows-only метода.
            if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(key))
                return null;

            return ReadCore(TargetPrefix + key);
        }

        public bool Write(string key, string value)
        {
            if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(key))
                return false;

            return WriteCore(TargetPrefix + key, value ?? string.Empty);
        }

        public void Delete(string key)
        {
            if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(key))
                return;

            DeleteCore(TargetPrefix + key);
        }

        [SupportedOSPlatform("windows")]
        private string? ReadCore(string target)
        {
            if (!CredRead(target, CRED_TYPE_GENERIC, 0, out var handle))
            {
                var error = Marshal.GetLastWin32Error();

                // Отсутствие записи — штатный исход при первом запуске,
                // в журнал не пишется, чтобы не пугать ошибкой на пустом месте.
                if (error != ERROR_NOT_FOUND)
                    _logger.LogWarning("Failed to read credential, win32 error {Error}", error);

                return null;
            }

            try
            {
                var credential = Marshal.PtrToStructure<CREDENTIAL>(handle);

                if (credential.CredentialBlobSize == 0 || credential.CredentialBlob == IntPtr.Zero)
                    return null;

                var bytes = new byte[credential.CredentialBlobSize];
                Marshal.Copy(credential.CredentialBlob, bytes, 0, bytes.Length);

                try
                {
                    // Блоб в UTF-16: так его пишет и читает диспетчер учётных
                    // данных, и запись остаётся читаемой руками.
                    return Encoding.Unicode.GetString(bytes);
                }
                finally
                {
                    Array.Clear(bytes);
                }
            }
            finally
            {
                CredFree(handle);
            }
        }

        [SupportedOSPlatform("windows")]
        private bool WriteCore(string target, string value)
        {
            var bytes = Encoding.Unicode.GetBytes(value);

            if (bytes.Length > MaxBlobBytes)
            {
                _logger.LogWarning("Secret is too large for the credential store: {Size} bytes", bytes.Length);
                Array.Clear(bytes);
                return false;
            }

            var blob = Marshal.AllocHGlobal(bytes.Length);

            try
            {
                Marshal.Copy(bytes, 0, blob, bytes.Length);

                var credential = new CREDENTIAL
                {
                    Type = CRED_TYPE_GENERIC,
                    TargetName = target,
                    CredentialBlobSize = (uint)bytes.Length,
                    CredentialBlob = blob,
                    Persist = CRED_PERSIST_LOCAL_MACHINE,
                    UserName = Environment.UserName
                };

                if (CredWrite(ref credential, 0))
                    return true;

                _logger.LogWarning("Failed to write credential, win32 error {Error}", Marshal.GetLastWin32Error());
                return false;
            }
            finally
            {
                // Память затирается и освобождается всегда: CredWrite копирует
                // блоб себе, а незатёртый пароль в куче процесса не нужен.
                Marshal.Copy(new byte[bytes.Length], 0, blob, bytes.Length);
                Marshal.FreeHGlobal(blob);
                Array.Clear(bytes);
            }
        }

        [SupportedOSPlatform("windows")]
        private void DeleteCore(string target)
        {
            if (CredDelete(target, CRED_TYPE_GENERIC, 0))
                return;

            var error = Marshal.GetLastWin32Error();
            if (error != ERROR_NOT_FOUND)
                _logger.LogWarning("Failed to delete credential, win32 error {Error}", error);
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct CREDENTIAL
        {
            public uint Flags;
            public uint Type;
            [MarshalAs(UnmanagedType.LPWStr)] public string TargetName;
            [MarshalAs(UnmanagedType.LPWStr)] public string? Comment;
            public long LastWritten;
            public uint CredentialBlobSize;
            public IntPtr CredentialBlob;
            public uint Persist;
            public uint AttributeCount;
            public IntPtr Attributes;
            [MarshalAs(UnmanagedType.LPWStr)] public string? TargetAlias;
            [MarshalAs(UnmanagedType.LPWStr)] public string? UserName;
        }

        [SupportedOSPlatform("windows")]
        [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CredRead(string target, int type, int reservedFlag, out IntPtr credentialPtr);

        [SupportedOSPlatform("windows")]
        [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CredWrite(ref CREDENTIAL credential, uint flags);

        [SupportedOSPlatform("windows")]
        [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CredDelete(string target, int type, int reservedFlag);

        [SupportedOSPlatform("windows")]
        [DllImport("advapi32.dll", EntryPoint = "CredFree")]
        private static extern void CredFree(IntPtr buffer);
    }
}
