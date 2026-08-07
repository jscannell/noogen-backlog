using System.Runtime.InteropServices;
using Noogen.Providers.GoogleWorkspace.Security;

namespace Noogen.Providers.GoogleWorkspace.Tests
{
    /// <summary>
    /// The claim being defended: a harvested file is useless elsewhere. The claim deliberately not
    /// made: this stops malware already running as the user. Both are asserted here.
    /// </summary>
    public class TokenProtectorTests
    {
        [Fact]
        public void ForCurrentPlatform_OnWindows_UsesDpapi()
        {
            var protector = TokenProtector.ForCurrentPlatform();

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                Assert.True(protector.IsOsBacked);
                Assert.Contains("DPAPI", protector.Description, StringComparison.Ordinal);
            }
            else
            {
                // Linux CI usually has no Secret Service, and falling back must still be visible.
                Assert.False(string.IsNullOrWhiteSpace(protector.Description));
            }
        }

        [Fact]
        public void ForCurrentPlatform_NoKeystoreAvailable_StillReturnsAUsableProtector() =>
            Assert.NotNull(TokenProtector.ForCurrentPlatform());

        [Fact]
        public void Protect_ThenUnprotect_ReturnsTheOriginalToken()
        {
            var protector = TokenProtector.ForCurrentPlatform();

            var ciphertext = protector.Protect("TokenResponse-someone@noogen.ai", "the-refresh-token");

            Assert.Equal("the-refresh-token", protector.Unprotect("TokenResponse-someone@noogen.ai", ciphertext));
            protector.Remove("TokenResponse-someone@noogen.ai");
        }

        [Fact]
        public void Protect_OsBackedKeystore_LeavesNothingReadableInTheStoredForm()
        {
            var protector = TokenProtector.ForCurrentPlatform();
            if (!protector.IsOsBacked)
                return;   // the plaintext fallback is honest about being plaintext

            var stored = protector.Protect("TokenResponse-x", "super-secret-refresh-token");

            // The whole point: a harvested file yields nothing readable.
            Assert.DoesNotContain("super-secret-refresh-token", stored, StringComparison.Ordinal);
            protector.Remove("TokenResponse-x");
        }

        [Fact]
        public void Unprotect_CiphertextFromAnotherMachine_ReturnsNullRatherThanThrowing()
        {
            // The key material never leaves the OS keystore, so a copied blob cannot be unwrapped.
            // The user must be told to sign in again, not shown a cryptographic stack trace.
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                return;

            var protector = TokenProtector.ForCurrentPlatform();

            Assert.Null(protector.Unprotect("TokenResponse-x", Convert.ToBase64String("not a real dpapi blob"u8.ToArray())));
        }

        [Fact]
        public void Unprotect_CiphertextIsNotEvenBase64_ReturnsNullRatherThanThrowing()
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                return;

            Assert.Null(TokenProtector.ForCurrentPlatform().Unprotect("TokenResponse-x", "!!! not base64 !!!"));
        }

        [Fact]
        public void Description_PlaintextFallback_SaysItIsUnencrypted()
        {
            // Silently writing plaintext while looking encrypted would be the worst outcome, so
            // the CLI warns off this flag.
            var protector = new PlaintextTokenProtector();

            Assert.False(protector.IsOsBacked);
            Assert.Contains("UNENCRYPTED", protector.Description, StringComparison.Ordinal);
        }

        [Fact]
        public void Protect_PlaintextFallback_RoundTripsWithoutPretendingToEncrypt()
        {
            var protector = new PlaintextTokenProtector();

            Assert.Equal("1//refresh", protector.Protect("TokenResponse-x", "1//refresh"));
            Assert.Equal("1//refresh", protector.Unprotect("TokenResponse-x", "1//refresh"));
        }
    }
}
