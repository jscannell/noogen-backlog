namespace Noogen.Providers.GoogleWorkspace.Security
{
    /// <summary>
    /// Encrypts the OAuth refresh token at rest using the operating system's own keystore.
    ///
    /// <b>The threat this addresses.</b> A refresh token with Drive scope is a high-value target,
    /// and the dominant real-world attack is commodity infostealer malware that sweeps known
    /// credential paths — browser cookie stores, cloud CLI config, npm and PyPI tokens — and
    /// exfiltrates the files for offline use. Plaintext JSON on disk is exactly what that
    /// harvesting expects to find.
    ///
    /// <b>What this actually buys.</b> A copied file is useless on the attacker's machine: the
    /// key material never leaves the OS keystore and is bound to this user on this device. That
    /// defeats bulk collection and offline reuse, which is how most of these compromises play out.
    ///
    /// <b>What it does not buy, stated plainly.</b> Malware already running as this user, on this
    /// machine, while the keystore is unlocked, can ask the OS to decrypt exactly as we do. No
    /// user-space scheme can prevent that — a process with your privileges can do what you can do.
    /// The honest claim is that this raises the cost from "copy a file" to "write
    /// target-specific code and run it on the victim's machine", and removes the offline attack
    /// entirely. Anything that stores a key beside the ciphertext would be theatre; this does not.
    /// </summary>
    public interface ITokenProtector
    {
        /// <summary>Human-readable description of the backing keystore, surfaced by `backlog whoami`.</summary>
        string Description { get; }

        /// <summary>False when falling back to plaintext, so callers can warn rather than imply safety.</summary>
        bool IsOsBacked { get; }

        string Protect(string key, string plaintext);

        string? Unprotect(string key, string ciphertext);

        void Remove(string key);
    }
}
