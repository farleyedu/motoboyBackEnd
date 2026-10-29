using System.Text.RegularExpressions;
using APIBack.Acontecimentos;
using APIBack.GestaoLogica.Documentos;

namespace APIBack.GestaoLogica.Vinculos;

// Correção do catálogo global e dos critérios pela Curadoria (L7). Toda correção vira decisão imutável "corrigir" com
// caso global:<alvo>:<id>, antes/depois, responsável e motivo. O insumo de cada loja nunca muda (regra 6).

public sealed record ProdutoGlobalEditavel(Guid Id, string Nome, string Unidade, string? Categoria, string? Ilustracao,
    string Status, int Lojas, int Kits);

public sealed record ItemDoKit(Guid GlobalId, string Nome, string Unidade, string? Ilustracao);

public sealed record KitGlobal(Guid Id, string Nome, string? Descricao, bool Ativo, IReadOnlyList<ItemDoKit> Itens);

public sealed record CategoriaGlobal(Guid Id, string Nome, bool Ativa, int Produtos);

/// <summary>Campo nulo = não mexe; texto vazio em categoria/ilustração = tira.</summary>
public sealed record CorrigirProdutoGlobalRequest(string? Nome, string? Unidade, string? Categoria, string? Ilustracao, string? Motivo);

public sealed record SalvarKitRequest(string? Nome, string? Descricao, IReadOnlyList<Guid>? Itens, bool? Ativo, string? Motivo);

public sealed record SalvarCategoriaRequest(string? Nome, bool? Ativa, string? Motivo);

public sealed record SalvarCriteriosRequest(int? Lojas, int? Confirmacoes, decimal? DivergenciaMax, string? Motivo);

public sealed record VersaoDosCriterios(Guid? Id, int Lojas, int Confirmacoes, decimal DivergenciaMax, string? Responsavel,
    string? Motivo, DateTimeOffset? Em);

public sealed record CriteriosDaCuradoria(VersaoDosCriterios Atual, IReadOnlyList<VersaoDosCriterios> Historico);

public sealed record ProdutoCorrigido(ProdutoGlobalEditavel Produto, RegistroDecisao Registro);

public sealed record KitSalvo(KitGlobal Kit, RegistroDecisao Registro);

public sealed record CategoriaSalva(CategoriaGlobal Categoria, RegistroDecisao Registro, int ProdutosAtualizados);

public static class CuradoriaGlobalRegras
{
    public const string TipoGlobal = "global";
    public const int MaxItensDoKit = 60;
    private static readonly Regex Ilustracao = new("^[a-z0-9][a-z0-9_-]*$", RegexOptions.Compiled);

    public static string IdProduto(Guid id) => "global:produto:" + id;
    public static string IdKit(Guid id) => "global:kit:" + id;
    public static string IdCategoria(Guid id) => "global:categoria:" + id;

    public static string Motivo(string? motivo)
    {
        var m = (motivo ?? "").Trim();
        if (m.Length == 0) throw Invalido("Conte o motivo da correção.", "MOTIVO_OBRIGATORIO");
        return Cortar(m, 500);
    }

    public static string Nome(string? nome, int max, string rotulo)
    {
        var n = string.Join(' ', (nome ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries));
        if (n.Length == 0) throw Invalido($"Informe o nome {rotulo}.");
        if (n.Length > max) throw Invalido($"O nome {rotulo} pode ter até {max} letras.");
        return n;
    }

    public static string Unidade(string? unidade) =>
        unidade?.Trim().ToLowerInvariant() is "g" or "ml" or "un" ? unidade.Trim().ToLowerInvariant() : throw Invalido("Unidade deve ser g, ml ou un.");

    /// <summary>Nome do arquivo da ilustração Zippy (sem extensão). Vazio = sem ilustração. URL ou foto nunca entram.</summary>
    public static string? ArquivoDeIlustracao(string ilustracao)
    {
        var i = ilustracao.Trim();
        if (i.Length == 0) return null;
        if (i.Length > 120 || !Ilustracao.IsMatch(i)) throw Invalido("Escolha uma das ilustrações Zippy.");
        return i;
    }

    public static CriteriosCuradoria Criterios(SalvarCriteriosRequest req)
    {
        if (req.Lojas is not (>= 1 and <= 100)) throw Invalido("Lojas: de 1 a 100.");
        if (req.Confirmacoes is not (>= 1 and <= 1000)) throw Invalido("Confirmações: de 1 a 1000.");
        if (req.DivergenciaMax is not { } d || d < 0 || d > 0.5m) throw Invalido("Divergência: de 0% a 50%.");
        return new CriteriosCuradoria(req.Lojas.Value, req.Confirmacoes.Value, Math.Round(d, 4));
    }

    /// <summary>Antes/depois só com o que mudou ("Nome: A" → "Nome: B"), para caber no registro e ser lido de relance.</summary>
    public static (string Antes, string Depois) Diferenca(IReadOnlyList<(string Campo, string? Antes, string? Depois)> campos)
    {
        var mudou = campos.Where(c => (c.Antes ?? "") != (c.Depois ?? "")).ToList();
        if (mudou.Count == 0) throw Invalido("Nada mudou.", "SEM_MUDANCA");
        string Texto(Func<(string Campo, string? Antes, string? Depois), string?> v) =>
            Cortar(string.Join(" · ", mudou.Select(c => $"{c.Campo}: {(string.IsNullOrEmpty(v(c)) ? "—" : v(c))}")), 300);
        return (Texto(c => c.Antes), Texto(c => c.Depois));
    }

    public static string Chave(string? texto) => DocumentoRegras.NomeChave(texto);

    public static string Cortar(string s, int max) => s.Length <= max ? s : s[..max];

    public static GestaoException Invalido(string m, string codigo = "CAMPO_INVALIDO") => new(codigo, m, 422);
}
