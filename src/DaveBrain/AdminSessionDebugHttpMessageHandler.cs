using DaveHome;

namespace DaveBrain;

public sealed class AdminSessionDebugHttpMessageHandler : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var requestBody = request.Content is null
            ? string.Empty
            : await request.Content.ReadAsStringAsync(cancellationToken);
        try
        {
            var response = await base.SendAsync(request, cancellationToken);
            var responseBody = response.Content is null
                ? string.Empty
                : await response.Content.ReadAsStringAsync(cancellationToken);
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
}
