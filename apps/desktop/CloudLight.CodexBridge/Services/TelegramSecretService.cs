namespace CloudLight.CodexBridge.Services;

/// <summary>Stores the Telegram token at the v0.3-compatible DPAPI path and entropy.</summary>
public sealed class TelegramSecretService
{
    private readonly DpapiSecretStore _store = new(
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CloudLight", "CodexBridge", "secrets", "telegram-token.dat"),
        "CloudLight.CodexBridge/telegram-token/v1",
        "Telegram Token");

    public string SecretFile => _store.SecretFile;

    public async Task SaveAsync(string token, CancellationToken cancellationToken = default)
    {
        try { await _store.SaveAsync(token, cancellationToken).ConfigureAwait(false); }
        catch (DpapiSecretException exception) { throw new TelegramSecretException(exception.Message, exception); }
    }

    public async Task<string?> LoadAsync(CancellationToken cancellationToken = default)
    {
        try { return await _store.LoadAsync(cancellationToken).ConfigureAwait(false); }
        catch (DpapiSecretException exception) { throw new TelegramSecretException(exception.Message, exception); }
    }

    public async Task DeleteAsync(CancellationToken cancellationToken = default)
    {
        try { await _store.DeleteAsync(cancellationToken).ConfigureAwait(false); }
        catch (DpapiSecretException exception) { throw new TelegramSecretException(exception.Message, exception); }
    }
}

public sealed class TelegramSecretException(string message, Exception? innerException = null) : Exception(message, innerException)
{
}
