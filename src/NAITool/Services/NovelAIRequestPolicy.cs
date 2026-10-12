namespace NAITool.Services;

internal static class NovelAIRequestPolicy
{
    // One timeout covers the request and response body.
    public static TimeSpan Budget(int waitingSeconds, int operationSeconds, bool readOnly = false)
        => TimeSpan.FromSeconds(Clamp(operationSeconds) + (readOnly ? 0 : Clamp(waitingSeconds)));
    private static int Clamp(int value) => Math.Clamp(value, 1, 3600);
    public static string StatusKey(int status) => "api.safe." + (status switch
    {
        401 => "authentication", 403 => "forbidden", 402 => "payment", 404 => "unsupported",
        429 => "limited", 503 => "unavailable", 504 => "timeout", _ => "failed",
    });
    public static string ExceptionKey(Exception error) => "api.safe." + (error switch
    {
        OperationCanceledException => "cancelled", InvalidDataException or EndOfStreamException => "invalid_response",
        ArgumentException => "invalid_request", _ => "failed",
    });

    public static string Status(int status) => status switch
    {
        401 => "Authentication rejected.", 403 => "Operation is not authorized.",
        402 => "Insufficient quota or payment required.", 404 => "Endpoint is unavailable or unsupported.",
        429 => "Request or queue limit reached.", 503 => "Service or queue unavailable.",
        504 => "Service timed out; generation billing outcome may be unknown. Do not automatically retry.",
        _ => "API request failed. Response details are withheld to protect credentials.",
    };
    public static string Exception(Exception error) => error switch
    {
        OperationCanceledException => "Request cancelled or timed out; billing outcome may be unknown. No automatic retry was made.",
        InvalidDataException or EndOfStreamException => "Invalid or incomplete API response; billing outcome may be unknown. No automatic retry was made.",
        ArgumentException => "Invalid API configuration or request.",
        _ => "Request failed. Details are withheld to protect credentials. No automatic retry was made.",
    };
}
