using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodeInsights.Api;

public sealed class ApiException(int statusCode, object detail) : Exception(detail.ToString())
{
    public int StatusCode { get; } = statusCode;
    public object Detail { get; } = detail;
}

public static class ApiJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        NumberHandling = JsonNumberHandling.Strict,
    };

    public static async Task<T> ReadAsync<T>(HttpContext context, CancellationToken ct = default)
    {
        T value;
        try
        {
            value = await JsonSerializer.DeserializeAsync<T>(context.Request.Body, Options, ct)
                ?? throw new ApiException(400, "A request body is required.");
        }
        catch (JsonException)
        {
            throw new ApiException(400, "The JSON body does not match the request contract.");
        }
        var errors = new List<ValidationResult>();
        if (!Validator.TryValidateObject(value!, new ValidationContext(value!), errors, true))
            throw new ApiException(422, string.Join(" ", errors.Select(e => e.ErrorMessage)));
        return value;
    }

    public static string Iso(DateTime value) => value.ToString("O", System.Globalization.CultureInfo.InvariantCulture);
}
