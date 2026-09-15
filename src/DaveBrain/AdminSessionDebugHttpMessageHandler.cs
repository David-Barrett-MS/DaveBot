using DaveHome;

namespace DaveBrain;

public sealed class AdminSessionDebugHttpMessageHandler : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        if (request.Options.TryGetValue(
                new HttpRequestOptionsKey<bool>("DaveBot.SuppressDebugLogging"),
                out var suppressDebugLogging) &&
            suppressDebugLogging)
        {
            return await base.SendAsync(request, cancellationToken);
        }

        var requestBody = request.Content is null
            ? string.Empty
            : await request.Content.ReadAsStringAsync(cancellationToken);
        try
        {
            var response = await base.SendAsync(request, cancellationToken);
            var responseBody = await ReadResponseBodyAsync(response, cancellationToken);
            RuntimeAuditContext.Write(
                "External HTTP",
                $"{request.Method} {request.RequestUri}\n{requestBody}",
                $"{(int)response.StatusCode} {response.ReasonPhrase}\n{responseBody}");
            return response;
        }
        catch (HttpRequestException exception)
        {
            RuntimeAuditContext.Write(
                "External HTTP",
                $"{request.Method} {request.RequestUri}\n{requestBody}",
                $"{exception.GetType().Name}: {exception.Message}");
            throw;
        }
        catch (OperationCanceledException exception)
        {
            RuntimeAuditContext.Write(
                "External HTTP",
                $"{request.Method} {request.RequestUri}\n{requestBody}",
                $"{exception.GetType().Name}: {exception.Message}");
            throw;
        }
    }

    private static async Task<string> ReadResponseBodyAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (response.Content is null)
        {
            return string.Empty;
        }

        var contentType = response.Content.Headers.ContentType?.MediaType;
        if (contentType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) == true)
        {
            return $"[binary image; {response.Content.Headers.ContentLength?.ToString() ?? "unknown"} bytes]";
        }

        return await response.Content.ReadAsStringAsync(cancellationToken);
    }
}
