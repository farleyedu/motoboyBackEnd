using System.Text.Json;
using Dapper;
using Npgsql;

namespace APIBack.Assistente;

public sealed record MensagemGuardada(Guid Id, string Papel, string Texto, string CartoesJson, DateTimeOffset CriadaEm);

public sealed class AcaoGuardada
{
    public Guid Id { get; set; }
    public Guid EstabelecimentoId { get; set; }
    public int UsuarioId { get; set; }
    public Guid? ConversaId { get; set; }
    public string FuncionalidadeId { get; set; } = "";
    public string Acao { get; set; } = "";
    public string Parametros { get; set; } = "{}";
    public string Resumo { get; set; } = "";
    public string? Antes { get; set; }
    public string? Depois { get; set; }
    public string Situacao { get; set; } = "";
    public DateTimeOffset ExpiraEm { get; set; }
}

public sealed record NovaAcao(Guid Id, Guid Loja, int Usuario, Guid? ConversaId, string FuncionalidadeId, string Acao, string ParametrosJson,
    string Resumo, string? Antes, string? Depois, DateTimeOffset ExpiraEm, Guid? DesfazAcaoId);

public sealed record UsoTokens(long Entrada, long Saida, long CacheLidos, string Modelo, string Provedor, bool UsouReserva, string? ItemId = null);

/// <summary>Uma resposta da IA, para uso e custo (fica depois que a conversa some).</summary>
public sealed record RegistroUso(Guid Loja, Guid Conversa, int Usuario, UsoTokens Uso, int AcoesPropostas, int Telas, int Rascunhos);

public static class TiposEvento
{
    public const string Erro = "erro";
    public const string Limite = "limite";
    public const string ChamadoEnviado = "chamado_enviado";
    public const string SemFuncionalidade = "sem_funcionalidade";
    /// <summary>Item da cadeia pulado: detalhe = motivo, extra = id do item.</summary>
    public const string Pulo = "pulo";
    /// <summary>Valor de <c>extra</c> no evento <c>erro</c> quando nenhum item da cadeia respondeu (alerta na Gestão).</summary>
    public const string CadeiaEsgotada = "cadeia_esgotada";
}

public interface IAssistenteRepositorio
{
    Task RegistrarUso(RegistroUso registro, CancellationToken ct);
    /// <summary>Evento de uso. <c>limite</c> grava no máximo um por loja por dia.</summary>
    Task RegistrarEvento(Guid loja, Guid? conversa, int? usuario, string tipo, string? detalhe, string? extra, DateOnly dia, CancellationToken ct);
    Task<bool> ConversaExiste(Guid conversa, Guid loja, DateTimeOffset desde, CancellationToken ct);
    Task CriarConversa(Guid conversa, Guid loja, int usuario, CancellationToken ct);
    Task<IReadOnlyList<MensagemGuardada>> Mensagens(Guid conversa, Guid loja, CancellationToken ct);
    Task<int> MensagensDaLojaDesde(Guid loja, DateTimeOffset desde, CancellationToken ct);
    Task GuardarMensagem(Guid id, Guid conversa, Guid loja, int usuario, string papel, string texto, string cartoesJson, UsoTokens? uso, CancellationToken ct);
    Task CriarAcao(NovaAcao acao, CancellationToken ct);
    Task<AcaoGuardada?> ObterAcao(Guid id, Guid loja, CancellationToken ct);
    /// <summary>Passa de pendente para <paramref name="nova"/> só se ainda estiver pendente (trava contra duplo clique). False = outra decisão chegou antes.</summary>
    Task<bool> Decidir(Guid id, string nova, int usuario, CancellationToken ct);
    Task Finalizar(Guid id, string situacao, string? erro, CancellationToken ct);
    Task Auditar(AcaoGuardada acao, int usuario, string resultado, string? detalhe, CancellationToken ct);
    Task<int> ApagarConversasAntigas(DateTimeOffset antesDe, CancellationToken ct);
}

public sealed class AssistenteRepositorio(NpgsqlDataSource source) : IAssistenteRepositorio
{
    public async Task RegistrarUso(RegistroUso r, CancellationToken ct)
    {
        await using var c = await source.OpenConnectionAsync(ct);
        await c.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO assistente_uso (estabelecimento_id, conversa_id, usuario_id, provedor, modelo, usou_reserva, tokens_entrada, tokens_saida,
                                        tokens_cache_lidos, acoes_propostas, telas, rascunhos, item_id)
            VALUES (@Loja, @Conversa, @Usuario, @Provedor, @Modelo, @UsouReserva, @Entrada, @Saida, @CacheLidos, @AcoesPropostas, @Telas, @Rascunhos, @ItemId)
            """,
            new
            {
                r.Loja, r.Conversa, r.Usuario, r.Uso.Provedor, r.Uso.Modelo, r.Uso.UsouReserva, r.Uso.Entrada, r.Uso.Saida, r.Uso.CacheLidos,
                r.AcoesPropostas, r.Telas, r.Rascunhos, r.Uso.ItemId,
            }, cancellationToken: ct));
    }

    public async Task RegistrarEvento(Guid loja, Guid? conversa, int? usuario, string tipo, string? detalhe, string? extra, DateOnly dia, CancellationToken ct)
    {
        await using var c = await source.OpenConnectionAsync(ct);
        await c.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO assistente_eventos (estabelecimento_id, conversa_id, usuario_id, tipo, detalhe, extra, dia)
            VALUES (@loja, @conversa, @usuario, @tipo, @detalhe, @extra, @dia)
            ON CONFLICT (estabelecimento_id, dia) WHERE tipo = 'limite' DO NOTHING
            """, new { loja, conversa, usuario, tipo, detalhe, extra, dia = dia.ToDateTime(TimeOnly.MinValue) }, cancellationToken: ct));
    }

    public async Task<bool> ConversaExiste(Guid conversa, Guid loja, DateTimeOffset desde, CancellationToken ct)
    {
        await using var c = await source.OpenConnectionAsync(ct);
        return await c.ExecuteScalarAsync<bool>(new CommandDefinition(
            "SELECT EXISTS (SELECT 1 FROM assistente_conversas WHERE id = @conversa AND estabelecimento_id = @loja AND atualizada_em >= @desde)",
            new { conversa, loja, desde }, cancellationToken: ct));
    }

    public async Task CriarConversa(Guid conversa, Guid loja, int usuario, CancellationToken ct)
    {
        await using var c = await source.OpenConnectionAsync(ct);
        await c.ExecuteAsync(new CommandDefinition(
            "INSERT INTO assistente_conversas (id, estabelecimento_id, usuario_id) VALUES (@conversa, @loja, @usuario)",
            new { conversa, loja, usuario }, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<MensagemGuardada>> Mensagens(Guid conversa, Guid loja, CancellationToken ct)
    {
        await using var c = await source.OpenConnectionAsync(ct);
        var linhas = await c.QueryAsync<(Guid Id, string Papel, string Texto, string Cartoes, DateTime CriadaEm)>(new CommandDefinition(
            """
            SELECT id, papel, texto, cartoes::text, criada_em FROM assistente_mensagens
            WHERE conversa_id = @conversa AND estabelecimento_id = @loja ORDER BY criada_em, id
            """, new { conversa, loja }, cancellationToken: ct));
        return linhas.Select(l => new MensagemGuardada(l.Id, l.Papel, l.Texto, l.Cartoes,
            new DateTimeOffset(DateTime.SpecifyKind(l.CriadaEm, DateTimeKind.Utc)))).ToList();
    }

    public async Task<int> MensagensDaLojaDesde(Guid loja, DateTimeOffset desde, CancellationToken ct)
    {
        await using var c = await source.OpenConnectionAsync(ct);
        return await c.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COUNT(*)::int FROM assistente_mensagens WHERE estabelecimento_id = @loja AND papel = 'usuario' AND criada_em >= @desde",
            new { loja, desde }, cancellationToken: ct));
    }

    public async Task GuardarMensagem(Guid id, Guid conversa, Guid loja, int usuario, string papel, string texto, string cartoesJson, UsoTokens? uso, CancellationToken ct)
    {
        await using var c = await source.OpenConnectionAsync(ct);
        await c.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO assistente_mensagens (id, conversa_id, estabelecimento_id, usuario_id, papel, texto, cartoes, tokens_entrada, tokens_saida, tokens_cache_lidos, modelo, provedor, usou_reserva)
            VALUES (@id, @conversa, @loja, @usuario, @papel, @texto, @cartoesJson::jsonb, @entrada, @saida, @cache, @modelo, @provedor, @reserva);
            UPDATE assistente_conversas SET atualizada_em = NOW() WHERE id = @conversa;
            """,
            new
            {
                id, conversa, loja, usuario, papel, texto, cartoesJson,
                entrada = (int?)uso?.Entrada, saida = (int?)uso?.Saida, cache = (int?)uso?.CacheLidos, modelo = uso?.Modelo, provedor = uso?.Provedor, reserva = uso?.UsouReserva,
            }, cancellationToken: ct));
    }

    public async Task CriarAcao(NovaAcao a, CancellationToken ct)
    {
        await using var c = await source.OpenConnectionAsync(ct);
        await c.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO assistente_acoes (id, estabelecimento_id, usuario_id, conversa_id, funcionalidade_id, acao, parametros, resumo, antes, depois, expira_em, desfaz_acao_id)
            VALUES (@Id, @Loja, @Usuario, @ConversaId, @FuncionalidadeId, @Acao, @ParametrosJson::jsonb, @Resumo, @Antes, @Depois, @ExpiraEm, @DesfazAcaoId)
            """, a, cancellationToken: ct));
    }

    public async Task<AcaoGuardada?> ObterAcao(Guid id, Guid loja, CancellationToken ct)
    {
        await using var c = await source.OpenConnectionAsync(ct);
        return await c.QuerySingleOrDefaultAsync<AcaoGuardada>(new CommandDefinition(
            """
            SELECT id, estabelecimento_id AS EstabelecimentoId, usuario_id AS UsuarioId, conversa_id AS ConversaId, funcionalidade_id AS FuncionalidadeId,
                   acao, parametros::text AS Parametros, resumo, antes, depois, situacao, expira_em AS ExpiraEm
            FROM assistente_acoes WHERE id = @id AND estabelecimento_id = @loja
            """, new { id, loja }, cancellationToken: ct));
    }

    public async Task<bool> Decidir(Guid id, string nova, int usuario, CancellationToken ct)
    {
        await using var c = await source.OpenConnectionAsync(ct);
        return await c.ExecuteAsync(new CommandDefinition(
            "UPDATE assistente_acoes SET situacao = @nova, decidida_em = NOW(), decidida_por = @usuario WHERE id = @id AND situacao = 'pendente'",
            new { id, nova, usuario }, cancellationToken: ct)) == 1;
    }

    public async Task Finalizar(Guid id, string situacao, string? erro, CancellationToken ct)
    {
        await using var c = await source.OpenConnectionAsync(ct);
        await c.ExecuteAsync(new CommandDefinition(
            "UPDATE assistente_acoes SET situacao = @situacao, erro = @erro WHERE id = @id",
            new { id, situacao, erro }, cancellationToken: ct));
    }

    public async Task Auditar(AcaoGuardada a, int usuario, string resultado, string? detalhe, CancellationToken ct)
    {
        await using var c = await source.OpenConnectionAsync(ct);
        await c.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO assistente_auditoria (estabelecimento_id, usuario_id, acao_id, conversa_id, funcionalidade_id, acao, parametros, antes, depois, resultado, detalhe)
            VALUES (@loja, @usuario, @id, @conversa, @funcionalidade, @acao, @parametros::jsonb, @antes, @depois, @resultado, @detalhe)
            """,
            new
            {
                loja = a.EstabelecimentoId, usuario, id = a.Id, conversa = a.ConversaId, funcionalidade = a.FuncionalidadeId, acao = a.Acao,
                parametros = a.Parametros, antes = a.Antes, depois = a.Depois, resultado, detalhe,
            }, cancellationToken: ct));
    }

    public async Task<int> ApagarConversasAntigas(DateTimeOffset antesDe, CancellationToken ct)
    {
        await using var c = await source.OpenConnectionAsync(ct);
        return await c.ExecuteAsync(new CommandDefinition(
            "DELETE FROM assistente_conversas WHERE atualizada_em < @antesDe", new { antesDe }, cancellationToken: ct));
    }
}

internal static class JsonAssistente
{
    public static readonly JsonSerializerOptions Opcoes = new(JsonSerializerDefaults.Web)
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// Lê os cartões guardados. Não usa o discriminador automático do System.Text.Json porque o jsonb do Postgres reordena as chaves
    /// e o "tipo" pode não vir primeiro.
    /// </summary>
    public static List<CartaoDto> LerCartoes(string? json)
    {
        var lista = new List<CartaoDto>();
        if (string.IsNullOrWhiteSpace(json) || System.Text.Json.Nodes.JsonNode.Parse(json) is not System.Text.Json.Nodes.JsonArray arr) return lista;
        foreach (var n in arr.OfType<System.Text.Json.Nodes.JsonObject>())
        {
            CartaoDto? c = n["tipo"]?.GetValue<string>() switch
            {
                "acao" => n.Deserialize<CartaoAcaoDto>(Opcoes),
                "tela" => n.Deserialize<CartaoTelaDto>(Opcoes),
                "chamado" => n.Deserialize<CartaoChamadoDto>(Opcoes),
                _ => null,
            };
            if (c is not null) lista.Add(c);
        }
        return lista;
    }
}
