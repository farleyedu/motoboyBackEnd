using System.Threading;
using Dapper;

namespace APIBack.Infrastructure
{
    /// <summary>
    /// Configuracao global do Dapper, chamada uma vez no boot (Program.cs) e pelos
    /// testes, para que ambos exercitem exatamente o mesmo registro.
    /// </summary>
    public static class DapperConfiguration
    {
        private static int _configured;

        public static void Configure()
        {
            if (Interlocked.Exchange(ref _configured, 1) == 1)
            {
                return;
            }

            // Mapeia colunas snake_case para propriedades PascalCase.
            DefaultTypeMap.MatchNamesWithUnderscores = true;

            SqlMapper.AddTypeHandler(new DateTimeOffsetTypeHandler());
        }
    }
}
