using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace Captioner.Infrastructure;

internal static class OpenAiHttp
{
    public static Uri Endpoint(string baseUrl, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            throw new ArgumentException("An endpoint base URL is required.", nameof(baseUrl));
        }

        var normalized = baseUrl.TrimEnd('/') + "/" + relativePath.TrimStart('/');
        if (!Uri.TryCreate(normalized, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https"))
        {
            throw new ArgumentException("The endpoint base URL must be an absolute HTTP(S) URL.", nameof(baseUrl));
        }

        return uri;
    }

    public static string GetApiKey(Captioner.Core.EndpointProfile profile)
    {
        if (!string.IsNullOrWhiteSpace(profile.ApiKey))
        {
            return profile.ApiKey;
        }

        if (string.IsNullOrWhiteSpace(profile.ApiKeyEnvironmentVariable))
        {
            throw new InvalidOperationException("The endpoint API key is not configured.");
        }

        var value = Environment.GetEnvironmentVariable(profile.ApiKeyEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException(
                $"The configured API-key environment variable is not set for endpoint '{profile.BaseUrl}'.");
        }

        return value;
    }

    public static void SetAuthorization(HttpRequestMessage request, string key)
    {
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
    }

    public static async Task<HttpResponseMessage> SendWithRetryAsync(
        HttpClient client,
        Func<HttpRequestMessage> requestFactory,
        TimeSpan retryBaseDelay,
        CancellationToken cancellationToken)
    {
        Exception? lastNetworkException = null;

        for (var attempt = 1; attempt <= 3; attempt++)
        {
            using var request = requestFactory();
            HttpResponseMessage response;
            try
            {
                response = await client.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken);
            }
            catch (HttpRequestException exception) when (attempt < 3)
            {
                lastNetworkException = exception;
                await Task.Delay(
                    TimeSpan.FromMilliseconds(Math.Max(0, retryBaseDelay.TotalMilliseconds) * Math.Pow(2, attempt - 1)),
                    cancellationToken);
                continue;
            }
            catch (TaskCanceledException exception) when (!cancellationToken.IsCancellationRequested && attempt < 3)
            {
                lastNetworkException = exception;
                await Task.Delay(
                    TimeSpan.FromMilliseconds(Math.Max(0, retryBaseDelay.TotalMilliseconds) * Math.Pow(2, attempt - 1)),
                    cancellationToken);
                continue;
            }
            catch (HttpRequestException exception)
            {
                throw new HttpRequestException("The endpoint request failed after three attempts.", exception);
            }
            catch (TaskCanceledException exception) when (!cancellationToken.IsCancellationRequested)
            {
                throw new HttpRequestException("The endpoint request timed out after three attempts.", exception);
            }

            if (response.IsSuccessStatusCode)
            {
                return response;
            }

            var retryable = response.StatusCode == HttpStatusCode.TooManyRequests ||
                (int)response.StatusCode >= 500;
            if (!retryable || attempt == 3)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                var statusCode = response.StatusCode;
                response.Dispose();
                throw new HttpRequestException(
                    $"Endpoint returned {(int)statusCode} ({statusCode}): {TrimBody(body)}",
                    inner: null,
                    statusCode: statusCode);
            }

            var retryAfter = GetRetryAfter(response) ??
                TimeSpan.FromMilliseconds(Math.Max(0, retryBaseDelay.TotalMilliseconds) * Math.Pow(2, attempt - 1));
            response.Dispose();
            await Task.Delay(retryAfter, cancellationToken);
        }

        throw new HttpRequestException("The endpoint request failed after three attempts.", lastNetworkException);
    }

    private static TimeSpan? GetRetryAfter(HttpResponseMessage response)
    {
        if (response.Headers.RetryAfter is { } value)
        {
            if (value.Delta is { } delta && delta >= TimeSpan.Zero)
            {
                return delta;
            }

            if (value.Date is { } date)
            {
                var delay = date - DateTimeOffset.UtcNow;
                if (delay >= TimeSpan.Zero)
                {
                    return delay;
                }
            }
        }

        if (response.Headers.TryGetValues("retry-after", out var values) &&
            double.TryParse(values.FirstOrDefault(), NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) &&
            seconds >= 0)
        {
            return TimeSpan.FromSeconds(seconds);
        }

        return null;
    }

    private static string TrimBody(string body)
    {
        var normalized = body.Trim();
        return normalized.Length <= 500 ? normalized : normalized[..500] + "…";
    }

    public static StringContent Json(string value) => new(value, Encoding.UTF8, "application/json");
}
