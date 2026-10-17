using Microsoft.Extensions.Options;

namespace Almirante.Api.Options;

public sealed class ViaCepOptions
{
    public const string SectionName = "ViaCep";

    public string BaseUrl { get; set; } = "https://viacep.com.br/ws/";

    /// <summary>Prazo total de uma consulta, somando a nova tentativa após falha transitória.</summary>
    public int TimeoutMs { get; set; } = 5000;

    public TimeSpan Timeout => TimeSpan.FromMilliseconds(TimeoutMs);
}

public sealed class ViaCepOptionsValidator : IValidateOptions<ViaCepOptions>
{
    public const int TimeoutMinimoMs = 200;
    public const int TimeoutMaximoMs = 30000;

    public ValidateOptionsResult Validate(string? name, ViaCepOptions options)
    {
        var erros = new List<string>();
        if (!Uri.TryCreate(options.BaseUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || !options.BaseUrl.EndsWith('/'))
        {
            erros.Add("ViaCep:BaseUrl deve ser uma URL https absoluta terminada em '/' (ex.: https://viacep.com.br/ws/).");
        }

        if (options.TimeoutMs is < TimeoutMinimoMs or > TimeoutMaximoMs)
        {
            erros.Add($"ViaCep:TimeoutMs deve estar entre {TimeoutMinimoMs} e {TimeoutMaximoMs}.");
        }

        return erros.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(erros);
    }
}
