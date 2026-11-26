using System.Text.Json;
using System.Text.Json.Nodes;
using APIBack.Options;
using APIBack.Service;
using APIBack.Social.Arquivos;
using APIBack.Social.Editor;
using APIBack.Social.Nucleo;
using Dapper;
using Microsoft.Extensions.Options;
using Npgsql;

namespace APIBack.Social.Fila;

public sealed record ParearRequest(string? Codigo, string? DispositivoId, string? Nome, string? Versao, bool UsaGpu, string? Gpu,
    long? EspacoLivre);

public sealed record PareadaDto(string Token, long MaquinaId, string Nome);

public sealed record SinalRequest(string? Versao, bool UsaGpu, string? Gpu, long? EspacoLivre, Guid? TrabalhoId);

public sealed record SinalDto(bool Pausada, IReadOnlyList<Guid> Cancelar);

public sealed record TrabalhoDto(Guid Id, string Tipo, Guid RenderId, int Tentativa, JsonObject Payload);

public sealed record ProgressoRequest(decimal Progresso, int? Quadro, int? Total, decimal? Qps);

public sealed record ContinuarDto(bool Continuar);

public sealed record ResultadoRequest(long Bytes, decimal Duracao, decimal GastoS, int? Quadros, decimal? Qps, string? Aceleracao,
    string? NomeArquivo, string? CaminhoLocal);

public sealed record ConcluirRequest(ResultadoRequest? Resultado);

public sealed record FalharRequest(string? Erro, bool PodeRepetir);

public sealed record VaiRepetirDto(bool VaiRepetir);

/// <summary>Quem está chamando pelo token da máquina.</summary>
public sealed record Maquina(long Id, Guid Loja, int Dono);

/// <summary>A loja bloqueada pela Gestão não recebe trabalho novo.</summary>
public interface ISocialLojaLiberada
{
    Task<bool> Liberada(Guid loja, CancellationToken ct);
}

public sealed class SocialLojaPelaSituacao(ISituacaoComercialService situacoes) : ISocialLojaLiberada
{
    public async Task<bool> Liberada(Guid loja, CancellationToken ct) =>
        (await situacoes.ObterAsync(loja, ct))?.Situacao != SituacoesComerciais.Bloqueado;
}

public interface ISocialWorkerService
{
    Task<PareadaDto> ParearAsync(ParearRequest req, CancellationToken ct);
    Task<Maquina> AutenticarAsync(string? token, CancellationToken ct);
    Task<SinalDto> SinalAsync(Maquina maquina, SinalRequest req, CancellationToken ct);
    /// <summary>Uma tentativa de pegar o próximo trabalho (a espera longa fica no controller).</summary>
    Task<TrabalhoDto?> PegarAsync(Maquina maquina, CancellationToken ct);
    Task<ContinuarDto> ProgressoAsync(Maquina maquina, Guid trabalhoId, ProgressoRequest req, CancellationToken ct);
    Task ConcluirAsync(Maquina maquina, Guid trabalhoId, ConcluirRequest req, CancellationToken ct);
    Task<VaiRepetirDto> FalharAsync(Maquina maquina, Guid trabalhoId, FalharRequest req, CancellationToken ct);
}

/// <summary>
/// O lado da máquina (R14). Toda consulta é presa à loja e ao id da máquina do token: trabalho de outra é 404.
/// </summary>
public sealed class SocialWorkerService(NpgsqlDataSource fonte, IArmazenamento armazenamento, IOptions<SocialMaquinasOptions> opcoes,
    ISocialLojaLiberada loja, ISocialTempoReal? tempoReal = null) : ISocialWorkerService
{
    private sealed class TrabalhoLinha
    {
        public Guid Id { get; set; }
        public Guid RenderId { get; set; }
        public string Status { get; set; } = "";
        public int Tentativas { get; set; }
        public int PedidoPor { get; set; }
        public string Payload { get; set; } = "{}";
    }

    private const string Colunas = """
        id AS "Id", alvo_id AS "RenderId", status AS "Status", tentativas AS "Tentativas", pedido_por_usuario_id AS "PedidoPor",
        payload::text AS "Payload"
        """;

    public async Task<PareadaDto> ParearAsync(ParearRequest req, CancellationToken ct)
    {
        var codigo = FilaRegras.Codigo(req.Codigo);
        var dispositivo = FilaRegras.DispositivoId(req.DispositivoId);
        var nome = FilaRegras.Nome(req.Nome);
        var token = FilaRegras.NovoToken();
        var limite = Math.Max(0, opcoes.Value.LimitePorLoja);
        var (maquinaId, lojaId, nomeFinal) = await SocialBanco.ComoSistema(fonte, ct, async (c, tx) =>
        {
            var par = await c.QuerySingleOrDefaultAsync<(long Id, Guid Loja, int Usuario)?>(new CommandDefinition("""
                SELECT id, estabelecimento_id, gerado_por_usuario_id FROM social_pareamentos
                 WHERE codigo = @codigo AND usado_em IS NULL AND expira_em > NOW() FOR UPDATE
                """, new { codigo }, tx, cancellationToken: ct)) ?? throw FilaRegras.CodigoInvalido();

            var anterior = await c.QuerySingleOrDefaultAsync<(long Id, bool Removida)?>(new CommandDefinition("""
                SELECT id, removida_em IS NOT NULL FROM social_maquinas
                 WHERE estabelecimento_id = @loja AND usuario_id = @usuario AND dispositivo_id = @dispositivo FOR UPDATE
                """, new { loja = par.Loja, usuario = par.Usuario, dispositivo }, tx, cancellationToken: ct));
            if ((anterior is null || anterior.Value.Removida) && await SocialMaquinasService.Usadas(c, tx, par.Loja, ct) >= limite)
                throw FilaRegras.Limite(limite);

            var dados = new
            {
                loja = par.Loja, usuario = par.Usuario, dispositivo, nome, hash = FilaRegras.Hash(token),
                versao = FilaRegras.Curto(req.Versao, 40), usaGpu = req.UsaGpu, gpu = FilaRegras.Curto(req.Gpu, 120),
                espaco = req.EspacoLivre is >= 0 ? req.EspacoLivre : null, id = anterior?.Id ?? 0
            };
            // O mesmo PC do mesmo dono reaproveita a máquina (com o nome que a pessoa já deu, se ela estava na lista).
            var (id, gravado) = await c.QuerySingleAsync<(long, string)>(new CommandDefinition(anterior is null
                ? """
                  INSERT INTO social_maquinas (estabelecimento_id, usuario_id, dispositivo_id, nome, token_hash, versao, usa_gpu, gpu,
                                               espaco_livre, ultimo_sinal_em)
                  VALUES (@loja, @usuario, @dispositivo, @nome, @hash, @versao, @usaGpu, @gpu, @espaco, NOW())
                  RETURNING id, nome
                  """
                : """
                  UPDATE social_maquinas
                     SET nome = CASE WHEN removida_em IS NULL THEN nome ELSE @nome END, token_hash = @hash, versao = @versao,
                         usa_gpu = @usaGpu, gpu = @gpu, espaco_livre = @espaco, ultimo_sinal_em = NOW(), removida_em = NULL, ativa = TRUE
                   WHERE id = @id
                  RETURNING id, nome
                  """, dados, tx, cancellationToken: ct));

            await c.ExecuteAsync(new CommandDefinition(
                "UPDATE social_pareamentos SET usado_em = NOW(), usado_por_maquina_id = @id WHERE id = @par",
                new { id, par = par.Id }, tx, cancellationToken: ct));
            return (id, par.Loja, gravado);
        });
        if (tempoReal is not null) await tempoReal.MaquinaAtualizada(lojaId, maquinaId, ct);
        return new PareadaDto(token, maquinaId, nomeFinal);
    }

    public async Task<Maquina> AutenticarAsync(string? token, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > 200) throw FilaRegras.NaoAutorizada();
        var hash = FilaRegras.Hash(token.Trim());
        var linha = await SocialBanco.ComoSistema(fonte, ct, (c, tx) => c.QuerySingleOrDefaultAsync<(long Id, Guid Loja, int Dono)?>(
            new CommandDefinition("""
                SELECT id, estabelecimento_id, usuario_id FROM social_maquinas WHERE token_hash = @hash AND removida_em IS NULL
                """, new { hash }, tx, cancellationToken: ct)));
        return linha is { } m ? new Maquina(m.Id, m.Loja, m.Dono) : throw FilaRegras.NaoAutorizada();
    }

    public async Task<SinalDto> SinalAsync(Maquina m, SinalRequest req, CancellationToken ct)
    {
        var liberada = await loja.Liberada(m.Loja, ct);
        var (pausada, ligou, cancelar) = await SocialBanco.NaLoja(fonte, m.Loja, ct, async (c, tx) =>
        {
            var antes = await c.QuerySingleAsync<(DateTime? Sinal, bool Ativa, bool Pausada)>(new CommandDefinition("""
                SELECT ultimo_sinal_em, ativa, pausada_por_plano FROM social_maquinas WHERE id = @id AND estabelecimento_id = @loja FOR UPDATE
                """, new { id = m.Id, loja = m.Loja }, tx, cancellationToken: ct));
            await c.ExecuteAsync(new CommandDefinition("""
                UPDATE social_maquinas
                   SET ultimo_sinal_em = NOW(), versao = COALESCE(@versao, versao), usa_gpu = @usaGpu, gpu = @gpu,
                       espaco_livre = COALESCE(@espaco, espaco_livre)
                 WHERE id = @id AND estabelecimento_id = @loja
                """, new
            {
                id = m.Id, loja = m.Loja, versao = FilaRegras.Curto(req.Versao, 40), usaGpu = req.UsaGpu,
                gpu = FilaRegras.Curto(req.Gpu, 120), espaco = req.EspacoLivre is >= 0 ? req.EspacoLivre : null
            }, tx, cancellationToken: ct));

            var sair = new List<Guid>();
            if (req.TrabalhoId is { } t)
            {
                var vivo = await c.ExecuteAsync(new CommandDefinition("""
                    UPDATE social_trabalhos SET sinal_em = NOW()
                     WHERE id = @t AND estabelecimento_id = @loja AND maquina_id = @id AND status = 'rodando'
                    """, new { t, loja = m.Loja, id = m.Id }, tx, cancellationToken: ct));
                if (vivo == 0) sair.Add(t);
            }
            return (!antes.Ativa || antes.Pausada || !liberada, !FilaRegras.EstaOnline(antes.Sinal, DateTime.UtcNow), sair);
        });
        if (ligou && tempoReal is not null) await tempoReal.MaquinaAtualizada(m.Loja, m.Id, ct);
        return new SinalDto(pausada, cancelar);
    }

    public async Task<TrabalhoDto?> PegarAsync(Maquina m, CancellationToken ct)
    {
        if (!await loja.Liberada(m.Loja, ct)) return null;
        var (pego, arquivos, mudaram) = await SocialBanco.NaLoja(fonte, m.Loja, ct, async (c, tx) =>
        {
            // trava a máquina: duas chamadas juntas não podem pegar dois trabalhos
            var maquina = await c.QuerySingleOrDefaultAsync<(bool Ativa, bool Pausada, bool Compartilhada)?>(new CommandDefinition("""
                SELECT ativa, pausada_por_plano, compartilhada_equipe FROM social_maquinas
                 WHERE id = @id AND estabelecimento_id = @loja AND removida_em IS NULL FOR UPDATE
                """, new { id = m.Id, loja = m.Loja }, tx, cancellationToken: ct));
            IReadOnlyList<RenderDto> avisos = await DevolverParados(c, tx, m, ct);
            var nada = ((TrabalhoLinha?)null, new List<ArquivoLinha>(), avisos);
            if (maquina is not { Ativa: true, Pausada: false } dona) return nada;

            // só trabalhos desta máquina, pedidos por quem ainda tem acesso a ela, e um de cada vez
            var linha = await c.QuerySingleOrDefaultAsync<TrabalhoLinha>(new CommandDefinition($"""
                UPDATE social_trabalhos
                   SET status = 'rodando', tentativas = tentativas + 1, progresso = 0, erro = NULL, iniciado_em = NOW(), sinal_em = NOW()
                 WHERE id = (
                       SELECT id FROM social_trabalhos
                        WHERE estabelecimento_id = @loja AND maquina_id = @id AND status = 'pendente'
                          AND (pedido_por_usuario_id = @dono OR @compartilhada)
                          AND NOT EXISTS (SELECT 1 FROM social_trabalhos r
                                           WHERE r.estabelecimento_id = @loja AND r.maquina_id = @id AND r.status = 'rodando')
                        ORDER BY criado_em, id
                        LIMIT 1
                        FOR UPDATE SKIP LOCKED)
                RETURNING {Colunas}
                """, new { loja = m.Loja, id = m.Id, dono = m.Dono, compartilhada = dona.Compartilhada }, tx, cancellationToken: ct));
            if (linha is null) return nada;

            await c.ExecuteAsync(new CommandDefinition("""
                INSERT INTO social_maquina_uso (estabelecimento_id, maquina_id, usuario_id, trabalho_id) VALUES (@loja, @id, @usuario, @t)
                """, new { loja = m.Loja, id = m.Id, usuario = linha.PedidoPor, t = linha.Id }, tx, cancellationToken: ct));

            using var receita = JsonDocument.Parse(JsonNode.Parse(linha.Payload)!["receita"]!.ToJsonString());
            var ids = FilaRegras.ArquivosDaReceita(receita.RootElement);
            var lista = ids.Count == 0 || !armazenamento.Configurado ? new List<ArquivoLinha>()
                : (await c.QueryAsync<ArquivoLinha>(new CommandDefinition("""
                    SELECT id AS "Id", nome AS "Nome", mime AS "Mime", bytes AS "Bytes", r2_key AS "R2Key" FROM social_arquivos
                     WHERE estabelecimento_id = @loja AND id = ANY(@ids) AND enviado_em IS NOT NULL
                    """, new { loja = m.Loja, ids = ids.ToArray() }, tx, cancellationToken: ct))).ToList();
            return ((TrabalhoLinha?)linha, lista, avisos);
        });

        if (tempoReal is not null)
            foreach (var r in mudaram) await tempoReal.RenderAtualizado(m.Loja, r, ct);
        if (pego is null) return null;

        var payload = JsonNode.Parse(pego.Payload)!.AsObject();
        var enviados = new JsonArray();
        foreach (var a in arquivos.Where(a => ArmazenamentoChaves.DaLoja(m.Loja, a.R2Key)))
            enviados.Add(new JsonObject
            {
                ["id"] = a.Id.ToString("D"), ["nome"] = a.Nome, ["mime"] = a.Mime, ["bytes"] = a.Bytes,
                ["urlLer"] = armazenamento.UrlParaLer(a.R2Key, FilaRegras.ValidadeArquivo)
            });
        payload["arquivos"] = enviados;
        return new TrabalhoDto(pego.Id, FilaRegras.TipoRenderizar, pego.RenderId, pego.Tentativas, payload);
    }

    public async Task<ContinuarDto> ProgressoAsync(Maquina m, Guid trabalhoId, ProgressoRequest req, CancellationToken ct)
    {
        // 100 é só no concluir
        var progresso = Math.Min(99m, RendersRegras.Progresso(req.Progresso));
        var render = await SocialBanco.NaLoja(fonte, m.Loja, ct, async (c, tx) =>
        {
            var t = await Ler(c, tx, m, trabalhoId, ct);
            if (t.Status != "rodando") return null;
            await c.ExecuteAsync(new CommandDefinition(
                "UPDATE social_trabalhos SET progresso = @progresso, sinal_em = NOW() WHERE id = @id",
                new { progresso, id = t.Id }, tx, cancellationToken: ct));
            return await RenderDaFila.Progresso(c, tx, m.Loja, t.RenderId, progresso, ct);
        });
        if (render is null) return new ContinuarDto(false);
        if (tempoReal is not null) await tempoReal.RenderAtualizado(m.Loja, render, ct);
        return new ContinuarDto(true);
    }

    public async Task ConcluirAsync(Maquina m, Guid trabalhoId, ConcluirRequest req, CancellationToken ct)
    {
        var r = req.Resultado ?? throw SocialException.Validacao("Informe o resultado.");
        if (r.Bytes is <= 0 or > RendersRegras.LimiteBytes) throw SocialException.Validacao("Tamanho do vídeo inválido.");
        if (r.Duracao is < 0 or > 100_000 || r.GastoS is < 0 or > 10_000_000) throw SocialException.Validacao("Tempo inválido.");
        var nome = NomeOuNada(r.NomeArquivo);
        var resultado = JsonSerializer.Serialize(r with { NomeArquivo = nome, CaminhoLocal = FilaRegras.Curto(r.CaminhoLocal, 500) },
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var render = await SocialBanco.NaLoja(fonte, m.Loja, ct, async (c, tx) =>
        {
            var t = await Ler(c, tx, m, trabalhoId, ct);
            // repetir não muda nada; e o que o painel cancelou continua cancelado
            if (t.Status is "concluido" or "falhou" or "cancelado") return null;
            await c.ExecuteAsync(new CommandDefinition("""
                UPDATE social_trabalhos
                   SET status = 'concluido', progresso = 100, resultado = CAST(@resultado AS jsonb), erro = NULL, concluido_em = NOW(),
                       sinal_em = NOW()
                 WHERE id = @id
                """, new { resultado, id = t.Id }, tx, cancellationToken: ct));
            return await RenderDaFila.Concluir(c, tx, m.Loja, t.RenderId, r.Bytes, r.Duracao, r.GastoS, nome, ct);
        });
        if (render is not null && tempoReal is not null) await tempoReal.RenderAtualizado(m.Loja, render, ct);
    }

    public async Task<VaiRepetirDto> FalharAsync(Maquina m, Guid trabalhoId, FalharRequest req, CancellationToken ct)
    {
        var erro = FilaRegras.Curto(req.Erro, 500) ?? "Falhou sem motivo.";
        var (repetir, renders) = await SocialBanco.NaLoja(fonte, m.Loja, ct, async (c, tx) =>
        {
            var t = await Ler(c, tx, m, trabalhoId, ct);
            if (t.Status != "rodando") return (t.Status == "pendente", (IReadOnlyList<RenderDto>)[]);
            var vai = FilaRegras.VaiRepetir(req.PodeRepetir, t.Tentativas);
            await c.ExecuteAsync(new CommandDefinition("""
                UPDATE social_trabalhos
                   SET status = CASE WHEN @vai THEN 'pendente' ELSE 'falhou' END, erro = @erro, progresso = 0,
                       concluido_em = CASE WHEN @vai THEN NULL ELSE NOW() END
                 WHERE id = @id
                """, new { vai, erro, id = t.Id }, tx, cancellationToken: ct));
            if (!vai) return (vai, await RenderDaFila.Falhar(c, tx, m.Loja, [t.RenderId], erro, ct));
            var voltou = await RenderDaFila.Progresso(c, tx, m.Loja, t.RenderId, 0, ct);
            return (vai, voltou is null ? [] : new[] { voltou });
        });
        if (tempoReal is not null)
            foreach (var r in renders) await tempoReal.RenderAtualizado(m.Loja, r, ct);
        return new VaiRepetirDto(repetir);
    }

    /// <summary>Trabalhos desta máquina parados há mais de 2 min (programa fechado, PC desligado) voltam para a fila.</summary>
    private static async Task<IReadOnlyList<RenderDto>> DevolverParados(NpgsqlConnection c, NpgsqlTransaction tx, Maquina m,
        CancellationToken ct)
    {
        const string motivo = "A máquina parou no meio do vídeo.";
        var parados = (await c.QueryAsync<(Guid Render, string Status)>(new CommandDefinition("""
            UPDATE social_trabalhos
               SET status = CASE WHEN tentativas < @max THEN 'pendente' ELSE 'falhou' END, erro = @motivo, progresso = 0,
                   concluido_em = CASE WHEN tentativas < @max THEN NULL ELSE NOW() END
             WHERE estabelecimento_id = @loja AND maquina_id = @id AND status = 'rodando'
               AND COALESCE(sinal_em, iniciado_em, criado_em) < NOW() - make_interval(secs => @prazo)
            RETURNING alvo_id, status
            """, new
        {
            loja = m.Loja, id = m.Id, motivo, max = FilaRegras.MaxTentativas, prazo = FilaRegras.SemSinal.TotalSeconds
        }, tx, cancellationToken: ct))).ToList();
        var avisos = new List<RenderDto>();
        avisos.AddRange(await RenderDaFila.Falhar(c, tx, m.Loja, parados.Where(p => p.Status == "falhou").Select(p => p.Render).ToArray(),
            motivo, ct));
        foreach (var p in parados.Where(p => p.Status == "pendente"))
            if (await RenderDaFila.Progresso(c, tx, m.Loja, p.Render, 0, ct) is { } r) avisos.Add(r);
        return avisos;
    }

    private static async Task<TrabalhoLinha> Ler(NpgsqlConnection c, NpgsqlTransaction tx, Maquina m, Guid id, CancellationToken ct) =>
        await c.QuerySingleOrDefaultAsync<TrabalhoLinha>(new CommandDefinition(
            $"SELECT {Colunas} FROM social_trabalhos WHERE id = @id AND estabelecimento_id = @loja AND maquina_id = @maquina FOR UPDATE",
            new { id, loja = m.Loja, maquina = m.Id }, tx, cancellationToken: ct))
        ?? throw SocialException.NaoEncontrado("Esse trabalho não é desta máquina.");

    private static string? NomeOuNada(string? nome)
    {
        try { return RendersRegras.NomeArquivo(nome) is { Length: > 0 } n ? n : null; }
        catch (SocialException) { return null; }
    }

    private sealed class ArquivoLinha
    {
        public Guid Id { get; set; }
        public string Nome { get; set; } = "";
        public string Mime { get; set; } = "";
        public long Bytes { get; set; }
        public string R2Key { get; set; } = "";
    }
}
