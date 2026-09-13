namespace RWCompanion.Mod;

internal sealed class HostControlPermission
{
    private object? _lobby;
    private object? _host;
    private string _gameplay = "";
    private bool _allowed;
    internal string Grant { get; private set; } = "";

    internal bool Update(object? lobby, object? host, string gameplay, bool allowed)
    {
        if (ReferenceEquals(_lobby, lobby) && ReferenceEquals(_host, host) && _gameplay == gameplay && _allowed == allowed) return false;
        _lobby = lobby;
        _host = host;
        _gameplay = gameplay;
        _allowed = allowed;
        Grant = lobby != null && host != null && allowed && gameplay.Length > 0 ? Guid.NewGuid().ToString("N") : "";
        return true;
    }

    internal bool Accepts(object sender, string? grant) => _allowed && Grant.Length > 0
        && ReferenceEquals(sender, _host) && grant == Grant;
}
