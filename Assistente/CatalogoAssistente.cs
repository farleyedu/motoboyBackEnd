using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace APIBack.Assistente;

/// <summary>Um item do catálogo (formato em zippy-admin/docs/assistente-ajuda/ANALISE.md, "O catálogo").</summary>
public sealed class ItemCatalogo
{
    public string Id { get; set; } = "";
    public string Nome { get; set; } = "";
    public string Area { get; set; } = "";
    public string OQueFaz { get; set; } = "";
    public List<string> ComoAsPessoasPedem { get; set; } = [];
    /// <summary><c>ia_faz</c> | <c>tela</c> | <c>consulta</c>.</summary>
    public string Modo { get; set; } = "";
    /// <summary>Nome da ação no backend (ver <see cref="IAcaoAssistente.Nome"/>). Obrigatório só no modo <c>ia_faz</c>.</summary>
    public string? Acao { get; set; }
    /// <summary>Rota da tela no painel.</summary>
    public string Rota { get; set; } = "";
    public List<string> Passos { get; set; } = [];
    /// <summary><c>Modulo:acao</c> como no <c>[RequirePermission]</c> da rota (ex.: <c>Cardapio:editar</c>), ou nulo para todos.</summary>
    public string? Permissao { get; set; }
    /// <summary>Id do item que desfaz este, ou nulo.</summary>
    public string? Desfazer { get; set; }

    public (string Modulo, string Acao)? PermissaoSeparada()
    {
        if (string.IsNullOrWhiteSpace(Permissao)) return null;
        var partes = Permissao.Split(':', 2);
        return (partes[0], partes[1]);
    }
}

public static class ModosCatalogo
{
    public const string IaFaz = "ia_faz";
    public const string Tela = "tela";
    public const string Consulta = "consulta";
    public static readonly string[] Todos = [IaFaz, Tela, Consulta];
}

public sealed class CatalogoInvalidoException(string message) : Exception(message);

/// <summary>
/// Catálogo de funcionalidades que a IA consulta. Lido uma vez dos <c>Assistente/Catalogo/*.json</c> embutidos (um arquivo por área,
/// cada um com um array de itens). Item inválido ou id repetido derruba a carga com a lista de problemas: melhor falhar no boot do
/// que a IA prometer algo que não existe. Arquivos que começam com "_" são referência do esquema: validados sempre, mas só entram no
/// catálogo servido enquanto não houver nenhum arquivo de área.
/// </summary>
public sealed class CatalogoAssistente
{
    public const string PrefixoRecurso = "APIBack.Assistente.Catalogo.";

    private static readonly Regex IdValido = new("^[a-z0-9]+(-[a-z0-9]+)*\\.[a-z0-9]+(-[a-z0-9]+)*$", RegexOptions.Compiled);
    private static readonly Regex PermissaoValida = new("^[A-Za-z]+:[a-z]+(_[a-z]+)*$", RegexOptions.Compiled);
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };
    private static readonly JsonSerializerOptions JsonPrompt = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly Dictionary<string, ItemCatalogo> _porId;

    public IReadOnlyList<ItemCatalogo> Itens { get; }

    private CatalogoAssistente(IReadOnlyList<ItemCatalogo> itens)
    {
        Itens = itens.OrderBy(i => i.Id, StringComparer.Ordinal).ToList();
        _porId = Itens.ToDictionary(i => i.Id, StringComparer.Ordinal);
    }

    public ItemCatalogo? Obter(string? id) => id is not null && _porId.TryGetValue(id, out var item) ? item : null;

    public IEnumerable<ItemCatalogo> DoModo(string modo) => Itens.Where(i => i.Modo == modo);

    /// <summary>Catálogo em JSON estável (ordenado por id) para o prompt de sistema: mesmo catálogo = mesmo texto = cache aproveitado.</summary>
    /// <remarks>Item <c>ia_faz</c> cuja ação ainda não existe no backend vai como <c>tela</c>: a IA explica e abre, não propõe.</remarks>
    public string ParaPrompt(Func<string, bool> acaoExiste) => JsonSerializer.Serialize(
        Itens.Select(i => i.Modo == ModosCatalogo.IaFaz && !acaoExiste(i.Acao!)
            ? new ItemCatalogo
            {
                Id = i.Id, Nome = i.Nome, Area = i.Area, OQueFaz = i.OQueFaz, ComoAsPessoasPedem = i.ComoAsPessoasPedem,
                Modo = ModosCatalogo.Tela, Rota = i.Rota, Passos = i.Passos, Permissao = i.Permissao,
            }
            : i),
        JsonPrompt);

    /// <summary>Lê os JSON embutidos neste assembly.</summary>
    public static CatalogoAssistente CarregarEmbutido() => CarregarEmbutido(typeof(CatalogoAssistente).Assembly);

    public static CatalogoAssistente CarregarEmbutido(Assembly assembly)
    {
        var arquivos = assembly.GetManifestResourceNames()
            .Where(n => n.StartsWith(PrefixoRecurso, StringComparison.Ordinal) && n.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            .OrderBy(n => n, StringComparer.Ordinal)
            .Select(n =>
            {
                using var stream = assembly.GetManifestResourceStream(n)!;
                using var leitor = new StreamReader(stream);
                return (Nome: n[PrefixoRecurso.Length..], Conteudo: leitor.ReadToEnd());
            })
            .ToList();
        return Carregar(arquivos);
    }

    /// <summary>Valida e monta o catálogo a partir de (nome do arquivo, conteúdo). Público para teste.</summary>
    public static CatalogoAssistente Carregar(IEnumerable<(string Nome, string Conteudo)> arquivos, ISet<string>? acoesConhecidas = null)
    {
        var erros = new List<string>();
        var area = new List<ItemCatalogo>();
        var referencia = new List<ItemCatalogo>();
        var origem = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var (nome, conteudo) in arquivos)
        {
            List<ItemCatalogo?>? itens;
            try
            {
                itens = JsonSerializer.Deserialize<List<ItemCatalogo?>>(conteudo, Json);
            }
            catch (JsonException ex)
            {
                erros.Add($"{nome}: JSON inválido ({ex.Message})");
                continue;
            }
            if (itens is null) { erros.Add($"{nome}: o arquivo precisa ser um array de itens"); continue; }
            var destino = nome.StartsWith('_') ? referencia : area;
            for (var i = 0; i < itens.Count; i++)
            {
                var item = itens[i];
                var onde = $"{nome}[{i}]";
                if (item is null) { erros.Add($"{onde}: item nulo"); continue; }
                onde = $"{nome} ({(string.IsNullOrWhiteSpace(item.Id) ? $"item {i}" : item.Id)})";
                erros.AddRange(Validar(item, acoesConhecidas).Select(e => $"{onde}: {e}"));
                if (!string.IsNullOrWhiteSpace(item.Id))
                {
                    if (origem.TryGetValue(item.Id, out var primeiro)) erros.Add($"{onde}: id repetido (já existe em {primeiro})");
                    else origem[item.Id] = nome;
                }
                destino.Add(item);
            }
        }

        // "desfazer" tem que apontar para um item que existe (entre todos os arquivos).
        foreach (var item in area.Concat(referencia).Where(i => i.Desfazer is not null))
            if (!origem.ContainsKey(item.Desfazer!)) erros.Add($"{item.Id}: desfazer aponta para '{item.Desfazer}', que não existe");

        if (erros.Count > 0)
            throw new CatalogoInvalidoException("Catálogo do assistente inválido:\n- " + string.Join("\n- ", erros));
        return new CatalogoAssistente(area.Count > 0 ? area : referencia);
    }

    /// <summary>Regras de um item isolado. Público para teste.</summary>
    public static IEnumerable<string> Validar(ItemCatalogo item, ISet<string>? acoesConhecidas = null)
    {
        if (string.IsNullOrWhiteSpace(item.Id)) yield return "id é obrigatório";
        else if (!IdValido.IsMatch(item.Id)) yield return "id deve ser 'area.nome-com-hifen' em minúsculas, sem acento";
        if (string.IsNullOrWhiteSpace(item.Nome)) yield return "nome é obrigatório";
        if (string.IsNullOrWhiteSpace(item.Area)) yield return "area é obrigatória";
        if (string.IsNullOrWhiteSpace(item.OQueFaz)) yield return "oQueFaz é obrigatório";
        if (item.ComoAsPessoasPedem is null || item.ComoAsPessoasPedem.Count == 0 || item.ComoAsPessoasPedem.Any(string.IsNullOrWhiteSpace))
            yield return "comoAsPessoasPedem precisa de pelo menos uma frase (sem vazias)";
        if (!ModosCatalogo.Todos.Contains(item.Modo)) yield return "modo deve ser ia_faz, tela ou consulta";
        if (string.IsNullOrWhiteSpace(item.Rota) || !item.Rota.StartsWith('/')) yield return "rota deve começar com /";
        if (item.Passos is null || item.Passos.Any(string.IsNullOrWhiteSpace)) yield return "passos não pode ter item vazio";
        else if (item.Modo is ModosCatalogo.IaFaz or ModosCatalogo.Tela && item.Passos.Count == 0)
            yield return "passos é obrigatório nos modos ia_faz e tela";
        if (item.Modo == ModosCatalogo.IaFaz)
        {
            if (string.IsNullOrWhiteSpace(item.Acao)) yield return "acao é obrigatória no modo ia_faz";
            else if (acoesConhecidas is not null && !acoesConhecidas.Contains(item.Acao)) yield return $"acao '{item.Acao}' não existe no backend";
            if (string.IsNullOrWhiteSpace(item.Permissao)) yield return "permissao é obrigatória no modo ia_faz";
        }
        else if (item.Acao is not null) yield return "acao só vale no modo ia_faz";
        if (item.Permissao is not null && !PermissaoValida.IsMatch(item.Permissao))
            yield return "permissao deve ser 'Modulo:acao' (ex.: Cardapio:editar)";
        if (item.Desfazer is not null && item.Desfazer == item.Id) yield return "desfazer não pode apontar para o próprio item";
    }
}
