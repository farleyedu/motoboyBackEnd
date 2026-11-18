using System.Text.Json.Nodes;
using Dapper;
using Npgsql;

namespace APIBack.Mesas.Nucleo;

public sealed class MesasException(string codigo, string mensagem, int status) : Exception(mensagem)
{
    public string Codigo { get; } = codigo;
    public int Status { get; } = status;
    /// <summary>Vai em <c>details</c> da resposta (P05: erros por item da planta, quem mudou o rascunho).</summary>
    public object? Detalhes { get; init; }

    public static MesasException SemPermissao(string mensagem = "Você não pode fazer isso.") => new("SEM_PERMISSAO", mensagem, 403);
    public static MesasException Desligado() => new("MESAS_DESLIGADO", "O Mesas está desligado nesta loja.", 403);
    public static MesasException Validacao(string mensagem) => new("VALIDACAO", mensagem, 400);
    public static MesasException NaoEncontrado(string mensagem) => new("NAO_ENCONTRADO", mensagem, 404);
}

public sealed record MesasConfigDto(bool Ativo, JsonObject Dados, int Versao);
public sealed record MesasConfigurarRequest(string? Chave, JsonNode? Valor, string? Rotulo);
public sealed record MesasConfigHistoricoDto(Guid Id, string Chave, string? Antes, string? Depois, string? Rotulo, int Versao, int? Por, string? PorNome, DateTime Quando);
public sealed record MesasPessoaFuncaoDto(Guid Id, string Nome, string Funcao, string Vinculo, string? FuncaoCodigo, int? UsuarioId, bool Ativo);
public sealed record MesasDefinirFuncaoRequest(string? FuncaoCodigo);
public sealed record MinhaLojaFuncoesDto(Guid EstabelecimentoId, string Nome, IReadOnlyList<string> Funcoes, Guid? PessoaId, bool MesasContratado, bool MesasLigado);

public interface IMesasNucleoService
{
    Task<string?> FuncaoDoUsuarioAsync(Guid loja, int usuario, CancellationToken ct);
    Task<bool> LigadoAsync(Guid loja, CancellationToken ct);
    Task<MesasConfigDto> ConfigAsync(Guid loja, CancellationToken ct);
    Task<MesasConfigDto> ConfigurarAsync(Guid loja, int usuario, string papel, MesasConfigurarRequest req, CancellationToken ct);
    Task<IReadOnlyList<MesasConfigHistoricoDto>> HistoricoAsync(Guid loja, int limite, CancellationToken ct);
    Task<IReadOnlyList<MesasPessoaFuncaoDto>> EquipeAsync(Guid loja, CancellationToken ct);
    Task<MesasPessoaFuncaoDto> DefinirFuncaoAsync(Guid loja, int usuario, Guid pessoaId, string? funcao, CancellationToken ct);
    Task<IReadOnlyList<MinhaLojaFuncoesDto>> MinhasFuncoesAsync(int usuario, CancellationToken ct);
}

/// <summary>Configuração do Mesas, funções da equipe e funções da conta (P01).</summary>
public sealed class MesasNucleoService(NpgsqlDataSource source) : IMesasNucleoService
{
    private const string PessoaColunas = """
        id AS "Id", nome::text AS "Nome", funcao::text AS "Funcao", vinculo::text AS "Vinculo", funcao_codigo AS "FuncaoCodigo",
        usuario_id AS "UsuarioId", ativo AS "Ativo"
        """;

    private sealed class ConfigRow
    {
        public bool Ativo { get; set; }
        public string Dados { get; set; } = "{}";
        public int Versao { get; set; }
    }

    private sealed class FuncaoRow
    {
        public Guid EstabelecimentoId { get; set; }
        public string Nome { get; set; } = string.Empty;
        public string Funcao { get; set; } = string.Empty;
        public Guid? PessoaId { get; set; }
        public bool MesasContratado { get; set; }
        public bool MesasLigado { get; set; }
    }

    public async Task<string?> FuncaoDoUsuarioAsync(Guid loja, int usuario, CancellationToken ct)
    {
        await using var c = await source.OpenConnectionAsync(ct);
        return await c.QueryFirstOrDefaultAsync<string?>(new CommandDefinition("""
            SELECT funcao_codigo FROM gestao_pessoas
             WHERE estabelecimento_id = @loja AND usuario_id = @usuario AND ativo AND funcao_codigo IS NOT NULL
             ORDER BY atualizado_em DESC LIMIT 1
            """, new { loja, usuario }, cancellationToken: ct));
    }

    public async Task<bool> LigadoAsync(Guid loja, CancellationToken ct)
    {
        await using var c = await source.OpenConnectionAsync(ct);
        return await c.ExecuteScalarAsync<bool?>(new CommandDefinition(
            "SELECT ativo FROM mesas_config WHERE estabelecimento_id = @loja", new { loja }, cancellationToken: ct)) ?? false;
    }

    public async Task<MesasConfigDto> ConfigAsync(Guid loja, CancellationToken ct)
    {
        await using var c = await source.OpenConnectionAsync(ct);
        var row = await c.QueryFirstOrDefaultAsync<ConfigRow>(new CommandDefinition(
            "SELECT ativo, dados::text AS dados, versao FROM mesas_config WHERE estabelecimento_id = @loja", new { loja }, cancellationToken: ct));
        return Dto(row);
    }

    public async Task<MesasConfigDto> ConfigurarAsync(Guid loja, int usuario, string papel, MesasConfigurarRequest req, CancellationToken ct)
    {
        var chave = req.Chave?.Trim();
        if (MesasConfigRegras.Validar(chave, req.Valor) is { } erro) throw MesasException.Validacao(erro);
        if (chave == MesasConfigRegras.ChaveAtivo && !MesasPermissoes.PodeLigar(papel))
            throw MesasException.SemPermissao("Só o dono liga ou desliga o Mesas.");
        var rotulo = string.IsNullOrWhiteSpace(req.Rotulo) ? null : req.Rotulo.Trim();
        if (rotulo is { Length: > 200 }) throw MesasException.Validacao("Descrição longa demais.");

        await using var c = await source.OpenConnectionAsync(ct);
        await using var tx = await c.BeginTransactionAsync(ct);
        await c.ExecuteAsync(new CommandDefinition(
            "INSERT INTO mesas_config (estabelecimento_id) VALUES (@loja) ON CONFLICT (estabelecimento_id) DO NOTHING",
            new { loja }, tx, cancellationToken: ct));
        var row = await c.QuerySingleAsync<ConfigRow>(new CommandDefinition(
            "SELECT ativo, dados::text AS dados, versao FROM mesas_config WHERE estabelecimento_id = @loja FOR UPDATE",
            new { loja }, tx, cancellationToken: ct));

        var gravado = (JsonObject?)JsonNode.Parse(row.Dados) ?? new JsonObject();
        JsonNode? antes;
        if (chave == MesasConfigRegras.ChaveAtivo)
        {
            antes = JsonValue.Create(row.Ativo);
        }
        else
        {
            antes = MesasConfigRegras.Ler(MesasConfigRegras.Efetiva(gravado), chave!)?.DeepClone();
        }

        // Mesmo valor: não sobe versão nem cria histórico (repetir é seguro).
        if (MesasConfigRegras.Iguais(antes, req.Valor))
        {
            await tx.CommitAsync(ct);
            return Dto(row);
        }

        var novaVersao = row.Versao + 1;
        if (chave == MesasConfigRegras.ChaveAtivo)
        {
            await c.ExecuteAsync(new CommandDefinition("""
                UPDATE mesas_config SET ativo = @ativo, versao = @novaVersao, atualizado_por = @usuario, atualizado_em = NOW()
                 WHERE estabelecimento_id = @loja
                """, new { loja, ativo = req.Valor!.GetValue<bool>(), novaVersao, usuario }, tx, cancellationToken: ct));
        }
        else
        {
            MesasConfigRegras.Gravar(gravado, chave!, req.Valor);
            await c.ExecuteAsync(new CommandDefinition("""
                UPDATE mesas_config SET dados = CAST(@dados AS jsonb), versao = @novaVersao, atualizado_por = @usuario, atualizado_em = NOW()
                 WHERE estabelecimento_id = @loja
                """, new { loja, dados = gravado.ToJsonString(), novaVersao, usuario }, tx, cancellationToken: ct));
        }

        await c.ExecuteAsync(new CommandDefinition("""
            INSERT INTO mesas_config_historico (id, estabelecimento_id, chave, antes, depois, rotulo, versao, por)
            VALUES (@id, @loja, @chave, CAST(@antes AS jsonb), CAST(@depois AS jsonb), @rotulo, @novaVersao, @usuario)
            """, new
        {
            id = Guid.NewGuid(), loja, chave, antes = antes?.ToJsonString(), depois = req.Valor?.ToJsonString(),
            rotulo, novaVersao, usuario
        }, tx, cancellationToken: ct));
        await tx.CommitAsync(ct);

        return await ConfigAsync(loja, ct);
    }

    public async Task<IReadOnlyList<MesasConfigHistoricoDto>> HistoricoAsync(Guid loja, int limite, CancellationToken ct)
    {
        await using var c = await source.OpenConnectionAsync(ct);
        var rows = await c.QueryAsync<MesasConfigHistoricoDto>(new CommandDefinition("""
            SELECT h.id AS "Id", h.chave AS "Chave", h.antes::text AS "Antes", h.depois::text AS "Depois", h.rotulo AS "Rotulo",
                   h.versao AS "Versao", h.por AS "Por", u.nome::text AS "PorNome", h.quando AS "Quando"
              FROM mesas_config_historico h
              LEFT JOIN usuario u ON u.id = h.por
             WHERE h.estabelecimento_id = @loja
             ORDER BY h.quando DESC
             LIMIT @limite
            """, new { loja, limite = Math.Clamp(limite, 1, 200) }, cancellationToken: ct));
        return rows.ToList();
    }

    public async Task<IReadOnlyList<MesasPessoaFuncaoDto>> EquipeAsync(Guid loja, CancellationToken ct)
    {
        await using var c = await source.OpenConnectionAsync(ct);
        var rows = await c.QueryAsync<MesasPessoaFuncaoDto>(new CommandDefinition($"""
            SELECT {PessoaColunas}
              FROM gestao_pessoas
             WHERE estabelecimento_id = @loja AND ativo
             ORDER BY nome
            """, new { loja }, cancellationToken: ct));
        return rows.ToList();
    }

    public async Task<MesasPessoaFuncaoDto> DefinirFuncaoAsync(Guid loja, int usuario, Guid pessoaId, string? funcao, CancellationToken ct)
    {
        var codigo = string.IsNullOrWhiteSpace(funcao) ? null : funcao.Trim();
        if (codigo is not null && !MesasPermissoes.FuncaoValida(codigo)) throw MesasException.Validacao("Escolha uma função da lista.");

        await using var c = await source.OpenConnectionAsync(ct);
        // Não mexe em 'versao' da pessoa: a tela da Equipe usa a versão para o cadastro (nome, vínculo, regra).
        var pessoa = await c.QueryFirstOrDefaultAsync<MesasPessoaFuncaoDto>(new CommandDefinition($"""
            UPDATE gestao_pessoas SET funcao_codigo = @codigo, atualizado_por = @usuario, atualizado_em = NOW()
             WHERE estabelecimento_id = @loja AND id = @pessoaId
            RETURNING {PessoaColunas}
            """, new { loja, pessoaId, codigo, usuario }, cancellationToken: ct));
        return pessoa ?? throw MesasException.NaoEncontrado("Pessoa não encontrada.");
    }

    public async Task<IReadOnlyList<MinhaLojaFuncoesDto>> MinhasFuncoesAsync(int usuario, CancellationToken ct)
    {
        await using var c = await source.OpenConnectionAsync(ct);
        // Funções do salão (gestao_pessoas) + motoboy pelos vínculos que já existem (motoboy_estabelecimento ativo).
        var rows = await c.QueryAsync<FuncaoRow>(new CommandDefinition("""
            WITH funcoes AS (
                SELECT p.estabelecimento_id, p.funcao_codigo AS funcao, p.id AS pessoa_id
                  FROM gestao_pessoas p
                 WHERE p.usuario_id = @usuario AND p.ativo AND p.funcao_codigo IS NOT NULL
                UNION
                SELECT me.estabelecimento_id, 'motoboy', NULL::uuid
                  FROM motoboy m
                  JOIN motoboy_estabelecimento me ON me.motoboy_id = m.canonical_motoboy_id
                 WHERE m.id_usuario = @usuario AND me.ativo = TRUE
            )
            SELECT f.estabelecimento_id AS "EstabelecimentoId", COALESCE(e.nome_fantasia::text, '') AS "Nome", f.funcao AS "Funcao",
                   f.pessoa_id AS "PessoaId",
                   COALESCE('MESAS' = ANY (e.modulos_ativos::text[]), FALSE) AS "MesasContratado",
                   COALESCE(mc.ativo, FALSE) AS "MesasLigado"
              FROM funcoes f
              JOIN estabelecimentos e ON e.id = f.estabelecimento_id
              LEFT JOIN mesas_config mc ON mc.estabelecimento_id = f.estabelecimento_id
             ORDER BY 2, 3
            """, new { usuario }, cancellationToken: ct));
        return Agrupar(rows);
    }

    private static List<MinhaLojaFuncoesDto> Agrupar(IEnumerable<FuncaoRow> rows) => rows
        .GroupBy(r => r.EstabelecimentoId)
        .Select(g => new MinhaLojaFuncoesDto(g.Key, g.First().Nome,
            g.Select(r => r.Funcao).Distinct(StringComparer.Ordinal).ToList(),
            g.Select(r => r.PessoaId).FirstOrDefault(id => id.HasValue),
            g.First().MesasContratado, g.First().MesasLigado))
        .ToList();

    private static MesasConfigDto Dto(ConfigRow? row) => new(
        row?.Ativo ?? false,
        MesasConfigRegras.Efetiva(row is null ? null : (JsonObject?)JsonNode.Parse(row.Dados)),
        row?.Versao ?? 0);
}
