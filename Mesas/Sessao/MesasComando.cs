using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using APIBack.Mesas.Nucleo;
using Dapper;
using Npgsql;

namespace APIBack.Mesas.Sessao;

/// <summary>Mesa travada (<c>FOR UPDATE</c> em <c>mesa_estado</c>), com o que os comandos precisam.</summary>
public sealed class MesaTravada
{
    public Guid MesaId { get; set; }
    public string Numero { get; set; } = "";
    public string Chave { get; set; } = "";
    public short Capacidade { get; set; }
    public string Status { get; set; } = "";
    public int Versao { get; set; }
    public Guid? SessaoId { get; set; }
    public string Rotulo => "Mesa " + Numero;
}

/// <summary>O garçom recebe <c>AGUARDANDO_GERENTE</c>: a solicitação fica gravada, nada mais muda.</summary>
public sealed class PedeGerenteException(Guid solicitacaoId) : Exception("Pedido enviado ao gerente.")
{
    public Guid SolicitacaoId { get; } = solicitacaoId;
}

/// <summary>
/// Uma execução de comando do Mesas: conexão + transação, quem fez, hora do servidor e o que avisar depois do commit.
/// Use pelo <see cref="MesasComandos.Executar"/>, que cuida da idempotência (<c>mesa_comando</c>).
/// </summary>
public sealed class MesasOperacao
{
    internal MesasOperacao(NpgsqlConnection c, NpgsqlTransaction tx, MesasContexto ctx, string atorNome, Guid chave, string comando,
        DateTime agora, CancellationToken ct)
    {
        C = c; Tx = tx; Ctx = ctx; AtorNome = atorNome; Chave = chave; Comando = comando; Agora = agora; Ct = ct;
    }

    public NpgsqlConnection C { get; }
    public NpgsqlTransaction Tx { get; }
    public MesasContexto Ctx { get; }
    public string AtorNome { get; }
    public Guid Chave { get; }
    public string Comando { get; }
    /// <summary><c>now()</c> da transação: a mesma hora em tudo que o comando grava.</summary>
    public DateTime Agora { get; }
    public CancellationToken Ct { get; }
    public Dictionary<Guid, int> Versoes { get; } = new();
    internal List<Func<IMesasTempoReal, Task>> Avisos { get; } = new();
    private JsonObject? _config;

    private sealed class UltimoRow { public string Nome { get; set; } = ""; public DateTime Quando { get; set; } }
    private sealed class GarcomRow { public string Nome { get; set; } = ""; public string? Funcao { get; set; } }

    public CommandDefinition Sql(string sql, object? args = null) => new(sql, args, Tx, cancellationToken: Ct);

    public void Avisar(Func<IMesasTempoReal, Task> aviso) => Avisos.Add(aviso);

    public async Task<JsonObject> Config()
    {
        if (_config is not null) return _config;
        var dados = await C.ExecuteScalarAsync<string?>(Sql("SELECT dados::text FROM mesas_config WHERE estabelecimento_id = @loja", new { loja = Ctx.Loja }));
        return _config = MesasConfigRegras.Efetiva(dados is null ? null : JsonNode.Parse(dados) as JsonObject);
    }

    public async Task<string?> ConfigTexto(string chave) => MesasConfigRegras.Ler(await Config(), chave) is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    public async Task<int> ConfigInt(string chave, int padrao) =>
        MesasConfigRegras.Ler(await Config(), chave) is JsonValue v && v.TryGetValue<double>(out var d) ? (int)d : padrao;

    /// <summary>Trava as mesas (ordem por id, contra deadlock). Mesa de outra loja ou fora da planta = <c>NAO_ENCONTRADO</c>.</summary>
    public async Task<IReadOnlyList<MesaTravada>> Travar(IEnumerable<Guid> mesaIds)
    {
        var ids = mesaIds.Distinct().OrderBy(x => x).ToArray();
        var rows = (await C.QueryAsync<MesaTravada>(Sql("""
            SELECT e.mesa_id AS mesaId, m.numero, m.chave_planta AS chave, m.capacidade, e.status, e.versao, e.sessao_id AS sessaoId
              FROM mesa_estado e JOIN mesa m ON m.id = e.mesa_id
             WHERE e.estabelecimento_id = @loja AND e.mesa_id = ANY(@ids) AND m.ativa
             ORDER BY e.mesa_id
               FOR UPDATE OF e
            """, new { loja = Ctx.Loja, ids }))).ToList();
        if (rows.Count != ids.Length) throw MesasException.NaoEncontrado("Mesa não encontrada.");
        return rows;
    }

    public async Task<MesaTravada> Travar(Guid mesaId) => (await Travar(new[] { mesaId }))[0];

    /// <summary>Trava todas as mesas da sessão (a principal e as juntadas).</summary>
    public async Task<IReadOnlyList<MesaTravada>> TravarSessao(Guid sessaoId)
    {
        var ids = await C.QueryAsync<Guid>(Sql("SELECT mesa_id FROM mesa_sessao_mesa WHERE sessao_id = @sessaoId AND ate IS NULL", new { sessaoId }));
        return await Travar(ids);
    }

    /// <summary><c>VERSAO_VELHA</c> (409) com o estado atual e quem mexeu por último.</summary>
    public async Task ConferirVersao(MesaTravada m, int? versao)
    {
        if (versao is null) throw MesasException.Validacao("Envie a versão da mesa.");
        if (versao == m.Versao) return;
        var ultimo = await C.QueryFirstOrDefaultAsync<UltimoRow>(Sql("""
            SELECT ator_nome AS nome, quando FROM mesa_evento WHERE estabelecimento_id = @loja AND mesa_id = @mesaId ORDER BY quando DESC LIMIT 1
            """, new { loja = Ctx.Loja, mesaId = m.MesaId }));
        throw new MesasException("VERSAO_VELHA", $"A {m.Rotulo} mudou. Confira de novo.", 409)
        {
            Detalhes = new
            {
                conflito = true, mesaId = m.MesaId, versaoAtual = m.Versao, status = m.Status, sessaoId = m.SessaoId,
                porQuem = ultimo?.Nome, quando = ultimo?.Quando
            }
        };
    }

    /// <summary>Muda o estado da mesa e sobe a versão (toda resposta leva as versões novas).</summary>
    public async Task Mudar(MesaTravada m, string status, Guid? sessaoId)
    {
        var v = await C.ExecuteScalarAsync<int>(Sql("""
            UPDATE mesa_estado SET status = @status, sessao_id = @sessaoId, motivo_bloqueio = NULL, versao = versao + 1, atualizado_em = NOW()
             WHERE mesa_id = @mesaId RETURNING versao
            """, new { mesaId = m.MesaId, status, sessaoId }));
        m.Status = status; m.SessaoId = sessaoId; m.Versao = v;
        Versoes[m.MesaId] = v;
        var loja = Ctx.Loja;
        Avisar(t => t.MesaMudou(loja, m.MesaId, v));
    }

    /// <summary>Só sobe a versão (pessoas, chamados e anotações mudam a leitura da mesa).</summary>
    public Task Tocar(MesaTravada m) => Mudar(m, m.Status, m.SessaoId);

    public async Task TocarTodas(IEnumerable<MesaTravada> mesas) { foreach (var m in mesas) await Tocar(m); }

    /// <summary>Linha da auditoria (imutável). Quem fez = a pessoa logada; o papel vem do <see cref="MesasContexto"/>.</summary>
    public async Task<Guid> Evento(string acao, string alvoTipo, Guid? alvoId, string? rotulo, string? antes = null, string? depois = null,
        string? motivo = null, Guid? mesaId = null, Guid? sessaoId = null, bool ocorrencia = false)
    {
        var id = Guid.NewGuid();
        await C.ExecuteAsync(Sql("""
            INSERT INTO mesa_evento (id, estabelecimento_id, quando, ator_id, ator_nome, ator_papel, acao, alvo_tipo, alvo_id, alvo_rotulo,
                                     antes, depois, motivo, mesa_id, sessao_id, ocorrencia, comando_chave)
            VALUES (@id, @loja, @quando, @atorId, @atorNome, @papel, @acao, @alvoTipo, @alvoId, @rotulo, @antes, @depois, @motivo, @mesaId,
                    @sessaoId, @ocorrencia, @chave)
            """, new
        {
            id, loja = Ctx.Loja, quando = Agora, atorId = Ctx.Usuario.ToString(), atorNome = AtorNome, papel = Ctx.Papel, acao, alvoTipo, alvoId,
            rotulo, antes, depois, motivo, mesaId, sessaoId, ocorrencia, chave = Chave
        }));
        return id;
    }

    /// <summary>Pessoa da Equipe ligada a quem está logado (o garçom do app).</summary>
    public Task<Guid?> MinhaPessoa() => C.QueryFirstOrDefaultAsync<Guid?>(Sql("""
        SELECT id FROM gestao_pessoas WHERE estabelecimento_id = @loja AND usuario_id = @usuario AND ativo ORDER BY atualizado_em DESC LIMIT 1
        """, new { loja = Ctx.Loja, usuario = Ctx.Usuario }));

    public Task<bool> PontoAberto(Guid pessoaId) => C.ExecuteScalarAsync<bool>(Sql("""
        SELECT EXISTS (SELECT 1 FROM gestao_ponto WHERE estabelecimento_id = @loja AND pessoa_id = @pessoaId AND saida IS NULL)
        """, new { loja = Ctx.Loja, pessoaId }));

    /// <summary>Garçom com ponto aberto agora (§6.1: garçom só opera com ponto). Devolve o nome.</summary>
    public async Task<string> GarcomComPonto(Guid pessoaId)
    {
        var g = await C.QueryFirstOrDefaultAsync<GarcomRow>(Sql("""
            SELECT nome::text AS nome, funcao_codigo AS funcao FROM gestao_pessoas WHERE estabelecimento_id = @loja AND id = @pessoaId AND ativo
            """, new { loja = Ctx.Loja, pessoaId }));
        if (g is null) throw MesasException.NaoEncontrado("Garçom não encontrado.");
        if (g.Funcao is not ("garcom" or "gerente_salao")) throw MesasException.Validacao($"{SessaoRegras.PrimeiroNome(g.Nome)} não é garçom.");
        if (!await PontoAberto(pessoaId))
            throw new MesasException("SEM_PONTO", $"{SessaoRegras.PrimeiroNome(g.Nome)} não bateu o ponto.", 409) { Detalhes = new { garcomId = pessoaId } };
        return g.Nome;
    }

    /// <summary>Garçom logado precisa de ponto aberto para operar. Devolve a pessoa dele (ou null para quem não é garçom).</summary>
    public async Task<Guid?> ExigirPontoSeGarcom()
    {
        if (Ctx.Papel != MesasPermissoes.PapelGarcom) return await MinhaPessoa();
        var eu = await MinhaPessoa() ?? throw MesasException.SemPermissao("Sua conta não está na equipe desta loja.");
        if (!await PontoAberto(eu)) throw new MesasException("SEM_PONTO", "Bata o ponto para começar.", 409);
        return eu;
    }

    /// <summary>
    /// Autonomia do garçom (§5). <c>motivo</c> sem motivo → <c>EXIGE_MOTIVO</c>; <c>gerente</c> → grava a solicitação e devolve
    /// <c>AGUARDANDO_GERENTE</c> (202). Chame antes de mudar qualquer coisa.
    /// </summary>
    public async Task Autonomia(string regra, string? motivo, Guid? mesaId, Guid? sessaoId, string detalhe, object corpo)
    {
        var valor = await ConfigTexto("autonomiaGarcom.regras." + regra);
        switch (SessaoRegras.Decidir(Ctx.Papel, valor, !string.IsNullOrWhiteSpace(motivo)))
        {
            case SessaoRegras.Autonomia.ExigeMotivo:
                throw new MesasException("EXIGE_MOTIVO", "Diga o motivo.", 422);
            case SessaoRegras.Autonomia.PedeGerente:
                var acao = JsonSerializer.Serialize(new { comando = Comando, corpo }, MesasComandos.Json);
                var ja = await C.QueryFirstOrDefaultAsync<Guid?>(Sql("""
                    SELECT id FROM mesa_solicitacao
                     WHERE estabelecimento_id = @loja AND estado = 'pendente' AND tipo = @regra AND pedido_por = @por
                       AND mesa_id IS NOT DISTINCT FROM @mesaId AND acao->>'comando' = @comando
                     LIMIT 1
                    """, new { loja = Ctx.Loja, regra, por = Ctx.Usuario.ToString(), mesaId, comando = Comando }));
                if (ja is Guid existente) throw new PedeGerenteException(existente);
                var id = Guid.NewGuid();
                await C.ExecuteAsync(Sql("""
                    INSERT INTO mesa_solicitacao (id, estabelecimento_id, tipo, mesa_id, sessao_id, detalhe, acao, pedido_por, pedido_por_nome,
                                                  motivo, comando_chave)
                    VALUES (@id, @loja, @regra, @mesaId, @sessaoId, @detalhe, CAST(@acao AS jsonb), @por, @nome, @motivo, @chave)
                    """, new { id, loja = Ctx.Loja, regra, mesaId, sessaoId, detalhe, acao, por = Ctx.Usuario.ToString(), nome = AtorNome,
                    motivo, chave = Chave }));
                await Evento("pedirAutorizacao", "solicitacao", id, detalhe, depois: "Esperando o gerente", motivo: motivo, mesaId: mesaId, sessaoId: sessaoId);
                var loja = Ctx.Loja;
                Avisar(t => t.Aviso(loja, "autorizacao", id, mesaId, null));
                Avisar(t => t.SolicitacaoMudou(loja, id, "pendente"));
                throw new PedeGerenteException(id);
        }
    }
}

/// <summary>
/// Executor dos comandos do Mesas (CONTRATO-API §0.2). Mesma <c>chave</c> + mesmo corpo devolve a resposta gravada, sem refazer;
/// mesma chave com corpo diferente = <c>CHAVE_REUSADA</c>. Erro desfaz tudo (inclusive a reserva da chave), então reenviar refaz.
/// </summary>
public sealed class MesasComandos(NpgsqlDataSource source, IMesasTempoReal tempoReal)
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private sealed class ComandoRow
    {
        public string Comando { get; set; } = "";
        public string Hash { get; set; } = "";
        public string? Resposta { get; set; }
        public short Status { get; set; }
    }

    public async Task<T> Executar<T>(MesasContexto ctx, string comando, Guid? chave, object corpo, Func<MesasOperacao, Task<T>> fazer, CancellationToken ct)
    {
        if (chave is not Guid k || k == Guid.Empty) throw MesasException.Validacao("Envie a chave do comando.");
        var hash = Hash(comando, corpo);

        await using var c = await source.OpenConnectionAsync(ct);
        await using var tx = await c.BeginTransactionAsync(ct);
        // Reserva a chave. Se outro pedido com a mesma chave está no meio, o INSERT espera ele terminar.
        var reservada = await c.ExecuteScalarAsync<Guid?>(new CommandDefinition("""
            INSERT INTO mesa_comando (id, estabelecimento_id, chave, comando, hash, status, usuario_id)
            VALUES (@id, @loja, @k, @comando, @hash, 0, @usuario)
            ON CONFLICT (estabelecimento_id, chave) DO NOTHING
            RETURNING id
            """, new { id = Guid.NewGuid(), loja = ctx.Loja, k, comando, hash, usuario = ctx.Usuario }, tx, cancellationToken: ct));
        if (reservada is null)
        {
            var row = await c.QuerySingleAsync<ComandoRow>(new CommandDefinition(
                "SELECT comando, hash, resposta::text AS resposta, status FROM mesa_comando WHERE estabelecimento_id = @loja AND chave = @k",
                new { loja = ctx.Loja, k }, tx, cancellationToken: ct));
            await tx.RollbackAsync(ct);
            if (row.Comando != comando || row.Hash != hash)
                throw new MesasException("CHAVE_REUSADA", "Essa chave já foi usada em outro pedido.", 422);
            if (row.Status == 202)
                throw Aguardando(JsonNode.Parse(row.Resposta ?? "{}")?["solicitacaoId"]?.GetValue<Guid>() ?? Guid.Empty);
            return JsonSerializer.Deserialize<T>(row.Resposta ?? "null", Json)!;
        }

        var agora = await c.ExecuteScalarAsync<DateTime>(new CommandDefinition("SELECT NOW()", transaction: tx, cancellationToken: ct));
        var op = new MesasOperacao(c, tx, ctx, await Ator(c, tx, ctx, ct), k, comando, agora, ct);
        T resultado;
        try
        {
            resultado = await fazer(op);
        }
        catch (PedeGerenteException pg)
        {
            await Gravar(c, tx, ctx, k, 202, new { solicitacaoId = pg.SolicitacaoId }, ct);
            await tx.CommitAsync(ct);
            await Publicar(op);
            throw Aguardando(pg.SolicitacaoId);
        }
        await Gravar(c, tx, ctx, k, 200, resultado, ct);
        await tx.CommitAsync(ct);
        await Publicar(op);
        return resultado;
    }

    private static MesasException Aguardando(Guid solicitacaoId) =>
        new("AGUARDANDO_GERENTE", "Pedido enviado ao gerente.", 202) { Detalhes = new { solicitacaoId } };

    private static Task Gravar(NpgsqlConnection c, NpgsqlTransaction tx, MesasContexto ctx, Guid k, short status, object? resposta, CancellationToken ct) =>
        c.ExecuteAsync(new CommandDefinition("""
            UPDATE mesa_comando SET status = @status, resposta = CAST(@resposta AS jsonb), quando = NOW()
             WHERE estabelecimento_id = @loja AND chave = @k
            """, new { status, resposta = JsonSerializer.Serialize(resposta, Json), loja = ctx.Loja, k }, tx, cancellationToken: ct));

    private async Task Publicar(MesasOperacao op)
    {
        foreach (var aviso in op.Avisos) await aviso(tempoReal);
    }

    public static string Hash(string comando, object corpo)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(comando + "\n" + JsonSerializer.Serialize(corpo, corpo.GetType(), Json)));
        return Convert.ToHexString(bytes);
    }

    private static async Task<string> Ator(NpgsqlConnection c, NpgsqlTransaction tx, MesasContexto ctx, CancellationToken ct)
    {
        var nome = await c.ExecuteScalarAsync<string?>(new CommandDefinition(
            "SELECT nome::text FROM usuario WHERE id = @id", new { id = ctx.Usuario }, tx, cancellationToken: ct));
        nome = string.IsNullOrWhiteSpace(nome) ? "Equipe" : nome.Trim();
        return nome.Length <= 80 ? nome : nome[..80];
    }
}
