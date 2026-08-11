namespace CloudLight.CodexBridge.Services;

/// <summary>Stores the QQ Official Bot AppSecret independently from Telegram.</summary>
public sealed class QqSecretService
{
    private readonly DpapiSecretStore _store = new(
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
			"CloudLight", "CodexBridge", "secrets", "qqbot-app-secret.dat"),
		"CloudLight.CodexBridge/qqbot-app-secret/v1",
		"QQ Bot AppSecret");

    public string SecretFile => _store.SecretFile;

    public async Task SaveAsync(string token, CancellationToken cancellationToken = default)
    {
        try { await _store.SaveAsync(token, cancellationToken).ConfigureAwait(false); }
        catch (DpapiSecretException exception) { throw new QqSecretException(exception.Message, exception); }
    }

    public async Task<string?> LoadAsync(CancellationToken cancellationToken = default)
    {
        try { return await _store.LoadAsync(cancellationToken).ConfigureAwait(false); }
        catch (DpapiSecretException exception) { throw new QqSecretException(exception.Message, exception); }
    }

    public async Task DeleteAsync(CancellationToken cancellationToken = default)
    {
        try { await _store.DeleteAsync(cancellationToken).ConfigureAwait(false); }
        catch (DpapiSecretException exception) { throw new QqSecretException(exception.Message, exception); }
    }
}

public sealed class QqSecretException(string message, Exception? innerException = null) : Exception(message, innerException)
{
}
