using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace Noogen.Providers.GoogleWorkspace.Security
{
    public static class TokenProtector
    {
        /// <summary>
        /// Picks the strongest keystore this machine offers, and says so rather than failing
        /// silently. A caller that gets <see cref="ITokenProtector.IsOsBacked"/> false is expected
        /// to warn — quietly writing plaintext while looking encrypted would be the worst outcome.
        /// </summary>
        public static ITokenProtector ForCurrentPlatform()
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                return new WindowsDpapiTokenProtector();

            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX) && MacKeychainTokenProtector.IsAvailable())
                return new MacKeychainTokenProtector();

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux) && SecretServiceTokenProtector.IsAvailable())
                return new SecretServiceTokenProtector();

            return new PlaintextTokenProtector();
        }
    }

    /// <summary>
    /// DPAPI, scoped to the current user. The key is derived from the user's Windows credentials
    /// and never materialises for us, so the ciphertext is meaningless on another machine or under
    /// another account.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public class WindowsDpapiTokenProtector : ITokenProtector
    {
        // Ties the ciphertext to this application: a blob lifted from here cannot be unwrapped by
        // asking DPAPI on behalf of some other program without also knowing this value.
        static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Noogen.Backlog.OAuth.v1");

        public string Description => "Windows DPAPI (current user)";

        public bool IsOsBacked => true;

        public string Protect(string key, string plaintext) =>
            Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(plaintext), Entropy, DataProtectionScope.CurrentUser));

        public string? Unprotect(string key, string ciphertext)
        {
            try
            {
                return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(ciphertext), Entropy, DataProtectionScope.CurrentUser));
            }
            catch (Exception exception) when (exception is CryptographicException or FormatException)
            {
                // Copied from another machine or account, or corrupt. Treat as "no credential"
                // so the user is told to sign in again rather than shown a crash.
                return null;
            }
        }

        public void Remove(string key)
        {
        }
    }

    /// <summary>
    /// macOS Keychain, via the `security` tool. The token itself lives in the keychain rather than
    /// on disk, so the file we write holds only a marker.
    /// </summary>
    public class MacKeychainTokenProtector : ITokenProtector
    {
        const string ServiceName = "com.noogen.backlog";
        const string Marker = "keychain";

        public string Description => "macOS Keychain";

        public bool IsOsBacked => true;

        public static bool IsAvailable() => File.Exists("/usr/bin/security");

        public string Protect(string key, string plaintext)
        {
            // -U updates in place if the item already exists. Note the secret passes through argv,
            // which is briefly visible to other processes on a shared machine; the keychain has no
            // stdin path for this, and the exposure window is a single exec.
            Run("/usr/bin/security", ["add-generic-password", "-a", key, "-s", ServiceName, "-w", plaintext, "-U"]);
            return Marker;
        }

        public string? Unprotect(string key, string ciphertext)
        {
            var result = Run("/usr/bin/security", ["find-generic-password", "-a", key, "-s", ServiceName, "-w"], throwOnError: false);
            return string.IsNullOrWhiteSpace(result) ? null : result.Trim();
        }

        public void Remove(string key) =>
            Run("/usr/bin/security", ["delete-generic-password", "-a", key, "-s", ServiceName], throwOnError: false);

        internal static string Run(string fileName, string[] arguments, bool throwOnError = true) =>
            ProcessRunner.Run(fileName, arguments, throwOnError);
    }

    /// <summary>
    /// Linux Secret Service (gnome-keyring, KWallet) through `secret-tool`. Commonly absent on
    /// headless machines, which is why availability is probed rather than assumed.
    /// </summary>
    public class SecretServiceTokenProtector : ITokenProtector
    {
        const string ServiceName = "noogen-backlog";
        const string Marker = "secret-service";

        public string Description => "Linux Secret Service (libsecret)";

        public bool IsOsBacked => true;

        public static bool IsAvailable()
        {
            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS")))
                return false;

            return ProcessRunner.Exists("secret-tool");
        }

        public string Protect(string key, string plaintext)
        {
            ProcessRunner.RunWithStdin("secret-tool", ["store", "--label=Noogen backlog", "service", ServiceName, "account", key], plaintext);
            return Marker;
        }

        public string? Unprotect(string key, string ciphertext)
        {
            var result = ProcessRunner.Run("secret-tool", ["lookup", "service", ServiceName, "account", key], throwOnError: false);
            return string.IsNullOrWhiteSpace(result) ? null : result.TrimEnd('\n');
        }

        public void Remove(string key) =>
            ProcessRunner.Run("secret-tool", ["clear", "service", ServiceName, "account", key], throwOnError: false);
    }

    /// <summary>
    /// Last resort when no keystore exists — typically a headless Linux box. Stores plaintext and
    /// reports that it does, so the CLI can warn and the user can decide to use a service account
    /// instead.
    /// </summary>
    public class PlaintextTokenProtector : ITokenProtector
    {
        public string Description => "UNENCRYPTED (no OS keystore available)";

        public bool IsOsBacked => false;

        public string Protect(string key, string plaintext) => plaintext;

        public string? Unprotect(string key, string ciphertext) => ciphertext;

        public void Remove(string key)
        {
        }
    }

    static class ProcessRunner
    {
        public static bool Exists(string fileName)
        {
            try
            {
                return !string.IsNullOrWhiteSpace(Run("/usr/bin/which", [fileName], throwOnError: false));
            }
            catch (Exception)
            {
                return false;
            }
        }

        public static string Run(string fileName, string[] arguments, bool throwOnError = true)
        {
            using var process = Start(fileName, arguments, redirectInput: false);

            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();

            if (process.ExitCode != 0 && throwOnError)
                throw new InvalidOperationException($"{fileName} failed with exit code {process.ExitCode}.");

            return process.ExitCode == 0 ? output : string.Empty;
        }

        public static void RunWithStdin(string fileName, string[] arguments, string input)
        {
            using var process = Start(fileName, arguments, redirectInput: true);

            process.StandardInput.Write(input);
            process.StandardInput.Close();
            process.WaitForExit();

            if (process.ExitCode != 0)
                throw new InvalidOperationException($"{fileName} failed with exit code {process.ExitCode}.");
        }

        static Process Start(string fileName, string[] arguments, bool redirectInput)
        {
            var info = new ProcessStartInfo
            {
                FileName = fileName,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = redirectInput,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            foreach (var argument in arguments)
                info.ArgumentList.Add(argument);

            return Process.Start(info) ?? throw new InvalidOperationException($"Could not start {fileName}.");
        }
    }
}
