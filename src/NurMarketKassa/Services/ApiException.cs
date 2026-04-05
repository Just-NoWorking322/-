using System.Text.Json;

namespace NurMarketKassa.Services;

/// <summary>Ошибка ответа API (аналог api_client.ApiError).</summary>
public sealed class ApiException : Exception
{
    public int? StatusCode { get; }
    public JsonElement? Payload { get; }

    public ApiException(string message, int? statusCode = null, JsonElement? payload = null)
        : base(message)
    {
        StatusCode = statusCode;
        Payload = payload;
    }
}
