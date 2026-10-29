using System.Text.RegularExpressions;
using APIBack.Acontecimentos;
using Dapper;
using Npgsql;

namespace APIBack.GestaoLogica.Despesas;

// Equipamentos da loja (auditoria-edicao item 7). Antes ficavam no navegador; o id continua o mesmo ("equip-<uuid>") porque as
// manutenções já lançadas guardam esse id em dados.equipamentoId. Espelha Equipamento do Painel (despesas/despesasApi.ts).

public sealed record EquipamentoDto(string Id, string Nome, string Modelo, string Icone, string Cor, string Estado, string? CompradoEm,
    decimal? ValorDoBem, string? GarantiaAte, string? Local, bool Arquivado, int Versao);

public sealed record EquipamentoRequest(string? Id, string? Nome, string? Modelo, string? Icone, string? Cor, string? Estado, string? CompradoEm = null,
    decimal? ValorDoBem = null, string? GarantiaAte = null, string? Local = null, int? Versao = null, bool Arquivado = false);

public sealed record ImportarEquipamentosRequest(IReadOnlyList<EquipamentoRequest>? Equipamentos);

/// <summary>Resultado da migração do navegador: quantos entraram agora, quantos já estavam no servidor e quais foram recusados.</summary>
public sealed record ImportacaoEquipamentosDto(int Importados, int JaExistiam, IReadOnlyList<string> Recusados);

public static class EquipamentoRegras
{
    public static readonly string[] Icones = { "frio", "fogo", "agua", "eletrico", "ferramenta", "cozinha", "outro" };
    public static readonly string[] Estados = { "funcionando", "revisao", "parado" };
    private static readonly Regex IdValido = new("^[A-Za-z0-9_-]{3,80}$", RegexOptions.Compiled);
    private static readonly Regex Mes = new(@"^\d{4}-(0[1-9]|1[0-2])$", RegexOptions.Compiled);
    private static readonly Regex Cor = new("^#[0-9A-Fa-f]{6}$", RegexOptions.Compiled);
    public const int LimiteImportacao = 500;

    public static EquipamentoRequest Validar(EquipamentoRequest r)
    {
        var nome = r.Nome?.Trim() ?? "";
        if (nome.Length is < 2 or > 120) throw Invalido("Dê um nome ao equipamento (ex.: Freezer horizontal).");
        if (r.Id is { } id && !IdValido.IsMatch(id)) throw Invalido("Identificador do equipamento inválido.");
        if (!Icones.Contains(r.Icone)) throw Invalido("Escolha o tipo do equipamento.");
        if (!Estados.Contains(r.Estado)) throw Invalido("Escolha a situação do equipamento.");
        var comprado = string.IsNullOrWhiteSpace(r.CompradoEm) ? null : r.CompradoEm.Trim();
        if (comprado is not null && !Mes.IsMatch(comprado)) throw Invalido("Compra no formato 2023-03 (ano e mês), ou deixe em branco.");
        if (r.ValorDoBem is { } v && (v < 0 || decimal.Round(v, 2) != v)) throw Invalido("Valor do bem com até 2 casas.");
        var garantia = string.IsNullOrWhiteSpace(r.GarantiaAte) ? null : r.GarantiaAte.Trim();
        if (garantia is { Length: > 10 }) throw Invalido("Garantia no formato 2025-12 ou 2025-12-31.");
        return r with
        {
            Nome = nome, Modelo = Cortar(r.Modelo?.Trim() ?? "", 120), Cor = r.Cor is { } c && Cor.IsMatch(c) ? c : "#64748B", CompradoEm = comprado,
            GarantiaAte = garantia, Local = string.IsNullOrWhiteSpace(r.Local) ? null : Cortar(r.Local.Trim(), 120),
        };
    }

    private static string Cortar(string s, int max) => s.Length <= max ? s : s[..max];

    public static GestaoException Invalido(string m) => new("CAMPO_INVALIDO", m, 422);
}

public interface IEquipamentosService
{
    Task<IReadOnlyList<EquipamentoDto>> Listar(Guid loja, bool incluirArquivados, CancellationToken ct);
    Task<EquipamentoDto> Criar(Guid loja, int usuario, EquipamentoRequest req, CancellationToken ct);
    Task<ImportacaoEquipamentosDto> Importar(Guid loja, int usuario, ImportarEquipamentosRequest req, CancellationToken ct);
    Task<EquipamentoDto> Atualizar(Guid loja, int usuario, string id, EquipamentoRequest req, CancellationToken ct);
    Task<EquipamentoDto> Arquivar(Guid loja, int usuario, string id, bool arquivar, CancellationToken ct);
}

public sealed class EquipamentosService(NpgsqlDataSource dataSource, TimeProvider? relogio = null) : IEquipamentosService
{
    private readonly TimeProvider _relogio = relogio ?? TimeProvider.System;

    private const string Colunas = "id, nome, modelo, icone, cor, estado, comprado_em, valor_do_bem, garantia_ate, local, arquivado, versao";

    public async Task<IReadOnlyList<EquipamentoDto>> Listar(Guid loja, bool incluirArquivados, CancellationToken ct)
    {
        await using var c = await dataSource.OpenConnectionAsync(ct);
        return (await c.QueryAsync<EquipamentoDto>($"SELECT {Colunas} FROM gestao_equipamentos WHERE estabelecimento_id = @loja AND (@todos OR NOT arquivado) ORDER BY nome",
            new { loja, todos = incluirArquivados })).ToList();
    }

    /// <summary>Com <c>id</c> já cadastrado, devolve o que existe (repetir o envio não duplica).</summary>
    public async Task<EquipamentoDto> Criar(Guid loja, int usuario, EquipamentoRequest req, CancellationToken ct)
    {
        var r = EquipamentoRegras.Validar(req);
        var id = r.Id ?? $"equip-{Guid.NewGuid()}";
        await using var c = await dataSource.OpenConnectionAsync(ct);
        await Inserir(c, loja, usuario, id, r with { Arquivado = false }, "painel");
        return (await Obter(c, loja, id))!;
    }

    /// <summary>
    /// Leva o cadastro que estava no navegador para o servidor, com o mesmo id (as manutenções já lançadas continuam ligadas). O que já
    /// está no servidor não é sobrescrito: abrir a tela em outro aparelho, ou de novo, não duplica nem volta a ficha antiga.
    /// </summary>
    public async Task<ImportacaoEquipamentosDto> Importar(Guid loja, int usuario, ImportarEquipamentosRequest req, CancellationToken ct)
    {
        var lista = req.Equipamentos ?? Array.Empty<EquipamentoRequest>();
        if (lista.Count > EquipamentoRegras.LimiteImportacao) throw EquipamentoRegras.Invalido($"Envie até {EquipamentoRegras.LimiteImportacao} equipamentos.");
        int importados = 0, jaExistiam = 0;
        var recusados = new List<string>();
        await using var c = await dataSource.OpenConnectionAsync(ct);
        foreach (var bruto in lista)
        {
            EquipamentoRequest r;
            try { r = EquipamentoRegras.Validar(bruto); }
            catch (GestaoException) { recusados.Add(bruto.Nome ?? bruto.Id ?? "?"); continue; }
            if (r.Id is null) { recusados.Add(r.Nome!); continue; }
            if (await Inserir(c, loja, usuario, r.Id, r, "navegador")) importados++; else jaExistiam++;
        }
        return new ImportacaoEquipamentosDto(importados, jaExistiam, recusados);
    }

    public async Task<EquipamentoDto> Atualizar(Guid loja, int usuario, string id, EquipamentoRequest req, CancellationToken ct)
    {
        var r = EquipamentoRegras.Validar(req with { Id = null });
        await using var c = await dataSource.OpenConnectionAsync(ct);
        var n = await c.ExecuteAsync(@"
UPDATE gestao_equipamentos SET nome = @Nome, modelo = @Modelo, icone = @Icone, cor = @Cor, estado = @Estado, comprado_em = @CompradoEm,
       valor_do_bem = @ValorDoBem, garantia_ate = @GarantiaAte, local = @Local, versao = versao + 1, atualizado_por = @usuario, atualizado_em = @agora
 WHERE estabelecimento_id = @loja AND id = @id AND (@Versao::int IS NULL OR versao = @Versao)",
            new { loja, id, r.Nome, r.Modelo, r.Icone, r.Cor, r.Estado, r.CompradoEm, r.ValorDoBem, r.GarantiaAte, r.Local, r.Versao, usuario, agora = _relogio.GetUtcNow() });
        if (n == 0)
            throw await Obter(c, loja, id) is null
                ? NaoEncontrado()
                : new GestaoException("IDEMPOTENCIA_CONFLITO", "Outra pessoa alterou este equipamento. Atualize a tela.", 409);
        return (await Obter(c, loja, id))!;
    }

    /// <summary>Nada se apaga: arquivado sai da lista, o histórico de manutenção continua; reativar traz de volta.</summary>
    public async Task<EquipamentoDto> Arquivar(Guid loja, int usuario, string id, bool arquivar, CancellationToken ct)
    {
        await using var c = await dataSource.OpenConnectionAsync(ct);
        await c.ExecuteAsync(@"UPDATE gestao_equipamentos SET arquivado = @arquivar, versao = versao + 1, atualizado_por = @usuario, atualizado_em = @agora
 WHERE estabelecimento_id = @loja AND id = @id AND arquivado <> @arquivar", new { loja, id, arquivar, usuario, agora = _relogio.GetUtcNow() });
        return await Obter(c, loja, id) ?? throw NaoEncontrado();
    }

    private async Task<bool> Inserir(NpgsqlConnection c, Guid loja, int usuario, string id, EquipamentoRequest r, string origem) =>
        await c.ExecuteAsync(@"
INSERT INTO gestao_equipamentos (estabelecimento_id, id, nome, modelo, icone, cor, estado, comprado_em, valor_do_bem, garantia_ate, local, arquivado,
    origem, criado_por, criado_em, atualizado_por, atualizado_em)
VALUES (@loja, @id, @Nome, @Modelo, @Icone, @Cor, @Estado, @CompradoEm, @ValorDoBem, @GarantiaAte, @Local, @Arquivado, @origem, @usuario, @agora, @usuario, @agora)
ON CONFLICT (estabelecimento_id, id) DO NOTHING",
            new { loja, id, r.Nome, r.Modelo, r.Icone, r.Cor, r.Estado, r.CompradoEm, r.ValorDoBem, r.GarantiaAte, r.Local, r.Arquivado, origem, usuario,
                agora = _relogio.GetUtcNow() }) == 1;

    private static async Task<EquipamentoDto?> Obter(NpgsqlConnection c, Guid loja, string id) =>
        await c.QuerySingleOrDefaultAsync<EquipamentoDto>($"SELECT {Colunas} FROM gestao_equipamentos WHERE estabelecimento_id = @loja AND id = @id", new { loja, id });

    private static GestaoException NaoEncontrado() => new("NAO_ENCONTRADO", "Equipamento não encontrado.", 404);
}
