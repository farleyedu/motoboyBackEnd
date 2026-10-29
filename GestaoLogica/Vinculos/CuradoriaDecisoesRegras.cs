using System.Globalization;
using System.Text.RegularExpressions;
using APIBack.Acontecimentos;
using APIBack.GestaoLogica.Documentos;

namespace APIBack.GestaoLogica.Vinculos;

// Curadoria Zippy (Integração 6B): casos gerados dos sinais das lojas e decisões imutáveis. Espelho de
// zippy-admin/src/features/zippy-gestao/curadoria/curadoriaRegras.ts (mesmos nomes de campo, mesmos critérios).

public static class TipoCaso
{
    public const string DePara = "depara", Produto = "prod", Contra = "contra", Duplicidade = "dup", Categoria = "cat";
}

public static class DecisaoCuradoria
{
    public const string Aprovar = "aprovar", Observar = "observar", Corrigir = "corrigir", Rejeitar = "rejeitar", Reabrir = "reabrir";
    public static bool Valida(string? d) => d is Aprovar or Observar or Corrigir or Rejeitar;
}

public sealed record OpcaoDoCaso(string Nome, string? Detalhe, int Lojas, int Confirmacoes, Guid? GlobalId = null);

public sealed record EventoDoCaso(string Quando, string Titulo, string Detalhe);

public sealed record CasoCuradoria(string Id, string Tipo, string Nome, string Resumo, int Lojas, int Confirmacoes,
    int Divergencias, int Cidades, DateTimeOffset AbertoEm, string? Entrada, IReadOnlyList<OpcaoDoCaso> Opcoes,
    IReadOnlyList<string> Variacoes, string? Sinal, IReadOnlyList<EventoDoCaso> Historico);

/// <summary>Uma decisão gravada. Nunca muda: correção = reabertura (registro novo com <c>CorrigeId</c>).</summary>
public sealed record RegistroDecisao(Guid Id, string CasoId, string CasoNome, string CasoTipo, string Decisao, string Responsavel,
    string Motivo, DateTimeOffset Em, string Anterior, string Novo, Guid? Lote, Guid? CorrigeId, bool? Excecao,
    int? ConfirmacoesNoRegistro, Guid? ProdutoGlobalId);

public sealed record ProdutoGlobalCuradoria(Guid Id, string Nome, string Base);

public sealed record ProdutoGlobalEscolhido(string? Id, string? Nome, string? Base);

public sealed record DecidirCuradoriaRequest(string? CasoId, string? Decisao, string? Responsavel, string? Motivo,
    ProdutoGlobalEscolhido? ProdutoGlobal);

public sealed record DecidirEmLoteRequest(IReadOnlyList<string>? CasoIds, string? Decisao, string? Responsavel, string? Motivo);

public sealed record ReabrirDecisaoRequest(string? Responsavel, string? Motivo);

public sealed record ExcluidoDoLote(CasoCuradoria Caso, string Motivo);

public sealed record PlanoDoLote(IReadOnlyList<CasoCuradoria> Incluidos, IReadOnlyList<ExcluidoDoLote> Excluidos);

public sealed record ResultadoDoLote(IReadOnlyList<RegistroDecisao> Registros, PlanoDoLote Plano);

/// <summary>Sinal de vínculo confirmado numa loja (texto do cupom → item global).</summary>
public sealed record SinalDeVinculo(string NomeChave, string Descricao, Guid Loja, string? LojaNome, string? Cidade,
    Guid GlobalId, string GlobalNome, string GlobalStatus, DateTimeOffset ConfirmadoEm, int Confirmacoes);

public sealed record ItemGlobal(Guid Id, string Nome, string Unidade, string? Categoria, string Status, DateTimeOffset CriadoEm,
    int Lojas, int Confirmacoes, int Cidades);

/// <summary>Critérios de promoção. Os vigentes são do super admin (gestao_curadoria_criterios); sem registro, vale o padrão.</summary>
public sealed record CriteriosCuradoria(int Lojas, int Confirmacoes, decimal DivergenciaMax)
{
    public static readonly CriteriosCuradoria Padrao = new(CuradoriaDecisoesRegras.CriterioLojas,
        CuradoriaDecisoesRegras.CriterioConfirmacoes, CuradoriaDecisoesRegras.CriterioDivergenciaMax);
}

public static class CuradoriaDecisoesRegras
{
    public const int CriterioLojas = 5, CriterioConfirmacoes = 10;
    public const decimal CriterioDivergenciaMax = 0.1m;
    /// <summary>Um texto de cupom vira caso quando pelo menos duas lojas o confirmaram.</summary>
    public const int MinimoLojasTexto = 2;
    public const int MinimoLojasCategoria = 3;
    /// <summary>Duplicidade: no máximo uma palavra de diferença em três ("Óleo de soja" × "Óleo de soja Liza").</summary>
    public const decimal SemelhancaDuplicidade = 0.66m, SemelhancaParecido = 0.5m;
    public const string MotivoPadraoAprovacao = "Critérios de promoção atingidos";

    private static readonly CultureInfo PtBr = CultureInfo.GetCultureInfo("pt-BR");
    private static readonly TimeSpan Brasilia = TimeSpan.FromHours(-3);

    // ------------------------------------------------------------ critérios e estado

    public static decimal TaxaDeDivergencia(CasoCuradoria c) => (decimal)c.Divergencias / Math.Max(c.Confirmacoes, 1);

    public static bool AtingeCriterios(CasoCuradoria c, CriteriosCuradoria? k = null)
    {
        k ??= CriteriosCuradoria.Padrao;
        return c.Lojas >= k.Lojas && c.Confirmacoes >= k.Confirmacoes && TaxaDeDivergencia(c) <= k.DivergenciaMax;
    }

    public static string EstadoApos(string decisao) => decisao switch
    {
        DecisaoCuradoria.Aprovar => "aprovado", DecisaoCuradoria.Observar => "observacao", DecisaoCuradoria.Corrigir => "corrigido",
        DecisaoCuradoria.Rejeitar => "rejeitado", _ => "aberto",
    };

    public static IReadOnlyList<RegistroDecisao> EmOrdem(IEnumerable<RegistroDecisao> registros) =>
        registros.OrderBy(r => r.Em).ThenBy(r => r.Id.ToString(), StringComparer.Ordinal).ToList();

    /// <summary>O estado nunca é guardado: sai da última decisão do caso.</summary>
    public static string EstadoDoCaso(string casoId, IEnumerable<RegistroDecisao> registros) =>
        EmOrdem(registros.Where(r => r.CasoId == casoId)).LastOrDefault() is { } ultimo ? EstadoApos(ultimo.Decisao) : "aberto";

    public static string RotuloDaDecisao(string decisao) => decisao switch
    {
        DecisaoCuradoria.Aprovar => "Aprovado", DecisaoCuradoria.Observar => "Em observação", DecisaoCuradoria.Corrigir => "Vínculo corrigido",
        DecisaoCuradoria.Rejeitar => "Promoção rejeitada", _ => "Reaberto",
    };

    public static OpcaoDoCaso? OpcaoMaisAceita(CasoCuradoria c) =>
        c.Opcoes.OrderByDescending(o => o.Confirmacoes).ThenByDescending(o => o.Lojas).FirstOrDefault();

    public static string VinculoAtual(CasoCuradoria c) => OpcaoMaisAceita(c)?.Nome ?? c.Entrada ?? "Sem vínculo global";

    public static (string Anterior, string Novo) AntesEDepois(CasoCuradoria c, string decisao, string? produtoEscolhido) => decisao switch
    {
        DecisaoCuradoria.Corrigir => (VinculoAtual(c), produtoEscolhido?.Trim() ?? ""),
        DecisaoCuradoria.Aprovar => ("Vale só nas lojas", "Validado para todas as lojas"),
        DecisaoCuradoria.Observar => ("Aguardando decisão", "Em observação"),
        _ => ("Aguardando decisão", "Promoção rejeitada"),
    };

    // ------------------------------------------------------------ validação da decisão (regra 2)

    /// <summary>Aprovar dentro dos critérios dispensa o motivo (usa o padrão). Todo o resto pede motivo.</summary>
    public static bool MotivoObrigatorio(CasoCuradoria c, string decisao, CriteriosCuradoria? k = null) =>
        !(decisao == DecisaoCuradoria.Aprovar && AtingeCriterios(c, k));

    public sealed record NovaDecisao(string Decisao, string Motivo, string Anterior, string Novo, bool Excecao, int? ConfirmacoesNoRegistro);

    /// <summary>Monta o que vai ser gravado. Só decide caso aberto ou em observação; responsável vem do login.</summary>
    public static NovaDecisao Montar(CasoCuradoria c, string estadoAtual, string? decisao, string? motivo, string? produtoEscolhido,
        CriteriosCuradoria? k = null)
    {
        if (!DecisaoCuradoria.Valida(decisao)) throw Invalido("Escolha aprovar, observar, corrigir ou rejeitar.");
        if (estadoAtual is not ("aberto" or "observacao"))
            throw new GestaoException("CASO_JA_DECIDIDO", "Este caso já foi decidido. Reabra a decisão para mudar.", 409);
        var m = (motivo ?? "").Trim();
        if (m.Length == 0 && !MotivoObrigatorio(c, decisao!, k)) m = MotivoPadraoAprovacao;
        if (m.Length == 0) throw Invalido("Conte o motivo da decisão.", "MOTIVO_OBRIGATORIO");
        if (decisao == DecisaoCuradoria.Corrigir && string.IsNullOrWhiteSpace(produtoEscolhido))
            throw Invalido("Escolha o produto global do vínculo corrigido.", "ALVO_OBRIGATORIO");
        var (anterior, novo) = AntesEDepois(c, decisao!, produtoEscolhido);
        return new NovaDecisao(decisao!, Cortar(m, 500), Cortar(anterior, 300), Cortar(novo, 300),
            decisao == DecisaoCuradoria.Aprovar && !AtingeCriterios(c, k),
            decisao == DecisaoCuradoria.Observar ? c.Confirmacoes : null);
    }

    /// <summary>Reabrir não apaga: só a última decisão do caso pode ser reaberta, e reabertura não se reabre.</summary>
    public static void ValidarReabertura(RegistroDecisao alvo, IEnumerable<RegistroDecisao> doCaso, string? motivo)
    {
        if (string.IsNullOrWhiteSpace(motivo)) throw Invalido("Conte por que o caso está sendo reaberto.", "MOTIVO_OBRIGATORIO");
        if (alvo.Decisao == DecisaoCuradoria.Reabrir || EmOrdem(doCaso).LastOrDefault()?.Id != alvo.Id)
            throw new GestaoException("REGISTRO_JA_REABERTO", "Esta decisão já foi reaberta ou há outra mais recente.", 409);
    }

    /// <summary>Tipos que pedem uma escolha humana (qual lado vale, o que mesclar): nunca entram em lote.</summary>
    public static bool ExigeEscolha(string tipo) => tipo is TipoCaso.Contra or TipoCaso.Duplicidade or "inc";

    public static string? MotivoForaDoLote(CasoCuradoria c, string decisao, string estado, CriteriosCuradoria? k = null)
    {
        if (estado is not ("aberto" or "observacao")) return "Já foi decidido.";
        if (decisao == DecisaoCuradoria.Observar) return null;
        if (ExigeEscolha(c.Tipo)) return "Pede uma escolha sua (qual lado vale). Decida no caso.";
        if (!AtingeCriterios(c, k)) return "Ainda não cumpre os critérios de promoção.";
        return null;
    }

    // ------------------------------------------------------------ casos gerados dos sinais das lojas

    public static string IdTexto(string nomeChave) => "texto:" + nomeChave;
    public static string IdProduto(Guid id) => "prod:" + id;
    public static string IdDuplicidade(Guid antigo, Guid novo) => $"dup:{antigo}:{novo}";
    public static string IdCategoria(string chave) => "cat:" + chave;

    /// <summary>
    /// Texto de cupom confirmado em várias lojas. Um item global só = de-para candidato; mais de um = "lojas divergem"
    /// (associação contraditória). <c>desfeitos</c> conta vínculos que alguma loja fez e depois trocou.
    /// </summary>
    public static CasoCuradoria? CasoDeTexto(string nomeChave, IReadOnlyList<SinalDeVinculo> sinais, int desfeitos)
    {
        if (sinais.Count == 0 || sinais.Select(s => s.Loja).Distinct().Count() < MinimoLojasTexto) return null;
        var opcoes = sinais.GroupBy(s => s.GlobalId).Select(g => new OpcaoDoCaso(g.First().GlobalNome,
                $"{Plural(g.Select(s => s.Loja).Distinct().Count(), "loja", "lojas")}{(g.First().GlobalStatus == "validado" ? " · validado" : " · ainda candidato")}",
                g.Select(s => s.Loja).Distinct().Count(), g.Sum(s => s.Confirmacoes), g.Key))
            .OrderByDescending(o => o.Confirmacoes).ThenByDescending(o => o.Lojas).ToList();
        var contra = opcoes.Count > 1;
        var lojas = sinais.Select(s => s.Loja).Distinct().Count();
        var confirmacoes = sinais.Sum(s => s.Confirmacoes);
        var divergencias = desfeitos + opcoes.Skip(1).Sum(o => o.Confirmacoes);
        var entrada = sinais.GroupBy(s => s.Descricao).OrderByDescending(g => g.Count()).First().Key;
        var primeiro = sinais.MinBy(s => s.ConfirmadoEm)!;
        var historico = new List<EventoDoCaso>
        {
            new(Quando(primeiro.ConfirmadoEm), "Primeira loja vinculou", Lugar(primeiro.LojaNome, primeiro.Cidade)),
        };
        if (contra)
        {
            var segundo = sinais.Where(s => s.GlobalId != primeiro.GlobalId).MinBy(s => s.ConfirmadoEm)!;
            historico.Add(new(Quando(segundo.ConfirmadoEm), $"Surgiu o vínculo para {segundo.GlobalNome}", Lugar(segundo.LojaNome, segundo.Cidade)));
            historico.Add(new("hoje", "Lojas divergem", $"{Plural(divergencias, "divergência", "divergências")} em {confirmacoes} confirmações"));
        }
        else historico.Add(new("hoje", "Confirmado por várias lojas", $"{Plural(lojas, "loja", "lojas")} · {confirmacoes} confirmações"));
        return new CasoCuradoria(IdTexto(nomeChave), contra ? TipoCaso.Contra : TipoCaso.DePara,
            contra ? entrada : $"{entrada} → {opcoes[0].Nome}",
            contra ? string.Join(", ", opcoes.Take(2).Select(o => $"{Plural(o.Lojas, "loja diz", "lojas dizem")} {o.Nome}"))
                : $"{Plural(lojas, "loja", "lojas")} · {confirmacoes} confirmações",
            lojas, confirmacoes, divergencias, sinais.Select(s => s.Cidade).Where(c => !string.IsNullOrWhiteSpace(c)).Distinct().Count(),
            primeiro.ConfirmadoEm, entrada, opcoes,
            sinais.Select(s => s.Descricao).Distinct().Take(8).ToList(),
            contra ? "Lojas ligaram o mesmo texto a itens diferentes. Aprovar valida o mais aceito; corrigir escolhe outro. O vínculo de cada loja não muda."
                : opcoes[0].Detalhe?.Contains("candidato") == true ? "O item global ainda é candidato: aprovar também o valida para todas as lojas." : null,
            historico);
    }

    /// <summary>Produto que uma loja criou e virou candidato no catálogo global.</summary>
    public static CasoCuradoria CasoDeProduto(ItemGlobal item, IReadOnlyList<string> variacoes, IReadOnlyList<ItemGlobal> validados,
        string? lojaQueCriou)
    {
        var parecido = validados.Where(v => v.Unidade == item.Unidade)
            .Select(v => (v, s: VinculoRegras.Semelhanca(item.Nome, v.Nome)))
            .Where(x => x.s >= SemelhancaParecido).OrderByDescending(x => x.s).Select(x => x.v).FirstOrDefault();
        return new CasoCuradoria(IdProduto(item.Id), TipoCaso.Produto, item.Nome,
            item.Lojas <= 1 ? "1 loja criou" : $"{item.Lojas} lojas usam", item.Lojas, item.Confirmacoes, 0, item.Cidades, item.CriadoEm,
            null, Array.Empty<OpcaoDoCaso>(), variacoes,
            parecido is null
                ? "Produto criado por loja. Aprovar valida no catálogo global e passa a ser sugerido para quem compra o mesmo item."
                : $"Parecido com \"{parecido.Nome}\", já validado. Talvez seja o caso de corrigir para ele.",
            new[] { new EventoDoCaso(Quando(item.CriadoEm), "Loja criou o produto", lojaQueCriou ?? "Loja"),
                new EventoDoCaso("hoje", "Na fila da curadoria", $"{Plural(item.Lojas, "loja", "lojas")} · {item.Confirmacoes} confirmações") });
    }

    /// <summary>Pares de itens validados com nome muito parecido e a mesma unidade. O mais antigo fica.</summary>
    public static IReadOnlyList<CasoCuradoria> CasosDeDuplicidade(IReadOnlyList<ItemGlobal> validados)
    {
        var casos = new List<CasoCuradoria>();
        var ordem = validados.OrderBy(v => v.CriadoEm).ThenBy(v => v.Id).ToList();
        for (var i = 0; i < ordem.Count; i++)
            for (var j = i + 1; j < ordem.Count; j++)
            {
                var (a, b) = (ordem[i], ordem[j]);
                if (a.Unidade != b.Unidade || !MesmosNumeros(a.Nome, b.Nome)
                    || VinculoRegras.Semelhanca(a.Nome, b.Nome) < SemelhancaDuplicidade) continue;
                casos.Add(new CasoCuradoria(IdDuplicidade(a.Id, b.Id), TipoCaso.Duplicidade, $"{a.Nome} × {b.Nome}", "2 produtos globais",
                    a.Lojas + b.Lojas, 0, 0, a.Cidades + b.Cidades, b.CriadoEm, null,
                    new[] { Opcao(a), Opcao(b) }, Array.Empty<string>(),
                    $"Ao mesclar, os vínculos de \"{b.Nome}\" passam para \"{a.Nome}\" (o mais antigo). O insumo de cada loja não muda.",
                    new[] { new EventoDoCaso(Quando(b.CriadoEm), "Segundo produto criado", b.Nome),
                        new EventoDoCaso("hoje", "Nomes muito parecidos", "mesma unidade") }));
            }
        return casos;

        static OpcaoDoCaso Opcao(ItemGlobal g) => new(g.Nome, $"{Plural(g.Lojas, "loja", "lojas")} · desde {g.CriadoEm.ToOffset(Brasilia).ToString("MMM/yyyy", PtBr)}",
            g.Lojas, g.Confirmacoes, g.Id);
    }

    /// <summary>Categoria criada localmente por várias lojas e ausente do catálogo global (regra 7: finalidade não muda).</summary>
    public static IReadOnlyList<CasoCuradoria> CasosDeCategoria(IEnumerable<(string Categoria, Guid Loja, DateTimeOffset Em)> usos,
        IEnumerable<string?> categoriasGlobais)
    {
        var globais = categoriasGlobais.Select(DocumentoRegras.NomeChave).Where(k => k.Length > 0).ToHashSet();
        return usos.Select(u => (u.Categoria, u.Loja, u.Em, Chave: DocumentoRegras.NomeChave(u.Categoria)))
            .Where(u => u.Chave.Length > 0 && !globais.Contains(u.Chave))
            .GroupBy(u => u.Chave)
            .Where(g => g.Select(u => u.Loja).Distinct().Count() >= MinimoLojasCategoria)
            .Select(g =>
            {
                var lojas = g.Select(u => u.Loja).Distinct().Count();
                var nome = g.GroupBy(u => u.Categoria.Trim()).OrderByDescending(x => x.Count()).First().Key;
                var inicio = g.Min(u => u.Em);
                return new CasoCuradoria(IdCategoria(g.Key), TipoCaso.Categoria, nome, $"{lojas} lojas usam como categoria local", lojas,
                    g.Count(), 0, 0, inicio, null, Array.Empty<OpcaoDoCaso>(), g.Select(u => u.Categoria.Trim()).Distinct().Take(8).ToList(),
                    "Categoria criada localmente por várias lojas. Aprovar disponibiliza para todas, sem mudar as categorias atuais.",
                    new[] { new EventoDoCaso(Quando(inicio), "Primeira loja criou a categoria", nome),
                        new EventoDoCaso("hoje", "Usada por várias lojas", Plural(lojas, "loja", "lojas")) });
            }).ToList();
    }

    /// <summary>"Óleo 500 ml" e "Óleo 900 ml" não são o mesmo produto: os números do nome (medida) precisam bater.</summary>
    private static bool MesmosNumeros(string a, string b)
    {
        static string Numeros(string s) => string.Join(' ', Regex.Matches(s, @"\d+").Select(m => m.Value));
        return Numeros(a) == Numeros(b);
    }

    private static string Quando(DateTimeOffset em) => em.ToOffset(Brasilia).ToString("dd/MM", PtBr);
    private static string Lugar(string? loja, string? cidade) =>
        string.Join(" · ", new[] { loja, cidade }.Where(s => !string.IsNullOrWhiteSpace(s))) is { Length: > 0 } t ? t : "Loja";
    private static string Plural(int n, string um, string varios) => $"{n} {(n == 1 ? um : varios)}";
    private static string Cortar(string s, int max) => s.Length <= max ? s : s[..max];
    private static GestaoException Invalido(string m, string codigo = "CAMPO_INVALIDO") => new(codigo, m, 422);
}
