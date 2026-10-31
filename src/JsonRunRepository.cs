using System.Text.Json;
using BuildAdvisor.Core;

namespace BuildAdvisor.Data;

public sealed class JsonRunRepository
{
    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };

    public async Task<IReadOnlyList<RunSnapshot>> LoadAsync(Stream stream, CancellationToken cancellationToken = default) =>
        await JsonSerializer.DeserializeAsync<List<RunSnapshot>>(stream, Options, cancellationToken)
        ?? new List<RunSnapshot>();
}
