using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using APIBack.DTOs.Common;

namespace APIBack.Service
{
    /// <summary>
    /// Para onde levar quem precisa corrigir. area/foco usam as chaves das Configuracoes do front
    /// (negocio/endereco, negocio/horarios, delivery, atendimento/numeros, ifood, impressao, equipe, remuneracao);
    /// rota e um caminho do app (/apps/cardapio, /apps/cardapioweb).
    /// </summary>
    public sealed record CorrecaoDto(
        [property: JsonPropertyName("area"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Area = null,
        [property: JsonPropertyName("foco"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Foco = null,
        [property: JsonPropertyName("rota"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Rota = null)
    {
        public static CorrecaoDto Config(string area, string? foco = null) => new(area, foco);
        public static CorrecaoDto Pagina(string rota) => new(Rota: rota);
    }

    /// <summary>Quem resolve: a propria loja, a equipe da Zippy (Gestao) ou a infraestrutura (variaveis do servidor).</summary>
    public static class QuemResolve
    {
        public const string Loja = "loja";
        public const string Zippy = "zippy";
        public const string Infra = "infra";
    }

    /// <summary>Pendencia citada dentro de um erro (resumo de um item da prontidao).</summary>
    public sealed record PendenciaErroDto(string Codigo, string Titulo, string Mensagem, string ComoResolver, string QuemResolve, CorrecaoDto? Correcao);

    /// <summary>Corpo de <c>details</c> de toda recusa operacional: o que fazer e para onde ir.</summary>
    public sealed record DetalhesErroOperacional(
        [property: JsonPropertyName("comoResolver")] string ComoResolver,
        [property: JsonPropertyName("quemResolve")] string QuemResolve,
        [property: JsonPropertyName("correcao"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] CorrecaoDto? Correcao,
        [property: JsonPropertyName("pendencias"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<PendenciaErroDto>? Pendencias = null,
        [property: JsonPropertyName("dados"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyDictionary<string, object?>? Dados = null);

    public sealed record ErroOperacional(string Code, int Status, string Mensagem, string ComoResolver, string QuemResolve, CorrecaoDto? Correcao);

    /// <summary>
    /// Catalogo unico das recusas operacionais: code estavel, mensagem em portugues simples, como resolver, quem resolve e para
    /// onde ir. Nenhuma recusa de operacao sai sem comoResolver. A mensagem pode ser trocada por uma mais especifica
    /// (ex.: com a distancia), mas o code e o caminho de correcao vem sempre daqui.
    /// </summary>
    public static class ErrosOperacionais
    {
        public const string ProntidaoIncompleta = "PRONTIDAO_INCOMPLETA";
        public const string OperacaoNaoIniciada = "OPERACAO_NAO_INICIADA";
        public const string EstabelecimentoBloqueado = "ESTABELECIMENTO_BLOQUEADO";
        public const string StoreClosed = "STORE_CLOSED";
        public const string StoreNotAccepting = "STORE_NOT_ACCEPTING";
        public const string LojaFechadaCardapio = "loja_fechada";
        public const string MinimumNotReached = "MINIMUM_NOT_REACHED";
        public const string OutOfDeliveryRange = "OUT_OF_DELIVERY_RANGE";
        public const string RaioEntregaNaoConfigurado = "RAIO_ENTREGA_NAO_CONFIGURADO";
        public const string LojaSemCoordenada = "LOJA_SEM_COORDENADA";
        public const string CardapioSemWhatsapp = "CARDAPIO_SEM_WHATSAPP";
        public const string CardapioIndisponivel = "CARDAPIO_INDISPONIVEL";
        public const string VagasMotoboyEsgotadas = "VAGAS_MOTOBOY_ESGOTADAS";
        public const string DeliveryTrackingDisabled = "DELIVERY_TRACKING_DISABLED";
        public const string ModuloInativo = "MODULO_INATIVO";
        public const string RiderPayPlanRequired = "RIDER_PAY_PLAN_REQUIRED";
        public const string OperacaoJaIniciada = "OPERACAO_JA_INICIADA";
        public const string LojaSemHorario = "SEM_HORARIO";
        public const string LojaForaHorario = "FORA_HORARIO";

        private static readonly Dictionary<string, ErroOperacional> Itens = Montar();

        public static IReadOnlyCollection<ErroOperacional> Todos => Itens.Values;

        public static bool Existe(string code) => Itens.ContainsKey(code);

        public static ErroOperacional Obter(string code) =>
            Itens.TryGetValue(code, out var erro) ? erro : throw new KeyNotFoundException($"Erro operacional sem cadastro: {code}.");

        private static Dictionary<string, ErroOperacional> Montar()
        {
            var lista = new[]
            {
                new ErroOperacional(ProntidaoIncompleta, 422,
                    "Ainda faltam itens para começar a operar.",
                    "Conclua os itens obrigatórios da lista de prontidão e tente de novo.",
                    QuemResolve.Loja, null),
                new ErroOperacional(OperacaoNaoIniciada, 403,
                    "A operação desta loja ainda não foi iniciada.",
                    "Conclua os itens obrigatórios da lista de prontidão e toque em \"Começar a operar\".",
                    QuemResolve.Loja, null),
                new ErroOperacional(EstabelecimentoBloqueado, 403,
                    "Acesso suspenso. Fale com a Zippy para reativar.",
                    "Fale com a Zippy: o motivo do bloqueio aparece na situação da conta.",
                    QuemResolve.Zippy, null),
                new ErroOperacional(StoreClosed, 409,
                    "A loja está fechada agora.",
                    "Cadastre o horário semanal ou abra a loja manualmente por hoje.",
                    QuemResolve.Loja, CorrecaoDto.Config("negocio", "horarios")),
                new ErroOperacional(LojaFechadaCardapio, 409,
                    "A loja está fechada agora e não está aceitando pedidos.",
                    "Cadastre o horário semanal ou abra a loja manualmente por hoje.",
                    QuemResolve.Loja, CorrecaoDto.Config("negocio", "horarios")),
                new ErroOperacional(StoreNotAccepting, 409,
                    "A loja pausou os pedidos no momento.",
                    "Reabra a loja para voltar a receber pedidos.",
                    QuemResolve.Loja, CorrecaoDto.Config("negocio", "horarios")),
                new ErroOperacional(MinimumNotReached, 422,
                    "O pedido não atingiu o valor mínimo da loja.",
                    "Adicione itens ao pedido ou ajuste o pedido mínimo em Delivery.",
                    QuemResolve.Loja, CorrecaoDto.Config("delivery")),
                new ErroOperacional(OutOfDeliveryRange, 422,
                    "O endereço está fora da área de entrega.",
                    "Confira o endereço ou aumente o raio de entrega em Delivery.",
                    QuemResolve.Loja, CorrecaoDto.Config("delivery")),
                new ErroOperacional(RaioEntregaNaoConfigurado, 422,
                    "A loja ainda não tem o raio de entrega configurado.",
                    "Informe o raio de entrega (em km, maior que zero) em Configurações > Delivery.",
                    QuemResolve.Loja, CorrecaoDto.Config("delivery")),
                new ErroOperacional(LojaSemCoordenada, 422,
                    "A loja ainda não tem a posição no mapa.",
                    "Complete o endereço da loja em Configurações > Negócio > Endereço e confirme o ponto no mapa.",
                    QuemResolve.Loja, CorrecaoDto.Config("negocio", "endereco")),
                new ErroOperacional(CardapioSemWhatsapp, 409,
                    "Este restaurante ainda não recebe pedidos pelo WhatsApp.",
                    "Conecte um número de WhatsApp para o cardápio web em Configurações > Atendimento > Números.",
                    QuemResolve.Loja, CorrecaoDto.Config("atendimento", "numeros")),
                new ErroOperacional(CardapioIndisponivel, 404,
                    "Loja fechada ou indisponível no momento. Tente mais tarde.",
                    "O dono vê o motivo exato na lista de prontidão da loja.",
                    QuemResolve.Loja, CorrecaoDto.Pagina("/apps/cardapioweb")),
                new ErroOperacional(VagasMotoboyEsgotadas, 409,
                    "As vagas de motoboy deste dia operacional acabaram.",
                    "Peça mais motoboys à Zippy ou aguarde o próximo dia operacional.",
                    QuemResolve.Zippy, null),
                new ErroOperacional(DeliveryTrackingDisabled, 503,
                    "O rastreamento do delivery ainda não foi ativado neste ambiente.",
                    "A Zippy precisa ligar DeliveryTracking:Enabled no servidor.",
                    QuemResolve.Infra, null),
                new ErroOperacional(ModuloInativo, 403,
                    "Este recurso não faz parte do plano da loja.",
                    "Peça à Zippy para incluir o módulo no plano da loja.",
                    QuemResolve.Zippy, null),
                new ErroOperacional(RiderPayPlanRequired, 422,
                    "Defina quanto você paga aos motoboys antes de atribuir entregas.",
                    "Configure a remuneração padrão da loja (ou a de um motoboy) em Configurações > Remuneração.",
                    QuemResolve.Loja, CorrecaoDto.Config("remuneracao")),
                new ErroOperacional(OperacaoJaIniciada, 409,
                    "A operação desta loja já foi iniciada.",
                    "Nada a fazer: a loja já está em teste ou ativa.",
                    QuemResolve.Loja, null),
                new ErroOperacional(LojaSemHorario, 409,
                    "A loja não tem horário de funcionamento cadastrado para hoje.",
                    "Cadastre o horário semanal ou abra a loja manualmente por hoje.",
                    QuemResolve.Loja, CorrecaoDto.Config("negocio", "horarios")),
                new ErroOperacional(LojaForaHorario, 409,
                    "A loja está fora do horário de funcionamento.",
                    "Espere o próximo horário de abertura, ajuste o horário semanal ou abra a loja manualmente por hoje.",
                    QuemResolve.Loja, CorrecaoDto.Config("negocio", "horarios")),
            };
            return lista.ToDictionary(e => e.Code, StringComparer.Ordinal);
        }

        public static DetalhesErroOperacional Detalhes(string code, IEnumerable<PendenciaErroDto>? pendencias = null,
            IReadOnlyDictionary<string, object?>? dados = null)
        {
            var erro = Obter(code);
            var lista = pendencias?.ToList();
            return new DetalhesErroOperacional(erro.ComoResolver, erro.QuemResolve, erro.Correcao,
                lista is { Count: > 0 } ? lista : null, dados is { Count: > 0 } ? dados : null);
        }

        /// <summary>Excecao de dominio pronta: status, code e details (comoResolver/correcao) saem do catalogo.</summary>
        public static DeliveryDomainException Excecao(string code, string? mensagem = null,
            IEnumerable<PendenciaErroDto>? pendencias = null, IReadOnlyDictionary<string, object?>? dados = null)
        {
            var erro = Obter(code);
            return new DeliveryDomainException(erro.Status, code, mensagem ?? erro.Mensagem, Detalhes(code, pendencias, dados));
        }

        /// <summary>Corpo JSON { success:false, error, code, details } para respostas escritas direto no pipeline.</summary>
        public static ApiResponse<object> Corpo(string code, string? mensagem = null,
            IEnumerable<PendenciaErroDto>? pendencias = null, IReadOnlyDictionary<string, object?>? dados = null) =>
            ApiResponse<object>.Fail(mensagem ?? Obter(code).Mensagem, code, Detalhes(code, pendencias, dados));
    }
}
