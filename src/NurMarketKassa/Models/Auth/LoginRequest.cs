using System.Text.Json.Serialization;

namespace NurMarketKassa.Models.Auth;

public sealed class LoginRequest
{
    [JsonPropertyName("email")]
    public string Email { get; set; } = "";

    [JsonPropertyName("password")]
    public string Password { get; set; } = "";
}

public sealed class RefreshRequest
{
    [JsonPropertyName("refresh")]
    public string Refresh { get; set; } = "";
}
