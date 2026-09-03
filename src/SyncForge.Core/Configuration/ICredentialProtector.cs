namespace SyncForge.Core.Configuration;

public interface ICredentialProtector
{
    string Protect(string plaintext);
    string Unprotect(string protectedValue);
}
