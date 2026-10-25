using Dapper;
using Npgsql;

namespace APIBack.Service
{
    public sealed class ChamadoDto
    {
        public long Id { get; set; }
        public Guid EstabelecimentoId { get; set; }
        public string Loja { get; set; } = string.Empty;
        public string Categoria { get; set; } = string.Empty;
        public string Titulo { get; set; } = string.Empty;
        public string Status { get; set; } = "aberto";
        public DateTimeOffset PrazoEm { get; set; }
        public DateTimeOffset? PrimeiraRespostaEm { get; set; }
        public DateTimeOffset? ResolvidoEm { get; set; }
        public short? Nota { get; set; }
        public DateTimeOffset CriadoEm { get; set; }
        public string? AbertoPor { get; set; }
        public string? UltimaMensagem { get; set; }
        public int Mensagens { get; set; }
    }

    public sealed class ChamadoMensagemDto
    {
        public long Id { get; set; }
        public bool DaZippy { get; set; }
        public string Texto { get; set; } = string.Empty;
        public string? Autor { get; set; }
        public DateTimeOffset CriadoEm { get; set; }
        public IReadOnlyList<ChamadoAnexoDto> Anexos { get; set; } = [];
    }

    public sealed record AbrirChamadoRequest(string? Categoria, string? Texto);
    public sealed record ResponderChamadoRequest(string? Texto);
    public sealed record StatusChamadoRequest(string? Status);
    public sealed record NotaChamadoRequest(short Nota);

    public sealed class ChamadoException(int status, string message) : Exception(message)
    {
        public int Status { get; } = status;
    }

    public interface IChamadosService
    {
        Task<ChamadoDto> AbrirAsync(Guid loja, int usuarioId, AbrirChamadoRequest request, IReadOnlyList<ArquivoEnviado>? arquivos = null, CancellationToken ct = default);
        Task<IReadOnlyList<ChamadoDto>> ListarAsync(Guid? loja, CancellationToken ct = default);
        Task<(ChamadoDto Chamado, IReadOnlyList<ChamadoMensagemDto> Mensagens)> ObterAsync(long id, Guid? loja, CancellationToken ct = default);
        Task ResponderAsync(long id, Guid? loja, int usuarioId, bool daZippy, string texto, IReadOnlyList<ArquivoEnviado>? arquivos = null, CancellationToken ct = default);
        Task MudarStatusAsync(long id, string status, CancellationToken ct = default);
        Task AvaliarAsync(long id, Guid loja, short nota, CancellationToken ct = default);
    }

    /// <summary>Chamados entre a loja e a Zippy. A loja so enxerga os dela (loja informada); a Gestao enxerga todos (loja nula).</summary>
    public sealed class ChamadosService(NpgsqlDataSource dataSource, IChamadoAnexoService anexos) : IChamadosService
    {
        private static readonly string[] Categorias = ["parou", "duvida", "pagamento", "ideia"];
        private static readonly string[] Status = ["aberto", "andamento", "loja", "resolvido"];

        private sealed class AnexoLinha
        {
            public long Id { get; set; }
            public long MensagemId { get; set; }
            public string Url { get; set; } = string.Empty;
            public string Nome { get; set; } = string.Empty;
            public string Tipo { get; set; } = string.Empty;
            public int Bytes { get; set; }
        }

        private const string Select = @"
SELECT c.id, c.estabelecimento_id AS EstabelecimentoId, e.nome_fantasia AS Loja, c.categoria, c.titulo, c.status, c.prazo_em AS PrazoEm,
       c.primeira_resposta_em AS PrimeiraRespostaEm, c.resolvido_em AS ResolvidoEm, c.nota, c.criado_em AS CriadoEm, u.nome AS AbertoPor,
       (SELECT COALESCE(NULLIF(m.texto, ''), '📷 Imagem') FROM chamado_mensagem m WHERE m.chamado_id = c.id ORDER BY m.criado_em DESC, m.id DESC LIMIT 1) AS UltimaMensagem,
       (SELECT COUNT(*) FROM chamado_mensagem m WHERE m.chamado_id = c.id)::int AS Mensagens
  FROM chamado c
  JOIN estabelecimentos e ON e.id = c.estabelecimento_id
  LEFT JOIN usuario u ON u.id = c.aberto_por";

        public async Task<ChamadoDto> AbrirAsync(Guid loja, int usuarioId, AbrirChamadoRequest request, IReadOnlyList<ArquivoEnviado>? arquivos = null, CancellationToken ct = default)
        {
            var categoria = request.Categoria?.Trim().ToLowerInvariant() ?? string.Empty;
            var texto = request.Texto?.Trim();
            if (!Categorias.Contains(categoria)) throw new ChamadoException(422, "Escolha sobre o que é o chamado.");
            if (string.IsNullOrEmpty(texto) || texto.Length < 5) throw new ChamadoException(422, "Conte o que aconteceu.");
            var prontos = await anexos.PrepararAsync(arquivos, ct);
            // O titulo e a primeira linha, curta: e o que a Gestao le na fila.
            var titulo = texto.Split('\n')[0];
            if (titulo.Length > 90) titulo = titulo[..87] + "…";
            var horas = categoria == "parou" ? 4 : 24;
            await using var connection = await dataSource.OpenConnectionAsync(ct);
            // O numero do chamado e reservado antes para nomear a pasta dos prints. Os arquivos sobem antes de qualquer
            // gravacao: se o upload falhar nada foi criado; se o banco falhar, os arquivos sao apagados.
            var id = await connection.ExecuteScalarAsync<long>(new CommandDefinition("SELECT nextval(pg_get_serial_sequence('chamado', 'id'));", cancellationToken: ct));
            var salvos = await anexos.EnviarAsync(loja, id, prontos, ct);
            try
            {
                await using var tx = await connection.BeginTransactionAsync(ct);
                await connection.ExecuteAsync(new CommandDefinition(@"
INSERT INTO chamado (id, estabelecimento_id, aberto_por, categoria, titulo, prazo_em)
VALUES (@Id, @Loja, @Usuario, @Categoria, @Titulo, NOW() + make_interval(hours => @Horas));",
                    new { Id = id, Loja = loja, Usuario = usuarioId, Categoria = categoria, Titulo = titulo, Horas = horas }, tx, cancellationToken: ct));
                await GravarMensagemAsync(connection, tx, id, usuarioId, false, texto, salvos, ct);
                var dto = await connection.QuerySingleAsync<ChamadoDto>(new CommandDefinition(Select + " WHERE c.id = @Id;", new { Id = id }, tx, cancellationToken: ct));
                await tx.CommitAsync(ct);
                return dto;
            }
            catch
            {
                await anexos.DescartarAsync(salvos);
                throw;
            }
        }

        private static async Task GravarMensagemAsync(NpgsqlConnection connection, NpgsqlTransaction tx, long chamadoId, int usuarioId, bool daZippy, string texto, IReadOnlyList<ChamadoAnexoSalvo> salvos, CancellationToken ct)
        {
            var mensagemId = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
                "INSERT INTO chamado_mensagem (chamado_id, autor_id, da_zippy, texto) VALUES (@Id, @Usuario, @DaZippy, @Texto) RETURNING id;",
                new { Id = chamadoId, Usuario = usuarioId, DaZippy = daZippy, Texto = texto }, tx, cancellationToken: ct));
            foreach (var anexo in salvos)
                await connection.ExecuteAsync(new CommandDefinition(
                    "INSERT INTO chamado_anexo (mensagem_id, url, nome, tipo, bytes, public_id) VALUES (@Mensagem, @Url, @Nome, @Tipo, @Bytes, @PublicId);",
                    new { Mensagem = mensagemId, anexo.Url, anexo.Nome, anexo.Tipo, anexo.Bytes, anexo.PublicId }, tx, cancellationToken: ct));
        }

        public async Task<IReadOnlyList<ChamadoDto>> ListarAsync(Guid? loja, CancellationToken ct = default)
        {
            await using var connection = await dataSource.OpenConnectionAsync(ct);
            return (await connection.QueryAsync<ChamadoDto>(new CommandDefinition(Select + @"
 WHERE (@Loja::uuid IS NULL OR c.estabelecimento_id = @Loja)
   AND (c.status <> 'resolvido' OR c.resolvido_em > NOW() - INTERVAL '30 days')
 ORDER BY (c.status = 'resolvido'), c.prazo_em LIMIT 300;", new { Loja = loja }, cancellationToken: ct))).ToList();
        }

        public async Task<(ChamadoDto, IReadOnlyList<ChamadoMensagemDto>)> ObterAsync(long id, Guid? loja, CancellationToken ct = default)
        {
            await using var connection = await dataSource.OpenConnectionAsync(ct);
            var dto = await connection.QuerySingleOrDefaultAsync<ChamadoDto>(new CommandDefinition(Select + " WHERE c.id = @Id AND (@Loja::uuid IS NULL OR c.estabelecimento_id = @Loja);",
                new { Id = id, Loja = loja }, cancellationToken: ct)) ?? throw new ChamadoException(404, "Chamado não encontrado.");
            var msgs = (await connection.QueryAsync<ChamadoMensagemDto>(new CommandDefinition(@"
SELECT m.id, m.da_zippy AS DaZippy, m.texto, u.nome AS Autor, m.criado_em AS CriadoEm
  FROM chamado_mensagem m LEFT JOIN usuario u ON u.id = m.autor_id WHERE m.chamado_id = @Id ORDER BY m.criado_em, m.id;",
                new { Id = id }, cancellationToken: ct))).ToList();
            // A loja ja foi conferida na consulta do chamado; os anexos vem so das mensagens dele.
            var porMensagem = (await connection.QueryAsync<AnexoLinha>(new CommandDefinition(@"
SELECT a.id, a.mensagem_id AS MensagemId, a.url, a.nome, a.tipo, a.bytes
  FROM chamado_anexo a JOIN chamado_mensagem m ON m.id = a.mensagem_id WHERE m.chamado_id = @Id ORDER BY a.id;",
                new { Id = id }, cancellationToken: ct))).ToLookup(a => a.MensagemId);
            foreach (var m in msgs)
                m.Anexos = porMensagem[m.Id].Select(a => new ChamadoAnexoDto { Id = a.Id, Url = a.Url, Nome = a.Nome, Tipo = a.Tipo, Bytes = a.Bytes }).ToList();
            return (dto, msgs);
        }

        public async Task ResponderAsync(long id, Guid? loja, int usuarioId, bool daZippy, string texto, IReadOnlyList<ArquivoEnviado>? arquivos = null, CancellationToken ct = default)
        {
            texto = texto?.Trim() ?? string.Empty;
            var prontos = await anexos.PrepararAsync(arquivos, ct);
            if (texto.Length == 0 && prontos.Count == 0) throw new ChamadoException(422, "Escreva a resposta.");
            await using var connection = await dataSource.OpenConnectionAsync(ct);
            // Confere o dono antes de subir qualquer arquivo: o print de uma loja nunca vai parar no chamado de outra.
            var dono = await connection.QuerySingleOrDefaultAsync<Guid?>(new CommandDefinition(
                "SELECT estabelecimento_id FROM chamado WHERE id = @Id AND (@Loja::uuid IS NULL OR estabelecimento_id = @Loja);",
                new { Id = id, Loja = loja }, cancellationToken: ct)) ?? throw new ChamadoException(404, "Chamado não encontrado.");
            var salvos = await anexos.EnviarAsync(dono, id, prontos, ct);
            try
            {
                await using var tx = await connection.BeginTransactionAsync(ct);
                // Resposta da Zippy passa para "andamento"; nova mensagem da loja reabre um chamado que esperava por ela.
                var linhas = await connection.ExecuteAsync(new CommandDefinition(@"
UPDATE chamado SET atualizado_em = NOW(),
       primeira_resposta_em = CASE WHEN @DaZippy THEN COALESCE(primeira_resposta_em, NOW()) ELSE primeira_resposta_em END,
       status = CASE WHEN @DaZippy AND status = 'aberto' THEN 'andamento' WHEN NOT @DaZippy AND status IN ('loja', 'resolvido') THEN 'aberto' ELSE status END,
       resolvido_em = CASE WHEN NOT @DaZippy THEN NULL ELSE resolvido_em END
 WHERE id = @Id AND (@Loja::uuid IS NULL OR estabelecimento_id = @Loja);", new { Id = id, Loja = loja, DaZippy = daZippy }, tx, cancellationToken: ct));
                if (linhas == 0) throw new ChamadoException(404, "Chamado não encontrado.");
                await GravarMensagemAsync(connection, tx, id, usuarioId, daZippy, texto, salvos, ct);
                await tx.CommitAsync(ct);
            }
            catch
            {
                await anexos.DescartarAsync(salvos);
                throw;
            }
        }

        public async Task MudarStatusAsync(long id, string status, CancellationToken ct = default)
        {
            status = status?.Trim().ToLowerInvariant() ?? string.Empty;
            if (!Status.Contains(status)) throw new ChamadoException(422, "Situação inválida.");
            await using var connection = await dataSource.OpenConnectionAsync(ct);
            var linhas = await connection.ExecuteAsync(new CommandDefinition(@"
UPDATE chamado SET status = @Status, atualizado_em = NOW(), resolvido_em = CASE WHEN @Status = 'resolvido' THEN NOW() ELSE NULL END WHERE id = @Id;",
                new { Id = id, Status = status }, cancellationToken: ct));
            if (linhas == 0) throw new ChamadoException(404, "Chamado não encontrado.");
        }

        public async Task AvaliarAsync(long id, Guid loja, short nota, CancellationToken ct = default)
        {
            if (nota is < 1 or > 5) throw new ChamadoException(422, "Dê uma nota de 1 a 5.");
            await using var connection = await dataSource.OpenConnectionAsync(ct);
            var linhas = await connection.ExecuteAsync(new CommandDefinition(
                "UPDATE chamado SET nota = @Nota, atualizado_em = NOW() WHERE id = @Id AND estabelecimento_id = @Loja AND status = 'resolvido';",
                new { Id = id, Loja = loja, Nota = nota }, cancellationToken: ct));
            if (linhas == 0) throw new ChamadoException(404, "Só dá para avaliar um chamado resolvido.");
        }
    }
}
