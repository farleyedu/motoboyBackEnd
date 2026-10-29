using System.Text.Json;
using APIBack.Acontecimentos;
using APIBack.Service;
using Dapper;
using Npgsql;

namespace APIBack.GestaoLogica.Documentos;

/// <summary>Registra documentos lidos e barra duplicados. Não mexe em financeiro nem estoque.</summary>
public interface IDocumentosService
{
    Task<DocumentoDto> RegistrarXml(Guid loja, int usuario, byte[] arquivo, CancellationToken ct);
    Task<DocumentoDto> RegistrarFoto(Guid loja, int usuario, byte[] imagem, string mediaType, CancellationToken ct);
    Task<DocumentoDto> Registrar(Guid loja, int usuario, DocumentoLido lido, string? hashArquivo, CancellationToken ct);
    Task<DocumentoDto?> Obter(Guid loja, Guid id, CancellationToken ct);
    Task<(byte[] Conteudo, string MediaType)?> Arquivo(Guid loja, Guid id, CancellationToken ct);
}

public sealed class DocumentosService(NpgsqlDataSource dataSource, ILeitorCupomFoto leitorFoto, TimeProvider? relogio = null)
    : IDocumentosService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly TimeProvider _relogio = relogio ?? TimeProvider.System;

    public async Task<DocumentoDto> RegistrarXml(Guid loja, int usuario, byte[] arquivo, CancellationToken ct)
    {
        var doc = await Registrar(loja, usuario, DocumentoRegras.LerXml(System.Text.Encoding.UTF8.GetString(arquivo)), DocumentoRegras.Hash(arquivo), ct);
        await GuardarArquivo(loja, doc.Id, arquivo, "application/xml", ct);
        return doc;
    }

    public async Task<DocumentoDto> RegistrarFoto(Guid loja, int usuario, byte[] imagem, string mediaType, CancellationToken ct)
    {
        var hash = DocumentoRegras.Hash(imagem);
        // A mesma foto não é lida (nem paga) duas vezes.
        await using (var c = await dataSource.OpenConnectionAsync(ct))
            if (await c.ExecuteScalarAsync<Guid?>("SELECT id FROM gestao_documentos WHERE estabelecimento_id = @loja AND hash_arquivo = @hash",
                    new { loja, hash }) is { } existente)
                throw Duplicado("Esta foto já foi enviada.", existente);
        var doc = await Registrar(loja, usuario, await leitorFoto.Ler(imagem, mediaType, ct), hash, ct);
        await GuardarArquivo(loja, doc.Id, imagem, mediaType, ct);
        return doc;
    }

    /// <summary>O arquivo original (foto do cupom ou XML), para a conferência mostrar o documento de verdade.</summary>
    public async Task<(byte[] Conteudo, string MediaType)?> Arquivo(Guid loja, Guid id, CancellationToken ct)
    {
        await using var c = await dataSource.OpenConnectionAsync(ct);
        var row = await c.QuerySingleOrDefaultAsync<(byte[]? Conteudo, string? MediaType)>(
            "SELECT conteudo, media_type FROM gestao_documento_arquivos WHERE estabelecimento_id = @loja AND documento_id = @id", new { loja, id });
        return row.Conteudo is null ? null : (row.Conteudo, row.MediaType!);
    }

    private async Task GuardarArquivo(Guid loja, Guid documentoId, byte[] conteudo, string mediaType, CancellationToken ct)
    {
        await using var c = await dataSource.OpenConnectionAsync(ct);
        await c.ExecuteAsync(@"
INSERT INTO gestao_documento_arquivos (documento_id, estabelecimento_id, media_type, conteudo, criado_em)
VALUES (@documentoId, @loja, @mediaType, @conteudo, @agora) ON CONFLICT (documento_id) DO NOTHING",
            new { documentoId, loja, mediaType, conteudo, agora = _relogio.GetUtcNow() });
    }

    public async Task<DocumentoDto> Registrar(Guid loja, int usuario, DocumentoLido lido, string? hashArquivo, CancellationToken ct)
    {
        await using var c = await dataSource.OpenConnectionAsync(ct);
        var fuso = HorarioRegras.ResolverFuso(await c.ExecuteScalarAsync<string?>(
            "SELECT timezone_iana FROM estabelecimentos WHERE id = @loja", new { loja }));
        var impressao = DocumentoRegras.Impressao(lido);
        var semelhanca = DocumentoRegras.Semelhanca(lido, fuso);
        var iguais = (await c.QueryAsync<(Guid Id, string? Chave, string? Hash, string? Impressao)>(@"
SELECT id, chave_acesso, hash_arquivo, impressao FROM gestao_documentos
 WHERE estabelecimento_id = @loja AND (chave_acesso = @chave OR hash_arquivo = @hash OR impressao = @impressao)",
            new { loja, chave = lido.ChaveAcesso, hash = hashArquivo, impressao })).ToList();
        if (iguais.FirstOrDefault(d => d.Chave != null && d.Chave == lido.ChaveAcesso) is { Id: var porChave } && porChave != Guid.Empty)
            throw Duplicado("Este documento já foi lançado (mesma chave de acesso).", porChave);
        if (iguais.FirstOrDefault(d => d.Hash != null && d.Hash == hashArquivo) is { Id: var porHash } && porHash != Guid.Empty)
            throw Duplicado("Este arquivo já foi enviado.", porHash);
        if (iguais.FirstOrDefault(d => d.Impressao != null && d.Impressao == impressao) is { Id: var porNumero } && porNumero != Guid.Empty)
            throw Duplicado("Este documento já foi lançado (mesmo CNPJ, série e número).", porNumero);

        var id = Guid.NewGuid();
        try
        {
            await c.ExecuteAsync(@"
INSERT INTO gestao_documentos (id, estabelecimento_id, origem, chave_acesso, hash_arquivo, impressao, semelhanca, fornecedor_cnpj,
    fornecedor_nome, data_emissao, numero, serie, total, leitura, criado_por, criado_em)
VALUES (@id, @loja, @Origem, @ChaveAcesso, @hashArquivo, @impressao, @semelhanca, @Cnpj, @Nome, @DataEmissao, @Numero, @Serie,
    @Total, CAST(@leitura AS jsonb), @usuario, @agora)", new
            {
                id, loja, lido.Origem, lido.ChaveAcesso, hashArquivo, impressao, semelhanca, lido.Fornecedor.Cnpj,
                Nome = Cortar(lido.Fornecedor.Nome, 160), DataEmissao = lido.DataEmissao?.ToUniversalTime(), Numero = Cortar(lido.Numero, 20), Serie = Cortar(lido.Serie, 5),
                lido.Total, leitura = JsonSerializer.Serialize(lido, Json), usuario, agora = _relogio.GetUtcNow(),
            });
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            throw Duplicado("Este documento acabou de ser lançado por outra pessoa.", Guid.Empty);
        }
        return (await Obter(c, loja, id))!;
    }

    public async Task<DocumentoDto?> Obter(Guid loja, Guid id, CancellationToken ct)
    {
        await using var c = await dataSource.OpenConnectionAsync(ct);
        return await Obter(c, loja, id);
    }

    private static async Task<DocumentoDto?> Obter(NpgsqlConnection c, Guid loja, Guid id)
    {
        var row = await c.QuerySingleOrDefaultAsync<(Guid Id, string Leitura, string? Semelhanca, int CriadoPor, DateTime CriadoEm)>(@"
SELECT id, leitura::text, semelhanca, criado_por, criado_em FROM gestao_documentos WHERE estabelecimento_id = @loja AND id = @id",
            new { loja, id });
        if (row.Id == Guid.Empty) return null;
        var lido = JsonSerializer.Deserialize<DocumentoLido>(row.Leitura, Json)!;
        // Parecido não bloqueia: duas compras iguais no mesmo dia existem. A tela mostra o aviso.
        var parecidos = row.Semelhanca is null ? new List<Guid>() : (await c.QueryAsync<Guid>(
            "SELECT id FROM gestao_documentos WHERE estabelecimento_id = @loja AND semelhanca = @s AND id <> @id ORDER BY criado_em",
            new { loja, s = row.Semelhanca, id })).ToList();
        return new DocumentoDto(row.Id, lido.Origem, "lido", lido.Fornecedor, lido.DataEmissao, lido.Numero, lido.Serie,
            lido.ChaveAcesso, lido.Total, lido.Itens, lido.Avisos,
            parecidos.Select(p => new Duplicidade(false, "Há outro documento do mesmo fornecedor, no mesmo dia e com o mesmo total.", p)).ToList(),
            null, row.CriadoPor, new DateTimeOffset(DateTime.SpecifyKind(row.CriadoEm, DateTimeKind.Utc)));
    }

    private static string? Cortar(string? s, int max) => s is null || s.Length <= max ? s : s[..max];

    private static GestaoException Duplicado(string mensagem, Guid existente) =>
        new("DUPLICADO", existente == Guid.Empty ? mensagem : $"{mensagem} Documento {existente}.", 409);
}
