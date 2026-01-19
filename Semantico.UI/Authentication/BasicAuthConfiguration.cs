namespace Semantico.UI.Authentication;

public class BasicAuthConfiguration
{
    public bool Enabled { get; internal set; }
    public string Username { get; internal set; } = null!;
    public string Password { get; internal set; } = null!;
    public string Realm { get; internal set; } = "Semantico Admin";
}