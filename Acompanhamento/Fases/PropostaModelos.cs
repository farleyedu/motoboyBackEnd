using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using APIBack.DTOs.CRM;
using APIBack.Payments.Assinaturas;

namespace APIBack.Acompanhamento.Fases
{
    public static class StatusProposta
    {
        public const string Rascunho = "rascunho", Enviada = "enviada", EmNegociacao = "em_negociacao", Aceita = "aceita",
            Recusada = "recusada", Substituida = "substituida", Guardada = "guardada";

        /// <summary>Enviada e ainda sem desfecho: e a que pode ser aceita, respondida ou substituida.</summary>
        public static bool Vigente(string status) => status is Enviada or EmNegociacao;
    }

    public static class CanaisProposta
    {
        public const string NaoInformado = "nao_informado";
        /// <summary>Canais que uma pessoa declara. "nao_informado" so existe para o que veio do /crm legado.</summary>
        public static readonly string[] Registraveis = { "whatsapp", "email", "presencial", "ligacao", "reuniao" };
    }

    public static class RespostasProposta
    {
        public const string Interessado = "interessado", PediuAjuste = "pediu_ajuste", Recusou = "recusou", SemResposta = "sem_resposta";
        public static readonly string[] Todas = { Interessado, PediuAjuste, Recusou, SemResposta };
    }

    public static class OrigensProposta
    {
        public const string Acompanhamento = "acompanhamento", CrmLegado = "crm_legado";
    }

    /// <summary>Campos de <c>dados</c>: os de <see cref="CrmPropostaDto"/> mais o plano e o que se sabe da conversa. Nunca um preco inventado.</summary>
    public class PropostaDados : CrmPropostaDto
    {
        public string? Plano { get; set; }
        public string? QuemDecide { get; set; }
        public string? Contato { get; set; }
        public string? Telefone { get; set; }
        public string? Email { get; set; }
        public int? PedidosPorDia { get; set; }
        public string? DorPrincipal { get; set; }
    }

    /// <summary>Corpo do POST (cria rascunho ou revisao) e do PUT (edita rascunho). Campo omitido fica como esta; texto vazio apaga.</summary>
    public sealed class PropostaRequest : PropostaDados
    {
        public decimal? ValorMensal { get; set; }
        public decimal? ValorImplantacao { get; set; }
        /// <summary>Confirma R$ 0 de proposito. Vale so para esta versao e fica no historico.</summary>
        public bool? ValorZeroConsciente { get; set; }
        /// <summary>POST: versao de origem da revisao. Omitido = a ultima.</summary>
        public Guid? RevisarVersaoId { get; set; }
        /// <summary>PUT: <c>edicao</c> que a tela leu. Diferente = 409 PROPOSTA_MUDOU.</summary>
        public long? VersaoEsperada { get; set; }
    }

    public sealed class EnviarPropostaRequest
    {
        public string? Canal { get; set; }
        public string? Destinatario { get; set; }
        /// <summary>Quando foi enviada. Omitido = agora; no futuro = 422.</summary>
        public DateTimeOffset? EnviadaEm { get; set; }
        /// <summary>Mensagem real em gestao_mensagem. Tem de ser saida entregue desta oportunidade ou loja.</summary>
        public Guid? MensagemId { get; set; }
        public string? ChaveIdempotencia { get; set; }
        public long? VersaoEsperada { get; set; }
    }

    public sealed class ResponderPropostaRequest
    {
        public string? Resposta { get; set; }
        public string? Nota { get; set; }
        public string? ChaveIdempotencia { get; set; }
        public long? VersaoEsperada { get; set; }
    }

    /// <summary>Uma linha de acompanhamento_proposta_versao.</summary>
    public sealed class PropostaVersao
    {
        public Guid Id { get; set; }
        public Guid OportunidadeId { get; set; }
        public int Versao { get; set; }
        public long Edicao { get; set; } = 1;
        public string Status { get; set; } = StatusProposta.Rascunho;
        public decimal? ValorMensal { get; set; }
        public decimal? ValorImplantacao { get; set; }
        public bool ValorZeroConsciente { get; set; }
        public PropostaDados Dados { get; set; } = new();
        public string? Canal { get; set; }
        public string? Destinatario { get; set; }
        public int? ApresentadaPor { get; set; }
        public Guid? EvidenciaMensagemId { get; set; }
        public string? Origem { get; set; }
        public int? RevisaVersao { get; set; }
        public string? Resposta { get; set; }
        public string? RespostaNota { get; set; }
        public DateTimeOffset? RespostaEm { get; set; }
        public string? ChaveEnvio { get; set; }
        public string? ChaveResposta { get; set; }
        public int? CriadaPor { get; set; }
        public DateTimeOffset CriadaEm { get; set; }
        public DateTimeOffset? EnviadaEm { get; set; }
        public DateOnly? ValidadeAte { get; set; }
        public DateTimeOffset AtualizadaEm { get; set; }
        public Guid? PassagemId { get; set; }
    }

    /// <summary>O que a oportunidade ja sabe, para nao digitar de novo.</summary>
    public sealed record PropostaContexto(string? Contato, string? Telefone, string? Email, string? Decisor, decimal? ValorMensal, decimal? ValorImplantacao);

    /// <summary>"Pronta" e "vencida" sao derivadas, nao status. <c>Edicao</c> e o valor de <c>versaoEsperada</c>.</summary>
    public sealed record PropostaDto(
        Guid Id, Guid OportunidadeId, int Versao, long Edicao, string Status, decimal? ValorMensal, decimal? ValorImplantacao, bool ValorZeroConsciente,
        PropostaDados Dados, string? Canal, string? Destinatario, int? ApresentadaPor, Guid? EvidenciaMensagemId, string? Origem, int? RevisaVersao,
        string? Resposta, string? RespostaNota, DateTimeOffset? RespostaEm, int? CriadaPor, DateTimeOffset CriadaEm, DateTimeOffset? EnviadaEm,
        DateOnly? ValidadeAte, DateTimeOffset AtualizadaEm, bool Pronta, IReadOnlyList<RequisitoEstadoDto> Pendencias, bool Vencida);

    /// <summary>GET .../propostas. <c>PodeRegistrar</c> = ha oportunidade e a fase aceita proposta (Conversa, Proposta, Teste).</summary>
    public sealed record PropostasDto(Guid AcompanhamentoId, Guid? OportunidadeId, string FaseAtual, bool PodeRegistrar, PropostaDto? Atual, IReadOnlyList<PropostaDto> Versoes);

    /// <summary>Resposta das escritas. <c>Passagem</c> so existe quando o envio moveu de Conversa para Proposta.</summary>
    public sealed record PropostaResultadoDto(Guid AcompanhamentoId, string FaseAtual, long VersaoAcompanhamento, bool Repetida, PropostaDto Proposta, PassagemRegistroDto? Passagem);

    /// <summary>Regras puras da proposta: sem banco, sem relogio proprio.</summary>
    public static class PropostaRegras
    {
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        /// <summary>Mensalidade informada; R$ 0 so com confirmacao explicita.</summary>
        public static bool ValorDefinido(decimal? valor, bool zeroConsciente) => valor > 0 || (valor == 0 && zeroConsciente);

        public static DateOnly Validade(DateTimeOffset enviadaEm) => FusoComercial.Data(enviadaEm).AddDays(PassagemMotor.DiasValidadeProposta);

        public static bool Vencida(PropostaVersao v, DateOnly hoje) => StatusProposta.Vigente(v.Status) && v.ValidadeAte < hoje;

        public static bool FaseAceita(string fase) => fase is FasesAcompanhamento.Conversa or FasesAcompanhamento.Proposta or FasesAcompanhamento.Teste;

        /// <summary>O que falta para o rascunho estar pronto: os requisitos da antiga "Montar a proposta" mais a mensalidade.</summary>
        public static IReadOnlyList<RequisitoEstadoDto> Pendencias(PropostaVersao v, AcompanhamentoEstado e)
        {
            if (v.Status != StatusProposta.Rascunho) return Array.Empty<RequisitoEstadoDto>();
            var faltam = new List<RequisitoEstadoDto>();
            void Falta(string chave, string descricao, string pendencia) => faltam.Add(new RequisitoEstadoDto(chave, descricao, false, pendencia));

            if (string.IsNullOrWhiteSpace(v.Dados.QuemDecide ?? e.QuemDecide)) Falta("quemDecide", "Quem decide", "Informe quem decide na loja.");
            if ((v.Dados.PedidosPorDia ?? e.PedidosPorDia) is not > 0) Falta("pedidosPorDia", "Pedidos por dia", "Informe quantos pedidos a loja faz por dia.");
            if (string.IsNullOrWhiteSpace(v.Dados.DorPrincipal ?? e.DorPrincipal)) Falta("dorPrincipal", "Dor principal", "Informe a dor principal da loja.");
            if (!ValorDefinido(v.ValorMensal, v.ValorZeroConsciente))
                Falta("valor", "Mensalidade", v.ValorMensal == 0
                    ? "Confirme que a proposta é de R$ 0 de propósito."
                    : "Informe a mensalidade da proposta.");
            return faltam;
        }

        public static PropostaDto Dto(PropostaVersao v, AcompanhamentoEstado e, DateOnly hoje)
        {
            var pendencias = Pendencias(v, e);
            return new PropostaDto(v.Id, v.OportunidadeId, v.Versao, v.Edicao, v.Status, v.ValorMensal, v.ValorImplantacao, v.ValorZeroConsciente, v.Dados,
                v.Canal, v.Destinatario, v.ApresentadaPor, v.EvidenciaMensagemId, v.Origem, v.RevisaVersao, v.Resposta, v.RespostaNota, v.RespostaEm,
                v.CriadaPor, v.CriadaEm, v.EnviadaEm, v.ValidadeAte, v.AtualizadaEm,
                v.Status == StatusProposta.Rascunho && pendencias.Count == 0, pendencias, Vencida(v, hoje));
        }

        /// <summary>A vigente, senao o rascunho, senao a mais nova.</summary>
        public static PropostaVersao? Atual(IReadOnlyCollection<PropostaVersao> versoes) =>
            versoes.FirstOrDefault(v => StatusProposta.Vigente(v.Status))
            ?? versoes.FirstOrDefault(v => v.Status == StatusProposta.Rascunho)
            ?? versoes.OrderByDescending(v => v.Versao).FirstOrDefault();

        /// <summary>
        /// Novo rascunho. Revisao copia a anterior (o R$ 0 consciente nao vem junto: cada versao confirma o seu).
        /// Sem anterior, aproveita o que a loja e a oportunidade ja sabem.
        /// </summary>
        public static PropostaVersao Nova(Guid oportunidadeId, int numero, PropostaVersao? anterior, AcompanhamentoEstado e, PropostaContexto contexto, int? usuarioId)
        {
            var nova = new PropostaVersao { OportunidadeId = oportunidadeId, Versao = numero, CriadaPor = usuarioId, Origem = OrigensProposta.Acompanhamento };
            if (anterior != null)
            {
                nova.RevisaVersao = anterior.Versao;
                nova.ValorMensal = anterior.ValorMensal;
                nova.ValorImplantacao = anterior.ValorImplantacao;
                nova.Dados = JsonSerializer.Deserialize<PropostaDados>(JsonSerializer.Serialize(anterior.Dados, Json), Json) ?? new PropostaDados();
                nova.Dados.EnviadaEm = null;
                return nova;
            }

            nova.ValorMensal = e.ValorMensal ?? contexto.ValorMensal;
            nova.ValorImplantacao = contexto.ValorImplantacao;
            nova.Dados = new PropostaDados
            {
                QuemDecide = e.QuemDecide ?? contexto.Decisor, PedidosPorDia = e.PedidosPorDia, DorPrincipal = e.DorPrincipal,
                Contato = contexto.Contato, Telefone = contexto.Telefone, Email = contexto.Email,
                Implantacao = contexto.ValorImplantacao
            };
            return nova;
        }

        /// <summary>Aplica o que veio no corpo. Nada de preco e calculado aqui: a mensalidade e a informada ou a soma das partes informadas.</summary>
        public static void Sobrepor(PropostaVersao v, PropostaRequest r)
        {
            var d = v.Dados;
            d.SaaSMensalidade = r.SaaSMensalidade ?? d.SaaSMensalidade;
            d.MarketingMensalidade = r.MarketingMensalidade ?? d.MarketingMensalidade;
            d.MarketingValorFixo = r.MarketingValorFixo ?? d.MarketingValorFixo;
            d.Implantacao = r.Implantacao ?? d.Implantacao;
            d.PedidosPorDia = r.PedidosPorDia ?? d.PedidosPorDia;
            d.Observacoes = Texto(r.Observacoes, d.Observacoes);
            d.Plano = Texto(r.Plano, d.Plano)?.ToLowerInvariant();
            d.QuemDecide = Texto(r.QuemDecide, d.QuemDecide);
            d.Contato = Texto(r.Contato, d.Contato);
            d.Telefone = Texto(r.Telefone, d.Telefone);
            d.Email = Texto(r.Email, d.Email);
            d.DorPrincipal = Texto(r.DorPrincipal, d.DorPrincipal);

            var antes = v.ValorMensal;
            if (r.ValorMensal.HasValue) v.ValorMensal = r.ValorMensal;
            else if (r.SaaSMensalidade.HasValue || r.MarketingMensalidade.HasValue)
                v.ValorMensal = (d.SaaSMensalidade ?? 0m) + (d.MarketingMensalidade ?? 0m);
            if (r.ValorImplantacao.HasValue) v.ValorImplantacao = r.ValorImplantacao;
            else if (r.Implantacao.HasValue) v.ValorImplantacao = r.Implantacao;

            // A confirmacao de R$ 0 vale para o valor que a pessoa viu: mudou o valor, confirma de novo.
            if (v.ValorMensal != antes) v.ValorZeroConsciente = false;
            if (r.ValorZeroConsciente.HasValue) v.ValorZeroConsciente = r.ValorZeroConsciente.Value;
            if (v.ValorMensal is not 0) v.ValorZeroConsciente = false;
        }

        /// <summary>Texto vazio apaga; nulo mantem.</summary>
        private static string? Texto(string? novo, string? atual) => novo == null ? atual : string.IsNullOrWhiteSpace(novo) ? null : novo.Trim();

        public static bool ValoresNegativos(PropostaRequest r) =>
            r.ValorMensal < 0 || r.ValorImplantacao < 0 || r.SaaSMensalidade < 0 || r.MarketingMensalidade < 0 || r.MarketingValorFixo < 0 || r.Implantacao < 0;

        /// <summary>O que o /crm legado le de proposta_json. A mensalidade SaaS sai do valor da versao quando nao veio separada.</summary>
        public static string Espelho(PropostaVersao v)
        {
            var d = v.Dados;
            return JsonSerializer.Serialize(new CrmPropostaDto
            {
                SaaSMensalidade = d.SaaSMensalidade ?? (v.ValorMensal.HasValue ? v.ValorMensal - (d.MarketingMensalidade ?? 0m) : null),
                Implantacao = d.Implantacao ?? v.ValorImplantacao,
                MarketingMensalidade = d.MarketingMensalidade,
                MarketingValorFixo = d.MarketingValorFixo,
                Observacoes = d.Observacoes,
                EnviadaEm = v.EnviadaEm.HasValue ? FusoComercial.Data(v.EnviadaEm.Value) : null
            }, Json);
        }
    }
}
