namespace R007.Pos.Core.Http;

internal static class RequestCloner
{
    /// <summary>Clones a request whose content is buffered (our JSON bodies are <see cref="ByteArrayContent"/>).</summary>
    public static async Task<HttpRequestMessage> CloneAsync(HttpRequestMessage source, CancellationToken ct)
    {
        var clone = new HttpRequestMessage(source.Method, source.RequestUri) { Version = source.Version };

        foreach (var header in source.Headers)
        {
            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        if (source.Content is not null)
        {
            var bytes = await source.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            var content = new ByteArrayContent(bytes);
            foreach (var header in source.Content.Headers)
            {
                content.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }

            clone.Content = content;
        }

        return clone;
    }
}
