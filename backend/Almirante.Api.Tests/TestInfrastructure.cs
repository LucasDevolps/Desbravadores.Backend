using System.Collections.Concurrent;
using Almirante.Api.Entities;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace Almirante.Api.Tests;

// Fábrica com relógio controlável, hasher instrumentado e captura de logs, sobre a mesma
// configuração da AlmiranteApiFactory. A validação criptográfica do JWT continua com o relógio real
// do framework; o relógio falso afeta emissão, sessões e refresh.
public sealed class InstrumentedApiFactory : AlmiranteApiFactory
{
    public FakeTimeProvider Clock { get; } = new(DateTimeOffset.UtcNow);
    public CountingPasswordHasher Hasher { get; } = new();
    public CapturingLoggerProvider Logs { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(Clock);
            services.RemoveAll<IPasswordHasher<Usuario>>();
            services.AddSingleton<IPasswordHasher<Usuario>>(Hasher);
        });
        builder.ConfigureLogging(logging => logging.AddProvider(Logs));
    }
}

public sealed class CountingPasswordHasher : IPasswordHasher<Usuario>
{
    private readonly PasswordHasher<Usuario> _inner = new();
    private int _verifications;

    public int Verifications => _verifications;

    public string HashPassword(Usuario user, string password) => _inner.HashPassword(user, password);

    public PasswordVerificationResult VerifyHashedPassword(Usuario user, string hashedPassword, string providedPassword)
    {
        Interlocked.Increment(ref _verifications);
        return _inner.VerifyHashedPassword(user, hashedPassword, providedPassword);
    }
}

public sealed record CapturedLog(string Category, LogLevel Level, EventId EventId, string Message, IReadOnlyList<KeyValuePair<string, object?>> State);

public sealed class CapturingLoggerProvider : ILoggerProvider
{
    private readonly ConcurrentQueue<CapturedLog> _entries = new();

    public IReadOnlyCollection<CapturedLog> Entries => _entries;

    public ILogger CreateLogger(string categoryName) => new Logger(categoryName, _entries);

    public void Dispose() { }

    private sealed class Logger(string category, ConcurrentQueue<CapturedLog> entries) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            entries.Enqueue(new CapturedLog(category, logLevel, eventId, formatter(state, exception),
                state as IReadOnlyList<KeyValuePair<string, object?>> ?? []));
    }
}
