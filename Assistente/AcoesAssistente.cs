using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using APIBack.DTOs.Cardapio;
using APIBack.DTOs.Configuracoes;
using APIBack.DTOs.Delivery;
using APIBack.Repository.Interface;
using APIBack.Service;
using APIBack.Service.Interface;

namespace APIBack.Assistente;

/// <summary>Quem está agindo: loja e usuário do token, e as permissões dele (as mesmas do <c>[RequirePermission]</c> da tela).</summary>
public sealed record ContextoAcao(Guid Loja, int Usuario, Func<string, string, bool> TemPermissao, IReadOnlyCollection<string> ModulosAtivos);

/// <summary>Parâmetros que a IA pode mandar em <c>propor_acao</c> (todos opcionais; cada ação usa os seus).</summary>
public sealed record ParametrosAcao(string? Produto, string? Data, bool? Fechado, string? AbreAs, string? FechaAs, string? Motivo);

/// <summary>Ação resolvida e ainda não executada: o que vai mudar (antes → depois) e os parâmetros já normalizados (ids).</summary>
public sealed record AcaoPreparada(string Resumo, string? Antes, string? Depois, JsonObject Parametros);

/// <summary>Ação inversa sugerida depois de executar (vira uma nova ação pendente).</summary>
public sealed record Desfazer(string Acao, AcaoPreparada Preparada);

public sealed record AcaoExecutada(string Mensagem, Desfazer? Desfazer);

/// <summary>Erro esperado de uma ação: na proposta vira resposta para a IA; no confirmar vira o HTTP <see cref="Status"/>.</summary>
public sealed class AcaoAssistenteException(int status, string codigo, string mensagem) : Exception(mensagem)
{
    public int Status { get; } = status;
    public string Codigo { get; } = codigo;
}

/// <summary>
/// Uma ação que a IA pode propor (modo <c>ia_faz</c> no catálogo). <see cref="Preparar"/> só lê; <see cref="Executar"/> chama o
/// mesmo serviço da tela e só roda pelo endpoint confirmar. A permissão exigida é a da rota da tela.
/// </summary>
public interface IAcaoAssistente
{
    string Nome { get; }
    string PermissaoModulo { get; }
    string PermissaoAcao { get; }
    /// <summary>Módulo comercial que precisa estar ativo na loja (nulo = nenhum).</summary>
    string? ModuloAtivo { get; }
    Task<AcaoPreparada> Preparar(ContextoAcao ctx, ParametrosAcao p, CancellationToken ct);
    Task<AcaoExecutada> Executar(ContextoAcao ctx, JsonObject parametros, CancellationToken ct);
}

public static class AcoesRegras
{
    private static readonly CultureInfo PtBr = CultureInfo.GetCultureInfo("pt-BR");

    public static AcaoAssistenteException Invalida(string mensagem) => new(422, "PARAMETRO_INVALIDO", mensagem);

    /// <summary>Texto sem acento e minúsculo, para comparar nome de produto.</summary>
    public static string Normalizar(string? s)
    {
        var d = (s ?? "").Trim().ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(d.Length);
        foreach (var c in d)
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) sb.Append(c);
        return string.Join(' ', sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>Escolhe o produto pedido: nome igual (sem acento) ganha; senão, se só um contém o texto, é ele; senão pergunta.</summary>
    public static CardapioProdutoContractDto EscolherProduto(string? pedido, IReadOnlyList<CardapioProdutoContractDto> candidatos)
    {
        var alvo = Normalizar(pedido);
        if (alvo.Length == 0) throw Invalida("Diga qual produto.");
        var iguais = candidatos.Where(c => Normalizar(c.Nome) == alvo).ToList();
        if (iguais.Count == 1) return iguais[0];
        var contem = iguais.Count > 1 ? iguais : candidatos.Where(c => Normalizar(c.Nome).Contains(alvo)).ToList();
        if (contem.Count == 1) return contem[0];
        if (contem.Count == 0)
            throw new AcaoAssistenteException(404, "PRODUTO_NAO_ENCONTRADO", $"Não achei produto com o nome \"{pedido}\" no cardápio.");
        throw Invalida($"Achei mais de um produto: {string.Join(", ", contem.Take(6).Select(c => c.Nome))}. Pergunte qual.");
    }

    public static DateOnly Data(string? valor)
    {
        if (DateOnly.TryParseExact(valor, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)) return d;
        throw Invalida("Data inválida: use AAAA-MM-DD.");
    }

    public static string Hora(string? valor, string campo)
    {
        if (TimeOnly.TryParseExact(valor, ["HH:mm", "H:mm"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var h)) return h.ToString("HH:mm");
        throw Invalida($"{campo} inválido: use HH:mm.");
    }

    /// <summary>"sexta, 20/11".</summary>
    public static string DataCurta(DateOnly d) => $"{PtBr.DateTimeFormat.GetDayName(d.DayOfWeek).Split('-')[0]}, {d:dd/MM}";

    public static string DescreverEspecial(bool fechado, string? abre, string? fecha) =>
        fechado ? "Fechado o dia todo" : $"Aberto das {abre} às {fecha}";

    public static string? Texto(JsonObject o, string campo) => o[campo]?.GetValue<string>();
}

/// <summary>Pausar e voltar produto: <c>PATCH api/cardapio/{loja}/produtos/{id}/disponivel</c>.</summary>
public abstract class AcaoDisponibilidadeProduto(ICardapioContractService cardapio, bool disponivel) : IAcaoAssistente
{
    public abstract string Nome { get; }
    public string PermissaoModulo => "Cardapio";
    public string PermissaoAcao => "editar";
    public string? ModuloAtivo => "Cardapio";

    public async Task<AcaoPreparada> Preparar(ContextoAcao ctx, ParametrosAcao p, CancellationToken ct)
    {
        var termo = (p.Produto ?? "").Trim();
        var lista = await cardapio.ListarProdutosAsync(ctx.Loja, termo, null, null, 1, 20);
        var produto = AcoesRegras.EscolherProduto(termo, lista.Itens.ToList());
        if (produto.Disponivel == disponivel)
            throw new AcaoAssistenteException(409, "SEM_MUDANCA",
                disponivel ? $"{produto.Nome} já está disponível." : $"{produto.Nome} já está pausado.");
        return Preparada(produto.Id, produto.Nome);
    }

    internal AcaoPreparada Preparada(Guid id, string nome) => new(
        disponivel ? $"Voltar {nome} ao cardápio" : $"Pausar {nome}",
        disponivel ? "Pausado" : "Disponível",
        disponivel ? "Disponível" : "Pausado",
        new JsonObject { ["produtoId"] = id.ToString(), ["produtoNome"] = nome });

    protected abstract AcaoDisponibilidadeProduto Inversa { get; }

    public async Task<AcaoExecutada> Executar(ContextoAcao ctx, JsonObject parametros, CancellationToken ct)
    {
        var id = Guid.Parse(AcoesRegras.Texto(parametros, "produtoId")!);
        var nome = AcoesRegras.Texto(parametros, "produtoNome") ?? "Produto";
        if (!await cardapio.AtualizarProdutoDisponibilidadeAsync(ctx.Loja, id, disponivel))
            throw new AcaoAssistenteException(404, "PRODUTO_NAO_ENCONTRADO", "Esse produto não existe mais no cardápio.");
        return new AcaoExecutada(disponivel ? $"Pronto: {nome} voltou ao cardápio." : $"Pronto: {nome} pausado.",
            new Desfazer(Inversa.Nome, Inversa.Preparada(id, nome)));
    }
}

public sealed class AcaoPausarProduto(ICardapioContractService cardapio) : AcaoDisponibilidadeProduto(cardapio, false)
{
    private readonly ICardapioContractService _cardapio = cardapio;
    public const string NomeAcao = "produto.pausar";
    public override string Nome => NomeAcao;
    protected override AcaoDisponibilidadeProduto Inversa => new AcaoVoltarProduto(_cardapio);
}

public sealed class AcaoVoltarProduto(ICardapioContractService cardapio) : AcaoDisponibilidadeProduto(cardapio, true)
{
    private readonly ICardapioContractService _cardapio = cardapio;
    public const string NomeAcao = "produto.voltar";
    public override string Nome => NomeAcao;
    protected override AcaoDisponibilidadeProduto Inversa => new AcaoPausarProduto(_cardapio);
}

/// <summary>Criar (ou trocar) horário especial de um dia: <c>POST api/v2/delivery/horarios-especiais</c>.</summary>
public sealed class AcaoCriarHorarioEspecial(IHorarioOperacaoRepository horarios) : IAcaoAssistente
{
    public const string NomeAcao = "horario-especial.criar";
    public string Nome => NomeAcao;
    public string PermissaoModulo => "Delivery";
    public string PermissaoAcao => "configurar";
    public string? ModuloAtivo => null;

    public async Task<AcaoPreparada> Preparar(ContextoAcao ctx, ParametrosAcao p, CancellationToken ct)
    {
        var novo = Normalizar(p);
        var anterior = (await horarios.ListarEspeciaisAsync(ctx.Loja)).FirstOrDefault(e => e.Data == novo.Data);
        return Montar(novo, anterior);
    }

    /// <summary>Valida e normaliza o pedido. Público para teste.</summary>
    public static SalvarHorarioEspecialRequest Normalizar(ParametrosAcao p)
    {
        var data = AcoesRegras.Data(p.Data);
        var fechado = p.Fechado ?? true;
        string? abre = null, fecha = null;
        if (!fechado)
        {
            abre = AcoesRegras.Hora(p.AbreAs, "Horário de abrir");
            fecha = AcoesRegras.Hora(p.FechaAs, "Horário de fechar");
            if (abre == fecha) throw AcoesRegras.Invalida("Abrir e fechar precisam ser horários diferentes.");
        }
        var motivo = string.IsNullOrWhiteSpace(p.Motivo) ? null : p.Motivo.Trim()[..Math.Min(p.Motivo.Trim().Length, 120)];
        return new SalvarHorarioEspecialRequest { Data = data.ToString("yyyy-MM-dd"), Fechado = fechado, AbreAs = abre, FechaAs = fecha, Motivo = motivo };
    }

    public static AcaoPreparada Montar(SalvarHorarioEspecialRequest novo, HorarioEspecialDto? anterior)
    {
        var dia = AcoesRegras.DataCurta(AcoesRegras.Data(novo.Data));
        var depois = AcoesRegras.DescreverEspecial(novo.Fechado, novo.AbreAs, novo.FechaAs);
        var antes = anterior is null ? "Horário normal da semana" : AcoesRegras.DescreverEspecial(anterior.Fechado, anterior.AbreAs, anterior.FechaAs);
        return new AcaoPreparada(
            novo.Fechado ? $"Fechar a loja em {dia}" : $"Horário especial em {dia}: {novo.AbreAs}–{novo.FechaAs}",
            antes, depois,
            new JsonObject
            {
                ["data"] = novo.Data, ["fechado"] = novo.Fechado, ["abreAs"] = novo.AbreAs, ["fechaAs"] = novo.FechaAs, ["motivo"] = novo.Motivo,
                ["anterior"] = anterior is null ? null : new JsonObject
                {
                    ["fechado"] = anterior.Fechado, ["abreAs"] = anterior.AbreAs, ["fechaAs"] = anterior.FechaAs, ["motivo"] = anterior.Motivo,
                },
            });
    }

    public async Task<AcaoExecutada> Executar(ContextoAcao ctx, JsonObject parametros, CancellationToken ct)
    {
        var req = new SalvarHorarioEspecialRequest
        {
            Data = AcoesRegras.Texto(parametros, "data")!,
            Fechado = parametros["fechado"]?.GetValue<bool>() ?? true,
            AbreAs = AcoesRegras.Texto(parametros, "abreAs"),
            FechaAs = AcoesRegras.Texto(parametros, "fechaAs"),
            Motivo = AcoesRegras.Texto(parametros, "motivo"),
        };
        var salvo = await horarios.SalvarEspecialAsync(ctx.Loja, req);
        var dia = AcoesRegras.DataCurta(AcoesRegras.Data(req.Data));
        Desfazer desfazer;
        if (parametros["anterior"] is JsonObject ant)
        {
            // Já havia exceção nesse dia: desfazer = voltar a que existia.
            var antigo = new SalvarHorarioEspecialRequest
            {
                Data = req.Data, Fechado = ant["fechado"]?.GetValue<bool>() ?? true,
                AbreAs = AcoesRegras.Texto(ant, "abreAs"), FechaAs = AcoesRegras.Texto(ant, "fechaAs"), Motivo = AcoesRegras.Texto(ant, "motivo"),
            };
            desfazer = new Desfazer(NomeAcao, Montar(antigo, salvo));
        }
        else
        {
            desfazer = new Desfazer(AcaoRemoverHorarioEspecial.NomeAcao, AcaoRemoverHorarioEspecial.Montar(salvo));
        }
        return new AcaoExecutada($"Pronto: {dia} ficou {AcoesRegras.DescreverEspecial(req.Fechado, req.AbreAs, req.FechaAs).ToLowerInvariant()}.", desfazer);
    }
}

/// <summary>Tirar o horário especial de um dia (volta a grade da semana): <c>DELETE api/v2/delivery/horarios-especiais/{id}</c>.</summary>
public sealed class AcaoRemoverHorarioEspecial(IHorarioOperacaoRepository horarios) : IAcaoAssistente
{
    public const string NomeAcao = "horario-especial.remover";
    public string Nome => NomeAcao;
    public string PermissaoModulo => "Delivery";
    public string PermissaoAcao => "configurar";
    public string? ModuloAtivo => null;

    public async Task<AcaoPreparada> Preparar(ContextoAcao ctx, ParametrosAcao p, CancellationToken ct)
    {
        var data = AcoesRegras.Data(p.Data).ToString("yyyy-MM-dd");
        var atual = (await horarios.ListarEspeciaisAsync(ctx.Loja)).FirstOrDefault(e => e.Data == data)
            ?? throw new AcaoAssistenteException(404, "HORARIO_NAO_ENCONTRADO", $"Não há horário especial em {AcoesRegras.DataCurta(AcoesRegras.Data(data))}.");
        return Montar(atual);
    }

    public static AcaoPreparada Montar(HorarioEspecialDto atual) => new(
        $"Tirar o horário especial de {AcoesRegras.DataCurta(AcoesRegras.Data(atual.Data))}",
        AcoesRegras.DescreverEspecial(atual.Fechado, atual.AbreAs, atual.FechaAs),
        "Horário normal da semana",
        new JsonObject
        {
            ["data"] = atual.Data, ["fechado"] = atual.Fechado, ["abreAs"] = atual.AbreAs, ["fechaAs"] = atual.FechaAs, ["motivo"] = atual.Motivo,
        });

    public async Task<AcaoExecutada> Executar(ContextoAcao ctx, JsonObject parametros, CancellationToken ct)
    {
        var data = AcoesRegras.Texto(parametros, "data")!;
        var atual = (await horarios.ListarEspeciaisAsync(ctx.Loja)).FirstOrDefault(e => e.Data == data);
        if (atual is null || !await horarios.ExcluirEspecialAsync(ctx.Loja, atual.Id))
            throw new AcaoAssistenteException(404, "HORARIO_NAO_ENCONTRADO", "Esse horário especial já não existe.");
        var antigo = new SalvarHorarioEspecialRequest { Data = atual.Data, Fechado = atual.Fechado, AbreAs = atual.AbreAs, FechaAs = atual.FechaAs, Motivo = atual.Motivo };
        return new AcaoExecutada($"Pronto: {AcoesRegras.DataCurta(AcoesRegras.Data(data))} volta ao horário normal.",
            new Desfazer(AcaoCriarHorarioEspecial.NomeAcao, AcaoCriarHorarioEspecial.Montar(antigo, null)));
    }
}

/// <summary>Abrir a loja hoje até HH:mm: <c>POST api/configuracoes/estabelecimento/negocio/operacao/abrir-hoje</c>.</summary>
public sealed class AcaoAbrirLojaHoje(IOperacaoLojaService operacao) : IAcaoAssistente
{
    public const string NomeAcao = "loja.abrir-hoje";
    public string Nome => NomeAcao;
    public string PermissaoModulo => "Configuracoes";
    public string PermissaoAcao => "editar";
    public string? ModuloAtivo => null;

    public async Task<AcaoPreparada> Preparar(ContextoAcao ctx, ParametrosAcao p, CancellationToken ct)
    {
        var fecha = AcoesRegras.Hora(p.FechaAs, "Horário de fechar");
        var estado = await operacao.StatusAsync(ctx.Loja, ct);
        return Montar(estado, fecha);
    }

    /// <summary>Público para teste.</summary>
    public static AcaoPreparada Montar(StoreOperationDto estado, string fechaAs)
    {
        if (estado.AbertoAgora)
            throw new AcaoAssistenteException(409, "SEM_MUDANCA", estado.FechaAs is null ? "A loja já está aberta." : $"A loja já está aberta até {estado.FechaAs}.");
        if (!estado.PodeAbrir)
            throw new AcaoAssistenteException(422, "NAO_PODE_ABRIR",
                "A loja não pode abrir agora" + (string.IsNullOrWhiteSpace(estado.ComoResolver) ? "." : $": {estado.ComoResolver}"));
        // Se o horário serve (inclusive virar a meia-noite) quem decide é o serviço da tela, no confirmar.
        return new AcaoPreparada($"Abrir a loja hoje até {fechaAs}", "Fechada", $"Aberta hoje até {fechaAs}",
            new JsonObject { ["dataLocal"] = estado.DataLocal, ["fechaAs"] = fechaAs });
    }

    public async Task<AcaoExecutada> Executar(ContextoAcao ctx, JsonObject parametros, CancellationToken ct)
    {
        var fecha = AcoesRegras.Texto(parametros, "fechaAs")!;
        await operacao.AbrirHojeAsync(ctx.Loja, ctx.Usuario, new OpenStoreTodayRequest
        {
            EstabelecimentoId = ctx.Loja, DataLocal = AcoesRegras.Texto(parametros, "dataLocal")!, FechaAs = fecha,
        }, ct);
        return new AcaoExecutada($"Pronto: loja aberta hoje até {fecha}.", null);
    }
}
