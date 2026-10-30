using Microsoft.Data.SqlClient;

namespace Almirante.Api.Tests;

/// <summary>
/// Remoção dos bancos descartáveis dos testes. Várias classes limpam os próprios bancos ao mesmo tempo, e o SQL Server às vezes escolhe
/// um desses DROP como vítima de deadlock (erro 1205, nos metadados do master): a remoção é repetida, nunca ignorada.
/// </summary>
public static class BancosDeTeste
{
    private const int TentativasMaximas = 5;

    public static string ComandoRemover(string database) =>
        $"IF DB_ID(N'{database}') IS NOT NULL BEGIN ALTER DATABASE [{database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{database}]; END";

    public static void Remover(SqlConnection master, string database)
    {
        for (var tentativa = 1; ; tentativa++)
        {
            try
            {
                using var comando = master.CreateCommand();
                comando.CommandText = ComandoRemover(database);
                comando.ExecuteNonQuery();
                return;
            }
            catch (SqlException ex) when (ex.Number == 1205 && tentativa < TentativasMaximas)
            {
                Thread.Sleep(100 * tentativa);
            }
        }
    }

    public static async Task RemoverAsync(SqlConnection master, string database)
    {
        for (var tentativa = 1; ; tentativa++)
        {
            try
            {
                await using var comando = master.CreateCommand();
                comando.CommandText = ComandoRemover(database);
                await comando.ExecuteNonQueryAsync();
                return;
            }
            catch (SqlException ex) when (ex.Number == 1205 && tentativa < TentativasMaximas)
            {
                await Task.Delay(100 * tentativa);
            }
        }
    }
}
