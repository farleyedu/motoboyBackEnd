using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using APIBack.Model.Gestao;
using Dapper;
using Npgsql;

namespace APIBack.Service;

public sealed record PacoteComercialDto(string Chave, string Nome, IReadOnlyList<string> Modulos,
    int PacotesMotoboyBase, int LojasAfetadas);

public sealed record EditarPacoteComercialRequest(IReadOnlyList<string>? Modulos, int? LojasAfetadasConfirmadas);

public interface IPacotesComerciaisService
{
    Task<IReadOnlyList<PacoteComercialDto>> ListarAsync(CancellationToken ct = default);
    Task<PacoteComercialDto> AtualizarAsync(string chave, EditarPacoteComercialRequest request, int usuarioId, CancellationToken ct = default);
}

public sealed class PacotesComerciaisService(NpgsqlDataSource source) : IPacotesComerciaisService
{
    private static readonly HashSet<string> ModulosPermitidos = new(StringComparer.OrdinalIgnoreCase)
    {
        "Pedidos", "Delivery", "iFood", "Impressao", "Remuneracao", "WhatsApp", "Clientes",
        "Cardapio", "CardapioWeb", "Inteligencia", "Financeiro", "Experience", "Simulador",
        "Servicos", "FAQ", "Disponibilidade"
    };

    private sealed class Row
    {
        public string Chave { get; set; } = string.Empty;
        public string Nome { get; set; } = string.Empty;
        public string[] ModulosAtivos { get; set; } = Array.Empty<string>();
        public int PacotesMotoboyBase { get; set; }
        public int LojasAfetadas { get; set; }
    }

    private const string SelectSql = @"
SELECT p.chave AS Chave, p.nome AS Nome, p.modulos_ativos::text[] AS ModulosAtivos,
       p.pacotes_motoboy_base AS PacotesMotoboyBase,
       (SELECT COUNT(*) FROM estabelecimentos e WHERE e.plano_comercial = p.chave) AS LojasAfetadas
  FROM pacotes_comerciais p";

    public async Task<IReadOnlyList<PacoteComercialDto>> ListarAsync(CancellationToken ct = default)
    {
        await using var connection = await source.OpenConnectionAsync(ct);
        var rows = await connection.QueryAsync<Row>(new CommandDefinition(
            SelectSql + " ORDER BY CASE p.chave WHEN 'partida' THEN 1 WHEN 'rota' THEN 2 WHEN 'turbo' THEN 3 ELSE 4 END;",
            cancellationToken: ct));
        return rows.Select(ToDto).ToArray();
    }

    public async Task<PacoteComercialDto> AtualizarAsync(string chave, EditarPacoteComercialRequest request, int usuarioId, CancellationToken ct = default)
    {
        chave = (chave ?? string.Empty).Trim().ToLowerInvariant();
        if (request.Modulos == null || request.Modulos.Count == 0 || request.Modulos.Any(m => !ModulosPermitidos.Contains(m)))
            throw new SituacaoComercialException(422, "MODULOS_INVALIDOS", "Escolha módulos válidos para o pacote.");
        if (request.LojasAfetadasConfirmadas is null or < 0)
            throw new SituacaoComercialException(409, "CONFIRMACAO_NECESSARIA", "Confirme quantas lojas usam este pacote antes de salvar.");

        var modulos = EstabelecimentoModuleMapper.ToDatabaseModules(request.Modulos, null);
        await using var connection = await source.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        var existe = await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
            "SELECT EXISTS (SELECT 1 FROM pacotes_comerciais WHERE chave=@Chave FOR UPDATE);",
            new { Chave = chave }, tx, cancellationToken: ct));
        if (!existe) throw new SituacaoComercialException(404, "PACOTE_NAO_ENCONTRADO", "Pacote não encontrado.");

        var ids = (await connection.QueryAsync<Guid>(new CommandDefinition(
            "SELECT id FROM estabelecimentos WHERE plano_comercial=@Chave ORDER BY id FOR UPDATE;",
            new { Chave = chave }, tx, cancellationToken: ct))).ToArray();
        if (ids.Length != request.LojasAfetadasConfirmadas)
            throw new SituacaoComercialException(409, "IMPACTO_ALTERADO",
                $"Agora {ids.Length} loja(s) usam este pacote. Confira o impacto e confirme novamente.");

        await connection.ExecuteAsync(new CommandDefinition(@"
UPDATE pacotes_comerciais
   SET modulos_ativos=@Modulos::modulo_enum[], atualizado_em=NOW(), atualizado_por=@UsuarioId
 WHERE chave=@Chave;",
            new { Chave = chave, Modulos = modulos, UsuarioId = usuarioId }, tx, cancellationToken: ct));
        if (ids.Length > 0)
            await connection.ExecuteAsync(new CommandDefinition(@"
UPDATE estabelecimentos
   SET modulos_ativos=@Modulos::modulo_enum[], data_atualizacao=NOW()
 WHERE id=ANY(@Ids) AND plano_comercial=@Chave;",
                new { Ids = ids, Chave = chave, Modulos = modulos }, tx, cancellationToken: ct));

        var result = await connection.QuerySingleAsync<Row>(new CommandDefinition(
            SelectSql + " WHERE p.chave=@Chave;", new { Chave = chave }, tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
        return ToDto(result);
    }

    private static PacoteComercialDto ToDto(Row row) => new(row.Chave, row.Nome,
        EstabelecimentoModuleMapper.ToUiModules(string.Empty, row.ModulosAtivos),
        row.PacotesMotoboyBase, row.LojasAfetadas);
}
