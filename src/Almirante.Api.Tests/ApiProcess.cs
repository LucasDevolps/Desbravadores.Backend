using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Almirante.Api.Tests;

// Sobe o binário REAL da API (dotnet Almirante.Api.dll, Program.cs completo, Kestrel em HTTPS com um
// certificado autoassinado descartável) — o mesmo caminho de startup do container. Toda a configuração vai por
// variável de ambiente do processo, como no Compose. Serve para provar o que só o startup real prova: a API
// sobe com a identidade administrativa dedicada, recusa "sa"/SQL_SA_PASSWORD e reinicia sem "sa".
public sealed class ApiProcess : IAsyncDisposable
{
    public const string AdminEmail = "admin@local.dev";
    public const string AdminSenha = "Tst-Senha-Longa-2026-ok-Xz9";

    private readonly Process _process;
    private readonly StringBuilder _output = new();
    private readonly TaskCompletionSource<Uri> _listening = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly string _certificatePath;

    public int ProcessId => _process.Id;
    public string Output { get { lock (_output) return _output.ToString(); } }
    public Task<Uri> Listening => _listening.Task;
    public Task<int> Exited { get; }

    private ApiProcess(Process process, string certificatePath)
    {
        _process = process;
        _certificatePath = certificatePath;
        var exit = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        process.EnableRaisingEvents = true;
        process.Exited += (_, _) => exit.TrySetResult(process.ExitCode);
        Exited = exit.Task;
    }

    // Variáveis mínimas para a API subir; o teste acrescenta/sobrescreve as de SQL.
    public static Dictionary<string, string> BaseEnvironment() => new()
    {
        ["ASPNETCORE_ENVIRONMENT"] = "Development",
        ["Jwt__Issuer"] = "Almirante.Api.Tests",
        ["Jwt__Audience"] = "Almirante.Api.Tests",
        ["Jwt__ActiveKeyId"] = "v1",
        ["Jwt__Keys__v1"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(64)),
        ["SeedAdmin__Email"] = AdminEmail,
        ["SeedAdmin__Senha"] = AdminSenha,
        ["LoginProtection__RateLimit__PermitLimit"] = "10000",
    };

    public static ApiProcess Start(IDictionary<string, string> environment)
    {
        var dll = Path.Combine(AppContext.BaseDirectory, "Almirante.Api.dll");
        Assert.True(File.Exists(dll), "Almirante.Api.dll deve estar no diretório de saída dos testes.");

        var certificatePath = Path.Combine(Path.GetTempPath(), $"almirante-test-{Guid.NewGuid():N}.pfx");
        var certificatePassword = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        using (var rsa = RSA.Create(2048))
        {
            var request = new CertificateRequest("CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            var san = new SubjectAlternativeNameBuilder();
            san.AddIpAddress(System.Net.IPAddress.Loopback);
            san.AddDnsName("localhost");
            request.CertificateExtensions.Add(san.Build());
            using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(1));
            File.WriteAllBytes(certificatePath, certificate.Export(X509ContentType.Pfx, certificatePassword));
        }

        var psi = new ProcessStartInfo("dotnet", $"\"{dll}\"")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            WorkingDirectory = AppContext.BaseDirectory,
        };
        // Nada herdado do ambiente do desenvolvedor pode mudar o teste (nem trazer SQL_SA_PASSWORD).
        foreach (var key in psi.Environment.Keys.Where(k => k.StartsWith("ConnectionStrings__", StringComparison.OrdinalIgnoreCase)
                     || k.StartsWith("DbCredentials__", StringComparison.OrdinalIgnoreCase)
                     || k.StartsWith("Jwt__", StringComparison.OrdinalIgnoreCase)
                     || k.StartsWith("SeedAdmin__", StringComparison.OrdinalIgnoreCase)
                     || k is "SQL_SA_PASSWORD" or "MSSQL_SA_PASSWORD" or "ASPNETCORE_URLS" or "ASPNETCORE_HTTP_PORTS").ToList())
            psi.Environment.Remove(key);
        psi.Environment["ASPNETCORE_URLS"] = "https://127.0.0.1:0";
        psi.Environment["ASPNETCORE_Kestrel__Certificates__Default__Path"] = certificatePath;
        psi.Environment["ASPNETCORE_Kestrel__Certificates__Default__Password"] = certificatePassword;
        foreach (var (key, value) in environment) psi.Environment[key] = value;

        var process = new Process { StartInfo = psi };
        var api = new ApiProcess(process, certificatePath);
        DataReceivedEventHandler handler = (_, e) =>
        {
            if (e.Data is null) return;
            lock (api._output) api._output.AppendLine(e.Data);
            var match = Regex.Match(e.Data, @"Now listening on:\s*(https://\S+)");
            if (match.Success) api._listening.TrySetResult(new Uri(match.Groups[1].Value));
        };
        process.OutputDataReceived += handler;
        process.ErrorDataReceived += handler;
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        process.StandardInput.Close();
        return api;
    }

    // Espera a API aceitar conexões ou o processo terminar (o que, num startup recusado, é o resultado esperado).
    public async Task<(bool Started, int? ExitCode)> WaitAsync(TimeSpan timeout)
    {
        var finished = await Task.WhenAny(Listening, Exited, Task.Delay(timeout));
        if (finished == Listening) return (true, null);
        if (finished == Exited) return (false, await Exited);
        throw new TimeoutException($"A API não subiu nem terminou em {timeout}. Saída:\n{Output}");
    }

    public HttpClient CreateClient(Uri baseAddress)
    {
        var handler = new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => true, UseCookies = true };
        return new HttpClient(handler) { BaseAddress = baseAddress, Timeout = TimeSpan.FromSeconds(60) };
    }

    // Operações normais da aplicação, todas pela identidade de runtime (e a auditoria pelo trigger): CSRF,
    // login, perfil, listagens, registrar/alterar/excluir logicamente um lançamento, refresh e logout.
    public static async Task ExercitarOperacoesNormaisAsync(HttpClient client)
    {
        async Task CsrfAsync()
        {
            var response = await client.GetAsync("/api/Auth/csrf");
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadFromJsonAsync<JsonElement>();
            client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
            client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", json.GetProperty("csrfToken").GetString());
        }

        Assert.Equal(System.Net.HttpStatusCode.OK, (await client.GetAsync("/health")).StatusCode);

        await CsrfAsync();
        var login = await client.PostAsJsonAsync("/api/Auth/login", new { email = AdminEmail, senha = AdminSenha });
        login.EnsureSuccessStatusCode();
        var token = (await login.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetProperty("accessToken").GetString()!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        await CsrfAsync();

        var me = await client.GetFromJsonAsync<JsonElement>("/api/Auth/Me");
        var meId = me.GetProperty("id").GetGuid();
        Assert.Equal(System.Net.HttpStatusCode.OK, (await client.GetAsync("/api/Cargos")).StatusCode);
        Assert.Equal(System.Net.HttpStatusCode.OK, (await client.GetAsync("/api/Usuarios")).StatusCode);

        var registrar = await client.PostAsJsonAsync("/api/Lancamentos/Registrar", new
        {
            membroId = meId, finalidade = "Mensalidade", descricao = "operacao-normal", categoria = "Clube", tipoFluxo = "Entrada", valor = 12.5m,
            vencimento = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(2).ToString("yyyy-MM-dd"), aplicarATodosOsMembros = false,
        });
        Assert.Equal(System.Net.HttpStatusCode.Created, registrar.StatusCode);
        var id = (await registrar.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        Assert.Equal(System.Net.HttpStatusCode.OK, (await client.PutAsJsonAsync($"/api/Lancamentos/{id}", new { status = "Pago" })).StatusCode);
        var delete = new HttpRequestMessage(HttpMethod.Delete, $"/api/Lancamentos/{id}") { Content = JsonContent.Create(new { motivo = "teste de operacao normal" }) };
        Assert.Equal(System.Net.HttpStatusCode.NoContent, (await client.SendAsync(delete)).StatusCode);

        Assert.Equal(System.Net.HttpStatusCode.OK, (await client.PostAsync("/api/Auth/refresh", null)).StatusCode);
        Assert.Equal(System.Net.HttpStatusCode.NoContent, (await client.PostAsync("/api/Auth/logout", null)).StatusCode);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
                await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
            }
        }
        catch (InvalidOperationException) { }
        _process.Dispose();
        try { File.Delete(_certificatePath); } catch (IOException) { }
    }
}
